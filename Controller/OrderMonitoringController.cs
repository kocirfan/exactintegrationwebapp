using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopifyProductApp.Data;
using ShopifyProductApp.Services;

namespace ShopifyProductApp.Controllers
{
    /// <summary>
    /// İşlenmiş siparişleri (ProcessedOrders) dış monitoring uygulamalarına sunar.
    /// Liste DB'den gelir; detay istendiğinde Shopify ve Exact'tan sipariş bilgisi çekilir.
    /// Sadece okuma yapar, sipariş oluşturmaz/göndermez.
    /// </summary>
    [AllowAnonymous]
    [ApiController]
    [Route("api/order-monitoring")]
    public class OrderMonitoringController : ControllerBase
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly ShopifyOrderCrud _shopifyOrderCrud;
        private readonly ExactService _exactService;
        private readonly ILogger<OrderMonitoringController> _logger;

        public OrderMonitoringController(
            ApplicationDbContext dbContext,
            ShopifyOrderCrud shopifyOrderCrud,
            ExactService exactService,
            ILogger<OrderMonitoringController> logger)
        {
            _dbContext = dbContext;
            _shopifyOrderCrud = shopifyOrderCrud;
            _exactService = exactService;
            _logger = logger;
        }

        /// <summary>
        /// İşlenmiş siparişleri sayfalı listeler (en yeni önce).
        /// </summary>
        /// <param name="pageIndex">0 tabanlı sayfa numarası</param>
        /// <param name="pageSize">Sayfa başına kayıt (1-500)</param>
        /// <param name="search">Shopify sipariş ID, sipariş no veya Exact sipariş no araması</param>
        /// <param name="from">ProcessedAt alt sınırı</param>
        /// <param name="to">ProcessedAt üst sınırı</param>
        [HttpGet("orders")]
        public async Task<IActionResult> GetOrders(
            [FromQuery] int pageIndex = 0,
            [FromQuery] int pageSize = 50,
            [FromQuery] string search = null,
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null)
        {
            pageIndex = Math.Max(0, pageIndex);
            pageSize = Math.Clamp(pageSize, 1, 500);

            var query = _dbContext.ProcessedOrders.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(o =>
                    o.ShopifyOrderId.ToString().Contains(term) ||
                    (o.ShopifyOrderNumber != null && o.ShopifyOrderNumber.ToString().Contains(term)) ||
                    (o.ExactOrderId != null && o.ExactOrderId.Contains(term)));
            }

            if (from.HasValue)
                query = query.Where(o => o.ProcessedAt >= from.Value);

            if (to.HasValue)
                query = query.Where(o => o.ProcessedAt <= to.Value);

            var totalCount = await query.CountAsync();

            var items = await query
                .OrderByDescending(o => o.ProcessedAt)
                .Skip(pageIndex * pageSize)
                .Take(pageSize)
                .Select(o => new
                {
                    o.ShopifyOrderId,
                    o.ShopifyOrderNumber,
                    o.ProcessedAt,
                    ExactOrderNumber = o.ExactOrderId,
                    o.ExactOrderGuid
                })
                .ToListAsync();

