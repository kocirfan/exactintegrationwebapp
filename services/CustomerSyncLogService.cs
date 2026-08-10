using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShopifyProductApp.Data;
using ShopifyProductApp.Models;

namespace ShopifyProductApp.Services
{
    public class CustomerSyncLogSaveResult
    {
        public int SavedCount { get; set; }
        public int InsertedCount { get; set; }
        public int UpdatedCount { get; set; }
        public int FailedCount { get; set; }
        public string FallbackFile { get; set; }
    }

    /// <summary>
    /// Müşteri senkron log kayıtlarını DB'ye yazar (upsert: email başına tek satır).
    /// Best-effort çalışır: DB hatası çağıran akışı asla durdurmaz,
    /// yazılamayan kayıtlar yedek JSON dosyasına düşer.
    /// </summary>
    public class CustomerSyncLogService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<CustomerSyncLogService> _logger;

        public CustomerSyncLogService(IServiceProvider serviceProvider, ILogger<CustomerSyncLogService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public async Task<CustomerSyncLogSaveResult> SaveAsync(List<CustomerSyncLog> logEntries)
        {
            var saveResult = new CustomerSyncLogSaveResult();

            if (logEntries == null || logEntries.Count == 0)
            {
                return saveResult;
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                await EnsureCustomerSyncLogTableAsync(db);

                // Kolon limitlerini aşan veriyi kırp; email boşsa Exact ID anahtar olur
                foreach (var entry in logEntries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Email))
                        entry.Email = $"(email yok) {entry.ExactCustomerId}";
                    if (entry.Email.Length > 256)
                        entry.Email = entry.Email.Substring(0, 256);
                    if (entry.ErrorMessage != null && entry.ErrorMessage.Length > 2000)
                        entry.ErrorMessage = entry.ErrorMessage.Substring(0, 2000);
                    if (entry.CustomerName != null && entry.CustomerName.Length > 512)
                        entry.CustomerName = entry.CustomerName.Substring(0, 512);
                }

                // Mevcut kayıtları topluca çek.
                // Anahtar önceliği: Exact müşteri ID (kalıcı/tekil) > email (değişebilir).
                var existingRows = new List<CustomerSyncLog>();
                var emails = logEntries.Select(e => e.Email).Distinct().ToList();
                var exactIds = logEntries.Select(e => e.ExactCustomerId)
                    .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();

                foreach (var chunk in emails.Chunk(1000))
                {
                    var chunkList = chunk.ToList();
                    existingRows.AddRange(await db.CustomerSyncLogs
                        .Where(l => chunkList.Contains(l.Email))
                        .ToListAsync());
                }

                foreach (var chunk in exactIds.Chunk(1000))
                {
                    var chunkList = chunk.ToList();
                    existingRows.AddRange(await db.CustomerSyncLogs
                        .Where(l => l.ExactCustomerId != null && chunkList.Contains(l.ExactCustomerId))
                        .ToListAsync());
                }

                existingRows = existingRows.GroupBy(r => r.Id).Select(g => g.First()).ToList();

                // Aynı müşteriyi işaret eden satırlar: en yenisi kalır, eskiler silinir.
                // ID'si olanlar ID'ye, olmayanlar email'e göre gruplanır.
                var existingByExactId = new Dictionary<string, CustomerSyncLog>(StringComparer.OrdinalIgnoreCase);
                var existingByEmail = new Dictionary<string, CustomerSyncLog>(StringComparer.OrdinalIgnoreCase);

                foreach (var group in existingRows.GroupBy(r =>
                             !string.IsNullOrWhiteSpace(r.ExactCustomerId) ? $"id:{r.ExactCustomerId}" : $"mail:{r.Email}",
                             StringComparer.OrdinalIgnoreCase))
                {
                    var newest = group.OrderByDescending(r => r.UpdatedAt).First();

                    var duplicates = group.Where(r => r.Id != newest.Id).ToList();
                    if (duplicates.Count > 0)
                        db.CustomerSyncLogs.RemoveRange(duplicates);

                    if (!string.IsNullOrWhiteSpace(newest.ExactCustomerId))
                        existingByExactId[newest.ExactCustomerId] = newest;
                    if (!string.IsNullOrWhiteSpace(newest.Email))
                        existingByEmail[newest.Email] = newest;
                }

                // Upsert: önce ID ile eşleştir, bulunamazsa email ile
                foreach (var entry in logEntries)
                {
                    CustomerSyncLog existing = null;

                    if (!string.IsNullOrWhiteSpace(entry.ExactCustomerId))
                        existingByExactId.TryGetValue(entry.ExactCustomerId, out existing);

                    if (existing == null && !string.IsNullOrWhiteSpace(entry.Email))
                        existingByEmail.TryGetValue(entry.Email, out existing);

                    if (existing != null)
                    {
                        existing.PreviousUpdatedAt = existing.UpdatedAt;
                        existing.ExactCustomerId = entry.ExactCustomerId;
                        existing.CustomerCode = entry.CustomerCode;
                        existing.CustomerName = entry.CustomerName;
                        existing.Email = entry.Email; // email değiştiyse yeni değere güncellenir
                        existing.UpdatedAt = entry.UpdatedAt;
                        existing.Success = entry.Success;
                        existing.ErrorMessage = entry.ErrorMessage;

                        // Çağıranın elindeki kopya da DB ile aynı görünsün
                        entry.PreviousUpdatedAt = existing.PreviousUpdatedAt;
                        saveResult.UpdatedCount++;
                    }
                    else
                    {
                        entry.PreviousUpdatedAt = null;
                        db.CustomerSyncLogs.Add(entry);
                        saveResult.InsertedCount++;
                        existing = entry;
                    }

                    // Aynı batch'te tekrar gelirse update'e düşsün
                    if (!string.IsNullOrWhiteSpace(existing.ExactCustomerId))
                        existingByExactId[existing.ExactCustomerId] = existing;
                    if (!string.IsNullOrWhiteSpace(existing.Email))
                        existingByEmail[existing.Email] = existing;
                }

                await db.SaveChangesAsync();
                saveResult.SavedCount = saveResult.InsertedCount + saveResult.UpdatedCount;

                _logger.LogInformation("💾 Müşteri sync logları DB'ye yazıldı: {Saved} kayıt (yeni: {Inserted}, güncellenen: {Updated})",
                    saveResult.SavedCount, saveResult.InsertedCount, saveResult.UpdatedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Müşteri sync logları DB'ye yazılamadı, yedek JSON'a kaydediliyor");
                saveResult.SavedCount = 0;
                saveResult.InsertedCount = 0;
                saveResult.UpdatedCount = 0;
                saveResult.FailedCount = logEntries.Count;
                saveResult.FallbackFile = WriteFallbackJson(logEntries);
            }

            return saveResult;
        }

