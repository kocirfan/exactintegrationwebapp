using Microsoft.EntityFrameworkCore;
using ShopifyProductApp.Data;
using ShopifyProductApp.Models;

namespace ShopifyProductApp.Services.PendingOperations
{
    /// <summary>
    /// Bekleyen işlem havuzunu periyodik olarak (varsayılan her 1 dk) işleyen hosted service.
    /// Ayar: App:BackgroundServices:PendingOperationInterval. TriggerNow ile beklemeden uyanır.
    /// </summary>
    public class PendingOperationProcessorService : BackgroundService
    {
        private readonly PendingOperationProcessor _processor;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<PendingOperationProcessorService> _logger;
        private readonly TimeSpan _interval;

        public PendingOperationProcessorService(
            PendingOperationProcessor processor,
            IServiceProvider serviceProvider,
            ILogger<PendingOperationProcessorService> logger,
            IConfiguration configuration)
        {
            _processor = processor;
            _serviceProvider = serviceProvider;
            _logger = logger;

            var intervalStr = configuration["App:BackgroundServices:PendingOperationInterval"] ?? "00:01:00";
            _interval = TimeSpan.TryParse(intervalStr, out var interval) && interval > TimeSpan.Zero
                ? interval
                : TimeSpan.FromMinutes(1);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🚀 Bekleyen işlem havuzu işlemcisi başlatıldı (her {Interval})", _interval);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); // uygulama otursun
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await ResetStuckProcessingAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _processor.RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Havuz işlemcisi döngü hatası");
                }

                try
                {
                    await _processor.WaitForTriggerAsync(_interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Uygulama önceki kapanışta bir siparişi işlerken kesildiyse kayıt Processing'de kalır.
        /// Exact'a gitmiş olma ihtimali olduğundan otomatik tekrar denemek yerine Failed yapılır;
        /// kullanıcı Exact'ta kontrol edip panelden tekrar dener.
        /// </summary>
        private async Task ResetStuckProcessingAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var stuck = await db.PendingOperations
                    .Where(p => p.Status == PendingOperationStatus.Processing)
                    .ToListAsync(cancellationToken);

                if (stuck.Count == 0) return;

                foreach (var op in stuck)
                {
                    op.Status = PendingOperationStatus.Failed;
                    op.LastError = "Uygulama yeniden başlatıldı, işlem yarım kaldı. Exact'ta siparişi kontrol edip gerekirse tekrar deneyin.";
                }

                await db.SaveChangesAsync(cancellationToken);
                _logger.LogWarning("⚠️ Yarım kalan {Count} havuz kaydı Failed yapıldı", stuck.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Yarım kalan havuz kayıtları sıfırlanamadı");
            }
        }
    }
}
