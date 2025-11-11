using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ShopifyProductApp.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ShopifyProductApp.Services
{
    public class ProductSyncBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ProductSyncBackgroundService> _logger;
        private readonly string _archiveFilePath = "Data/arcivedproduct.json";
        private readonly string _updateLogFile = "Data/batch_log.json";
        private readonly int _batchSize = 20; // Batch boyutu - ihtiyaca göre ayarlanabilir

        public ProductSyncBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<ProductSyncBackgroundService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🚀 Product Sync Service başlatıldı - Her 10 dakikada bir çalışacak");
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("🔄 Product sync işlemi başlıyor...");

                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var exactService = scope.ServiceProvider.GetRequiredService<ExactService>();
                        var shopifyService = scope.ServiceProvider.GetRequiredService<ShopifyService>();
                        var settingsService = scope.ServiceProvider.GetRequiredService<SettingsService>();

                        // Token kontrolü - ExactService içinden
                        var tokenResponse = await exactService.GetValidToken();
                        if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.access_token))
                        {
                            _logger.LogWarning("⚠️ Geçerli token yok, 5 dakika sonra tekrar denenecek");
                            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                            continue;
                        }

                        await PerformSyncOperations(exactService, shopifyService, settingsService);
                    }

                    _logger.LogInformation("✅ Product sync işlemi tamamlandı");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Product sync service hatası: {Error}", ex.Message);
                    // Hata durumunda daha uzun bekle
                    _logger.LogInformation("⏳ Hata nedeniyle 10 dakika bekleniyor...");
                    await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
                    continue;
                }

                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        private async Task PerformSyncOperations(ExactService exactService, ShopifyService shopifyService, SettingsService settingsService)
        {
            try
            {
                var allItems = await exactService.GetNonWebshopItemCodesAsync();

                if (allItems == null || !allItems.Any())
                {
                    _logger.LogWarning("⚠️ İşlenecek ürün bulunamadı");
                    return;
                }

                _logger.LogInformation("📊 Toplam {Count} ürün işlenecek, {BatchSize}'li batch'lerde",
                    allItems.Count, _batchSize);

                var batchStartTime = DateTime.Now;
                var batchId = batchStartTime.ToString("yyyyMMdd_HHmmss");
                var allUpdatedProducts = new List<ProductArchiveItem>();

                // SKU'ları batch'lere böl
                var batches = allItems
                    .Select((sku, index) => new { sku, index })
                    .GroupBy(x => x.index / _batchSize)
                    .Select(g => g.Select(x => x.sku).ToList())
                    .ToList();

                _logger.LogInformation("🔢 {BatchCount} batch oluşturuldu", batches.Count);

                int totalSuccessCount = 0;
                int totalErrorCount = 0;
                int batchNumber = 1;

                foreach (var batch in batches)
                {
                    try
                    {
                        // Her batch öncesi token kontrolü
                        var tokenResponse = await exactService.GetValidToken();
                        if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.access_token))
                        {
                            _logger.LogWarning("⚠️ İşlem sırasında token geçersiz hale geldi");
                            break;
                        }

                        _logger.LogInformation("🔄 Batch {Current}/{Total} işleniyor ({Count} SKU)",
    batchNumber, batches.Count, batch.Count);

                        // ✨ Sabit log dosyası kullan - her seferinde aynı dosyanın üzerine yaz
                        var batchLogFile = _updateLogFile;

                        // Yeni optimize edilmiş metodu kullan
                        await shopifyService.UpdateProductStatusBySkuListAndSaveRawAsync(batch, batchLogFile);

                        // Log dosyasını oku ve sonuçları analiz et
                        var batchResults = await ProcessBatchResults(batchLogFile, batchId);
                        allUpdatedProducts.AddRange(batchResults);

                        // Başarılı ve hatalı sayıları güncelle
                        var successInBatch = batchResults.Count(r => r.Status == "Success");
                        var errorInBatch = batchResults.Count(r => r.Status == "Error");

                        totalSuccessCount += successInBatch;
                        totalErrorCount += errorInBatch;

                        _logger.LogInformation("✅ Batch {Current} tamamlandı - Başarılı: {Success}, Hatalı: {Error}",
                            batchNumber, successInBatch, errorInBatch);

                        batchNumber++;

                        // Batch'ler arası rate limiting
                        await Task.Delay(2000); // 2 saniye bekleme
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Batch {BatchNumber} işlenirken hata: {Error}", batchNumber, ex.Message);

                        // Hatalı batch'teki tüm SKU'ları hatalı olarak kaydet
                        var errorItems = batch.Select(sku => new ProductArchiveItem
                        {
                            Sku = sku,
                            UpdatedAt = DateTime.Now,
                            Status = "Error",
                            ErrorMessage = $"Batch error: {ex.Message}",
                            BatchId = batchId
                        }).ToList();

                        allUpdatedProducts.AddRange(errorItems);
                        totalErrorCount += batch.Count;
                        batchNumber++;
                    }
                }

                // Archive dosyasını güncelle
                if (allUpdatedProducts.Any())
                {
                    await UpdateArchiveFileAsync(allUpdatedProducts);
                    _logger.LogInformation("📁 {Count} ürün archive dosyasına eklendi", allUpdatedProducts.Count);
                }

                _logger.LogInformation("🎉 Tüm batch'ler tamamlandı - Toplam Başarılı: {Success}, Toplam Hatalı: {Error}",
                    totalSuccessCount, totalErrorCount);

                await settingsService.SetSettingAsync("LastProductSync",
                    DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    "Son product sync zamanı",
                    "System");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Product sync operasyonları sırasında hata");
            }
        }

        private async Task<List<ProductArchiveItem>> ProcessBatchResults(string batchLogFile, string batchId)
        {
            var results = new List<ProductArchiveItem>();

            try
            {
                if (!File.Exists(batchLogFile))
                {
                    _logger.LogWarning("⚠️ Batch log dosyası bulunamadı: {FilePath}", batchLogFile);
                    return results;
                }

                var logContent = await File.ReadAllTextAsync(batchLogFile);
                var logEntries = JsonSerializer.Deserialize<JsonElement[]>(logContent);

                foreach (var entry in logEntries)
                {
                    if (entry.TryGetProperty("sku", out var skuElement) &&
                        entry.TryGetProperty("status", out var statusElement))
                    {
                        var sku = skuElement.GetString();
                        var status = statusElement.GetString();

                        var archiveItem = new ProductArchiveItem
                        {
                            Sku = sku,
                            UpdatedAt = DateTime.Now,
                            Status = DetermineArchiveStatus(status),
                            ErrorMessage = status?.Contains("hata") == true || status?.Contains("error") == true ? status : null,
                            BatchId = batchId,
                            Notes = status
                        };

                        results.Add(archiveItem);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Batch results işlenirken hata: {FilePath}", batchLogFile);
            }

            return results;
        }

        private string DetermineArchiveStatus(string shopifyStatus)
        {
            if (string.IsNullOrEmpty(shopifyStatus))
                return "Unknown";

            if (shopifyStatus.Contains("bulunamadı") || shopifyStatus.Contains("not found"))
                return "NotFound";

            if (shopifyStatus.Contains("hata") || shopifyStatus.Contains("error"))
                return "Error";

            if (shopifyStatus.Contains("silindi") || shopifyStatus.Contains("deleted") ||
                shopifyStatus.Contains("archived"))
                return "Success";

            return "Success";
        }

        private async Task UpdateArchiveFileAsync(List<ProductArchiveItem> newItems)
        {
            try
            {
                List<ProductArchiveItem> allItems = new List<ProductArchiveItem>();

                // Mevcut dosyayı oku (varsa)
                if (File.Exists(_archiveFilePath))
                {
                    var existingContent = await File.ReadAllTextAsync(_archiveFilePath);
                    if (!string.IsNullOrEmpty(existingContent))
                    {
                        var existingItems = JsonSerializer.Deserialize<List<ProductArchiveItem>>(existingContent);
                        if (existingItems != null)
                        {
                            allItems.AddRange(existingItems);
                        }
                    }
                }

                // Yeni itemları ekle
                allItems.AddRange(newItems);

                // Tarihe göre sırala (en yeni en üstte)
                allItems = allItems.OrderByDescending(x => x.UpdatedAt).ToList();

                // Dosyaya yaz
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var jsonContent = JsonSerializer.Serialize(allItems, options);

                // Dosya dizinini oluştur (yoksa)
                var directory = Path.GetDirectoryName(_archiveFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await File.WriteAllTextAsync(_archiveFilePath, jsonContent);

                _logger.LogDebug("💾 Archive dosyası güncellendi: {FilePath}", _archiveFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Archive dosyası güncellenirken hata: {Error}", ex.Message);
            }
        }

        // Archive için product bilgilerini tutacak sınıf
        public class ProductArchiveItem
        {
            public string Sku { get; set; }
            public DateTime UpdatedAt { get; set; }
            public string Status { get; set; } // "Success", "Error", "NotFound", etc.
            public string ErrorMessage { get; set; } // Hata durumunda
            public string BatchId { get; set; } // Hangi batch'te güncellendiği
            public string Notes { get; set; } // Ek notlar için (original status mesajı)
        }
    }
}