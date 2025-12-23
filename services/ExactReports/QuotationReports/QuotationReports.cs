using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using ShopifyProductApp.Services;
using System.Text;
using ExactOnline.Models;
using ExactOnline.Converters;
using System.Text.RegularExpressions;

public class QuotationReports
{
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _redirectUri;
    private readonly ITokenManager _tokenManager;
    private readonly string _baseUrl;
    private readonly string _divisionCode;
    private readonly ILogger _logger;
    private readonly string _tokenFile;
    private readonly ISettingsService _settingsService;

    public QuotationReports(
     string clientId,
     string clientSecret,
     string redirectUri,
     ITokenManager tokenManager,
     string baseUrl,
     string divisionCode,
     string tokenFile,
     ISettingsService settingsService,
     IServiceProvider serviceProvider,
     ILogger logger)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _redirectUri = redirectUri;
        _tokenManager = tokenManager;
        _baseUrl = baseUrl;
        _divisionCode = divisionCode;
        _tokenFile = tokenFile;
        _settingsService = settingsService;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    // Quotation raporu - tarih aralığında
    public async Task<string> GetQuotationReportAsync(DateTime startDate, DateTime endDate)
    {
        var exactService = _serviceProvider.GetRequiredService<ExactService>();
        var token = await exactService.GetValidToken();

        if (token == null)
        {
            _logger.LogError("❌ Geçerli bir token alınamadı");
            return null;
        }

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var startDateStr = startDate.ToString("yyyy-MM-dd");
        var endDateStr = endDate.ToString("yyyy-MM-dd");
        int pageSize = 60;

        var filter = $"Created ge datetime'{startDateStr}' and Created le datetime'{endDateStr}'";
        var select = "QuotationID,QuotationNumber,Created,Status,OrderAccountName,AmountFC,AmountDC,CloseDate,CreatorFullName,DeliveryAccount,DeliveryAccountCode,DeliveryAccountContact,DeliveryAccountContactFullName,DeliveryAccountName,Project,DeliveryAddress,Description,ClosingDate,DeliveryDate,DocumentSubject,InvoiceAccountCode,DueDate,Document,InvoiceAccount,Opportunity,OpportunityName,OrderAccount,OrderAccountCode,OrderAccountContact,OrderAccountContactFullName,OrderAccountName,PaymentCondition,PaymentConditionDescription,Currency,ProjectCode,ProjectDescription,QuotationDate";

        var encodedFilter = Uri.EscapeDataString(filter);
        var encodedSelect = Uri.EscapeDataString(select);

        var url = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Quotations" +
                  $"?$filter={encodedFilter}" +
                  $"&$select={encodedSelect}" +
                  $"&$top={pageSize}";

        _logger.LogInformation($"📡 API URL: {url}");

        var response = await client.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError($"❌ API Hatası: {response.StatusCode}");
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError($"❌ Hata Detayı: {errorContent}");
            return "[]";
        }

