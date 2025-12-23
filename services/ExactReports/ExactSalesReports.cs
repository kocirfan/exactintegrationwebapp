

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using ShopifyProductApp.Services;
using System.Text;
using ExactOnline.Models;
using ExactOnline.Converters;
using System.Text.RegularExpressions;

public class ExactSalesReports
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

    public ExactSalesReports(
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

    //yeni
    //iki tarih arasındaki veriyi çeker
    public async Task<List<TopProductDto>> GetTopSalesProductsAsync(
        DateTime? startDate = null,
        DateTime? endDate = null,
        int topCount = 5)
    {
        try
        {
            _logger.LogInformation($"📊 Top {topCount} Ürün Çıkartılıyor - Başlangıç: {startDate:yyyy-MM-dd}, Bitiş: {endDate:yyyy-MM-dd}");

            // Tarih aralığını belirle (eğer verilmemişse son 1 yıl)
            var actualEndDate = endDate ?? DateTime.UtcNow;
            var actualStartDate = startDate ?? actualEndDate.AddYears(-1);

            // Önce siparişleri al
            var rawOrdersJson = await GetAllSalesOrderByDateRangeAsync(actualStartDate, actualEndDate);

            if (rawOrdersJson == "[]")
            {
                _logger.LogWarning("⚠️ Sipariş verisi alınamadı");
                return new List<TopProductDto>();
            }

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

            using var doc = JsonDocument.Parse(rawOrdersJson);
            var salesOrdersData = new Dictionary<string, ProductSalesData>();

            if (!doc.RootElement.TryGetProperty("d", out var dataElement))
            {
                _logger.LogError("❌ Beklenmeyen JSON yapısı: 'd' property bulunamadı");
                return null;
            }

            JsonElement resultsElement;
            if (dataElement.ValueKind == JsonValueKind.Object &&
                dataElement.TryGetProperty("results", out var res))
            {
                resultsElement = res;
            }
            else if (dataElement.ValueKind == JsonValueKind.Array)
            {
                resultsElement = dataElement;
            }
            else
            {
                _logger.LogError("❌ Beklenmeyen JSON yapısı");
                return null;
            }

            var orderCount = 0;
            var lineCount = 0;

            foreach (var salesOrder in resultsElement.EnumerateArray())
            {
                orderCount++;

                if (!salesOrder.TryGetProperty("SalesOrderLines", out var salesOrderLinesRef))
                {
                    _logger.LogWarning($"⚠️ Sipariş {orderCount}: SalesOrderLines property bulunamadı");
                    continue;
                }

                if (!salesOrderLinesRef.TryGetProperty("__deferred", out var deferredElement))
                {
                    continue;
                }

                if (!deferredElement.TryGetProperty("uri", out var uriElement))
                {
                    continue;
                }

                var linesUrl = uriElement.GetString();
                if (string.IsNullOrEmpty(linesUrl))
                {
                    continue;
                }

                _logger.LogInformation($"📡 Sipariş {orderCount}: SalesOrderLines çekiliyor...");

                try
                {
                    var linesResponse = await client.GetAsync(linesUrl);
                    if (!linesResponse.IsSuccessStatusCode)
                    {
                        _logger.LogWarning($"⚠️ Satır çekme hatası: {linesResponse.StatusCode}");
                        continue;
                    }

                    var linesJson = await linesResponse.Content.ReadAsStringAsync();
                    using var linesDoc = JsonDocument.Parse(linesJson);

                    if (!linesDoc.RootElement.TryGetProperty("d", out var linesDataElement))
                    {
                        continue;
                    }

                    JsonElement linesResultsElement;
                    if (linesDataElement.ValueKind == JsonValueKind.Object &&
                        linesDataElement.TryGetProperty("results", out var linesRes))
                    {
                        linesResultsElement = linesRes;
                    }
                    else if (linesDataElement.ValueKind == JsonValueKind.Array)
                    {
                        linesResultsElement = linesDataElement;
                    }
                    else
                    {
                        continue;
                    }

                    var orderLineCount = 0;
                    foreach (var line in linesResultsElement.EnumerateArray())
                    {
                        orderLineCount++;

                        var itemCode = line.TryGetProperty("ItemCode", out var code)
                            ? code.GetString() ?? "" : "";
                        var itemDescription = line.TryGetProperty("ItemDescription", out var desc)
                            ? desc.GetString() ?? "" : "";
                        var quantity = line.TryGetProperty("Quantity", out var qty)
                            ? qty.GetDouble() : 0;
                        var unitPrice = line.TryGetProperty("UnitPrice", out var price)
                            ? price.GetDouble() : 0;
                        var lineAmount = line.TryGetProperty("AmountDC", out var amount)
                            ? amount.GetDouble() : 0;

                        // NaN kontrolü
                        quantity = SanitizeDouble(quantity);
                        unitPrice = SanitizeDouble(unitPrice);
                        lineAmount = SanitizeDouble(lineAmount);

                        if (string.IsNullOrEmpty(itemCode))
                            continue;

                        if (salesOrdersData.ContainsKey(itemCode))
                        {
                            salesOrdersData[itemCode].TotalQuantity += quantity;
                            salesOrdersData[itemCode].TotalAmount += lineAmount;
                            salesOrdersData[itemCode].TransactionCount++;
                        }
                        else
                        {
                            salesOrdersData[itemCode] = new ProductSalesData
                            {
                                ItemCode = itemCode,
                                ItemDescription = itemDescription,
                                TotalQuantity = quantity,
                                TotalAmount = lineAmount,
                                UnitPrice = unitPrice,
                                TransactionCount = 1
                            };
                        }

                        lineCount++;
                    }

                    _logger.LogInformation($" ✅ Sipariş {orderCount}: {orderLineCount} satır işlendi. Toplam: {lineCount} satır");

                    await Task.Delay(100);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"⚠️ Satır işleme hatası: {ex.Message}");
                    continue;
                }
            }

            if (!salesOrdersData.Any())
            {
                _logger.LogWarning("⚠️ Satış verisi bulunamadı");
                return new List<TopProductDto>();
            }

            // Parametrik olarak istenen sayıda ürün döndür
            var topProducts = salesOrdersData.Values
                .OrderByDescending(x => x.TotalQuantity)
                .Take(topCount)
                .Select((p, index) => new TopProductDto
                {
                    Rank = index + 1,
                    ItemCode = p.ItemCode,
                    ItemDescription = p.ItemDescription,
                    TotalQuantity = SanitizeDouble(p.TotalQuantity),
                    TotalAmount = SanitizeDouble(p.TotalAmount),
                    UnitPrice = SanitizeDouble(p.UnitPrice),
                    TransactionCount = p.TransactionCount,
                    AverageQuantityPerTransaction = SanitizeDouble(
                        p.TransactionCount > 0 ? p.TotalQuantity / p.TransactionCount : 0)
                })
                .ToList();

            _logger.LogInformation($"✅ {orderCount} sipariş işlendi, {lineCount} satır alındı");
            _logger.LogInformation($"✅ Top {topProducts.Count} ürün bulundu");
            _logger.LogInformation($"💰 Toplam Satış Tutarı: ₺{SanitizeDouble(salesOrdersData.Values.Sum(x => x.TotalAmount)):N2}");

            return topProducts;
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Hata: {ex.Message}");
            return null;
        }
    }

    // Yardımcı metod: Tarih aralığına göre siparişleri getir
    private async Task<string> GetAllSalesOrderByDateRangeAsync(DateTime startDate, DateTime endDate)
    {
        try
        {
            var exactService = _serviceProvider.GetRequiredService<ExactService>();
            var token = await exactService.GetValidToken();

            if (token == null)
            {
                _logger.LogError("❌ Geçerli bir token alınamadı");
                return "[]";
            }

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // OData filter: tarih aralığı filtresi
            var startDateStr = startDate.ToString("yyyy-MM-dd");
            var endDateStr = endDate.ToString("yyyy-MM-dd");
            int pageSize = 60;
            int skip = 0;

            var filter = $"Created ge datetime'{startDateStr}' and Created le datetime'{endDateStr}'";
            //var url = $"{_baseUrl}/SalesOrder?$filter={filter}";
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/salesorder/SalesOrders" +
                        $"?{filter}" +
                        $"&$top={pageSize}" +
                        $"&$skip={skip}";

            _logger.LogInformation($"📡 API URL: {url}");

            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"❌ API Hatası: {response.StatusCode}");
                return "[]";
            }

            var json = await response.Content.ReadAsStringAsync();
            return json;
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ GetAllSalesOrderByDateRangeAsync Hatası: {ex.Message}");
            return "[]";
        }
    }





    /// <summary>
    /// Belirtilen zaman aralığında tüm satış siparişlerini alır
    /// </summary>
    /// <param name="period">Zaman periyodu (OneDay, OneWeek, OneMonth, vb.)</param>
    /// <returns>JSON formatında satış siparişleri</returns>
    public async Task<string> GetAllSalesOrderAsync(TimePeriod period = TimePeriod.OneYear)
    {
        var exactService = _serviceProvider.GetRequiredService<ExactService>();

        var token = await exactService.GetValidToken();

        if (token == null)
        {
            _logger.LogError("❌ Token alınamadı");
            return "[]";
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var allSalesOrders = new List<JsonElement>();
            int pageSize = 60;
            int skip = 0;

            // Belirtilen periyoda göre başlangıç tarihini hesapla
            int daysBack = (int)period;
            var startDate = DateTime.UtcNow.AddDays(-daysBack).ToString("yyyy-MM-dd");

            _logger.LogInformation($"📅 Tarih Aralığı: {daysBack} gün öncesi ({startDate}) - Bugün");

            bool hasMoreData = true;
            int pageNumber = 1;

            while (hasMoreData)
            {
                var filter = $"$filter=Created ge datetime'{startDate}'";
                var url = $"{_baseUrl}/api/v1/{_divisionCode}/salesorder/SalesOrders" +
                         $"?{filter}" +
                         $"&$top={pageSize}" +
                         $"&$skip={skip}";

                _logger.LogInformation($"📄 Sayfa {pageNumber} çekiliyor... (Skip: {skip}, Toplam: {allSalesOrders.Count})");

                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"❌ API Hatası {response.StatusCode}");
                    break;
                }

                var content = await response.Content.ReadAsStringAsync();

                try
                {
                    var jsonDocument = JsonDocument.Parse(content);
                    var root = jsonDocument.RootElement;
                    JsonElement dataToProcess = default;
                    bool found = false;

                    // Case 1: "d" array olarak gelmiş
                    if (root.TryGetProperty("d", out var dProperty))
                    {
                        if (dProperty.ValueKind == JsonValueKind.Array)
                        {
                            dataToProcess = dProperty;
                            found = true;
                        }
                        // Case 2: "d" object içinde "results"
                        else if (dProperty.ValueKind == JsonValueKind.Object &&
                                 dProperty.TryGetProperty("results", out var results))
                        {
                            dataToProcess = results;
                            found = true;
                        }
                    }
                    // Case 3: "value" property
                    else if (root.TryGetProperty("value", out var valueElement))
                    {
                        dataToProcess = valueElement;
                        found = true;
                    }

                    if (!found)
                    {
                        _logger.LogWarning("⚠️ Beklenmeyen JSON yapısı");
                        break;
                    }

                    if (dataToProcess.ValueKind == JsonValueKind.Array)
                    {
                        var items = dataToProcess.EnumerateArray().ToList();

                        if (items.Count == 0)
                        {
                            hasMoreData = false;
                            _logger.LogInformation("✓ Tüm veriler alındı");
                        }
                        else
                        {
                            allSalesOrders.AddRange(items);
                            skip += pageSize;
                            pageNumber++;
                        }
                    }
                    else
                    {
                        hasMoreData = false;
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogError($"❌ JSON Parse Hatası: {ex.Message}");
                    break;
                }

                await Task.Delay(500);
            }

            _logger.LogInformation($"✅ Toplam {allSalesOrders.Count} satış siparişi başarıyla alındı");

            var finalResult = new { d = allSalesOrders };
            return JsonSerializer.Serialize(finalResult, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Hata oluştu: {ex.Message}");
            return "[]";
        }
    }

    /// <summary>
    /// Belirtilen zaman aralığında en çok satılan ürünleri getirir
    /// </summary>
    /// <param name="period">Zaman periyodu</param>
    /// <param name="topCount">Kaç ürün istediğini belirt (5, 10, 15, vb.)</param>
    /// <returns>Top ürünlerin listesi</returns>
    public async Task<List<TopProductDto>> GetTopSalesPeriodProductsAsync(
        TimePeriod period = TimePeriod.OneYear,
        int topCount = 5)
    {
        try
        {
            _logger.LogInformation($"📊 Top {topCount} Ürün Çıkartılıyor - Periyod: {period}");

            // Önce siparişleri al
            var rawOrdersJson = await GetAllSalesOrderAsync(period);

            if (rawOrdersJson == "[]")
            {
                _logger.LogWarning("⚠️ Sipariş verisi alınamadı");
                return new List<TopProductDto>();
            }

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

            using var doc = JsonDocument.Parse(rawOrdersJson);
            var salesOrdersData = new Dictionary<string, ProductSalesData>();

            if (!doc.RootElement.TryGetProperty("d", out var dataElement))
            {
                _logger.LogError("❌ Beklenmeyen JSON yapısı: 'd' property bulunamadı");
                return null;
            }

            JsonElement resultsElement;
            if (dataElement.ValueKind == JsonValueKind.Object &&
                dataElement.TryGetProperty("results", out var res))
            {
                resultsElement = res;
            }
            else if (dataElement.ValueKind == JsonValueKind.Array)
            {
                resultsElement = dataElement;
            }
            else
            {
                _logger.LogError("❌ Beklenmeyen JSON yapısı");
                return null;
            }

            var orderCount = 0;
            var lineCount = 0;

            foreach (var salesOrder in resultsElement.EnumerateArray())
            {
                orderCount++;

                if (!salesOrder.TryGetProperty("SalesOrderLines", out var salesOrderLinesRef))
                {
                    _logger.LogWarning($"⚠️ Sipariş {orderCount}: SalesOrderLines property bulunamadı");
                    continue;
                }

                if (!salesOrderLinesRef.TryGetProperty("__deferred", out var deferredElement))
                {
                    continue;
                }

                if (!deferredElement.TryGetProperty("uri", out var uriElement))
                {
                    continue;
                }

                var linesUrl = uriElement.GetString();
                if (string.IsNullOrEmpty(linesUrl))
                {
                    continue;
                }

                _logger.LogInformation($"📡 Sipariş {orderCount}: SalesOrderLines çekiliyor...");

                try
                {
                    var linesResponse = await client.GetAsync(linesUrl);
                    if (!linesResponse.IsSuccessStatusCode)
                    {
                        _logger.LogWarning($"⚠️ Satır çekme hatası: {linesResponse.StatusCode}");
                        continue;
                    }

                    var linesJson = await linesResponse.Content.ReadAsStringAsync();
                    using var linesDoc = JsonDocument.Parse(linesJson);

                    if (!linesDoc.RootElement.TryGetProperty("d", out var linesDataElement))
                    {
                        continue;
                    }

                    JsonElement linesResultsElement;
                    if (linesDataElement.ValueKind == JsonValueKind.Object &&
                        linesDataElement.TryGetProperty("results", out var linesRes))
                    {
                        linesResultsElement = linesRes;
                    }
                    else if (linesDataElement.ValueKind == JsonValueKind.Array)
                    {
                        linesResultsElement = linesDataElement;
                    }
                    else
                    {
                        continue;
                    }

                    var orderLineCount = 0;
                    foreach (var line in linesResultsElement.EnumerateArray())
                    {
                        orderLineCount++;

                        var itemCode = line.TryGetProperty("ItemCode", out var code)
                            ? code.GetString() ?? "" : "";
                        var itemDescription = line.TryGetProperty("ItemDescription", out var desc)
                            ? desc.GetString() ?? "" : "";
                        var quantity = line.TryGetProperty("Quantity", out var qty)
                            ? qty.GetDouble() : 0;
                        var unitPrice = line.TryGetProperty("UnitPrice", out var price)
                            ? price.GetDouble() : 0;
                        var lineAmount = line.TryGetProperty("AmountDC", out var amount)
                            ? amount.GetDouble() : 0;

                        // NaN kontrolü
                        quantity = SanitizeDouble(quantity);
                        unitPrice = SanitizeDouble(unitPrice);
                        lineAmount = SanitizeDouble(lineAmount);

                        if (string.IsNullOrEmpty(itemCode))
                            continue;

                        if (salesOrdersData.ContainsKey(itemCode))
                        {
                            salesOrdersData[itemCode].TotalQuantity += quantity;
                            salesOrdersData[itemCode].TotalAmount += lineAmount;
                            salesOrdersData[itemCode].TransactionCount++;
                        }
                        else
                        {
                            salesOrdersData[itemCode] = new ProductSalesData
                            {
                                ItemCode = itemCode,
                                ItemDescription = itemDescription,
                                TotalQuantity = quantity,
                                TotalAmount = lineAmount,
                                UnitPrice = unitPrice,
                                TransactionCount = 1
                            };
                        }

                        lineCount++;
                    }

                    _logger.LogInformation($" ✅ Sipariş {orderCount}: {orderLineCount} satır işlendi. Toplam: {lineCount} satır");

                    await Task.Delay(100);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"⚠️ Satır işleme hatası: {ex.Message}");
                    continue;
                }
            }

            if (!salesOrdersData.Any())
            {
                _logger.LogWarning("⚠️ Satış verisi bulunamadı");
                return new List<TopProductDto>();
            }

            // Parametrik olarak istenen sayıda ürün döndür
            var topProducts = salesOrdersData.Values
                .OrderByDescending(x => x.TotalQuantity)
                .Take(topCount)
                .Select((p, index) => new TopProductDto
                {
                    Rank = index + 1,
                    ItemCode = p.ItemCode,
                    ItemDescription = p.ItemDescription,
                    TotalQuantity = SanitizeDouble(p.TotalQuantity),
                    TotalAmount = SanitizeDouble(p.TotalAmount),
                    UnitPrice = SanitizeDouble(p.UnitPrice),
                    TransactionCount = p.TransactionCount,
                    AverageQuantityPerTransaction = SanitizeDouble(
                        p.TransactionCount > 0 ? p.TotalQuantity / p.TransactionCount : 0)
                })
                .ToList();

            _logger.LogInformation($"✅ {orderCount} sipariş işlendi, {lineCount} satır alındı");
            _logger.LogInformation($"✅ Top {topProducts.Count} ürün bulundu");
            _logger.LogInformation($"💰 Toplam Satış Tutarı: ₺{SanitizeDouble(salesOrdersData.Values.Sum(x => x.TotalAmount)):N2}");

            return topProducts;
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Hata: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Belirtilen kriterlere göre ürün performansını analiz eder
    /// </summary>
    public async Task<SalesAnalysisDto> AnalyzeSalesAsync(
        TimePeriod period = TimePeriod.OneYear,
        int topProductCount = 5)
    {
        try
        {
            var topProducts = await GetTopSalesPeriodProductsAsync(period, topProductCount);

            if (topProducts == null || !topProducts.Any())
            {
                return new SalesAnalysisDto { Success = false, Message = "Veri alınamadı" };
            }

            var totalQuantity = topProducts.Sum(x => x.TotalQuantity);
            var totalAmount = topProducts.Sum(x => x.TotalAmount);
            var averageUnitPrice = topProducts.Average(x => x.UnitPrice);

            return new SalesAnalysisDto
            {
                Success = true,
                Period = period.ToString(),
                TopProductCount = topProductCount,
                TotalProductCount = topProducts.Count,
                TotalQuantitySold = totalQuantity,
                TotalSalesAmount = totalAmount,
                AverageUnitPrice = averageUnitPrice,
                TopProducts = topProducts,
                Message = $"✅ Analiz başarılı - {topProducts.Count} ürün bulundu"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Analiz hatası: {ex.Message}");
            return new SalesAnalysisDto
            {
                Success = false,
                Message = $"Hata oluştu: {ex.Message}"
            };
        }
    }

    private double SanitizeDouble(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0;
        return value;
    }


    /// <summary>
    /// Belirtilen zaman aralığında satış siparişlerini alır
    /// </summary>

    public async Task<ProductComparisonAnalysisDto> CompareDateRangesAsync(
        DateRangeQuery currentRange,
        DateRangeQuery previousRange,
        int topCount = 5)
    {
        try
        {
            _logger.LogInformation($"📊 Tarih Aralığı Karşılaştırması Başlatıldı");
            _logger.LogInformation($"   - Şimdiki: {currentRange.Description} ({currentRange})");
            _logger.LogInformation($"   - Önceki: {previousRange.Description} ({previousRange})");

            // Şimdiki dönemin verilerini al (tarih aralığı ile)
            var currentOrdersJson = await GetAllSalesOrderByDateRangeAsync(
                currentRange.StartDate,
                currentRange.EndDate);

            // Önceki dönemin verilerini al (tarih aralığı ile)
            var previousOrdersJson = await GetAllSalesOrderByDateRangeAsync(
                previousRange.StartDate,
                previousRange.EndDate);

            if (currentOrdersJson == "[]" && previousOrdersJson == "[]")
            {
                return new ProductComparisonAnalysisDto
                {
                    Success = false,
                    Message = "Her iki dönem için de veri bulunamadı"
                };
            }

            // Ürün verilerini çıkart
            var currentProducts = await ExtractProductDataFromJson(currentOrdersJson, currentRange.Description);
            var previousProducts = await ExtractProductDataFromJson(previousOrdersJson, previousRange.Description);

            if (!currentProducts.Any() && !previousProducts.Any())
            {
                return new ProductComparisonAnalysisDto
                {
                    Success = false,
                    Message = "Ürün verisi bulunamadı"
                };
            }

            // Top ürünleri seç
            var currentTopProducts = currentProducts.Values
                .OrderByDescending(x => x.TotalQuantity)
                .Take(topCount)
                .Select((p, index) => new TopProductDto
                {
                    Rank = index + 1,
                    ItemCode = p.ItemCode,
                    ItemDescription = p.ItemDescription,
                    TotalQuantity = SanitizeDouble(p.TotalQuantity),
                    TotalAmount = SanitizeDouble(p.TotalAmount),
                    UnitPrice = 0,
                    TransactionCount = p.TransactionCount,
                    AverageQuantityPerTransaction = SanitizeDouble(
                        p.TransactionCount > 0 ? p.TotalQuantity / p.TransactionCount : 0)
                })
                .ToList();

            var previousTopProducts = previousProducts.Values
                .OrderByDescending(x => x.TotalQuantity)
                .Take(topCount)
                .Select((p, index) => new TopProductDto
                {
                    Rank = index + 1,
                    ItemCode = p.ItemCode,
                    ItemDescription = p.ItemDescription,
                    TotalQuantity = SanitizeDouble(p.TotalQuantity),
                    TotalAmount = SanitizeDouble(p.TotalAmount),
                    UnitPrice = 0,
                    TransactionCount = p.TransactionCount,
                    AverageQuantityPerTransaction = SanitizeDouble(
                        p.TransactionCount > 0 ? p.TotalQuantity / p.TransactionCount : 0)
                })
                .ToList();

            // Karşılaştırma yap
            var currentTotal = currentTopProducts.Sum(x => x.TotalAmount);
            var previousTotal = previousTopProducts.Sum(x => x.TotalAmount);

            var amountDifference = currentTotal - previousTotal;
            var amountDifferencePercent = previousTotal > 0
                ? (amountDifference / previousTotal) * 100
                : 0;

            var currentQuantity = currentTopProducts.Sum(x => x.TotalQuantity);
            var previousQuantity = previousTopProducts.Sum(x => x.TotalQuantity);

            var quantityDifference = currentQuantity - previousQuantity;
            var quantityDifferencePercent = previousQuantity > 0
                ? ((quantityDifference) / previousQuantity) * 100
                : 0;

            var currentProductCount = currentTopProducts.Count;
            var previousProductCount = previousTopProducts.Count;

            var productDifference = currentProductCount - previousProductCount;
            var productDifferencePercent = previousProductCount > 0
                ? ((double)productDifference / previousProductCount) * 100
                : 0;

            var productComparisons = CompareProductLists(currentTopProducts, previousTopProducts);

            _logger.LogInformation($"✅ Karşılaştırma tamamlandı");
            _logger.LogInformation($"   - Şimdiki: ₺{currentTotal:N2} ({currentQuantity:F2} birim, {currentProductCount} ürün)");
            _logger.LogInformation($"   - Önceki: ₺{previousTotal:N2} ({previousQuantity:F2} birim, {previousProductCount} ürün)");
            _logger.LogInformation($"   - Fark: {amountDifferencePercent:+0.00;-0.00;0.00}%");

            return new ProductComparisonAnalysisDto
            {
                Success = true,
                Message = "✅ Tarih aralığı karşılaştırması başarılı",
                CurrentPeriod = currentRange.Description,
                PreviousPeriod = previousRange.Description,

                CurrentAmount = SanitizeDouble(currentTotal),
                PreviousAmount = SanitizeDouble(previousTotal),
                AmountDifference = SanitizeDouble(amountDifference),
                AmountDifferencePercent = SanitizeDouble(amountDifferencePercent),
                AmountTrend = GetTrend(amountDifferencePercent),

                CurrentQuantity = SanitizeDouble(currentQuantity),
                PreviousQuantity = SanitizeDouble(previousQuantity),
                QuantityDifference = SanitizeDouble(quantityDifference),
                QuantityDifferencePercent = SanitizeDouble(quantityDifferencePercent),
                QuantityTrend = GetTrend(quantityDifferencePercent),

                CurrentProductCount = currentProductCount,
                PreviousProductCount = previousProductCount,
                ProductDifference = productDifference,
                ProductDifferencePercent = SanitizeDouble(productDifferencePercent),
                ProductTrend = GetTrend(productDifferencePercent),

                CurrentAverageUnitPrice = currentQuantity > 0 ? currentTotal / currentQuantity : 0,
                PreviousAverageUnitPrice = previousQuantity > 0 ? previousTotal / previousQuantity : 0,
                AverageUnitPriceDifference = (currentQuantity > 0 ? currentTotal / currentQuantity : 0) -
                                             (previousQuantity > 0 ? previousTotal / previousQuantity : 0),

                CurrentTopProducts = currentTopProducts,
                PreviousTopProducts = previousTopProducts,
                ProductComparisons = productComparisons
            };
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Tarih aralığı karşılaştırması hatası: {ex.Message}");
            return new ProductComparisonAnalysisDto
            {
                Success = false,
                Message = $"Hata oluştu: {ex.Message}"
            };
        }
    }
    /// <summary>
    /// JSON'dan ürün verilerini çıkart
    /// </summary>
    private async Task<Dictionary<string, ProductSalesData>> ExtractProductDataFromJson(
        string rawOrdersJson,
        string periodDescription)
    {
        var productData = new Dictionary<string, ProductSalesData>();
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

        //using var doc = JsonDocument.Parse(rawOrdersJson);
        var salesOrdersData = new Dictionary<string, ProductSalesData>();
        if (rawOrdersJson == "[]")
        {
            _logger.LogWarning($"⚠️ {periodDescription}: Veri bulunamadı");
            return productData;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawOrdersJson);

            if (!doc.RootElement.TryGetProperty("d", out var dataElement))
            {
                _logger.LogError($"❌ {periodDescription}: 'd' property bulunamadı");
                return productData;
            }

            JsonElement resultsElement;
            if (dataElement.ValueKind == JsonValueKind.Object &&
                dataElement.TryGetProperty("results", out var res))
            {
                resultsElement = res;
            }
            else if (dataElement.ValueKind == JsonValueKind.Array)
            {
                resultsElement = dataElement;
            }
            else
            {
                _logger.LogError($"❌ {periodDescription}: Beklenmeyen JSON yapısı");
                return productData;
            }

            var orderCount = 0;
            var lineCount = 0;
            foreach (var salesOrder in resultsElement.EnumerateArray())
            {
                orderCount++;

                if (!salesOrder.TryGetProperty("SalesOrderLines", out var salesOrderLinesRef))
                {
                    _logger.LogWarning($"⚠️ Sipariş {orderCount}: SalesOrderLines property bulunamadı");
                    continue;
                }

                if (!salesOrderLinesRef.TryGetProperty("__deferred", out var deferredElement))
                {
                    continue;
                }

                if (!deferredElement.TryGetProperty("uri", out var uriElement))
                {
                    continue;
                }

                var linesUrl = uriElement.GetString();
                if (string.IsNullOrEmpty(linesUrl))
                {
                    continue;
                }

                _logger.LogInformation($"📡 Sipariş {orderCount}: SalesOrderLines çekiliyor...");

                try
                {
                    var linesResponse = await client.GetAsync(linesUrl);
                    if (!linesResponse.IsSuccessStatusCode)
                    {
                        _logger.LogWarning($"⚠️ Satır çekme hatası: {linesResponse.StatusCode}");
                        continue;
                    }

                    var linesJson = await linesResponse.Content.ReadAsStringAsync();
                    using var linesDoc = JsonDocument.Parse(linesJson);

                    if (!linesDoc.RootElement.TryGetProperty("d", out var linesDataElement))
                    {
                        continue;
                    }

                    JsonElement linesResultsElement;
                    if (linesDataElement.ValueKind == JsonValueKind.Object &&
                        linesDataElement.TryGetProperty("results", out var linesRes))
                    {
                        linesResultsElement = linesRes;
                    }
                    else if (linesDataElement.ValueKind == JsonValueKind.Array)
                    {
                        linesResultsElement = linesDataElement;
                    }
                    else
                    {
                        continue;
                    }

                    var orderLineCount = 0;
                    foreach (var line in linesResultsElement.EnumerateArray())
                    {
                        orderLineCount++;

                        var itemCode = line.TryGetProperty("ItemCode", out var code)
                            ? code.GetString() ?? "" : "";
                        var itemDescription = line.TryGetProperty("ItemDescription", out var desc)
                            ? desc.GetString() ?? "" : "";
                        var quantity = line.TryGetProperty("Quantity", out var qty)
                            ? qty.GetDouble() : 0;
                        var unitPrice = line.TryGetProperty("UnitPrice", out var price)
                            ? price.GetDouble() : 0;
                        var lineAmount = line.TryGetProperty("AmountDC", out var amount)
                            ? amount.GetDouble() : 0;

                        // NaN kontrolü
                        quantity = SanitizeDouble(quantity);
                        unitPrice = SanitizeDouble(unitPrice);
                        lineAmount = SanitizeDouble(lineAmount);

                        if (string.IsNullOrEmpty(itemCode))
                            continue;

                        if (salesOrdersData.ContainsKey(itemCode))
                        {
                            salesOrdersData[itemCode].TotalQuantity += quantity;
                            salesOrdersData[itemCode].TotalAmount += lineAmount;
                            salesOrdersData[itemCode].TransactionCount++;
                        }
                        else
                        {
                            salesOrdersData[itemCode] = new ProductSalesData
                            {
                                ItemCode = itemCode,
                                ItemDescription = itemDescription,
                                TotalQuantity = quantity,
                                TotalAmount = lineAmount,
                                UnitPrice = unitPrice,
                                TransactionCount = 1
                            };
                        }

                        lineCount++;
                    }

                    _logger.LogInformation($" ✅ Sipariş {orderCount}: {orderLineCount} satır işlendi. Toplam: {lineCount} satır");

                    await Task.Delay(100);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"⚠️ Satır işleme hatası: {ex.Message}");
                    continue;
                }
            }


            _logger.LogInformation($"✅ {periodDescription}: {orderCount} sipariş, {productData.Count} ürün");
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ {periodDescription} JSON çıkarma hatası: {ex.Message}");
        }

        return salesOrdersData;
    }
    /// <summary>
    /// İki ürün listesini karşılaştırır ve detaylı farkları hesaplar
    /// </summary>
    private List<ProductComparisonDetailDto> CompareProductLists(
        List<TopProductDto> currentProducts,
        List<TopProductDto> previousProducts)
    {
        var comparisons = new List<ProductComparisonDetailDto>();

        var previousDict = previousProducts
            .ToDictionary(x => x.ItemCode, x => x);

        foreach (var current in currentProducts)
        {
            var comparison = new ProductComparisonDetailDto
            {
                ItemCode = current.ItemCode,
                ItemDescription = current.ItemDescription,
                CurrentRank = current.Rank,
                CurrentQuantity = SanitizeDouble(current.TotalQuantity),
                CurrentAmount = SanitizeDouble(current.TotalAmount),
                CurrentPercentage = SanitizeDouble(current.TotalQuantity)
            };

            if (previousDict.TryGetValue(current.ItemCode, out var previous))
            {
                comparison.PreviousRank = previous.Rank;
                comparison.PreviousQuantity = SanitizeDouble(previous.TotalQuantity);
                comparison.PreviousAmount = SanitizeDouble(previous.TotalAmount);
                comparison.PreviousPercentage = SanitizeDouble(previous.TotalQuantity);

                // Farklılıkları hesapla
                comparison.RankChange = previous.Rank - current.Rank; // Negatif = düştü, pozitif = yükseldi
                comparison.QuantityChange = SanitizeDouble(current.TotalQuantity - previous.TotalQuantity);
                comparison.AmountChange = SanitizeDouble(current.TotalAmount - previous.TotalAmount);
                comparison.AmountChangePercent = previous.TotalAmount > 0
                    ? (comparison.AmountChange / previous.TotalAmount) * 100
                    : 0;
                comparison.Status = GetProductStatus(comparison.QuantityChange, comparison.AmountChange);
            }
            else
            {
                comparison.Status = "🆕 Yeni"; // Yeni ürün
            }

            comparisons.Add(comparison);
        }

        // Önceki dönemde var ama şimdiki dönemde top'ta olmayan ürünler
        foreach (var previous in previousProducts)
        {
            if (!currentProducts.Any(x => x.ItemCode == previous.ItemCode))
            {
                comparisons.Add(new ProductComparisonDetailDto
                {
                    ItemCode = previous.ItemCode,
                    ItemDescription = previous.ItemDescription,
                    PreviousRank = previous.Rank,
                    PreviousQuantity = SanitizeDouble(previous.TotalQuantity),
                    PreviousAmount = SanitizeDouble(previous.TotalAmount),
                    PreviousPercentage = SanitizeDouble(previous.TotalQuantity),
                    Status = "❌ Çıktı" // Top'tan çıktı
                });
            }
        }

        return comparisons.OrderBy(x => x.CurrentRank ?? x.PreviousRank).ToList();
    }

    private string GetProductStatus(double quantityChange, double amountChange)
    {
        if (quantityChange > 0 && amountChange > 0)
            return "📈 Büyüyor";
        else if (quantityChange > 0 || amountChange > 0)
            return "📊 Gelişiyor";
        else if (quantityChange < 0 || amountChange < 0)
            return "📉 Düşüyor";
        else
            return "➡️ Sabit";
    }
    private string GetTrend(double percentageChange)
    {
        if (percentageChange > 5)
            return "📈 Güçlü Artış";
        else if (percentageChange > 0)
            return "📊 Hafif Artış";
        else if (percentageChange < -5)
            return "📉 Güçlü Azalış";
        else if (percentageChange < 0)
            return "📊 Hafif Azalış";
        else
            return "➡️ Sabit";
    }
}

