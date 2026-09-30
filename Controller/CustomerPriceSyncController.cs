using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopifyProductApp.Services.ExactPriceList;

namespace ShopifyProductApp.Controllers
{
    /// <summary>
    /// Exact müşteri fiyatlarının Shopify B2B kataloğuna aktarımı.
    ///
    /// Varsayılan davranış <b>dry-run</b>'dır. Canlı yazım için hem
    /// <c>apply=true</c> hem de <c>confirm=EVET</c> gerekir.
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/customer-price-sync")]
    public class CustomerPriceSyncController : ControllerBase
    {
        private readonly CustomerPriceSyncService _syncService;
        private readonly ILogger<CustomerPriceSyncController> _logger;

        public CustomerPriceSyncController(
            CustomerPriceSyncService syncService,
            ILogger<CustomerPriceSyncController> logger)
        {
            _syncService = syncService;
            _logger = logger;
        }

        /// <summary>
        /// Dry-run: tek müşteri için ne yazılacağını raporlar, hiçbir şey yazmaz.
        /// </summary>
        [HttpGet("preview/{accountId}")]
        public async Task<IActionResult> Preview(string accountId, CancellationToken cancellationToken)
        {
            _logger.LogInformation("🧪 Dry-run önizleme istendi: {AccountId}", accountId);

            var plan = await _syncService.BuildPlanAsync(accountId, applyChanges: false, cancellationToken);

            return Ok(new
            {
                mode = "DRY-RUN — hiçbir şey yazılmadı",
                plan.AccountId,
                plan.AccountName,
                plan.Blocked,
                plan.BlockReason,
                plan.CompanyName,
                plan.CompanyLocationId,

                eligibleCount = plan.EligibleCount,
                matchedCount = plan.MatchedCount,
                skippedExpired = plan.SkippedExpired,
                skippedQuantityTiered = plan.SkippedQuantityTiered,
                unmatchedSkuCount = plan.UnmatchedSkus.Count,
                unmatchedSkus = plan.UnmatchedSkus.Take(50),

                // Yazılacak ilk 50 satır — gözle doğrulamak için
                samplePrices = plan.Prices.Take(50).Select(p => new { p.Sku, p.Amount, p.CurrencyCode })
            });
        }

        /// <summary>
        /// CANLI YAZIM. Shopify'da katalog + price list oluşturur ve sabit fiyatları yazar.
        /// İki onay parametresi de zorunludur.
        /// </summary>
        [HttpPost("apply/{accountId}")]
        public async Task<IActionResult> Apply(
            string accountId,
            [FromQuery] bool apply,
            [FromQuery] string confirm,
            CancellationToken cancellationToken)
        {
            if (!apply || !string.Equals(confirm, "EVET", StringComparison.Ordinal))
            {
                return BadRequest(new
                {
                    error = "Canlı yazım için apply=true ve confirm=EVET gereklidir",
                    hint = "Önce GET /api/customer-price-sync/preview/{accountId} ile planı inceleyin"
                });
            }

            _logger.LogWarning("✍️ CANLI YAZIM onaylandı: {AccountId}", accountId);

            var plan = await _syncService.BuildPlanAsync(accountId, applyChanges: true, cancellationToken);

            if (plan.Blocked)
                return BadRequest(new { plan.Blocked, plan.BlockReason, plan.AccountName });

            return Ok(new
            {
                mode = "CANLI YAZIM tamamlandı",
                plan.AccountName,
                plan.CatalogId,
                plan.PriceListId,
                writtenCount = plan.WrittenCount,
                failedCount = plan.FailedCount,
                unmatchedSkuCount = plan.UnmatchedSkus.Count,
                skippedExpired = plan.SkippedExpired,
                skippedQuantityTiered = plan.SkippedQuantityTiered,
                errors = plan.WriteErrors
            });
        }
    }
}
