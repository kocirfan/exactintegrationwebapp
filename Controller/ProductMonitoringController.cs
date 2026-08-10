using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopifyProductApp.Data;
using ShopifyProductApp.Services;

namespace ShopifyProductApp.Controllers
{
    /// <summary>
    /// Exact'taki webshop ürünlerini dış monitoring uygulamalarına sayfalı sunar.
    /// Her istek Exact'tan tek sayfa çeker (varsayılan 30 ürün) - tüm katalog taranmaz.
    /// </summary>
    [AllowAnonymous]
    [ApiController]
    [Route("api/product-monitoring")]
    public class ProductMonitoringController : ControllerBase
    {
        private readonly ExactService _exactService;
        private readonly ShopifyService _shopifyService;
        private readonly StockSyncLogService _stockSyncLogService;
        private readonly PriceSyncLogService _priceSyncLogService;
        private readonly ManualPriceSyncRunner _priceSyncRunner;
        private readonly ApplicationDbContext _dbContext;
        private readonly ILogger<ProductMonitoringController> _logger;

        public ProductMonitoringController(
            ExactService exactService,
            ShopifyService shopifyService,
            StockSyncLogService stockSyncLogService,
            PriceSyncLogService priceSyncLogService,
            ManualPriceSyncRunner priceSyncRunner,
            ApplicationDbContext dbContext,
            ILogger<ProductMonitoringController> logger)
        {
            _exactService = exactService;
            _shopifyService = shopifyService;
            _stockSyncLogService = stockSyncLogService;
            _priceSyncLogService = priceSyncLogService;
            _priceSyncRunner = priceSyncRunner;
            _dbContext = dbContext;
            _logger = logger;
        }

        /// <summary>
        /// Exact webshop ürünlerini sayfalı listeler.
        /// </summary>
        /// <param name="pageIndex">0 tabanlı sayfa numarası</param>
        /// <param name="pageSize">Sayfa başına ürün (1-100, varsayılan 30)</param>
        /// <param name="search">Ürün kodu veya adında arama</param>
        /// <param name="modifiedAfter">Bu tarihten sonra değişen ürünler</param>
        [HttpGet("products")]
        public async Task<IActionResult> GetWebshopProducts(
            [FromQuery] int pageIndex = 0,
            [FromQuery] int pageSize = 30,
            [FromQuery] string search = null,
            [FromQuery] DateTime? modifiedAfter = null)
        {
            pageIndex = Math.Max(0, pageIndex);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var extraFilter = BuildExtraFilter(search, modifiedAfter);

            var skip = pageIndex * pageSize;
            // İmza: GetWebshopItemsPageAsync(int skip, int top, string extraFilter)
            var items = await _exactService.GetWebshopItemsPageAsync(skip, pageSize, extraFilter);

            if (items == null)
            {
                return StatusCode(502, new { error = "Exact'tan ürünler alınamadı (token veya API hatası)" });
            }

            return Ok(new
            {
                pageIndex,
                pageSize,
                count = items.Count,
                // Sayfa tam doluysa muhtemelen devamı vardır
                hasMore = items.Count == pageSize,
                items = items.Select(i => new
                {
                    Id = i.GetValueOrDefault("ID"),
                    Code = i.GetValueOrDefault("Code"),
                    Description = i.GetValueOrDefault("Description"),
                    Stock = i.GetValueOrDefault("Stock"),
                    Price = i.GetValueOrDefault("StandardSalesPrice"),
                    Unit = i.GetValueOrDefault("Unit"),
                    PictureUrl = i.GetValueOrDefault("PictureUrl"),
                    Created = ParseExactDate(i.GetValueOrDefault("Created")),
                    Modified = ParseExactDate(i.GetValueOrDefault("Modified"))
                }),
                timestamp = DateTime.Now
            });
        }

        /// <summary>
        /// Exact'taki toplam webshop ürün sayısı (ana ekran sayacı / sayfalama toplamı).
        /// Liste ile aynı filtreleri alır - filtreli toplam da hesaplanabilir.
        /// </summary>
        [HttpGet("products/count")]
        public async Task<IActionResult> GetWebshopProductCount(
            [FromQuery] string search = null,
            [FromQuery] DateTime? modifiedAfter = null)
        {
            var extraFilter = BuildExtraFilter(search, modifiedAfter);
            var count = await _exactService.GetWebshopItemsCountAsync(extraFilter);

            if (count == null)
            {
                return StatusCode(502, new { error = "Exact'tan ürün sayısı alınamadı (token veya API hatası)" });
            }

            return Ok(new
            {
                totalWebshopProductCount = count.Value,
                timestamp = DateTime.Now
            });
        }

