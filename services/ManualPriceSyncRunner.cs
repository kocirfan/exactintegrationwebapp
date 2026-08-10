using Microsoft.Extensions.DependencyInjection;

namespace ShopifyProductApp.Services
{
    public class ManualPriceSyncStatus
    {
        public bool IsRunning { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public int BatchSize { get; set; }
        public int ProcessedItems { get; set; }
        public int CompletedBatches { get; set; }
        public int PriceUpdatedCount { get; set; }
        public int UnchangedCount { get; set; }
        public int SkippedZeroPriceCount { get; set; }
        public int ErrorCount { get; set; }
        public int DbSavedCount { get; set; }
        public int DbFailedCount { get; set; }
        public string CurrentStep { get; set; }
        public string LastError { get; set; }

        public ManualPriceSyncStatus Clone() => (ManualPriceSyncStatus)MemberwiseClone();
    }

    /// <summary>
    /// Tüm webshop ürünlerinin fiyatlarını Exact'tan Shopify'a senkronlar.
    /// Ürünler Exact'tan batch batch (sayfa sayfa) çekilir - stok senkronundaki gibi
    /// uzun bir ön tarama yoktur; her sayfa çekildiği anda işlenip DB'ye yazılır.
    /// Aynı anda tek çalıştırma: ikinci tetikleme reddedilir.
    /// </summary>
    public class ManualPriceSyncRunner
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PriceSyncLogService _priceSyncLogService;
        private readonly ILogger<ManualPriceSyncRunner> _logger;
        private readonly object _lock = new();
        private ManualPriceSyncStatus _status = new() { CurrentStep = "Henüz çalıştırılmadı" };

        public ManualPriceSyncRunner(
            IServiceProvider serviceProvider,
            PriceSyncLogService priceSyncLogService,
            ILogger<ManualPriceSyncRunner> logger)
        {
            _serviceProvider = serviceProvider;
            _priceSyncLogService = priceSyncLogService;
            _logger = logger;
        }

        public ManualPriceSyncStatus GetStatus()
        {
            lock (_lock)
            {
                return _status.Clone();
            }
        }

        /// <summary>
        /// Fiyat senkronunu arka planda başlatır. Zaten çalışıyorsa false döner.
        /// maxItems verilirse sadece ilk N ürün işlenir (test için).
        /// </summary>
        public bool TryStart(int batchSize, int? maxItems = null)
        {
            lock (_lock)
            {
                if (_status.IsRunning)
                    return false;

                _status = new ManualPriceSyncStatus
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
                _logger.LogInformation("💶 Manuel fiyat senkronu başladı (batchSize: {BatchSize}, maxItems: {MaxItems})",
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

                SetStep("Shopify eşleştirme indeksi oluşturuluyor (exact_product_id / SKU / barcode)");
                var (byExactId, bySku, byBarcode) = await shopifyService.BuildShopifyMatchIndexAsync();

                if (byExactId.Count == 0 && bySku.Count == 0 && byBarcode.Count == 0)
                {
                    Fail("Shopify eşleştirme indeksi boş - ürünler alınamadı");
                    return;
                }

                {
                    int skip = 0;
                    int batchNo = 0;

                    while (true)
                    {
                        // maxItems'a ulaşıldıysa dur
                        if (maxItems.HasValue && _status.ProcessedItems >= maxItems.Value)
                            break;

                        int pageSize = batchSize;
                        if (maxItems.HasValue)
                            pageSize = Math.Min(batchSize, maxItems.Value - _status.ProcessedItems);

                        batchNo++;
                        SetStep($"Batch {batchNo}: Exact'tan {pageSize} ürün çekiliyor (skip: {skip})");

                        var pageItems = await exactService.GetWebshopItemsPageAsync(skip, pageSize);
                        if (pageItems == null || pageItems.Count == 0)
                            break; // katalog bitti

                        SetStep($"Batch {batchNo}: {pageItems.Count} ürünün fiyatı güncelleniyor");

                        var batchResult = await shopifyService.UpdateMultiplePricesBatchAsync(pageItems, byExactId, bySku, byBarcode);

                        // Her batch sonunda DB'ye yaz (upsert)
                        var saveResult = await _priceSyncLogService.SaveAsync(batchResult.LogEntries);

                        lock (_lock)
                        {
                            _status.ProcessedItems += pageItems.Count;
                            _status.CompletedBatches = batchNo;
                            _status.PriceUpdatedCount += batchResult.SuccessCount;
                            _status.UnchangedCount += batchResult.UnchangedCount;
                            _status.SkippedZeroPriceCount += batchResult.SkippedZeroPriceCount;
                            _status.ErrorCount += batchResult.ErrorCount;
                            _status.DbSavedCount += saveResult.SavedCount;
                            _status.DbFailedCount += saveResult.FailedCount;
                        }

                        _logger.LogInformation("✅ Fiyat senkronu batch {BatchNo} tamamlandı - İşlenen toplam: {Processed}", batchNo, _status.ProcessedItems);

                        if (pageItems.Count < pageSize)
                            break; // son sayfa

                        skip += pageItems.Count;
                        await Task.Delay(500); // Exact rate limit
                    }
                }

                SetStep("Tamamlandı");
                _logger.LogInformation("🎉 Manuel fiyat senkronu tamamlandı - İşlenen: {Processed}, Güncellenen: {Updated}, Değişmeyen: {Unchanged}, Atlanan(0 fiyat): {Skipped}, Hatalı: {Error}",
                    _status.ProcessedItems, _status.PriceUpdatedCount, _status.UnchangedCount, _status.SkippedZeroPriceCount, _status.ErrorCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Manuel fiyat senkronu hatası: {Error}", ex.Message);
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
            _logger.LogWarning("⚠️ Manuel fiyat senkronu başlatılamadı: {Error}", error);
            lock (_lock)
            {
                _status.LastError = error;
                _status.CurrentStep = "Başlatılamadı";
            }
        }
    }
}
