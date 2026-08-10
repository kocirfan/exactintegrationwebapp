using Microsoft.Extensions.DependencyInjection;

namespace ShopifyProductApp.Services
{
    public class ManualCustomerSyncStatus
    {
        public bool IsRunning { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public int Hours { get; set; }
        public int TotalCustomers { get; set; }
        public int ProcessedCustomers { get; set; }
        public int SuccessCount { get; set; }
        public int ErrorCount { get; set; }
        public int DbSavedCount { get; set; }
        public int DbFailedCount { get; set; }
        public string CurrentStep { get; set; }
        public string LastError { get; set; }

        public ManualCustomerSyncStatus Clone() => (ManualCustomerSyncStatus)MemberwiseClone();
    }

    /// <summary>
    /// Müşteri senkronunun manuel tetiklenen versiyonu:
    /// Exact'ta son N saatte değişen müşterileri çekip Shopify'da günceller,
    /// her müşteri için sonucu CustomerSyncLogs tablosuna yazar.
    /// Aynı anda tek çalıştırma: ikinci tetikleme reddedilir.
    /// </summary>
    public class ManualCustomerSyncRunner
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly CustomerSyncLogService _customerSyncLogService;
        private readonly ILogger<ManualCustomerSyncRunner> _logger;
        private readonly object _lock = new();
        private ManualCustomerSyncStatus _status = new() { CurrentStep = "Henüz çalıştırılmadı" };

        private const int DB_SAVE_BATCH_SIZE = 20;

        public ManualCustomerSyncRunner(
            IServiceProvider serviceProvider,
            CustomerSyncLogService customerSyncLogService,
            ILogger<ManualCustomerSyncRunner> logger)
        {
            _serviceProvider = serviceProvider;
            _customerSyncLogService = customerSyncLogService;
            _logger = logger;
        }

        public ManualCustomerSyncStatus GetStatus()
        {
            lock (_lock)
            {
                return _status.Clone();
            }
        }

        /// <summary>
        /// Müşteri senkronunu arka planda başlatır. Zaten çalışıyorsa false döner.
        /// hours: Exact'ta son kaç saatte değişen müşteriler alınacak.
        /// maxItems verilirse sadece ilk N müşteri işlenir (test için).
        /// </summary>
        public bool TryStart(int hours = 24, int? maxItems = null)
        {
            lock (_lock)
            {
                if (_status.IsRunning)
                    return false;

                _status = new ManualCustomerSyncStatus
                {
                    IsRunning = true,
                    StartedAt = DateTime.Now,
                    Hours = hours,
                    CurrentStep = "Başlatılıyor"
                };
            }

            _ = Task.Run(() => RunAsync(hours, maxItems));
            return true;
        }

        private async Task RunAsync(int hours, int? maxItems)
        {
            try
            {
                _logger.LogInformation("👥 Manuel müşteri senkronu başladı (son {Hours} saat, maxItems: {MaxItems})",
                    hours, maxItems?.ToString() ?? "tümü");

                using var scope = _serviceProvider.CreateScope();
                var exactCustomerService = scope.ServiceProvider.GetRequiredService<ExactCustomerCrud>();
                var shopifyCustomerService = scope.ServiceProvider.GetRequiredService<ShopifyCustomerCrud>();

                SetStep($"Exact'ta son {hours} saatte değişen müşteriler alınıyor");
                var customers = await exactCustomerService.GetAllUpdateCustomersAsync(hours);

                if (customers == null || customers.Count == 0)
                {
                    SetStep("Tamamlandı - değişen müşteri yok");
                    _logger.LogInformation("ℹ️ Son {Hours} saatte değişen müşteri bulunamadı", hours);
                    return;
                }

                if (maxItems.HasValue && maxItems.Value > 0 && customers.Count > maxItems.Value)
                {
                    customers = customers.Take(maxItems.Value).ToList();
                }

                lock (_lock)
                {
                    _status.TotalCustomers = customers.Count;
                }

                var logFilePath = Path.Combine("logs", $"customer-sync-{DateTime.Now:yyyyMMdd}.log");
                var pendingLogs = new List<Models.CustomerSyncLog>();

                foreach (var customer in customers)
                {
                    SetStep($"Müşteri işleniyor: {_status.ProcessedCustomers + 1}/{_status.TotalCustomers}");

                    var logEntry = new Models.CustomerSyncLog
                    {
                        ExactCustomerId = customer.ID.ToString(),
                        CustomerCode = customer.Code,
                        Email = customer.Email,
                        CustomerName = customer.Name,
                        UpdatedAt = DateTime.Now
                    };

                    try
                    {
                        var (success, error) = await shopifyCustomerService.UpdateCustomerDetailedAsync(customer, logFilePath, sendWelcomeEmail: false);
                        logEntry.Success = success;
                        logEntry.ErrorMessage = success ? null : error;
                    }
                    catch (Exception ex)
                    {
                        logEntry.Success = false;
                        logEntry.ErrorMessage = ex.Message;
                    }

                    pendingLogs.Add(logEntry);

                    lock (_lock)
                    {
                        _status.ProcessedCustomers++;
                        if (logEntry.Success) _status.SuccessCount++;
                        else _status.ErrorCount++;
                    }

                    // Belirli aralıklarla DB'ye yaz
                    if (pendingLogs.Count >= DB_SAVE_BATCH_SIZE)
                    {
                        var saveResult = await _customerSyncLogService.SaveAsync(pendingLogs);
                        lock (_lock)
                        {
                            _status.DbSavedCount += saveResult.SavedCount;
                            _status.DbFailedCount += saveResult.FailedCount;
                        }
                        pendingLogs = new List<Models.CustomerSyncLog>();
                    }

                    await Task.Delay(300); // Shopify rate limit
                }

                // Kalan logları yaz
                if (pendingLogs.Count > 0)
                {
                    var finalSave = await _customerSyncLogService.SaveAsync(pendingLogs);
                    lock (_lock)
                    {
                        _status.DbSavedCount += finalSave.SavedCount;
                        _status.DbFailedCount += finalSave.FailedCount;
                    }
                }

                SetStep("Tamamlandı");
                _logger.LogInformation("🎉 Manuel müşteri senkronu tamamlandı - İşlenen: {Processed}, Başarılı: {Success}, Hatalı: {Error}",
                    _status.ProcessedCustomers, _status.SuccessCount, _status.ErrorCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Manuel müşteri senkronu hatası: {Error}", ex.Message);
                lock (_lock)
                {
                    _status.LastError = ex.Message;
                    _status.CurrentStep = "Hata ile durdu";
                }
            }
            finally
            {
                lock (_lock)
                {
                    _status.IsRunning = false;
                    _status.FinishedAt = DateTime.Now;
                }
            }
        }

        private void SetStep(string step)
        {
            lock (_lock)
            {
                _status.CurrentStep = step;
            }
        }
    }
}
