using Microsoft.EntityFrameworkCore;
using ShopifyProductApp.Data;
using ShopifyProductApp.Models;

namespace ShopifyProductApp.Services.PendingOperations
{
    /// <summary>
    /// Bekleyen işlem havuzunun DB katmanı (scoped).
    /// Webhook (havuza alma), işlemci (durum güncelleme) ve panel controller'ı (retry/cancel) kullanır.
    /// </summary>
    public class PendingOperationService
    {
        private readonly ApplicationDbContext _db;
        private readonly ILogger<PendingOperationService> _logger;

        public PendingOperationService(ApplicationDbContext db, ILogger<PendingOperationService> logger)
        {
            _db = db;
            _logger = logger;
        }

        /// <summary>Sipariş havuzda bekliyor ya da şu an işleniyor mu?</summary>
        public Task<bool> IsQueuedAsync(long shopifyOrderId) =>
            _db.PendingOperations.AnyAsync(p =>
                p.ShopifyOrderId == shopifyOrderId &&
                (p.Status == PendingOperationStatus.Pending || p.Status == PendingOperationStatus.Processing));

        /// <summary>
        /// Siparişi havuza ekler (upsert):
        ///  - kayıt yoksa oluşturur,
        ///  - Pending / Processing / Completed ise dokunmaz,
        ///  - Failed / Cancelled ise istenen duruma çevirir (yeni deneme).
        /// </summary>
        public async Task<PendingOperation> EnqueueOrderAsync(
            long shopifyOrderId,
            long? shopifyOrderNumber,
            string shopifyOrderName,
            string payload,
            string reason,
            string source,
            string status = PendingOperationStatus.Pending,
            string error = null)
        {
            var existing = await _db.PendingOperations.FirstOrDefaultAsync(p => p.ShopifyOrderId == shopifyOrderId);
            if (existing != null)
            {
                return await UpdateExistingAsync(existing, shopifyOrderNumber, shopifyOrderName, payload, reason, source, status, error);
            }

            var op = new PendingOperation
            {
                OperationType = PendingOperationTypes.OrderTransfer,
                ShopifyOrderId = shopifyOrderId,
                ShopifyOrderNumber = shopifyOrderNumber,
                ShopifyOrderName = shopifyOrderName,
                Status = status,
                Reason = reason,
                Source = source,
                Payload = payload,
                LastError = Truncate(error, 2000),
                AttemptCount = 0,
                CreatedAt = DateTime.UtcNow
            };

            try
            {
                _db.PendingOperations.Add(op);
                await _db.SaveChangesAsync();
                _logger.LogInformation("🏊 Sipariş havuza alındı: {OrderId} (#{OrderNumber}) → {Status} ({Reason})",
                    shopifyOrderId, shopifyOrderNumber, status, reason);
                return op;
            }
            catch (DbUpdateException)
            {
                // Aynı anda gelen ikinci webhook unique index'e takıldı → mevcut kaydı döndür
                _db.Entry(op).State = EntityState.Detached;
                var raced = await _db.PendingOperations.FirstOrDefaultAsync(p => p.ShopifyOrderId == shopifyOrderId);
                if (raced != null) return raced;
                throw;
            }
        }

        private async Task<PendingOperation> UpdateExistingAsync(
            PendingOperation existing, long? shopifyOrderNumber, string shopifyOrderName,
            string payload, string reason, string source, string status, string error)
        {
            if (existing.Status == PendingOperationStatus.Pending ||
                existing.Status == PendingOperationStatus.Processing ||
                existing.Status == PendingOperationStatus.Completed)
            {
                return existing;
            }

            existing.Status = status;
            existing.Reason = reason;
            existing.Source = source;
            existing.LastError = Truncate(error, 2000);
            existing.CompletedAt = null;
            if (!string.IsNullOrEmpty(payload)) existing.Payload = payload;
            if (shopifyOrderNumber.HasValue) existing.ShopifyOrderNumber = shopifyOrderNumber;
            if (!string.IsNullOrEmpty(shopifyOrderName)) existing.ShopifyOrderName = shopifyOrderName;

            await _db.SaveChangesAsync();
            _logger.LogInformation("🏊 Havuz kaydı güncellendi: {OrderId} → {Status} ({Reason})",
                existing.ShopifyOrderId, status, reason);
            return existing;
        }

        /// <summary>Webhook (veya başka bir yol) siparişi başarıyla aktardıysa havuzdaki kaydı Completed yapar.</summary>
        public async Task MarkCompletedAsync(long shopifyOrderId, Guid? exactOrderGuid, string exactOrderNumber, string note = null)
        {
            var existing = await _db.PendingOperations.FirstOrDefaultAsync(p => p.ShopifyOrderId == shopifyOrderId);
            if (existing == null || existing.Status == PendingOperationStatus.Completed) return;

            existing.Status = PendingOperationStatus.Completed;
            existing.CompletedAt = DateTime.UtcNow;
            existing.ExactOrderGuid = exactOrderGuid;
            existing.ExactOrderNumber = exactOrderNumber;
            existing.LastError = note;
            await _db.SaveChangesAsync();
        }

        /// <summary>Failed / Cancelled → Pending (panelden "tekrar dene").</summary>
        public async Task<(bool ok, string message, PendingOperation op)> RetryAsync(int id)
        {
            var op = await _db.PendingOperations.FindAsync(id);
            if (op == null) return (false, "Kayıt bulunamadı", null);

            if (op.Status != PendingOperationStatus.Failed && op.Status != PendingOperationStatus.Cancelled)
                return (false, $"Yalnızca Failed / Cancelled kayıtlar tekrar denenebilir (mevcut durum: {op.Status})", op);

            op.Status = PendingOperationStatus.Pending;
            op.LastError = null;
            op.CompletedAt = null;
            op.Reason = "Panelden tekrar denendi";
            await _db.SaveChangesAsync();
            return (true, "Kayıt tekrar kuyruğa alındı", op);
        }

        /// <summary>Pending / Failed → Cancelled (panelden "iptal").</summary>
        public async Task<(bool ok, string message, PendingOperation op)> CancelAsync(int id)
        {
            var op = await _db.PendingOperations.FindAsync(id);
            if (op == null) return (false, "Kayıt bulunamadı", null);

            if (op.Status != PendingOperationStatus.Pending && op.Status != PendingOperationStatus.Failed)
                return (false, $"Yalnızca Pending / Failed kayıtlar iptal edilebilir (mevcut durum: {op.Status})", op);

            op.Status = PendingOperationStatus.Cancelled;
            await _db.SaveChangesAsync();
            return (true, "Kayıt iptal edildi", op);
        }

        /// <summary>Durum bazında adetler (tüm durumlar için anahtar döner, olmayanlar 0).</summary>
        public async Task<Dictionary<string, int>> GetCountsAsync()
        {
            var groups = await _db.PendingOperations
                .GroupBy(p => p.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync();

            var counts = PendingOperationStatus.All.ToDictionary(s => s, _ => 0);
            foreach (var g in groups) counts[g.Status] = g.Count;
            return counts;
        }

        public Task<int> CountPendingAsync() =>
            _db.PendingOperations.CountAsync(p => p.Status == PendingOperationStatus.Pending);

        private static string Truncate(string value, int max) =>
            string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
    }
}
