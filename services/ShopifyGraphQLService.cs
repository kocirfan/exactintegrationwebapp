// ============================================
// 1. ShopifyGraphQLService.cs (Service klasörüne ekleyin)
// ============================================
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;

public class ShopifyGraphQLService
{
    private readonly HttpClient _client;
    private readonly string _graphqlEndpoint;
    private readonly IConfiguration _config;
    
    public ShopifyGraphQLService(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _config = config;
        var storeUrl = _config["Shopify:StoreUrl"];
        var accessToken = _config["Shopify:AccessToken"];
        
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri(storeUrl);
        _client.DefaultRequestHeaders.Add("X-Shopify-Access-Token", accessToken);
        
        _graphqlEndpoint = "admin/api/2024-01/graphql.json";
    }

    public async Task<List<ShopifyProduct>> GetAllProductsAsync(
        int batchSize = 250, 
        int? maxProducts = null)
    {
        if (batchSize > 250) batchSize = 250;
        if (batchSize < 1) batchSize = 250;

        var allProducts = new List<ShopifyProduct>();
        string cursor = null;
        bool hasNextPage = true;
        int pageCount = 0;
        int retryCount = 0;
        const int maxRetries = 3;

        Console.WriteLine($"🚀 GraphQL ile ürünler getiriliyor (sayfa başına {batchSize} ürün)...");

        while (hasNextPage)
        {
            pageCount++;
            var query = BuildProductQuery(batchSize, cursor);

            try
            {
                var response = await ExecuteGraphQLAsync(query);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    if (retryCount < maxRetries)
                    {
                        retryCount++;
                        var retryAfter = GetRetryAfterSeconds(response);
                        Console.WriteLine($"⏳ Rate limit! {retryAfter}sn bekleniyor... ({retryCount}/{maxRetries})");
                        await Task.Delay(retryAfter * 1000);
                        continue;
                    }
                    throw new Exception($"Rate limit aşıldı ({maxRetries} deneme)");
                }

                response.EnsureSuccessStatusCode();
                retryCount = 0;

                var jsonResponse = await response.Content.ReadAsStringAsync();
                
                // FULL RESPONSE'U LOGLA
                Console.WriteLine("═══════════════════════════════════════════════");
                Console.WriteLine($"📄 FULL GraphQL Response (Sayfa {pageCount}):");
                Console.WriteLine(jsonResponse);
                Console.WriteLine("═══════════════════════════════════════════════");
                
                using var document = JsonDocument.Parse(jsonResponse);

                // GraphQL hatalarını kontrol et - THROTTLED özel durumu
                if (document.RootElement.TryGetProperty("errors", out var errors))
                {
                    var errorsList = errors.EnumerateArray().ToList();
                    
                    // Throttled hatası mı?
                    var throttledError = errorsList.FirstOrDefault(e => 
                        e.TryGetProperty("extensions", out var ext) &&
                        ext.TryGetProperty("code", out var code) &&
                        code.GetString() == "THROTTLED"
                    );
                    
                    if (throttledError.ValueKind != JsonValueKind.Undefined)
                    {
                        if (retryCount < maxRetries)
                        {
                            retryCount++;
                            Console.WriteLine($"⏳ GraphQL Throttled! 5 saniye bekleniyor... ({retryCount}/{maxRetries})");
                            await Task.Delay(5000); // 5 saniye bekle
                            pageCount--; // Sayfa sayacını geri al
                            continue;
                        }
                        else
                        {
                            Console.WriteLine($"❌ Maximum retry sayısına ulaşıldı ({maxRetries})");
                            throw new Exception($"GraphQL throttled ve {maxRetries} deneme başarısız");
                        }
                    }
                    
                    // Diğer hatalar
                    Console.WriteLine("❌ GraphQL Hataları:");
                    foreach (var error in errorsList)
                    {
                        var message = error.GetProperty("message").GetString();
                        Console.WriteLine($"   - {message}");
                        
                        if (error.TryGetProperty("extensions", out var ext))
                        {
                            Console.WriteLine($"   Extensions: {ext.GetRawText()}");
                        }
                    }
                    throw new Exception($"GraphQL Error: {errors.GetRawText()}");
                }

                if (!document.RootElement.TryGetProperty("data", out var data))
                {
                    Console.WriteLine("❌ GraphQL response'da 'data' yok");
                    Console.WriteLine($"Response keys: {string.Join(", ", document.RootElement.EnumerateObject().Select(p => p.Name))}");
                    break;
                }

                CheckThrottleStatus(document);

                var products = data.GetProperty("products");
                var edges = products.GetProperty("edges");
                var pageInfo = products.GetProperty("pageInfo");

                int pageProductCount = 0;
                foreach (var edge in edges.EnumerateArray())
                {
                    var node = edge.GetProperty("node");
                    var product = ConvertToShopifyProduct(node);
                    allProducts.Add(product);
                    pageProductCount++;

                    if (maxProducts.HasValue && allProducts.Count >= maxProducts.Value)
                    {
                        Console.WriteLine($"✅ Maksimum ürün sayısına ulaşıldı: {maxProducts.Value}");
                        return allProducts.Take(maxProducts.Value).ToList();
                    }
                }

                Console.WriteLine($"📄 Sayfa {pageCount}: {pageProductCount} ürün alındı. Toplam: {allProducts.Count}");

                hasNextPage = pageInfo.GetProperty("hasNextPage").GetBoolean();
                if (hasNextPage)
                {
                    cursor = edges.EnumerateArray().Last()
                        .GetProperty("cursor").GetString();
                    Console.WriteLine($"   ➡️ Sonraki sayfa cursor: {cursor?.Substring(0, Math.Min(20, cursor.Length))}...");
                }
                else
                {
                    Console.WriteLine("   🏁 Son sayfaya ulaşıldı");
                }

                await SmartDelayAsync(document);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Sayfa {pageCount} hatası: {ex.Message}");
                throw;
            }
        }

