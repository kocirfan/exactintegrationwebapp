#nullable enable
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopifyProductApp.Data;

namespace ShopifyProductApp.Services.MegaDeals
{
    /// <summary>MegaDeals satırı (ham SQL ile okunur).</summary>
    public class MegaDealRow
    {
        public int Id { get; set; }
        public string Campaign { get; set; } = "";
        public string Sku { get; set; } = "";
        public decimal RegularPrice { get; set; }
        public decimal? DealPrice { get; set; }
        public DateTime StartsAtUtc { get; set; }
        public DateTime EndsAtUtc { get; set; }
        public string Status { get; set; } = "";
        public int? VariantCount { get; set; }
        public decimal? ShopifyPrice { get; set; }
        public decimal? ShopifyCompareAt { get; set; }
        public string? LastAction { get; set; }
        public string? LastError { get; set; }
        public DateTime? LastCheckedAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public static class MegaDealStatus
    {
        public const string Scheduled = "Scheduled";               // Kampanya henüz başlamadı
        public const string Active = "Active";                     // compare-at = normal fiyat yazılı
        public const string WaitingPriceDrop = "WaitingPriceDrop"; // Kampanya içinde ama Shopify fiyatı henüz düşmedi
        public const string NotFound = "NotFound";                 // Shopify'da bu SKU'lu variant yok
        public const string Conflict = "Conflict";                 // Variant'ta elle girilmiş farklı bir compare-at var — dokunulmadı
        public const string Ended = "Ended";                       // Kampanya bitti, bizim compare-at temizlendi
        public const string Error = "Error";
    }

    public class MegaDealVariantResult
    {
        public string VariantId { get; set; } = "";
        public string ProductId { get; set; } = "";
        public decimal Price { get; set; }
        public decimal? CompareAtBefore { get; set; }
        public decimal? CompareAtAfter { get; set; }
        public string Action { get; set; } = "";   // none | set | clear | conflict
    }

    public class MegaDealRowResult
    {
        public string Campaign { get; set; } = "";
        public string Sku { get; set; } = "";
        public decimal RegularPrice { get; set; }
        public decimal? DealPrice { get; set; }
        public string Status { get; set; } = "";
        public List<string> Warnings { get; set; } = new();
        public List<MegaDealVariantResult> Variants { get; set; } = new();
    }

    public class MegaDealRunReport
    {
        public bool DryRun { get; set; }
        public DateTime RunAtUtc { get; set; }
        public int RowsChecked { get; set; }
        public int CompareAtSet { get; set; }
        public int CompareAtCleared { get; set; }
        public int Unchanged { get; set; }
        public int NotFound { get; set; }
        public int Conflicts { get; set; }
        public int Errors { get; set; }
        public Dictionary<string, int> StatusCounts { get; set; } = new();
        public List<MegaDealRowResult> Rows { get; set; } = new();
    }

    /// <summary>
    /// Mega Deal ürünlerinde Shopify compareAtPrice'ı yönetir. Exact'a dokunmaz; yalnızca kendi DB'miz
    /// (MegaDeals tablosu) ve Shopify variant compareAtPrice alanı.
    ///
    /// Kural (her variant için):
    /// - Kampanya başlamadıysa: dokunulmaz.
    /// - Kampanya içinde ve Shopify fiyatı &lt; RegularPrice: compareAtPrice = RegularPrice.
    /// - Kampanya içinde ama fiyat henüz düşmediyse, veya kampanya bittiyse: compareAtPrice yalnızca
    ///   BİZİM yazdığımız değerse (= RegularPrice) temizlenir.
    /// - Variant'ta RegularPrice dışında, elle girilmiş bir compare-at varsa hiç dokunulmaz (Conflict).
    ///
    /// Fiyatın kendisi değişmez; fiyat mevcut Exact → Shopify senkronuyla (saatlik / gece) gelir.
    /// Fiyat senkronu yalnızca "price" alanını yazdığı için compare-at'i ezmez.
    ///
    /// Ayarlar (App:BackgroundServices:MegaDealCompareAt):
    ///   Enabled (varsayılan false), DryRun (varsayılan TRUE), IntervalMinutes (varsayılan 15)
    /// </summary>
    public class MegaDealCompareAtService : BackgroundService
    {
        private const string SECTION = "App:BackgroundServices:MegaDealCompareAt";
        private const string SHOPIFY_API_VERSION = "2025-01";
        private const int SKU_CHUNK = 25;
        private const decimal TOLERANCE = 0.005m;

