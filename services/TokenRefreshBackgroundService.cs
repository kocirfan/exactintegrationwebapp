using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace ShopifyProductApp.Services
{
    /// <summary>
    /// Token'ı proaktif olarak yeniler ve sağlık durumunu izler
    /// </summary>
    public class TokenRefreshBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<TokenRefreshBackgroundService> _logger;
        private readonly IConfiguration _configuration;
        private readonly TimeSpan _checkInterval;
        private readonly TimeSpan _refreshThreshold; // Token'ın ne kadar kala yenileneceği
        private readonly int _maxConsecutiveFailures = 5;
        private int _consecutiveFailures = 0;

        public TokenRefreshBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<TokenRefreshBackgroundService> logger,
            IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _configuration = configuration;

            // Ayarları oku
            var intervalString = _configuration["App:BackgroundServices:TokenRefreshInterval"] ?? "00:03:00";
            if (!TimeSpan.TryParse(intervalString, out _checkInterval))
            {
                _checkInterval = TimeSpan.FromMinutes(3);
            }

            // Token'ı ne kadar süre kala yenileyeceğiz? (Varsayılan: 10 dakika)
            var thresholdString = _configuration["App:BackgroundServices:TokenRefreshThresholdMinutes"] ?? "10";
            if (!int.TryParse(thresholdString, out var thresholdMinutes))
            {
                thresholdMinutes = 10;
            }
            _refreshThreshold = TimeSpan.FromMinutes(thresholdMinutes);

            _logger.LogInformation("⚙️ Token Refresh Service yapılandırıldı:");
            _logger.LogInformation("   - Kontrol Aralığı: {CheckInterval}", _checkInterval);
            _logger.LogInformation("   - Yenileme Eşiği: {RefreshThreshold} dakika", thresholdMinutes);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🚀 Token Refresh Background Service başlatıldı");

            // İlk başlangıçta biraz bekle (sistem hazır olsun)
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

            // İlk token kontrolü ve gerekirse yenileme
            await PerformInitialTokenCheck(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAndRefreshToken(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Token kontrol döngüsünde kritik hata");
                    _consecutiveFailures++;

                    // Çok fazla hata varsa alarm ver
                    if (_consecutiveFailures >= _maxConsecutiveFailures)
                    {
                        _logger.LogCritical("🚨 TOKEN YÖNETİMİ KRİTİK DURUMDA! {Failures} ardışık hata", 
                            _consecutiveFailures);
                        
                        // Hata durumunda daha sık kontrol et
                        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                        continue;
                    }
                }

                // Bir sonraki kontrole kadar bekle
                await Task.Delay(_checkInterval, stoppingToken);
            }

            _logger.LogInformation("🛑 Token Refresh Background Service durduruluyor");
        }

        /// <summary>
        /// İlk başlangıçta token durumunu kontrol et ve gerekirse hemen yenile
        /// </summary>
        private async Task PerformInitialTokenCheck(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🔍 İlk token kontrolü yapılıyor...");

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var tokenManager = scope.ServiceProvider.GetRequiredService<ITokenManager>();

                var health = await tokenManager.GetTokenHealthAsync();
                
                if (!health.IsHealthy)
                {
                    _logger.LogWarning("⚠️ Başlangıçta token sağlıksız, yenileniyor...");
                    await tokenManager.RefreshTokenIfNeededAsync();
                    _logger.LogInformation("✅ Başlangıç token yenileme tamamlandı");
                }
                else
                {
                    _logger.LogInformation("✅ Başlangıçta token sağlıklı");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ İlk token kontrolü başarısız");
            }
        }

        /// <summary>
        /// Token'ı kontrol et ve gerekiyorsa yenile
        /// </summary>
        private async Task CheckAndRefreshToken(CancellationToken stoppingToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var tokenManager = scope.ServiceProvider.GetRequiredService<ITokenManager>();

            _logger.LogDebug("🔍 Token durumu kontrol ediliyor...");

            // Token sağlık durumunu al
            var health = await tokenManager.GetTokenHealthAsync();

            PrintTokenStatus(health);

            // Token geçerli mi?
            if (!health.IsHealthy)
            {
                _logger.LogWarning("⚠️ Token sağlıksız, yenileniyor...");
                await tokenManager.RefreshTokenIfNeededAsync();
                
                // Yenileme sonrası kontrol
                var newHealth = await tokenManager.GetTokenHealthAsync();
                
                if (newHealth.IsHealthy)
                {
                    _logger.LogInformation("✅ Token başarıyla yenilendi");
                    _consecutiveFailures = 0;
                }
                else
                {
                    _logger.LogError("❌ Token yenileme başarısız!");
                    _consecutiveFailures++;
                }
            }
            // Token dolmak üzere mi? (Proaktif yenileme)
            else if (health.RemainingMinutes.HasValue && 
                     health.RemainingMinutes.Value <= _refreshThreshold.TotalMinutes)
            {
                _logger.LogInformation("🔄 Token {Minutes:F1} dakika içinde dolacak, proaktif yenileniyor...", 
                    health.RemainingMinutes.Value);
                
                await tokenManager.RefreshTokenIfNeededAsync();
                
                // Yenileme sonrası kontrol
                var newHealth = await tokenManager.GetTokenHealthAsync();
                
                if (newHealth.RemainingMinutes.HasValue && 
                    newHealth.RemainingMinutes.Value > health.RemainingMinutes.Value)
                {
                    _logger.LogInformation("✅ Proaktif token yenileme başarılı, yeni süre: {Minutes:F1} dakika", 
                        newHealth.RemainingMinutes.Value);
                    _consecutiveFailures = 0;
                }
                else
                {
                    _logger.LogError("❌ Proaktif token yenileme başarısız!");
                    _consecutiveFailures++;
                }
            }
            else
            {
                // Her şey yolunda
                _consecutiveFailures = 0;
            }

            // Hata sayacı çok yüksekse uyarı ver
            if (_consecutiveFailures >= 3)
            {
                _logger.LogWarning("⚠️ {Failures} ardışık token yenileme hatası", _consecutiveFailures);
            }
        }

        /// <summary>
        /// Token durumunu konsola yazdır
        /// </summary>
        private void PrintTokenStatus(TokenHealthStatus health)
        {
            Console.WriteLine("\n╔════════════════════════════════════════╗");
            Console.WriteLine($"║  🕐 Token Kontrol: {DateTime.Now:HH:mm:ss}          ║");
            Console.WriteLine("╠════════════════════════════════════════╣");
            
            if (health.IsHealthy)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"║  ✅ Durum: SAĞLIKLI                    ║");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"║  ❌ Durum: SAĞLIKSIZ                   ║");
                Console.ResetColor();
            }

            Console.WriteLine($"║  💬 Mesaj: {health.Message,-26}║");
            
            if (health.RemainingMinutes.HasValue)
            {
                var color = health.RemainingMinutes.Value switch
                {
                    <= 5 => ConsoleColor.Red,
                    <= 10 => ConsoleColor.Yellow,
                    _ => ConsoleColor.Green
                };

                Console.ForegroundColor = color;
                Console.WriteLine($"║  ⏱️  Kalan: {health.RemainingMinutes.Value:F1} dakika{new string(' ', 19 - health.RemainingMinutes.Value.ToString("F1").Length)}║");
                Console.ResetColor();
            }

            if (health.ExpiryTime.HasValue)
            {
                Console.WriteLine($"║  ⏰ Dolma: {health.ExpiryTime.Value:HH:mm:ss}             ║");
            }

            Console.WriteLine($"║  🔄 Ardışık Hata: {health.ConsecutiveFailures,-15}║");
            Console.WriteLine($"║  💾 Cache'li: {(health.IsCached ? "Evet" : "Hayır"),-20}║");
            
            if (health.LastSuccessfulRefresh != DateTime.MinValue)
            {
                var timeSinceRefresh = DateTime.UtcNow - health.LastSuccessfulRefresh;
                Console.WriteLine($"║  🔄 Son Başarılı: {timeSinceRefresh.TotalMinutes:F0} dk önce{new string(' ', 11 - ((int)timeSinceRefresh.TotalMinutes).ToString().Length)}║");
            }

            Console.WriteLine("╚════════════════════════════════════════╝\n");
        }
    }
}