// using System.Text.Json;
// using System.Text.Json.Serialization;
// using System.Net.Http.Headers;
// using ShopifyProductApp.Services;
// using System.Text;
// using ExactOnline.Models;
// using ExactOnline.Converters;
// using System.Text.RegularExpressions;
// using ExactWebApp.Dto;
// using System.Collections.Concurrent;

// public class QuotationReports
// {
//     private readonly string _clientId;
//     private readonly string _clientSecret;
//     private readonly IServiceProvider _serviceProvider;
//     private readonly string _redirectUri;
//     private readonly ITokenManager _tokenManager;
//     private readonly string _baseUrl;
//     private readonly string _divisionCode;
//     private readonly ILogger _logger;
//     private readonly string _tokenFile;
//     private readonly ISettingsService _settingsService;

//     // ⚡ CACHE - Tekrarlanan API çağrılarını önle
//     private readonly ConcurrentDictionary<string, string> _quotationLinesCache = 
//         new ConcurrentDictionary<string, string>();

//     // ⚡ HttpClient reuse (performance)
//     private readonly HttpClient _httpClient;

//     public QuotationReports(
//      string clientId,
//      string clientSecret,
//      string redirectUri,
//      ITokenManager tokenManager,
//      string baseUrl,
//      string divisionCode,
//      string tokenFile,
//      ISettingsService settingsService,
//      IServiceProvider serviceProvider,
//      ILogger logger)
//     {
//         _clientId = clientId;
//         _clientSecret = clientSecret;
//         _redirectUri = redirectUri;
//         _tokenManager = tokenManager;
//         _baseUrl = baseUrl;
//         _divisionCode = divisionCode;
//         _tokenFile = tokenFile;
//         _settingsService = settingsService;
//         _serviceProvider = serviceProvider;
//         _logger = logger;

//         // ⚡ HttpClient singleton oluştur (reuse et)
//         _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
//     }

//     public async Task<QuotationResponse> GetQuotationReportNewAsync(DateTime startDate, DateTime endDate)
//     {
//         var exactService = _serviceProvider.GetRequiredService<ExactService>();
//         var token = await exactService.GetValidToken();

//         if (token == null)
//         {
//             _logger.LogError("❌ Geçerli bir token alınamadı");
//             return null;
//         }

//         _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
//         _httpClient.DefaultRequestHeaders.Accept.Clear();
//         _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

//         var startDateStr = startDate.ToString("yyyy-MM-dd");
//         var endDateStr = endDate.ToString("yyyy-MM-dd");
//         int pageSize = 60;

//         var filter = $"Created ge datetime'{startDateStr}' and Created le datetime'{endDateStr}'";
//         var select = "QuotationID,QuotationNumber,Created,Status,OrderAccountName,AmountFC,AmountDC,CloseDate,CreatorFullName,DeliveryAccount,DeliveryAccountCode,DeliveryAccountContact,DeliveryAccountContactFullName,DeliveryAccountName,Project,DeliveryAddress,Description,ClosingDate,DeliveryDate,DocumentSubject,InvoiceAccountCode,DueDate,Document,InvoiceAccount,Opportunity,OpportunityName,OrderAccount,OrderAccountCode,OrderAccountContact,OrderAccountContactFullName,OrderAccountName,PaymentCondition,PaymentConditionDescription,Currency,ProjectCode,ProjectDescription,QuotationDate";

//         var encodedFilter = Uri.EscapeDataString(filter);
//         var encodedSelect = Uri.EscapeDataString(select);

//         var url = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Quotations" +
//                   $"?$filter={encodedFilter}" +
//                   $"&$select={encodedSelect}" +
//                   $"&$top={pageSize}";

//         _logger.LogInformation($"📡 API URL: {url}");

//         var response = await _httpClient.GetAsync(url);

//         if (!response.IsSuccessStatusCode)
//         {
//             _logger.LogError($"❌ API Hatası: {response.StatusCode}");
//             var errorContent = await response.Content.ReadAsStringAsync();
//             _logger.LogError($"❌ Hata Detayı: {errorContent}");
//             return null;
//         }

//         var json = await response.Content.ReadAsStringAsync();
//         var quotationResponse = JsonSerializer.Deserialize<QuotationResponse>(json, new JsonSerializerOptions
//         {
//             PropertyNameCaseInsensitive = true
//         });

//         // API'den gelen ham JSON'u işle
//         return quotationResponse;
//     }

//     // Quotation raporu - tarih aralığında
//     public async Task<string> GetQuotationReportAsync(DateTime startDate, DateTime endDate)
//     {
//         var exactService = _serviceProvider.GetRequiredService<ExactService>();
//         var token = await exactService.GetValidToken();

//         if (token == null)
//         {
//             _logger.LogError("❌ Geçerli bir token alınamadı");
//             return null;
//         }

//         _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
//         _httpClient.DefaultRequestHeaders.Accept.Clear();
//         _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

//         var startDateStr = startDate.ToString("yyyy-MM-dd");
//         var endDateStr = endDate.ToString("yyyy-MM-dd");
//         int pageSize = 60;

//         var filter = $"Created ge datetime'{startDateStr}' and Created le datetime'{endDateStr}'";
//         var select = "QuotationID,QuotationNumber,Created,Status,OrderAccountName,AmountFC,AmountDC,CloseDate,CreatorFullName,DeliveryAccount,DeliveryAccountCode,DeliveryAccountContact,DeliveryAccountContactFullName,DeliveryAccountName,Project,DeliveryAddress,Description,ClosingDate,DeliveryDate,DocumentSubject,InvoiceAccountCode,DueDate,Document,InvoiceAccount,Opportunity,OpportunityName,OrderAccount,OrderAccountCode,OrderAccountContact,OrderAccountContactFullName,OrderAccountName,PaymentCondition,PaymentConditionDescription,Currency,ProjectCode,ProjectDescription,QuotationDate";

//         var encodedFilter = Uri.EscapeDataString(filter);
//         var encodedSelect = Uri.EscapeDataString(select);

//         var url = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Quotations" +
//                   $"?$filter={encodedFilter}" +
//                   $"&$select={encodedSelect}" +
//                   $"&$top={pageSize}";

//         _logger.LogInformation($"📡 API URL: {url}");

//         var response = await _httpClient.GetAsync(url);

//         if (!response.IsSuccessStatusCode)
//         {
//             _logger.LogError($"❌ API Hatası: {response.StatusCode}");
//             var errorContent = await response.Content.ReadAsStringAsync();
//             _logger.LogError($"❌ Hata Detayı: {errorContent}");
//             return "[]";
//         }

//         var json = await response.Content.ReadAsStringAsync();
//         return json;
//     }

//     // ⚡ OPTİMİZED: QuotationLines'ı fetch et (cache ile)
//     private async Task<string> GetQuotationLinesAsync(string quotationLinesUri, string token)
//     {
//         try
//         {
//             // ⚡ Cache kontrol et
//             if (_quotationLinesCache.TryGetValue(quotationLinesUri, out var cachedResult))
//             {
//                 _logger.LogDebug($"💾 Cache HIT: {quotationLinesUri}");
//                 return cachedResult;
//             }

//             _logger.LogDebug($"📥 QuotationLines URI çağrılıyor: {quotationLinesUri}");

//             _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
//             _httpClient.DefaultRequestHeaders.Accept.Clear();
//             _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

//             var response = await _httpClient.GetAsync(quotationLinesUri);

//             if (!response.IsSuccessStatusCode)
//             {
//                 var errorContent = await response.Content.ReadAsStringAsync();
//                 _logger.LogError($"❌ QuotationLines Fetch Hatası: {response.StatusCode}");
//                 _logger.LogError($"   Hata Detayı: {errorContent}");
//                 return "{\"d\":{\"results\":[]}}";  // Empty result
//             }

//             var content = await response.Content.ReadAsStringAsync();
            
//             // ⚡ Cache'e ekle
//             _quotationLinesCache.TryAdd(quotationLinesUri, content);
            
//             _logger.LogDebug($"✅ QuotationLines alındı. Boyut: {content.Length} bytes");
//             return content;
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError($"❌ QuotationLines Exception: {ex.Message}");
//             _logger.LogError($"   Stack Trace: {ex.StackTrace}");
//             return "{\"d\":{\"results\":[]}}";  // Empty result
//         }
//     }

//     // ⚡ OPTİMİZED: En çok teklif verilen ürünleri getir (Paralel işleme)
//     public async Task<List<TopProductDTO>> GetTopQuotedProductsAsync(DateTime startDate, DateTime endDate, int topCount = 10, ReportFilterModel filter = null)
//     {
//         var stopwatch = System.Diagnostics.Stopwatch.StartNew();

//         var exactService = _serviceProvider.GetRequiredService<ExactService>();
//         var token = await exactService.GetValidToken();

//         if (token == null)
//         {
//             _logger.LogError("❌ Geçerli bir token alınamadı");
//             return new List<TopProductDTO>();
//         }

//         var quotationJson = await GetQuotationReportAsync(startDate, endDate);
//         var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

//         var quotationResponse = JsonSerializer.Deserialize<QuotationResponse>(quotationJson, options);
//         var quotations = quotationResponse?.GetQuotations();

//         if (quotations == null || quotations.Count == 0)
//         {
//             _logger.LogWarning("⚠️ Quotation bulunamadı");
//             return new List<TopProductDTO>();
//         }

//         _logger.LogInformation($"📥 {quotations.Count} quotation bulundu. QuotationLines alınıyor...");

//         // ⚡ Paralel olarak tüm QuotationLines'ı al
//         var allLines = await FetchAllQuotationLinesParallelAsync(quotations, token.access_token);

//         _logger.LogInformation($"✅ {allLines.Count} satır bulundu. İşleniyor...");

//         // ⚡ Verileri verimli şekilde işle
//         var productCounts = new Dictionary<string, ProductInfo>(StringComparer.OrdinalIgnoreCase);

//         foreach (var line in allLines)
//         {
//             // Item tanımlaması: ItemCode varsa kullan, yoksa Item GUID'ini kullan
//             var itemKey = !string.IsNullOrWhiteSpace(line.ItemCode)
//                 ? line.ItemCode
//                 : (line.Item ?? line.ID);

//             // Ürün açıklaması: ItemDescription varsa kullan, yoksa Description'ı kullan
//             var itemDescription = !string.IsNullOrWhiteSpace(line.ItemDescription)
//                 ? line.ItemDescription
//                 : line.Description;
            
//             var itemImage = "";


//             if (!productCounts.ContainsKey(itemKey))
//             {
//                 productCounts[itemKey] = new ProductInfo
//                 {
//                     ItemCode = itemKey,
//                     path = itemImage,
//                     ItemDescription = itemDescription,
//                     Quantity = 0,
//                     TotalAmount = 0,
//                     QuotationIds = new HashSet<string>()
//                 };
//             }

//             productCounts[itemKey].Quantity += line.Quantity ?? 0;
//             productCounts[itemKey].TotalAmount += line.AmountFC ?? 0;
//             productCounts[itemKey].QuotationIds.Add(line.QuotationID);

//             _logger.LogDebug($"📦 Ürün eklendi: {itemDescription} - Miktar: {line.Quantity}, Tutar: {line.AmountFC}");
//         }

//         // ⚡ QuotationCount'ı güncelle (benzersiz quotation sayısı)
//         foreach (var product in productCounts.Values)
//         {
//             product.QuotationCount = product.QuotationIds.Count;
//         }

//         // Apply filters
//         var filteredProducts = productCounts.Values.AsEnumerable();

//         if (filter != null)
//         {
//             if (filter.ProductCodes != null && filter.ProductCodes.Any())
//             {
//                 var codes = new HashSet<string>(filter.ProductCodes, StringComparer.OrdinalIgnoreCase);
//                 filteredProducts = filteredProducts.Where(p => codes.Contains(p.ItemCode));
//             }

//             if (!string.IsNullOrEmpty(filter.SearchTerm))
//             {
//                 var searchLower = filter.SearchTerm.ToLowerInvariant();
//                 filteredProducts = filteredProducts.Where(p =>
//                     p.ItemDescription?.ToLowerInvariant().Contains(searchLower) == true ||
//                     p.ItemCode?.ToLowerInvariant().Contains(searchLower) == true);
//             }

//             if (filter.MinAmount.HasValue)
//             {
//                 filteredProducts = filteredProducts.Where(p => p.TotalAmount >= (decimal)filter.MinAmount.Value);
//             }

//             if (filter.MaxAmount.HasValue)
//             {
//                 filteredProducts = filteredProducts.Where(p => p.TotalAmount <= (decimal)filter.MaxAmount.Value);
//             }
//         }

//         // En çok teklif verilen ürünleri sırala
//         var topProducts = filteredProducts
//             .OrderByDescending(p => p.QuotationCount)
//             .ThenByDescending(p => p.Quantity)
//             .Take(topCount)
//             .Select((p, index) => new TopProductDTO
//             {
//                 Rank = index + 1,
//                 ItemCode = p.ItemCode,
//                 ItemDescription = p.ItemDescription,
//                 QuotationCount = p.QuotationCount,
//                 TotalQuantity = p.Quantity,
//                 TotalAmount = p.TotalAmount
//             })
//             .ToList();

//         stopwatch.Stop();
//         _logger.LogInformation($"✅ {topProducts.Count} ürün bulundu ({stopwatch.ElapsedMilliseconds}ms)");
//         return topProducts;
//     }

