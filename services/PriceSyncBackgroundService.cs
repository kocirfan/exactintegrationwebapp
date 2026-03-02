using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ShopifyProductApp.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ShopifyProductApp.Services
{
    public class PriceSyncBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<PriceSyncBackgroundService> _logger;
        private readonly string _logFile = "Data/price_sync_log.json";

        public PriceSyncBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<PriceSyncBackgroundService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🚀 Price Sync Service başlatıldı - Her 10 dakikada bir çalışacak");
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("🔄 Fiyat sync başlıyor...");

                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var exactService = scope.ServiceProvider.GetRequiredService<ExactService>();
                        var shopifyService = scope.ServiceProvider.GetRequiredService<ShopifyService>();

                        var tokenResponse = await exactService.GetValidToken();
                        if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.access_token))
                        {
                            _logger.LogWarning("⚠️ Geçerli token yok, 5 dakika sonra tekrar denenecek");
                            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                            continue;
                        }

                        await PerformPriceSync(exactService, shopifyService);
                    }

                    _logger.LogInformation("✅ Fiyat sync tamamlandı");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Price sync service hatası: {Error}", ex.Message);
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                    continue;
                }

                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            }
        }

        private async Task PerformPriceSync(ExactService exactService, ShopifyService shopifyService)
        {
            try
            {
                // Son 15 dakikada değişen fiyatları al
                var since = DateTime.UtcNow.AddMinutes(-15);
                var changedPrices = await exactService.GetRecentlyChangedSalesItemPricesAsync(since);

                if (changedPrices == null || changedPrices.Count == 0)
                {
                    _logger.LogInformation("ℹ️ Son 15 dakikada değişen fiyat yok");
                    return;
                }

                _logger.LogInformation("💰 {Count} değişmiş fiyat bulundu, Shopify güncelleniyor...", changedPrices.Count);

                foreach (var priceEntry in changedPrices)
                {
                    try
                    {
                        // Shopify'daki mevcut title'ı al — title değişmesin
                        var searchResult = await shopifyService.GetProductBySkuWithDuplicateHandlingAsync(priceEntry.ItemCode);

                        if (!searchResult.Found || searchResult.Match == null)
                        {
                            _logger.LogInformation("⚠️ SKU '{ItemCode}' Shopify'da bulunamadı, atlanıyor", priceEntry.ItemCode);
                            continue;
                        }

                        var currentTitle = searchResult.Match.ProductTitle;

                        await shopifyService.UpdateProductTitleAndPriceBySkuAndSaveRawAsync(
                            priceEntry.ItemCode,
                            currentTitle,
                            priceEntry.Price,
                            _logFile);

                        _logger.LogInformation("✅ Fiyat güncellendi: SKU={ItemCode}, Price={Price}", priceEntry.ItemCode, priceEntry.Price);

                        // Shopify rate limit için ürünler arası bekleme
                        await Task.Delay(2000);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ SKU '{ItemCode}' fiyat güncellenirken hata: {Error}", priceEntry.ItemCode, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Fiyat sync operasyonu sırasında hata");
            }
        }
    }
}
