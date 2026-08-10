using System.Net.Http.Headers;
using System.Text.Json;
using ExactOnline.Models;
using System.Text.Json.Serialization;

namespace ShopifyProductApp.Services;

public class CrudBatchUpdateResult
{
    public int SuccessCount { get; set; }
    public int ErrorCount { get; set; }
    public List<string> UpdatedCodes { get; set; } = new();
}

public class ShopifyCustomerCrud
{
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _shopifyStoreUrl;
    private readonly ShopifyGraphQLService _graphqlService;
     private readonly ILogger<ShopifyCustomerCrud> _logger;
      private readonly IServiceProvider _serviceProvider;

    public ShopifyCustomerCrud(string shopifyStoreUrl, string accessToken, ShopifyGraphQLService graphqlService,ILogger<ShopifyCustomerCrud> logger, IServiceProvider serviceProvider)
    {
        _shopifyStoreUrl = shopifyStoreUrl.TrimEnd('/');
        _client = new HttpClient
        {
            BaseAddress = new Uri($"{_shopifyStoreUrl}/admin/api/2025-01/")
        };
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _client.DefaultRequestHeaders.Add("X-Shopify-Access-Token", accessToken);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
        _graphqlService = graphqlService;
         _logger = logger;
          _serviceProvider = serviceProvider;
    }