//     // ⚡ OPTİMİZED: Paralel QuotationLines Fetch
//     private async Task<List<QuotationLine>> FetchAllQuotationLinesParallelAsync(List<Quotation> quotations, string token)
//     {
//         var allLines = new ConcurrentBag<QuotationLine>();
//         var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

//         // ⚡ Paralel işlem sınırı (API rate limit'ine uygun)
//         var maxDegreeOfParallelism = 5;
//         var semaphore = new SemaphoreSlim(maxDegreeOfParallelism, maxDegreeOfParallelism);
//         var tasks = new List<Task>();

//         foreach (var quotation in quotations)
//         {
//             await semaphore.WaitAsync();
//             tasks.Add(Task.Run(async () =>
//             {
//                 try
//                 {
//                     var linesUri = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Quotations(guid'{quotation.QuotationID}')/QuotationLines";
//                     var linesJson = await GetQuotationLinesAsync(linesUri, token);

//                     var linesResponse = JsonSerializer.Deserialize<QuotationLineResponse>(linesJson, options);
//                     var lines = linesResponse?.GetLines();

//                     if (lines != null && lines.Count > 0)
//                     {
//                         foreach (var line in lines)
//                             allLines.Add(line);
//                     }

//                     _logger.LogDebug($"✅ {quotation.QuotationID}: {lines?.Count ?? 0} satır alındı");
//                 }
//                 finally
//                 {
//                     semaphore.Release();
//                 }
//             }));
//         }

//         await Task.WhenAll(tasks);
//         return allLines.ToList();
//     }

//     // ⚡ OPTİMİZED: En çok teklif verilen müşterileri getir (Verimli gruplama)
//     public async Task<List<TopCustomerDTO>> GetTopQuotedCustomersAsync(DateTime startDate, DateTime endDate, int topCount = 10, ReportFilterModel filter = null)
//     {
//         var stopwatch = System.Diagnostics.Stopwatch.StartNew();

//         var quotationJson = await GetQuotationReportAsync(startDate, endDate);
//         var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

//         var quotationResponse = JsonSerializer.Deserialize<QuotationResponse>(quotationJson, options);
//         var quotations = quotationResponse?.GetQuotations();

//         if (quotations == null || quotations.Count == 0)
//         {
//             _logger.LogWarning("⚠️ Quotation bulunamadı");
//             return new List<TopCustomerDTO>();
//         }

//         var customerCounts = new Dictionary<string, CustomerInfo>();

//         // ⚡ Tek pass'te grupla
//         foreach (var quotation in quotations)
//         {
//             var customerKey = quotation.DeliveryAccountName ?? quotation.OrderAccountName ?? "Unknown";

//             if (!customerCounts.ContainsKey(customerKey))
//             {
//                 customerCounts[customerKey] = new CustomerInfo
//                 {
//                     CustomerName = customerKey,
//                     AccountCode = quotation.DeliveryAccountCode ?? quotation.OrderAccountCode,
//                     TotalAmount = 0,
//                     QuotationIds = new HashSet<string>()
//                 };
//             }

//             customerCounts[customerKey].QuotationIds.Add(quotation.QuotationID);
//             customerCounts[customerKey].TotalAmount += quotation.AmountFC ?? 0;
//             customerCounts[customerKey].Currency = quotation.Currency;
//         }

//         // ⚡ QuotationCount'ı güncelle
//         foreach (var customer in customerCounts.Values)
//         {
//             customer.QuotationCount = customer.QuotationIds.Count;
//         }

//         // Apply filters
//         var filteredCustomers = customerCounts.Values.AsEnumerable();

//         if (filter != null)
//         {
//             if (filter.CustomerNames != null && filter.CustomerNames.Any())
//             {
//                 var names = new HashSet<string>(filter.CustomerNames, StringComparer.OrdinalIgnoreCase);
//                 filteredCustomers = filteredCustomers.Where(c => names.Contains(c.CustomerName));
//             }

//             if (!string.IsNullOrEmpty(filter.SearchTerm))
//             {
//                 var searchLower = filter.SearchTerm.ToLowerInvariant();
//                 filteredCustomers = filteredCustomers.Where(c => 
//                     c.CustomerName?.ToLowerInvariant().Contains(searchLower) == true);
//             }

//             if (filter.MinAmount.HasValue)
//             {
//                 filteredCustomers = filteredCustomers.Where(c => c.TotalAmount >= (decimal)filter.MinAmount.Value);
//             }

//             if (filter.MaxAmount.HasValue)
//             {
//                 filteredCustomers = filteredCustomers.Where(c => c.TotalAmount <= (decimal)filter.MaxAmount.Value);
//             }
//         }

//         // En çok teklif verilen müşterileri sırala
//         var topCustomers = filteredCustomers
//             .OrderByDescending(c => c.QuotationCount)
//             .ThenByDescending(c => c.TotalAmount)
//             .Take(topCount)
//             .Select((c, index) => new TopCustomerDTO
//             {
//                 Rank = index + 1,
//                 CustomerName = c.CustomerName,
//                 AccountCode = c.AccountCode,
//                 QuotationCount = c.QuotationCount,
//                 TotalAmount = c.TotalAmount,
//                 Currency = c.Currency
//             })
//             .ToList();

//         stopwatch.Stop();
//         _logger.LogInformation($"✅ {topCustomers.Count} müşteri bulundu ({stopwatch.ElapsedMilliseconds}ms)");
//         return topCustomers;
//     }

//     // İki tarih aralığında ürünleri karşılaştır
//     public async Task<ComparisonProductResultDTO> CompareProductsByDateRangeAsync(
//         DateTime startDate1, DateTime endDate1,
//         DateTime startDate2, DateTime endDate2,
//         int topCount = 10,
//         ReportFilterModel filter = null)
//     {
//         var stopwatch = System.Diagnostics.Stopwatch.StartNew();

//         _logger.LogInformation($"📊 Ürün karşılaştırması başlıyor: Period1({startDate1:yyyy-MM-dd} - {endDate1:yyyy-MM-dd}) vs Period2({startDate2:yyyy-MM-dd} - {endDate2:yyyy-MM-dd})");

//         // ⚡ İki periyodu PARALEL olarak işle
//         var period1Task = GetTopQuotedProductsAsync(startDate1, endDate1, topCount * 2, filter);
//         var period2Task = GetTopQuotedProductsAsync(startDate2, endDate2, topCount * 2, filter);

//         await Task.WhenAll(period1Task, period2Task);

//         var period1Products = period1Task.Result;
//         var period2Products = period2Task.Result;

//         var result = new ComparisonProductResultDTO
//         {
//             Period1 = new PeriodDTO { StartDate = startDate1, EndDate = endDate1 },
//             Period2 = new PeriodDTO { StartDate = startDate2, EndDate = endDate2 },
//             ComparisonProducts = new List<ProductComparisonDTO>()
//         };

//         // ⚡ Dictionary'e dönüştür (lookup hızı için O(1))
//         var period1Dict = period1Products.ToDictionary(p => p.ItemCode, p => p);
//         var period2Dict = period2Products.ToDictionary(p => p.ItemCode, p => p);

//         // Tüm ürün keyleri topla
//         var allProductKeys = new HashSet<string>();
//         foreach (var product in period1Products)
//             allProductKeys.Add(product.ItemCode);
//         foreach (var product in period2Products)
//             allProductKeys.Add(product.ItemCode);

//         // Her ürün için karşılaştırma yap
//         var comparisons = new List<ProductComparisonDTO>();

//         foreach (var key in allProductKeys)
//         {
//             period1Dict.TryGetValue(key, out var p1);
//             period2Dict.TryGetValue(key, out var p2);

//             var comparison = new ProductComparisonDTO
//             {
//                 ItemCode = key,
//                 ItemDescription = p1?.ItemDescription ?? p2?.ItemDescription,
//                 Period1QuotationCount = p1?.QuotationCount ?? 0,
//                 Period2QuotationCount = p2?.QuotationCount ?? 0,
//                 QuotationCountChange = (p2?.QuotationCount ?? 0) - (p1?.QuotationCount ?? 0),
//                 Period1TotalAmount = p1?.TotalAmount ?? 0,
//                 Period2TotalAmount = p2?.TotalAmount ?? 0,
//                 TotalAmountChange = (p2?.TotalAmount ?? 0) - (p1?.TotalAmount ?? 0),
//                 Period1TotalQuantity = p1?.TotalQuantity ?? 0,
//                 Period2TotalQuantity = p2?.TotalQuantity ?? 0,
//                 QuantityChange = (p2?.TotalQuantity ?? 0) - (p1?.TotalQuantity ?? 0),
//                 ChangePercentage = p1?.QuotationCount > 0
//                     ? ((((p2?.QuotationCount ?? 0) - (p1?.QuotationCount ?? 0)) * 100m) / (p1?.QuotationCount ?? 1))
//                     : (p2?.QuotationCount > 0 ? 100 : 0)
//             };

//             comparisons.Add(comparison);
//         }

//         // Değişime göre sırala
//         result.ComparisonProducts = comparisons
//             .OrderByDescending(c => Math.Abs(c.QuotationCountChange))
//             .ThenByDescending(c => c.Period2QuotationCount)
//             .Take(topCount)
//             .Select((c, index) =>
//             {
//                 c.Rank = index + 1;
//                 return c;
//             })
//             .ToList();

//         stopwatch.Stop();
//         _logger.LogInformation($"✅ {result.ComparisonProducts.Count} ürün karşılaştırıldı ({stopwatch.ElapsedMilliseconds}ms)");
//         return result;
//     }

//     // İki tarih aralığında ürünleri karşılaştır - Detaylı Versiyon
//     public async Task<DetailedProductComparisonResponseDTO> CompareProductsByDateRangeDetailedAsync(
//         DateTime startDate1, DateTime endDate1,
//         DateTime startDate2, DateTime endDate2,
//         int topCount = 10,
//         ReportFilterModel filter = null)
//     {
//         var stopwatch = System.Diagnostics.Stopwatch.StartNew();

//         _logger.LogInformation($"📊 Detaylı ürün karşılaştırması başlıyor: Period1({startDate1:yyyy-MM-dd} - {endDate1:yyyy-MM-dd}) vs Period2({startDate2:yyyy-MM-dd} - {endDate2:yyyy-MM-dd})");

//         // ⚡ Paralel işle
//         var period1Task = GetTopQuotedProductsAsync(startDate1, endDate1, topCount, filter);
//         var period2Task = GetTopQuotedProductsAsync(startDate2, endDate2, topCount, filter);

//         await Task.WhenAll(period1Task, period2Task);

//         var period1Products = period1Task.Result;
//         var period2Products = period2Task.Result;

//         // Calculate totals
//         var currentAmount = period1Products.Sum(p => p.TotalAmount);
//         var previousAmount = period2Products.Sum(p => p.TotalAmount);
//         var currentQuantity = period1Products.Sum(p => p.TotalQuantity);
//         var previousQuantity = period2Products.Sum(p => p.TotalQuantity);
//         var currentQuotationCount = period1Products.Sum(p => p.QuotationCount);
//         var previousQuotationCount = period2Products.Sum(p => p.QuotationCount);

//         // Calculate differences
//         var amountDifference = currentAmount - previousAmount;
//         var amountDifferencePercent = previousAmount > 0 ? (amountDifference / previousAmount) * 100 : (currentAmount > 0 ? 100 : 0);
//         var quantityDifference = currentQuantity - previousQuantity;
//         var quantityDifferencePercent = previousQuantity > 0 ? (quantityDifference / previousQuantity) * 100 : (currentQuantity > 0 ? 100 : 0);
//         var quotationDifference = currentQuotationCount - previousQuotationCount;
//         var quotationDifferencePercent = previousQuotationCount > 0 ? ((decimal)quotationDifference / previousQuotationCount) * 100 : (currentQuotationCount > 0 ? 100 : 0);

//         // Product count comparison
//         var productDifference = period1Products.Count - period2Products.Count;
//         var productDifferencePercent = period2Products.Count > 0 ? ((decimal)productDifference / period2Products.Count) * 100 : (period1Products.Count > 0 ? 100 : 0);

//         // Average unit prices
//         var currentAvgPrice = currentQuantity > 0 ? currentAmount / currentQuantity : 0;
//         var previousAvgPrice = previousQuantity > 0 ? previousAmount / previousQuantity : 0;

//         // Create detailed top products
//         var currentTopProducts = period1Products.Select((p, index) => new DetailedTopProductDTO
//         {
//             Rank = index + 1,
//             ItemCode = p.ItemCode,
//             ItemDescription = p.ItemDescription,
//             TotalQuantity = p.TotalQuantity,
//             TotalAmount = p.TotalAmount,
//             UnitPrice = p.TotalQuantity > 0 ? p.TotalAmount / p.TotalQuantity : 0,
//             QuotationCount = p.QuotationCount,
//             AverageQuantityPerQuotation = p.QuotationCount > 0 ? p.TotalQuantity / p.QuotationCount : 0
//         }).ToList();

//         var previousTopProducts = period2Products.Select((p, index) => new DetailedTopProductDTO
//         {
//             Rank = index + 1,
//             ItemCode = p.ItemCode,
//             ItemDescription = p.ItemDescription,
//             TotalQuantity = p.TotalQuantity,
//             TotalAmount = p.TotalAmount,
//             UnitPrice = p.TotalQuantity > 0 ? p.TotalAmount / p.TotalQuantity : 0,
//             QuotationCount = p.QuotationCount,
//             AverageQuantityPerQuotation = p.QuotationCount > 0 ? p.TotalQuantity / p.QuotationCount : 0
//         }).ToList();

