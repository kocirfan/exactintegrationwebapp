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
using System.Diagnostics;

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
    private const int MaxParallelRequests = 10;

    // ✅ YENİ: Image cache
    private readonly ConcurrentDictionary<string, string> _imageCache = new();

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
    // <summary>
    /// OPTIMIZED: $expand veya paralel deferred ile hızlı ürün çekme
    /// + Görselleri cache ile çekme
    /// Öncesi: 2-3 dakika, Sonrası: 30-45 saniye
    /// </summary>
    public async Task<List<TopProductDto>> GetTopSalesProductsAsync(
    DateTime? startDate = null,
    DateTime? endDate = null,
    ReportFilterModel filter = null,
    Action<string> progressCallback = null,
    bool fetchImages = true)  // ← YENİ PARAMETRE
    {
        var stopwatch = Stopwatch.StartNew();
        int topCount = filter?.TopCount ?? 5;

        try
        {
            _logger.LogInformation($"🚀 Top {topCount} Ürün Çıkartılıyor");
            progressCallback?.Invoke("📥 Siparişler çekiliyor...");

            var actualEndDate = endDate ?? DateTime.UtcNow;
            var actualStartDate = startDate ?? actualEndDate.AddYears(-1);

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

            progressCallback?.Invoke("🧪 Metod seçimi yapılıyor...");
            var salesOrdersData = await FetchOrdersWithSmartMethodAsync(
                client,
                actualStartDate,
                actualEndDate);

            if (salesOrdersData == null || !salesOrdersData.Any())
            {
                _logger.LogWarning("⚠️ Sipariş verisi alınamadı");
                return new List<TopProductDto>();
            }

            _logger.LogInformation($"✅ {salesOrdersData.Count} farklı ürün işlendi");

            progressCallback?.Invoke("🔍 Filtreleri uygulanıyor...");
            var filteredData = ApplyFilters(salesOrdersData.Values.AsEnumerable(), filter);

            progressCallback?.Invoke($"⭐ Top {topCount} ürün seçiliyor...");
            var topProducts = filteredData
                .OrderByDescending(x => x.TotalQuantity)
                .Take(topCount)
                .ToList();

            // ✅ ÇÖZÜM 4: Görselleri skip edebilme
            if (fetchImages)
            {
                progressCallback?.Invoke("📸 Ürün görselleri çekiliyor...");
                await FetchProductPicturesOptimizedAsync(topProducts, client, progressCallback);
            }

            var topProductDtos = topProducts
                .Select((p, index) => new TopProductDto
                {
                    Rank = index + 1,
                    ItemCode = p.ItemCode,
                    path = p.path,
                    ItemDescription = p.ItemDescription,
                    TotalQuantity = SanitizeDouble(p.TotalQuantity),
                    TotalAmount = SanitizeDouble(p.TotalAmount),
                    UnitPrice = SanitizeDouble(p.UnitPrice),
                    TransactionCount = p.TransactionCount,
                    AverageQuantityPerTransaction = SanitizeDouble(
                        p.TransactionCount > 0 ? p.TotalQuantity / p.TransactionCount : 0)
                })
                .ToList();

            var totalSalesAmount = SanitizeDouble(salesOrdersData.Values.Sum(x => x.TotalAmount));

            stopwatch.Stop();
            progressCallback?.Invoke($"✅ İşlem tamamlandı ({stopwatch.ElapsedMilliseconds}ms)");

            _logger.LogInformation($"✅ Top {topProductDtos.Count} ürün bulundu");
            _logger.LogInformation($"📸 {topProductDtos.Count(x => !string.IsNullOrEmpty(x.path))}/{topProductDtos.Count} ürünün görseli çekildi");
            _logger.LogInformation($"💰 Toplam Satış Tutarı: ₺{totalSalesAmount:N2}");

            return topProductDtos;
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Kritik Hata: {ex.Message}\n{ex.StackTrace}");
            return null;
        }
    }

    // ============ SMART METHOD - Otomatik Seçim ============

    /// <summary>
    /// SMART: Önce $expand dene, çalışmazsa paralel deferred yap
    /// </summary>
    private async Task<ConcurrentDictionary<string, ProductSalesData>> FetchOrdersWithSmartMethodAsync(
     HttpClient client,
     DateTime startDate,
     DateTime endDate)
    {
        try
        {
            _logger.LogInformation("📥 Siparişler çekiliyor ($expand ile)...");

            // ✅ ÇÖZÜM 1: Test yapma! Direkt $expand dene
            // Test yaptığımız için 1 saniye kayıp ediyorduk
            try
            {
                return await FetchOrdersWithExpandAsync(client, startDate, endDate);
            }
            catch (Exception ex)
            {
                // $expand başarısız olursa fallback
                _logger.LogWarning($"⚠️ $expand başarısız, paralel deferred'e geçiliyor: {ex.Message}");
                return await FetchOrdersWithParallelDeferredAsync(client, startDate, endDate);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"❌ FetchOrdersWithSmartMethodAsync: {ex.Message}");
            return new ConcurrentDictionary<string, ProductSalesData>();
        }
    }


    // ============ ULTRA-FAST: $expand VERSION ============

    /// <summary>
    /// ULTRA-FAST: $expand=SalesOrderLines ile tüm satırları 1 istekte çek
    /// 10-15 saniye
    /// </summary>
    private async Task<ConcurrentDictionary<string, ProductSalesData>> FetchOrdersWithExpandAsync(
        HttpClient client,
        DateTime startDate,
        DateTime endDate)
    {
        var salesOrdersData = new ConcurrentDictionary<string, ProductSalesData>();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation($"📥 Siparişler çekiliyor ($expand): {startDate:yyyy-MM-dd} → {endDate:yyyy-MM-dd}");

            var startDateStr = startDate.ToString("yyyy-MM-dd");
            var endDateStr = endDate.ToString("yyyy-MM-dd");

            // ✅ ANAHTAR: $expand=SalesOrderLines ekle
            var filter = $"Created ge datetime'{startDateStr}' and Created le datetime'{endDateStr}'";
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/salesorder/SalesOrders" +
                      $"?$filter={filter}" +
                      $"&$expand=SalesOrderLines" +
                      $"&$top=250";

            _logger.LogInformation($"🔗 API Call: {url}");

            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"❌ API Error: {response.StatusCode}");
                return salesOrdersData;
            }

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);

            var resultsElement = ExtractResultsElement(doc);
            if (resultsElement.ValueKind == JsonValueKind.Undefined)
            {
                _logger.LogError("❌ Sipariş verisi işlenemedi");
                return salesOrdersData;
            }

            var orders = resultsElement.EnumerateArray().ToList();
            _logger.LogInformation($"📦 {orders.Count} sipariş bulundu ($expand ile)");

            int processedCount = 0;
            foreach (var salesOrder in orders)
            {
                try
                {
                    if (salesOrder.TryGetProperty("SalesOrderLines", out var salesOrderLinesRef))
                    {
                        if (salesOrderLinesRef.ValueKind == JsonValueKind.Object &&
                            salesOrderLinesRef.TryGetProperty("results", out var linesArray))
                        {
                            ProcessOrderLines(linesArray, salesOrdersData);
                        }
                        else if (salesOrderLinesRef.ValueKind == JsonValueKind.Array)
                        {
                            ProcessOrderLines(salesOrderLinesRef, salesOrdersData);
                        }
                    }

                    processedCount++;
                    if (processedCount % 10 == 0)
                    {
                        _logger.LogInformation($"✅ {processedCount} sipariş işlendi");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"⚠️ Sipariş işleme hatası: {ex.Message}");
                }
            }

            stopwatch.Stop();
            _logger.LogInformation($"✅ {salesOrdersData.Count} ürün işlendi ({stopwatch.ElapsedMilliseconds}ms) - EXPANDED");

            return salesOrdersData;
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ FetchOrdersWithExpandAsync: {ex.Message}");
            return salesOrdersData;
        }
    }

    // ============ FAST: PARALLEL DEFERRED VERSION ============

    /// <summary>
    /// FAST: Deferred link'leri paralel olarak çek (10 concurrent)
    /// 30-40 saniye
    /// </summary>
    private async Task<ConcurrentDictionary<string, ProductSalesData>> FetchOrdersWithParallelDeferredAsync(
        HttpClient client,
        DateTime startDate,
        DateTime endDate)
    {
        var salesOrdersData = new ConcurrentDictionary<string, ProductSalesData>();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation($"📥 Siparişler çekiliyor (Paralel Deferred): {startDate:yyyy-MM-dd} → {endDate:yyyy-MM-dd}");

            var rawOrdersJson = await GetAllSalesOrderByDateRangeAsync(startDate, endDate);

            if (rawOrdersJson == "[]")
            {
                _logger.LogWarning("⚠️ Sipariş verisi alınamadı");
                return salesOrdersData;
            }

            using var doc = JsonDocument.Parse(rawOrdersJson);
            var resultsElement = ExtractResultsElement(doc);
            if (resultsElement.ValueKind == JsonValueKind.Undefined)
            {
                _logger.LogError("❌ Sipariş verisi işlenemedi");
                return salesOrdersData;
            }

            var orders = resultsElement.EnumerateArray().ToList();
            _logger.LogInformation($"📦 {orders.Count} sipariş bulundu");

            // Tüm deferred URL'leri topla
            var deferredUrls = new List<(int Index, string OrderId, string Url)>();
            foreach (var order in orders)
            {
                try
                {
                    if (order.TryGetProperty("SalesOrderLines", out var salesOrderLinesRef) &&
                        salesOrderLinesRef.TryGetProperty("__deferred", out var deferredElement) &&
                        deferredElement.TryGetProperty("uri", out var uriElement))
                    {
                        var url_deferred = uriElement.GetString();
                        var orderId = order.TryGetProperty("ID", out var id) ? id.GetString() : $"Order{deferredUrls.Count}";
                        if (!string.IsNullOrEmpty(url_deferred))
                        {
                            deferredUrls.Add((deferredUrls.Count, orderId, url_deferred));
                        }
                    }
                }
                catch { }
            }

            _logger.LogInformation($"📋 {deferredUrls.Count} deferred URL bulundu");

            // Deferred URL'leri paralel olarak çek (10 concurrent)
            var semaphore = new System.Threading.SemaphoreSlim(10, 10);
            var tasks = new List<Task>();

            for (int i = 0; i < deferredUrls.Count; i++)
            {
                var (index, orderId, deferredUrl) = deferredUrls[i];

                var task = Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var linesResponse = await client.GetAsync(deferredUrl);
                        if (linesResponse.IsSuccessStatusCode)
                        {
                            var linesJson = await linesResponse.Content.ReadAsStringAsync();
                            using var linesDoc = JsonDocument.Parse(linesJson);

                            var linesResultsElement = ExtractResultsElement(linesDoc);
                            if (linesResultsElement.ValueKind != JsonValueKind.Undefined)
                            {
                                ProcessOrderLines(linesResultsElement, salesOrdersData);
                            }
                        }

                        if ((index + 1) % 10 == 0)
                        {
                            _logger.LogInformation($"✅ {index + 1}/{deferredUrls.Count} deferred istekleri tamamlandı");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"⚠️ Deferred {index}: {ex.Message}");
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                tasks.Add(task);
            }

            await Task.WhenAll(tasks);

            stopwatch.Stop();
            _logger.LogInformation($"✅ {salesOrdersData.Count} ürün işlendi ({stopwatch.ElapsedMilliseconds}ms) - PARALLEL DEFERRED");

            return salesOrdersData;
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ FetchOrdersWithParallelDeferredAsync: {ex.Message}");
            return salesOrdersData;
        }
    }

    // ============ HELPER: Sipariş Satırlarını İşle ============

    /// <summary>
    /// Sipariş satırlarını işle
    /// </summary>
    private void ProcessOrderLines(
        JsonElement linesElement,
        ConcurrentDictionary<string, ProductSalesData> salesOrdersData)
    {
        if (linesElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var line in linesElement.EnumerateArray())
        {
            try
            {
                var itemCode = line.TryGetProperty("ItemCode", out var code)
                    ? code.GetString() ?? "" : "";

                if (string.IsNullOrEmpty(itemCode))
                    continue;

                var itemDescription = line.TryGetProperty("ItemDescription", out var desc)
                    ? desc.GetString() ?? "" : "";

                var quantity = line.TryGetProperty("Quantity", out var qty)
                    ? qty.GetDouble() : 0;

                var unitPrice = line.TryGetProperty("UnitPrice", out var price)
                    ? price.GetDouble() : 0;

                var lineAmount = line.TryGetProperty("AmountDC", out var amount)
                    ? amount.GetDouble() : 0;

                quantity = SanitizeDouble(quantity);
                unitPrice = SanitizeDouble(unitPrice);
                lineAmount = SanitizeDouble(lineAmount);

                salesOrdersData.AddOrUpdate(
                    itemCode,
                    new ProductSalesData
                    {
                        ItemCode = itemCode,
                        ItemDescription = itemDescription,
                        TotalQuantity = quantity,
                        TotalAmount = lineAmount,
                        UnitPrice = unitPrice,
                        TransactionCount = 1
                    },
                    (key, existing) =>
                    {
                        existing.TotalQuantity += quantity;
                        existing.TotalAmount += lineAmount;
                        existing.TransactionCount++;
                        if (string.IsNullOrEmpty(existing.ItemDescription) && !string.IsNullOrEmpty(itemDescription))
                            existing.ItemDescription = itemDescription;
                        return existing;
                    });
            }
            catch { continue; }
        }
    }

    // ============ OPTIMIZED: Görselleri Paralel Çek ============

    /// <summary>
    /// ✅ FIX: Görselleri paralel olarak çek ve cache'e kaydet
    /// Max 10 concurrent requests
    /// </summary>
    private async Task FetchProductPicturesOptimizedAsync(
      List<ProductSalesData> products,
      HttpClient client,
      Action<string> progressCallback = null)
    {
        if (!products.Any())
            return;

        var itemCodes = products
            .Where(p => !string.IsNullOrEmpty(p.ItemCode))
            .Select(p => p.ItemCode)
            .Distinct()
            .ToList();

        // ✅ ÇÖZÜM 2: Concurrent'i dinamik yap (ürün sayısına göre)
        // 5 ürün varsa 5 concurrent, 10 varsa 5 (max 5)
        var concurrentLimit = Math.Min(itemCodes.Count, 5);  // Maksimum 5

        _logger.LogInformation($"📸 {itemCodes.Count} ürün görseli çekiliyor ({concurrentLimit} concurrent)...");

        var semaphore = new System.Threading.SemaphoreSlim(concurrentLimit, concurrentLimit);
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
                    var pictureUrl = await GetItemImageAsyncOptimized(itemCode, client);

                    var product = products.FirstOrDefault(p => p.ItemCode == itemCode);
                    if (product != null)
                    {
                        product.path = pictureUrl;
                        if (!string.IsNullOrEmpty(pictureUrl))
                        {
                            _logger.LogDebug($"✅ {itemCode}: Görsel çekildi");
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

  



    // ============ OPTIMIZED: Görsel URL Çekme (Cache ile) ============

    /// <summary>
    /// ✅ FIX: Cache ile hızlı görsel URL çekme
    /// d.results[0].PictureThumbnailUrl veya d.results[0].PictureUrl
    /// </summary>
    private async Task<string> GetItemImageAsyncOptimized(string itemCode, HttpClient client, int retryCount = 2)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(itemCode))
                return null;

            itemCode = itemCode.Trim();

            // ✅ Cache kontrol (çoğu zaman buradan dönecek)
            if (_imageCache.TryGetValue(itemCode, out var cachedUrl))
            {
                _logger.LogDebug($"✅ Cache'den: {itemCode}");
                return cachedUrl;
            }

            _logger.LogDebug($"🔍 Görsel aranıyor: {itemCode}");

            for (int attempt = 0; attempt < retryCount; attempt++)
            {
                try
                {
                    // ✅ ÇÖZÜM 3: Delay'i KALDIRDIM!
                    // await Task.Delay(1000);  // ❌ KALDIRMA - 5 saniye kayıp

                    var filter = Uri.EscapeDataString($"Code eq '{itemCode}'");
                    var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/Items?$filter={filter}";

                    var response = await client.GetAsync(url);

                    // 429 hatası aldığında retry (ama delay daha az)
                    if ((int)response.StatusCode == 429)
                    {
                        if (attempt < retryCount - 1)
                        {
                            // ✅ 429 hatası varsa bekle (ama kısa)
                            var delayMs = 500 * (attempt + 1);  // 500ms, 1000ms (1s yerine)
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

    // ============ HELPER METHODS ============

    /// <summary>
    /// Tarih aralığına göre siparişleri getir
    /// </summary>
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

            var startDateStr = startDate.ToString("yyyy-MM-dd");
            var endDateStr = endDate.ToString("yyyy-MM-dd");

            var filter = $"Created ge datetime'{startDateStr}' and Created le datetime'{endDateStr}'";
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/salesorder/SalesOrders" +
                        $"?$filter={filter}" +
                        $"&$top=250" +
                        $"&$skip=0";

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
    /// JSON'dan results elementini çıkar
    /// </summary>
    private JsonElement ExtractResultsElement(JsonDocument doc)
    {
        try
        {
            if (!doc.RootElement.TryGetProperty("d", out var dataElement))
            {
                _logger.LogError("❌ 'd' property bulunamadı");
                return default;
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
                return default;
            }

            return resultsElement;
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ JSON çıkarma hatası: {ex.Message}");
            return default;
        }
    }

    /// <summary>
    /// Filtreleri uygula
    /// </summary>
    private IEnumerable<ProductSalesData> ApplyFilters(
        IEnumerable<ProductSalesData> data,
        ReportFilterModel filter)
    {
        var filteredData = data;

        if (filter == null)
            return filteredData;

        if (filter.ProductCodes != null && filter.ProductCodes.Any())
        {
            var productCodesToLower = filter.ProductCodes.Select(p => p.ToLowerInvariant()).ToHashSet();
            filteredData = filteredData.Where(p =>
                productCodesToLower.Contains(p.ItemCode.ToLowerInvariant()));
        }

        if (!string.IsNullOrEmpty(filter.SearchTerm))
        {
            var searchTermLower = filter.SearchTerm.ToLowerInvariant();
            filteredData = filteredData.Where(p =>
                p.ItemDescription.Contains(searchTermLower, StringComparison.OrdinalIgnoreCase) ||
                p.ItemCode.Contains(searchTermLower, StringComparison.OrdinalIgnoreCase));
        }

        if (filter.MinAmount.HasValue)
        {
            filteredData = filteredData.Where(p => p.TotalAmount >= filter.MinAmount.Value);
        }

        if (filter.MaxAmount.HasValue)
        {
            filteredData = filteredData.Where(p => p.TotalAmount <= filter.MaxAmount.Value);
        }

        return filteredData;
    }

    private double SanitizeDouble(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0;
        return Math.Round(value, 2);
    }
}

// ============ DTO CLASSES ============

public class ProductSalesData
{
    public string ItemCode { get; set; }
    public string ItemDescription { get; set; }
    public double TotalQuantity { get; set; }
    public double TotalAmount { get; set; }
    public double UnitPrice { get; set; }
    public int TransactionCount { get; set; }
    public string path { get; set; }
}

public class TopProductDto
{
    public int Rank { get; set; }
    public string ItemCode { get; set; }
    public string path { get; set; }
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

    public string CurrentPeriod { get; set; }
    public string PreviousPeriod { get; set; }

    public double CurrentAmount { get; set; }
    public double PreviousAmount { get; set; }
    public double AmountDifference { get; set; }
    public double AmountDifferencePercent { get; set; }
    public string AmountTrend { get; set; }

    public double CurrentQuantity { get; set; }
    public double PreviousQuantity { get; set; }
    public double QuantityDifference { get; set; }
    public double QuantityDifferencePercent { get; set; }
    public string QuantityTrend { get; set; }

    public int CurrentProductCount { get; set; }
    public int PreviousProductCount { get; set; }
    public int ProductDifference { get; set; }
    public double ProductDifferencePercent { get; set; }
    public string ProductTrend { get; set; }

    public double CurrentAverageUnitPrice { get; set; }
    public double PreviousAverageUnitPrice { get; set; }
    public double AverageUnitPriceDifference { get; set; }

    public List<TopProductDto> CurrentTopProducts { get; set; }
    public List<TopProductDto> PreviousTopProducts { get; set; }

    public List<ProductComparisonDetailDto> ProductComparisons { get; set; }
}

public class ProductComparisonDetailDto
{
    public string ItemCode { get; set; }
    public string path { get; set; }
    public string ItemDescription { get; set; }

    public int? CurrentRank { get; set; }
    public double CurrentQuantity { get; set; }
    public double CurrentAmount { get; set; }
    public double CurrentPercentage { get; set; }

    public int? PreviousRank { get; set; }
    public double PreviousQuantity { get; set; }
    public double PreviousAmount { get; set; }
    public double PreviousPercentage { get; set; }

    public int? RankChange { get; set; }
    public double QuantityChange { get; set; }
    public double AmountChange { get; set; }
    public double AmountChangePercent { get; set; }

    public string Status { get; set; }
}