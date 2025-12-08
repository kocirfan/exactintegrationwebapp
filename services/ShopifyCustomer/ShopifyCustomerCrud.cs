using System.Net.Http.Headers;
using System.Text.Json;
using ExactOnline.Models;

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

    public ShopifyCustomerCrud(string shopifyStoreUrl, string accessToken)
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
    }

    // --> Webhook için tek bir müşteri güncelleme metodu

    public async Task<bool> UpdateCustomerAsync(Account exactAccount, string logFilePath, bool sendWelcomeEmail = false)
    {
        try
        {
            var emailLower = exactAccount.Email.ToLower();
            var emailExists = await CustomerFindByEmail(exactAccount.Email);
            if (emailExists == null)
            {
                await CreateCustomerEmailAsync(exactAccount, "b2b-customer", logFilePath, sendWelcomeEmail);
                Console.WriteLine($"⚠️ Müşteri bulunamadı Shopify'da: {exactAccount.Email}, yeni müşteri oluşturuldu.");
                //return false;
            }
            string customerId = emailExists;
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
                        @namespace = "exact_online",
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
                                @namespace = "exact_online",
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

        if (digitsOnly.Length < 5 || digitsOnly.Length > 17)
        {
            return "";
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


    private async Task<string> CustomerFindByEmail(string email)
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


}