//         // ⚡ Dictionary lookup ile hızlandır
//         var period1Dict = period1Products.ToDictionary(p => p.ItemCode, p => p);
//         var period2Dict = period2Products.ToDictionary(p => p.ItemCode, p => p);

//         // Create product comparisons
//         var allProductKeys = new HashSet<string>();
//         foreach (var product in period1Products) allProductKeys.Add(product.ItemCode);
//         foreach (var product in period2Products) allProductKeys.Add(product.ItemCode);

//         var productComparisons = new List<DetailedProductComparisonDTO>();
//         foreach (var key in allProductKeys)
//         {
//             period1Dict.TryGetValue(key, out var p1);
//             period2Dict.TryGetValue(key, out var p2);

//             var comparison = new DetailedProductComparisonDTO
//             {
//                 ItemCode = key,
//                 ItemDescription = p1?.ItemDescription ?? p2?.ItemDescription,
//                 CurrentRank = p1?.Rank,
//                 CurrentQuantity = p1?.TotalQuantity ?? 0,
//                 CurrentAmount = p1?.TotalAmount ?? 0,
//                 CurrentPercentage = currentQuantity > 0 ? ((p1?.TotalQuantity ?? 0) / currentQuantity) * 100 : 0,
//                 PreviousRank = p2?.Rank,
//                 PreviousQuantity = p2?.TotalQuantity ?? 0,
//                 PreviousAmount = p2?.TotalAmount ?? 0,
//                 PreviousPercentage = previousQuantity > 0 ? ((p2?.TotalQuantity ?? 0) / previousQuantity) * 100 : 0,
//                 RankChange = (p1 != null && p2 != null) ? p2.Rank - p1.Rank : null,
//                 QuantityChange = (p1?.TotalQuantity ?? 0) - (p2?.TotalQuantity ?? 0),
//                 AmountChange = (p1?.TotalAmount ?? 0) - (p2?.TotalAmount ?? 0),
//                 AmountChangePercent = (p2?.TotalAmount ?? 0) > 0
//                     ? (((p1?.TotalAmount ?? 0) - (p2?.TotalAmount ?? 0)) / (p2?.TotalAmount ?? 1)) * 100
//                     : ((p1?.TotalAmount ?? 0) > 0 ? 100 : 0),
//                 Status = GetProductStatus(p1, p2)
//             };

//             productComparisons.Add(comparison);
//         }

//         var result = new DetailedProductComparisonResponseDTO
//         {
//             Success = true,
//             Message = "✅ Tarih aralığı karşılaştırması başarılı",
//             CurrentPeriod = $"{startDate1:yyyy-MM-dd} to {endDate1:yyyy-MM-dd}",
//             PreviousPeriod = $"{startDate2:yyyy-MM-dd} to {endDate2:yyyy-MM-dd}",
//             CurrentAmount = currentAmount,
//             PreviousAmount = previousAmount,
//             AmountDifference = amountDifference,
//             AmountDifferencePercent = amountDifferencePercent,
//             AmountTrend = GetTrend(amountDifferencePercent),
//             CurrentQuantity = currentQuantity,
//             PreviousQuantity = previousQuantity,
//             QuantityDifference = quantityDifference,
//             QuantityDifferencePercent = quantityDifferencePercent,
//             QuantityTrend = GetTrend(quantityDifferencePercent),
//             CurrentProductCount = period1Products.Count,
//             PreviousProductCount = period2Products.Count,
//             ProductDifference = productDifference,
//             ProductDifferencePercent = productDifferencePercent,
//             ProductTrend = GetTrend(productDifferencePercent),
//             CurrentQuotationCount = currentQuotationCount,
//             PreviousQuotationCount = previousQuotationCount,
//             QuotationDifference = quotationDifference,
//             QuotationDifferencePercent = quotationDifferencePercent,
//             QuotationTrend = GetTrend(quotationDifferencePercent),
//             CurrentAverageUnitPrice = currentAvgPrice,
//             PreviousAverageUnitPrice = previousAvgPrice,
//             AverageUnitPriceDifference = currentAvgPrice - previousAvgPrice,
//             CurrentTopProducts = currentTopProducts,
//             PreviousTopProducts = previousTopProducts,
//             ProductComparisons = productComparisons
//         };

//         stopwatch.Stop();
//         _logger.LogInformation($"✅ Detaylı karşılaştırma tamamlandı ({stopwatch.ElapsedMilliseconds}ms)");
//         return result;
//     }

//     // İki tarih aralığında müşterileri karşılaştır
//     public async Task<ComparisonCustomerResultDTO> CompareCustomersByDateRangeAsync(
//         DateTime startDate1, DateTime endDate1,
//         DateTime startDate2, DateTime endDate2,
//         int topCount = 10,
//         ReportFilterModel filter = null)
//     {
//         var stopwatch = System.Diagnostics.Stopwatch.StartNew();

//         _logger.LogInformation($"📊 Müşteri karşılaştırması başlıyor: Period1({startDate1:yyyy-MM-dd} - {endDate1:yyyy-MM-dd}) vs Period2({startDate2:yyyy-MM-dd} - {endDate2:yyyy-MM-dd})");

//         // ⚡ Paralel işle
//         var period1Task = GetTopQuotedCustomersAsync(startDate1, endDate1, topCount * 2, filter);
//         var period2Task = GetTopQuotedCustomersAsync(startDate2, endDate2, topCount * 2, filter);

//         await Task.WhenAll(period1Task, period2Task);

//         var period1Customers = period1Task.Result;
//         var period2Customers = period2Task.Result;

//         var result = new ComparisonCustomerResultDTO
//         {
//             Period1 = new PeriodDTO { StartDate = startDate1, EndDate = endDate1 },
//             Period2 = new PeriodDTO { StartDate = startDate2, EndDate = endDate2 },
//             ComparisonCustomers = new List<CustomerComparisonDTO>()
//         };

//         // ⚡ Dictionary'e dönüştür (lookup hızı için)
//         var period1Dict = period1Customers.ToDictionary(c => c.CustomerName, c => c);
//         var period2Dict = period2Customers.ToDictionary(c => c.CustomerName, c => c);

//         // Tüm müşteri keyleri topla
//         var allCustomerNames = new HashSet<string>();
//         foreach (var customer in period1Customers)
//             allCustomerNames.Add(customer.CustomerName);
//         foreach (var customer in period2Customers)
//             allCustomerNames.Add(customer.CustomerName);

//         // Her müşteri için karşılaştırma yap
//         var comparisons = new List<CustomerComparisonDTO>();

//         foreach (var name in allCustomerNames)
//         {
//             period1Dict.TryGetValue(name, out var c1);
//             period2Dict.TryGetValue(name, out var c2);

//             var comparison = new CustomerComparisonDTO
//             {
//                 CustomerName = name,
//                 AccountCode = c1?.AccountCode ?? c2?.AccountCode,
//                 Period1QuotationCount = c1?.QuotationCount ?? 0,
//                 Period2QuotationCount = c2?.QuotationCount ?? 0,
//                 QuotationCountChange = (c2?.QuotationCount ?? 0) - (c1?.QuotationCount ?? 0),
//                 Period1TotalAmount = c1?.TotalAmount ?? 0,
//                 Period2TotalAmount = c2?.TotalAmount ?? 0,
//                 TotalAmountChange = (c2?.TotalAmount ?? 0) - (c1?.TotalAmount ?? 0),
//                 Currency = c1?.Currency ?? c2?.Currency,
//                 ChangePercentage = c1?.QuotationCount > 0
//                     ? ((((c2?.QuotationCount ?? 0) - (c1?.QuotationCount ?? 0)) * 100m) / (c1?.QuotationCount ?? 1))
//                     : (c2?.QuotationCount > 0 ? 100 : 0)
//             };

//             comparisons.Add(comparison);
//         }

//         // Değişime göre sırala
//         result.ComparisonCustomers = comparisons
//             .OrderByDescending(c => Math.Abs(c.QuotationCountChange))
//             .ThenByDescending(c => c.Period2QuotationCount)
//             .Take(topCount)
//             .Select((c, index) =>
//             {
//                 c.Rank = index + 1;
//                 return c;
//             })
//             .ToList();

//         stopwatch.Stop();
//         _logger.LogInformation($"✅ {result.ComparisonCustomers.Count} müşteri karşılaştırıldı ({stopwatch.ElapsedMilliseconds}ms)");
//         return result;
//     }

//     // İki tarih aralığında müşterileri karşılaştır - Detaylı Versiyon
//     public async Task<DetailedCustomerComparisonResponseDTO> CompareCustomersByDateRangeDetailedAsync(
//         DateTime startDate1, DateTime endDate1,
//         DateTime startDate2, DateTime endDate2,
//         int topCount = 10,
//         ReportFilterModel filter = null)
//     {
//         var stopwatch = System.Diagnostics.Stopwatch.StartNew();

//         _logger.LogInformation($"📊 Detaylı müşteri karşılaştırması başlıyor: Period1({startDate1:yyyy-MM-dd} - {endDate1:yyyy-MM-dd}) vs Period2({startDate2:yyyy-MM-dd} - {endDate2:yyyy-MM-dd})");

//         // ⚡ Paralel işle
//         var period1Task = GetTopQuotedCustomersAsync(startDate1, endDate1, topCount, filter);
//         var period2Task = GetTopQuotedCustomersAsync(startDate2, endDate2, topCount, filter);

//         await Task.WhenAll(period1Task, period2Task);

//         var period1Customers = period1Task.Result;
//         var period2Customers = period2Task.Result;

//         // Calculate totals
//         var currentAmount = period1Customers.Sum(c => c.TotalAmount);
//         var previousAmount = period2Customers.Sum(c => c.TotalAmount);
//         var currentQuotationCount = period1Customers.Sum(c => c.QuotationCount);
//         var previousQuotationCount = period2Customers.Sum(c => c.QuotationCount);

//         // Calculate differences
//         var amountDifference = currentAmount - previousAmount;
//         var amountDifferencePercent = previousAmount > 0 ? (amountDifference / previousAmount) * 100 : (currentAmount > 0 ? 100 : 0);
//         var quotationDifference = currentQuotationCount - previousQuotationCount;
//         var quotationDifferencePercent = previousQuotationCount > 0 ? ((decimal)quotationDifference / previousQuotationCount) * 100 : (currentQuotationCount > 0 ? 100 : 0);

//         // Customer count comparison
//         var customerDifference = period1Customers.Count - period2Customers.Count;
//         var customerDifferencePercent = period2Customers.Count > 0 ? ((decimal)customerDifference / period2Customers.Count) * 100 : (period1Customers.Count > 0 ? 100 : 0);

//         // Average quotation amounts
//         var currentAvgQuotationAmount = currentQuotationCount > 0 ? currentAmount / currentQuotationCount : 0;
//         var previousAvgQuotationAmount = previousQuotationCount > 0 ? previousAmount / previousQuotationCount : 0;

//         // Create detailed top customers
//         var currentTopCustomers = period1Customers.Select((c, index) => new DetailedTopCustomerDTO
//         {
//             Rank = index + 1,
//             CustomerName = c.CustomerName,
//             AccountCode = c.AccountCode,
//             TotalAmount = c.TotalAmount,
//             QuotationCount = c.QuotationCount,
//             AverageQuotationAmount = c.QuotationCount > 0 ? c.TotalAmount / c.QuotationCount : 0,
//             Currency = c.Currency
//         }).ToList();

//         var previousTopCustomers = period2Customers.Select((c, index) => new DetailedTopCustomerDTO
//         {
//             Rank = index + 1,
//             CustomerName = c.CustomerName,
//             AccountCode = c.AccountCode,
//             TotalAmount = c.TotalAmount,
//             QuotationCount = c.QuotationCount,
//             AverageQuotationAmount = c.QuotationCount > 0 ? c.TotalAmount / c.QuotationCount : 0,
//             Currency = c.Currency
//         }).ToList();

//         // ⚡ Dictionary lookup ile hızlandır
//         var period1Dict = period1Customers.ToDictionary(c => c.CustomerName, c => c);
//         var period2Dict = period2Customers.ToDictionary(c => c.CustomerName, c => c);

//         // Create customer comparisons
//         var allCustomerNames = new HashSet<string>();
//         foreach (var customer in period1Customers) allCustomerNames.Add(customer.CustomerName);
//         foreach (var customer in period2Customers) allCustomerNames.Add(customer.CustomerName);

//         var customerComparisons = new List<DetailedCustomerComparisonDTO>();
//         foreach (var name in allCustomerNames)
//         {
//             period1Dict.TryGetValue(name, out var c1);
//             period2Dict.TryGetValue(name, out var c2);

