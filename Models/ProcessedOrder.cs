// Models/ProcessedOrder.cs
namespace ShopifyProductApp.Models
{
    public class ProcessedOrder
    {
        public long ShopifyOrderId { get; set; }        // Primary Key - Shopify'dan gelen ID
        public long? ShopifyOrderNumber { get; set; }   // Order numarası (#1001, #1002, vb.)
        public DateTime ProcessedAt { get; set; }       // Ne zaman işlendiği
        public string? ExactOrderId { get; set; }       // Exact'taki sipariş ID (opsiyonel)
    }
}