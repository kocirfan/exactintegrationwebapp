using System.Net.Http.Headers;
using System.Text.Json;

namespace ShopifyProductApp.Services.ExactPriceList;

/// <summary>
/// Exact Online'daki <b>müşteriye özel ürün fiyatlarını</b> yalnızca okuyarak keşif raporu üretir.
///
/// Kaynak: <c>logistics/SalesItemPrices</c> içinde <c>Account</c> alanı dolu olan satırlar.
/// (<c>sales/SalesPriceLists</c> ayrı bir kampanya nesnesidir ve müşteri fiyatlarını içermez.)
///
/// Bu servis hiçbir yere yazmaz. Amacı Shopify B2B katalog kurulumuna geçmeden önce
/// gerçek veriyi görmektir: kaç müşteri, kaç SKU, kaç satır aktif, hangileri sorunlu.
/// </summary>
public class ExactPriceListDiscoveryService
{
    private readonly ExactService _exactService;
    private readonly ILogger<ExactPriceListDiscoveryService> _logger;
    private readonly string _baseUrl;
    private readonly string _divisionCode;

    private const int PageSize = 60;

    /// <summary>Exact $filter kullanırken $select zorunlu; çekilen alanlar.</summary>
    private const string SelectFields =
        "ID,Account,AccountName,Item,ItemCode,ItemDescription,Price,Currency,Quantity,StartDate,EndDate";

    public ExactPriceListDiscoveryService(
        ExactService exactService,
        ILogger<ExactPriceListDiscoveryService> logger,
        IConfiguration configuration)
    {
        _exactService = exactService;
        _logger = logger;
        _baseUrl = configuration["ExactOnline:BaseUrl"];
        _divisionCode = configuration["ExactOnline:DivisionCode"];
    }

    /// <summary>
    /// Tüm müşteriye özel fiyatları tarar ve raporu döndürür. Hiçbir veri yazılmaz.
    /// </summary>
    public async Task<CustomerPriceDiscoveryReport> RunDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        var report = new CustomerPriceDiscoveryReport();

        var token = await _exactService.GetValidToken();
        if (token == null)
        {
            _logger.LogError("❌ Exact token alınamadı, keşif iptal edildi");
            return report;
        }

        using var client = CreateClient(token.access_token);

        _logger.LogInformation("🔍 Müşteriye özel fiyat keşfi başlıyor (salt okunur)...");

        var lines = await FetchCustomerPricesAsync(client, cancellationToken);
        report.TotalCustomerPriceLines = lines.Count;

        if (lines.Count == 0)
        {
            _logger.LogWarning("⚠️ Account alanı dolu hiçbir fiyat satırı bulunamadı");
            return report;
        }

        var standardPrices = await BuildStandardPriceMapAsync(cancellationToken);
        var now = DateTime.UtcNow;

        // Müşteri bazında grupla — her grup bir Shopify kataloğuna karşılık gelir.
        foreach (var group in lines.GroupBy(l => l.AccountId))
        {
            var groupDto = new CustomerPriceGroupDto
            {
                AccountId = group.Key,
                AccountName = group.First().AccountName,
                Prices = group.ToList()
            };

            foreach (var line in group)
            {
                if (line.EndDate.HasValue && line.EndDate.Value < now)
                {
                    groupDto.ExpiredPriceCount++;
                    report.ExpiredPriceLines++;
                }
                else if (line.StartDate.HasValue && line.StartDate.Value > now)
                {
                    groupDto.FuturePriceCount++;
                    report.FuturePriceLines++;
                }
                else
                {
                    groupDto.ActivePriceCount++;
                    report.ActivePriceLines++;
                }

                // Adet kademeli fiyat Shopify'da sabit fiyata çevrilemez.
                if (line.Quantity > 1)
                {
                    groupDto.QuantityTieredCount++;
                    report.QuantityTieredLines++;
                }

                if (!string.IsNullOrWhiteSpace(line.Currency) &&
                    !line.Currency.Trim().Equals("EUR", StringComparison.OrdinalIgnoreCase) &&
                    !report.NonEuroCurrencies.Contains(line.Currency.Trim()))
                {
                    report.NonEuroCurrencies.Add(line.Currency.Trim());
                }

                AnalyzeAgainstStandard(line, standardPrices, report);
            }

            report.Customers.Add(groupDto);
        }