//             var comparison = new DetailedCustomerComparisonDTO
//             {
//                 CustomerName = name,
//                 AccountCode = c1?.AccountCode ?? c2?.AccountCode,
//                 CurrentRank = c1?.Rank,
//                 CurrentQuotationCount = c1?.QuotationCount ?? 0,
//                 CurrentAmount = c1?.TotalAmount ?? 0,
//                 CurrentPercentage = currentAmount > 0 ? ((c1?.TotalAmount ?? 0) / currentAmount) * 100 : 0,
//                 PreviousRank = c2?.Rank,
//                 PreviousQuotationCount = c2?.QuotationCount ?? 0,
//                 PreviousAmount = c2?.TotalAmount ?? 0,
//                 PreviousPercentage = previousAmount > 0 ? ((c2?.TotalAmount ?? 0) / previousAmount) * 100 : 0,
//                 RankChange = (c1 != null && c2 != null) ? c2.Rank - c1.Rank : null,
//                 QuotationCountChange = (c1?.QuotationCount ?? 0) - (c2?.QuotationCount ?? 0),
//                 AmountChange = (c1?.TotalAmount ?? 0) - (c2?.TotalAmount ?? 0),
//                 AmountChangePercent = (c2?.TotalAmount ?? 0) > 0
//                     ? (((c1?.TotalAmount ?? 0) - (c2?.TotalAmount ?? 0)) / (c2?.TotalAmount ?? 1)) * 100
//                     : ((c1?.TotalAmount ?? 0) > 0 ? 100 : 0),
//                 Status = GetCustomerStatus(c1, c2)
//             };

//             customerComparisons.Add(comparison);
//         }

//         var result = new DetailedCustomerComparisonResponseDTO
//         {
//             Success = true,
//             Message = "✅ Tarih aralığı karşılaştırması başarılı",
//             CurrentPeriod = $"{startDate1:yyyy-MM-dd} to {endDate1:yyyy-MM-dd}",
//             PreviousPeriod = $"{startDate2:yyyy-MM-dd} to {endDate2:yyyy-MM-dd}",
//             CurrentAmount = currentAmount,
//             PreviousAmount = previousAmount,
//             AmountDifference = amountDifference,
//             AmountDifferencePercent = amountDifferencePercent,
//             AmountTrend = GetTrend(amountDifferencePercent),
//             CurrentQuotationCount = currentQuotationCount,
//             PreviousQuotationCount = previousQuotationCount,
//             QuotationDifference = quotationDifference,
//             QuotationDifferencePercent = quotationDifferencePercent,
//             QuotationTrend = GetTrend(quotationDifferencePercent),
//             CurrentCustomerCount = period1Customers.Count,
//             PreviousCustomerCount = period2Customers.Count,
//             CustomerDifference = customerDifference,
//             CustomerDifferencePercent = customerDifferencePercent,
//             CustomerTrend = GetTrend(customerDifferencePercent),
//             CurrentAverageQuotationAmount = currentAvgQuotationAmount,
//             PreviousAverageQuotationAmount = previousAvgQuotationAmount,
//             AverageQuotationAmountDifference = currentAvgQuotationAmount - previousAvgQuotationAmount,
//             CurrentTopCustomers = currentTopCustomers,
//             PreviousTopCustomers = previousTopCustomers,
//             CustomerComparisons = customerComparisons
//         };

//         stopwatch.Stop();
//         _logger.LogInformation($"✅ Detaylı karşılaştırma tamamlandı ({stopwatch.ElapsedMilliseconds}ms)");
//         return result;
//     }

//     // Helper: Trend hesaplama
//     private string GetTrend(decimal percentChange)
//     {
//         if (percentChange > 50) return "📈 Güçlü Artış";
//         if (percentChange > 10) return "📈 Artış";
//         if (percentChange > 0) return "↗️ Hafif Artış";
//         if (percentChange == 0) return "➡️ Sabit";
//         if (percentChange > -10) return "↘️ Hafif Düşüş";
//         if (percentChange > -50) return "📉 Düşüş";
//         return "📉 Güçlü Düşüş";
//     }

//     // Helper: Ürün durumu
//     private string GetProductStatus(TopProductDTO current, TopProductDTO previous)
//     {
//         if (current == null && previous != null) return "❌ Çıktı";
//         if (current != null && previous == null) return "🆕 Yeni";
//         if (current == null && previous == null) return "➖ Bilinmiyor";

//         var quantityChange = current.TotalQuantity - previous.TotalQuantity;
//         if (quantityChange > 0) return "📊 Gelişiyor";
//         if (quantityChange < 0) return "📉 Azalıyor";
//         return "➡️ Sabit";
//     }

//     // Helper: Müşteri durumu
//     private string GetCustomerStatus(TopCustomerDTO current, TopCustomerDTO previous)
//     {
//         if (current == null && previous != null) return "❌ Çıktı";
//         if (current != null && previous == null) return "🆕 Yeni";
//         if (current == null && previous == null) return "➖ Bilinmiyor";

//         var quotationChange = current.QuotationCount - previous.QuotationCount;
//         if (quotationChange > 0) return "📊 Gelişiyor";
//         if (quotationChange < 0) return "📉 Azalıyor";
//         return "➡️ Sabit";
//     }

//     // ⚡ Cache temizle (opsiyonel - bellek kontrolü için)
//     public void ClearCache()
//     {
//         _quotationLinesCache.Clear();
//         _logger.LogInformation("🗑️ Cache temizlendi");
//     }
// }

// // ============================================
// // Model sınıfları (Değişmemiş)
// // ============================================

// public class QuotationResponse
// {
//     [JsonPropertyName("d")]
//     [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
//     public List<Quotation> D { get; set; }

//     [JsonPropertyName("value")]
//     [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
//     public List<Quotation> Value { get; set; }

//     public List<Quotation> GetQuotations()
//     {
//         return D ?? Value ?? new List<Quotation>();
//     }
// }

// public class Quotation
// {
//     [JsonPropertyName("QuotationID")]
//     public string QuotationID { get; set; }

//     [JsonPropertyName("QuotationNumber")]
//     public int QuotationNumber { get; set; }

//     [JsonPropertyName("DeliveryAccountName")]
//     public string DeliveryAccountName { get; set; }

//     [JsonPropertyName("DeliveryAccountCode")]
//     public string DeliveryAccountCode { get; set; }

//     [JsonPropertyName("OrderAccountName")]
//     public string OrderAccountName { get; set; }

//     [JsonPropertyName("OrderAccountCode")]
//     public string OrderAccountCode { get; set; }

//     [JsonPropertyName("AmountFC")]
//     public decimal? AmountFC { get; set; }

//     [JsonPropertyName("Currency")]
//     public string Currency { get; set; }

//     [JsonPropertyName("Created")]
//     [JsonConverter(typeof(JsonDateTimeConverter))]
//     public DateTime? Created { get; set; }

//     [JsonPropertyName("Status")]
//     public int Status { get; set; }
// }

// public class JsonDateTimeConverter : JsonConverter<DateTime?>
// {
//     public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
//     {
//         string value = reader.GetString();

//         if (string.IsNullOrEmpty(value))
//             return null;

//         if (value.StartsWith("/Date(") && value.EndsWith(")/"))
//         {
//             var ticksStr = value.Substring(6, value.Length - 9);

//             if (long.TryParse(ticksStr, out long ticks))
//             {
//                 return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ticks);
//             }
//         }

//         if (DateTime.TryParse(value, out var result))
//             return result;

//         return null;
//     }

//     public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
//     {
//         if (value.HasValue)
//             writer.WriteStringValue(value.Value.ToString("O"));
//         else
//             writer.WriteNullValue();
//     }
// }

// public class QuotationLineResponse
// {
//     [JsonPropertyName("d")]
//     [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
//     public QuotationLineData D { get; set; }

//     [JsonPropertyName("value")]
//     [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
//     public List<QuotationLine> Value { get; set; }

//     public List<QuotationLine> GetLines()
//     {
//         if (D?.Results != null && D.Results.Count > 0)
//             return D.Results;

//         return Value ?? new List<QuotationLine>();
//     }
// }

// public class QuotationLineData
// {
//     [JsonPropertyName("results")]
//     public List<QuotationLine> Results { get; set; }
// }

// public class QuotationLine
// {
//     [JsonPropertyName("Item")]
//     public string Item { get; set; }

//     [JsonPropertyName("ItemCode")]
//     public string ItemCode { get; set; }

//     [JsonPropertyName("ItemDescription")]
//     public string ItemDescription { get; set; }

//     [JsonPropertyName("Description")]
//     public string Description { get; set; }

//     [JsonPropertyName("Quantity")]
//     public decimal? Quantity { get; set; }

//     [JsonPropertyName("AmountFC")]
//     public decimal? AmountFC { get; set; }

//     [JsonPropertyName("AmountDC")]
//     public decimal? AmountDC { get; set; }

//     [JsonPropertyName("UnitPrice")]
//     public decimal? UnitPrice { get; set; }

//     [JsonPropertyName("NetPrice")]
//     public decimal? NetPrice { get; set; }

//     [JsonPropertyName("UnitCode")]
//     public string UnitCode { get; set; }

//     [JsonPropertyName("UnitDescription")]
//     public string UnitDescription { get; set; }

//     [JsonPropertyName("ID")]
//     public string ID { get; set; }

//     [JsonPropertyName("QuotationID")]
//     public string QuotationID { get; set; }

//     [JsonPropertyName("QuotationNumber")]
//     public int? QuotationNumber { get; set; }

//     [JsonPropertyName("LineNumber")]
//     public int? LineNumber { get; set; }

//     [JsonPropertyName("VATAmountFC")]
//     public decimal? VATAmountFC { get; set; }

//     [JsonPropertyName("VATCode")]
//     public string VATCode { get; set; }

//     [JsonPropertyName("VATPercentage")]
//     public decimal? VATPercentage { get; set; }

//     [JsonPropertyName("Discount")]
//     public decimal? Discount { get; set; }

//     [JsonPropertyName("Division")]
//     public int? Division { get; set; }

//     [JsonPropertyName("VersionNumber")]
//     public int? VersionNumber { get; set; }
// }

// public class TopProductDTO
// {
//     public int Rank { get; set; }
//     public string ItemCode { get; set; }
//     public string path { get; set; }
//     public string ItemDescription { get; set; }
//     public int QuotationCount { get; set; }
//     public decimal TotalQuantity { get; set; }
//     public decimal TotalAmount { get; set; }
// }

// public class TopCustomerDTO
// {
//     public int Rank { get; set; }
//     public string CustomerName { get; set; }

//     private string _accountCode;
//     public string AccountCode
//     {
//         get => _accountCode;
//         set => _accountCode = value?.Trim();
//     }
//     public int QuotationCount { get; set; }
//     public decimal TotalAmount { get; set; }
//     public string Currency { get; set; }
// }

// public class PeriodDTO
// {
//     public DateTime StartDate { get; set; }
//     public DateTime EndDate { get; set; }

//     public string DisplayPeriod => $"{StartDate:yyyy-MM-dd} - {EndDate:yyyy-MM-dd}";
// }

// public class ComparisonProductResultDTO
// {
//     public PeriodDTO Period1 { get; set; }
//     public PeriodDTO Period2 { get; set; }
//     public List<ProductComparisonDTO> ComparisonProducts { get; set; }

//     public int TotalProducts => ComparisonProducts?.Count ?? 0;
//     public int NewProducts => ComparisonProducts?.Count(p => p.Period1QuotationCount == 0) ?? 0;
//     public int RemovedProducts => ComparisonProducts?.Count(p => p.Period2QuotationCount == 0) ?? 0;
//     public int IncreasedProducts => ComparisonProducts?.Count(p => p.QuotationCountChange > 0) ?? 0;
//     public int DecreasedProducts => ComparisonProducts?.Count(p => p.QuotationCountChange < 0) ?? 0;
// }

// public class ProductComparisonDTO
// {
//     public int Rank { get; set; }
//     public string ItemCode { get; set; }
//     public string ItemDescription { get; set; }

//     public int Period1QuotationCount { get; set; }
//     public decimal Period1TotalAmount { get; set; }
//     public decimal Period1TotalQuantity { get; set; }

//     public int Period2QuotationCount { get; set; }
//     public decimal Period2TotalAmount { get; set; }
//     public decimal Period2TotalQuantity { get; set; }

//     public int QuotationCountChange { get; set; }
//     public decimal TotalAmountChange { get; set; }
//     public decimal QuantityChange { get; set; }
//     public decimal ChangePercentage { get; set; }

//     public string Status
//     {
//         get
//         {
//             if (Period1QuotationCount == 0 && Period2QuotationCount > 0) return "NEW";
//             if (Period1QuotationCount > 0 && Period2QuotationCount == 0) return "REMOVED";
//             if (QuotationCountChange > 0) return "INCREASED";
//             if (QuotationCountChange < 0) return "DECREASED";
//             return "STABLE";
//         }
//     }
// }

// public class ComparisonCustomerResultDTO
// {
//     public PeriodDTO Period1 { get; set; }
//     public PeriodDTO Period2 { get; set; }
//     public List<CustomerComparisonDTO> ComparisonCustomers { get; set; }

//     public int TotalCustomers => ComparisonCustomers?.Count ?? 0;
//     public int NewCustomers => ComparisonCustomers?.Count(c => c.Period1QuotationCount == 0) ?? 0;
//     public int LostCustomers => ComparisonCustomers?.Count(c => c.Period2QuotationCount == 0) ?? 0;
//     public int IncreasingCustomers => ComparisonCustomers?.Count(c => c.QuotationCountChange > 0) ?? 0;
//     public int DecreasingCustomers => ComparisonCustomers?.Count(c => c.QuotationCountChange < 0) ?? 0;
// }

