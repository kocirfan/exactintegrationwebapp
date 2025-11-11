public class TokenRefreshBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TokenRefreshBackgroundService> _logger;
    private readonly IConfiguration _configuration;
    private readonly TimeSpan _interval;

    public TokenRefreshBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<TokenRefreshBackgroundService> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configuration = configuration;

        var intervalString = _configuration["App:BackgroundServices:TokenRefreshInterval"] ?? "00:05:00";
        if (!TimeSpan.TryParse(intervalString, out _interval))
        {
            _interval = TimeSpan.FromMinutes(5);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation($"🚀 Token Refresh Service başlatıldı (Interval: {_interval})");

        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var exactService = scope.ServiceProvider.GetRequiredService<ExactService>();

                Console.WriteLine("\n═══════════════════════════════════════");
                Console.WriteLine($"🕐 Token Kontrol Zamanı: {DateTime.Now:HH:mm:ss}");

                var token = await exactService.GetValidToken();

                if (token == null)
                {
                    _logger.LogError("❌ Token NULL!");
                }
                else
                {
                    var now = DateTime.UtcNow;
                    var expiry = token.ExpiryTime;
                    var remaining = (expiry - now).TotalMinutes;

                    Console.WriteLine($"⏰ Şu an (UTC): {now:yyyy-MM-dd HH:mm:ss}");
                    Console.WriteLine($"⏰ Expiry (UTC): {expiry:yyyy-MM-dd HH:mm:ss}");
                    Console.WriteLine($"⏱️ Kalan süre: {remaining:F2} dakika");
                    Console.WriteLine($"🔑 Access Token (ilk 20): {token.access_token?.Substring(0, Math.Min(20, token.access_token.Length))}...");
                    Console.WriteLine($"🔄 Refresh Token (ilk 20): {token.refresh_token?.Substring(0, Math.Min(20, token.refresh_token.Length))}...");

                    if (remaining < 2)
                    {
                        _logger.LogError("❌❌❌ TOKEN SÜRESİ BİTMİŞ! ❌❌❌");
                    }
                    else if (remaining < 5)
                    {
                        _logger.LogWarning("⚠️ Token süresi kritik seviyede!");
                    }
                }
                Console.WriteLine("═══════════════════════════════════════\n");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Token kontrol hatası");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }
}