        var json = await response.Content.ReadAsStringAsync();
        return json;
    }

    // QuotationLines'ı fetch et
    private async Task<string> GetQuotationLinesAsync(string quotationLinesUri, string token)
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            _logger.LogDebug($"📥 QuotationLines URI çağrılıyor: {quotationLinesUri}");

            var response = await client.GetAsync(quotationLinesUri);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"❌ QuotationLines Fetch Hatası: {response.StatusCode}");
                _logger.LogError($"   Hata Detayı: {errorContent}");
                return "{\"d\":{\"results\":[]}}";  // Empty result
            }

            var content = await response.Content.ReadAsStringAsync();
            _logger.LogDebug($"✅ QuotationLines alındı. Boyut: {content.Length} bytes");
            return content;
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ QuotationLines Exception: {ex.Message}");
            _logger.LogError($"   Stack Trace: {ex.StackTrace}");
            return "{\"d\":{\"results\":[]}}";  // Empty result
        }
    }

    // En çok teklif verilen ürünleri getir
    public async Task<List<TopProductDTO>> GetTopQuotedProductsAsync(DateTime startDate, DateTime endDate, int topCount = 10)
    {
        var exactService = _serviceProvider.GetRequiredService<ExactService>();
        var token = await exactService.GetValidToken();

        if (token == null)
        {
            _logger.LogError("❌ Geçerli bir token alınamadı");
            return new List<TopProductDTO>();
        }

        var quotationJson = await GetQuotationReportAsync(startDate, endDate);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var quotationResponse = JsonSerializer.Deserialize<QuotationResponse>(quotationJson, options);
        var quotations = quotationResponse?.GetQuotations();

        if (quotations == null || quotations.Count == 0)
        {
            _logger.LogWarning("⚠️ Quotation bulunamadı");
            return new List<TopProductDTO>();
        }

        var productCounts = new Dictionary<string, ProductInfo>();

        // Her quotation için QuotationLines'ı fetch et
        foreach (var quotation in quotations)
        {
            var linesUri = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Quotations(guid'{quotation.QuotationID}')/QuotationLines";
            var linesJson = await GetQuotationLinesAsync(linesUri, token.access_token);

            var linesResponse = JsonSerializer.Deserialize<QuotationLineResponse>(linesJson, options);
            var lines = linesResponse?.GetLines();

            if (lines != null && lines.Count > 0)
            {
                foreach (var line in lines)
                {
                    // Item tanımlaması: ItemCode varsa kullan, yoksa Item GUID'ini kullan
                    var itemKey = !string.IsNullOrWhiteSpace(line.ItemCode)
                        ? line.ItemCode
                        : (line.Item ?? line.ID);

                    // Ürün açıklaması: ItemDescription varsa kullan, yoksa Description'ı kullan
                    var itemDescription = !string.IsNullOrWhiteSpace(line.ItemDescription)
                        ? line.ItemDescription
                        : line.Description;

                    if (!productCounts.ContainsKey(itemKey))
                    {
                        productCounts[itemKey] = new ProductInfo
                        {
                            ItemCode = itemKey,
                            ItemDescription = itemDescription,
                            Quantity = 0,
                            TotalAmount = 0,
                            QuotationCount = 0
                        };
                    }

                    productCounts[itemKey].Quantity += line.Quantity ?? 0;
                    productCounts[itemKey].TotalAmount += line.AmountFC ?? 0;
                    productCounts[itemKey].QuotationCount++;

                    _logger.LogDebug($"📦 Ürün eklendi: {itemDescription} - Miktar: {line.Quantity}, Tutar: {line.AmountFC}");
                }
            }
        }

        // En çok teklif verilen ürünleri sırala
        var topProducts = productCounts.Values
            .OrderByDescending(p => p.QuotationCount)
            .ThenByDescending(p => p.Quantity)
            .Take(topCount)
            .Select((p, index) => new TopProductDTO
            {
                Rank = index + 1,
                ItemCode = p.ItemCode,
                ItemDescription = p.ItemDescription,
                QuotationCount = p.QuotationCount,
                TotalQuantity = p.Quantity,
                TotalAmount = p.TotalAmount
            })
            .ToList();

        _logger.LogInformation($"✅ {topProducts.Count} ürün bulundu");
        return topProducts;
    }

    // En çok teklif verilen müşterileri getir
    public async Task<List<TopCustomerDTO>> GetTopQuotedCustomersAsync(DateTime startDate, DateTime endDate, int topCount = 10)
    {
        var quotationJson = await GetQuotationReportAsync(startDate, endDate);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        
        var quotationResponse = JsonSerializer.Deserialize<QuotationResponse>(quotationJson, options);
        var quotations = quotationResponse?.GetQuotations();

        if (quotations == null || quotations.Count == 0)
        {
            _logger.LogWarning("⚠️ Quotation bulunamadı");
            return new List<TopCustomerDTO>();
        }

        var customerCounts = new Dictionary<string, CustomerInfo>();

        // DeliveryAccountName göre gruplaştır
        foreach (var quotation in quotations)
        {
            var customerKey = quotation.DeliveryAccountName ?? quotation.OrderAccountName ?? "Unknown";

            if (!customerCounts.ContainsKey(customerKey))
            {
                customerCounts[customerKey] = new CustomerInfo
                {
                    CustomerName = customerKey,
                    AccountCode = quotation.DeliveryAccountCode ?? quotation.OrderAccountCode,
                    QuotationCount = 0,
                    TotalAmount = 0,
                    Currency = quotation.Currency
                };
            }

            customerCounts[customerKey].QuotationCount++;
            customerCounts[customerKey].TotalAmount += quotation.AmountFC ?? 0;
        }

        // En çok teklif verilen müşterileri sırala
        var topCustomers = customerCounts.Values
            .OrderByDescending(c => c.QuotationCount)
            .ThenByDescending(c => c.TotalAmount)
            .Take(topCount)
            .Select((c, index) => new TopCustomerDTO
            {
                Rank = index + 1,
                CustomerName = c.CustomerName,
                AccountCode = c.AccountCode,
                QuotationCount = c.QuotationCount,
                TotalAmount = c.TotalAmount,
                Currency = c.Currency
            })
            .ToList();

        _logger.LogInformation($"✅ {topCustomers.Count} müşteri bulundu");
        return topCustomers;
    }

    // İki tarih aralığında ürünleri karşılaştır
    public async Task<ComparisonProductResultDTO> CompareProductsByDateRangeAsync(
        DateTime startDate1, DateTime endDate1,
        DateTime startDate2, DateTime endDate2,
        int topCount = 10)
    {
        _logger.LogInformation($"📊 Ürün karşılaştırması başlıyor: Period1({startDate1:yyyy-MM-dd} - {endDate1:yyyy-MM-dd}) vs Period2({startDate2:yyyy-MM-dd} - {endDate2:yyyy-MM-dd})");

        var period1Products = await GetTopQuotedProductsAsync(startDate1, endDate1, topCount * 2);
        var period2Products = await GetTopQuotedProductsAsync(startDate2, endDate2, topCount * 2);

        var result = new ComparisonProductResultDTO
        {
            Period1 = new PeriodDTO { StartDate = startDate1, EndDate = endDate1 },
            Period2 = new PeriodDTO { StartDate = startDate2, EndDate = endDate2 },
            ComparisonProducts = new List<ProductComparisonDTO>()
        };

        // Tüm ürün keyleri topla
        var allProductKeys = new HashSet<string>();
        foreach (var product in period1Products)
            allProductKeys.Add(product.ItemCode);
        foreach (var product in period2Products)
            allProductKeys.Add(product.ItemCode);

        // Her ürün için karşılaştırma yap
        var comparisons = new List<ProductComparisonDTO>();

        foreach (var key in allProductKeys)
        {
            var p1 = period1Products.FirstOrDefault(p => p.ItemCode == key);
            var p2 = period2Products.FirstOrDefault(p => p.ItemCode == key);

            var comparison = new ProductComparisonDTO
            {
                ItemCode = key,
                ItemDescription = p1?.ItemDescription ?? p2?.ItemDescription,
                Period1QuotationCount = p1?.QuotationCount ?? 0,
                Period2QuotationCount = p2?.QuotationCount ?? 0,
                QuotationCountChange = (p2?.QuotationCount ?? 0) - (p1?.QuotationCount ?? 0),
                Period1TotalAmount = p1?.TotalAmount ?? 0,
                Period2TotalAmount = p2?.TotalAmount ?? 0,
                TotalAmountChange = (p2?.TotalAmount ?? 0) - (p1?.TotalAmount ?? 0),
                Period1TotalQuantity = p1?.TotalQuantity ?? 0,
                Period2TotalQuantity = p2?.TotalQuantity ?? 0,
                QuantityChange = (p2?.TotalQuantity ?? 0) - (p1?.TotalQuantity ?? 0),
                ChangePercentage = p1?.QuotationCount > 0
                    ? ((((p2?.QuotationCount ?? 0) - (p1?.QuotationCount ?? 0)) * 100m) / (p1?.QuotationCount ?? 1))
                    : (p2?.QuotationCount > 0 ? 100 : 0)
            };

            comparisons.Add(comparison);
        }

        // Değişime göre sırala
        result.ComparisonProducts = comparisons
            .OrderByDescending(c => Math.Abs(c.QuotationCountChange))
            .ThenByDescending(c => c.Period2QuotationCount)
            .Take(topCount)
            .Select((c, index) => 
            {
                c.Rank = index + 1;
                return c;
            })
            .ToList();

        _logger.LogInformation($"✅ {result.ComparisonProducts.Count} ürün karşılaştırıldı");
        return result;
    }

    // İki tarih aralığında müşterileri karşılaştır
    public async Task<ComparisonCustomerResultDTO> CompareCustomersByDateRangeAsync(
        DateTime startDate1, DateTime endDate1,
        DateTime startDate2, DateTime endDate2,
        int topCount = 10)
    {
        _logger.LogInformation($"📊 Müşteri karşılaştırması başlıyor: Period1({startDate1:yyyy-MM-dd} - {endDate1:yyyy-MM-dd}) vs Period2({startDate2:yyyy-MM-dd} - {endDate2:yyyy-MM-dd})");

        var period1Customers = await GetTopQuotedCustomersAsync(startDate1, endDate1, topCount * 2);
        var period2Customers = await GetTopQuotedCustomersAsync(startDate2, endDate2, topCount * 2);

        var result = new ComparisonCustomerResultDTO
        {
            Period1 = new PeriodDTO { StartDate = startDate1, EndDate = endDate1 },
            Period2 = new PeriodDTO { StartDate = startDate2, EndDate = endDate2 },
            ComparisonCustomers = new List<CustomerComparisonDTO>()
        };

        // Tüm müşteri keyleri topla
        var allCustomerNames = new HashSet<string>();
        foreach (var customer in period1Customers)
            allCustomerNames.Add(customer.CustomerName);
        foreach (var customer in period2Customers)
            allCustomerNames.Add(customer.CustomerName);

        // Her müşteri için karşılaştırma yap
        var comparisons = new List<CustomerComparisonDTO>();

        foreach (var name in allCustomerNames)
        {
            var c1 = period1Customers.FirstOrDefault(c => c.CustomerName == name);
            var c2 = period2Customers.FirstOrDefault(c => c.CustomerName == name);

            var comparison = new CustomerComparisonDTO
            {
                CustomerName = name,
                AccountCode = c1?.AccountCode ?? c2?.AccountCode,
                Period1QuotationCount = c1?.QuotationCount ?? 0,
                Period2QuotationCount = c2?.QuotationCount ?? 0,
                QuotationCountChange = (c2?.QuotationCount ?? 0) - (c1?.QuotationCount ?? 0),
                Period1TotalAmount = c1?.TotalAmount ?? 0,
                Period2TotalAmount = c2?.TotalAmount ?? 0,
                TotalAmountChange = (c2?.TotalAmount ?? 0) - (c1?.TotalAmount ?? 0),
                Currency = c1?.Currency ?? c2?.Currency,
                ChangePercentage = c1?.QuotationCount > 0
                    ? ((((c2?.QuotationCount ?? 0) - (c1?.QuotationCount ?? 0)) * 100m) / (c1?.QuotationCount ?? 1))
                    : (c2?.QuotationCount > 0 ? 100 : 0)
            };

            comparisons.Add(comparison);
        }

        // Değişime göre sırala
        result.ComparisonCustomers = comparisons
            .OrderByDescending(c => Math.Abs(c.QuotationCountChange))
            .ThenByDescending(c => c.Period2QuotationCount)
            .Take(topCount)
            .Select((c, index) => 
            {
                c.Rank = index + 1;
                return c;
            })
            .ToList();

        _logger.LogInformation($"✅ {result.ComparisonCustomers.Count} müşteri karşılaştırıldı");
        return result;
    }
}

