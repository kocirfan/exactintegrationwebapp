using Microsoft.AspNetCore.Mvc;
using ShopifyProductApp.Services;
using System.Text.Json;

namespace ShopifyProductApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class StockTestController : ControllerBase
    {
        private readonly ShopifyService _shopifyService;

        public StockTestController(ShopifyService shopifyService)
        {
            _shopifyService = shopifyService;
        }
        public class StockUpdateRequest
        {
            public string Sku { get; set; }
            public int NewStock { get; set; }
        }

        [HttpPost("update-stock")]
        public async Task<IActionResult> UpdateStockTest([FromBody] StockUpdateRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.Sku))
                {
                    return BadRequest(new { error = "⚠️ SKU boş olamaz." });
                }

                // Stok güncelleme
                await _shopifyService.UpdateProductStockBySkuAndSaveRawAsync(request.Sku, request.NewStock, "stock-update.json");

                // Güncellenmiş ürünü getir
                var updatedProduct = await GetUpdatedProductBySku(request.Sku);

                return Ok(new
                {
                    message = "✅ Stok güncelleme tamamlandı!",
                    sku = request.Sku,
                    newStock = request.NewStock,
                    updatedProduct = updatedProduct,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new
                {
                    error = $"⚠️ Stok güncelleme hatası: {ex.Message}",
                    timestamp = DateTime.Now
                });
            }
        }

        [HttpGet("product/{sku}")]
        public async Task<IActionResult> GetProductBySku(string sku)
        {
            try
            {
                var product = await GetUpdatedProductBySku(sku);
                if (product == null)
                {
                    return NotFound(new { message = $"SKU '{sku}' bulunamadı." });
                }

                return Ok(product);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        // [HttpPost("update-stocks")]
        // public async Task<object> GetUpdatedProductBySkus([FromBody] StockUpdateRequest request)
        // {
        //     var result = await _shopifyService.GetProductBySkuWithDuplicateHandlingAsync(request.Sku);

        //     // Response formatını düzenle
        //     if (result is JsonElement element && element.TryGetProperty("Found", out var found))
        //     {
        //         if (found.GetBoolean())
        //         {
        //             // Çoklu eşleşme varsa uyar
        //             if (element.TryGetProperty("DuplicateCount", out var count) && count.GetInt32() > 1)
        //             {
        //                 Console.WriteLine($"ℹ️ SKU '{request.Sku}' için {count.GetInt32()} eşleşme bulundu, en uygun olanı seçildi");
        //             }

        //             // Match objesini döndür
        //             if (element.TryGetProperty("Match", out var match))
        //             {
        //                 return match;
        //             }
        //         }
        //     }

        //     return result;
        // }




        private async Task<object> GetUpdatedProductBySku(string sku)
        {
            var rawProductsDoc = await _shopifyService.GetAllProductsRawAsync();

            if (rawProductsDoc.RootElement.TryGetProperty("products", out var products))
            {
                foreach (var product in products.EnumerateArray())
                {
                    if (product.TryGetProperty("variants", out var variants))
                    {
                        foreach (var variant in variants.EnumerateArray())
                        {
                            if (variant.TryGetProperty("sku", out var skuElement) &&
                                skuElement.GetString() == sku)
                            {
                                var productInfo = new
                                {
                                    ProductId = product.TryGetProperty("id", out var prodId) ? prodId.ToString() : null,
                                    ProductTitle = product.TryGetProperty("title", out var title) ? title.GetString() : null,
                                    ProductStatus = product.TryGetProperty("status", out var status) ? status.GetString() : null,
                                    VariantId = variant.TryGetProperty("id", out var varId) ? varId.ToString() : null,
                                    SKU = sku,
                                    Price = variant.TryGetProperty("price", out var price) ? price.GetString() : null,
                                    InventoryQuantity = variant.TryGetProperty("inventory_quantity", out var stock) ? stock.GetInt32() : 0,
                                    CreatedAt = product.TryGetProperty("created_at", out var created) ? created.GetString() : null,
                                    UpdatedAt = product.TryGetProperty("updated_at", out var updated) ? updated.GetString() : null
                                };

                                rawProductsDoc.Dispose();
                                return productInfo;
                            }
                        }
                    }
                }
            }

            rawProductsDoc.Dispose();
            return null;
        }
    }
}