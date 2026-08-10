using Microsoft.Extensions.Hosting;

namespace ShopifyProductApp.Services
{
    /// <summary>
    /// Her gece 04:30'da Exact'ta son 24 saatte değişen müşterileri Shopify'a senkronlar.
    /// Motor olarak ManualCustomerSyncRunner kullanılır: her müşteri Shopify'da güncellenir
    /// ve sonuç CustomerSyncLogs tablosuna yazılır (dashboard'dan izlenebilir).
    /// Saat, appsettings "App:BackgroundServices:CustomerSyncTime" ile değiştirilebilir.
    /// </summary>
    public class NightlyCustomerSyncBackgroundService : BackgroundService
    {
        private readonly ManualCustomerSyncRunner _customerSyncRunner;
        private readonly IConfiguration _configuration;
        private readonly ILogger<NightlyCustomerSyncBackgroundService> _logger;

        private const int LOOKBACK_HOURS = 24;

        public NightlyCustomerSyncBackgroundService(
            ManualCustomerSyncRunner customerSyncRunner,
            IConfiguration configuration,
            ILogger<NightlyCustomerSyncBackgroundService> logger)
        {
            _customerSyncRunner = customerSyncRunner;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var runTime = GetConfiguredRunTime();
            _logger.LogInformation("👥 Nightly Customer Sync Service başlatıldı - Her gece {RunTime}'te son {Hours} saatte değişen müşteriler senkronlanacak",
                runTime.ToString(@"hh\:mm"), LOOKBACK_HOURS);

            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.Now;
                    var nextRun = GetNextRunTime(now, runTime);
                    var delay = nextRun - now;

                    _logger.LogInformation("⏰ Sonraki müşteri senkronu: {NextRun}", nextRun.ToString("dd.MM.yyyy HH:mm:ss"));

                    await Task.Delay(delay, stoppingToken);

                    if (stoppingToken.IsCancellationRequested) break;

                    _logger.LogInformation("👥 Gece müşteri senkronizasyonu başlıyor (son {Hours} saat)...", LOOKBACK_HOURS);

                    // Aynı motoru kullan: dashboard status'unda da canlı görünür
                    if (!_customerSyncRunner.TryStart(LOOKBACK_HOURS))
                    {
                        _logger.LogWarning("⚠️ Müşteri senkronu zaten çalışıyor (muhtemelen manuel tetiklendi), bu gece atlanıyor");
                        continue;
                    }

                    // Bitmesini bekle ki aynı gün ikinci kez tetiklenmesin ve sonuç loglanabilsin
                    while (_customerSyncRunner.GetStatus().IsRunning && !stoppingToken.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    }

                    var status = _customerSyncRunner.GetStatus();
                    _logger.LogInformation("✅ Gece müşteri senkronizasyonu bitti - İşlenen: {Processed}, Başarılı: {Success}, Hatalı: {Error}",
                        status.ProcessedCustomers, status.SuccessCount, status.ErrorCount);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Nightly customer sync service hatası: {Error}", ex.Message);
                    _logger.LogInformation("⏳ Hata nedeniyle 1 saat bekleniyor, sonra normal programa devam edilecek...");
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                }
            }
        }

        private TimeSpan GetConfiguredRunTime()
        {
            var configured = _configuration["App:BackgroundServices:CustomerSyncTime"];
            if (!string.IsNullOrWhiteSpace(configured) && TimeSpan.TryParse(configured, out var parsed))
                return parsed;

            return new TimeSpan(4, 30, 0); // Varsayılan: 04:30
        }

        private static DateTime GetNextRunTime(DateTime currentTime, TimeSpan runTime)
        {
            var todayRun = currentTime.Date.Add(runTime);
            return currentTime < todayRun ? todayRun : todayRun.AddDays(1);
        }
    }
}