        private readonly IServiceProvider _serviceProvider;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MegaDealCompareAtService> _logger;
        private readonly SemaphoreSlim _runLock = new(1, 1);

        public MegaDealCompareAtService(
            IServiceProvider serviceProvider,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<MegaDealCompareAtService> logger)
        {
            _serviceProvider = serviceProvider;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
        }

        private bool Enabled => _configuration.GetValue($"{SECTION}:Enabled", false);
        public bool ConfiguredDryRun => _configuration.GetValue($"{SECTION}:DryRun", true);
        private int IntervalMinutes => Math.Max(5, _configuration.GetValue($"{SECTION}:IntervalMinutes", 15));

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!Enabled)
            {
                _logger.LogInformation("🏷️ Mega Deal compare-at servisi kapalı ({Section}:Enabled=false)", SECTION);
                return;
            }

            _logger.LogInformation("🏷️ Mega Deal compare-at servisi başlatıldı - her {Interval} dk, DryRun={DryRun}", IntervalMinutes, ConfiguredDryRun);
            await Task.Delay(TimeSpan.FromSeconds(120), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var report = await RunOnceAsync(ConfiguredDryRun, includeScheduled: false, stoppingToken);
                    if (report.RowsChecked > 0)
                        _logger.LogInformation(
                            "🏷️ Mega Deal: {Rows} satır, set: {Set}, temizlenen: {Cleared}, değişmeyen: {Unchanged}, bulunamayan: {NotFound}, conflict: {Conflict}, hata: {Errors}{Dry}",
                            report.RowsChecked, report.CompareAtSet, report.CompareAtCleared, report.Unchanged,
                            report.NotFound, report.Conflicts, report.Errors, report.DryRun ? " (DRY RUN)" : "");
                }
                catch (TaskCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Mega Deal compare-at hatası: {Error}", ex.Message);
                }

