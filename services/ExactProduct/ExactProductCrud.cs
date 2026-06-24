using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using ShopifyProductApp.Services;
using System.Text;
using ExactOnline.Models;
using ExactOnline.Converters;

public class ExactProductCrud
{
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _redirectUri;
    private readonly ITokenManager _tokenManager;
    private readonly string _baseUrl;
    private readonly string _divisionCode;
    private readonly ILogger _logger;
    private readonly string _tokenFile;
    private readonly ISettingsService _settingsService;

    public ExactProductCrud(
     string clientId,
     string clientSecret,
     string redirectUri,
     ITokenManager tokenManager,
     string baseUrl,
     string divisionCode,
     string tokenFile,
     ISettingsService settingsService,
     IServiceProvider serviceProvider,
     ILogger logger)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _redirectUri = redirectUri;
        _tokenManager = tokenManager;
        _baseUrl = baseUrl;
        _divisionCode = divisionCode;
        _tokenFile = tokenFile;
        _settingsService = settingsService;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }


    //Get Product by ItemCode

   public async Task<ExactProductResponse?> GetItemByCodeAsync(string itemCode)
{
    var exactService = _serviceProvider.GetRequiredService<ExactService>();
    var token = await exactService.GetValidToken();

    if (token == null)
    {
        _logger.LogError("Token alınamadı");
        return new ExactProductResponse 
        { 
            Success = false, 
            ProcessedCount = 0, 
            Results = new List<ExactProduct>() 
        };
    }

    using var client = new HttpClient();
    client.Timeout = TimeSpan.FromMinutes(2);
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    int retryCount = 0;
    const int maxRetries = 3;

    while (retryCount < maxRetries)
    {
        try
        {
            // ✅ Code'a göre filtrele
            var filter = Uri.EscapeDataString($"Code eq '{itemCode}'");
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/Items?$filter={filter}";

            _logger.LogInformation($"🔍 Ürün aranıyor: {itemCode}");
            _logger.LogInformation($"📡 API URL: {url}");

            var response = await client.GetAsync(url);

            // Hata kontrolü
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"❌ API Hatası: {response.StatusCode} - {response.ReasonPhrase}");
                _logger.LogError($"❌ Hata Detayı: {errorContent}");

                // Unauthorized - Token yenile
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    _logger.LogWarning("🔑 Token süresi dolmuş, yeniden deneniyor...");
                    token = await exactService.GetValidToken();
                    if (token == null) return new ExactProductResponse 
                    { 
                        Success = false, 
                        ProcessedCount = 0, 
                        Results = new List<ExactProduct>() 
                    };

                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
                    retryCount++;
                    continue;
                }

                // Too Many Requests
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    retryCount++;
                    _logger.LogWarning($"⏳ Rate limit aşıldı, {retryCount}. deneme için 10 saniye bekleniyor...");
                    await Task.Delay(10000);
                    continue;
                }

                return new ExactProductResponse 
                { 
                    Success = false, 
                    ProcessedCount = 0, 
                    Results = new List<ExactProduct>() 
                };
            }

            // JSON parse
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("d", out var dataElement))
            {
                _logger.LogWarning("⚠️ Beklenmeyen JSON yapısı: 'd' property bulunamadı");
                return new ExactProductResponse 
                { 
                    Success = false, 
                    ProcessedCount = 0, 
                    Results = new List<ExactProduct>() 
                };
            }

            JsonElement resultsElement;
            if (dataElement.ValueKind == JsonValueKind.Object && dataElement.TryGetProperty("results", out var res))
            {
                resultsElement = res;
            }
            else if (dataElement.ValueKind == JsonValueKind.Array)
            {
                resultsElement = dataElement;
            }
            else
            {
                _logger.LogWarning("⚠️ Beklenmeyen JSON yapısı");
                return new ExactProductResponse 
                { 
                    Success = false, 
                    ProcessedCount = 0, 
                    Results = new List<ExactProduct>() 
                };
            }

            // İlk sonucu al (Code unique olmalı)
            if (resultsElement.GetArrayLength() == 0)
            {
                _logger.LogWarning($"⚠️ Ürün bulunamadı: {itemCode}");
                return new ExactProductResponse 
                { 
                    Success = false, 
                    ProcessedCount = 0, 
                    Results = new List<ExactProduct>() 
                };
            }

            var item = resultsElement[0];
            var exactProduct = item.Deserialize<ExactProduct>(new System.Text.Json.JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true 
            });

            if (exactProduct == null)
            {
                _logger.LogWarning($"⚠️ Ürün deserialization başarısız: {itemCode}");
                return new ExactProductResponse 
                { 
                    Success = false, 
                    ProcessedCount = 0, 
                    Results = new List<ExactProduct>() 
                };
            }

            _logger.LogInformation($"✅ Ürün bulundu: {itemCode}");

            // Ürün bilgilerini logla
            if (!string.IsNullOrEmpty(exactProduct.Description))
            {
                _logger.LogInformation($"📦 Ürün Adı: {exactProduct.Description}");
            }
            if (exactProduct.ID != Guid.Empty)
            {
                _logger.LogInformation($"🆔 Ürün ID: {exactProduct.ID}");
                bool hasBundle = await exactService.GetItemExtraFieldAsync(exactProduct.ID.ToString());
                if (hasBundle)
                {
                    _logger.LogInformation($"✅ isBundle mevcut ve dolu.");
                }
                else
                {
                    _logger.LogInformation($"ℹ️ isBundle mevcut değil veya boş/false.");
                }
            }

            return new ExactProductResponse 
            { 
                Success = true, 
                ProcessedCount = 1, 
                Results = new List<ExactProduct> { exactProduct } 
            };
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError($"⏰ Timeout hatası: {ex.Message}");
            retryCount++;
            if (retryCount < maxRetries)
            {
                await Task.Delay(2000);
                continue;
            }
            return new ExactProductResponse 
            { 
                Success = false, 
                ProcessedCount = 0, 
                Results = new List<ExactProduct>() 
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError($"🌐 Network hatası: {ex.Message}");
            retryCount++;
            if (retryCount < maxRetries)
            {
                await Task.Delay(5000);
                continue;
            }
            return new ExactProductResponse 
            { 
                Success = false, 
                ProcessedCount = 0, 
                Results = new List<ExactProduct>() 
            };
        }
        catch (JsonException ex)
        {
            _logger.LogError($"📄 JSON parse hatası: {ex.Message}");
            return new ExactProductResponse 
            { 
                Success = false, 
                ProcessedCount = 0, 
                Results = new List<ExactProduct>() 
            };
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Beklenmeyen hata: {ex.Message}");
            _logger.LogError($"Stack trace: {ex.StackTrace}");
            return new ExactProductResponse 
            { 
                Success = false, 
                ProcessedCount = 0, 
                Results = new List<ExactProduct>() 
            };
        }
    }

    _logger.LogError($"❌ Maksimum deneme sayısına ulaşıldı");
    return new ExactProductResponse 
    { 
        Success = false, 
        ProcessedCount = 0, 
        Results = new List<ExactProduct>() 
    };
}

