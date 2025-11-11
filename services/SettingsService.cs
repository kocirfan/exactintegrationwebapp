using Microsoft.EntityFrameworkCore;
using ShopifyProductApp.Data;
using ShopifyProductApp.Models;

namespace ShopifyProductApp.Services
{
    // ✅ INTERFACE TANIMI EKLE
    public interface ISettingsService
    {
        Task<string?> GetSettingAsync(string key);
        Task SetSettingAsync(string key, string value, string? description = null, string? category = null);
        Task<List<GeneralSetting>> GetSettingsByCategoryAsync(string category);
        Task<List<GeneralSetting>> GetAllSettingsAsync();
        Task<ExactSettings> GetExactSettingsAsync();
        Task<ShopifySettings> GetShopifySettingsAsync();
        Task<ExactTokenInfo> GetExactTokenInfoAsync();
        Task UpdateExactTokenAsync(string accessToken, string refreshToken, DateTime expiryTime, int expiresIn = 600);
        Task DeleteSettingAsync(string key);
    }

    // ✅ CLASS'A INTERFACE IMPLEMENT ET
    public class SettingsService : ISettingsService
    {
        private readonly ApplicationDbContext _context;

        public SettingsService(ApplicationDbContext context)
        {
            _context = context;
        }

        // Tek bir setting değerini getir
        public async Task<string?> GetSettingAsync(string key)
        {
            var setting = await _context.GeneralSettings
                .Where(s => s.Key == key)
                .FirstOrDefaultAsync();
            
            return setting?.Value;
        }

        // Tek bir setting değerini kaydet/güncelle
        public async Task SetSettingAsync(string key, string value, string? description = null, string? category = null)
        {
            var setting = await _context.GeneralSettings
                .Where(s => s.Key == key)
                .FirstOrDefaultAsync();

            if (setting == null)
            {
                // Yeni kayıt oluştur
                setting = new GeneralSetting
                {
                    Key = key,
                    Value = value,
                    Description = description,
                    Category = category,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };
                _context.GeneralSettings.Add(setting);
            }
            else
            {
                // Mevcut kaydı güncelle
                setting.Value = value;
                setting.UpdatedAt = DateTime.Now;
                
                if (!string.IsNullOrEmpty(description))
                    setting.Description = description;
                
                if (!string.IsNullOrEmpty(category))
                    setting.Category = category;
            }

            await _context.SaveChangesAsync();
        }

        // Kategoriye göre ayarları getir
        public async Task<List<GeneralSetting>> GetSettingsByCategoryAsync(string category)
        {
            return await _context.GeneralSettings
                .Where(s => s.Category == category)
                .OrderBy(s => s.Key)
                .ToListAsync();
        }

        // Tüm ayarları getir
        public async Task<List<GeneralSetting>> GetAllSettingsAsync()
        {
            return await _context.GeneralSettings
                .OrderBy(s => s.Category)
                .ThenBy(s => s.Key)
                .ToListAsync();
        }

        // Exact Online ayarlarını getir
        public async Task<ExactSettings> GetExactSettingsAsync()
        {
            var exactSettings = await _context.GeneralSettings
                .Where(s => s.Category == "Exact")
                .ToDictionaryAsync(s => s.Key, s => s.Value);

            return new ExactSettings
            {
                ClientId = exactSettings.GetValueOrDefault("ExactClientId"),
                ClientSecret = exactSettings.GetValueOrDefault("ExactClientSecret"),
                RedirectUri = exactSettings.GetValueOrDefault("ExactRedirectUri"),
                BaseUrl = exactSettings.GetValueOrDefault("ExactBaseUrl"),
                DivisionCode = exactSettings.GetValueOrDefault("ExactDivisionCode")
            };
        }

        // Shopify ayarlarını getir
        public async Task<ShopifySettings> GetShopifySettingsAsync()
        {
            var shopifySettings = await _context.GeneralSettings
                .Where(s => s.Category == "Shopify")
                .ToDictionaryAsync(s => s.Key, s => s.Value);

            return new ShopifySettings
            {
                StoreUrl = shopifySettings.GetValueOrDefault("ShopifyStoreUrl"),
                AccessToken = shopifySettings.GetValueOrDefault("ShopifyAccessToken")
            };
        }

        // Exact Token ayarlarını getir
        public async Task<ExactTokenInfo> GetExactTokenInfoAsync()
        {
            var tokenSettings = await _context.GeneralSettings
                .Where(s => s.Category == "ExactToken")
                .ToDictionaryAsync(s => s.Key, s => s.Value);

            return new ExactTokenInfo
            {
                AccessToken = tokenSettings.GetValueOrDefault("ExactAccessToken"),
                RefreshToken = tokenSettings.GetValueOrDefault("ExactRefreshToken"),
                TokenType = tokenSettings.GetValueOrDefault("ExactTokenType"),
                ExpiryTime = tokenSettings.GetValueOrDefault("ExactTokenExpiry"),
                ExpiresIn = int.TryParse(tokenSettings.GetValueOrDefault("ExactExpiresIn"), out var expires) ? expires : 600
            };
        }

        // Exact Token bilgilerini güncelle
        public async Task UpdateExactTokenAsync(string accessToken, string refreshToken, DateTime expiryTime, int expiresIn = 600)
        {
            await SetSettingAsync("ExactAccessToken", accessToken, "Exact Online Access Token", "ExactToken");
            await SetSettingAsync("ExactRefreshToken", refreshToken, "Exact Online Refresh Token", "ExactToken");
            
            // ✅ Bu doğru - UTC formatında kaydediyor
            await SetSettingAsync("ExactTokenExpiry", expiryTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), "Exact Online Token Expiry Time", "ExactToken");
            
            await SetSettingAsync("ExactExpiresIn", expiresIn.ToString(), "Exact Online Token Expires In (seconds)", "ExactToken");
        }

        // Setting silme
        public async Task DeleteSettingAsync(string key)
        {
            var setting = await _context.GeneralSettings
                .Where(s => s.Key == key)
                .FirstOrDefaultAsync();

            if (setting != null)
            {
                _context.GeneralSettings.Remove(setting);
                await _context.SaveChangesAsync();
            }
        }
    }

    // Helper sınıflar
    public class ExactSettings
    {
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }
        public string? RedirectUri { get; set; }
        public string? BaseUrl { get; set; }
        public string? DivisionCode { get; set; }
    }

    public class ShopifySettings
    {
        public string? StoreUrl { get; set; }
        public string? AccessToken { get; set; }
    }

    public class ExactTokenInfo
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public string? TokenType { get; set; }
        public string? ExpiryTime { get; set; }
        public int ExpiresIn { get; set; }
        
        public bool IsExpired()
        {
            if (DateTime.TryParse(ExpiryTime, out var expiry))
            {
                return DateTime.UtcNow >= expiry; // ✅ DateTime.Now yerine DateTime.UtcNow kullan!
            }
            return true; // Eğer tarih parse edilemezse expired kabul et
        }
    }
}