// TokenManagerService.cs - ENHANCED VERSION
using ShopifyProductApp.Services; 
using System.Collections.Concurrent;
using System.Text.Json;
using ExactOnline.Models;
using ExactOnline.Converters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace ShopifyProductApp.Services
{
    public interface ITokenManager
    {
        Task<string?> GetValidAccessTokenAsync();
        Task<TokenResponse?> GetValidTokenAsync();
        Task<bool> IsTokenValidAsync();
        Task RefreshTokenIfNeededAsync();
        Task<TokenHealthStatus> GetTokenHealthAsync();
    }

    public class TokenManagerService : ITokenManager
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<TokenManagerService> _logger;
        private readonly IConfiguration _configuration;
        private readonly SemaphoreSlim _tokenSemaphore;
        private readonly SemaphoreSlim _refreshSemaphore;
        private readonly SemaphoreSlim _fileLock;
        
        // Cache
        private TokenResponse? _cachedToken;
        private DateTime _cacheExpiry = DateTime.MinValue;
        private readonly TimeSpan _minCacheLifetime = TimeSpan.FromMinutes(1);
        private readonly double _cachePercentage = 0.75; // Cache süresini %75'e düşürdük (daha erken yenileme)

        // Health
        private DateTime _lastSuccessfulRefresh = DateTime.MinValue;
        private int _consecutiveFailures = 0;
        private const int MaxConsecutiveFailures = 5;
        private DateTime _lastRefreshAttempt = DateTime.MinValue;
        private readonly TimeSpan _minRefreshInterval = TimeSpan.FromMinutes(1); // Çok sık yenileme yapma

        // Config
        private readonly string _tokenFile;
        private readonly string _backupTokenFile; // YENI: Backup dosyası
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly string _redirectUri;
        private readonly string _baseUrl;
        
        // YENI: Token yenileme eşiği (dakika)
        private readonly int _refreshThresholdMinutes;

        public TokenManagerService(
            IServiceProvider serviceProvider, 
            ILogger<TokenManagerService> logger,
            IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _configuration = configuration;
            
            _tokenSemaphore = new SemaphoreSlim(1, 1);
            _refreshSemaphore = new SemaphoreSlim(1, 1);
            _fileLock = new SemaphoreSlim(1, 1);

            // Config'den oku
            _clientId = configuration["ExactOnline:ClientId"] 
                ?? throw new InvalidOperationException("ExactOnline:ClientId missing");
            _clientSecret = configuration["ExactOnline:ClientSecret"] 
                ?? throw new InvalidOperationException("ExactOnline:ClientSecret missing");
            _redirectUri = configuration["ExactOnline:RedirectUri"] 
                ?? throw new InvalidOperationException("ExactOnline:RedirectUri missing");
            _baseUrl = configuration["ExactOnline:BaseUrl"] ?? "https://start.exactonline.nl";
            
            // Token dosyaları
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _tokenFile = Path.Combine(baseDir, "exact_token.json");
            _backupTokenFile = Path.Combine(baseDir, "exact_token.backup.json");

            // Refresh threshold
            if (!int.TryParse(configuration["App:BackgroundServices:TokenRefreshThresholdMinutes"], 
                out _refreshThresholdMinutes))
            {
                _refreshThresholdMinutes = 2; // Varsayılan: 10 dakika kala yenile
            }

            _logger.LogInformation("🔧 TokenManager yapılandırıldı:");
            _logger.LogInformation("   - Refresh Threshold: {Threshold} dakika", _refreshThresholdMinutes);
            _logger.LogInformation("   - Cache Percentage: {Percentage}%", _cachePercentage * 100);
        }

        public async Task<string?> GetValidAccessTokenAsync()
        {
            var token = await GetValidTokenAsync();
            return token?.access_token;
        }

        public async Task<TokenResponse?> GetValidTokenAsync()
        {
            // 1️⃣ Cache kontrolü
            if (IsCacheValid())
            {
                _logger.LogDebug("💨 Token cache'den döndürüldü");
                return _cachedToken;
            }

            await _tokenSemaphore.WaitAsync();
            try
            {
                // Double-check
                if (IsCacheValid())
                {
                    return _cachedToken;
                }

                _logger.LogInformation("🔍 Token alınıyor...");

                using var scope = _serviceProvider.CreateScope();
                var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                
                // 2️⃣ Token bilgilerini al
                var tokenInfo = await settingsService.GetExactTokenInfoAsync();
                var token = ParseTokenInfo(tokenInfo);

                // 3️⃣ Token yoksa dosyadan yükle
                if (token == null)
                {
                    _logger.LogWarning("⚠️ Veritabanında token bulunamadı");
                    token = await LoadTokenWithFallback(settingsService);

                    if (token == null)
                    {
                        _logger.LogError("❌ Hiçbir kaynakta token bulunamadı");
                        return null;
                    }
                }

                LogTokenStatus(token);

                // 4️⃣ Token dolmuş, dolmak üzere veya eşik altında mı?
                if (token.IsExpired() || ShouldRefreshToken(token))
                {
                    _logger.LogWarning("⚠️ Token yenilenmeli (Expiry: {ExpiryTime}, Kalan: {Remaining:F1} dk)", 
                        token.ExpiryTime.ToString("HH:mm:ss"),
                        (token.ExpiryTime - DateTime.UtcNow).TotalMinutes);
                    
                    token = await RefreshTokenSafelyAsync(token, settingsService);
                    
                    if (token == null)
                    {
                        _logger.LogError("❌ Token yenilenemedi");
                        return null;
                    }
                }

                // 5️⃣ Cache'e al
                UpdateCache(token);
                
                _consecutiveFailures = 0;
                _lastSuccessfulRefresh = DateTime.UtcNow;

                return token;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ GetValidTokenAsync kritik hatası");
                _consecutiveFailures++;
                
                // Fallback stratejisi
                return await GetFallbackToken();
            }
            finally
            {
                _tokenSemaphore.Release();
            }
        }

        public async Task<bool> IsTokenValidAsync()
        {
            var token = await GetValidTokenAsync();
            return token != null && !token.IsExpired() && !ShouldRefreshToken(token);
        }

        public async Task RefreshTokenIfNeededAsync()
        {
            // Rate limiting: Çok sık yenileme yapma
            if (DateTime.UtcNow - _lastRefreshAttempt < _minRefreshInterval)
            {
                _logger.LogDebug("⏳ Son yenileme çok yakın zamanda yapıldı, atlıyoruz");
                return;
            }

            await _refreshSemaphore.WaitAsync();
            try
            {
                _lastRefreshAttempt = DateTime.UtcNow;
                _logger.LogInformation("🔄 Manuel token yenileme başlatıldı");
                
                using var scope = _serviceProvider.CreateScope();
                var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                
                var tokenInfo = await settingsService.GetExactTokenInfoAsync();
                var currentToken = ParseTokenInfo(tokenInfo);
                
                if (currentToken == null)
                {
                    _logger.LogError("❌ Mevcut token alınamadı, fallback deneniyor");
                    currentToken = await LoadTokenWithFallback(settingsService);
                    
                    if (currentToken == null)
                    {
                        _logger.LogError("❌ Fallback'te de token bulunamadı");
                        return;
                    }
                }
                
                // Gerçekten yenilemeye ihtiyaç var mı?
                if (!ShouldRefreshToken(currentToken) && !currentToken.IsExpired())
                {
                    _logger.LogInformation("✅ Token hala geçerli, yenilemeye gerek yok");
                    return;
                }

                var newToken = await RefreshTokenSafelyAsync(currentToken, settingsService);
                
                if (newToken != null)
                {
                    UpdateCache(newToken);
                    _logger.LogInformation("✅ Manuel token yenileme başarılı");
                    _consecutiveFailures = 0;
                }
                else
                {
                    _consecutiveFailures++;
                    _logger.LogError("❌ Manuel token yenileme başarısız (Ardışık hata: {Count})", 
                        _consecutiveFailures);
                }
            }
            finally
            {
                _refreshSemaphore.Release();
            }
        }

        public async Task<TokenHealthStatus> GetTokenHealthAsync()
        {
            try
            {
                var token = await GetValidTokenAsync();
                
                if (token == null)
                {
                    return new TokenHealthStatus
                    {
                        IsHealthy = false,
                        Message = "Token alınamadı",
                        LastCheck = DateTime.UtcNow,
                        ConsecutiveFailures = _consecutiveFailures,
                        LastSuccessfulRefresh = _lastSuccessfulRefresh
                    };
                }

                var remaining = (token.ExpiryTime - DateTime.UtcNow).TotalMinutes;
                var isHealthy = remaining > _refreshThresholdMinutes && 
                                _consecutiveFailures < MaxConsecutiveFailures;

                return new TokenHealthStatus
                {
                    IsHealthy = isHealthy,
                    Message = isHealthy 
                        ? $"Token geçerli, {remaining:F1} dk kaldı"
                        : $"Token yenilenmeli, {remaining:F1} dk kaldı",
                    ExpiryTime = token.ExpiryTime,
                    RemainingMinutes = remaining,
                    LastCheck = DateTime.UtcNow,
                    ConsecutiveFailures = _consecutiveFailures,
                    LastSuccessfulRefresh = _lastSuccessfulRefresh,
                    IsCached = _cachedToken != null
                };
            }
            catch (Exception ex)
            {
                return new TokenHealthStatus
                {
                    IsHealthy = false,
                    Message = $"Hata: {ex.Message}",
                    LastCheck = DateTime.UtcNow,
                    ConsecutiveFailures = _consecutiveFailures
                };
            }
        }

        // ============= PRIVATE METODLAR =============

        private bool IsCacheValid()
        {
            return _cachedToken != null && 
                   DateTime.UtcNow < _cacheExpiry && 
                   !_cachedToken.IsExpired() &&
                   !ShouldRefreshToken(_cachedToken);
        }

        /// <summary>
        /// Token'ın yenilenmesi gerekip gerekmediğini belirler
        /// </summary>
        private bool ShouldRefreshToken(TokenResponse token)
        {
            if (token == null) return true;
            
            var remainingMinutes = (token.ExpiryTime - DateTime.UtcNow).TotalMinutes;
            return remainingMinutes <= _refreshThresholdMinutes;
        }

        private TokenResponse? ParseTokenInfo(dynamic tokenInfo)
        {
            if (tokenInfo == null)
            {
                return null;
            }

            string? accessToken = tokenInfo.AccessToken;
            string? refreshToken = tokenInfo.RefreshToken;

            if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
            {
                return null;
            }

            string? expiryTimeStr = tokenInfo.ExpiryTime;
            if (string.IsNullOrEmpty(expiryTimeStr) || 
                !DateTimeOffset.TryParse(expiryTimeStr, out var expiry))
            {
                _logger.LogWarning("⚠️ ExpiryTime parse edilemedi: {ExpiryTime}", expiryTimeStr);
                return null;
            }

            return new TokenResponse
            {
                access_token = accessToken,
                refresh_token = refreshToken,
                token_type = tokenInfo.TokenType ?? "bearer",
                expires_in = tokenInfo.ExpiresIn,
                ExpiryTime = expiry.UtcDateTime
            };
        }

        private bool ValidateToken(TokenResponse token)
        {
            if (token == null) return false;
            if (string.IsNullOrEmpty(token.access_token)) return false;
            if (string.IsNullOrEmpty(token.refresh_token)) return false;
            if (token.ExpiryTime <= DateTime.UtcNow) return false;
            if (token.expires_in <= 0) return false;
            return true;
        }

        private void UpdateCache(TokenResponse token)
        {
            _cachedToken = token;
            
            var remainingTime = token.ExpiryTime - DateTime.UtcNow;
            var cacheTime = TimeSpan.FromMilliseconds(remainingTime.TotalMilliseconds * _cachePercentage);
            
            if (cacheTime < _minCacheLifetime)
            {
                cacheTime = _minCacheLifetime;
            }
            
            _cacheExpiry = DateTime.UtcNow.Add(cacheTime);
            
            _logger.LogDebug("💾 Token cache'lendi, süre: {CacheTime:F1} dk", cacheTime.TotalMinutes);
        }

        private void LogTokenStatus(TokenResponse token)
        {
            var remaining = (token.ExpiryTime - DateTime.UtcNow).TotalMinutes;

            if (remaining > _refreshThresholdMinutes)
            {
                _logger.LogInformation("✅ Token sağlıklı, kalan: {Remaining:F1} dk (Expiry: {ExpiryTime})", 
                    remaining, token.ExpiryTime.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            else if (remaining > 0)
            {
                _logger.LogWarning("⚠️ Token eşik altında, kalan: {Remaining:F1} dk", remaining);
            }
            else
            {
                _logger.LogError("❌ Token dolmuş, {Expired:F1} dk önce expired", Math.Abs(remaining));
            }
        }

        /// <summary>
        /// Fallback token yükleme stratejisi: Cache -> Dosya -> Backup -> null
        /// </summary>
        private async Task<TokenResponse?> GetFallbackToken()
        {
            _logger.LogWarning("🆘 Fallback token stratejisi başlatıldı");

            // 1. Cache
            if (_cachedToken != null && !_cachedToken.IsExpired())
            {
                _logger.LogWarning("⚠️ Cache'deki token kullanılıyor (fallback)");
                return _cachedToken;
            }

            // 2. Ana dosya
            var tokenFromFile = await LoadTokenFromFile();
            if (tokenFromFile != null && ValidateToken(tokenFromFile))
            {
                _logger.LogWarning("⚠️ Dosyadan token yüklendi (fallback)");
                return tokenFromFile;
            }

            // 3. Backup dosya
            var tokenFromBackup = await LoadTokenFromBackup();
            if (tokenFromBackup != null && ValidateToken(tokenFromBackup))
            {
                _logger.LogWarning("⚠️ Backup dosyadan token yüklendi (fallback)");
                return tokenFromBackup;
            }

            _logger.LogError("❌ Hiçbir fallback stratejisi başarılı olmadı");
            return null;
        }

        /// <summary>
        /// Ana dosya + backup ile token yükleme
        /// </summary>
        private async Task<TokenResponse?> LoadTokenWithFallback(ISettingsService settingsService)
        {
            // 1. Ana dosyadan yükle
            if (File.Exists(_tokenFile))
            {
                _logger.LogInformation("📁 Ana dosyadan token yükleniyor...");
                var token = await LoadTokenFromFileAndSaveToDb(settingsService);
                
                if (token != null)
                {
                    return token;
                }
            }

            // 2. Backup dosyadan yükle
            if (File.Exists(_backupTokenFile))
            {
                _logger.LogWarning("⚠️ Ana dosya başarısız, backup'tan yükleniyor...");
                var token = await LoadTokenFromBackup();
                
                if (token != null && ValidateToken(token))
                {
                    // DB'ye kaydet
                    try
                    {
                        await SaveTokenToDatabase(token, settingsService);
                        return token;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Backup token DB'ye kaydedilemedi");
                        return token; // Yine de token'ı dön
                    }
                }
            }

            return null;
        }

        private async Task<TokenResponse?> RefreshTokenSafelyAsync(
            TokenResponse currentToken, 
            ISettingsService settingsService)
        {
            await _refreshSemaphore.WaitAsync();
            try
            {
                _logger.LogInformation("🔄 Token yenileniyor...");
                
                // Double-check: Başka thread yeniledi mi?
                var freshTokenInfo = await settingsService.GetExactTokenInfoAsync();
                var freshToken = ParseTokenInfo(freshTokenInfo);

                if (freshToken != null && !freshToken.IsExpired() && !ShouldRefreshToken(freshToken))
                {
                    _logger.LogInformation("✅ Token başka thread tarafından yenilendi");
                    return freshToken;
                }

                var refreshTokenToUse = freshToken?.refresh_token ?? currentToken.refresh_token;

                if (string.IsNullOrEmpty(refreshTokenToUse))
                {
                    _logger.LogError("❌ Refresh token boş!");
                    return null;
                }

                var newToken = await RefreshTokenAsync(refreshTokenToUse);

                if (newToken == null)
                {
                    _logger.LogError("❌ Token yenileme başarısız");
                    _consecutiveFailures++;
                    return null;
                }

                if (!ValidateToken(newToken))
                {
                    _logger.LogError("❌ Yeni token geçersiz");
                    _consecutiveFailures++;
                    return null;
                }

                // İlk önce backup yap
                await CreateBackup(currentToken);

                // Sonra yeni token'ı kaydet
                await SaveTokenToFileSafely(newToken);
                await SaveTokenToDatabase(newToken, settingsService);

                _logger.LogInformation("✅ Token başarıyla yenilendi, yeni expiry: {ExpiryTime}", 
                    newToken.ExpiryTime.ToString("yyyy-MM-dd HH:mm:ss"));
                
                _consecutiveFailures = 0;
                _lastSuccessfulRefresh = DateTime.UtcNow;

                return newToken;
            }
            finally
            {
                _refreshSemaphore.Release();
            }
        }

        private async Task<TokenResponse?> RefreshTokenAsync(string refreshToken, int maxRetries = 3)
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    using var client = new HttpClient();
                    client.Timeout = TimeSpan.FromSeconds(30);

                    var form = new Dictionary<string, string>
                    {
                        { "grant_type", "refresh_token" },
                        { "refresh_token", refreshToken },
                        { "client_id", _clientId },
                        { "client_secret", _clientSecret },
                        { "redirect_uri", _redirectUri }
                    };

                    _logger.LogInformation("🔄 Token yenileme denemesi {Attempt}/{MaxRetries}", 
                        attempt, maxRetries);

                    var resp = await client.PostAsync($"{_baseUrl}/api/oauth2/token",
                        new FormUrlEncodedContent(form));

                    var json = await resp.Content.ReadAsStringAsync();

                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger.LogError("Token yenileme hatası: {StatusCode} - {Response}",
                            resp.StatusCode, json);

                        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                            resp.StatusCode == System.Net.HttpStatusCode.BadRequest)
                        {
                            _logger.LogError("❌ Refresh token geçersiz, yeniden auth gerekli");
                            return null;
                        }

                        if (attempt < maxRetries)
                        {
                            var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                            _logger.LogWarning("⏳ {Delay} saniye sonra tekrar denenecek", delay.TotalSeconds);
                            await Task.Delay(delay);
                            continue;
                        }

                        return null;
                    }

                    var token = JsonSerializer.Deserialize<TokenResponse>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        Converters = { new FlexibleIntConverter() }
                    });

                    if (token != null)
                    {
                        token.ExpiryTime = DateTime.UtcNow.AddSeconds(token.expires_in);
                        _logger.LogInformation("✅ Token başarıyla yenilendi (Expiry: {ExpiryTime})", 
                            token.ExpiryTime.ToString("yyyy-MM-dd HH:mm:ss"));
                        return token;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Token yenileme hatası (Deneme {Attempt})", attempt);
                }

                if (attempt < maxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                }
            }

            return null;
        }

        private async Task SaveTokenToDatabase(TokenResponse token, ISettingsService settingsService)
        {
            try
            {
                await settingsService.UpdateExactTokenAsync(
                    token.access_token,
                    token.refresh_token,
                    token.ExpiryTime,
                    token.expires_in
                );

                _logger.LogInformation("💾 Token veritabanına kaydedildi");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Token veritabanına kaydetme hatası");
                throw;
            }
        }

        private async Task SaveTokenToFileSafely(TokenResponse token)
        {
            await _fileLock.WaitAsync();
            try
            {
                var json = JsonSerializer.Serialize(token, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                var tempFile = _tokenFile + ".tmp";
                await File.WriteAllTextAsync(tempFile, json);
                File.Move(tempFile, _tokenFile, overwrite: true);

                _logger.LogInformation("📁 Token dosyaya kaydedildi");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Token dosyaya kaydetme hatası");
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Mevcut token'ın backup'ını oluşturur
        /// </summary>
        private async Task CreateBackup(TokenResponse token)
        {
            if (token == null) return;

            await _fileLock.WaitAsync();
            try
            {
                var json = JsonSerializer.Serialize(token, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                await File.WriteAllTextAsync(_backupTokenFile, json);
                _logger.LogDebug("💾 Token backup oluşturuldu");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Token backup oluşturulamadı");
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private async Task<TokenResponse?> LoadTokenFromFileAndSaveToDb(ISettingsService settingsService)
        {
            var token = await LoadTokenFromFile();

            if (token != null && ValidateToken(token))
            {
                try
                {
                    await SaveTokenToDatabase(token, settingsService);
                    _logger.LogInformation("🔄 Token dosyadan yüklendi ve DB'ye aktarıldı");
                    return token;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Token DB'ye kaydedilemedi");
                    return token;
                }
            }

            return null;
        }

        private async Task<TokenResponse?> LoadTokenFromFile()
        {
            await _fileLock.WaitAsync();
            try
            {
                if (!File.Exists(_tokenFile))
                    return null;

                var text = await File.ReadAllTextAsync(_tokenFile);
                var token = JsonSerializer.Deserialize<TokenResponse>(text, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new FlexibleIntConverter() }
                });

                if (token != null && ValidateToken(token))
                {
                    _logger.LogInformation("📁 Token dosyadan yüklendi");
                    return token;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dosyadan token yükleme hatası");
                return null;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Backup dosyasından token yükler
        /// </summary>
        private async Task<TokenResponse?> LoadTokenFromBackup()
        {
            await _fileLock.WaitAsync();
            try
            {
                if (!File.Exists(_backupTokenFile))
                    return null;

                var text = await File.ReadAllTextAsync(_backupTokenFile);
                var token = JsonSerializer.Deserialize<TokenResponse>(text, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new FlexibleIntConverter() }
                });

                if (token != null && ValidateToken(token))
                {
                    _logger.LogInformation("📁 Token backup dosyadan yüklendi");
                    return token;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backup dosyadan token yükleme hatası");
                return null;
            }
            finally
            {
                _fileLock.Release();
            }
        }
    }

    public class TokenHealthStatus
    {
        public bool IsHealthy { get; set; }
        public string? Message { get; set; }
        public DateTime? ExpiryTime { get; set; }
        public double? RemainingMinutes { get; set; }
        public DateTime LastCheck { get; set; }
        public int ConsecutiveFailures { get; set; }
        public DateTime LastSuccessfulRefresh { get; set; }
        public bool IsCached { get; set; }
    }
}