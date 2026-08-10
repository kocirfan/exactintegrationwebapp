
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Text.Json;
using ExactOnline.Models;
using System.Text.Json.Serialization;

namespace ShopifyProductApp.Services;

public class ShopifyOrderCrud
{
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _shopifyStoreUrl;
    private readonly ShopifyGraphQLService _graphqlService;
    private readonly ILogger<ShopifyCustomerCrud> _logger;
    private readonly IServiceProvider _serviceProvider;

    // Rate limiting delay'leri (ms cinsinden)
    private const int API_REQUEST_DELAY_MS = 500;      // Her API isteği arasında 500ms
    private const int ADDRESS_OPERATION_DELAY_MS = 300; // Adres işlemleri arasında 300ms
    private const int RETRY_DELAY_MS = 2000;            // TooManyRequests (429) için 2 saniye

    private ExactService _exactService => _serviceProvider.GetRequiredService<ExactService>();
    private ExactAddressCrud _exactAddressCrud => _serviceProvider.GetRequiredService<ExactAddressCrud>();
    private IConfiguration _configuration => _serviceProvider.GetRequiredService<IConfiguration>();


    public ShopifyOrderCrud(string shopifyStoreUrl, string accessToken, ShopifyGraphQLService graphqlService, ILogger<ShopifyCustomerCrud> logger, IServiceProvider serviceProvider)
    {
        _shopifyStoreUrl = shopifyStoreUrl.TrimEnd('/');
        _client = new HttpClient
        {
            BaseAddress = new Uri($"{_shopifyStoreUrl}/admin/api/2025-01/")
        };
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _client.DefaultRequestHeaders.Add("X-Shopify-Access-Token", accessToken);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };
        _graphqlService = graphqlService;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }
    //just get order
    public async Task<ShopifyOrder?> JustGetOrderByIdAsync(long orderId)
    {
        var response = await _client.GetAsync($"orders/{orderId}.json");
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Shopify sipariş getirilemedi");
            return null;
        }

        var content = await response.Content.ReadAsStringAsync();

        // JsonDocument ile manuel parse
        using var doc = JsonDocument.Parse(content);
        if (doc.RootElement.TryGetProperty("order", out var orderElement))
        {
            var order = JsonSerializer.Deserialize<ShopifyOrder>(
                orderElement.GetRawText(),
                _jsonOptions
            );
            return order;
        }

        return null;
    }

    // manuel olarak shopify sipariş getir ve Exact'a gönder.
    // Webhook akışıyla aynı korumaları uygular: daha önce işlendiyse tekrar göndermez,
    // başarılı gönderimi ProcessedOrders tablosuna yazar (monitoring listesinde görünür).
    public async Task<ShopifyOrder?> GetOrderByIdAsync(long orderId)
    {
        var response = await _client.GetAsync($"orders/{orderId}.json");
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Shopify sipariş getirilemedi");
            return null;
        }

        var content = await response.Content.ReadAsStringAsync();

        // JsonDocument ile manuel parse
        using var doc = JsonDocument.Parse(content);
        if (doc.RootElement.TryGetProperty("order", out var orderElement))
        {
            var order = JsonSerializer.Deserialize<ShopifyOrder>(
                orderElement.GetRawText(),
                _jsonOptions
            );

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ShopifyProductApp.Data.ApplicationDbContext>();

            // Duplicate koruması: sipariş daha önce Exact'a gönderildiyse tekrar gönderme
            var alreadyProcessed = await db.ProcessedOrders
                .AnyAsync(o => o.ShopifyOrderId == order.Id ||
                               (order.OrderNumber != 0 && o.ShopifyOrderNumber == order.OrderNumber));

            if (alreadyProcessed)
            {
                _logger.LogWarning("⚠️ Sipariş zaten işlenmiş, tekrar gönderilmedi: {OrderId} (#{OrderNumber})",
                    order.Id, order.OrderNumber);
                return order;
            }

            var (success, exactOrderId, exactOrderNumber) = await ProcessShopifyOrderToExact(order);

            if (success)
            {
                try
                {
                    db.ProcessedOrders.Add(new ShopifyProductApp.Models.ProcessedOrder
                    {
                        ShopifyOrderId = order.Id,
                        ShopifyOrderNumber = order.OrderNumber,
                        ProcessedAt = DateTime.UtcNow,
                        ExactOrderId = exactOrderNumber,
                        ExactOrderGuid = exactOrderId
                    });
                    await db.SaveChangesAsync();

                    _logger.LogInformation("💾 Manuel gönderilen sipariş DB'ye kaydedildi: {OrderId} → Exact {ExactOrderNumber}",
                        order.Id, exactOrderNumber);
                }
                catch (Exception ex)
                {
                    // DB yazımı başarısız olsa da sipariş Exact'a gitti; akışı durdurma
                    _logger.LogError("❌ Sipariş DB'ye kaydedilemedi ({OrderId}): {Error}", order.Id, ex.Message);
                }
            }

            return order;
        }

        return null;
    }


    // exact'a sipariş gönder
    private async Task<(bool success, Guid? exactOrderId, string? exactOrderNumber)> ProcessShopifyOrderToExact(ShopifyOrder shopifyOrder)
    {
        try
        {
            _logger.LogInformation("Shopify siparişi ExactOnline'a gönderiliyor...");

            // 0. Kurumsal müşteri bilgisi (note_attributes: company_name, company_address, ...)
            var companyInfo = ShopifyCompanyInfo.FromNoteAttributes(shopifyOrder.NoteAttributes);
            if (companyInfo.HasCompany)
            {
                _logger.LogInformation("🏢 Kurumsal sipariş tespit edildi: {CompanyName}", companyInfo.Name);
            }

            // 1. Müşteriyi  bul
            var customerId = await _exactService.CreateOrGetCustomerAsync(shopifyOrder.Customer, companyInfo);
            if (customerId == null)
            {
                _logger.LogError("Müşteri oluşturulamadı veya bulunamadı");
                return (false, null, null);
            }

            _logger.LogInformation($"ExactOnline Customer ID: {customerId}");
            await Task.Delay(API_REQUEST_DELAY_MS); // Müşteri işlemi sonrası bekle

            // 1.2. Kurumsal ise ilgili kişiyi ve fatura adresini garanti et
            Guid? contactPersonId = null;
            if (companyInfo.HasCompany)
            {
                contactPersonId = await EnsureCompanyContactAndInvoiceAddress(shopifyOrder, companyInfo, customerId.Value);
            }

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
            int? pickupDiscountIndex = null;
            double totalPickupDiscount = 0;
            double pickupDiscountPercentage = 0;
            bool hasPickupDiscount = false;

            if (shopifyOrder.DiscountApplications != null && shopifyOrder.DiscountApplications.Count > 0)
            {
                _logger.LogInformation("📋 Discount Applications sayısı: {Count}", shopifyOrder.DiscountApplications.Count);

                for (int i = 0; i < shopifyOrder.DiscountApplications.Count; i++)
                {
                    var discountApp = shopifyOrder.DiscountApplications[i];
                    _logger.LogInformation("📋 DiscountApp[{Index}]: Title={Title}, Value={Value}, ValueType={ValueType}",
                        i, discountApp.Title ?? "NULL", discountApp.Value ?? "NULL", discountApp.ValueType ?? "NULL");

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
                // TEST: Ürün kodu sabit olarak OKK30ZHC7021 yapıldı

                //_logger.LogInformation("⚠️ TEST MODU: Orijinal SKU={OriginalSku} yerine {TestSku} kullanılıyor", lineItem.Sku);
                var exactItem = await _exactService.GetOrCreateItemAsync(lineItem.Sku);
                await Task.Delay(ADDRESS_OPERATION_DELAY_MS); // Her item işleminden sonra bekle

                if (exactItem != null && exactItem.ID.HasValue)
                {
                    double? vatPercentage = null;
                    if (exactItem.SalesVat.HasValue)
                    {
                        _logger.LogInformation("🔍 SalesVat raw değeri: {SalesVat}", exactItem.SalesVat.Value);
                        vatPercentage = (double)(exactItem.SalesVat.Value / 100);
                    }

                    //  ORİJİNAL FİYAT (İndirim öncesi) - Shopify'dan "price"
                    double unitPrice = double.TryParse(lineItem.Price, NumberStyles.Any, CultureInfo.InvariantCulture, out var price) ? price : 0d;

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
                                double allocationAmount = double.TryParse(allocation.Amount, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount) ? amount : 0d;

                                _logger.LogInformation("   📋 Allocation: Amount={Amount}, Index={Index}, PickupIndex={PickupIndex}, HasPickup={HasPickup}",
                                    allocationAmount, allocation.DiscountApplicationIndex,
                                    pickupDiscountIndex?.ToString() ?? "NULL", hasPickupDiscount);

                                // Pickup indirimi ise sepet bazında topla, ürün indiriminden çıkar
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
                        totalDiscount = double.TryParse(lineItem.TotalDiscount, NumberStyles.Any, CultureInfo.InvariantCulture, out var td) ? td : 0d;
                        _logger.LogInformation("⚠️ Total_discount'dan indirim alındı: {TotalDiscount}€", totalDiscount);
                    }

                    //  BİRİM BAŞINA İNDİRİM
                    double discountPerUnit = lineItem.Quantity > 0 ? totalDiscount / lineItem.Quantity : 0;

                    //  İNDİRİMLİ FİYAT (NetPrice)
                    double unitPriceWithDiscount = unitPrice - discountPerUnit;

                    //  İNDİRİM YÜZDESİ (Exact için) -
                    double discountPercentage = unitPrice > 0
                        ? Math.Round(((unitPrice - unitPriceWithDiscount) / unitPrice) * 100, 2)
                        : 0;

                    // ORDERTOESLAG için Shopify fiyatı aynen kullanılır, indirim uygulanmaz
                    bool isOrderToeslag = string.Equals(lineItem.Sku, "ORDERTOESLAG", StringComparison.OrdinalIgnoreCase);
                    if (isOrderToeslag)
                    {
                        unitPriceWithDiscount = unitPrice;
                        discountPercentage = 0;
                        _logger.LogInformation("💰 ORDERTOESLAG ürünü: Shopify fiyatı kullanılıyor ({Price}€), indirim sıfırlandı", unitPrice);
                    }
                    // SalesVat null ise Exact Online'a SalesVatCode'u bırakan API kodu belirleyecek (0.21 fallback)
                    // SalesVat 0.0 ise gerçek %0 KDV'li ürün, olduğu gibi gönder
                    var finalVATPercentage = vatPercentage ?? 0.21;
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
                        VATCode = exactItem.SalesVatCode?.Trim(),
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
            if (hasPickupDiscount && totalPickupDiscount > 0)
            {
                double currentSubtotalForPickup = double.TryParse(shopifyOrder.current_subtotal_price ?? "0", NumberStyles.Any, CultureInfo.InvariantCulture, out var cstp) ? cstp : 0;
                double subtotalBeforePickup = currentSubtotalForPickup + totalPickupDiscount;

                if (subtotalBeforePickup > 0)
                {
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
                    await Task.Delay(ADDRESS_OPERATION_DELAY_MS);
                    if (shippingItem != null && shippingItem.ID.HasValue)
                    {
                        double? shippingVatPercentage = null;
                        if (shippingItem.SalesVat.HasValue)
                        {
                            shippingVatPercentage = (double)(shippingItem.SalesVat.Value / 100);
                        }

                        var finalShippingVATPercentage = shippingVatPercentage ?? 0.21;

                        const double defaultShippingPrice = 63.50;
                        double shippingPrice = shippingItem.StandardSalesPrice.HasValue && shippingItem.StandardSalesPrice.Value > 0
                            ? (double)shippingItem.StandardSalesPrice.Value
                            : defaultShippingPrice;

                        // Dinamik gönderim ücreti varsa onu kullan
                        if (!string.IsNullOrEmpty(dynamicShippingPrice))
                        {
                            shippingPrice = double.TryParse(dynamicShippingPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var dsp) ? dsp : shippingPrice;
                        }
                        else
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
                            VATCode = shippingItem.SalesVatCode?.Trim(),
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
            var totalPrice = decimal.TryParse(shopifyOrder.TotalPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var total) ? total : 0m;

            // Shopify'dan gelen değerler:
            // total_line_items_price = 299.00 (İndirim öncesi)
            // current_total_discounts = 119.60 (Toplam indirim)
            // current_subtotal_price = 179.40 (İndirimli, KDV dahil)

            double totalLineItemsPrice = double.TryParse(shopifyOrder.total_line_items_price ?? "0", NumberStyles.Any, CultureInfo.InvariantCulture, out var tlip) ? tlip : 0d;
            double currentTotalDiscounts = double.TryParse(shopifyOrder.current_total_discounts ?? "0", NumberStyles.Any, CultureInfo.InvariantCulture, out var ctd) ? ctd : 0d;
            double currentSubtotalPrice = double.TryParse(shopifyOrder.current_subtotal_price ?? "0", NumberStyles.Any, CultureInfo.InvariantCulture, out var csp) ? csp : 0d;
            double currentTotalTax = double.TryParse(shopifyOrder.current_total_tax ?? "0", NumberStyles.Any, CultureInfo.InvariantCulture, out var ctt) ? ctt : 0d;

            // Salesperson
            //"DefaultSalesperson": "5acbab56-ef3d-47d4-873e-f494dcfe1426"
            Guid? salespersonGuid = null;
            var salespersonConfig = _configuration["ExactOnline:DefaultSalesperson"];
            var accountManager1 = await _exactService.GetAccountManagerByCustomerIdAsync(customerId.Value);
            if (accountManager1 == null)
            {
                if (!string.IsNullOrEmpty(salespersonConfig) && Guid.TryParse(salespersonConfig, out var sp))
                {
                    salespersonGuid = sp;
                }
                else
                {
                    salespersonGuid = new Guid("5acbab56-ef3d-47d4-873e-f494dcfe1426");
                }
            }
            else
            {
                salespersonGuid = accountManager1.Value;
            }




            // Warehouse
            Guid? warehouseGuid = null;
            var warehouseConfig = _configuration["ExactOnline:DefaultWarehouse"];
            if (!string.IsNullOrEmpty(warehouseConfig) && Guid.TryParse(warehouseConfig, out var wh))
            {
                warehouseGuid = wh;
            }

            //adress kontrol
            bool addressesDiffer = IsBillingAddressDifferentFromShippingAddress(shopifyOrder);
            if (addressesDiffer)
            {
                var delivery = shopifyOrder.ShippingAddress;
                if (delivery != null)
                {
                    var customerDeliveryAddress = await GetCustomerDeliveryAddressesWithDelay(customerId.Value.ToString());

                    if (customerDeliveryAddress.Count > 0)
                    {
                        bool addressFound = false;
                        foreach (var address in customerDeliveryAddress)
                        {
                            _logger.LogInformation($"   🔍 Exact'teki teslimat adresi: {address.AddressLine1}, {address.PostalCode} {address.City}");

                            if (address.FullAddress == delivery.Address1 + ", " + delivery.Zip + ", " + delivery.City)
                            {
                                address.IsMain = true;
                                await _exactAddressCrud.UpdateAddress(address.Id.ToString(), address);
                                await Task.Delay(ADDRESS_OPERATION_DELAY_MS);
                                _logger.LogInformation("   ✅ Exact'teki teslimat adresi Shopify adresi ile eşleşiyor.");
                                addressFound = true;
                                break;
                            }
                        }
                        if (!addressFound)
                        {
                            await CreateDeliveryAddress(delivery, customerId.Value.ToString());
                        }
                        else
                        {
                            _logger.LogInformation("   ✅ Müşterinin teslimat adresi Exact'te bulundu ve kullanılacak.");
                        }
                    }
                    else
                    {
                        ExactAddress newDeliveryAddress = new ExactAddress
                        {
                            AccountId = Guid.Parse(customerId.Value.ToString()),
                            Type = 4, // 4 = Teslimat Adresi
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
                        await Task.Delay(ADDRESS_OPERATION_DELAY_MS);
                        if (createdAddress != null)
                        {
                            _logger.LogInformation("   ✅ Müşterinin teslimat adresi Exact'te oluşturuldu ve kullanılacak.");
                        }
                        else
                        {
                            _logger.LogWarning("   ⚠️ Müşterinin teslimat adresi oluşturulamadı.");
                        }
                    }
                }
            }
            else
            {
                var delivery = shopifyOrder.ShippingAddress;
                if (delivery != null)
                {
                    var customerDeliveryAddress = await GetCustomerDeliveryAddressesWithDelay(customerId.Value.ToString());

                    if (customerDeliveryAddress.Count > 0)
                    {
                        bool addressFound = false;
                        foreach (var address in customerDeliveryAddress)
                        {
                            _logger.LogInformation($"   🔍 Exact'teki teslimat adresi: {address.AddressLine1}, {address.PostalCode} {address.City}");

                            if (address.FullAddress == delivery.Address1 + ", " + delivery.Zip + ", " + delivery.City)
                            {
                                address.IsMain = true;
                                await _exactAddressCrud.UpdateAddress(address.Id.ToString(), address);
                                await Task.Delay(ADDRESS_OPERATION_DELAY_MS);
                                _logger.LogInformation("   ✅ Exact'teki teslimat adresi Shopify adresi ile eşleşiyor.");
                                addressFound = true;
                                break;
                            }
                        }
                        if (!addressFound)
                        {
                            await CreateDeliveryAddress(delivery, customerId.Value.ToString());
                        }
                        else
                        {
                            _logger.LogInformation("   ✅ Müşterinin teslimat adresi Exact'te bulundu ve kullanılacak.");
                        }
                    }
                    else
                    {
                        ExactAddress newDeliveryAddress = new ExactAddress
                        {
                            AccountId = Guid.Parse(customerId.Value.ToString()),
                            Type = 4, // 4 = Teslimat Adresi
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
                        await Task.Delay(ADDRESS_OPERATION_DELAY_MS);
                        if (createdAddress != null)
                        {
                            _logger.LogInformation("   ✅ Müşterinin teslimat adresi Exact'te oluşturuldu ve kullanılacak.");
                        }
                        else
                        {
                            _logger.LogWarning("   ⚠️ Müşterinin teslimat adresi oluşturulamadı.");
                        }
                    }
                }
            }

            _logger.LogInformation($"📄 Sipariş açıklaması adresleri ile oluşturuluyor...");
            await Task.Delay(API_REQUEST_DELAY_MS); // Adres işlemleri bittikten sonra bekle

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

                // Kurumsal siparişlerde firmanın ilgili kişisi
                OrderedByContactPerson = contactPersonId,
                DeliverToContactPerson = contactPersonId,
                InvoiceToContactPerson = contactPersonId,
                OrderDate = orderDate,
                DeliveryDate = defaultDeliveryDate,  // Pickup date veya varsayılan
                Description = $"Shopify Order #{shopifyOrder.OrderNumber}",
                Currency = _configuration["ExactOnline:DefaultCurrency"] ?? "EUR",
                Status = 12,
                Division = 553201,
                WarehouseID = warehouseGuid,
                SalesOrderLines = salesOrderLines,
                // ShippingMethod = shippingMethodGuid,
                YourRef = referenceNumber,
                Salesperson = salespersonGuid,

                // Amount değerlerini Exact hesaplasın
                AmountDC = currentSubtotalPrice - currentTotalTax,  // KDV hariç
                AmountFC = currentSubtotalPrice - currentTotalTax,  // KDV hariç
                AmountFCExclVat = currentSubtotalPrice - currentTotalTax,

                // 🎁 Pickup indirimi - HER İKİ ALANI DA GÖNDER
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

            // Salesperson hatası varsa müşterinin AccountManager'ını kullanarak tekrar dene
            if (!success && exactOrder.Salesperson.HasValue)
            {
                _logger.LogWarning("⚠️ Sipariş oluşturulamadı (muhtemelen Salesperson hatası). Müşterinin AccountManager'ı deneniyor...");
                var accountManager = await _exactService.GetAccountManagerByCustomerIdAsync(customerId.Value);
                if (accountManager.HasValue)
                {
                    _logger.LogInformation("🔄 AccountManager bulundu: {AccountManager}, sipariş tekrar gönderiliyor...", accountManager.Value);
                    exactOrder.Salesperson = accountManager.Value;
                    (success, exactOrderId, exactOrderNumber) = await _exactService.CreateSalesOrderAsync(exactOrder);
                }
                else
                {
                    _logger.LogWarning("⚠️ AccountManager bulunamadı, fallback Salesperson ile tekrar deneniyor...");
                    exactOrder.Salesperson = new Guid("5acbab56-ef3d-47d4-873e-f494dcfe1426");
                    (success, exactOrderId, exactOrderNumber) = await _exactService.CreateSalesOrderAsync(exactOrder);
                    if (!success)
                        _logger.LogError("❌ Fallback Salesperson ile de sipariş oluşturulamadı. Müşteri ID: {CustomerId}", customerId.Value);
                }

                if (accountManager.HasValue && !success)
                {
                    _logger.LogWarning("⚠️ AccountManager ({AccountManager}) de geçersiz, fallback Salesperson ile son deneme yapılıyor...", accountManager.Value);
                    exactOrder.Salesperson = new Guid("5acbab56-ef3d-47d4-873e-f494dcfe1426");
                    (success, exactOrderId, exactOrderNumber) = await _exactService.CreateSalesOrderAsync(exactOrder);
                    if (!success)
                        _logger.LogError("❌ Fallback Salesperson ile de sipariş oluşturulamadı. Müşteri ID: {CustomerId}", customerId.Value);
                }
            }

            return (success, exactOrderId, exactOrderNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError($"ExactOnline entegrasyonu hatası: {ex.Message}");
            _logger.LogError($"Stack trace: {ex.StackTrace}");
            return (false, null, null);
        }
    }

    /// <summary>
    /// Kurumsal siparişlerde ilgili kişiyi (contact) ve firma fatura adresini (Type=3) oluşturur/bulur.
    /// Oluşturulan/bulunan contact GUID'ini döner; hata durumunda null döner ve sipariş akışını durdurmaz.
    /// </summary>
    private async Task<Guid?> EnsureCompanyContactAndInvoiceAddress(ShopifyOrder shopifyOrder, ShopifyCompanyInfo companyInfo, Guid customerId)
    {
        Guid? contactId = null;
        try
        {
            // İlgili kişi: siparişteki müşteri adı, yoksa company_contact_person
            var firstName = shopifyOrder.Customer?.FirstName?.Trim();
            var lastName = shopifyOrder.Customer?.LastName?.Trim();

            if (string.IsNullOrWhiteSpace(firstName) && !string.IsNullOrWhiteSpace(companyInfo.ContactPerson))
            {
                var parts = companyInfo.ContactPerson.Trim().Split(' ', 2);
                firstName = parts[0];
                lastName = parts.Length > 1 ? parts[1] : parts[0];
            }

            if (!string.IsNullOrWhiteSpace(firstName))
            {
                contactId = await _exactService.GetOrCreateContactAsync(
                    customerId, firstName, lastName,
                    shopifyOrder.Customer?.Email, companyInfo.Phone);
                await Task.Delay(ADDRESS_OPERATION_DELAY_MS);

                if (contactId != null)
                {
                    _logger.LogInformation("👤 İlgili kişi hazır: {ContactId} ({FirstName} {LastName})", contactId, firstName, lastName);
                }
            }
            else
            {
                _logger.LogWarning("⚠️ Kurumsal siparişte ilgili kişi adı bulunamadı, contact oluşturulmadı");
            }

            // Fatura adresi (Type=3): company_address
            if (!string.IsNullOrWhiteSpace(companyInfo.Address))
            {
                var billingAddresses = await GetCustomerBillingAddressesWithDelay(customerId.ToString());

                bool addressExists = billingAddresses.Any(a =>
                    string.Equals(a.FullAddress, companyInfo.FullAddress, StringComparison.OrdinalIgnoreCase));

                if (!addressExists)
                {
                    var invoiceAddress = new ExactAddress
                    {
                        AccountId = customerId,
                        Type = 3, // 3 = Fatura Adresi
                        AddressLine1 = companyInfo.Address ?? "",
                        City = companyInfo.City ?? "",
                        PostalCode = companyInfo.PostalCode ?? "",
                        IsMain = true,
                        CountryCode = shopifyOrder.BillingAddress?.CountryCode
                            ?? shopifyOrder.ShippingAddress?.CountryCode ?? "",
                        AccountName = companyInfo.Name,
                        ContactName = $"{firstName} {lastName}".Trim(),
                        Phone = companyInfo.Phone ?? "",
                        Division = int.TryParse(_configuration["ExactOnline:DivisionCode"], out var div) ? div : 0
                    };

                    var createdInvoiceAddress = await _exactAddressCrud.CreateAddress(invoiceAddress);
                    await Task.Delay(ADDRESS_OPERATION_DELAY_MS);

                    if (createdInvoiceAddress != null)
                    {
                        _logger.LogInformation("🧾 Firma fatura adresi oluşturuldu: {Address}", companyInfo.FullAddress);
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ Firma fatura adresi oluşturulamadı: {Address}", companyInfo.FullAddress);
                    }
                }
                else
                {
                    _logger.LogInformation("🧾 Firma fatura adresi zaten mevcut: {Address}", companyInfo.FullAddress);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("❌ Kurumsal contact/fatura adresi hazırlanırken hata: {Error}", ex.Message);
        }

        return contactId;
    }

    /// <summary>
    /// Fatura adreslerini delay ile getir
    /// </summary>
    private async Task<List<ExactAddress>> GetCustomerBillingAddressesWithDelay(string customerId)
    {
        try
        {
            var addresses = await _exactAddressCrud.GetCustomerBillingAddresses(customerId);
            await Task.Delay(API_REQUEST_DELAY_MS);
            return addresses;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Fatura adresleri getirilirken hata: {ex.Message}");
            return new List<ExactAddress>();
        }
    }

    /// <summary>
    /// Teslimat adreslerini delay ile getir
    /// </summary>
    private async Task<List<ExactAddress>> GetCustomerDeliveryAddressesWithDelay(string customerId)
    {
        try
        {
            var addresses = await _exactAddressCrud.GetCustomerDeliveryAddresses(customerId);
            await Task.Delay(API_REQUEST_DELAY_MS);
            return addresses;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Teslimat adresleri getirilirken hata: {ex.Message}");
            return new List<ExactAddress>();
        }
    }

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

    private async Task CreateNewBillingAddress(ShopifyAddress billing, string customerId)
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
        await Task.Delay(ADDRESS_OPERATION_DELAY_MS); // Adres oluşturulduktan sonra bekle
        if (createdAddress != null)
        {
            _logger.LogInformation("   ✅ Müşterinin fatura adresi Exact'te oluşturuldu ve kullanılacak.");
        }
        else
        {
            _logger.LogWarning("   ⚠️ Müşterinin fatura adresi oluşturulamadı.");
        }
    }

    private async Task CreateDeliveryAddress(ShopifyAddress delivery, string customerId)
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
        await Task.Delay(ADDRESS_OPERATION_DELAY_MS); // Adres oluşturulduktan sonra bekle
        if (createdAddress != null)
        {
            _logger.LogInformation("   ✅ Müşterinin teslimat adresi Exact'te oluşturuldu ve kullanılacak.");
        }
        else
        {
            _logger.LogWarning("   ⚠️ Müşterinin teslimat adresi oluşturulamadı.");

        }
    }

    private string NormalizeString(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        return input.Trim().ToLowerInvariant();
    }
}