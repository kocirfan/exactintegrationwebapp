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
                string callbackUrl;

                if (request?.CallbackUrl != null)
                {
                    callbackUrl = request.CallbackUrl;
                }
                else
                {
                    // Mevcut request'ten URL'yi oluştur (ngrok için uygun)
                    callbackUrl = $"{Request.Scheme}://{Request.Host}/api/webhook/exact/items";
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
                // Item verilerini çıkar
                var code = itemData.TryGetProperty("Code", out var codeEl) ? codeEl.GetString() : null;
                var description = itemData.TryGetProperty("Description", out var descEl) ? descEl.GetString() : null;
                var price = itemData.TryGetProperty("StandardSalesPrice", out var priceEl) ? priceEl.GetDecimal() : 0m;
                var action = webhookContent.TryGetProperty("Action", out var actionEl) ? actionEl.GetString() : "Unknown";
                var isWebshopItem = itemData.TryGetProperty("IsWebshopItem", out var webshopEl) ? webshopEl.GetDecimal() : 0m;


                _logger.LogInformation("🔄 Webhook item işleniyor: SKU={Code}, Action={Action}, Price={Price}",
                    code, action, price);

                if (!string.IsNullOrEmpty(code))
                {
                    // Item change log'a kaydet
                    var itemLog = new ItemChangeLog
                    {
                        Timestamp = DateTime.UtcNow,
                        SKU = code,
                        Description = description,
                        Price = price,
                        Action = action,
                        Source = "ExactWebhook",
                        RawData = itemData
                    };

                    await SaveItemChangeLogAsync(itemLog);

                    // Shopify güncelleme yapılsın mı?
                    if (action == "Update" && price > 0)
                    {
                        _logger.LogInformation("🛒 Shopify güncelleme başlatılıyor: {Code}", code);

                        using var scope = _serviceProvider.CreateScope();
                        var shopifyService = scope.ServiceProvider.GetRequiredService<ShopifyService>();

                        var logFile = _updateLogFile;
                         await shopifyService.UpdateProductTitleAndPriceBySkuAndSaveRawAsync(code, description, price, logFile);
                        //     

                        await shopifyService.ActiveOrPassif(code, isWebshopItem);
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
}
