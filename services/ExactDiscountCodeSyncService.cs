using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ShopifyProductApp.Services
{
    /// <summary>
    /// Exact'tan Status eq 'C' olan müşterileri çekip ClassificationDescription değerini
    /// Shopify'da custom.exact_discount_code metafield'ına yazan background service.
    /// Program başladığında hemen çalışır.
    /// </summary>
    public class ExactDiscountCodeSyncService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ExactDiscountCodeSyncService> _logger;

        private int _currentSkip = 0;
        private const int BatchSize = 100;

        private int _totalProcessed = 0;
        private int _totalSuccess = 0;
        private int _totalSkipped = 0;
        private int _totalError = 0;
        private int _turNumber = 0;

        private static readonly string TurLogFile = Path.Combine(AppContext.BaseDirectory, "exact_discount_code_sync_tur_log.txt");

        public ExactDiscountCodeSyncService(
            IServiceProvider serviceProvider,
            ILogger<ExactDiscountCodeSyncService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🚀 ExactDiscountCode Sync Service başlatıldı — hemen çalışıyor...");

            _currentSkip = 0;
            _totalProcessed = 0;
            _totalSuccess = 0;
            _totalSkipped = 0;
            _totalError = 0;

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
                        _logger.LogInformation("✅ ExactDiscountCode Sync tamamlandı, servis duruyor.");
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ ExactDiscountCode sync hatası: {Error}", ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
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
            _logger.LogInformation("🔄 skip={Skip} konumundan {BatchSize} müşteri çekiliyor...", _currentSkip, BatchSize);

            List<Dictionary<string, object>> items;
            try
            {
                items = await exactService.GetCustomersPageAsync(_currentSkip, BatchSize);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Exact customer batch çekme hatası");
                return false;
            }

            if (items == null || items.Count == 0)
            {
                _turNumber++;
                _logger.LogInformation(
                    "🏁 ===== TUR {Tur} TAMAMLANDI ===== " +
                    "Toplam işlenen: {Total} | Güncellenen: {Success} | Atlandı: {Skipped} | Hatalı: {Error}",
                    _turNumber, _totalProcessed, _totalSuccess, _totalSkipped, _totalError);

                AppendTurLog(_turNumber, _totalProcessed, _totalSuccess, _totalSkipped, _totalError);
                return true;
            }

            _logger.LogInformation("📦 {Count} müşteri alındı, Shopify discount_code güncelleniyor...", items.Count);

            int successCount = 0;
            int skipCount = 0;
            int errorCount = 0;

            foreach (var item in items)
            {
                if (stoppingToken.IsCancellationRequested) break;

                try
                {
                    var email = item.ContainsKey("Email") ? item["Email"]?.ToString() : null;
                    var classificationDescription = item.ContainsKey("ClassificationDescription") ? item["ClassificationDescription"]?.ToString() : null;

                    if (string.IsNullOrEmpty(email))
                    {
                        _logger.LogWarning("⚠️ Email boş, atlanıyor");
                        skipCount++;
                        continue;
                    }

                    if (string.IsNullOrEmpty(classificationDescription))
                    {
                        _logger.LogInformation("⚠️ Email '{Email}' için ClassificationDescription boş, atlanıyor", email);
                        skipCount++;
                        await Task.Delay(200, stoppingToken);
                        continue;
                    }

                    var shopifyCustomerId = await shopifyService.GetShopifyCustomerIdByEmailAsync(email);

                    if (string.IsNullOrEmpty(shopifyCustomerId))
                    {
                        _logger.LogInformation("⚠️ Email '{Email}' Shopify'da bulunamadı, atlanıyor", email);
                        skipCount++;
                        await Task.Delay(500, stoppingToken);
                        continue;
                    }

                    var updated = await shopifyService.UpdateCustomerDiscountCodeMetafieldAsync(shopifyCustomerId, classificationDescription);

                    if (updated)
                    {
                        successCount++;
                        _logger.LogInformation("✅ exact_discount_code güncellendi: Email={Email}, Değer={Value}", email, classificationDescription);
                    }
                    else
                    {
                        errorCount++;
                        _logger.LogWarning("❌ exact_discount_code güncellenemedi: Email={Email}", email);
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

            _logger.LogInformation(
                "📊 Batch bitti (skip {Skip} → {NextSkip}) | Bu batch: ✅{Success} ⚠️{Skipped} ❌{Error} | " +
                "Toplam: işlenen={Total}, güncellenen={TotalSuccess}",
                _currentSkip - items.Count, _currentSkip,
                successCount, skipCount, errorCount,
                _totalProcessed, _totalSuccess);

            return false;
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
    }
}