// ============================================
// Model sınıfları
// ============================================

public class QuotationResponse
{
    [JsonPropertyName("d")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Quotation> D { get; set; }

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Quotation> Value { get; set; }

    public List<Quotation> GetQuotations()
    {
        return D ?? Value ?? new List<Quotation>();
    }
}

public class Quotation
{
    [JsonPropertyName("QuotationID")]
    public string QuotationID { get; set; }

    [JsonPropertyName("QuotationNumber")]
    public int QuotationNumber { get; set; }

    [JsonPropertyName("DeliveryAccountName")]
    public string DeliveryAccountName { get; set; }

    [JsonPropertyName("DeliveryAccountCode")]
    public string DeliveryAccountCode { get; set; }

    [JsonPropertyName("OrderAccountName")]
    public string OrderAccountName { get; set; }

    [JsonPropertyName("OrderAccountCode")]
    public string OrderAccountCode { get; set; }

    [JsonPropertyName("AmountFC")]
    public decimal? AmountFC { get; set; }

    [JsonPropertyName("Currency")]
    public string Currency { get; set; }

    [JsonPropertyName("Created")]
    [JsonConverter(typeof(JsonDateTimeConverter))]
    public DateTime? Created { get; set; }

    [JsonPropertyName("Status")]
    public int Status { get; set; }
}

// DateTime Converter for "/Date(...)/" format
public class JsonDateTimeConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string value = reader.GetString();

