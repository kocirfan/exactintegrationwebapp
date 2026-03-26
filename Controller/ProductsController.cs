
using Microsoft.AspNetCore.Mvc;
using ShopifyProductApp.Services;
using Newtonsoft.Json;
using System.Text;
using ExactOnline.Models;
using Microsoft.AspNetCore.Authorization;

namespace ShopifyProductApp.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class ProductsController : ControllerBase
    {
        private readonly ExactService _exactService;
        private readonly ExactCustomerCrud _exactCustomerService;
        private readonly ShopifyService _shopifyService;
        private readonly ShopifyCustomerCrud _shopifyCustomerService;
        private readonly ShopifyGraphQLService _graphqlService;
        private readonly ExactAddressCrud _exactAddressCrud;
        private readonly ExactProductCrud _exactProductCrud;
        private readonly AppConfiguration _config;
        private readonly ILogger<ProductsController> _logger;
        private readonly IConfiguration _configg;
        private readonly ProductPriceAndTitleUpdateService _productSyncService;

        // ✅ Cache sistemi
        private const string CUSTOMER_CACHE_PATH = "data/customers_cache.json";
        private const int CACHE_VALIDITY_HOURS = 24;


        public ProductsController(ShopifyGraphQLService graphqlService,
        ExactService exactService,
        ExactCustomerCrud exactCustomerService,
        ShopifyService shopifyService,
        ShopifyCustomerCrud shopifyCustomerCrud,
        ExactAddressCrud exactAddressCrud,
         ExactProductCrud exactProductCrud,
        ProductPriceAndTitleUpdateService productSyncService,
         AppConfiguration config, ILogger<ProductsController> logger, IConfiguration configg)
        {
            _graphqlService = graphqlService;
            _exactService = exactService;
            _exactCustomerService = exactCustomerService;
            _shopifyService = shopifyService;
            _shopifyCustomerService = shopifyCustomerCrud;
            _exactAddressCrud = exactAddressCrud;
            _exactProductCrud = exactProductCrud;
            _productSyncService = productSyncService;
            _config = config;
            _logger = logger;
            _configg = configg;
        }


        [HttpGet("all-items")]
        public async Task<IActionResult> GetAllItems()
        {
            try
            {
                _config.LogMessage("🚀 İşlem başlatıldı");
                var response = await _exactService.GetItemsWebShopAndModified(); // Metot adını düzeltin

                if (response == null || !response.Success || !response.Results.Any())
                {
                    _config.LogMessage("⚠️ Items alınamadı veya filtreye uyan ürün bulunamadı.");
                    return Ok(new
                    {
                        Success = false,
                        Message = "Items alınamadı veya filtreye uyan ürün bulunamadı.",
                        Timestamp = DateTime.UtcNow
                    });
                }

                _config.LogMessage($"📦 Toplam {response.ProcessedCount} ürün bulundu");

                var result = new
                {
                    Success = response.Success,
                    ProcessedCount = response.ProcessedCount,
                    Results = response.Results, // ExactProduct listesi
                    FilePath = _config.ShopifyFilePath,
                    Timestamp = DateTime.UtcNow
                };

                _config.LogMessage($"🎉 İşlem tamamlandı: {response.ProcessedCount} ürün işlendi");
                return Ok(result);
            }
            catch (Exception ex)
            {
                _config.LogMessage($"❌ Genel hata: {ex.Message}");
                return Ok(new
                {
                    Success = false,
                    Error = ex.Message,
                    Timestamp = DateTime.UtcNow
                });
            }
        }

        [HttpGet("all-items-summary")]
        public async Task<IActionResult> GetAllItemsSummary()
        {
            try
            {
                var items = await _exactService.GetAllItemsSummaryAsync();
                return Ok(new
                {
                    Count = items.Count,
                    Items = items
                });
            }
            catch (Exception ex)
            {
                return Ok(new { Success = false, Error = ex.Message });
            }
        }

        [HttpGet("shopify-items-graphql")]
        public async Task<IActionResult> GetShopifyItemsGraphql()
        {
            try
            {
                _logger.LogInformation("🛍️ Shopify ürünleri getiriliyor (GraphQL)...");

                // Önce configuration'ı kontrol et
                var shopifySection = _configg.GetSection("Shopify");
                var storeUrl = _configg["Shopify:StoreUrl"];
                var accessToken = _configg["Shopify:AccessToken"];

                _logger.LogInformation($"Store URL: {storeUrl}");
                _logger.LogInformation($"Token var mı: {!string.IsNullOrEmpty(accessToken)}");

                if (string.IsNullOrEmpty(storeUrl) || string.IsNullOrEmpty(accessToken))
                {
                    _logger.LogError("❌ Shopify configuration eksik!");
                    return BadRequest(new
                    {
                        error = "Shopify configuration eksik",
                        storeUrl = string.IsNullOrEmpty(storeUrl) ? "Eksik" : "Mevcut",
                        accessToken = string.IsNullOrEmpty(accessToken) ? "Eksik" : "Mevcut"
                    });
                }

                // GraphQL ile tüm ürünleri çek
                var products = await _graphqlService.GetAllProductsAsync(batchSize: 250);

                if (products == null || products.Count == 0)
                {
                    _logger.LogWarning("⚠️ Ürün listesi boş döndü");
                    return Ok(new
                    {
                        message = "Shopify'da ürün bulunamadı",
                        count = 0,
                        products = new List<object>()
                    });
                }

                _logger.LogInformation($"✅ {products.Count} ürün başarıyla alındı");

                return Ok(new
                {
                    success = true,
                    count = products.Count,
                    products = products
                });
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError($"❌ HTTP Hatası: {httpEx.Message}");
                _logger.LogError($"Stack Trace: {httpEx.StackTrace}");

                return StatusCode(500, new
                {
                    error = "Shopify API bağlantı hatası",
                    message = httpEx.Message,
                    detail = "Shopify store URL veya access token hatalı olabilir"
                });
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError($"❌ JSON Parse Hatası: {jsonEx.Message}");

                return StatusCode(500, new
                {
                    error = "GraphQL response parse hatası",
                    message = jsonEx.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Genel Hata: {ex.Message}");
                _logger.LogError($"Stack Trace: {ex.StackTrace}");
                _logger.LogError($"Inner Exception: {ex.InnerException?.Message}");

                return StatusCode(500, new
                {
                    error = "Bir hata oluştu",
                    message = ex.Message,
                    innerException = ex.InnerException?.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }
        [HttpGet("test-config")]
        public IActionResult TestConfig()
        {
            try
            {
                var storeUrl = _configg["Shopify:StoreUrl"];
                var accessToken = _configg["Shopify:AccessToken"];

                return Ok(new
                {
                    storeUrl = storeUrl,
                    hasAccessToken = !string.IsNullOrEmpty(accessToken),
                    tokenLength = accessToken?.Length ?? 0,
                    tokenPrefix = accessToken?.Substring(0, Math.Min(10, accessToken?.Length ?? 0))
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // Test endpoint - Basit GraphQL query
        [HttpGet("test-graphql")]
        public async Task<IActionResult> TestGraphQL()
        {
            try
            {
                _logger.LogInformation("🧪 GraphQL test başlatılıyor...");

                // Sadece 5 ürün çek (test için)
                var products = await _graphqlService.GetAllProductsAsync(
                    batchSize: 5,
                    maxProducts: 5
                );

                _logger.LogInformation($"✅ Test başarılı: {products?.Count ?? 0} ürün");

                return Ok(new
                {
                    success = true,
                    productCount = products?.Count ?? 0,
                    firstProduct = products?.FirstOrDefault()?.Title
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Test hatası: {ex.Message}");
                return StatusCode(500, new
                {
                    error = ex.Message,
                    stackTrace = ex.StackTrace
                });
            }
        }

        [HttpGet("test-rest-api")]
        public async Task<IActionResult> TestRestApi()
        {
            try
            {
                var storeUrl = _configg["Shopify:StoreUrl"];
                var accessToken = _configg["Shopify:AccessToken"];

                using var client = new HttpClient();
                client.BaseAddress = new Uri(storeUrl);
                client.DefaultRequestHeaders.Add("X-Shopify-Access-Token", accessToken);

                // Basit bir REST endpoint test et
                var response = await client.GetAsync("admin/api/2024-01/products.json?limit=5");
                var content = await response.Content.ReadAsStringAsync();

                _logger.LogInformation($"REST API Response Status: {response.StatusCode}");
                _logger.LogInformation($"REST API Response: {content.Substring(0, Math.Min(500, content.Length))}");

                if (!response.IsSuccessStatusCode)
                {
                    return StatusCode((int)response.StatusCode, new
                    {
                        error = "REST API başarısız",
                        status = response.StatusCode,
                        response = content
                    });
                }

                return Ok(new
                {
                    success = true,
                    message = "REST API çalışıyor",
                    statusCode = response.StatusCode,
                    response = content
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // GraphQL endpoint'i detaylı test
        [HttpGet("test-graphql-detailed")]
        public async Task<IActionResult> TestGraphQLDetailed()
        {
            try
            {
                var storeUrl = _configg["Shopify:StoreUrl"];
                var accessToken = _configg["Shopify:AccessToken"];

                using var client = new HttpClient();
                client.BaseAddress = new Uri(storeUrl);
                client.DefaultRequestHeaders.Add("X-Shopify-Access-Token", accessToken);

                // Çok basit bir GraphQL query
                var query = @"{
          shop {
            name
            email
          }
        }";

                var requestBody = new { query };
                var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PostAsync("admin/api/2024-01/graphql.json", content);
                var responseContent = await response.Content.ReadAsStringAsync();

                _logger.LogInformation($"GraphQL Response Status: {response.StatusCode}");
                _logger.LogInformation($"GraphQL Response: {responseContent}");

                return Ok(new
                {
                    statusCode = response.StatusCode,
                    response = responseContent,
                    success = response.IsSuccessStatusCode
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message, stack = ex.StackTrace });
            }
        }
        [HttpGet("compare-rest-graphql")]
        public async Task<IActionResult> CompareRestAndGraphQL()
        {
            try
            {
                var storeUrl = _configg["Shopify:StoreUrl"];
                var accessToken = _configg["Shopify:AccessToken"];

                using var client = new HttpClient();
                client.BaseAddress = new Uri(storeUrl);
                client.DefaultRequestHeaders.Add("X-Shopify-Access-Token", accessToken);

                // ==========================================
                // 1. REST API TEST
                // ==========================================
                _logger.LogInformation("🔵 REST API test başlıyor...");

                var restResponse = await client.GetAsync("admin/api/2024-01/products.json?limit=5");
                var restContent = await restResponse.Content.ReadAsStringAsync();

                _logger.LogInformation($"REST Status: {restResponse.StatusCode}");
                _logger.LogInformation($"REST Response: {restContent.Substring(0, Math.Min(500, restContent.Length))}...");

                int restProductCount = 0;
                List<string> restProductTitles = new();

                if (restResponse.IsSuccessStatusCode)
                {
                    using var restDoc = System.Text.Json.JsonDocument.Parse(restContent);
                    if (restDoc.RootElement.TryGetProperty("products", out var restProducts))
                    {
                        foreach (var product in restProducts.EnumerateArray())
                        {
                            restProductCount++;
                            if (product.TryGetProperty("title", out var title))
                            {
                                restProductTitles.Add(title.GetString());
                            }
                        }
                    }
                }

                // ==========================================
                // 2. GRAPHQL TEST
                // ==========================================
                _logger.LogInformation("🟢 GraphQL test başlıyor...");

                var graphqlQuery = @"{
          products(first: 5) {
            edges {
              node {
                id
                title
                legacyResourceId
              }
            }
          }
        }";

                var requestBody = new { query = graphqlQuery };
                var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var graphqlResponse = await client.PostAsync("admin/api/2024-01/graphql.json", content);
                var graphqlContent = await graphqlResponse.Content.ReadAsStringAsync();

                _logger.LogInformation($"GraphQL Status: {graphqlResponse.StatusCode}");
                _logger.LogInformation($"GraphQL Response: {graphqlContent}");

                int graphqlProductCount = 0;
                List<string> graphqlProductTitles = new();
                List<string> graphqlErrors = new();

                if (graphqlResponse.IsSuccessStatusCode)
                {
                    using var graphqlDoc = System.Text.Json.JsonDocument.Parse(graphqlContent);

                    // Errors kontrolü
                    if (graphqlDoc.RootElement.TryGetProperty("errors", out var errors))
                    {
                        foreach (var error in errors.EnumerateArray())
                        {
                            graphqlErrors.Add(error.GetProperty("message").GetString());
                        }
                    }

                    // Data kontrolü
                    if (graphqlDoc.RootElement.TryGetProperty("data", out var data))
                    {
                        if (data.TryGetProperty("products", out var products))
                        {
                            if (products.TryGetProperty("edges", out var edges))
                            {
                                foreach (var edge in edges.EnumerateArray())
                                {
                                    var node = edge.GetProperty("node");
                                    graphqlProductCount++;
                                    if (node.TryGetProperty("title", out var title))
                                    {
                                        graphqlProductTitles.Add(title.GetString());
                                    }
                                }
                            }
                        }
                    }
                }

                // ==========================================
                // 3. KARŞILAŞTIRMA SONUÇLARI
                // ==========================================
                var result = new
                {
                    summary = new
                    {
                        restApiWorks = restResponse.IsSuccessStatusCode,
                        graphqlWorks = graphqlResponse.IsSuccessStatusCode,
                        restProductCount,
                        graphqlProductCount,
                        productsMatch = restProductCount == graphqlProductCount
                    },
                    restApi = new
                    {
                        statusCode = restResponse.StatusCode,
                        productCount = restProductCount,
                        productTitles = restProductTitles,
                        rawResponse = restContent.Length > 1000 ? restContent.Substring(0, 1000) + "..." : restContent
                    },
                    graphql = new
                    {
                        statusCode = graphqlResponse.StatusCode,
                        productCount = graphqlProductCount,
                        productTitles = graphqlProductTitles,
                        errors = graphqlErrors,
                        rawResponse = graphqlContent
                    }
                };

                // Sonuçları logla
                _logger.LogInformation("═══════════════════════════════════════");
                _logger.LogInformation($"📊 KARŞILAŞTIRMA SONUÇLARI:");
                _logger.LogInformation($"   REST API: {restProductCount} ürün");
                _logger.LogInformation($"   GraphQL: {graphqlProductCount} ürün");
                _logger.LogInformation($"   GraphQL Errors: {graphqlErrors.Count}");

                if (restProductCount > 0)
                {
                    _logger.LogInformation($"   REST Ürünler: {string.Join(", ", restProductTitles)}");
                }

                if (graphqlProductCount > 0)
                {
                    _logger.LogInformation($"   GraphQL Ürünler: {string.Join(", ", graphqlProductTitles)}");
                }

                if (graphqlErrors.Count > 0)
                {
                    _logger.LogInformation($"   GraphQL Hatalar: {string.Join(", ", graphqlErrors)}");
                }

                _logger.LogInformation("═══════════════════════════════════════");

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Hata: {ex.Message}");
                return StatusCode(500, new { error = ex.Message, stack = ex.StackTrace });
            }
        }


        [HttpGet("shopify-items")]
        public async Task<IActionResult> GetShopifyItems()
        {
            _config.LogMessage("🛍️ Shopify ürünleri getiriliyor...");

            var jsonDoc = await _shopifyService.GetAllProductsRawAsync();
            string json = jsonDoc.RootElement.GetRawText();

            if (string.IsNullOrWhiteSpace(json))
            {
                _config.LogMessage("❌ Shopify ürünleri alınamadı");
                return Problem("Items alınamadı veya token geçersiz.");
            }

            var data = JsonConvert.DeserializeObject<ShopifyProductResponse>(json);

            foreach (var product in data.Products)
            {
                _config.LogMessage($"Ürün: {product.Id} - {product.Title} ({product.Vendor})");
            }

            _config.LogMessage($"✅ {data.Products.Count} ürün alındı");
            return Ok(data.Products);
        }

        [HttpGet("shopify-itemss")]
        public async Task<IActionResult> GetShopifyItemTitleAndPrice()
        {
            _config.LogMessage("🛍️ Shopify ürünleri getiriliyor...");

            var jsonDoc = await _shopifyService.GetProductBySkuWithDuplicateHandlingAsync("10402");
            string json = JsonConvert.SerializeObject(jsonDoc);

            if (string.IsNullOrWhiteSpace(json))
            {
                _config.LogMessage("❌ Shopify ürünleri alınamadı");
                return Problem("Items alınamadı veya token geçersiz.");
            }

            var data = JsonConvert.DeserializeObject<ShopifyProductResponse>(json);

            return Ok(json);
        }

        [HttpGet("exact-customer")]
        public async Task<IActionResult> GetAll()
        {
            var customers = await _exactService.GetAllCustomersAsync();

            if (customers == null || customers.Count == 0)
            {
                return Ok(new { message = "Müşteri bulunamadı", count = 0, data = new List<Account>() });
            }

            return Ok(new
            {
                message = "Müşteriler başarıyla getirildi",
                count = customers.Count,
                data = customers
            });
        }

        [HttpGet("exact-warehouse")]
        public async Task<IActionResult> GetAllWarehouse()
        {
            var customersJson = await _exactService.GetAllWarehouseAsync();
            return Content(customersJson, "application/json");
        }
        [HttpGet("exact-shipping")]
        public async Task<IActionResult> GetShiping()
        {
            var shippingJson = await _exactService.GetAllShippingMethodAsync();
            return Content(shippingJson, "application/json"); // Raw JSON döndür
        }

        [HttpGet("new-product")]
        public async Task<ActionResult<List<ExactProduct>>> GetNewProduct()
        {
            var products = await _exactService.GetNewCreatedProductAsync();
            return Ok(products);
        }


        //----
        [HttpGet("updated-customer")]
        public async Task<ActionResult> GetUpdatedCustomer()
        {
            var products = await _exactCustomerService.GetAllUpdateCustomersAsync();
            return Ok(products);
        }

        [HttpGet("sync-products")]
        public async Task<IActionResult> SyncProducts()
        {
            try
            {
                _logger.LogInformation("Manuel product sync başlatıldı");
                await _productSyncService.ExecuteAsync();

                return Ok(new { message = "Product sync işlemi başarıyla tamamlandı" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Product sync işleminde hata oluştu");
                return StatusCode(500, new { error = ex.Message });
            }
        }
        //---

        // ✅ REFAKTÖRLÜ METOD: Cache ile müşteri senkronizasyonu
        [HttpGet("customer/by-email")]
        public async Task<IActionResult> GetByEmail([FromQuery] string email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return BadRequest(new { message = "Email parametresi gerekli" });
            }

            // ✅ Cache'den veya API'den müşterileri al
            //var allCustomer = await GetCustomersWithCacheAsync();
            //var allCustomer = await _exactService.GetAllCustomersAsync();
            var customer = await _exactService.GetCustomerByEmailAsync(email);
            var addresses = await _exactAddressCrud.GetCustomerAddresses(customer.ID.ToString());
            var shopifyCustomer = await _shopifyCustomerService.CustomerFindByEmail(email);

            // if (allCustomer == null || allCustomer.Count == 0)
            // {
            //     return NotFound(new { message = "Müşteri bulunamadı" });
            // }

            // var results = new List<object>();
            // int successCount = 0;
            // int failureCount = 0;
            // var logFilePath = Path.Combine("logs", $"customer-sync-{DateTime.Now:yyyyMMdd}.log");
            // // foreach (var customer in allCustomer)
            // // {



            // // }
            // try
            //     {
            //         var shopifyResult = await _shopifyCustomerService.UpdateCustomerAsync(
            //             customer,
            //             logFilePath,
            //             sendWelcomeEmail: false
            //         );

            //         if (shopifyResult)
            //         {
            //             successCount++;
            //             results.Add(new
            //             {
            //                 email = customer.Email,
            //                 status = "✅ Başarılı",
            //                 name = customer.Name
            //             });
            //             Console.WriteLine($"✅ Müşteri aktarıldı: {customer.Email}");
            //         }
            //         else
            //         {
            //             failureCount++;
            //             results.Add(new
            //             {
            //                 email = customer.Email,
            //                 status = "⚠️ Zaten mevcut veya hata",
            //                 name = customer.Name
            //             });
            //             Console.WriteLine($"⚠️ Müşteri oluşturulamadı: {customer.Email}");
            //         }
            //     }
            //     catch (Exception ex)
            //     {
            //         failureCount++;
            //         results.Add(new
            //         {
            //             email = customer.Email,
            //             status = "❌ Hata",
            //             error = ex.Message,
            //             name = customer.Name
            //         });
            //         Console.WriteLine($"❌ Hata: {customer.Email} - {ex.Message}");
            //     }
            return Ok(new
            {
                message = "Tüm müşteriler işlendi",
                //totalProcessed = allCustomer.Count,
                // successCount = successCount,
                // failureCount = failureCount,
                // logFile = logFilePath,
                cacheUsed = await IsCacheValidAsync(),
                details = shopifyCustomer
            });

        }

        // ✅ YENİ METOD: Cache'den veya API'den müşterileri al
        private async Task<List<Account>> GetCustomersWithCacheAsync()
        {
            // Önce cache'yi kontrol et
            if (await IsCacheValidAsync())
            {
                Console.WriteLine("📦 Cache'den müşteriler yükleniyor...");
                return await LoadCustomersFromCacheAsync();
            }

            // Cache geçersiz veya yok, API'den yükle
            Console.WriteLine("🔄 API'den müşteriler yükleniyor (cache yenileniyor)...");
            var customers = await _exactService.GetAllCustomersAsync();

            // Cache'e kaydet
            if (customers != null && customers.Count > 0)
            {
                await SaveCustomersToCacheAsync(customers);
                Console.WriteLine($"✅ {customers.Count} müşteri cache'e kaydedildi");
            }

            return customers;
        }

        // ✅ Cache dosyasından müşterileri oku
        private async Task<List<Account>> LoadCustomersFromCacheAsync()
        {
            try
            {
                if (!System.IO.File.Exists(CUSTOMER_CACHE_PATH))
                {
                    return null;
                }

                var json = await System.IO.File.ReadAllTextAsync(CUSTOMER_CACHE_PATH);
                var cacheData = System.Text.Json.JsonSerializer.Deserialize<CustomerCacheData>(json);

                Console.WriteLine($"📦 Cache'den {cacheData?.Customers?.Count ?? 0} müşteri yüklendi");
                return cacheData?.Customers ?? new List<Account>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Cache yükleme hatası: {ex.Message}");
                return null;
            }
        }

        // ✅ Müşterileri cache dosyasına kaydet
        private async Task SaveCustomersToCacheAsync(List<Account> customers)
        {
            try
            {
                var directory = Path.GetDirectoryName(CUSTOMER_CACHE_PATH);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var cacheData = new CustomerCacheData
                {
                    Customers = customers,
                    CachedAt = DateTime.UtcNow
                };

                var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                var json = System.Text.Json.JsonSerializer.Serialize(cacheData, options);

                await System.IO.File.WriteAllTextAsync(CUSTOMER_CACHE_PATH, json);
                Console.WriteLine($"💾 Cache kaydedildi: {CUSTOMER_CACHE_PATH}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Cache kaydetme hatası: {ex.Message}");
            }
        }

        // ✅ Cache'in hala geçerli olup olmadığını kontrol et
        private async Task<bool> IsCacheValidAsync()
        {
            try
            {
                if (!System.IO.File.Exists(CUSTOMER_CACHE_PATH))
                {
                    return false;
                }

                var json = await System.IO.File.ReadAllTextAsync(CUSTOMER_CACHE_PATH);
                var cacheData = System.Text.Json.JsonSerializer.Deserialize<CustomerCacheData>(json);

                if (cacheData?.CachedAt == null)
                {
                    return false;
                }

                var cacheAge = DateTime.UtcNow - cacheData.CachedAt;
                var isValid = cacheAge.TotalHours < CACHE_VALIDITY_HOURS;

                if (isValid)
                {
                    Console.WriteLine($"✅ Cache geçerli ({cacheAge.TotalMinutes:F1} dakika eski)");
                }
                else
                {
                    Console.WriteLine($"⏳ Cache eski ({cacheAge.TotalHours:F1} saat) - yenilenecek");
                }

                return isValid;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Cache kontrolü hatası: {ex.Message}");
                return false;
            }
        }

        // ✅ Cache'i manuel olarak yenile
        [HttpPost("refresh-customer-cache")]
        public async Task<IActionResult> RefreshCustomerCache()
        {
            Console.WriteLine("🔄 Müşteri cache'i manuel olarak yenileniyor...");

            var customers = await _exactService.GetAllCustomersAsync();

            if (customers != null && customers.Count > 0)
            {
                await SaveCustomersToCacheAsync(customers);
                return Ok(new
                {
                    message = "Müşteri cache'i başarıyla yenilendi",
                    customerCount = customers.Count,
                    cachedAt = DateTime.UtcNow
                });
            }

            return BadRequest(new { message = "Müşteri verileri alınamadı" });
        }

        // ✅ Cache'i temizle
        [HttpDelete("clear-customer-cache")]
        public IActionResult ClearCustomerCache()
        {
            try
            {
                if (System.IO.File.Exists(CUSTOMER_CACHE_PATH))
                {
                    System.IO.File.Delete(CUSTOMER_CACHE_PATH);
                    Console.WriteLine("🗑️ Müşteri cache'i temizlendi");
                    return Ok(new { message = "Müşteri cache'i başarıyla temizlendi" });
                }

                return NotFound(new { message = "Cache dosyası bulunamadı" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = $"Cache silme hatası: {ex.Message}" });
            }
        }

        [HttpGet("exact-inactive-items")]
        public async Task<IActionResult> GetInactiveItems()
        {
            try
            {
                _logger.LogInformation("🔍 Exact'ten inactive ürünler getiriliyor...");

                var inactiveSkus = await _exactService.GetInactiveItemCodesAsync();

                if (inactiveSkus == null || !inactiveSkus.Any())
                {
                    _logger.LogWarning("⚠️ Exact'te inactive ürün bulunamadı");
                    return Ok(new
                    {
                        success = true,
                        message = "Exact'te inactive ürün bulunamadı",
                        exactInactiveCount = 0,
                        shopifyFoundCount = 0,
                        products = new List<object>()
                    });
                }

                _logger.LogInformation($"✅ Exact'ten {inactiveSkus.Count} inactive ürün alındı");

                var shopifyMatches = new List<object>();
                var foundSkuList = new List<string>();
                var notFoundSkus = new List<string>();
                int processedCount = 0;

                foreach (var sku in inactiveSkus)
                {
                    processedCount++;
                    _logger.LogInformation($"📦 [{processedCount}/{inactiveSkus.Count}] Shopify'da aranıyor: {sku}");

                    try
                    {
                        var searchResult = await _shopifyService.GetProductBySkuWithDuplicateHandlingAsync(sku);
                        if (searchResult.Found)
                        {
                            _logger.LogInformation($"   ✅ Bulundu: {searchResult.Match.ProductTitle}");
                            foundSkuList.Add(sku);
                        }
                        else
                        {
                            _logger.LogWarning($"   ❌ Bulunamadı: {sku}");
                            notFoundSkus.Add(sku);
                        }
                        await Task.Delay(300);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"   ❌ Hata: {sku} - {ex.Message}");
                    }
                }

                if (foundSkuList.Any())
                {
                    _logger.LogInformation($"🔄 {foundSkuList.Count} ürün için toplu güncelleme başlatılıyor...");

                    var batchLogFile = $"Data/batch_log_inactive_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                    await _shopifyService.UpdateProductStatusBySkuListAndSaveRawAsync(
                        foundSkuList,
                        batchLogFile
                    );

                    _logger.LogInformation($"✅ Toplu güncelleme tamamlandı");
                }

                _logger.LogInformation($"✅ Tarama tamamlandı:");
                _logger.LogInformation($"   📊 Toplam Exact Inactive: {inactiveSkus.Count}");
                _logger.LogInformation($"   ✅ Shopify'da Bulundu: {shopifyMatches.Count(x => ((dynamic)x).found == true)}");
                _logger.LogInformation($"   ❌ Shopify'da Bulunamadı: {notFoundSkus.Count}");

                var result = new
                {
                    success = true,
                    summary = new
                    {
                        exactInactiveCount = inactiveSkus.Count,
                        shopifyFoundCount = shopifyMatches.Count(x => ((dynamic)x).found == true),
                        shopifyNotFoundCount = notFoundSkus.Count,
                        updatedCount = foundSkuList.Count
                    },
                    products = shopifyMatches,
                    notFoundSkus = notFoundSkus,
                    updatedSkus = foundSkuList
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Genel hata: {ex.Message}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");

                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        [HttpGet("get-item-by-code")]
        public async Task<IActionResult> GetItemByCode([FromQuery] string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return BadRequest("Code parametresi gereklidir");
            }

            var item = await _exactService.GetItemByCodeAsync(code);

            if (item == null)
            {
                return NotFound($"Ürün bulunamadı: {code}");
            }

            return Ok(item);
        }
        [HttpGet("get-item-by-new-code")]
        public async Task<IActionResult> GetItemNewByCode([FromQuery] string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return BadRequest("Code parametresi gereklidir");
            }

            var item = await _exactProductCrud.GetItemByCodeAsync(code);
            if (item?.Success == true && item.Results.Count > 0)
            {
                var product = item.Results[0];
                var logFile = Path.Combine("logs", $"shopify-product-create-{DateTime.Now:yyyyMMdd}.log");
                var success = await _shopifyService.CreateProductAsync(product, logFile);
                if(success)
                {
                    Console.WriteLine("Shopify ürün oluşturuldu.");
                }
                else
                {
                    Console.WriteLine("Shopify ürün oluşturulamadı.");

                }
                return Ok(item.Results[0]);
            }
            return Ok("Ürün bulunamadı");
        }

        [HttpGet("exact-recent-orders")]
        public async Task<IActionResult> GetRecentOrders()
        {
            var orders = await _exactService.GetRecentOrdersRawJsonAsync();
            return Ok(orders);
        }


        ////
        [HttpGet("shopify-customers")]
        public async Task<IActionResult> GetShopifyCustomers()
        {
            try
            {
                Console.WriteLine("👥 Shopify müşterileri getiriliyor (GraphQL)...");

                // GraphQL ile tüm müşterileri çek
                //var customers = await _graphqlService.GetAllCustomersAsync(batchSize: 250);
                var testcustomer = await _graphqlService.SearchCustomerByEmailOrCodeAsync(customerCode: "1301311");
                if (testcustomer != null)
                {
                    Console.WriteLine($"✅ Test Müşteri Bulundu: {testcustomer.Id} - {testcustomer.Email}");
                }
                else
                {
                    Console.WriteLine("❌ Test Müşteri Bulunamadı");
                }

                // if (customers == null || customers.Count == 0)
                // {
                //     Console.WriteLine("❌ Shopify müşterileri alınamadı");
                //     return Problem("Müşteriler alınamadı veya token geçersiz.");
                // }

                // foreach (var customer in customers)
                // {
                //     Console.WriteLine($"Müşteri: {customer.Id} - {customer.FirstName} {customer.LastName} ({customer.Email})");
                // }

                //Console.WriteLine($"✅ {customers.Count} müşteri alındı");
                return Ok(testcustomer);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Hata: {ex.Message}");
                return StatusCode(500, $"Bir hata oluştu: {ex.Message}");
            }
        }


        //bir müşterinin adreslerini getir
        [HttpGet("sales-item-prices")]
        public async Task<IActionResult> GetSalesItemPrices([FromQuery] string itemCode)
        {
            if (string.IsNullOrWhiteSpace(itemCode))
                return BadRequest(new { message = "itemCode parametresi gereklidir" });

            var prices = await _exactService.GetSalesItemPricesByItemCodeAsync(itemCode);

            if (prices == null || !prices.Any())
                return Ok(new { success = true, itemCode, count = 0, prices = new List<object>() });

            var result = prices.Select(p => new
            {
                itemCode = p.ItemCode,
                itemDescription = p.ItemDescription,
                price = p.Price,
                currency = p.Currency,
                unit = p.Unit,
                unitDescription = p.UnitDescription,
                quantity = p.Quantity,
                startDate = p.StartDate?.ToString("yyyy-MM-dd HH:mm:ss"),
                endDate = p.EndDate?.ToString("yyyy-MM-dd HH:mm:ss"),
                lastModified = p.Modified?.ToString("yyyy-MM-dd HH:mm:ss"),
                lastModifiedBy = p.ModifierFullName,
                created = p.Created?.ToString("yyyy-MM-dd HH:mm:ss"),
                account = p.Account,
                accountName = p.AccountName
            });

            return Ok(new
            {
                success = true,
                itemCode,
                count = prices.Count,
                prices = result
            });
        }

        [HttpGet("sales-item-price-summary")]
        public async Task<IActionResult> GetSalesItemPriceSummary([FromQuery] string itemCode)
        {
            if (string.IsNullOrWhiteSpace(itemCode))
                return BadRequest(new { message = "itemCode parametresi gereklidir" });

            var prices = await _exactService.GetSalesItemPricesByItemCodeAsync(itemCode);

            if (prices == null || prices.Count == 0)
                return Ok(new { success = false, message = "Fiyat kaydı bulunamadı" });

            // Genel liste fiyatı (müşteriye özel olmayan)
            var generalPrice = prices
                .Where(p => p.Account == null)
                .OrderByDescending(p => p.Modified)
                .FirstOrDefault();

            // Müşteriye özel fiyatlar (aktif olanlar)
            var now = DateTime.UtcNow;
            var activeCustomerPrices = prices
                .Where(p => p.Account != null
                    && (p.StartDate == null || p.StartDate <= now)
                    && (p.EndDate == null || p.EndDate >= now))
                .OrderByDescending(p => p.Modified)
                .Select(p => new
                {
                    accountName = p.AccountName,
                    price = p.Price,
                    currency = p.Currency,
                    startDate = p.StartDate?.ToString("yyyy-MM-dd"),
                    endDate = p.EndDate?.ToString("yyyy-MM-dd"),
                    lastModified = p.Modified?.ToString("yyyy-MM-dd HH:mm:ss"),
                    lastModifiedBy = p.ModifierFullName
                })
                .ToList();

            // En son değiştirilen kayıt (tüm kayıtlar içinde)
            var mostRecentChange = prices.OrderByDescending(p => p.Modified).First();

            return Ok(new
            {
                success = true,
                itemCode,
                itemDescription = prices.First().ItemDescription,
                generalListPrice = generalPrice == null ? null : new
                {
                    price = generalPrice.Price,
                    currency = generalPrice.Currency,
                    lastModified = generalPrice.Modified?.ToString("yyyy-MM-dd HH:mm:ss"),
                    lastModifiedBy = generalPrice.ModifierFullName,
                    activeFrom = generalPrice.StartDate?.ToString("yyyy-MM-dd")
                },
                mostRecentChange = new
                {
                    price = mostRecentChange.Price,
                    currency = mostRecentChange.Currency,
                    accountName = mostRecentChange.AccountName ?? "Genel Liste Fiyatı",
                    lastModified = mostRecentChange.Modified?.ToString("yyyy-MM-dd HH:mm:ss"),
                    lastModifiedBy = mostRecentChange.ModifierFullName
                },
                activeCustomerPriceCount = activeCustomerPrices.Count,
                activeCustomerPrices
            });
        }

        [HttpGet("sales-item-prices-debug")]
        public async Task<IActionResult> DebugSalesItemPrices([FromQuery] string itemCode)
        {
            var rawJson = await _exactService.GetSalesItemPricesRawAsync(itemCode);
            return Content(rawJson, "application/json");
        }

        [HttpGet("exact-customer-addresses")]
        public async Task<IActionResult> GetCustomerAddresses([FromQuery] string customerId)
        {
            if (string.IsNullOrEmpty(customerId))
            {
                return BadRequest("customerId parametresi gereklidir");
            }
            else
            {
                var addresses = await _exactAddressCrud.GetCustomerDeliveryAddresses(customerId);
                return Ok(addresses);
            }
            

        }
    }

    // ✅ Cache veri modeli
    public class CustomerCacheData
    {
        public List<Account> Customers { get; set; }
        public DateTime CachedAt { get; set; }
    }
}