using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShopifyProductApp.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ShopifyProductApp.Services
{
    public class NoDiscountTagSyncService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<NoDiscountTagSyncService> _logger;

        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan LookbackWindow = TimeSpan.FromMinutes(12); // 10dk + 2dk tolerans

        public NoDiscountTagSyncService(
            IServiceProvider serviceProvider,
            ILogger<NoDiscountTagSyncService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🚀 NoDiscount Tag Sync Service başlatıldı - Her 10 dakikada bir çalışacak");

            // Uygulama başlarken diğer servislerin başlaması için kısa bekle
            await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("🔄 NoDiscount tag sync başlıyor...");

                    using var scope = _serviceProvider.CreateScope();
                    var exactService = scope.ServiceProvider.GetRequiredService<ExactService>();
                    var shopifyService = scope.ServiceProvider.GetRequiredService<ShopifyService>();

                    var token = await exactService.GetValidToken();
                    if (token == null)
                    {
                        _logger.LogWarning("⚠️ Geçerli token yok, 5 dakika sonra tekrar denenecek");
                        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                        continue;
                    }

                    await SyncNoDiscountTags(exactService, shopifyService, stoppingToken);

                    _logger.LogInformation("✅ NoDiscount tag sync tamamlandı");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ NoDiscount tag sync hatası: {Error}", ex.Message);
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }

        private async Task SyncNoDiscountTags(ExactService exactService, ShopifyService shopifyService, CancellationToken stoppingToken)
        {
            try
            {
                var since = DateTime.UtcNow - LookbackWindow;
                var dateFilter = since.ToString("yyyy-MM-ddTHH:mm:ss");

                // Son 12 dakikada modified olan webshop item'larını çek
                var recentItems = await exactService.GetRecentlyModifiedWebshopItemsAsync(since);

                if (recentItems == null || recentItems.Count == 0)
                {
                    _logger.LogInformation("ℹ️ Son {Minutes} dakikada modified webshop ürünü yok", LookbackWindow.TotalMinutes);
                    return;
                }

                _logger.LogInformation("📦 {Count} modified ürün bulundu, isNoDiscount kontrol ediliyor...", recentItems.Count);

                foreach (var item in recentItems)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    try
                    {
                        var itemId = item.ID.ToString();
                        var sku = item.Code;

                        if (string.IsNullOrEmpty(sku))
                        {
                            _logger.LogWarning("⚠️ SKU boş, ürün atlanıyor: ID={ItemId}", itemId);
                            continue;
                        }

                        bool isNoDiscount = await exactService.GetItemExtraFieldNoDiscountAsync(itemId);

                        _logger.LogInformation("🏷️ SKU={Sku} isNoDiscount={IsNoDiscount}", sku, isNoDiscount);

                        await shopifyService.SyncNoDiscountTagBySkuAsync(item.ID, sku, isNoDiscount);

                        // Shopify rate limit için ürünler arası bekleme
                        await Task.Delay(1500, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ SKU={Sku} isNoDiscount sync hatası: {Error}", item.Code, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ SyncNoDiscountTags operasyonu sırasında hata");
            }
        }
    }
}