        if (string.IsNullOrEmpty(value))
            return null;

        // "/Date(1764928223777)/" formatını parse et
        if (value.StartsWith("/Date(") && value.EndsWith(")/"))
        {
            var ticksStr = value.Substring(6, value.Length - 9);

            if (long.TryParse(ticksStr, out long ticks))
            {
                return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ticks);
            }
        }

        // Normal DateTime parse
        if (DateTime.TryParse(value, out var result))
            return result;

        return null;
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteStringValue(value.Value.ToString("O"));
        else
            writer.WriteNullValue();
    }
}

public class QuotationLineResponse
{
    [JsonPropertyName("d")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QuotationLineData D { get; set; }

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<QuotationLine> Value { get; set; }

    public List<QuotationLine> GetLines()
    {
        // D.results varsa onu döndür, yoksa Value'yu döndür
        if (D?.Results != null && D.Results.Count > 0)
            return D.Results;

        return Value ?? new List<QuotationLine>();
    }
}

// Nested data structure
public class QuotationLineData
{
    [JsonPropertyName("results")]
    public List<QuotationLine> Results { get; set; }
}

public class QuotationLine
{
    [JsonPropertyName("Item")]
    public string Item { get; set; }

    [JsonPropertyName("ItemCode")]
    public string ItemCode { get; set; }