        // Müşteri tipi: VAT/KvK numarası olmayanlara B2B katalog kurulamaz.
        await EnrichBusinessIdentityAsync(client, report, cancellationToken);

        report.DistinctCustomerCount = report.Customers.Count;
        report.DistinctItemCount = lines
            .Where(l => !string.IsNullOrWhiteSpace(l.ItemCode))
            .Select(l => l.ItemCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        _logger.LogInformation(
            "✅ Keşif tamamlandı: {Lines} satır, {Customers} müşteri, {Items} SKU, {Active} aktif, {Expired} süresi geçmiş, {Tiered} adet kademeli",
            report.TotalCustomerPriceLines, report.DistinctCustomerCount, report.DistinctItemCount,
            report.ActivePriceLines, report.ExpiredPriceLines, report.QuantityTieredLines);

        return report;
    }

    /// <summary>
    /// Her müşteri için Exact'tan VAT ve KvK numarasını çekip B2B uygunluğunu belirler.
    /// Hiçbiri olmayanlar şahıs kabul edilir — onlara katalog kurulmaz.
    /// </summary>
    private async Task EnrichBusinessIdentityAsync(
        HttpClient client, CustomerPriceDiscoveryReport report, CancellationToken cancellationToken)
    {
        foreach (var customer in report.Customers)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var url = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Accounts" +
                      $"?$filter=ID eq guid'{customer.AccountId}'&$select=VATNumber,ChamberOfCommerce";

            var items = await GetResultsArrayAsync(client, url, cancellationToken);
            if (items == null || items.Value.GetArrayLength() == 0) continue;

            var el = items.Value[0];
            customer.VatNumber = ReadString(el, "VATNumber")?.Trim();
            customer.ChamberOfCommerce = ReadString(el, "ChamberOfCommerce")?.Trim();

            if (!customer.LooksLikeBusiness)
                report.CustomersWithoutBusinessId.Add(customer.AccountName);
        }

