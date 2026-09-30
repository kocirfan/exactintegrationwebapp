using Microsoft.EntityFrameworkCore;
using ShopifyProductApp.Data;
using ShopifyProductApp.Models;
using ShopifyProductApp.Services.Notifications;

namespace ShopifyProductApp.Services.PendingOperations
{
    public class PendingOperationProcessorStatus
    {
        public bool IsRunning { get; set; }
        public DateTime? LastStartedAt { get; set; }
        public DateTime? LastFinishedAt { get; set; }
        public int LastProcessed { get; set; }
        public int LastSucceeded { get; set; }
        public int LastFailed { get; set; }
        public string LastMessage { get; set; }
        public string LastError { get; set; }
        public int TotalSucceededSinceStart { get; set; }
        public int TotalFailedSinceStart { get; set; }
    }

    public class PendingOperationRunResult
    {
        public bool Ran { get; set; }
        public string Message { get; set; }
        public int Processed { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
    }

    /// <summary>
    /// Havuzdaki Pending sipariş aktarımlarını Exact token'ı müsaitken sırayla işler (singleton).
    ///
    /// Periyodik olarak PendingOperationProcessorService çağırır; panel "şimdi işle" dediğinde
    /// ya da token geri geldiğinde TriggerNow ile beklemeden tetiklenir.
    /// Aynı anda yalnızca bir çalıştırma olur (_runLock).
    ///
    /// Sonuç kuralları:
    ///  - başarı veya "zaten aktarılmış"  → Completed
    ///  - Shopify'da iptal edilmiş        → Cancelled (Exact'a gönderilmez)
    ///  - hata + token hâlâ var           → Failed   (panelden manuel tekrar)
    ///  - hata + token bu arada gitti     → Pending  (token gelince otomatik tekrar)
    /// </summary>
    public class PendingOperationProcessor
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<PendingOperationProcessor> _logger;
        private readonly SemaphoreSlim _runLock = new(1, 1);
        private readonly SemaphoreSlim _trigger = new(0, 1);
        private readonly int _batchSize;

        public PendingOperationProcessorStatus Status { get; } = new();

        public PendingOperationProcessor(
            IServiceProvider serviceProvider,
            ILogger<PendingOperationProcessor> logger,
            IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _batchSize = int.TryParse(configuration["App:BackgroundServices:PendingOperationBatchSize"], out var b) && b > 0 ? b : 20;
        }

        /// <summary>Periyodik bekleme süresini beklemeden bir sonraki çalıştırmayı tetikler.</summary>
        public bool TriggerNow()
        {
            if (_trigger.CurrentCount > 0) return false; // zaten tetiklenmiş, bekleyen bir sinyal var
            try
            {
                _trigger.Release();
                return true;
            }
            catch (SemaphoreFullException)
            {
                return false;
            }
        }

        /// <summary>Süre dolana ya da TriggerNow çağrılana kadar bekler.</summary>
        public Task<bool> WaitForTriggerAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            _trigger.WaitAsync(timeout, cancellationToken);

