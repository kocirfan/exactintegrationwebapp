using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ShopifyProductApp.Services.ExactPriceList;

/// <summary>
/// Shopify B2B katalog/price list işlemleri (Admin GraphQL 2025-01).
///
/// Zincir: Company → CompanyLocation → Catalog → PriceList → fixed price.
/// Sabit fiyat kataloğa bağlı olduğu için yalnızca o location'a bağlı müşteri görür;
/// tag tabanlı çözümlerdeki sızıntı riski yapısal olarak yoktur.
/// </summary>
public class ShopifyB2BCatalogService
{
    private readonly HttpClient _client;
    private readonly ILogger<ShopifyB2BCatalogService> _logger;

    /// <summary>B2B mutation'ları için 2025-01 gerekir (mevcut ShopifyGraphQLService 2024-01'de kalıyor).</summary>
    private const string Endpoint = "admin/api/2025-01/graphql.json";

    /// <summary>priceListFixedPricesAdd tek çağrıda sınırlı sayıda fiyat kabul eder.</summary>
    private const int FixedPriceBatchSize = 250;

    public ShopifyB2BCatalogService(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<ShopifyB2BCatalogService> logger)
    {
        _logger = logger;

        var storeUrl = config["Shopify:StoreUrl"];
        var accessToken = config["Shopify:AccessToken"];

        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri(storeUrl);
        _client.DefaultRequestHeaders.Add("X-Shopify-Access-Token", accessToken);
        _client.Timeout = TimeSpan.FromMinutes(3);
    }

    // ---------------------------------------------------------------
    // SKU -> variant ID eşlemesi
    // ---------------------------------------------------------------

    /// <summary>
    /// Verilen SKU'ları Shopify variant GID'lerine çevirir.
    /// Bulunamayanlar sonuçta yer almaz; çağıran taraf farkı raporlamalıdır.
    /// </summary>
    public async Task<Dictionary<string, string>> ResolveVariantIdsBySkuAsync(
        IEnumerable<string> skus, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var distinct = skus
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation("🔎 {Count} SKU için variant ID çözümleniyor...", distinct.Count);

        // Shopify sorgu uzunluğu sınırlı; SKU'ları gruplar halinde ara.
        const int chunkSize = 25;

        for (int i = 0; i < distinct.Count; i += chunkSize)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var chunk = distinct.Skip(i).Take(chunkSize).ToList();
            var searchQuery = string.Join(" OR ", chunk.Select(s => $"sku:{EscapeSearchTerm(s)}"));

            var query = @"
query($q: String!) {
  productVariants(first: 250, query: $q) {
    edges { node { id sku } }
  }
}";

            var payload = new { query, variables = new { q = searchQuery } };
            var json = await ExecuteAsync(payload, cancellationToken);

            if (json == null) continue;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data)) continue;
                if (!data.TryGetProperty("productVariants", out var variants)) continue;
                if (!variants.TryGetProperty("edges", out var edges)) continue;

                foreach (var edge in edges.EnumerateArray())
                {
                    var node = edge.GetProperty("node");
                    var sku = node.TryGetProperty("sku", out var s) ? s.GetString() : null;
                    var id = node.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(sku) && !string.IsNullOrWhiteSpace(id))
                        result[sku.Trim()] = id;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Variant çözümleme yanıtı ayrıştırılamadı");
            }

            await Task.Delay(300, cancellationToken);
        }

        _logger.LogInformation("✅ {Found}/{Total} SKU eşleşti", result.Count, distinct.Count);
        return result;
    }

    // ---------------------------------------------------------------
    // Company / Location / Catalog / PriceList arama ve kurulum
    // ---------------------------------------------------------------

    /// <summary>
    /// İsme göre Shopify B2B company arar. Bulamazsa null döner (oluşturmaz).
    /// </summary>
    public async Task<CompanyInfo> FindCompanyByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var query = @"
