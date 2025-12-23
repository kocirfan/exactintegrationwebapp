using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using ShopifyProductApp.Services;
using System.Text;
using ExactOnline.Models;
using ExactOnline.Converters;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

public class CustomerReports
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


    public CustomerReports(
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


    //tarih ile
     public async Task<List<TopCustomerDto>> GetTopCustomersDateRangeAsync(
        DateTime startDate,
        DateTime endDate,
        int topCount = 5)
    {
        try
        {
            _logger.LogInformation($"👥 Top {topCount} Müşteri Çıkartılıyor - Periyod: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");

            // ExactSalesReports'u kullan
            var rawOrdersJson = await GetSalesOrderByDateRangeAsync(startDate, endDate);

            if (rawOrdersJson == "[]")
            {
                _logger.LogWarning("⚠️ Sipariş verisi alınamadı");
                return new List<TopCustomerDto>();
            }

            using var doc = JsonDocument.Parse(rawOrdersJson);
            var customerData = new Dictionary<string, CustomerSalesData>();

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

            foreach (var salesOrder in resultsElement.EnumerateArray())
            {
                orderCount++;

                // DeliverToName'i al
                var customerName = salesOrder.TryGetProperty("DeliverToName", out var name)
                    ? name.GetString() ?? "Bilinmeyen Müşteri"
                    : "Bilinmeyen Müşteri";

                // Sipariş tutarını al
                double orderAmount = 0;
                if (salesOrder.TryGetProperty("AmountDC", out var amount))
                {
                    orderAmount = SanitizeDouble(amount.GetDouble());
                }
                else if (salesOrder.TryGetProperty("AmountFC", out var topAmount))
                {
                    orderAmount = SanitizeDouble(topAmount.GetDouble());
                }

                if (string.IsNullOrWhiteSpace(customerName) || customerName == "Bilinmeyen Müşteri")
                {
                    _logger.LogWarning($"⚠️ Sipariş {orderCount}: Müşteri adı boş");
                    continue;
                }

                if (customerData.ContainsKey(customerName))
                {
                    customerData[customerName].TotalOrderAmount += orderAmount;
                    customerData[customerName].OrderCount++;
                    customerData[customerName].AverageOrderAmount =
                        customerData[customerName].TotalOrderAmount / customerData[customerName].OrderCount;
                }
                else
                {
                    customerData[customerName] = new CustomerSalesData
                    {
                        CustomerName = customerName,
                        TotalOrderAmount = orderAmount,
                        OrderCount = 1,
                        AverageOrderAmount = orderAmount
                    };
                }
            }

            if (!customerData.Any())
            {
                _logger.LogWarning("⚠️ Müşteri verisi bulunamadı");
                return new List<TopCustomerDto>();
            }

            var totalSalesAmount = customerData.Values.Sum(x => x.TotalOrderAmount);

            var topCustomers = customerData.Values
                .OrderByDescending(x => x.OrderCount)
                .ThenByDescending(x => x.TotalOrderAmount)
                .Take(topCount)
                .Select((c, index) => new TopCustomerDto
                {
                    Rank = index + 1,
                    CustomerName = c.CustomerName,
                    TotalOrders = c.OrderCount,
                    TotalOrderAmount = SanitizeDouble(c.TotalOrderAmount),
                    AverageOrderAmount = SanitizeDouble(c.AverageOrderAmount),
                    PercentageOfTotalSales = SanitizeDouble((c.TotalOrderAmount / totalSalesAmount) * 100)
                })
                .ToList();

            _logger.LogInformation($"✅ {orderCount} sipariş işlendi, {customerData.Count} farklı müşteri bulundu");
            _logger.LogInformation($"✅ Top {topCustomers.Count} müşteri listelendi");
            _logger.LogInformation($"💰 Toplam Satış Tutarı: ₺{SanitizeDouble(totalSalesAmount):N2}");

            return topCustomers;
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Müşteri analiz hatası: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Belirtilen zaman aralığında en çok sipariş veren müşterileri getirir
    /// </summary>
    public async Task<List<TopCustomerDto>> GetTopCustomersAsync(
        TimePeriod period = TimePeriod.OneYear,
        int topCount = 5)
    {
        try
        {
            _logger.LogInformation($"👥 Top {topCount} Müşteri Çıkartılıyor - Periyod: {period}");

            // ExactSalesReports'u kullan
            var rawOrdersJson = await GetAllSalesOrderAsync(period);

            if (rawOrdersJson == "[]")
            {
                _logger.LogWarning("⚠️ Sipariş verisi alınamadı");
                return new List<TopCustomerDto>();
            }

            using var doc = JsonDocument.Parse(rawOrdersJson);
            var customerData = new Dictionary<string, CustomerSalesData>();

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

            foreach (var salesOrder in resultsElement.EnumerateArray())
            {
                orderCount++;

                // DeliverToName'i al
                var customerName = salesOrder.TryGetProperty("DeliverToName", out var name)
                    ? name.GetString() ?? "Bilinmeyen Müşteri"
                    : "Bilinmeyen Müşteri";

                // Sipariş tutarını al
                double orderAmount = 0;
                if (salesOrder.TryGetProperty("AmountDC", out var amount))
                {
                    orderAmount = SanitizeDouble(amount.GetDouble());
                }
                else if (salesOrder.TryGetProperty("AmountFC", out var topAmount))
                {
                    orderAmount = SanitizeDouble(topAmount.GetDouble());
                }

                if (string.IsNullOrWhiteSpace(customerName) || customerName == "Bilinmeyen Müşteri")
                {
                    _logger.LogWarning($"⚠️ Sipariş {orderCount}: Müşteri adı boş");
                    continue;
                }

                if (customerData.ContainsKey(customerName))
                {
                    customerData[customerName].TotalOrderAmount += orderAmount;
                    customerData[customerName].OrderCount++;
                    customerData[customerName].AverageOrderAmount =
                        customerData[customerName].TotalOrderAmount / customerData[customerName].OrderCount;
                }
                else
                {
                    customerData[customerName] = new CustomerSalesData
                    {
                        CustomerName = customerName,
                        TotalOrderAmount = orderAmount,
                        OrderCount = 1,
                        AverageOrderAmount = orderAmount
                    };
                }
            }

            if (!customerData.Any())
            {
                _logger.LogWarning("⚠️ Müşteri verisi bulunamadı");
                return new List<TopCustomerDto>();
            }

            var totalSalesAmount = customerData.Values.Sum(x => x.TotalOrderAmount);

            var topCustomers = customerData.Values
                .OrderByDescending(x => x.OrderCount)
                .ThenByDescending(x => x.TotalOrderAmount)
                .Take(topCount)
                .Select((c, index) => new TopCustomerDto
                {
                    Rank = index + 1,
                    CustomerName = c.CustomerName,
                    TotalOrders = c.OrderCount,
                    TotalOrderAmount = SanitizeDouble(c.TotalOrderAmount),
                    AverageOrderAmount = SanitizeDouble(c.AverageOrderAmount),
                    PercentageOfTotalSales = SanitizeDouble((c.TotalOrderAmount / totalSalesAmount) * 100)
                })
                .ToList();

            _logger.LogInformation($"✅ {orderCount} sipariş işlendi, {customerData.Count} farklı müşteri bulundu");
            _logger.LogInformation($"✅ Top {topCustomers.Count} müşteri listelendi");
            _logger.LogInformation($"💰 Toplam Satış Tutarı: ₺{SanitizeDouble(totalSalesAmount):N2}");

            return topCustomers;
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Müşteri analiz hatası: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Belirtilen zaman aralığında müşteri performansını analiz eder
    /// </summary>
    public async Task<CustomerAnalysisDto> AnalyzeCustomersAsync(
        TimePeriod period = TimePeriod.OneYear,
        int topCustomerCount = 5)
    {
        try
        {
            var topCustomers = await GetTopCustomersAsync(period, topCustomerCount);

            if (topCustomers == null || !topCustomers.Any())
            {
                return new CustomerAnalysisDto
                {
                    Success = false,
                    Message = "Müşteri verisi alınamadı"
                };
            }

            var totalOrders = topCustomers.Sum(x => x.TotalOrders);
            var totalAmount = topCustomers.Sum(x => x.TotalOrderAmount);
            var averageOrderAmount = topCustomers.Average(x => x.AverageOrderAmount);
            var averageCustomerValue = totalAmount / topCustomers.Count;

            return new CustomerAnalysisDto
            {
                Success = true,
                Period = period.ToString(),
                TopCustomerCount = topCustomerCount,
                TotalCustomerCount = topCustomers.Count,
                TotalOrderCount = totalOrders,
                TotalSalesAmount = SanitizeDouble(totalAmount),
                AverageOrderAmount = SanitizeDouble(averageOrderAmount),
                AverageCustomerValue = SanitizeDouble(averageCustomerValue),
                TopCustomers = topCustomers,
                Message = $"✅ Müşteri analizi başarılı - {topCustomers.Count} müşteri bulundu"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Müşteri analizi hatası: {ex.Message}");
            return new CustomerAnalysisDto
            {
                Success = false,
                Message = $"Hata oluştu: {ex.Message}"
            };
        }
    }
    private List<CustomerComparisonDetailDto> CompareCustomerLists(
        List<TopCustomerDto> currentCustomers,
        List<TopCustomerDto> previousCustomers)
    {
        var comparisons = new List<CustomerComparisonDetailDto>();

        // Müşteriler için dictionary oluştur
        var previousDict = previousCustomers
            .ToDictionary(x => x.CustomerName, x => x);

        foreach (var current in currentCustomers)
        {
            var comparison = new CustomerComparisonDetailDto
            {
                CustomerName = current.CustomerName,
                CurrentRank = current.Rank,
                CurrentOrders = current.TotalOrders,
                CurrentAmount = SanitizeDouble(current.TotalOrderAmount),
                CurrentPercentage = SanitizeDouble(current.PercentageOfTotalSales)
            };

            if (previousDict.TryGetValue(current.CustomerName, out var previous))
            {
                comparison.PreviousRank = previous.Rank;
                comparison.PreviousOrders = previous.TotalOrders;
                comparison.PreviousAmount = SanitizeDouble(previous.TotalOrderAmount);
                comparison.PreviousPercentage = SanitizeDouble(previous.PercentageOfTotalSales);

                // Farklılıkları hesapla
                comparison.RankChange = previous.Rank - current.Rank; // Negatif = düştü, pozitif = yükseldi
                comparison.OrderChange = current.TotalOrders - previous.TotalOrders;
                comparison.AmountChange = SanitizeDouble(current.TotalOrderAmount - previous.TotalOrderAmount);
                comparison.AmountChangePercent = previous.TotalOrderAmount > 0
                    ? (comparison.AmountChange / previous.TotalOrderAmount) * 100
                    : 0;
                comparison.Status = GetCustomerStatus(comparison.OrderChange, comparison.AmountChange);
            }
            else
            {
                comparison.Status = "🆕 Yeni"; // Yeni müşteri
            }

            comparisons.Add(comparison);
        }

        // Önceki dönemde var ama şimdiki dönemde top'ta olmayan müşteriler
        foreach (var previous in previousCustomers)
        {
            if (!currentCustomers.Any(x => x.CustomerName == previous.CustomerName))
            {
                comparisons.Add(new CustomerComparisonDetailDto
                {
                    CustomerName = previous.CustomerName,
                    PreviousRank = previous.Rank,
                    PreviousOrders = previous.TotalOrders,
                    PreviousAmount = SanitizeDouble(previous.TotalOrderAmount),
                    PreviousPercentage = SanitizeDouble(previous.PercentageOfTotalSales),
                    Status = "❌ Çıktı" // Top'tan çıktı
                });
            }
        }

        return comparisons.OrderBy(x => x.CurrentRank ?? x.PreviousRank).ToList();
    }
    public async Task<CustomerComparisonAnalysisDto> ComparePeriodsAsync(
       TimePeriod currentPeriod = TimePeriod.OneMonth,
       TimePeriod previousPeriod = TimePeriod.OneMonth,
       int topCount = 5)
    {
        try
        {
            _logger.LogInformation($"📊 Periyod Karşılaştırması Başlatılıyor");
            _logger.LogInformation($"   - Şimdiki Periyod: {currentPeriod}");
            _logger.LogInformation($"   - Önceki Periyod: {previousPeriod}");

            // Şimdiki dönemin verilerini al
            var currentAnalysis = await AnalyzeCustomersAsync(currentPeriod, topCount);

            // Önceki dönemin verilerini al
            var previousAnalysis = await AnalyzeCustomersAsync(previousPeriod, topCount);

            if (!currentAnalysis.Success || !previousAnalysis.Success)
            {
                return new CustomerComparisonAnalysisDto
                {
                    Success = false,
                    Message = "Bir veya her iki dönemin verisi alınamadı"
                };
            }

            // Karşılaştırma verilerini hesapla
            var currentAmount = currentAnalysis.TotalSalesAmount;
            var previousAmount = previousAnalysis.TotalSalesAmount;

            var amountDifference = currentAmount - previousAmount;
            var amountDifferencePercent = previousAmount > 0
                ? (amountDifference / previousAmount) * 100
                : 0;

            var currentOrderCount = currentAnalysis.TotalOrderCount;
            var previousOrderCount = previousAnalysis.TotalOrderCount;

            var orderDifference = currentOrderCount - previousOrderCount;
            var orderDifferencePercent = previousOrderCount > 0
                ? ((double)orderDifference / previousOrderCount) * 100
                : 0;

            var currentCustomerCount = currentAnalysis.TotalCustomerCount;
            var previousCustomerCount = previousAnalysis.TotalCustomerCount;

            var customerDifference = currentCustomerCount - previousCustomerCount;
            var customerDifferencePercent = previousCustomerCount > 0
                ? ((double)customerDifference / previousCustomerCount) * 100
                : 0;

            // Müşteri seviyesinde karşılaştırma
            var customerComparisons = CompareCustomerLists(
                currentAnalysis.TopCustomers,
                previousAnalysis.TopCustomers);

            return new CustomerComparisonAnalysisDto
            {
                Success = true,
                Message = "✅ Periyod karşılaştırması başarılı",
                CurrentPeriod = currentPeriod.ToString(),
                PreviousPeriod = previousPeriod.ToString(),

                // Satış Tutarı Karşılaştırması
                CurrentAmount = SanitizeDouble(currentAmount),
                PreviousAmount = SanitizeDouble(previousAmount),
                AmountDifference = SanitizeDouble(amountDifference),
                AmountDifferencePercent = SanitizeDouble(amountDifferencePercent),
                AmountTrend = GetTrend(amountDifferencePercent),

                // Sipariş Sayısı Karşılaştırması
                CurrentOrderCount = currentOrderCount,
                PreviousOrderCount = previousOrderCount,
                OrderDifference = orderDifference,
                OrderDifferencePercent = SanitizeDouble(orderDifferencePercent),
                OrderTrend = GetTrend(orderDifferencePercent),

                // Müşteri Sayısı Karşılaştırması
                CurrentCustomerCount = currentCustomerCount,
                PreviousCustomerCount = previousCustomerCount,
                CustomerDifference = customerDifference,
                CustomerDifferencePercent = SanitizeDouble(customerDifferencePercent),
                CustomerTrend = GetTrend(customerDifferencePercent),

                // Ortalama Sipariş Tutarı
                CurrentAverageOrderAmount = SanitizeDouble(currentAnalysis.AverageOrderAmount),
                PreviousAverageOrderAmount = SanitizeDouble(previousAnalysis.AverageOrderAmount),
                AverageOrderDifference = SanitizeDouble(
                    currentAnalysis.AverageOrderAmount - previousAnalysis.AverageOrderAmount),

                // Müşteri Seviyesi Karşılaştırması
                CurrentTopCustomers = currentAnalysis.TopCustomers,
                PreviousTopCustomers = previousAnalysis.TopCustomers,
                CustomerComparisons = customerComparisons
            };
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Periyod karşılaştırması hatası: {ex.Message}");
            return new CustomerComparisonAnalysisDto
            {
                Success = false,
                Message = $"Hata oluştu: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// İki farklı tarih aralığını karşılaştırır (Geliştirilmiş versyon)
    /// </summary>
    public async Task<CustomerComparisonAnalysisDto> CompareDateRangesAsync(
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
            var currentOrdersJson = await GetSalesOrderByDateRangeAsync(
                currentRange.StartDate,
                currentRange.EndDate);

            // Önceki dönemin verilerini al (tarih aralığı ile)
            var previousOrdersJson = await GetSalesOrderByDateRangeAsync(
                previousRange.StartDate,
                previousRange.EndDate);

            if (currentOrdersJson == "[]" && previousOrdersJson == "[]")
            {
                return new CustomerComparisonAnalysisDto
                {
                    Success = false,
                    Message = "Her iki dönem için de veri bulunamadı"
                };
            }

            // Müşteri verilerini çıkart
            var currentCustomers = ExtractCustomerDataFromJson(currentOrdersJson, currentRange.Description);
            var previousCustomers = ExtractCustomerDataFromJson(previousOrdersJson, previousRange.Description);

            if (!currentCustomers.Any() && !previousCustomers.Any())
            {
                return new CustomerComparisonAnalysisDto
                {
                    Success = false,
                    Message = "Müşteri verisi bulunamadı"
                };
            }

            // Top müşterileri seç
            var currentTopCustomers = currentCustomers.Values
                .OrderByDescending(x => x.OrderCount)
                .ThenByDescending(x => x.TotalOrderAmount)
                .Take(topCount)
                .Select((c, index) => new TopCustomerDto
                {
                    Rank = index + 1,
                    CustomerName = c.CustomerName,
                    TotalOrders = c.OrderCount,
                    TotalOrderAmount = SanitizeDouble(c.TotalOrderAmount),
                    AverageOrderAmount = SanitizeDouble(c.AverageOrderAmount),
                    PercentageOfTotalSales = 0  // Aşağıda hesaplanacak
                })
                .ToList();

            var previousTopCustomers = previousCustomers.Values
                .OrderByDescending(x => x.OrderCount)
                .ThenByDescending(x => x.TotalOrderAmount)
                .Take(topCount)
                .Select((c, index) => new TopCustomerDto
                {
                    Rank = index + 1,
                    CustomerName = c.CustomerName,
                    TotalOrders = c.OrderCount,
                    TotalOrderAmount = SanitizeDouble(c.TotalOrderAmount),
                    AverageOrderAmount = SanitizeDouble(c.AverageOrderAmount),
                    PercentageOfTotalSales = 0  // Aşağıda hesaplanacak
                })
                .ToList();

            // Yüzdeleri hesapla
            var currentTotal = currentTopCustomers.Sum(x => x.TotalOrderAmount);
            var previousTotal = previousTopCustomers.Sum(x => x.TotalOrderAmount);

            currentTopCustomers.ForEach(c =>
                c.PercentageOfTotalSales = currentTotal > 0
                    ? (c.TotalOrderAmount / currentTotal) * 100
                    : 0);

            previousTopCustomers.ForEach(c =>
                c.PercentageOfTotalSales = previousTotal > 0
                    ? (c.TotalOrderAmount / previousTotal) * 100
                    : 0);

            // Karşılaştırma yap
            var amountDifference = currentTotal - previousTotal;
            var amountDifferencePercent = previousTotal > 0
                ? (amountDifference / previousTotal) * 100
                : 0;

            var currentOrderCount = currentTopCustomers.Sum(x => x.TotalOrders);
            var previousOrderCount = previousTopCustomers.Sum(x => x.TotalOrders);

            var orderDifference = currentOrderCount - previousOrderCount;
            var orderDifferencePercent = previousOrderCount > 0
                ? ((double)orderDifference / previousOrderCount) * 100
                : 0;

            var currentCustomerCount = currentTopCustomers.Count;
            var previousCustomerCount = previousTopCustomers.Count;

            var customerDifference = currentCustomerCount - previousCustomerCount;
            var customerDifferencePercent = previousCustomerCount > 0
                ? ((double)customerDifference / previousCustomerCount) * 100
                : 0;

            var customerComparisons = CompareCustomerLists(currentTopCustomers, previousTopCustomers);

            _logger.LogInformation($"✅ Karşılaştırma tamamlandı");
            _logger.LogInformation($"   - Şimdiki: ₺{currentTotal:N2} ({currentOrderCount} sipariş, {currentCustomerCount} müşteri)");
            _logger.LogInformation($"   - Önceki: ₺{previousTotal:N2} ({previousOrderCount} sipariş, {previousCustomerCount} müşteri)");
            _logger.LogInformation($"   - Fark: {amountDifferencePercent:+0.00;-0.00;0.00}%");

            return new CustomerComparisonAnalysisDto
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

                CurrentOrderCount = currentOrderCount,
                PreviousOrderCount = previousOrderCount,
                OrderDifference = orderDifference,
                OrderDifferencePercent = SanitizeDouble(orderDifferencePercent),
                OrderTrend = GetTrend(orderDifferencePercent),

                CurrentCustomerCount = currentCustomerCount,
                PreviousCustomerCount = previousCustomerCount,
                CustomerDifference = customerDifference,
                CustomerDifferencePercent = SanitizeDouble(customerDifferencePercent),
                CustomerTrend = GetTrend(customerDifferencePercent),

                CurrentAverageOrderAmount = currentOrderCount > 0
                    ? currentTotal / currentOrderCount
                    : 0,
                PreviousAverageOrderAmount = previousOrderCount > 0
                    ? previousTotal / previousOrderCount
                    : 0,
                AverageOrderDifference = (currentOrderCount > 0 ? currentTotal / currentOrderCount : 0) -
                                         (previousOrderCount > 0 ? previousTotal / previousOrderCount : 0),

                CurrentTopCustomers = currentTopCustomers,
                PreviousTopCustomers = previousTopCustomers,
                CustomerComparisons = customerComparisons
            };
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Tarih aralığı karşılaştırması hatası: {ex.Message}");
            return new CustomerComparisonAnalysisDto
            {
                Success = false,
                Message = $"Hata oluştu: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// JSON'dan müşteri verilerini çıkart
    /// </summary>
    private Dictionary<string, CustomerSalesData> ExtractCustomerDataFromJson(
        string rawOrdersJson,
        string periodDescription)
    {
        var customerData = new Dictionary<string, CustomerSalesData>();

        if (rawOrdersJson == "[]")
        {
            _logger.LogWarning($"⚠️ {periodDescription}: Veri bulunamadı");
            return customerData;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawOrdersJson);

            if (!doc.RootElement.TryGetProperty("d", out var dataElement))
            {
                _logger.LogError($"❌ {periodDescription}: 'd' property bulunamadı");
                return customerData;
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
                return customerData;
            }

            var orderCount = 0;
            foreach (var salesOrder in resultsElement.EnumerateArray())
            {
                orderCount++;

                var customerName = salesOrder.TryGetProperty("DeliverToName", out var name)
                    ? name.GetString() ?? "Bilinmeyen Müşteri"
                    : "Bilinmeyen Müşteri";

                double orderAmount = 0;
                if (salesOrder.TryGetProperty("AmountDC", out var amount))
                {
                    orderAmount = SanitizeDouble(amount.GetDouble());
                }
                else if (salesOrder.TryGetProperty("AmountFC", out var topAmount))
                {
                    orderAmount = SanitizeDouble(topAmount.GetDouble());
                }

                if (string.IsNullOrWhiteSpace(customerName) || customerName == "Bilinmeyen Müşteri")
                    continue;

                if (customerData.ContainsKey(customerName))
                {
                    customerData[customerName].TotalOrderAmount += orderAmount;
                    customerData[customerName].OrderCount++;
                    customerData[customerName].AverageOrderAmount =
                        customerData[customerName].TotalOrderAmount / customerData[customerName].OrderCount;
                }
                else
                {
                    customerData[customerName] = new CustomerSalesData
                    {
                        CustomerName = customerName,
                        TotalOrderAmount = orderAmount,
                        OrderCount = 1,
                        AverageOrderAmount = orderAmount
                    };
                }
            }

            _logger.LogInformation($"✅ {periodDescription}: {orderCount} sipariş, {customerData.Count} müşteri");
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ {periodDescription} JSON çıkarma hatası: {ex.Message}");
        }

        return customerData;
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
    private string GetCustomerStatus(int orderChange, double amountChange)
    {
        if (orderChange > 0 && amountChange > 0)
            return "📈 Büyüyor";
        else if (orderChange > 0 || amountChange > 0)
            return "📊 Gelişiyor";
        else if (orderChange < 0 || amountChange < 0)
            return "📉 Düşüyor";
        else
            return "➡️ Sabit";
    }

    private double SanitizeDouble(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0;
        return value;
    }
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

    

    public async Task<string> GetSalesOrderByDateRangeAsync(
    DateTime startDate,
    DateTime endDate)
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

            // Tarih aralığını Exact Online format'ına çevir
            var startDateStr = startDate.ToString("yyyy-MM-dd");
            var endDateStr = endDate.ToString("yyyy-MM-dd");

            _logger.LogInformation($"📅 Tarih Aralığı: {startDateStr} - {endDateStr}");

            bool hasMoreData = true;
            int pageNumber = 1;

            while (hasMoreData)
            {
                // Filter: Belirtilen tarih aralığında olan siparişler
                // Başlangıç tarihi >= startDate AND Başlangıç tarihi <= endDate
                var filter = $"$filter=Created ge datetime'{startDateStr}' and Created le datetime'{endDateStr}'";
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
}

public class CustomerSalesData
{
    public string CustomerName { get; set; }
    public double TotalOrderAmount { get; set; }
    public int OrderCount { get; set; }
    public double AverageOrderAmount { get; set; }
}

public class TopCustomerDto
{
    public int Rank { get; set; }
    public string CustomerName { get; set; }
    public int TotalOrders { get; set; }
    public double TotalOrderAmount { get; set; }
    public double AverageOrderAmount { get; set; }
    public double PercentageOfTotalSales { get; set; }
}

public class CustomerAnalysisDto
{
    public bool Success { get; set; }
    public string Message { get; set; }
    public string Period { get; set; }
    public int TopCustomerCount { get; set; }
    public int TotalCustomerCount { get; set; }
    public int TotalOrderCount { get; set; }
    public double TotalSalesAmount { get; set; }
    public double AverageOrderAmount { get; set; }
    public double AverageCustomerValue { get; set; }
    public List<TopCustomerDto> TopCustomers { get; set; }
}
public class CustomerComparisonDetailDto
{
    public string CustomerName { get; set; }

    // Şimdiki Dönem
    public int? CurrentRank { get; set; }
    public int CurrentOrders { get; set; }
    public double CurrentAmount { get; set; }
    public double CurrentPercentage { get; set; }

    // Önceki Dönem
    public int? PreviousRank { get; set; }
    public int PreviousOrders { get; set; }
    public double PreviousAmount { get; set; }
    public double PreviousPercentage { get; set; }

    // Farklılıklar
    public int? RankChange { get; set; } // Negatif = düştü, pozitif = yükseldi
    public int OrderChange { get; set; }
    public double AmountChange { get; set; }
    public double AmountChangePercent { get; set; }

    // Durum
    public string Status { get; set; } // 📈 Büyüyor, 📉 Düşüyor, 🆕 Yeni, ❌ Çıktı
}
public class CustomerComparisonAnalysisDto
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

    // Sipariş Sayısı Karşılaştırması
    public int CurrentOrderCount { get; set; }
    public int PreviousOrderCount { get; set; }
    public int OrderDifference { get; set; }
    public double OrderDifferencePercent { get; set; }
    public string OrderTrend { get; set; }

    // Müşteri Sayısı Karşılaştırması
    public int CurrentCustomerCount { get; set; }
    public int PreviousCustomerCount { get; set; }
    public int CustomerDifference { get; set; }
    public double CustomerDifferencePercent { get; set; }
    public string CustomerTrend { get; set; }

    // Ortalama Değerler
    public double CurrentAverageOrderAmount { get; set; }
    public double PreviousAverageOrderAmount { get; set; }
    public double AverageOrderDifference { get; set; }

    // Müşteri Listeleri
    public List<TopCustomerDto> CurrentTopCustomers { get; set; }
    public List<TopCustomerDto> PreviousTopCustomers { get; set; }

    // Müşteri Seviyesi Karşılaştırması
    public List<CustomerComparisonDetailDto> CustomerComparisons { get; set; }
}


/// <summary>
/// Belirli bir tarih aralığında veri çekmeyi sağlayan DTO
/// </summary>
public class DateRangeQuery
{
    /// <summary>
    /// Başlangıç tarihi (inclusive)
    /// </summary>
    public DateTime StartDate { get; set; }

    /// <summary>
    /// Bitiş tarihi (inclusive)
    /// </summary>
    public DateTime EndDate { get; set; }

    /// <summary>
    /// Kaç gün olduğunu gösterir (bilgi amaçlı)
    /// </summary>
    public int DayCount => (EndDate - StartDate).Days + 1;

    /// <summary>
    /// Tarih aralığının açıklaması (raporlarda kullanmak için)
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Constructor
    /// </summary>
    public DateRangeQuery(DateTime startDate, DateTime endDate, string description = "")
    {
        StartDate = startDate;
        EndDate = endDate;
        Description = description;
    }

    public override string ToString()
    {
        return $"{StartDate:yyyy-MM-dd} to {EndDate:yyyy-MM-dd} ({DayCount} days)";
    }
}

/// <summary>
/// Ortak tarih aralığı sorguları
/// </summary>
public static class DateRangeFactory
{
    /// <summary>
    /// Bugün
    /// </summary>
    public static DateRangeQuery Today()
    {
        var now = DateTime.UtcNow.Date;
        return new DateRangeQuery(now, now, "Bugün");
    }

    /// <summary>
    /// Dün
    /// </summary>
    public static DateRangeQuery Yesterday()
    {
        var yesterday = DateTime.UtcNow.Date.AddDays(-1);
        return new DateRangeQuery(yesterday, yesterday, "Dün");
    }

    /// <summary>
    /// Son N gün (bugün dahil)
    /// </summary>
    public static DateRangeQuery LastDays(int dayCount)
    {
        var endDate = DateTime.UtcNow.Date;
        var startDate = endDate.AddDays(-(dayCount - 1));
        return new DateRangeQuery(startDate, endDate, $"Son {dayCount} gün");
    }

    /// <summary>
    /// Önceki N gün
    /// </summary>
    public static DateRangeQuery PreviousDays(int dayCount)
    {
        var endDate = DateTime.UtcNow.Date.AddDays(-1);
        var startDate = endDate.AddDays(-(dayCount - 1));
        return new DateRangeQuery(startDate, endDate, $"Önceki {dayCount} gün");
    }

    /// <summary>
    /// Bu hafta (Pazartesi-Pazar)
    /// </summary>
    public static DateRangeQuery ThisWeek()
    {
        var today = DateTime.UtcNow.Date;
        // Pazartesi: 0 = Pazar, 1 = Pazartesi
        var dayOfWeek = (int)today.DayOfWeek;
        var daysToMonday = dayOfWeek == 0 ? 6 : dayOfWeek - 1;
        var startDate = today.AddDays(-daysToMonday);
        var endDate = startDate.AddDays(6);
        return new DateRangeQuery(startDate, endDate, "Bu hafta");
    }

    /// <summary>
    /// Geçen hafta
    /// </summary>
    public static DateRangeQuery LastWeek()
    {
        var lastWeek = LastDays(7);
        var endDate = lastWeek.EndDate.AddDays(-7);
        var startDate = endDate.AddDays(-6);
        return new DateRangeQuery(startDate, endDate, "Geçen hafta");
    }

    /// <summary>
    /// Bu ay
    /// </summary>
    public static DateRangeQuery ThisMonth()
    {
        var today = DateTime.UtcNow.Date;
        var startDate = new DateTime(today.Year, today.Month, 1);
        var endDate = startDate.AddMonths(1).AddDays(-1);
        return new DateRangeQuery(startDate, endDate, "Bu ay");
    }

    /// <summary>
    /// Geçen ay
    /// </summary>
    public static DateRangeQuery LastMonth()
    {
        var today = DateTime.UtcNow.Date;
        var startDate = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
        var endDate = startDate.AddMonths(1).AddDays(-1);
        return new DateRangeQuery(startDate, endDate, "Geçen ay");
    }

    /// <summary>
    /// Son 30 gün
    /// </summary>
    public static DateRangeQuery Last30Days()
    {
        return LastDays(30);
    }

    /// <summary>
    /// Önceki 30 gün
    /// </summary>
    public static DateRangeQuery Previous30Days()
    {
        return PreviousDays(30);
    }

    /// <summary>
    /// Bu yıl
    /// </summary>
    public static DateRangeQuery ThisYear()
    {
        var today = DateTime.UtcNow.Date;
        var startDate = new DateTime(today.Year, 1, 1);
        var endDate = new DateTime(today.Year, 12, 31);
        return new DateRangeQuery(startDate, endDate, "Bu yıl");
    }

    /// <summary>
    /// Geçen yıl
    /// </summary>
    public static DateRangeQuery LastYear()
    {
        var today = DateTime.UtcNow.Date;
        var startDate = new DateTime(today.Year - 1, 1, 1);
        var endDate = new DateTime(today.Year - 1, 12, 31);
        return new DateRangeQuery(startDate, endDate, "Geçen yıl");
    }

    /// <summary>
    /// Son N aya göre (bugün dahil)
    /// </summary>
    public static DateRangeQuery LastMonths(int monthCount)
    {
        var endDate = DateTime.UtcNow.Date;
        var startDate = endDate.AddMonths(-monthCount).AddDays(1);
        return new DateRangeQuery(startDate, endDate, $"Son {monthCount} ay");
    }

    /// <summary>
    /// Önceki N aya göre
    /// </summary>
    public static DateRangeQuery PreviousMonths(int monthCount)
    {
        var today = DateTime.UtcNow.Date;
        var endDate = new DateTime(today.Year, today.Month, 1).AddDays(-1);
        var startDate = endDate.AddMonths(-monthCount).AddDays(1);
        return new DateRangeQuery(startDate, endDate, $"Önceki {monthCount} ay");
    }

    /// <summary>
    /// Dün ile Bugün karşılaştırması
    /// </summary>
    public static (DateRangeQuery current, DateRangeQuery previous) YesterdayVsToday()
    {
        return (Today(), Yesterday());
    }

    /// <summary>
    /// Bu hafta ile Geçen hafta karşılaştırması
    /// </summary>
    public static (DateRangeQuery current, DateRangeQuery previous) ThisWeekVsLastWeek()
    {
        return (ThisWeek(), LastWeek());
    }

    /// <summary>
    /// Bu ay ile Geçen ay karşılaştırması
    /// </summary>
    public static (DateRangeQuery current, DateRangeQuery previous) ThisMonthVsLastMonth()
    {
        return (ThisMonth(), LastMonth());
    }

    /// <summary>
    /// Bu yıl ile Geçen yıl karşılaştırması
    /// </summary>
    public static (DateRangeQuery current, DateRangeQuery previous) ThisYearVsLastYear()
    {
        return (ThisYear(), LastYear());
    }

    /// <summary>
    /// Son 30 gün ile Önceki 30 gün karşılaştırması
    /// </summary>
    public static (DateRangeQuery current, DateRangeQuery previous) Last30DaysVsPrevious30Days()
    {
        return (Last30Days(), Previous30Days());
    }

    /// <summary>
    /// Son 3 ay ile Önceki 3 ay karşılaştırması
    /// </summary>
    public static (DateRangeQuery current, DateRangeQuery previous) Last3MonthsVsPrevious3Months()
    {
        return (LastMonths(3), PreviousMonths(3));
    }
}

// ============================================
// KULLANIM ÖRNEKLERİ
// ============================================

/*

// Örnek 1: Bugün vs Dün
var ranges = DateRangeFactory.YesterdayVsToday();
Console.WriteLine($"Şimdiki: {ranges.current}");
Console.WriteLine($"Önceki: {ranges.previous}");

// Örnek 2: Bu ay vs Geçen ay
var ranges2 = DateRangeFactory.ThisMonthVsLastMonth();
Console.WriteLine($"Şimdiki: {ranges2.current}");
Console.WriteLine($"Önceki: {ranges2.previous}");

// Örnek 3: Özel tarih aralığı
var custom = new DateRangeQuery(
    new DateTime(2024, 01, 01),
    new DateTime(2024, 01, 31),
    "Ocak 2024"
);
Console.WriteLine($"Özel: {custom}");

// Örnek 4: Son 7 gün
var last7 = DateRangeFactory.LastDays(7);
Console.WriteLine($"Son 7 Gün: {last7}");

// Örnek 5: Önceki 7 gün
var prev7 = DateRangeFactory.PreviousDays(7);
Console.WriteLine($"Önceki 7 Gün: {prev7}");

*/