        /// <summary>
        /// Tek ürün için anlık stok senkronu: Exact'tan güncel stok çekilir,
        /// o SKU'yu taşıyan tüm Shopify ürün/variantlarına yazılır ve StockSyncLogs'a kaydedilir.
        /// Gece senkronuyla aynı güncelleme ve loglama yolunu kullanır.
        /// </summary>
        [HttpPost("products/{code}/sync-stock")]
        public async Task<IActionResult> SyncProductStock(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return BadRequest(new { error = "Ürün kodu gereklidir" });
            }

            code = code.Trim();
            _logger.LogInformation("🔄 Tek ürün stok senkronu istendi: {Code}", code);

            // 1. Exact'tan güncel ürün ve stok bilgisi
            var exactItem = await _exactService.GetItemByCodeAsync(code);
            if (exactItem == null)
            {
                return NotFound(new { error = $"Exact'ta ürün bulunamadı: {code}" });
            }

            if (!exactItem.TryGetValue("Stock", out var stockValue) ||
                !double.TryParse(stockValue?.ToString(), out var exactStock))
            {
                return BadRequest(new { error = $"'{code}' için Exact'ta stok bilgisi okunamadı" });
            }

            // 2. Shopify'ı güncelle + log üret (gece senkronuyla aynı yol)
            // Eşleştirme: exact_product_id → kod → barcode
            var shopifyProducts = await _shopifyService.GetAllProductsRawAsync();
            var stockMatchIndex = await _shopifyService.BuildSingleItemMatchIndexAsync(exactItem);
            try
            {
                var batchResult = await _shopifyService.UpdateMultipleStocksBatchAsync(
                    new List<Dictionary<string, object>> { exactItem },
                    shopifyProducts,
                    "Data/manual_single_stock_sync.json",
                    stockMatchIndex);

                // 3. DB'ye yaz (upsert - monitoring kayıtları güncellenir)
                var saveResult = await _stockSyncLogService.SaveAsync(batchResult.LogEntries);

                bool foundInShopify = batchResult.SuccessCount > 0 || batchResult.ErrorCount > 0;

                return Ok(new
                {
                    code,
                    exactStock = (int)exactStock,
                    foundInShopify,
                    shopify = new
                    {
                        successCount = batchResult.SuccessCount,
                        errorCount = batchResult.ErrorCount
                    },
                    db = new
                    {
                        savedCount = saveResult.SavedCount,
                        failedCount = saveResult.FailedCount
                    },
                    logEntries = batchResult.LogEntries.Select(e => new
                    {
                        e.ProductCode,
                        e.ProductName,
                        e.ShopifyVariantId,
                        e.OldStock,
                        e.NewStock,
                        e.UpdatedAt,
                        e.PreviousUpdatedAt,
                        e.Success,
                        e.ErrorMessage
                    }),
                    timestamp = DateTime.Now
                });
            }
            finally
            {
                shopifyProducts.Dispose();
            }
        }

        /// <summary>
        /// TÜM webshop ürünlerinin fiyatlarını Exact'tan Shopify'a senkronlar.
        /// Arka planda çalışır; ürünler Exact'tan batchSize'lık sayfalar halinde çekilir,
        /// her batch sonunda PriceSyncLogs tablosuna yazılır.
        /// İlerleme price-sync/status endpoint'inden izlenir.
        /// </summary>
        /// <param name="batchSize">Batch başına ürün sayısı (varsayılan 50)</param>
        /// <param name="maxItems">Test için: sadece ilk N ürünü işle (boş = tümü)</param>
        [HttpPost("price-sync")]
        public IActionResult StartPriceSync([FromQuery] int batchSize = 50, [FromQuery] int? maxItems = null)
        {
            batchSize = Math.Clamp(batchSize, 1, 500);

            if (!_priceSyncRunner.TryStart(batchSize, maxItems))
            {
                return Conflict(new
                {
                    error = "Fiyat senkronu zaten çalışıyor",
                    status = _priceSyncRunner.GetStatus()
                });
            }

            _logger.LogInformation("💶 Manuel fiyat senkronu tetiklendi (batchSize: {BatchSize}, maxItems: {MaxItems})",
                batchSize, maxItems?.ToString() ?? "tümü");

            return Accepted(new
            {
                message = "Fiyat senkronu başlatıldı",
                batchSize,
                maxItems,
                statusUrl = "/api/product-monitoring/price-sync/status"
            });
        }

        /// <summary>
        /// Fiyat senkronunun anlık durumu.
        /// </summary>
        [HttpGet("price-sync/status")]
        public IActionResult GetPriceSyncStatus()
        {
            return Ok(_priceSyncRunner.GetStatus());
        }

        /// <summary>
        /// Tek ürün için anlık fiyat senkronu: Exact'tan güncel fiyat çekilir,
        /// o SKU'yu taşıyan tüm Shopify ürün/variantlarına yazılır ve PriceSyncLogs'a kaydedilir.
        /// </summary>
        [HttpPost("products/{code}/sync-price")]
        public async Task<IActionResult> SyncProductPrice(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return BadRequest(new { error = "Ürün kodu gereklidir" });
            }

