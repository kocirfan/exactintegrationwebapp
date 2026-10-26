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

    // ============================================
    // ShopifyGraphQLService.cs içine ekleyin
    // ============================================

    /// <summary>
    /// GraphQL ile tüm müşterileri paginate ederek çeker
    /// </summary>
    public async Task<List<ShopifyCustomer>> GetAllCustomersAsync(
        int batchSize = 250,
        int? maxCustomers = null)
    {
        if (batchSize > 250) batchSize = 250;
        if (batchSize < 1) batchSize = 250;

        var allCustomers = new List<ShopifyCustomer>();
        string cursor = null;
        bool hasNextPage = true;
        int pageCount = 0;
        int retryCount = 0;
        const int maxRetries = 3;

        Console.WriteLine($"🛍️ GraphQL ile müşteriler getiriliyor (sayfa başına {batchSize} müşteri)...");

        while (hasNextPage)
        {
            pageCount++;
            var query = BuildCustomerQuery(batchSize, cursor);

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
                            await Task.Delay(5000);
                            pageCount--;
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

                var customers = data.GetProperty("customers");
                var edges = customers.GetProperty("edges");
                var pageInfo = customers.GetProperty("pageInfo");

                int pageCustomerCount = 0;
                foreach (var edge in edges.EnumerateArray())
                {
                    var node = edge.GetProperty("node");
                    var customer = ConvertToShopifyCustomer(node);
                    allCustomers.Add(customer);
                    pageCustomerCount++;

                    if (maxCustomers.HasValue && allCustomers.Count >= maxCustomers.Value)
                    {
                        Console.WriteLine($"✅ Maksimum müşteri sayısına ulaşıldı: {maxCustomers.Value}");
                        return allCustomers.Take(maxCustomers.Value).ToList();
                    }
                }

                Console.WriteLine($"📄 Sayfa {pageCount}: {pageCustomerCount} müşteri alındı. Toplam: {allCustomers.Count}");

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

        Console.WriteLine($"✅ Toplam {allCustomers.Count} müşteri {pageCount} sayfada alındı");
        return allCustomers;
    }

    /// <summary>
    /// Müşteri verileri için GraphQL query oluşturur
    /// </summary>
    private string BuildCustomerQuery(int first, string cursor)
    {
        var afterClause = string.IsNullOrEmpty(cursor) ? "" : $", after: \"{cursor}\"";

        return $@"
    {{
      customers(first: {first}{afterClause}) {{
        edges {{
          cursor
          node {{
            id
            legacyResourceId
            firstName
            lastName
            email
            phone
            state
            tags
            createdAt
            updatedAt
            defaultAddress {{
              id
              firstName
              lastName
              address1
              address2
              city
              zip
              country
              countryCode
              province
              provinceCode
            }}
            metafields(first: 100) {{
              edges {{
                node {{
                  id
                  namespace
                  key
                  value
                  type
                }}
              }}
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

    /// <summary>
    /// GraphQL customer node'unu ShopifyCustomer objesine dönüştürür
    /// </summary>
    private ShopifyCustomer ConvertToShopifyCustomer(JsonElement node)
    {
        var customer = new ShopifyCustomer
        {
            Id = ParseLegacyResourceId(node, "legacyResourceId"),
            FirstName = GetStringOrNull(node, "firstName"),
            LastName = GetStringOrNull(node, "lastName"),
            Email = GetStringOrNull(node, "email"),
            Metafields = new List<ShopifyMetafield>()
        };

        // Default Address
        if (node.TryGetProperty("defaultAddress", out var defaultAddr) &&
            defaultAddr.ValueKind != JsonValueKind.Null)
        {
            customer.DefaultAddress = ConvertToShopifyAddress(defaultAddr);
        }

        // Tüm adresler
        var addresses = new List<ShopifyAddress>();
        if (node.TryGetProperty("addresses", out var addressesNode))
        {
            foreach (var edge in addressesNode.GetProperty("edges").EnumerateArray())
            {
                var addrNode = edge.GetProperty("node");
                addresses.Add(ConvertToShopifyAddress(addrNode));
            }
        }

        // Metafields
        if (node.TryGetProperty("metafields", out var metafields))
        {
            foreach (var edge in metafields.GetProperty("edges").EnumerateArray())
            {
                var mfNode = edge.GetProperty("node");
                customer.Metafields.Add(ConvertToShopifyMetafield(mfNode));
            }
        }

        return customer;
    }

    /// <summary>
    /// GraphQL address node'unu ShopifyAddress objesine dönüştürür
    /// </summary>
    private ShopifyAddress ConvertToShopifyAddress(JsonElement node)
    {
        return new ShopifyAddress
        {
            FirstName = GetStringOrNull(node, "firstName"),
            LastName = GetStringOrNull(node, "lastName"),
            Address1 = GetStringOrNull(node, "address1"),
            Address2 = GetStringOrNull(node, "address2"),
            City = GetStringOrNull(node, "city"),
            Zip = GetStringOrNull(node, "zip"),
            Country = GetStringOrNull(node, "country"),
            CountryCode = GetStringOrNull(node, "countryCode")
        };
    }

    /// <summary>
    /// GraphQL metafield node'unu ShopifyMetafield objesine dönüştürür
    /// </summary>
    private ShopifyMetafield ConvertToShopifyMetafield(JsonElement node)
    {
        return new ShopifyMetafield
        {
            Id = GetStringOrNull(node, "id"),
            Namespace = GetStringOrNull(node, "namespace"),
            Key = GetStringOrNull(node, "key"),
            Value = GetStringOrNull(node, "value"),
            Type = GetStringOrNull(node, "type")
        };
    }


    public async Task<ShopifyCustomer> SearchCustomerByEmailOrCodeAsync(
    string email = null,
    string customerCode = null)
    {
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(customerCode))
        {
            Console.WriteLine("❌ Email veya Customer Code parametrelerinden en az biri gereklidir");
            return null;
        }

        try
        {
            // 1. ADIM: Email ile ara (varsa direkt döndür)
            if (!string.IsNullOrWhiteSpace(email))
            {
                Console.WriteLine($"🔍 Email ile aranıyor: {email}");
                var customerByEmail = await SearchCustomerByEmailAsync(email);

                if (customerByEmail != null)
                {
                    Console.WriteLine($"✅ Müşteri email ile bulundu!");
                    Console.WriteLine($"   ID: {customerByEmail.Id}");
                    Console.WriteLine($"   Ad: {customerByEmail.FirstName} {customerByEmail.LastName}");
                    return customerByEmail;
                }

                Console.WriteLine($"⚠️ Email ile müşteri bulunamadı: {email}");
            }

            // 2. ADIM: Customer Code ile ara
            if (!string.IsNullOrWhiteSpace(customerCode))
            {
                Console.WriteLine($"🔍 Customer Code ile aranıyor: {customerCode}");
                var customerByCode = await SearchCustomerByMetafieldCodeAsync(customerCode);

                if (customerByCode != null)
                {
                    Console.WriteLine($"✅ Müşteri code ile bulundu!");
                    Console.WriteLine($"   ID: {customerByCode.Id}");
                    Console.WriteLine($"   Ad: {customerByCode.FirstName} {customerByCode.LastName}");
                    Console.WriteLine($"   Email: {customerByCode.Email}");
                    return customerByCode;
                }

                Console.WriteLine($"⚠️ Customer Code ile müşteri bulunamadı: {customerCode}");
            }

            Console.WriteLine("❌ Hiçbir müşteri bulunamadı");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Müşteri arama hatası: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Email ile müşteri arar (GraphQL)
    /// </summary>
    private async Task<ShopifyCustomer> SearchCustomerByEmailAsync(string email)
    {
        var query = $@"
    {{
      customers(first: 1, query: ""email:{email}"") {{
        edges {{
          node {{
            id
            legacyResourceId
            firstName
            lastName
            email
            phone
            state
            tags
            createdAt
            updatedAt
            defaultAddress {{
              id
              firstName
              lastName
              address1
              address2
              city
              zip
              country
              countryCode
              province
              provinceCode
            }}
            metafields(first: 100) {{
              edges {{
                node {{
                  id
                  namespace
                  key
                  value
                  type
                }}
              }}
            }}
          }}
        }}
      }}
    }}";

        try
        {
            var response = await ExecuteGraphQLAsync(query);
            response.EnsureSuccessStatusCode();

            var jsonResponse = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(jsonResponse);

            if (!document.RootElement.TryGetProperty("data", out var data))
            {
                return null;
            }

            var customers = data.GetProperty("customers");
            var edges = customers.GetProperty("edges").EnumerateArray().ToList();

            if (edges.Count == 0)
            {
                return null;
            }

            var node = edges[0].GetProperty("node");
            return ConvertToShopifyCustomer(node);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Email arama hatası: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Metafield Code ile müşteri arar
    /// "exact_online.customer_code" veya "custom.customer_code" içinde arar
    /// </summary>
    /// 
    // Müşterinin custom.exact_customer_id metafield'ı (Exact Account GUID); yoksa/hata olursa null
    public async Task<string> GetCustomerExactIdAsync(long shopifyCustomerId)
    {
        if (shopifyCustomerId <= 0)
            return null;

        try
        {
            var query = $@"{{ customer(id: ""gid://shopify/Customer/{shopifyCustomerId}"") {{ metafield(namespace: ""custom"", key: ""exact_customer_id"") {{ value }} }} }}";
            var payload = JsonSerializer.Serialize(new { query });
            var response = await _client.PostAsync(_graphqlEndpoint, new StringContent(payload, Encoding.UTF8, "application/json"));
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"⚠️ exact_customer_id metafield okunamadı: {response.StatusCode}");
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("customer", out var customer) && customer.ValueKind == JsonValueKind.Object &&
                customer.TryGetProperty("metafield", out var metafield) && metafield.ValueKind == JsonValueKind.Object &&
                metafield.TryGetProperty("value", out var value))
            {
                var exactId = value.GetString()?.Trim();
                return string.IsNullOrEmpty(exactId) ? null : exactId;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ exact_customer_id metafield okuma hatası: {ex.Message}");
        }

        return null;
    }

    public async Task<ShopifyCustomer> SearchCustomerByMetafieldCodeAsync(string customerCode)
    {
        if (string.IsNullOrWhiteSpace(customerCode))
        {
            Console.WriteLine("❌ Customer Code boş olamaz");
            return null;
        }

        // İki namespace'i deneyeceğiz
        string[] namespaces = { "exact_online", "custom" };

        foreach (var ns in namespaces)
        {
            Console.WriteLine($"   └─ Namespace '{ns}' kontrol ediliyor...");

            string cursor = null;
            bool hasNextPage = true;
            int pageCount = 0;

            while (hasNextPage)
            {
                pageCount++;
                var afterClause = string.IsNullOrEmpty(cursor) ? "" : $", after: \"{cursor}\"";

                var query = $@"
            {{
              customers(first: 250{afterClause}) {{
                edges {{
                  cursor
                  node {{
                    id
                    legacyResourceId
                    firstName
                    lastName
                    email
                    phone
                    state
                    tags
                    createdAt
                    updatedAt
                    defaultAddress {{
                      id
                      firstName
                      lastName
                      address1
                      address2
                      city
                      zip
                      country
                      countryCode
                      province
                      provinceCode
                    }}
                    metafields(first: 100, namespace: ""{ns}"") {{
                      edges {{
                        node {{
                          id
                          namespace
                          key
                          value
                          type
                        }}
                      }}
                    }}
                  }}
                }}
                pageInfo {{
                  hasNextPage
                  hasPreviousPage
                }}
              }}
            }}";

                try
                {
                    var response = await ExecuteGraphQLAsync(query);
                    response.EnsureSuccessStatusCode();

                    var jsonResponse = await response.Content.ReadAsStringAsync();
                    using var document = JsonDocument.Parse(jsonResponse);

                    // GraphQL hatalarını kontrol et
                    if (document.RootElement.TryGetProperty("errors", out var errors))
                    {
                        var errorList = errors.EnumerateArray().ToList();
                        Console.WriteLine($"   └─ ⚠️ GraphQL Hatası: {errorList.FirstOrDefault().GetProperty("message").GetString()}");
                        break;
                    }

                    if (!document.RootElement.TryGetProperty("data", out var data))
                    {
                        Console.WriteLine($"   └─ ⚠️ Response'da 'data' bulunamadı");
                        break;
                    }

                    var customers = data.GetProperty("customers");
                    var edges = customers.GetProperty("edges").EnumerateArray().ToList();

                    Console.WriteLine($"   └─ Sayfa {pageCount}: {edges.Count} müşteri kontrol ediliyor...");

                    // Tüm müşterileri kontrol et
                    foreach (var edge in edges)
                    {
                        var node = edge.GetProperty("node");
                        var foundCustomer = await CheckCustomerMetafieldAsync(node, ns, customerCode);

                        if (foundCustomer != null)
                        {
                            Console.WriteLine($"   └─ ✅ Eşleşme bulundu! ({ns}.customer_code = {customerCode})");
                            return foundCustomer;
                        }
                    }

                    // Sonraki sayfa var mı kontrol et
                    var pageInfo = customers.GetProperty("pageInfo");
                    hasNextPage = pageInfo.GetProperty("hasNextPage").GetBoolean();

                    if (hasNextPage)
                    {
                        cursor = edges.Last().GetProperty("cursor").GetString();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   └─ ⚠️ Namespace '{ns}' Sayfa {pageCount} hatası: {ex.Message}");
                    break;
                }

                // Rate limit için delay
                await SmartDelayAsync(null);
            }
        }

        return null;
    }

    /// <summary>
    /// Tek bir müşterinin metafield'larını kontrol eder
    /// </summary>
    private async Task<ShopifyCustomer> CheckCustomerMetafieldAsync(JsonElement node, string targetNamespace, string targetCode)
    {
        try
        {
            // Metafield'ları kontrol et
            if (node.TryGetProperty("metafields", out var metafields))
            {
                var metafieldEdges = metafields.GetProperty("edges").EnumerateArray();

                foreach (var mfEdge in metafieldEdges)
                {
                    var mfNode = mfEdge.GetProperty("node");

                    var key = GetStringOrNull(mfNode, "key");
                    var value = GetStringOrNull(mfNode, "value");
                    var mfNamespace = GetStringOrNull(mfNode, "namespace");

                    // KRITIK KONTROL: Namespace, Key ve Value'nin hepsinin eşleşmesi gerekir
                    if (mfNamespace == targetNamespace &&
                        key == "customer_code" &&
                        value == targetCode)
                    {
                        // Eşleşme bulundu!
                        return ConvertToShopifyCustomer(node);
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   └─ ⚠️ Metafield kontrol hatası: {ex.Message}");
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // BONUS: Tüm Code'ları Listele (Debug İçin)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tüm müşterilerin metafield customer_code'larını listeler (DEBUG)
    /// </summary>
    public async Task DebugListAllCustomerCodesAsync()
    {
        Console.WriteLine("\n🔍 TÜM MÜŞTERİ KODLARI LİSTELENİYOR...\n");

        string[] namespaces = { "exact_online", "custom" };

        foreach (var ns in namespaces)
        {
            Console.WriteLine($"📌 Namespace: {ns}");
            Console.WriteLine("─────────────────────────────────────────");

            string cursor = null;
            bool hasNextPage = true;
            int pageCount = 0;
            int totalFound = 0;

            while (hasNextPage)
            {
                pageCount++;
                var afterClause = string.IsNullOrEmpty(cursor) ? "" : $", after: \"{cursor}\"";

                var query = $@"
            {{
              customers(first: 250{afterClause}) {{
                edges {{
                  cursor
                  node {{
                    id
                    email
                    firstName
                    lastName
                    metafields(first: 100, namespace: ""{ns}"") {{
                      edges {{
                        node {{
                          namespace
                          key
                          value
                        }}
                      }}
                    }}
                  }}
                }}
                pageInfo {{
                  hasNextPage
                }}
              }}
            }}";

                try
                {
                    var response = await ExecuteGraphQLAsync(query);
                    response.EnsureSuccessStatusCode();

                    var jsonResponse = await response.Content.ReadAsStringAsync();
                    using var document = JsonDocument.Parse(jsonResponse);

                    if (!document.RootElement.TryGetProperty("data", out var data))
                        break;

                    var customers = data.GetProperty("customers");
                    var edges = customers.GetProperty("edges").EnumerateArray().ToList();

                    foreach (var edge in edges)
                    {
                        var node = edge.GetProperty("node");
                        var email = GetStringOrNull(node, "email");
                        var firstName = GetStringOrNull(node, "firstName");
                        var lastName = GetStringOrNull(node, "lastName");

                        if (node.TryGetProperty("metafields", out var metafields))
                        {
                            var mfEdges = metafields.GetProperty("edges").EnumerateArray();

                            foreach (var mfEdge in mfEdges)
                            {
                                var mfNode = mfEdge.GetProperty("node");
                                var key = GetStringOrNull(mfNode, "key");
                                var value = GetStringOrNull(mfNode, "value");
                                var mfNamespace = GetStringOrNull(mfNode, "namespace");

                                if (key == "customer_code")
                                {
                                    Console.WriteLine($"   ✅ {firstName} {lastName} ({email})");
                                    Console.WriteLine($"      └─ {mfNamespace}.customer_code = {value}");
                                    totalFound++;
                                }
                            }
                        }
                    }

                    var pageInfo = customers.GetProperty("pageInfo");
                    hasNextPage = pageInfo.GetProperty("hasNextPage").GetBoolean();

                    if (hasNextPage)
                    {
                        cursor = edges.Last().GetProperty("cursor").GetString();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ⚠️ Sayfa {pageCount} hatası: {ex.Message}");
                    break;
                }

                await SmartDelayAsync(null);
            }

            Console.WriteLine($"📊 {ns} namespace'inde {totalFound} kod bulundu\n");
        }
    }
}