query($q: String!) {
  companies(first: 5, query: $q) {
    edges {
      node {
        id
        name
        locations(first: 10) { edges { node { id name } } }
      }
    }
  }
}";

        var payload = new { query, variables = new { q = $"name:{EscapeSearchTerm(name)}" } };
        var json = await ExecuteAsync(payload, cancellationToken);
        if (json == null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
            if (!data.TryGetProperty("companies", out var companies)) return null;
            if (!companies.TryGetProperty("edges", out var edges)) return null;
            if (edges.GetArrayLength() == 0) return null;

            var node = edges[0].GetProperty("node");
            var info = new CompanyInfo
            {
                Id = node.GetProperty("id").GetString(),
                Name = node.TryGetProperty("name", out var n) ? n.GetString() : null
            };

            if (node.TryGetProperty("locations", out var locs) &&
                locs.TryGetProperty("edges", out var locEdges))
            {
                foreach (var le in locEdges.EnumerateArray())
                {
                    var ln = le.GetProperty("node");
                    info.LocationIds.Add(ln.GetProperty("id").GetString());
                }
            }

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Company arama yanıtı ayrıştırılamadı");
            return null;
        }
    }

    /// <summary>
    /// Bir company location'a bağlı katalog ve price list oluşturur.
    /// Zaten varsa mevcut olanı döndürmez — çağıran taraf önce kontrol etmelidir.
    /// </summary>
    public async Task<CatalogInfo> CreateCatalogWithPriceListAsync(
        string title, string companyLocationId, string currencyCode = "EUR",
        CancellationToken cancellationToken = default)
    {
        var mutation = @"
mutation($input: CatalogCreateInput!) {
  catalogCreate(input: $input) {
    catalog {
      id
      title
      ... on CompanyLocationCatalog {
        priceList { id name }
      }
    }
    userErrors { field message }
  }
}";

        var input = new
        {
            title,
            status = "ACTIVE",
            context = new { companyLocationIds = new[] { companyLocationId } },
            priceListInput = new
            {
                name = title,
                currency = currencyCode
            }
        };

        var payload = new { query = mutation, variables = new { input } };
        var json = await ExecuteAsync(payload, cancellationToken);
        if (json == null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data)) return null;
            if (!data.TryGetProperty("catalogCreate", out var cc)) return null;

            if (HasUserErrors(cc, out var errors))
            {
                _logger.LogError("❌ catalogCreate hatası: {Errors}", errors);
                return null;
            }

            if (!cc.TryGetProperty("catalog", out var catalog) || catalog.ValueKind == JsonValueKind.Null)
                return null;

            var info = new CatalogInfo
            {
                CatalogId = catalog.GetProperty("id").GetString(),
                Title = catalog.TryGetProperty("title", out var t) ? t.GetString() : title
            };

            if (catalog.TryGetProperty("priceList", out var pl) && pl.ValueKind != JsonValueKind.Null)
                info.PriceListId = pl.GetProperty("id").GetString();

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ catalogCreate yanıtı ayrıştırılamadı");
            return null;
        }
    }

    // ---------------------------------------------------------------
    // Sabit fiyat yazma
    // ---------------------------------------------------------------

    /// <summary>
    /// Price list'e sabit fiyatları toplu yazar. Mevcut aynı variant fiyatı güncellenir.
    /// </summary>
    public async Task<FixedPriceResult> AddFixedPricesAsync(
        string priceListId,
        IReadOnlyList<FixedPriceInput> prices,
        CancellationToken cancellationToken = default)
    {
        var result = new FixedPriceResult();

        var mutation = @"
mutation($priceListId: ID!, $prices: [PriceListPriceInput!]!) {
  priceListFixedPricesAdd(priceListId: $priceListId, prices: $prices) {
    pricesAdded { variant { id sku } price { amount currencyCode } }
    userErrors { field message code }
  }
}";

        for (int i = 0; i < prices.Count; i += FixedPriceBatchSize)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var batch = prices.Skip(i).Take(FixedPriceBatchSize).ToList();

            var variables = new
            {
                priceListId,
                prices = batch.Select(p => new
                {
                    variantId = p.VariantId,
                    price = new
                    {
                        amount = p.Amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                        currencyCode = p.CurrencyCode
                    }
                }).ToArray()
            };

            var payload = new { query = mutation, variables };
            var json = await ExecuteAsync(payload, cancellationToken);

            if (json == null)
            {
                result.FailedCount += batch.Count;
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("priceListFixedPricesAdd", out var add))
                {
                    result.FailedCount += batch.Count;
                    continue;
                }

                if (HasUserErrors(add, out var errors))
                {
                    _logger.LogError("❌ priceListFixedPricesAdd hatası: {Errors}", errors);
                    result.Errors.Add(errors);
                    result.FailedCount += batch.Count;
                    continue;
                }

                int added = add.TryGetProperty("pricesAdded", out var pa) && pa.ValueKind == JsonValueKind.Array
                    ? pa.GetArrayLength()
                    : 0;

                result.AddedCount += added;
                result.FailedCount += batch.Count - added;

                _logger.LogInformation("💾 {Added}/{Total} sabit fiyat yazıldı (toplam {Running})",
                    added, batch.Count, result.AddedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Sabit fiyat yanıtı ayrıştırılamadı");
                result.FailedCount += batch.Count;
            }

            await Task.Delay(500, cancellationToken);
        }

        return result;
    }

    // ---------------------------------------------------------------
    // Altyapı
    // ---------------------------------------------------------------

    private async Task<string> ExecuteAsync(object payload, CancellationToken cancellationToken)
    {
        int attempt = 0;
        const int maxAttempts = 4;

        while (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
        {
            attempt++;

            try
            {
                var body = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var resp = await _client.PostAsync(Endpoint, body, cancellationToken);

                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning("⏳ Shopify rate limit, 5 saniye bekleniyor...");
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    continue;
                }

                var json = await resp.Content.ReadAsStringAsync(cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogError("❌ Shopify GraphQL {Status}: {Body}",
                        resp.StatusCode, Truncate(json, 400));
                    return null;
                }

                // GraphQL seviyesi hatalar 200 ile de dönebilir.
                if (json.Contains("\"errors\"", StringComparison.Ordinal))
                    _logger.LogWarning("⚠️ GraphQL errors: {Body}", Truncate(json, 400));

                return json;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Shopify isteği başarısız (deneme {Attempt}/{Max})", attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
        }

        return null;
    }

    private static bool HasUserErrors(JsonElement element, out string message)
    {
        message = null;

        if (!element.TryGetProperty("userErrors", out var ue) ||
            ue.ValueKind != JsonValueKind.Array ||
            ue.GetArrayLength() == 0)
            return false;

        message = string.Join("; ", ue.EnumerateArray()
            .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : "?"));
        return true;
    }

    /// <summary>Shopify arama sözdiziminde sorun çıkaran karakterleri temizler.</summary>
    private static string EscapeSearchTerm(string term)
        => "\"" + term.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Truncate(string value, int max)
        => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max] + "...";
}

public class CompanyInfo
{
    public string Id { get; set; }
    public string Name { get; set; }
    public List<string> LocationIds { get; set; } = new();
}

public class CatalogInfo
{
    public string CatalogId { get; set; }
    public string PriceListId { get; set; }
    public string Title { get; set; }
}

public class FixedPriceInput
{
    public string VariantId { get; set; }
    public string Sku { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "EUR";
}

public class FixedPriceResult
{
    public int AddedCount { get; set; }
    public int FailedCount { get; set; }
    public List<string> Errors { get; set; } = new();
}
