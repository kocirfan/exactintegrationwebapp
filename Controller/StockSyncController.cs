using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopifyProductApp.Data;
using ShopifyProductApp.Services;

namespace ShopifyProductApp.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class StockSyncController : ControllerBase
    {
        private readonly ExactService _exactService;
        private readonly ShopifyService _shopifyService;
        private readonly ILogger<StockSyncController> _logger;
        private readonly StockSyncLogService _stockSyncLogService;
        private readonly ApplicationDbContext _dbContext;

        public StockSyncController(
            ExactService exactService,
            ShopifyService shopifyService,
            ILogger<StockSyncController> logger,
            StockSyncLogService stockSyncLogService,
            ApplicationDbContext dbContext)
        {
            _exactService = exactService;
            _shopifyService = shopifyService;
            _logger = logger;
            _stockSyncLogService = stockSyncLogService;
            _dbContext = dbContext;
        }

        // Verilen ürün koduyla Exact'ta ürünü bulur, "Stock" değerini alır ve
        // aynı kodu SKU olarak taşıyan tüm Shopify ürün/varyantlarına bu stok değerini yazar.
        [HttpGet("by-code")]
        public async Task<IActionResult> UpdateStockByCode([FromQuery] string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return BadRequest(new { error = "code parametresi gereklidir" });
            }

            var item = await _exactService.GetItemByCodeAsync(code);
            if (item == null)
            {
                return NotFound(new { error = $"Ürün bulunamadı: {code}" });
            }

            if (!item.TryGetValue("Stock", out var stockValue) || !double.TryParse(stockValue.ToString(), out var stockDouble))
            {
                return BadRequest(new { error = $"Ürün '{code}' için Stock bilgisi bulunamadı" });
            }

            var newStock = (int)stockDouble;

            _logger.LogInformation("🔄 {Code} için Exact stok değeri {Stock} - Shopify'a yazılacak", code, newStock);

            var result = await _shopifyService.UpdateStockByCodeAsync(code, newStock);

            if (result.UpdatedCodes.Count == 0 && result.SuccessCount == 0 && result.ErrorCount == 0)
            {
                return NotFound(new { error = $"Shopify'da '{code}' SKU'suna sahip ürün/varyant bulunamadı" });
            }

            return Ok(new
            {
                code,
                exactStock = newStock,
                successCount = result.SuccessCount,
                errorCount = result.ErrorCount,
                updatedCodes = result.UpdatedCodes,
                timestamp = DateTime.Now
            });
        }

        // TEST: Rastgele N ürün için gerçek senkron akışını çalıştırır
        // (Exact stok -> Shopify güncelleme -> StockSyncLogs tablosuna yazım)
        // codes parametresi verilirse rastgele seçim yerine o SKU'lar kullanılır (virgülle ayrılmış)
        [HttpPost("test-random")]
        [AllowAnonymous]
        public async Task<IActionResult> TestRandomStockSync([FromQuery] int count = 10, [FromQuery] string codes = null)
        {
            if (count < 1 || count > 50)
            {
                return BadRequest(new { error = "count 1-50 arasında olmalıdır" });
            }

            _logger.LogInformation("🧪 TEST: Rastgele {Count} ürün için stok senkron testi başlıyor", count);

            // 1. Shopify ürünlerini al ve SKU havuzu çıkar
            var shopifyProducts = await _shopifyService.GetAllProductsRawAsync();
            try
            {
                var skus = new List<string>();
                if (shopifyProducts.RootElement.TryGetProperty("products", out var products))
                {
                    foreach (var product in products.EnumerateArray())
                    {
                        if (product.TryGetProperty("variants", out var variants))
                        {
                            foreach (var variant in variants.EnumerateArray())
                            {
                                if (variant.TryGetProperty("sku", out var skuEl))
                                {
                                    var sku = skuEl.GetString();
                                    if (!string.IsNullOrWhiteSpace(sku))
                                        skus.Add(sku);
                                }
                            }
                        }
                    }
                }

                if (skus.Count == 0)
                {
                    return NotFound(new { error = "Shopify'da SKU'lu ürün bulunamadı" });
                }

                // 2. Rastgele karıştır (codes verildiyse onları kullan), Exact'ta bulunan ilk N ürünü topla
                List<string> shuffled;
                if (!string.IsNullOrWhiteSpace(codes))
                {
                    shuffled = codes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    count = Math.Min(count, shuffled.Count);
                }
                else
                {
                    var random = new Random();
                    shuffled = skus.Distinct().OrderBy(_ => random.Next()).ToList();
                }

                var exactItems = new List<Dictionary<string, object>>();
                var notFoundInExact = new List<string>();

                foreach (var sku in shuffled)
                {
                    if (exactItems.Count >= count) break;
                    if (notFoundInExact.Count >= count * 3) break; // sonsuz aramayı engelle

                    var item = await _exactService.GetItemByCodeAsync(sku);
                    if (item != null && item.ContainsKey("Stock"))
                    {
                        exactItems.Add(item);
                        _logger.LogInformation("🧪 TEST ürünü seçildi: {Sku} (Stok: {Stock})", sku, item["Stock"]);
                    }
                    else
                    {
                        notFoundInExact.Add(sku);
                    }

                    await Task.Delay(300); // Exact rate limit
                }

                if (exactItems.Count == 0)
                {
                    return NotFound(new { error = "Exact'ta eşleşen ürün bulunamadı", deneneneSkular = notFoundInExact });
                }

                // 3. Gerçek senkronla aynı batch güncelleme yolu
                var batchResult = await _shopifyService.UpdateMultipleStocksBatchAsync(
                    exactItems, shopifyProducts, "Data/test_stock_sync.json");

                // 4. DB'ye yaz (gerçek senkronla aynı servis)
                var saveResult = await _stockSyncLogService.SaveAsync(batchResult.LogEntries);

                return Ok(new
                {
                    message = "Test tamamlandı",
                    testedProductCount = exactItems.Count,
                    shopify = new
                    {
                        successCount = batchResult.SuccessCount,
                        errorCount = batchResult.ErrorCount,
                        updatedCodes = batchResult.UpdatedCodes
                    },
                    db = new
                    {
                        savedCount = saveResult.SavedCount,
                        failedCount = saveResult.FailedCount,
                        fallbackFile = saveResult.FallbackFile
                    },
                    exactTaBulunamayanSkular = notFoundInExact,
                    logEntries = batchResult.LogEntries.Select(e => new
                    {
                        e.ProductCode,
                        e.ProductName,
                        e.ExactItemId,
                        e.ShopifyProductId,
                        e.ShopifyVariantId,
                        e.Price,
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

        // StockSyncLogs tablosundaki kayıtları görüntüler (doğrulama için)
        [HttpGet("logs")]
        [AllowAnonymous]
        public async Task<IActionResult> GetLogs(
            [FromQuery] string code = null,
            [FromQuery] DateTime? date = null,
            [FromQuery] bool onlyErrors = false,
            [FromQuery] int take = 50)
        {
            var query = _dbContext.StockSyncLogs.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(code))
                query = query.Where(l => l.ProductCode == code);

            if (date.HasValue)
            {
                var dayStart = date.Value.Date;
                var dayEnd = dayStart.AddDays(1);
                query = query.Where(l => l.UpdatedAt >= dayStart && l.UpdatedAt < dayEnd);
            }

            if (onlyErrors)
                query = query.Where(l => !l.Success);

            var logs = await query
                .OrderByDescending(l => l.UpdatedAt)
                .Take(Math.Clamp(take, 1, 500))
                .ToListAsync();

            var totalCount = await query.CountAsync();

            return Ok(new
            {
                totalCount,
                returnedCount = logs.Count,
                logs
            });
        }
    }
}