public class ProductSalesData
{
    public string ItemCode { get; set; }
    public string ItemDescription { get; set; }
    public double TotalQuantity { get; set; }
    public double TotalAmount { get; set; }
    public double UnitPrice { get; set; }
    public int TransactionCount { get; set; }
}

public class TopProductDto
{
    public int Rank { get; set; }
    public string ItemCode { get; set; }
    public string ItemDescription { get; set; }
    public double TotalQuantity { get; set; }
    public double TotalAmount { get; set; }
    public double UnitPrice { get; set; }
    public int TransactionCount { get; set; }
    public double AverageQuantityPerTransaction { get; set; }
}
public class SalesAnalysisDto
{
    public bool Success { get; set; }
    public string Message { get; set; }
    public string Period { get; set; }
    public int TopProductCount { get; set; }
    public int TotalProductCount { get; set; }
    public double TotalQuantitySold { get; set; }
    public double TotalSalesAmount { get; set; }
    public double AverageUnitPrice { get; set; }
    public List<TopProductDto> TopProducts { get; set; }
}
public class ProductComparisonAnalysisDto
{
    public bool Success { get; set; }
    public string Message { get; set; }

    // Periyod Bilgileri
    public string CurrentPeriod { get; set; }
    public string PreviousPeriod { get; set; }

