using Microsoft.AspNetCore.Mvc;
using ShopifyProductApp.Services;

namespace ShopifyProductApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SettingsController : ControllerBase
    {
        private readonly SettingsService _settingsService;

        public SettingsController(SettingsService settingsService)
        {
            _settingsService = settingsService;
        }

        [HttpGet]
        public async Task<IActionResult> GetAllSettings()
        {
            try
            {
                var settings = await _settingsService.GetAllSettingsAsync();
                return Ok(new
                {
                    Success = true,
                    Count = settings.Count,
                   Settings = settings.GroupBy(s => s.Category ?? "Other").ToDictionary(g => g.Key, g => g.ToList())
                });
            }
            catch (Exception ex)
            {
                return Ok(new { Success = false, Error = ex.Message });
            }
        }

        [HttpGet("{key}")]
        public async Task<IActionResult> GetSetting(string key)
        {
            try
            {
                var value = await _settingsService.GetSettingAsync(key);
                
                if (value == null)
                {
                    return Ok(new
                    {
                        Success = false,
                        Message = $"Setting '{key}' bulunamadı",
                        Key = key
                    });
                }

                return Ok(new
                {
                    Success = true,
                    Key = key,
                    Value = value
                });
            }
            catch (Exception ex)
            {
                return Ok(new { Success = false, Error = ex.Message });
            }
        }

        [HttpPost("{key}")]
        public async Task<IActionResult> SetSetting(string key, [FromBody] SetSettingRequest request)
        {
            try
            {
                await _settingsService.SetSettingAsync(key, request.Value, request.Description, request.Category);
                
                return Ok(new
                {
                    Success = true,
                    Message = $"Setting '{key}' başarıyla kaydedildi",
                    Key = key,
                    Value = request.Value
                });
            }
            catch (Exception ex)
            {
                return Ok(new { Success = false, Error = ex.Message });
            }
        }

        [HttpGet("category/{category}")]
        public async Task<IActionResult> GetSettingsByCategory(string category)
        {
            try
            {
                var settings = await _settingsService.GetSettingsByCategoryAsync(category);
                
                return Ok(new
                {
                    Success = true,
                    Category = category,
                    Count = settings.Count,
                    Settings = settings
                });
            }
            catch (Exception ex)
            {
                return Ok(new { Success = false, Error = ex.Message });
            }
        }

        [HttpGet("exact/config")]
        public async Task<IActionResult> GetExactConfig()
        {
            try
            {
                var exactSettings = await _settingsService.GetExactSettingsAsync();
                
                return Ok(new
                {
                    Success = true,
                    ExactSettings = exactSettings
                });
            }
            catch (Exception ex)
            {
                return Ok(new { Success = false, Error = ex.Message });
            }
        }

        [HttpGet("shopify/config")]
        public async Task<IActionResult> GetShopifyConfig()
        {
            try
            {
                var shopifySettings = await _settingsService.GetShopifySettingsAsync();
                
                return Ok(new
                {
                    Success = true,
                    ShopifySettings = shopifySettings
                });
            }
            catch (Exception ex)
            {
                return Ok(new { Success = false, Error = ex.Message });
            }
        }

        [HttpGet("exact/token")]
        public async Task<IActionResult> GetExactToken()
        {
            try
            {
                var tokenInfo = await _settingsService.GetExactTokenInfoAsync();
                
                return Ok(new
                {
                    Success = true,
                    TokenInfo = new
                    {
                        HasAccessToken = !string.IsNullOrEmpty(tokenInfo.AccessToken),
                        HasRefreshToken = !string.IsNullOrEmpty(tokenInfo.RefreshToken),
                        TokenType = tokenInfo.TokenType,
                        ExpiryTime = tokenInfo.ExpiryTime,
                        ExpiresIn = tokenInfo.ExpiresIn,
                        IsExpired = tokenInfo.IsExpired(),
                        // Güvenlik için token'ları tam olarak gösterme
                        AccessTokenPreview = !string.IsNullOrEmpty(tokenInfo.AccessToken) ? 
                            tokenInfo.AccessToken.Substring(0, Math.Min(20, tokenInfo.AccessToken.Length)) + "..." : null
                    }
                });
            }
            catch (Exception ex)
            {
                return Ok(new { Success = false, Error = ex.Message });
            }
        }

        [HttpPost("exact/token")]
        public async Task<IActionResult> UpdateExactToken([FromBody] UpdateTokenRequest request)
        {
            try
            {
                var expiryTime = DateTime.UtcNow.AddSeconds(request.ExpiresIn);
                
                await _settingsService.UpdateExactTokenAsync(
                    request.AccessToken, 
                    request.RefreshToken, 
                    expiryTime, 
                    request.ExpiresIn
                );
                
                return Ok(new
                {
                    Success = true,
                    Message = "Exact token bilgileri başarıyla güncellendi",
                    ExpiryTime = expiryTime
                });
            }
            catch (Exception ex)
            {
                return Ok(new { Success = false, Error = ex.Message });
            }
        }

        [HttpDelete("{key}")]
        public async Task<IActionResult> DeleteSetting(string key)
        {
            try
            {
                await _settingsService.DeleteSettingAsync(key);
                
                return Ok(new
                {
                    Success = true,
                    Message = $"Setting '{key}' başarıyla silindi",
                    Key = key
                });
            }
            catch (Exception ex)
            {
                return Ok(new { Success = false, Error = ex.Message });
            }
        }
    }

    // Request modelleri
    public class SetSettingRequest
    {
        public string Value { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Category { get; set; }
    }

    public class UpdateTokenRequest
    {
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public int ExpiresIn { get; set; } = 600;
    }
}