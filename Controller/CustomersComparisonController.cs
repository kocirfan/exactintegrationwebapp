using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class CustomersComparisonController : ControllerBase
{
    private readonly CustomerReports _customerReports;
    private readonly ILogger<CustomersComparisonController> _logger;

    public CustomersComparisonController(
        CustomerReports customerReports,
        ILogger<CustomersComparisonController> logger)
    {
        _customerReports = customerReports;
        _logger = logger;
    }

    /// <summary>
    /// İki farklı periyodu karşılaştırır
    /// 
    /// Örnekler:
    /// GET /api/customerscomparison/compare?currentPeriod=OneMonth&previousPeriod=OneMonth&topCount=5
    /// GET /api/customerscomparison/compare?currentPeriod=OneWeek&previousPeriod=OneWeek&topCount=10
    /// GET /api/customerscomparison/compare?currentPeriod=OneDay&previousPeriod=OneDay&topCount=20
    /// GET /api/customerscomparison/compare?currentPeriod=OneYear&previousPeriod=OneYear&topCount=50
    /// </summary>
    [HttpGet("compare")]
    public async Task<IActionResult> ComparisonAnalysis(
        [FromQuery] string currentPeriod = "OneMonth",
        [FromQuery] string previousPeriod = "OneMonth",
        [FromQuery] int topCount = 5)
    {
        try
        {
            _logger.LogInformation($"📊 Karşılaştırma Analizi Başlatıldı");
            _logger.LogInformation($"   - Şimdiki Periyod: {currentPeriod}");
            _logger.LogInformation($"   - Önceki Periyod: {previousPeriod}");
            _logger.LogInformation($"   - Top Müşteri Sayısı: {topCount}");

            // Parametreleri valide et
            if (!Enum.TryParse<TimePeriod>(currentPeriod, out var currentTP))
            {
                return BadRequest(new
                {
                    error = "Invalid currentPeriod",
                    validPeriods = new[] { "OneDay", "OneWeek", "OneMonth", "ThreeMonths", "SixMonths", "OneYear" }
                });
            }

            if (!Enum.TryParse<TimePeriod>(previousPeriod, out var previousTP))
            {
                return BadRequest(new
                {
                    error = "Invalid previousPeriod",
                    validPeriods = new[] { "OneDay", "OneWeek", "OneMonth", "ThreeMonths", "SixMonths", "OneYear" }
                });
            }

            if (topCount < 1 || topCount > 100)
            {
                return BadRequest(new { error = "topCount must be between 1 and 100" });
            }

            // Karşılaştırma yap
            var result = await _customerReports.ComparePeriodsAsync(currentTP, previousTP, topCount);

            if (!result.Success)
            {
                return StatusCode(500, result);
            }

            _logger.LogInformation($"✅ Karşılaştırma başarılı");

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Hata: {ex.Message}");
            return StatusCode(500, new { error = ex.Message });
        }
    }


    /// <summary>
    /// Dün ile Bugünü karşılaştırır
    /// GET /api/customersdatecomparison/yesterday-today?topCount=5
    /// </summary>
    [HttpGet("yesterday-today")]
    public async Task<IActionResult> YesterdayVsToday([FromQuery] int topCount = 5)
    {
        try
        {
            _logger.LogInformation("📊 Dün vs Bugün Karşılaştırması");

            if (topCount < 1 || topCount > 100)
                return BadRequest(new { error = "topCount must be between 1 and 100" });

            var ranges = DateRangeFactory.YesterdayVsToday();
            var result = await _customerReports.CompareDateRangesAsync(
                ranges.current,
                ranges.previous,
                topCount);

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

    /// <summary>
    /// Bu hafta ile Geçen haftayı karşılaştırır
    /// GET /api/customersdatecomparison/this-week-last-week?topCount=10
    /// </summary>
    [HttpGet("this-week-last-week")]
    public async Task<IActionResult> ThisWeekVsLastWeek([FromQuery] int topCount = 10)
    {
        try
        {
            _logger.LogInformation("📊 Bu Hafta vs Geçen Hafta Karşılaştırması");

            if (topCount < 1 || topCount > 100)
                return BadRequest(new { error = "topCount must be between 1 and 100" });

            var ranges = DateRangeFactory.ThisWeekVsLastWeek();
            var result = await _customerReports.CompareDateRangesAsync(
                ranges.current,
                ranges.previous,
                topCount);

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

    /// <summary>
    /// Bu ay ile Geçen ayı karşılaştırır
    /// GET /api/customersdatecomparison/this-month-last-month?topCount=10
    /// </summary>
    [HttpGet("this-month-last-month")]
    public async Task<IActionResult> ThisMonthVsLastMonth([FromQuery] int topCount = 10)
    {
        try
        {
            _logger.LogInformation("📊 Bu Ay vs Geçen Ay Karşılaştırması");

            if (topCount < 1 || topCount > 100)
                return BadRequest(new { error = "topCount must be between 1 and 100" });

            var ranges = DateRangeFactory.ThisMonthVsLastMonth();
            var result = await _customerReports.CompareDateRangesAsync(
                ranges.current,
                ranges.previous,
                topCount);

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

    /// <summary>
    /// Bu yıl ile Geçen yılı karşılaştırır
    /// GET /api/customersdatecomparison/this-year-last-year?topCount=20
    /// </summary>
    [HttpGet("this-year-last-year")]
    public async Task<IActionResult> ThisYearVsLastYear([FromQuery] int topCount = 20)
    {
        try
        {
            _logger.LogInformation("📊 Bu Yıl vs Geçen Yıl Karşılaştırması");

            if (topCount < 1 || topCount > 100)
                return BadRequest(new { error = "topCount must be between 1 and 100" });

            var ranges = DateRangeFactory.ThisYearVsLastYear();
            var result = await _customerReports.CompareDateRangesAsync(
                ranges.current,
                ranges.previous,
                topCount);

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

    /// <summary>
    /// Son 30 gün ile Önceki 30 günü karşılaştırır
    /// GET /api/customersdatecomparison/last-30-days?topCount=15
    /// </summary>
    [HttpGet("last-30-days")]
    public async Task<IActionResult> Last30Days([FromQuery] int topCount = 15)
    {
        try
        {
            _logger.LogInformation("📊 Son 30 Gün vs Önceki 30 Gün Karşılaştırması");

            if (topCount < 1 || topCount > 100)
                return BadRequest(new { error = "topCount must be between 1 and 100" });

            var ranges = DateRangeFactory.Last30DaysVsPrevious30Days();
            var result = await _customerReports.CompareDateRangesAsync(
                ranges.current,
                ranges.previous,
                topCount);

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

    /// <summary>
    /// Son 3 ay ile Önceki 3 ayı karşılaştırır
    /// GET /api/customersdatecomparison/last-3-months?topCount=20
    /// </summary>
    [HttpGet("last-3-months")]
    public async Task<IActionResult> Last3Months([FromQuery] int topCount = 20)
    {
        try
        {
            _logger.LogInformation("📊 Son 3 Ay vs Önceki 3 Ay Karşılaştırması");

            if (topCount < 1 || topCount > 100)
                return BadRequest(new { error = "topCount must be between 1 and 100" });

            var ranges = DateRangeFactory.Last3MonthsVsPrevious3Months();
            var result = await _customerReports.CompareDateRangesAsync(
                ranges.current,
                ranges.previous,
                topCount);

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

    //larşılaştırmada bunu temel alacağım
    /// <summary>
    /// Özel tarih aralığını karşılaştırır
    /// GET /api/customersdatecomparison/custom
    ///     ?startDate1=2024-01-01&endDate1=2024-01-31
    ///     &startDate2=2023-01-01&endDate2=2023-01-31
    ///     &topCount=10
    /// </summary>
    [HttpGet("custom")]
    public async Task<IActionResult> CustomDateRange(
        [FromQuery] DateTime startDate1,
        [FromQuery] DateTime endDate1,
        [FromQuery] DateTime startDate2,
        [FromQuery] DateTime endDate2,
        [FromQuery] int topCount = 10)
    {
        try
        {
            _logger.LogInformation("📊 Özel Tarih Aralığı Karşılaştırması");
            _logger.LogInformation($"   - Tarih 1: {startDate1:yyyy-MM-dd} to {endDate1:yyyy-MM-dd}");
            _logger.LogInformation($"   - Tarih 2: {startDate2:yyyy-MM-dd} to {endDate2:yyyy-MM-dd}");

            if (topCount < 1 || topCount > 100)
                return BadRequest(new { error = "topCount must be between 1 and 100" });

            if (startDate1 > endDate1 || startDate2 > endDate2)
                return BadRequest(new { error = "Start date must be before end date" });

            var range1 = new DateRangeQuery(startDate1, endDate1, $"{startDate1:yyyy-MM-dd} to {endDate1:yyyy-MM-dd}");
            var range2 = new DateRangeQuery(startDate2, endDate2, $"{startDate2:yyyy-MM-dd} to {endDate2:yyyy-MM-dd}");

            var result = await _customerReports.CompareDateRangesAsync(range1, range2, topCount);

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

     [HttpGet("top-sales-by-date")]
        public async Task<ActionResult<List<TopProductDto>>> GetTopSalesProductsByDate(
    [FromQuery] DateTime startDate,
    [FromQuery] DateTime endDate,
    [FromQuery] int topCount = 5)
        {
            if (startDate == null || endDate == null || startDate > endDate)
            {
                return BadRequest(new { message = "Başlangıç tarihi bitiş tarihinden sonra olamaz" });
            }

            var result = await _customerReports.GetTopCustomersDateRangeAsync(startDate, endDate, topCount);

            if (result == null)
            {
                return StatusCode(500, new { message = "Veri işleme sırasında bir hata oluştu" });
            }

            return Ok(result);
        }

    /// <summary>
    /// Örnek endpoint - API response görmek için
    /// </summary>
    [HttpGet("example")]
    public IActionResult Example()
    {
        var example = new
        {
            success = true,
            message = "✅ Tarih aralığı karşılaştırması başarılı",
            currentPeriod = "Bugün",
            previousPeriod = "Dün",
            currentAmount = 25000.50,
            previousAmount = 18500.25,
            amountDifference = 6500.25,
            amountDifferencePercent = 35.14,
            amountTrend = "📈 Güçlü Artış",
            currentOrderCount = 25,
            previousOrderCount = 18,
            orderDifference = 7,
            orderDifferencePercent = 38.89,
            currentCustomerCount = 12,
            previousCustomerCount = 10
        };

        return Ok(example);
    }
}

//endpointler -- Karşılaştırma örnekleri
/// Dün ile Bugünü karşılaştırır
/// GET /api/customersdatecomparison/yesterday-today?topCount=5
///  /// Bu ay ile Geçen ayı karşılaştırır
/// GET /api/customersdatecomparison/this-month-last-month?topCount=10
/// Bu yıl ile Geçen yılı karşılaştırır
/// GET /api/customersdatecomparison/this-year-last-year?topCount=20
///   /// Son 30 gün ile Önceki 30 günü karşılaştırır
/// GET /api/customersdatecomparison/last-30-days?topCount=15
///  /// Son 3 ay ile Önceki 3 ayı karşılaştırır
/// GET /api/customersdatecomparison/last-3-months?topCount=20
/// Özel tarih aralığını karşılaştırır
/// http://localhost:5057/api/customerscomparison/custom?startDate1=2024-12-01&endDate1=2024-12-31&startDate2=2025-12-01&endDate2=2025-12-31&topCount=10


/// endpointler -- normal veri çekme örnekleri
/// Son 1 ayın en çok sipariş veren 5 müşteri:
// GET /api/sales/top-customers?period=OneMonth&topCount=5

//Son 1 yılın en çok sipariş veren 15 müşteri:
// GET /api/sales/top-customers?period=OneYear&topCount=15

//Son 1 günün en çok sipariş veren 10 müşteri:
// GET /api/sales/top-customers?period=OneDay&topCount=10

//Son 3 ayın tam müşteri analizi (top 20 müşteri):
// GET /api/sales/customer-analysis?period=ThreeMonths&topCount=20





///--> son olarak düzenlenen dosyalar
/// Karşılaştırmada
/// http://localhost:5057/api/customerscomparison/custom?startDate1=2024-12-01&endDate1=2024-12-02&startDate2=2025-12-01&endDate2=2025-12-02&topCount=5
/// Veri getirmede
/// http://localhost:5057/api/customerscomparison/top-sales-by-date?startDate=2024-12-01&endDate=2024-12-02&topCount=5