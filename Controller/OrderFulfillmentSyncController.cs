#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopifyProductApp.Data;
using ShopifyProductApp.Services.OrderFulfillmentSync;

namespace ShopifyProductApp.Controllers
{
    /// <summary>
    /// Exact → Shopify fulfillment senkronunun durumunu listeler (SADECE OKUMA).
    /// Atlanan siparişler (pickup, bol, iptal, NeedsReview) burada görülüp manuel kapatılır.
    /// </summary>
    [AllowAnonymous]
    [ApiController]
    [Route("api/order-fulfillment-sync")]
    public class OrderFulfillmentSyncController : ControllerBase
    {
        private readonly ApplicationDbContext _dbContext;

        public OrderFulfillmentSyncController(ApplicationDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        /// <param name="status">Opsiyonel: Pending, Fulfilled, AlreadyFulfilled, SkippedPickup, SkippedBol, SkippedCancelled, NeedsReview, Expired</param>
        /// <param name="top">En fazla kayıt (1-1000, varsayılan 200)</param>
        [HttpGet]
        public async Task<IActionResult> List([FromQuery] string? status = null, [FromQuery] int top = 200)
        {
            top = Math.Clamp(top, 1, 1000);
            var query = _dbContext.Database.SqlQueryRaw<OrderFulfillmentSyncRow>(
                "SELECT TOP (" + top + ") * FROM [OrderFulfillmentSyncs] WHERE ({0} = '' OR Status = {0}) ORDER BY CreatedAt DESC",
                status ?? "");
            var rows = await query.ToListAsync();

            var summary = await _dbContext.Database
                .SqlQueryRaw<StatusCount>("SELECT Status, COUNT(*) AS [Count] FROM [OrderFulfillmentSyncs] GROUP BY Status")
                .ToListAsync();

            return Ok(new { summary, rows });
        }

        public class StatusCount
        {
            public string Status { get; set; } = "";
            public int Count { get; set; }
        }
    }
}
