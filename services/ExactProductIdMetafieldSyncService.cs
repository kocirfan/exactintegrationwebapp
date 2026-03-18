using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ShopifyProductApp.Services
{
    /// <summary>
    /// Exact'tan 10 ürün çekip Shopify'da ilgili ürünün
    /// custom.exact_product_id metafield'ını güncelleyen background service.
    /// Her çalıştığında 10 ürün işler, sonraki çalışmada kaldığı yerden devam eder.
    /// Restart sonrası da kaldığı yerden devam eder (skip dosyaya kaydedilir).
    /// </summary>
    public class ExactProductIdMetafieldSyncService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ExactProductIdMetafieldSyncService> _logger;

        // Sayfalama durumunu bellekte tut
        private int _currentSkip = 0;
        private const int BatchSize = 100;

        // İstatistik takibi
        private int _totalProcessed = 0;
        private int _totalSuccess = 0;
        private int _totalSkipped = 0;
        private int _totalError = 0;
        private int _turNumber = 0;

        // Skip durumunu kaydetmek için dosya
        private static readonly string SkipStateFile = Path.Combine(AppContext.BaseDirectory, "exact_sync_skip.txt");

        // Tur özetlerini kaydetmek için log dosyası
        private static readonly string TurLogFile = Path.Combine(AppContext.BaseDirectory, "exact_sync_tur_log.txt");

        // ExactProductId → ShopifyProductId cache dosyası
        public static readonly string ExactIdCacheFile = Path.Combine(AppContext.BaseDirectory, "exact_id_cache.json");
        private static readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);

        public ExactProductIdMetafieldSyncService(
            IServiceProvider serviceProvider,
            ILogger<ExactProductIdMetafieldSyncService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🕐 ExactProductId Metafield Sync Service başlatıldı - her gün 04:30'da çalışacak");

            while (!stoppingToken.IsCancellationRequested)
            {
                // Bir sonraki 04:30'a kadar bekle
                var delay = GetDelayUntilNextRun(hour: 4, minute: 30);
                _logger.LogInformation("⏳ Sonraki çalışma: {NextRun:yyyy-MM-dd HH:mm:ss} ({Delay:hh\\:mm\\:ss} sonra)",
                    DateTime.Now.Add(delay), delay);

                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                _logger.LogInformation("🚀 04:30 geldi, ExactProductId Metafield Sync turu başlıyor...");

                // Önceki oturumdan kalan skip değerini yükle (her tur başında sıfırdan başla)
                _currentSkip = 0;
                _totalProcessed = 0;
                _totalSuccess = 0;
                _totalSkipped = 0;
                _totalError = 0;
                SaveSkipState(0);

                // Tüm ürünler işlenene kadar batch batch ilerle
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var exactService = scope.ServiceProvider.GetRequiredService<ExactService>();
                        var shopifyService = scope.ServiceProvider.GetRequiredService<ShopifyService>();

                        // Token al — DB hatası oluşursa retry ile tekrar dene
                        var tokenResponse = await GetValidTokenWithRetryAsync(exactService, stoppingToken);
                        if (string.IsNullOrEmpty(tokenResponse?.access_token))
                        {
                            _logger.LogWarning("⚠️ Geçerli token yok, 1 dakika sonra tekrar denenecek");
                            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                            continue;
                        }

                        bool turBitti = await ProcessBatchAndCheckFinished(exactService, shopifyService, stoppingToken);
                        if (turBitti)
                        {
                            _logger.LogInformation("✅ Tur tamamlandı, servis bir sonraki 04:30'u bekliyor.");
                            break; // İç döngüden çık, dış döngü bir sonraki 04:30'u bekleyecek
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ ExactProductId sync hatası: {Error}", ex.Message);
                        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    }

                    // Her batch arasında 5 saniye bekle
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }

        /// <summary>
        /// Bir sonraki belirtilen saate kadar olan gecikmeyi hesaplar.
        /// </summary>
        private static TimeSpan GetDelayUntilNextRun(int hour, int minute)
        {
            var now = DateTime.Now;
            var next = DateTime.Today.AddHours(hour).AddMinutes(minute);
            if (next <= now)
                next = next.AddDays(1);
            return next - now;
        }

        /// <summary>
        /// Token almayı dener. DB bağlantı hatası gelirse 3 kez 15'er saniye bekleyerek retry yapar.
        /// </summary>
        private async Task<TokenResponse> GetValidTokenWithRetryAsync(ExactService exactService, CancellationToken stoppingToken)
        {
            int attempt = 0;
            const int maxAttempts = 3;

            while (attempt < maxAttempts)
            {
                try
                {
                    return await exactService.GetValidToken();
                }
                catch (Microsoft.Data.SqlClient.SqlException sqlEx) when (IsTransientError(sqlEx))
                {
                    attempt++;
                    if (attempt >= maxAttempts)
                    {
                        _logger.LogError(sqlEx, "❌ Token için DB bağlantısı {Max} denemede de kurulamadı", maxAttempts);
                        return null;
                    }
                    _logger.LogWarning("⚠️ DB geçici hata (deneme {Attempt}/{Max}), 15sn bekleniyor: {Msg}", attempt, maxAttempts, sqlEx.Message);
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Token alma hatası");
                    return null;
                }
            }

            return null;
        }

        private static bool IsTransientError(Microsoft.Data.SqlClient.SqlException ex)
        {
            // Transport hatası (35), bağlantı hatası (233, -2, 20), timeout (‑2)
            int[] transientErrors = { -2, 20, 35, 233, 10053, 10054, 10060 };
            return Array.IndexOf(transientErrors, ex.Number) >= 0;
        }

        /// <summary>
        /// Bir batch işler. Tur tamamlandıysa (ürün kalmadıysa) true döner.
        /// </summary>
        private async Task<bool> ProcessBatchAndCheckFinished(ExactService exactService, ShopifyService shopifyService, CancellationToken stoppingToken)
        {
            _logger.LogInformation("🔄 [Tur {Tur}] skip={Skip} konumundan {BatchSize} ürün çekiliyor...", _turNumber + 1, _currentSkip, BatchSize);

            // Exact'tan batch al
            var items = await FetchExactItemsBatchAsync(exactService, _currentSkip, BatchSize);

            if (items == null || items.Count == 0)
            {
                // ──────────── TUR SONU ────────────
                _turNumber++;
                _logger.LogInformation(
                    "🏁 ===== TUR {Tur} TAMAMLANDI ===== " +
                    "Toplam işlenen: {Total} | Güncellenen: {Success} | Shopify'da yok: {Skipped} | Hatalı: {Error}",
                    _turNumber, _totalProcessed, _totalSuccess, _totalSkipped, _totalError);

                // Tur özetini log dosyasına yaz
                AppendTurLog(_turNumber, _totalProcessed, _totalSuccess, _totalSkipped, _totalError);

                // Skip dosyasını temizle
                SaveSkipState(0);

                return true; // Tur bitti
            }

            _logger.LogInformation("📦 {Count} ürün alındı (toplam şimdiye kadar işlenen: {Total}), Shopify güncelleniyor...",
                items.Count, _totalProcessed + items.Count);

            int successCount = 0;
            int skipCount = 0;
            int errorCount = 0;

            foreach (var item in items)
            {
                if (stoppingToken.IsCancellationRequested) break;

                try
                {
                    var exactId = item.ContainsKey("ID") ? item["ID"]?.ToString() : null;
                    var itemCode = item.ContainsKey("Code") ? item["Code"]?.ToString() : null;

                    if (string.IsNullOrEmpty(exactId) || string.IsNullOrEmpty(itemCode))
                    {
                        _logger.LogWarning("⚠️ ID veya Code boş, atlanıyor");
                        skipCount++;
                        continue;
                    }

                    // Shopify'da SKU ile ürünü bul
                    var searchResult = await shopifyService.GetProductBySkuWithDuplicateHandlingAsync(itemCode);

                    if (!searchResult.Found || searchResult.Match == null)
                    {
                        _logger.LogInformation("⚠️ SKU '{ItemCode}' Shopify'da bulunamadı, atlanıyor", itemCode);
                        skipCount++;
                        await Task.Delay(500, stoppingToken);
                        continue;
                    }

                    var productId = searchResult.Match.ProductId;

                    var updated = await UpdateProductMetafieldAsync(shopifyService, productId, exactId, itemCode);

                    if (updated)
                    {
                        successCount++;
                        _logger.LogInformation("✅ Metafield güncellendi: SKU={ItemCode}, ExactID={ExactId}", itemCode, exactId);
                        await SaveToCacheAsync(exactId, productId);
                    }
                    else
                    {
                        errorCount++;
                        _logger.LogWarning("❌ Metafield güncellenemedi: SKU={ItemCode}", itemCode);
                    }

                    await Task.Delay(1000, stoppingToken);
                }
                catch (Exception ex)
                {
                    errorCount++;
                    _logger.LogError(ex, "❌ Ürün işlenirken hata: {Error}", ex.Message);
                    await Task.Delay(1000, stoppingToken);
                }
            }

            // Batch istatistiklerini güncelle
            _totalProcessed += items.Count;
            _totalSuccess += successCount;
            _totalSkipped += skipCount;
            _totalError += errorCount;

            // Sonraki batch için skip'i ilerlet ve dosyaya kaydet
            _currentSkip += items.Count;
            SaveSkipState(_currentSkip);

            _logger.LogInformation(
                "📊 Batch bitti (skip {Skip} → {NextSkip}) | Bu batch: ✅{Success} ⚠️{Skipped} ❌{Error} | " +
                "Toplam tur genelinde: işlenen={Total}, güncellenen={TotalSuccess}",
                _currentSkip - items.Count, _currentSkip,
                successCount, skipCount, errorCount,
                _totalProcessed, _totalSuccess);

            return false; // Tur henüz bitmedi, devam et
        }

        /// <summary>
        /// Exact'tan belirtilen skip ve top değeriyle webshop ürünleri çeker.
        /// </summary>
        private async Task<List<Dictionary<string, object>>?> FetchExactItemsBatchAsync(ExactService exactService, int skip, int top)
        {
            try
            {
                return await exactService.GetWebshopItemsPageAsync(skip, top);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Exact batch çekme hatası");
                return null;
            }
        }

        /// <summary>
        /// Shopify GraphQL ile ürünün custom.exact_product_id metafield'ını günceller.
        /// </summary>
        private async Task<bool> UpdateProductMetafieldAsync(ShopifyService shopifyService, string shopifyProductId, string exactId, string itemCode)
        {
            try
            {
                return await shopifyService.UpdateProductExactIdMetafieldAsync(shopifyProductId, exactId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Metafield güncelleme hatası: SKU={ItemCode}", itemCode);
                return false;
            }
        }

        // ──── ExactId → ShopifyProductId cache ────

        public static async Task SaveToCacheAsync(string exactId, string shopifyProductId)
        {
            await _cacheLock.WaitAsync();
            try
            {
                Dictionary<string, string> cache;
                if (File.Exists(ExactIdCacheFile))
                {
                    var json = await File.ReadAllTextAsync(ExactIdCacheFile);
                    cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                }
                else
                {
                    cache = new Dictionary<string, string>();
                }

                cache[exactId.ToLowerInvariant()] = shopifyProductId;
                await File.WriteAllTextAsync(ExactIdCacheFile, JsonSerializer.Serialize(cache));
            }
            catch
            {
                // Cache yazma hatası kritik değil, sessizce geç
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        public static async Task<string?> GetFromCacheAsync(string exactId)
        {
            try
            {
                if (!File.Exists(ExactIdCacheFile)) return null;
                var json = await File.ReadAllTextAsync(ExactIdCacheFile);
                var cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                string? shopifyId = null;
                cache?.TryGetValue(exactId.ToLowerInvariant(), out shopifyId);
                return shopifyId;
            }
            catch
            {
                return null;
            }
        }

        // ──── Tur log dosyasına yaz ────

        private void AppendTurLog(int turNumber, int totalProcessed, int totalSuccess, int totalSkipped, int totalError)
        {
            try
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TUR {turNumber} TAMAMLANDI | İşlenen: {totalProcessed} | Güncellenen: {totalSuccess} | Atlandı: {totalSkipped} | Hatalı: {totalError}";
                File.AppendAllText(TurLogFile, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ Tur logu yazılamadı: {Msg}", ex.Message);
            }
        }

        // ──── Skip durumu dosyaya kaydet / yükle ────

        private void SaveSkipState(int skip)
        {
            try
            {
                File.WriteAllText(SkipStateFile, skip.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ Skip state kaydedilemedi: {Msg}", ex.Message);
            }
        }

    }
}
