using Microsoft.Extensions.Hosting;

namespace ShopifyProductApp.Services
{
    /// <summary>
    /// Her gece 03:00'te TÜM webshop ürünlerinin fiyatlarını Exact'tan Shopify'a senkronlar.
    /// Motor olarak ManualPriceSyncRunner kullanılır: ürünler Exact'tan batch batch çekilir,
    /// fiyatı değişenler Shopify'da güncellenir, her ürün PriceSyncLogs tablosuna yazılır
    /// (0/negatif fiyat koruması ve değişmeyen fiyatları atlama optimizasyonu dahil).
    /// Saat, appsettings "App:BackgroundServices:PriceSyncTime" ile değiştirilebilir.
    /// </summary>
    public class NightlyPriceSyncBackgroundService : BackgroundService
    {
        private readonly ManualPriceSyncRunner _priceSyncRunner;
        private readonly IConfiguration _configuration;
        private readonly ILogger<NightlyPriceSyncBackgroundService> _logger;

        private const int DEFAULT_BATCH_SIZE = 50;

        public NightlyPriceSyncBackgroundService(
            ManualPriceSyncRunner priceSyncRunner,
            IConfiguration configuration,
            ILogger<NightlyPriceSyncBackgroundService> logger)
        {
            _priceSyncRunner = priceSyncRunner;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var runTime = GetConfiguredRunTime();
            _logger.LogInformation("💶 Nightly Price Sync Service başlatıldı - Her gece {RunTime} 'te tüm ürünlerin fiyatı senkronlanacak",
                runTime.ToString(@"hh\:mm"));

            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.Now;
                    var nextRun = GetNextRunTime(now, runTime);
                    var delay = nextRun - now;

                    _logger.LogInformation("⏰ Sonraki fiyat senkronu: {NextRun}", nextRun.ToString("dd.MM.yyyy HH:mm:ss"));

                    await Task.Delay(delay, stoppingToken);

                    if (stoppingToken.IsCancellationRequested) break;

                    _logger.LogInformation("💶 Gece fiyat senkronizasyonu başlıyor (tüm ürünler)...");

                    // Aynı motoru kullan: dashboard status'unda da canlı görünür
                    if (!_priceSyncRunner.TryStart(DEFAULT_BATCH_SIZE))
                    {
                        _logger.LogWarning("⚠️ Fiyat senkronu zaten çalışıyor (muhtemelen manuel tetiklendi), bu gece atlanıyor");
                        continue;
                    }

                    // Bitmesini bekle ki aynı gün ikinci kez tetiklenmesin ve sonuç loglanabilsin
                    while (_priceSyncRunner.GetStatus().IsRunning && !stoppingToken.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    }

                    var status = _priceSyncRunner.GetStatus();
                    _logger.LogInformation("✅ Gece fiyat senkronizasyonu bitti - İşlenen: {Processed}, Güncellenen: {Updated}, Değişmeyen: {Unchanged}, Atlanan(0 fiyat): {Skipped}, Hatalı: {Error}",
                        status.ProcessedItems, status.PriceUpdatedCount, status.UnchangedCount,
                        status.SkippedZeroPriceCount, status.ErrorCount);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Nightly price sync service hatası: {Error}", ex.Message);
                    _logger.LogInformation("⏳ Hata nedeniyle 1 saat bekleniyor, sonra normal programa devam edilecek...");
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                }
            }
        }

        private TimeSpan GetConfiguredRunTime()
        {
            var configured = _configuration["App:BackgroundServices:PriceSyncTime"];
            if (!string.IsNullOrWhiteSpace(configured) && TimeSpan.TryParse(configured, out var parsed))
                return parsed;

            return new TimeSpan(3, 0, 0); // Varsayılan: 03:00
        }

        private static DateTime GetNextRunTime(DateTime currentTime, TimeSpan runTime)
        {
            var todayRun = currentTime.Date.Add(runTime);
            return currentTime < todayRun ? todayRun : todayRun.AddDays(1);
        }
    }
}
