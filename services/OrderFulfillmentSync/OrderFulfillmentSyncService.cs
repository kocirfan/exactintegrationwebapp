#nullable enable
using System.Data;
using System.Net.Http.Headers;
using Microsoft.Data.SqlClient;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopifyProductApp.Data;

namespace ShopifyProductApp.Services.OrderFulfillmentSync
{
    /// <summary>OrderFulfillmentSyncs satırı (ham SQL ile okunur).</summary>
    public class OrderFulfillmentSyncRow
    {
        public long ShopifyOrderId { get; set; }
        public long? ShopifyOrderNumber { get; set; }
        public string? ShopifyOrderName { get; set; }
        public Guid ExactOrderGuid { get; set; }
        public string? ExactOrderNumber { get; set; }
        public string Status { get; set; } = "";
        public int? ExactStatus { get; set; }
        public string? Reason { get; set; }
        public string? LastError { get; set; }
        public int AttemptCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastCheckedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    public static class OrderFulfillmentSyncStatus
    {
        public const string Pending = "Pending";                     // Exact'ta henüz teslim edilmedi, izleniyor
        public const string Fulfilled = "Fulfilled";                 // Shopify'da fulfilled yapıldı
        public const string AlreadyFulfilled = "AlreadyFulfilled";   // Shopify'da zaten fulfilled idi
        public const string SkippedPickup = "SkippedPickup";         // Mağazadan teslim alma — manuel kapatılmalı
        public const string SkippedBol = "SkippedBol";               // bol.com (Market Sync) siparişi — dokunulmaz
        public const string SkippedCancelled = "SkippedCancelled";   // Exact'ta (45) veya Shopify'da iptal
        public const string NeedsReview = "NeedsReview";             // Fulfill edilebilir FO yok / hold vb.
        public const string Expired = "Expired";                     // MaxAgeDays içinde Exact'ta tamamlanmadı
    }

    /// <summary>
    /// Exact'ta TAMAMLANAN (SalesOrder.Status = 21) siparişleri Shopify'da fulfilled yapar.
    ///
    /// - Yalnızca servisin ilk açıldığı andan SONRA Exact'a aktarılan siparişler işlenir. Başlangıç anı
    ///   GeneralSettings'te ("OrderFulfillmentSync:StartFromUtc") kalıcı tutulur; eski siparişlere dokunulmaz.
    /// - Exact'a sadece OKUMA yapılır.
    /// - Shopify'da fulfillmentCreate notifyCustomer=false ile çağrılır (müşteriye e-posta gitmez).
    /// - Mağazadan teslim alma (PICK_UP) siparişleri ve bol.com siparişleri atlanır, tabloda raporlanır.
    /// - Exact'ta iptal (45) olanlar raporlanır, Shopify'da iptal edilmez.
    ///
    /// Ayarlar (App:BackgroundServices:OrderFulfillmentSync):
    ///   Enabled (varsayılan false), DryRun (varsayılan false), IntervalMinutes (60), MaxAgeDays (90),
    ///   StartFromUtc (opsiyonel; verilmezse ilk çalışma anı kaydedilir)
    /// </summary>
    public class OrderFulfillmentSyncService : BackgroundService
    {
        private const string SECTION = "App:BackgroundServices:OrderFulfillmentSync";
        private const string START_SETTING_KEY = "OrderFulfillmentSync:StartFromUtc";
        private const string SHOPIFY_API_VERSION = "2025-01";
        private const int EXACT_STATUS_COMPLETE = 21;
        private const int EXACT_STATUS_CANCELLED = 45;
        private const int BATCH_SIZE = 100;
        // Exact API limiti 60 istek/dk; sipariş başına bir Exact çağrısı yapılıyor
        private static readonly TimeSpan PER_ORDER_DELAY = TimeSpan.FromMilliseconds(1200);

        private readonly IServiceProvider _serviceProvider;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<OrderFulfillmentSyncService> _logger;

        public OrderFulfillmentSyncService(
            IServiceProvider serviceProvider,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<OrderFulfillmentSyncService> logger)
        {
            _serviceProvider = serviceProvider;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
        }

