using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ShopifyProductApp.Controller
{
    [Authorize]
    [ApiController]
    [Route("api/v1/system")]
    public class SystemController : ControllerBase
    {
        private readonly ExactService _exactService;

        public SystemController(ExactService exactService)
        {
            _exactService = exactService;
        }

        /// <summary>
        /// Exact Online'dan tüm kullanıcıları getirir.
        /// GET /api/v1/system/Users
        /// </summary>
        [HttpGet("Users")]
        public async Task<IActionResult> GetAllUsers()
        {
            try
            {
                var users = await _exactService.GetUsersAsync();
                return Ok(users);
            }
            catch (Exception ex)
            {
                return StatusCode(502, new { error = ex.Message });
            }
        }
    }
}
