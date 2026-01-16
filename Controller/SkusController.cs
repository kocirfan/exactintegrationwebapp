using Microsoft.AspNetCore.Mvc;
using ShopifyProductApp.Services;
using System.Text.Json;
using System.IO;

namespace ShopifyProductApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SkusController : ControllerBase
    {
        private readonly AppConfiguration _config;

        public SkusController(AppConfiguration config)
        {
            _config = config;
        }

        // [HttpGet("last-updated")]
        // public async Task<IActionResult> GetLastUpdatedSkus()
        // {
        //     try
        //     {
        //         if (!System.IO.File.Exists(_config.LastUpdatedSkusPath))
        //         {
        //             _config.LogMessage("📋 Son güncellenen SKU dosyası henüz oluşturulmadı");
        //             return Ok(new
        //             {
        //                 Message = "Henüz güncellenen SKU bulunamadı",
        //                 FilePath = _config.LastUpdatedSkusPath,
        //                 Count = 0,
        //                 LastUpdatedSkus = new List<object>(),
        //                 Timestamp = DateTime.UtcNow
        //             });
        //         }

        //         var content = await System.IO.File.ReadAllTextAsync(_config.LastUpdatedSkusPath);
        //         if (string.IsNullOrWhiteSpace(content))
        //         {
        //             return Ok(new
        //             {
        //                 Message = "SKU dosyası boş",
        //                 Count = 0,
        //                 LastUpdatedSkus = new List<object>(),
        //                 Timestamp = DateTime.UtcNow
        //             });
        //         }

        //         var skus = JsonSerializer.Deserialize<List<object>>(content);

        //         _config.LogMessage($"📋 Son güncellenen SKU'lar görüntülendi: {skus?.Count ?? 0} kayıt");

        //         return Ok(new
        //         {
        //             Success = true,
        //             Count = skus?.Count ?? 0,
        //             FilePath = _config.LastUpdatedSkusPath,
        //             LastFileUpdate = System.IO.File.GetLastWriteTime(_config.LastUpdatedSkusPath),
        //             LastUpdatedSkus = skus ?? new List<object>(),
        //             Timestamp = DateTime.UtcNow
        //         });
        //     }
        //     catch (Exception ex)
        //     {
        //         _config.LogMessage($"❌ Son güncellenen SKU'lar alınırken hata: {ex.Message}");
        //         return Ok(new
        //         {
        //             Success = false,
        //             Error = ex.Message,
        //             Timestamp = DateTime.UtcNow
        //         });
        //     }
        // }

        // [HttpGet("last-updated/{count:int}")]
        // public async Task<IActionResult> GetLastUpdatedSkusLimited(int count)
        // {
        //     try
        //     {
        //         if (!System.IO.File.Exists(_config.LastUpdatedSkusPath))
        //         {
        //             return Ok(new
        //             {
        //                 Message = "Henüz güncellenen SKU bulunamadı",
        //                 Count = 0,
        //                 RequestedCount = count,
        //                 LastUpdatedSkus = new List<object>()
        //             });
        //         }

        //         var content = await System.IO.File.ReadAllTextAsync(_config.LastUpdatedSkusPath);
        //         if (string.IsNullOrWhiteSpace(content))
        //         {
        //             return Ok(new
        //             {
        //                 Message = "SKU dosyası boş",
        //                 Count = 0,
        //                 RequestedCount = count,
        //                 LastUpdatedSkus = new List<object>()
        //             });
        //         }

        //         var allSkus = JsonSerializer.Deserialize<List<object>>(content);
        //         var limitedSkus = allSkus?.Take(Math.Min(count, 100)).ToList() ?? new List<object>();

        //         _config.LogMessage($"📋 Son {limitedSkus.Count} güncellenen SKU görüntülendi");

        //         return Ok(new
        //         {
        //             Success = true,
        //             RequestedCount = count,
        //             ActualCount = limitedSkus.Count,
        //             TotalCount = allSkus?.Count ?? 0,
        //             LastUpdatedSkus = limitedSkus,
        //             Timestamp = DateTime.UtcNow
        //         });
        //     }
        //     catch (Exception ex)
        //     {
        //         _config.LogMessage($"❌ Son güncellenen SKU'lar (limit: {count}) alınırken hata: {ex.Message}");
        //         return Ok(new
        //         {
        //             Success = false,
        //             Error = ex.Message,
        //             RequestedCount = count,
        //             Timestamp = DateTime.UtcNow
        //         });
        //     }
        // }

        // [HttpGet("updated")]
        // public async Task<IActionResult> GetUpdatedSkus()
        // {
        //     try
        //     {
        //         if (!System.IO.File.Exists(_config.ShopifyFilePath))
        //         {
        //             return Ok(new
        //             {
        //                 Success = true,
        //                 Count = 0,
        //                 FilePath = _config.ShopifyFilePath,
        //                 LastUpdatedSkus = new List<object>(),
        //                 Timestamp = DateTime.UtcNow
        //             });
        //         }

        //         // Dosya içeriğini komple oku
        //         var fileContent = await System.IO.File.ReadAllTextAsync(_config.ShopifyFilePath);

        //         if (string.IsNullOrWhiteSpace(fileContent))
        //         {
        //             return Ok(new
        //             {
        //                 Success = true,
        //                 Count = 0,
        //                 FilePath = _config.ShopifyFilePath,
        //                 LastUpdatedSkus = new List<object>(),
        //                 Timestamp = DateTime.UtcNow
        //             });
        //         }

        //         List<object> skus = new List<object>();

        //         try
        //         {
        //             // JSON array olarak parse et
        //             var jsonArray = JsonSerializer.Deserialize<JsonElement[]>(fileContent);

        //             if (jsonArray != null)
        //             {
        //                 foreach (var item in jsonArray)
        //                 {
        //                     skus.Add(item);
        //                 }
        //             }
        //         }
        //         catch (JsonException)
        //         {
        //             // Eğer JSON array parse edilemezse, eski format (satır satır) deneyelim
        //             var lines = fileContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        //             foreach (var line in lines)
        //             {
        //                 if (string.IsNullOrWhiteSpace(line)) continue;

        //                 try
        //                 {
        //                     var obj = JsonSerializer.Deserialize<object>(line.Trim());
        //                     if (obj != null)
        //                         skus.Add(obj);
        //                 }
        //                 catch { /* hatalı satır olursa atla */ }
        //             }
        //         }

        //         return Ok(new
        //         {
        //             Success = true,
        //             Count = skus.Count,
        //             FilePath = _config.ShopifyFilePath,
        //             LastFileUpdate = System.IO.File.GetLastWriteTime(_config.ShopifyFilePath),
        //             LastUpdatedSkus = skus,
        //             FileSize = new FileInfo(_config.ShopifyFilePath).Length,
        //             Timestamp = DateTime.UtcNow
        //         });
        //     }
        //     catch (Exception ex)
        //     {
        //         return Ok(new
        //         {
        //             Success = false,
        //             Error = ex.Message,
        //             StackTrace = ex.StackTrace, // Debug için
        //             Timestamp = DateTime.UtcNow
        //         });
        //     }
        // }
        // // Alternatif: Daha detaylı response için bu versiyonu kullanabilirsiniz
        // [HttpGet("updated-detailed")]
        // public async Task<IActionResult> GetUpdatedSkusDetailed()
        // {
        //     try
        //     {
        //         if (!System.IO.File.Exists(_config.ShopifyFilePath))
        //         {
        //             return Ok(new
        //             {
        //                 Success = true,
        //                 Count = 0,
        //                 FilePath = _config.ShopifyFilePath,
        //                 Products = new List<ProductArchiveItem>(),
        //                 Summary = new { Updated = 0, Errors = 0, Total = 0 },
        //                 Timestamp = DateTime.UtcNow
        //             });
        //         }

        //         var fileContent = await System.IO.File.ReadAllTextAsync(_config.ShopifyFilePath);

        //         if (string.IsNullOrWhiteSpace(fileContent))
        //         {
        //             return Ok(new
        //             {
        //                 Success = true,
        //                 Count = 0,
        //                 FilePath = _config.ShopifyFilePath,
        //                 Products = new List<ProductArchiveItem>(),
        //                 Summary = new { Updated = 0, Errors = 0, Total = 0 },
        //                 Timestamp = DateTime.UtcNow
        //             });
        //         }

        //         // ProductArchiveItem listesi olarak parse et
        //         var products = JsonSerializer.Deserialize<List<ProductArchiveItem>>(fileContent, new JsonSerializerOptions
        //         {
        //             PropertyNameCaseInsensitive = true
        //         });

        //         if (products == null)
        //         {
        //             products = new List<ProductArchiveItem>();
        //         }

        //         // İstatistik hesapla
        //         var updatedCount = products.Count(p => p.Status == "Updated");
        //         var errorCount = products.Count(p => p.Status == "Error");

        //         // Son 50 kaydı al (performans için)
        //         var recentProducts = products.Take(50).ToList();

        //         return Ok(new
        //         {
        //             Success = true,
        //             Count = products.Count,
        //             FilePath = _config.ShopifyFilePath,
        //             LastFileUpdate = System.IO.File.GetLastWriteTime(_config.ShopifyFilePath),
        //             Products = recentProducts,
        //             Summary = new
        //             {
        //                 Updated = updatedCount,
        //                 Errors = errorCount,
        //                 Total = products.Count,
        //                 SuccessRate = products.Count > 0 ? Math.Round((double)updatedCount / products.Count * 100, 2) : 0
        //             },
        //             Batches = products.GroupBy(p => p.BatchId)
        //                             .Select(g => new
        //                             {
        //                                 BatchId = g.Key,
        //                                 Count = g.Count(),
        //                                 Date = g.First().UpdatedAt
        //                             })
        //                             .OrderByDescending(b => b.Date)
        //                             .Take(10)
        //                             .ToList(),
        //             Timestamp = DateTime.UtcNow
        //         });
        //     }
        //     catch (Exception ex)
        //     {
        //         return Ok(new
        //         {
        //             Success = false,
        //             Error = ex.Message,
        //             Timestamp = DateTime.UtcNow
        //         });
        //     }
        // }




        // [HttpGet("today-updated")]
        // public async Task<IActionResult> GetTodayUpdatedSkus()
        // {
        //     try
        //     {
        //         if (!System.IO.File.Exists(_config.LastUpdatedSkusPath))
        //         {
        //             return Ok(new
        //             {
        //                 Message = "Henüz güncellenen SKU bulunamadı",
        //                 Count = 0,
        //                 Date = DateTime.Today.ToString("yyyy-MM-dd"),
        //                 TodayUpdatedSkus = new List<Dictionary<string, object>>()
        //             });
        //         }

        //         var content = await System.IO.File.ReadAllTextAsync(_config.LastUpdatedSkusPath);
        //         if (string.IsNullOrWhiteSpace(content))
        //         {
        //             return Ok(new
        //             {
        //                 Message = "SKU dosyası boş",
        //                 Count = 0,
        //                 Date = DateTime.Today.ToString("yyyy-MM-dd"),
        //                 TodayUpdatedSkus = new List<Dictionary<string, object>>()
        //             });
        //         }

        //         var allSkus = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(content);
        //         var today = DateTime.Today;

        //         var todaySkus = allSkus?.Where(sku =>
        //         {
        //             if (sku.TryGetValue("updatedAt", out var updatedAtObj) && updatedAtObj != null)
        //             {
        //                 if (DateTime.TryParse(updatedAtObj.ToString(), out var updatedAt))
        //                 {
        //                     return updatedAt.Date == today;
        //                 }
        //             }
        //             return false;
        //         }).ToList() ?? new List<Dictionary<string, object>>();

        //         _config.LogMessage($"📅 Bugün güncellenen SKU'lar görüntülendi: {todaySkus.Count} kayıt");

        //         return Ok(new
        //         {
        //             Success = true,
        //             Date = today.ToString("yyyy-MM-dd"),
        //             Count = todaySkus.Count,
        //             TotalCount = allSkus?.Count ?? 0,
        //             TodayUpdatedSkus = todaySkus,
        //             Timestamp = DateTime.UtcNow
        //         });
        //     }
        //     catch (Exception ex)
        //     {
        //         _config.LogMessage($"❌ Bugünkü güncellemeler alınırken hata: {ex.Message}");
        //         return Ok(new
        //         {
        //             Success = false,
        //             Error = ex.Message,
        //             Date = DateTime.Today.ToString("yyyy-MM-dd"),
        //             Timestamp = DateTime.UtcNow
        //         });
        //     }
        // }

        // [HttpDelete("last-updated")]
        // public IActionResult ClearLastUpdatedSkus()
        // {
        //     try
        //     {
        //         if (System.IO.File.Exists(_config.LastUpdatedSkusPath))
        //         {
        //             System.IO.File.Delete(_config.LastUpdatedSkusPath);
        //             _config.LogMessage("🗑️ SKU güncelleme geçmişi temizlendi");
        //             return Ok(new
        //             {
        //                 Success = true,
        //                 Message = "SKU güncelleme geçmişi başarıyla temizlendi",
        //                 Timestamp = DateTime.UtcNow
        //             });
        //         }
        //         else
        //         {
        //             return Ok(new
        //             {
        //                 Success = true,
        //                 Message = "Temizlenecek dosya bulunamadı",
        //                 Timestamp = DateTime.UtcNow
        //             });
        //         }
        //     }
        //     catch (Exception ex)
        //     {
        //         _config.LogMessage($"❌ SKU geçmişi temizlenirken hata: {ex.Message}");
        //         return Ok(new
        //         {
        //             Success = false,
        //             Error = ex.Message,
        //             Timestamp = DateTime.UtcNow
        //         });
        //     }
        // }
    }

    // ProductArchiveItem sınıfını da controller'a ekleyin
    public class ProductArchiveItem
    {
        public string Sku { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string Status { get; set; }
        public string ErrorMessage { get; set; }
        public string BatchId { get; set; }
        public string Notes { get; set; }
    }
}