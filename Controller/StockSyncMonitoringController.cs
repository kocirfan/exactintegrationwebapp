using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopifyProductApp.Data;
using ShopifyProductApp.Services;

namespace ShopifyProductApp.Controllers
{
    /// <summary>
    /// Stok senkronizasyon kayıtlarını (StockSyncLogs) dış monitoring uygulamalarına sunar.
    /// Sadece okuma yapar, senkronu tetiklemez.
    /// </summary>
    [AllowAnonymous]
    [ApiController]
    [Route("api/stock-sync-monitoring")]
    public class StockSyncMonitoringController : ControllerBase
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly ILogger<StockSyncMonitoringController> _logger;
        private readonly ManualStockSyncRunner _manualSyncRunner;

        public StockSyncMonitoringController(
            ApplicationDbContext dbContext,
            ILogger<StockSyncMonitoringController> logger,
            ManualStockSyncRunner manualSyncRunner)
        {
            _dbContext = dbContext;
            _logger = logger;
            _manualSyncRunner = manualSyncRunner;
        }

        /// <summary>
        /// Senkron kayıtlarını sayfalı olarak döner.
        /// </summary>
        /// <param name="pageIndex">0 tabanlı sayfa numarası</param>
        /// <param name="pageSize">Sayfa başına kayıt (1-500)</param>
        /// <param name="search">Ürün kodu veya ürün adında arama</param>
        /// <param name="success">true: sadece başarılı, false: sadece hatalı, boş: hepsi</param>
        /// <param name="changedOnly">true: sadece stoğu değişenler (OldStock != NewStock)</param>
        /// <param name="from">UpdatedAt alt sınırı</param>
        /// <param name="to">UpdatedAt üst sınırı</param>
        [HttpGet("logs")]
        public async Task<IActionResult> GetLogs(
            [FromQuery] int pageIndex = 0,
            [FromQuery] int pageSize = 50,
            [FromQuery] string search = null,
            [FromQuery] bool? success = null,
            [FromQuery] bool changedOnly = false,
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null)
        {
            pageIndex = Math.Max(0, pageIndex);
            pageSize = Math.Clamp(pageSize, 1, 500);

            var query = _dbContext.StockSyncLogs.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(l => l.ProductCode.Contains(term) || l.ProductName.Contains(term));
            }

            if (success.HasValue)
                query = query.Where(l => l.Success == success.Value);

            if (changedOnly)
                query = query.Where(l => l.OldStock == null || l.OldStock != l.NewStock);

            if (from.HasValue)
                query = query.Where(l => l.UpdatedAt >= from.Value);

            if (to.HasValue)
                query = query.Where(l => l.UpdatedAt <= to.Value);

            var totalCount = await query.CountAsync();

            var items = await query
                .OrderByDescending(l => l.UpdatedAt)
                .Skip(pageIndex * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Ok(new
            {
                pageIndex,
                pageSize,
                totalCount,
                totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                items
            });
        }

        /// <summary>
        /// Tek bir ürün kodunun kayıtlarını döner (bir SKU birden çok Shopify variantında olabilir).
        /// </summary>
        [HttpGet("logs/{code}")]
        public async Task<IActionResult> GetLogByCode(string code)
        {
            var logs = await _dbContext.StockSyncLogs
                .AsNoTracking()
                .Where(l => l.ProductCode == code)
                .OrderByDescending(l => l.UpdatedAt)
                .ToListAsync();

            if (logs.Count == 0)
                return NotFound(new { error = $"'{code}' için senkron kaydı bulunamadı" });

            return Ok(new
            {
                productCode = code,
                variantCount = logs.Count,
                lastUpdatedAt = logs.Max(l => l.UpdatedAt),
                items = logs
            });
        }