//yeni test için no discount
public async Task<ExactProductResponse?> GetItemByCodeNoDiscountAsync(string itemCode)
{
    var exactService = _serviceProvider.GetRequiredService<ExactService>();
    var token = await exactService.GetValidToken();

    if (token == null)
    {
        _logger.LogError("Token alınamadı");
        return new ExactProductResponse 
        { 
            Success = false, 
            ProcessedCount = 0, 
            Results = new List<ExactProduct>() 
        };
    }

    using var client = new HttpClient();
    client.Timeout = TimeSpan.FromMinutes(2);
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    int retryCount = 0;
    const int maxRetries = 3;

    while (retryCount < maxRetries)
    {
        try
        {
            // ✅ Code'a göre filtrele
            var filter = Uri.EscapeDataString($"Code eq '{itemCode}'");
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/Items?$filter={filter}";

            _logger.LogInformation($"🔍 Ürün aranıyor: {itemCode}");
            _logger.LogInformation($"📡 API URL: {url}");

            var response = await client.GetAsync(url);

            // Hata kontrolü
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"❌ API Hatası: {response.StatusCode} - {response.ReasonPhrase}");
                _logger.LogError($"❌ Hata Detayı: {errorContent}");

                // Unauthorized - Token yenile
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    _logger.LogWarning("🔑 Token süresi dolmuş, yeniden deneniyor...");
                    token = await exactService.GetValidToken();
                    if (token == null) return new ExactProductResponse 
                    { 
                        Success = false, 
                        ProcessedCount = 0, 
                        Results = new List<ExactProduct>() 
                    };

                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
                    retryCount++;
                    continue;
                }

                // Too Many Requests
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    retryCount++;
                    _logger.LogWarning($"⏳ Rate limit aşıldı, {retryCount}. deneme için 10 saniye bekleniyor...");
                    await Task.Delay(10000);
                    continue;
                }

                return new ExactProductResponse 
                { 
                    Success = false, 
                    ProcessedCount = 0, 
                    Results = new List<ExactProduct>() 
                };
            }

            // JSON parse
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("d", out var dataElement))
            {
                _logger.LogWarning("⚠️ Beklenmeyen JSON yapısı: 'd' property bulunamadı");
                return new ExactProductResponse 
                { 
                    Success = false, 
                    ProcessedCount = 0, 
                    Results = new List<ExactProduct>() 
                };
            }

            JsonElement resultsElement;
            if (dataElement.ValueKind == JsonValueKind.Object && dataElement.TryGetProperty("results", out var res))
            {
                resultsElement = res;
            }
            else if (dataElement.ValueKind == JsonValueKind.Array)
            {
                resultsElement = dataElement;
            }
            else
            {
                _logger.LogWarning("⚠️ Beklenmeyen JSON yapısı");
                return new ExactProductResponse 
                { 
                    Success = false, 
                    ProcessedCount = 0, 
                    Results = new List<ExactProduct>() 
                };
            }

            // İlk sonucu al (Code unique olmalı)
            if (resultsElement.GetArrayLength() == 0)
            {
                _logger.LogWarning($"⚠️ Ürün bulunamadı: {itemCode}");
                return new ExactProductResponse 
                { 
                    Success = false, 
                    ProcessedCount = 0, 
                    Results = new List<ExactProduct>() 
                };
            }

            var item = resultsElement[0];
            var exactProduct = item.Deserialize<ExactProduct>(new System.Text.Json.JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true 
            });

            if (exactProduct == null)
            {
                _logger.LogWarning($"⚠️ Ürün deserialization başarısız: {itemCode}");
                return new ExactProductResponse 
                { 
                    Success = false, 
                    ProcessedCount = 0, 
                    Results = new List<ExactProduct>() 
                };
            }

            _logger.LogInformation($"✅ Ürün bulundu: {itemCode}");

            // Ürün bilgilerini logla
            if (!string.IsNullOrEmpty(exactProduct.Description))
            {
                _logger.LogInformation($"📦 Ürün Adı: {exactProduct.Description}");
            }
            if (exactProduct.ID != Guid.Empty)
            {
                _logger.LogInformation($"🆔 Ürün ID: {exactProduct.ID}");
                bool hasBundle = await exactService.GetItemExtraFieldNoDiscountAsync(exactProduct.ID.ToString());
                if (hasBundle)
                {
                    _logger.LogInformation($"✅ No Discount mevcut ve dolu.");
                }
                else
                {
                    _logger.LogInformation($"ℹ️ nodiscount mevcut değil veya boş/false.");
                }
            }

            return new ExactProductResponse 
            { 
                Success = true, 
                ProcessedCount = 1, 
                Results = new List<ExactProduct> { exactProduct } 
            };
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError($"⏰ Timeout hatası: {ex.Message}");
            retryCount++;
            if (retryCount < maxRetries)
            {
                await Task.Delay(2000);
                continue;
            }
            return new ExactProductResponse 
            { 
                Success = false, 
                ProcessedCount = 0, 
                Results = new List<ExactProduct>() 
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError($"🌐 Network hatası: {ex.Message}");
            retryCount++;
            if (retryCount < maxRetries)
            {
                await Task.Delay(5000);
                continue;
            }
            return new ExactProductResponse 
            { 
                Success = false, 
                ProcessedCount = 0, 
                Results = new List<ExactProduct>() 
            };
        }
        catch (JsonException ex)
        {
            _logger.LogError($"📄 JSON parse hatası: {ex.Message}");
            return new ExactProductResponse 
            { 
                Success = false, 
                ProcessedCount = 0, 
                Results = new List<ExactProduct>() 
            };
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Beklenmeyen hata: {ex.Message}");
            _logger.LogError($"Stack trace: {ex.StackTrace}");
            return new ExactProductResponse 
            { 
                Success = false, 
                ProcessedCount = 0, 
                Results = new List<ExactProduct>() 
            };
        }
    }

    _logger.LogError($"❌ Maksimum deneme sayısına ulaşıldı");
    return new ExactProductResponse 
    { 
        Success = false, 
        ProcessedCount = 0, 
        Results = new List<ExactProduct>() 
    };
}
}

