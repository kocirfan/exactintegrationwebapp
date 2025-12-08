using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using ShopifyProductApp.Services;
using System.Text;
using ExactOnline.Models;
using ExactOnline.Converters;

public class ExactCustomerCrud
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


    public ExactCustomerCrud(
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

    /// Son 24 saatte güncellenen müşterileri getirir
    public async Task<List<Account>> GetAllUpdateCustomersAsync(int hours = 24)
    {
        var exactService = _serviceProvider.GetRequiredService<ExactService>();
        var token = await exactService.GetValidToken();

        if (token == null)
        {
            _logger.LogError("Token alınamadı");
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
            var cutoffDate = DateTime.UtcNow.AddHours(-hours);
            var dateFilter = cutoffDate.ToString("yyyy-MM-ddTHH:mm:ss");
            int skip = 0;
            int top = 60;
            bool hasMore = true;
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
                var url = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Accounts?$filter=Modified ge datetime'{dateFilter}'&$top={top}&$skip={skip}";
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
            if (errorEmails.Count > 0)
            {
                var errorJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    errorCount = errorEmails.Count,
                    emails = errorEmails.Distinct().ToList()
                }, new JsonSerializerOptions { WriteIndented = true });
            }

            return allCustomers;
        }
        catch (Exception ex)
        {
            return new List<Account>();
        }
    }

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
}