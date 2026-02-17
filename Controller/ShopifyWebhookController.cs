using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using ShopifyProductApp.Models;
using Microsoft.Extensions.Caching.Memory;
using ShopifyProductApp.Data;
using Microsoft.EntityFrameworkCore;
using System.Reflection;


namespace ShopifyProductApp.Controllers
{
    [ApiController]
    [Route("api/webhooks")]
    public class ShopifyWebhookController : ControllerBase
    {
        private readonly ExactService _exactService;
        private readonly ILogger<ShopifyWebhookController> _logger;
        private readonly IConfiguration _configuration;

        private readonly ExactAddressCrud _exactAddressCrud;
        private readonly IMemoryCache _cache;
        private readonly ApplicationDbContext _dbContext; // ← Ekle
        private readonly AddressMatchingService _addressMatchingService;
        private readonly string _failedOrdersLogPath;


        public ShopifyWebhookController(
            ExactService exactService,
            ILogger<ShopifyWebhookController> logger,
            ExactAddressCrud exactAddressCrud,
            IConfiguration configuration, IMemoryCache cache, ApplicationDbContext dbContext, AddressMatchingService addressMatchingService)
        {
            _exactService = exactService;
            _logger = logger;
            _configuration = configuration;
            _exactAddressCrud = exactAddressCrud;
            _cache = cache;
            _dbContext = dbContext;
            _addressMatchingService = addressMatchingService;
            
            // Failed orders log dosya yolu
            var logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }
            _failedOrdersLogPath = Path.Combine(logDirectory, "FailedOrders.log");
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

                    string lockKey = $"lock_order_{shopifyOrder.Id}";

                    // 🔐 ÖNCE DB'ye placeholder kaydet (Exact Order ID olmadan)
                    // Bu, duplicate gönderimi engelleyecek
                    var dbSaveSuccess = await ReserveOrderInDatabase(shopifyOrder.Id, shopifyOrder.OrderNumber);

                    if (!dbSaveSuccess)
                    {
                        _logger.LogWarning("⚠️ Sipariş DB'ye kaydedilemedi (zaten var) - İşlem durduruldu: {OrderId}", shopifyOrder.Id);
                        _cache.Remove(lockKey);
                        return Ok();
                    }

                    // ExactOnline'a gönder
                    var (success, exactOrderId, exactOrderNumber) = await ProcessShopifyOrderToExact(shopifyOrder);

                    if (success)
                    {
                        // ✅ Exact Order ID ile DB kaydını güncelle
                        await UpdateOrderWithExactDetails(shopifyOrder.Id, exactOrderId, exactOrderNumber);

                        // 🔓 Lock'u temizle
                        _cache.Remove(lockKey);

                        _logger.LogInformation("✅ Sipariş başarıyla işlendi! Exact OrderID: {ExactOrderId}, OrderNumber: {ExactOrderNumber}",
                            exactOrderId, exactOrderNumber);
                    }
                    else
                    {
                        _logger.LogError("❌ ExactOnline'a gönderme başarısız!");

                        // Hatalı siparişi dosyaya kaydet
                        await LogFailedOrder(shopifyOrder.Id, shopifyOrder.OrderNumber, "ExactOnline'a gönderme başarısız");

                        // Başarısız sipariş için DB kaydını sil (tekrar denenebilsin)
                        await RemoveOrderFromDatabase(shopifyOrder.Id);

                        // 🔓 Lock'u temizle
                        _cache.Remove(lockKey);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"⚠️ Hata: {ex.Message}");
                
                // Hatalı siparişi dosyaya kaydet (eğer shopifyOrder parse edilebildiyse)
                try
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var shopifyOrder = JsonSerializer.Deserialize<ShopifyOrder>(body, options);
                    if (shopifyOrder != null)
                    {
                        await LogFailedOrder(shopifyOrder.Id, shopifyOrder.OrderNumber, ex.Message);
                    }
                }
                catch { /* Ignore parse errors */ }
                
