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
    public class UpdateExactCustomerJob : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<UpdateExactCustomerJob> _logger;
        private readonly string _archiveFilePath = "Data/arcivedproduct.json";
        private readonly string _updateLogFile = "Data/update_log.json";
        private readonly int _batchSize = 20;

        public UpdateExactCustomerJob(
            IServiceProvider serviceProvider,
            ILogger<UpdateExactCustomerJob> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("🔄 Product sync işlemi başlıyor...");

                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var shopifyService = scope.ServiceProvider.GetRequiredService<ShopifyCustomerCrud>();
                        var exactCustomerService = scope.ServiceProvider.GetRequiredService<ExactCustomerCrud>();
                        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

                        await PerformSyncOperations(exactCustomerService, shopifyService, settingsService);
                    }

                    _logger.LogInformation("Customer sync işlemi tamamlandı");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Customer sync service hatası: {Error}", ex.Message);
                }

                await Task.Delay(TimeSpan.FromMinutes(60), stoppingToken);
            }
        }

        private async Task PerformSyncOperations(ExactCustomerCrud exactCustomerService, ShopifyCustomerCrud shopifyService, ISettingsService settingsService)
        {
            try
            {
                var exactResponse = await exactCustomerService.GetAllUpdateCustomersAsync();
                var logFilePath = Path.Combine("logs", $"customer-sync-{DateTime.Now:yyyyMMdd}.log");
                foreach (var customer in exactResponse)
                {
                    try
                    {
                        var shopifyResult = await shopifyService.UpdateCustomerAsync(
                            customer,
                            logFilePath,
                            sendWelcomeEmail: false
                        );

                        if (shopifyResult)
                        {

                            Console.WriteLine($"✅ Müşteri aktarıldı: {customer.Email}");
                        }
                        else
                        {

                            Console.WriteLine($"⚠️ Müşteri oluşturulamadı: {customer.Email}");
                        }
                    }
                    catch (Exception ex)
                    {

                        Console.WriteLine($" Hata: {customer.Email} - {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Customer sync operasyonları sırasında hata");
            }
        }
    }
}