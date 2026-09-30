using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using ShopifyProductApp.Services.Notifications;

namespace ShopifyProductApp.Services
{
    public class TokenRefreshBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<TokenRefreshBackgroundService> _logger;
        private readonly TimeSpan _checkInterval;
        private readonly TokenOutageMonitor _outageMonitor;

        public TokenRefreshBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<TokenRefreshBackgroundService> logger,
            IConfiguration configuration,
            TokenOutageMonitor outageMonitor)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _outageMonitor = outageMonitor;

            var intervalStr = configuration["App:BackgroundServices:TokenRefreshInterval"] ?? "00:03:00";
            _checkInterval = TimeSpan.TryParse(intervalStr, out var interval) 
                ? interval 
                : TimeSpan.FromMinutes(3);

            _logger.LogInformation("🔧 Token Refresh Service: Her {Interval}", _checkInterval);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🚀 Token Refresh başlatıldı");

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var tokenManager = scope.ServiceProvider.GetRequiredService<ITokenManager>();

                    var health = await tokenManager.GetTokenHealthAsync();

                    Console.WriteLine($"\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                    Console.WriteLine($"🕐 {DateTime.Now:HH:mm:ss}");
                    Console.ForegroundColor = health.IsHealthy ? ConsoleColor.Green : ConsoleColor.Red;
                    Console.WriteLine($"{(health.IsHealthy ? "✅" : "❌")} {health.Message}");
                    Console.ResetColor();
                    Console.WriteLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n");

                    if (!health.IsHealthy)
                    {
                        _logger.LogWarning("⚠️ Token yenileniyor...");
                        await tokenManager.RefreshTokenIfNeededAsync();
                        health = await tokenManager.GetTokenHealthAsync();
                    }

                    // Kesinti takibi: token 30 dk+ alınamazsa e-posta uyarısı, geri gelince havuzu tetikle
                    await _outageMonitor.CheckAsync(health);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Hata");
                }

                await Task.Delay(_checkInterval, stoppingToken);
            }
        }
    }
}