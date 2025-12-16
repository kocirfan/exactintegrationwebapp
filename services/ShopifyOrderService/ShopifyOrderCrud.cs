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

    // manuel olarak shopify sipariş getir
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
            await ProcessShopifyOrderToExact(order);
            return order;
        }

        return null;
    }


    // exact'a sipariş gönder
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
                    var finalVATPercentage = vatPercentage == 0 ? 0.21 : vatPercentage;
                    salesOrderLines.Add(new ExactOrderLine
                    {
                        ID = Guid.NewGuid(),
                        Item = exactItem.ID.Value,
                        Description = lineItem.Title,
                        Quantity = lineItem.Quantity,
                        UnitPrice = unitPrice,                      // 299.00 (Orijinal)
                        NetPrice = unitPriceWithDiscount,           // 179.40 (İndirimli)
                        Discount = discountPercentage,              // 40.00 (YÜZDE!)
                        VATPercentage = finalVATPercentage,            //VATPercentage = vatPercentage,
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
            //ExactAddress matchingBillingAddress = null;
            // ExactAddress matchingShippingAddress = null;
            ///Guid? deliveryAddressId = matchingShippingAddress?.Id;
            Guid invoiceAddressId = Guid.Empty;
            //adress kontrol
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
                Description = $"Shopify Manuel Order #{shopifyOrder.OrderNumber}",
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

    private string NormalizeString(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        return input.Trim().ToLowerInvariant();
    }
}
