using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ShopifyProductApp.Services
{
    /// <summary>
    /// Exact'tan Status eq 'C' olan müşterileri çekip Shopify'da email eşleşmesiyle
    /// custom.exact_customer_id metafield'ını güncelleyen background service.
    /// Her gün 05:00'da çalışır.
    /// </summary>
    public class ExactCustomerIdMetafieldSyncService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ExactCustomerIdMetafieldSyncService> _logger;

        private int _currentSkip = 0;
        private const int BatchSize = 100;

        private int _totalProcessed = 0;
        private int _totalSuccess = 0;
        private int _totalSkipped = 0;
        private int _totalError = 0;
        private int _turNumber = 0;

        private static readonly string SkipStateFile = Path.Combine(AppContext.BaseDirectory, "exact_customer_sync_skip.txt");
        private static readonly string TurLogFile = Path.Combine(AppContext.BaseDirectory, "exact_customer_sync_tur_log.txt");

        public ExactCustomerIdMetafieldSyncService(
            IServiceProvider serviceProvider,
            ILogger<ExactCustomerIdMetafieldSyncService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🕐 ExactCustomerId Metafield Sync Service başlatıldı - her gün 05:00'da çalışacak");

            bool firstRun = true;

            while (!stoppingToken.IsCancellationRequested)
            {
                if (firstRun)
                {
                    firstRun = false;
                    _logger.LogInformation("🚀 İlk çalışma — hemen başlıyor (test modu)...");
                }
                else
                {
                    var delay = GetDelayUntilNextRun(hour: 5, minute: 0);
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

                    _logger.LogInformation("🚀 05:00 geldi, ExactCustomerId Metafield Sync turu başlıyor...");
                }

                _currentSkip = 0;
                _totalProcessed = 0;
                _totalSuccess = 0;
                _totalSkipped = 0;
                _totalError = 0;
                SaveSkipState(0);

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var exactService = scope.ServiceProvider.GetRequiredService<ExactService>();
                        var shopifyService = scope.ServiceProvider.GetRequiredService<ShopifyService>();

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
                            _logger.LogInformation("✅ Tur tamamlandı, servis bir sonraki 05:00'ı bekliyor.");
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ ExactCustomerId sync hatası: {Error}", ex.Message);
                        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }

        private static TimeSpan GetDelayUntilNextRun(int hour, int minute)
        {
            var now = DateTime.Now;
            var next = DateTime.Today.AddHours(hour).AddMinutes(minute);
            if (next <= now)
                next = next.AddDays(1);
            return next - now;
        }

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
            int[] transientErrors = { -2, 20, 35, 233, 10053, 10054, 10060 };
            return Array.IndexOf(transientErrors, ex.Number) >= 0;
        }

        private async Task<bool> ProcessBatchAndCheckFinished(ExactService exactService, ShopifyService shopifyService, CancellationToken stoppingToken)
        {
            _logger.LogInformation("🔄 [Tur {Tur}] skip={Skip} konumundan {BatchSize} müşteri çekiliyor...", _turNumber + 1, _currentSkip, BatchSize);

            var items = await FetchExactCustomersBatchAsync(exactService, _currentSkip, BatchSize);

            if (items == null || items.Count == 0)
            {
                _turNumber++;
                _logger.LogInformation(
                    "🏁 ===== TUR {Tur} TAMAMLANDI ===== " +
                    "Toplam işlenen: {Total} | Güncellenen: {Success} | Shopify'da yok: {Skipped} | Hatalı: {Error}",
                    _turNumber, _totalProcessed, _totalSuccess, _totalSkipped, _totalError);

                AppendTurLog(_turNumber, _totalProcessed, _totalSuccess, _totalSkipped, _totalError);
                SaveSkipState(0);
                return true;
            }

            _logger.LogInformation("📦 {Count} müşteri alındı, Shopify güncelleniyor...", items.Count);

            int successCount = 0;
            int skipCount = 0;
            int errorCount = 0;

            foreach (var item in items)
            {
                if (stoppingToken.IsCancellationRequested) break;

                try
                {
                    var exactId = item.ContainsKey("ID") ? item["ID"]?.ToString() : null;
                    var email = item.ContainsKey("Email") ? item["Email"]?.ToString() : null;

                    if (string.IsNullOrEmpty(exactId) || string.IsNullOrEmpty(email))
                    {
                        _logger.LogWarning("⚠️ ID veya Email boş, atlanıyor");
                        skipCount++;
                        continue;
                    }

                    // Shopify'da email ile müşteri ara
                    var shopifyCustomerId = await shopifyService.GetShopifyCustomerIdByEmailAsync(email);

                    if (string.IsNullOrEmpty(shopifyCustomerId))
                    {
                        _logger.LogInformation("⚠️ Email '{Email}' Shopify'da bulunamadı, atlanıyor", email);
                        skipCount++;
                        await Task.Delay(500, stoppingToken);
                        continue;
                    }

                    var updated = await shopifyService.UpdateCustomerExactIdMetafieldAsync(shopifyCustomerId, exactId);

                    if (updated)
                    {
                        successCount++;
                        _logger.LogInformation("✅ Metafield güncellendi: Email={Email}, ExactID={ExactId}", email, exactId);
                    }
                    else
                    {
                        errorCount++;
                        _logger.LogWarning("❌ Metafield güncellenemedi: Email={Email}", email);
                    }

                    await Task.Delay(1000, stoppingToken);
                }
                catch (Exception ex)
                {
                    errorCount++;
                    _logger.LogError(ex, "❌ Müşteri işlenirken hata: {Error}", ex.Message);
                    await Task.Delay(1000, stoppingToken);
                }
            }

            _totalProcessed += items.Count;
            _totalSuccess += successCount;
            _totalSkipped += skipCount;
            _totalError += errorCount;

            _currentSkip += items.Count;
            SaveSkipState(_currentSkip);

            _logger.LogInformation(
                "📊 Batch bitti (skip {Skip} → {NextSkip}) | Bu batch: ✅{Success} ⚠️{Skipped} ❌{Error} | " +
                "Toplam: işlenen={Total}, güncellenen={TotalSuccess}",
                _currentSkip - items.Count, _currentSkip,
                successCount, skipCount, errorCount,
                _totalProcessed, _totalSuccess);

            return false;
        }

        private async Task<List<Dictionary<string, object>>> FetchExactCustomersBatchAsync(ExactService exactService, int skip, int top)
        {
            try
            {
                return await exactService.GetCustomersPageAsync(skip, top);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Exact customer batch çekme hatası");
                return null;
            }
        }

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
