using System.ComponentModel.DataAnnotations;

namespace ShopifyProductApp.Models
{
    // Müşteri senkronizasyonunda (Exact -> Shopify) her müşterinin kaydı
    // (müşteri başına tek satır - her senkronda üzerine yazılır)
    public class CustomerSyncLog
    {
        public int Id { get; set; }

        [MaxLength(64)]
        public string ExactCustomerId { get; set; }

        [MaxLength(64)]
        public string CustomerCode { get; set; }

        [Required]
        [MaxLength(256)]
        public string Email { get; set; }

        [MaxLength(512)]
        public string CustomerName { get; set; }

        public DateTime UpdatedAt { get; set; }

        // Aynı müşterinin bir önceki başarılı senkron tarihi
        public DateTime? PreviousUpdatedAt { get; set; }

        public bool Success { get; set; }

        [MaxLength(2000)]
        public string ErrorMessage { get; set; }
    }
}