        private bool Enabled => _configuration.GetValue($"{SECTION}:Enabled", false);
        private bool DryRun => _configuration.GetValue($"{SECTION}:DryRun", false);
        private int IntervalMinutes => Math.Max(5, _configuration.GetValue($"{SECTION}:IntervalMinutes", 60));
        private int MaxAgeDays => Math.Max(1, _configuration.GetValue($"{SECTION}:MaxAgeDays", 90));

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!Enabled)
            {
                _logger.LogInformation("📦 Order Fulfillment Sync kapalı ({Section}:Enabled=false)", SECTION);
                return;
            }

            _logger.LogInformation("📦 Order Fulfillment Sync başlatıldı - her {Interval} dk, DryRun={DryRun}", IntervalMinutes, DryRun);
            await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (TaskCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Order Fulfillment Sync hatası: {Error}", ex.Message);
                }

                try { await Task.Delay(TimeSpan.FromMinutes(IntervalMinutes), stoppingToken); }
                catch (TaskCanceledException) { break; }
            }
        }

        public async Task RunOnceAsync(CancellationToken ct)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            var exact = scope.ServiceProvider.GetRequiredService<ExactService>();

            var startFromUtc = await GetStartFromUtcAsync(settings);

            // 1) Başlangıç anından sonra Exact'a aktarılmış yeni siparişleri takibe al
            var added = await db.Database.ExecuteSqlRawAsync(@"
INSERT INTO [OrderFulfillmentSyncs] (ShopifyOrderId, ShopifyOrderNumber, ExactOrderGuid, ExactOrderNumber, Status, AttemptCount, CreatedAt)
SELECT p.ShopifyOrderId, p.ShopifyOrderNumber, p.ExactOrderGuid, p.ExactOrderId, {0}, 0, SYSUTCDATETIME()
FROM [ProcessedOrders] p
WHERE p.ProcessedAt >= {1} AND p.ExactOrderGuid IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM [OrderFulfillmentSyncs] s WHERE s.ShopifyOrderId = p.ShopifyOrderId)",
                new object[] { OrderFulfillmentSyncStatus.Pending, startFromUtc }, ct);
            if (added > 0)
                _logger.LogInformation("📦 Fulfillment takibine {Count} yeni sipariş eklendi", added);

            // 2) Bekleyenleri işle (en uzun süredir kontrol edilmeyen önce)
            var pending = await db.Database.SqlQueryRaw<OrderFulfillmentSyncRow>(@"
SELECT TOP (" + BATCH_SIZE + @") * FROM [OrderFulfillmentSyncs]
WHERE Status = {0}
ORDER BY CASE WHEN LastCheckedAt IS NULL THEN 0 ELSE 1 END, LastCheckedAt, CreatedAt",
                OrderFulfillmentSyncStatus.Pending).ToListAsync(ct);

            if (pending.Count == 0) return;

            int fulfilled = 0, skipped = 0, waiting = 0, errors = 0;
            foreach (var row in pending)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var outcome = await ProcessOrderAsync(db, exact, row, ct);
                    switch (outcome)
                    {
                        case OrderFulfillmentSyncStatus.Fulfilled: fulfilled++; break;
                        case OrderFulfillmentSyncStatus.Pending: waiting++; break;
                        default: skipped++; break;
                    }
                }
                catch (Exception ex)
                {
                    errors++;
                    _logger.LogError("❌ Fulfillment sync hata - Shopify {OrderId}: {Error}", row.ShopifyOrderId, ex.Message);
                    await UpdateRowAsync(db, row.ShopifyOrderId, OrderFulfillmentSyncStatus.Pending, row.ExactStatus, row.Reason,
                        Truncate(ex.Message, 2000), null, row.ShopifyOrderName, ct);
                }
                await Task.Delay(PER_ORDER_DELAY, ct);
            }

            _logger.LogInformation("📦 Fulfillment sync tamamlandı - fulfilled: {F}, atlanan/rapor: {S}, bekleyen: {W}, hata: {E}{Dry}",
                fulfilled, skipped, waiting, errors, DryRun ? " (DRY RUN)" : "");
        }

        private async Task<string> ProcessOrderAsync(ApplicationDbContext db, ExactService exact, OrderFulfillmentSyncRow row, CancellationToken ct)
        {
            // Süre aşımı: Exact'ta MaxAgeDays içinde tamamlanmadıysa izlemeyi bırak
            if (row.CreatedAt < DateTime.UtcNow.AddDays(-MaxAgeDays))
            {
                await UpdateRowAsync(db, row.ShopifyOrderId, OrderFulfillmentSyncStatus.Expired, row.ExactStatus,
                    $"{MaxAgeDays} gün içinde Exact'ta tamamlanmadı", null, DateTime.UtcNow, row.ShopifyOrderName, ct);
                return OrderFulfillmentSyncStatus.Expired;
            }

            // Exact durumu (sadece okuma)
            var detail = await exact.GetOrderDetailByOrderId(row.ExactOrderGuid);
            if (detail == null)
                throw new Exception("Exact sipariş detayı alınamadı (token/erişim veya sipariş yok)");

            var exactStatus = detail.Status;
            if (exactStatus == EXACT_STATUS_CANCELLED)
            {
                await UpdateRowAsync(db, row.ShopifyOrderId, OrderFulfillmentSyncStatus.SkippedCancelled, exactStatus,
                    "Exact'ta iptal (45) — Shopify'da manuel kontrol edilmeli", null, DateTime.UtcNow, row.ShopifyOrderName, ct);
                return OrderFulfillmentSyncStatus.SkippedCancelled;
            }
            if (exactStatus != EXACT_STATUS_COMPLETE)
            {
                await UpdateRowAsync(db, row.ShopifyOrderId, OrderFulfillmentSyncStatus.Pending, exactStatus,
                    $"Exact durumu {exactStatus} — tamamlanması bekleniyor", null, null, row.ShopifyOrderName, ct);
                return OrderFulfillmentSyncStatus.Pending;
            }

            // Shopify sipariş + fulfillment order bilgisi
            var order = await GetShopifyOrderAsync(row.ShopifyOrderId, ct);
            if (order == null)
            {
                await UpdateRowAsync(db, row.ShopifyOrderId, OrderFulfillmentSyncStatus.NeedsReview, exactStatus,
                    "Shopify'da sipariş bulunamadı", null, DateTime.UtcNow, null, ct);
                return OrderFulfillmentSyncStatus.NeedsReview;
            }

            var name = order.Value.GetProperty("name").GetString();
            var sourceName = order.Value.TryGetProperty("sourceName", out var src) ? src.GetString() : null;
            var fulfillmentStatus = order.Value.GetProperty("displayFulfillmentStatus").GetString();
            var cancelled = order.Value.GetProperty("cancelledAt").ValueKind != JsonValueKind.Null;

            if (cancelled)
                return await Finish(OrderFulfillmentSyncStatus.SkippedCancelled, "Shopify'da iptal edilmiş");
            if (fulfillmentStatus == "FULFILLED")
                return await Finish(OrderFulfillmentSyncStatus.AlreadyFulfilled, "Shopify'da zaten fulfilled");
            if (string.Equals(sourceName, "bol", StringComparison.OrdinalIgnoreCase) ||
                (name?.StartsWith("BOLC", StringComparison.OrdinalIgnoreCase) ?? false))
                return await Finish(OrderFulfillmentSyncStatus.SkippedBol, "bol.com (Market Sync) siparişi — otomatik fulfill edilmez");

            var openFos = order.Value.GetProperty("fulfillmentOrders").GetProperty("nodes").EnumerateArray()
                .Where(fo => fo.GetProperty("status").GetString() is "OPEN" or "IN_PROGRESS")
                .ToList();

            // Mağazadan teslim alma FO'ları: API yolu müşteriye "Ready for pickup" e-postası gönderir →
            // bunlara dokunulmaz, raporlanır (müşteri Shopify admin'den "picked up" onaylar).
            // Aynı siparişteki pickup OLMAYAN FO'lar (örn. ORDERTOESLAG5 gibi ek kalemler) sessizce fulfill edilir.
            static bool IsPickup(JsonElement fo) =>
                fo.GetProperty("deliveryMethod").ValueKind == JsonValueKind.Object &&
                fo.GetProperty("deliveryMethod").GetProperty("methodType").GetString() == "PICK_UP";

            var pickupCount = openFos.Count(IsPickup);

            var fulfillable = openFos
                .Where(fo => !IsPickup(fo))
                .Where(fo => fo.GetProperty("supportedActions").EnumerateArray()
                    .Any(a => a.GetProperty("action").GetString() == "CREATE_FULFILLMENT"))
                .Select(fo => fo.GetProperty("id").GetString()!)
                .ToList();

            const string pickupNote = "Mağazadan teslim alma — Shopify admin'den 'picked up' olarak manuel kapatılmalı";

            if (fulfillable.Count == 0)
            {
                if (pickupCount > 0)
                    return await Finish(OrderFulfillmentSyncStatus.SkippedPickup, pickupNote);
                return await Finish(OrderFulfillmentSyncStatus.NeedsReview,
                    $"Fulfill edilebilir açık fulfillment order yok (durum: {fulfillmentStatus}, hold/scheduled olabilir)");
            }

            if (DryRun)
            {
                _logger.LogInformation("🧪 [DRY RUN] {Name} fulfill edilecekti ({Count} FO, pickup atlanacak: {Pickup})", name, fulfillable.Count, pickupCount);
                await UpdateRowAsync(db, row.ShopifyOrderId, OrderFulfillmentSyncStatus.Pending, exactStatus,
                    $"DRY RUN: {fulfillable.Count} FO fulfill edilecek" + (pickupCount > 0 ? $", {pickupCount} pickup FO atlanacak" : ""),
                    null, null, name, ct);
                return OrderFulfillmentSyncStatus.Pending;
            }

            var userErrors = await CreateFulfillmentAsync(fulfillable, ct);
            if (userErrors != null)
                throw new Exception("Shopify fulfillmentCreate hatası: " + userErrors);

            _logger.LogInformation("✅ {Name} Shopify'da {Count} FO fulfill edildi (Exact {ExactNo} tamamlandı)", name, fulfillable.Count, row.ExactOrderNumber);

            if (pickupCount > 0)
                return await Finish(OrderFulfillmentSyncStatus.SkippedPickup,
                    $"{fulfillable.Count} pickup olmayan FO fulfill edildi; {pickupCount} pickup FO açık. {pickupNote}");

            return await Finish(OrderFulfillmentSyncStatus.Fulfilled, $"{fulfillable.Count} FO fulfill edildi (notifyCustomer=false)");

            async Task<string> Finish(string status, string reason)
            {
                await UpdateRowAsync(db, row.ShopifyOrderId, status, exactStatus, reason, null, DateTime.UtcNow, name, ct);
                if (status != OrderFulfillmentSyncStatus.Fulfilled && status != OrderFulfillmentSyncStatus.AlreadyFulfilled)
                    _logger.LogWarning("⚠️ {Name}: {Status} - {Reason}", name, status, reason);
                return status;
            }
        }

        private static Task UpdateRowAsync(ApplicationDbContext db, long shopifyOrderId, string status, int? exactStatus,
            string? reason, string? lastError, DateTime? completedAt, string? orderName, CancellationToken ct) =>
            db.Database.ExecuteSqlRawAsync(@"
UPDATE [OrderFulfillmentSyncs]
SET Status = @status, ExactStatus = @exactStatus, Reason = @reason, LastError = @lastError, CompletedAt = @completedAt,
    ShopifyOrderName = COALESCE(@orderName, ShopifyOrderName),
    AttemptCount = AttemptCount + 1, LastCheckedAt = SYSUTCDATETIME()
WHERE ShopifyOrderId = @id",
                new[]
                {
                    new SqlParameter("@id", SqlDbType.BigInt) { Value = shopifyOrderId },
                    new SqlParameter("@status", SqlDbType.NVarChar, 30) { Value = status },
                    new SqlParameter("@exactStatus", SqlDbType.Int) { Value = (object?)exactStatus ?? DBNull.Value },
                    new SqlParameter("@reason", SqlDbType.NVarChar, 500) { Value = (object?)Truncate(reason, 500) ?? DBNull.Value },
                    new SqlParameter("@lastError", SqlDbType.NVarChar, 2000) { Value = (object?)Truncate(lastError, 2000) ?? DBNull.Value },
                    new SqlParameter("@completedAt", SqlDbType.DateTime2) { Value = (object?)completedAt ?? DBNull.Value },
                    new SqlParameter("@orderName", SqlDbType.NVarChar, 50) { Value = (object?)orderName ?? DBNull.Value },
                }, ct);

        private async Task<DateTime> GetStartFromUtcAsync(ISettingsService settings)
        {
            var configured = _configuration[$"{SECTION}:StartFromUtc"];
            if (DateTime.TryParse(configured, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var fromConfig))
                return fromConfig;

            var stored = await settings.GetSettingAsync(START_SETTING_KEY);
            if (DateTime.TryParse(stored, null, System.Globalization.DateTimeStyles.RoundtripKind, out var fromDb))
                return fromDb.ToUniversalTime();

            // İlk çalışma: şu andan itibaren aktarılan siparişler işlenir, eskiler hariç
            var now = DateTime.UtcNow;
            await settings.SetSettingAsync(START_SETTING_KEY, now.ToString("o"),
                "Bu andan sonra Exact'a aktarılan siparişler Exact'ta tamamlanınca Shopify'da fulfilled yapılır", "OrderFulfillmentSync");
            _logger.LogInformation("📌 Order Fulfillment Sync başlangıç anı kaydedildi: {Start:o} (öncesindeki siparişlere dokunulmaz)", now);
            return now;
        }

        // ---------------- Shopify GraphQL ----------------

        private async Task<JsonElement?> GetShopifyOrderAsync(long shopifyOrderId, CancellationToken ct)
        {
            const string query = @"query($id: ID!) {
  order(id: $id) {
    name sourceName cancelledAt displayFulfillmentStatus
    fulfillmentOrders(first: 20) {
      nodes { id status deliveryMethod { methodType } supportedActions { action } }
    }
  }
}";
            var data = await GraphQLAsync(query, new { id = $"gid://shopify/Order/{shopifyOrderId}" }, ct);
            var order = data.GetProperty("order");
            return order.ValueKind == JsonValueKind.Null ? null : order.Clone();
        }

        /// <summary>Başarılıysa null, aksi halde userErrors metni döner.</summary>
        private async Task<string?> CreateFulfillmentAsync(List<string> fulfillmentOrderIds, CancellationToken ct)
        {
            const string mutation = @"mutation($f: FulfillmentInput!) {
  fulfillmentCreate(fulfillment: $f) { fulfillment { id status } userErrors { field message } }
}";
            var variables = new
            {
                f = new
                {
                    notifyCustomer = false,
                    lineItemsByFulfillmentOrder = fulfillmentOrderIds.Select(id => new { fulfillmentOrderId = id }).ToArray()
                }
            };
            var data = await GraphQLAsync(mutation, variables, ct);
            var result = data.GetProperty("fulfillmentCreate");
            var errors = result.GetProperty("userErrors");
            if (errors.GetArrayLength() > 0)
                return string.Join("; ", errors.EnumerateArray().Select(e => e.GetProperty("message").GetString()));
            if (result.GetProperty("fulfillment").ValueKind == JsonValueKind.Null)
                return "fulfillment oluşturulmadı";
            return null;
        }

        private async Task<JsonElement> GraphQLAsync(string query, object variables, CancellationToken ct)
        {
            var storeUrl = _configuration["Shopify:StoreUrl"] ?? throw new InvalidOperationException("Shopify:StoreUrl is missing");
            var token = _configuration["Shopify:AccessToken"] ?? throw new InvalidOperationException("Shopify:AccessToken is missing");

            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{storeUrl.TrimEnd('/')}/admin/api/{SHOPIFY_API_VERSION}/graphql.json")
            {
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

        private static string? Truncate(string? s, int max) => s == null || s.Length <= max ? s : s[..max];
    }
}