        public async Task<PendingOperationRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
        {
            var result = new PendingOperationRunResult();

            if (!await _runLock.WaitAsync(0, cancellationToken))
            {
                result.Message = "İşlemci zaten çalışıyor";
                return result;
            }

            try
            {
                result.Ran = true;
                Status.IsRunning = true;
                Status.LastStartedAt = DateTime.UtcNow;
                Status.LastError = null;
                Status.LastMessage = null;
                Status.LastProcessed = 0;
                Status.LastSucceeded = 0;
                Status.LastFailed = 0;

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var tokenMonitor = scope.ServiceProvider.GetRequiredService<TokenOutageMonitor>();
                var orderCrud = scope.ServiceProvider.GetRequiredService<ShopifyOrderCrud>();

                var pending = await db.PendingOperations
                    .Where(p => p.Status == PendingOperationStatus.Pending &&
                                p.OperationType == PendingOperationTypes.OrderTransfer)
                    .OrderBy(p => p.CreatedAt)
                    .Take(_batchSize)
                    .ToListAsync(cancellationToken);

                if (pending.Count == 0)
                {
                    Status.LastMessage = "Bekleyen işlem yok";
                    result.Message = Status.LastMessage;
                    return result;
                }

                if (!await tokenMonitor.IsTokenAvailableAsync())
                {
                    Status.LastMessage = $"Exact token yok, {pending.Count} işlem beklemede";
                    result.Message = Status.LastMessage;
                    _logger.LogWarning("⏸️ {Message}", Status.LastMessage);
                    return result;
                }

                _logger.LogInformation("🏊 Havuz işleniyor: {Count} bekleyen sipariş", pending.Count);

                foreach (var op in pending)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Her siparişten önce tekrar bak: token ortada giderse kalanlar Pending kalır
                    if (!await tokenMonitor.IsTokenAvailableAsync())
                    {
                        Status.LastMessage = "Token işlem sırasında kayboldu, kalan siparişler beklemede";
                        _logger.LogWarning("⏸️ {Message}", Status.LastMessage);
                        break;
                    }

                    op.Status = PendingOperationStatus.Processing;
                    op.AttemptCount++;
                    op.LastAttemptAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);

                    try
                    {
                        var transfer = await orderCrud.TransferOrderToExactAsync(op.ShopifyOrderId);

                        if (transfer.ShopifyOrderNumber.HasValue) op.ShopifyOrderNumber = transfer.ShopifyOrderNumber;
                        if (!string.IsNullOrEmpty(transfer.ShopifyOrderName)) op.ShopifyOrderName = transfer.ShopifyOrderName;

                        if (transfer.Success || transfer.AlreadyProcessed)
                        {
                            op.Status = PendingOperationStatus.Completed;
                            op.CompletedAt = DateTime.UtcNow;
                            op.ExactOrderGuid = transfer.ExactOrderGuid;
                            op.ExactOrderNumber = transfer.ExactOrderNumber;
                            op.LastError = transfer.AlreadyProcessed
                                ? "Sipariş zaten aktarılmıştı (ProcessedOrders kaydı var)"
                                : Truncate(transfer.Error, 2000);
                            result.Succeeded++;
                            Status.TotalSucceededSinceStart++;
                            _logger.LogInformation("✅ Havuz: sipariş aktarıldı {OrderId} → Exact {ExactNo}",
                                op.ShopifyOrderId, transfer.ExactOrderNumber ?? "(zaten vardı)");
                        }
                        else if (transfer.Skipped)
                        {
                            op.Status = PendingOperationStatus.Cancelled;
                            op.LastError = Truncate(transfer.Error, 2000);
                            _logger.LogWarning("⏭️ Havuz: sipariş atlandı {OrderId}: {Error}", op.ShopifyOrderId, transfer.Error);
                        }
                        else
                        {
                            var tokenStillOk = await tokenMonitor.IsTokenAvailableAsync();
                            op.Status = tokenStillOk ? PendingOperationStatus.Failed : PendingOperationStatus.Pending;
                            op.LastError = Truncate(transfer.Error ?? "Exact'a gönderim başarısız", 2000);
                            result.Failed++;
                            Status.TotalFailedSinceStart++;
                            _logger.LogError("❌ Havuz: sipariş aktarılamadı {OrderId}: {Error} (yeni durum: {Status})",
                                op.ShopifyOrderId, op.LastError, op.Status);
                        }
                    }
                    catch (Exception ex)
                    {
                        op.Status = PendingOperationStatus.Failed;
                        op.LastError = Truncate(ex.Message, 2000);
                        result.Failed++;
                        Status.TotalFailedSinceStart++;
                        _logger.LogError(ex, "❌ Havuz: sipariş işlenirken hata {OrderId}", op.ShopifyOrderId);
                    }

                    result.Processed++;
                    await db.SaveChangesAsync(cancellationToken);
                }

                Status.LastProcessed = result.Processed;
                Status.LastSucceeded = result.Succeeded;
                Status.LastFailed = result.Failed;
                Status.LastMessage ??= $"{result.Processed} işlendi: {result.Succeeded} başarılı, {result.Failed} hatalı";
                result.Message = Status.LastMessage;
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Status.LastError = ex.Message;
                result.Message = "Hata: " + ex.Message;
                _logger.LogError(ex, "❌ Havuz işlemcisi hatası");
                return result;
            }
            finally
            {
                Status.IsRunning = false;
                Status.LastFinishedAt = DateTime.UtcNow;
                _runLock.Release();
            }
        }

        private static string Truncate(string value, int max) =>
            string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
    }
}
