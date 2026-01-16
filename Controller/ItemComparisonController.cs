using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class ItemComparisonController : ControllerBase
{
    private readonly CustomerReports _customerReports;
    private readonly ILogger<ItemComparisonController> _logger;

    public ItemComparisonController(
        CustomerReports customerReports,
        ILogger<ItemComparisonController> logger)
    {
        _customerReports = customerReports;
        _logger = logger;
    }

    /// <summary>
    /// Dün ile Bugünü karşılaştırır
    /// GET /api/itemdatecomparison/yesterday-today?topCount=5
    /// </summary>
    // [HttpGet("yesterday-today")]
    // public async Task<IActionResult> YesterdayVsToday([FromQuery] int topCount = 5)
    // {
    //     try
    //     {
    //         _logger.LogInformation("📊 Dün vs Bugün Karşılaştırması");

    //         if (topCount < 1 || topCount > 100)
    //             return BadRequest(new { error = "topCount must be between 1 and 100" });

    //         var ranges = DateRangeFactory.YesterdayVsToday();
    //         var result = await _customerReports.CompareDateRangesAsync(
    //             ranges.current,
    //             ranges.previous,
    //             new ExactWebApp.Dto.ReportFilterModel { TopCount = topCount });

    //         if (!result.Success)
    //             return StatusCode(500, result);

    //         return Ok(result);
    //     }
    //     catch (Exception ex)
    //     {
    //         _logger.LogError($"❌ Hata: {ex.Message}");
    //         return StatusCode(500, new { error = ex.Message });
    //     }
    // }
}

