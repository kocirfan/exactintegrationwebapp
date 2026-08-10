using Microsoft.Extensions.DependencyInjection;

namespace ShopifyProductApp.Services
{
    public class ManualStockSyncStatus
    {
        public bool IsRunning { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public int BatchSize { get; set; }
        public int TotalItems { get; set; }
        public int ProcessedItems { get; set; }
        public int TotalBatches { get; set; }
        public int CompletedBatches { get; set; }
        public int ShopifySuccessCount { get; set; }
        public int ShopifyErrorCount { get; set; }
        public int DbSavedCount { get; set; }
        public int DbFailedCount { get; set; }
        public string CurrentStep { get; set; }
        public string LastError { get; set; }

        public ManualStockSyncStatus Clone() => (ManualStockSyncStatus)MemberwiseClone();
    }

    /// <summary>
    /// Gece 01:30'daki stok senkronunun manuel tetiklenen versiyonu.
    /// Ürünleri batch'ler halinde (varsayılan 50) işler; her batch sonunda
    /// Shopify güncellemeleri StockSyncLogs tablosuna yazılır.
    /// Aynı anda tek çalıştırma: ikinci tetikleme reddedilir.
    /// </summary>
    public class ManualStockSyncRunner
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly StockSyncLogService _stockSyncLogService;
        private readonly ILogger<ManualStockSyncRunner> _logger;
        private readonly object _lock = new();
        private ManualStockSyncStatus _status = new() { CurrentStep = "Henüz çalıştırılmadı" };

        public ManualStockSyncRunner(
            IServiceProvider serviceProvider,
            StockSyncLogService stockSyncLogService,
            ILogger<ManualStockSyncRunner> logger)
        {
            _serviceProvider = serviceProvider;
            _stockSyncLogService = stockSyncLogService;
            _logger = logger;
        }

        public ManualStockSyncStatus GetStatus()
        {
            lock (_lock)
            {
                return _status.Clone();
            }
        }

        /// <summary>
        /// Senkronu arka planda başlatır. Zaten çalışıyorsa false döner.
        /// maxItems verilirse sadece ilk N ürün işlenir (test için).
        /// </summary>
        public bool TryStart(int batchSize, int? maxItems = null)
        {
            lock (_lock)
            {
                if (_status.IsRunning)
                    return false;

                _status = new ManualStockSyncStatus
                {
                    IsRunning = true,
                    StartedAt = DateTime.Now,
                    BatchSize = batchSize,
                    CurrentStep = "Başlatılıyor"
                };
            }

            _ = Task.Run(() => RunAsync(batchSize, maxItems));
            return true;
        }

        private async Task RunAsync(int batchSize, int? maxItems)
        {
            try
            {
                _logger.LogInformation("🔄 Manuel stok senkronu başladı (batchSize: {BatchSize}, maxItems: {MaxItems})",
                    batchSize, maxItems?.ToString() ?? "tümü");

                using var scope = _serviceProvider.CreateScope();
                var exactService = scope.ServiceProvider.GetRequiredService<ExactService>();
                var shopifyService = scope.ServiceProvider.GetRequiredService<ShopifyService>();

                SetStep("Exact token kontrol ediliyor");
                var token = await exactService.GetValidToken();
                if (token == null || string.IsNullOrEmpty(token.access_token))
                {
                    Fail("Geçerli Exact token yok");
                    return;
                }

                SetStep(maxItems.HasValue
                    ? $"Exact'tan ilk {maxItems.Value} webshop ürünü alınıyor (erken durdurma aktif)"
                    : "Exact'tan tüm webshop ürünleri alınıyor (bu adım uzun sürebilir)");

                // maxItems verildiyse tarama limite ulaşınca erken durur (tüm katalog beklenmez)
                var exactItems = await exactService.GetAllStockedItemsAsync(maxItems);
                if (exactItems == null || exactItems.Count == 0)
                {
                    Fail("Exact'ta webshop ürünü bulunamadı");
                    return;
                }

                if (maxItems.HasValue && maxItems.Value > 0 && exactItems.Count > maxItems.Value)
                {
                    exactItems = exactItems.Take(maxItems.Value).ToList();
                }

                lock (_lock)
                {
                    _status.TotalItems = exactItems.Count;
                    _status.TotalBatches = (int)Math.Ceiling(exactItems.Count / (double)batchSize);
                }

                SetStep("Shopify ürünleri alınıyor");
                var shopifyProducts = await shopifyService.GetAllProductsRawAsync();

                SetStep("Shopify eşleştirme indeksi oluşturuluyor (exact_product_id / SKU / barcode)");
                var matchIndex = await shopifyService.BuildShopifyMatchIndexAsync();
                try
                {
                    int batchNo = 0;
                    foreach (var chunk in exactItems.Chunk(batchSize))
                    {
                        batchNo++;
                        SetStep($"Batch {batchNo}/{_status.TotalBatches} işleniyor ({chunk.Length} ürün)");

                        // Gece senkronu ile aynı güncelleme yolu
                        var batchResult = await shopifyService.UpdateMultipleStocksBatchAsync(
                            chunk.ToList(), shopifyProducts, "Data/manual_stock_sync.json", matchIndex);

                        // Her batch sonunda DB'ye yaz (upsert)
                        var saveResult = await _stockSyncLogService.SaveAsync(batchResult.LogEntries);

                        lock (_lock)
                        {
                            _status.ProcessedItems += chunk.Length;
                            _status.CompletedBatches = batchNo;
                            _status.ShopifySuccessCount += batchResult.SuccessCount;
                            _status.ShopifyErrorCount += batchResult.ErrorCount;
                            _status.DbSavedCount += saveResult.SavedCount;
                            _status.DbFailedCount += saveResult.FailedCount;
                        }

                        _logger.LogInformation("✅ Manuel senkron batch {BatchNo}/{TotalBatches} tamamlandı", batchNo, _status.TotalBatches);
                    }
                }
                finally
                {
                    shopifyProducts.Dispose();
                }

                SetStep("Tamamlandı");
                _logger.LogInformation("🎉 Manuel stok senkronu tamamlandı - İşlenen: {Processed}, Başarılı: {Success}, Hatalı: {Error}",
                    _status.ProcessedItems, _status.ShopifySuccessCount, _status.ShopifyErrorCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Manuel stok senkronu hatası: {Error}", ex.Message);
                lock (_lock)
                {
                    _status.LastError = ex.Message;
                    _status.CurrentStep = "Hata ile durdu";
                }
            }
            finally
            {
                lock (_lock)
                {
                    _status.IsRunning = false;
                    _status.FinishedAt = DateTime.Now;
                }
            }
        }

        private void SetStep(string step)
        {
            lock (_lock)
            {
                _status.CurrentStep = step;
            }
        }

        private void Fail(string error)
        {
            _logger.LogWarning("⚠️ Manuel stok senkronu başlatılamadı: {Error}", error);
            lock (_lock)
            {
                _status.LastError = error;
                _status.CurrentStep = "Başlatılamadı";
            }
        }
    }
}
