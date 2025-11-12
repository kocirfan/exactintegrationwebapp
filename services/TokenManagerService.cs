// TokenManagerService.cs - NO SEMAPHORE VERSION
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
        
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly string _redirectUri;
        private readonly string _baseUrl;
        private readonly int _thresholdMinutes;
        
        private DateTime _lastRefreshAttempt = DateTime.MinValue;

        public TokenManagerService(
            IServiceProvider serviceProvider, 
            ILogger<TokenManagerService> logger,
            IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _configuration = configuration;

            _clientId = configuration["ExactOnline:ClientId"]!;
            _clientSecret = configuration["ExactOnline:ClientSecret"]!;
            _redirectUri = configuration["ExactOnline:RedirectUri"]!;
            _baseUrl = configuration["ExactOnline:BaseUrl"] ?? "https://start.exactonline.nl";
            _thresholdMinutes = int.Parse(configuration["App:BackgroundServices:TokenRefreshThresholdMinutes"] ?? "10");

            _logger.LogInformation("🔧 TokenManager başlatıldı (Threshold: {Threshold} dk)", _thresholdMinutes);
        }

        public async Task<string?> GetValidAccessTokenAsync()
        {
            var token = await GetValidTokenAsync();
            return token?.access_token;
        }

        public async Task<TokenResponse?> GetValidTokenAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            
            var info = await settings.GetExactTokenInfoAsync();
            var token = ParseToken(info);

            if (token == null)
            {
                _logger.LogError("❌ Token bulunamadı");
                return null;
            }

            var remaining = (token.ExpiryTime - DateTime.UtcNow).TotalMinutes;

            // Token yenilenmeli mi?
            if (remaining <= _thresholdMinutes)
            {
                _logger.LogWarning("⚠️ Token yenilenmeli ({Remaining:F1} dk kaldı)", remaining);
                
                // Çok sık yenileme yapma (1 dakikada max 1 kez)
                if ((DateTime.UtcNow - _lastRefreshAttempt).TotalMinutes < 1)
                {
                    _logger.LogDebug("⏳ Son yenileme çok yakın, atlıyoruz");
                    return token; // Eski token'ı dön
                }
                
                var newToken = await RefreshToken(token, settings);
                return newToken ?? token; // Yenileme başarısız olursa eski token'ı dön
            }

            _logger.LogDebug("✅ Token geçerli ({Remaining:F1} dk kaldı)", remaining);
            return token;
        }

        public async Task<bool> IsTokenValidAsync()
        {
            var token = await GetValidTokenAsync();
            return token != null;
        }

        public async Task RefreshTokenIfNeededAsync()
        {
            await GetValidTokenAsync(); // Zaten kontrol eder ve yeniler
        }

        public async Task<TokenHealthStatus> GetTokenHealthAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            
            var info = await settings.GetExactTokenInfoAsync();
            var token = ParseToken(info);

            if (token == null)
            {
                return new TokenHealthStatus
                {
                    IsHealthy = false,
                    Message = "Token bulunamadı",
                    LastCheck = DateTime.UtcNow
                };
            }

            var remaining = (token.ExpiryTime - DateTime.UtcNow).TotalMinutes;

            return new TokenHealthStatus
            {
                IsHealthy = remaining > _thresholdMinutes,
                Message = $"{remaining:F1} dk kaldı",
                ExpiryTime = token.ExpiryTime,
                RemainingMinutes = remaining,
                LastCheck = DateTime.UtcNow
            };
        }

        // ============= PRIVATE =============

        private async Task<TokenResponse?> RefreshToken(TokenResponse current, ISettingsService settings)
        {
            _lastRefreshAttempt = DateTime.UtcNow;
            
            try
            {
                _logger.LogInformation("🔄 Token yenileniyor...");

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

                var form = new Dictionary<string, string>
                {
                    { "grant_type", "refresh_token" },
                    { "refresh_token", current.refresh_token },
                    { "client_id", _clientId },
                    { "client_secret", _clientSecret },
                    { "redirect_uri", _redirectUri }
                };

                var response = await client.PostAsync(
                    $"{_baseUrl}/api/oauth2/token",
                    new FormUrlEncodedContent(form));

                var json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("❌ API hatası: {Status} - {Error}", 
                        response.StatusCode, 
                        json.Length > 200 ? json.Substring(0, 200) : json);
                    return null;
                }

                var newToken = JsonSerializer.Deserialize<TokenResponse>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new FlexibleIntConverter() }
                });

                if (newToken == null)
                {
                    _logger.LogError("❌ Token parse edilemedi");
                    return null;
                }

                newToken.ExpiryTime = DateTime.UtcNow.AddSeconds(newToken.expires_in);

                // DB'ye kaydet
                await settings.UpdateExactTokenAsync(
                    newToken.access_token,
                    newToken.refresh_token,
                    newToken.ExpiryTime,
                    newToken.expires_in);

                _logger.LogInformation("✅ Token başarıyla yenilendi! Yeni süre: {Remaining:F1} dk", 
                    (newToken.ExpiryTime - DateTime.UtcNow).TotalMinutes);

                return newToken;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Token yenileme hatası: {Message}", ex.Message);
                return null;
            }
        }

        private TokenResponse? ParseToken(dynamic info)
        {
            if (info == null) return null;

            try
            {
                string? access = info.AccessToken;
                string? refresh = info.RefreshToken;
                string? expiryStr = info.ExpiryTime;

                if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh) || string.IsNullOrEmpty(expiryStr))
                    return null;

                if (!DateTimeOffset.TryParse(expiryStr, out var expiry))
                    return null;

                return new TokenResponse
                {
                    access_token = access,
                    refresh_token = refresh,
                    token_type = "bearer",
                    expires_in = info.ExpiresIn ?? 3600,
                    ExpiryTime = expiry.UtcDateTime
                };
            }
            catch
            {
                return null;
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