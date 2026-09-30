using System.ComponentModel.DataAnnotations;

namespace ShopifyProductApp.Models
{
    /// <summary>
    /// Bekleyen işlem havuzu: Exact token yokken (veya gönderim hatasında) tamamlanamayan
    /// işlemler burada bekler. Şimdilik tek işlem tipi var: OrderTransfer
    /// (Shopify siparişini Exact'a aktarma).
    ///
    /// Pending durumundaki kayıtlar token geri geldiğinde PendingOperationProcessor
    /// tarafından otomatik işlenir. Failed kayıtlar panelden (nlmonitoringapp)
    /// "tekrar dene" ile Pending'e çevrilir.
    /// </summary>
    public class PendingOperation
    {
        public int Id { get; set; }

        [Required]
        [MaxLength(50)]
        public string OperationType { get; set; } = PendingOperationTypes.OrderTransfer;

        /// <summary>Shopify sipariş ID — bir sipariş havuzda yalnızca bir kez bulunur (unique index).</summary>
        public long ShopifyOrderId { get; set; }

        public long? ShopifyOrderNumber { get; set; }

        /// <summary>Shopify'daki görünen ad, örn. "#1234".</summary>
        [MaxLength(50)]
        public string ShopifyOrderName { get; set; }

        /// <summary>Pending | Processing | Completed | Failed | Cancelled</summary>
        [Required]
        [MaxLength(20)]
        public string Status { get; set; } = PendingOperationStatus.Pending;

        /// <summary>Havuza alınma nedeni (örn. "Exact token yok veya geçersiz").</summary>
        [MaxLength(500)]
        public string Reason { get; set; }

        /// <summary>Webhook | Manual</summary>
        [MaxLength(20)]
        public string Source { get; set; }

        /// <summary>
        /// Ham webhook gövdesi (JSON). İnceleme içindir; işleme sırasında sipariş
        /// Shopify'dan taze çekilir (bu arada iptal edilmiş olabilir).
        /// </summary>
        public string Payload { get; set; }

        public int AttemptCount { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime? LastAttemptAt { get; set; }

        public DateTime? CompletedAt { get; set; }

        [MaxLength(2000)]
        public string LastError { get; set; }

        public Guid? ExactOrderGuid { get; set; }

        [MaxLength(50)]
        public string ExactOrderNumber { get; set; }
    }

    public static class PendingOperationTypes
    {
        public const string OrderTransfer = "OrderTransfer";
    }

    public static class PendingOperationStatus
    {
        public const string Pending = "Pending";
        public const string Processing = "Processing";
        public const string Completed = "Completed";
        public const string Failed = "Failed";
        public const string Cancelled = "Cancelled";

        public static readonly string[] All = { Pending, Processing, Completed, Failed, Cancelled };
    }

    public static class PendingOperationSources
    {
        public const string Webhook = "Webhook";
        public const string Manual = "Manual";
    }
}
