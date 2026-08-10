using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShopifyProductApp.Data;
using ShopifyProductApp.Models;

namespace ShopifyProductApp.Services
{
    public class PriceSyncLogSaveResult
    {
        public int SavedCount { get; set; }
        public int InsertedCount { get; set; }
        public int UpdatedCount { get; set; }
        public int FailedCount { get; set; }
        public string FallbackFile { get; set; }
    }

    /// <summary>
    /// Fiyat senkron log kayıtlarını DB'ye yazar (upsert: ürün/variant başına tek satır).
    /// Mevcut kayıt varsa üzerine yazılır, PreviousUpdatedAt bir önceki senkron tarihini taşır.
    /// Best-effort çalışır: DB hatası çağıran akışı asla durdurmaz,
    /// yazılamayan kayıtlar yedek JSON dosyasına düşer.
    /// </summary>
    public class PriceSyncLogService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<PriceSyncLogService> _logger;

        public PriceSyncLogService(IServiceProvider serviceProvider, ILogger<PriceSyncLogService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public async Task<PriceSyncLogSaveResult> SaveAsync(List<PriceSyncLog> logEntries)
        {
            var saveResult = new PriceSyncLogSaveResult();

            if (logEntries == null || logEntries.Count == 0)
            {
                return saveResult;
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // Tablo yoksa oluştur (EnsureCreated mevcut DB'ye yeni tablo eklemez)
                await EnsurePriceSyncLogTableAsync(db);

                // Kolon limitlerini aşan veriyi kırp
                foreach (var entry in logEntries)
                {
                    if (entry.ErrorMessage != null && entry.ErrorMessage.Length > 2000)
                        entry.ErrorMessage = entry.ErrorMessage.Substring(0, 2000);
                    if (entry.ProductName != null && entry.ProductName.Length > 512)
                        entry.ProductName = entry.ProductName.Substring(0, 512);
                }

                // Mevcut kayıtları topluca çek (SQL parametre limiti için 1000'lik parçalar)
                var existingRows = new List<PriceSyncLog>();
                var codes = logEntries.Select(e => e.ProductCode).Distinct().ToList();
                foreach (var chunk in codes.Chunk(1000))
                {
                    var chunkList = chunk.ToList();
                    existingRows.AddRange(await db.PriceSyncLogs
                        .Where(l => chunkList.Contains(l.ProductCode))
                        .ToListAsync());
                }

                // (ProductCode, ShopifyVariantId) başına tek satır: en yenisi kalır, eski mükerrerler silinir
                var existingByKey = new Dictionary<(string Code, string VariantId), PriceSyncLog>();
                foreach (var group in existingRows.GroupBy(r => (r.ProductCode, r.ShopifyVariantId ?? "")))
                {
                    var newest = group.OrderByDescending(r => r.UpdatedAt).First();
                    existingByKey[group.Key] = newest;

                    var duplicates = group.Where(r => r.Id != newest.Id).ToList();
                    if (duplicates.Count > 0)
                        db.PriceSyncLogs.RemoveRange(duplicates);
                }

                // Ürün artık bir variantla eşleşiyorsa, o ürünün eski "eşleşmedi" (VariantId boş)
                // kaydı anlamsızdır - silinir ki dashboard'da yanlış "Failed" görünmesin.
                var codesNowMatched = logEntries
                    .Where(e => !string.IsNullOrEmpty(e.ShopifyVariantId))
                    .Select(e => e.ProductCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var kv in existingByKey.Where(k => string.IsNullOrEmpty(k.Key.VariantId)).ToList())
                {
                    if (codesNowMatched.Contains(kv.Key.Code))
                    {
                        db.PriceSyncLogs.Remove(kv.Value);
                        existingByKey.Remove(kv.Key);
                    }
                }

                // Upsert: kayıt varsa üzerine yaz, yoksa ekle
                foreach (var entry in logEntries)
                {
                    var key = (entry.ProductCode, entry.ShopifyVariantId ?? "");
                    if (existingByKey.TryGetValue(key, out var existing))
                    {
                        existing.PreviousUpdatedAt = existing.UpdatedAt; // bir önceki senkron tarihi
                        existing.ExactItemId = entry.ExactItemId;
                        existing.ShopifyProductId = entry.ShopifyProductId;
                        existing.ProductName = entry.ProductName;
                        existing.OldPrice = entry.OldPrice;
                        existing.NewPrice = entry.NewPrice;
                        existing.UpdatedAt = entry.UpdatedAt;
                        existing.Success = entry.Success;
                        existing.ErrorMessage = entry.ErrorMessage;

                        // Çağıranın elindeki kopya da DB ile aynı görünsün (API yanıtlarında kullanılıyor)
                        entry.PreviousUpdatedAt = existing.PreviousUpdatedAt;
                        saveResult.UpdatedCount++;
                    }
                    else
                    {
                        entry.PreviousUpdatedAt = null; // ilk kayıt
                        db.PriceSyncLogs.Add(entry);
                        existingByKey[key] = entry; // aynı batch'te tekrar gelirse update'e düşsün
                        saveResult.InsertedCount++;
                    }
                }

                await db.SaveChangesAsync();
                saveResult.SavedCount = saveResult.InsertedCount + saveResult.UpdatedCount;

                _logger.LogInformation("💾 Fiyat sync logları DB'ye yazıldı: {Saved} kayıt (yeni: {Inserted}, güncellenen: {Updated})",
                    saveResult.SavedCount, saveResult.InsertedCount, saveResult.UpdatedCount);
            }
            catch (Exception ex)
            {
                // DB tamamen erişilemez olsa bile çağıran akış etkilenmez; kayıtlar yedek dosyaya
                _logger.LogError(ex, "❌ Fiyat sync logları DB'ye yazılamadı, yedek JSON'a kaydediliyor");
                saveResult.SavedCount = 0;
                saveResult.InsertedCount = 0;
                saveResult.UpdatedCount = 0;
                saveResult.FailedCount = logEntries.Count;
                saveResult.FallbackFile = WriteFallbackJson(logEntries);
            }

            return saveResult;
        }

        /// <summary>
        /// PriceSyncLogs tablosunu yoksa oluşturur (idempotent).
        /// Startup'ta EnsureCreated kullanıldığı için mevcut veritabanlarına yeni tablo otomatik eklenmiyor.
        /// </summary>
        private static async Task EnsurePriceSyncLogTableAsync(ApplicationDbContext db)
        {
            const string sql = @"
IF OBJECT_ID(N'[PriceSyncLogs]', N'U') IS NULL
BEGIN
    CREATE TABLE [PriceSyncLogs] (
        [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_PriceSyncLogs] PRIMARY KEY,
        [ExactItemId] nvarchar(64) NULL,
        [ShopifyProductId] nvarchar(32) NULL,
        [ShopifyVariantId] nvarchar(32) NULL,
        [ProductCode] nvarchar(128) NOT NULL,
        [ProductName] nvarchar(512) NULL,
        [OldPrice] decimal(18,2) NULL,
        [NewPrice] decimal(18,2) NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        [PreviousUpdatedAt] datetime2 NULL,
        [Success] bit NOT NULL,
        [ErrorMessage] nvarchar(2000) NULL
    );
    CREATE INDEX [IX_PriceSyncLogs_ProductCode] ON [PriceSyncLogs] ([ProductCode]);
    CREATE INDEX [IX_PriceSyncLogs_UpdatedAt] ON [PriceSyncLogs] ([UpdatedAt]);
END";
            await db.Database.ExecuteSqlRawAsync(sql);
        }

        private string WriteFallbackJson(List<PriceSyncLog> entries)
        {
            try
            {
                var dir = Path.Combine(Directory.GetCurrentDirectory(), "Data");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"price_sync_db_fallback_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                var json = System.Text.Json.JsonSerializer.Serialize(entries,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
                _logger.LogWarning("📁 {Count} fiyat sync log kaydı yedek dosyaya yazıldı: {Path}", entries.Count, path);
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