            return Ok(new
            {
                pageIndex,
                pageSize,
                totalCount,
                totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                items
            });
        }

        /// <summary>
        /// Ana ekran sayacı: bugün işlenen sipariş adedi.
        /// </summary>
        [HttpGet("today-count")]
        public async Task<IActionResult> GetTodayOrderCount()
        {
            var dayStart = DateTime.Today;
            var dayEnd = dayStart.AddDays(1);

            var processedOrderCount = await _dbContext.ProcessedOrders
                .AsNoTracking()
                .CountAsync(o => o.ProcessedAt >= dayStart && o.ProcessedAt < dayEnd);

            return Ok(new
            {
                date = dayStart.ToString("yyyy-MM-dd"),
                processedOrderCount
            });
        }

        /// <summary>
        /// Sipariş detayı: DB kaydı + Shopify sipariş bilgisi + Exact sipariş detayı.
        /// Shopify/Exact'a sadece OKUMA yapılır; kaynaklardan biri erişilemezse
        /// diğerleri yine döner, hata mesajı yanıtın içinde belirtilir.
        /// </summary>
        [HttpGet("orders/{shopifyOrderId:long}")]
        public async Task<IActionResult> GetOrderDetail(long shopifyOrderId)
        {
            var dbRecord = await _dbContext.ProcessedOrders
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.ShopifyOrderId == shopifyOrderId);

            if (dbRecord == null)
            {
                return NotFound(new { error = $"Sipariş bulunamadı: {shopifyOrderId}" });
            }

            // Shopify sipariş bilgisi (sadece okuma - Exact'a gönderim YAPMAZ)
            object shopifyOrderSummary = null;
            string shopifyError = null;
            try
            {
                var shopifyOrder = await _shopifyOrderCrud.JustGetOrderByIdAsync(shopifyOrderId);
                if (shopifyOrder != null)
                {
                    shopifyOrderSummary = new
                    {
                        shopifyOrder.Id,
                        shopifyOrder.OrderNumber,
                        shopifyOrder.TotalPrice,
                        Customer = shopifyOrder.Customer == null ? null : new
                        {
                            shopifyOrder.Customer.Id,
                            shopifyOrder.Customer.FirstName,
                            shopifyOrder.Customer.LastName,
                            shopifyOrder.Customer.Email
                        },
                        LineItems = shopifyOrder.LineItems?.Select(li => new
                        {
                            li.Sku,
                            li.Title,
                            li.Quantity,
                            li.Price
                        }),
                        ShippingAddress = shopifyOrder.ShippingAddress == null ? null : new
                        {
                            shopifyOrder.ShippingAddress.FirstName,
                            shopifyOrder.ShippingAddress.LastName,
                            shopifyOrder.ShippingAddress.Address1,
                            shopifyOrder.ShippingAddress.Address2,
                            shopifyOrder.ShippingAddress.Zip,
                            shopifyOrder.ShippingAddress.City,
                            shopifyOrder.ShippingAddress.Country
                        },
                        NoteAttributes = shopifyOrder.NoteAttributes?.Select(na => new { na.Name, na.Value })
                    };
                }
                else
                {
                    shopifyError = "Shopify'da sipariş bulunamadı";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("❌ Shopify sipariş detayı alınamadı ({OrderId}): {Error}", shopifyOrderId, ex.Message);
                shopifyError = ex.Message;
            }

            // Exact sipariş detayı (GUID kayıtlıysa)
            object exactOrderDetail = null;
            string exactError = null;
            if (dbRecord.ExactOrderGuid.HasValue)
            {
                try
                {
                    var detail = await _exactService.GetOrderDetailByOrderId(dbRecord.ExactOrderGuid.Value);
                    if (detail != null)
                    {
                        exactOrderDetail = detail;
                    }
                    else
                    {
                        exactError = "Exact'ta sipariş detayı bulunamadı";
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError("❌ Exact sipariş detayı alınamadı ({Guid}): {Error}", dbRecord.ExactOrderGuid, ex.Message);
                    exactError = ex.Message;
                }
            }
            else
            {
                exactError = "DB kaydında Exact sipariş GUID'i yok";
            }

            return Ok(new
            {
                db = new
                {
                    dbRecord.ShopifyOrderId,
                    dbRecord.ShopifyOrderNumber,
                    dbRecord.ProcessedAt,
                    ExactOrderNumber = dbRecord.ExactOrderId,
                    dbRecord.ExactOrderGuid
                },
                shopify = shopifyOrderSummary,
                shopifyError,
                exact = exactOrderDetail,
                exactError,
                timestamp = DateTime.Now
            });
        }
    }
}
