using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopifyProductApp.Data;
using ShopifyProductApp.Models;
using ShopifyProductApp.Services;
using ShopifyProductApp.Services.Notifications;
using ShopifyProductApp.Services.PendingOperations;

namespace ShopifyProductApp.Controllers
{
    /// <summary>
    /// Bekleyen işlem havuzu (Exact token yokken aktarılamayan siparişler) ve Exact token
    /// kesinti durumu. nlmonitoringapp bu uçları kullanır; diğer monitoring controller'ları
    /// gibi anonim erişime açıktır.
    /// </summary>
    [AllowAnonymous]
    [ApiController]
    [Route("api/pending-operations")]
    public class PendingOperationsController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly PendingOperationService _service;
        private readonly PendingOperationProcessor _processor;
        private readonly TokenOutageMonitor _tokenMonitor;
        private readonly EmailNotificationService _email;
        private readonly ShopifyOrderCrud _orderCrud;
        private readonly ILogger<PendingOperationsController> _logger;

        public PendingOperationsController(
            ApplicationDbContext db,
            PendingOperationService service,
            PendingOperationProcessor processor,
            TokenOutageMonitor tokenMonitor,
            EmailNotificationService email,
            ShopifyOrderCrud orderCrud,
            ILogger<PendingOperationsController> logger)
        {
            _db = db;
            _service = service;
            _processor = processor;
            _tokenMonitor = tokenMonitor;
            _email = email;
            _orderCrud = orderCrud;
            _logger = logger;
        }

