using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ShopifyProductApp.Data;
using ShopifyProductApp.Models;
using ShopifyProductApp.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ShopifyProductApp.Services
{
    public class StockSyncBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ITokenManager _tokenManager;
        private readonly ILogger<StockSyncBackgroundService> _logger;
        private readonly StockSyncLogService _stockSyncLogService;

        public StockSyncBackgroundService(
            IServiceProvider serviceProvider,
            ITokenManager tokenManager,
            ILogger<StockSyncBackgroundService> logger,
            StockSyncLogService stockSyncLogService)
        {
            _serviceProvider = serviceProvider;
            _tokenManager = tokenManager;
            _logger = logger;
            _stockSyncLogService = stockSyncLogService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🚀 Stock Sync Service başlatıldı - Her gün 09:30'da çalışacak");
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.Now;
                    var nextRun = GetNextRunTime(now);
                    var delay = nextRun - now;

                    _logger.LogInformation("⏰ Sonraki çalıştırma zamanı: {NextRun}", nextRun.ToString("dd.MM.yyyy HH:mm:ss"));

                    await Task.Delay(delay, stoppingToken);

                    if (stoppingToken.IsCancellationRequested) break;

                    _logger.LogInformation("🔄 Günlük stok senkronizasyonu başlıyor...");

                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var exactService = scope.ServiceProvider.GetRequiredService<ExactService>();
                        var shopifyService = scope.ServiceProvider.GetRequiredService<ShopifyService>();
                        // ✅ DÜZELTME: ISettingsService kullan
                        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

                        // Token kontrolü
                        var tokenResponse = await exactService.GetValidToken();
                        if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.access_token))
                        {
                            _logger.LogWarning("⚠️ Geçerli token yok, stok sync atlanıyor");
                            continue;
                        }

                        await PerformStockSync(exactService, shopifyService, settingsService);
                    }

                    _logger.LogInformation("✅ Günlük stok senkronizasyonu tamamlandı");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Stock sync service hatası: {Error}", ex.Message);
                    _logger.LogInformation("⏳ Hata nedeniyle 1 saat bekleniyor, sonra normal programa devam edilecek...");
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                }
            }
        }

        private DateTime GetNextRunTime(DateTime currentTime)
        {
            var today0930 = new DateTime(currentTime.Year, currentTime.Month, currentTime.Day, 01, 30, 0);
            return currentTime < today0930 ? today0930 : today0930.AddDays(1);
        }

        // ✅ DÜZELTME: ISettingsService parametresi
        private async Task PerformStockSync(
            ExactService exactService,
            ShopifyService shopifyService,
            ISettingsService settingsService)
        {
            try
            {
                // Stoklu ürünleri al - yeni metodu kullan
                var exactItems = await exactService.GetAllStockedItemsAsync();

                if (exactItems == null || !exactItems.Any())
                {
                    _logger.LogWarning("⚠️ Stoklu ürün bulunamadı");
                    return;
                }

                _logger.LogInformation("📊 {Count} stoklu ürün bulundu", exactItems.Count);

                var shopifyProducts = await shopifyService.GetAllProductsRawAsync();

                // Eşleştirme indeksi: exact_product_id → kod → barcode (kod değişse bile ürün bulunur)
                var matchIndex = await shopifyService.BuildShopifyMatchIndexAsync();

                var batchResult = await shopifyService.UpdateMultipleStocksBatchAsync(
                    exactItems, shopifyProducts, "Data/daily_stock_sync.json", matchIndex);

                _logger.LogInformation("🎉 Stok senkronizasyonu tamamlandı - Başarılı: {Success}, Hatalı: {Error}",
                    batchResult.SuccessCount, batchResult.ErrorCount);

                // Her ürünün güncelleme kaydını DB'ye yaz (best-effort: DB hatası senkronu durdurmaz)
                await _stockSyncLogService.SaveAsync(batchResult.LogEntries);

                // NOT: Eski fiyat+başlık güncellemesi kaldırıldı - fiyatlar artık
                // NightlyPriceSyncBackgroundService ile her gece ayrıca senkronlanıyor

                shopifyProducts.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Stok sync operasyonları sırasında hata");
            }
        }
    }
}
