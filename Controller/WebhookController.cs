using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Net.Http.Headers;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using ShopifyProductApp.Services;
using ExactOnline.Models;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ShopifyProductApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class WebhookController : ControllerBase
    {
        private readonly ExactService _exactService;
        private readonly ILogger<WebhookController> _logger;
        private readonly IServiceProvider _serviceProvider; // Bu eksik
        private readonly string _webhookLogPath = "Data/webhook_logs.json";

        private readonly string _updateLogFile = "Data/webhook_update.json";

        public WebhookController(ExactService exactService, ILogger<WebhookController> logger, IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider; // Bu önce olmalı
            _exactService = exactService;
            _logger = logger;
        }


        //Customer
        // Customer Webhook Endpoints
        [HttpGet("exact/customers")]
        public IActionResult ValidateCustomerWebhookEndpoint()
        {
            _logger.LogInformation("📡 Customer webhook endpoint validation - GET request");
            return Ok(new { status = "active", timestamp = DateTime.UtcNow });
        }

        [HttpPost("exact/customers")]
        public async Task<IActionResult> HandleCustomerWebhook()
        {
            var reader = new StreamReader(this.HttpContext.Request.Body);
            var requestBody = await reader.ReadToEndAsync();

            if (string.IsNullOrEmpty(requestBody))
            {
                return Ok();
            }

            var webhookData = JsonSerializer.Deserialize<JsonElement>(requestBody);

            try
            {
                _logger.LogInformation("📨 Webhook alındı: Customer değişikliği");

                var logEntry = new WebhookLogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Topic = "Customers",
                    Data = webhookData,
                    ProcessedAt = DateTime.UtcNow,
                    Status = "Received"
                };

                await SaveWebhookLogAsync(logEntry);
                await ProcessCustomerWebhook(webhookData);

                return Ok(new { status = "success", timestamp = DateTime.UtcNow });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Customer webhook işlenirken hata: {Error}", ex.Message);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        private async Task ProcessCustomerWebhook(JsonElement webhookData)
        {
            try
            {
                if (webhookData.TryGetProperty("Content", out var contentElement))
                {
                    if (contentElement.TryGetProperty("ExactOnlineEndpoint", out var endpointElement))
                    {
                        var exactEndpoint = endpointElement.GetString();
                        _logger.LogInformation("🔗 Customer endpoint alındı: {Endpoint}", exactEndpoint);

                        var customerData = await FetchCustomerFromExact(exactEndpoint);

                        if (customerData != null)
                        {
                            await ProcessWebhookCustomer(customerData, contentElement);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Customer webhook işlenirken hata: {Error}", ex.Message);
            }
        }

        private async Task<Account> FetchCustomerFromExact(string exactEndpoint)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var exactService = scope.ServiceProvider.GetRequiredService<ExactService>();

                var token = await exactService.GetValidToken();
                if (token == null)
                {
                    _logger.LogWarning("Token alınamadı, customer getirilemedi");
                    return null;
                }

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token.access_token);
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                _logger.LogInformation("📡 Customer getiriliyor: {Endpoint}", exactEndpoint);

                var response = await client.GetAsync(exactEndpoint);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Customer getirilemedi: {Status} - {Error}", response.StatusCode, error);
                    return null;
                }

                var jsonContent = await response.Content.ReadAsStringAsync();
                var jsonDoc = JsonDocument.Parse(jsonContent);

                if (jsonDoc.RootElement.TryGetProperty("d", out var dataElement))
                {
                    _logger.LogInformation("✅ Customer başarıyla getirildi");
                    Console.WriteLine(dataElement);
                    var email = dataElement.TryGetProperty("Email", out var emailEl) ? emailEl.GetString() : null;
                    if (email != null)
                    {
                        var newcustomer = await exactService.GetCustomerByEmailAsync(email);
                        return newcustomer;
                    }

                    return null;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Customer getirme hatası: {Error}", ex.Message);
                return null;
            }
        }

        private async Task ProcessWebhookCustomer(Account customerData, JsonElement webhookContent)
        {
            try
            {

                //var action = webhookContent.TryGetProperty("Action", out var actionEl) ? actionEl.GetString() : "Unknown";
                var createdDate = customerData.Created;
                var modifiedDate = customerData.Modified;

                // Eğer Created ve Modified aynı ve 1 dakikadan yeniyse = yeni item
                var isNewItem = false;
                if (createdDate.HasValue && modifiedDate.HasValue)
                {
                    var timeDifference = Math.Abs((modifiedDate.Value - createdDate.Value).TotalSeconds);
                    isNewItem = timeDifference < 5 && // 5 saniye tolerans
                            (DateTime.UtcNow - createdDate.Value).TotalMinutes < 2;
                }
                var action = isNewItem ? "Insert" : "Update";
                // _logger.LogInformation("🔄 Webhook customer işleniyor: Code={Code}, Name={Name}, Action={Action}, Email={Email}",
                //     code, name, action, email);

                if (customerData != null)
                {
                    var customerLog = new CustomerChangeLog
                    {
                        Timestamp = DateTime.UtcNow,
                        // CustomerID = id,
                        // Code = code,
                        // Name = name,
                        // Email = email,
                        // Phone = phone,
                        // Mobile = mobile,
                        // City = city,
                        // Country = country,
                        Action = action,
                        Source = "ExactWebhook",
                        // RawData = customerData
                    };

                    await SaveCustomerChangeLogAsync(customerLog);
                    using var scope = _serviceProvider.CreateScope();
                    var shopifyService = scope.ServiceProvider.GetRequiredService<ShopifyService>();
                    var shopifyCustomerService = scope.ServiceProvider.GetRequiredService<ShopifyCustomerCrud>();
                    // Yeni müşteri oluşturulduğunda veya güncellendiğinde
                    if (action == "Insert")
                    {
                        _logger.LogInformation("🆕 Yeni müşteri oluşturuldu: {Name} ({Code})", customerData.Name, customerData.Code);
                        var logFile = _webhookLogPath;

                        // customerData tüm verilerini txt log dosyasına yaz
                        try
                        {
                            var customerLogDir = "Data";
                            if (!Directory.Exists(customerLogDir))
                                Directory.CreateDirectory(customerLogDir);

                            var na = "N/A";
                            var logLine = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] YENİ MÜŞTERİ OLUŞTURMA\n" +
                                $"  ID: {customerData.ID}\n" +
                                $"  Code: {customerData.Code ?? na}\n" +
                                $"  Name: {customerData.Name ?? na}\n" +
                                $"  Email: {customerData.Email ?? na}\n" +
                                $"  Phone: {customerData.Phone ?? na}\n" +
                                $"  PhoneExtension: {customerData.PhoneExtension ?? na}\n" +
                                $"  Type: {customerData.Type ?? na}\n" +
                                $"  Status: {customerData.Status ?? na}\n" +
                                $"  Division: {customerData.Division}\n" +
                                $"  AddressLine1: {customerData.AddressLine1 ?? na}\n" +
                                $"  AddressLine2: {customerData.AddressLine2 ?? na}\n" +
                                $"  AddressLine3: {customerData.AddressLine3 ?? na}\n" +
                                $"  Postcode: {customerData.Postcode ?? na}\n" +
                                $"  City: {customerData.City ?? na}\n" +
                                $"  State: {customerData.State ?? na}\n" +
                                $"  StateName: {customerData.StateName ?? na}\n" +
                                $"  Country: {customerData.Country ?? na}\n" +
                                $"  CountryName: {customerData.CountryName ?? na}\n" +
                                $"  VATNumber: {customerData.VATNumber ?? na}\n" +
                                $"  ClassificationDescription: {customerData.ClassificationDescription ?? na}\n" +
                                $"  Classification1: {customerData.Classification1?.ToString() ?? na}\n" +
                                $"  Created: {customerData.Created?.ToString("o") ?? na}\n" +
                                $"  Modified: {customerData.Modified?.ToString("o") ?? na}\n" +
                                $"  StartDate: {customerData.StartDate?.ToString("o") ?? na}\n" +
                                $"  EndDate: {customerData.EndDate?.ToString("o") ?? na}\n" +
                                $"  ControlledDate: {customerData.ControlledDate?.ToString("o") ?? na}\n" +
                                $"  --- FULL JSON ---\n";
                            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
                            logLine += $"  {JsonSerializer.Serialize(customerData, jsonOptions)}\n" +
                                $"========================================\n\n";

                            await System.IO.File.AppendAllTextAsync("Data/new_customer_log.txt", logLine);
                            _logger.LogInformation("📝 Yeni müşteri verisi loglandı: {Code} - {Name}", customerData.Code, customerData.Name);
                        }
                        catch (Exception logEx)
                        {
                            _logger.LogError(logEx, "Customer log dosyasına yazılırken hata: {Error}", logEx.Message);
                        }

                        // Sadece Status "C" olan müşteriler Shopify'a gönderilecek
                        if (customerData.Status == "C")
                        {
                            var test = await shopifyCustomerService.CreateCustomerEmailAsync(customerData, logFile);
                        }
                        else
                        {
                            _logger.LogInformation("⏭️ Müşteri Status={Status}, 'C' değil - Shopify'a gönderilmedi: {Name} ({Code})",
                                customerData.Status, customerData.Name, customerData.Code);
                        }

                    }
                    else if (action == "Update")
                    {
                        _logger.LogInformation("🛠️ Müşteri güncelleme başlatılıyor: {Name} ({Code})", customerData.Name, customerData.Code);
                        var logFile = _webhookLogPath;
                        var test = await shopifyCustomerService.UpdateCustomerAsync(customerData, "logFile");

                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Webhook customer verisi işlenirken hata: {Error}", ex.Message);
            }
        }

        private async Task SaveCustomerChangeLogAsync(CustomerChangeLog customerLog)
        {
            try
            {
                var customerLogPath = "Data/customer_changes.json";
                var logs = new List<CustomerChangeLog>();

                if (System.IO.File.Exists(customerLogPath))
                {
                    var content = await System.IO.File.ReadAllTextAsync(customerLogPath);
                    if (!string.IsNullOrEmpty(content))
                    {
                        logs = JsonSerializer.Deserialize<List<CustomerChangeLog>>(content) ?? new List<CustomerChangeLog>();
                    }
                }

                logs.Add(customerLog);

                if (logs.Count > 5000)
                {
                    logs = logs.OrderByDescending(l => l.Timestamp).Take(5000).ToList();
                }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var json = JsonSerializer.Serialize(logs, options);

                var directory = Path.GetDirectoryName(customerLogPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await System.IO.File.WriteAllTextAsync(customerLogPath, json);

                _logger.LogInformation("✅ Customer change log kaydedildi: {Name}", customerLog.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Customer change log kaydedilirken hata: {Error}", ex.Message);
            }
        }

        [HttpPost("setup/customer")]
        public async Task<IActionResult> SetupCustomerWebhook([FromBody] WebhookSetupRequest? request = null)
        {
            try
            {
                string callbackUrl;

                if (request?.CallbackUrl != null)
                {
                    callbackUrl = request.CallbackUrl;
                }
                else
                {
                    // ngrok üzerinden gelirse X-Forwarded-Proto header'ını kontrol et
                    var scheme = Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? Request.Scheme;
                    callbackUrl = $"{scheme}://{Request.Host}/api/webhook/exact/customers";
                }

                _logger.LogInformation("🔗 Customer webhook kurulumu başlatılıyor. Callback URL: {CallbackUrl}", callbackUrl);

                // Exact'ta customer için topic "Accounts" olarak geçer
                var success = await _exactService.CreateWebhookSubscriptionAsync(callbackUrl, "Accounts");

                if (success)
                {
                    return Ok(new
                    {
                        success = true,
                        message = "Customer webhook aboneliği başarıyla oluşturuldu",
                        callbackUrl = callbackUrl,
                        topic = "Accounts",
                        note = "ngrok URL kullanılıyor - ngrok yeniden başlatıldığında webhook'u tekrar kurun"
                    });
                }
                else
                {
                    return BadRequest(new { success = false, message = "Customer webhook aboneliği oluşturulamadı" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Customer webhook kurulumunda hata: {Error}", ex.Message);
                return StatusCode(500, new { error = ex.Message });
            }
        }
        //Customer Webhook endpoint


        [HttpGet("exact/items")]
        public IActionResult ValidateWebhookEndpoint()
        {
            _logger.LogInformation("📡 Webhook endpoint validation - GET request");
            return Ok(new { status = "active", timestamp = DateTime.UtcNow });
        }

        [HttpPost("exact/items")]
        public async Task<IActionResult> HandleItemWebhook()
        {
            // [FromBody] JsonElement webhookData
            var reader = new StreamReader(this.HttpContext.Request.Body);
            var requestBody = await reader.ReadToEndAsync();
            if (requestBody == "")
            {
                return Ok();
            }
            else
            {
                var webhookData = JsonSerializer.Deserialize<JsonElement>(requestBody);
                try
                {
                    _logger.LogInformation("📨 Webhook alındı: Item değişikliği");

                    var logEntry = new WebhookLogEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Topic = "Items",
                        Data = webhookData,
                        ProcessedAt = DateTime.UtcNow,
                        Status = "Received"
                    };

                    await SaveWebhookLogAsync(logEntry);
                    await ProcessItemWebhook(webhookData);

                    return Ok(new { status = "success", timestamp = DateTime.UtcNow });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Webhook işlenirken hata: {Error}", ex.Message);
                    return StatusCode(500, new { error = ex.Message });
                }
            }


        }

        // Webhook aboneliği oluşturma endpoint'i
        [HttpPost("setup")]
        public async Task<IActionResult> SetupWebhook([FromBody] WebhookSetupRequest? request = null)
        {
            try
            {
                // ngrok URL'ini request'ten al, yoksa otomatik oluştur
                // string callbackUrl;

                // if (request?.CallbackUrl != null)
                // {
                //     callbackUrl = request.CallbackUrl;
                // }
                // else
                // {
                //     // Mevcut request'ten URL'yi oluştur (ngrok için uygun)
                //     callbackUrl = $"{Request.Scheme}://{Request.Host}/api/webhook/exact/items";
                // }
                string callbackUrl;

                if (request?.CallbackUrl != null)
                {
                    callbackUrl = request.CallbackUrl;
                }
                else
                {
                    // ngrok üzerinden gelirse X-Forwarded-Proto header'ını kontrol et
                    var scheme = Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? Request.Scheme;
                    callbackUrl = $"{scheme}://{Request.Host}/api/webhook/exact/items";
                }

                _logger.LogInformation("🔗 Webhook kurulumu başlatılıyor. Callback URL: {CallbackUrl}", callbackUrl);

                // Items konusu için webhook oluştur
                var success = await _exactService.CreateWebhookSubscriptionAsync(callbackUrl, "Items"); // "logistics/Items" otomatik eklenir

                if (success)
                {
                    return Ok(new
                    {
                        success = true,
                        message = "Webhook aboneliği başarıyla oluşturuldu",
                        callbackUrl = callbackUrl,
                        topic = "Items",
                        note = "ngrok URL kullanılıyor - ngrok yeniden başlatıldığında webhook'u tekrar kurun"
                    });
                }
                else
                {
                    return BadRequest(new { success = false, message = "Webhook aboneliği oluşturulamadı" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Webhook kurulumunda hata: {Error}", ex.Message);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // Webhook loglarını getir
        [HttpGet("logs")]
        public async Task<IActionResult> GetWebhookLogs([FromQuery] int take = 50)
        {
            try
            {
                var logs = await ReadWebhookLogsAsync();

                var recentLogs = logs
                    .OrderByDescending(l => l.Timestamp)
                    .Take(take)
                    .ToList();

                return Ok(new
                {
                    success = true,
                    count = recentLogs.Count,
                    logs = recentLogs
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Webhook logları okunurken hata: {Error}", ex.Message);
                return StatusCode(500, new { error = ex.Message });
            }
        }


        [HttpGet("exact/subscriptions")]
        public async Task<IActionResult> ListSubscriptions()
        {
            try
            {
                var response = await _exactService.ListWebhookSubscriptionsAsync(); // ✅ await eklendi
                return Content(response, "application/json");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Webhook abonelikleri listelenirken hata: {Error}", ex.Message);
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        [HttpDelete("exact/subscriptions/{id:guid}")]
        public async Task<IActionResult> DeleteSubscription(Guid id)
        {
            var result = await _exactService.DeleteWebhookSubscriptionAsync(id);
            return result
                ? Ok(new { success = true, message = $"Webhook {id} silindi" })
                : StatusCode(500, new { success = false, message = "Silme başarısız" });
        }


        private async Task ProcessItemWebhook(JsonElement webhookData)
        {
            try
            {
                if (webhookData.TryGetProperty("Content", out var contentElement))
                {
                    if (contentElement.TryGetProperty("ExactOnlineEndpoint", out var endpointElement))
                    {
                        var exactEndpoint = endpointElement.GetString();
                        _logger.LogInformation("🔗 Item endpoint alındı: {Endpoint}", exactEndpoint);

                        var itemData = await FetchItemFromExact(exactEndpoint);

                        // Null kontrolü ekle
                        if (itemData.HasValue)
                        {
                            await ProcessWebhookItem(itemData.Value, contentElement);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Webhook item işlenirken hata: {Error}", ex.Message);
            }
        }

        private async Task<JsonElement?> FetchItemFromExact(string exactEndpoint)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var exactService = scope.ServiceProvider.GetRequiredService<ExactService>();

                var token = await exactService.GetValidToken();
                if (token == null)
                {
                    _logger.LogWarning("Token alınamadı, item getirilemedi");
                    return null;
                }

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token.access_token);
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                _logger.LogInformation("📡 Item getiriliyor: {Endpoint}", exactEndpoint);

                var response = await client.GetAsync(exactEndpoint);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Item getirilemedi: {Status} - {Error}", response.StatusCode, error);
                    return null;
                }

                var jsonContent = await response.Content.ReadAsStringAsync();
                var jsonDoc = JsonDocument.Parse(jsonContent);

                // Exact API response format: {"d": {...}}
                if (jsonDoc.RootElement.TryGetProperty("d", out var dataElement))
                {
                    _logger.LogInformation("✅ Item başarıyla getirildi");

                    return dataElement;
                }

                return jsonDoc.RootElement;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Item getirme hatası: {Error}", ex.Message);
                return null;
            }
        }

        private async Task ProcessWebhookItem(JsonElement itemData, JsonElement webhookContent)
        {
            try
            {
                var code = itemData.TryGetProperty("Code", out var codeEl) ? codeEl.GetString() : null;
                var description = itemData.TryGetProperty("Description", out var descEl) ? descEl.GetString() : null;
                var price = itemData.TryGetProperty("StandardSalesPrice", out var priceEl) ? priceEl.GetDecimal() : 0m;
                var isWebshopItem = itemData.TryGetProperty("IsWebshopItem", out var webshopEl) ? webshopEl.GetDecimal() : 0m;
                var productId = itemData.TryGetProperty("ID", out var idEl) ? idEl.GetGuid() : Guid.Empty;

                // Tarih alanlarını parse et
                var createdDate = ParseMicrosoftJsonDate(itemData, "Created");
                var modifiedDate = ParseMicrosoftJsonDate(itemData, "Modified");

                // Eğer Created ve Modified aynı ve 1 dakikadan yeniyse = yeni item
                var isNewItem = false;
                if (createdDate.HasValue && modifiedDate.HasValue)
                {
                    isNewItem = createdDate.Value == modifiedDate.Value &&
                                (DateTime.UtcNow - createdDate.Value).TotalMinutes < 2;
                }

                var action = isNewItem ? "Create" : "Update";

                _logger.LogInformation("🔄 Webhook item işleniyor: SKU={Code}, Action={Action}, Created={Created}, Modified={Modified}",
                    code, action, createdDate, modifiedDate);

                if (!string.IsNullOrEmpty(code))
                {
                    var itemLog = new ItemChangeLog
                    {
                        Timestamp = DateTime.UtcNow,
                        SKU = code,
                        Description = description,
                        Price = price,
                        Action = action,
                        ModifiedDate = modifiedDate?.ToString("o"),
                        Source = "ExactWebhook",
                        RawData = itemData
                    };

                    await SaveItemChangeLogAsync(itemLog);
                    using var scope = _serviceProvider.CreateScope();
                    var shopifyService = scope.ServiceProvider.GetRequiredService<ShopifyService>();
                    if (action == "Update")
                    {
                        bool isBundle = await _exactService.GetItemExtraFieldAsync(productId.ToString());
                        if(isBundle)
                        {
                           _logger.LogInformation("🛒 Shopify sadece isim ve fiyat: {Code}", code);
                            await shopifyService.UpdateProductTitleAndPriceBySkuAndSaveRawAsync(code, description, price, _updateLogFile);
                        }
                        else
                        {
                             _logger.LogInformation("🛒 Shopify her ikiside güncellenilecek: {Code}", code);
                            await shopifyService.UpdateProductTitleAndPriceBySkuAndSaveRawAsync(code, description, price, _updateLogFile);
                            await shopifyService.ActiveOrPassif(code, isWebshopItem);
                        }
                        
                        
                    }
                    else if (action == "Create")
                    {
                        var product = JsonSerializer.Deserialize<ExactProduct>(itemData.GetRawText());
                        if (product != null)
                        {
                            var logFile = _webhookLogPath;
                            var success = await shopifyService.CreateProductAsync(product, logFile);
                        }
                        else
                        {
                            _logger.LogWarning("❌ Ürün verisi deserialize edilemedi: {Code}", code);
                        }

                    }
                    else if (action == "Delete")
                    {
                        _logger.LogInformation("🛒 Shopify ürün silme başlatılıyor: {Code}", code);
                        await shopifyService.ActiveOrPassif(code, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Webhook item verisi işlenirken hata: {Error}", ex.Message);
            }
        }

        private async Task ProcessSingleItem(JsonElement itemData)
        {
            try
            {
                // Item'dan gerekli bilgileri çıkar
                var code = itemData.TryGetProperty("Code", out var codeEl) ? codeEl.GetString() : null;
                var description = itemData.TryGetProperty("Description", out var descEl) ? descEl.GetString() : null;
                var price = itemData.TryGetProperty("StandardSalesPrice", out var priceEl) ? priceEl.GetDecimal() : 0m;
                var modified = itemData.TryGetProperty("Modified", out var modEl) ? modEl.GetString() : null;

                if (!string.IsNullOrEmpty(code))
                {
                    _logger.LogInformation("🔄 Item güncelleniyor: SKU={Code}, Description={Description}, Price={Price}",
                        code, description, price);

                    // Detayları JSON'a kaydet
                    var itemLog = new ItemChangeLog
                    {
                        Timestamp = DateTime.UtcNow,
                        SKU = code,
                        Description = description,
                        Price = price,
                        ModifiedDate = modified,
                        Source = "ExactWebhook",
                        RawData = itemData
                    };

                    await SaveItemChangeLogAsync(itemLog);

                    _logger.LogInformation("✅ Item kaydedildi: {Code}", code);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Tekil item işlenirken hata: {Error}", ex.Message);
            }
        }

        private async Task SaveWebhookLogAsync(WebhookLogEntry logEntry)
        {
            try
            {
                var logs = await ReadWebhookLogsAsync();
                logs.Add(logEntry);

                // Son 1000 log'u tut
                if (logs.Count > 1000)
                {
                    logs = logs.OrderByDescending(l => l.Timestamp).Take(1000).ToList();
                }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var json = JsonSerializer.Serialize(logs, options);

                // Dizin oluştur
                var directory = Path.GetDirectoryName(_webhookLogPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await System.IO.File.WriteAllTextAsync(_webhookLogPath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Webhook log kaydedilirken hata: {Error}", ex.Message);
            }
        }

        private async Task SaveItemChangeLogAsync(ItemChangeLog itemLog)
        {
            try
            {
                var itemLogPath = "Data/item_changes.json";
                var logs = new List<ItemChangeLog>();

                // Mevcut logları oku
                if (System.IO.File.Exists(itemLogPath))
                {
                    var content = await System.IO.File.ReadAllTextAsync(itemLogPath);
                    if (!string.IsNullOrEmpty(content))
                    {
                        logs = JsonSerializer.Deserialize<List<ItemChangeLog>>(content) ?? new List<ItemChangeLog>();
                    }
                }

                logs.Add(itemLog);

                // Son 5000 item değişikliğini tut
                if (logs.Count > 5000)
                {
                    logs = logs.OrderByDescending(l => l.Timestamp).Take(5000).ToList();
                }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var json = JsonSerializer.Serialize(logs, options);
                await System.IO.File.WriteAllTextAsync(itemLogPath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Item change log kaydedilirken hata: {Error}", ex.Message);
            }
        }

        private async Task<List<WebhookLogEntry>> ReadWebhookLogsAsync()
        {
            try
            {
                if (!System.IO.File.Exists(_webhookLogPath))
                    return new List<WebhookLogEntry>();

                var content = await System.IO.File.ReadAllTextAsync(_webhookLogPath);
                if (string.IsNullOrEmpty(content))
                    return new List<WebhookLogEntry>();

                return JsonSerializer.Deserialize<List<WebhookLogEntry>>(content) ?? new List<WebhookLogEntry>();
            }
            catch
            {
                return new List<WebhookLogEntry>();
            }
        }

        private DateTime? ParseMicrosoftJsonDate(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var dateEl))
                return null;

            var dateString = dateEl.GetString();
            if (string.IsNullOrEmpty(dateString))
                return null;

            var match = System.Text.RegularExpressions.Regex.Match(dateString, @"\/Date\((\d+)\)\/");
            if (match.Success && long.TryParse(match.Groups[1].Value, out var timestamp))
            {
                return DateTime.UnixEpoch.AddMilliseconds(timestamp);
            }

            return null;
        }
    }



    public class WebhookSetupRequest
    {
        public string CallbackUrl { get; set; }
    }

    public class WebhookLogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Topic { get; set; }
        public JsonElement Data { get; set; }
        public DateTime ProcessedAt { get; set; }
        public string Status { get; set; }
    }

    public class ItemChangeLog
    {
        public DateTime Timestamp { get; set; }
        public string SKU { get; set; }
        public string Description { get; set; }
        public decimal Price { get; set; }
        public string Action { get; set; } // Bu eksik
        public string ModifiedDate { get; set; }
        public string Source { get; set; }
        public JsonElement RawData { get; set; }
    }
    public class CustomerChangeLog
    {
        public DateTime Timestamp { get; set; }
        public Guid CustomerID { get; set; }
        public string Code { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public string Phone { get; set; }
        public string Mobile { get; set; }
        public string City { get; set; }
        public string Country { get; set; }
        public string Action { get; set; }
        public string Source { get; set; }
        public JsonElement RawData { get; set; }
    }
}
