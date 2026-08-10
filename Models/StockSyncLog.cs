using System.ComponentModel.DataAnnotations;

namespace ShopifyProductApp.Models
{
    // Günlük stok senkronizasyonunda her ürün/variant güncellemesinin kaydı
    public class StockSyncLog
    {
        public int Id { get; set; }

        [MaxLength(64)]
        public string ExactItemId { get; set; }

        [MaxLength(32)]
        public string ShopifyProductId { get; set; }

        [MaxLength(32)]
        public string ShopifyVariantId { get; set; }

        [Required]
        [MaxLength(128)]
        public string ProductCode { get; set; }

        [MaxLength(512)]
        public string ProductName { get; set; }

        public decimal? Price { get; set; }

        // Shopify'daki güncelleme öncesi stok (eşleşme bulunamadıysa null)
        public int? OldStock { get; set; }

        // Exact'tan gelen yeni stok
        public int NewStock { get; set; }

        public DateTime UpdatedAt { get; set; }

        // Aynı ürün kodunun bir önceki başarılı senkron tarihi
        public DateTime? PreviousUpdatedAt { get; set; }

        public bool Success { get; set; }

        [MaxLength(2000)]
        public string ErrorMessage { get; set; }
    }
}
