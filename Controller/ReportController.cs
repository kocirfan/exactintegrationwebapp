
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using ShopifyProductApp.Services;
using Microsoft.AspNetCore.Cors;
using Newtonsoft.Json;
using System.Text;
using ExactOnline.Models;
using ExactWebApp.Dto;

namespace ShopifyProductApp.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class ReportController : ControllerBase
    {
        private readonly ExactService _exactService;
        private readonly ShopifyService _shopifyService;
        private readonly ShopifyGraphQLService _graphqlService;
        private readonly AppConfiguration _config;
        private readonly ShopifyOrderCrud _shopifyOrderCrud;
        private readonly ILogger<ReportController> _logger;
        private readonly IConfiguration _configg;
        private readonly IServiceProvider _serviceProvider;
        private readonly ITokenManager _tokenManager;
        private readonly ISettingsService _settingsService;
        private readonly ExactSalesReports _exactSalesReports;

        private readonly ExactSalesReportsUltraOptimized _exactSalesReportsUltraOptimized;

        private readonly CustomerReports _customerReports;



        public ReportController(
            ShopifyGraphQLService graphqlService,
            ExactService exactService,
            ShopifyService shopifyService,
            AppConfiguration config,
            ShopifyOrderCrud shopifyOrderCrud,
            ILogger<ReportController> logger,
            IConfiguration configg,
            IServiceProvider serviceProvider,
            ITokenManager tokenManager,
            ISettingsService settingsService,
            ExactSalesReports exactSalesReports,
            CustomerReports customerReports,
            ExactSalesReportsUltraOptimized exactSalesReportsUltraOptimized
            )
        {
            _graphqlService = graphqlService;
            _exactService = exactService;
            _shopifyService = shopifyService;
            _config = config;
            _shopifyOrderCrud = shopifyOrderCrud;
            _logger = logger;
            _configg = configg;
            _serviceProvider = serviceProvider;
            _tokenManager = tokenManager;
            _settingsService = settingsService;
            _exactSalesReports = exactSalesReports;
            _customerReports = customerReports;
            _exactSalesReportsUltraOptimized = exactSalesReportsUltraOptimized;
        }


        /// NaN ve Infinity değerlerini 0'a dönüştür (JSON serialize hatasını önlemek için)
        private double SanitizeDouble(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0;
            }
            return value;
        }

        //     [HttpGet("top-products")]
        //     public async Task<IActionResult> GetTopProducts(
        //    [FromQuery] string period = "OneYear",
        //    [FromQuery] ReportFilterModel filter = null)
        //     {
        //         filter ??= new ReportFilterModel();
        //         if (!Enum.TryParse<TimePeriod>(period, out var timePeriod))
        //         {
        //             return BadRequest("Geçersiz periyod. Geçerli değerler: OneDay, OneWeek, OneMonth, ThreeMonths, SixMonths, OneYear");
        //         }

        //         if (filter.TopCount < 1 || filter.TopCount > 100)
        //         {
        //             return BadRequest("topCount 1 ile 100 arasında olmalıdır");
        //         }

        //         var result = await _exactSalesReports.GetTopSalesPeriodProductsAsync(timePeriod, filter);

        //         if (result == null)
        //         {
        //             return StatusCode(500, "Veri alınamadı");
        //         }

        //         return Ok(new
        //         {
        //             success = true,
        //             count = result.Count,
        //             period = period,
        //             data = result
        //         });
        //     }

        //yeni metot iki tarih arasındaki veriyi alacak
        [HttpGet("top-sales-by-date")]
        public async Task<ActionResult<List<TopProductDto>>> GetTopSalesProductsByDate(
    [FromQuery] DateTime? startDate,
    [FromQuery] DateTime? endDate,
    [FromQuery] ReportFilterModel filter = null)
        {
            filter ??= new ReportFilterModel();
            if (startDate.HasValue && endDate.HasValue && startDate > endDate)
            {
                return BadRequest(new { message = "Başlangıç tarihi bitiş tarihinden sonra olamaz" });
            }

            var result = await _exactSalesReports.GetTopSalesProductsAsync(startDate, endDate, filter);

            if (result == null)
            {
                return StatusCode(500, new { message = "Veri işleme sırasında bir hata oluştu" });
            }

            return Ok(result);
        }



        //karşılaştırmada bunu temel alacağım
        /// <summary>
        /// Özel tarih aralığını karşılaştırır
        /// GET /api/customersdatecomparison/custom
        ///     ?startDate1=2024-01-01&amp;endDate1=2024-01-31
        ///     &amp;startDate2=2023-01-01&amp;endDate2=2023-01-31
        ///     &amp;topCount=10
        /// </summary>
        [HttpGet("custom")]
        public async Task<IActionResult> CustomDateRange(
            [FromQuery] DateTime startDate1,
            [FromQuery] DateTime endDate1,
            [FromQuery] DateTime startDate2,
            [FromQuery] DateTime endDate2,
            [FromQuery] ReportFilterModel filter = null)
        {
            filter ??= new ReportFilterModel { TopCount = 10 };
            try
            {
                _logger.LogInformation("📊 Özel Tarih Aralığı Karşılaştırması");
                _logger.LogInformation($"   - Tarih 1: {startDate1:yyyy-MM-dd} to {endDate1:yyyy-MM-dd}");
                _logger.LogInformation($"   - Tarih 2: {startDate2:yyyy-MM-dd} to {endDate2:yyyy-MM-dd}");

                if (filter.TopCount < 1 || filter.TopCount > 100)
                    return BadRequest(new { error = "topCount must be between 1 and 100" });

                if (startDate1 > endDate1 || startDate2 > endDate2)
                    return BadRequest(new { error = "Start date must be before end date" });

                var range1 = new DateRangeQuery(startDate1, endDate1, $"{startDate1:yyyy-MM-dd} to {endDate1:yyyy-MM-dd}");
                var range2 = new DateRangeQuery(startDate2, endDate2, $"{startDate2:yyyy-MM-dd} to {endDate2:yyyy-MM-dd}");

                var result = await _exactSalesReportsUltraOptimized.CompareDateRangesAsyncThreaded(range1, range2, filter);
                

                if (!result.Success)
                    return StatusCode(500, result);

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Hata: {ex.Message}");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // [HttpGet("analysis")]
        // public async Task<IActionResult> GetAnalysis(
        //     [FromQuery] string period = "OneYear",
        //     [FromQuery] ReportFilterModel filter = null)
        // {
        //     filter ??= new ReportFilterModel { TopCount = 10 };
        //     if (!Enum.TryParse<TimePeriod>(period, out var timePeriod))
        //     {
        //         return BadRequest("Geçersiz periyod");
        //     }

        //     var result = await _exactSalesReports.AnalyzeSalesAsync(timePeriod, filter);
        //     return Ok(result);
        // }

        // [HttpGet("all-orders")]
        // public async Task<IActionResult> GetAllOrders([FromQuery] string period = "OneYear")
        // {
        //     if (!Enum.TryParse<TimePeriod>(period, out var timePeriod))
        //     {
        //         return BadRequest("Geçersiz periyod");
        //     }

        //     var result = await _exactSalesReports.GetAllSalesOrderAsync(timePeriod);
        //     return Ok(result);
        // }



        // ==================== MÜŞTERİ ANALİZİ ====================

        // [HttpGet("top-customers")]
        // public async Task<IActionResult> GetTopCustomers(
        //     [FromQuery] string period = "OneYear",
        //     [FromQuery] ReportFilterModel filter = null)
        // {
        //     filter ??= new ReportFilterModel();
        //     if (!Enum.TryParse<TimePeriod>(period, out var timePeriod))
        //     {
        //         return BadRequest("Geçersiz periyod. Geçerli değerler: OneDay, OneWeek, OneMonth, ThreeMonths, SixMonths, OneYear");
        //     }

        //     if (filter.TopCount < 1 || filter.TopCount > 100)
        //     {
        //         return BadRequest("topCount 1 ile 100 arasında olmalıdır");
        //     }

        //     var result = await _customerReports.GetTopCustomersAsync(timePeriod, filter);

        //     if (result == null)
        //     {
        //         return StatusCode(500, "Veri alınamadı");
        //     }

        //     return Ok(new
        //     {
        //         success = true,
        //         count = result.Count,
        //         period = period,
        //         data = result
        //     });
        // }

        // [HttpGet("customer-analysis")]
        // public async Task<IActionResult> GetCustomerAnalysis(
        //     [FromQuery] string period = "OneYear",
        //     [FromQuery] ReportFilterModel filter = null)
        // {
        //     filter ??= new ReportFilterModel { TopCount = 10 };
        //     if (!Enum.TryParse<TimePeriod>(period, out var timePeriod))
        //     {
        //         return BadRequest("Geçersiz periyod");
        //     }

        //     var result = await _customerReports.AnalyzeCustomersAsync(timePeriod, filter);
        //     return Ok(result);
        // }

    }
}

