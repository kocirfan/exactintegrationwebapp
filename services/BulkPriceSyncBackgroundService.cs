using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShopifyProductApp.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShopifyProductApp.Services
{
    /// <summary>
    /// Her Pazartesi 20:45'de çalışır (TEST MODU - normalde Her Pazar 20:00):
    /// 1. Exact'tan tüm ürünleri (ID, Code, StandardSalesPrice) çeker
    /// 2. Her ürün kodunu Shopify'da SKU ile arar, eşleşen varyantın fiyatını günceller
    /// 3. Sonuçları Data/bulk_price_sync_log_<timestamp>.txt dosyasına yazar
    /// </summary>
    public class BulkPriceSyncBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<BulkPriceSyncBackgroundService> _logger;

        public BulkPriceSyncBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<BulkPriceSyncBackgroundService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🚀 Bulk Price Sync Service başlatıldı - TEST: Her Pazartesi 20:45'de çalışacak");

            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = GetDelayUntilNextMondayAt2045();
                _logger.LogInformation("⏳ TEST - Bir sonraki çalışma (Pazartesi 20:45): {NextRun:dd.MM.yyyy HH:mm}", DateTime.Now.Add(delay));

                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (stoppingToken.IsCancellationRequested) break;

                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var exactService = scope.ServiceProvider.GetRequiredService<ExactService>();
                    var shopifyService = scope.ServiceProvider.GetRequiredService<ShopifyService>();

                    var tokenResponse = await exactService.GetValidToken();
                    if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.access_token))
                    {
                        _logger.LogWarning("⚠️ Geçerli token yok, Bulk Price Sync çalıştırılamadı");
                        continue;
                    }

                    await PerformBulkPriceSync(exactService, shopifyService, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("ℹ️ Bulk Price Sync iptal edildi");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Bulk Price Sync servisinde beklenmeyen hata: {Error}", ex.Message);
                }
            }
        }

        private static TimeSpan GetDelayUntilNextMondayAt2045()
        {
            var now = DateTime.Now;
            // Bu haftanın Pazartesi günü 20:45'ini hesapla
            var daysUntilMonday = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
            var nextMonday = now.Date.AddDays(daysUntilMonday).AddHours(20).AddMinutes(45);

            // Eğer bu an Pazartesi 20:45'ten geçtiyse, gelecek haftaya al
            if (nextMonday <= now)
                nextMonday = nextMonday.AddDays(7);

            return nextMonday - now;
        }

        private async Task PerformBulkPriceSync(ExactService exactService, ShopifyService shopifyService, CancellationToken stoppingToken)
        {
            _logger.LogInformation("📦 Exact'tan tüm ürünler çekiliyor...");

            var exactItems = await exactService.GetAllItemsSummaryAsync();

            if (exactItems == null || exactItems.Count == 0)
            {
                _logger.LogWarning("⚠️ Exact'tan ürün çekilemedi, işlem sonlandırılıyor");
                return;
            }

            _logger.LogInformation("✅ {Count} ürün çekildi. Shopify fiyat güncellemesi başlıyor...", exactItems.Count);

            Directory.CreateDirectory("Data");
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var logFilePath = Path.Combine("Data", $"bulk_price_sync_log_{timestamp}.txt");

            // Log satırları: (kod, fiyat)
            var logLines = new List<(string Code, decimal Price)>();
            int totalUpdated = 0;
            int totalSkipped = 0;
            int totalFailed = 0;

            for (int i = 0; i < exactItems.Count; i++)
            {
                if (stoppingToken.IsCancellationRequested) break;

                var item = exactItems[i];

                // Fiyatsız veya kodu olmayanları atla
                if (item.StandardSalesPrice == null || item.StandardSalesPrice <= 0 || string.IsNullOrWhiteSpace(item.Code))
                {
                    totalSkipped++;
                    continue;
                }

                var newPrice = item.StandardSalesPrice.Value;

                try
                {
                    var searchResult = await shopifyService.GetProductBySkuWithDuplicateHandlingAsync(item.Code);

                    if (!searchResult.Found || searchResult.Match == null)
                    {
                        _logger.LogDebug("⚠️ SKU '{Code}' Shopify'da bulunamadı, atlanıyor", item.Code);
                        totalSkipped++;
                        continue;
                    }

                    var allMatches = searchResult.AllMatches ?? new List<ShopifyService.ProductInfo> { searchResult.Match };
                    bool anySuccess = false;

                    foreach (var product in allMatches)
                    {
                        if (stoppingToken.IsCancellationRequested) break;
                        if (string.IsNullOrEmpty(product.ProductId) || string.IsNullOrEmpty(product.VariantId)) continue;

                        var updated = await shopifyService.UpdateVariantPriceDirectAsync(
                            product.ProductId,
                            product.VariantId,
                            newPrice
                        );

                        if (updated)
                            anySuccess = true;

                        // Shopify rate limit için bekleme
                        await Task.Delay(500, stoppingToken);
                    }

                    if (anySuccess)
                    {
                        totalUpdated++;
                        logLines.Add((item.Code, newPrice));
                        _logger.LogInformation("[{Index}/{Total}] ✅ {Code} => {Price:F2}", i + 1, exactItems.Count, item.Code, newPrice);
                    }
                    else
                    {
                        totalFailed++;
                        _logger.LogWarning("[{Index}/{Total}] ❌ {Code} güncellenemedi", i + 1, exactItems.Count, item.Code);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ SKU '{Code}' işlenirken hata: {Error}", item.Code, ex.Message);
                    totalFailed++;
                }

                // Her 100 üründe bir ilerleme raporu
                if ((i + 1) % 100 == 0)
                {
                    _logger.LogInformation("📊 [{Done}/{Total}] Güncellendi: {Updated} | Atlandı: {Skipped} | Hata: {Failed}",
                        i + 1, exactItems.Count, totalUpdated, totalSkipped, totalFailed);
                }
            }

            // Log dosyasını yaz
            await WriteLogFileAsync(logFilePath, exactItems.Count, totalUpdated, totalSkipped, totalFailed, logLines, stoppingToken);

            _logger.LogInformation("🎉 Bulk Price Sync tamamlandı!");
            _logger.LogInformation("   Exact toplam ürün : {Total}", exactItems.Count);
            _logger.LogInformation("   Güncellenen       : {Updated}", totalUpdated);
            _logger.LogInformation("   Atlandı           : {Skipped}", totalSkipped);
            _logger.LogInformation("   Hata              : {Failed}", totalFailed);
            _logger.LogInformation("   Log dosyası       : {LogFile}", logFilePath);
        }

        private static async Task WriteLogFileAsync(
            string logFilePath,
            int totalExact,
            int totalUpdated,
            int totalSkipped,
            int totalFailed,
            List<(string Code, decimal Price)> logLines,
            CancellationToken stoppingToken)
        {
            var sb = new StringBuilder();
            sb.AppendLine("==============================================");
            sb.AppendLine("         BULK PRICE SYNC LOG                 ");
            sb.AppendLine("==============================================");
            sb.AppendLine($"Tarih                  : {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
            sb.AppendLine($"Exact'tan çekilen ürün : {totalExact}");
            sb.AppendLine($"Güncellenen            : {totalUpdated}");
            sb.AppendLine($"Atlandı                : {totalSkipped}");
            sb.AppendLine($"Hata                   : {totalFailed}");
            sb.AppendLine("----------------------------------------------");
            sb.AppendLine();
            sb.AppendLine($"{"Ürün Kodu",-30} {"Güncellenen Fiyat",15}");
            sb.AppendLine(new string('-', 47));

            foreach (var (code, price) in logLines)
                sb.AppendLine($"{code,-30} {price,15:F2}");

            sb.AppendLine();
            sb.AppendLine("==============================================");
            sb.AppendLine($"TOTAL UPDATED: {totalUpdated}");
            sb.AppendLine("==============================================");

            await File.WriteAllTextAsync(logFilePath, sb.ToString(), Encoding.UTF8, stoppingToken);
        }
    }
}
