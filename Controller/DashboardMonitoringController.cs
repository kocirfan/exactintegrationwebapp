using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopifyProductApp.Data;
using ShopifyProductApp.Services;

namespace ShopifyProductApp.Controllers
{
    /// <summary>
    /// Monitoring dashboard'u için tek çatı controller:
    /// stok / fiyat / müşteri senkronlarının durumu, manuel tetikleme ve sonuçları.
    /// </summary>
    [AllowAnonymous]
    [ApiController]
    [Route("api/dashboard")]
    public class DashboardMonitoringController : ControllerBase
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly ManualStockSyncRunner _stockSyncRunner;
        private readonly ManualPriceSyncRunner _priceSyncRunner;
        private readonly ManualCustomerSyncRunner _customerSyncRunner;
        private readonly ExactCustomerCrud _exactCustomerCrud;
        private readonly ShopifyCustomerCrud _shopifyCustomerCrud;
        private readonly CustomerSyncLogService _customerSyncLogService;
        private readonly ILogger<DashboardMonitoringController> _logger;

        public DashboardMonitoringController(
            ApplicationDbContext dbContext,
            ManualStockSyncRunner stockSyncRunner,
            ManualPriceSyncRunner priceSyncRunner,
            ManualCustomerSyncRunner customerSyncRunner,
            ExactCustomerCrud exactCustomerCrud,
            ShopifyCustomerCrud shopifyCustomerCrud,
            CustomerSyncLogService customerSyncLogService,
            ILogger<DashboardMonitoringController> logger)
        {
            _dbContext = dbContext;
            _stockSyncRunner = stockSyncRunner;
            _priceSyncRunner = priceSyncRunner;
            _customerSyncRunner = customerSyncRunner;
            _exactCustomerCrud = exactCustomerCrud;
            _shopifyCustomerCrud = shopifyCustomerCrud;
            _customerSyncLogService = customerSyncLogService;
            _logger = logger;
        }

        // ============ GENEL BAKIŞ ============

        /// <summary>
        /// Dashboard ana ekranı: üç senkronun da özet durumu tek çağrıda.
        /// </summary>
        [HttpGet("overview")]
        public async Task<IActionResult> GetOverview()
        {
            var dayStart = DateTime.Today;
            var dayEnd = dayStart.AddDays(1);

            // --- Stok ---
            var stockQuery = _dbContext.StockSyncLogs.AsNoTracking();
            var stockTotal = await stockQuery.CountAsync();
            var stockLastSyncAt = stockTotal > 0 ? await stockQuery.MaxAsync(l => (DateTime?)l.UpdatedAt) : null;
            var stockTodayUpdated = await stockQuery
                .Where(l => l.Success && l.UpdatedAt >= dayStart && l.UpdatedAt < dayEnd)
                .Select(l => l.ProductCode).Distinct().CountAsync();
            var stockTodayErrors = await stockQuery
                .CountAsync(l => !l.Success && l.UpdatedAt >= dayStart && l.UpdatedAt < dayEnd);

            // --- Fiyat ---
            var priceQuery = _dbContext.PriceSyncLogs.AsNoTracking();
            var priceTotal = await priceQuery.CountAsync();
            var priceLastSyncAt = priceTotal > 0 ? await priceQuery.MaxAsync(l => (DateTime?)l.UpdatedAt) : null;
            var priceTodayUpdated = await priceQuery
                .Where(l => l.Success && l.UpdatedAt >= dayStart && l.UpdatedAt < dayEnd)
                .Select(l => l.ProductCode).Distinct().CountAsync();
            var priceTodayErrors = await priceQuery
                .CountAsync(l => !l.Success && l.UpdatedAt >= dayStart && l.UpdatedAt < dayEnd);

            // --- Müşteri ---
            var customerQuery = _dbContext.CustomerSyncLogs.AsNoTracking();
            var customerTotal = await customerQuery.CountAsync();
            var customerLastSyncAt = customerTotal > 0 ? await customerQuery.MaxAsync(l => (DateTime?)l.UpdatedAt) : null;
            var customerTodayUpdated = await customerQuery
                .CountAsync(l => l.Success && l.UpdatedAt >= dayStart && l.UpdatedAt < dayEnd);
            var customerTodayErrors = await customerQuery
                .CountAsync(l => !l.Success && l.UpdatedAt >= dayStart && l.UpdatedAt < dayEnd);

            return Ok(new
            {
                stock = new
                {
                    schedule = "Her gece 01:30 otomatik",
                    lastSyncAt = stockLastSyncAt,
                    todayUpdatedProductCount = stockTodayUpdated,
                    todayErrorCount = stockTodayErrors,
                    totalTrackedProducts = stockTotal,
                    manualSync = _stockSyncRunner.GetStatus(),
                    triggerUrl = "/api/dashboard/stock/trigger"
                },
                price = new
                {
                    schedule = "Her gece 03:00 otomatik (tüm ürünler, PriceSyncLogs'a yazar)",
                    lastSyncAt = priceLastSyncAt,
                    todayUpdatedProductCount = priceTodayUpdated,
                    todayErrorCount = priceTodayErrors,
                    totalTrackedProducts = priceTotal,
                    manualSync = _priceSyncRunner.GetStatus(),
                    triggerUrl = "/api/dashboard/price/trigger"
                },
                customer = new
                {
                    schedule = "Her gece 04:30 otomatik (son 24 saatte değişenler) + Exact webhook + manuel tetikleme",
                    lastSyncAt = customerLastSyncAt,
                    todayUpdatedCustomerCount = customerTodayUpdated,
                    todayErrorCount = customerTodayErrors,
                    totalTrackedCustomers = customerTotal,
                    manualSync = _customerSyncRunner.GetStatus(),
                    triggerUrl = "/api/dashboard/customer/trigger"
                },
                timestamp = DateTime.Now
            });
        }

