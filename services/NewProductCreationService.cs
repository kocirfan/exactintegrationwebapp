
using ExactOnline.Models;
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
    public class NewProductCreationService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<NewProductCreationService> _logger;
        private readonly string _newProductLogFilePath = "Data/Logs/new_product_creation.json";
        private readonly string _newProductArchiveFilePath = "Data/newproducts.json";
        private const int ScheduledHour = 23;
        private const int ScheduledMinute = 30;

        public NewProductCreationService(
            IServiceProvider serviceProvider,
            ILogger<NewProductCreationService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🆕 New Product Creation Service başlatıldı - Günlük çalışma saati: {Hour:D2}:{Minute:D2}",
                ScheduledHour, ScheduledMinute);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.Now;
                    var nextRunTime = GetNextScheduledTime(now);
                    var waitTime = nextRunTime - now;

                    _logger.LogInformation("⏰ Sonraki çalışma zamanı: {NextTime} ({Hours} saat {Minutes} dakika sonra)",
                        nextRunTime.ToString("dd.MM.yyyy HH:mm:ss"),
                        (int)waitTime.TotalHours,
                        waitTime.Minutes);

                    await Task.Delay(waitTime, stoppingToken);

                    _logger.LogInformation("🔄 Yeni ürün kontrolü başlıyor... ({Time})",
                        DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"));

                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var exactService = scope.ServiceProvider.GetRequiredService<ExactService>();
                        var shopifyService = scope.ServiceProvider.GetRequiredService<ShopifyService>();
                        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

                        var tokenResponse = await exactService.GetValidToken();
                        if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.access_token))
                        {
                            _logger.LogWarning("⚠️ Geçerli token yok, işlem atlanıyor. Yarın {Hour:D2}:{Minute:D2}'da tekrar denenecek.",
                                ScheduledHour, ScheduledMinute);
                        }
                        else
                        {
                            await ProcessNewProducts(exactService, shopifyService, settingsService);
                        }
                    }

                    _logger.LogInformation("✅ Yeni ürün kontrolü tamamlandı ({Time})",
                        DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ New Product Creation Service hatası: {Error}", ex.Message);
                }
            }

            _logger.LogInformation("🛑 New Product Creation Service durduruluyor...");
        }

        private DateTime GetNextScheduledTime(DateTime currentTime)
        {
            var scheduledTime = currentTime.Date
                .AddHours(ScheduledHour)
                .AddMinutes(ScheduledMinute);

            if (currentTime >= scheduledTime)
            {
                scheduledTime = scheduledTime.AddDays(1);
            }

            return scheduledTime;
        }

        // ✅ DÜZELTME: ISettingsService parametresi
        private async Task ProcessNewProducts(
            ExactService exactService,
            ShopifyService shopifyService,
            ISettingsService settingsService)
        {
            try
            {
                _logger.LogInformation("🔍 Yeni ürünler kontrol ediliyor...");

                var newProducts = await exactService.GetNewCreatedProductAsync();

                if (newProducts == null || !newProducts.Any())
                {
                    _logger.LogInformation("ℹ️ Yeni ürün bulunamadı");
                    return;
                }

                _logger.LogInformation("📦 {Count} yeni ürün bulundu, Shopify'a eklenecek", newProducts.Count);

                var batchId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                var createdProducts = new List<NewProductArchiveItem>();

                int successCount = 0;
                int errorCount = 0;
                int skippedCount = 0;

                foreach (var exactProduct in newProducts)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(exactProduct.Code))
                        {
                            _logger.LogWarning("⚠️ Ürün kodu boş, atlanıyor: {ProductId}", exactProduct.ID);
                            skippedCount++;
                            continue;
                        }

                        if (await IsProductAlreadyCreated(exactProduct.Code))
                        {
                            _logger.LogInformation("ℹ️ Ürün daha önce oluşturulmuş, atlanıyor: {Sku}", exactProduct.Code);
                            skippedCount++;
                            continue;
                        }

                        var logFile = _newProductLogFilePath;

                        _logger.LogInformation("🆕 Yeni ürün oluşturuluyor: SKU={Sku}, Title={Title}, Price={Price}",
                            exactProduct.Code, exactProduct.Description, exactProduct.StandardSalesPrice);

                        var success = await shopifyService.CreateProductAsync(exactProduct, logFile);

                        var archiveItem = new NewProductArchiveItem
                        {
                            Sku = exactProduct.Code,
                            Title = exactProduct.Description,
                            Price = exactProduct.StandardSalesPrice,
                            Stock = exactProduct.Stock,
                            Barcode = exactProduct.Barcode,
                            CreatedAt = DateTime.UtcNow,
                            Status = success ? "Success" : "Error",
                            ErrorMessage = success ? null : "Ürün Shopify'da oluşturulamadı",
                            BatchId = batchId,
                            ExactCreatedDate = exactProduct.Created,
                            ExactProductId = exactProduct.ID.ToString()
                        };

                        createdProducts.Add(archiveItem);

                        if (success)
                        {
                            successCount++;
                            _logger.LogInformation("✅ Yeni ürün başarıyla oluşturuldu: {Sku} - {Title}",
                                exactProduct.Code, exactProduct.Description);
                        }
                        else
                        {
                            errorCount++;
                            _logger.LogWarning("❌ Yeni ürün oluşturulamadı: {Sku}", exactProduct.Code);
                        }

                        await Task.Delay(1000);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Yeni ürün oluşturulurken hata: SKU={Sku}, Error={Error}",
                            exactProduct.Code, ex.Message);

                        var errorItem = new NewProductArchiveItem
                        {
                            Sku = exactProduct.Code ?? "UNKNOWN",
                            Title = exactProduct.Description ?? "N/A",
                            Price = exactProduct.StandardSalesPrice,
                            CreatedAt = DateTime.UtcNow,
                            Status = "Error",
                            ErrorMessage = ex.Message,
                            BatchId = batchId,
                            ExactCreatedDate = exactProduct.Created,
                            ExactProductId = exactProduct.ID.ToString()
                        };

                        createdProducts.Add(errorItem);
                        errorCount++;
                    }
                }

                if (createdProducts.Any())
                {
                    await UpdateArchiveFileAsync(createdProducts);
                    _logger.LogInformation("📁 {Count} yeni ürün kaydı archive dosyasına eklendi", createdProducts.Count);
                }

                _logger.LogInformation(
                    "🎉 Yeni ürün işlemi tamamlandı\n" +
                    "   📊 Toplam Bulunan: {Total}\n" +
                    "   ✅ Başarılı: {Success}\n" +
                    "   ❌ Hatalı: {Error}\n" +
                    "   ⏭️ Atlanan: {Skipped}",
                    newProducts.Count, successCount, errorCount, skippedCount);

                await settingsService.SetSettingAsync(
                    "LastNewProductSync",
                    DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    "Son yeni ürün sync zamanı",
                    "System");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Yeni ürün işleme sırasında kritik hata");
            }
        }

        private async Task<bool> IsProductAlreadyCreated(string sku)
        {
            try
            {
                if (!File.Exists(_newProductArchiveFilePath))
                    return false;

                var content = await File.ReadAllTextAsync(_newProductArchiveFilePath);
                if (string.IsNullOrEmpty(content))
                    return false;

                var items = JsonSerializer.Deserialize<List<NewProductArchiveItem>>(content);
                if (items == null)
                    return false;

                return items.Any(x => x.Sku == sku && x.Status == "Success");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Archive dosyası kontrol edilirken hata: {Error}", ex.Message);
                return false;
            }
        }

        private async Task UpdateArchiveFileAsync(List<NewProductArchiveItem> newItems)
        {
            try
            {
                List<NewProductArchiveItem> allItems = new List<NewProductArchiveItem>();

                if (File.Exists(_newProductArchiveFilePath))
                {
                    var existingContent = await File.ReadAllTextAsync(_newProductArchiveFilePath);
                    if (!string.IsNullOrEmpty(existingContent))
                    {
                        var existingItems = JsonSerializer.Deserialize<List<NewProductArchiveItem>>(existingContent);
                        if (existingItems != null)
                        {
                            allItems.AddRange(existingItems);
                        }
                    }
                }

                allItems.AddRange(newItems);
                allItems = allItems.OrderByDescending(x => x.CreatedAt).ToList();

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var jsonContent = JsonSerializer.Serialize(allItems, options);

                var directory = Path.GetDirectoryName(_newProductArchiveFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await File.WriteAllTextAsync(_newProductArchiveFilePath, jsonContent);

                _logger.LogDebug("💾 Archive dosyası güncellendi: {FilePath} - Toplam kayıt: {Count}",
                    _newProductArchiveFilePath, allItems.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Archive dosyası güncellenirken hata: {Error}", ex.Message);
            }
        }

        public class NewProductArchiveItem
        {
            public string Sku { get; set; }
            public string Title { get; set; }
            public decimal? Price { get; set; }
            public decimal? Stock { get; set; }
            public string Barcode { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTimeOffset? ExactCreatedDate { get; set; }
            public string ExactProductId { get; set; }
            public string Status { get; set; }
            public string ErrorMessage { get; set; }
            public string BatchId { get; set; }
        }
    }
}