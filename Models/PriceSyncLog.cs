using System.ComponentModel.DataAnnotations;

namespace ShopifyProductApp.Models
{
    // Fiyat senkronizasyonunda her ürün/variant güncellemesinin kaydı
    // (ürün/variant başına tek satır - her senkronda üzerine yazılır)
    public class PriceSyncLog
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

        // Shopify'daki güncelleme öncesi fiyat (eşleşme bulunamadıysa null)
        public decimal? OldPrice { get; set; }

        // Exact'tan gelen yeni fiyat
        public decimal NewPrice { get; set; }

        public DateTime UpdatedAt { get; set; }

        // Aynı ürünün bir önceki başarılı senkron tarihi
        public DateTime? PreviousUpdatedAt { get; set; }

        public bool Success { get; set; }

        [MaxLength(2000)]
        public string ErrorMessage { get; set; }
    }
}