// public class CustomerComparisonDTO
// {
//     public int Rank { get; set; }
//     public string CustomerName { get; set; }
//     public string AccountCode { get; set; }

//     public int Period1QuotationCount { get; set; }
//     public decimal Period1TotalAmount { get; set; }

//     public int Period2QuotationCount { get; set; }
//     public decimal Period2TotalAmount { get; set; }

//     public int QuotationCountChange { get; set; }
//     public decimal TotalAmountChange { get; set; }
//     public decimal ChangePercentage { get; set; }
//     public string Currency { get; set; }

//     public string Status
//     {
//         get
//         {
//             if (Period1QuotationCount == 0 && Period2QuotationCount > 0) return "NEW";
//             if (Period1QuotationCount > 0 && Period2QuotationCount == 0) return "LOST";
//             if (QuotationCountChange > 0) return "INCREASING";
//             if (QuotationCountChange < 0) return "DECREASING";
//             return "STABLE";
//         }
//     }
// }

// internal class ProductInfo
// {
//     public string ItemCode { get; set; }
//     public string path { get; set; }
//     public string ItemDescription { get; set; }
//     public decimal Quantity { get; set; }
//     public decimal TotalAmount { get; set; }
//     public int QuotationCount { get; set; }
//     public HashSet<string> QuotationIds { get; set; } // ⚡ Benzersiz quotation sayması
// }

// internal class CustomerInfo
// {
//     public string CustomerName { get; set; }
//     public string AccountCode { get; set; }
//     public decimal TotalAmount { get; set; }
//     public int QuotationCount { get; set; }
//     public HashSet<string> QuotationIds { get; set; } // ⚡ Benzersiz quotation sayması
//     public string Currency { get; set; }
// }

// public class DetailedProductComparisonResponseDTO
// {
//     public bool Success { get; set; }
//     public string Message { get; set; }

//     public string CurrentPeriod { get; set; }
//     public string PreviousPeriod { get; set; }

//     public decimal CurrentAmount { get; set; }
//     public decimal PreviousAmount { get; set; }
//     public decimal AmountDifference { get; set; }
//     public decimal AmountDifferencePercent { get; set; }
//     public string AmountTrend { get; set; }

//     public decimal CurrentQuantity { get; set; }
//     public decimal PreviousQuantity { get; set; }
//     public decimal QuantityDifference { get; set; }
//     public decimal QuantityDifferencePercent { get; set; }
//     public string QuantityTrend { get; set; }

//     public int CurrentProductCount { get; set; }
//     public int PreviousProductCount { get; set; }
//     public int ProductDifference { get; set; }
//     public decimal ProductDifferencePercent { get; set; }
//     public string ProductTrend { get; set; }

//     public int CurrentQuotationCount { get; set; }
//     public int PreviousQuotationCount { get; set; }
//     public int QuotationDifference { get; set; }
//     public decimal QuotationDifferencePercent { get; set; }
//     public string QuotationTrend { get; set; }

//     public decimal CurrentAverageUnitPrice { get; set; }
//     public decimal PreviousAverageUnitPrice { get; set; }
//     public decimal AverageUnitPriceDifference { get; set; }

//     public List<DetailedTopProductDTO> CurrentTopProducts { get; set; }
//     public List<DetailedTopProductDTO> PreviousTopProducts { get; set; }

//     public List<DetailedProductComparisonDTO> ProductComparisons { get; set; }
// }

// public class DetailedCustomerComparisonResponseDTO
// {
//     public bool Success { get; set; }
//     public string Message { get; set; }

//     public string CurrentPeriod { get; set; }
//     public string PreviousPeriod { get; set; }

//     public decimal CurrentAmount { get; set; }
//     public decimal PreviousAmount { get; set; }
//     public decimal AmountDifference { get; set; }
//     public decimal AmountDifferencePercent { get; set; }
//     public string AmountTrend { get; set; }

//     public int CurrentQuotationCount { get; set; }
//     public int PreviousQuotationCount { get; set; }
//     public int QuotationDifference { get; set; }
//     public decimal QuotationDifferencePercent { get; set; }
//     public string QuotationTrend { get; set; }

//     public int CurrentCustomerCount { get; set; }
//     public int PreviousCustomerCount { get; set; }
//     public int CustomerDifference { get; set; }
//     public decimal CustomerDifferencePercent { get; set; }
//     public string CustomerTrend { get; set; }

//     public decimal CurrentAverageQuotationAmount { get; set; }
//     public decimal PreviousAverageQuotationAmount { get; set; }
//     public decimal AverageQuotationAmountDifference { get; set; }

//     public List<DetailedTopCustomerDTO> CurrentTopCustomers { get; set; }
//     public List<DetailedTopCustomerDTO> PreviousTopCustomers { get; set; }

//     public List<DetailedCustomerComparisonDTO> CustomerComparisons { get; set; }
// }

// public class DetailedTopProductDTO
// {
//     public int Rank { get; set; }
//     public string ItemCode { get; set; }
//     public string ItemDescription { get; set; }
//     public decimal TotalQuantity { get; set; }
//     public decimal TotalAmount { get; set; }
//     public decimal UnitPrice { get; set; }
//     public int QuotationCount { get; set; }
//     public decimal AverageQuantityPerQuotation { get; set; }
// }

// public class DetailedTopCustomerDTO
// {
//     public int Rank { get; set; }
//     public string CustomerName { get; set; }
//     public string AccountCode { get; set; }
//     public decimal TotalAmount { get; set; }
//     public int QuotationCount { get; set; }
//     public decimal AverageQuotationAmount { get; set; }
//     public string Currency { get; set; }
// }

// public class DetailedProductComparisonDTO
// {
//     public string ItemCode { get; set; }
//     public string ItemDescription { get; set; }

//     public int? CurrentRank { get; set; }
//     public decimal CurrentQuantity { get; set; }
//     public decimal CurrentAmount { get; set; }
//     public decimal CurrentPercentage { get; set; }

//     public int? PreviousRank { get; set; }
//     public decimal PreviousQuantity { get; set; }
//     public decimal PreviousAmount { get; set; }
//     public decimal PreviousPercentage { get; set; }

//     public int? RankChange { get; set; }
//     public decimal QuantityChange { get; set; }
//     public decimal AmountChange { get; set; }
//     public decimal AmountChangePercent { get; set; }

//     public string Status { get; set; }
// }

// public class DetailedCustomerComparisonDTO
// {
//     public string CustomerName { get; set; }
//     public string AccountCode { get; set; }

//     public int? CurrentRank { get; set; }
//     public int CurrentQuotationCount { get; set; }
//     public decimal CurrentAmount { get; set; }
//     public decimal CurrentPercentage { get; set; }

//     public int? PreviousRank { get; set; }
//     public int PreviousQuotationCount { get; set; }
//     public decimal PreviousAmount { get; set; }
//     public decimal PreviousPercentage { get; set; }

//     public int? RankChange { get; set; }
//     public int QuotationCountChange { get; set; }
//     public decimal AmountChange { get; set; }
//     public decimal AmountChangePercent { get; set; }

