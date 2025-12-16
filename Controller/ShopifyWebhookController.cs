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
               
               
                //adress kontrol
                //fatura adresi
                bool addressesDiffer = IsBillingAddressDifferentFromShippingAddress(shopifyOrder);
                if (addressesDiffer)
                {
                    var billing = shopifyOrder.BillingAddress;
                    var customerBillingAddress = _exactAddressCrud.GetCustomerBillingAddresses(customerId.Value.ToString());
                    if (customerBillingAddress.Result.Count > 0)
                    {
                        bool addressFound = false;
                        foreach (var address in customerBillingAddress.Result)
                        {
                            _logger.LogInformation($"   🔍 Exact'teki fatura adresi: {address.AddressLine1}, {address.PostalCode} {address.City}");

                            if (address.FullAddress == billing.Address1 + ", " + billing.Zip + ", " + billing.City)
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
                            await CreateNewBillingAddress(billing, customerId.Value.ToString());
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
                        ExactAddress newBillingAddress = new ExactAddress
                        {
                            AccountId = Guid.Parse(customerId.Value.ToString()),
                            Type = 3, // 3 = Fatura Adresi
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
                         //await CreateNewBillingAddress(billing, customerId.Value.ToString());
                    }
                    var delivery = shopifyOrder.ShippingAddress;
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
                else
                {
                   var billing = shopifyOrder.BillingAddress;
                    var customerBillingAddress = _exactAddressCrud.GetCustomerBillingAddresses(customerId.Value.ToString());
                    if (customerBillingAddress.Result.Count > 0)
                    {
                        bool addressFound = false;
                        foreach (var address in customerBillingAddress.Result)
                        {
                            _logger.LogInformation($"   🔍 Exact'teki fatura adresi: {address.AddressLine1}, {address.PostalCode} {address.City}");

                            if (address.FullAddress == billing.Address1 + ", " + billing.Zip + ", " + billing.City)
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
                            await CreateNewBillingAddress(billing, customerId.Value.ToString());
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
                        ExactAddress newBillingAddress = new ExactAddress
                        {
                            AccountId = Guid.Parse(customerId.Value.ToString()),
                            Type = 3, // 3 = Fatura Adresi
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
                         //await CreateNewBillingAddress(billing, customerId.Value.ToString());
                    }
                    var delivery = shopifyOrder.ShippingAddress;
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
               
                
               
                



                _logger.LogInformation($"📄 Sipariş açıklaması adresleri ile oluşturuluyor...");

                DateTime orderDate = DateTime.Now;
                //shiping method ekle
                //13 --> f4b84d79-3796-4fdc-a24e-08cd7628ce82
                // Mağazadan teslim  02 --> 19eb5f3e-7131-4d48-8a38-5b66eb44aa5b
                Guid shippingMethodGuid = Guid.Parse("19eb5f3e-7131-4d48-8a38-5b66eb44aa5b"); // Varsayılan: Mağazadan teslim
                if (shopifyOrder.ShippingLines != null && shopifyOrder.ShippingLines.Any())
                {
                    var shippingLine = shopifyOrder.ShippingLines.FirstOrDefault();
                    bool hasVerzendkosten = shippingLine?.Title?.Contains("Verzendkosten") ?? false;
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