        // ============ STOK ============

        /// <summary>
        /// Stok senkronunu manuel tetikler (gece 01:30'daki işlemin aynısı, arka planda).
        /// </summary>
        [HttpPost("stock/trigger")]
        public IActionResult TriggerStockSync([FromQuery] int batchSize = 50, [FromQuery] int? maxItems = null)
        {
            batchSize = Math.Clamp(batchSize, 1, 500);

            if (!_stockSyncRunner.TryStart(batchSize, maxItems))
            {
                return Conflict(new { error = "Stok senkronu zaten çalışıyor", status = _stockSyncRunner.GetStatus() });
            }

            _logger.LogInformation("📊 Dashboard: stok senkronu tetiklendi");
            return Accepted(new { message = "Stok senkronu başlatıldı", statusUrl = "/api/dashboard/stock/status" });
        }

        /// <summary>Manuel stok senkronunun anlık durumu.</summary>
        [HttpGet("stock/status")]
        public IActionResult GetStockSyncStatus() => Ok(_stockSyncRunner.GetStatus());

        /// <summary>Stok senkron sonuçları (StockSyncLogs, sayfalı).</summary>
        [HttpGet("stock/results")]
        public async Task<IActionResult> GetStockResults(
            [FromQuery] int pageIndex = 0,
            [FromQuery] int pageSize = 50,
            [FromQuery] string search = null,
            [FromQuery] bool onlyErrors = false,
            [FromQuery] DateTime? date = null)
        {
            pageIndex = Math.Max(0, pageIndex);
            pageSize = Math.Clamp(pageSize, 1, 500);

            var query = _dbContext.StockSyncLogs.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(l => l.ProductCode.Contains(term) || l.ProductName.Contains(term));
            }

            if (onlyErrors)
                query = query.Where(l => !l.Success);

            if (date.HasValue)
            {
                var dayStart = date.Value.Date;
                var dayEnd = dayStart.AddDays(1);
                query = query.Where(l => l.UpdatedAt >= dayStart && l.UpdatedAt < dayEnd);
            }

            var totalCount = await query.CountAsync();
            var items = await query
                .OrderByDescending(l => l.UpdatedAt)
                .Skip(pageIndex * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Ok(new { pageIndex, pageSize, totalCount, totalPages = (int)Math.Ceiling(totalCount / (double)pageSize), items });
        }

        // ============ FİYAT ============

        /// <summary>
        /// Fiyat senkronunu manuel tetikler (tüm ürünler, batch batch, arka planda).
        /// </summary>
        [HttpPost("price/trigger")]
        public IActionResult TriggerPriceSync([FromQuery] int batchSize = 50, [FromQuery] int? maxItems = null)
        {
            batchSize = Math.Clamp(batchSize, 1, 500);

            if (!_priceSyncRunner.TryStart(batchSize, maxItems))
            {
                return Conflict(new { error = "Fiyat senkronu zaten çalışıyor", status = _priceSyncRunner.GetStatus() });
            }

            _logger.LogInformation("💶 Dashboard: fiyat senkronu tetiklendi");
            return Accepted(new { message = "Fiyat senkronu başlatıldı", statusUrl = "/api/dashboard/price/status" });
        }

        /// <summary>Manuel fiyat senkronunun anlık durumu.</summary>
        [HttpGet("price/status")]
        public IActionResult GetPriceSyncStatus() => Ok(_priceSyncRunner.GetStatus());

