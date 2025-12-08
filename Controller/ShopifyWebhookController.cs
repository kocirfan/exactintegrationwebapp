using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using ShopifyProductApp.Models;
using Microsoft.Extensions.Caching.Memory;
using ShopifyProductApp.Data;
using Microsoft.EntityFrameworkCore;


namespace ShopifyProductApp.Controllers
{
    [ApiController]
    [Route("api/webhooks")]
    public class ShopifyWebhookController : ControllerBase
    {
        private readonly ExactService _exactService;
        private readonly ILogger<ShopifyWebhookController> _logger;
        private readonly IConfiguration _configuration;
        private readonly IMemoryCache _cache;
        private readonly ApplicationDbContext _dbContext; // ← Ekle


        public ShopifyWebhookController(
            ExactService exactService,
            ILogger<ShopifyWebhookController> logger,
            IConfiguration configuration, IMemoryCache cache, ApplicationDbContext dbContext)
        {
            _exactService = exactService;
            _logger = logger;
            _configuration = configuration;
            _cache = cache;
            _dbContext = dbContext;
        }

        [HttpPost("order-created")]
        public async Task<IActionResult> OrderCreated()
        {
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();

            // 🔍 Webhook bilgilerini logla
            var webhookId = Request.Headers["X-Shopify-Webhook-Id"].FirstOrDefault();
            _logger.LogInformation($"📦 Webhook ID: {webhookId}");
            _logger.LogInformation($"📦 Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");

            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var shopifyOrder = JsonSerializer.Deserialize<ShopifyOrder>(body, options);

                if (shopifyOrder != null)
                {
                    // ✅ Lock mekanizması ile kontrol
                    if (await IsOrderAlreadyProcessed(shopifyOrder.Id))
                    {
                        _logger.LogWarning($"⚠️ Sipariş atlandı (zaten işlendi veya işleniyor): {shopifyOrder.Id}");
                        return Ok();
                    }

                    _logger.LogInformation($"🆕 YENİ sipariş işleniyor: {shopifyOrder.Id}");

                    // ExactOnline'a gönder
                    var success = await ProcessShopifyOrderToExact(shopifyOrder);

                    if (success)
                    {
                        // ✅ Kalıcı kayıt
                        await MarkOrderAsProcessed(shopifyOrder.Id, shopifyOrder.OrderNumber);

                        // 🔓 Lock'u temizle
                        string lockKey = $"lock_order_{shopifyOrder.Id}";
                        _cache.Remove(lockKey);

                        _logger.LogInformation("✅ Sipariş başarıyla işlendi!");
                    }
                    else
                    {
                        _logger.LogError("❌ ExactOnline'a gönderme başarısız!");

                        // 🔓 Başarısız olursa lock'u temizle (tekrar denenebilsin)
                        string lockKey = $"lock_order_{shopifyOrder.Id}";
                        _cache.Remove(lockKey);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"⚠️ Hata: {ex.Message}");
                return StatusCode(500, "Internal Server Error");
            }

            return Ok();
        }



