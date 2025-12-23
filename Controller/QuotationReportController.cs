using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

[ApiController]
[Route("api/[controller]")]
public class QuotationReportsController : ControllerBase
{
    private readonly QuotationReports _quotationReports;
    private readonly ILogger<QuotationReportsController> _logger;

    public QuotationReportsController(
        QuotationReports quotationReports,
        ILogger<QuotationReportsController> logger)
    {
        _quotationReports = quotationReports;
        _logger = logger;
    }

    /// <summary>
    /// En çok teklif verilen ürünleri getirir
    /// </summary>
    /// <param name="startDate">Başlangıç tarihi (YYYY-MM-DD)</param>
    /// <param name="endDate">Bitiş tarihi (YYYY-MM-DD)</param>
    /// <param name="topCount">Kaç tane ürün gösterilecek (varsayılan: 10)</param>
    /// <returns>Top ürünlerin listesi</returns>
    [HttpGet("top-products")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse<List<TopProductDTO>>>> GetTopProducts(
        [FromQuery] string startDate,
        [FromQuery] string endDate,
        [FromQuery] int topCount = 10)
    {
        try
        {
            if (!DateTime.TryParse(startDate, out var start) || !DateTime.TryParse(endDate, out var end))
            {
                return BadRequest(new ApiResponse<List<TopProductDTO>>
                {
                    Success = false,
                    Message = "Geçersiz tarih formatı. YYYY-MM-DD formatını kullanın.",
                    Data = null
                });
            }

            if (start > end)
            {
                return BadRequest(new ApiResponse<List<TopProductDTO>>
                {
                    Success = false,
                    Message = "Başlangıç tarihi bitiş tarihinden önce olmalıdır.",
                    Data = null
                });
            }

            if (topCount <= 0 || topCount > 100)
            {
                return BadRequest(new ApiResponse<List<TopProductDTO>>
                {
                    Success = false,
                    Message = "topCount değeri 1 ile 100 arasında olmalıdır.",
                    Data = null
                });
            }

            _logger.LogInformation($"📊 Top ürünler istendi: {start:yyyy-MM-dd} - {end:yyyy-MM-dd}, Top: {topCount}");

            var products = await _quotationReports.GetTopQuotedProductsAsync(start, end, topCount);

            return Ok(new ApiResponse<List<TopProductDTO>>
            {
                Success = true,
                Message = $"{products.Count} ürün bulundu",
                Data = products
            });
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Top ürünler hatası: {ex.Message}");
            return StatusCode(500, new ApiResponse<List<TopProductDTO>>
            {
                Success = false,
                Message = "Sunucu hatası oluştu",
                Data = null,
                Error = ex.Message
            });
        }
    }

    /// <summary>
    /// En çok teklif verilen müşterileri getirir
    /// </summary>
    /// <param name="startDate">Başlangıç tarihi (YYYY-MM-DD)</param>
    /// <param name="endDate">Bitiş tarihi (YYYY-MM-DD)</param>
    /// <param name="topCount">Kaç tane müşteri gösterilecek (varsayılan: 10)</param>
    /// <returns>Top müşterilerin listesi</returns>
    [HttpGet("top-customers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse<List<TopCustomerDTO>>>> GetTopCustomers(
        [FromQuery] string startDate,
        [FromQuery] string endDate,
        [FromQuery] int topCount = 10)
    {
        try
        {
            if (!DateTime.TryParse(startDate, out var start) || !DateTime.TryParse(endDate, out var end))
            {
                return BadRequest(new ApiResponse<List<TopCustomerDTO>>
                {
                    Success = false,
                    Message = "Geçersiz tarih formatı. YYYY-MM-DD formatını kullanın.",
                    Data = null
                });
            }

            if (start > end)
            {
                return BadRequest(new ApiResponse<List<TopCustomerDTO>>
                {
                    Success = false,
                    Message = "Başlangıç tarihi bitiş tarihinden önce olmalıdır.",
                    Data = null
                });
            }

            if (topCount <= 0 || topCount > 100)
            {
                return BadRequest(new ApiResponse<List<TopCustomerDTO>>
                {
                    Success = false,
                    Message = "topCount değeri 1 ile 100 arasında olmalıdır.",
                    Data = null
                });
            }

            _logger.LogInformation($"📊 Top müşteriler istendi: {start:yyyy-MM-dd} - {end:yyyy-MM-dd}, Top: {topCount}");

            var customers = await _quotationReports.GetTopQuotedCustomersAsync(start, end, topCount);

            return Ok(new ApiResponse<List<TopCustomerDTO>>
            {
                Success = true,
                Message = $"{customers.Count} müşteri bulundu",
                Data = customers
            });
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Top müşteriler hatası: {ex.Message}");
            return StatusCode(500, new ApiResponse<List<TopCustomerDTO>>
            {
                Success = false,
                Message = "Sunucu hatası oluştu",
                Data = null,
                Error = ex.Message
            });
        }
    }

    /// <summary>
    /// İki tarih aralığında ürünleri karşılaştırır
    /// </summary>
    /// <param name="startDate1">Period 1 - Başlangıç tarihi (YYYY-MM-DD)</param>
    /// <param name="endDate1">Period 1 - Bitiş tarihi (YYYY-MM-DD)</param>
    /// <param name="startDate2">Period 2 - Başlangıç tarihi (YYYY-MM-DD)</param>
    /// <param name="endDate2">Period 2 - Bitiş tarihi (YYYY-MM-DD)</param>
    /// <param name="topCount">Kaç tane ürün gösterilecek (varsayılan: 10)</param>
    /// <returns>Karşılaştırılmış ürün verileri</returns>
    [HttpGet("compare-products")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse<ComparisonProductResultDTO>>> CompareProducts(
        [FromQuery] string startDate1,
        [FromQuery] string endDate1,
        [FromQuery] string startDate2,
        [FromQuery] string endDate2,
        [FromQuery] int topCount = 10)
    {
        try
        {
            if (!DateTime.TryParse(startDate1, out var start1) || !DateTime.TryParse(endDate1, out var end1) ||
                !DateTime.TryParse(startDate2, out var start2) || !DateTime.TryParse(endDate2, out var end2))
            {
                return BadRequest(new ApiResponse<ComparisonProductResultDTO>
                {
                    Success = false,
                    Message = "Geçersiz tarih formatı. YYYY-MM-DD formatını kullanın.",
                    Data = null
                });
            }

            if (start1 > end1 || start2 > end2)
            {
                return BadRequest(new ApiResponse<ComparisonProductResultDTO>
                {
                    Success = false,
                    Message = "Başlangıç tarihleri bitiş tarihlerinden önce olmalıdır.",
                    Data = null
                });
            }

            if (topCount <= 0 || topCount > 100)
            {
                return BadRequest(new ApiResponse<ComparisonProductResultDTO>
                {
                    Success = false,
                    Message = "topCount değeri 1 ile 100 arasında olmalıdır.",
                    Data = null
                });
            }

            _logger.LogInformation($"📊 Ürün karşılaştırması: P1({start1:yyyy-MM-dd}-{end1:yyyy-MM-dd}) vs P2({start2:yyyy-MM-dd}-{end2:yyyy-MM-dd})");

            var result = await _quotationReports.CompareProductsByDateRangeAsync(start1, end1, start2, end2, topCount);

            return Ok(new ApiResponse<ComparisonProductResultDTO>
            {
                Success = true,
                Message = $"{result.TotalProducts} ürün karşılaştırıldı. Yeni: {result.NewProducts}, Çıkarılan: {result.RemovedProducts}, Artan: {result.IncreasedProducts}, Azalan: {result.DecreasedProducts}",
                Data = result
            });
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Ürün karşılaştırması hatası: {ex.Message}");
            return StatusCode(500, new ApiResponse<ComparisonProductResultDTO>
            {
                Success = false,
                Message = "Sunucu hatası oluştu",
                Data = null,
                Error = ex.Message
            });
        }
    }

    /// <summary>
    /// İki tarih aralığında müşterileri karşılaştırır
    /// </summary>
    /// <param name="startDate1">Period 1 - Başlangıç tarihi (YYYY-MM-DD)</param>
    /// <param name="endDate1">Period 1 - Bitiş tarihi (YYYY-MM-DD)</param>
    /// <param name="startDate2">Period 2 - Başlangıç tarihi (YYYY-MM-DD)</param>
    /// <param name="endDate2">Period 2 - Bitiş tarihi (YYYY-MM-DD)</param>
    /// <param name="topCount">Kaç tane müşteri gösterilecek (varsayılan: 10)</param>
    /// <returns>Karşılaştırılmış müşteri verileri</returns>
    [HttpGet("compare-customers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse<ComparisonCustomerResultDTO>>> CompareCustomers(
        [FromQuery] string startDate1,
        [FromQuery] string endDate1,
        [FromQuery] string startDate2,
        [FromQuery] string endDate2,
        [FromQuery] int topCount = 10)
    {
        try
        {
            if (!DateTime.TryParse(startDate1, out var start1) || !DateTime.TryParse(endDate1, out var end1) ||
                !DateTime.TryParse(startDate2, out var start2) || !DateTime.TryParse(endDate2, out var end2))
            {
                return BadRequest(new ApiResponse<ComparisonCustomerResultDTO>
                {
                    Success = false,
                    Message = "Geçersiz tarih formatı. YYYY-MM-DD formatını kullanın.",
                    Data = null
                });
            }

            if (start1 > end1 || start2 > end2)
            {
                return BadRequest(new ApiResponse<ComparisonCustomerResultDTO>
                {
                    Success = false,
                    Message = "Başlangıç tarihleri bitiş tarihlerinden önce olmalıdır.",
                    Data = null
                });
            }

            if (topCount <= 0 || topCount > 100)
            {
                return BadRequest(new ApiResponse<ComparisonCustomerResultDTO>
                {
                    Success = false,
                    Message = "topCount değeri 1 ile 100 arasında olmalıdır.",
                    Data = null
                });
            }

            _logger.LogInformation($"📊 Müşteri karşılaştırması: P1({start1:yyyy-MM-dd}-{end1:yyyy-MM-dd}) vs P2({start2:yyyy-MM-dd}-{end2:yyyy-MM-dd})");

            var result = await _quotationReports.CompareCustomersByDateRangeAsync(start1, end1, start2, end2, topCount);

            return Ok(new ApiResponse<ComparisonCustomerResultDTO>
            {
                Success = true,
                Message = $"{result.TotalCustomers} müşteri karşılaştırıldı. Yeni: {result.NewCustomers}, Kaybedilen: {result.LostCustomers}, Artan: {result.IncreasingCustomers}, Azalan: {result.DecreasingCustomers}",
                Data = result
            });
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Müşteri karşılaştırması hatası: {ex.Message}");
            return StatusCode(500, new ApiResponse<ComparisonCustomerResultDTO>
            {
                Success = false,
                Message = "Sunucu hatası oluştu",
                Data = null,
                Error = ex.Message
            });
        }
    }

    /// <summary>
    /// Belirli tarih aralığında tüm teklifleri getirir
    /// </summary>
    /// <param name="startDate">Başlangıç tarihi (YYYY-MM-DD)</param>
    /// <param name="endDate">Bitiş tarihi (YYYY-MM-DD)</param>
    /// <returns>Quotation JSON'ı</returns>
    [HttpGet("quotations")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse<object>>> GetQuotations(
        [FromQuery] string startDate,
        [FromQuery] string endDate)
    {
        try
        {
            if (!DateTime.TryParse(startDate, out var start) || !DateTime.TryParse(endDate, out var end))
            {
                return BadRequest(new ApiResponse<object>
                {
                    Success = false,
                    Message = "Geçersiz tarih formatı. YYYY-MM-DD formatını kullanın.",
                    Data = null
                });
            }

            if (start > end)
            {
                return BadRequest(new ApiResponse<object>
                {
                    Success = false,
                    Message = "Başlangıç tarihi bitiş tarihinden önce olmalıdır.",
                    Data = null
                });
            }

            _logger.LogInformation($"📊 Tüm teklifler istendi: {start:yyyy-MM-dd} - {end:yyyy-MM-dd}");

            var quotations = await _quotationReports.GetQuotationReportAsync(start, end);

            return Ok(new ApiResponse<object>
            {
                Success = true,
                Message = "Teklif verileri getirildi",
                Data = quotations
            });
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Teklifler hatası: {ex.Message}");
            return StatusCode(500, new ApiResponse<object>
            {
                Success = false,
                Message = "Sunucu hatası oluştu",
                Data = null,
                Error = ex.Message
            });
        }
    }
}

// ============================================
// API Response Models
// ============================================

public class ApiResponse<T>
{
    public bool Success { get; set; }
    public string Message { get; set; }
    public T Data { get; set; }
    public string Error { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}