        Console.WriteLine($"✅ Toplam {allProducts.Count} ürün {pageCount} sayfada alındı");
        return allProducts;
    }

    private string BuildProductQuery(int first, string cursor)
    {
        var afterClause = string.IsNullOrEmpty(cursor) ? "" : $", after: \"{cursor}\"";
        
        // DÜZELTİLMİŞ QUERY - Sorunlu fieldlar kaldırıldı/düzeltildi
        return $@"
        {{
          products(first: {first}{afterClause}) {{
            edges {{
              cursor
              node {{
                id
                legacyResourceId
                title
                descriptionHtml
                handle
                status
                vendor
                productType
                tags
                createdAt
                updatedAt
                publishedAt
                totalInventory
                variants(first: 100) {{
                  edges {{
                    node {{
                      id
                      legacyResourceId
                      title
                      sku
                      price
                      compareAtPrice
                      inventoryPolicy
                      inventoryQuantity
                      barcode
                      taxable
                      inventoryItem {{
                        id
                        legacyResourceId
                        tracked
                      }}
                      createdAt
                      updatedAt
                      image {{
                        id
                        url
                      }}
                      selectedOptions {{
                        name
                        value
                      }}
                    }}
                  }}
                }}
                images(first: 250) {{
                  edges {{
                    node {{
                      id
                      url
                      altText
                      width
                      height
                    }}
                  }}
                }}
                options {{
                  id
                  name
                  values
                }}
              }}
            }}
            pageInfo {{
              hasNextPage
              hasPreviousPage
            }}
          }}
        }}";
    }

    private ShopifyProduct ConvertToShopifyProduct(JsonElement node)
    {
        var product = new ShopifyProduct
        {
            // legacyResourceId string olarak gelebilir, parse et
            Id = ParseLegacyResourceId(node, "legacyResourceId"),
            AdminGraphqlApiId = node.GetProperty("id").GetString(),
            Title = node.GetProperty("title").GetString(),
            BodyHtml = GetStringOrNull(node, "descriptionHtml"),
            Handle = node.GetProperty("handle").GetString(),
            Status = node.GetProperty("status").GetString()?.ToLower(),
            Vendor = GetStringOrNull(node, "vendor"),
            ProductType = GetStringOrNull(node, "productType"),
            Tags = string.Join(", ", node.GetProperty("tags").EnumerateArray().Select(t => t.GetString())),
            CreatedAt = DateTime.Parse(node.GetProperty("createdAt").GetString()),
            UpdatedAt = DateTime.Parse(node.GetProperty("updatedAt").GetString()),
            PublishedAt = TryParseDateTime(node, "publishedAt"),
            TemplateSuffix = GetStringOrNull(node, "templateSuffix"),
            PublishedScope = "web",
            Variants = new List<ShopifyVariant>(),
            Images = new List<ShopifyImage>(),
            Options = new List<ShopifyOption>()
        };

        if (node.TryGetProperty("variants", out var variants))
        {
            int position = 1;
            foreach (var edge in variants.GetProperty("edges").EnumerateArray())
            {
                var variantNode = edge.GetProperty("node");
                var variant = ConvertToShopifyVariant(variantNode, product.Id, position);
                product.Variants.Add(variant);
                position++;
            }
        }

        if (node.TryGetProperty("images", out var images))
        {
            int position = 1;
            foreach (var edge in images.GetProperty("edges").EnumerateArray())
            {
                var imgNode = edge.GetProperty("node");
                product.Images.Add(ConvertToShopifyImage(imgNode, product.Id, position));
                position++;
            }
        }

        product.Image = product.Images.FirstOrDefault();

        if (node.TryGetProperty("options", out var options))
        {
            int position = 1;
            foreach (var option in options.EnumerateArray())
            {
                product.Options.Add(ConvertToShopifyOption(option, product.Id, position));
                position++;
            }
        }

        return product;
    }

    private ShopifyVariant ConvertToShopifyVariant(JsonElement node, long productId, int position)
    {
        var variant = new ShopifyVariant
        {
            // legacyResourceId string olarak gelebilir, parse et
            Id = ParseLegacyResourceId(node, "legacyResourceId"),
            ProductId = productId,
            AdminGraphqlApiId = node.GetProperty("id").GetString(),
            Title = node.GetProperty("title").GetString(),
            Sku = GetStringOrNull(node, "sku"),
            Price = node.GetProperty("price").GetString(),
            CompareAtPrice = GetStringOrNull(node, "compareAtPrice"),
            Position = position,
            InventoryPolicy = node.GetProperty("inventoryPolicy").GetString()?.ToLower(),
            InventoryQuantity = node.GetProperty("inventoryQuantity").GetInt32(),
            OldInventoryQuantity = node.GetProperty("inventoryQuantity").GetInt32(),
            Barcode = GetStringOrNull(node, "barcode"),
            Weight = 0, // GraphQL'de farklı yerde, varsayılan 0
            WeightUnit = "kg", // Varsayılan
            Taxable = node.GetProperty("taxable").GetBoolean(),
            RequiresShipping = true, // GraphQL'de yok, varsayılan true
            CreatedAt = DateTime.Parse(node.GetProperty("createdAt").GetString()),
            UpdatedAt = DateTime.Parse(node.GetProperty("updatedAt").GetString()),
            Grams = 0, // GraphQL'de farklı yerde
            FulfillmentService = "manual",
            InventoryManagement = null // GraphQL'de yok
        };

        // InventoryItem bilgilerini al
        if (node.TryGetProperty("inventoryItem", out var invItem) &&
            invItem.ValueKind != JsonValueKind.Null)
        {
            if (invItem.TryGetProperty("legacyResourceId", out var invItemIdElement))
            {
                variant.InventoryItemId = ParseLegacyResourceId(invItem, "legacyResourceId");
            }
            
            // Tracked field'ından inventory management'ı çıkar
            if (invItem.TryGetProperty("tracked", out var tracked) && tracked.GetBoolean())
            {
                variant.InventoryManagement = "shopify";
            }
        }

        // Image ID
        if (node.TryGetProperty("image", out var img) &&
            img.ValueKind != JsonValueKind.Null &&
            img.TryGetProperty("id", out var imgId))
        {
            var imgIdStr = imgId.GetString();
            if (imgIdStr.Contains("/"))
            {
                var parts = imgIdStr.Split('/');
                if (long.TryParse(parts.Last(), out var legacyImgId))
                    variant.ImageId = legacyImgId;
            }
        }

        // Selected Options (option1, option2, option3)
        if (node.TryGetProperty("selectedOptions", out var selectedOptions))
        {
            var optionsArray = selectedOptions.EnumerateArray().ToList();
            if (optionsArray.Count > 0) variant.Option1 = optionsArray[0].GetProperty("value").GetString();
            if (optionsArray.Count > 1) variant.Option2 = optionsArray[1].GetProperty("value").GetString();
            if (optionsArray.Count > 2) variant.Option3 = optionsArray[2].GetProperty("value").GetString();
        }

        return variant;
    }

    private ShopifyImage ConvertToShopifyImage(JsonElement node, long productId, int position)
    {
        var imgIdStr = node.GetProperty("id").GetString();
        long legacyId = 0;
        if (imgIdStr.Contains("/"))
        {
            var parts = imgIdStr.Split('/');
            long.TryParse(parts.Last(), out legacyId);
        }

        return new ShopifyImage
        {
            Id = legacyId,
            ProductId = productId,
            Position = position,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Alt = GetStringOrNull(node, "altText"),
            Width = GetIntOrNull(node, "width") ?? 0,
            Height = GetIntOrNull(node, "height") ?? 0,
            Src = node.GetProperty("url").GetString(),
            AdminGraphqlApiId = imgIdStr
        };
    }

    private ShopifyOption ConvertToShopifyOption(JsonElement node, long productId, int position)
    {
        var optIdStr = node.GetProperty("id").GetString();
        long legacyId = 0;
        if (optIdStr.Contains("/"))
        {
            var parts = optIdStr.Split('/');
            long.TryParse(parts.Last(), out legacyId);
        }

        return new ShopifyOption
        {
            Id = legacyId,
            ProductId = productId,
            Name = node.GetProperty("name").GetString(),
            Position = position, // Position GraphQL'de yok, biz manuel set ediyoruz
            Values = node.GetProperty("values").EnumerateArray()
                .Select(v => v.GetString()).ToList()
        };
    }

    private async Task<HttpResponseMessage> ExecuteGraphQLAsync(string query)
    {
        var requestBody = new { query };
        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _client.PostAsync(_graphqlEndpoint, content);
    }

    private void CheckThrottleStatus(JsonDocument document)
    {
        if (document.RootElement.TryGetProperty("extensions", out var extensions))
        {
            if (extensions.TryGetProperty("cost", out var cost))
            {
                var available = cost.GetProperty("throttleStatus")
                    .GetProperty("currentlyAvailable").GetInt32();
                var max = cost.GetProperty("throttleStatus")
                    .GetProperty("maximumAvailable").GetDouble(); // Double olarak oku
                var usagePercent = (1 - (double)available / max) * 100;
                if (usagePercent > 80)
                {
                    Console.WriteLine($"   ⚠️ Throttle yüksek: {usagePercent:F1}% kullanıldı ({available}/{max:F0} kaldı)");
                }
            }
        }
    }

    private async Task SmartDelayAsync(JsonDocument document)
    {
        try
        {
            if (document.RootElement.TryGetProperty("extensions", out var extensions))
            {
                if (extensions.TryGetProperty("cost", out var cost))
                {
                    var available = cost.GetProperty("throttleStatus")
                        .GetProperty("currentlyAvailable").GetInt32();
                    var restoreRate = cost.GetProperty("throttleStatus")
                        .GetProperty("restoreRate").GetDouble();

                    // Eğer kullanılabilir puan çok düşükse, daha uzun bekle
                    if (available < 500)
                    {
                        var waitMs = Math.Max(2000, (int)((500 - available) / restoreRate * 1000));
                        Console.WriteLine($"   ⏳ Throttle düşük ({available} kaldı), {waitMs}ms bekleniyor...");
                        await Task.Delay(waitMs);
                        return;
                    }
                    else if (available < 1000)
                    {
                        Console.WriteLine($"   ⏳ Throttle orta ({available} kaldı), 1000ms bekleniyor...");
                        await Task.Delay(1000);
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ⚠️ SmartDelay hatası (ignore): {ex.Message}");
        }
        
        // Varsayılan minimal bekleme
        await Task.Delay(500);
    }

    private int GetRetryAfterSeconds(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            if (int.TryParse(values.FirstOrDefault(), out var seconds))
                return seconds;
        }
        return 60;
    }

    private string GetStringOrNull(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && 
               prop.ValueKind != JsonValueKind.Null 
            ? prop.GetString() 
            : null;
    }

    private DateTime? TryParseDateTime(JsonElement element, string propertyName)
    {
        var str = GetStringOrNull(element, propertyName);
        return string.IsNullOrEmpty(str) ? null : DateTime.Parse(str);
    }

    private decimal GetDecimal(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number)
                return prop.GetDecimal();
            if (prop.ValueKind == JsonValueKind.String && 
                decimal.TryParse(prop.GetString(), out var result))
                return result;
        }
        return 0;
    }

    private int? GetIntOrNull(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && 
            prop.ValueKind == JsonValueKind.Number)
            return prop.GetInt32();
        return null;
    }

    // Helper method: legacyResourceId'yi parse et (string veya number olabilir)
    private long ParseLegacyResourceId(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            // Number ise direkt al
            if (prop.ValueKind == JsonValueKind.Number)
                return prop.GetInt64();
            
            // String ise parse et
            if (prop.ValueKind == JsonValueKind.String)
            {
                var str = prop.GetString();
                if (long.TryParse(str, out var result))
                    return result;
            }
        }
        return 0;
    }
}