    // Uzun API yanıtlarını log/DB için kısaltır
    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        value = value.Replace("\n", " ").Replace("\r", " ").Trim();
        return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
    }

    /// <summary>
    /// UpdateCustomerAsync ile aynı işi yapar, ek olarak başarısızlık sebebini de döndürür.
    /// Böylece hata mesajı log dosyası yerine DB'ye (CustomerSyncLogs) yazılabilir.
    /// </summary>
    public async Task<(bool Success, string Error)> UpdateCustomerDetailedAsync(Account exactAccount, string logFilePath, bool sendWelcomeEmail = false)
    {
        var errors = new List<string>();
        void Collect(string message)
        {
            if (!string.IsNullOrWhiteSpace(message)) errors.Add(message);
        }

        var success = await UpdateCustomerAsync(exactAccount, logFilePath, sendWelcomeEmail, Collect);

        if (success) return (true, null);

        var error = errors.Count > 0
            ? string.Join(" | ", errors.Distinct())
            : "Shopify müşteri güncelleme başarısız (sebep belirlenemedi)";

        return (false, error);
    }

    // --> Webhook için tek bir müşteri güncelleme metodu

    public Task<bool> UpdateCustomerAsync(Account exactAccount, string logFilePath, bool sendWelcomeEmail = false)
        => UpdateCustomerAsync(exactAccount, logFilePath, sendWelcomeEmail, null);

    private async Task<bool> UpdateCustomerAsync(Account exactAccount, string logFilePath, bool sendWelcomeEmail, Action<string> onError)
    {
        try
        {

            var emailLower = exactAccount.Email.ToLower();
            var emailExists = await CustomerFindByEmail(exactAccount.Email);
            if (emailExists == null)
            {
                var customerCode = await _graphqlService.SearchCustomerByMetafieldCodeAsync(customerCode: exactAccount.Code.Trim());
                if (customerCode == null)
                {
                    var created = await CreateCustomerEmailAsync(exactAccount, "b2b-customer", logFilePath, sendWelcomeEmail);
                    Console.WriteLine($"⚠️ Müşteri bulunamadı Shopify'da: {exactAccount.Email}, yeni müşteri oluşturuldu (sonuç: {created}).");

                    // Müşteri yeni oluşturuldu; tüm alanlar zaten yazıldı.
                    // Devam edip PUT customers/null.json çağırmak 406 hatası verir.
                    return created;
                }
                else
                {
                    emailExists = customerCode.Id.ToString();
                }
            }

            string customerId = emailExists;
            if (string.IsNullOrWhiteSpace(customerId))
            {
                Console.WriteLine($"❌ Shopify müşteri ID'si belirlenemedi: {exactAccount.Email}");
                onError?.Invoke("Shopify müşteri ID'si bulunamadı (email ve customer_code ile eşleşme yok)");
                if (!string.IsNullOrEmpty(logFilePath))
                {
                    await AppendToLogFileAsync(logFilePath, new
                    {
                        Timestamp = DateTimeOffset.Now,
                        Action = "UpdateCustomer",
                        Email = exactAccount.Email,
                        Name = exactAccount.Name,
                        Code = exactAccount.Code,
                        Success = false,
                        Error = "Shopify müşteri ID'si bulunamadı (email ve customer_code ile eşleşme yok)",
                        ProcessType = "CustomerUpdate"
                    });
                }
                return false;
            }
            //Ülke kodunu düzenle
            var countryCode = ConvertToCountryCode(exactAccount.Country, exactAccount.CountryName);
            var validatedPhone = ValidatePhoneNumber(exactAccount.Phone);
            // ✅ Yeni adres oluştur
            var newAddress = new
            {
                address1 = exactAccount.AddressLine1 ?? "",
                address2 = exactAccount.AddressLine2 ?? "",
                city = exactAccount.City ?? "",
                province = exactAccount.StateName ?? "",
                country = countryCode,
                zip = exactAccount.Postcode ?? "",
                phone = validatedPhone ?? "",
                name = exactAccount.Name ?? "",
                company = exactAccount.Name ?? ""
            };
            var customerData = new
            {
                customer = new
                {
                    first_name = GetFirstName(exactAccount.Name),
                    last_name = GetLastName(exactAccount.Name),
                    email = exactAccount.Email ?? "",
                    phone = validatedPhone ?? "",
                    addresses = new[] { newAddress },
                    tags = $"{exactAccount.ClassificationDescription},betaling-factuur",
                    note = $"Exact Online ID: {exactAccount.ID}\nCode: {exactAccount.Code}\nVAT: {exactAccount.VATNumber ?? "N/A"}\nLast Updated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}",
                    tax_exempt = countryCode == "NL" ? false : true,
                    metafields = new[]
                    {
                    new
                    {
                        @namespace = "custom",
                        key = "customer_id",
                        value = exactAccount.ID.ToString(),
                        type = "single_line_text_field"
                    },
                    new{
                        @namespace = "custom",
                        key = "exact_discount_code",
                        value = exactAccount.ClassificationDescription,
                        type = "single_line_text_field"
                    },
                    new
                    {
                        @namespace = "custom",
                        key = "customer_code",
                        value = exactAccount.Code?.Trim() ?? "",
                        type = "single_line_text_field"
                    },
                    new
                    {
                        @namespace = "custom",
                        key = "last_synced",
                        value = DateTimeOffset.Now.ToString("O"),
                        type = "single_line_text_field"
                    }
                }
                }
            };

            var jsonContent = new StringContent(JsonSerializer.Serialize(customerData));
            jsonContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            //SHOPIFY API'YE PUT İSTEĞİ GÖNDERİ
            var response = await _client.PutAsync($"customers/{customerId}.json", jsonContent);
            var responseContent = await response.Content.ReadAsStringAsync();
            await Task.Delay(500); // Rate limit
            if (!string.IsNullOrEmpty(logFilePath))
            {
                await AppendToLogFileAsync(logFilePath, new
                {
                    Timestamp = DateTimeOffset.Now,
                    Action = "UpdateCustomer",
                    Email = exactAccount.Email,
                    Name = exactAccount.Name,
                    Code = exactAccount.Code,
                    Country = countryCode,
                    CustomerId = customerId,
                    Success = response.IsSuccessStatusCode,
                    StatusCode = (int)response.StatusCode,
                    SendWelcomeEmail = sendWelcomeEmail,
                    ProcessType = "CustomerUpdate"
                });
            }

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Hata: {responseContent}");
                onError?.Invoke($"Shopify HTTP {(int)response.StatusCode}: {Truncate(responseContent, 500)}");
                if (responseContent.Contains("phone"))
                {
                    Console.WriteLine($"❌ Müşteri güncelleme başarısız - Geçersiz telefon numarası: {validatedPhone} için, telefon numarası boş bırakılıyor.");
                    //Telefonu boşalt ve tekrar dene
                    var customerDataNoPhone = new
                    {
                        customer = new
                        {
                            first_name = GetFirstName(exactAccount.Name),
                            last_name = GetLastName(exactAccount.Name),
                            email = exactAccount.Email ?? "",
                            phone = "",
                            addresses = new[] { newAddress },
                            tags = $"{exactAccount.ClassificationDescription},betaling-factuur",
                            note = $"Exact Online ID: {exactAccount.ID}\nCode: {exactAccount.Code}\nVAT: {exactAccount.VATNumber ?? "N/A"}\nLast Updated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}",
                            tax_exempt = countryCode == "NL" ? false : true,
                            metafields = new[]
                            {
                            new
                            {
                                @namespace = "custom",
                                key = "customer_id",
                                value = exactAccount.ID.ToString(),
                                type = "single_line_text_field"
                            },
                            new
                            {
                                @namespace = "custom",
                                key = "customer_code",
                                value = exactAccount.Code?.Trim() ?? "",
                                type = "single_line_text_field"
                            },
                            new{
                        @namespace = "custom",
                        key = "exact_discount_code",
                        value = exactAccount.ClassificationDescription,
                        type = "single_line_text_field"
                    },
                            new
                            {
                                @namespace = "custom",
                                key = "last_synced",
                                value = DateTimeOffset.Now.ToString("O"),
                                type = "single_line_text_field"
                            }
                        }
                        }
                    };

                    var jsonContentNoPhone = new StringContent(JsonSerializer.Serialize(customerDataNoPhone));
                    jsonContentNoPhone.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    response = await _client.PutAsync($"customers/{customerId}.json", jsonContentNoPhone);
                    responseContent = await response.Content.ReadAsStringAsync();
                    await Task.Delay(500); // Rate limit

                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"❌ Müşteri güncelleme başarısız (telefon boş) - Status: {response.StatusCode}");
                        onError?.Invoke($"Telefonsuz tekrar denendi, yine başarısız - HTTP {(int)response.StatusCode}: {Truncate(responseContent, 500)}");
                        return false;
                    }
                    else
                    {
                        Console.WriteLine($"✅ Müşteri güncellendi (telefon boş): {exactAccount.Email}");
                        return true;
                    }
                }
                else
                {
                    Console.WriteLine($"❌ Müşteri güncelleme başarısız - Status: {response.StatusCode}");
                    return false;
                }

            }
            return true;
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("429"))
        {
            Console.WriteLine($"⏳ Rate limit hatası: {ex.Message}");
            await Task.Delay(2000);
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Kritik hata: {ex.Message}");
            return false;
        }
    }

    // --> Webhook için tek müşteri kayıt
    public async Task<bool> CreateCustomerEmailAsync(Account exactAccount, string customerTag = "b2b-customer", string logFilePath = null, bool sendWelcomeEmail = true)
    {
        //var testcustomer = await _graphqlService.SearchCustomerByMetafieldCodeAsync(exactAccount.Code.Trim());
        var customerCode = await _graphqlService.SearchCustomerByMetafieldCodeAsync(customerCode: exactAccount.Code.Trim());
        if (customerCode != null)
        {
            Console.WriteLine($"⚠️ Müşteri zaten mevcut Shopify'da: {exactAccount.Email}, oluşturma atlandı.");
            return false;
        }
        try
        {

            var countryCode = ConvertToCountryCode(exactAccount.Country, exactAccount.CountryName);
            //Telefon numarası validasyonu
            var validatedPhone = ValidatePhoneNumber(exactAccount.Phone);
            var customerData = new
            {
                customer = new
                {
                    first_name = GetFirstName(exactAccount.Name),
                    last_name = GetLastName(exactAccount.Name),
                    email = exactAccount.Email ?? "",
                    phone = validatedPhone ?? "",
                    verified_email = true,
                    tax_number = exactAccount.VATNumber ?? "",
                    send_email_welcome = sendWelcomeEmail,
                    send_email_invite = true,
                    addresses = new[]
                    {
                    new
                    {
                        address1 = exactAccount.AddressLine1 ?? "",
                        address2 = exactAccount.AddressLine2 ?? "",
                        city = exactAccount.City ?? "",
                        province = exactAccount.StateName ?? "",
                        country = countryCode,
                        zip = exactAccount.Postcode ?? "",
                        phone = validatedPhone ?? "",
                        name = exactAccount.Name ?? "",
                        company = exactAccount.Name ?? ""
                    }
                },
                    tags = $"{exactAccount.ClassificationDescription},betaling-factuur",
                    note = $"Exact Online ID: {exactAccount.ID}\nVAT: {exactAccount.VATNumber ?? "N/A"}",
                    tax_exempt = countryCode == "NL" ? false : true,
                    metafields = new[]
                    {
                    new
                    {
                        @namespace = "exact_online",
                        key = "customer_id",
                        value = exactAccount.ID.ToString(),
                        type = "single_line_text_field"
                    },
                    new
                    {
                        @namespace = "exact_online",
                        key = "customer_code",
                        value = exactAccount.Code?.Trim() ?? "",
                        type = "single_line_text_field"
                    },
                    new
                    {
                        @namespace = "custom",
                        key = "customer_code",
                        value = exactAccount.Code?.Trim() ?? "",
                        type = "single_line_text_field"
                    },
                    new
                    {
                        @namespace = "exact_online",
                        key = "vat_number",
                        value = exactAccount.VATNumber?.Trim() ?? "",
                        type = "single_line_text_field"
                    }
                }
                }
            };

            var jsonContent = new StringContent(JsonSerializer.Serialize(customerData));
            jsonContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            var response = await _client.PostAsync("customers.json", jsonContent);
            var responseContent = await response.Content.ReadAsStringAsync();

            //API çağrısından sonra delay
            await Task.Delay(500);

            if (!string.IsNullOrEmpty(logFilePath))
            {
                await AppendToLogFileAsync(logFilePath, new
                {
                    Timestamp = DateTimeOffset.Now,
                    Action = "CreateCustomer",
                    Email = exactAccount.Email,
                    Name = exactAccount.Name,
                    Code = exactAccount.Code,
                    Country = countryCode,
                    Phone = "",  // ✅ Validasyon sonrası telefon
                    OriginalPhone = exactAccount.Phone,
                    Tag = customerTag,
                    SendWelcomeEmail = sendWelcomeEmail,
                    Success = response.IsSuccessStatusCode,
                    StatusCode = (int)response.StatusCode,
                    ProcessType = "NewCustomerCreation"
                });

                Console.WriteLine($"   📝 Log kaydedildi: {logFilePath}");
            }

            if (response.IsSuccessStatusCode)
            {
                //Yeni müşteri cache'e ekle
                //_existingCustomerEmails.Add(exactAccount.Email.ToLower());
                Console.WriteLine($"✅ Müşteri başarıyla oluşturuldu: {exactAccount.Email}");
                return true;
            }
            else
            {
                // Console.WriteLine($"❌ Müşteri oluşturma başarısız - Status: {response.StatusCode}");
                // Console.WriteLine($"Hata: {responseContent}");
                // return false;
                if (responseContent.Contains("phone"))
                {
                    Console.WriteLine($"❌ Müşteri oluşturma başarısız - Geçersiz telefon numarası: {validatedPhone} için, telefon numarası boş bırakılıyor.");
                    //Telefonu boşalt ve tekrar dene
                    var customerDataNoPhone = new
                    {
                        customer = new
                        {
                            first_name = GetFirstName(exactAccount.Name),
                            last_name = GetLastName(exactAccount.Name),
                            email = exactAccount.Email ?? "",
                            phone = "",
                            verified_email = true,
                            tax_number = exactAccount.VATNumber ?? "",
                            send_email_welcome = sendWelcomeEmail,
                            send_email_invite = true,
                            addresses = new[]
                            {
                            new
                            {
                                address1 = exactAccount.AddressLine1 ?? "",
                                address2 = exactAccount.AddressLine2 ?? "",
                                city = exactAccount.City ?? "",
                                province = exactAccount.StateName ?? "",
                                country = countryCode,
                                zip = exactAccount.Postcode ?? "",
                                phone = "",
                                name = exactAccount.Name ?? "",
                                company = exactAccount.Name ?? ""
                            }
                        },
                            tags = $"{exactAccount.ClassificationDescription},betaling-factuur",
                            note = $"Exact Online ID: {exactAccount.ID}\nVAT: {exactAccount.VATNumber ?? "N/A"}",
                            tax_exempt = countryCode == "NL" ? false : true,
                            metafields = new[]
                            {
                            new
                            {
                                @namespace = "exact_online",
                                key = "customer_id",
                                value = exactAccount.ID.ToString(),
                                type = "single_line_text_field"
                            },
                            new
                            {
                                @namespace = "custom",
                                key = "customer_code",
                                value = exactAccount.Code?.Trim() ?? "",
                                type = "single_line_text_field"
                            },
                            new
                            {
                                @namespace = "exact_online",
                                key = "customer_code",
                                value = exactAccount.Code?.Trim() ?? "",
                                type = "single_line_text_field"
                            },
                            new
                            {
                                @namespace = "exact_online",
                                key = "vat_number",
                                value = exactAccount.VATNumber?.Trim() ?? "",
                                type = "single_line_text_field"
                            }
                        }
                        }
                    };

                    var jsonContentNoPhone = new StringContent(JsonSerializer.Serialize(customerDataNoPhone));
                    jsonContentNoPhone.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    response = await _client.PostAsync("customers.json", jsonContentNoPhone);
                    responseContent = await response.Content.ReadAsStringAsync();
                    await Task.Delay(500); // Rate limit
                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"❌ Müşteri güncelleme başarısız (telefon boş) - Status: {response.StatusCode}");
                        return false;
                    }
                    else
                    {
                        Console.WriteLine($"✅ Müşteri güncellendi (telefon boş): {exactAccount.Email}");
                        return true;
                    }
                }
                else
                {
                    Console.WriteLine($"❌ Müşteri oluşturma başarısız - Status: {response.StatusCode}");
                    Console.WriteLine($"Hata: {responseContent}");
                    return false;
                }
            }
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("429"))
        {
            Console.WriteLine($"   ⏳ Rate limit hatası: {ex.Message}");
            Console.WriteLine($"   ⏳ 2 saniye bekleniyor...");
            await Task.Delay(2000);
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Kritik hata: {ex.Message}");
            return false;
        }
    }



    // Eski Tüm müşteri email'lerini ve ID'lerini al
    private async Task<Dictionary<string, string>> GetAllCustomerEmailsWithIdAsync()
    {
        var emailIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string nextPageCursor = null;
        int pageCount = 0;

        try
        {
            while (true)
            {
                pageCount++;
                Console.WriteLine($"   📄 Sayfa {pageCount} yükleniyor...");

                // GraphQL sorgusu ile tüm müşterileri pagination ile al
                var query = @"
            {
                customers(first: 250" + (string.IsNullOrEmpty(nextPageCursor) ? "" : $", after: \"{nextPageCursor}\"") + @") {
                    edges {
                        node {
                            id
                            email
                        }
                    }
                    pageInfo {
                        hasNextPage
                        endCursor
                    }
                }
            }";

                var graphqlRequest = new
                {
                    query = query
                };

                var jsonContent = new StringContent(JsonSerializer.Serialize(graphqlRequest));
                jsonContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                var response = await _client.PostAsync("graphql.json", jsonContent);

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"❌ Müşteri listesi alınamadı: {response.StatusCode}");
                    return emailIdMap;
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);

                // Response'daki emails ve ID'leri Dictionary'e ekle
                if (jsonResponse.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("customers", out var customers))
                {
                    if (customers.TryGetProperty("edges", out var edges))
                    {
                        foreach (var edge in edges.EnumerateArray())
                        {
                            if (edge.TryGetProperty("node", out var node))
                            {
                                if (node.TryGetProperty("email", out var email) &&
                                    node.TryGetProperty("id", out var id))
                                {
                                    var emailValue = email.GetString()?.ToLower();
                                    var idValue = id.GetString();

                                    if (!string.IsNullOrEmpty(emailValue) && !string.IsNullOrEmpty(idValue))
                                    {
                                        // ID'yi sadeleştir (gid://shopify/Customer/123456 -> 123456)
                                        var customerId = idValue.Split('/').Last();
                                        emailIdMap[emailValue] = customerId;
                                    }
                                }
                            }
                        }
                    }

                    // Sonraki sayfa kontrolü
                    var hasNextPage = false;
                    var endCursor = "";

                    if (customers.TryGetProperty("pageInfo", out var pageInfo))
                    {
                        if (pageInfo.TryGetProperty("hasNextPage", out var hasNextPageElement))
                        {
                            hasNextPage = hasNextPageElement.GetBoolean();
                        }

                        if (hasNextPage && pageInfo.TryGetProperty("endCursor", out var cursor))
                        {
                            endCursor = cursor.GetString() ?? "";
                        }
                    }

                    if (hasNextPage && !string.IsNullOrEmpty(endCursor))
                    {
                        nextPageCursor = endCursor;
                    }
                    else
                    {
                        break; // Tüm sayfalar yüklendi
                    }
                }

                // API rate limit'i aşmamak için bekle
                await Task.Delay(300);
            }

            Console.WriteLine($"   ✅ Toplam {emailIdMap.Count} müşteri email->ID mapping yüklendi ({pageCount} sayfa)");
            return emailIdMap;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Müşteri listesi yüklenirken hata: {ex.Message}");
            return emailIdMap;
        }
    }


    private string ValidatePhoneNumber(string phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
            return null;

        // Sadece rakamları al
        var digitsOnly = System.Text.RegularExpressions.Regex.Replace(phoneNumber, @"[^\d]", "");
        string cleaned = phoneNumber.Trim();
        if (digitsOnly.Length < 5)
        {
            return "";
        }
        else if (!cleaned.StartsWith("0") && digitsOnly.Length < 5)
        {
            return "0" + digitsOnly;
        }
        else if (digitsOnly.Length > 17)
        {
            digitsOnly = digitsOnly.Substring(1, 12);
            return digitsOnly;
        }
        else
        {
            return digitsOnly;
        }

    }

    private string DetectCountryCode(string digitsOnly)
    {
        // Avrupa ülke kodları
        var europeanCodes = new Dictionary<string, string>
    {
        // Batı Avrupa
        { "31", "+31" },   // Hollanda
        { "33", "+33" },   // Fransa
        { "32", "+32" },   // Belçika
        { "49", "+49" },   // Almanya
        { "43", "+43" },   // Avusturya
        { "41", "+41" },   // İsviçre
        
        // Kuzey Avrupa
        { "44", "+44" },   // İngiltere
        { "45", "+45" },   // Danimarka
        { "46", "+46" },   // İsveç
        { "47", "+47" },   // Norveç
        { "358", "+358" }, // Finlandiya
        
        // Güney Avrupa
        { "34", "+34" },   // İspanya
        { "39", "+39" },   // İtalya
        { "351", "+351" }, // Portekiz
        { "30", "+30" },   // Yunanistan
        
        // Doğu Avrupa
        { "48", "+48" },   // Polonya
        { "420", "+420" }, // Çek Cumhuriyeti
        { "421", "+421" }, // Slovakya
        { "36", "+36" },   // Macaristan
        { "40", "+40" },   // Romanya
        { "359", "+359" }, // Bulgaristan
        
        // Diğer
        { "90", "+90" },   // Türkiye
        { "212", "+212" }, // Fas
        { "213", "+213" }, // Cezayir
    };

        // En uzun maç ilk kontrol et (358 için, 35 olmasın diye)
        var matchedCode = europeanCodes
            .OrderByDescending(x => x.Key.Length)
            .FirstOrDefault(x => digitsOnly.StartsWith(x.Key));

        return matchedCode.Value ?? "+31"; // Varsayılan Hollanda
    }



    //  Ülke isimlerini 2 harfli koda çevir
    private string ConvertToCountryCode(string countryCode, string countryName)
    {
        // Eğer zaten 2 harfli kod varsa, temizle ve kullan
        if (!string.IsNullOrEmpty(countryCode))
        {
            var cleaned = countryCode.Trim().ToUpper();
            if (cleaned.Length == 2)
                return cleaned;
        }

        // Ülke ismine göre kod döndür
        if (string.IsNullOrEmpty(countryName))
            return "NL"; // Varsayılan

        var name = countryName.Trim().ToLower();

        // Yaygın ülkeler
        return name switch
        {
            "the netherlands" or "netherlands" or "holland" => "NL",
            "belgium" or "belgie" or "belgique" => "BE",
            "germany" or "deutschland" => "DE",
            "france" => "FR",
            "united kingdom" or "uk" or "great britain" => "GB",
            "spain" or "españa" => "ES",
            "italy" or "italia" => "IT",
            "portugal" => "PT",
            "poland" or "polska" => "PL",
            "austria" or "österreich" => "AT",
            "switzerland" or "schweiz" or "suisse" => "CH",
            "denmark" or "danmark" => "DK",
            "sweden" or "sverige" => "SE",
            "norway" or "norge" => "NO",
            "finland" or "suomi" => "FI",
            "ireland" or "éire" => "IE",
            "greece" or "hellas" => "GR",
            "czech republic" or "czechia" => "CZ",
            "hungary" or "magyarország" => "HU",
            "romania" or "românia" => "RO",
            "bulgaria" or "българия" => "BG",
            "croatia" or "hrvatska" => "HR",
            "turkey" or "türkiye" => "TR",
            "united states" or "usa" or "united states of america" => "US",
            "canada" => "CA",
            "australia" => "AU",
            "new zealand" => "NZ",
            "japan" or "日本" => "JP",
            "china" or "中国" => "CN",
            "south korea" or "korea" => "KR",
            "india" => "IN",
            "brazil" or "brasil" => "BR",
            "mexico" or "méxico" => "MX",
            "argentina" => "AR",
            "chile" => "CL",
            "south africa" => "ZA",
            _ => "NL" // Bilinmeyen ülkeler için varsayılan Hollanda
        };
    }


    public async Task<string> CustomerFindByEmail(string email)
    {
        try
        {
            if (string.IsNullOrEmpty(email))
                return null;

            var response = await _client.GetAsync($"customers/search.json?query=email:{email}");
            if (!response.IsSuccessStatusCode)
                return null;

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);

            if (doc.RootElement.TryGetProperty("customers", out var customers))
            {
                if (customers.GetArrayLength() > 0)
                {
                    //  İlk müşteriyi al ve ID'sini döndür
                    var customerId = customers[0].GetProperty("id").ToString();
                    return customerId;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ⚠️ Müşteri arama hatası: {ex.Message}");
            return null;
        }
    }

    private string GetFirstName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName))
            return "";

        var parts = fullName.Trim().Split(' ', 2);
        return parts[0];
    }

    private string GetLastName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName))
            return "";

        var parts = fullName.Trim().Split(' ', 2);
        return parts.Length > 1 ? parts[1] : "";
    }

    // 3. Rate limit tracker sınıfı
    public class RateLimitTracker
    {
        private readonly Queue<DateTime> _requestTimes = new Queue<DateTime>();
        private readonly int _maxRequestsPerSecond = 2; // Shopify limit'i 4/sec, güvenli olmak için 2 kullanıyoruz
        private readonly object _lock = new object();

        public async Task WaitIfNeededAsync()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;

                // 1 saniyeden eski requestleri temizle
                while (_requestTimes.Count > 0 && (now - _requestTimes.Peek()).TotalSeconds >= 1)
                {
                    _requestTimes.Dequeue();
                }

                // Rate limit kontrolü
                if (_requestTimes.Count >= _maxRequestsPerSecond)
                {
                    var oldestRequest = _requestTimes.Peek();
                    var waitTime = TimeSpan.FromSeconds(1) - (now - oldestRequest);

                    if (waitTime.TotalMilliseconds > 0)
                    {
                        Console.WriteLine($"⏳ Rate limit koruması - {waitTime.TotalMilliseconds:F0}ms bekleniyor");
                        Task.Delay(waitTime).Wait();
                    }
                }

                _requestTimes.Enqueue(DateTime.UtcNow);
            }
        }
    }


    //  Yeni helper metod ekleyin (class içinde)
    private async Task AppendToLogFileAsync(string logFilePath, object logData)
    {
        var logDirectory = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrEmpty(logDirectory) && !Directory.Exists(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);
        }

        // Mevcut logları oku
        List<object> allLogs = new List<object>();
        if (File.Exists(logFilePath))
        {
            var existingContent = await File.ReadAllTextAsync(logFilePath);
            if (!string.IsNullOrEmpty(existingContent))
            {
                try
                {
                    allLogs = JsonSerializer.Deserialize<List<object>>(existingContent) ?? new List<object>();
                }
                catch
                {
                    allLogs = new List<object>();
                }
            }
        }

        // Yeni log'u ekle
        allLogs.Add(logData);

        // Tüm listeyi JSON'a çevir ve kaydet
        var finalLogJson = JsonSerializer.Serialize(allLogs, _jsonOptions);
        await File.WriteAllTextAsync(logFilePath, finalLogJson);
    }


    //  public async Task<bool> SyncCustomerAddressesFromExact(string email)
    //     {
    //         try
    //         {
    //             _logger.LogInformation($"📧 Müşteri senkronizasyonu başlıyor: {email}");

    //             // 1. Shopify'da müşteriyi bul
    //             var shopifyCustomerId = await CustomerFindByEmailNew(email);
    //             if (string.IsNullOrEmpty(shopifyCustomerId.Id.ToString()))
    //             {
    //                 _logger.LogWarning($"❌ Shopify'da müşteri bulunamadı: {email}");
    //                 return false;
    //             }

    //             _logger.LogInformation($"✅ Shopify müşterisi bulundu: {shopifyCustomerId}");

    //             // 2. Shopify'dan müşteri ID'sini al (tam ID gerekli)
                

    //             // 3. Exact'tan adresler listesini çek
    //             var exactService = _serviceProvider.GetRequiredService<ExactService>();
    //             var exactAddressService = _serviceProvider.GetRequiredService<ExactAddressCrud>();
    //             var exactAddresses = await exactAddressService.GetCustomerAddresses(shopifyCustomerId.ExactCustomerId);
                
    //             if (exactAddresses == null || exactAddresses.Count == 0)
    //             {
    //                 _logger.LogWarning($"❌ Exact'ta adres bulunamadı");
    //                 return false;
    //             }

    //             _logger.LogInformation($"✅ Exact'tan {exactAddresses.Count} adres alındı");

    //             // 4. Metafield'ları oluştur (varsa güncelle)
    //             await CreateOrUpdateAddressMetafields();

    //             // 5. Her bir adresi Shopify'a ekle
    //             var successCount = 0;
    //             foreach (var exactAddress in exactAddresses)
    //             {
    //                 var result = await AddAddressToShopifyCustomer(
    //                     shopifyCustomerId.Id.ToString(),
    //                     exactAddress);

    //                 if (result)
    //                     successCount++;
    //             }

    //             _logger.LogInformation($"✅ {successCount}/{exactAddresses.Count} adres başarıyla eklendi");

    //             return successCount > 0;
    //         }
    //         catch (Exception ex)
    //         {
    //             _logger.LogError($"❌ Senkronizasyon hatası: {ex.Message}");
    //             return false;
    //         }
    //     }

    //     /// <summary>
    //     /// Shopify'da müşteriyi email ile bulur
    //     /// </summary>
    //     private async Task<ShopifyCustomerDto> CustomerFindByEmailNew(string email)
    //     {
    //         try
    //         {
    //             if (string.IsNullOrEmpty(email))
    //                 return null;
    //              var response = await _client.GetAsync($"customers/search.json?query=email:{email}");
                
    //             if (!response.IsSuccessStatusCode)
    //                 return null;

    //             var content = await response.Content.ReadAsStringAsync();
    //             using var doc = JsonDocument.Parse(content);

    //             if (doc.RootElement.TryGetProperty("customers", out var customers))
    //             {
    //                 if (customers.GetArrayLength() > 0)
    //                 {
    //                      var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    //                     var shopifyCustomer = JsonSerializer.Deserialize<ShopifyCustomerDto>(
    //                     customers.GetRawText(), options);
    //                     return shopifyCustomer;
    //                     // var customerId = customers[0].GetProperty("id").GetInt64().ToString();
    //                     // return customerId;
    //                 }
    //             }

    //             return null;
    //         }
    //         catch (Exception ex)
    //         {
    //             _logger.LogError($"❌ Müşteri arama hatası: {ex.Message}");
    //             return null;
    //         }
    //     }

    //     /// <summary>
    //     /// Shopify'a adres ekler ve exactId metafield'ını atar
    //     /// </summary>
    //     private async Task<bool> AddAddressToShopifyCustomer(
    //         string shopifyCustomerId,
    //         ExactAddress exactAddress)
    //     {
    //         try
    //         {
                
    //             // Exact adresini Shopify formatına dönüştür
    //             var shopifyAddress = new
    //             {
    //                 address = new
    //                 {
    //                     first_name = exactAddress.ContactName ?? "",
    //                     last_name = exactAddress.Surname ?? "",
    //                     company = exactAddress.AddressLine2 ?? "",
    //                     address1 = exactAddress.Street ?? "",
    //                     address2 = exactAddress.HouseNumberSuffix ?? "",
    //                     city = exactAddress.City ?? "",
    //                     province = exactAddress.State ?? "",
    //                     country = exactAddress.Country ?? "",
    //                     zip = exactAddress.PostalCode ?? "",
    //                     phone = exactAddress.PhoneNumber ?? "",
    //                     province_code = exactAddress.State ?? ""
    //                 }
    //             };

    //             var json = JsonSerializer.Serialize(shopifyAddress);
    //             var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

    //             var response = await _client.PostAsync(
    //                 $"{_shopifyBaseUrl}/admin/api/2024-01/customers/{shopifyCustomerId}/addresses.json",
    //                 content);

    //             if (!response.IsSuccessStatusCode)
    //             {
    //                 _logger.LogWarning($"⚠️ Adres ekleme başarısız: {response.StatusCode}");
    //                 return false;
    //             }

    //             var responseContent = await response.Content.ReadAsStringAsync();
    //             using var doc = JsonDocument.Parse(responseContent);

    //             if (doc.RootElement.TryGetProperty("customer_address", out var address))
    //             {
    //                 var addressId = address.GetProperty("id").GetInt64().ToString();

    //                 // Metafield'ı ata
    //                 var metafieldsResult = await SetAddressMetafield(
    //                     shopifyCustomerId,
    //                     addressId,
    //                     exactAddress.ID);

    //                 if (metafieldsResult)
    //                 {
    //                     _logger.LogInformation(
    //                         $"✅ Adres eklendi: {exactAddress.City} (Exact ID: {exactAddress.ID})");
    //                     return true;
    //                 }
    //             }

    //             return false;
    //         }
    //         catch (Exception ex)
    //         {
    //             _logger.LogError($"❌ Adres ekleme hatası: {ex.Message}");
    //             return false;
    //         }
    //     }

    //     /// <summary>
    //     /// Adres metafield'larını oluşturur veya günceller
    //     /// </summary>
    //     private async Task CreateOrUpdateAddressMetafields()
    //     {
    //         try
    //         {
    //             var client = _httpClientFactory.CreateClient();
    //             var auth = Convert.ToBase64String(
    //                 System.Text.Encoding.ASCII.GetBytes($"{_shopifyApiKey}:{_shopifyApiPassword}"));
                
    //             client.DefaultRequestHeaders.Authorization = 
    //                 new AuthenticationHeaderValue("Basic", auth);

    //             // exactId metafield'ını oluştur
    //             var metafield = new
    //             {
    //                 metafield = new {
    //                     "namespace" = "custom",
    //                     "key" = "exactId",
    //                     "value" = "",
    //                     "type" = "single_line_text",
    //                     "description" = "Exact sistemindeki adres ID'si"
    //                 }
    //             };

    //             var json = JsonSerializer.Serialize(metafield);
    //             var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

    //             var response = await client.PostAsync(
    //                 $"{_shopifyBaseUrl}/admin/api/2024-01/customer_address_metafields.json",
    //                 content);

    //             // 409 = Zaten var (bok sorun değil)
    //             if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Conflict)
    //             {
    //                 _logger.LogInformation("✅ Metafield hazır");
    //                 return;
    //             }

    //             _logger.LogWarning($"⚠️ Metafield oluşturma başarısız: {response.StatusCode}");
    //         }
    //         catch (Exception ex)
    //         {
    //             _logger.LogError($"❌ Metafield hatası: {ex.Message}");
    //         }
    //     }

    //     /// <summary>
    //     /// Adrese exactId metafield'ını atar
    //     /// </summary>
    //     private async Task<bool> SetAddressMetafield(
    //         string customerId,
    //         string addressId,
    //         string exactId)
    //     {
    //         try
    //         {
    //             var client = _httpClientFactory.CreateClient();
    //             var auth = Convert.ToBase64String(
    //                 System.Text.Encoding.ASCII.GetBytes($"{_shopifyApiKey}:{_shopifyApiPassword}"));
                
    //             client.DefaultRequestHeaders.Authorization = 
    //                 new AuthenticationHeaderValue("Basic", auth);

    //             var metafield = new
    //             {
    //                 metafield = new
    //                 {
    //                     "namespace" = "custom",
    //                     "key" = "exactId",
    //                     "value" = exactId,
    //                     "type" = "single_line_text"
    //                 }
    //             };

    //             var json = JsonSerializer.Serialize(metafield);
    //             var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

    //             var response = await client.PostAsync(
    //                 $"{_shopifyBaseUrl}/admin/api/2024-01/customer_addresses/{addressId}/metafields.json",
    //                 content);

    //             return response.IsSuccessStatusCode;
    //         }
    //         catch (Exception ex)
    //         {
    //             _logger.LogError($"❌ Metafield atama hatası: {ex.Message}");
    //             return false;
    //         }
    //     }

    

    //tüm aktarım kodları ihtiyaç halinde buraya eklenebilir
    //tüm aktarım için
    //private Dictionary<string, string> _customerEmailIdMap;
    // private bool _customersLoaded = false;
    // private HashSet<string> _existingCustomerEmails;
    // private bool _customersLoaded = false;
    //Cache'i sıfırlamak için metod (gerekirse)
    // public void ResetCustomerCache()
    // {
    //     _existingCustomerEmails = null;
    //     _customersLoaded = false;
    //     Console.WriteLine("🔄 Müşteri cache'i sıfırlandı");
    // }
    //// Tüm müşteri aktarımı için eski sadece tek seferlik çalışan metod
    // public async Task<bool> CreateCustomerAsync(Account exactAccount, string customerTag = "b2b-customer", string logFilePath = null, bool sendWelcomeEmail = true)
    // {
    //     try
    //     {
    //         Console.WriteLine($"🆕 Yeni müşteri oluşturuluyor: Email={exactAccount.Email}");

    //         // ✅ İlk çağrıda tüm müşterileri bir kez yükle
    //         if (!_customersLoaded)
    //         {
    //             Console.WriteLine($"📥 Shopify'dan tüm müşteriler yükleniyor...");
    //             //_existingCustomerEmails = await GetAllCustomerEmailsAsync();
    //             _customersLoaded = true;
    //             //Console.WriteLine($"✅ {_existingCustomerEmails.Count} müşteri yüklendi");
    //             await Task.Delay(500);
    //         }

    //         // ✅ Bellekte kontrol et (çok hızlı)
    //         var emailExists = _existingCustomerEmails.Contains(exactAccount.Email.ToLower());

    //         if (emailExists)
    //         {
    //             Console.WriteLine($"⚠️ Bu email zaten mevcut, müşteri oluşturulmadı: {exactAccount.Email}");

    //             if (!string.IsNullOrEmpty(logFilePath))
    //             {
    //                 await AppendToLogFileAsync(logFilePath, new
    //                 {
    //                     Timestamp = DateTimeOffset.Now,
    //                     Action = "CreateCustomer_Skipped",
    //                     Email = exactAccount.Email,
    //                     Name = exactAccount.Name,
    //                     Reason = "Email already exists in Shopify",
    //                     ProcessType = "NewCustomerCreation"
    //                 });

    //                 Console.WriteLine($"   📝 Log kaydedildi: {logFilePath}");
    //             }

    //             return false;
    //         }

    //         Console.WriteLine($" Email mevcut değil, yeni müşteri oluşturulacak");
    //         Console.WriteLine($"   📧 Hoşgeldin emaili: {(sendWelcomeEmail ? "GÖNDERİLECEK" : "GÖNDERİLMEYECEK")}");

    //         var countryCode = ConvertToCountryCode(exactAccount.Country, exactAccount.CountryName);
    //         Console.WriteLine($"   🌍 Ülke: {exactAccount.CountryName} → {countryCode}");

    //         var customerData = new
    //         {
    //             customer = new
    //             {
    //                 first_name = GetFirstName(exactAccount.Name),
    //                 last_name = GetLastName(exactAccount.Name),
    //                 email = exactAccount.Email ?? "",
    //                 phone = "",
    //                 verified_email = true,
    //                 tax_number = exactAccount.VATNumber ?? "",
    //                 send_email_welcome = sendWelcomeEmail,
    //                 send_email_invite = true,
    //                 addresses = new[]
    //                 {
    //                 new
    //                 {
    //                     address1 = exactAccount.AddressLine1 ?? "",
    //                     address2 = exactAccount.AddressLine2 ?? "",
    //                     city = exactAccount.City ?? "",
    //                     province = exactAccount.StateName ?? "",
    //                     country = countryCode,
    //                     zip = exactAccount.Postcode ?? "",
    //                     phone =  "",
    //                     name = exactAccount.Name ?? "",
    //                     company = exactAccount.Name ?? ""
    //                 }
    //             },
    //                 tags = $"{exactAccount.ClassificationDescription},betaling-factuur",
    //                 note = $"Exact Online ID: {exactAccount.ID}\nVAT: {exactAccount.VATNumber ?? "N/A"}",
    //                 tax_exempt = countryCode == "NL" ? false : true,
    //                 metafields = new[]
    //                 {
    //                 new
    //                 {
    //                     @namespace = "exact_online",
    //                     key = "customer_id",
    //                     value = exactAccount.ID.ToString(),
    //                     type = "single_line_text_field"
    //                 },
    //                 new
    //                 {
    //                     @namespace = "exact_online",
    //                     key = "customer_code",
    //                     value = exactAccount.Code?.Trim() ?? "",
    //                     type = "single_line_text_field"
    //                 },
    //                 new
    //                 {
    //                     @namespace = "exact_online",
    //                     key = "vat_number",
    //                     value = exactAccount.VATNumber?.Trim() ?? "",
    //                     type = "single_line_text_field"
    //                 }
    //             }
    //             }
    //         };

    //         var jsonContent = new StringContent(JsonSerializer.Serialize(customerData));
    //         jsonContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

    //         Console.WriteLine($"   📤 Shopify API'ye istek gönderiliyor...");
    //         Console.WriteLine($"   🏷️  Tag: {customerTag}");

    //         var response = await _client.PostAsync("customers.json", jsonContent);
    //         var responseContent = await response.Content.ReadAsStringAsync();

    //         // ✅ API çağrısından sonra delay
    //         await Task.Delay(500);

    //         if (!string.IsNullOrEmpty(logFilePath))
    //         {
    //             await AppendToLogFileAsync(logFilePath, new
    //             {
    //                 Timestamp = DateTimeOffset.Now,
    //                 Action = "CreateCustomer",
    //                 Email = exactAccount.Email,
    //                 Name = exactAccount.Name,
    //                 Code = exactAccount.Code,
    //                 Country = countryCode,
    //                 Tag = customerTag,
    //                 SendWelcomeEmail = sendWelcomeEmail,
    //                 Success = response.IsSuccessStatusCode,
    //                 StatusCode = (int)response.StatusCode,
    //                 ProcessType = "NewCustomerCreation"
    //             });

    //             Console.WriteLine($"   📝 Log kaydedildi: {logFilePath}");
    //         }

    //         if (response.IsSuccessStatusCode)
    //         {
    //             // ✅ Yeni müşteri cache'e ekle
    //             _existingCustomerEmails.Add(exactAccount.Email.ToLower());

    //             Console.WriteLine($"✅ Müşteri başarıyla oluşturuldu: {exactAccount.Email}");
    //             return true;
    //         }
    //         else
    //         {
    //             Console.WriteLine($"❌ Müşteri oluşturma başarısız - Status: {response.StatusCode}");
    //             Console.WriteLine($"Hata: {responseContent}");
    //             return false;
    //         }
    //     }
    //     catch (HttpRequestException ex) when (ex.Message.Contains("429"))
    //     {
    //         Console.WriteLine($"   ⏳ Rate limit hatası: {ex.Message}");
    //         Console.WriteLine($"   ⏳ 2 saniye bekleniyor...");
    //         await Task.Delay(2000);
    //         return false;
    //     }
    //     catch (Exception ex)
    //     {
    //         Console.WriteLine($"❌ Kritik hata: {ex.Message}");
    //         return false;
    //     }
    // }

   

    // DTO'lar
  

    // public class ShopifyCustomerDto
    // {
    //     [JsonPropertyName("id")]
    //     public long Id { get; set; }

    //     [JsonPropertyName("email")]
    //     public string Email { get; set; }

    //     [JsonPropertyName("first_name")]
    //     public string FirstName { get; set; }

    //     [JsonPropertyName("last_name")]
    //     public string LastName { get; set; }

    //     [JsonPropertyName("exact_customer_id")]
    //     public string ExactCustomerId { get; set; }
    // }

}
