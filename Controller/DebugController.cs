using Microsoft.AspNetCore.Mvc;
using ShopifyProductApp.Services;

namespace ShopifyProductApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DebugController : ControllerBase
    {
        private readonly AppConfiguration _config;

        public DebugController(AppConfiguration config)
        {
            _config = config;
        }

        // [HttpGet("paths")]
        // public IActionResult GetDebugPaths()
        // {
        //     return Ok(new
        //     {
        //         CurrentDirectory = Directory.GetCurrentDirectory(),
        //         DataDirectory = _config.DataDirectory,
        //         ShopifyFilePath = _config.ShopifyFilePath,
        //         LogFilePath = _config.LogFilePath,
        //         LastUpdatedSkusPath = _config.LastUpdatedSkusPath,
        //         SkuConsoleLogPath = _config.SkuConsoleLogPath,
        //         UpdatedSkusPath = _config.UpdatedSkusPath,
        //         DataDirectoryExists = Directory.Exists(_config.DataDirectory),
        //         FileExists = System.IO.File.Exists(_config.ShopifyFilePath),
        //         LogFileExists = System.IO.File.Exists(_config.LogFilePath),
        //         LastUpdatedSkusFileExists = System.IO.File.Exists(_config.LastUpdatedSkusPath),
        //         SkuConsoleLogFileExists = System.IO.File.Exists(_config.SkuConsoleLogPath),
        //         UpdatedSkusFileExists = System.IO.File.Exists(_config.UpdatedSkusPath)
        //     });
        // }

        // [HttpGet("background-status")]
        // public IActionResult GetBackgroundStatus()
        // {
        //     return Ok(new
        //     {
        //         Status = "Background service çalışıyor",
        //         Interval = "Her 5 dakikada bir",
        //         NextRun = "Yaklaşık olarak her 5 dakikada",
        //         LogFile = _config.LogFilePath,
        //         SkuConsoleLogFile = _config.SkuConsoleLogPath,
        //         Timestamp = DateTime.UtcNow
        //     });
        // }

        // [HttpGet("health")]
        // public IActionResult HealthCheck()
        // {
        //     return Ok(new
        //     {
        //         Status = "Healthy",
        //         Timestamp = DateTime.UtcNow,
        //         Application = "ShopifyProductApp",
        //         Version = "1.0.0"
        //     });
        // }
    }
}