//     public string Status { get; set; }
// }
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using ShopifyProductApp.Services;
using System.Text;
using ExactOnline.Models;
using ExactOnline.Converters;
using System.Text.RegularExpressions;
using ExactWebApp.Dto;
using System.Collections.Concurrent;

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

    // ⚡ CACHE - Tekrarlanan API çağrılarını önle
    private readonly ConcurrentDictionary<string, string> _quotationLinesCache = 
        new ConcurrentDictionary<string, string>();

    // ⚡ Image Cache - Ürün görsellerini cache'le
    private readonly ConcurrentDictionary<string, string> _imageCache = 
        new ConcurrentDictionary<string, string>();

    // ⚡ HttpClient reuse (performance)
    private readonly HttpClient _httpClient;

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

        // ⚡ HttpClient singleton oluştur (reuse et)
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public async Task<QuotationResponse> GetQuotationReportNewAsync(DateTime startDate, DateTime endDate)
    {
        var exactService = _serviceProvider.GetRequiredService<ExactService>();
        var token = await exactService.GetValidToken();

        if (token == null)
        {
            _logger.LogError("❌ Geçerli bir token alınamadı");
            return null;
        }

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

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

        var response = await _httpClient.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError($"❌ API Hatası: {response.StatusCode}");
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError($"❌ Hata Detayı: {errorContent}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        var quotationResponse = JsonSerializer.Deserialize<QuotationResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return quotationResponse;
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

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

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

        var response = await _httpClient.GetAsync(url);

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

    // ⚡ OPTİMİZED: QuotationLines'ı fetch et (cache ile)
    private async Task<string> GetQuotationLinesAsync(string quotationLinesUri, string token)
    {
        try
        {
            if (_quotationLinesCache.TryGetValue(quotationLinesUri, out var cachedResult))
            {
                _logger.LogDebug($"💾 Cache HIT: {quotationLinesUri}");
                return cachedResult;
            }

            _logger.LogDebug($"📥 QuotationLines URI çağrılıyor: {quotationLinesUri}");

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _httpClient.DefaultRequestHeaders.Accept.Clear();
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.GetAsync(quotationLinesUri);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"❌ QuotationLines Fetch Hatası: {response.StatusCode}");
                _logger.LogError($"   Hata Detayı: {errorContent}");
                return "{\"d\":{\"results\":[]}}";
            }

            var content = await response.Content.ReadAsStringAsync();
            _quotationLinesCache.TryAdd(quotationLinesUri, content);
            
            _logger.LogDebug($"✅ QuotationLines alındı. Boyut: {content.Length} bytes");
            return content;
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ QuotationLines Exception: {ex.Message}");
            _logger.LogError($"   Stack Trace: {ex.StackTrace}");
            return "{\"d\":{\"results\":[]}}";
        }
    }

    // ⚡ OPTİMİZED: Ürün görseli çek (cache ile)
    private async Task<string> GetItemImageAsyncOptimized(string itemCode, int retryCount = 2)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(itemCode))
                return null;

            itemCode = itemCode.Trim();

            // ✅ Cache kontrol
            if (_imageCache.TryGetValue(itemCode, out var cachedUrl))
            {
                _logger.LogDebug($"✅ Görsel Cache HIT: {itemCode}");
                return cachedUrl;
            }

            _logger.LogDebug($"🔍 Görsel aranıyor: {itemCode}");

            for (int attempt = 0; attempt < retryCount; attempt++)
            {
                try
                {
                    var filter = Uri.EscapeDataString($"ID eq guid'{itemCode}'");
                    var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/Items?$filter={filter}";

                    var response = await _httpClient.GetAsync(url);

                    // 429 hatası aldığında retry (ama kısa delay)
                    if ((int)response.StatusCode == 429)
                    {
                        if (attempt < retryCount - 1)
                        {
                            var delayMs = 500 * (attempt + 1);
                            _logger.LogWarning($"⏸️ {itemCode}: Rate limit, {delayMs}ms bekleniyor...");
                            await Task.Delay(delayMs);
                            continue;
                        }
                        _imageCache.TryAdd(itemCode, null);
                        return null;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogDebug($"❌ {itemCode}: HTTP {response.StatusCode}");
                        _imageCache.TryAdd(itemCode, null);
                        return null;
                    }

                    var json = await response.Content.ReadAsStringAsync();

                    if (string.IsNullOrEmpty(json))
                    {
                        _imageCache.TryAdd(itemCode, null);
                        return null;
                    }

                    string pictureThumbnailUrl = null;

                    using (var doc = JsonDocument.Parse(json))
                    {
                        if (!doc.RootElement.TryGetProperty("d", out var dElement))
                        {
                            _logger.LogDebug($"⚠️ {itemCode}: 'd' property yok");
                            _imageCache.TryAdd(itemCode, null);
                            return null;
                        }

                        JsonElement resultsElement = default;

                        if (dElement.ValueKind == JsonValueKind.Object)
                        {
                            if (!dElement.TryGetProperty("results", out resultsElement))
                            {
                                _logger.LogDebug($"⚠️ {itemCode}: 'results' property yok");
                                _imageCache.TryAdd(itemCode, null);
                                return null;
                            }
                        }
                        else if (dElement.ValueKind == JsonValueKind.Array)
                        {
                            resultsElement = dElement;
                        }
                        else
                        {
                            _logger.LogDebug($"⚠️ {itemCode}: Beklenmeyen JSON yapısı");
                            _imageCache.TryAdd(itemCode, null);
                            return null;
                        }

                        if (resultsElement.ValueKind != JsonValueKind.Array)
                        {
                            _logger.LogDebug($"⚠️ {itemCode}: results array değil");
                            _imageCache.TryAdd(itemCode, null);
                            return null;
                        }

                        var arrayLength = resultsElement.GetArrayLength();
                        if (arrayLength == 0)
                        {
                            _logger.LogDebug($"⚠️ {itemCode}: Ürün bulunamadı");
                            _imageCache.TryAdd(itemCode, null);
                            return null;
                        }

                        var firstItem = resultsElement[0];

                        if (firstItem.TryGetProperty("PictureThumbnailUrl", out var thumbElement) &&
                            thumbElement.ValueKind == JsonValueKind.String)
                        {
                            pictureThumbnailUrl = thumbElement.GetString();
                            if (!string.IsNullOrEmpty(pictureThumbnailUrl) && pictureThumbnailUrl != "null")
                            {
                                _logger.LogDebug($"✅ {itemCode}: PictureThumbnailUrl bulundu");
                            }
                            else
                            {
                                pictureThumbnailUrl = null;
                            }
                        }

                        if (string.IsNullOrEmpty(pictureThumbnailUrl) &&
                            firstItem.TryGetProperty("PictureUrl", out var picElement) &&
                            picElement.ValueKind == JsonValueKind.String)
                        {
                            pictureThumbnailUrl = picElement.GetString();
                            if (!string.IsNullOrEmpty(pictureThumbnailUrl) && pictureThumbnailUrl != "null")
                            {
                                _logger.LogDebug($"✅ {itemCode}: PictureUrl bulundu");
                            }
                            else
                            {
                                pictureThumbnailUrl = null;
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(pictureThumbnailUrl))
                    {
                        _logger.LogDebug($"⚠️ {itemCode}: Görsel URL'si bulunamadı");
                        _imageCache.TryAdd(itemCode, null);
                        return null;
                    }

                    _logger.LogDebug($"✅ {itemCode}: Görsel bulundu");
                    _imageCache.TryAdd(itemCode, pictureThumbnailUrl);

                    return pictureThumbnailUrl;
                }
                catch (JsonException ex)
                {
                    _logger.LogDebug($"❌ {itemCode}: JSON Parse Hatası - {ex.Message}");
                    return null;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogDebug($"❌ {itemCode}: HTTP Hatası - {ex.Message}");
                    return null;
                }
                catch (Exception ex)
                {
                    if (attempt < retryCount - 1)
                    {
                        _logger.LogWarning($"⚠️ {itemCode}: Hata (retry {attempt + 1}/{retryCount}) - {ex.Message}");
                        await Task.Delay(500 * (attempt + 1));
                        continue;
                    }
                    _logger.LogDebug($"❌ {itemCode}: Hata - {ex.Message}");
                    return null;
                }
            }

            _logger.LogWarning($"❌ {itemCode}: {retryCount} deneme sonrası başarısız");
            _imageCache.TryAdd(itemCode, null);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ GetItemImageAsyncOptimized Hata: {ex.Message}");
            return null;
        }
    }

    // ⚡ OPTİMİZED: Ürün görsellerini paralel olarak çek
    private async Task FetchProductPicturesOptimizedAsync(
        List<TopProductDTO> products,
        Action<string> progressCallback = null)
    {
        if (!products.Any())
            return;

        var itemCodes = products
            .Where(p => !string.IsNullOrEmpty(p.ItemCode))
            .Select(p => p.ItemCode)
            .Distinct()
            .ToList();

        var concurrentLimit = Math.Min(itemCodes.Count, 5);  // Maksimum 5

        _logger.LogInformation($"📸 {itemCodes.Count} ürün görseli çekiliyor ({concurrentLimit} concurrent)...");

        var semaphore = new SemaphoreSlim(concurrentLimit, concurrentLimit);
        var tasks = new List<Task>();

        for (int i = 0; i < itemCodes.Count; i++)
        {
            int index = i;
            var itemCode = itemCodes[i];

            var task = Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var pictureUrl = await GetItemImageAsyncOptimized(itemCode);

                    var product = products.FirstOrDefault(p => p.ItemCode == itemCode);
                    if (product != null)
                    {
                        product.path = pictureUrl;
                        if (!string.IsNullOrEmpty(pictureUrl))
                        {
                            _logger.LogDebug($"✅ {itemCode}: Görsel atandı");
                        }
                    }

                    if ((index + 1) % concurrentLimit == 0)
                    {
                        progressCallback?.Invoke($"📸 İlerleme: {index + 1}/{itemCodes.Count}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"⚠️ {itemCode} görsel çekme hatası: {ex.Message}");
                }
                finally
                {
                    semaphore.Release();
                }
            });

            tasks.Add(task);
        }

        await Task.WhenAll(tasks);

        var successCount = products.Count(p => !string.IsNullOrEmpty(p.path));
        _logger.LogInformation($"✅ {successCount}/{itemCodes.Count} ürün görseli çekildi");
    }

    // ⚡ OPTİMİZED: En çok teklif verilen ürünleri getir (Paralel işleme + Görsel)
    public async Task<List<TopProductDTO>> GetTopQuotedProductsAsync(DateTime startDate, DateTime endDate, int topCount = 10, ReportFilterModel filter = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

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

        _logger.LogInformation($"📥 {quotations.Count} quotation bulundu. QuotationLines alınıyor...");

        // ⚡ Paralel olarak tüm QuotationLines'ı al
        var allLines = await FetchAllQuotationLinesParallelAsync(quotations, token.access_token);

        _logger.LogInformation($"✅ {allLines.Count} satır bulundu. İşleniyor...");

        // ⚡ Verileri verimli şekilde işle
        var productCounts = new Dictionary<string, ProductInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in allLines)
        {
            var itemKey = !string.IsNullOrWhiteSpace(line.ItemCode)
                ? line.ItemCode
                : (line.Item ?? line.ID);

            var itemDescription = !string.IsNullOrWhiteSpace(line.ItemDescription)
                ? line.ItemDescription
                : line.Description;

            if (!productCounts.ContainsKey(itemKey))
            {
                productCounts[itemKey] = new ProductInfo
                {
                    ItemCode = itemKey,
                    path = "",
                    ItemDescription = itemDescription,
                    Quantity = 0,
                    TotalAmount = 0,
                    QuotationIds = new HashSet<string>()
                };
            }

            productCounts[itemKey].Quantity += line.Quantity ?? 0;
            productCounts[itemKey].TotalAmount += line.AmountFC ?? 0;
            productCounts[itemKey].QuotationIds.Add(line.QuotationID);

            _logger.LogDebug($"📦 Ürün eklendi: {itemDescription} - Miktar: {line.Quantity}, Tutar: {line.AmountFC}");
        }

        // ⚡ QuotationCount'ı güncelle
        foreach (var product in productCounts.Values)
        {
            product.QuotationCount = product.QuotationIds.Count;
        }

        // Apply filters
        var filteredProducts = productCounts.Values.AsEnumerable();

        if (filter != null)
        {
            if (filter.ProductCodes != null && filter.ProductCodes.Any())
            {
                var codes = new HashSet<string>(filter.ProductCodes, StringComparer.OrdinalIgnoreCase);
                filteredProducts = filteredProducts.Where(p => codes.Contains(p.ItemCode));
            }

            if (!string.IsNullOrEmpty(filter.SearchTerm))
            {
                var searchLower = filter.SearchTerm.ToLowerInvariant();
                filteredProducts = filteredProducts.Where(p =>
                    p.ItemDescription?.ToLowerInvariant().Contains(searchLower) == true ||
                    p.ItemCode?.ToLowerInvariant().Contains(searchLower) == true);
            }

            if (filter.MinAmount.HasValue)
            {
                filteredProducts = filteredProducts.Where(p => p.TotalAmount >= (decimal)filter.MinAmount.Value);
            }

            if (filter.MaxAmount.HasValue)
            {
                filteredProducts = filteredProducts.Where(p => p.TotalAmount <= (decimal)filter.MaxAmount.Value);
            }
        }

        // En çok teklif verilen ürünleri sırala
        var topProducts = filteredProducts
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
                TotalAmount = p.TotalAmount,
                path = ""  // ← Paralel fetch'ten doldurulacak
            })
            .ToList();

        // ⚡ Ürün görsellerini paralel olarak çek
        _logger.LogInformation($"📸 {topProducts.Count} ürünün görselleri çekiliyor...");
        await FetchProductPicturesOptimizedAsync(topProducts, null);

        stopwatch.Stop();
        _logger.LogInformation($"✅ {topProducts.Count} ürün bulundu ({stopwatch.ElapsedMilliseconds}ms)");
        return topProducts;
    }

    // ⚡ OPTİMİZED: Paralel QuotationLines Fetch
    private async Task<List<QuotationLine>> FetchAllQuotationLinesParallelAsync(List<Quotation> quotations, string token)
    {
        var allLines = new ConcurrentBag<QuotationLine>();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var maxDegreeOfParallelism = 5;
        var semaphore = new SemaphoreSlim(maxDegreeOfParallelism, maxDegreeOfParallelism);
        var tasks = new List<Task>();

        foreach (var quotation in quotations)
        {
            await semaphore.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var linesUri = $"{_baseUrl}/api/v1/{_divisionCode}/crm/Quotations(guid'{quotation.QuotationID}')/QuotationLines";
                    var linesJson = await GetQuotationLinesAsync(linesUri, token);

                    var linesResponse = JsonSerializer.Deserialize<QuotationLineResponse>(linesJson, options);
                    var lines = linesResponse?.GetLines();

                    if (lines != null && lines.Count > 0)
                    {
                        foreach (var line in lines)
                            allLines.Add(line);
                    }

                    _logger.LogDebug($"✅ {quotation.QuotationID}: {lines?.Count ?? 0} satır alındı");
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
        return allLines.ToList();
    }

    // ⚡ OPTİMİZED: En çok teklif verilen müşterileri getir
    public async Task<List<TopCustomerDTO>> GetTopQuotedCustomersAsync(DateTime startDate, DateTime endDate, int topCount = 10, ReportFilterModel filter = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

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

        foreach (var quotation in quotations)
        {
            var customerKey = quotation.DeliveryAccountName ?? quotation.OrderAccountName ?? "Unknown";

            if (!customerCounts.ContainsKey(customerKey))
            {
                customerCounts[customerKey] = new CustomerInfo
                {
                    CustomerName = customerKey,
                    AccountCode = quotation.DeliveryAccountCode ?? quotation.OrderAccountCode,
                    TotalAmount = 0,
                    QuotationIds = new HashSet<string>()
                };
            }

            customerCounts[customerKey].QuotationIds.Add(quotation.QuotationID);
            customerCounts[customerKey].TotalAmount += quotation.AmountFC ?? 0;
            customerCounts[customerKey].Currency = quotation.Currency;
        }

        foreach (var customer in customerCounts.Values)
        {
            customer.QuotationCount = customer.QuotationIds.Count;
        }

        // Apply filters
        var filteredCustomers = customerCounts.Values.AsEnumerable();

        if (filter != null)
        {
            if (filter.CustomerNames != null && filter.CustomerNames.Any())
            {
                var names = new HashSet<string>(filter.CustomerNames, StringComparer.OrdinalIgnoreCase);
                filteredCustomers = filteredCustomers.Where(c => names.Contains(c.CustomerName));
            }

            if (!string.IsNullOrEmpty(filter.SearchTerm))
            {
                var searchLower = filter.SearchTerm.ToLowerInvariant();
                filteredCustomers = filteredCustomers.Where(c => 
                    c.CustomerName?.ToLowerInvariant().Contains(searchLower) == true);
            }

            if (filter.MinAmount.HasValue)
            {
                filteredCustomers = filteredCustomers.Where(c => c.TotalAmount >= (decimal)filter.MinAmount.Value);
            }

            if (filter.MaxAmount.HasValue)
            {
                filteredCustomers = filteredCustomers.Where(c => c.TotalAmount <= (decimal)filter.MaxAmount.Value);
            }
        }

        var topCustomers = filteredCustomers
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

        stopwatch.Stop();
        _logger.LogInformation($"✅ {topCustomers.Count} müşteri bulundu ({stopwatch.ElapsedMilliseconds}ms)");
        return topCustomers;
    }

    // İki tarih aralığında ürünleri karşılaştır
    public async Task<ComparisonProductResultDTO> CompareProductsByDateRangeAsync(
        DateTime startDate1, DateTime endDate1,
        DateTime startDate2, DateTime endDate2,
        int topCount = 10,
        ReportFilterModel filter = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation($"📊 Ürün karşılaştırması başlıyor: Period1({startDate1:yyyy-MM-dd} - {endDate1:yyyy-MM-dd}) vs Period2({startDate2:yyyy-MM-dd} - {endDate2:yyyy-MM-dd})");

        var period1Task = GetTopQuotedProductsAsync(startDate1, endDate1, topCount * 2, filter);
        var period2Task = GetTopQuotedProductsAsync(startDate2, endDate2, topCount * 2, filter);

        await Task.WhenAll(period1Task, period2Task);

        var period1Products = period1Task.Result;
        var period2Products = period2Task.Result;

        var result = new ComparisonProductResultDTO
        {
            Period1 = new PeriodDTO { StartDate = startDate1, EndDate = endDate1 },
            Period2 = new PeriodDTO { StartDate = startDate2, EndDate = endDate2 },
            ComparisonProducts = new List<ProductComparisonDTO>()
        };

        var period1Dict = period1Products.ToDictionary(p => p.ItemCode, p => p);
        var period2Dict = period2Products.ToDictionary(p => p.ItemCode, p => p);

        var allProductKeys = new HashSet<string>();
        foreach (var product in period1Products)
            allProductKeys.Add(product.ItemCode);
        foreach (var product in period2Products)
            allProductKeys.Add(product.ItemCode);

        var comparisons = new List<ProductComparisonDTO>();

        foreach (var key in allProductKeys)
        {
            period1Dict.TryGetValue(key, out var p1);
            period2Dict.TryGetValue(key, out var p2);

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

        stopwatch.Stop();
        _logger.LogInformation($"✅ {result.ComparisonProducts.Count} ürün karşılaştırıldı ({stopwatch.ElapsedMilliseconds}ms)");
        return result;
    }

    // ⚡ OPTİMİZED: İki tarih aralığında ürünleri karşılaştır - Detaylı Versiyon (Görsel ile)
    public async Task<DetailedProductComparisonResponseDTO> CompareProductsByDateRangeDetailedAsync(
        DateTime startDate1, DateTime endDate1,
        DateTime startDate2, DateTime endDate2,
        int topCount = 10,
        ReportFilterModel filter = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation($"📊 Detaylı ürün karşılaştırması başlıyor: Period1({startDate1:yyyy-MM-dd} - {endDate1:yyyy-MM-dd}) vs Period2({startDate2:yyyy-MM-dd} - {endDate2:yyyy-MM-dd})");

        var period1Task = GetTopQuotedProductsAsync(startDate1, endDate1, topCount, filter);
        var period2Task = GetTopQuotedProductsAsync(startDate2, endDate2, topCount, filter);

        await Task.WhenAll(period1Task, period2Task);

        var period1Products = period1Task.Result;
        var period2Products = period2Task.Result;

        // ⚡ Detaylı DTO'lar oluştursun (görsel ile)
        var currentAvgPrice = 0m;
        var previousAvgPrice = 0m;

        // Calculate totals
        var currentAmount = period1Products.Sum(p => p.TotalAmount);
        var previousAmount = period2Products.Sum(p => p.TotalAmount);
        var currentQuantity = period1Products.Sum(p => p.TotalQuantity);
        var previousQuantity = period2Products.Sum(p => p.TotalQuantity);
        var currentQuotationCount = period1Products.Sum(p => p.QuotationCount);
        var previousQuotationCount = period2Products.Sum(p => p.QuotationCount);

        // Calculate differences
        var amountDifference = currentAmount - previousAmount;
        var amountDifferencePercent = previousAmount > 0 ? (amountDifference / previousAmount) * 100 : (currentAmount > 0 ? 100 : 0);
        var quantityDifference = currentQuantity - previousQuantity;
        var quantityDifferencePercent = previousQuantity > 0 ? (quantityDifference / previousQuantity) * 100 : (currentQuantity > 0 ? 100 : 0);
        var quotationDifference = currentQuotationCount - previousQuotationCount;
        var quotationDifferencePercent = previousQuotationCount > 0 ? ((decimal)quotationDifference / previousQuotationCount) * 100 : (currentQuotationCount > 0 ? 100 : 0);

        // Product count comparison
        var productDifference = period1Products.Count - period2Products.Count;
        var productDifferencePercent = period2Products.Count > 0 ? ((decimal)productDifference / period2Products.Count) * 100 : (period1Products.Count > 0 ? 100 : 0);

        // Average unit prices
        currentAvgPrice = currentQuantity > 0 ? currentAmount / currentQuantity : 0;
        previousAvgPrice = previousQuantity > 0 ? previousAmount / previousQuantity : 0;

        // ⚡ Detaylı ürün DTO'ları (görsel ile doldurulacak)
        var currentTopProducts = period1Products.Select((p, index) => new DetailedTopProductDTO
        {
            Rank = index + 1,
            ItemCode = p.ItemCode,
            ItemDescription = p.ItemDescription,
            TotalQuantity = p.TotalQuantity,
            TotalAmount = p.TotalAmount,
            UnitPrice = p.TotalQuantity > 0 ? p.TotalAmount / p.TotalQuantity : 0,
            QuotationCount = p.QuotationCount,
            AverageQuantityPerQuotation = p.QuotationCount > 0 ? p.TotalQuantity / p.QuotationCount : 0,
            path = p.path  // ← Görsel URL
        }).ToList();

        var previousTopProducts = period2Products.Select((p, index) => new DetailedTopProductDTO
        {
            Rank = index + 1,
            ItemCode = p.ItemCode,
            ItemDescription = p.ItemDescription,
            TotalQuantity = p.TotalQuantity,
            TotalAmount = p.TotalAmount,
            UnitPrice = p.TotalQuantity > 0 ? p.TotalAmount / p.TotalQuantity : 0,
            QuotationCount = p.QuotationCount,
            AverageQuantityPerQuotation = p.QuotationCount > 0 ? p.TotalQuantity / p.QuotationCount : 0,
            path = p.path  // ← Görsel URL
        }).ToList();

        // Dictionary lookup ile hızlandır
        var period1Dict = period1Products.ToDictionary(p => p.ItemCode, p => p);
        var period2Dict = period2Products.ToDictionary(p => p.ItemCode, p => p);

        // Create product comparisons
        var allProductKeys = new HashSet<string>();
        foreach (var product in period1Products) allProductKeys.Add(product.ItemCode);
        foreach (var product in period2Products) allProductKeys.Add(product.ItemCode);

        var productComparisons = new List<DetailedProductComparisonDTO>();
        foreach (var key in allProductKeys)
        {
            period1Dict.TryGetValue(key, out var p1);
            period2Dict.TryGetValue(key, out var p2);

            var comparison = new DetailedProductComparisonDTO
            {
                ItemCode = key,
                ItemDescription = p1?.ItemDescription ?? p2?.ItemDescription,
                CurrentRank = p1?.Rank,
                CurrentQuantity = p1?.TotalQuantity ?? 0,
                CurrentAmount = p1?.TotalAmount ?? 0,
                CurrentPercentage = currentQuantity > 0 ? ((p1?.TotalQuantity ?? 0) / currentQuantity) * 100 : 0,
                PreviousRank = p2?.Rank,
                PreviousQuantity = p2?.TotalQuantity ?? 0,
                PreviousAmount = p2?.TotalAmount ?? 0,
                PreviousPercentage = previousQuantity > 0 ? ((p2?.TotalQuantity ?? 0) / previousQuantity) * 100 : 0,
                RankChange = (p1 != null && p2 != null) ? p2.Rank - p1.Rank : null,
                QuantityChange = (p1?.TotalQuantity ?? 0) - (p2?.TotalQuantity ?? 0),
                AmountChange = (p1?.TotalAmount ?? 0) - (p2?.TotalAmount ?? 0),
                AmountChangePercent = (p2?.TotalAmount ?? 0) > 0
                    ? (((p1?.TotalAmount ?? 0) - (p2?.TotalAmount ?? 0)) / (p2?.TotalAmount ?? 1)) * 100
                    : ((p1?.TotalAmount ?? 0) > 0 ? 100 : 0),
                Status = GetProductStatus(p1, p2),
                path = p1.path ?? p2.path
            };

            productComparisons.Add(comparison);
        }

        var result = new DetailedProductComparisonResponseDTO
        {
            Success = true,
            Message = "✅ Tarih aralığı karşılaştırması başarılı",
            CurrentPeriod = $"{startDate1:yyyy-MM-dd} to {endDate1:yyyy-MM-dd}",
            PreviousPeriod = $"{startDate2:yyyy-MM-dd} to {endDate2:yyyy-MM-dd}",
            CurrentAmount = currentAmount,
            PreviousAmount = previousAmount,
            AmountDifference = amountDifference,
            AmountDifferencePercent = amountDifferencePercent,
            AmountTrend = GetTrend(amountDifferencePercent),
            CurrentQuantity = currentQuantity,
            PreviousQuantity = previousQuantity,
            QuantityDifference = quantityDifference,
            QuantityDifferencePercent = quantityDifferencePercent,
            QuantityTrend = GetTrend(quantityDifferencePercent),
            CurrentProductCount = period1Products.Count,
            PreviousProductCount = period2Products.Count,
            ProductDifference = productDifference,
            ProductDifferencePercent = productDifferencePercent,
            ProductTrend = GetTrend(productDifferencePercent),
            CurrentQuotationCount = currentQuotationCount,
            PreviousQuotationCount = previousQuotationCount,
            QuotationDifference = quotationDifference,
            QuotationDifferencePercent = quotationDifferencePercent,
            QuotationTrend = GetTrend(quotationDifferencePercent),
            CurrentAverageUnitPrice = currentAvgPrice,
            PreviousAverageUnitPrice = previousAvgPrice,
            AverageUnitPriceDifference = currentAvgPrice - previousAvgPrice,
            CurrentTopProducts = currentTopProducts,
            PreviousTopProducts = previousTopProducts,
            ProductComparisons = productComparisons
        };

        stopwatch.Stop();
        _logger.LogInformation($"✅ Detaylı karşılaştırma tamamlandı ({stopwatch.ElapsedMilliseconds}ms)");
        return result;
    }

    // İki tarih aralığında müşterileri karşılaştır
    public async Task<ComparisonCustomerResultDTO> CompareCustomersByDateRangeAsync(
        DateTime startDate1, DateTime endDate1,
        DateTime startDate2, DateTime endDate2,
        int topCount = 10,
        ReportFilterModel filter = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation($"📊 Müşteri karşılaştırması başlıyor: Period1({startDate1:yyyy-MM-dd} - {endDate1:yyyy-MM-dd}) vs Period2({startDate2:yyyy-MM-dd} - {endDate2:yyyy-MM-dd})");

        var period1Task = GetTopQuotedCustomersAsync(startDate1, endDate1, topCount * 2, filter);
        var period2Task = GetTopQuotedCustomersAsync(startDate2, endDate2, topCount * 2, filter);

        await Task.WhenAll(period1Task, period2Task);

        var period1Customers = period1Task.Result;
        var period2Customers = period2Task.Result;

        var result = new ComparisonCustomerResultDTO
        {
            Period1 = new PeriodDTO { StartDate = startDate1, EndDate = endDate1 },
            Period2 = new PeriodDTO { StartDate = startDate2, EndDate = endDate2 },
            ComparisonCustomers = new List<CustomerComparisonDTO>()
        };

        var period1Dict = period1Customers.ToDictionary(c => c.CustomerName, c => c);
        var period2Dict = period2Customers.ToDictionary(c => c.CustomerName, c => c);

        var allCustomerNames = new HashSet<string>();
        foreach (var customer in period1Customers)
            allCustomerNames.Add(customer.CustomerName);
        foreach (var customer in period2Customers)
            allCustomerNames.Add(customer.CustomerName);

        var comparisons = new List<CustomerComparisonDTO>();

        foreach (var name in allCustomerNames)
        {
            period1Dict.TryGetValue(name, out var c1);
            period2Dict.TryGetValue(name, out var c2);

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

        stopwatch.Stop();
        _logger.LogInformation($"✅ {result.ComparisonCustomers.Count} müşteri karşılaştırıldı ({stopwatch.ElapsedMilliseconds}ms)");
        return result;
    }

    // İki tarih aralığında müşterileri karşılaştır - Detaylı Versiyon
    public async Task<DetailedCustomerComparisonResponseDTO> CompareCustomersByDateRangeDetailedAsync(
        DateTime startDate1, DateTime endDate1,
        DateTime startDate2, DateTime endDate2,
        int topCount = 10,
        ReportFilterModel filter = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation($"📊 Detaylı müşteri karşılaştırması başlıyor: Period1({startDate1:yyyy-MM-dd} - {endDate1:yyyy-MM-dd}) vs Period2({startDate2:yyyy-MM-dd} - {endDate2:yyyy-MM-dd})");

        var period1Task = GetTopQuotedCustomersAsync(startDate1, endDate1, topCount, filter);
        var period2Task = GetTopQuotedCustomersAsync(startDate2, endDate2, topCount, filter);

        await Task.WhenAll(period1Task, period2Task);

        var period1Customers = period1Task.Result;
        var period2Customers = period2Task.Result;

        // Calculate totals
        var currentAmount = period1Customers.Sum(c => c.TotalAmount);
        var previousAmount = period2Customers.Sum(c => c.TotalAmount);
        var currentQuotationCount = period1Customers.Sum(c => c.QuotationCount);
        var previousQuotationCount = period2Customers.Sum(c => c.QuotationCount);

        // Calculate differences
        var amountDifference = currentAmount - previousAmount;
        var amountDifferencePercent = previousAmount > 0 ? (amountDifference / previousAmount) * 100 : (currentAmount > 0 ? 100 : 0);
        var quotationDifference = currentQuotationCount - previousQuotationCount;
        var quotationDifferencePercent = previousQuotationCount > 0 ? ((decimal)quotationDifference / previousQuotationCount) * 100 : (currentQuotationCount > 0 ? 100 : 0);

        // Customer count comparison
        var customerDifference = period1Customers.Count - period2Customers.Count;
        var customerDifferencePercent = period2Customers.Count > 0 ? ((decimal)customerDifference / period2Customers.Count) * 100 : (period1Customers.Count > 0 ? 100 : 0);

        // Average quotation amounts
        var currentAvgQuotationAmount = currentQuotationCount > 0 ? currentAmount / currentQuotationCount : 0;
        var previousAvgQuotationAmount = previousQuotationCount > 0 ? previousAmount / previousQuotationCount : 0;

        // Create detailed top customers
        var currentTopCustomers = period1Customers.Select((c, index) => new DetailedTopCustomerDTO
        {
            Rank = index + 1,
            CustomerName = c.CustomerName,
            AccountCode = c.AccountCode,
            TotalAmount = c.TotalAmount,
            QuotationCount = c.QuotationCount,
            AverageQuotationAmount = c.QuotationCount > 0 ? c.TotalAmount / c.QuotationCount : 0,
            Currency = c.Currency
        }).ToList();

        var previousTopCustomers = period2Customers.Select((c, index) => new DetailedTopCustomerDTO
        {
            Rank = index + 1,
            CustomerName = c.CustomerName,
            AccountCode = c.AccountCode,
            TotalAmount = c.TotalAmount,
            QuotationCount = c.QuotationCount,
            AverageQuotationAmount = c.QuotationCount > 0 ? c.TotalAmount / c.QuotationCount : 0,
            Currency = c.Currency
        }).ToList();

        var period1Dict = period1Customers.ToDictionary(c => c.CustomerName, c => c);
        var period2Dict = period2Customers.ToDictionary(c => c.CustomerName, c => c);

        // Create customer comparisons
        var allCustomerNames = new HashSet<string>();
        foreach (var customer in period1Customers) allCustomerNames.Add(customer.CustomerName);
        foreach (var customer in period2Customers) allCustomerNames.Add(customer.CustomerName);

        var customerComparisons = new List<DetailedCustomerComparisonDTO>();
        foreach (var name in allCustomerNames)
        {
            period1Dict.TryGetValue(name, out var c1);
            period2Dict.TryGetValue(name, out var c2);

            var comparison = new DetailedCustomerComparisonDTO
            {
                CustomerName = name,
                AccountCode = c1?.AccountCode ?? c2?.AccountCode,
                CurrentRank = c1?.Rank,
                CurrentQuotationCount = c1?.QuotationCount ?? 0,
                CurrentAmount = c1?.TotalAmount ?? 0,
                CurrentPercentage = currentAmount > 0 ? ((c1?.TotalAmount ?? 0) / currentAmount) * 100 : 0,
                PreviousRank = c2?.Rank,
                PreviousQuotationCount = c2?.QuotationCount ?? 0,
                PreviousAmount = c2?.TotalAmount ?? 0,
                PreviousPercentage = previousAmount > 0 ? ((c2?.TotalAmount ?? 0) / previousAmount) * 100 : 0,
                RankChange = (c1 != null && c2 != null) ? c2.Rank - c1.Rank : null,
                QuotationCountChange = (c1?.QuotationCount ?? 0) - (c2?.QuotationCount ?? 0),
                AmountChange = (c1?.TotalAmount ?? 0) - (c2?.TotalAmount ?? 0),
                AmountChangePercent = (c2?.TotalAmount ?? 0) > 0
                    ? (((c1?.TotalAmount ?? 0) - (c2?.TotalAmount ?? 0)) / (c2?.TotalAmount ?? 1)) * 100
                    : ((c1?.TotalAmount ?? 0) > 0 ? 100 : 0),
                Status = GetCustomerStatus(c1, c2)
            };

            customerComparisons.Add(comparison);
        }

        var result = new DetailedCustomerComparisonResponseDTO
        {
            Success = true,
            Message = "✅ Tarih aralığı karşılaştırması başarılı",
            CurrentPeriod = $"{startDate1:yyyy-MM-dd} to {endDate1:yyyy-MM-dd}",
            PreviousPeriod = $"{startDate2:yyyy-MM-dd} to {endDate2:yyyy-MM-dd}",
            CurrentAmount = currentAmount,
            PreviousAmount = previousAmount,
            AmountDifference = amountDifference,
            AmountDifferencePercent = amountDifferencePercent,
            AmountTrend = GetTrend(amountDifferencePercent),
            CurrentQuotationCount = currentQuotationCount,
            PreviousQuotationCount = previousQuotationCount,
            QuotationDifference = quotationDifference,
            QuotationDifferencePercent = quotationDifferencePercent,
            QuotationTrend = GetTrend(quotationDifferencePercent),
            CurrentCustomerCount = period1Customers.Count,
            PreviousCustomerCount = period2Customers.Count,
            CustomerDifference = customerDifference,
            CustomerDifferencePercent = customerDifferencePercent,
            CustomerTrend = GetTrend(customerDifferencePercent),
            CurrentAverageQuotationAmount = currentAvgQuotationAmount,
            PreviousAverageQuotationAmount = previousAvgQuotationAmount,
            AverageQuotationAmountDifference = currentAvgQuotationAmount - previousAvgQuotationAmount,
            CurrentTopCustomers = currentTopCustomers,
            PreviousTopCustomers = previousTopCustomers,
            CustomerComparisons = customerComparisons
        };

        stopwatch.Stop();
        _logger.LogInformation($"✅ Detaylı karşılaştırma tamamlandı ({stopwatch.ElapsedMilliseconds}ms)");
        return result;
    }

    // Helper: Trend hesaplama
    private string GetTrend(decimal percentChange)
    {
        if (percentChange > 50) return "📈 Güçlü Artış";
        if (percentChange > 10) return "📈 Artış";
        if (percentChange > 0) return "↗️ Hafif Artış";
        if (percentChange == 0) return "➡️ Sabit";
        if (percentChange > -10) return "↘️ Hafif Düşüş";
        if (percentChange > -50) return "📉 Düşüş";
        return "📉 Güçlü Düşüş";
    }

    // Helper: Ürün durumu
    private string GetProductStatus(TopProductDTO current, TopProductDTO previous)
    {
        if (current == null && previous != null) return "❌ Çıktı";
        if (current != null && previous == null) return "🆕 Yeni";
        if (current == null && previous == null) return "➖ Bilinmiyor";

        var quantityChange = current.TotalQuantity - previous.TotalQuantity;
        if (quantityChange > 0) return "📊 Gelişiyor";
        if (quantityChange < 0) return "📉 Azalıyor";
        return "➡️ Sabit";
    }

    // Helper: Müşteri durumu
    private string GetCustomerStatus(TopCustomerDTO current, TopCustomerDTO previous)
    {
        if (current == null && previous != null) return "❌ Çıktı";
        if (current != null && previous == null) return "🆕 Yeni";
        if (current == null && previous == null) return "➖ Bilinmiyor";

        var quotationChange = current.QuotationCount - previous.QuotationCount;
        if (quotationChange > 0) return "📊 Gelişiyor";
        if (quotationChange < 0) return "📉 Azalıyor";
        return "➡️ Sabit";
    }

    // ⚡ Cache temizle
    public void ClearCache()
    {
        _quotationLinesCache.Clear();
        _imageCache.Clear();
        _logger.LogInformation("🗑️ Tüm cache temizlendi (Quotation + Görsel)");
    }
}

// ============================================
// Model sınıfları (Değişmemiş + İlaveler)
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

public class JsonDateTimeConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string value = reader.GetString();

        if (string.IsNullOrEmpty(value))
            return null;

        if (value.StartsWith("/Date(") && value.EndsWith(")/"))
        {
            var ticksStr = value.Substring(6, value.Length - 9);

            if (long.TryParse(ticksStr, out long ticks))
            {
                return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ticks);
            }
        }

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
        if (D?.Results != null && D.Results.Count > 0)
            return D.Results;

        return Value ?? new List<QuotationLine>();
    }
}

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

