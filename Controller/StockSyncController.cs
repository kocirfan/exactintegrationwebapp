using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

        public StockSyncController(
            ExactService exactService,
            ShopifyService shopifyService,
            ILogger<StockSyncController> logger)
        {
            _exactService = exactService;
            _shopifyService = shopifyService;
            _logger = logger;
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
    }
}