        private async Task<bool> ProcessShopifyOrderToExact(ShopifyOrder shopifyOrder)
        {
            try
            {
                _logger.LogInformation("Shopify siparişi ExactOnline'a gönderiliyor...");

                // 1. Müşteriyi  bul
                var customerId = await _exactService.CreateOrGetCustomerAsync(shopifyOrder.Customer);
                if (customerId == null)
                {
                    _logger.LogError("Müşteri oluşturulamadı veya bulunamadı");
                    return false;
                }

                _logger.LogInformation($"ExactOnline Customer ID: {customerId}");

                // 2. Sipariş satırlarını hazırla
                var salesOrderLines = new List<ExactOrderLine>();

                foreach (var lineItem in shopifyOrder.LineItems)
                {
                    var exactItem = await _exactService.GetOrCreateItemAsync(lineItem.Sku);

                    if (exactItem != null && exactItem.ID.HasValue)
                    {
                        double vatPercentage = 0;
                        if (exactItem.SalesVat.HasValue && exactItem.SalesVat.Value > 0)
                        {
                            vatPercentage = (double)(exactItem.SalesVat.Value / 100);
                        }

                        //  ORİJİNAL FİYAT (İndirim öncesi) - Shopify'dan "price"
                        double unitPrice = double.TryParse(lineItem.Price.Replace(".", ","), out var price) ? price : 0d;

                        //  TOPLAM İNDİRİM - Shopify'dan "total_discount"
                        double totalDiscount = 0;
                        if (lineItem.DiscountAllocations != null && lineItem.DiscountAllocations.Any())
                        {
                            foreach (var allocation in lineItem.DiscountAllocations)
                            {
                                if (!string.IsNullOrEmpty(allocation.Amount))
                                {
                                    totalDiscount += double.TryParse(allocation.Amount.Replace(".", ","), out var amount) ? amount : 0d;
                                }
                            }
                            _logger.LogInformation($"✅ Discount allocations'dan indirim alındı: {totalDiscount}€");
                        }

                        // Fallback: total_discount
                        else if (!string.IsNullOrEmpty(lineItem.TotalDiscount))
                        {
                            totalDiscount = double.TryParse(lineItem.TotalDiscount.Replace(".", ","), out var td) ? td : 0d;
                            _logger.LogInformation($"⚠️ Total_discount'dan indirim alındı: {totalDiscount}€");
                        }
                        // // double discountPerUnit = lineItem.Quantity > 0 ? totalDiscount / lineItem.Quantity : 0;
                        // if (!string.IsNullOrEmpty(lineItem.TotalDiscount))
                        // {
                        //     totalDiscount = double.TryParse(lineItem.TotalDiscount.Replace(".", ","), out var td) ? td : 0d;
                        // }

                        //  BİRİM BAŞINA İNDİRİM
                        double discountPerUnit = lineItem.Quantity > 0 ? totalDiscount / lineItem.Quantity : 0;

                        //  İNDİRİMLİ FİYAT (NetPrice)
                        double unitPriceWithDiscount = unitPrice - discountPerUnit;

                        //  İNDİRİM YÜZDESİ (Exact için) - 
                        double discountPercentage = unitPrice > 0
                            ? ((unitPrice - unitPriceWithDiscount) / unitPrice) * 100
                            : 0;

                        _logger.LogInformation($"📊 Ürün: {lineItem.Sku}");
                        _logger.LogInformation($"   UnitPrice (Orijinal): {unitPrice:F2}€");
                        _logger.LogInformation($"   NetPrice (İndirimli): {unitPriceWithDiscount:F2}€");
                        _logger.LogInformation($"   Discount: {discountPercentage:F2}%");
                        _logger.LogInformation($"   Quantity: {lineItem.Quantity}");
                        _logger.LogInformation($"   VATPercentage: {vatPercentage * 100}%");

                        salesOrderLines.Add(new ExactOrderLine
                        {
                            ID = Guid.NewGuid(),
                            Item = exactItem.ID.Value,
                            Description = lineItem.Title,
                            Quantity = lineItem.Quantity,
                            UnitPrice = unitPrice,                      // 299.00 (Orijinal)
                            NetPrice = unitPriceWithDiscount,           // 179.40 (İndirimli)
                            Discount = discountPercentage,              // 40.00 (YÜZDE!)
                            VATPercentage = vatPercentage,            //VATPercentage = vatPercentage,
                            UnitCode = exactItem.Unit?.Trim() ?? "pc",
                            DeliveryDate = DateTime.Now.AddDays(7),
                            Division = int.TryParse(_configuration["ExactOnline:DivisionCode"], out var div) ? div : 0
                        });
                    }
                    else
                    {
                        _logger.LogWarning($"Ürün bulunamadı: {lineItem.Title} (SKU: {lineItem.Sku})");
                    }
                }

                if (!salesOrderLines.Any())
                {
                    _logger.LogError("Hiç sipariş satırı oluşturulamadı");
                    return false;
                }

                // 3. Satış siparişini oluştur
                var totalPrice = decimal.TryParse(shopifyOrder.TotalPrice.Replace(".", ","), out var total) ? total : 0m;

                // Shopify'dan gelen değerler:
                // total_line_items_price = 299.00 (İndirim öncesi)
                // current_total_discounts = 119.60 (Toplam indirim)
                // current_subtotal_price = 179.40 (İndirimli, KDV dahil)

                double totalLineItemsPrice = double.TryParse(shopifyOrder.total_line_items_price?.Replace(".", ",") ?? "0", out var tlip) ? tlip : 0d;
                double currentTotalDiscounts = double.TryParse(shopifyOrder.current_total_discounts?.Replace(".", ",") ?? "0", out var ctd) ? ctd : 0d;
                double currentSubtotalPrice = double.TryParse(shopifyOrder.current_subtotal_price?.Replace(".", ",") ?? "0", out var csp) ? csp : 0d;
                double currentTotalTax = double.TryParse(shopifyOrder.current_total_tax?.Replace(".", ",") ?? "0", out var ctt) ? ctt : 0d;

                // Salesperson
                Guid? salespersonGuid = null;
                var salespersonConfig = _configuration["ExactOnline:DefaultSalesperson"];
                if (!string.IsNullOrEmpty(salespersonConfig) && Guid.TryParse(salespersonConfig, out var sp))
                {
                    salespersonGuid = sp;
                }

                // Warehouse
                Guid? warehouseGuid = null;
                var warehouseConfig = _configuration["ExactOnline:DefaultWarehouse"];
                if (!string.IsNullOrEmpty(warehouseConfig) && Guid.TryParse(warehouseConfig, out var wh))
                {
                    warehouseGuid = wh;
                }

                DateTime orderDate = DateTime.Now;

                _logger.LogInformation($" Sipariş tarihi: {orderDate:yyyy-MM-dd HH:mm:ss}");
                _logger.LogInformation($" Finansal Özet:");
                _logger.LogInformation($"   Toplam (İndirim öncesi): {totalLineItemsPrice}€");
                _logger.LogInformation($"   Toplam İndirim: {currentTotalDiscounts}€");
                _logger.LogInformation($"   Ara Toplam (KDV dahil): {currentSubtotalPrice}€");
                _logger.LogInformation($"   KDV Tutarı: {currentTotalTax}€");

                //shiping method ekle
                //13 --> f4b84d79-3796-4fdc-a24e-08cd7628ce82
                // Mağazadan teslim  02 --> 19eb5f3e-7131-4d48-8a38-5b66eb44aa5b
                Guid shippingMethodGuid = Guid.Parse("19eb5f3e-7131-4d48-8a38-5b66eb44aa5b"); // Varsayılan: Mağazadan teslim
                if (shopifyOrder.ShippingLines != null && shopifyOrder.ShippingLines.Any())
                {
                    var shippingLine = shopifyOrder.ShippingLines.FirstOrDefault();
                    bool hasVerzendkosten = shippingLine?.Title?.Contains("Verzendkosten") ?? false;
                    bool hasShippingAddress = shopifyOrder.ShippingAddress != null;

                    _logger.LogInformation($"🚚 Shipping Kontrol:");
                    _logger.LogInformation($"   Verzendkosten içeriyor: {hasVerzendkosten}");
                    _logger.LogInformation($"   Gönderim Adresi Belirtilmiş: {hasShippingAddress}");
                    _logger.LogInformation($"   Shipping Title: {shippingLine?.Title ?? "null"}");

                    if (hasVerzendkosten && hasShippingAddress)
                    {
                        shippingMethodGuid = Guid.Parse("f4b84d79-3796-4fdc-a24e-08cd7628ce82"); // Kargo
                        _logger.LogInformation($"   ✅ Kargo seçildi");
                    }
                    else
                    {
                        _logger.LogInformation($"   ℹ️ Mağazadan teslim seçildi (varsayılan)");
                    }
                }
                else
                {
                    _logger.LogInformation($"   ℹ️ Shipping lines bulunamadı, Mağazadan teslim seçildi (varsayılan)");
                }

                var exactOrder = new ExactOrder
                {
                    OrderedBy = customerId.Value,
                    DeliverTo = customerId.Value,
                    InvoiceTo = customerId.Value,
                    OrderDate = orderDate,
                    Description = $"Shopify Order #{shopifyOrder.OrderNumber}",
                    Currency = _configuration["ExactOnline:DefaultCurrency"] ?? "EUR",
                    Status = 12,
                    Division = 553201,
                    WarehouseID = warehouseGuid,
                    SalesOrderLines = salesOrderLines,
                    ShippingMethod = shippingMethodGuid,

                    // Amount değerlerini Exact hesaplasın
                    AmountDC = currentSubtotalPrice - currentTotalTax,  // KDV hariç
                    AmountFC = currentSubtotalPrice - currentTotalTax,  // KDV hariç
                    AmountFCExclVat = currentSubtotalPrice - currentTotalTax,
                    AmountDiscount = 0,  // Satır bazında gönderildiği için 0
                    AmountDiscountExclVat = 0,  // Satır bazında gönderildiği için 0
                };

                _logger.LogInformation($"Sipariş hazırlandı - Satır: {salesOrderLines.Count}");

                // 4. ExactOnline'a gönder
                var success = await _exactService.CreateSalesOrderAsync(exactOrder);
                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError($"ExactOnline entegrasyonu hatası: {ex.Message}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");
                return false;
            }
        }



