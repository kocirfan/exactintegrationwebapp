#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopifyProductApp.Data;
using ShopifyProductApp.Services.MegaDeals;

namespace ShopifyProductApp.Controllers
{
    /// <summary>
    /// Mega Deal (ör. Black Friday) kampanya ürünleri: normal fiyatların içe aktarımı ve
    /// Shopify compare-at yönetimi. Exact'a hiçbir şey yazılmaz.
    ///
    /// Akış:
    ///   1) POST /api/mega-deals/import        → Excel'deki SKU + normal fiyat + kampanya penceresi
    ///   2) POST /api/mega-deals/run?dryRun=true → ne yazılacağını gör (Shopify'a dokunmaz)
    ///   3) Servis (App:BackgroundServices:MegaDealCompareAt) kampanya boyunca compare-at'i yönetir
    ///   4) Acil durdurma: POST /api/mega-deals/{campaign}/end → kampanya şimdi biter, compare-at'ler temizlenir
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/mega-deals")]
    public class MegaDealController : ControllerBase
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly MegaDealCompareAtService _service;

        public MegaDealController(ApplicationDbContext dbContext, MegaDealCompareAtService service)
        {
            _dbContext = dbContext;
            _service = service;
        }

        public class ImportItem
        {
            public string Sku { get; set; } = "";
            public decimal RegularPrice { get; set; }
            public decimal? DealPrice { get; set; }
        }

        public class ImportRequest
        {
            public string Campaign { get; set; } = "";
            /// <summary>Zaman dilimi ile verilmeli, ör. "2026-11-25T00:00:00+01:00"</summary>
            public DateTimeOffset StartsAt { get; set; }
            /// <summary>Bu andan sonra compare-at temizlenir, ör. "2026-12-01T00:00:00+01:00"</summary>
            public DateTimeOffset EndsAt { get; set; }
            public List<ImportItem> Items { get; set; } = new();
        }

        /// <summary>
        /// Kampanya ürünlerini ekler/günceller (Campaign + Sku bazında). Shopify'a dokunmaz.
        /// Mevcut satırın fiyatı/penceresi güncellenir; durum Scheduled'a döner, sonraki çalışmada yeniden değerlendirilir.
        /// </summary>
        [HttpPost("import")]
        public async Task<IActionResult> Import([FromBody] ImportRequest request, CancellationToken ct)
        {
            var errors = new List<string>();
            var campaign = request.Campaign?.Trim() ?? "";
            if (campaign.Length == 0) errors.Add("Campaign boş olamaz");
            if (request.EndsAt <= request.StartsAt) errors.Add("EndsAt, StartsAt'ten sonra olmalı");
            if (request.Items.Count == 0) errors.Add("Items boş");

            var items = new List<ImportItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in request.Items)
            {
                var sku = (it.Sku ?? "").Trim(); // Trim nbsp ( ) dahil boşlukları siler
                if (sku.Length == 0) { errors.Add("Boş SKU"); continue; }
                if (sku.Any(char.IsWhiteSpace)) { errors.Add($"SKU içinde boşluk var (ürün adı olabilir): '{sku}'"); continue; }
                if (!seen.Add(sku)) { errors.Add($"Tekrarlanan SKU: {sku}"); continue; }
                if (it.RegularPrice <= 0) { errors.Add($"{sku}: RegularPrice > 0 olmalı"); continue; }
                if (it.DealPrice.HasValue && (it.DealPrice < 0 || it.DealPrice >= it.RegularPrice))
                { errors.Add($"{sku}: DealPrice 0 ile RegularPrice arasında olmalı"); continue; }
                items.Add(new ImportItem { Sku = sku, RegularPrice = Math.Round(it.RegularPrice, 2), DealPrice = it.DealPrice.HasValue ? Math.Round(it.DealPrice.Value, 2) : null });
            }

            if (errors.Count > 0)
                return BadRequest(new { message = "Import yapılmadı, hiçbir satır yazılmadı", errors });