public class TopProductDTO
{
    public int Rank { get; set; }
    public string ItemCode { get; set; }
    public string path { get; set; }  // ← Görseli buraya dolduruyoruz
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

    public int Period1QuotationCount { get; set; }
    public decimal Period1TotalAmount { get; set; }
    public decimal Period1TotalQuantity { get; set; }

    public int Period2QuotationCount { get; set; }
    public decimal Period2TotalAmount { get; set; }
    public decimal Period2TotalQuantity { get; set; }

    public int QuotationCountChange { get; set; }
    public decimal TotalAmountChange { get; set; }
    public decimal QuantityChange { get; set; }
    public decimal ChangePercentage { get; set; }

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

    public int Period1QuotationCount { get; set; }
    public decimal Period1TotalAmount { get; set; }

    public int Period2QuotationCount { get; set; }
    public decimal Period2TotalAmount { get; set; }

    public int QuotationCountChange { get; set; }
    public decimal TotalAmountChange { get; set; }
    public decimal ChangePercentage { get; set; }
    public string Currency { get; set; }

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

internal class ProductInfo
{
    public string ItemCode { get; set; }
    public string path { get; set; }
    public string ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public decimal TotalAmount { get; set; }
    public int QuotationCount { get; set; }
    public HashSet<string> QuotationIds { get; set; }
}

internal class CustomerInfo
{
    public string CustomerName { get; set; }
    public string AccountCode { get; set; }
    public decimal TotalAmount { get; set; }
    public int QuotationCount { get; set; }
    public HashSet<string> QuotationIds { get; set; }
    public string Currency { get; set; }
}

public class DetailedProductComparisonResponseDTO
{
    public bool Success { get; set; }
    public string Message { get; set; }