        if (report.CustomersWithoutBusinessId.Count > 0)
        {
            _logger.LogWarning(
                "⚠️ {Count} müşteride VAT/KvK numarası yok — B2B katalog kurulamaz, normal fiyattan alışveriş yaparlar",
                report.CustomersWithoutBusinessId.Count);
        }
    }

    /// <summary>Özel fiyatı normal satış fiyatıyla karşılaştırır.</summary>
    private static void AnalyzeAgainstStandard(
        CustomerItemPriceDto line,
        Dictionary<string, decimal> standardPrices,
        CustomerPriceDiscoveryReport report)
    {
        if (string.IsNullOrWhiteSpace(line.ItemCode)) return;

        if (!standardPrices.TryGetValue(line.ItemCode, out var standard))
        {
            if (!report.UnresolvedItemCodes.Contains(line.ItemCode))
                report.UnresolvedItemCodes.Add(line.ItemCode);
            return;
        }

        if (standard > 0 && line.Price > standard)
        {
            report.PricesAboveStandard.Add(new PriceAnomalyDto
            {
                AccountName = line.AccountName,
                ItemCode = line.ItemCode,
                CustomerPrice = line.Price,
                StandardSalesPrice = standard
            });
        }
    }

    /// <summary>SKU -> StandardSalesPrice sözlüğü kurar.</summary>
    private async Task<Dictionary<string, decimal>> BuildStandardPriceMapAsync(CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var items = await _exactService.GetAllItemsSummaryAsync();
            foreach (var item in items ?? new List<ExactItemSummaryDto>())
            {
                if (!string.IsNullOrWhiteSpace(item.Code) && item.StandardSalesPrice.HasValue)
                    map[item.Code] = item.StandardSalesPrice.Value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Normal satış fiyatları çekilemedi, karşılaştırma atlanacak");
        }

        return map;
    }

    /// <summary>
    /// Account alanı dolu tüm fiyat satırlarını sayfalayarak çeker.
    /// </summary>
    private async Task<List<CustomerItemPriceDto>> FetchCustomerPricesAsync(
        HttpClient client, CancellationToken cancellationToken)
    {
        var result = new List<CustomerItemPriceDto>();
        int skip = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var filter = Uri.EscapeDataString("Account ne null");
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/SalesItemPrices" +
                      $"?$filter={filter}&$select={SelectFields}&$top={PageSize}&$skip={skip}";

            var items = await GetResultsArrayAsync(client, url, cancellationToken);
            if (items == null) break;

            int count = 0;
            foreach (var item in items.Value.EnumerateArray())
            {
                count++;

                var accountId = ReadString(item, "Account");
                if (string.IsNullOrWhiteSpace(accountId)) continue;

                result.Add(new CustomerItemPriceDto
                {
                    ID = ReadString(item, "ID"),
                    AccountId = accountId,
                    AccountName = ReadString(item, "AccountName"),
                    ItemId = ReadString(item, "Item"),
                    ItemCode = ReadString(item, "ItemCode")?.Trim(),
                    ItemDescription = ReadString(item, "ItemDescription"),
                    Price = ReadDecimal(item, "Price"),
                    Currency = ReadString(item, "Currency")?.Trim(),
                    Quantity = ReadDouble(item, "Quantity"),
                    StartDate = ReadDate(item, "StartDate"),
                    EndDate = ReadDate(item, "EndDate")
                });
            }

            _logger.LogInformation("📄 {Skip}-{End} arası {Count} satır okundu", skip, skip + count, count);

            if (count < PageSize) break;
            skip += PageSize;
        }

        return result;
    }

    /// <summary>
    /// Exact OData çağrısı yapar; rate limit ve token yenilemeyi yönetip "d.results" dizisini döndürür.
    /// </summary>
    private async Task<JsonElement?> GetResultsArrayAsync(
        HttpClient client, string url, CancellationToken cancellationToken)
    {
        int attempt = 0;
        const int maxAttempts = 4;

        while (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
        {
            attempt++;

            HttpResponseMessage resp;
            try
            {
                resp = await client.GetAsync(url, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Exact isteği başarısız (deneme {Attempt}/{Max})", attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                continue;
            }

            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("⏳ Rate limit, 30 saniye bekleniyor...");
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                continue;
            }

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                var refreshed = await _exactService.GetValidToken();
                if (refreshed == null) return null;
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", refreshed.access_token);
                continue;
            }

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("❌ Exact API hatası {Status}: {Body}", resp.StatusCode, Truncate(body, 300));
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("d", out var d))
                return null;

            // JsonDocument dispose edilince element geçersiz olur; klonlanmalı.
            if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("results", out var res))
                return res.Clone();
            if (d.ValueKind == JsonValueKind.Array)
                return d.Clone();

            return null;
        }

        return null;
    }

    private HttpClient CreateClient(string accessToken)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static string ReadString(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static decimal ReadDecimal(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDecimal() : 0m;

    private static double ReadDouble(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0d;

    /// <summary>Exact tarihleri "/Date(1699596233367)/" formatında gelir.</summary>
    private static DateTime? ReadDate(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String)
            return null;

        var raw = p.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (raw.StartsWith("/Date(", StringComparison.Ordinal))
        {
            var digits = new string(raw.Where(c => char.IsDigit(c) || c == '-').ToArray());
            if (long.TryParse(digits, out var ms))
                return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            return null;
        }

        return DateTime.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static string Truncate(string value, int max)
        => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max] + "...";
}
