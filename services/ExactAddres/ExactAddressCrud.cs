using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using ShopifyProductApp.Services;
using System.Text;
using ExactOnline.Models;
using ExactOnline.Converters;

public class ExactAddressCrud
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

    public ExactAddressCrud(
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

    /// Belirli bir müşteriye ait adresleri getirir
    public async Task<List<ExactAddress>> GetCustomerAddresses(string customerId)
    {
        var exactService = _serviceProvider.GetRequiredService<ExactService>();
        var token = await exactService.GetValidToken();

        if (token == null)
        {
            _logger.LogError("Token alınamadı");
            return new List<ExactAddress>();
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var allAddresses = new List<ExactAddress>();
            var errorAddresses = new List<string>(); // Hata alan adresler

            int skip = 0;
            int top = 60;
            bool hasMore = true;

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = null,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
                Converters = { new JsonStringEnumConverter() }
            };

            // JSON serializer options özelleştirme
            jsonOptions.Converters.Add(new JsonStringEnumConverter());

            // Address tipi için custom converter ekleyebiliriz
            // jsonOptions.Converters.Add(new NullableDateConverter());
            // jsonOptions.Converters.Add(new NullableGuidConverter());

            while (hasMore)
            {
                // Müşteriye ait tüm adresleri getir
                var url = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Addresses?$filter=Account eq guid'{customerId}'";

                Console.WriteLine($"🔍 Adres sorgusu: {url}");

                var response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"API hatası: {response.StatusCode}");
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"Hata detayı: {errorContent}");
                    break;
                }

                var content = await response.Content.ReadAsStringAsync();

                // JSON'u önce temizle
                content = PreProcessAddressJson(content);

                using var doc = JsonDocument.Parse(content);

                // "d" property'sini al
                if (!doc.RootElement.TryGetProperty("d", out var dElement))
                {
                    _logger.LogError("'d' property bulunamadı");
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
                    _logger.LogError("Results bulunamadı");
                    break;
                }

                int count = 0;
                int errorCount = 0;

                // API'den gelen gerçek kayıt sayısını al
                int totalFromApi = resultsArray.EnumerateArray().Count();

                if (totalFromApi == 0)
                {
                    _logger.LogInformation($"Müşteri ({customerId}) için adres bulunamadı");
                    break;
                }

                foreach (var addressElement in resultsArray.EnumerateArray())
                {
                    ExactAddress address = null;
                    var addressJson = addressElement.GetRawText();

                    try
                    {
                        // Her adres için de temizlik yap
                        addressJson = PreProcessAddressJson(addressJson);

                        address = JsonSerializer.Deserialize<ExactAddress>(addressJson, jsonOptions);

                        if (address != null)
                        {
                            allAddresses.Add(address);
                            count++;
                            // Debug için adres bilgilerini logla
                            _logger.LogDebug($"Adres alındı: {address.Id}, Tür: {address.TypeDescription}, Şehir: {address.City}");
                        }
                    }
                    catch (JsonException jsonEx)
                    {
                        errorCount++;

                        // Deserialize hatası sırasında ID'yi çıkar
                        try
                        {
                            var errorDoc = JsonDocument.Parse(addressJson);
                            if (errorDoc.RootElement.TryGetProperty("ID", out var idProp))
                            {
                                var id = idProp.GetString();
                                if (!string.IsNullOrWhiteSpace(id))
                                    errorAddresses.Add(id);
                                _logger.LogWarning($"JSON parse hatası - Adres ID: {id}");
                            }
                        }
                        catch { }

                        _logger.LogWarning($"JSON parse hatası ({errorCount}): {jsonEx.Message}");
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        _logger.LogError($"Genel hata: {ex.Message}");
                    }
                }

                if (errorCount > 0)
                {
                    _logger.LogWarning($"{count} başarılı, {errorCount} hatalı adres kaydı (API'den {totalFromApi} kayıt geldi)");
                }
                else
                {
                    _logger.LogInformation($"{count} adres başarıyla alındı");
                }

                // API'den gelen kayıt sayısını kontrol et
                if (totalFromApi < top)
                {
                    hasMore = false;
                    _logger.LogInformation("Son sayfaya ulaşıldı");
                }
                else
                {
                    skip += top;
                    await Task.Delay(500); // Rate limiting için bekle
                }
            }

            if (errorAddresses.Count > 0)
            {
                _logger.LogWarning($"{errorAddresses.Count} adres parse hatası yaşadı");
            }

            // Ana adresi ilk sıraya al
            var sortedAddresses = allAddresses
                .OrderByDescending(a => a.IsMain)
                .ThenBy(a => a.Type)
                .ToList();

            return sortedAddresses;
        }
        catch (Exception ex)
        {
            _logger.LogError($"GetCustomerAddresses metodu hatası: {ex.Message}");
            return new List<ExactAddress>();
        }
    }

    /// Belirli bir adresi ID ile getirir
    public async Task<ExactAddress> GetAddressById(string addressId)
    {
        var exactService = _serviceProvider.GetRequiredService<ExactService>();
        var token = await exactService.GetValidToken();

        if (token == null)
        {
            _logger.LogError("Token alınamadı");
            return null;
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Addresses(guid'{addressId}')";

            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"API hatası: {response.StatusCode}");
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"Hata detayı: {errorContent}");
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            content = PreProcessAddressJson(content);

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = null,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };

            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("d", out var dElement))
            {
                _logger.LogError("'d' property bulunamadı");
                return null;
            }

            var addressJson = dElement.GetRawText();
            addressJson = PreProcessAddressJson(addressJson);

            var address = JsonSerializer.Deserialize<ExactAddress>(addressJson, jsonOptions);

            // if (address != null && address.Type.HasValue)
            // {
            //     address.AddressType = GetAddressTypeDescription(address.Type.Value);
            // }

            return address;
        }
        catch (Exception ex)
        {
            _logger.LogError($"GetAddressById metodu hatası: {ex.Message}");
            return null;
        }
    }

    /// Müşteri için yeni adres oluşturur
    public async Task<ExactAddress> CreateAddress(ExactAddress address)
    {
        var exactService = _serviceProvider.GetRequiredService<ExactService>();
        var token = await exactService.GetValidToken();

        if (token == null)
        {
            _logger.LogError("Token alınamadı");
            return null;
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Addresses";

            _logger.LogInformation($"📝 Adres oluşturuluyor - Account: {address.AccountId}, Type: {address.Type}");

            var addressDto = new
            {
                Account = address.AccountId.ToString("D"),
                AddressLine1 = string.IsNullOrEmpty(address.AddressLine1) ? null : address.AddressLine1,
                AddressLine2 = string.IsNullOrEmpty(address.AddressLine2) ? null : address.AddressLine2,
                AddressLine3 = string.IsNullOrEmpty(address.AddressLine3) ? null : address.AddressLine3,
                City = string.IsNullOrEmpty(address.City) ? null : address.City,
                Country = string.IsNullOrEmpty(address.CountryCode) ? null : address.CountryCode,
                Postcode = string.IsNullOrEmpty(address.PostalCode) ? null : address.PostalCode,
                Type = address.Type ?? 3,
                Main = address.IsMain,
                // İlgili kişi: Exact adres bloğunda adresin hemen üstünde isim olarak görünür.
                // Null ise JSON'a dahil edilmez (WhenWritingNull), diğer çağrı yerleri etkilenmez.
                Contact = address.ContactId?.ToString("D")
            };

            var json = JsonSerializer.Serialize(addressDto, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });

            _logger.LogInformation($"📤 Gönderilen JSON:\n{json}");

            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"❌ Adres oluşturma hatası: {response.StatusCode} ({response.ReasonPhrase})");
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"📋 Tam hata: {errorContent}");
                return null;
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            _logger.LogInformation($"✅ Adres API Cevabı:\n{responseContent}");

            using var doc = JsonDocument.Parse(responseContent);

            if (doc.RootElement.TryGetProperty("d", out var dElement))
            {
                var addressJson = dElement.GetRawText();

                // ID'yi al
                if (dElement.TryGetProperty("ID", out var idElement))
                {
                    var createdId = idElement.GetString();
                    _logger.LogInformation($"✅ Adres oluşturuldu. ID: {createdId}");

                    // ✅ YENİ: ID ile adresi tekrar çek
                    var fullAddress = await GetAddressById(createdId);

                    if (fullAddress != null)
                    {
                        _logger.LogInformation($"✅ Adres başarıyla oluşturuldu ve veriler çekildi: {fullAddress.AddressLine1}, {fullAddress.City}");
                        return fullAddress;
                    }
                    else
                    {
                        _logger.LogWarning($"⚠️ Adres oluşturuldu ama detaylı veriler çekilemedi. ID: {createdId}");
                        // En azından ID'yi ata
                        var minimalAddress = new ExactAddress { Id = Guid.Parse(createdId) };
                        return minimalAddress;
                    }
                }
                else
                {
                    _logger.LogWarning($"⚠️ Response'da ID bulunamadı.");
                    return null;
                }
            }
            else
            {
                _logger.LogWarning($"⚠️ Beklenilen 'd' property'si bulunamadı.");
                return null;
            }
        }
        catch (JsonException jsonEx)
        {
            _logger.LogError($"❌ JSON Parse Hatası: {jsonEx.Message}");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ CreateAddress hatası: {ex.Message}");
            return null;
        }
    }

    // ---

    /// <summary>



    private string PreProcessAddressJson(string json)
    {
        try
        {
            // Eğer "d" sarması varsa, direkt döndür
            if (json.Contains("\"d\""))
                return json;

            // Eğer sarması yoksa (örneğin sadece obje ise), "d" ekle
            return json; // Veya wrap et: $"{{\"d\":{json}}}"
        }
        catch
        {
            return json;
        }
    }

    /// Adresi günceller
    public async Task<bool> UpdateAddress(string addressId, ExactAddress address)
    {
        var exactService = _serviceProvider.GetRequiredService<ExactService>();
        var token = await exactService.GetValidToken();

        if (token == null)
        {
            _logger.LogError("Token alınamadı");
            return false;
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Addresses(guid'{addressId}')";

            // Güncellenecek alanları seç
            var updateData = new Dictionary<string, object>
            {
                ["AddressLine1"] = address.AddressLine1,
                ["AddressLine2"] = address.AddressLine2,
                ["AddressLine3"] = address.AddressLine3,
                ["City"] = address.City,
                ["Country"] = address.CountryCode,
                ["Postcode"] = address.PostalCode,
                ["Type"] = address.Type,
                ["Main"] = address.IsMain
            };

            // İlgili kişi: Exact adres bloğunda adresin hemen üstünde isim olarak görünür.
            // Yalnızca doluysa gönderilir; aksi halde Exact'teki mevcut bağlantı silinirdi.
            if (address.ContactId.HasValue)
                updateData["Contact"] = address.ContactId.Value.ToString("D");

            var json = JsonSerializer.Serialize(updateData);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PutAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"Adres güncelleme hatası: {response.StatusCode}");
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"Hata detayı: {errorContent}");
                return false;
            }

            _logger.LogInformation($"Adres başarıyla güncellendi: {addressId}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"UpdateAddress metodu hatası: {ex.Message}");
            return false;
        }
    }

    /// Adresi siler
    public async Task<bool> DeleteAddress(string addressId)
    {
        var exactService = _serviceProvider.GetRequiredService<ExactService>();
        var token = await exactService.GetValidToken();

        if (token == null)
        {
            _logger.LogError("Token alınamadı");
            return false;
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);

        try
        {
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Addresses(guid'{addressId}')";

            var response = await client.DeleteAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"Adres silme hatası: {response.StatusCode}");
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"Hata detayı: {errorContent}");
                return false;
            }

            _logger.LogInformation($"Adres başarıyla silindi: {addressId}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"DeleteAddress metodu hatası: {ex.Message}");
            return false;
        }
    }


    //müşterinin fatura adreslerini getir
    public async Task<List<ExactAddress>> GetCustomerBillingAddresses(string customerId)
    {
        var exactService = _serviceProvider.GetRequiredService<ExactService>();
        var token = await exactService.GetValidToken();

        if (token == null)
        {
            _logger.LogError("Token alınamadı");
            return new List<ExactAddress>();
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var allAddresses = new List<ExactAddress>();
            var errorAddresses = new List<string>();

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = null,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
                Converters = { new JsonStringEnumConverter() }
            };

            jsonOptions.Converters.Add(new JsonStringEnumConverter());

            // Sadece ilk sayfayı çek ($top=10) - tüm adresleri çekmeye gerek yok
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Addresses?$filter=Account eq guid'{customerId}' and Type eq 3&$top=10";

            _logger.LogInformation($"🔍 Fatura adresi sorgusu (ilk sayfa): {url}");

            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"API hatası: {response.StatusCode}");
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"Hata detayı: {errorContent}");
                return new List<ExactAddress>();
            }

            var content = await response.Content.ReadAsStringAsync();
            content = PreProcessAddressJson(content);

            using var doc = JsonDocument.Parse(content);

            if (!doc.RootElement.TryGetProperty("d", out var dElement))
            {
                _logger.LogError("'d' property bulunamadı");
                return new List<ExactAddress>();
            }

            JsonElement resultsArray;

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
                _logger.LogError("Results bulunamadı");
                return new List<ExactAddress>();
            }

            int count = 0;
            int errorCount = 0;
            int totalFromApi = resultsArray.EnumerateArray().Count();

            if (totalFromApi == 0)
            {
                _logger.LogInformation($"Müşteri ({customerId}) için fatura adresi bulunamadı");
                return new List<ExactAddress>();
            }

            foreach (var addressElement in resultsArray.EnumerateArray())
            {
                ExactAddress address = null;
                var addressJson = addressElement.GetRawText();

                try
                {
                    addressJson = PreProcessAddressJson(addressJson);
                    address = JsonSerializer.Deserialize<ExactAddress>(addressJson, jsonOptions);

                    if (address != null)
                    {
                        allAddresses.Add(address);
                        count++;
                        _logger.LogDebug($"Adres alındı: {address.Id}, Tür: {address.TypeDescription}, Şehir: {address.City}");
                    }
                }
                catch (JsonException jsonEx)
                {
                    errorCount++;
                    try
                    {
                        var errorDoc = JsonDocument.Parse(addressJson);
                        if (errorDoc.RootElement.TryGetProperty("ID", out var idProp))
                        {
                            var id = idProp.GetString();
                            if (!string.IsNullOrWhiteSpace(id))
                                errorAddresses.Add(id);
                            _logger.LogWarning($"JSON parse hatası - Adres ID: {id}");
                        }
                    }
                    catch { }
                    _logger.LogWarning($"JSON parse hatası ({errorCount}): {jsonEx.Message}");
                }
                catch (Exception ex)
                {
                    errorCount++;
                    _logger.LogError($"Genel hata: {ex.Message}");
                }
            }

            if (errorCount > 0)
            {
                _logger.LogWarning($"{count} başarılı, {errorCount} hatalı adres kaydı (API'den {totalFromApi} kayıt geldi)");
            }
            else
            {
                _logger.LogInformation($"{count} fatura adresi başarıyla alındı (ilk sayfa)");
            }

            var sortedAddresses = allAddresses
                .OrderByDescending(a => a.IsMain)
                .ThenBy(a => a.Type)
                .ToList();

            return sortedAddresses;
        }
        catch (Exception ex)
        {
            _logger.LogError($"GetCustomerBillingAddresses metodu hatası: {ex.Message}");
            return new List<ExactAddress>();
        }
    }

    //delivery address
    public async Task<List<ExactAddress>> GetCustomerDeliveryAddresses(string customerId)
    {
        var exactService = _serviceProvider.GetRequiredService<ExactService>();
        var token = await exactService.GetValidToken();

        if (token == null)
        {
            _logger.LogError("Token alınamadı");
            return new List<ExactAddress>();
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var allAddresses = new List<ExactAddress>();
            var errorAddresses = new List<string>();

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = null,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
                Converters = { new JsonStringEnumConverter() }
            };

            jsonOptions.Converters.Add(new JsonStringEnumConverter());

            // Sadece ilk sayfayı çek ($top=10) - tüm adresleri çekmeye gerek yok
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Addresses?$filter=Account eq guid'{customerId}' and Type eq 4&$top=10";

            _logger.LogInformation($"🔍 Teslimat adresi sorgusu (ilk sayfa): {url}");

            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"API hatası: {response.StatusCode}");
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"Hata detayı: {errorContent}");
                return new List<ExactAddress>();
            }

            var content = await response.Content.ReadAsStringAsync();
            content = PreProcessAddressJson(content);

            using var doc = JsonDocument.Parse(content);

            if (!doc.RootElement.TryGetProperty("d", out var dElement))
            {
                _logger.LogError("'d' property bulunamadı");
                return new List<ExactAddress>();
            }

            JsonElement resultsArray;

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
                _logger.LogError("Results bulunamadı");
                return new List<ExactAddress>();
            }

            int count = 0;
            int errorCount = 0;
            int totalFromApi = resultsArray.EnumerateArray().Count();

            if (totalFromApi == 0)
            {
                _logger.LogInformation($"Müşteri ({customerId}) için teslimat adresi bulunamadı");
                return new List<ExactAddress>();
            }

            foreach (var addressElement in resultsArray.EnumerateArray())
            {
                ExactAddress address = null;
                var addressJson = addressElement.GetRawText();

                try
                {
                    addressJson = PreProcessAddressJson(addressJson);
                    address = JsonSerializer.Deserialize<ExactAddress>(addressJson, jsonOptions);

                    if (address != null)
                    {
                        allAddresses.Add(address);
                        count++;
                        _logger.LogDebug($"Adres alındı: {address.Id}, Tür: {address.TypeDescription}, Şehir: {address.City}");
                    }
                }
                catch (JsonException jsonEx)
                {
                    errorCount++;
                    try
                    {
                        var errorDoc = JsonDocument.Parse(addressJson);
                        if (errorDoc.RootElement.TryGetProperty("ID", out var idProp))
                        {
                            var id = idProp.GetString();
                            if (!string.IsNullOrWhiteSpace(id))
                                errorAddresses.Add(id);
                            _logger.LogWarning($"JSON parse hatası - Adres ID: {id}");
                        }
                    }
                    catch { }
                    _logger.LogWarning($"JSON parse hatası ({errorCount}): {jsonEx.Message}");
                }
                catch (Exception ex)
                {
                    errorCount++;
                    _logger.LogError($"Genel hata: {ex.Message}");
                }
            }

            if (errorCount > 0)
            {
                _logger.LogWarning($"{count} başarılı, {errorCount} hatalı adres kaydı (API'den {totalFromApi} kayıt geldi)");
            }
            else
            {
                _logger.LogInformation($"{count} teslimat adresi başarıyla alındı (ilk sayfa)");
            }

            var sortedAddresses = allAddresses
                .OrderByDescending(a => a.IsMain)
                .ThenBy(a => a.Type)
                .ToList();

            return sortedAddresses;
        }
        catch (Exception ex)
        {
            _logger.LogError($"GetCustomerDeliveryAddresses metodu hatası: {ex.Message}");
            return new List<ExactAddress>();
        }
    }

    /// Adres tipi numarasını açıklamaya çevirir
    private int GetAddressTypeDescription(int type)
    {
        return type switch
        {
            1 => 1,
            2 => 2,
            3 => 3,
            4 => 4
        };
    }

    /// Adres JSON'u için ön işleme
    // private string PreProcessAddressJson(string json)
    // {
    //     // Type field'ı: sayı ise string'e çevir (JSON'da int olarak geliyor ama güvenli olsun diye)
    //     json = System.Text.RegularExpressions.Regex.Replace(
    //         json,
    //         @"""Type""\s*:\s*(\d+)",
    //         @"""Type"":""$1"""
    //     );

    //     // Main field'ı: 0 veya 1'i boolean'a çevir
    //     json = json.Replace(@"""Main"":0", @"""Main"":false");
    //     json = json.Replace(@"""Main"":1", @"""Main"":true");

    //     // AccountIsSupplier field'ı: 0 veya 1'i boolean'a çevir
    //     json = json.Replace(@"""AccountIsSupplier"":0", @"""AccountIsSupplier"":false");
    //     json = json.Replace(@"""AccountIsSupplier"":1", @"""AccountIsSupplier"":true");

    //     // FreeBoolField'lar: 0 veya 1'i boolean'a çevir
    //     for (int i = 1; i <= 5; i++)
    //     {
    //         json = json.Replace($@"""FreeBoolField_0{i}""\s*:\s*0", $@"""FreeBoolField_0{i}"":false");
    //         json = json.Replace($@"""FreeBoolField_0{i}""\s*:\s*1", $@"""FreeBoolField_0{i}"":true");
    //     }

    //     // Null string'leri temizle
    //     json = System.Text.RegularExpressions.Regex.Replace(
    //         json,
    //         @"""(\w+)""\s*:\s*""""",
    //         @"""$1"":null"
    //     );

    //     // Trim trailing spaces in Country and State codes
    //     json = System.Text.RegularExpressions.Regex.Replace(
    //         json,
    //         @"""Country""\s*:\s*""([^""]+?) """,
    //         @"""Country"":""$1"" "
    //     );

    //     json = System.Text.RegularExpressions.Regex.Replace(
    //         json,
    //         @"""State""\s*:\s*""([^""]+?) """,
    //         @"""State"":""$1"" "
    //     );

    //     return json;
    // }
}

// Address modelini de güncelleyelim
public class Address
{
    public Guid ID { get; set; }
    public Guid Account { get; set; }
    public string AccountName { get; set; }
    public string AddressLine1 { get; set; }
    public string AddressLine2 { get; set; }
    public string AddressLine3 { get; set; }
    public string City { get; set; }
    public string Country { get; set; }
    public string CountryName { get; set; }
    public string Postcode { get; set; }
    public string State { get; set; }
    public string StateDescription { get; set; }
    public int? Type { get; set; }
    public string TypeDescription { get; set; }
    public bool Main { get; set; }

    // Ek property'ler
    public string AddressType { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Modified { get; set; }
}