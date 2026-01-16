using Microsoft.AspNetCore.Mvc;
using ShopifyProductApp.Services;
using System.Text.Json;

namespace ShopifyProductApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LogsController : ControllerBase
    {
        private readonly AppConfiguration _config;

        public LogsController(AppConfiguration config)
        {
            _config = config;
        }

    //     [HttpGet]
    //     public async Task<IActionResult> GetLogs()
    //     {
    //         try
    //         {
    //             if (!System.IO.File.Exists(_config.LogFilePath))
    //             {
    //                 return Ok(new { Message = "Log dosyası henüz oluşturulmadı" });
    //             }

    //             var logs = await System.IO.File.ReadAllTextAsync(_config.LogFilePath);
    //             var logLines = logs.Split('\n')
    //                 .Where(x => !string.IsNullOrWhiteSpace(x))
    //                 .Reverse()
    //                 .Take(100)
    //                 .Reverse();

    //             return Ok(new
    //             {
    //                 LogFile = _config.LogFilePath,
    //                 LastUpdated = System.IO.File.GetLastWriteTime(_config.LogFilePath),
    //                 RecentLogs = logLines
    //             });
    //         }
    //         catch (Exception ex)
    //         {
    //             return Ok(new { Error = ex.Message });
    //         }
    //     }

    //     [HttpGet("latest")]
    //     public async Task<IActionResult> GetLatestLogs()
    //     {
    //         try
    //         {
    //             if (!System.IO.File.Exists(_config.LogFilePath))
    //             {
    //                 return Ok(new { Message = "Log dosyası henüz oluşturulmadı" });
    //             }

    //             var logs = await System.IO.File.ReadAllTextAsync(_config.LogFilePath);
    //             var latestLogs = logs.Split('\n')
    //                 .Where(x => !string.IsNullOrWhiteSpace(x))
    //                 .TakeLast(20)
    //                 .ToList();

    //             return Ok(new
    //             {
    //                 Count = latestLogs.Count,
    //                 Logs = latestLogs,
    //                 LastUpdated = System.IO.File.GetLastWriteTime(_config.LogFilePath),
    //                 CurrentTime = DateTime.Now
    //             });
    //         }
    //         catch (Exception ex)
    //         {
    //             return Ok(new { Error = ex.Message });
    //         }
    //     }

    //     [HttpGet("sku-console")]
    //     public async Task<IActionResult> GetSkuConsoleLogs()
    //     {
    //         try
    //         {
    //             if (!System.IO.File.Exists(_config.SkuConsoleLogPath))
    //             {
    //                 return Ok(new
    //                 {
    //                     Message = "SKU console log dosyası bulunamadı",
    //                     FilePath = _config.SkuConsoleLogPath,
    //                     Count = 0,
    //                     Logs = new List<Dictionary<string, object>>()
    //                 });
    //             }

    //             var content = await System.IO.File.ReadAllTextAsync(_config.SkuConsoleLogPath);
    //             if (string.IsNullOrWhiteSpace(content))
    //             {
    //                 return Ok(new
    //                 {
    //                     Message = "SKU Console log dosyası boş",
    //                     FilePath = _config.SkuConsoleLogPath,
    //                     Count = 0,
    //                     Logs = new List<Dictionary<string, object>>()
    //                 });
    //             }

    //             var logs = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(content);

    //             _config.LogMessage($"📋 SKU Console logları görüntülendi: {logs?.Count ?? 0} kayıt");

    //             return Ok(new
    //             {
    //                 Success = true,
    //                 FilePath = _config.SkuConsoleLogPath,
    //                 Count = logs?.Count ?? 0,
    //                 LastUpdate = System.IO.File.GetLastWriteTime(_config.SkuConsoleLogPath),
    //                 Logs = logs ?? new List<Dictionary<string, object>>()
    //             });
    //         }
    //         catch (Exception ex)
    //         {
    //             return Ok(new
    //             {
    //                 Success = false,
    //                 Error = ex.Message
    //             });
    //         }
    //     }

    //     [HttpGet("sku-console/{count:int}")]
    //     public async Task<IActionResult> GetSkuConsoleLogsLimited(int count)
    //     {
    //         try
    //         {
    //             if (!System.IO.File.Exists(_config.SkuConsoleLogPath))
    //             {
    //                 return Ok(new 
    //                 { 
    //                     Message = "Log dosyası bulunamadı", 
    //                     Logs = new List<Dictionary<string, object>>() 
    //                 });
    //             }

    //             var content = await System.IO.File.ReadAllTextAsync(_config.SkuConsoleLogPath);
    //             if (string.IsNullOrWhiteSpace(content))
    //             {
    //                 return Ok(new 
    //                 { 
    //                     Message = "Log dosyası boş", 
    //                     Logs = new List<Dictionary<string, object>>() 
    //                 });
    //             }

    //             var allLogs = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(content);
    //             var limitedLogs = allLogs?.Take(Math.Min(count, 100)).ToList() ?? new List<Dictionary<string, object>>();

    //             _config.LogMessage($"📋 Son {limitedLogs.Count} SKU console log görüntülendi");

    //             return Ok(new
    //             {
    //                 Success = true,
    //                 RequestedCount = count,
    //                 ActualCount = limitedLogs.Count,
    //                 TotalCount = allLogs?.Count ?? 0,
    //                 Logs = limitedLogs
    //             });
    //         }
    //         catch (Exception ex)
    //         {
    //             return Ok(new { Success = false, Error = ex.Message });
    //         }
    //     }

    //     [HttpGet("test-sku-console")]
    //     public IActionResult TestSkuConsole()
    //     {
    //         try
    //         {
    //             _config.LogSkuAndSaveToJson("TEST-SKU-001", "Manual-Test", "Bu bir test kaydıdır");
    //             _config.LogSkuAndSaveToJson("TEST-SKU-002", "Success", "Test başarılı");
    //             _config.LogSkuAndSaveToJson("TEST-SKU-003", "Error", "Test hatası simülasyonu");

    //             return Ok(new
    //             {
    //                 Success = true,
    //                 Message = "Test SKU console logları oluşturuldu",
    //                 FilePath = _config.SkuConsoleLogPath,
    //                 FileExists = System.IO.File.Exists(_config.SkuConsoleLogPath)
    //             });
    //         }
    //         catch (Exception ex)
    //         {
    //             return Ok(new { Success = false, Error = ex.Message });
    //         }
    //     }
     }
}