        /// İki katmanlı kontrol: Önce cache (hızlı), sonra DB (kalıcı)       
        private async Task<bool> IsOrderAlreadyProcessed(long orderId)
        {
            string cacheKey = $"shopify_order_{orderId}";
            string lockKey = $"lock_order_{orderId}";

            // 🔒 Atomik kontrol + kayıt
            var lockAcquired = _cache.TryGetValue(lockKey, out _);

            if (lockAcquired)
            {
                _logger.LogInformation($"🔒 Sipariş şu anda işleniyor (lock var): #{orderId}");
                return true; // Başka bir thread işliyor
            }

            // Cache kontrolü
            if (_cache.TryGetValue(cacheKey, out _))
            {
                _logger.LogInformation($"📦 Cache HIT: Sipariş #{orderId} daha önce işlendi");
                return true;
            }

            // DB kontrolü
            var existsInDb = await _dbContext.ProcessedOrders
                .AnyAsync(x => x.ShopifyOrderId == orderId);

            if (existsInDb)
            {
                _logger.LogInformation($"💾 Database HIT: Sipariş #{orderId} daha önce işlendi");

                // Cache'e ekle
                var cacheOptions = new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2)
                };
                _cache.Set(cacheKey, true, cacheOptions);
                return true;
            }

            // 🔒 İşlem başlamadan ÖNCE lock koy (5 dakika boyunca)
            var lockOptions = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
            };
            _cache.Set(lockKey, true, lockOptions);

            _logger.LogInformation($"🔓 Lock alındı, sipariş işlenecek: #{orderId}");
            return false;
        }


        /// Siparişi hem cache'e hem DB'ye kaydet
        private async Task MarkOrderAsProcessed(long orderId, long? orderNumber)
        {
            string cacheKey = $"shopify_order_{orderId}";

            // 1 Cache'e ekle (hızlı erişim için)
            var cacheOptions = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2)
            };
            _cache.Set(cacheKey, true, cacheOptions);

            // 2️ DB'ye kaydet (kalıcı kayıt için)
            try
            {
                var processedOrder = new ProcessedOrder
                {
                    ShopifyOrderId = orderId,
                    ShopifyOrderNumber = orderNumber,
                    ProcessedAt = DateTime.UtcNow
                };

                await _dbContext.ProcessedOrders.AddAsync(processedOrder);
                await _dbContext.SaveChangesAsync();

                _logger.LogInformation($"💾 Sipariş DB'ye kaydedildi: #{orderId}");
            }
            catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate key") == true)
            {
                // Aynı anda iki istek geldiyse biri başarılı olur, diğeri bu hatayı alır - sorun değil
                _logger.LogWarning($"⚠️ Sipariş #{orderId} zaten DB'de kayıtlı (race condition)");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ DB kayıt hatası: {ex.Message}");
                // Cache'de zaten var, DB hatası kritik değil
            }
        }
    }
}
