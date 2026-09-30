using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopifyProductApp.Services.ExactPriceList;

namespace ShopifyProductApp.Controllers
{
    /// <summary>
    /// Exact'taki müşteriye özel fiyatların keşif raporu. <b>Salt okunur</b> —
    /// ne Exact'a ne Shopify'a hiçbir şey yazmaz.
    /// Shopify B2B katalog kurulumuna geçmeden önce gerçek veriyi görmek için kullanılır.
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/price-list-discovery")]
    public class PriceListDiscoveryController : ControllerBase
    {
        private readonly ExactPriceListDiscoveryService _discoveryService;
        private readonly ILogger<PriceListDiscoveryController> _logger;

        public PriceListDiscoveryController(
            ExactPriceListDiscoveryService discoveryService,
            ILogger<PriceListDiscoveryController> logger)
        {
            _discoveryService = discoveryService;
            _logger = logger;
        }

        /// <summary>
        /// Özet rapor: kaç müşteri, kaç SKU, kaç satır aktif, hangileri sorunlu.
        /// </summary>
        [HttpGet("summary")]
        public async Task<IActionResult> GetSummary(CancellationToken cancellationToken)
        {
            _logger.LogInformation("🔍 Müşteri fiyat keşif özeti istendi");

            var report = await _discoveryService.RunDiscoveryAsync(cancellationToken);

            return Ok(new
            {
                generatedAt = report.GeneratedAt,

                totalCustomerPriceLines = report.TotalCustomerPriceLines,
                distinctCustomerCount = report.DistinctCustomerCount,
                distinctItemCount = report.DistinctItemCount,

                activePriceLines = report.ActivePriceLines,
                futurePriceLines = report.FuturePriceLines,
                expiredPriceLines = report.ExpiredPriceLines,

                // Shopify sabit fiyatına çevrilemeyen satırlar
                quantityTieredLines = report.QuantityTieredLines,
                nonEuroCurrencies = report.NonEuroCurrencies,

                pricesAboveStandardCount = report.PricesAboveStandard.Count,
                unresolvedItemCodeCount = report.UnresolvedItemCodes.Count,

                // Her müşteri bir Shopify kataloğu demek
                customers = report.Customers
                    .OrderByDescending(c => c.ActivePriceCount)
                    .Select(c => new
                    {
                        c.AccountId,
                        c.AccountName,
                        c.ActivePriceCount,
                        c.FuturePriceCount,
                        c.ExpiredPriceCount,
                        c.QuantityTieredCount
                    })
            });
        }

        /// <summary>
        /// Yalnızca sayılar — müşteri listesi olmadan. Büyük çıktıları okumak için.
        /// </summary>
        [HttpGet("counts")]
        public async Task<IActionResult> GetCounts(CancellationToken cancellationToken)
        {
            var report = await _discoveryService.RunDiscoveryAsync(cancellationToken);

            return Ok(new
            {
                totalCustomerPriceLines = report.TotalCustomerPriceLines,
                distinctCustomerCount = report.DistinctCustomerCount,
                distinctItemCount = report.DistinctItemCount,
                activePriceLines = report.ActivePriceLines,
                expiredPriceLines = report.ExpiredPriceLines,
                quantityTieredLines = report.QuantityTieredLines,
                pricesAboveStandardCount = report.PricesAboveStandard.Count,

                // B2C kontrolü: VAT/KvK numarası olmayan müşteriler
                customersWithoutBusinessIdCount = report.CustomersWithoutBusinessId.Count,
                customersWithoutBusinessId = report.CustomersWithoutBusinessId,

                // Katalog kurulabilecek müşteri sayısı
                b2bEligibleCustomerCount =
                    report.DistinctCustomerCount - report.CustomersWithoutBusinessId.Count
            });
        }

        /// <summary>
        /// Tam rapor: tüm fiyat satırları, anomaliler ve çözülemeyen SKU'lar dahil.
        /// </summary>
        [HttpGet("full")]
        public async Task<IActionResult> GetFull(CancellationToken cancellationToken)
        {
            _logger.LogInformation("🔍 Müşteri fiyat tam keşif raporu istendi");

            var report = await _discoveryService.RunDiscoveryAsync(cancellationToken);
            return Ok(report);
        }

        /// <summary>
        /// Müşteriyi isim veya Exact müşteri koduna göre arar; AccountId'sini ve
        /// özel fiyat durumunu döndürür. Pilot hesabı bulmak için.
        /// </summary>
        [HttpGet("find")]
        public async Task<IActionResult> Find([FromQuery] string q, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(q))
                return BadRequest(new { error = "q parametresi gereklidir (isim parçası)" });

            var report = await _discoveryService.RunDiscoveryAsync(cancellationToken);

            var matches = report.Customers
                .Where(c => c.AccountName != null &&
                            c.AccountName.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Select(c => new
                {
                    c.AccountId,
                    c.AccountName,
                    c.ActivePriceCount,
                    c.ExpiredPriceCount,
                    c.QuantityTieredCount,
                    c.VatNumber,
                    c.ChamberOfCommerce,
                    // Aktif/süresi geçmiş ayrımı olmadan TÜM satırlar — tarihleriyle birlikte.
                    allPrices = c.Prices
                        .Take(20)
                        .Select(p => new
                        {
                            p.ItemCode,
                            p.ItemDescription,
                            p.Price,
                            p.Quantity,
                            p.StartDate,
                            p.EndDate,
                            isActive = p.IsActiveOn(DateTime.UtcNow)
                        })
                })
                .ToList();

            return Ok(new
            {
                query = q,
                matchCount = matches.Count,
                matches
            });
        }

        /// <summary>
        /// Tek bir müşterinin özel fiyatları — Shopify'a yazılmadan önce gözle doğrulamak için.
        /// </summary>
        [HttpGet("customer/{accountId}")]
        public async Task<IActionResult> GetCustomer(string accountId, CancellationToken cancellationToken)
        {
            var report = await _discoveryService.RunDiscoveryAsync(cancellationToken);

            var customer = report.Customers
                .FirstOrDefault(c => string.Equals(c.AccountId, accountId, StringComparison.OrdinalIgnoreCase));

            if (customer == null)
                return NotFound(new { error = $"'{accountId}' için özel fiyat bulunamadı" });

            return Ok(customer);
        }
    }
}