                return StatusCode(500, "Internal Server Error");
            }

            return Ok();
        }



        private async Task<(bool success, Guid? exactOrderId, string? exactOrderNumber)> ProcessShopifyOrderToExact(ShopifyOrder shopifyOrder)
        {
            try
            {
                _logger.LogInformation("Shopify siparişi ExactOnline'a gönderiliyor...");

                // 1. Müşteriyi  bul
                var customerId = await _exactService.CreateOrGetCustomerAsync(shopifyOrder.Customer);
                if (customerId == null)
                {
                    _logger.LogError("Müşteri oluşturulamadı veya bulunamadı");
                    return (false, null, null);
                }

                _logger.LogInformation($"ExactOnline Customer ID: {customerId}");

                // 1.5. Note attributes'tan teslimat bilgilerini al
                string deliveryType = null;
                DateTime? pickupDeliveryDate = null;

                if (shopifyOrder.NoteAttributes != null && shopifyOrder.NoteAttributes.Any())
                {
                    var deliveryTypeAttr = shopifyOrder.NoteAttributes
                        .FirstOrDefault(attr => attr.Name == "selected_delivery_type");
                    if (deliveryTypeAttr != null)
                    {
                        deliveryType = deliveryTypeAttr.Value;
                        _logger.LogInformation("📦 Teslimat tipi: {DeliveryType}", deliveryType);
                    }

                    var pickupDateAttr = shopifyOrder.NoteAttributes
                        .FirstOrDefault(attr => attr.Name == "pickup_delivery_date");
                    if (pickupDateAttr != null && !string.IsNullOrEmpty(pickupDateAttr.Value))
                    {
                        if (DateTime.TryParse(pickupDateAttr.Value, out var parsedDate))
                        {
                            pickupDeliveryDate = parsedDate;
                            _logger.LogInformation("📅 Pickup teslimat tarihi: {DeliveryDate}", pickupDeliveryDate.Value.ToString("dd.MM.yyyy"));
                        }
                    }
                }

                bool isPickup = deliveryType?.ToLower()?.Contains("pickup") == true;
                DateTime defaultDeliveryDate = pickupDeliveryDate ?? DateTime.Now.AddDays(7);

                // 2. Sipariş satırlarını hazırla
                var salesOrderLines = new List<ExactOrderLine>();

                // 🎯 Pickup indirimi için discount_application index'ini bul
                // NOT: isPickup kontrolü kaldırıldı - discount_applications'dan direkt tespit edilir
                int? pickupDiscountIndex = null;
                double totalPickupDiscount = 0; // Sepet bazında toplanacak pickup indirimi (tutar)
                double pickupDiscountPercentage = 0; // Pickup indirim yüzdesi (Exact'a gönderilecek)
                bool hasPickupDiscount = false; // Pickup indirimi var mı?

                if (shopifyOrder.DiscountApplications != null && shopifyOrder.DiscountApplications.Count > 0)
                {
                    _logger.LogInformation("📋 Discount Applications sayısı: {Count}", shopifyOrder.DiscountApplications.Count);

                    for (int i = 0; i < shopifyOrder.DiscountApplications.Count; i++)
                    {
                        var discountApp = shopifyOrder.DiscountApplications[i];
                        _logger.LogInformation("📋 DiscountApp[{Index}]: Title={Title}, Value={Value}, ValueType={ValueType}",
                            i, discountApp.Title ?? "NULL", discountApp.Value ?? "NULL", discountApp.ValueType ?? "NULL");

                        // Title içinde "pickup" geçiyorsa bu pickup indirimi
                        if (!string.IsNullOrEmpty(discountApp.Title) &&
                            discountApp.Title.ToLower().Contains("pickup"))
                        {
                            pickupDiscountIndex = i;
                            hasPickupDiscount = true;
                            _logger.LogInformation("🎯 PICKUP İNDİRİMİ BULUNDU: Index={Index}, Title={Title}, Value={Value} {ValueType}",
                                i, discountApp.Title, discountApp.Value, discountApp.ValueType);
                            break;
                        }
                    }
                }
                else
                {
                    _logger.LogInformation("📋 Discount Applications BOŞ veya NULL");
                }

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

                        //  TOPLAM İNDİRİM - Shopify'dan discount_allocations
                        //  ⚠️ Pickup indirimi varsa: pickup indirimi hariç tutulacak (sepet bazında uygulanacak)
                        double totalDiscount = 0;
                        if (lineItem.DiscountAllocations != null && lineItem.DiscountAllocations.Count > 0)
                        {
                            _logger.LogInformation("📋 Ürün: {Sku} - DiscountAllocations sayısı: {Count}",
                                lineItem.Sku, lineItem.DiscountAllocations.Count);

                            foreach (var allocation in lineItem.DiscountAllocations)
                            {
                                if (!string.IsNullOrEmpty(allocation.Amount))
                                {
                                    double allocationAmount = double.TryParse(allocation.Amount.Replace(".", ","), out var amount) ? amount : 0d;

                                    _logger.LogInformation("   📋 Allocation: Amount={Amount}, Index={Index}, PickupIndex={PickupIndex}, HasPickup={HasPickup}",
                                        allocationAmount, allocation.DiscountApplicationIndex,
                                        pickupDiscountIndex?.ToString() ?? "NULL", hasPickupDiscount);

                                    // Pickup indirimi ise sepet bazında topla, ürün indiriminden çıkar
                                    // NOT: isPickup yerine hasPickupDiscount kullanılıyor
                                    if (hasPickupDiscount && pickupDiscountIndex.HasValue &&
                                        allocation.DiscountApplicationIndex == pickupDiscountIndex.Value)
                                    {
                                        totalPickupDiscount += allocationAmount;
                                        _logger.LogInformation("   🚫 PICKUP İNDİRİMİ ÇIKARILDI: {Amount}€ (SKU: {Sku})",
                                            allocationAmount, lineItem.Sku);
                                    }
                                    else
                                    {
                                        // Normal ürün indirimi - ürün bazında uygula
                                        totalDiscount += allocationAmount;
                                        _logger.LogInformation("   ✅ ÜRÜN İNDİRİMİ EKLENDİ: {Amount}€ (SKU: {Sku})",
                                            allocationAmount, lineItem.Sku);
                                    }
                                }
                            }
                            _logger.LogInformation("📊 SONUÇ - Ürün: {Sku}, Ürün İndirimi: {TotalDiscount}€, Pickup İndirimi (sepet): {PickupDiscount}€",
                                lineItem.Sku, totalDiscount, totalPickupDiscount);
                        }
                        // Fallback: total_discount
                        else if (!string.IsNullOrEmpty(lineItem.TotalDiscount))
                        {
                            totalDiscount = double.TryParse(lineItem.TotalDiscount.Replace(".", ","), out var td) ? td : 0d;
                            _logger.LogInformation("⚠️ Total_discount'dan indirim alındı: {TotalDiscount}€", totalDiscount);
                        }

                        //  BİRİM BAŞINA İNDİRİM
                        double discountPerUnit = lineItem.Quantity > 0 ? totalDiscount / lineItem.Quantity : 0;

                        //  İNDİRİMLİ FİYAT (NetPrice)
                        double unitPriceWithDiscount = unitPrice - discountPerUnit;

                        //  İNDİRİM YÜZDESİ (Exact için) -
                        double discountPercentage = unitPrice > 0
                            ? ((unitPrice - unitPriceWithDiscount) / unitPrice) * 100
                            : 0;
                        var finalVATPercentage = vatPercentage == 0 ? 0.21 : vatPercentage;
                        salesOrderLines.Add(new ExactOrderLine
                        {
                            ID = Guid.NewGuid(),
                            Item = exactItem.ID.Value,
                            Description = lineItem.Title,
                            Quantity = lineItem.Quantity,
                            UnitPrice = unitPrice,                      // 299.00 (Orijinal)
                            NetPrice = unitPriceWithDiscount,           // İndirimli (pickup hariç)
                            Discount = discountPercentage,              // YÜZDE (pickup hariç)
                            VATPercentage = finalVATPercentage,
                            UnitCode = exactItem.Unit?.Trim() ?? "pc",
                            DeliveryDate = defaultDeliveryDate,
                            Division = int.TryParse(_configuration["ExactOnline:DivisionCode"], out var div) ? div : 0
                        });
                    }
                    else
                    {
                        _logger.LogWarning("Ürün bulunamadı: {Title} (SKU: {Sku})", lineItem.Title, lineItem.Sku);
                    }
                }

                // 🎁 Pickup indirimi varsa - yüzdeyi doğru hesapla
                // Pickup indirimi, ürün indirimleri uygulandıktan SONRA kalan tutara uygulanır
                // Örnek: 686.70€ (indirimli toplam) * %2 = 13.73€
                // NOT: isPickup yerine hasPickupDiscount kullanılıyor
                if (hasPickupDiscount && totalPickupDiscount > 0)
                {
                    // current_subtotal_price = pickup dahil son fiyat (672.97€)
                    // Pickup indirim öncesi = current_subtotal_price + totalPickupDiscount (686.70€)
                    double currentSubtotalForPickup = double.TryParse(shopifyOrder.current_subtotal_price?.Replace(".", ",") ?? "0", out var cstp) ? cstp : 0;
                    double subtotalBeforePickup = currentSubtotalForPickup + totalPickupDiscount;

                    if (subtotalBeforePickup > 0)
                    {
                        // Exact ondalık bekliyor: %2 = 0.02
                        pickupDiscountPercentage = totalPickupDiscount / subtotalBeforePickup;
                    }

                    _logger.LogInformation("🎁 PICKUP İNDİRİMİ HESAPLANDI: {TotalPickupDiscount}€ / {SubtotalBeforePickup}€ = {Percentage} (Exact için ondalık)",
                        totalPickupDiscount, subtotalBeforePickup, pickupDiscountPercentage);
                }

                if (!salesOrderLines.Any())
                {
                    _logger.LogError("Hiç sipariş satırı oluşturulamadı");
                    return (false, null, null);
                }

                // 📦 Gönderim ücreti ürününü ekle (SKU: 09CH9902) - SADECE pickup değilse
                if (!isPickup)
                {
                    try
                    {
                        const string shippingProductSku = "09CH9902";
                        var dynamicShippingPrice = shopifyOrder.ShippingLines.FirstOrDefault()?.Price;
                        if (!string.IsNullOrEmpty(dynamicShippingPrice))
                        {
                            _logger.LogInformation("🚚 Dinamik gönderim ücreti alınıyor: {Price}€", dynamicShippingPrice);
                        }
                        else
                        {
                            _logger.LogInformation("🚚 Dinamik gönderim ücreti bulunamadı, varsayılan ürün fiyatı kullanılacak.");
                        }

                        _logger.LogInformation("🚚 Gönderim ücreti ürünü ekleniyor (Teslimat tipi: {DeliveryType}): {Sku}",
                            deliveryType ?? "N/A", shippingProductSku);

                        var shippingItem = await _exactService.GetOrCreateItemAsync(shippingProductSku);
                        if (shippingItem != null && shippingItem.ID.HasValue)
                        {
                            double shippingVatPercentage = 0;
                            if (shippingItem.SalesVat.HasValue && shippingItem.SalesVat.Value > 0)
                            {
                                shippingVatPercentage = (double)(shippingItem.SalesVat.Value / 100);
                            }

                            var finalShippingVATPercentage = shippingVatPercentage == 0 ? 0.21 : shippingVatPercentage;

                            // Gönderim ücreti fiyatı: Exact'tan gelen fiyat yoksa veya 0 ise standart 63,50
                            const double defaultShippingPrice = 63.50;
                            double shippingPrice = shippingItem.StandardSalesPrice.HasValue && shippingItem.StandardSalesPrice.Value > 0
                                ? (double)shippingItem.StandardSalesPrice.Value
                                : defaultShippingPrice;
                                
                            // Dinamik gönderim ücreti varsa onu kulla
                            if (!string.IsNullOrEmpty(dynamicShippingPrice))
                            {
                                shippingPrice = double.TryParse(dynamicShippingPrice.Replace(".", ","), out var dsp) ? dsp : shippingPrice;
                            }else
                            {
                                _logger.LogInformation("🚚 Dinamik gönderim ücreti bulunamadı, varsayılan fiyat kullanılıyor: {Price}€", shippingPrice);
                            }
                            salesOrderLines.Add(new ExactOrderLine
                            {
                                ID = Guid.NewGuid(),
                                Item = shippingItem.ID.Value,
                                Description = shippingItem.Description ?? "Gönderim Ücreti",
                                Quantity = 1,
                                UnitPrice = shippingPrice,
                                NetPrice = shippingPrice,
                                Discount = 0,
                                VATPercentage = finalShippingVATPercentage,
                                UnitCode = shippingItem.Unit?.Trim() ?? "pc",
                                DeliveryDate = defaultDeliveryDate,
                                Division = int.TryParse(_configuration["ExactOnline:DivisionCode"], out var divShipping) ? divShipping : 0
                            });

                            _logger.LogInformation("✅ Gönderim ücreti ürünü eklendi: {Description}, Fiyat: {Price}€",
                                shippingItem.Description ?? "Gönderim Ücreti", shippingPrice);
                        }
                        else
                        {
                            _logger.LogWarning("⚠️ Gönderim ücreti ürünü bulunamadı: {Sku}", shippingProductSku);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("❌ Gönderim ücreti ürünü eklenirken hata: {Error}", ex.Message);
                        // Gönderim ücreti eklenemese bile sipariş devam etsin
                    }
                }
                else
                {
                    _logger.LogInformation("🏪 Pickup siparişi - Gönderim ücreti eklenmedi. Teslimat tarihi: {DeliveryDate}",
                        defaultDeliveryDate.ToString("dd.MM.yyyy"));
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


                //adress kontrol
                //fatura adresi
                bool addressesDiffer = IsBillingAddressDifferentFromShippingAddress(shopifyOrder);
                if (addressesDiffer)
                {
                   //billing yer 1


                    var delivery = shopifyOrder.ShippingAddress;
                    if (delivery != null)
                    {
                        var customerDeliveryAddress = _exactAddressCrud.GetCustomerDeliveryAddresses(customerId.Value.ToString());
                        //sipariş adresi
                        if (customerDeliveryAddress.Result.Count > 0)
                        {
                            bool addressFound = false;
                            foreach (var address in customerDeliveryAddress.Result)
                            {
                                _logger.LogInformation($"   🔍 Exact'teki fatura adresi: {address.AddressLine1}, {address.PostalCode} {address.City}");

                                if (address.FullAddress == delivery.Address1 + ", " + delivery.Zip + ", " + delivery.City)
                                {

                                    address.IsMain = true;
                                    await _exactAddressCrud.UpdateAddress(address.Id.ToString(), address);
                                    _logger.LogInformation("   ✅ Exact'teki fatura adresi Shopify fatura adresi ile eşleşiyor.");
                                    addressFound = true;
                                    break;
                                }

                            }
                            if (!addressFound)
                            {
                                // Hiçbir adres eşleşmediyse yeni adres oluştur
                                await CreateDeliveryAddress(delivery, customerId.Value.ToString());
                            }
                            else
                            {
                                _logger.LogInformation("   ✅ Müşterinin fatura adresi Exact'te bulundu ve kullanılacak.");
                            }


                            _logger.LogInformation("   ✅ Müşterinin fatura adresi Exact'te bulundu ve kullanılacak.");
                        }
                        else
                        {
                            // // Fatura adresi Exact'te yoksa oluştur
                            ExactAddress newDeliveryAddress = new ExactAddress
                            {
                                AccountId = Guid.Parse(customerId.Value.ToString()),
                                Type = 4, // 3 = Fatura Adresi
                                AddressLine1 = delivery.Address1 ?? "",
                                AddressLine2 = delivery.Address2 ?? "",
                                City = delivery.City ?? "",
                                PostalCode = delivery.Zip ?? "",
                                IsMain = true,
                                CountryCode = delivery.CountryCode ?? "",
                                AccountName = $"{delivery.FirstName} {delivery.LastName}" ?? "",
                                Division = int.TryParse(_configuration["ExactOnline:DivisionCode"], out var div) ? div : 0
                            };

                            var createdAddress = await _exactAddressCrud.CreateAddress(newDeliveryAddress);
                            if (createdAddress != null)
                            {
                                _logger.LogInformation("   ✅ Müşterinin fatura adresi Exact'te oluşturuldu ve kullanılacak.");
                            }
                            else
                            {
                                _logger.LogWarning("   ⚠️ Müşterinin fatura adresi oluşturulamadı.");
                            }

                        }
                    }

                }
                else
                {
                    
                    var delivery = shopifyOrder.ShippingAddress;
                    if (delivery != null)
                    {
                        var customerDeliveryAddress = _exactAddressCrud.GetCustomerDeliveryAddresses(customerId.Value.ToString());
                        //sipariş adresi
                        if (customerDeliveryAddress.Result.Count > 0)
                        {
                            bool addressFound = false;
                            foreach (var address in customerDeliveryAddress.Result)
                            {
                                _logger.LogInformation($"   🔍 Exact'teki fatura adresi: {address.AddressLine1}, {address.PostalCode} {address.City}");

                                if (address.FullAddress == delivery.Address1 + ", " + delivery.Zip + ", " + delivery.City)
                                {

                                    address.IsMain = true;
                                    await _exactAddressCrud.UpdateAddress(address.Id.ToString(), address);
                                    _logger.LogInformation("   ✅ Exact'teki fatura adresi Shopify fatura adresi ile eşleşiyor.");
                                    addressFound = true;
                                    break;
                                }

                            }
                            if (!addressFound)
                            {
                                // Hiçbir adres eşleşmediyse yeni adres oluştur
                                await CreateDeliveryAddress(delivery, customerId.Value.ToString());
                            }
                            else
                            {
                                _logger.LogInformation("   ✅ Müşterinin fatura adresi Exact'te bulundu ve kullanılacak.");
                            }


                            _logger.LogInformation("   ✅ Müşterinin fatura adresi Exact'te bulundu ve kullanılacak.");
                        }
                        else
                        {
                            // // Fatura adresi Exact'te yoksa oluştur
                            ExactAddress newDeliveryAddress = new ExactAddress
                            {
                                AccountId = Guid.Parse(customerId.Value.ToString()),
                                Type = 4, // 3 = Fatura Adresi
                                AddressLine1 = delivery.Address1 ?? "",
                                AddressLine2 = delivery.Address2 ?? "",
                                City = delivery.City ?? "",
                                PostalCode = delivery.Zip ?? "",
                                IsMain = true,
                                CountryCode = delivery.CountryCode ?? "",
                                AccountName = $"{delivery.FirstName} {delivery.LastName}" ?? "",
                                Division = int.TryParse(_configuration["ExactOnline:DivisionCode"], out var div) ? div : 0
                            };

                            var createdAddress = await _exactAddressCrud.CreateAddress(newDeliveryAddress);
                            if (createdAddress != null)
                            {
                                _logger.LogInformation("   ✅ Müşterinin fatura adresi Exact'te oluşturuldu ve kullanılacak.");
                            }
                            else
                            {
                                _logger.LogWarning("   ⚠️ Müşterinin fatura adresi oluşturulamadı.");
                            }

                        }
                    }

                }

                _logger.LogInformation($"📄 Sipariş açıklaması adresleri ile oluşturuluyor...");

                DateTime orderDate = DateTime.Now;
                //shiping method ekle
                //13 --> f4b84d79-3796-4fdc-a24e-08cd7628ce82
                // Mağazadan teslim  02 --> 19eb5f3e-7131-4d48-8a38-5b66eb44aa5b
                Guid shippingMethodGuid = Guid.Parse("19eb5f3e-7131-4d48-8a38-5b66eb44aa5b"); // Varsayılan: Mağazadan teslim
                if (shopifyOrder.ShippingLines != null && shopifyOrder.ShippingLines.Any())
                {
                    var shippingLine = shopifyOrder.ShippingLines.FirstOrDefault();
                    bool hasVerzendkosten =
                    shippingLine?.Title?.Contains("Verzendkosten") == true ||
                    shippingLine?.Title?.Contains("Gratis") == true;
                    bool hasShippingAddress = shopifyOrder.ShippingAddress != null;
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

                // Extract reference_number from note_attributes
                string referenceNumber = null;
                if (shopifyOrder.NoteAttributes != null && shopifyOrder.NoteAttributes.Any())
                {
                    var referenceAttribute = shopifyOrder.NoteAttributes
                        .FirstOrDefault(attr => attr.Name == "reference_number");

                    if (referenceAttribute != null && !string.IsNullOrWhiteSpace(referenceAttribute.Value))
                    {
                        referenceNumber = referenceAttribute.Value;
                        _logger.LogInformation($"   ✅ Reference number bulundu: {referenceNumber}");
                    }
                    else
                    {
                        _logger.LogInformation($"   ℹ️ Reference number bulunamadı");
                    }
                }

                // 🎁 Pickup indirimi yüzdesini logla
                if (isPickup && pickupDiscountPercentage > 0)
                {
                    _logger.LogInformation("🎁 Pickup indirimi Exact'a gönderilecek: {PickupDiscountPercentage}% (Tutar: {TotalPickupDiscount}€)",
                        pickupDiscountPercentage, totalPickupDiscount);
                }

                var exactOrder = new ExactOrder
                {
                    OrderedBy = customerId.Value,
                    DeliverTo = customerId.Value,
                    InvoiceTo = customerId.Value,
                    OrderDate = orderDate,
                    DeliveryDate = defaultDeliveryDate,  // Pickup date veya varsayılan
                    Description = $"Shopify Order #{shopifyOrder.OrderNumber}",
                    Currency = _configuration["ExactOnline:DefaultCurrency"] ?? "EUR",
                    Status = 12,
                    Division = 553201,
                    WarehouseID = warehouseGuid,
                    SalesOrderLines = salesOrderLines,
                    ShippingMethod = shippingMethodGuid,
                    YourRef = referenceNumber,

                    // Amount değerlerini Exact hesaplasın
                    AmountDC = currentSubtotalPrice - currentTotalTax,  // KDV hariç
                    AmountFC = currentSubtotalPrice - currentTotalTax,  // KDV hariç
                    AmountFCExclVat = currentSubtotalPrice - currentTotalTax,

                    // 🎁 Pickup indirimi - HER İKİ ALANI DA GÖNDER
                    // AmountDiscount = 8.14€ (KDV dahil: 6.73 * 1.21)
                    // AmountDiscountExclVat = 6.73€ (KDV hariç)
                    AmountDiscount = hasPickupDiscount ? (totalPickupDiscount * 1.21) : 0,
                    AmountDiscountExclVat = hasPickupDiscount ? totalPickupDiscount : 0,
                };

                _logger.LogInformation("📤 EXACT'A GÖNDERİLECEK: AmountDiscount={AmountDiscount}€ (KDV dahil), AmountDiscountExclVat={AmountDiscountExclVat}€ (KDV hariç), hasPickupDiscount={HasPickup}",
                    hasPickupDiscount ? (totalPickupDiscount * 1.21) : 0,
                    hasPickupDiscount ? totalPickupDiscount : 0,
                    hasPickupDiscount);

                _logger.LogInformation($"Sipariş hazırlandı - Satır: {salesOrderLines.Count}");

                // 4. ExactOnline'a gönder
                var (success, exactOrderId, exactOrderNumber) = await _exactService.CreateSalesOrderAsync(exactOrder);
                return (success, exactOrderId, exactOrderNumber);
            }
            catch (Exception ex)
            {
                _logger.LogError($"ExactOnline entegrasyonu hatası: {ex.Message}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");

                // Hatalı siparişi dosyaya kaydet
                try
                {
                    await LogFailedOrder(shopifyOrder.Id, shopifyOrder.OrderNumber, $"Integration Error: {ex.Message}");
                }
                catch { /* Ignore logging errors */ }

                return (false, null, null);
            }
        }

        //adress kontorl
        private bool IsBillingAddressDifferentFromShippingAddress(ShopifyOrder shopifyOrder)
        {
            // Eğer teslimat adresi yoksa varsayılan olarak aynı kabul et
            if (shopifyOrder.ShippingAddress == null)
            {
                _logger.LogInformation("ℹ️ Teslimat adresi bulunamadı, aynı kabul edildi");
                return false;
            }

            // Eğer fatura adresi yoksa varsayılan olarak aynı kabul et
            if (shopifyOrder.BillingAddress == null)
            {
                _logger.LogInformation("ℹ️ Fatura adresi bulunamadı, aynı kabul edildi");
                return false;
            }

            var billing = shopifyOrder.BillingAddress;
            var shipping = shopifyOrder.ShippingAddress;

            // Karşılaştırma (büyük/küçük harfe duyarsız, boşluk kontrollü)
            bool addressesDiffer =
                !NormalizeString(billing.Address1).Equals(NormalizeString(shipping.Address1)) ||
                !NormalizeString(billing.Address2).Equals(NormalizeString(shipping.Address2)) ||
                !NormalizeString(billing.City).Equals(NormalizeString(shipping.City)) ||
                !NormalizeString(billing.Zip).Equals(NormalizeString(shipping.Zip)) ||
                !NormalizeString(billing.Country).Equals(NormalizeString(shipping.Country)) ||
                !NormalizeString(billing.FirstName).Equals(NormalizeString(shipping.FirstName)) ||
                !NormalizeString(billing.LastName).Equals(NormalizeString(shipping.LastName));

            if (addressesDiffer)
            {
                _logger.LogWarning("⚠️ FATURA VE TESLİMAT ADRESLERİ FARKI:");
                _logger.LogWarning($"   Fatura: {billing.FirstName} {billing.LastName}");
                _logger.LogWarning($"           {billing.Address1} {billing.Address2}");
                _logger.LogWarning($"           {billing.Zip} {billing.City}, {billing.Country}");
                _logger.LogWarning($"   Teslimat: {shipping.FirstName} {shipping.LastName}");
                _logger.LogWarning($"             {shipping.Address1} {shipping.Address2}");
                _logger.LogWarning($"             {shipping.Zip} {shipping.City}, {shipping.Country}");
            }
            else
            {
                _logger.LogInformation("✅ Fatura ve teslimat adresleri aynı");
            }

            return addressesDiffer;
        }

        private async Task CreateNewBillingAddress(ShopifyAddress billing, String customerId)
        {
            ExactAddress newBillingAddress = new ExactAddress
            {
                AccountId = Guid.Parse(customerId),
                Type = 3,
                AddressLine1 = billing.Address1 ?? "",
                AddressLine2 = billing.Address2 ?? "",
                City = billing.City ?? "",
                PostalCode = billing.Zip ?? "",
                IsMain = true,
                CountryCode = billing.CountryCode ?? "",
                AccountName = $"{billing.FirstName} {billing.LastName}" ?? "",
                Division = int.TryParse(_configuration["ExactOnline:DivisionCode"], out var div) ? div : 0
            };

            var createdAddress = await _exactAddressCrud.CreateAddress(newBillingAddress);
            if (createdAddress != null)
            {
                _logger.LogInformation("   ✅ Müşterinin fatura adresi Exact'te oluşturuldu ve kullanılacak.");
            }
            else
            {
                _logger.LogWarning("   ⚠️ Müşterinin fatura adresi oluşturulamadı.");
            }
        }

        //delivery address
        private async Task CreateDeliveryAddress(ShopifyAddress delivery, String customerId)
        {
            ExactAddress newDeliveryAddress = new ExactAddress
            {
                AccountId = Guid.Parse(customerId),
                Type = 4,
                AddressLine1 = delivery.Address1 ?? "",
                AddressLine2 = delivery.Address2 ?? "",
                City = delivery.City ?? "",
                PostalCode = delivery.Zip ?? "",
                IsMain = true,
                CountryCode = delivery.CountryCode ?? "",
                AccountName = $"{delivery.FirstName} {delivery.LastName}" ?? "",
                Division = int.TryParse(_configuration["ExactOnline:DivisionCode"], out var div) ? div : 0
            };

            var createdAddress = await _exactAddressCrud.CreateAddress(newDeliveryAddress);
            if (createdAddress != null)
            {
                _logger.LogInformation("   ✅ Müşterinin fatura adresi Exact'te oluşturuldu ve kullanılacak.");
            }
            else
            {
                _logger.LogWarning("   ⚠️ Müşterinin fatura adresi oluşturulamadı.");
            }
        }

        /// <summary>
        /// String'i normalize et (boşlukları kaldır, küçük harfe çevir)
        /// </summary>
        private string NormalizeString(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            return input.Trim().ToLowerInvariant();
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

            // DB kontrolü (hem OrderId hem de OrderNumber ile)
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


        /// <summary>
        /// Siparişi DB'ye rezerve eder (Exact'a göndermeden ÖNCE)
        /// OrderId VE OrderNumber ile kontrol yapar
        /// </summary>
        private async Task<bool> ReserveOrderInDatabase(long orderId, long? orderNumber)
        {
            string cacheKey = $"shopify_order_{orderId}";

            // Ek güvenlik: OrderNumber ile de kontrol
            if (orderNumber.HasValue)
            {
                var existsByOrderNumber = await _dbContext.ProcessedOrders
                    .AnyAsync(x => x.ShopifyOrderNumber == orderNumber.Value);

                if (existsByOrderNumber)
                {
                    _logger.LogWarning("⚠️ Sipariş OrderNumber ile bulundu #{OrderNumber} - Duplicate gönderim ENGELLENDİ!", orderNumber.Value);
                    return false;
                }
            }

            try
            {
                var processedOrder = new ProcessedOrder
                {
                    ShopifyOrderId = orderId,
                    ShopifyOrderNumber = orderNumber,
                    ProcessedAt = DateTime.UtcNow,
                    ExactOrderGuid = null,  // Henüz Exact'a gönderilmedi
                    ExactOrderId = null
                };

                await _dbContext.ProcessedOrders.AddAsync(processedOrder);
                await _dbContext.SaveChangesAsync();

                // Cache'e de ekle
                var cacheOptions = new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2)
                };
                _cache.Set(cacheKey, true, cacheOptions);

                _logger.LogInformation("🔒 Sipariş DB'ye rezerve edildi: OrderId #{OrderId}, OrderNumber #{OrderNumber}",
                    orderId, orderNumber);
                return true;
            }
            catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate key") == true ||
                                               ex.InnerException?.Message.Contains("PRIMARY KEY") == true)
            {
                _logger.LogWarning("⚠️ Sipariş OrderId #{OrderId} zaten DB'de var - Duplicate gönderim ENGELLENDİ!", orderId);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError("❌ DB rezervasyon hatası: {Error}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// DB'deki sipariş kaydını Exact Order ID ile günceller
        /// </summary>
        private async Task UpdateOrderWithExactDetails(long orderId, Guid? exactOrderId, string exactOrderNumber)
        {
            try
            {
                var existingOrder = await _dbContext.ProcessedOrders.FindAsync(orderId);
                if (existingOrder != null)
                {
                    existingOrder.ExactOrderGuid = exactOrderId;
                    existingOrder.ExactOrderId = exactOrderNumber;

                    await _dbContext.SaveChangesAsync();

                    _logger.LogInformation("✅ Sipariş DB'de güncellendi: Shopify #{OrderId} -> Exact OrderID: {ExactOrderId}, OrderNumber: {ExactOrderNumber}",
                        orderId, exactOrderId, exactOrderNumber);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("❌ DB güncelleme hatası: {Error}", ex.Message);
            }
        }

        /// <summary>
        /// Başarısız sipariş için DB kaydını siler (tekrar denenebilsin)
        /// </summary>
        private async Task RemoveOrderFromDatabase(long orderId)
        {
            try
            {
                var existingOrder = await _dbContext.ProcessedOrders.FindAsync(orderId);
                if (existingOrder != null)
                {
                    _dbContext.ProcessedOrders.Remove(existingOrder);
                    await _dbContext.SaveChangesAsync();

                    _logger.LogInformation("🗑️ Başarısız sipariş DB'den silindi: #{OrderId}", orderId);
                }

                // Cache'den de temizle
                string cacheKey = $"shopify_order_{orderId}";
                _cache.Remove(cacheKey);
            }
            catch (Exception ex)
            {
                _logger.LogError("❌ DB silme hatası: {Error}", ex.Message);
            }
        }

        /// <summary>
        /// Exact Online'a gönderilemeyen siparişleri dosyaya kaydeder
        /// </summary>
        private async Task LogFailedOrder(long orderId, long? orderNumber, string errorMessage)
        {
            try
            {
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] " +
                              $"OrderID: {orderId} | " +
                              $"OrderNumber: {orderNumber?.ToString() ?? "N/A"} | " +
                              $"Error: {errorMessage}" +
                              Environment.NewLine;

                // Dosyaya asenkron yaz (thread-safe)
                await System.IO.File.AppendAllTextAsync(_failedOrdersLogPath, logEntry);
                
                _logger.LogWarning($"📝 Hatalı sipariş kaydedildi: {_failedOrdersLogPath}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Hatalı sipariş loglama hatası: {ex.Message}");
            }
        }
    }
}