// ============================================
// 2. Program.cs'e ekleyin (Dependency Injection)
// ============================================
/*
// HttpClientFactory ekleyin
builder.Services.AddHttpClient();

// ShopifyGraphQLService'i ekleyin
builder.Services.AddScoped<ShopifyGraphQLService>();
*/

// ============================================
// 3. Controller'ınızı güncelleyin
// ============================================
/*
[ApiController]
[Route("api/[controller]")]
public class ShopifyController : ControllerBase
{
    private readonly ShopifyGraphQLService _graphqlService;
    private readonly IConfiguration _config;

    public ShopifyController(ShopifyGraphQLService graphqlService, IConfiguration config)
    {
        _graphqlService = graphqlService;
        _config = config;
    }

    [HttpGet("shopify-items")]
    public async Task<IActionResult> GetShopifyItems()
    {
        try
        {
            Console.WriteLine("🛍️ Shopify ürünleri getiriliyor (GraphQL)...");

            // GraphQL ile tüm ürünleri çek
            var products = await _graphqlService.GetAllProductsAsync(batchSize: 250);

            if (products == null || products.Count == 0)
            {
                Console.WriteLine("❌ Shopify ürünleri alınamadı");
                return Problem("Ürünler alınamadı veya token geçersiz.");
            }

            foreach (var product in products)
            {
                Console.WriteLine($"Ürün: {product.Id} - {product.Title} ({product.Vendor})");
            }

            Console.WriteLine($"✅ {products.Count} ürün alındı");
            return Ok(products);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Hata: {ex.Message}");
            return StatusCode(500, $"Bir hata oluştu: {ex.Message}");
        }
    }

    // İsteğe bağlı: Limit parametresiyle
    [HttpGet("shopify-items-limited")]
    public async Task<IActionResult> GetShopifyItemsLimited([FromQuery] int limit = 100)
    {
        try
        {
            Console.WriteLine($"🛍️ İlk {limit} Shopify ürünü getiriliyor (GraphQL)...");

            var products = await _graphqlService.GetAllProductsAsync(
                batchSize: 250, 
                maxProducts: limit
            );

            Console.WriteLine($"✅ {products.Count} ürün alındı");
            return Ok(products);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Hata: {ex.Message}");
            return StatusCode(500, $"Bir hata oluştu: {ex.Message}");
        }
    }
}
*/