/* API ÇAĞRI ÖRNEKLERİ:

=== ÜRÜN ANALİZİ ===

1. Son 1 ayın en çok satılan 5 ürün:
   GET /api/sales/top-products?period=OneMonth&topCount=5

2. Son 1 yılın en çok satılan 15 ürün:
   GET /api/sales/top-products?period=OneYear&topCount=15

3. Son 1 günün en çok satılan 10 ürün:
   GET /api/sales/top-products?period=OneDay&topCount=10

4. Son 3 ayın tam ürün analizi (top 20 ürün):
   GET /api/sales/product-analysis?period=ThreeMonths&topCount=20

=== MÜŞTERİ ANALİZİ ===

5. Son 1 ayın en çok sipariş veren 5 müşteri:
   GET /api/sales/top-customers?period=OneMonth&topCount=5

6. Son 1 yılın en çok sipariş veren 15 müşteri:
   GET /api/sales/top-customers?period=OneYear&topCount=15

7. Son 1 günün en çok sipariş veren 10 müşteri:
   GET /api/sales/top-customers?period=OneDay&topCount=10

8. Son 3 ayın tam müşteri analizi (top 20 müşteri):
   GET /api/sales/customer-analysis?period=ThreeMonths&topCount=20

=== GENEL ===

9. Tüm siparişleri son 6 aydan itibaren:
   GET /api/sales/all-orders?period=SixMonths

10. Varsayılan (1 yıl):
    GET /api/sales/all-orders

*/



///--> son olarak düzenlenen dosyalar
/// Karşılaştırmada
/// http://localhost:5057/api/report/custom?startDate1=2024-12-01&amp;endDate1=2024-12-02&amp;startDate2=2025-12-01&amp;endDate2=2025-12-02&amp;topCount=5
/// Veri getirmede
/// http://localhost:5057/api/report/top-sales-by-date?startDate=2024-12-01&amp;endDate=2024-12-02&amp;topCount=5