            code = code.Trim();
            _logger.LogInformation("💶 Tek ürün fiyat senkronu istendi: {Code}", code);

            // 1. Exact'tan güncel ürün ve fiyat bilgisi
            var exactItem = await _exactService.GetItemByCodeAsync(code);
            if (exactItem == null)
            {
                return NotFound(new { error = $"Exact'ta ürün bulunamadı: {code}" });
            }

            decimal exactPrice = 0;
            if (exactItem.TryGetValue("StandardSalesPrice", out var priceVal))
                exactPrice = ShopifyService.ConvertToDecimalSafe(priceVal);

            // 2. Shopify'ı güncelle + log üret (eşleştirme: exact_product_id → kod → barcode)
            var (byExactId, bySku, byBarcode) = await _shopifyService.BuildSingleItemMatchIndexAsync(exactItem);
            {
                var batchResult = await _shopifyService.UpdateMultiplePricesBatchAsync(
                    new List<Dictionary<string, object>> { exactItem },
                    byExactId, bySku, byBarcode);

                // 3. DB'ye yaz (upsert)
                var saveResult = await _priceSyncLogService.SaveAsync(batchResult.LogEntries);

                bool foundInShopify = batchResult.LogEntries.Any(e => e.ShopifyVariantId != null);

                return Ok(new
                {
                    code,
                    exactPrice,
                    foundInShopify,
                    shopify = new
                    {
                        updatedCount = batchResult.SuccessCount,
                        unchangedCount = batchResult.UnchangedCount,
                        skippedZeroPriceCount = batchResult.SkippedZeroPriceCount,
                        errorCount = batchResult.ErrorCount
                    },
                    db = new
                    {
                        savedCount = saveResult.SavedCount,
                        failedCount = saveResult.FailedCount
                    },
                    logEntries = batchResult.LogEntries.Select(e => new
                    {
                        e.ProductCode,
                        e.ProductName,
                        e.ShopifyVariantId,
                        e.OldPrice,
                        e.NewPrice,
                        e.UpdatedAt,
                        e.PreviousUpdatedAt,
                        e.Success,
                        e.ErrorMessage
                    }),
                    timestamp = DateTime.Now
                });
            }
        }

        /// <summary>
        /// PriceSyncLogs tablosundaki fiyat senkron kayıtlarını sayfalı listeler.
        /// </summary>
        [HttpGet("price-logs")]
        public async Task<IActionResult> GetPriceLogs(
            [FromQuery] int pageIndex = 0,
            [FromQuery] int pageSize = 50,
            [FromQuery] string search = null,
            [FromQuery] bool? success = null,
            [FromQuery] bool onlyErrors = false,
            [FromQuery] bool changedOnly = false)
        {
            pageIndex = Math.Max(0, pageIndex);
            pageSize = Math.Clamp(pageSize, 1, 500);

            var query = _dbContext.PriceSyncLogs.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(l => l.ProductCode.Contains(term) || l.ProductName.Contains(term));
            }

            // onlyErrors, dashboard endpoint'leriyle aynı adlandırma; success ile aynı işi yapar
            if (onlyErrors)
                query = query.Where(l => !l.Success);
            else if (success.HasValue)
                query = query.Where(l => l.Success == success.Value);

            if (changedOnly)
                query = query.Where(l => l.OldPrice == null || l.OldPrice != l.NewPrice);

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

        // Query parametrelerinden Exact OData filtresi üretir (null = filtre yok)
        // NOT: Stock alanı Exact tarafında filtrelenemiyor (NotImplemented) - stok filtresi eklenemez
        private static string BuildExtraFilter(string search, DateTime? modifiedAfter)
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(search))
            {
                // OData string literal içinde tek tırnak escape edilir
                var term = search.Trim().Replace("'", "''");
                parts.Add($"(substringof('{term}',Code) eq true or substringof('{term}',Description) eq true)");
            }

            if (modifiedAfter.HasValue)
            {
                parts.Add($"Modified ge datetime'{modifiedAfter.Value:yyyy-MM-ddTHH:mm:ss}'");
            }

            return parts.Count > 0 ? string.Join(" and ", parts) : null;
        }

        // Exact'ın "/Date(1767873703633)/" formatını okunabilir tarihe çevirir
        private static DateTime? ParseExactDate(object value)
        {
            var s = value?.ToString();
            if (string.IsNullOrWhiteSpace(s))
                return null;

            var match = System.Text.RegularExpressions.Regex.Match(s, @"\/Date\((\d+)");
            if (match.Success && long.TryParse(match.Groups[1].Value, out var ms))
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
            }

            return DateTime.TryParse(s, out var parsed) ? parsed : null;
        }
    }
}