        /// <summary>
        /// Monitoring dashboard özeti: son senkron zamanı, başarı/hata sayıları, stok değişimleri.
        /// </summary>
        [HttpGet("summary")]
        public async Task<IActionResult> GetSummary()
        {
            var query = _dbContext.StockSyncLogs.AsNoTracking();

            var totalCount = await query.CountAsync();

            if (totalCount == 0)
            {
                return Ok(new
                {
                    totalCount = 0,
                    message = "Henüz senkron kaydı yok"
                });
            }

            var lastSyncAt = await query.MaxAsync(l => l.UpdatedAt);
            var successCount = await query.CountAsync(l => l.Success);
            var errorCount = await query.CountAsync(l => !l.Success);
            var stockChangedCount = await query.CountAsync(l => l.OldStock != null && l.OldStock != l.NewStock);
            var notMatchedCount = await query.CountAsync(l => l.ShopifyVariantId == null);

            // Son senkron turunda işlenen kayıtlar (son senkron zamanından geriye 6 saatlik pencere)
            var lastRunWindowStart = lastSyncAt.AddHours(-6);
            var lastRunCount = await query.CountAsync(l => l.UpdatedAt >= lastRunWindowStart);
            var lastRunErrorCount = await query.CountAsync(l => l.UpdatedAt >= lastRunWindowStart && !l.Success);

            // Son hatalar (ilk 10)
            var recentErrors = await query
                .Where(l => !l.Success)
                .OrderByDescending(l => l.UpdatedAt)
                .Take(10)
                .Select(l => new
                {
                    l.ProductCode,
                    l.ProductName,
                    l.NewStock,
                    l.UpdatedAt,
                    l.ErrorMessage
                })
                .ToListAsync();

            return Ok(new
            {
                totalCount,
                successCount,
                errorCount,
                stockChangedCount,
                notMatchedCount,
                lastSyncAt,
                lastRun = new
                {
                    windowStart = lastRunWindowStart,
                    processedCount = lastRunCount,
                    errorCount = lastRunErrorCount
                },
                recentErrors,
                timestamp = DateTime.Now
            });
        }

        /// <summary>
        /// Ana ekran sayacı: bugün stok güncellemesi başarıyla yapılan ürün adedi.
        /// </summary>
        [HttpGet("today-count")]
        public async Task<IActionResult> GetTodayUpdatedCount()
        {
            var dayStart = DateTime.Today;
            var dayEnd = dayStart.AddDays(1);

            var updatedProductCount = await _dbContext.StockSyncLogs
                .AsNoTracking()
                .Where(l => l.Success && l.UpdatedAt >= dayStart && l.UpdatedAt < dayEnd)
                .Select(l => l.ProductCode)
                .Distinct()
                .CountAsync();

            return Ok(new
            {
                date = dayStart.ToString("yyyy-MM-dd"),
                updatedProductCount
            });
        }

        /// <summary>
        /// Gece 01:30'daki stok senkronunu manuel tetikler. Arka planda çalışır;
        /// ürünler batchSize'lık gruplar halinde işlenir, her batch sonunda DB'ye yazılır.
        /// İlerleme manual-sync/status endpoint'inden izlenir.
        /// </summary>
        /// <param name="batchSize">Batch başına ürün sayısı (varsayılan 50)</param>
        /// <param name="maxItems">Test için: sadece ilk N ürünü işle (boş = tümü)</param>
        [HttpPost("manual-sync")]
        public IActionResult StartManualSync([FromQuery] int batchSize = 50, [FromQuery] int? maxItems = null)
        {
            batchSize = Math.Clamp(batchSize, 1, 500);

            if (!_manualSyncRunner.TryStart(batchSize, maxItems))
            {
                return Conflict(new
                {
                    error = "Manuel senkron zaten çalışıyor",
                    status = _manualSyncRunner.GetStatus()
                });
            }

            _logger.LogInformation("🔄 Manuel stok senkronu tetiklendi (batchSize: {BatchSize}, maxItems: {MaxItems})",
                batchSize, maxItems?.ToString() ?? "tümü");

            return Accepted(new
            {
                message = "Manuel stok senkronu başlatıldı",
                batchSize,
                maxItems,
                statusUrl = "/api/stock-sync-monitoring/manual-sync/status"
            });
        }

        /// <summary>
        /// Manuel senkronun anlık durumu: hangi batch'te, kaç ürün işlendi, hata var mı.
        /// </summary>
        [HttpGet("manual-sync/status")]
        public IActionResult GetManualSyncStatus()
        {
            return Ok(_manualSyncRunner.GetStatus());
        }
    }
}