        /// <summary>
        /// Günlük fiyat özeti: verilen gün (varsayılan bugün) kaç ürünün fiyatı senkronlandı,
        /// kaçının fiyatı gerçekten değişti, kaçı hata aldı.
        /// </summary>
        /// <param name="date">Hangi gün (boş = bugün)</param>
        [HttpGet("price/daily-summary")]
        public async Task<IActionResult> GetPriceDailySummary([FromQuery] DateTime? date = null)
        {
            var dayStart = (date ?? DateTime.Today).Date;
            var dayEnd = dayStart.AddDays(1);

            var dayQuery = _dbContext.PriceSyncLogs.AsNoTracking()
                .Where(l => l.UpdatedAt >= dayStart && l.UpdatedAt < dayEnd);

            var syncedProductCount = await dayQuery
                .Where(l => l.Success)
                .Select(l => l.ProductCode).Distinct().CountAsync();

            var priceChangedProductCount = await dayQuery
                .Where(l => l.Success && l.OldPrice != null && l.OldPrice != l.NewPrice)
                .Select(l => l.ProductCode).Distinct().CountAsync();

            var errorCount = await dayQuery.CountAsync(l => !l.Success);
            var lastSyncAt = await dayQuery.MaxAsync(l => (DateTime?)l.UpdatedAt);

            // Fiyatı gerçekten değişen ürünler (ilk 20)
            var changedProducts = await dayQuery
                .Where(l => l.Success && l.OldPrice != null && l.OldPrice != l.NewPrice)
                .OrderByDescending(l => l.UpdatedAt)
                .Take(20)
                .Select(l => new
                {
                    l.ProductCode,
                    l.ProductName,
                    l.OldPrice,
                    l.NewPrice,
                    l.UpdatedAt
                })
                .ToListAsync();

            return Ok(new
            {
                date = dayStart.ToString("yyyy-MM-dd"),
                syncedProductCount,
                priceChangedProductCount,
                errorCount,
                lastSyncAt,
                changedProducts,
                timestamp = DateTime.Now
            });
        }

        /// <summary>Fiyat senkron sonuçları (PriceSyncLogs, sayfalı).</summary>
        [HttpGet("price/results")]
        public async Task<IActionResult> GetPriceResults(
            [FromQuery] int pageIndex = 0,
            [FromQuery] int pageSize = 50,
            [FromQuery] string search = null,
            [FromQuery] bool onlyErrors = false,
            [FromQuery] bool changedOnly = false,
            [FromQuery] DateTime? date = null)
        {
            pageIndex = Math.Max(0, pageIndex);
            pageSize = Math.Clamp(pageSize, 1, 500);

            var query = _dbContext.PriceSyncLogs.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(l => l.ProductCode.Contains(term) || l.ProductName.Contains(term));
            }

            if (onlyErrors)
                query = query.Where(l => !l.Success);

            if (changedOnly)
                query = query.Where(l => l.OldPrice == null || l.OldPrice != l.NewPrice);

            if (date.HasValue)
            {
                var dayStart = date.Value.Date;
                var dayEnd = dayStart.AddDays(1);
                query = query.Where(l => l.UpdatedAt >= dayStart && l.UpdatedAt < dayEnd);
            }

            var totalCount = await query.CountAsync();
            var items = await query
                .OrderByDescending(l => l.UpdatedAt)
                .Skip(pageIndex * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Ok(new { pageIndex, pageSize, totalCount, totalPages = (int)Math.Ceiling(totalCount / (double)pageSize), items });
        }

        // ============ MÜŞTERİ ============

        /// <summary>
        /// Müşteri senkronunu manuel tetikler: Exact'ta son N saatte değişen müşteriler
        /// Shopify'da güncellenir, sonuçlar CustomerSyncLogs'a yazılır (arka planda).
        /// </summary>
        /// <param name="hours">Son kaç saatte değişen müşteriler alınsın (varsayılan 24)</param>
        /// <param name="maxItems">Test için: sadece ilk N müşteriyi işle</param>
        [HttpPost("customer/trigger")]
        public IActionResult TriggerCustomerSync([FromQuery] int hours = 24, [FromQuery] int? maxItems = null)
        {
            hours = Math.Clamp(hours, 1, 720);

            if (!_customerSyncRunner.TryStart(hours, maxItems))
            {
                return Conflict(new { error = "Müşteri senkronu zaten çalışıyor", status = _customerSyncRunner.GetStatus() });
            }

            _logger.LogInformation("👥 Dashboard: müşteri senkronu tetiklendi (son {Hours} saat)", hours);
            return Accepted(new { message = "Müşteri senkronu başlatıldı", hours, statusUrl = "/api/dashboard/customer/status" });
        }

        /// <summary>Manuel müşteri senkronunun anlık durumu.</summary>
        [HttpGet("customer/status")]
        public IActionResult GetCustomerSyncStatus() => Ok(_customerSyncRunner.GetStatus());