    public string CurrentPeriod { get; set; }
    public string PreviousPeriod { get; set; }

    public decimal CurrentAmount { get; set; }
    public decimal PreviousAmount { get; set; }
    public decimal AmountDifference { get; set; }
    public decimal AmountDifferencePercent { get; set; }
    public string AmountTrend { get; set; }

    public decimal CurrentQuantity { get; set; }
    public decimal PreviousQuantity { get; set; }
    public decimal QuantityDifference { get; set; }
    public decimal QuantityDifferencePercent { get; set; }
    public string QuantityTrend { get; set; }

    public int CurrentProductCount { get; set; }
    public int PreviousProductCount { get; set; }
    public int ProductDifference { get; set; }
    public decimal ProductDifferencePercent { get; set; }
    public string ProductTrend { get; set; }

    public int CurrentQuotationCount { get; set; }
    public int PreviousQuotationCount { get; set; }
    public int QuotationDifference { get; set; }
    public decimal QuotationDifferencePercent { get; set; }
    public string QuotationTrend { get; set; }

    public decimal CurrentAverageUnitPrice { get; set; }
    public decimal PreviousAverageUnitPrice { get; set; }
    public decimal AverageUnitPriceDifference { get; set; }

    public List<DetailedTopProductDTO> CurrentTopProducts { get; set; }
    public List<DetailedTopProductDTO> PreviousTopProducts { get; set; }

    public List<DetailedProductComparisonDTO> ProductComparisons { get; set; }
}

public class DetailedCustomerComparisonResponseDTO
{
    public bool Success { get; set; }
    public string Message { get; set; }

    public string CurrentPeriod { get; set; }
    public string PreviousPeriod { get; set; }

    public decimal CurrentAmount { get; set; }
    public decimal PreviousAmount { get; set; }
    public decimal AmountDifference { get; set; }
    public decimal AmountDifferencePercent { get; set; }
    public string AmountTrend { get; set; }

    public int CurrentQuotationCount { get; set; }
    public int PreviousQuotationCount { get; set; }
    public int QuotationDifference { get; set; }
    public decimal QuotationDifferencePercent { get; set; }
    public string QuotationTrend { get; set; }

    public int CurrentCustomerCount { get; set; }
    public int PreviousCustomerCount { get; set; }
    public int CustomerDifference { get; set; }
    public decimal CustomerDifferencePercent { get; set; }
    public string CustomerTrend { get; set; }

    public decimal CurrentAverageQuotationAmount { get; set; }
    public decimal PreviousAverageQuotationAmount { get; set; }
    public decimal AverageQuotationAmountDifference { get; set; }

    public List<DetailedTopCustomerDTO> CurrentTopCustomers { get; set; }
    public List<DetailedTopCustomerDTO> PreviousTopCustomers { get; set; }

    public List<DetailedCustomerComparisonDTO> CustomerComparisons { get; set; }
}

public class DetailedTopProductDTO
{
    public int Rank { get; set; }
    public string ItemCode { get; set; }
    public string ItemDescription { get; set; }
    public decimal TotalQuantity { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal UnitPrice { get; set; }
    public int QuotationCount { get; set; }
    public decimal AverageQuantityPerQuotation { get; set; }
    public string path { get; set; }  // ← Görsel URL
}

public class DetailedTopCustomerDTO
{
    public int Rank { get; set; }
    public string CustomerName { get; set; }
    public string AccountCode { get; set; }
    public decimal TotalAmount { get; set; }
    public int QuotationCount { get; set; }
    public decimal AverageQuotationAmount { get; set; }
    public string Currency { get; set; }
}

public class DetailedProductComparisonDTO
{
    public string ItemCode { get; set; }
    public string ItemDescription { get; set; }

    public int? CurrentRank { get; set; }
    public decimal CurrentQuantity { get; set; }
    public decimal CurrentAmount { get; set; }
    public decimal CurrentPercentage { get; set; }

    public int? PreviousRank { get; set; }
    public decimal PreviousQuantity { get; set; }
    public decimal PreviousAmount { get; set; }
    public decimal PreviousPercentage { get; set; }

    public int? RankChange { get; set; }
    public decimal QuantityChange { get; set; }
    public decimal AmountChange { get; set; }
    public decimal AmountChangePercent { get; set; }

    public string Status { get; set; }
    public string path { get; set; }  // ← Görsel URL
}

public class DetailedCustomerComparisonDTO
{
    public string CustomerName { get; set; }
    public string AccountCode { get; set; }

    public int? CurrentRank { get; set; }
    public int CurrentQuotationCount { get; set; }
    public decimal CurrentAmount { get; set; }
    public decimal CurrentPercentage { get; set; }

    public int? PreviousRank { get; set; }
    public int PreviousQuotationCount { get; set; }
    public decimal PreviousAmount { get; set; }
    public decimal PreviousPercentage { get; set; }

    public int? RankChange { get; set; }
    public int QuotationCountChange { get; set; }
    public decimal AmountChange { get; set; }
    public decimal AmountChangePercent { get; set; }

    public string Status { get; set; }
}