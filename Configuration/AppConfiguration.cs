using System.Text.Json;

namespace ShopifyProductApp.Services
{
    public class AppConfiguration
    {
        public string DataDirectory { get; }
        public string ShopifyFilePath { get; }
        public string LastUpdatedSkusPath { get; }
        public string UpdatedSkusPath { get; }
        public string SkuConsoleLogPath { get; }
        public string LogFilePath { get; }

        public AppConfiguration()
        {
            DataDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Data");
            ShopifyFilePath = Path.Combine(DataDirectory, "arcivedproduct.json");
            LastUpdatedSkusPath = Path.Combine(DataDirectory, "last_updated_skus.json");
            UpdatedSkusPath = Path.Combine(DataDirectory, "background_process_log.json");
            SkuConsoleLogPath = Path.Combine(DataDirectory, "sku_console_log.json");
            LogFilePath = Path.Combine(DataDirectory, "sync_log.txt");

            EnsureDataDirectoryExists();
        }

        private void EnsureDataDirectoryExists()
        {
            if (!Directory.Exists(DataDirectory))
            {
                Directory.CreateDirectory(DataDirectory);
                Console.WriteLine($"Data klasörü oluşturuldu: {DataDirectory}");
            }
        }

        public void LogMessage(string message)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var logEntry = $"[{timestamp}] {message}";

            Console.WriteLine(logEntry);

            try
            {
                File.AppendAllText(LogFilePath, logEntry + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Log yazma hatası: {ex.Message}");
            }
        }

        public void LogSkuAndSaveToJson(string sku, string status, string message = "")
        {
            var timestamp = DateTime.UtcNow;
            var timeString = timestamp.ToString("yyyy-MM-dd HH:mm:ss");

            var consoleMessage = $"[{timeString}] SKU: {sku} - Status: {status}";
            if (!string.IsNullOrEmpty(message))
                consoleMessage += $" - {message}";

            Console.WriteLine(consoleMessage);
            LogMessage(consoleMessage);

            try
            {
                var skuLog = new Dictionary<string, object>
                {
                    {"sku", sku},
                    {"status", status},
                    {"message", message},
                    {"timestamp", timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")},
                    {"unixTimestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds()},
                    {"readableTime", timeString}
                };

                List<Dictionary<string, object>> existingLogs = new();

                if (File.Exists(SkuConsoleLogPath))
                {
                    var existingContent = File.ReadAllText(SkuConsoleLogPath);
                    if (!string.IsNullOrWhiteSpace(existingContent))
                    {
                        try
                        {
                            var existing = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(existingContent);
                            if (existing != null)
                                existingLogs = existing;
                        }
                        catch (Exception ex)
                        {
                            LogMessage($"⚠️ SKU Console JSON okuma hatası: {ex.Message}");
                        }
                    }
                }

                existingLogs.Insert(0, skuLog);

                if (existingLogs.Count > 500)
                {
                    existingLogs = existingLogs.Take(500).ToList();
                }

                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var json = JsonSerializer.Serialize(existingLogs, jsonOptions);
                File.WriteAllText(SkuConsoleLogPath, json);
            }
            catch (Exception ex)
            {
                LogMessage($"❌ SKU Console JSON kaydetme hatası: {ex.Message}");
            }
        }

        public void SaveLastUpdatedSku(string sku, string status, string? error = null)
        {
            try
            {
                var skuUpdate = new
                {
                    Sku = sku,
                    Status = status,
                    Error = error,
                    UpdatedAt = DateTime.UtcNow,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                List<object> existingSkus = new List<object>();

                if (File.Exists(LastUpdatedSkusPath))
                {
                    var existingContent = File.ReadAllText(LastUpdatedSkusPath);
                    if (!string.IsNullOrWhiteSpace(existingContent))
                    {
                        try
                        {
                            var existing = JsonSerializer.Deserialize<List<object>>(existingContent);
                            if (existing != null)
                                existingSkus = existing;
                        }
                        catch (Exception ex)
                        {
                            LogMessage($"⚠️ Mevcut SKU dosyası okunurken hata: {ex.Message}");
                        }
                    }
                }

                existingSkus.Insert(0, skuUpdate);

                if (existingSkus.Count > 100)
                {
                    existingSkus = existingSkus.Take(100).ToList();
                }

                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var json = JsonSerializer.Serialize(existingSkus, jsonOptions);
                File.WriteAllText(LastUpdatedSkusPath, json);

                LogMessage($"📝 Son güncellenen SKU kaydedildi: {sku} - {status}");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ SKU kaydetme hatası: {ex.Message}");
            }
        }
    }
}