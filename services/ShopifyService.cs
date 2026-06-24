using System.Net.Http.Headers;
using System.Text.Json;
using ExactOnline.Models;

namespace ShopifyProductApp.Services;

public class BatchUpdateResult
{
    public int SuccessCount { get; set; }
    public int ErrorCount { get; set; }
    public List<string> UpdatedCodes { get; set; } = new();
}

public class ShopifyService
{
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _shopifyStoreUrl;

    public ShopifyService(string shopifyStoreUrl, string accessToken)
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




    //müşterileri aktar
    //  Bu satırı ekleyin
    // ✅ Cache: Email -> Customer ID mapping
    private Dictionary<string, string> _customerEmailIdMap;
    // private bool _customersLoaded = false;
    private HashSet<string> _existingCustomerEmails;
    private bool _customersLoaded = false;
    // ✅ Cache: Email -> Customer ID mapping


    public async Task<bool> UpdateCustomerAsync(Account exactAccount, string logFilePath, bool sendWelcomeEmail = true)
    {
        try
        {
            // ✅ İlk çağrıda tüm müşterileri ve ID'lerini yükle
            if (!_customersLoaded)
            {
                Console.WriteLine($"📥 Shopify'dan tüm müşteriler yükleniyor (Email -> ID mapping)...");
                _customerEmailIdMap = await GetAllCustomerEmailsWithIdAsync();
                _customersLoaded = true;
                Console.WriteLine($"✅ {_customerEmailIdMap.Count} müşteri yüklendi");
                await Task.Delay(500);
            }

            // ✅ Email'den customer ID'yi hemen al (cache'den, API sorgusu yok!)
            var emailLower = exactAccount.Email.ToLower();


            if (!_customerEmailIdMap.ContainsKey(emailLower))
            {
                Console.WriteLine($"⚠️ Müşteri cache'de bulunamadı: {exactAccount.Email}");
                return false;
            }

            string customerId = _customerEmailIdMap[emailLower];
            Console.WriteLine($"✅ Müşteri ID bulundu: {emailLower} -> {customerId}");

            // ✅ Ülke kodunu düzenle
            var countryCode = ConvertToCountryCode(exactAccount.Country, exactAccount.CountryName);

            // ✅ Yeni adres oluştur
            var newAddress = new
            {
                address1 = exactAccount.AddressLine1 ?? "",
                address2 = exactAccount.AddressLine2 ?? "",
                city = exactAccount.City ?? "",
                province = exactAccount.StateName ?? "",
                country = countryCode,
                zip = exactAccount.Postcode ?? "",
                phone = exactAccount.Phone ?? "",
                name = exactAccount.Name ?? "",
                company = exactAccount.Name ?? ""
            };

            // ✅ Güncellenecek müşteri verisi
            // ❌ verified_email, send_email_welcome, send_email_invite UPDATE'de gönderilmemeli!
            // ✅ Mailler ayrı GraphQL mutation ile gönderilecek
            var customerData = new
            {
                customer = new
                {
                    first_name = GetFirstName(exactAccount.Name),
                    last_name = GetLastName(exactAccount.Name),
                    email = exactAccount.Email ?? "",
                    phone = exactAccount.Phone ?? "",
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

            // ✅ SHOPIFY API'YE PUT İSTEĞİ GÖNDERİ
            Console.WriteLine($"📤 Shopify'a güncelleme isteği gönderiliyor: {customerId}");
            var response = await _client.PutAsync($"customers/{customerId}.json", jsonContent);
            var responseContent = await response.Content.ReadAsStringAsync();

            await Task.Delay(500); // Rate limit

            // ✅ Log dosyasına kaydet
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
                Console.WriteLine($"❌ Müşteri güncelleme başarısız - Status: {response.StatusCode}");
                Console.WriteLine($"Hata: {responseContent}");
                return false;
            }

            Console.WriteLine($"✅ Müşteri başarıyla güncellendi: {exactAccount.Email}");

            // ✅ Eğer sendWelcomeEmail = true ise, ayrı GraphQL mutation ile mail gönder
            if (emailLower == "irfnk83@gmail.com")
            {
                Console.WriteLine($"📧 Hoşgeldin maili gönderiliyor...");
                var emailSent = await SendWelcomeEmailToCustomerAsync(customerId, logFilePath);

                if (!emailSent)
                {
                    Console.WriteLine($"⚠️ Müşteri güncellendi ama mail gönderilemedi");
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

    // ✅ YENİ METOD: Mevcut müşteriye hoşgeldin maili gönder (GraphQL mutation)
    private async Task<bool> SendWelcomeEmailToCustomerAsync(string customerId, string logFilePath = null)
    {
        try
        {
            Console.WriteLine($"   📧 Hoşgeldin maili gönderiliyor: Customer ID {customerId}");

            // ✅ REST API endpoint: POST /admin/api/{version}/customers/{customer_id}/send_invite.json
            // Request body MUTLAKA {"customer_invite":{}} olmalı
            var inviteData = new { customer_invite = new { } };
            var jsonContent = new StringContent(JsonSerializer.Serialize(inviteData));
            jsonContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            var response = await _client.PostAsync($"customers/{customerId}/send_invite.json", jsonContent);
            var responseContent = await response.Content.ReadAsStringAsync();

            await Task.Delay(500); // Rate limit

            // ✅ Log dosyasına kaydet
            if (!string.IsNullOrEmpty(logFilePath))
            {
                await AppendToLogFileAsync(logFilePath, new
                {
                    Timestamp = DateTimeOffset.Now,
                    Action = "SendWelcomeEmail",
                    CustomerId = customerId,
                    Success = response.IsSuccessStatusCode,
                    StatusCode = (int)response.StatusCode,
                    ProcessType = "WelcomeEmailSend"
                });
            }

            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"   ✅ Hoşgeldin maili gönderildi: {customerId}");
                return true;
            }
            else
            {
                Console.WriteLine($"   ❌ Hoşgeldin maili gönderilemedi - Status: {response.StatusCode}");
                if (!string.IsNullOrEmpty(responseContent))
                {
                    Console.WriteLine($"   Response: {responseContent}");
                }
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ❌ Mail gönderme hatası: {ex.Message}");
            return false;
        }
    }

    // ✅ TOPLU MAIL GÖNDERME
    public async Task<(int successCount, int failureCount)> SendWelcomeEmailBatchAsync(
        List<string> customerIds,
        string logFilePath = null)
    {
        int successCount = 0;
        int failureCount = 0;

        Console.WriteLine($"📧 Toplu hoşgeldin maili gönderiliyor: {customerIds.Count} müşteri");

        foreach (var customerId in customerIds)
        {
            try
            {
                var result = await SendWelcomeEmailToCustomerAsync(customerId, logFilePath);

                if (result)
                    successCount++;
                else
                    failureCount++;

                // Rate limit: Shopify = 2 requests/second max
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ {customerId} için hata: {ex.Message}");
                failureCount++;
            }
        }

        Console.WriteLine($"✅ Toplu işlem tamamlandı: {successCount} başarılı, {failureCount} başarısız");
        return (successCount, failureCount);
    }

    // ✅ YENİ METOD: Tüm müşteri email'lerini ve ID'lerini al
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



    // Validasyon helper metodu - sınıfınıza ekleyin
    private string ValidatePhoneNumber(string phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
            return "";

        // Sadece rakamları al
        var digitsOnly = System.Text.RegularExpressions.Regex.Replace(phoneNumber, @"[^\d]", "");

        // Minimum 10 rakam kontrolü
        if (digitsOnly.Length < 4)
        {
            return ""; // Geçersiz - boş döndür
        }
        else if (digitsOnly.Length < 20)
        {
            return digitsOnly.Substring(1, 11);
        }


        // İlk 10 rakamı döndür
        return digitsOnly.Substring(1, 11);
    }

    // Ana metod - güncellenmiş kısım
    public async Task<bool> CreateCustomerEmailAsync(Account exactAccount, string customerTag = "b2b-customer", string logFilePath = null, bool sendWelcomeEmail = true)
    {
        try
        {
            var emailExists = CustomerFindByEmail(exactAccount.Email);

            Console.WriteLine($"🆕 Yeni müşteri oluşturuluyor: Email={exactAccount.Email}");

            if (emailExists == null)
            {
                Console.WriteLine($"⚠️ Bu email zaten mevcut, müşteri oluşturulmadı: {exactAccount.Email}");

                if (!string.IsNullOrEmpty(logFilePath))
                {
                    await AppendToLogFileAsync(logFilePath, new
                    {
                        Timestamp = DateTimeOffset.Now,
                        Action = "CreateCustomer_Skipped",
                        Email = exactAccount.Email,
                        Name = exactAccount.Name,
                        Reason = "Email already exists in Shopify",
                        ProcessType = "NewCustomerCreation"
                    });

                    Console.WriteLine($"   📝 Log kaydedildi: {logFilePath}");
                }

                return false;
            }

            Console.WriteLine($" Email mevcut değil, yeni müşteri oluşturulacak");
            Console.WriteLine($"   📧 Hoşgeldin emaili: {(sendWelcomeEmail ? "GÖNDERİLECEK" : "GÖNDERİLMEYECEK")}");

            var countryCode = ConvertToCountryCode(exactAccount.Country, exactAccount.CountryName);
            Console.WriteLine($"   🌍 Ülke: {exactAccount.CountryName} → {countryCode}");

            // ✅ Telefon numarası validasyonu
            // var validatedPhone = ValidatePhoneNumber(exactAccount.Phone);
            // if (string.IsNullOrEmpty(validatedPhone) && !string.IsNullOrEmpty(exactAccount.Phone))
            // {
            //     Console.WriteLine($"   ⚠️ Telefon numarası geçersiz: {exactAccount.Phone} (atlanıyor, müşteri yine kaydedilecek)");
            // }

            var customerData = new
            {
                customer = new
                {
                    first_name = GetFirstName(exactAccount.Name),
                    last_name = GetLastName(exactAccount.Name),
                    email = exactAccount.Email ?? "",
                    phone = "",  // ✅ Validasyon yapılmış telefon
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
                        phone = "",  // ✅ Validasyon yapılmış telefon
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

            Console.WriteLine($"   📤 Shopify API'ye istek gönderiliyor...");
            Console.WriteLine($"   🏷️  Tag: {customerTag}");

            var response = await _client.PostAsync("customers.json", jsonContent);
            var responseContent = await response.Content.ReadAsStringAsync();

            // ✅ API çağrısından sonra delay
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
                // ✅ Yeni müşteri cache'e ekle
                _existingCustomerEmails.Add(exactAccount.Email.ToLower());

                Console.WriteLine($"✅ Müşteri başarıyla oluşturuldu: {exactAccount.Email}");
                return true;
            }
            else
            {
                Console.WriteLine($"❌ Müşteri oluşturma başarısız - Status: {response.StatusCode}");
                Console.WriteLine($"Hata: {responseContent}");
                return false;
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

    public async Task<bool> CreateCustomerAsync(Account exactAccount, string customerTag = "b2b-customer", string logFilePath = null, bool sendWelcomeEmail = true)
    {
        try
        {
            Console.WriteLine($"🆕 Yeni müşteri oluşturuluyor: Email={exactAccount.Email}");

            // ✅ İlk çağrıda tüm müşterileri bir kez yükle
            if (!_customersLoaded)
            {
                Console.WriteLine($"📥 Shopify'dan tüm müşteriler yükleniyor...");
                _existingCustomerEmails = await GetAllCustomerEmailsAsync();
                _customersLoaded = true;
                Console.WriteLine($"✅ {_existingCustomerEmails.Count} müşteri yüklendi");
                await Task.Delay(500);
            }

            // ✅ Bellekte kontrol et (çok hızlı)
            var emailExists = _existingCustomerEmails.Contains(exactAccount.Email.ToLower());

            if (emailExists)
            {
                Console.WriteLine($"⚠️ Bu email zaten mevcut, müşteri oluşturulmadı: {exactAccount.Email}");

                if (!string.IsNullOrEmpty(logFilePath))
                {
                    await AppendToLogFileAsync(logFilePath, new
                    {
                        Timestamp = DateTimeOffset.Now,
                        Action = "CreateCustomer_Skipped",
                        Email = exactAccount.Email,
                        Name = exactAccount.Name,
                        Reason = "Email already exists in Shopify",
                        ProcessType = "NewCustomerCreation"
                    });

                    Console.WriteLine($"   📝 Log kaydedildi: {logFilePath}");
                }

                return false;
            }

            Console.WriteLine($" Email mevcut değil, yeni müşteri oluşturulacak");
            Console.WriteLine($"   📧 Hoşgeldin emaili: {(sendWelcomeEmail ? "GÖNDERİLECEK" : "GÖNDERİLMEYECEK")}");

            var countryCode = ConvertToCountryCode(exactAccount.Country, exactAccount.CountryName);
            Console.WriteLine($"   🌍 Ülke: {exactAccount.CountryName} → {countryCode}");

            var customerData = new
            {
                customer = new
                {
                    first_name = GetFirstName(exactAccount.Name),
                    last_name = GetLastName(exactAccount.Name),
                    email = exactAccount.Email ?? "",
                    phone = exactAccount.Phone ?? "",
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
                        phone =  exactAccount.Phone ?? "",
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

            Console.WriteLine($"   📤 Shopify API'ye istek gönderiliyor...");
            Console.WriteLine($"   🏷️  Tag: {customerTag}");

            var response = await _client.PostAsync("customers.json", jsonContent);
            var responseContent = await response.Content.ReadAsStringAsync();

            // ✅ API çağrısından sonra delay
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
                // ✅ Yeni müşteri cache'e ekle
                _existingCustomerEmails.Add(exactAccount.Email.ToLower());

                Console.WriteLine($"✅ Müşteri başarıyla oluşturuldu: {exactAccount.Email}");
                return true;
            }
            else
            {
                Console.WriteLine($"❌ Müşteri oluşturma başarısız - Status: {response.StatusCode}");
                Console.WriteLine($"Hata: {responseContent}");
                return false;
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

    // ✅ YENİ METOD: Tüm müşteri emaillerini bir kez yükle
    private async Task<HashSet<string>> GetAllCustomerEmailsAsync()
    {
        var allEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    return allEmails;
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);

                var hasNextPage = false;
                var endCursor = "";

                // Response'daki emails'i HashSet'e ekle
                if (jsonResponse.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("customers", out var customers))
                {
                    if (customers.TryGetProperty("edges", out var edges))
                    {
                        foreach (var edge in edges.EnumerateArray())
                        {
                            if (edge.TryGetProperty("node", out var node) &&
                                node.TryGetProperty("email", out var email))
                            {
                                var emailValue = email.GetString()?.ToLower();
                                if (!string.IsNullOrEmpty(emailValue))
                                {
                                    allEmails.Add(emailValue);
                                }
                            }
                        }
                    }

                    // Sonraki sayfa kontrolü
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
                }

                if (hasNextPage && !string.IsNullOrEmpty(endCursor))
                {
                    nextPageCursor = endCursor;
                }
                else
                {
                    break; // Tüm sayfalar yüklendi
                }

                // API rate limit'i aşmamak için bekle
                await Task.Delay(300);
            }

            Console.WriteLine($"   ✅ Toplam {allEmails.Count} müşteri email'i yüklendi ({pageCount} sayfa)");
            return allEmails;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Müşteri listesi yüklenirken hata: {ex.Message}");
            return allEmails;
        }
    }

    // ✅ Cache'i sıfırlamak için metod (gerekirse)
    public void ResetCustomerCache()
    {
        _existingCustomerEmails = null;
        _customersLoaded = false;
        Console.WriteLine("🔄 Müşteri cache'i sıfırlandı");
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

    //  Yardımcı metodlar
    private async Task<bool> IsCustomerEmailExistsAsync(string email)
    {
        try
        {
            if (string.IsNullOrEmpty(email))
                return false;

            var response = await _client.GetAsync($"customers/search.json?query=email:{email}");
            if (!response.IsSuccessStatusCode)
                return false;

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);

            if (doc.RootElement.TryGetProperty("customers", out var customers))
            {
                return customers.GetArrayLength() > 0;
            }

            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ⚠️ Email kontrolünde hata: {ex.Message}");
            return false;
        }
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


    // yeni ürün ekle 
    // ShopifyService.cs içine ekleyin

    // public async Task<bool> CreateProductAsync(ExactProduct exactProduct, string logFilePath = null)
    // {
    //     try
    //     {
    //         Console.WriteLine($"🆕 Yeni ürün oluşturuluyor: SKU={exactProduct.Code}");

    //         // Shopify'da oluşturulacak ürün verisi
    //         var productData = new
    //         {
    //             product = new
    //             {
    //                 title = exactProduct.Description ?? "Başlık Yok",
    //                 body_html = exactProduct.ExtraDescription ?? "",
    //                 vendor = "Exact Online",
    //                 product_type = exactProduct.ItemGroupDescription ?? "",
    //                 tags = "exact-import",
    //                 status = "active",
    //                 variants = new[]
    //                 {
    //                 new
    //                 {
    //                     sku = exactProduct.Code,
    //                     price = exactProduct.StandardSalesPrice?.ToString("F2") ?? "0.00",
    //                     inventory_management = "shopify",
    //                     inventory_quantity = (int)(exactProduct.Stock ?? 0),
    //                     barcode = exactProduct.Barcode ?? "",
    //                     weight = TryParseWeight(exactProduct.NetWeight),
    //                     weight_unit = exactProduct.NetWeightUnit ?? "kg",
    //                     taxable = ParseTaxable(exactProduct.IsTaxableItem)
    //                 }
    //             },
    //                 images = string.IsNullOrEmpty(exactProduct.PictureUrl)
    //                     ? null
    //                     : new[] { new { src = exactProduct.PictureUrl } }
    //             }
    //         };

    //         var jsonContent = new StringContent(JsonSerializer.Serialize(productData));
    //         jsonContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

    //         Console.WriteLine($"   📤 Shopify API'ye istek gönderiliyor...");

    //         var response = await _client.PostAsync("products.json", jsonContent);
    //         var responseContent = await response.Content.ReadAsStringAsync();

    //         // Log dosyasına kaydet
    //         if (!string.IsNullOrEmpty(logFilePath))
    //         {
    //             var logData = new
    //             {
    //                 Timestamp = DateTimeOffset.Now,
    //                 Action = "CreateProduct",
    //                 SKU = exactProduct.Code,
    //                 Title = exactProduct.Description,
    //                 Price = exactProduct.StandardSalesPrice,
    //                 Stock = exactProduct.Stock,
    //                 Success = response.IsSuccessStatusCode,
    //                 StatusCode = (int)response.StatusCode,
    //                 RequestPayload = productData,
    //                 Response = responseContent,
    //                 ProcessType = "NewProductCreation"
    //             };

    //             var logJson = JsonSerializer.Serialize(logData, _jsonOptions);

    //             // Log dizinini oluştur
    //             var logDirectory = Path.GetDirectoryName(logFilePath);
    //             if (!string.IsNullOrEmpty(logDirectory) && !Directory.Exists(logDirectory))
    //             {
    //                 Directory.CreateDirectory(logDirectory);
    //             }

    //             await File.WriteAllTextAsync(logFilePath, logJson);
    //             Console.WriteLine($"   📝 Log kaydedildi: {logFilePath}");
    //         }

    //         if (response.IsSuccessStatusCode)
    //         {
    //             // Response'dan ürün ID'sini parse et
    //             using var responseDoc = JsonDocument.Parse(responseContent);
    //             string productId = null;
    //             string variantId = null;

    //             if (responseDoc.RootElement.TryGetProperty("product", out var product))
    //             {
    //                 if (product.TryGetProperty("id", out var prodId))
    //                 {
    //                     productId = prodId.ToString();
    //                 }

    //                 if (product.TryGetProperty("variants", out var variants))
    //                 {
    //                     var firstVariant = variants.EnumerateArray().FirstOrDefault();
    //                     if (firstVariant.ValueKind != JsonValueKind.Undefined &&
    //                         firstVariant.TryGetProperty("id", out var varId))
    //                     {
    //                         variantId = varId.ToString();
    //                     }
    //                 }
    //             }

    //             Console.WriteLine($"    Ürün başarıyla oluşturuldu");
    //             Console.WriteLine($"      - SKU: {exactProduct.Code}");
    //             Console.WriteLine($"      - Title: {exactProduct.Description}");
    //             Console.WriteLine($"      - Price: {exactProduct.StandardSalesPrice:F2}");
    //             Console.WriteLine($"      - Stock: {exactProduct.Stock}");
    //             Console.WriteLine($"      - Product ID: {productId}");
    //             Console.WriteLine($"      - Variant ID: {variantId}");

    //             return true;
    //         }
    //         else
    //         {
    //             Console.WriteLine($"   ❌ Ürün oluşturulamadı");
    //             Console.WriteLine($"      - StatusCode: {response.StatusCode}");
    //             Console.WriteLine($"      - Response: {responseContent}");

    //             return false;
    //         }
    //     }
    //     catch (HttpRequestException ex) when (ex.Message.Contains("429"))
    //     {
    //         Console.WriteLine($"   ⏳ Rate limit hatası: {ex.Message}");
    //         return false;
    //     }
    //     catch (Exception ex)
    //     {
    //         Console.WriteLine($"   ❌ Exception: {ex.Message}");
    //         Console.WriteLine($"      - StackTrace: {ex.StackTrace}");
    //         return false;
    //     }
    // }
    // public async Task<bool> CreateProductAsync(ExactProduct exactProduct, string logFilePath = null)
    // {
    //     try
    //     {
    //         Console.WriteLine($"🆕 Yeni ürün oluşturuluyor: SKU={exactProduct.Code}");

    //         //  Basit SKU kontrolü
    //         var skuExists = await IsSkuExistsAsync(exactProduct.Code);

    //         if (skuExists)
    //         {
    //             Console.WriteLine($"⚠️ Bu SKU zaten mevcut, ürün oluşturulmadı: {exactProduct.Code}");

    //             // Log dosyasına kaydet
    //             if (!string.IsNullOrEmpty(logFilePath))
    //             {
    //                 await AppendToLogFileAsync(logFilePath, new
    //                 {
    //                     Timestamp = DateTimeOffset.Now,
    //                     Action = "CreateProduct_Skipped",
    //                     SKU = exactProduct.Code,
    //                     Title = exactProduct.Description,
    //                     Reason = "SKU already exists in Shopify",
    //                     ProcessType = "NewProductCreation"
    //                 });

    //                 Console.WriteLine($"   📝 Log kaydedildi: {logFilePath}");
    //             }

    //             return false;
    //         }

    //         Console.WriteLine($" SKU mevcut değil, yeni ürün oluşturulacak");

    //         //  Ürün oluşturma kodu (değişmedi)
    //         var productData = new
    //         {
    //             product = new
    //             {
    //                 title = exactProduct.Description ?? "Başlık Yok",
    //                 body_html = exactProduct.ExtraDescription ?? "",
    //                 vendor = "Exact Online",
    //                 product_type = exactProduct.ItemGroupDescription ?? "",
    //                 tags = "exact-import",
    //                 status = "active",
    //                 variants = new[]
    //                 {
    //                 new
    //                 {
    //                     sku = exactProduct.Code,
    //                     price = exactProduct.StandardSalesPrice?.ToString("F2") ?? "0.00",
    //                     inventory_management = "shopify",
    //                     inventory_quantity = (int)(exactProduct.Stock ?? 0),
    //                     barcode = exactProduct.Barcode ?? "",
    //                     weight = TryParseWeight(exactProduct.NetWeight),
    //                     weight_unit = exactProduct.NetWeightUnit ?? "kg",
    //                     taxable = ParseTaxable(exactProduct.IsTaxableItem)
    //                 }
    //             },
    //                 images = string.IsNullOrEmpty(exactProduct.PictureUrl)
    //                     ? null
    //                     : new[] { new { src = exactProduct.PictureUrl } }
    //             }
    //         };

    //         var jsonContent = new StringContent(JsonSerializer.Serialize(productData));
    //         jsonContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

    //         Console.WriteLine($"   📤 Shopify API'ye istek gönderiliyor...");

    //         var response = await _client.PostAsync("products.json", jsonContent);
    //         var responseContent = await response.Content.ReadAsStringAsync();

    //         //  Log dosyasına kaydet (DÜZELTME YAPILAN YER)
    //         if (!string.IsNullOrEmpty(logFilePath))
    //         {
    //             await AppendToLogFileAsync(logFilePath, new
    //             {
    //                 Timestamp = DateTimeOffset.Now,
    //                 Action = "CreateProduct",
    //                 SKU = exactProduct.Code,
    //                 Title = exactProduct.Description,
    //                 Price = exactProduct.StandardSalesPrice,
    //                 Stock = exactProduct.Stock,
    //                 Success = response.IsSuccessStatusCode,
    //                 StatusCode = (int)response.StatusCode,
    //                 RequestPayload = productData,
    //                 Response = responseContent,
    //                 ProcessType = "NewProductCreation"
    //             });

    //             Console.WriteLine($"   📝 Log kaydedildi: {logFilePath}");
    //         }

    //         if (response.IsSuccessStatusCode)
    //         {
    //             using var responseDoc = JsonDocument.Parse(responseContent);
    //             string productId = null;
    //             string variantId = null;

    //             if (responseDoc.RootElement.TryGetProperty("product", out var product))
    //             {
    //                 if (product.TryGetProperty("id", out var prodId))
    //                 {
    //                     productId = prodId.ToString();
    //                 }

    //                 if (product.TryGetProperty("variants", out var variants))
    //                 {
    //                     var firstVariant = variants.EnumerateArray().FirstOrDefault();
    //                     if (firstVariant.ValueKind != JsonValueKind.Undefined &&
    //                         firstVariant.TryGetProperty("id", out var varId))
    //                     {
    //                         variantId = varId.ToString();
    //                     }
    //                 }
    //             }

    //             Console.WriteLine($"    Ürün başarıyla oluşturuldu");
    //             Console.WriteLine($"      - SKU: {exactProduct.Code}");
    //             Console.WriteLine($"      - Title: {exactProduct.Description}");
    //             Console.WriteLine($"      - Price: {exactProduct.StandardSalesPrice:F2}");
    //             Console.WriteLine($"      - Stock: {exactProduct.Stock}");
    //             Console.WriteLine($"      - Product ID: {productId}");
    //             Console.WriteLine($"      - Variant ID: {variantId}");

    //             return true;
    //         }
    //         else
    //         {
    //             Console.WriteLine($"   ❌ Ürün oluşturulamadı");
    //             Console.WriteLine($"      - StatusCode: {response.StatusCode}");
    //             Console.WriteLine($"      - Response: {responseContent}");

    //             return false;
    //         }
    //     }
    //     catch (HttpRequestException ex) when (ex.Message.Contains("429"))
    //     {
    //         Console.WriteLine($"   ⏳ Rate limit hatası: {ex.Message}");
    //         return false;
    //     }
    //     catch (Exception ex)
    //     {
    //         Console.WriteLine($"   ❌ Exception: {ex.Message}");
    //         Console.WriteLine($"      - StackTrace: {ex.StackTrace}");
    //         return false;
    //     }
    // }
    public async Task<bool> CreateProductAsync(ExactProduct exactProduct, string logFilePath = null)
    {
        try
        {
            Console.WriteLine($"🆕 Yeni ürün oluşturuluyor: SKU={exactProduct.Code}");

            //  Basit SKU kontrolü
            var skuExists = await IsSkuExistsAsync(exactProduct.Code);

            if (skuExists)
            {
                Console.WriteLine($"⚠️ Bu SKU zaten mevcut, ürün oluşturulmadı: {exactProduct.Code}");

                // Log dosyasına kaydet
                if (!string.IsNullOrEmpty(logFilePath))
                {
                    await AppendToLogFileAsync(logFilePath, new
                    {
                        Timestamp = DateTimeOffset.Now,
                        Action = "CreateProduct_Skipped",
                        SKU = exactProduct.Code,
                        Title = exactProduct.Description,
                        Reason = "SKU already exists in Shopify",
                        ProcessType = "NewProductCreation"
                    });

                    Console.WriteLine($"   📝 Log kaydedildi: {logFilePath}");
                }

                return false;
            }

            Console.WriteLine($" SKU mevcut değil, yeni ürün oluşturulacak");

            //  Ürün oluşturma kodu (images kısmı kaldırıldı)
            var productData = new
            {
                product = new
                {
                    title = exactProduct.Description ?? "Başlık Yok",
                    body_html = exactProduct.ExtraDescription ?? "",
                    vendor = "Exact Online",
                    product_type = exactProduct.ItemGroupDescription ?? "",
                    tags = "exact-import",
                    status = "active",
                    variants = new[]
                    {
                    new
                    {
                        sku = exactProduct.Code,
                        price = exactProduct.StandardSalesPrice?.ToString("F2") ?? "0.00",
                        inventory_management = "shopify",
                        inventory_quantity = (int)(exactProduct.Stock ?? 0),
                        barcode = exactProduct.Barcode ?? "",
                        weight = TryParseWeight(exactProduct.NetWeight),
                        weight_unit = exactProduct.NetWeightUnit ?? "kg",
                        taxable = ParseTaxable(exactProduct.IsTaxableItem)
                    }
                }
                    // ❌ images kısmı kaldırıldı
                }
            };

            var jsonContent = new StringContent(JsonSerializer.Serialize(productData));
            jsonContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            Console.WriteLine($"   📤 Shopify API'ye istek gönderiliyor...");

            var response = await _client.PostAsync("products.json", jsonContent);
            var responseContent = await response.Content.ReadAsStringAsync();

            //  Log dosyasına kaydet
            if (!string.IsNullOrEmpty(logFilePath))
            {
                await AppendToLogFileAsync(logFilePath, new
                {
                    Timestamp = DateTimeOffset.Now,
                    Action = "CreateProduct",
                    SKU = exactProduct.Code,
                    Title = exactProduct.Description,
                    Price = exactProduct.StandardSalesPrice,
                    Stock = exactProduct.Stock,
                    Success = response.IsSuccessStatusCode,
                    StatusCode = (int)response.StatusCode,
                    RequestPayload = productData,
                    Response = responseContent,
                    ProcessType = "NewProductCreation"
                });

                Console.WriteLine($"   📝 Log kaydedildi: {logFilePath}");
            }

            if (response.IsSuccessStatusCode)
            {
                using var responseDoc = JsonDocument.Parse(responseContent);
                string productId = null;
                string variantId = null;

                if (responseDoc.RootElement.TryGetProperty("product", out var product))
                {
                    if (product.TryGetProperty("id", out var prodId))
                    {
                        productId = prodId.ToString();
                    }

                    if (product.TryGetProperty("variants", out var variants))
                    {
                        var firstVariant = variants.EnumerateArray().FirstOrDefault();
                        if (firstVariant.ValueKind != JsonValueKind.Undefined &&
                            firstVariant.TryGetProperty("id", out var varId))
                        {
                            variantId = varId.ToString();
                        }
                    }
                }

                Console.WriteLine($"    Ürün başarıyla oluşturuldu");
                Console.WriteLine($"      - SKU: {exactProduct.Code}");
                Console.WriteLine($"      - Title: {exactProduct.Description}");
                Console.WriteLine($"      - Price: {exactProduct.StandardSalesPrice:F2}");
                Console.WriteLine($"      - Stock: {exactProduct.Stock}");
                Console.WriteLine($"      - Product ID: {productId}");
                Console.WriteLine($"      - Variant ID: {variantId}");

                return true;
            }
            else
            {
                Console.WriteLine($"   ❌ Ürün oluşturulamadı");
                Console.WriteLine($"      - StatusCode: {response.StatusCode}");
                Console.WriteLine($"      - Response: {responseContent}");

                return false;
            }
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("429"))
        {
            Console.WriteLine($"   ⏳ Rate limit hatası: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ❌ Exception: {ex.Message}");
            Console.WriteLine($"      - StackTrace: {ex.StackTrace}");
            return false;
        }
    }
    // get shopify product by sku
    public async Task<List<object>> GetProductsBySkuAsync(string sku, int limit = 250)
    {
        var allProductsDoc = await GetAllProductsRawAsync(limit);
        var matchedProducts = new List<object>();

        if (allProductsDoc.RootElement.TryGetProperty("products", out var products))
        {
            foreach (var product in products.EnumerateArray())
            {
                if (product.TryGetProperty("variants", out var variants))
                {
                    foreach (var variant in variants.EnumerateArray())
                    {
                        if (variant.TryGetProperty("sku", out var skuElement) &&
                            skuElement.GetString() == sku)
                        {
                            // Ürün bilgilerini sadeleştirerek ekle
                            matchedProducts.Add(new
                            {
                                ProductId = product.TryGetProperty("id", out var prodId) ? prodId.ToString() : null,
                                ProductTitle = product.TryGetProperty("title", out var title) ? title.GetString() : null,
                                ProductStatus = product.TryGetProperty("status", out var status) ? status.GetString() : null,
                                VariantId = variant.TryGetProperty("id", out var varId) ? varId.ToString() : null,
                                SKU = sku,
                                Price = variant.TryGetProperty("price", out var price) ? price.GetString() : null,
                                InventoryQuantity = variant.TryGetProperty("inventory_quantity", out var stock) ? stock.GetInt32() : 0,
                                CreatedAt = product.TryGetProperty("created_at", out var created) ? created.GetString() : null,
                                UpdatedAt = product.TryGetProperty("updated_at", out var updated) ? updated.GetString() : null
                            });
                        }
                    }
                }
            }
        }

        allProductsDoc.Dispose();
        return matchedProducts;
    }


    //all shopify products
    public async Task<JsonDocument> GetAllProductsRawAsync(int limit = 250)
    {
        // Shopify API limiti maksimum 250'dir
        if (limit > 250)
        {
            Console.WriteLine($"⚠️ Uyarı: Limit {limit} olarak verildi, ancak Shopify maksimum 250'e izin verir. 250 kullanılacak.");
            limit = 250;
        }
        if (limit < 1)
        {
            Console.WriteLine($"⚠️ Uyarı: Limit {limit} geçersiz. Varsayılan 250 kullanılacak.");
            limit = 250;
        }

        var allProductsJson = new List<JsonElement>();
        string endpoint = $"products.json?limit={limit}";
        int pageCount = 0;
        int totalProducts = 0;
        int retryCount = 0;
        const int maxRetries = 3;

        Console.WriteLine($"🔄 Tüm ürünler getiriliyor (sayfa başına {limit} ürün)...");

        while (!string.IsNullOrEmpty(endpoint))
        {
            pageCount++;
            Console.WriteLine($"📄 Sayfa {pageCount} getiriliyor: {endpoint}");

            try
            {
                var response = await _client.GetAsync(endpoint);

                // 429 Rate Limit hatası için özel işlem
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    if (retryCount < maxRetries)
                    {
                        retryCount++;
                        Console.WriteLine($"⏳ Rate limit aşıldı, {retryCount}. deneme için 60 saniye bekleniyor...");
                        await Task.Delay(60000); // 60 saniye bekle
                        pageCount--; // Sayfa sayacını geri al
                        continue;
                    }
                    else
                    {
                        Console.WriteLine($"❌ Maximum retry sayısına ulaşıldı ({maxRetries})");
                        throw new Exception($"Rate limit aşıldı ve {maxRetries} deneme başarısız");
                    }
                }

                response.EnsureSuccessStatusCode();
                retryCount = 0; // Başarılı istek sonrası retry sayacını sıfırla

                string jsonResponse = await response.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(jsonResponse);

                if (document.RootElement.TryGetProperty("products", out var products))
                {
                    int pageProductCount = 0;
                    foreach (var product in products.EnumerateArray())
                    {
                        allProductsJson.Add(product.Clone());
                        pageProductCount++;
                        totalProducts++;
                    }

                    Console.WriteLine($"    Bu sayfada {pageProductCount} ürün alındı. Toplam: {totalProducts}");

                    // Eğer sayfa belirlenen limitten az ürün döndürdüyse, son sayfadayız demektir
                    // if (pageProductCount < limit)
                    // {
                    //     Console.WriteLine($"   🏁 Son sayfa tespit edildi (bu sayfada {pageProductCount} ürün)");
                    //     break;
                    // }
                }
                else
                {
                    Console.WriteLine("   ⚠️ Bu sayfada 'products' property bulunamadı");
                    break;
                }

                // Pagination - Link header'dan next URL'i al
                endpoint = null;
                if (response.Headers.TryGetValues("Link", out var values))
                {
                    string linkHeader = values.FirstOrDefault() ?? "";
                    Console.WriteLine($"   🔗 Link Header: {linkHeader}");

                    if (linkHeader.Contains("rel=\"next\""))
                    {
                        var parts = linkHeader.Split(',');
                        var nextLink = parts.FirstOrDefault(p => p.Contains("rel=\"next\""));
                        if (nextLink != null)
                        {
                            var start = nextLink.IndexOf('<') + 1;
                            var end = nextLink.IndexOf('>') - start;
                            if (start > 0 && end > 0)
                            {
                                var nextUrl = nextLink.Substring(start, end);

                                // Full URL'den relative path'e çevir
                                if (nextUrl.Contains("/admin/api/"))
                                {
                                    var apiIndex = nextUrl.IndexOf("/admin/api/");
                                    var versionEndIndex = nextUrl.IndexOf('/', apiIndex + 11); // "/admin/api/" + version
                                    if (versionEndIndex > 0)
                                    {
                                        endpoint = nextUrl.Substring(versionEndIndex + 1);
                                    }
                                    else
                                    {
                                        endpoint = nextUrl.Split("/admin/api/").Last().Split('/').Skip(1).Aggregate((a, b) => $"{a}/{b}");
                                    }
                                }
                                else
                                {
                                    endpoint = nextUrl;
                                }

                                Console.WriteLine($"   ➡️ Sonraki sayfa: {endpoint}");
                            }
                            else
                            {
                                Console.WriteLine("   ❌ Link header parse edilemedi");
                                break;
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("   🏁 Link header'da 'next' bulunamadı - son sayfa");
                        break;
                    }
                }
                else
                {
                    Console.WriteLine("   🏁 Link header bulunamadı - son sayfa");
                    break;
                }

                // Rate limiting için daha uzun bekleme
                Console.WriteLine("   ⏳ Rate limit için 2 saniye bekleniyor...");
                await Task.Delay(2000); // 2 saniye bekleme
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Sayfa {pageCount} alınırken hata: {ex.Message}");
                throw;
            }
        }

        Console.WriteLine($" Toplam {totalProducts} ürün {pageCount} sayfada alındı");

        // Tüm ürünleri tek bir JSON'da birleştir
        var finalJson = new { products = allProductsJson };
        string finalJsonString = JsonSerializer.Serialize(finalJson, _jsonOptions);

        var result = JsonDocument.Parse(finalJsonString);
        Console.WriteLine($"📦 JSON dokümani oluşturuldu - {allProductsJson.Count} ürün");

        return result;
    }

    public async Task SaveRawProductsToFileAsync(string filePath)
    {
        var rawProductsDoc = await GetAllProductsRawAsync();

        // Ürün sayısını konsola yazdır
        if (rawProductsDoc.RootElement.TryGetProperty("products", out var products))
        {
            Console.WriteLine($"Toplam ürün: {products.GetArrayLength()}");
        }

        string jsonString = JsonSerializer.Serialize(rawProductsDoc.RootElement, _jsonOptions);
        await File.WriteAllTextAsync(filePath, jsonString);

        Console.WriteLine($"Ham ürün verileri {filePath} dosyasına kaydedildi.");

        rawProductsDoc.Dispose();
    }

    // update stock by sku
    public async Task UpdateProductStockBySkuAndSaveRawAsync(string sku, int stock, string filePath)
    {
        var rawProductsDoc = await GetAllProductsRawAsync();
        var variantsToUpdate = new List<(string variantId, string inventoryItemId, int currentStock)>();

        // SKU'yu tüm ürünlerde ara (hem tekil hem varyant)
        if (rawProductsDoc.RootElement.TryGetProperty("products", out var products))
        {
            foreach (var product in products.EnumerateArray())
            {
                // Variants içinde ara
                if (product.TryGetProperty("variants", out var variants))
                {
                    foreach (var variant in variants.EnumerateArray())
                    {
                        if (variant.TryGetProperty("sku", out var skuElement) &&
                            skuElement.GetString() == sku)
                        {
                            string variantId = variant.TryGetProperty("id", out var idElement) ? idElement.ToString() : null;
                            string inventoryItemId = variant.TryGetProperty("inventory_item_id", out var invItemElement) ? invItemElement.ToString() : null;
                            int currentStock = variant.TryGetProperty("inventory_quantity", out var stockElement) ? stockElement.GetInt32() : 0;

                            if (!string.IsNullOrEmpty(variantId) && !string.IsNullOrEmpty(inventoryItemId))
                            {
                                variantsToUpdate.Add((variantId, inventoryItemId, currentStock));
                                Console.WriteLine($"Bulunan variant - Product: {(product.TryGetProperty("title", out var title) ? title.GetString() : "N/A")}, Variant ID: {variantId}, Current Stock: {currentStock}");
                            }
                        }
                    }
                }
            }
        }

        if (variantsToUpdate.Count == 0)
        {
            Console.WriteLine($"SKU '{sku}' hiçbir üründe bulunamadı.");
            rawProductsDoc.Dispose();
            return;
        }

        Console.WriteLine($"SKU '{sku}' için {variantsToUpdate.Count} adet variant bulundu. Hepsini güncelleniyor...");

        try
        {
            // Location ID'yi al
            var locationsResponse = await _client.GetAsync("locations.json");
            locationsResponse.EnsureSuccessStatusCode();
            var locationsContent = await locationsResponse.Content.ReadAsStringAsync();
            var locationsDoc = JsonDocument.Parse(locationsContent);

            string locationId = null;
            if (locationsDoc.RootElement.TryGetProperty("locations", out var locations))
            {
                var firstLocation = locations.EnumerateArray().FirstOrDefault();
                if (firstLocation.ValueKind != JsonValueKind.Undefined)
                {
                    if (firstLocation.TryGetProperty("id", out var locIdElement))
                    {
                        locationId = locIdElement.ToString();
                    }
                }
            }
            locationsDoc.Dispose();

            if (string.IsNullOrEmpty(locationId))
            {
                Console.WriteLine("Location ID bulunamadı.");
                rawProductsDoc.Dispose();
                return;
            }

            Console.WriteLine($"Location ID: {locationId}");

            // Her variant için stok güncelle
            int successCount = 0;
            foreach (var (variantId, inventoryItemId, currentStock) in variantsToUpdate)
            {
                try
                {
                    // 1. Inventory item'ı track edilen hale getir
                    var trackPayload = new
                    {
                        inventory_item = new
                        {
                            id = inventoryItemId,
                            tracked = true
                        }
                    };

                    var trackContent = new StringContent(JsonSerializer.Serialize(trackPayload));
                    trackContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                    var trackResponse = await _client.PutAsync($"inventory_items/{inventoryItemId}.json", trackContent);
                    // Track response'u kontrol etmiyoruz çünkü zaten tracked olabilir

                    // 2. Inventory level'ı güncelle
                    var inventoryPayload = new
                    {
                        location_id = locationId,
                        inventory_item_id = inventoryItemId,
                        available = stock
                    };

                    var inventoryContent = new StringContent(JsonSerializer.Serialize(inventoryPayload));
                    inventoryContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                    var inventoryResponse = await _client.PostAsync("inventory_levels/set.json", inventoryContent);
                    inventoryResponse.EnsureSuccessStatusCode();

                    var responseContent = await inventoryResponse.Content.ReadAsStringAsync();
                    Console.WriteLine($" Variant ID {variantId} - Stok {currentStock}'den {stock}'e güncellendi");
                    Console.WriteLine($"   Inventory Item ID: {inventoryItemId}");
                    Console.WriteLine($"   Response: {responseContent}");

                    successCount++;
                }
                catch (Exception variantEx)
                {
                    Console.WriteLine($"❌ Variant ID {variantId} güncellenirken hata: {variantEx.Message}");
                }
            }

            Console.WriteLine($" SKU '{sku}' için {successCount}/{variantsToUpdate.Count} variant başarıyla güncellendi.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Genel stok güncelleme hatası: {ex.Message}");
            throw;
        }

        // Ham veriyi dosyaya yaz
        string jsonString = JsonSerializer.Serialize(rawProductsDoc.RootElement, _jsonOptions);
        await File.WriteAllTextAsync(filePath, jsonString);
        Console.WriteLine($"📁 Ham ürün verileri {filePath} dosyasına yazıldı.");

        rawProductsDoc.Dispose();
    }


    // 1. Exponential backoff ile retry mekanizması ekleyin
    private async Task<bool> ExecuteWithRetryAsync(Func<Task> operation, string operationName, int maxRetries = 3)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await operation();
                return true;
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("429"))
            {
                if (attempt == maxRetries)
                {
                    Console.WriteLine($"❌ {operationName} - Maksimum deneme sayısına ulaşıldı: {ex.Message}");
                    return false;
                }

                // Exponential backoff: 2^attempt seconds + random jitter
                var random = new Random();
                var baseDelay = Math.Pow(2, attempt) * 1000; // 2, 4, 8 saniye
                var jitter = random.Next(0, 1000); // 0-1 saniye random
                var totalDelay = (int)(baseDelay + jitter);

                Console.WriteLine($"⏳ {operationName} - 429 hatası, {attempt}/{maxRetries} deneme. {totalDelay}ms bekleniyor...");
                await Task.Delay(totalDelay);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ {operationName} - Beklenmeyen hata: {ex.Message}");
                return false;
            }
        }
        return false;
    }

    // 2. Geliştirilmiş batch update metodu
    public async Task<BatchUpdateResult> UpdateMultipleStocksBatchAsync(
        List<Dictionary<string, object>> exactItems,
        JsonDocument shopifyProducts,
        string filePath)
    {
        Console.WriteLine("🔄 Batch stok güncelleme başlıyor...");

        var result = new BatchUpdateResult();
        var updatedCodes = new List<string>();

        // SKU -> Stock mapping oluştur
        var stockUpdates = new Dictionary<string, int>();
        foreach (var item in exactItems)
        {
            string code = item.ContainsKey("Code") ? item["Code"].ToString() : "";
            double stock = item.ContainsKey("Stock") ? Convert.ToDouble(item["Stock"]) : 0;
            int stockInt = Convert.ToInt32(stock);

            if (!string.IsNullOrEmpty(code))
            {
                stockUpdates[code] = stockInt;
            }
        }

        Console.WriteLine($"📊 {stockUpdates.Count} adet ürün stok güncellemesi için hazırlandı");

        // Location ID'yi al
        var locationId = await GetLocationIdAsync();
        if (string.IsNullOrEmpty(locationId))
        {
            throw new Exception("Location ID bulunamadı");
        }

        Console.WriteLine($"📍 Location ID: {locationId}");

        // Update tasks listesi oluştur
        var updateTasks = PrepareUpdateTasks(shopifyProducts, stockUpdates);

        Console.WriteLine($"🎯 {updateTasks.Count} variant stok güncellemesi yapılacak");

        // Rate limiting için değişkenler
        int processedCount = 0;
        var skuSuccessCount = new Dictionary<string, int>();
        var skuErrorCount = new Dictionary<string, int>();
        var rateLimitTracker = new RateLimitTracker();

        // Batch güncelleme - Daha agresif rate limiting ile
        foreach (var (sku, variantId, inventoryItemId, newStock, productTitle, currentStock) in updateTasks)
        {
            // Rate limit kontrolü
            await rateLimitTracker.WaitIfNeededAsync();

            var success = await ExecuteWithRetryAsync(async () =>
            {
                // 1. Inventory item'ı track edilen hale getir
                await TrackInventoryItemAsync(inventoryItemId);

                // Requests arası ek bekleme
                await Task.Delay(200);

                // 2. Inventory level'ı güncelle
                await UpdateInventoryLevelAsync(locationId, inventoryItemId, newStock);

            }, $"SKU {sku} Variant {variantId}");

            if (success)
            {
                result.SuccessCount++;

                if (!skuSuccessCount.ContainsKey(sku))
                {
                    skuSuccessCount[sku] = 0;
                    updatedCodes.Add(sku);
                }
                skuSuccessCount[sku]++;

                Console.WriteLine($" Variant ID {variantId} - SKU {sku} - Stok {currentStock}'den {newStock}'e güncellendi");
            }
            else
            {
                result.ErrorCount++;

                if (!skuErrorCount.ContainsKey(sku))
                    skuErrorCount[sku] = 0;
                skuErrorCount[sku]++;

                Console.WriteLine($"❌ Variant ID {variantId} - SKU {sku} güncellenemedi");
            }

            processedCount++;

            // Progress raporu
            if (processedCount % 25 == 0) // Daha sık rapor
            {
                Console.WriteLine($"📈 İlerleme: {processedCount}/{updateTasks.Count} variant işlendi");
                Console.WriteLine($"    Başarılı: {result.SuccessCount} | ❌ Başarısız: {result.ErrorCount}");
            }

            // Her request sonrası minimum bekleme
            await Task.Delay(1000); // 500ms'den 1000ms'ye çıkarıldı
        }

        result.UpdatedCodes = updatedCodes;

        // Sonuç raporları
        WriteResults(filePath, shopifyProducts, result, updateTasks.Count, updatedCodes.Count, skuSuccessCount);

        return result;
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

    // 4. Yardımcı metodlar
    private List<(string sku, string variantId, string inventoryItemId, int newStock, string productTitle, int currentStock)> PrepareUpdateTasks(
        JsonDocument shopifyProducts,
        Dictionary<string, int> stockUpdates)
    {
        var updateTasks = new List<(string sku, string variantId, string inventoryItemId, int newStock, string productTitle, int currentStock)>();
        var processedSkus = new HashSet<string>();

        if (shopifyProducts.RootElement.TryGetProperty("products", out var products))
        {
            foreach (var product in products.EnumerateArray())
            {
                var productTitle = product.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : "N/A";

                if (product.TryGetProperty("variants", out var variants))
                {
                    foreach (var variant in variants.EnumerateArray())
                    {
                        if (variant.TryGetProperty("sku", out var skuElement))
                        {
                            string sku = skuElement.GetString();
                            if (!string.IsNullOrEmpty(sku) && stockUpdates.ContainsKey(sku))
                            {
                                // Variant bilgilerini al
                                string variantId = variant.TryGetProperty("id", out var idElement) ? idElement.ToString() : null;
                                string inventoryItemId = variant.TryGetProperty("inventory_item_id", out var invItemElement) ? invItemElement.ToString() : null;
                                int currentStock = variant.TryGetProperty("inventory_quantity", out var stockElement) ? stockElement.GetInt32() : 0;

                                if (!string.IsNullOrEmpty(variantId) && !string.IsNullOrEmpty(inventoryItemId))
                                {
                                    int newStock = stockUpdates[sku];
                                    updateTasks.Add((sku, variantId, inventoryItemId, newStock, productTitle, currentStock));

                                    // Çoklu SKU durumunu logla
                                    if (processedSkus.Contains(sku))
                                    {
                                        Console.WriteLine($"🔄 Çoklu variant tespit edildi - SKU: {sku}, Product: {productTitle}, Variant ID: {variantId}, Mevcut Stok: {currentStock}");
                                    }
                                    else
                                    {
                                        processedSkus.Add(sku);
                                        Console.WriteLine($"📦 SKU bulundu - {sku}, Product: {productTitle}, Mevcut Stok: {currentStock}");
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // Çoklu SKU raporu
        var duplicateSkus = updateTasks.GroupBy(x => x.sku)
                                       .Where(g => g.Count() > 1)
                                       .Select(g => new { SKU = g.Key, Count = g.Count() })
                                       .ToList();

        if (duplicateSkus.Any())
        {
            Console.WriteLine("📋 Çoklu variant'a sahip SKU'lar:");
            foreach (var dup in duplicateSkus)
            {
                Console.WriteLine($"   - {dup.SKU}: {dup.Count} variant");
            }
        }

        return updateTasks;
    }
    //     private List<(string sku, string variantId, string inventoryItemId, int newStock, string productTitle, int currentStock)> PrepareUpdateTasks(
    //     JsonDocument shopifyProducts,
    //     Dictionary<string, int> stockUpdates)
    // {
    //     var updateTasks = new List<(string sku, string variantId, string inventoryItemId, int newStock, string productTitle, int currentStock)>();
    //     var processedSkus = new HashSet<string>();

    //     if (shopifyProducts.RootElement.TryGetProperty("products", out var products))
    //     {
    //         foreach (var product in products.EnumerateArray())
    //         {
    //             var productTitle = product.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : "N/A";

    //             if (product.TryGetProperty("variants", out var variants))
    //             {
    //                 var variantArray = variants.EnumerateArray().ToList();

    //                 // 🔑 SADECE TEK VARIANT'I OLAN ÜRÜNLERİ İŞLE
    //                 if (variantArray.Count == 1)
    //                 {
    //                     var variant = variantArray[0];

    //                     if (variant.TryGetProperty("sku", out var skuElement))
    //                     {
    //                         string sku = skuElement.GetString();
    //                         if (!string.IsNullOrEmpty(sku) && stockUpdates.ContainsKey(sku))
    //                         {
    //                             string variantId = variant.TryGetProperty("id", out var idElement) ? idElement.ToString() : null;
    //                             string inventoryItemId = variant.TryGetProperty("inventory_item_id", out var invItemElement) ? invItemElement.ToString() : null;
    //                             int currentStock = variant.TryGetProperty("inventory_quantity", out var stockElement) ? stockElement.GetInt32() : 0;

    //                             if (!string.IsNullOrEmpty(variantId) && !string.IsNullOrEmpty(inventoryItemId))
    //                             {
    //                                 int newStock = stockUpdates[sku];
    //                                 updateTasks.Add((sku, variantId, inventoryItemId, newStock, productTitle, currentStock));
    //                                 processedSkus.Add(sku);
    //                                 Console.WriteLine($" Ana ürün bulundu - SKU: {sku}, Product: {productTitle}, Mevcut Stok: {currentStock}");
    //                             }
    //                         }
    //                     }
    //                 }
    //                 else
    //                 {
    //                     // Çoklu variant'lı ürünleri logla (güncelleme yapma)
    //                     Console.WriteLine($"⏭️  Atlandı (Çoklu variant) - Product: {productTitle}, Variant Sayısı: {variantArray.Count}");
    //                 }
    //             }
    //         }
    //     }

    //     Console.WriteLine($"\n📊 Özet: {updateTasks.Count} ana ürün güncellenecek");

    //     return updateTasks;
    // }

    private async Task<string> GetLocationIdAsync()
    {
        var locationsResponse = await _client.GetAsync("locations.json");
        locationsResponse.EnsureSuccessStatusCode();
        var locationsContent = await locationsResponse.Content.ReadAsStringAsync();
        var locationsDoc = JsonDocument.Parse(locationsContent);

        string locationId = null;
        if (locationsDoc.RootElement.TryGetProperty("locations", out var locations))
        {
            var firstLocation = locations.EnumerateArray().FirstOrDefault();
            if (firstLocation.ValueKind != JsonValueKind.Undefined)
            {
                locationId = firstLocation.GetProperty("id").ToString();
            }
        }
        locationsDoc.Dispose();

        return locationId;
    }

    private async Task TrackInventoryItemAsync(string inventoryItemId)
    {
        var trackPayload = new
        {
            inventory_item = new
            {
                id = inventoryItemId,
                tracked = true
            }
        };

        var trackContent = new StringContent(JsonSerializer.Serialize(trackPayload));
        trackContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var trackResponse = await _client.PutAsync($"inventory_items/{inventoryItemId}.json", trackContent);
        // Track response'u kontrol etmiyoruz çünkü zaten tracked olabilir
    }

    private async Task UpdateInventoryLevelAsync(string locationId, string inventoryItemId, int newStock)
    {
        var inventoryPayload = new
        {
            location_id = locationId,
            inventory_item_id = inventoryItemId,
            available = newStock
        };

        var inventoryContent = new StringContent(JsonSerializer.Serialize(inventoryPayload));
        inventoryContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var inventoryResponse = await _client.PostAsync("inventory_levels/set.json", inventoryContent);
        inventoryResponse.EnsureSuccessStatusCode();
    }

    private void WriteResults(string filePath, JsonDocument shopifyProducts, BatchUpdateResult result,
        int totalTasks, int uniqueSkuCount, Dictionary<string, int> skuSuccessCount)
    {
        // Detaylı rapor
        Console.WriteLine($"🎉 Batch güncelleme tamamlandı:");
        Console.WriteLine($"   📊 Toplam variant: {totalTasks}");
        Console.WriteLine($"    Başarılı variant: {result.SuccessCount}");
        Console.WriteLine($"   ❌ Başarısız variant: {result.ErrorCount}");
        Console.WriteLine($"   🏷️ İşlenen benzersiz SKU: {uniqueSkuCount}");

        if (skuSuccessCount.Any())
        {
            Console.WriteLine("📈 SKU başına başarı detayları:");
            foreach (var kvp in skuSuccessCount.Take(5))
            {
                Console.WriteLine($"   - {kvp.Key}: {kvp.Value} variant başarılı");
            }
            if (skuSuccessCount.Count > 5)
                Console.WriteLine($"   ... ve {skuSuccessCount.Count - 5} SKU daha");
        }

        // Ham veriyi dosyaya yaz
        try
        {
            string jsonString = JsonSerializer.Serialize(shopifyProducts.RootElement, _jsonOptions);
            File.WriteAllText(filePath, jsonString);
            Console.WriteLine($"📁 Ham ürün verileri {filePath} dosyasına yazıldı");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Dosya yazma hatası: {ex.Message}");
        }
    }


    //
    public async Task<bool> IsSkuExistsAsync(string sku)
    {
        try
        {
            Console.WriteLine($"🔍 SKU kontrol ediliyor: {sku}");

            // GraphQL ile SKU'yu ara
            var query = @"
        {
          productVariants(first: 1, query: ""sku:'" + sku + @"'"") {
            edges {
              node {
                id
                sku
              }
            }
          }
        }";

            var payload = new { query = query };
            var jsonContent = new StringContent(JsonSerializer.Serialize(payload));
            jsonContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            var response = await _client.PostAsync("graphql.json", jsonContent);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"⚠️ GraphQL isteği başarısız: {response.StatusCode}");
                return false;
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonElement>(responseContent);

            // Sonuç var mı kontrol et
            if (result.TryGetProperty("data", out var data) &&
                data.TryGetProperty("productVariants", out var variants) &&
                variants.TryGetProperty("edges", out var edges))
            {
                var edgesArray = edges.EnumerateArray().ToList();

                if (edgesArray.Any())
                {
                    var firstNode = edgesArray.First().GetProperty("node");
                    var foundSku = firstNode.TryGetProperty("sku", out var skuProp)
                        ? skuProp.GetString()
                        : null;

                    Console.WriteLine($" SKU bulundu: {foundSku}");
                    return true;
                }
            }

            Console.WriteLine($"❌ SKU bulunamadı: {sku}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ SKU kontrol hatası: {ex.Message}");
            // Hata durumunda false döndür (güvenli taraf)
            return false;
        }
    }

    //grapql deneme
    // Çoklu SKU durumunu handle eden gelişmiş arama
    public async Task<ProductSearchResult> GetProductBySkuWithDuplicateHandlingAsync(string sku)
    {
        try
        {
            // GraphQL ile tüm eşleşen variant'ları getir
            var query = @"
        {
          productVariants(first: 10, query: ""sku:'" + sku + @"'"") {
            edges {
              node {
                id
                sku
                price
                inventoryQuantity
                product {
                  id
                  title
                  status
                  createdAt
                  updatedAt
                  variants(first: 10) {
                    edges {
                      node {
                        id
                        sku
                      }
                    }
                  }
                }
              }
            }
          }
        }";

            var payload = new { query = query };
            var jsonContent = new StringContent(JsonSerializer.Serialize(payload));
            jsonContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            var response = await _client.PostAsync("graphql.json", jsonContent);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonElement>(responseContent);

            var matches = new List<ProductInfo>();

            if (result.TryGetProperty("data", out var data) &&
                data.TryGetProperty("productVariants", out var variants) &&
                variants.TryGetProperty("edges", out var edges))
            {
                foreach (var edge in edges.EnumerateArray())
                {
                    var node = edge.GetProperty("node");
                    var product = node.GetProperty("product");

                    // Bu ürünün kaç variant'ı var?
                    int totalVariants = 0;
                    if (product.TryGetProperty("variants", out var productVariants) &&
                        productVariants.TryGetProperty("edges", out var variantEdges))
                    {
                        totalVariants = variantEdges.GetArrayLength();
                    }

                    var matchInfo = new ProductInfo
                    {
                        ProductId = product.TryGetProperty("id", out var pId) ? pId.GetString() : null,
                        ProductTitle = product.TryGetProperty("title", out var title) ? title.GetString() : null,
                        ProductStatus = product.TryGetProperty("status", out var status) ? status.GetString() : null,
                        VariantId = node.TryGetProperty("id", out var vId) ? vId.GetString() : null,
                        SKU = sku,
                        Price = node.TryGetProperty("price", out var price) ? price.GetString() : null,
                        InventoryQuantity = node.TryGetProperty("inventoryQuantity", out var stock) ? stock.GetInt32() : 0,
                        CreatedAt = product.TryGetProperty("createdAt", out var created) ? created.GetString() : null,
                        UpdatedAt = product.TryGetProperty("updatedAt", out var updated) ? updated.GetString() : null,
                        TotalVariants = totalVariants,
                        ProductType = totalVariants == 1 ? "SingleProduct" : "MultiVariant",
                        SearchMethod = "GraphQL_Multiple"
                    };

                    matches.Add(matchInfo);
                }
            }

            // Sonuçları analiz et ve en uygun olanı döndür
            return AnalyzeAndSelectBestMatch(sku, matches);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GraphQL çoklu arama hatası: {ex.Message}");
            return new ProductSearchResult
            {
                Found = false,
                SKU = sku,
                Error = ex.Message
            };
        }
    }

    private ProductSearchResult AnalyzeAndSelectBestMatch(string sku, List<ProductInfo> matches)
    {
        if (!matches.Any())
        {
            return new ProductSearchResult
            {
                Found = false,
                SKU = sku,
                Reason = "No matches found"
            };
        }

        if (matches.Count == 1)
        {
            var singleMatch = matches.First();
            return new ProductSearchResult
            {
                Found = true,
                SKU = sku,
                Match = singleMatch,
                AllMatches = matches,
                DuplicateCount = 1,
                Selection = "Single match"
            };
        }

        // Çoklu eşleşme durumu - önceliklendirme yaparak seç
        Console.WriteLine($"⚠️ SKU '{sku}' için {matches.Count} eşleşme bulundu");

        var prioritizedMatches = matches.Select(match =>
        {
            int priority = 0;

            // Öncelik 1: Tekli ürünler öncelikli (genelde asıl ürün budur)
            if (match.ProductType == "SingleProduct")
                priority += 100;

            // Öncelik 2: Active ürünler öncelikli
            if (match.ProductStatus == "active")
                priority += 50;

            // Öncelik 3: Daha yeni ürünler öncelikli
            if (DateTime.TryParse(match.UpdatedAt, out DateTime updatedDate))
            {
                var daysSinceUpdate = (DateTime.UtcNow - updatedDate).TotalDays;
                priority += Math.Max(0, 30 - (int)daysSinceUpdate); // Son 30 gün içinde güncellenenler
            }

            return new { Match = match, Priority = priority };
        }).OrderByDescending(x => x.Priority).ToList();

        var selectedMatch = prioritizedMatches.First().Match;

        // Tüm eşleşmeleri logla
        foreach (var match in matches)
        {
            Console.WriteLine($"  - Product: {match.ProductTitle} | Type: {match.ProductType} | Status: {match.ProductStatus} | ID: {match.ProductId}");
        }

        Console.WriteLine($" Seçilen: {selectedMatch.ProductTitle} ({selectedMatch.ProductType})");

        return new ProductSearchResult
        {
            Found = true,
            SKU = sku,
            Match = selectedMatch,
            AllMatches = matches,
            DuplicateCount = matches.Count,
            Selection = "Prioritized selection",
            SelectionReason = $"Selected {selectedMatch.ProductType} product with status {selectedMatch.ProductStatus}"
        };
    }


    /// <summary>
    /// Sadece varyant fiyatını günceller. VariantId zaten biliniyorsa direkt PUT yapar.
    /// </summary>
    public async Task<bool> UpdateVariantPriceDirectAsync(string productId, string variantId, decimal newPrice)
    {
        try
        {
            var productIdRaw = productId.Replace("gid://shopify/Product/", "");
            var variantIdRaw = variantId.Replace("gid://shopify/ProductVariant/", "");

            var payload = new
            {
                variant = new
                {
                    id = variantIdRaw,
                    price = newPrice.ToString("F2")
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await _client.PutAsync($"products/{productIdRaw}/variants/{variantIdRaw}.json", content);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task UpdateProductTitleAndPriceBySkuAndSaveRawAsync(Guid productId, string sku, string newTitle, decimal newPrice, string filePath)
    {
        var logEntry = new
        {
            Timestamp = DateTimeOffset.Now,
            Sku = sku,
            Title = newTitle,
            Price = newPrice,
            Status = "",
            UpdatedCount = 0,
            UpdatedProducts = new List<string>(),
            ProcessType = "BackgroundService"
        };

        try
        {
            // GetProductBySkuWithDuplicateHandlingAsync metodunu kullanarak ürünü bul
            var searchResult = await GetProductBySkuWithDuplicateHandlingAsync(sku);

            if (!searchResult.Found)
            {
                Console.WriteLine($"SKU '{sku}' bulunamadı. Sebep: {searchResult.Reason}. ExactProductId '{productId}' ile metafield araması yapılıyor...");
                searchResult = await GetProductByExactProductIdAsync(productId);

                if (!searchResult.Found)
                {
                    Console.WriteLine($"ExactProductId '{productId}' ile de ürün bulunamadı. Sebep: {searchResult.Reason}");
                    logEntry = new
                    {
                        Timestamp = DateTimeOffset.Now,
                        Sku = sku,
                        Title = newTitle,
                        Price = newPrice,
                        Status = $"SKU ve ExactProductId ile ürün bulunamadı: {searchResult.Reason}",
                        UpdatedCount = 0,
                        UpdatedProducts = new List<string>(),
                        ProcessType = "BackgroundService"
                    };
                    // Log dosyasına ekle (append) ve çık
                    List<object> logListNotFound;
                    if (File.Exists(filePath))
                    {
                        try
                        {
                            var existing = await File.ReadAllTextAsync(filePath);
                            logListNotFound = JsonSerializer.Deserialize<List<object>>(existing) ?? new List<object>();
                        }
                        catch { logListNotFound = new List<object>(); }
                    }
                    else { logListNotFound = new List<object>(); }
                    logListNotFound.Add(logEntry);
                    await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(logListNotFound, _jsonOptions));
                    Console.WriteLine($"📝 İşlem logu {filePath} dosyasına yazıldı. (Toplam {logListNotFound.Count} kayıt)");
                    return;
                }

                Console.WriteLine($"✅ ExactProductId '{productId}' ile ürün bulundu: {searchResult.Match?.ProductTitle}");
            }

            {
                var allMatches = searchResult.AllMatches ?? new List<ProductInfo> { searchResult.Match };
                var successfulUpdates = new List<string>();
                var failedUpdates = new List<string>();

                Console.WriteLine($"📦 SKU '{sku}' için {allMatches.Count} eşleşme bulundu, hepsini güncelliyorum...");

                foreach (var product in allMatches)
                {
                    try
                    {
                        var productIdToUpdate = product.ProductId.Replace("gid://shopify/Product/", "");
                        var variantIdToUpdate = product.VariantId.Replace("gid://shopify/ProductVariant/", "");
                        var currentTitle = product.ProductTitle;
                        var currentPrice = product.Price;
                        var isMultiVariant = product.ProductType == "MultiVariant";

                        Console.WriteLine($"🔄 Güncelleniyor: {currentTitle} ({product.ProductType}, {product.ProductStatus})");

                        if (isMultiVariant)
                        {
                            // Çoklu varyantlı ürün - sadece varyant price'ı güncelle
                            // 2. Varyant price'ını güncelle
                            var variantPayload = new
                            {
                                variant = new
                                {
                                    id = variantIdToUpdate,
                                    price = newPrice.ToString("F2"),
                                }
                            };

                            var variantJsonContent = new StringContent(JsonSerializer.Serialize(variantPayload));
                            variantJsonContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                            var variantResponse = await _client.PutAsync($"products/{productIdToUpdate}/variants/{variantIdToUpdate}.json", variantJsonContent);
                            variantResponse.EnsureSuccessStatusCode();

                            Console.WriteLine($" MultiVariant güncellendi: {currentTitle}");
                            Console.WriteLine($"   Title: '{currentTitle}' -> '{newTitle}'");
                            Console.WriteLine($"   Price: '{currentPrice}' -> '{newPrice:F2}'");

                            successfulUpdates.Add($"{currentTitle} (MultiVariant-{product.ProductStatus})");
                        }
                        else
                        {
                            // Tekli varyantlı ürün - title, price ve SKU güncelle
                            var currentSku = product.SKU;
                            var payload = new
                            {
                                product = new
                                {
                                    id = productIdToUpdate,
                                    title = newTitle,
                                    variants = new[]
                                    {
                                        new
                                        {
                                            id = variantIdToUpdate,
                                            price = newPrice.ToString("F2"),
                                            sku
                                        }
                                    }
                                }
                            };

                            var jsonContent = new StringContent(JsonSerializer.Serialize(payload));
                            jsonContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                            var response = await _client.PutAsync($"products/{productIdToUpdate}.json", jsonContent);
                            response.EnsureSuccessStatusCode();

                            Console.WriteLine($" SingleProduct güncellendi: {currentTitle}");
                            Console.WriteLine($"   Title: '{currentTitle}' -> '{newTitle}'");
                            Console.WriteLine($"   Price: '{currentPrice}' -> '{newPrice:F2}'");
                            if (currentSku != sku)
                                Console.WriteLine($"   SKU: '{currentSku}' -> '{sku}'");

                            successfulUpdates.Add($"{currentTitle} (SingleProduct-{product.ProductStatus})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Ürün güncellenirken hata: {product.ProductTitle} - {ex.Message}");
                        failedUpdates.Add($"{product.ProductTitle} (HATA: {ex.Message})");
                    }
                }

                // Sonuç raporu
                var statusMessage = $"Toplam {allMatches.Count} ürün - {successfulUpdates.Count} başarılı, {failedUpdates.Count} başarısız";

                if (successfulUpdates.Any())
                {
                    Console.WriteLine($"🎯 Başarılı güncellemeler: {string.Join(", ", successfulUpdates)}");
                }

                if (failedUpdates.Any())
                {
                    Console.WriteLine($"⚠️ Başarısız güncellemeler: {string.Join(", ", failedUpdates)}");
                }

                logEntry = new
                {
                    Timestamp = DateTimeOffset.Now,
                    Sku = sku,
                    Title = newTitle,
                    Price = newPrice,
                    Status = statusMessage,
                    UpdatedCount = successfulUpdates.Count,
                    UpdatedProducts = successfulUpdates,
                    ProcessType = "BackgroundService"
                };
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Genel hata: {ex.Message}");
            logEntry = new
            {
                Timestamp = DateTimeOffset.Now,
                Sku = sku,
                Title = newTitle,
                Price = newPrice,
                Status = $"Genel hata: {ex.Message}",
                UpdatedCount = 0,
                UpdatedProducts = new List<string>(),
                ProcessType = "BackgroundService"
            };
        }
        // Log dosyasına ekle (append) — geçmiş kayıtlar silinmez
        List<object> logList;
        if (File.Exists(filePath))
        {
            try
            {
                var existing = await File.ReadAllTextAsync(filePath);
                logList = JsonSerializer.Deserialize<List<object>>(existing) ?? new List<object>();
            }
            catch
            {
                logList = new List<object>();
            }
        }
        else
        {
            logList = new List<object>();
        }
        logList.Add(logEntry);
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(logList, _jsonOptions));
        Console.WriteLine($"📝 İşlem logu {filePath} dosyasına yazıldı. (Toplam {logList.Count} kayıt)");
    }


    //NoDiscount Etiketi
    public async Task SyncNoDiscountTagBySkuAsync(Guid productId, string sku, bool isNoDiscount)
    {
        try
        {
            var searchResult = await GetProductBySkuWithDuplicateHandlingAsync(sku);

            if (!searchResult.Found)
            {
                Console.WriteLine($"SKU '{sku}' bulunamadı, ExactProductId '{productId}' ile aranıyor...");
                searchResult = await GetProductByExactProductIdAsync(productId);

                if (!searchResult.Found)
                {
                    Console.WriteLine($"ExactProductId '{productId}' ile de ürün bulunamadı: {searchResult.Reason}");
                    return;
                }

                Console.WriteLine($"✅ ExactProductId '{productId}' ile ürün bulundu: {searchResult.Match?.ProductTitle}");
            }

            var allMatches = searchResult.AllMatches ?? [searchResult.Match];

            foreach (var product in allMatches)
            {
                try
                {
                    var productIdToUpdate = product.ProductId.Replace("gid://shopify/Product/", "");

                    // Mevcut tag'ları oku
                    var getResponse = await _client.GetAsync($"products/{productIdToUpdate}.json?fields=id,tags");
                    getResponse.EnsureSuccessStatusCode();
                    var getJson = await getResponse.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(getJson);
                    var existingTags = doc.RootElement
                        .GetProperty("product")
                        .GetProperty("tags")
                        .GetString() ?? "";

                    var tagList = existingTags
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList();

                    bool hasTag = tagList.Any(t => t.Equals("nodiscount", StringComparison.OrdinalIgnoreCase));

                    if (isNoDiscount && hasTag)
                    {
                        Console.WriteLine($"ℹ️ '{product.ProductTitle}' zaten 'nodiscount' tag'ına sahip, atlanıyor.");
                        continue;
                    }

                    if (!isNoDiscount && !hasTag)
                    {
                        Console.WriteLine($"ℹ️ '{product.ProductTitle}' zaten 'nodiscount' tag'ı yok, atlanıyor.");
                        continue;
                    }

                    if (isNoDiscount)
                        tagList.Add("nodiscount");
                    else
                        tagList.RemoveAll(t => t.Equals("nodiscount", StringComparison.OrdinalIgnoreCase));

                    var newTags = string.Join(", ", tagList);

                    var payload = new
                    {
                        product = new
                        {
                            id = productIdToUpdate,
                            tags = newTags
                        }
                    };

                    var jsonContent = new StringContent(JsonSerializer.Serialize(payload));
                    jsonContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                    var response = await _client.PutAsync($"products/{productIdToUpdate}.json", jsonContent);
                    response.EnsureSuccessStatusCode();

                    var action = isNoDiscount ? "eklendi" : "silindi";
                    Console.WriteLine($"✅ '{product.ProductTitle}' ürününde 'nodiscount' tag'ı {action}. Yeni tag'lar: {newTags}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ '{product.ProductTitle}' tag güncellenirken hata: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ SyncNoDiscountTagBySkuAsync genel hata (SKU: {sku}): {ex.Message}");
        }
    }

    public async Task<List<ProcessResult>> UpdateProductStatusBySkuListAndSaveRawAsync(List<string> skuList, string filePath)
    {
        var logEntries = new List<ProcessResult>();

        foreach (string sku in skuList)
        {
            var logEntry = await ProcessSingleSkuAsync(sku);
            logEntries.Add(logEntry);
        }

        // Tüm log'ları tek dosyaya yaz
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(logEntries, jsonOptions));
        Console.WriteLine($"Toplam {logEntries.Count} işlem logu {filePath} dosyasına yazıldı.");

        return logEntries;
    }

    public async Task<ProcessResult> ProcessSingleSkuAsync(string sku)
    {
        try
        {
            // SKU'ya göre ürün ara
            var searchResult = await GetProductBySkuWithDuplicateHandlingAsync(sku);

            // Eğer ürün bulunamadıysa
            if (!searchResult.Found)
            {
                Console.WriteLine($"SKU '{sku}' bulunamadı.");
                return new ProcessResult
                {
                    Timestamp = DateTimeOffset.Now,
                    Sku = sku,
                    Status = "SKU bulunamadı",
                    ProcessType = "BackgroundService"
                };
            }

            var processResults = new List<ProcessResult>();

            // AllMatches'deki tüm ürünleri işle
            if (searchResult.AllMatches != null && searchResult.AllMatches.Any())
            {
                Console.WriteLine($"SKU '{sku}' için {searchResult.AllMatches.Count} adet eşleşme bulundu, hepsi işlenecek.");

                // Her match'i ayrı ayrı işle
                foreach (var productInfo in searchResult.AllMatches)
                {
                    Console.WriteLine($"İşleniyor: {productInfo.ProductTitle} (ID: {productInfo.ProductId}) - Type: {productInfo.ProductType}");

                    // ID'lerden Shopify prefix'lerini temizle
                    var cleanProductInfo = new ProductInfo
                    {
                        ProductId = productInfo.ProductId?.Replace("gid://shopify/Product/", ""),
                        ProductTitle = productInfo.ProductTitle,
                        ProductStatus = productInfo.ProductStatus,
                        VariantId = productInfo.VariantId?.Replace("gid://shopify/ProductVariant/", ""),
                        SKU = productInfo.SKU,
                        Price = productInfo.Price,
                        InventoryQuantity = productInfo.InventoryQuantity,
                        CreatedAt = productInfo.CreatedAt,
                        UpdatedAt = productInfo.UpdatedAt,
                        TotalVariants = productInfo.TotalVariants,
                        ProductType = productInfo.ProductType,
                        SearchMethod = productInfo.SearchMethod
                    };

                    // ProductType'a göre karar ver
                    ProcessResult processResult;
                    if (cleanProductInfo.ProductType != null &&
                        cleanProductInfo.ProductType.Equals("SingleProduct", StringComparison.OrdinalIgnoreCase))
                    {
                        // Tekli ürün - archived yap
                        Console.WriteLine($"SKU '{sku}' tekli ürün olarak tespit edildi (TotalVariants: {cleanProductInfo.TotalVariants}), arşivleniyor...");
                        processResult = await ArchiveProductAsync(sku, cleanProductInfo);
                    }
                    else
                    {
                        // Çoklu varyant - varyant silme işlemi  
                        Console.WriteLine($"SKU '{sku}' çoklu varyant ürün olarak tespit edildi (TotalVariants: {cleanProductInfo.TotalVariants}), varyant siliniyor...");
                        processResult = await DeleteVariantAsync(sku, cleanProductInfo);
                    }

                    processResults.Add(processResult);
                }
            }
            else
            {
                Console.WriteLine($"SKU '{sku}' için işlenebilir match bulunamadı.");
                return new ProcessResult
                {
                    Timestamp = DateTimeOffset.Now,
                    Sku = sku,
                    Status = "İşlenebilir match bulunamadı",
                    ProcessType = "BackgroundService"
                };
            }

            // Eğer birden fazla işlem yapıldıysa, hepsini birleştir
            if (processResults.Count == 1)
            {
                return processResults.First();
            }
            else
            {
                // Çoklu işlem durumu - özet döndür
                var successCount = 0;
                var errorCount = 0;
                var statuses = new List<string>();

                foreach (var result in processResults)
                {
                    statuses.Add(result.Status ?? "Unknown");
                    if (result.Status != null && (result.Status.Contains("hata") || result.Status.Contains("hatası")))
                    {
                        errorCount++;
                    }
                    else
                    {
                        successCount++;
                    }
                }

                return new ProcessResult
                {
                    Timestamp = DateTimeOffset.Now,
                    Sku = sku,
                    Status = $"Çoklu işlem tamamlandı - Başarılı: {successCount}, Hatalı: {errorCount}",
                    ProcessType = "BackgroundService",
                    TotalProcessed = processResults.Count,
                    DetailedResults = processResults,
                    StatusSummary = string.Join("; ", statuses)
                };
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SKU '{sku}' işlenirken genel hata oluştu: {ex.Message}");
            return new ProcessResult
            {
                Timestamp = DateTimeOffset.Now,
                Sku = sku,
                Status = $"Genel hata: {ex.Message}",
                ProcessType = "BackgroundService"
            };
        }
    }

    public async Task<ProcessResult> ActiveOrPassif(string sku, decimal isWebshopItem)
    {
        try
        {
            // SKU'ya göre ürün ara
            var searchResult = await GetProductBySkuWithDuplicateHandlingAsync(sku);

            // Eğer ürün bulunamadıysa
            if (!searchResult.Found)
            {
                Console.WriteLine($"SKU '{sku}' bulunamadı.");
                return new ProcessResult
                {
                    Timestamp = DateTimeOffset.Now,
                    Sku = sku,
                    Status = "SKU bulunamadı",
                    ProcessType = "BackgroundService"
                };
            }

            var processResults = new List<ProcessResult>();

            // AllMatches'deki tüm ürünleri işle
            if (searchResult.AllMatches != null && searchResult.AllMatches.Any())
            {
                Console.WriteLine($"SKU '{sku}' için {searchResult.AllMatches.Count} adet eşleşme bulundu, hepsi işlenecek.");

                // Her match'i ayrı ayrı işle
                foreach (var productInfo in searchResult.AllMatches)
                {
                    Console.WriteLine($"İşleniyor: {productInfo.ProductTitle} (ID: {productInfo.ProductId}) - Type: {productInfo.ProductType}");

                    // ID'lerden Shopify prefix'lerini temizle
                    var cleanProductInfo = new ProductInfo
                    {
                        ProductId = productInfo.ProductId?.Replace("gid://shopify/Product/", ""),
                        ProductTitle = productInfo.ProductTitle,
                        ProductStatus = productInfo.ProductStatus,
                        VariantId = productInfo.VariantId?.Replace("gid://shopify/ProductVariant/", ""),
                        SKU = productInfo.SKU,
                        Price = productInfo.Price,
                        InventoryQuantity = productInfo.InventoryQuantity,
                        CreatedAt = productInfo.CreatedAt,
                        UpdatedAt = productInfo.UpdatedAt,
                        TotalVariants = productInfo.TotalVariants,
                        ProductType = productInfo.ProductType,
                        SearchMethod = productInfo.SearchMethod
                    };

                    ProcessResult processResult = new();
                    if (isWebshopItem == 0)
                    {
                        // ProductType'a göre karar ver

                        if (cleanProductInfo.ProductType != null &&
                            cleanProductInfo.ProductType.Equals("SingleProduct", StringComparison.OrdinalIgnoreCase))
                        {
                            // Tekli ürün - archived yap
                            Console.WriteLine($"SKU '{sku}' tekli ürün olarak tespit edildi (TotalVariants: {cleanProductInfo.TotalVariants}), arşivleniyor...");
                            processResult = await ArchiveProductAsync(sku, cleanProductInfo);
                        }
                        else
                        {
                            // Çoklu varyant - varyant silme işlemi
                            Console.WriteLine($"SKU '{sku}' çoklu varyant ürün olarak tespit edildi (TotalVariants: {cleanProductInfo.TotalVariants}), varyant siliniyor...");
                            processResult = await DeleteVariantAsync(sku, cleanProductInfo);
                        }
                    }
                    // else
                    // {
                    //     // Aktife çekme: Eğer aynı SKU hem SingleProduct hem MultiVariant olarak bulunduysa,
                    //     // SingleProduct olanı atlıyoruz (sadece MultiVariant aktife çekilir)
                    //     bool hasBothTypes = searchResult.AllMatches.Any(m =>
                    //         m.ProductType != null && m.ProductType.Equals("SingleProduct", StringComparison.OrdinalIgnoreCase)) &&
                    //         searchResult.AllMatches.Any(m =>
                    //         m.ProductType != null && m.ProductType.Equals("MultiVariant", StringComparison.OrdinalIgnoreCase));

                    //     if (hasBothTypes && cleanProductInfo.ProductType != null &&
                    //         cleanProductInfo.ProductType.Equals("SingleProduct", StringComparison.OrdinalIgnoreCase))
                    //     {
                    //         Console.WriteLine($"SKU '{sku}' hem ana ürün hem varyant olarak mevcut. SingleProduct (ID: {cleanProductInfo.ProductId}) aktife çekilmeyecek, atlanıyor.");
                    //         processResult = new ProcessResult
                    //         {
                    //             Timestamp = DateTimeOffset.Now,
                    //             Sku = sku,
                    //             Status = "Atlandı - Aynı SKU için MultiVariant mevcut olduğundan SingleProduct aktife çekilmedi",
                    //             ProcessType = "BackgroundService",
                    //             ProductId = cleanProductInfo.ProductId
                    //         };
                    //     }
                    //     else
                    //     {
                    //          Console.WriteLine($"SKU '{sku}' ürün active etmeye düştü");
                    //         processResult = await ActiveProductAsync(sku, cleanProductInfo);
                    //     }
                    // }



                    processResults.Add(processResult);
                }
            }
            else
            {
                Console.WriteLine($"SKU '{sku}' için işlenebilir match bulunamadı.");
                return new ProcessResult
                {
                    Timestamp = DateTimeOffset.Now,
                    Sku = sku,
                    Status = "İşlenebilir match bulunamadı",
                    ProcessType = "BackgroundService"
                };
            }

            // Eğer birden fazla işlem yapıldıysa, hepsini birleştir
            if (processResults.Count == 1)
            {
                return processResults.First();
            }
            else
            {
                // Çoklu işlem durumu - özet döndür
                var successCount = 0;
                var errorCount = 0;
                var statuses = new List<string>();

                foreach (var result in processResults)
                {
                    statuses.Add(result.Status ?? "Unknown");
                    if (result.Status != null && (result.Status.Contains("hata") || result.Status.Contains("hatası")))
                    {
                        errorCount++;
                    }
                    else
                    {
                        successCount++;
                    }
                }

                return new ProcessResult
                {
                    Timestamp = DateTimeOffset.Now,
                    Sku = sku,
                    Status = $"Çoklu işlem tamamlandı - Başarılı: {successCount}, Hatalı: {errorCount}",
                    ProcessType = "BackgroundService",
                    TotalProcessed = processResults.Count,
                    DetailedResults = processResults,
                    StatusSummary = string.Join("; ", statuses)
                };
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SKU '{sku}' işlenirken genel hata oluştu: {ex.Message}");
            return new ProcessResult
            {
                Timestamp = DateTimeOffset.Now,
                Sku = sku,
                Status = $"Genel hata: {ex.Message}",
                ProcessType = "BackgroundService"
            };
        }
    }

    private async Task<ProcessResult> DeleteVariantAsync(string sku, ProductInfo productInfo)
    {
        try
        {
            Console.WriteLine($"Varyant siliniyor - ProductId: {productInfo.ProductId}, VariantId: {productInfo.VariantId}");

            var response = await _client.DeleteAsync($"products/{productInfo.ProductId}/variants/{productInfo.VariantId}.json");
            response.EnsureSuccessStatusCode();

            Console.WriteLine($"SKU '{sku}' varyantı başarıyla silindi. Ürün ve diğer varyantlar korundu.");
            return new ProcessResult
            {
                Timestamp = DateTimeOffset.Now,
                Sku = sku,
                Status = "Varyant silindi",
                ProcessType = "BackgroundService",
                ProductId = productInfo.ProductId,
                VariantId = productInfo.VariantId
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Varyant silinirken hata oluştu: {ex.Message}");
            return new ProcessResult
            {
                Timestamp = DateTimeOffset.Now,
                Sku = sku,
                Status = $"Varyant silme hatası: {ex.Message}",
                ProcessType = "BackgroundService"
            };
        }
    }

    private async Task<ProcessResult> ArchiveProductAsync(string sku, ProductInfo productInfo)
    {
        if (productInfo.ProductStatus != null &&
            (productInfo.ProductStatus.Equals("active", StringComparison.OrdinalIgnoreCase) ||
             productInfo.ProductStatus.Equals("actıve", StringComparison.OrdinalIgnoreCase)))
        {
            var payload = new
            {
                product = new
                {
                    id = productInfo.ProductId,
                    status = "archived"
                }
            };

            var jsonContent = new StringContent(JsonSerializer.Serialize(payload));
            jsonContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            try
            {
                Console.WriteLine($"Ürün arşivleniyor - ProductId: {productInfo.ProductId}");

                var response = await _client.PutAsync($"products/{productInfo.ProductId}.json", jsonContent);
                response.EnsureSuccessStatusCode();

                Console.WriteLine($"SKU '{sku}' için tekli ürün durumu 'archived' olarak güncellendi.");
                return new ProcessResult
                {
                    Timestamp = DateTimeOffset.Now,
                    Sku = sku,
                    Status = "Tekli ürün archived",
                    ProcessType = "BackgroundService",
                    ProductId = productInfo.ProductId
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ürün archived yaparken hata oluştu: {ex.Message}");
                return new ProcessResult
                {
                    Timestamp = DateTimeOffset.Now,
                    Sku = sku,
                    Status = $"Archive hatası: {ex.Message}",
                    ProcessType = "BackgroundService"
                };
            }
        }
        else
        {
            Console.WriteLine($"SKU '{sku}' için tekli ürün zaten '{productInfo.ProductStatus}' durumunda.");
            return new ProcessResult
            {
                Timestamp = DateTimeOffset.Now,
                Sku = sku,
                Status = $"Tekli ürün zaten '{productInfo.ProductStatus}' durumunda",
                ProcessType = "BackgroundService"
            };
        }
    }
    //şimdilik kullanmıyorum
    public async Task<ProcessResult> ActiveProductAsync(string sku, ProductInfo productInfo)
    {
        var payload = new
        {
            product = new
            {
                id = productInfo.ProductId,
                status = "archived"
            }
        };

        var jsonContent = new StringContent(JsonSerializer.Serialize(payload));
        jsonContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        try
        {
            Console.WriteLine($"Ürün arşivleniyor - ProductId: {productInfo.ProductId}");

            var response = await _client.PutAsync($"products/{productInfo.ProductId}.json", jsonContent);
            response.EnsureSuccessStatusCode();

            Console.WriteLine($"SKU '{sku}' için tekli ürün durumu 'active' olarak güncellendi.");
            return new ProcessResult
            {
                Timestamp = DateTimeOffset.Now,
                Sku = sku,
                Status = "Tekli ürün active",
                ProcessType = "BackgroundService",
                ProductId = productInfo.ProductId
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ürün active yaparken hata oluştu: {ex.Message}");
            return new ProcessResult
            {
                Timestamp = DateTimeOffset.Now,
                Sku = sku,
                Status = $"active hatası: {ex.Message}",
                ProcessType = "BackgroundService"
            };
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


    // Helper metodlar
    private decimal? TryParseWeight(string weightString)
    {
        if (string.IsNullOrWhiteSpace(weightString))
            return null;

        if (decimal.TryParse(weightString, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out decimal weight))
        {
            return weight;
        }

        return null;
    }

    private bool ParseTaxable(string isTaxableItem)
    {
        if (string.IsNullOrWhiteSpace(isTaxableItem))
            return true; // Varsayılan olarak vergiye tabi

        // "1", "true", "yes" gibi değerleri kontrol et
        return isTaxableItem == "1" ||
               isTaxableItem.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               isTaxableItem.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// custom.exact_product_id metafield değeri verilen exactProductId'e eşit olan Shopify ürününü GraphQL ile arar.
    /// </summary>
    public async Task<ProductSearchResult> GetProductByExactProductIdAsync(Guid exactProductId)
    {
        var exactIdStr = exactProductId.ToString();
        try
        {
            // 1. Önce yerel cache'e bak — anında sonuç
            var cachedProductId = await ExactProductIdMetafieldSyncService.GetFromCacheAsync(exactIdStr);
            if (!string.IsNullOrEmpty(cachedProductId))
            {
                Console.WriteLine($"⚡ Cache hit: ExactProductId '{exactIdStr}' → {cachedProductId}");
                var cachedInfo = await GetProductInfoByShopifyIdAsync(cachedProductId);
                if (cachedInfo != null)
                {
                    cachedInfo.SearchMethod = "Cache_ExactProductId";
                    return new ProductSearchResult
                    {
                        Found = true,
                        Match = cachedInfo,
                        AllMatches = [cachedInfo],
                        DuplicateCount = 1,
                        Selection = "Cache hit"
                    };
                }
            }

            // 2. Cache'de yoksa Shopify'ı tara, bulunca cache'e kaydet
            var graphqlResult = await SearchProductByMetafieldGraphQLAsync(exactIdStr);
            if (graphqlResult.Found && graphqlResult.Match?.ProductId != null)
                await ExactProductIdMetafieldSyncService.SaveToCacheAsync(exactIdStr, graphqlResult.Match.ProductId);

            if (graphqlResult.Found)
                return graphqlResult;

            return new ProductSearchResult { Found = false, Reason = $"ExactProductId '{exactIdStr}' için metafield eşleşmesi bulunamadı" };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GetProductByExactProductIdAsync hatası: {ex.Message}");
            return new ProductSearchResult { Found = false, Error = ex.Message };
        }
    }

    private async Task<ProductInfo> GetProductInfoByShopifyIdAsync(string shopifyProductId)
    {
        try
        {
            var gid = shopifyProductId.StartsWith("gid://") ? shopifyProductId : $"gid://shopify/Product/{shopifyProductId}";
            var query = $@"
{{
  product(id: ""{gid}"") {{
    id
    title
    status
    createdAt
    updatedAt
    variants(first: 10) {{
      edges {{
        node {{
          id
          sku
          price
          inventoryQuantity
        }}
      }}
    }}
  }}
}}";
            var payload = new { query };
            var jsonContent = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
            var response = await _client.PostAsync("graphql.json", jsonContent);
            if (!response.IsSuccessStatusCode) return null;

            var responseContent = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonElement>(responseContent);

            if (!result.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("product", out var node) ||
                node.ValueKind == JsonValueKind.Null)
                return null;

            var variantEdges = node.TryGetProperty("variants", out var variantsEl) &&
                               variantsEl.TryGetProperty("edges", out var ve) ? ve : default;
            int totalVariants = variantEdges.ValueKind == JsonValueKind.Array ? variantEdges.GetArrayLength() : 0;
            string variantId = null, variantSku = null, variantPrice = null;
            int inventoryQty = 0;
            if (variantEdges.ValueKind == JsonValueKind.Array)
            {
                foreach (var ve2 in variantEdges.EnumerateArray())
                {
                    var vNode = ve2.GetProperty("node");
                    variantId = vNode.TryGetProperty("id", out var vid) ? vid.GetString() : null;
                    variantSku = vNode.TryGetProperty("sku", out var vsku) ? vsku.GetString() : null;
                    variantPrice = vNode.TryGetProperty("price", out var vprice) ? vprice.GetString() : null;
                    inventoryQty = vNode.TryGetProperty("inventoryQuantity", out var vqty) ? vqty.GetInt32() : 0;
                    break;
                }
            }

            return new ProductInfo
            {
                ProductId = node.TryGetProperty("id", out var pId) ? pId.GetString() : null,
                ProductTitle = node.TryGetProperty("title", out var title) ? title.GetString() : null,
                ProductStatus = node.TryGetProperty("status", out var status) ? status.GetString() : null,
                VariantId = variantId,
                SKU = variantSku,
                Price = variantPrice,
                InventoryQuantity = inventoryQty,
                CreatedAt = node.TryGetProperty("createdAt", out var created) ? created.GetString() : null,
                UpdatedAt = node.TryGetProperty("updatedAt", out var updated) ? updated.GetString() : null,
                TotalVariants = totalVariants,
                ProductType = totalVariants == 1 ? "SingleProduct" : "MultiVariant"
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<ProductSearchResult> SearchProductByMetafieldGraphQLAsync(string exactIdStr)
    {
        // Shopify GraphQL products query'sinde metafield filtresi UUID için çalışmıyor.
        // Bunun yerine cursor-based pagination ile tüm ürünleri tara,
        // her ürünün metafield değerini kontrol et.
        // Ama bu çok yavaş (binlerce ürün varsa).
        //
        // Daha akıllıca: önce ilk 250 ürünü dene, bulamazsan devam et.
        // Çoğu durumda metafield sync çalıştıysa ilk turda bulunur.

        string cursor = null;
        int pageCount = 0;
        const int maxPages = 40; // 40 * 250 = 10.000 ürün limiti

        while (pageCount < maxPages)
        {
            pageCount++;
            var afterClause = cursor != null ? $", after: \"{cursor}\"" : "";

            var query = $@"
{{
  products(first: 250{afterClause}) {{
    pageInfo {{
      hasNextPage
      endCursor
    }}
    edges {{
      node {{
        id
        title
        status
        createdAt
        updatedAt
        metafield(namespace: ""custom"", key: ""exact_product_id"") {{
          value
        }}
        variants(first: 10) {{
          edges {{
            node {{
              id
              sku
              price
              inventoryQuantity
            }}
          }}
        }}
      }}
    }}
  }}
}}";

            var payload = new { query };
            var jsonContent = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
            var response = await _client.PostAsync("graphql.json", jsonContent);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonElement>(responseContent);

            if (!result.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("products", out var products) ||
                !products.TryGetProperty("edges", out var edges))
                break;

            foreach (var edge in edges.EnumerateArray())
            {
                var node = edge.GetProperty("node");

                // Metafield değerini oku ve tam eşleşme kontrol et
                if (!node.TryGetProperty("metafield", out var metafieldEl) ||
                    metafieldEl.ValueKind == JsonValueKind.Null ||
                    !metafieldEl.TryGetProperty("value", out var metafieldValue))
                    continue;

                var metafieldStr = metafieldValue.GetString();
                if (!string.Equals(metafieldStr, exactIdStr, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Tam eşleşme bulundu
                var variantEdges = node.TryGetProperty("variants", out var variantsEl) &&
                                   variantsEl.TryGetProperty("edges", out var ve) ? ve : default;

                int totalVariants = variantEdges.ValueKind == JsonValueKind.Array ? variantEdges.GetArrayLength() : 0;
                string variantId = null, variantSku = null, variantPrice = null;
                int inventoryQty = 0;

                if (variantEdges.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ve2 in variantEdges.EnumerateArray())
                    {
                        var vNode = ve2.GetProperty("node");
                        variantId = vNode.TryGetProperty("id", out var vid) ? vid.GetString() : null;
                        variantSku = vNode.TryGetProperty("sku", out var vsku) ? vsku.GetString() : null;
                        variantPrice = vNode.TryGetProperty("price", out var vprice) ? vprice.GetString() : null;
                        inventoryQty = vNode.TryGetProperty("inventoryQuantity", out var vqty) ? vqty.GetInt32() : 0;
                        break;
                    }
                }

                var match = new ProductInfo
                {
                    ProductId = node.TryGetProperty("id", out var pId) ? pId.GetString() : null,
                    ProductTitle = node.TryGetProperty("title", out var title) ? title.GetString() : null,
                    ProductStatus = node.TryGetProperty("status", out var status) ? status.GetString() : null,
                    VariantId = variantId,
                    SKU = variantSku,
                    Price = variantPrice,
                    InventoryQuantity = inventoryQty,
                    CreatedAt = node.TryGetProperty("createdAt", out var created) ? created.GetString() : null,
                    UpdatedAt = node.TryGetProperty("updatedAt", out var updated) ? updated.GetString() : null,
                    TotalVariants = totalVariants,
                    ProductType = totalVariants == 1 ? "SingleProduct" : "MultiVariant",
                    SearchMethod = "GraphQL_MetafieldScan"
                };

                Console.WriteLine($"✅ ExactProductId '{exactIdStr}' → Shopify ürünü bulundu: {match.ProductTitle} (sayfa {pageCount})");

                return new ProductSearchResult
                {
                    Found = true,
                    Match = match,
                    AllMatches = [match],
                    DuplicateCount = 1,
                    Selection = "Exact metafield match"
                };
            }

            // Sonraki sayfa var mı?
            if (!products.TryGetProperty("pageInfo", out var pageInfo) ||
                !pageInfo.TryGetProperty("hasNextPage", out var hasNext) ||
                !hasNext.GetBoolean())
                break;

            cursor = pageInfo.TryGetProperty("endCursor", out var endCursor) ? endCursor.GetString() : null;
            if (cursor == null) break;

            // Shopify rate limit için kısa bekleme
            await Task.Delay(200);
        }

        return new ProductSearchResult { Found = false };
    }

    /// <summary>
    /// Shopify ürününün custom.exact_product_id metafield'ını GraphQL ile günceller.
    /// </summary>
    public async Task<bool> UpdateProductExactIdMetafieldAsync(string shopifyProductId, string exactId)
    {
        try
        {
            // Shopify GraphQL product ID formatı: "gid://shopify/Product/1234567890"
            var gid = shopifyProductId.StartsWith("gid://") ? shopifyProductId : $"gid://shopify/Product/{shopifyProductId}";

            var mutation = @"
mutation productUpdate($input: ProductInput!) {
  productUpdate(input: $input) {
    product {
      id
      metafields(first: 5) {
        edges {
          node {
            namespace
            key
            value
          }
        }
      }
    }
    userErrors {
      field
      message
    }
  }
}";

            var variables = new
            {
                input = new
                {
                    id = gid,
                    metafields = new[]
                    {
                        new
                        {
                            @namespace = "custom",
                            key = "exact_product_id",
                            value = exactId,
                            type = "single_line_text_field"
                        }
                    }
                }
            };

            var payload = new { query = mutation, variables };
            var jsonContent = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

            var response = await _client.PostAsync("graphql.json", jsonContent);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"❌ Metafield GraphQL isteği başarısız: {response.StatusCode}");
                return false;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseBody);

            // userErrors kontrolü
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("productUpdate", out var productUpdate) &&
                productUpdate.TryGetProperty("userErrors", out var userErrors) &&
                userErrors.GetArrayLength() > 0)
            {
                var firstError = userErrors[0];
                var errMsg = firstError.TryGetProperty("message", out var msg) ? msg.GetString() : "Bilinmeyen hata";
                Console.WriteLine($"❌ Shopify metafield userError: {errMsg}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ UpdateProductExactIdMetafieldAsync hatası: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Email adresine göre Shopify customer ID'sini GraphQL ile döner. Bulunamazsa null.
    /// </summary>
    public async Task<string> GetShopifyCustomerIdByEmailAsync(string email)
    {
        try
        {
            var query = $@"{{
  customers(first: 1, query: ""email:{email}"") {{
    edges {{
      node {{
        id
      }}
    }}
  }}
}}";

            var payload = new { query };
            var jsonContent = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

            var response = await _client.PostAsync("graphql.json", jsonContent);
            if (!response.IsSuccessStatusCode) return null;

            var responseBody = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseBody);

            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("customers", out var customers) &&
                customers.TryGetProperty("edges", out var edges) &&
                edges.GetArrayLength() > 0)
            {
                var node = edges[0].GetProperty("node");
                var gid = node.GetProperty("id").GetString();
                // gid://shopify/Customer/123456 -> 123456
                return gid?.Split('/').Last();
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ GetShopifyCustomerIdByEmailAsync hatası: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Shopify customer'ının custom.exact_customer_id metafield'ını GraphQL ile günceller.
    /// </summary>
    public async Task<bool> UpdateCustomerExactIdMetafieldAsync(string shopifyCustomerId, string exactId)
    {
        try
        {
            var gid = shopifyCustomerId.StartsWith("gid://") ? shopifyCustomerId : $"gid://shopify/Customer/{shopifyCustomerId}";

            var mutation = @"
mutation customerUpdate($input: CustomerInput!) {
  customerUpdate(input: $input) {
    customer {
      id
    }
    userErrors {
      field
      message
    }
  }
}";

            var variables = new
            {
                input = new
                {
                    id = gid,
                    metafields = new[]
                    {
                        new
                        {
                            @namespace = "custom",
                            key = "exact_customer_id",
                            value = exactId,
                            type = "single_line_text_field"
                        }
                    }
                }
            };

            var payload = new { query = mutation, variables };
            var jsonContent = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

            var response = await _client.PostAsync("graphql.json", jsonContent);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"❌ Customer metafield GraphQL isteği başarısız: {response.StatusCode}");
                return false;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseBody);

            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("customerUpdate", out var customerUpdate) &&
                customerUpdate.TryGetProperty("userErrors", out var userErrors) &&
                userErrors.GetArrayLength() > 0)
            {
                var firstError = userErrors[0];
                var errMsg = firstError.TryGetProperty("message", out var msg) ? msg.GetString() : "Bilinmeyen hata";
                Console.WriteLine($"❌ Shopify customer metafield userError: {errMsg}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ UpdateCustomerExactIdMetafieldAsync hatası: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Shopify customer'ının custom.exact_discount_code metafield'ını GraphQL ile günceller.
    /// </summary>
    public async Task<bool> UpdateCustomerDiscountCodeMetafieldAsync(string shopifyCustomerId, string discountCode)
    {
        try
        {
            var gid = shopifyCustomerId.StartsWith("gid://") ? shopifyCustomerId : $"gid://shopify/Customer/{shopifyCustomerId}";

            var mutation = @"
mutation customerUpdate($input: CustomerInput!) {
  customerUpdate(input: $input) {
    customer {
      id
    }
    userErrors {
      field
      message
    }
  }
}";

            var variables = new
            {
                input = new
                {
                    id = gid,
                    metafields = new[]
                    {
                        new
                        {
                            @namespace = "custom",
                            key = "exact_discount_code",
                            value = discountCode ?? "",
                            type = "single_line_text_field"
                        }
                    }
                }
            };

            var payload = new { query = mutation, variables };
            var jsonContent = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

            var response = await _client.PostAsync("graphql.json", jsonContent);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"❌ Customer discount code metafield GraphQL isteği başarısız: {response.StatusCode}");
                return false;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseBody);

            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("customerUpdate", out var customerUpdate) &&
                customerUpdate.TryGetProperty("userErrors", out var userErrors) &&
                userErrors.GetArrayLength() > 0)
            {
                var firstError = userErrors[0];
                var errMsg = firstError.TryGetProperty("message", out var msg) ? msg.GetString() : "Bilinmeyen hata";
                Console.WriteLine($"❌ Shopify customer discount code metafield userError: {errMsg}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ UpdateCustomerDiscountCodeMetafieldAsync hatası: {ex.Message}");
            return false;
        }
    }

    // Helper class
    public class ProductInfo
    {
        public string ProductId { get; set; }
        public string ProductTitle { get; set; }
        public string ProductStatus { get; set; }
        public string VariantId { get; set; }
        public string SKU { get; set; }
        public string Price { get; set; }
        public int InventoryQuantity { get; set; }
        public string CreatedAt { get; set; }
        public string UpdatedAt { get; set; }
        public int TotalVariants { get; set; }
        public string ProductType { get; set; }
        public string SearchMethod { get; set; }
    }

    public class ProductSearchResult
    {
        public bool Found { get; set; }
        public string SKU { get; set; }
        public ProductInfo Match { get; set; }
        public List<ProductInfo> AllMatches { get; set; } = new List<ProductInfo>();
        public int DuplicateCount { get; set; }
        public string Selection { get; set; }
        public string SelectionReason { get; set; }
        public string Reason { get; set; }
        public string Error { get; set; }
    }

    public class ProcessResult
    {
        public DateTimeOffset Timestamp { get; set; }
        public string Sku { get; set; }
        public string Status { get; set; }
        public string ProcessType { get; set; }
        public string ProductId { get; set; }
        public string VariantId { get; set; }
        public int TotalProcessed { get; set; }
        public List<ProcessResult> DetailedResults { get; set; }
        public string StatusSummary { get; set; }
    }
}
