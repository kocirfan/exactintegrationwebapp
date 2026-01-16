

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ShopifyProductApp.Services;
using ExactWebApp.Dto;

/// <summary>
/// THREAD-OPTIMIZED: Arka planda çalışan task'larla rate limiting sorununu çözer
/// </summary>
public class ExactSalesReportsUltraOptimized
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

    // ============ THREAD MANAGEMENT ============
    private readonly BlockingCollection<Action> _backgroundQueue = new BlockingCollection<Action>(100);
    private readonly CancellationTokenSource _backgroundCts = new CancellationTokenSource();
    private readonly Thread _backgroundWorker;

    // Rate limiting değişkenleri
    private int _currentActiveRequests = 0;
    private readonly object _lockObject = new object();
    private const int MAX_CONCURRENT_REQUESTS = 2; // 429 hatası almamak için çok düşük
    private const int REQUEST_DELAY_MS = 1000; // Her istek arasında 1 saniye delay
    private const int RATE_LIMIT_WAIT_MS = 1000; // 429 hatası aldığında 10 saniye bekle

    // OPTIMIZATION CONSTANTS
    private const int BatchSize = 100;
    private const int MaxRetries = 3;

    public ExactSalesReportsUltraOptimized(
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

        // Background thread başlat
        _backgroundWorker = new Thread(BackgroundWorkerLoop)
        {
            IsBackground = true,
            Name = "ExactAPI-BackgroundWorker"
        };
        _backgroundWorker.Start();
    }

    // ============ BACKGROUND THREAD LOOP ============
    private void BackgroundWorkerLoop()
    {
        try
        {
            foreach (var work in _backgroundQueue.GetConsumingEnumerable(_backgroundCts.Token))
            {
                try
                {
                    work?.Invoke();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ Background worker error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("🛑 Background worker stopped");
        }
    }

    // ============ PUBLIC METHODS ============

    /// <summary>
    /// İki tarih aralığını karşılaştır (Thread-optimized)
    /// </summary>
    public async Task<ProductComparisonAnalysisDto> CompareDateRangesAsyncThreaded(
        DateRangeQuery currentRange,
        DateRangeQuery previousRange,
        ReportFilterModel filter = null,
        Action<string> progressCallback = null)
    {
        var stopwatch = Stopwatch.StartNew();
        progressCallback?.Invoke("🚀 Tarih aralığı karşılaştırması başladı...");

        try
        {
            int topCount = filter?.TopCount ?? 5;
            _logger.LogInformation($"🚀 CompareDateRangesAsyncThreaded - {currentRange.Description} vs {previousRange.Description}");

            // Adım 1: İki dönemin verilerini paralel olarak çek (arka planda)
            progressCallback?.Invoke("📥 Veriler çekiliyor...");
            var currentProductsTask = FetchAndProcessOrdersExpandedAsync(currentRange.StartDate, currentRange.EndDate);
            var previousProductsTask = FetchAndProcessOrdersExpandedAsync(previousRange.StartDate, previousRange.EndDate);

            await Task.WhenAll(currentProductsTask, previousProductsTask);

            var currentProducts = (await currentProductsTask).Values.AsEnumerable();
            var previousProducts = (await previousProductsTask).Values.AsEnumerable();

            if (!currentProducts.Any() && !previousProducts.Any())
            {
                return new ProductComparisonAnalysisDto
                {
                    Success = false,
                    Message = "Her iki dönem için de veri bulunamadı"
                };
            }

            // Adım 2: Filtreleri uygula
            progressCallback?.Invoke("🔍 Filtreler uygulanıyor...");
            currentProducts = ApplyFiltersOptimized(currentProducts, filter);
            previousProducts = ApplyFiltersOptimized(previousProducts, filter);

            // Adım 3: Top ürünleri seç
            progressCallback?.Invoke($"⭐ Top {topCount} ürün seçiliyor...");
            var currentTopProducts = SelectTopProducts(currentProducts, topCount);
            var previousTopProducts = SelectTopProducts(previousProducts, topCount);

            // Adım 4: Ürün görsellerini arka planda çek (asenkron)
            progressCallback?.Invoke("📸 Ürün görselleri çekiliyor...");
            var allItemCodes = currentTopProducts.Select(x => x.ItemCode)
                .Union(previousTopProducts.Select(x => x.ItemCode))
                .Distinct()
                .ToList();

            var itemPictureDict = await FetchProductPicturesThreadedAsync(allItemCodes, progressCallback);


            // Adım 5: Karşılaştırma yap
            progressCallback?.Invoke("📊 Ürünler karşılaştırılıyor...");
            var productComparisons = CompareProductLists(currentTopProducts, previousTopProducts, itemPictureDict);

            // Adım 6: İstatistikleri hesapla
            var currentTotal = currentTopProducts.Sum(x => x.TotalAmount);
            var previousTotal = previousTopProducts.Sum(x => x.TotalAmount);
            var amountDifference = currentTotal - previousTotal;
            var amountDifferencePercent = previousTotal > 0 ? (amountDifference / previousTotal) * 100 : 0;

            var currentQuantity = currentTopProducts.Sum(x => x.TotalQuantity);
            var previousQuantity = previousTopProducts.Sum(x => x.TotalQuantity);
            var quantityDifference = currentQuantity - previousQuantity;
            var quantityDifferencePercent = previousQuantity > 0 ? (quantityDifference / previousQuantity) * 100 : 0;

            stopwatch.Stop();
            progressCallback?.Invoke($"✅ İşlem tamamlandı ({stopwatch.ElapsedMilliseconds}ms)");

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

                CurrentProductCount = currentTopProducts.Count,
                PreviousProductCount = previousTopProducts.Count,
                ProductDifference = currentTopProducts.Count - previousTopProducts.Count,

                CurrentTopProducts = currentTopProducts,
                PreviousTopProducts = previousTopProducts,
                ProductComparisons = productComparisons
            };
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ CompareDateRangesAsyncThreaded Error: {ex.Message}");
            progressCallback?.Invoke($"❌ Hata: {ex.Message}");
            return new ProductComparisonAnalysisDto
            {
                Success = false,
                Message = $"Hata oluştu: {ex.Message}"
            };
        }
    }

    // ============ CORE PROCESSING METHODS (THREADED) ============

    /// <summary>
    /// Siparişleri çek ve işle (thread-safe, rate-limited)
    /// </summary>
    private async Task<ConcurrentDictionary<string, ProductSalesData>> FetchAndProcessOrdersThreadedAsync(
        DateTime startDate,
        DateTime endDate)
    {
        var salesOrdersData = new ConcurrentDictionary<string, ProductSalesData>();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation($"📥 Siparişler çekiliyor: {startDate:yyyy-MM-dd} → {endDate:yyyy-MM-dd}");

            var rawOrdersJson = await FetchSalesOrdersWithRateLimitAsync(startDate, endDate);

            if (string.IsNullOrEmpty(rawOrdersJson) || rawOrdersJson == "[]")
            {
                _logger.LogWarning("⚠️ Sipariş verisi alınamadı");
                return salesOrdersData;
            }

            var exactService = _serviceProvider.GetRequiredService<ExactService>();
            var token = await exactService.GetValidToken();

            if (token == null)
            {
                _logger.LogError("❌ Token alınamadı");
                return salesOrdersData;
            }

            using var client = CreateHttpClient(token);
            using var doc = JsonDocument.Parse(rawOrdersJson);

            var resultsElement = ExtractResultsElement(doc);
            if (resultsElement.ValueKind == JsonValueKind.Undefined)
            {
                _logger.LogError("❌ Sipariş verisi işlenemedi");
                return salesOrdersData;
            }

            var orders = resultsElement.EnumerateArray().ToList();
            _logger.LogInformation($"📦 {orders.Count} sipariş bulundu");

            // Siparişleri batch'ler halinde arka planda işle
            var batches = orders
                .Select((order, index) => new { order, index })
                .GroupBy(x => x.index / BatchSize)
                .Select(g => g.Select(x => x.order).ToList())
                .ToList();

            var batchProcessTasks = new List<Task>();

            for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                int currentBatchIndex = batchIndex;
                var batchTask = Task.Run(async () =>
                {
                    await ProcessBatchThreadedAsync(
                        batches[currentBatchIndex],
                        currentBatchIndex + 1,
                        batches.Count,
                        client,
                        salesOrdersData);
                });

                batchProcessTasks.Add(batchTask);

                // Çok fazla task açmamak için bekle
                if (batchProcessTasks.Count >= 3)
                {
                    await Task.WhenAny(batchProcessTasks);
                    batchProcessTasks.RemoveAll(t => t.IsCompleted);
                }
            }

            await Task.WhenAll(batchProcessTasks);

            stopwatch.Stop();
            _logger.LogInformation($"✅ {salesOrdersData.Count} ürün işlendi ({stopwatch.ElapsedMilliseconds}ms)");

            return salesOrdersData;
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ FetchAndProcessOrdersThreadedAsync: {ex.Message}");
            return salesOrdersData;
        }
    }



    private async Task<ConcurrentDictionary<string, ProductSalesData>> FetchAndProcessOrdersExpandedAsync(
    DateTime startDate,
    DateTime endDate)
    {
        var salesOrdersData = new ConcurrentDictionary<string, ProductSalesData>();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation($"📥 Siparişler çekiliyor (EXPANDED): {startDate:yyyy-MM-dd} → {endDate:yyyy-MM-dd}");

            var exactService = _serviceProvider.GetRequiredService<ExactService>();
            var token = await exactService.GetValidToken();

            if (token == null)
            {
                _logger.LogError("❌ Token alınamadı");
                return salesOrdersData;
            }

            using var client = CreateHttpClient(token);

            var startDateStr = startDate.ToString("yyyy-MM-dd");
            var endDateStr = endDate.ToString("yyyy-MM-dd");

            // ✅ ANAHTAR: $expand=SalesOrderLines ekle
            var filter = $"Created ge datetime'{startDateStr}' and Created le datetime'{endDateStr}'";
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/salesorder/SalesOrders" +
                      $"?$filter={filter}" +
                      $"&$expand=SalesOrderLines" +  // ← BU SATIR ÇOOOK ÖNEMLİ!
                      $"&$top=250";                    // Deferred olmadığı için daha fazla al

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

            // ✅ ARTIK DAHA HIZLI: SalesOrderLines doğrudan içeriyor
            int processedCount = 0;
            foreach (var salesOrder in orders)
            {
                try
                {
                    // Senaryo 1: SalesOrderLines doğrudan object (normal)
                    if (salesOrder.TryGetProperty("SalesOrderLines", out var salesOrderLinesRef))
                    {
                        // Deferred yok, direkt results
                        if (salesOrderLinesRef.ValueKind == JsonValueKind.Object &&
                            salesOrderLinesRef.TryGetProperty("results", out var linesArray))
                        {
                            ProcessOrderLines(linesArray, salesOrdersData);
                        }
                        // Fallback: SalesOrderLines doğrudan array ise
                        else if (salesOrderLinesRef.ValueKind == JsonValueKind.Array)
                        {
                            ProcessOrderLines(salesOrderLinesRef, salesOrdersData);
                        }
                    }

                    processedCount++;

                    // Progress
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
            _logger.LogError($"❌ FetchAndProcessOrdersExpandedAsync: {ex.Message}");
            return salesOrdersData;
        }
    }

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
                var itemCode = ExtractPropertyAsString(line, "ItemCode").Trim();
                if (string.IsNullOrEmpty(itemCode))
                    continue;

                var itemDescription = ExtractPropertyAsString(line, "ItemDescription");
                var quantity = SanitizeDouble(ExtractPropertyAsDouble(line, "Quantity"));
                var unitPrice = SanitizeDouble(ExtractPropertyAsDouble(line, "UnitPrice"));
                var lineAmount = SanitizeDouble(ExtractPropertyAsDouble(line, "AmountDC"));

                if (!ValidateLineData(quantity, unitPrice, lineAmount, out _))
                    continue;

                // Thread-safe update
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
                        return existing;
                    });
            }
            catch { continue; }
        }
    }


    /// <summary>
    /// Batch'i threaded olarak işle (her 3 sipariş için 1 thread)
    /// </summary>
    private async Task ProcessBatchThreadedAsync(
        List<JsonElement> orders,
        int batchIndex,
        int totalBatches,
        HttpClient client,
        ConcurrentDictionary<string, ProductSalesData> salesOrdersData)
    {
        _logger.LogInformation($"📋 Batch {batchIndex}/{totalBatches} işleniyor ({orders.Count} sipariş)");

        // Siparişleri 3'erli gruplara böl
        for (int i = 0; i < orders.Count; i += 3)
        {
            var groupSize = Math.Min(3, orders.Count - i);
            var group = orders.Skip(i).Take(groupSize).ToList();

            var groupTasks = group.Select(salesOrder =>
                ProcessOrderWithRateLimitAsync(salesOrder, client, salesOrdersData)
            ).ToList();

            await Task.WhenAll(groupTasks);

            // Batch içinde her 3 sipariş arasında delay
            if (i + 3 < orders.Count)
            {
                await Task.Delay(500);
            }
        }
    }

    /// <summary>
    /// Siparişi rate limiting ile işle
    /// </summary>
    private async Task ProcessOrderWithRateLimitAsync(
        JsonElement salesOrder,
        HttpClient client,
        ConcurrentDictionary<string, ProductSalesData> salesOrdersData)
    {
        // Rate limiting kontrolü
        await WaitForRateLimitAsync();

        try
        {
            if (!salesOrder.TryGetProperty("SalesOrderLines", out var salesOrderLinesRef))
                return;

            if (!salesOrderLinesRef.TryGetProperty("__deferred", out var deferredElement) ||
                !deferredElement.TryGetProperty("uri", out var uriElement))
                return;

            var linesUrl = uriElement.GetString();
            if (string.IsNullOrEmpty(linesUrl))
                return;

            var linesResponse = await client.GetAsync(linesUrl);
            if (!linesResponse.IsSuccessStatusCode)
                return;

            var linesJson = await linesResponse.Content.ReadAsStringAsync();
            using var linesDoc = JsonDocument.Parse(linesJson);

            var linesResultsElement = ExtractResultsElement(linesDoc);
            if (linesResultsElement.ValueKind == JsonValueKind.Undefined)
                return;

            foreach (var line in linesResultsElement.EnumerateArray())
            {
                try
                {
                    var itemCode = ExtractPropertyAsString(line, "ItemCode").Trim();
                    if (string.IsNullOrEmpty(itemCode))
                        continue;

                    var itemDescription = ExtractPropertyAsString(line, "ItemDescription");
                    var quantity = SanitizeDouble(ExtractPropertyAsDouble(line, "Quantity"));
                    var unitPrice = SanitizeDouble(ExtractPropertyAsDouble(line, "UnitPrice"));
                    var lineAmount = SanitizeDouble(ExtractPropertyAsDouble(line, "AmountDC"));

                    if (!ValidateLineData(quantity, unitPrice, lineAmount, out _))
                        continue;

                    // Thread-safe update
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
                            return existing;
                        });
                }
                catch { continue; }
            }
        }
        finally
        {
            ReleaseRateLimit();
        }
    }

    /// <summary>
    /// Ürün görsellerini threaded olarak çek (max 2 concurrent)
    /// </summary>
    private async Task<Dictionary<string, string>> FetchProductPicturesThreadedAsync(
        List<string> itemCodes,
        Action<string> progressCallback = null)
    {
        var result = new ConcurrentDictionary<string, string>();
        var stopwatch = Stopwatch.StartNew();

        if (!itemCodes.Any())
            return result.ToDictionary(x => x.Key, x => x.Value);

        var exactService = _serviceProvider.GetRequiredService<ExactService>();
        var token = await exactService.GetValidToken();

        if (token == null)
        {
            _logger.LogWarning("⚠️ Token alınamadı, görsel URL'leri çekilemeyecek");
            return result.ToDictionary(x => x.Key, x => x.Value);
        }

        using var client = CreateHttpClient(token);
        var distinctCodes = itemCodes.Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();

        _logger.LogInformation($"📸 {distinctCodes.Count} ürün görseli çekiliyor (thread-pooled)...");

        // Maksimum 2 concurrent istek (rate limiting)
        var semaphore = new SemaphoreSlim(2, 2);
        var tasks = new List<Task>();

        for (int i = 0; i < distinctCodes.Count; i++)
        {
            int index = i;
            var itemCode = distinctCodes[i];

            var task = Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    await FetchProductPictureWithRetryAsync(itemCode, client, result);

                    if ((index + 1) % 5 == 0)
                    {
                        progressCallback?.Invoke($"📸 İlerleme: {index + 1}/{distinctCodes.Count}");
                    }
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
        _logger.LogInformation($"✅ {result.Count}/{distinctCodes.Count} ürün görseli çekildi ({stopwatch.ElapsedMilliseconds}ms)");

        return result.ToDictionary(x => x.Key, x => x.Value);
    }

    /// <summary>
    /// Ürün görselini retry ile çek
    /// </summary>
    private async Task FetchProductPictureWithRetryAsync(
     string itemCode,
     HttpClient client,
     ConcurrentDictionary<string, string> resultDict)
    {
        itemCode = itemCode.Trim();

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                await WaitForRateLimitAsync();

                // Delay between requests
                await Task.Delay(REQUEST_DELAY_MS);

                var filter = Uri.EscapeDataString($"Code eq '{itemCode}'");
                var url = $"{_baseUrl}/api/v1/{_divisionCode}/logistics/Items?$filter={filter}";

                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(content);

                    var root = doc.RootElement;
                    string pictureUrl = null;

                    // ✅ DÜZELTME: d.results yapısını kontrol et
                    if (root.TryGetProperty("d", out var dProperty))
                    {
                        // Senaryo 1: d.results (array içinde)
                        if (dProperty.ValueKind == JsonValueKind.Object &&
                            dProperty.TryGetProperty("results", out var resultsArray))
                        {
                            if (resultsArray.ValueKind == JsonValueKind.Array)
                            {
                                var firstItem = resultsArray.EnumerateArray().FirstOrDefault();
                                if (firstItem.ValueKind != JsonValueKind.Undefined)
                                {
                                    pictureUrl = ExtractPictureUrl(firstItem);
                                }
                            }
                        }
                        // Senaryo 2: d direkt array
                        else if (dProperty.ValueKind == JsonValueKind.Array)
                        {
                            var firstItem = dProperty.EnumerateArray().FirstOrDefault();
                            if (firstItem.ValueKind != JsonValueKind.Undefined)
                            {
                                pictureUrl = ExtractPictureUrl(firstItem);
                            }
                        }
                        // Senaryo 3: d direkt object
                        else if (dProperty.ValueKind == JsonValueKind.Object)
                        {
                            pictureUrl = ExtractPictureUrl(dProperty);
                        }
                    }
                    // Fallback: value property
                    else if (root.TryGetProperty("value", out var valueElement))
                    {
                        if (valueElement.ValueKind == JsonValueKind.Array)
                        {
                            var firstItem = valueElement.EnumerateArray().FirstOrDefault();
                            if (firstItem.ValueKind != JsonValueKind.Undefined)
                            {
                                pictureUrl = ExtractPictureUrl(firstItem);
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(pictureUrl))
                    {
                        resultDict.TryAdd(itemCode, pictureUrl);
                        _logger.LogDebug($"✅ {itemCode}: Görsel bulundu - {pictureUrl}");
                    }
                    else
                    {
                        _logger.LogDebug($"⚠️ {itemCode}: Görsel URL bulunamadı");
                    }

                    ReleaseRateLimit();
                    return;
                }

                // 429 Too Many Requests
                if ((int)response.StatusCode == 429)
                {
                    _logger.LogWarning($"⏸️ {itemCode} Rate limit (429), {RATE_LIMIT_WAIT_MS}ms bekleniyor...");
                    await Task.Delay(RATE_LIMIT_WAIT_MS);
                    continue;
                }

                ReleaseRateLimit();
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"⚠️ {itemCode} Hata (attempt {attempt + 1}): {ex.Message}");

                if (attempt < MaxRetries - 1)
                    await Task.Delay(2000);
            }
        }

        _logger.LogWarning($"❌ {itemCode}: {MaxRetries} deneme sonrası başarısız");
    }


    // ============ RATE LIMITING ============

    /// <summary>
    /// Rate limiting: maksimum N concurrent request izin ver
    /// </summary>
    private async Task WaitForRateLimitAsync()
    {
        while (true)
        {
            lock (_lockObject)
            {
                if (_currentActiveRequests < MAX_CONCURRENT_REQUESTS)
                {
                    _currentActiveRequests++;
                    return;
                }
            }

            await Task.Delay(100);
        }
    }

    /// <summary>
    /// Rate limit sayacını azalt
    /// </summary>
    private void ReleaseRateLimit()
    {
        lock (_lockObject)
        {
            if (_currentActiveRequests > 0)
                _currentActiveRequests--;
        }
    }

    // ============ API CALLS ============

    /// <summary>
    /// Siparişleri çek (rate limit aware)
    /// </summary>
    private async Task<string> FetchSalesOrdersWithRateLimitAsync(DateTime startDate, DateTime endDate)
    {
        try
        {
            var exactService = _serviceProvider.GetRequiredService<ExactService>();
            var token = await exactService.GetValidToken();

            if (token == null)
                return "[]";

            using var client = CreateHttpClient(token);

            var startDateStr = startDate.ToString("yyyy-MM-dd");
            var endDateStr = endDate.ToString("yyyy-MM-dd");
            var filter = $"Created ge datetime'{startDateStr}' and Created le datetime'{endDateStr}'";
            var url = $"{_baseUrl}/api/v1/{_divisionCode}/salesorder/SalesOrders?$filter={filter}&$top=60&$skip=0";

            await WaitForRateLimitAsync();
            try
            {
                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    if ((int)response.StatusCode == 429)
                    {
                        _logger.LogWarning($"⏸️ Rate limit (429), {RATE_LIMIT_WAIT_MS}ms bekleniyor...");
                        await Task.Delay(RATE_LIMIT_WAIT_MS);
                        return await FetchSalesOrdersWithRateLimitAsync(startDate, endDate);
                    }
                    return "[]";
                }

                return await response.Content.ReadAsStringAsync();
            }
            finally
            {
                ReleaseRateLimit();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ FetchSalesOrdersWithRateLimitAsync: {ex.Message}");
            return "[]";
        }
    }

    // ============ HELPER METHODS ============

    /// <summary>
    /// HttpClient oluştur
    /// </summary>
    private HttpClient CreateHttpClient(dynamic token)
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>
    /// İki ürün listesini karşılaştır
    /// </summary>
    private List<ProductComparisonDetailDto> CompareProductLists(
        List<TopProductDto> currentProducts,
        List<TopProductDto> previousProducts,
        Dictionary<string, string> itemPictureDict)
    {
        var comparisons = new List<ProductComparisonDetailDto>();
        var previousDict = previousProducts.ToDictionary(x => x.ItemCode, x => x);

        foreach (var current in currentProducts)
        {
            var comparison = new ProductComparisonDetailDto
            {
                ItemCode = current.ItemCode,
                path = itemPictureDict.ContainsKey(current.ItemCode)
                    ? itemPictureDict[current.ItemCode]
                    : null,
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

                comparison.RankChange = previous.Rank - current.Rank;
                comparison.QuantityChange = SanitizeDouble(current.TotalQuantity - previous.TotalQuantity);
                comparison.AmountChange = SanitizeDouble(current.TotalAmount - previous.TotalAmount);
                comparison.AmountChangePercent = previous.TotalAmount > 0
                    ? (comparison.AmountChange / previous.TotalAmount) * 100
                    : 0;
                comparison.Status = GetProductStatus(comparison.QuantityChange, comparison.AmountChange);
            }
            else
            {
                comparison.Status = "🆕 Yeni";
            }

            comparisons.Add(comparison);
        }

        // Çıkan ürünler
        foreach (var previous in previousProducts)
        {
            if (!currentProducts.Any(x => x.ItemCode == previous.ItemCode))
            {
                comparisons.Add(new ProductComparisonDetailDto
                {
                    ItemCode = previous.ItemCode,
                    path = itemPictureDict.ContainsKey(previous.ItemCode)
                        ? itemPictureDict[previous.ItemCode]
                        : null,

                    ItemDescription = previous.ItemDescription,
                    PreviousRank = previous.Rank,
                    PreviousQuantity = SanitizeDouble(previous.TotalQuantity),
                    PreviousAmount = SanitizeDouble(previous.TotalAmount),
                    PreviousPercentage = SanitizeDouble(previous.TotalQuantity),
                    Status = "❌ Çıktı"
                });
            }
        }

        return comparisons.OrderBy(x => x.CurrentRank ?? x.PreviousRank).ToList();
    }

    /// <summary>
    /// Top ürünleri seç
    /// </summary>
    private List<TopProductDto> SelectTopProducts(IEnumerable<ProductSalesData> products, int topCount)
    {
        return products
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
    }

    /// <summary>
    /// Filtreleri uygula
    /// </summary>
    private IEnumerable<ProductSalesData> ApplyFiltersOptimized(
        IEnumerable<ProductSalesData> data,
        ReportFilterModel filter)
    {
        if (filter == null)
            return data;

        var filtered = data;

        if (filter.ProductCodes != null && filter.ProductCodes.Any())
        {
            var codes = new HashSet<string>(filter.ProductCodes.Select(p => p.ToUpperInvariant()),
                StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(p => codes.Contains(p.ItemCode.ToUpperInvariant()));
        }

        if (!string.IsNullOrEmpty(filter.SearchTerm))
        {
            var searchLower = filter.SearchTerm.ToLowerInvariant();
            filtered = filtered.Where(p =>
                p.ItemDescription.IndexOf(searchLower, StringComparison.OrdinalIgnoreCase) >= 0 ||
                p.ItemCode.IndexOf(searchLower, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        if (filter.MinAmount.HasValue)
            filtered = filtered.Where(p => p.TotalAmount >= filter.MinAmount.Value);

        if (filter.MaxAmount.HasValue)
            filtered = filtered.Where(p => p.TotalAmount <= filter.MaxAmount.Value);

        return filtered;
    }

    /// <summary>
    /// Utility: JSON element'ten results çıkar
    /// </summary>
    private JsonElement ExtractResultsElement(JsonDocument doc)
    {
        try
        {
            if (!doc.RootElement.TryGetProperty("d", out var dataElement))
                return default;

            if (dataElement.ValueKind == JsonValueKind.Object &&
                dataElement.TryGetProperty("results", out var res))
            {
                return res;
            }
            else if (dataElement.ValueKind == JsonValueKind.Array)
            {
                return dataElement;
            }

            return default;
        }
        catch { return default; }
    }

    private string ExtractPropertyAsString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private double ExtractPropertyAsDouble(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property))
        {
            try
            {
                return property.ValueKind == JsonValueKind.Number ? property.GetDouble() : 0;
            }
            catch { return 0; }
        }
        return 0;
    }

    private string ExtractPictureUrl(JsonElement element)
    {
        // Senaryo 1: PictureThumbnailUrl
        if (element.TryGetProperty("PictureThumbnailUrl", out var thumbElement))
        {
            if (thumbElement.ValueKind == JsonValueKind.String)
            {
                var url = thumbElement.GetString();
                if (!string.IsNullOrEmpty(url) && url != "null")
                {
                    _logger.LogDebug($"📸 PictureThumbnailUrl kullanıldı: {url}");
                    return url;
                }
            }
        }

        // Senaryo 2: PictureUrl
        if (element.TryGetProperty("PictureUrl", out var picElement))
        {
            if (picElement.ValueKind == JsonValueKind.String)
            {
                var url = picElement.GetString();
                if (!string.IsNullOrEmpty(url) && url != "null")
                {
                    _logger.LogDebug($"📸 PictureUrl kullanıldı: {url}");
                    return url;
                }
            }
        }

        // Senaryo 3: Picture
        if (element.TryGetProperty("Picture", out var picIdElement))
        {
            if (picIdElement.ValueKind == JsonValueKind.String)
            {
                var picId = picIdElement.GetString();
                if (!string.IsNullOrEmpty(picId) && picId != "null")
                {
                    _logger.LogDebug($"📸 Picture ID bulundu: {picId}");
                    // Picture ID'den URL oluşturabilirsen bunu kullan
                    return $"https://start.exactonline.nl/api/v1/docs/{picId}";
                }
            }
        }

        _logger.LogDebug($"📸 Görsel URL'si bulunamadı");
        return null;
    }

    private bool ValidateLineData(double quantity, double unitPrice, double lineAmount, out string error)
    {
        error = null;

        if (double.IsNaN(quantity) || double.IsInfinity(quantity) ||
            double.IsNaN(unitPrice) || double.IsInfinity(unitPrice) ||
            double.IsNaN(lineAmount) || double.IsInfinity(lineAmount))
        {
            error = "NaN/Infinity";
            return false;
        }

        if (quantity < 0 || unitPrice < 0 || lineAmount < 0)
        {
            error = "Negatif değer";
            return false;
        }

        return true;
    }

    private double SanitizeDouble(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0;
        return Math.Round(value, 2);
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

    public void Dispose()
    {
        _backgroundCts.Cancel();
        _backgroundCts.Dispose();
        _backgroundQueue.Dispose();
    }
}