    [JsonPropertyName("ItemDescription")]
    public string ItemDescription { get; set; }

    [JsonPropertyName("Description")]
    public string Description { get; set; }

    [JsonPropertyName("Quantity")]
    public decimal? Quantity { get; set; }

    [JsonPropertyName("AmountFC")]
    public decimal? AmountFC { get; set; }

    [JsonPropertyName("AmountDC")]
    public decimal? AmountDC { get; set; }

    [JsonPropertyName("UnitPrice")]
    public decimal? UnitPrice { get; set; }

    [JsonPropertyName("NetPrice")]
    public decimal? NetPrice { get; set; }

    [JsonPropertyName("UnitCode")]
    public string UnitCode { get; set; }

    [JsonPropertyName("UnitDescription")]
    public string UnitDescription { get; set; }

    [JsonPropertyName("ID")]
    public string ID { get; set; }

    [JsonPropertyName("QuotationID")]
    public string QuotationID { get; set; }

    [JsonPropertyName("QuotationNumber")]
    public int? QuotationNumber { get; set; }

    [JsonPropertyName("LineNumber")]
    public int? LineNumber { get; set; }

    [JsonPropertyName("VATAmountFC")]
    public decimal? VATAmountFC { get; set; }

    [JsonPropertyName("VATCode")]
    public string VATCode { get; set; }

    [JsonPropertyName("VATPercentage")]
    public decimal? VATPercentage { get; set; }

    [JsonPropertyName("Discount")]
    public decimal? Discount { get; set; }

    [JsonPropertyName("Division")]
    public int? Division { get; set; }

    [JsonPropertyName("VersionNumber")]
    public int? VersionNumber { get; set; }
}

// ============================================
// DTOs - Single Period
// ============================================

public class TopProductDTO
{
    public int Rank { get; set; }
    public string ItemCode { get; set; }
    public string ItemDescription { get; set; }
    public int QuotationCount { get; set; }
    public decimal TotalQuantity { get; set; }
    public decimal TotalAmount { get; set; }
}

public class TopCustomerDTO
{
    public int Rank { get; set; }
    public string CustomerName { get; set; }

    private string _accountCode;
    public string AccountCode 
    { 
        get => _accountCode;
        set => _accountCode = value?.Trim();
    }
    public int QuotationCount { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; }
}

// ============================================
// DTOs - Comparison
// ============================================

public class PeriodDTO
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    
    public string DisplayPeriod => $"{StartDate:yyyy-MM-dd} - {EndDate:yyyy-MM-dd}";
}

