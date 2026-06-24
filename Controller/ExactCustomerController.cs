using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ExactCustomerController : ControllerBase
{
    private readonly ExactCustomerCrud _exactCustomerCrud;
    private readonly ILogger<ExactCustomerController> _logger;

    public ExactCustomerController(
        ExactCustomerCrud exactCustomerCrud,
        ILogger<ExactCustomerController> logger)
    {
        _exactCustomerCrud = exactCustomerCrud;
        _logger = logger;
    }

    /// <summary>
    /// Verilen e-posta adresine göre Exact'tan müşteri bilgilerini getirir.
    /// GET /api/exactcustomer/by-email?email=ornek@mail.com
    /// </summary>
    [HttpGet("by-email")]
    public async Task<IActionResult> GetCustomerByEmail([FromQuery] string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return BadRequest(new { error = "email parametresi zorunludur." });

        _logger.LogInformation($"Müşteri aranıyor: {email}");

        var customer = await _exactCustomerCrud.GetCustomerByEmailAsync(email);

        if (customer == null)
            return NotFound(new { error = $"'{email}' e-postasına sahip müşteri bulunamadı." });

        return Ok(customer);
    }
}