        /// <summary>
        /// Tek müşteri için anlık senkron - Exact müşteri ID (GUID) ile.
        /// Email değişebildiği/boş olabildiği için ID en güvenilir yoldur.
        /// </summary>
        [HttpPost("customer/by-id/{customerId:guid}/sync")]
        public async Task<IActionResult> SyncSingleCustomerById(Guid customerId)
        {
            _logger.LogInformation("👤 Tek müşteri senkronu istendi (ID): {CustomerId}", customerId);

            var customer = await _exactCustomerCrud.GetCustomerByIdAsync(customerId);
            if (customer == null)
            {
                return NotFound(new { error = $"Exact'ta müşteri bulunamadı: {customerId}" });
            }

            return await SyncCustomerInternal(customer);
        }

        /// <summary>
        /// Tek müşteri için anlık senkron - e-posta ile.
        /// Exact'tan müşteri çekilir, Shopify'da güncellenir, sonuç CustomerSyncLogs'a yazılır.
        /// Ürünlerdeki sync-stock / sync-price ile aynı desendedir.
        /// </summary>
        /// <param name="email">Exact'taki müşteri e-posta adresi</param>
        [HttpPost("customer/{email}/sync")]
        public async Task<IActionResult> SyncSingleCustomer(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return BadRequest(new { error = "E-posta adresi gereklidir" });
            }

            email = email.Trim();
            _logger.LogInformation("👤 Tek müşteri senkronu istendi: {Email}", email);

            // 1. Exact'tan güncel müşteri bilgisi
            var customer = await _exactCustomerCrud.GetCustomerByEmailAsync(email);
            if (customer == null)
            {
                return NotFound(new { error = $"Exact'ta müşteri bulunamadı: {email}" });
            }

            return await SyncCustomerInternal(customer);
        }

        // Tek müşteri senkronunun ortak gövdesi (ID ve email endpoint'leri bunu kullanır)
        private async Task<IActionResult> SyncCustomerInternal(ExactOnline.Models.Account customer)
        {
            // 2. Shopify'da güncelle
            var logFilePath = Path.Combine("logs", $"customer-sync-{DateTime.Now:yyyyMMdd}.log");
            var logEntry = new Models.CustomerSyncLog
            {
                ExactCustomerId = customer.ID.ToString(),
                CustomerCode = customer.Code,
                Email = customer.Email,
                CustomerName = customer.Name,
                UpdatedAt = DateTime.Now
            };

            try
            {
                var (success, error) = await _shopifyCustomerCrud.UpdateCustomerDetailedAsync(customer, logFilePath, sendWelcomeEmail: false);
                logEntry.Success = success;
                logEntry.ErrorMessage = success ? null : error;
            }
            catch (Exception ex)
            {
                logEntry.Success = false;
                logEntry.ErrorMessage = ex.Message;
                _logger.LogError("❌ Tek müşteri senkronu hatası ({Email} / {CustomerId}): {Error}",
                    customer.Email, customer.ID, ex.Message);
            }

            // 3. DB'ye yaz (upsert)
            var saveResult = await _customerSyncLogService.SaveAsync(new List<Models.CustomerSyncLog> { logEntry });

            return Ok(new
            {
                email = customer.Email,
                exactCustomerId = logEntry.ExactCustomerId,
                customerCode = customer.Code,
                customerName = customer.Name,
                success = logEntry.Success,
                errorMessage = logEntry.ErrorMessage,
                db = new
                {
                    savedCount = saveResult.SavedCount,
                    failedCount = saveResult.FailedCount
                },
                updatedAt = logEntry.UpdatedAt,
                previousUpdatedAt = logEntry.PreviousUpdatedAt,
                timestamp = DateTime.Now
            });
        }

        /// <summary>Müşteri senkron sonuçları (CustomerSyncLogs, sayfalı).</summary>
        [HttpGet("customer/results")]
        public async Task<IActionResult> GetCustomerResults(
            [FromQuery] int pageIndex = 0,
            [FromQuery] int pageSize = 50,
            [FromQuery] string search = null,
            [FromQuery] bool onlyErrors = false)
        {
            pageIndex = Math.Max(0, pageIndex);
            pageSize = Math.Clamp(pageSize, 1, 500);

            var query = _dbContext.CustomerSyncLogs.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(l => l.Email.Contains(term) || l.CustomerName.Contains(term)
                    || l.CustomerCode.Contains(term) || l.ExactCustomerId.Contains(term));
            }

            if (onlyErrors)
                query = query.Where(l => !l.Success);

            var totalCount = await query.CountAsync();
            var items = await query
                .OrderByDescending(l => l.UpdatedAt)
                .Skip(pageIndex * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Ok(new { pageIndex, pageSize, totalCount, totalPages = (int)Math.Ceiling(totalCount / (double)pageSize), items });
        }
    }
}
