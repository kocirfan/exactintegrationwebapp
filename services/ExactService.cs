using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using ShopifyProductApp.Services;
using System.Text;
using ExactOnline.Models;
using ExactOnline.Converters;



public class ExactService
{
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _redirectUri;
    private readonly ITokenManager _tokenManager; // ✅ YENİ
    private readonly string _baseUrl;
    private readonly string _divisionCode;
    private readonly ILogger<ExactService> _logger;
    private readonly string _tokenFile; // Backup için hala tutabiliriz
    private readonly ISettingsService _settingsService;
    private static readonly SemaphoreSlim _tokenRefreshLock = new SemaphoreSlim(1, 1);

    public ExactService(
        string clientId,
        string clientSecret,
        string redirectUri,
 ITokenManager tokenManager,
        string baseUrl,
        string divisionCode,
        string tokenFile,
         ILogger<ExactService> logger,
       ISettingsService settingsService) // ✨ YENİ PARAMETRE
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _redirectUri = redirectUri;
        _tokenManager = tokenManager;
        _baseUrl = baseUrl;
        _divisionCode = divisionCode;
        _logger = logger;
        _tokenFile = tokenFile;
        _settingsService = settingsService; // ✨ YENİ EKLEME
    }

    //yeni eklenen ürün
    public async Task<List<ExactProduct>> GetNewCreatedProductAsync()
    {
        var token = await GetValidToken();
        if (token == null) return new List<ExactProduct>(); // Boş liste döndür

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        int top = 60; // Exact Online limitine uygun
        int skip = 0;
        var webshopProducts = new List<ExactProduct>(); // ExactProduct listesi
        var yesterday = DateTime.UtcNow.AddDays(-2);
        var dateFilter = yesterday.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        while (true)
        {
            try
            {
                //var filterQuery = $"IsWebshopItem eq 1 and Modified gt datetime'{dateFilter}'";
                //var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/Items?$filter={Uri.EscapeDataString(filterQuery)}&$top={top}&$skip={skip}";
                //var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/Items?$filter=Created gt datetime'{dateFilter}'&$top={top}&$skip={skip}";
                var filterQuery = $"(Created gt datetime'{dateFilter}' or Modified gt datetime'{dateFilter}') and IsWebshopItem eq 1";
                var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/Items?$filter={Uri.EscapeDataString(filterQuery)}&$top={top}&$skip={skip}";
                var resp = await client.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning($"HTTP hatası: {resp.StatusCode}");
                    break;
                }

                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                // JSON yapısı dizi mi object mi kontrol et
                JsonElement dataElement = doc.RootElement.GetProperty("d");
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
                    _logger.LogError("Beklenmeyen JSON yapısı, boş liste döndürülüyor");
                    return webshopProducts;
                }

                int countInPage = 0;
                int webshopOneCount = 0;

                foreach (var item in resultsElement.EnumerateArray())
                {
                    // IsWebshopItem değerini kontrol et
                    if (item.TryGetProperty("IsWebshopItem", out var isWebshopItemProp))
                    {
                        bool isWebshop = false;

                        // Farklı veri türlerini kontrol et
                        switch (isWebshopItemProp.ValueKind)
                        {
                            case JsonValueKind.True:
                                isWebshop = true;
                                break;
                            case JsonValueKind.Number:
                                isWebshop = isWebshopItemProp.GetDouble() == 1;
                                break;
                            case JsonValueKind.String:
                                var stringValue = isWebshopItemProp.GetString();
                                isWebshop = stringValue == "1" || stringValue?.ToLower() == "true";
                                break;
                        }

                        // IsWebshopItem = 1 ise tüm item'ı ExactProduct'a çevir
                        if (isWebshop)
                        {
                            try
                            {
                                var product = JsonSerializer.Deserialize<ExactProduct>(item.GetRawText());
                                if (product != null)
                                {
                                    webshopProducts.Add(product);
                                    webshopOneCount++;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Ürün deserialize edilemedi");
                            }
                        }
                    }
                    countInPage++;
                }

                Console.WriteLine($"Sayfa {skip / top + 1}: {countInPage} ürün alındı, {webshopOneCount} adet IsWebshopItem=1 bulundu.");

                if (countInPage < top) break; // Son sayfa
                skip += top;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "JSON parse hatası, mevcut sonuçlar döndürülüyor");
                break;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP isteği hatası, mevcut sonuçlar döndürülüyor");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Beklenmeyen hata, mevcut sonuçlar döndürülüyor");
                break;
            }
        }

        Console.WriteLine($"Toplam IsWebshopItem=1 olan ürün sayısı: {webshopProducts.Count}");

        // Tüm ürünleri logla
        if (webshopProducts.Any())
        {
            Console.WriteLine("Bulunan tüm ürünler:");
            foreach (var product in webshopProducts)
            {
                Console.WriteLine($"  - Code: {product.Code}, Description: {product.Description}");
            }
        }

        return webshopProducts; // Hiç yoksa boş liste, varsa dolu liste
    }

    // son 24 saatte güncellenen ürünlerin webshop 0 olanların codesini döner
    public async Task<List<string>?> GetNonWebshopItemCodesAsync()
    {
        var token = await GetValidToken();
        if (token == null) return null;

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        int top = 60; // Exact Online limitine uygun
        int skip = 0;
        var nonWebshopCodes = new List<string>();
        var yesterday = DateTime.UtcNow.AddDays(-10);
        var dateFilter = yesterday.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        while (true)
        {
            try
            {

                // var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/Items?$filter=Modified gt datetime'{dateFilter}'&$top={top}&$skip={skip}";
                var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/Items?$filter=(Created gt datetime'{dateFilter}' or Modified gt datetime'{dateFilter}')&$top={top}&$skip={skip}";
                var resp = await client.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("HTTP hatası: {StatusCode}", resp.StatusCode);
                    break;
                }

                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                // JSON yapısı dizi mi object mi kontrol et
                JsonElement dataElement = doc.RootElement.GetProperty("d");
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
                    _logger.LogError("Beklenmeyen JSON yapısı, mevcut sonuçlar döndürülüyor");
                    return nonWebshopCodes;
                }

                int countInPage = 0;
                int webshopZeroCount = 0;

                foreach (var item in resultsElement.EnumerateArray())
                {
                    // IsWebshopItem değerini kontrol et
                    if (item.TryGetProperty("IsWebshopItem", out var isWebshopItemProp))
                    {
                        bool isNonWebshop = false;

                        // Farklı veri türlerini kontrol et
                        switch (isWebshopItemProp.ValueKind)
                        {
                            case JsonValueKind.False:
                                isNonWebshop = true;
                                break;
                            case JsonValueKind.Number:
                                isNonWebshop = isWebshopItemProp.GetDouble() == 0;
                                break;
                            case JsonValueKind.String:
                                var stringValue = isWebshopItemProp.GetString();
                                isNonWebshop = stringValue == "0" || stringValue?.ToLower() == "false";
                                break;
                        }

                        // IsWebshopItem = 0 ise Code'u al
                        if (isNonWebshop && item.TryGetProperty("Code", out var codeProp))
                        {
                            var code = codeProp.GetString();
                            if (!string.IsNullOrEmpty(code))
                            {
                                nonWebshopCodes.Add(code);
                                webshopZeroCount++;
                            }
                        }
                    }
                    countInPage++;
                }

                Console.WriteLine($"Sayfa {skip / top + 1}: {countInPage} ürün alındı, {webshopZeroCount} adet IsWebshopItem=0 bulundu.");

                if (countInPage < top) break; // Son sayfa
                skip += top;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "JSON parse hatası, mevcut sonuçlar döndürülüyor");
                break;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP isteği hatası, mevcut sonuçlar döndürülüyor");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Beklenmeyen hata, mevcut sonuçlar döndürülüyor");
                break;
            }
        }

        Console.WriteLine($"Toplam IsWebshopItem=0 olan ürün Code sayısı: {nonWebshopCodes.Count}");

        // Tüm Code'ları logla
        if (nonWebshopCodes.Any())
        {
            Console.WriteLine("Bulunan tüm Code'lar:");
            foreach (var code in nonWebshopCodes)
            {
                Console.WriteLine($"  - {code}");
            }
        }

        return nonWebshopCodes;
    }

    //excat tüm ürünler fitresiz
    public async Task<List<Dictionary<string, object>>?> GetAllItemsAsync(int maxItems = 5000)
    {
        var token = await GetValidToken();
        if (token == null) return null;

        using var client = new HttpClient();

        // Timeout ekle
        client.Timeout = TimeSpan.FromMinutes(10);

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        int top = 60; // Exact Online limitine uygun
        int skip = 0;
        var allItems = new List<Dictionary<string, object>>();
        int retryCount = 0;
        const int maxRetries = 3;

        while (true)
        {
            try
            {
                var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/Items?$top={top}&$skip={skip}";

                Console.WriteLine($"📡 API çağrısı: Sayfa {skip / top + 1}");

                var resp = await client.GetAsync(url);

                // Detaylı hata yönetimi
                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"❌ API Hatası: {resp.StatusCode} - {resp.ReasonPhrase}");

                    // Rate limiting durumunda bekle ve tekrar dene
                    if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests && retryCount < maxRetries)
                    {
                        retryCount++;
                        Console.WriteLine($"⏳ Rate limit aşıldı, {retryCount}. deneme için 30 saniye bekleniyor...");
                        await Task.Delay(30000); // 30 saniye bekle
                        continue;
                    }

                    // Token süresi dolmuşsa
                    if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        Console.WriteLine("🔑 Token süresi dolmuş olabilir, yeniden deneniyor...");
                        token = await GetValidToken();
                        if (token == null) break;

                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
                        continue;
                    }

                    break; // Diğer hatalarda çık
                }

                retryCount = 0; // Başarılı istek sonrası retry sayacını sıfırla

                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                // JSON yapısı kontrol
                if (!doc.RootElement.TryGetProperty("d", out var dataElement))
                {
                    Console.WriteLine("⚠️ Beklenmeyen JSON yapısı: 'd' property bulunamadı");
                    break;
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
                    _logger.LogError("Beklenmeyen JSON yapısı, mevcut sonuçlar döndürülüyor");
                    return allItems;
                }

                int countInPage = 0;

                foreach (var item in resultsElement.EnumerateArray())
                {
                    // Maksimum item sayısını kontrol et
                    if (allItems.Count >= maxItems)
                    {
                        Console.WriteLine($"⚠️ Maksimum item limiti ({maxItems}) aşıldı, işlem durduruluyor");
                        return allItems;
                    }

                    var dict = new Dictionary<string, object>();
                    foreach (var prop in item.EnumerateObject())
                    {
                        dict[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.Number => prop.Value.GetDouble(),
                            JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.Null => string.Empty,
                            _ => prop.Value.ToString() ?? string.Empty
                        };
                    }
                    allItems.Add(dict);
                    countInPage++;
                }

                Console.WriteLine($"📦 Sayfa {skip / top + 1}: {countInPage} ürün alındı. Toplam: {allItems.Count}");

                if (countInPage < top) break; // Son sayfa
                skip += top;

                // API rate limiting için kısa bekleme
                await Task.Delay(200); // 200ms bekleme
            }
            catch (TaskCanceledException ex)
            {
                Console.WriteLine($"⏰ Timeout hatası: {ex.Message}");
                break;
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"🌐 Network hatası: {ex.Message}");

                if (retryCount < maxRetries)
                {
                    retryCount++;
                    Console.WriteLine($"🔄 {retryCount}. deneme için 5 saniye bekleniyor...");
                    await Task.Delay(5000);
                    continue;
                }
                break;
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"📄 JSON parse hatası: {ex.Message}");
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Beklenmeyen hata: {ex.Message}");
                break;
            }
        }

        Console.WriteLine($"✅ Toplam {allItems.Count} ürün başarıyla alındı");
        return allItems;
    }

    public async Task<List<string>?> GetInactiveItemCodesAsync(int maxItems = 5000)
    {
        var token = await GetValidToken();
        if (token == null) return null;

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        int top = 60;
        int skip = 0;
        var skuList = new List<string>();
        int retryCount = 0;
        const int maxRetries = 3;

        while (true)
        {
            try
            {
                // ✅ INACTIVE: EndDate dolu VE IsWebshopItem = 0
                var filter = Uri.EscapeDataString("EndDate ne null and IsWebshopItem eq 0");

                // ✅ Sadece Code alanını çek (performans için)
                var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/Items?$filter={filter}&$select=Code&$top={top}&$skip={skip}";

                _logger.LogInformation($"📡 API çağrısı: Sayfa {skip / top + 1}");

                var response = await client.GetAsync(url);

                // Detaylı hata yönetimi
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"❌ API Hatası: {response.StatusCode} - {response.ReasonPhrase}");
                    _logger.LogError($"❌ Hata Detayı: {errorContent}");

                    // Rate limiting durumunda bekle ve tekrar dene
                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && retryCount < maxRetries)
                    {
                        retryCount++;
                        _logger.LogWarning($"⏳ Rate limit aşıldı, {retryCount}. deneme için 30 saniye bekleniyor...");
                        await Task.Delay(30000);
                        continue;
                    }

                    // Token süresi dolmuşsa
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        _logger.LogWarning("🔑 Token süresi dolmuş olabilir, yeniden deneniyor...");
                        token = await GetValidToken();
                        if (token == null) break;

                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
                        continue;
                    }

                    break;
                }

                retryCount = 0;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("d", out var dataElement))
                {
                    _logger.LogWarning("⚠️ Beklenmeyen JSON yapısı: 'd' property bulunamadı");
                    break;
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
                    _logger.LogError("Beklenmeyen JSON yapısı, mevcut sonuçlar döndürülüyor");
                    return skuList;
                }

                int countInPage = 0;

                foreach (var item in resultsElement.EnumerateArray())
                {
                    if (skuList.Count >= maxItems)
                    {
                        _logger.LogInformation($"⚠️ Maksimum item limiti ({maxItems}) aşıldı, işlem durduruluyor");
                        return skuList;
                    }

                    // ✅ Code değerini al
                    if (item.TryGetProperty("Code", out var codeElement))
                    {
                        var code = codeElement.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(code))
                        {
                            skuList.Add(code);
                            countInPage++;
                        }
                    }
                }

                _logger.LogInformation($"📦 Sayfa {skip / top + 1}: {countInPage} ürün kodu alındı. Toplam: {skuList.Count}");

                if (countInPage < top) break;
                skip += top;

                await Task.Delay(200);
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError($"⏰ Timeout hatası: {ex.Message}");
                break;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError($"🌐 Network hatası: {ex.Message}");

                if (retryCount < maxRetries)
                {
                    retryCount++;
                    _logger.LogWarning($"🔄 {retryCount}. deneme için 5 saniye bekleniyor...");
                    await Task.Delay(5000);
                    continue;
                }
                break;
            }
            catch (JsonException ex)
            {
                _logger.LogError($"📄 JSON parse hatası: {ex.Message}");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Beklenmeyen hata: {ex.Message}");
                break;
            }
        }

        _logger.LogInformation($"✅ Toplam {skuList.Count} inactive ürün kodu başarıyla alındı");

        // İlk 10 ürünü logla
        if (skuList.Any())
        {
            _logger.LogInformation($"📋 İlk 10 SKU: {string.Join(", ", skuList.Take(10))}");
        }

        return skuList;
    }

    //inactive ürünler için sadece
    public async Task<List<Dictionary<string, object>>?> GetInactiveItemsAsync(int maxItems = 5000)
    {
        var token = await GetValidToken();
        if (token == null) return null;

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        int top = 60;
        int skip = 0;
        var allItems = new List<Dictionary<string, object>>();
        int retryCount = 0;
        const int maxRetries = 3;

        while (true)
        {
            try
            {
                // ✅ INACTIVE: EndDate dolu VE IsWebshopItem = 0
                var filter = Uri.EscapeDataString("EndDate ne null and IsWebshopItem eq 0");
                var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/Items?$filter={filter}&$top={top}&$skip={skip}";

                _logger.LogInformation($"📡 API çağrısı: Sayfa {skip / top + 1}");
                _logger.LogInformation($"🔍 Filtre: EndDate dolu VE IsWebshopItem = 0");

                var response = await client.GetAsync(url);

                // Detaylı hata yönetimi
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"❌ API Hatası: {response.StatusCode} - {response.ReasonPhrase}");
                    _logger.LogError($"❌ Hata Detayı: {errorContent}");

                    // Rate limiting durumunda bekle ve tekrar dene
                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && retryCount < maxRetries)
                    {
                        retryCount++;
                        _logger.LogWarning($"⏳ Rate limit aşıldı, {retryCount}. deneme için 30 saniye bekleniyor...");
                        await Task.Delay(30000);
                        continue;
                    }

                    // Token süresi dolmuşsa
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        _logger.LogWarning("🔑 Token süresi dolmuş olabilir, yeniden deneniyor...");
                        token = await GetValidToken();
                        if (token == null) break;

                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
                        continue;
                    }

                    break;
                }

                retryCount = 0;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("d", out var dataElement))
                {
                    _logger.LogWarning("⚠️ Beklenmeyen JSON yapısı: 'd' property bulunamadı");
                    break;
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
                    _logger.LogError("Beklenmeyen JSON yapısı, mevcut sonuçlar döndürülüyor");
                    return allItems;
                }

                int countInPage = 0;

                foreach (var item in resultsElement.EnumerateArray())
                {
                    if (allItems.Count >= maxItems)
                    {
                        _logger.LogInformation($"⚠️ Maksimum item limiti ({maxItems}) aşıldı, işlem durduruluyor");
                        return allItems;
                    }

                    var dict = new Dictionary<string, object>();
                    foreach (var prop in item.EnumerateObject())
                    {
                        dict[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.Number => prop.Value.GetDouble(),
                            JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.Null => string.Empty,
                            _ => prop.Value.ToString() ?? string.Empty
                        };
                    }

                    // Ürün detaylarını logla
                    var code = dict.ContainsKey("Code") ? dict["Code"].ToString() : "N/A";
                    var description = dict.ContainsKey("Description") ? dict["Description"].ToString() : "N/A";
                    var endDate = dict.ContainsKey("EndDate") ? dict["EndDate"].ToString() : "N/A";
                    var isWebshop = dict.ContainsKey("IsWebshopItem") ? dict["IsWebshopItem"].ToString() : "N/A";

                    _logger.LogInformation($"   📦 {code} - {description}");
                    _logger.LogInformation($"      EndDate: {ParseExactDateToString(endDate)}, IsWebshopItem: {isWebshop}");

                    allItems.Add(dict);
                    countInPage++;
                }

                _logger.LogInformation($"📦 Sayfa {skip / top + 1}: {countInPage} ürün alındı. Toplam: {allItems.Count}");

                if (countInPage < top) break;
                skip += top;

                await Task.Delay(200);
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError($"⏰ Timeout hatası: {ex.Message}");
                break;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError($"🌐 Network hatası: {ex.Message}");

                if (retryCount < maxRetries)
                {
                    retryCount++;
                    _logger.LogWarning($"🔄 {retryCount}. deneme için 5 saniye bekleniyor...");
                    await Task.Delay(5000);
                    continue;
                }
                break;
            }
            catch (JsonException ex)
            {
                _logger.LogError($"📄 JSON parse hatası: {ex.Message}");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Beklenmeyen hata: {ex.Message}");
                break;
            }
        }

        _logger.LogInformation($"✅ Toplam {allItems.Count} inactive ürün başarıyla alındı");
        _logger.LogInformation($"📊 Filtre Kriterleri: EndDate dolu + IsWebshopItem = 0");

        return allItems;
    }

    // Yardımcı metot: Exact tarihini okunabilir formata çevir
    private string ParseExactDateToString(string exactDateString)
    {
        if (string.IsNullOrEmpty(exactDateString) || exactDateString == "") return "Yok";

        var match = System.Text.RegularExpressions.Regex.Match(exactDateString, @"/Date\((\d+)\)/");
        if (match.Success)
        {
            long timestamp = long.Parse(match.Groups[1].Value);
            var date = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).DateTime;
            return date.ToString("yyyy-MM-dd");
        }
        return exactDateString;
    }


    // get by code
    public async Task<Dictionary<string, object>?> GetItemByCodeAsync(string itemCode)
    {
        var token = await GetValidToken();
        if (token == null)
        {
            _logger.LogError("❌ Token alınamadı");
            return null;
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
                        token = await GetValidToken();
                        if (token == null) return null;

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

                    return null;
                }

                // JSON parse
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("d", out var dataElement))
                {
                    _logger.LogWarning("⚠️ Beklenmeyen JSON yapısı: 'd' property bulunamadı");
                    return null;
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
                    return null;
                }

                // İlk sonucu al (Code unique olmalı)
                if (resultsElement.GetArrayLength() == 0)
                {
                    _logger.LogWarning($"⚠️ Ürün bulunamadı: {itemCode}");
                    return null;
                }

                var item = resultsElement[0];
                var dict = new Dictionary<string, object>();

                foreach (var prop in item.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.Number => prop.Value.GetDouble(),
                        JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => string.Empty,
                        _ => prop.Value.ToString() ?? string.Empty
                    };
                }

                _logger.LogInformation($"✅ Ürün bulundu: {itemCode}");

                // Ürün bilgilerini logla
                if (dict.ContainsKey("Description"))
                {
                    _logger.LogInformation($"📦 Ürün Adı: {dict["Description"]}");
                }
                if (dict.ContainsKey("ID"))
                {
                    _logger.LogInformation($"🆔 Ürün ID: {dict["ID"]}");
                    var itemId = dict["ID"].ToString();
                    bool hasBundle = await GetItemExtraFieldAsync(itemId);
                    if (hasBundle)
                    {
                        _logger.LogInformation($"✅ isBundle mevcut ve dolu.");
                    }
                    else
                    {
                        _logger.LogInformation($"ℹ️ isBundle mevcut değil veya boş/false.");
                    }
                }

                return dict;
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
                return null;
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
                return null;
            }
            catch (JsonException ex)
            {
                _logger.LogError($"📄 JSON parse hatası: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Beklenmeyen hata: {ex.Message}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");
                return null;
            }
        }

        _logger.LogError($"❌ Maksimum deneme sayısına ulaşıldı");
        return null;
    }

    public async Task<bool> GetItemExtraFieldAsync(string itemId)
    {
        var token = await GetValidToken();
        if (token == null)
        {
            _logger.LogError("❌ Token alınamadı");
            return false;
        }

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(2);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/read/logistics/ItemExtraField?itemId=guid'{itemId}'";

            _logger.LogInformation($"🔍 Ekstra alanlar aranıyor: ItemID = {itemId}");
            _logger.LogInformation($"📡 API URL: {url}");

            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"❌ API Hatası: {response.StatusCode} - {response.ReasonPhrase}");
                return false;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("d", out var dataElement))
            {
                _logger.LogWarning("⚠️ Beklenmeyen JSON yapısı: 'd' property bulunamadı");
                return false;
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
                return false;
            }

            // ✅ Boş array kontrolü
            if (resultsElement.GetArrayLength() == 0)
            {
                _logger.LogInformation($"ℹ️ Ekstra alan bulunamadı: ItemID = {itemId}");
                return false;
            }

            _logger.LogInformation($"✅ Ekstra alanlar bulundu: ItemID = {itemId}");

            // ✅ Tüm alanları oku ve "Description" = "isBundle" olanını bul
            for (int i = 0; i < resultsElement.GetArrayLength(); i++)
            {
                var item = resultsElement[i];
                var description = string.Empty;
                var value = string.Empty;

                foreach (var prop in item.EnumerateObject())
                {
                    if (prop.Name == "Description")
                    {
                        description = prop.Value.GetString() ?? string.Empty;
                    }
                    else if (prop.Name == "Value")
                    {
                        value = prop.Value.GetString() ?? string.Empty;
                    }
                }

                // ✅ isBundle bulundu ve değeri dolu mu kontrol et
                if (description.Equals("isBundle", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        _logger.LogInformation($"✅ isBundle bulundu ve dolu: Value = {value}");
                        return true;
                    }
                    else
                    {
                        _logger.LogInformation($"ℹ️ isBundle bulundu ancak boş");
                        return false;
                    }
                }
            }

            _logger.LogWarning($"⚠️ Description = 'isBundle' olan property bulunamadı");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Beklenemeyen hata: {ex.Message}");
            return false;
        }
    }



    //webshop olan tüm ürünler
    public async Task<List<Dictionary<string, object>>?> GetItemsAsync(int maxItems = 5000)
    {
        var token = await GetValidToken();
        if (token == null) return null;

        using var client = new HttpClient();

        // Timeout ekle
        client.Timeout = TimeSpan.FromMinutes(10);

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        int top = 60; // Exact Online limitine uygun
        int skip = 0;
        var allItems = new List<Dictionary<string, object>>();
        int retryCount = 0;
        const int maxRetries = 3;

        while (true)
        {
            try
            {
                var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/Items?$filter=IsWebshopItem eq 1&$top={top}&$skip={skip}";

                Console.WriteLine($"📡 API çağrısı: Sayfa {skip / top + 1}");

                var resp = await client.GetAsync(url);

                // Detaylı hata yönetimi
                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"❌ API Hatası: {resp.StatusCode} - {resp.ReasonPhrase}");

                    // Rate limiting durumunda bekle ve tekrar dene
                    if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests && retryCount < maxRetries)
                    {
                        retryCount++;
                        Console.WriteLine($"⏳ Rate limit aşıldı, {retryCount}. deneme için 30 saniye bekleniyor...");
                        await Task.Delay(30000); // 30 saniye bekle
                        continue;
                    }

                    // Token süresi dolmuşsa
                    if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        Console.WriteLine("🔑 Token süresi dolmuş olabilir, yeniden deneniyor...");
                        token = await GetValidToken();
                        if (token == null) break;

                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
                        continue;
                    }

                    break; // Diğer hatalarda çık
                }

                retryCount = 0; // Başarılı istek sonrası retry sayacını sıfırla

                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                // JSON yapısı kontrol
                if (!doc.RootElement.TryGetProperty("d", out var dataElement))
                {
                    Console.WriteLine("⚠️ Beklenmeyen JSON yapısı: 'd' property bulunamadı");
                    break;
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
                    _logger.LogError("Beklenmeyen JSON yapısı, mevcut sonuçlar döndürülüyor");
                    return allItems;
                }

                int countInPage = 0;

                foreach (var item in resultsElement.EnumerateArray())
                {
                    // Maksimum item sayısını kontrol et
                    if (allItems.Count >= maxItems)
                    {
                        Console.WriteLine($"⚠️ Maksimum item limiti ({maxItems}) aşıldı, işlem durduruluyor");
                        return allItems;
                    }

                    var dict = new Dictionary<string, object>();
                    foreach (var prop in item.EnumerateObject())
                    {
                        dict[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.Number => prop.Value.GetDouble(),
                            JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.Null => string.Empty,
                            _ => prop.Value.ToString() ?? string.Empty
                        };
                    }
                    allItems.Add(dict);
                    countInPage++;
                }

                Console.WriteLine($"📦 Sayfa {skip / top + 1}: {countInPage} ürün alındı. Toplam: {allItems.Count}");

                if (countInPage < top) break; // Son sayfa
                skip += top;

                // API rate limiting için kısa bekleme
                await Task.Delay(200); // 200ms bekleme
            }
            catch (TaskCanceledException ex)
            {
                Console.WriteLine($"⏰ Timeout hatası: {ex.Message}");
                break;
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"🌐 Network hatası: {ex.Message}");

                if (retryCount < maxRetries)
                {
                    retryCount++;
                    Console.WriteLine($"🔄 {retryCount}. deneme için 5 saniye bekleniyor...");
                    await Task.Delay(5000);
                    continue;
                }
                break;
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"📄 JSON parse hatası: {ex.Message}");
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Beklenmeyen hata: {ex.Message}");
                break;
            }
        }

        Console.WriteLine($"✅ Toplam {allItems.Count} ürün başarıyla alındı");
        return allItems;
    }

    /// <summary>
    /// Exact'tan webshop ürünlerini sayfalı olarak çeker (skip/top destekli).
    /// </summary>
    public async Task<List<Dictionary<string, object>>?> GetWebshopItemsPageAsync(int skip, int top)
    {
        var token = await GetValidToken();
        if (token == null) return null;

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/Items?$filter=IsWebshopItem eq 1&$top={top}&$skip={skip}&$select=ID,Code,Description";

        int retryCount = 0;
        const int maxRetries = 3;

        while (retryCount <= maxRetries)
        {
            try
            {
                var resp = await client.GetAsync(url);

                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    retryCount++;
                    _logger.LogWarning("⏳ Rate limit, {Retry}. deneme için 30sn bekleniyor...", retryCount);
                    await Task.Delay(30000);
                    continue;
                }

                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    token = await GetValidToken();
                    if (token == null) return null;
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogError("❌ Exact API hatası: {Status}", resp.StatusCode);
                    return null;
                }

                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("d", out var dataElement)) return null;

                JsonElement resultsElement;
                if (dataElement.ValueKind == JsonValueKind.Object && dataElement.TryGetProperty("results", out var res))
                    resultsElement = res;
                else if (dataElement.ValueKind == JsonValueKind.Array)
                    resultsElement = dataElement;
                else
                    return null;

                var items = new List<Dictionary<string, object>>();
                foreach (var item in resultsElement.EnumerateArray())
                {
                    var dict = new Dictionary<string, object>();
                    foreach (var prop in item.EnumerateObject())
                    {
                        dict[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.Number => prop.Value.GetDouble(),
                            JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.Null => string.Empty,
                            _ => prop.Value.ToString() ?? string.Empty
                        };
                    }
                    items.Add(dict);
                }

                return items;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ GetWebshopItemsPageAsync hatası");
                return null;
            }
        }

        return null;
    }

    // hem webshop hem 24 saatte güncellenen ürünler

   public async Task<ExactProductResponse> GetItemsWebShopAndModified(int maxItems = 5000)
{
    var response = new ExactProductResponse
    {
        Success = false,
        ProcessedCount = 0,
        Results = new List<ExactProduct>()
    };

    var token = await GetValidToken();
    if (token == null)
    {
        Console.WriteLine("❌ Token alınamadı");
        return response;
    }

    using var client = new HttpClient();
    client.Timeout = TimeSpan.FromMinutes(10);
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", token.access_token);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    var yesterday = DateTime.UtcNow.AddDays(-1);
    var dateFilter = yesterday.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    int top = 60;
    int skip = 0;
    int retryCount = 0;
    const int maxRetries = 1000;
    bool hasMorePages = true;
    int pageNumber = 1;

    Console.WriteLine($"🕐 Son 24 saat filtresi: Modified > {dateFilter}");
    Console.WriteLine($"📊 Maksimum item limiti: {maxItems}");

    while (hasMorePages)
    {
        try
        {
            var filterQuery = $"IsWebshopItem eq 1 and Modified gt datetime'{dateFilter}'";
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/Items?$filter={Uri.EscapeDataString(filterQuery)}&$top={top}&$skip={skip}";

            Console.WriteLine($"\n📡 API çağrısı: Sayfa {pageNumber}, Skip: {skip}, Top: {top}");

            var resp = await client.GetAsync(url);

            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"❌ API Hatası: {resp.StatusCode}");

                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests && retryCount < maxRetries)
                {
                    retryCount++;
                    Console.WriteLine($"⏳ Rate limit, {retryCount}. deneme için 30 saniye bekleniyor...");
                    await Task.Delay(30000);
                    continue;
                }

                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    Console.WriteLine("🔑 Token yenileniyor...");
                    token = await GetValidToken();
                    if (token == null) break;
                    client.DefaultRequestHeaders.Authorization = 
                        new AuthenticationHeaderValue("Bearer", token.access_token);
                    continue;
                }

                var errorContent = await resp.Content.ReadAsStringAsync();
                Console.WriteLine($"📄 Hata detayı: {errorContent}");
                return response;
            }

            retryCount = 0;

            var json = await resp.Content.ReadAsStringAsync();

            // ✅ İlk sayfayı debug et
            if (pageNumber == 1)
            {
                var previewLength = Math.Min(800, json.Length);
                Console.WriteLine($"\n🔍 API Yanıtı (ilk {previewLength} karakter):\n{json.Substring(0, previewLength)}...\n");
            }

            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("d", out var dataElement))
            {
                Console.WriteLine("⚠️ JSON yapısı hatalı - 'd' property bulunamadı");
                return response;
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
                Console.WriteLine("⚠️ JSON yapısı beklenen formatta değil");
                return response;
            }

            // ✅ API'dan gelen toplam ürün sayısı
            int apiReturnCount = resultsElement.GetArrayLength();
            Console.WriteLine($"📊 Sayfa {pageNumber}: API'dan gelen ürün sayısı: {apiReturnCount}");

            int countInPage = 0;
            int nullCount = 0;
            int deserializeErrorCount = 0;

            foreach (var item in resultsElement.EnumerateArray())
            {
                // ✅ maxItems kontrolü ÖNCESINDE
                if (response.Results.Count >= maxItems)
                {
                    Console.WriteLine($"⚠️ Maksimum limit ({maxItems}) aşıldı!");
                    hasMorePages = false;
                    break;
                }

                try
                {
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                    };

                    var jsonString = item.GetRawText();
                    var exactProduct = JsonSerializer.Deserialize<ExactProduct>(jsonString, options);

                    if (exactProduct != null)
                    {
                        // ✅ Ürünün gerekli alanlarını kontrol et
                        if (string.IsNullOrEmpty(exactProduct.Code))
                        {
                            Console.WriteLine($"⚠️ Ürün Code alanı boş (ID: {exactProduct.ID})");
                        }

                        response.Results.Add(exactProduct);
                        countInPage++;
                    }
                    else
                    {
                        nullCount++;
                        Console.WriteLine($"⚠️ Ürün null döndü (deserialize başarısız)");
                    }
                }
                catch (JsonException ex)
                {
                    deserializeErrorCount++;
                    Console.WriteLine($"⚠️ Deserialize hatası: {ex.Message}");
                }
                catch (Exception ex)
                {
                    deserializeErrorCount++;
                    Console.WriteLine($"❌ Beklenmeyen hata (item işleme): {ex.Message}");
                }
            }

            // ✅ Detaylı sayfa özeti
            Console.WriteLine($"───────────────────────────────────────");
            Console.WriteLine($"✅ Sayfa {pageNumber} - Özet:");
            Console.WriteLine($"   API'dan gelen: {apiReturnCount}");
            Console.WriteLine($"   Başarıyla işlenen: {countInPage}");
            Console.WriteLine($"   Null ürün: {nullCount}");
            Console.WriteLine($"   Deserialize hatası: {deserializeErrorCount}");
            Console.WriteLine($"   Toplam işlenen (tüm sayfalar): {response.Results.Count}/{maxItems}");
            Console.WriteLine($"───────────────────────────────────────\n");

            // ✅ Sayfanın durumu kontrol et
            if (apiReturnCount < top)
            {
                Console.WriteLine($"✅ Son sayfaya ulaşıldı (API'dan {apiReturnCount} ürün geldi, expected: {top})");
                hasMorePages = false;
            }
            else if (countInPage == 0 && apiReturnCount > 0)
            {
                Console.WriteLine($"⚠️ API ürün döndürdü ({apiReturnCount}) ama hiçbiri işlenmedi!");
                Console.WriteLine($"   Null sayısı: {nullCount}, Deserialize hatası: {deserializeErrorCount}");
                hasMorePages = false;
                break;
            }
            else
            {
                skip += top;
                pageNumber++;
                Console.WriteLine($"➡️ Sonraki sayfaya geçiliyor (skip: {skip})");
            }

            await Task.Delay(200);
        }
        catch (TaskCanceledException ex)
        {
            Console.WriteLine($"⏰ Timeout: {ex.Message}");
            break;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Hata: {ex.GetType().Name} - {ex.Message}");
            break;
        }
    }

    response.Success = true;
    response.ProcessedCount = response.Results.Count;

    Console.WriteLine($"\n🎉 İşlem Tamamlandı!");
    Console.WriteLine($"✅ Toplam işlenen ürün: {response.ProcessedCount}");
    Console.WriteLine($"📊 Maksimum limit: {maxItems}");

    return response;
}


    // saddece stok 0 dan büyük olan ürünler
    // public async Task<List<Dictionary<string, object>>?> GetAllStockedItemsAsync()
    // {
    //     var token = await GetValidToken();
    //     if (token == null) return null;

    //     using var client = new HttpClient();
    //     client.Timeout = TimeSpan.FromMinutes(15);
    //     client.DefaultRequestHeaders.Authorization =
    //         new AuthenticationHeaderValue("Bearer", token.access_token);
    //     client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    //     int top = 60;
    //     int skip = 0;
    //     var allStockedItems = new List<Dictionary<string, object>>();
    //     int retryCount = 0;
    //     const int maxRetries = 3;

    //     Console.WriteLine("📦 Tüm webshop ürünleri alınıyor ve stok kontrolü yapılıyor...");

    //     while (true)
    //     {
    //         try
    //         {
    //             var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/Items?$filter=IsWebshopItem eq 1&$top={top}&$skip={skip}";
    //             Console.WriteLine($"📡 API çağrısı: Sayfa {skip / top + 1}");

    //             var resp = await client.GetAsync(url);

    //             if (!resp.IsSuccessStatusCode)
    //             {
    //                 Console.WriteLine($"❌ API Hatası: {resp.StatusCode} - {resp.ReasonPhrase}");

    //                 if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests && retryCount < maxRetries)
    //                 {
    //                     retryCount++;
    //                     Console.WriteLine($"⏳ Rate limit aşıldı, {retryCount}. deneme için 30 saniye bekleniyor...");
    //                     await Task.Delay(30000);
    //                     continue;
    //                 }

    //                 if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
    //                 {
    //                     Console.WriteLine("🔑 Token süresi dolmuş olabilir, yeniden deneniyor...");
    //                     token = await GetValidToken();
    //                     if (token == null) break;
    //                     client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
    //                     continue;
    //                 }

    //                 break;
    //             }

    //             retryCount = 0;
    //             var json = await resp.Content.ReadAsStringAsync();
    //             using var doc = JsonDocument.Parse(json);

    //             if (!doc.RootElement.TryGetProperty("d", out var dataElement))
    //             {
    //                 Console.WriteLine("⚠️ Beklenmeyen JSON yapısı: 'd' property bulunamadı");
    //                 break;
    //             }

    //             JsonElement resultsElement;
    //             if (dataElement.ValueKind == JsonValueKind.Object && dataElement.TryGetProperty("results", out var res))
    //             {
    //                 resultsElement = res;
    //             }
    //             else if (dataElement.ValueKind == JsonValueKind.Array)
    //             {
    //                 resultsElement = dataElement;
    //             }
    //             else
    //             {
    //                 _logger.LogError("Beklenmeyen JSON yapısı, mevcut sonuçlar döndürülüyor");
    //                 return allStockedItems;
    //             }

    //             int countInPage = 0;
    //             int stockedInPage = 0;

    //             foreach (var item in resultsElement.EnumerateArray())
    //             {
    //                 var dict = new Dictionary<string, object>();
    //                 foreach (var prop in item.EnumerateObject())
    //                 {
    //                     dict[prop.Name] = prop.Value.ValueKind switch
    //                     {
    //                         JsonValueKind.Number => prop.Value.GetDouble(),
    //                         JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
    //                         JsonValueKind.True => true,
    //                         JsonValueKind.False => false,
    //                         JsonValueKind.Null => string.Empty,
    //                         _ => prop.Value.ToString() ?? string.Empty
    //                     };
    //                 }

    //                 // Stok kontrolü - verdiğiniz test kodundaki mantık
    //                 bool hasStock = false;
    //                 double stockValue = 0;

    //                 foreach (var kvp in dict)
    //                 {
    //                     // Stok ile ilgili field'ları tespit et
    //                     if (kvp.Key.ToLower().Contains("stock") ||
    //                         kvp.Key.ToLower().Contains("quantity") ||
    //                         kvp.Key.ToLower().Contains("available"))
    //                     {
    //                         // && value > 0
    //                         if (double.TryParse(kvp.Value.ToString(), out double value))
    //                         {
    //                             hasStock = true;
    //                             stockValue = value;
    //                             Console.WriteLine($"   🔍 STOK FIELD BULUNDU: {kvp.Key} = {value} (SKU: {dict.GetValueOrDefault("Code", "N/A")})");
    //                             break; // İlk pozitif stok bulunca dur
    //                         }
    //                     }
    //                 }

    //                 // ✅ Sadece stoku olan ürünleri kontrol et
    //                 // if (hasStock)
    //                 // {

    //                 // }
    //                  // ✅ isBundle kontrolü ekle
    //                     string itemId = dict.GetValueOrDefault("ID", "").ToString();

    //                     // if (string.IsNullOrEmpty(itemId))
    //                     // {
    //                     //     Console.WriteLine($"   ⚠️ Ürün ID'si bulunamadı: {dict.GetValueOrDefault("Code", "N/A")}");
    //                     //     continue;
    //                     // }

    //                     // ✅ isBundle kontrolü yap
    //                     bool isBundle = await GetItemExtraFieldAsync(itemId);

    //                     if (isBundle)
    //                     {
    //                         Console.WriteLine($"   🚫 Bundle ürünü atlandı: {dict.GetValueOrDefault("Code", "N/A")} (ID: {itemId})");
    //                         continue; // Bundle ürünü listeye ekleme
    //                     }

    //                     // ✅ Sadece stoklu ve bundle olmayan ürünleri listeye ekle
    //                     allStockedItems.Add(dict);
    //                     stockedInPage++;
    //                     Console.WriteLine($"   ✅ Stoklu ürün eklendi: {dict.GetValueOrDefault("Code", "N/A")} (Stok: {stockValue})");

    //                 countInPage++;
    //             }

    //             Console.WriteLine($"📦 Sayfa {skip / top + 1}: {countInPage} ürün alındı, {stockedInPage} stoklu ürün bulundu. Toplam stoklu: {allStockedItems.Count}");

    //             if (countInPage < top) break; // Son sayfa
    //             skip += top;

    //             // API rate limiting
    //             await Task.Delay(200);
    //         }
    //         catch (TaskCanceledException ex)
    //         {
    //             Console.WriteLine($"⏰ Timeout hatası: {ex.Message}");
    //             break;
    //         }
    //         catch (HttpRequestException ex)
    //         {
    //             Console.WriteLine($"🌐 Network hatası: {ex.Message}");
    //             if (retryCount < maxRetries)
    //             {
    //                 retryCount++;
    //                 Console.WriteLine($"🔄 {retryCount}. deneme için 5 saniye bekleniyor...");
    //                 await Task.Delay(5000);
    //                 continue;
    //             }
    //             break;
    //         }
    //         catch (Exception ex)
    //         {
    //             Console.WriteLine($"❌ Beklenmeyen hata: {ex.Message}");
    //             break;
    //         }
    //     }

    //     Console.WriteLine($"✅ Toplam {allStockedItems.Count} stoklu ve bundle olmayan ürün başarıyla alındı");

    //     // Stok bilgilerinin özetini göster
    //     if (allStockedItems.Any())
    //     {
    //         Console.WriteLine("📊 Stok özeti:");
    //         var stockSummary = allStockedItems
    //             .Where(item => item.ContainsKey("Code"))
    //             .Take(5)
    //             .Select(item => new
    //             {
    //                 Code = item["Code"],
    //                 Stock = item.FirstOrDefault(kvp => kvp.Key.ToLower().Contains("stock")).Value ?? 0
    //             });

    //         foreach (var summary in stockSummary)
    //         {
    //             Console.WriteLine($"   - {summary.Code}: {summary.Stock}");
    //         }
    //         if (allStockedItems.Count > 5)
    //             Console.WriteLine($"   ... ve {allStockedItems.Count - 5} ürün daha");
    //     }

    //     return allStockedItems;
    // }
    public async Task<List<Dictionary<string, object>>?> GetAllStockedItemsAsync()
    {
        var token = await GetValidToken();
        if (token == null) return null;

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(15);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        int top = 60;
        int skip = 0;
        var allStockedItems = new List<Dictionary<string, object>>();
        int retryCount = 0;
        const int maxRetries = 1000;

        // 📊 İstatistik sayaçları
        int totalItemsFromApi = 0;
        int bundleItemsCount = 0;
        int nonBundleItemsCount = 0;

        Console.WriteLine("📦 Tüm webshop ürünleri alınıyor ve stok kontrolü yapılıyor...");

        while (true)
        {
            try
            {
                var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/Items?$filter=IsWebshopItem eq 1&$top={top}&$skip={skip}";
                Console.WriteLine($"📡 API çağrısı: Sayfa {skip / top + 1} (Skip: {skip}, Top: {top})");

                var resp = await client.GetAsync(url);

                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"❌ API Hatası: {resp.StatusCode} - {resp.ReasonPhrase}");

                    if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests && retryCount < maxRetries)
                    {
                        retryCount++;
                        Console.WriteLine($"⏳ Rate limit aşıldı, {retryCount}. deneme için 30 saniye bekleniyor...");
                        await Task.Delay(30000);
                        continue;
                    }

                    if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        Console.WriteLine("🔑 Token süresi dolmuş olabilir, yeniden deneniyor...");
                        token = await GetValidToken();
                        if (token == null) break;
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
                        continue;
                    }

                    break;
                }

                retryCount = 0;
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("d", out var dataElement))
                {
                    Console.WriteLine("⚠️ Beklenmeyen JSON yapısı: 'd' property bulunamadı");
                    break;
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
                    _logger.LogError("Beklenmeyen JSON yapısı, mevcut sonuçlar döndürülüyor");
                    return allStockedItems;
                }

                int countInPage = 0;
                int stockedInPage = 0;

                foreach (var item in resultsElement.EnumerateArray())
                {
                    var dict = new Dictionary<string, object>();
                    foreach (var prop in item.EnumerateObject())
                    {
                        dict[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.Number => prop.Value.GetDouble(),
                            JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.Null => string.Empty,
                            _ => prop.Value.ToString() ?? string.Empty
                        };
                    }

                    // 📊 API'den gelen ürün sayısını artır
                    totalItemsFromApi++;

                    // Stok kontrolü - verdiğiniz test kodundaki mantık
                    bool hasStock = false;
                    double stockValue = 0;

                    foreach (var kvp in dict)
                    {
                        // Stok ile ilgili field'ları tespit et
                        if (kvp.Key.ToLower().Contains("stock") ||
                            kvp.Key.ToLower().Contains("quantity") ||
                            kvp.Key.ToLower().Contains("available"))
                        {
                            // && value > 0
                            if (double.TryParse(kvp.Value.ToString(), out double value))
                            {
                                hasStock = true;
                                stockValue = value;
                                Console.WriteLine($"   🔍 STOK FIELD BULUNDU: {kvp.Key} = {value} (SKU: {dict.GetValueOrDefault("Code", "N/A")})");
                                break; // İlk pozitif stok bulunca dur
                            }
                        }
                    }

                    // ✅ Sadece stoku olan ürünleri kontrol et
                    if (hasStock)
                    {
                        string itemId = dict.GetValueOrDefault("ID", "").ToString();
                        bool isBundle = await GetItemExtraFieldAsync(itemId);
                        if (isBundle)
                        {
                            bundleItemsCount++;
                            Console.WriteLine($"   🚫 Bundle ürünü atlandı: {dict.GetValueOrDefault("Code", "N/A")} (ID: {itemId})");
                            continue; // Bundle ürünü listeye ekleme

                        }
                        else
                        {
                            nonBundleItemsCount++;
                            allStockedItems.Add(dict);

                        }
                    }
                    stockedInPage++;
                    Console.WriteLine($"   ✅ Stoklu ürün eklendi: {dict.GetValueOrDefault("Code", "N/A")} (Stok: {stockValue})");
                    countInPage++;
                }

                Console.WriteLine($"📦 Sayfa {skip / top + 1}: {countInPage} ürün alındı, {stockedInPage} stoklu ürün bulundu. Toplam stoklu: {allStockedItems.Count}");

                // 🔑 ÖNEMLI FIX: Eğer hiç ürün gelmemişse, tüm ürünler alınmış demektir
                if (countInPage == 0)
                {
                    Console.WriteLine("✅ Tüm sayfalar alındı (boş sayfa bulundu)");
                    break;
                }

                skip += top;

                // API rate limiting
                await Task.Delay(200);
            }
            catch (TaskCanceledException ex)
            {
                Console.WriteLine($"⏰ Timeout hatası: {ex.Message}");
                break;
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"🌐 Network hatası: {ex.Message}");
                if (retryCount < maxRetries)
                {
                    retryCount++;
                    Console.WriteLine($"🔄 {retryCount}. deneme için 5 saniye bekleniyor...");
                    await Task.Delay(5000);
                    continue;
                }
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Beklenmeyen hata: {ex.Message}");
                break;
            }
        }

        Console.WriteLine($"✅ Toplam {allStockedItems.Count} stoklu ve bundle olmayan ürün başarıyla alındı");

        // 📊 DETAYLI İSTATİSTİK RAPORU
        Console.WriteLine("\n" + new string('=', 60));
        Console.WriteLine("📊 API İSTATİSTİK RAPORU");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"📦 API'den gelen toplam ürün sayısı: {totalItemsFromApi}");
        Console.WriteLine($"🚫 Bundle ürün sayısı: {bundleItemsCount}");
        Console.WriteLine($"✅ Bundle olmayan ürün sayısı: {nonBundleItemsCount}");
        Console.WriteLine($"💾 Kaydedilen stoklu ürün sayısı: {allStockedItems.Count}");
        Console.WriteLine(new string('=', 60) + "\n");

        // Stok bilgilerinin özetini göster
        if (allStockedItems.Any())
        {
            Console.WriteLine("📊 Stok özeti:");
            var stockSummary = allStockedItems
                .Where(item => item.ContainsKey("Code"))
                .Take(5)
                .Select(item => new
                {
                    Code = item["Code"],
                    Stock = item.FirstOrDefault(kvp => kvp.Key.ToLower().Contains("stock")).Value ?? 0
                });

            foreach (var summary in stockSummary)
            {
                Console.WriteLine($"   - {summary.Code}: {summary.Stock}");
            }
            if (allStockedItems.Count > 5)
                Console.WriteLine($"   ... ve {allStockedItems.Count - 5} ürün daha");
        }

        return allStockedItems;
    }

    public async Task<TokenResponse?> GetValidToken()
    {
        try
        {
            _logger.LogDebug("📞 TokenManager'dan token isteniyor...");

            var token = await _tokenManager.GetValidTokenAsync();

            if (token != null)
            {
                _logger.LogDebug("✅ TokenManager'dan token alındı");
            }
            else
            {
                _logger.LogWarning("⚠️ TokenManager'dan token alınamadı");
            }

            return token;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetValidToken hatası");
            return null;
        }
    }

    public async Task<string?> GetValidAccessToken()
    {
        var token = await GetValidToken();
        return token?.access_token;
    }

    //is webshop 
    public async Task<bool?> CheckSkuIsWebshopItemAsync(string sku)
    {
        if (string.IsNullOrEmpty(sku))
        {
            Console.WriteLine("❌ SKU değeri boş olamaz");
            return null;
        }

        var token = await GetValidToken();
        if (token == null)
        {
            Console.WriteLine("❌ Token alınamadı");
            return null;
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            // SKU (Code) ile ürünü ara
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/Items?$filter=Code eq '{sku}'&$select=Code,IsWebshopItem,Description";

            Console.WriteLine($"🔍 SKU kontrol ediliyor: {sku}");
            Console.WriteLine($"📡 Request URL: {url}");

            var resp = await client.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"❌ API Hatası: {resp.StatusCode} - {resp.ReasonPhrase}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            // JSON yapısını kontrol et
            JsonElement dataElement = doc.RootElement.GetProperty("d");
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
                throw new Exception("Beklenmeyen JSON yapısı.");
            }

            // Sonuçları kontrol et
            if (!resultsElement.EnumerateArray().Any())
            {
                Console.WriteLine($"⚠️ SKU '{sku}' bulunamadı");
                return null;
            }

            // İlk (ve tek olması gereken) sonucu al
            var item = resultsElement.EnumerateArray().First();

            // IsWebshopItem değerini çıkar
            if (item.TryGetProperty("IsWebshopItem", out var isWebshopItemProp))
            {
                bool isWebshopItem = false;

                // Farklı JSON değer türlerini kontrol et
                switch (isWebshopItemProp.ValueKind)
                {
                    case JsonValueKind.True:
                        isWebshopItem = true;
                        break;
                    case JsonValueKind.False:
                        isWebshopItem = false;
                        break;
                    case JsonValueKind.Number:
                        isWebshopItem = isWebshopItemProp.GetDouble() == 1;
                        break;
                    case JsonValueKind.String:
                        var stringValue = isWebshopItemProp.GetString();
                        isWebshopItem = stringValue == "1" || stringValue?.ToLower() == "true";
                        break;
                    default:
                        Console.WriteLine($"⚠️ IsWebshopItem değeri beklenmedik formatta: {isWebshopItemProp}");
                        return null;
                }

                // Ürün bilgilerini logla
                var description = item.TryGetProperty("Description", out var descProp)
                    ? descProp.GetString() ?? "N/A"
                    : "N/A";

                Console.WriteLine($"📦 Ürün bulundu:");
                Console.WriteLine($"   - SKU: {sku}");
                Console.WriteLine($"   - Açıklama: {description}");
                Console.WriteLine($"   - IsWebshopItem: {isWebshopItem} ({(isWebshopItem ? "✅ WEBSHOP ÜRÜNÜ" : "❌ WEBSHOP ÜRÜNÜ DEĞİL")})");

                return isWebshopItem;
            }
            else
            {
                Console.WriteLine($"⚠️ IsWebshopItem alanı bulunamadı");
                return null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ SKU webshop kontrolü sırasında hata: {ex.Message}");
            return null;
        }
    }

    public async Task<TokenResponse?> GetValidTokenPublicAsync()
    {
        return await GetValidToken(); // Private metodunu public yap
    }


    //webhook
    public async Task<bool> CreateWebhookSubscriptionAsync(string callbackUrl, string topic)
    {
        var token = await GetValidToken();
        if (token == null)
        {
            Console.WriteLine("❌ Token alınamadı, webhook oluşturulamıyor");
            return false;
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var subscription = new
        {
            CallbackURL = callbackUrl,
            Topic = topic, // Tam topic path
        };

        var json = JsonSerializer.Serialize(subscription);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        Console.WriteLine($"🔍 Debug - URL: {_baseUrl}/api/v1/{_divisionCode}/webhooks/WebhookSubscriptions");
        Console.WriteLine($"🔍 Debug - Payload: {json}");

        var response = await client.PostAsync($"{_baseUrl}/api/v1/{_divisionCode}/webhooks/WebhookSubscriptions", content);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ Webhook Error: {response.StatusCode}");
            Console.WriteLine($"📄 Full Response: {error}");
            Console.WriteLine($"🔗 Request URL: {response.RequestMessage?.RequestUri}");
            string logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            string logFilePath = Path.Combine(logDirectory, $"webhook_error_{DateTime.Now:yyyy-MM-dd}.log");

            if (!Directory.Exists(logDirectory))
                Directory.CreateDirectory(logDirectory);

            string errorMessage = $"""
            [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WEBHOOK HATA
            Status: {response.StatusCode}
            URL: {response.RequestMessage?.RequestUri}
            Body: {error}
            Headers: {string.Join(", ", response.Headers)}
            
            """;

            File.AppendAllText(logFilePath, errorMessage);
            return false;
        }

        Console.WriteLine($"✅ Webhook aboneliği başarıyla oluşturuldu. Topic: {topic}, Callback: {callbackUrl}");
        return true;
    }

    public async Task<string> ListWebhookSubscriptionsAsync()
    {
        var token = await GetValidToken();
        if (token == null)
        {
            return JsonSerializer.Serialize(new { success = false, message = "❌ Token alınamadı" });
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        var url = $"{_baseUrl}/api/v1/{_divisionCode}/webhooks/WebhookSubscriptions";
        var response = await client.GetAsync(url);

        var json = await response.Content.ReadAsStringAsync();
        return json;
    }


    public async Task<bool> DeleteWebhookSubscriptionAsync(Guid subscriptionId)
    {
        var token = await GetValidToken();
        if (token == null)
        {
            Console.WriteLine("❌ Token alınamadı, webhook silinemiyor");
            return false;
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);

        var url = $"{_baseUrl}/api/v1/{_divisionCode}/webhooks/WebhookSubscriptions(guid'{subscriptionId}')";
        var response = await client.DeleteAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ Webhook silinemedi: {response.StatusCode}");
            Console.WriteLine($"📄 Response: {error}");
            return false;
        }

        Console.WriteLine($"✅ Webhook {subscriptionId} başarıyla silindi.");
        return true;
    }

    public async Task<List<SalesItemPriceDto>> GetRecentlyChangedSalesItemPricesAsync(DateTime since)
    {
        var token = await GetValidToken();
        if (token == null)
        {
            Console.WriteLine("Token alınamadı, SalesItemPrices getirilemedi");
            return new List<SalesItemPriceDto>();
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var sinceStr = since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss");
        var filter = $"Modified gt datetime'{sinceStr}'";
        var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/SalesItemPrices?$filter={Uri.EscapeDataString(filter)}&$select=Item,ItemCode,Price,Modified";

        Console.WriteLine($"📡 SalesItemPrices sorgulanıyor: {url}");

        var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ SalesItemPrices alınamadı: {response.StatusCode} - {error}");
            return new List<SalesItemPriceDto>();
        }

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var results = new List<SalesItemPriceDto>();

        if (doc.RootElement.TryGetProperty("d", out var d) &&
            d.TryGetProperty("results", out var resultArray))
        {
            foreach (var item in resultArray.EnumerateArray())
            {
                var itemCode = item.TryGetProperty("ItemCode", out var codeEl) ? codeEl.GetString() : null;
                var price = item.TryGetProperty("Price", out var priceEl) ? priceEl.GetDecimal() : 0m;
                var productId = item.TryGetProperty("Item", out var idEl) ? idEl.GetGuid() : Guid.Empty;


                if (!string.IsNullOrEmpty(itemCode))
                    results.Add(new SalesItemPriceDto { ItemCode = itemCode, Price = price, ID = productId });
            }
        }

        Console.WriteLine($"✅ {results.Count} değişmiş fiyat bulundu (son {(int)(DateTime.UtcNow - since).TotalMinutes} dakika)");
        return results;
    }


   public async Task<string> GetSalesItemPricesRawAsync(string itemCode)
    {
        var token = await GetValidToken();
        if (token == null) return "{\"error\": \"Token alınamadı\"}";

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        string url;
        if (string.IsNullOrWhiteSpace(itemCode))
        {
            url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/SalesItemPrices?$top=5&$select=Item,ItemCode,ItemDescription,Price,Currency,Unit,UnitDescription,Quantity,StartDate,EndDate,Modified,ModifierFullName,Created,Account,AccountName";
        }
        else
        {
            var filter = $"ItemCode eq '{itemCode}'";
            url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/SalesItemPrices?$filter={Uri.EscapeDataString(filter)}&$top=10&$select=Item,ItemCode,ItemDescription,Price,Currency,Unit,UnitDescription,Quantity,StartDate,EndDate,Modified,ModifierFullName,Created,Account,AccountName";
        }

        Console.WriteLine($"📡 [DEBUG] SalesItemPrices raw sorgu: {url}");
        var response = await client.GetAsync(url);
        return await response.Content.ReadAsStringAsync();
    }

   public async Task<List<SalesItemPriceDto>> GetSalesItemPricesByItemCodeAsync(string itemCode)
    {
        var token = await GetValidToken();
        if (token == null)
        {
            Console.WriteLine("Token alınamadı, SalesItemPrices getirilemedi");
            return new List<SalesItemPriceDto>();
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var filter = $"ItemCode eq '{itemCode}'";
        var select = "$select=Item,ItemCode,ItemDescription,Price,Currency,Unit,UnitDescription,Quantity,StartDate,EndDate,Modified,ModifierFullName,Created,Account,AccountName";
        var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/SalesItemPrices?$filter={Uri.EscapeDataString(filter)}&{select}&$orderby=Modified desc";

        Console.WriteLine($"📡 SalesItemPrices sorgulanıyor: {url}");

        var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ SalesItemPrices alınamadı: {response.StatusCode} - {error}");
            return new List<SalesItemPriceDto>();
        }

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var results = new List<SalesItemPriceDto>();

        if (doc.RootElement.TryGetProperty("d", out var d) &&
            d.TryGetProperty("results", out var resultArray))
        {
            foreach (var item in resultArray.EnumerateArray())
            {
                DateTime? ParseExactDate(JsonElement el)
                {
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        var s = el.GetString();
                        if (s != null && s.StartsWith("/Date("))
                        {
                            var ms = long.Parse(s.Replace("/Date(", "").Replace(")/", ""));
                            return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                        }
                    }
                    return null;
                }

                var dto = new SalesItemPriceDto
                {
                    ID = item.TryGetProperty("Item", out var idEl) ? idEl.GetGuid() : Guid.Empty,
                    ItemCode = item.TryGetProperty("ItemCode", out var codeEl) ? codeEl.GetString() : null,
                    ItemDescription = item.TryGetProperty("ItemDescription", out var itemDescEl) ? itemDescEl.GetString() : null,
                    Price = item.TryGetProperty("Price", out var priceEl) ? priceEl.GetDecimal() : 0m,
                    Currency = item.TryGetProperty("Currency", out var currEl) ? currEl.GetString() : null,
                    Unit = item.TryGetProperty("Unit", out var unitEl) ? unitEl.GetString()?.Trim() : null,
                    UnitDescription = item.TryGetProperty("UnitDescription", out var unitDescEl) ? unitDescEl.GetString() : null,
                    Quantity = item.TryGetProperty("Quantity", out var qtyEl) && qtyEl.ValueKind != JsonValueKind.Null ? qtyEl.GetDouble() : null,
                    StartDate = item.TryGetProperty("StartDate", out var startEl) ? ParseExactDate(startEl) : null,
                    EndDate = item.TryGetProperty("EndDate", out var endEl) ? ParseExactDate(endEl) : null,
                    Modified = item.TryGetProperty("Modified", out var modEl) ? ParseExactDate(modEl) : null,
                    ModifierFullName = item.TryGetProperty("ModifierFullName", out var modNameEl) ? modNameEl.GetString() : null,
                    Created = item.TryGetProperty("Created", out var createdEl) ? ParseExactDate(createdEl) : null,
                    Account = item.TryGetProperty("Account", out var accEl) && accEl.ValueKind != JsonValueKind.Null ? accEl.GetString() : null,
                    AccountName = item.TryGetProperty("AccountName", out var accNameEl) ? accNameEl.GetString() : null,
                };

                results.Add(dto);
            }
        }

        Console.WriteLine($"✅ {results.Count} fiyat kaydı bulundu (ItemCode: {itemCode})");
        return results;
    }

   public async Task<List<Account>> GetAllCustomersAsync()
    {
        var token = await GetValidToken();
        if (token == null)
        {
            Console.WriteLine("Token alınamadı");
            return new List<Account>();
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var allCustomers = new List<Account>();
            var errorEmails = new List<string>(); // ❌ Hata alan emailler
            int skip = 0;
            int top = 60;
            bool hasMore = true;

            // ⚠️ ÖNEMLI: Converter'lar global options'da TANIMLANMALI
            // Eğer FlexibleConverters çalışmıyorsa, aşağıdaki inline converter'lar çalışacak
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = null,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
            };

            while (hasMore)
            {
               var url = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Accounts?$filter=Status eq 'C' and EndDate eq null&$top={top}&$skip={skip}";
                

                Console.WriteLine($"📥 Sayfa alınıyor - Skip: {skip}, Toplam: {allCustomers.Count}");

                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"❌ API hatası: {response.StatusCode}");
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Hata detayı: {errorContent}");
                    break;
                }

                var content = await response.Content.ReadAsStringAsync();

                // JSON'u önce temizle (Source ve PeppolIdentifierType field'larını düzelt)
                content = PreProcessJson(content);

                using var doc = JsonDocument.Parse(content);

                // "d" property'sini al
                if (!doc.RootElement.TryGetProperty("d", out var dElement))
                {
                    Console.WriteLine("❌ 'd' property bulunamadı");
                    break;
                }

                JsonElement resultsArray;

                // "d" direkt array mi yoksa object içinde "results" mi?
                if (dElement.ValueKind == JsonValueKind.Array)
                {
                    resultsArray = dElement;
                }
                else if (dElement.TryGetProperty("results", out var resultsProperty))
                {
                    resultsArray = resultsProperty;
                }
                else
                {
                    Console.WriteLine("❌ Results bulunamadı");
                    break;
                }

                int count = 0;
                int errorCount = 0;

                // API'den gelen gerçek kayıt sayısını al
                int totalFromApi = resultsArray.EnumerateArray().Count();

                foreach (var customerElement in resultsArray.EnumerateArray())
                {
                    Account account = null; // ✅ Try bloğunun dışında tanımla
                     var customerJson = customerElement.GetRawText();
                    try
                    {
                       

                        // Her customer için de temizlik yap
                        customerJson = PreProcessJson(customerJson);

                        account = JsonSerializer.Deserialize<Account>(customerJson, jsonOptions);

                        if (account != null)
                        {
                            // Ek güvenlik: EndDate kontrol et (null değilse ve geçmiş tarihse atla)
                            if (account.EndDate != null && account.EndDate < DateTime.Now)
                            {
                                Console.WriteLine($"⏳ Müşteri {account.Code} inaktif (EndDate: {account.EndDate}), atlaniyor");
                                continue;
                            }

                            if (account.Classification1 != null)
                            {
                                Console.WriteLine($"📊 Müşteri Sınıflandırması: {account.Classification1}");
                                var searcClassification = $"{_baseUrl}/api/v1/{_divisionCode}/crm/AccountClassifications?$filter=ID eq guid'{account.Classification1}'";



                                var responseClassification = await client.GetAsync(searcClassification);
                                await Task.Delay(1000);
                                if (!responseClassification.IsSuccessStatusCode)
                                {
                                    Console.WriteLine($"❌ API hatası: {responseClassification.StatusCode}");
                                    var errorContent = await responseClassification.Content.ReadAsStringAsync();
                                    Console.WriteLine($"Hata detayı: {errorContent}");
                                    continue;
                                }
                                else
                                {
                                    var contentClassification = await responseClassification.Content.ReadAsStringAsync();
                                    var classificationDoc = JsonDocument.Parse(contentClassification);
                                    var codeClassification = classificationDoc.RootElement.GetProperty("d").GetProperty("results")[0].GetProperty("Code").GetString();
                                    if (codeClassification != null)
                                    {
                                        account.ClassificationDescription = codeClassification;
                                    }
                                }
                            }
                            allCustomers.Add(account);
                            count++;
                        }
                    }
                    catch (JsonException jsonEx)
                    {
                        errorCount++;
                        
                        // Deserialize hatası sırasında email'i JSON string'inden çıkar
                        try
                        {
                            var errorDoc = JsonDocument.Parse(customerJson);
                            if (errorDoc.RootElement.TryGetProperty("email", out var emailProp))
                            {
                                var email = emailProp.GetString();
                                if (!string.IsNullOrWhiteSpace(email))
                                    errorEmails.Add(email);
                                Console.WriteLine($"⚠️ JSON parse hatası - Email: {email}");
                            }
                        }
                        catch { }
                        
                        Console.WriteLine($"⚠️ JSON parse hatası ({errorCount}): {jsonEx.Message}");

                        if (errorCount == 1)
                        {
                            Console.WriteLine("💡 İlk hata görüldü, json pre-processing devrede");
                        }
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        
                        // Genel hata sırasında da email'i çıkar
                        try
                        {
                            var errorDoc = JsonDocument.Parse(customerJson);
                            if (errorDoc.RootElement.TryGetProperty("email", out var emailProp))
                            {
                                var email = emailProp.GetString();
                                if (!string.IsNullOrWhiteSpace(email))
                                    errorEmails.Add(email);
                                Console.WriteLine($"⚠️ Genel hata - Email: {email}");
                            }
                        }
                        catch { }
                        
                        Console.WriteLine($"⚠️ Genel hata: {ex.Message}");
                    }
                }

                if (errorCount > 0)
                {
                    Console.WriteLine($"⚠️ {count} başarılı, {errorCount} hatalı kayıt (API'den {totalFromApi} kayıt geldi)");
                }
                else
                {
                    Console.WriteLine($"✅ {count} müşteri başarıyla alındı");
                }

                // API'den gelen kayıt sayısını kontrol et (parse hatası olanları göz ardı et)
                if (totalFromApi < top)
                {
                    hasMore = false;
                    Console.WriteLine("🏁 Son sayfaya ulaşıldı");
                }
                else
                {
                    skip += top;
                }

                await Task.Delay(1500);
            }

            Console.WriteLine($"🎯 TOPLAM: {allCustomers.Count} müşteri başarıyla alındı");
            
            // ❌ Hata alan emailleri JSON dosyasına yaz
            if (errorEmails.Count > 0)
            {
                var errorJson = System.Text.Json.JsonSerializer.Serialize(new { 
                    errorCount = errorEmails.Count, 
                    emails = errorEmails.Distinct().ToList() 
                }, new JsonSerializerOptions { WriteIndented = true });
                
                File.WriteAllText("error_emails.json", errorJson);
                Console.WriteLine($"📋 {errorEmails.Count} hata alan email 'error_emails.json' dosyasına kaydedildi");
            }
            
            return allCustomers;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"💥 Kritik Hata: {ex.Message}");
            Console.WriteLine($"Stack Trace: {ex.StackTrace}");
            return new List<Account>();
        }
    }

    /// <summary>
    /// JSON'u parse etmeden önce temizler ve tip uyuşmazlıklarını düzeltir
    /// </summary>
    private string PreProcessJson(string json)
    {
        // Source field'ı: sayı ise string'e çevir
        json = System.Text.RegularExpressions.Regex.Replace(
            json,
            @"""Source""\s*:\s*(\d+)",
            @"""Source"":""$1"""
        );

        // PeppolIdentifierType field'ı: sayı ise string'e çevir
        json = System.Text.RegularExpressions.Regex.Replace(
            json,
            @"""PeppolIdentifierType""\s*:\s*(\d+)",
            @"""PeppolIdentifierType"":""$1"""
        );

        // PeppolIdentifier field'ı: sayı ise string'e çevir
        json = System.Text.RegularExpressions.Regex.Replace(
            json,
            @"""PeppolIdentifier""\s*:\s*(\d+)",
            @"""PeppolIdentifier"":""$1"""
        );

        // AddressSource field'ı: sayı ise string'e çevir
        json = System.Text.RegularExpressions.Regex.Replace(
            json,
            @"""AddressSource""\s*:\s*(\d+)",
            @"""AddressSource"":""$1"""
        );

        // EnableSalesPaymentLink: 0 veya 1'i boolean'a çevir
        json = json.Replace(@"""EnableSalesPaymentLink"":0", @"""EnableSalesPaymentLink"":false");
        json = json.Replace(@"""EnableSalesPaymentLink"":1", @"""EnableSalesPaymentLink"":true");

        // Classification: boş string'i null yap
        json = System.Text.RegularExpressions.Regex.Replace(
            json,
            @"""Classification""\s*:\s*""""",
            @"""Classification"":null"
        );

        // Classification1-8: boş string'leri null yap
        for (int i = 1; i <= 8; i++)
        {
            json = System.Text.RegularExpressions.Regex.Replace(
                json,
                $@"""Classification{i}""\s*:\s*""""",
                $@"""Classification{i}"":null"
            );
        }

        return json;
    }
    //warehouse
    public async Task<string> GetAllWarehouseAsync()
    {
        var token = await GetValidToken();
        if (token == null)
        {
            Console.WriteLine("❌ Token alınamadı");
            return "[]"; // Boş JSON array döndür
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/inventory/Warehouses";
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"❌ ExactOnline müşteri isteği başarısız: {response.StatusCode}");
                return "[]";
            }

            var content = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"✅ Müşteri verileri alındı");
            return content; // Ham JSON'u döndür
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ ExactOnline müşteri listeleme hatası: {ex.Message}");
            return "[]";
        }
    }

    // shippingmethod

    public async Task<string> GetAllShippingMethodAsync()
    {
        var token = await GetValidToken();
        if (token == null)
        {
            Console.WriteLine("❌ Token alınamadı");
            return "[]"; // Boş JSON array döndür
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/sales/ShippingMethods";
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"❌ ExactOnline müşteri isteği başarısız: {response.StatusCode}");
                return "[]";
            }

            var content = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"✅ Müşteri verileri alındı");
            return content; // Ham JSON'u döndür
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ ExactOnline müşteri listeleme hatası: {ex.Message}");
            return "[]";
        }
    }

    //salesorder
    public async Task<string> GetAlSalesOrderAsync()
    {
        var token = await GetValidToken();
        if (token == null)
        {
            Console.WriteLine("❌ Token alınamadı");
            return "[]"; // Boş JSON array döndür
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/salesorder/SalesOrders";
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"❌ ExactOnline müşteri isteği başarısız: {response.StatusCode}");
                return "[]";
            }

            var content = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"✅ Müşteri verileri alındı");
            return content; // Ham JSON'u döndür
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ ExactOnline müşteri listeleme hatası: {ex.Message}");
            return "[]";
        }
    }

    // Customer oluşturma/bulma metodu
    public async Task<Guid?> CreateOrGetCustomerAsync(ShopifyCustomer customer)
    {
        var token = await GetValidToken();
        if (token == null)
        {
            Console.WriteLine("❌ Token alınamadı");
            return null;
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            // Önce müşteriyi email ile ara
            var email = customer.Email?.Replace("'", "''");
            var searchUrl = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Accounts?$filter=Email eq '{email}'&$select=ID,Name,Email";

            Console.WriteLine($"🔍 Müşteri aranıyor: {email}");

            var searchResponse = await client.GetAsync(searchUrl);
            if (searchResponse.IsSuccessStatusCode)
            {
                var searchContent = await searchResponse.Content.ReadAsStringAsync();
                using var searchDoc = JsonDocument.Parse(searchContent);

                var dataElement = searchDoc.RootElement.GetProperty("d");
                JsonElement resultsElement = dataElement.ValueKind == JsonValueKind.Object && dataElement.TryGetProperty("results", out var res)
                    ? res : dataElement;

                // Eğer müşteri varsa ID'sini döndür
                if (resultsElement.EnumerateArray().Any())
                {
                    var existingCustomer = resultsElement.EnumerateArray().First();
                    if (existingCustomer.TryGetProperty("ID", out var idProp))
                    {
                        var customerId = Guid.Parse(idProp.GetString());
                        var testUrl = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Accounts(guid'{customerId}')?$select=ID,Name,Email,Type,Status";
                        var testResponse = await client.GetAsync(testUrl);
                        var testContent = await testResponse.Content.ReadAsStringAsync();
                        Console.WriteLine($"🔍 Müşteri Detayları: {testContent}");

                        // Sadece Status "C" olan müşteriler kabul edilecek
                        var statusValue = existingCustomer.TryGetProperty("Status", out var statusProp) ? statusProp.GetString() : null;
                        if (statusValue != null && statusValue != "C")
                        {
                            Console.WriteLine($"⏭️ Mevcut müşteri Status={statusValue}, 'C' değil - atlanıyor: {customerId}");
                            _logger.LogInformation("Mevcut müşteri Status={Status}, 'C' değil - Exact'a yazılmadı: {Email}", statusValue, email);
                            return null;
                        }

                        Console.WriteLine($"✅ Mevcut müşteri bulundu (Status=C): {customerId}");
                        return customerId;
                    }
                }
            }

            // Müşteri bulunamadı - Yeni müşteri oluştur (sadece Status "C" olarak)
            Console.WriteLine($"⚠️ Müşteri bulunamadı: {email} - Yeni müşteri (Status=C) oluşturuluyor...");

            // Müşteri adını oluştur
            var customerName = $"{customer.FirstName} {customer.LastName}".Trim();
            if (string.IsNullOrEmpty(customerName))
            {
                customerName = customer.Email;
            }

            // Yeni müşteri verisi oluştur
            var newCustomerData = new Dictionary<string, object>
            {
                { "Name", customerName },
                { "Email", customer.Email ?? "" },
                { "Status", "C" } // C = Customer
            };

            // Adres bilgilerini ekle (varsa)
            if (customer.DefaultAddress != null)
            {
                if (!string.IsNullOrEmpty(customer.DefaultAddress.Address1))
                    newCustomerData["AddressLine1"] = customer.DefaultAddress.Address1;

                if (!string.IsNullOrEmpty(customer.DefaultAddress.Address2))
                    newCustomerData["AddressLine2"] = customer.DefaultAddress.Address2;

                if (!string.IsNullOrEmpty(customer.DefaultAddress.City))
                    newCustomerData["City"] = customer.DefaultAddress.City;

                if (!string.IsNullOrEmpty(customer.DefaultAddress.Zip))
                    newCustomerData["Postcode"] = customer.DefaultAddress.Zip;

                if (!string.IsNullOrEmpty(customer.DefaultAddress.CountryCode))
                    newCustomerData["Country"] = customer.DefaultAddress.CountryCode;
            }

            var createUrl = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Accounts";
            var jsonContent = JsonSerializer.Serialize(newCustomerData);
            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            Console.WriteLine($"📤 Yeni müşteri oluşturuluyor: {customerName}");
            Console.WriteLine($"📋 Gönderilen veri: {jsonContent}");

            var createResponse = await client.PostAsync(createUrl, httpContent);
            var createContent = await createResponse.Content.ReadAsStringAsync();

            if (createResponse.IsSuccessStatusCode)
            {
                using var createDoc = JsonDocument.Parse(createContent);

                // Response'dan yeni müşteri ID'sini al
                if (createDoc.RootElement.TryGetProperty("d", out var dElement))
                {
                    if (dElement.TryGetProperty("ID", out var newIdProp))
                    {
                        var newCustomerId = Guid.Parse(newIdProp.GetString());
                        Console.WriteLine($"✅ Yeni müşteri oluşturuldu: {newCustomerId}");
                        _logger.LogInformation($"ExactOnline'da yeni müşteri oluşturuldu: {newCustomerId} - {customerName}");
                        return newCustomerId;
                    }
                }

                Console.WriteLine($"⚠️ Müşteri oluşturuldu ancak ID alınamadı. Response: {createContent}");
            }
            else
            {
                Console.WriteLine($"❌ Müşteri oluşturma hatası: {createResponse.StatusCode}");
                Console.WriteLine($"📋 Hata detayı: {createContent}");
                _logger.LogError($"ExactOnline müşteri oluşturma hatası: {createResponse.StatusCode} - {createContent}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ ExactOnline müşteri arama/oluşturma hatası: {ex.Message}");
            _logger.LogError($"Müşteri arama/oluşturma hatası: {ex.Message}");
        }

        return null;
    }


    public async Task<Item> GetOrCreateItemAsync(string itemName)
    {
        try
        {
            var token = await GetValidToken();
            if (token == null)
            {
                Console.WriteLine("❌ Token alınamadı");
                return null;
            }

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token.access_token);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // Önce ürünü ara
            var searchUrl = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/Items?$filter=Code eq '{itemName}'";
            var searchResponse = await client.GetAsync(searchUrl);

            if (searchResponse.IsSuccessStatusCode)
            {
                var searchContent = await searchResponse.Content.ReadAsStringAsync();

                Console.WriteLine("📋 ExactOnline Response:");
                Console.WriteLine(searchContent.Substring(0, Math.Min(500, searchContent.Length)));

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                };

                // OData formatını parse et
                var oDataResponse = JsonSerializer.Deserialize<ItemResponse>(searchContent, options);

                if (oDataResponse?.d?.results != null && oDataResponse.d.results.Any())
                {
                    var existingProduct = oDataResponse.d.results.FirstOrDefault();
                    Console.WriteLine($"✅ Mevcut ürün bulundu: {existingProduct.ID} - {existingProduct.Description}");
                    Console.WriteLine($"📦 Ürün Detayları: Code={existingProduct.Code}, SalesVatCode={existingProduct.SalesVatCode}, Price={existingProduct.StandardSalesPrice}");

                    // Tek elemanlı liste oluştur ve KDV hesapla
                    var itemList = new List<Item> { existingProduct };
                    var itemsWithVat = await SetSalesVatAsync(itemList);
                    return itemsWithVat.FirstOrDefault();
                }
                else
                {
                    Console.WriteLine($"❌ Ürün listesi boş");
                }
            }
            else
            {
                Console.WriteLine($"❌ API Hatası: {searchResponse.StatusCode}");
                var errorContent = await searchResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"Hata: {errorContent}");
            }

            Console.WriteLine($"❌ Ürün bulunamadı: {itemName}");
            return null;
        }
        catch (JsonException jsonEx)
        {
            _logger.LogError($"JSON Parse Hatası: {jsonEx.Message}");
            Console.WriteLine($"JSON Parse Hatası: {jsonEx.Message}");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError($"ExactOnline ürün bulma hatası: {ex.Message}");
            Console.WriteLine($"Hata: {ex.Message}");
            return null;
        }
    }

    private async Task<decimal?> GetItemVatAsync(string vatCode)
    {
        try
        {
            var token = await GetValidToken();
            if (token == null)
            {
                Console.WriteLine("❌ Token alınamadı");
                return null;
            }

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token.access_token);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var cleanVatCode = vatCode.Trim();
            var requestUrl = $"{_baseUrl}/api/v1/{_divisionCode}/vat/VATCodes?$filter=Code eq '{cleanVatCode}'&$select=Account,Code,Percentage";

            Console.WriteLine($"🔍 KDV Sorgusu: Code={cleanVatCode}");

            var response = await client.GetAsync(requestUrl);
            if (response.IsSuccessStatusCode)
            {
                var jsonResponse = await response.Content.ReadAsStringAsync();
                var itemVatResponse = JsonSerializer.Deserialize<ItemVatResponse>(jsonResponse);
                var result = itemVatResponse?.d?.results?.FirstOrDefault();

                if (result == null)
                {
                    Console.WriteLine($"⚠️ KDV kodu bulunamadı (Kod: {cleanVatCode})");
                    return null;
                }

                var percentage = result.Percentage ?? 0;
                Console.WriteLine($"💰 KDV Oranı bulundu: {percentage} (Kod: {cleanVatCode})");
                return percentage;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"❌ KDV API Hatası: {response.StatusCode} - {errorContent}");
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"KDV API çağrısı hatası: {ex.Message}");
            Console.WriteLine($"❌ KDV çağrısı başarısız: {ex.Message}");
            return null;
        }
    }

    private async Task<List<Item>> SetSalesVatAsync(List<Item> items)
    {
        List<Item> updatedItems = new List<Item>();
        decimal? vatPercentage = null;
        string lastVatCode = null;

        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.SalesVatCode))
            {
                var currentVatCode = item.SalesVatCode.Trim();

                // Eğer bu KDV kodu daha önce sorgulanmadıysa, API'den çek
                if (lastVatCode != currentVatCode)
                {
                    vatPercentage = await GetItemVatAsync(currentVatCode);
                    lastVatCode = currentVatCode;
                }

                // API'den değer geldiyse kullan (0 da geçerli bir KDV oranı)
                if (vatPercentage.HasValue)
                {
                    item.SalesVat = vatPercentage.Value * 100;
                    Console.WriteLine($"✅ KDV hesaplandı - Kod: {currentVatCode}, Oran: %{item.SalesVat}");
                }
                else
                {
                    // SalesVat null bırak → ShopifyOrderCrud fallback 0.21 kullanır
                    item.SalesVat = null;
                    Console.WriteLine($"⚠️ KDV oranı alınamadı (Kod: {currentVatCode}), fallback kullanılacak");
                }

                updatedItems.Add(item);
            }
            else
            {
                // SalesVatCode boş → null bırak, Exact Online kendi kuralını uygulasın
                item.SalesVat = null;
                updatedItems.Add(item);
                Console.WriteLine($"⚠️ SalesVatCode boş, KDV null olarak ayarlandı");
            }
        }

        return updatedItems;
    }

    public async Task<(bool success, Guid? orderId, string? orderNumber)> CreateSalesOrderAsync(ExactOrder order)
    {
        try
        {
            var token = await GetValidToken();
            if (token == null)
            {
                Console.WriteLine("❌ Token alınamadı");
                return (false, null, null);
            }

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token.access_token);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var createUrl = $"{_baseUrl}/api/v1/{_divisionCode}/salesorder/SalesOrders";

            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = null,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                Converters = { new ExactDateTimeConverter() } // Custom converter ekle
            };

            var orderJson = JsonSerializer.Serialize(order, options);

            Console.WriteLine($"📤 Gönderilen JSON (ilk 1000 karakter):");
            Console.WriteLine(orderJson.Substring(0, Math.Min(5000, orderJson.Length)));

            var content = new StringContent(orderJson, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(createUrl, content);
            var responseBody = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"Response: {responseBody}");

            if (response.IsSuccessStatusCode)
            {
                // Response'dan OrderID ve OrderNumber'ı çıkar
                try
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    var root = doc.RootElement;

                    Guid? createdOrderId = null;
                    string? createdOrderNumber = null;

                    // Exact Online genellikle "d" altında response döner
                    if (root.TryGetProperty("d", out var dElement))
                    {
                        if (dElement.TryGetProperty("OrderID", out var orderIdProp) && orderIdProp.ValueKind == JsonValueKind.String)
                        {
                            if (Guid.TryParse(orderIdProp.GetString(), out var parsedGuid))
                            {
                                createdOrderId = parsedGuid;
                            }
                        }

                        if (dElement.TryGetProperty("OrderNumber", out var orderNumProp))
                        {
                            createdOrderNumber = orderNumProp.ValueKind == JsonValueKind.Number
                                ? orderNumProp.GetInt32().ToString()
                                : orderNumProp.GetString();
                        }
                    }

                    _logger.LogInformation("✅ ExactOnline satış siparişi başarıyla oluşturuldu - OrderID: {OrderId}, OrderNumber: {OrderNumber}",
                        createdOrderId, createdOrderNumber);

                    return (true, createdOrderId, createdOrderNumber);
                }
                catch (Exception parseEx)
                {
                    _logger.LogWarning("⚠️ Response parse edilemedi ama sipariş oluşturuldu: {Error}", parseEx.Message);
                    return (true, null, null);
                }
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"❌ ExactOnline sipariş oluşturma hatası: {errorContent}");
                Console.WriteLine($"❌ Hata detayı: {errorContent}");
                return (false, null, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ ExactOnline sipariş oluşturma hatası: {ex.Message}");
            Console.WriteLine($"❌ Exception: {ex.Message}");
            return (false, null, null);
        }
    }


    //son 24 saatte eklenen müşteriler
    public async Task<List<string>> GetRecentCustomerEmailsAsync(int hours = 24)
    {
        try
        {
            var token = await GetValidToken();
            if (token == null)
            {
                _logger.LogError("Token alınamadı");
                return new List<string>();
            }

            // Son X saatteki tarih
            var cutoffDate = DateTime.UtcNow.AddHours(-hours);
            var dateFilter = cutoffDate.ToString("yyyy-MM-ddTHH:mm:ss");

            int top = 60;
            int skip = 0;
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Accounts?$filter=Created ge datetime'{dateFilter}'&$top={top}&$skip={skip}&$select=Email,Name,Created";

            _logger.LogInformation("📡 Son {Hours} saatte oluşturulan müşteriler getiriliyor...", hours);
            _logger.LogInformation("🔗 API URL: {Url}", url);
            _logger.LogInformation("📅 Cutoff Date (UTC): {Date}", cutoffDate);

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token.access_token);
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("❌ Müşteriler getirilemedi: {Status} - {Error}",
                    response.StatusCode, error);
                return new List<string>();
            }

            var jsonContent = await response.Content.ReadAsStringAsync();
            var jsonDoc = JsonDocument.Parse(jsonContent);

            var emails = new List<string>();

            // Exact Online'ın format: { "d": [ {...}, {...} ] }
            if (jsonDoc.RootElement.TryGetProperty("d", out var dataArray))
            {
                foreach (var customer in dataArray.EnumerateArray())
                {
                    // Email'i al
                    if (customer.TryGetProperty("Email", out var emailProp))
                    {
                        var email = emailProp.GetString();
                        if (!string.IsNullOrWhiteSpace(email))
                        {
                            emails.Add(email);

                            // Debug için
                            if (customer.TryGetProperty("Name", out var nameProp))
                            {
                                _logger.LogInformation("✅ Yeni müşteri: {Name} - {Email}",
                                    nameProp.GetString(), email);
                            }
                        }
                    }
                }

                _logger.LogInformation("📧 Toplam {Count} email adresi bulundu", emails.Count);
            }
            else
            {
                _logger.LogWarning("⚠️ API yanıtında 'd' property'si bulunamadı");
            }

            return emails;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Email'leri getirirken hata: {Error}", ex.Message);
            return new List<string>();
        }
    }


    //get customer by email
    public async Task<Account> GetCustomerByEmailAsync(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            Console.WriteLine("⚠️ Email boş olamaz");
            return null;
        }

        var token = await GetValidToken();
        if (token == null)
        {
            Console.WriteLine("❌ Token alınamadı");
            return null;
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            // Email'deki tek tırnakları escape et (SQL injection koruması)
            var escapedEmail = email.Replace("'", "''");

            // API URL'i oluştur - $filter ile email'e göre ara
            var searchUrl = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Accounts?$filter=Email eq '{escapedEmail}'";

            Console.WriteLine($"🔍 Email araniyor: {email}");

            var response = await client.GetAsync(searchUrl);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"❌ API hatası: {response.StatusCode}");
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Hata detayı: {errorContent}");
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();

            // JSON'u ön işle (tip uyuşmazlıklarını düzelt)
            content = PreProcessJson(content);

            using var doc = JsonDocument.Parse(content);

            // "d" property'sini al
            if (!doc.RootElement.TryGetProperty("d", out var dElement))
            {
                Console.WriteLine("❌ 'd' property bulunamadı");
                return null;
            }

            JsonElement resultsArray;

            // "d" direkt array mi yoksa object içinde "results" mi?
            if (dElement.ValueKind == JsonValueKind.Array)
            {
                resultsArray = dElement;
            }
            else if (dElement.TryGetProperty("results", out var resultsProperty))
            {
                resultsArray = resultsProperty;
            }
            else
            {
                Console.WriteLine("❌ Results bulunamadı");
                return null;
            }

            // Sonuçları kontrol et
            var resultCount = resultsArray.GetArrayLength();

            if (resultCount == 0)
            {
                Console.WriteLine($"⚠️ '{email}' email adresine sahip müşteri bulunamadı");
                return null;
            }

            if (resultCount > 1)
            {
                Console.WriteLine($"⚠️ '{email}' email adresiyle {resultCount} müşteri bulundu, ilki döndürülüyor");
            }

            // JSON deserialization options
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = null,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            // İlk sonucu al ve deserialize et
            var firstCustomer = resultsArray[0];
            var customerJson = firstCustomer.GetRawText();
            customerJson = PreProcessJson(customerJson);

            var account = JsonSerializer.Deserialize<Account>(customerJson, jsonOptions);

            if (account != null)
            {
                if (account.Classification1 != null)
                {
                    Console.WriteLine($"📊 Müşteri Sınıflandırması: {account.Classification1}");
                    var searcClassification = $"{_baseUrl}/api/v1/{_divisionCode}/crm/AccountClassifications?$filter=ID eq guid'{account.Classification1}'";

                    Console.WriteLine($"🔍 Email araniyor: {email}");

                    var responseClassification = await client.GetAsync(searcClassification);

                    if (!responseClassification.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"❌ API hatası: {responseClassification.StatusCode}");
                        var errorContent = await responseClassification.Content.ReadAsStringAsync();
                        Console.WriteLine($"Hata detayı: {errorContent}");
                        return null;
                    }
                    else
                    {
                        var contentClassification = await responseClassification.Content.ReadAsStringAsync();
                        var classificationDoc = JsonDocument.Parse(contentClassification);
                        var codeClassification = classificationDoc.RootElement.GetProperty("d").GetProperty("results")[0].GetProperty("Code").GetString();
                        if (codeClassification != null)
                        {
                            account.ClassificationDescription = codeClassification;
                        }
                    }
                }
                Console.WriteLine($"✅ Müşteri bulundu: {account.Name} (ID: {account.ID})");
            }

            return account;
        }
        catch (JsonException jsonEx)
        {
            Console.WriteLine($"❌ JSON parse hatası: {jsonEx.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Hata: {ex.Message}");
            Console.WriteLine($"Stack Trace: {ex.StackTrace}");
            return null;
        }
    }


    public async Task<List<ExactOrderDetail>> GetOrdersByCustomerGuid(Guid customerGuid, int top = 100, int skip = 0)
    {
        var token = await GetValidToken();
        if (token == null)
        {
            Console.WriteLine("❌ Token alınamadı");
            return new List<ExactOrderDetail>();
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            // Bir yıl önceki tarihi hesapla
            var oneYearAgo = DateTime.UtcNow.AddYears(-1).ToString("yyyy-MM-ddTHH:mm:ss");

            var url = $"{_baseUrl}/api/v1/{_divisionCode}/salesorder/SalesOrders" +
                      $"?$filter=OrderedBy eq guid'{customerGuid}' and OrderDate ge datetime'{oneYearAgo}'" +
                      $"&$orderby=OrderDate desc" +
                      $"&$top={top}" +
                      $"&$skip={skip}";

            Console.WriteLine($"🔍 Sipariş URL: {url}");
            Console.WriteLine($"📅 Filtreleme tarihi: {oneYearAgo}");

            var searchResponse = await client.GetAsync(url);

            if (!searchResponse.IsSuccessStatusCode)
            {
                var errorContent = await searchResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"❌ API Hatası ({searchResponse.StatusCode}): {errorContent}");
                _logger.LogError($"ExactOnline API Hatası: {searchResponse.StatusCode} - {errorContent}");
                return new List<ExactOrderDetail>();
            }

            var searchContent = await searchResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"📝 API Response: {searchContent.Substring(0, Math.Min(500, searchContent.Length))}...");

            using var searchDoc = JsonDocument.Parse(searchContent);

            var dataElement = searchDoc.RootElement.GetProperty("d");
            JsonElement resultsElement = dataElement.ValueKind == JsonValueKind.Object &&
                                         dataElement.TryGetProperty("results", out var res)
                ? res
                : dataElement;

            var orderDetails = new List<ExactOrderDetail>();

            if (resultsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var orderElement in resultsElement.EnumerateArray())
                {
                    try
                    {
                        var orderDetail = new ExactOrderDetail
                        {
                            OrderID = orderElement.GetProperty("OrderID").GetGuid(),
                            OrderNumber = orderElement.TryGetProperty("OrderNumber", out var orderNum)
                                ? orderNum.GetInt32()
                                : 0,
                            OrderDate = ParseExactDate(orderElement, "OrderDate"),
                            AmountDC = orderElement.TryGetProperty("AmountDC", out var amountDC)
                        ? amountDC.GetDecimal()
                        : 0,
                            AmountFC = orderElement.TryGetProperty("AmountFC", out var amountFC)
                        ? amountFC.GetDecimal()
                        : 0,
                            AmountDiscount = orderElement.TryGetProperty("AmountDiscount", out var discount)
                        ? discount.GetDecimal()
                        : 0,
                            Status = orderElement.TryGetProperty("Status", out var status)
                        ? status.GetInt32()
                        : 0,

                        };

                        orderDetails.Add(orderDetail);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠️ Sipariş parse hatası: {ex.Message}");
                        _logger.LogWarning($"Sipariş parse hatası: {ex.Message}");
                        continue;
                    }
                }
            }

            Console.WriteLine($"✅ {orderDetails.Count} sipariş bulundu (son 1 yıl)");
            _logger.LogInformation($"Müşteri {customerGuid} için {orderDetails.Count} sipariş bulundu (son 1 yıl)");

            return orderDetails;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ ExactOnline sipariş çekme hatası: {ex.Message}");
            _logger.LogError(ex, $"Sipariş çekme hatası - CustomerGuid: {customerGuid}");
            return new List<ExactOrderDetail>();
        }
    }

    public async Task<ExactOrderDetail> GetOrderDetailByOrderId(Guid orderId)
    {
        var token = await GetValidToken();
        if (token == null)
        {
            Console.WriteLine("❌ Token alınamadı");
            return null;
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            // Sipariş detayını ve satırlarını expand ile birlikte çek
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/salesorder/SalesOrders(guid'{orderId}')" +
                      $"?$expand=SalesOrderLines";

            Console.WriteLine($"🔍 Sipariş Detay URL: {url}");

            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"❌ API Hatası ({response.StatusCode}): {errorContent}");
                _logger.LogError($"ExactOnline API Hatası: {response.StatusCode} - {errorContent}");
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);

            var dataElement = doc.RootElement.GetProperty("d");

            var orderDetail = new ExactOrderDetail
            {
                OrderID = dataElement.GetProperty("OrderID").GetGuid(),
                OrderNumber = dataElement.TryGetProperty("OrderNumber", out var orderNum)
                    ? orderNum.GetInt32()
                    : 0,
                OrderDate = ParseExactDate(dataElement, "OrderDate"),
                DeliveryDate = ParseExactDate(dataElement, "DeliveryDate"),
                OrderedBy = dataElement.TryGetProperty("OrderedBy", out var orderedBy)
                    ? orderedBy.GetGuid()
                    : Guid.Empty,
                DeliverTo = dataElement.TryGetProperty("DeliverTo", out var deliverTo)
                    ? deliverTo.GetGuid()
                    : Guid.Empty,
                InvoiceTo = dataElement.TryGetProperty("InvoiceTo", out var invoiceTo)
                    ? invoiceTo.GetGuid()
                    : Guid.Empty,
                AmountDC = dataElement.TryGetProperty("AmountDC", out var amountDC)
                    ? amountDC.GetDecimal()
                    : 0,
                AmountFC = dataElement.TryGetProperty("AmountFC", out var amountFC)
                    ? amountFC.GetDecimal()
                    : 0,
                AmountDiscount = dataElement.TryGetProperty("AmountDiscount", out var discount)
                    ? discount.GetDecimal()
                    : 0,
                Status = dataElement.TryGetProperty("Status", out var status)
                    ? status.GetInt32()
                    : 0,
                Description = dataElement.TryGetProperty("Description", out var desc)
                    ? desc.GetString()
                    : null,
                Currency = dataElement.TryGetProperty("Currency", out var currency)
                    ? currency.GetString()
                    : null,
                DeliveryAddress = dataElement.TryGetProperty("DeliveryAddress", out var deliveryAddr)
                    ? deliveryAddr.GetGuid()
                    : (Guid?)null,
                WarehouseID = dataElement.TryGetProperty("Warehouse", out var warehouse)
                    ? warehouse.GetGuid()
                    : (Guid?)null,
                SalesOrderLines = new List<ExactOrderLine>()
            };

            // Sipariş satırlarını parse et
            if (dataElement.TryGetProperty("SalesOrderLines", out var linesElement))
            {
                JsonElement resultsElement = linesElement.ValueKind == JsonValueKind.Object &&
                                            linesElement.TryGetProperty("results", out var res)
                    ? res
                    : linesElement;

                if (resultsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var lineElement in resultsElement.EnumerateArray())
                    {
                        var orderLine = new ExactOrderLine
                        {
                            ID = lineElement.GetProperty("ID").GetGuid(),
                            Item = lineElement.TryGetProperty("Item", out var item)
                                ? item.GetGuid()
                                : Guid.Empty,
                            Description = lineElement.TryGetProperty("Description", out var lineDesc)
                                ? lineDesc.GetString()
                                : null,
                            Quantity = lineElement.TryGetProperty("Quantity", out var qty)
                                ? qty.GetDouble()
                                : 0,
                            UnitPrice = lineElement.TryGetProperty("UnitPrice", out var unitPrice)
                                ? unitPrice.GetDouble()
                                : 0,
                            NetPrice = lineElement.TryGetProperty("NetPrice", out var netPrice)
                                ? netPrice.GetDouble()
                                : 0,
                            Discount = lineElement.TryGetProperty("Discount", out var lineDiscount)
                                ? lineDiscount.GetDouble()
                                : 0,
                            VATPercentage = lineElement.TryGetProperty("VATPercentage", out var vatPerc)
                                ? vatPerc.GetDouble()
                                : 0,
                            VATCode = lineElement.TryGetProperty("VATCode", out var vatCode)
                                ? vatCode.GetString()
                                : null,
                            UnitCode = lineElement.TryGetProperty("UnitCode", out var unitCode)
                                ? unitCode.GetString()
                                : null,
                            DeliveryDate = ParseExactDate(lineElement, "DeliveryDate"),
                            Division = lineElement.TryGetProperty("Division", out var division)
                                ? division.GetInt32()
                                : 0,
                            OrderNumber = lineElement.TryGetProperty("OrderNumber", out var lineOrderNum)
                                ? lineOrderNum.GetInt32()
                                : (int?)null,
                            OrderID = lineElement.TryGetProperty("OrderID", out var lineOrderId)
                                ? lineOrderId.GetGuid()
                                : (Guid?)null,
                        };

                        orderDetail.SalesOrderLines.Add(orderLine);
                    }
                }
            }

            Console.WriteLine($"✅ Sipariş detayı bulundu - {orderDetail.SalesOrderLines.Count} satır");
            _logger.LogInformation($"Sipariş {orderId} için {orderDetail.SalesOrderLines.Count} satır bulundu");

            return orderDetail;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ ExactOnline sipariş detay hatası: {ex.Message}");
            _logger.LogError(ex, $"Sipariş detay hatası - OrderID: {orderId}");
            return null;
        }
    }

    // Email ile müşteri GUID'ini bul
    public async Task<Guid?> GetCustomerGuidByEmail(string email)
    {
        var token = await GetValidToken();
        if (token == null)
        {
            Console.WriteLine("❌ Token alınamadı");
            return null;
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var encodedEmail = Uri.EscapeDataString(email);
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Accounts" +
                      $"?$filter=Email eq '{encodedEmail}'" +
                      $"&$select=ID,Email,Name" +
                      $"&$top=1";

            Console.WriteLine($"🔍 Müşteri Email Arama URL: {url}");

            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"❌ API Hatası ({response.StatusCode}): {errorContent}");
                _logger.LogError($"ExactOnline API Hatası: {response.StatusCode} - {errorContent}");
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);

            var dataElement = doc.RootElement.GetProperty("d");
            JsonElement resultsElement = dataElement.ValueKind == JsonValueKind.Object &&
                                         dataElement.TryGetProperty("results", out var res)
                ? res
                : dataElement;

            if (resultsElement.ValueKind == JsonValueKind.Array)
            {
                var array = resultsElement.EnumerateArray().ToList();
                if (array.Count > 0)
                {
                    var customerElement = array[0];
                    var customerId = customerElement.GetProperty("ID").GetGuid();
                    var customerName = customerElement.TryGetProperty("Name", out var name)
                        ? name.GetString()
                        : "Unknown";

                    Console.WriteLine($"✅ Müşteri bulundu: {customerName} ({customerId})");
                    return customerId;
                }
            }

            Console.WriteLine($"❌ Email ile müşteri bulunamadı: {email}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ ExactOnline müşteri arama hatası: {ex.Message}");
            _logger.LogError(ex, $"Müşteri arama hatası - Email: {email}");
            return null;
        }
    }

    // Email ile siparişleri getir (ana metod)
    public async Task<List<ExactOrderDetail>> GetOrdersByCustomerEmail(string email, int top = 100, int skip = 0)
    {
        var customerGuid = await GetCustomerGuidByEmail(email);

        if (!customerGuid.HasValue)
        {
            Console.WriteLine($"❌ Email '{email}' için müşteri bulunamadı");
            return new List<ExactOrderDetail>();
        }

        Console.WriteLine($"🔍 Müşteri GUID: {customerGuid.Value} - Siparişler getiriliyor...");
        return await GetOrdersByCustomerGuid(customerGuid.Value, top, skip);
    }


    // son 2 günlük siparişleri getir
    public async Task<string> GetRecentOrdersRawJsonAsync()
    {
        var token = await GetValidToken();
        if (token == null)
        {
            Console.WriteLine("❌ Token alınamadı");
            return null;
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            // Son 2 gün için tarih filtresi
            var twoDaysAgo = DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-ddTHH:mm:ss");

            var allOrders = new List<JsonElement>();
            int top = 100;
            int skip = 0;
            bool hasMore = true;

            Console.WriteLine($"🔍 Son 2 günlük siparişler çekiliyor (Tarih: {twoDaysAgo})");

            while (hasMore)
            {
                // ✅ Created tarihine göre filtrele + pagination
                var url = $"{_baseUrl}/api/v1/{_divisionCode}/salesorder/SalesOrders" +
                          $"?$filter=Created gt datetime'{twoDaysAgo}'" +
                          $"&$orderby=Created desc" +
                          $"&$top={top}" +
                          $"&$skip={skip}";

                Console.WriteLine($"📤 API Request: Skip={skip}, Top={top}");
                Console.WriteLine($"   URL: {url}");

                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"❌ API Hatası ({response.StatusCode}): {errorContent}");
                    _logger.LogError($"ExactOnline API Hatası: {response.StatusCode} - {errorContent}");
                    break;
                }

                var content = await response.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                // results array'ini bul
                JsonElement resultsElement;
                if (root.TryGetProperty("d", out var dataElement))
                {
                    if (dataElement.TryGetProperty("results", out var results))
                    {
                        resultsElement = results;
                    }
                    else
                    {
                        resultsElement = dataElement;
                    }
                }
                else
                {
                    resultsElement = root;
                }

                if (resultsElement.ValueKind == JsonValueKind.Array)
                {
                    var ordersInBatch = resultsElement.EnumerateArray().ToList();

                    if (ordersInBatch.Any())
                    {
                        // Her siparişi listeye ekle
                        foreach (var order in ordersInBatch)
                        {
                            allOrders.Add(order.Clone());
                        }

                        Console.WriteLine($"✅ {ordersInBatch.Count} sipariş alındı (Toplam: {allOrders.Count})");

                        skip += top;

                        // Eğer dönen sonuç top'tan azsa, daha fazla veri yok
                        if (ordersInBatch.Count < top)
                        {
                            hasMore = false;
                        }
                    }
                    else
                    {
                        hasMore = false;
                        Console.WriteLine("ℹ️ Daha fazla sipariş yok");
                    }
                }
                else
                {
                    hasMore = false;
                }

                // Rate limiting
                await Task.Delay(500);
            }

            Console.WriteLine($"🎉 Toplam {allOrders.Count} sipariş bulundu");

            // Tüm siparişleri tek bir JSON'a dönüştür
            var finalJson = new
            {
                TotalCount = allOrders.Count,
                FetchDate = DateTime.UtcNow,
                FilterDate = twoDaysAgo,
                Orders = allOrders.Select(order => JsonSerializer.Deserialize<JsonElement>(order.GetRawText()))
            };

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            var jsonResult = JsonSerializer.Serialize(finalJson, jsonOptions);

            // Opsiyonel: Dosyaya kaydet
            var logFile = $"Data/Logs/recent_orders_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            var logDirectory = Path.GetDirectoryName(logFile);
            if (!string.IsNullOrEmpty(logDirectory) && !Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }
            await File.WriteAllTextAsync(logFile, jsonResult);
            Console.WriteLine($"📁 Ham JSON kaydedildi: {logFile}");

            _logger.LogInformation($"{allOrders.Count} son 2 günlük sipariş ham JSON olarak alındı");

            return jsonResult;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ ExactOnline sipariş çekme hatası: {ex.Message}");
            Console.WriteLine($"   Stack Trace: {ex.StackTrace}");
            _logger.LogError(ex, $"Son 2 günlük sipariş çekme hatası");
            return null;
        }
    }

    // Yardımcı metod - Exact Online tarih formatını parse eder
    private DateTime ParseExactDate(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var dateElement))
        {
            return DateTime.MinValue;
        }

        // Exact Online bazen "/Date(1234567890000)/" formatında döndürür
        if (dateElement.ValueKind == JsonValueKind.String)
        {
            var dateString = dateElement.GetString();

            // /Date(...)/ formatı kontrolü
            if (dateString.StartsWith("/Date(") && dateString.EndsWith(")/"))
            {
                var ticksString = dateString.Substring(6, dateString.Length - 8);
                // +0300 gibi timezone bilgisi varsa temizle
                if (ticksString.Contains("+") || ticksString.Contains("-"))
                {
                    ticksString = ticksString.Split('+', '-')[0];
                }

                if (long.TryParse(ticksString, out var ticks))
                {
                    // Unix epoch'tan milisaniye cinsinden
                    return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                        .AddMilliseconds(ticks)
                        .ToLocalTime();
                }
            }

            // Normal ISO 8601 formatı
            if (DateTime.TryParse(dateString, out var parsedDate))
            {
                return parsedDate;
            }
        }

        // Doğrudan DateTime olarak dene
        try
        {
            return dateElement.GetDateTime();
        }
        catch
        {
            Console.WriteLine($"⚠️ Tarih parse edilemedi: {propertyName}");
            return DateTime.MinValue;
        }
    }

    public async Task<List<ExactItemSummaryDto>> GetAllItemsSummaryAsync()
    {
        var token = await GetValidToken();
        if (token == null)
        {
            Console.WriteLine("❌ Token alınamadı, Items getirilemedi");
            return new List<ExactItemSummaryDto>();
        }

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var results = new List<ExactItemSummaryDto>();
        int top = 60;
        int skip = 0;
        int page = 1;

        Console.WriteLine("📦 Tüm ürünler çekiliyor (ID, Code, StandardSalesPrice)...");

        while (true)
        {
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/Items?$select=ID,Code,StandardSalesPrice&$top={top}&$skip={skip}";
            Console.WriteLine($"📡 Sayfa {page}, Skip: {skip}");

            var resp = await client.GetAsync(url);

            if (!resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    Console.WriteLine("⏳ Rate limit, 30 saniye bekleniyor...");
                    await Task.Delay(30000);
                    continue;
                }
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    token = await GetValidToken();
                    if (token == null) break;
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
                    continue;
                }
                Console.WriteLine($"❌ API Hatası: {resp.StatusCode}");
                break;
            }

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("d", out var d)) break;

            JsonElement items;
            if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("results", out var res))
                items = res;
            else if (d.ValueKind == JsonValueKind.Array)
                items = d;
            else break;

            int count = items.GetArrayLength();
            Console.WriteLine($"✅ Sayfa {page}: {count} ürün");

            foreach (var item in items.EnumerateArray())
            {
                var id = item.TryGetProperty("ID", out var idEl) ? idEl.GetString() : null;
                var code = item.TryGetProperty("Code", out var codeEl) ? codeEl.GetString() : null;
                decimal? standardSalesPrice = null;
                if (item.TryGetProperty("StandardSalesPrice", out var priceEl) && priceEl.ValueKind == JsonValueKind.Number)
                    standardSalesPrice = priceEl.GetDecimal();

                results.Add(new ExactItemSummaryDto
                {
                    ID = id,
                    Code = code,
                    StandardSalesPrice = standardSalesPrice
                });
            }

            if (count < top) break;
            skip += top;
            page++;
        }

        Console.WriteLine($"🎉 Toplam {results.Count} ürün çekildi");
        return results;
    }

    /// <summary>
    /// Exact'tan Status eq 'C' olan müşterileri sayfalı olarak çeker (ID ve Email alanları).
    /// </summary>
    public async Task<List<Dictionary<string, object>>> GetCustomersPageAsync(int skip, int top)
    {
        var token = await GetValidToken();
        if (token == null) return null;

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var url = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Accounts?$filter=Status eq 'C'&$top={top}&$skip={skip}&$select=ID,Email,Name";

        int retryCount = 0;
        const int maxRetries = 3;

        while (retryCount <= maxRetries)
        {
            try
            {
                var resp = await client.GetAsync(url);

                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    retryCount++;
                    _logger.LogWarning("⏳ Rate limit, {Retry}. deneme için 30sn bekleniyor...", retryCount);
                    await Task.Delay(30000);
                    continue;
                }

                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    token = await GetValidToken();
                    if (token == null) return null;
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogError("❌ Exact API hatası: {Status}", resp.StatusCode);
                    return null;
                }

                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("d", out var dataElement)) return null;

                JsonElement resultsElement;
                if (dataElement.ValueKind == JsonValueKind.Object && dataElement.TryGetProperty("results", out var res))
                    resultsElement = res;
                else if (dataElement.ValueKind == JsonValueKind.Array)
                    resultsElement = dataElement;
                else
                    return null;

                var items = new List<Dictionary<string, object>>();
                foreach (var item in resultsElement.EnumerateArray())
                {
                    var dict = new Dictionary<string, object>();
                    foreach (var prop in item.EnumerateObject())
                    {
                        dict[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.Number => prop.Value.GetDouble(),
                            JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.Null => string.Empty,
                            _ => prop.Value.ToString() ?? string.Empty
                        };
                    }
                    items.Add(dict);
                }

                return items;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ GetCustomersPageAsync hatası");
                return null;
            }
        }

        return null;
    }
}





