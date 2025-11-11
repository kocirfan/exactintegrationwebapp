using Microsoft.AspNetCore.Mvc;
using ShopifyProductApp.Services;
using Newtonsoft.Json;
using System.Text;
using ExactOnline.Models;

namespace ShopifyProductApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ProductsController : ControllerBase
    {
        private readonly ExactService _exactService;
        private readonly ShopifyService _shopifyService;
        private readonly ShopifyGraphQLService _graphqlService;
        private readonly AppConfiguration _config;
        private readonly ILogger<ProductsController> _logger;
        private readonly IConfiguration _configg;


        public ProductsController(ShopifyGraphQLService graphqlService, ExactService exactService, ShopifyService shopifyService, AppConfiguration config, ILogger<ProductsController> logger, IConfiguration configg)
        {
            _graphqlService = graphqlService;
            _exactService = exactService;
            _shopifyService = shopifyService;
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


        //[HttpGet("process-items")]
        // public async Task<IActionResult> ProcessItems()
        // {
        //     try
        //     {
        //         _config.LogMessage("🚀 İşlem başlatıldı");
        //         var items = await _exactService.();

        //         if (items == null || !items.Any())
        //         {
        //             _config.LogMessage("⚠️ Items alınamadı veya filtreye uyan ürün bulunamadı.");
        //             return Ok(new
        //             {
        //                 Success = false,
        //                 Message = "Items alınamadı veya filtreye uyan ürün bulunamadı.",
        //                 Timestamp = DateTime.UtcNow
        //             });
        //         }

        //         _config.LogMessage($"📦 Toplam {items.Count} ürün bulundu");
        //         var results = new List<object>();

        //         foreach (var sku in items)
        //         {
        //             try
        //             {
        //                 _config.LogSkuAndSaveToJson(sku, "Processing", "SKU işleme başlatıldı");

        //                 var directory = Path.GetDirectoryName(_config.ShopifyFilePath);
        //                 if (!Directory.Exists(directory))
        //                 {
        //                     Directory.CreateDirectory(directory!);
        //                     _config.LogMessage($"📁 Klasör oluşturuldu: {directory}");
        //                 }

        //                 await _shopifyService.UpdateProductStatusBySkuAndSaveRawAsync(sku, _config.ShopifyFilePath);

        //                 results.Add(new
        //                 {
        //                     Sku = sku,
        //                     Status = "Processed",
        //                     Timestamp = DateTime.UtcNow
        //                 });

        //                 _config.SaveLastUpdatedSku(sku, "Success");
        //                 _config.LogSkuAndSaveToJson(sku, "Success", "SKU başarıyla işlendi ve kaydedildi");
        //             }
        //             catch (Exception ex)
        //             {
        //                 _config.LogMessage($"❌ SKU {sku} işlenirken hata: {ex.Message}");
        //                 results.Add(new
        //                 {
        //                     Sku = sku,
        //                     Status = "Error",
        //                     Error = ex.Message,
        //                     Timestamp = DateTime.UtcNow
        //                 });

        //                 _config.SaveLastUpdatedSku(sku, "Error", ex.Message);
        //                 _config.LogSkuAndSaveToJson(sku, "Error", $"İşlem hatası: {ex.Message}");
        //             }
        //         }

        //         var result = new
        //         {
        //             Success = true,
        //             ProcessedCount = items.Count,
        //             Results = results,
        //             FilePath = _config.ShopifyFilePath,
        //             Timestamp = DateTime.UtcNow
        //         };

        //         _config.LogMessage($"🎉 İşlem tamamlandı: {items.Count} ürün işlendi");
        //         return Ok(result);
        //     }
        //     catch (Exception ex)
        //     {
        //         _config.LogMessage($"❌ Genel hata: {ex.Message}");
        //         return Ok(new
        //         {
        //             Success = false,
        //             Error = ex.Message,
        //             Timestamp = DateTime.UtcNow
        //         });
        //     }
        // }

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

            // foreach (var product in data.Products)
            // {
            //     _config.LogMessage($"Ürün: {product.Id} - {product.Title} ({product.Vendor})");
            // }

            // _config.LogMessage($"✅ {data.Products.Count} ürün alındı");
            return Ok(json);
        }

        // [HttpGet("exact-customer-test")]
        // public async Task<IActionResult> GetAllCustomerTest()
        // {
        //     var customersJson = await _exactService.GetAllCustomersAsync();

        //     if (!string.IsNullOrWhiteSpace(customersJson))
        //     {
        //         // burada JSON dolu demektir
        //         return Content("OK", "text/plain");
        //     }

        //     // JSON boşsa normal JSON döndür
        //     return Content(customersJson ?? "[]", "application/json");
        // }

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
            return Content(customersJson, "application/json"); // Raw JSON döndür
        }

        [HttpGet("new-product")]
        public async Task<ActionResult<List<ExactProduct>>> GetNewProduct()
        {
            var products = await _exactService.GetNewCreatedProductAsync();
            return Ok(products);
        }

        // [HttpGet("exact-shipping")]
        // public async Task<IActionResult> GetShiping()
        // {
        //     var shippingJson = await _exactService.GetAllShippingMethodAsync();
        //     return Content(shippingJson, "application/json"); // Raw JSON döndür
        // }

        // [HttpGet("exact-inactive-items")]
        // public async Task<IActionResult> GetInactiveItems()
        // {
        //     var shippingJson = await _exactService.GetInactiveItemCodesAsync();
        //     var jsonString = JsonConvert.SerializeObject(shippingJson);

        //     return Content(jsonString, "application/json");


        // }

        //get customer by email
        [HttpGet("customer/by-email")]
        public async Task<IActionResult> GetByEmail([FromQuery] string email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return BadRequest(new { message = "Email parametresi gerekli" });
            }

            var customer = await _exactService.GetCustomerByEmailAsync(email);

            if (customer == null)
            {
                return NotFound(new { message = $"'{email}' email adresine sahip müşteri bulunamadı" });
            }

            return Ok(new
            {
                message = "Müşteri bulundu",
                data = customer
            });
        }

        [HttpGet("exact-inactive-items")]
        public async Task<IActionResult> GetInactiveItems()
        {
            try
            {
                _logger.LogInformation("🔍 Exact'ten inactive ürünler getiriliyor...");

                // 1. Exact'ten inactive ürün kodlarını al
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

                // 2. Bu SKU'ları Shopify'da ara
                var shopifyMatches = new List<object>();
                var foundSkuList = new List<string>(); // Bulunan SKU'ları topla
                var notFoundSkus = new List<string>();
                int processedCount = 0;

                foreach (var sku in inactiveSkus)
                {
                    processedCount++;
                    _logger.LogInformation($"📦 [{processedCount}/{inactiveSkus.Count}] Shopify'da aranıyor: {sku}");

                    try
                    {
                        // Shopify'da bu SKU'yu ara
                        var searchResult = await _shopifyService.GetProductBySkuWithDuplicateHandlingAsync(sku);
                        if (searchResult.Found)
                        {
                            _logger.LogInformation($"   ✅ Bulundu: {searchResult.Match.ProductTitle}");
                            foundSkuList.Add(sku); // SKU'yu listeye ekle
                        }
                        else
                        {
                            _logger.LogWarning($"   ❌ Bulunamadı: {sku}");
                            notFoundSkus.Add(sku);


                        }
                        await Task.Delay(300); // 300ms
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"   ❌ Hata: {sku} - {ex.Message}");


                    }
                }

                // 3. Döngü bittikten SONRA toplu güncelleme yap
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


        // 2 günlük siparişler
        [HttpGet("exact-recent-orders")]
        public async Task<IActionResult> GetRecentOrders()
        {
            var orders = await _exactService.GetRecentOrdersRawJsonAsync();
            return Ok(orders);
        }

    }
}