                try { await Task.Delay(TimeSpan.FromMinutes(IntervalMinutes), stoppingToken); }
                catch (TaskCanceledException) { break; }
            }
        }

        /// <param name="dryRun">true: Shopify'a ve tabloya yazmaz, yalnızca ne olacağını raporlar.</param>
        /// <param name="includeScheduled">true: henüz başlamamış kampanyaları da kontrol eder (SKU eşleşmesi, fiyat uyumu).</param>
        /// <param name="ct">İptal belirteci.</param>
        /// <param name="campaign">Opsiyonel: yalnızca bu kampanya.</param>
        public async Task<MegaDealRunReport> RunOnceAsync(bool dryRun, bool includeScheduled, CancellationToken ct, string? campaign = null)
        {
            if (!await _runLock.WaitAsync(TimeSpan.FromMinutes(5), ct))
                throw new InvalidOperationException("Başka bir Mega Deal çalışması sürüyor");

            try
            {
                return await RunInternalAsync(dryRun, includeScheduled, campaign, ct);
            }
            finally
            {
                _runLock.Release();
            }
        }

        private async Task<MegaDealRunReport> RunInternalAsync(bool dryRun, bool includeScheduled, string? campaign, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var report = new MegaDealRunReport { DryRun = dryRun, RunAtUtc = now };

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var rows = await db.Database.SqlQueryRaw<MegaDealRow>(@"
SELECT * FROM [MegaDeals]
WHERE Status <> {0}
  AND ({1} = 1 OR StartsAtUtc <= {2})
  AND ({3} = '' OR Campaign = {3})
ORDER BY Campaign, Sku",
                MegaDealStatus.Ended, includeScheduled ? 1 : 0, now, campaign ?? "").ToListAsync(ct);

            if (rows.Count == 0) return report;

            // Shopify variant'larını SKU ile topla (parçalar halinde)
            var variantsBySku = new Dictionary<string, List<ShopifyVariantInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in rows.Select(r => r.Sku).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(SKU_CHUNK))
            {
                var found = await FetchVariantsBySkuAsync(chunk, ct);
                foreach (var v in found)
                {
                    if (!variantsBySku.TryGetValue(v.Sku, out var list))
                        variantsBySku[v.Sku] = list = new List<ShopifyVariantInfo>();
                    list.Add(v);
                }
            }

            foreach (var row in rows)
            {
                if (ct.IsCancellationRequested) break;
                report.RowsChecked++;

                var result = new MegaDealRowResult
                {
                    Campaign = row.Campaign,
                    Sku = row.Sku,
                    RegularPrice = row.RegularPrice,
                    DealPrice = row.DealPrice
                };
                report.Rows.Add(result);

                try
                {
                    await ProcessRowAsync(db, row, result, variantsBySku, now, dryRun, report, ct);
                }
                catch (Exception ex)
                {
                    report.Errors++;
                    result.Status = MegaDealStatus.Error;
                    result.Warnings.Add(ex.Message);
                    _logger.LogError("❌ Mega Deal {Sku}: {Error}", row.Sku, ex.Message);
                    if (!dryRun)
                        await SaveRowStateAsync(db, row.Id, MegaDealStatus.Error, null, null, null,
                            row.LastAction, Truncate(ex.Message, 1000), ct);
                }
            }

            foreach (var g in report.Rows.GroupBy(r => r.Status))
                report.StatusCounts[g.Key] = g.Count();

            return report;
        }

        private async Task ProcessRowAsync(
            ApplicationDbContext db, MegaDealRow row, MegaDealRowResult result,
            Dictionary<string, List<ShopifyVariantInfo>> variantsBySku, DateTime now, bool dryRun,
            MegaDealRunReport report, CancellationToken ct)
        {
            var started = now >= row.StartsAtUtc;
            var ended = now > row.EndsAtUtc;

            if (!variantsBySku.TryGetValue(row.Sku, out var variants) || variants.Count == 0)
            {
                report.NotFound++;
                result.Status = MegaDealStatus.NotFound;
                result.Warnings.Add("Shopify'da bu SKU ile variant bulunamadı");
                if (!dryRun)
                    await SaveRowStateAsync(db, row.Id, MegaDealStatus.NotFound, 0, null, null,
                        "Shopify'da variant bulunamadı", null, ct);
                return;
            }

            if (variants.Count > 1)
                result.Warnings.Add($"Bu SKU Shopify'da {variants.Count} variant'ta var; hepsine uygulanır");

            var actions = new List<string>();
            bool anyConflict = false, anyActive = false, anyWaiting = false;

            foreach (var v in variants)
            {
                var vr = new MegaDealVariantResult
                {
                    VariantId = v.VariantId,
                    ProductId = v.ProductId,
                    Price = v.Price,
                    CompareAtBefore = v.CompareAt,
                    CompareAtAfter = v.CompareAt,
                    Action = "none"
                };
                result.Variants.Add(vr);

                var compareAtIsOurs = v.CompareAt.HasValue && Math.Abs(v.CompareAt.Value - row.RegularPrice) < TOLERANCE;
                var compareAtIsForeign = v.CompareAt.HasValue && v.CompareAt.Value > 0 && !compareAtIsOurs;
                var priceLowered = v.Price < row.RegularPrice - TOLERANCE;

                if (!started)
                {
                    // Kampanya öncesi kontrol: Shopify fiyatı Excel'deki normal fiyatla aynı mı?
                    if (Math.Abs(v.Price - row.RegularPrice) >= TOLERANCE)
                        result.Warnings.Add($"Kampanya öncesi Shopify fiyatı ({v.Price:0.00}) normal fiyat ({row.RegularPrice:0.00}) ile aynı değil");
                    if (compareAtIsForeign)
                    {
                        anyConflict = true;
                        result.Warnings.Add($"Variant'ta zaten başka bir compare-at var ({v.CompareAt:0.00}); kampanyada dokunulmayacak");
                    }
                    continue;
                }

                bool wantCompareAt = !ended && priceLowered;

                if (wantCompareAt)
                {
                    if (row.DealPrice.HasValue && Math.Abs(v.Price - row.DealPrice.Value) >= TOLERANCE)
                        result.Warnings.Add($"Shopify fiyatı ({v.Price:0.00}) BF fiyatından ({row.DealPrice:0.00}) farklı");

                    if (compareAtIsOurs) { anyActive = true; continue; }
                    if (compareAtIsForeign)
                    {
                        anyConflict = true;
                        vr.Action = "conflict";
                        result.Warnings.Add($"Variant'ta elle girilmiş compare-at var ({v.CompareAt:0.00}); üzerine yazılmadı");
                        continue;
                    }

                    vr.Action = "set";
                    vr.CompareAtAfter = row.RegularPrice;
                    if (!dryRun)
                        await SetCompareAtAsync(v.ProductId, v.VariantId, row.RegularPrice, ct);
                    report.CompareAtSet++;
                    anyActive = true;
                    actions.Add($"{ShortId(v.VariantId)}: compare-at {Fmt(v.CompareAt)} → {row.RegularPrice:0.00} (fiyat {v.Price:0.00})");
                }
                else
                {
                    if (!ended) anyWaiting = true;
                    if (ended && priceLowered)
                        result.Warnings.Add($"Kampanya bitti ama Shopify fiyatı hâlâ düşük ({v.Price:0.00}); Exact'ta fiyat geri alınmalı");

                    if (compareAtIsOurs)
                    {
                        vr.Action = "clear";
                        vr.CompareAtAfter = null;
                        if (!dryRun)
                            await SetCompareAtAsync(v.ProductId, v.VariantId, null, ct);
                        report.CompareAtCleared++;
                        actions.Add($"{ShortId(v.VariantId)}: compare-at {v.CompareAt:0.00} → boş ({(ended ? "kampanya bitti" : "fiyat düşük değil")})");
                    }
                    else if (compareAtIsForeign)
                    {
                        anyConflict = true;
                    }
                }
            }

            if (actions.Count == 0) report.Unchanged++;
            if (anyConflict) report.Conflicts++;

            result.Status =
                !started ? MegaDealStatus.Scheduled :
                ended ? MegaDealStatus.Ended :
                anyConflict && !anyActive ? MegaDealStatus.Conflict :
                anyActive ? MegaDealStatus.Active :
                anyWaiting ? MegaDealStatus.WaitingPriceDrop :
                MegaDealStatus.WaitingPriceDrop;

            if (!dryRun)
            {
                var first = result.Variants.First();
                var actionText = actions.Count > 0
                    ? string.Join("; ", actions)
                    : row.LastAction; // değişiklik yoksa son yapılan işlem korunur
                await SaveRowStateAsync(db, row.Id, result.Status, variants.Count, first.Price, first.CompareAtAfter,
                    Truncate(actionText, 1000), result.Warnings.Count > 0 ? Truncate(string.Join(" | ", result.Warnings), 1000) : null, ct);
            }
        }

        // ---------------- DB ----------------

        private static Task SaveRowStateAsync(ApplicationDbContext db, int id, string status, int? variantCount,
            decimal? shopifyPrice, decimal? shopifyCompareAt, string? lastAction, string? lastError, CancellationToken ct) =>
            db.Database.ExecuteSqlRawAsync(@"
UPDATE [MegaDeals]
SET Status = {1}, VariantCount = {2}, ShopifyPrice = {3}, ShopifyCompareAt = {4},
    LastAction = {5}, LastError = {6}, LastCheckedAt = SYSUTCDATETIME(), UpdatedAt = SYSUTCDATETIME()
WHERE Id = {0}",
                new object?[]
                {
                    id, status,
                    (object?)variantCount ?? DBNull.Value,
                    (object?)shopifyPrice ?? DBNull.Value,
                    (object?)shopifyCompareAt ?? DBNull.Value,
                    (object?)lastAction ?? DBNull.Value,
                    (object?)lastError ?? DBNull.Value
                }!, ct);

        // ---------------- Shopify GraphQL ----------------

        private class ShopifyVariantInfo
        {
            public string VariantId { get; set; } = "";
            public string ProductId { get; set; } = "";
            public string Sku { get; set; } = "";
            public decimal Price { get; set; }
            public decimal? CompareAt { get; set; }
        }

        private async Task<List<ShopifyVariantInfo>> FetchVariantsBySkuAsync(IEnumerable<string> skus, CancellationToken ct)
        {
            var skuList = skus.ToList();
            var search = string.Join(" OR ", skuList.Select(s => $"sku:\"{s.Replace("\"", "\\\"")}\""));
            const string query = @"