            var startsUtc = request.StartsAt.UtcDateTime;
            var endsUtc = request.EndsAt.UtcDateTime;
            int inserted = 0, updated = 0;

            await using var tx = await _dbContext.Database.BeginTransactionAsync(ct);
            foreach (var it in items)
            {
                var n = await _dbContext.Database.ExecuteSqlRawAsync(@"
UPDATE [MegaDeals]
SET RegularPrice = {2}, DealPrice = {3}, StartsAtUtc = {4}, EndsAtUtc = {5},
    Status = {6}, LastError = NULL, UpdatedAt = SYSUTCDATETIME()
WHERE Campaign = {0} AND Sku = {1}",
                    new object[] { campaign, it.Sku, it.RegularPrice, (object?)it.DealPrice ?? DBNull.Value, startsUtc, endsUtc, MegaDealStatus.Scheduled }, ct);

                if (n > 0) { updated++; continue; }

                await _dbContext.Database.ExecuteSqlRawAsync(@"
INSERT INTO [MegaDeals] (Campaign, Sku, RegularPrice, DealPrice, StartsAtUtc, EndsAtUtc, Status, CreatedAt, UpdatedAt)
VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, SYSUTCDATETIME(), SYSUTCDATETIME())",
                    new object[] { campaign, it.Sku, it.RegularPrice, (object?)it.DealPrice ?? DBNull.Value, startsUtc, endsUtc, MegaDealStatus.Scheduled }, ct);
                inserted++;
            }
            await tx.CommitAsync(ct);

            return Ok(new { campaign, startsAtUtc = startsUtc, endsAtUtc = endsUtc, inserted, updated });
        }

        /// <summary>Kampanya satırlarını ve durum özetini listeler (sadece okuma).</summary>
        [AllowAnonymous]
        [HttpGet]
        public async Task<IActionResult> List([FromQuery] string? campaign = null, [FromQuery] string? status = null, CancellationToken ct = default)
        {
            var rows = await _dbContext.Database.SqlQueryRaw<MegaDealRow>(@"
SELECT * FROM [MegaDeals]
WHERE ({0} = '' OR Campaign = {0}) AND ({1} = '' OR Status = {1})
ORDER BY Campaign, Sku", campaign ?? "", status ?? "").ToListAsync(ct);

            var summary = rows.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count());
            return Ok(new { summary, count = rows.Count, rows });
        }

        /// <summary>
        /// Elle çalıştırma. Varsayılan dryRun=true: Shopify'a ve tabloya YAZMAZ, ne olacağını raporlar.
        /// includeScheduled=true ile başlamamış kampanyalar da kontrol edilir (SKU eşleşmesi, fiyat uyumu).
        /// </summary>
        [HttpPost("run")]
        public async Task<IActionResult> Run([FromQuery] bool dryRun = true, [FromQuery] bool includeScheduled = true,
            [FromQuery] string? campaign = null, CancellationToken ct = default)
        {
            var report = await _service.RunOnceAsync(dryRun, includeScheduled && dryRun, ct, campaign);
            return Ok(report);
        }

        /// <summary>
        /// Acil durdurma: kampanyanın bitişini şimdiye çeker. Shopify'a yazmaz; compare-at'ler servisin
        /// sonraki çalışmasında (veya hemen run?dryRun=false ile) temizlenir.
        /// </summary>
        [HttpPost("{campaign}/end")]
        public async Task<IActionResult> EndNow(string campaign, CancellationToken ct)
        {
            var n = await _dbContext.Database.ExecuteSqlRawAsync(@"
UPDATE [MegaDeals]
SET EndsAtUtc = SYSUTCDATETIME(), UpdatedAt = SYSUTCDATETIME()
WHERE Campaign = {0} AND Status <> {1} AND EndsAtUtc > SYSUTCDATETIME()",
                new object[] { campaign, MegaDealStatus.Ended }, ct);
            return Ok(new { campaign, rowsEnded = n });
        }
    }
}