        /// <summary>Havuz kayıtları, sayfalı (en yeni önce).</summary>
        /// <param name="pageIndex">0 tabanlı sayfa numarası</param>
        /// <param name="pageSize">Sayfa başına kayıt (1-500)</param>
        /// <param name="status">Pending | Processing | Completed | Failed | Cancelled (virgülle birden fazla), boş = tümü</param>
        /// <param name="search">Shopify sipariş ID / no / adı veya Exact sipariş no</param>
        [HttpGet]
        public async Task<IActionResult> GetList(
            [FromQuery] int pageIndex = 0,
            [FromQuery] int pageSize = 50,
            [FromQuery] string status = null,
            [FromQuery] string search = null)
        {
            pageIndex = Math.Max(0, pageIndex);
            pageSize = Math.Clamp(pageSize, 1, 500);

            var query = _db.PendingOperations.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "All", StringComparison.OrdinalIgnoreCase))
            {
                var statuses = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                query = query.Where(p => statuses.Contains(p.Status));
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().TrimStart('#');
                query = query.Where(p =>
                    p.ShopifyOrderId.ToString().Contains(term) ||
                    (p.ShopifyOrderNumber != null && p.ShopifyOrderNumber.ToString().Contains(term)) ||
                    (p.ShopifyOrderName != null && p.ShopifyOrderName.Contains(term)) ||
                    (p.ExactOrderNumber != null && p.ExactOrderNumber.Contains(term)));
            }

            var totalCount = await query.CountAsync();

            var rows = await query
                .OrderByDescending(p => p.CreatedAt)
                .Skip(pageIndex * pageSize)
                .Take(pageSize)
                .Select(p => new
                {
                    p.Id,
                    p.OperationType,
                    p.ShopifyOrderId,
                    p.ShopifyOrderNumber,
                    p.ShopifyOrderName,
                    p.Status,
                    p.Reason,
                    p.Source,
                    p.AttemptCount,
                    p.CreatedAt,
                    p.LastAttemptAt,
                    p.CompletedAt,
                    p.LastError,
                    p.ExactOrderGuid,
                    p.ExactOrderNumber
                })
                .ToListAsync();

            var items = rows.Select(p => new
            {
                p.Id,
                p.OperationType,
                p.ShopifyOrderId,
                p.ShopifyOrderNumber,
                p.ShopifyOrderName,
                p.Status,
                p.Reason,
                p.Source,
                p.AttemptCount,
                CreatedAt = AsUtc(p.CreatedAt),
                LastAttemptAt = AsUtc(p.LastAttemptAt),
                CompletedAt = AsUtc(p.CompletedAt),
                p.LastError,
                p.ExactOrderGuid,
                p.ExactOrderNumber
            });

            return Ok(new
            {
                pageIndex,
                pageSize,
                totalCount,
                totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                items
            });
        }

        /// <summary>Durum adetleri + işlemci durumu + token durumu (panel üst kartları).</summary>
        [HttpGet("summary")]
        public async Task<IActionResult> GetSummary()
        {
            var counts = await _service.GetCountsAsync();
            return Ok(new
            {
                counts = new
                {
                    pending = counts[PendingOperationStatus.Pending],
                    processing = counts[PendingOperationStatus.Processing],
                    completed = counts[PendingOperationStatus.Completed],
                    failed = counts[PendingOperationStatus.Failed],
                    cancelled = counts[PendingOperationStatus.Cancelled],
                    total = counts.Values.Sum()
                },
                processor = _processor.Status,
                token = _tokenMonitor.GetStatus(),
                timestamp = DateTime.UtcNow
            });
        }

        /// <summary>Exact token kesinti / uyarı durumu.</summary>
        [HttpGet("token-status")]
        public IActionResult GetTokenStatus() => Ok(_tokenMonitor.GetStatus());

        /// <summary>Tek kayıt, ham webhook payload'ı ile birlikte.</summary>
        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById(int id)
        {
            var p = await _db.PendingOperations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (p == null) return NotFound(new { error = $"Kayıt bulunamadı: {id}" });

            return Ok(new
            {
                p.Id,
                p.OperationType,
                p.ShopifyOrderId,
                p.ShopifyOrderNumber,
                p.ShopifyOrderName,
                p.Status,
                p.Reason,
                p.Source,
                p.AttemptCount,
                CreatedAt = AsUtc(p.CreatedAt),
                LastAttemptAt = AsUtc(p.LastAttemptAt),
                CompletedAt = AsUtc(p.CompletedAt),
                p.LastError,
                p.ExactOrderGuid,
                p.ExactOrderNumber,
                p.Payload
            });
        }

        /// <summary>Failed / Cancelled kaydı tekrar kuyruğa alır ve işlemciyi tetikler.</summary>
        [HttpPost("{id:int}/retry")]
        public async Task<IActionResult> Retry(int id)
        {
            var (ok, message, op) = await _service.RetryAsync(id);
            if (!ok) return op == null ? NotFound(new { error = message }) : Conflict(new { error = message });

            _processor.TriggerNow();
            return Ok(new { success = true, message, id, status = op.Status });
        }

        /// <summary>Pending / Failed kaydı iptal eder (Exact'a gönderilmez).</summary>
        [HttpPost("{id:int}/cancel")]
        public async Task<IActionResult> Cancel(int id)
        {
            var (ok, message, op) = await _service.CancelAsync(id);
            if (!ok) return op == null ? NotFound(new { error = message }) : Conflict(new { error = message });

            return Ok(new { success = true, message, id, status = op.Status });
        }

        /// <summary>Havuzu periyodik döngüyü beklemeden hemen işler (202; zaten çalışıyorsa 409).</summary>
        [HttpPost("process-now")]
        public IActionResult ProcessNow()
        {
            if (_processor.Status.IsRunning)
                return Conflict(new { error = "Havuz işlemcisi zaten çalışıyor" });

            var triggered = _processor.TriggerNow();
            return Accepted(new
            {
                success = true,
                message = triggered ? "Havuz işleme tetiklendi" : "Tetikleme zaten bekliyor",
                triggered
            });
        }

        /// <summary>
        /// Bir Shopify siparişini elle havuza ekler (örn. token kesintisinde kaçan siparişler).
        /// Sipariş Shopify'dan doğrulanır; zaten aktarılmışsa veya havuzda bekliyorsa 409 döner.
        /// </summary>
        [HttpPost("enqueue/{shopifyOrderId:long}")]
        public async Task<IActionResult> Enqueue(long shopifyOrderId, [FromQuery] string reason = null)
        {
            var processed = await _db.ProcessedOrders.AsNoTracking()
                .FirstOrDefaultAsync(o => o.ShopifyOrderId == shopifyOrderId);
            if (processed != null)
            {
                return Conflict(new
                {
                    error = processed.ExactOrderGuid != null
                        ? $"Sipariş zaten Exact'a aktarılmış (Exact no: {processed.ExactOrderId})"
                        : "ProcessedOrders'da Exact numarası olmayan bir kayıt var (işlem devam ediyor olabilir)"
                });
            }

            if (await _service.IsQueuedAsync(shopifyOrderId))
                return Conflict(new { error = "Sipariş zaten havuzda bekliyor" });

            ShopifyOrder order = null;
            try
            {
                order = await _orderCrud.JustGetOrderByIdAsync(shopifyOrderId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ Shopify siparişi doğrulanamadı ({OrderId}): {Error}", shopifyOrderId, ex.Message);
            }
            if (order == null)
                return NotFound(new { error = $"Shopify'da sipariş bulunamadı: {shopifyOrderId}" });

            if (!string.IsNullOrEmpty(order.CancelledAt))
                return Conflict(new { error = $"Sipariş Shopify'da iptal edilmiş ({order.CancelledAt})" });

            var op = await _service.EnqueueOrderAsync(
                shopifyOrderId, order.OrderNumber, order.Name, null,
                string.IsNullOrWhiteSpace(reason) ? "Panelden elle eklendi" : reason.Trim(),
                PendingOperationSources.Manual);

            _processor.TriggerNow();

            return Ok(new
            {
                success = true,
                id = op.Id,
                status = op.Status,
                shopifyOrderId = op.ShopifyOrderId,
                shopifyOrderNumber = op.ShopifyOrderNumber,
                shopifyOrderName = op.ShopifyOrderName
            });
        }

        /// <summary>SMTP ayarlarını doğrulamak için test e-postası gönderir.</summary>
        [HttpPost("notifications/test-email")]
        public async Task<IActionResult> SendTestEmail()
        {
            var result = await _email.SendAsync(
                "[Exact Entegrasyon] Test e-postası",
                $"Bu bir test e-postasıdır.{Environment.NewLine}" +
                $"Sunucu: {Environment.MachineName}{Environment.NewLine}" +
                $"Zaman : {DateTime.Now:dd.MM.yyyy HH:mm:ss} (sunucu saati)");

            if (!result.Success)
                return StatusCode(500, new { success = false, error = result.Error });

            return Ok(new { success = true, message = $"Test e-postası gönderildi → {_email.Recipient}" });
        }

        private static DateTime? AsUtc(DateTime? value) =>
            value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;

        private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