    // Satış Tutarı Karşılaştırması
    public double CurrentAmount { get; set; }
    public double PreviousAmount { get; set; }
    public double AmountDifference { get; set; }
    public double AmountDifferencePercent { get; set; }
    public string AmountTrend { get; set; }

    // Satış Miktarı Karşılaştırması
    public double CurrentQuantity { get; set; }
    public double PreviousQuantity { get; set; }
    public double QuantityDifference { get; set; }
    public double QuantityDifferencePercent { get; set; }
    public string QuantityTrend { get; set; }

    // Ürün Sayısı Karşılaştırması
    public int CurrentProductCount { get; set; }
    public int PreviousProductCount { get; set; }
    public int ProductDifference { get; set; }
    public double ProductDifferencePercent { get; set; }
    public string ProductTrend { get; set; }

    // Ortalama Değerler
    public double CurrentAverageUnitPrice { get; set; }
    public double PreviousAverageUnitPrice { get; set; }
    public double AverageUnitPriceDifference { get; set; }

    // Ürün Listeleri
    public List<TopProductDto> CurrentTopProducts { get; set; }
    public List<TopProductDto> PreviousTopProducts { get; set; }

    // Ürün Seviyesi Karşılaştırması
    public List<ProductComparisonDetailDto> ProductComparisons { get; set; }
}
/// <summary>
/// Ürün seviyesinde karşılaştırma detayları
/// </summary>
public class ProductComparisonDetailDto
{
    public string ItemCode { get; set; }
    public string ItemDescription { get; set; }

    // Şimdiki Dönem
    public int? CurrentRank { get; set; }
    public double CurrentQuantity { get; set; }
    public double CurrentAmount { get; set; }
    public double CurrentPercentage { get; set; }

    // Önceki Dönem
    public int? PreviousRank { get; set; }
    public double PreviousQuantity { get; set; }
    public double PreviousAmount { get; set; }
    public double PreviousPercentage { get; set; }

    // Farklılıklar
    public int? RankChange { get; set; } // Negatif = düştü, pozitif = yükseldi
    public double QuantityChange { get; set; }
    public double AmountChange { get; set; }
    public double AmountChangePercent { get; set; }

    // Durum
    public string Status { get; set; } // 📈 Büyüyor, 📉 Düşüyor, 🆕 Yeni, ❌ Çıktı
}