public class ComparisonProductResultDTO
{
    public PeriodDTO Period1 { get; set; }
    public PeriodDTO Period2 { get; set; }
    public List<ProductComparisonDTO> ComparisonProducts { get; set; }
    
    public int TotalProducts => ComparisonProducts?.Count ?? 0;
    public int NewProducts => ComparisonProducts?.Count(p => p.Period1QuotationCount == 0) ?? 0;
    public int RemovedProducts => ComparisonProducts?.Count(p => p.Period2QuotationCount == 0) ?? 0;
    public int IncreasedProducts => ComparisonProducts?.Count(p => p.QuotationCountChange > 0) ?? 0;
    public int DecreasedProducts => ComparisonProducts?.Count(p => p.QuotationCountChange < 0) ?? 0;
}

public class ProductComparisonDTO
{
    public int Rank { get; set; }
    public string ItemCode { get; set; }
    public string ItemDescription { get; set; }
    
    // Period 1
    public int Period1QuotationCount { get; set; }
    public decimal Period1TotalAmount { get; set; }
    public decimal Period1TotalQuantity { get; set; }
    
    // Period 2
    public int Period2QuotationCount { get; set; }
    public decimal Period2TotalAmount { get; set; }
    public decimal Period2TotalQuantity { get; set; }
    
    // Change
    public int QuotationCountChange { get; set; }
    public decimal TotalAmountChange { get; set; }
    public decimal QuantityChange { get; set; }
    public decimal ChangePercentage { get; set; }
    
    // Status
    public string Status
    {
        get
        {
            if (Period1QuotationCount == 0 && Period2QuotationCount > 0) return "NEW";
            if (Period1QuotationCount > 0 && Period2QuotationCount == 0) return "REMOVED";
            if (QuotationCountChange > 0) return "INCREASED";
            if (QuotationCountChange < 0) return "DECREASED";
            return "STABLE";
        }
    }
}

public class ComparisonCustomerResultDTO
{
    public PeriodDTO Period1 { get; set; }
    public PeriodDTO Period2 { get; set; }
    public List<CustomerComparisonDTO> ComparisonCustomers { get; set; }
    
    public int TotalCustomers => ComparisonCustomers?.Count ?? 0;
    public int NewCustomers => ComparisonCustomers?.Count(c => c.Period1QuotationCount == 0) ?? 0;
    public int LostCustomers => ComparisonCustomers?.Count(c => c.Period2QuotationCount == 0) ?? 0;
    public int IncreasingCustomers => ComparisonCustomers?.Count(c => c.QuotationCountChange > 0) ?? 0;
    public int DecreasingCustomers => ComparisonCustomers?.Count(c => c.QuotationCountChange < 0) ?? 0;
}

public class CustomerComparisonDTO
{
    public int Rank { get; set; }
    public string CustomerName { get; set; }
    public string AccountCode { get; set; }
    
    // Period 1
    public int Period1QuotationCount { get; set; }
    public decimal Period1TotalAmount { get; set; }
    
    // Period 2
    public int Period2QuotationCount { get; set; }
    public decimal Period2TotalAmount { get; set; }
    
    // Change
    public int QuotationCountChange { get; set; }
    public decimal TotalAmountChange { get; set; }
    public decimal ChangePercentage { get; set; }
    public string Currency { get; set; }
    
    // Status
    public string Status
    {
        get
        {
            if (Period1QuotationCount == 0 && Period2QuotationCount > 0) return "NEW";
            if (Period1QuotationCount > 0 && Period2QuotationCount == 0) return "LOST";
            if (QuotationCountChange > 0) return "INCREASING";
            if (QuotationCountChange < 0) return "DECREASING";
            return "STABLE";
        }
    }
}

// ============================================
// Internal Helper Classes
// ============================================

internal class ProductInfo
{
    public string ItemCode { get; set; }
    public string ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public decimal TotalAmount { get; set; }
    public int QuotationCount { get; set; }
}

internal class CustomerInfo
{
    public string CustomerName { get; set; }
    public string AccountCode { get; set; }
    public decimal TotalAmount { get; set; }
    public int QuotationCount { get; set; }
    public string Currency { get; set; }
}