        private static async Task EnsureCustomerSyncLogTableAsync(ApplicationDbContext db)
        {
            const string sql = @"
IF OBJECT_ID(N'[CustomerSyncLogs]', N'U') IS NULL
BEGIN
    CREATE TABLE [CustomerSyncLogs] (
        [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_CustomerSyncLogs] PRIMARY KEY,
        [ExactCustomerId] nvarchar(64) NULL,
        [CustomerCode] nvarchar(64) NULL,
        [Email] nvarchar(256) NOT NULL,
        [CustomerName] nvarchar(512) NULL,
        [UpdatedAt] datetime2 NOT NULL,
        [PreviousUpdatedAt] datetime2 NULL,
        [Success] bit NOT NULL,
        [ErrorMessage] nvarchar(2000) NULL
    );
    CREATE INDEX [IX_CustomerSyncLogs_Email] ON [CustomerSyncLogs] ([Email]);
    CREATE INDEX [IX_CustomerSyncLogs_UpdatedAt] ON [CustomerSyncLogs] ([UpdatedAt]);
END";
            await db.Database.ExecuteSqlRawAsync(sql);
        }

        private string WriteFallbackJson(List<CustomerSyncLog> entries)
        {
            try
            {
                var dir = Path.Combine(Directory.GetCurrentDirectory(), "Data");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"customer_sync_db_fallback_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                var json = System.Text.Json.JsonSerializer.Serialize(entries,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
                _logger.LogWarning("📁 {Count} müşteri sync log kaydı yedek dosyaya yazıldı: {Path}", entries.Count, path);
                return path;
            }
            catch (Exception ex)
            {
                _logger.LogError("❌ Yedek JSON dosyası da yazılamadı: {Error}", ex.Message);
                return null;
            }
        }
    }
}