query($q: String!) {
  productVariants(first: 100, query: $q) {
    nodes { id sku price compareAtPrice product { id } }
  }
}";
            var data = await GraphQLAsync(query, new { q = search }, ct);
            var wanted = new HashSet<string>(skuList, StringComparer.OrdinalIgnoreCase);
            var result = new List<ShopifyVariantInfo>();

            foreach (var node in data.GetProperty("productVariants").GetProperty("nodes").EnumerateArray())
            {
                var sku = node.TryGetProperty("sku", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()?.Trim() : null;
                // Shopify araması kısmi eşleşme döndürebilir; yalnızca birebir SKU'lar alınır
                if (string.IsNullOrEmpty(sku) || !wanted.Contains(sku)) continue;

                result.Add(new ShopifyVariantInfo
                {
                    VariantId = node.GetProperty("id").GetString()!,
                    ProductId = node.GetProperty("product").GetProperty("id").GetString()!,
                    Sku = sku,
                    Price = ParseMoney(node.GetProperty("price")) ?? 0,
                    CompareAt = node.TryGetProperty("compareAtPrice", out var c) ? ParseMoney(c) : null
                });
            }
            return result;
        }

        private async Task SetCompareAtAsync(string productId, string variantId, decimal? compareAt, CancellationToken ct)
        {
            const string mutation = @"
mutation($productId: ID!, $variants: [ProductVariantsBulkInput!]!) {
  productVariantsBulkUpdate(productId: $productId, variants: $variants) {
    productVariants { id compareAtPrice }
    userErrors { field message }
  }
}";
            var variables = new
            {
                productId,
                variants = new[]
                {
                    new
                    {
                        id = variantId,
                        compareAtPrice = compareAt?.ToString("F2", CultureInfo.InvariantCulture)
                    }
                }
            };

            var data = await GraphQLAsync(mutation, variables, ct);
            var errors = data.GetProperty("productVariantsBulkUpdate").GetProperty("userErrors");
            if (errors.GetArrayLength() > 0)
                throw new Exception("compare-at yazılamadı: " + Truncate(errors.GetRawText(), 500));

            await Task.Delay(300, ct); // Shopify rate limit
        }

        private async Task<JsonElement> GraphQLAsync(string query, object variables, CancellationToken ct)
        {
            var storeUrl = _configuration["Shopify:StoreUrl"] ?? throw new InvalidOperationException("Shopify:StoreUrl is missing");
            var token = _configuration["Shopify:AccessToken"] ?? throw new InvalidOperationException("Shopify:AccessToken is missing");

            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{storeUrl.TrimEnd('/')}/admin/api/{SHOPIFY_API_VERSION}/graphql.json")
            {
                // compareAtPrice = null'ın JSON'a "null" olarak gitmesi gerekir (temizleme için)
                Content = new StringContent(JsonSerializer.Serialize(new { query, variables }), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Shopify-Access-Token", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Shopify GraphQL HTTP {(int)response.StatusCode}: {Truncate(body, 500)}");

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errs))
                throw new Exception("Shopify GraphQL hatası: " + Truncate(errs.GetRawText(), 500));
            return doc.RootElement.GetProperty("data").Clone();
        }

        private static decimal? ParseMoney(JsonElement el) =>
            el.ValueKind switch
            {
                JsonValueKind.String when decimal.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                JsonValueKind.Number => el.GetDecimal(),
                _ => null
            };

        private static string ShortId(string gid) => gid[(gid.LastIndexOf('/') + 1)..];
        private static string Fmt(decimal? d) => d.HasValue ? d.Value.ToString("0.00", CultureInfo.InvariantCulture) : "boş";
        private static string? Truncate(string? s, int max) => s == null || s.Length <= max ? s : s[..max];
    }
}