// === Converter ===
public class FlexibleIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var i))
            return i;
        if (reader.TokenType == JsonTokenType.String && int.TryParse(reader.GetString(), out var s))
            return s;
        return 0;
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

// === Token model ===
public class TokenResponse
{
    public string access_token { get; set; } = string.Empty;
    public string refresh_token { get; set; } = string.Empty;
    public string token_type { get; set; } = "bearer";
    public int expires_in { get; set; }
    public DateTime ExpiryTime { get; set; }

    public bool IsExpired()
    {
        Console.WriteLine(DateTime.UtcNow);
        return DateTime.UtcNow >= ExpiryTime;
    }
}


// OData wrapper class'ı
public class ODataResponse<T>
{
    [JsonPropertyName("d")]
    public T Data { get; set; }
}


public class ExactOrderDetail
{
    public Guid OrderID { get; set; }
    public int OrderNumber { get; set; }
    public DateTime OrderDate { get; set; }

    public DateTime DeliveryDate { get; set; }

    public Guid OrderedBy { get; set; }
    public Guid DeliverTo { get; set; }
    public Guid InvoiceTo { get; set; }
    public decimal AmountDC { get; set; }
    public decimal AmountFC { get; set; }
    public decimal AmountDiscount { get; set; }
    public int Status { get; set; }
    public string Description { get; set; }
    public string Currency { get; set; }
    public Guid? DeliveryAddress { get; set; }
    public Guid? WarehouseID { get; set; }
    public List<ExactOrderLine> SalesOrderLines { get; set; } // Mevcut sınıfınızı kullanıyor
}

public class SalesItemPriceDto
{
    public Guid ID { get; set; }
    public string ItemCode { get; set; }
    public string ItemDescription { get; set; }
    public decimal Price { get; set; }
    public string Currency { get; set; }
    public string Unit { get; set; }
    public string UnitDescription { get; set; }
    public double? Quantity { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime? Modified { get; set; }
    public string ModifierFullName { get; set; }
    public DateTime? Created { get; set; }
    public string Account { get; set; }
    public string AccountName { get; set; }
}

public class ExactItemSummaryDto
{
    public string ID { get; set; }
    public string Code { get; set; }
    public decimal? StandardSalesPrice { get; set; }
}