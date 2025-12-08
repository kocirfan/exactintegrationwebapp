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
    public class ProductPriceAndTitleUpdate : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ProductPriceAndTitleUpdate> _logger;
        private readonly string _archiveFilePath = "Data/arcivedproduct.json";
        private readonly string _updateLogFile = "Data/update_log.json";
        private readonly int _batchSize = 20; // Batch boyutu - ihtiyaca göre ayarlanabilir

        public ProductPriceAndTitleUpdate(
            IServiceProvider serviceProvider,
            ILogger<ProductPriceAndTitleUpdate> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🚀 Product Sync Service başlatıldı - Her 2 dakikada bir çalışacak");
            await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("🔄 Product sync işlemi başlıyor...");

                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var exactService = scope.ServiceProvider.GetRequiredService<ExactService>();
                        var shopifyService = scope.ServiceProvider.GetRequiredService<ShopifyService>();
                        // var settingsService = scope.ServiceProvider.GetRequiredService<SettingsService>();
                         var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

                        // Token kontrolü - ExactService içinden
                        var tokenResponse = await exactService.GetValidToken();
                        if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.access_token))
                        {
                            _logger.LogWarning("⚠️ Geçerli token yok, işlem atlanıyor");
                            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                            continue;
                        }

                        await PerformSyncOperations(exactService, shopifyService, settingsService);
                    }

                    _logger.LogInformation("✅ Product sync işlemi tamamlandı");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Product sync service hatası: {Error}", ex.Message);
                }

                await Task.Delay(TimeSpan.FromMinutes(140), stoppingToken);
            }
        }

        private async Task PerformSyncOperations(ExactService exactService, ShopifyService shopifyService, ISettingsService settingsService)
        {
            try
            {
                // ExactProductResponse döndürüyor artık
                var exactResponse = await exactService.GetItemsWebShopAndModified();

                if (exactResponse == null || !exactResponse.Success || !exactResponse.Results.Any())
                {
                    _logger.LogWarning("⚠️ İşlenecek ürün bulunamadı");
                    return;
                }

                var allItems = exactResponse.Results; // List<ExactProduct>

                _logger.LogInformation("📦 Toplam {Count} ürün işlenecek", allItems.Count);

                var batchStartTime = DateTime.Now;
                var batchId = batchStartTime.ToString("yyyyMMdd_HHmmss");
                var allUpdatedProducts = new List<ProductArchiveItem>();

                // Ürünleri batch'lere böl
                var batches = allItems
                    .Select((item, index) => new { item, index })
                    .GroupBy(x => x.index / _batchSize)
                    .Select(g => g.Select(x => x.item).ToList())
                    .ToList();

                _logger.LogInformation("🔢 {BatchCount} batch oluşturuldu (her batch'te ~{BatchSize} ürün)",
                    batches.Count, _batchSize);

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

                        _logger.LogInformation("🔄 Batch {Current}/{Total} işleniyor ({Count} ürün)",
                            batchNumber, batches.Count, batch.Count);

                        // Batch'teki her ürün için güncelleme yap
                        int batchSuccessCount = 0;
                        int batchErrorCount = 0;

                        foreach (var exactProduct in batch)
                        {
                            try
                            {
                                // Null kontrolü ve gerekli alanları kontrol et
                                if (string.IsNullOrEmpty(exactProduct.Code))
                                {
                                    _logger.LogWarning("⚠️ Ürün kodu boş, atlanıyor: {ProductId}", exactProduct.ID);
                                    continue;
                                }

                                var sku = exactProduct.Code;
                                var title = exactProduct.Description ?? "Başlık Bulunamadı";
                                var price = exactProduct.StandardSalesPrice ?? 0m;

                                // Log dosyası oluştur (her ürün için ayrı)
                               var logFile = _updateLogFile;

                                _logger.LogDebug("🔄 Güncelleniyor: SKU={Sku}, Title={Title}, Price={Price}",
                                    sku, title, price);
                                    if(sku == "CHS1051007016")
                                {
                                    Console.WriteLine("BURADA.");
                                }

                                // Shopify'da ürünü güncelle
                                await shopifyService.UpdateProductTitleAndPriceBySkuAndSaveRawAsync(
                                    sku, title, price, logFile);

                                // Log dosyasından sonucu oku
                                var updateResult = await ReadUpdateResult(logFile);

                                var archiveItem = new ProductArchiveItem
                                {
                                    Sku = sku,
                                    UpdatedAt = DateTime.Now,
                                    Status = updateResult.Success ? "Success" : "Error",
                                    ErrorMessage = updateResult.Success ? null : updateResult.ErrorMessage,
                                    BatchId = batchId,
                                    Notes = $"Title: {title}, Price: {price}, Status: {updateResult.StatusMessage}"
                                };

                                allUpdatedProducts.Add(archiveItem);

                                if (updateResult.Success)
                                {
                                    batchSuccessCount++;
                                    _logger.LogDebug("✅ Başarılı: {Sku}", sku);
                                }
                                else
                                {
                                    batchErrorCount++;
                                    _logger.LogWarning("❌ Hatalı: {Sku} - {Error}", sku, updateResult.ErrorMessage);
                                }

                                // Ürünler arası kısa bekleme (rate limiting)
                                await Task.Delay(500);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "❌ Ürün güncellenirken hata: SKU={Sku}, Error={Error}",
                                    exactProduct.Code, ex.Message);

                                var errorItem = new ProductArchiveItem
                                {
                                    Sku = exactProduct.Code ?? "UNKNOWN",
                                    UpdatedAt = DateTime.Now,
                                    Status = "Error",
                                    ErrorMessage = ex.Message,
                                    BatchId = batchId,
                                    Notes = $"Exception during update: {ex.Message}"
                                };

                                allUpdatedProducts.Add(errorItem);
                                batchErrorCount++;
                            }
                        }

                        totalSuccessCount += batchSuccessCount;
                        totalErrorCount += batchErrorCount;

                        _logger.LogInformation("✅ Batch {Current}/{Total} tamamlandı - Başarılı: {Success}, Hatalı: {Error}",
                            batchNumber, batches.Count, batchSuccessCount, batchErrorCount);

                        batchNumber++;

                        // Batch'ler arası rate limiting
                        await Task.Delay(2000); // 2 saniye bekleme
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Batch {BatchNumber} işlenirken hata: {Error}", batchNumber, ex.Message);

                        // Hatalı batch'teki tüm ürünleri hatalı olarak kaydet
                        var errorItems = batch.Select(product => new ProductArchiveItem
                        {
                            Sku = product.Code ?? "UNKNOWN",
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

                _logger.LogInformation("🎉 Tüm işlem tamamlandı - Toplam İşlenen: {Total}, Başarılı: {Success}, Hatalı: {Error}",
                    allItems.Count, totalSuccessCount, totalErrorCount);

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

        private async Task<UpdateResult> ReadUpdateResult(string logFilePath)
        {
            try
            {
                if (!File.Exists(logFilePath))
                {
                    return new UpdateResult { Success = false, ErrorMessage = "Log dosyası bulunamadı" };
                }

                var logContent = await File.ReadAllTextAsync(logFilePath);
                var logEntry = JsonSerializer.Deserialize<JsonElement>(logContent);

                if (logEntry.TryGetProperty("Status", out var statusElement))
                {
                    var status = statusElement.GetString();
                    var success = !status.Contains("hata") && !status.Contains("error") && !status.Contains("bulunamadı");

                    return new UpdateResult
                    {
                        Success = success,
                        StatusMessage = status,
                        ErrorMessage = success ? null : status
                    };
                }

                return new UpdateResult { Success = false, ErrorMessage = "Status bilgisi bulunamadı" };
            }
            catch (Exception ex)
            {
                return new UpdateResult { Success = false, ErrorMessage = $"Log okuma hatası: {ex.Message}" };
            }
        }

        private class UpdateResult
        {
            public bool Success { get; set; }
            public string StatusMessage { get; set; }
            public string ErrorMessage { get; set; }
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