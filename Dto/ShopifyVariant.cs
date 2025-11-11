using System;
using System.Collections.Generic;
using Newtonsoft.Json;
public class ShopifyVariant
{
    [JsonProperty("id")]
    public long Id { get; set; }

    [JsonProperty("product_id")]
    public long ProductId { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; }

    [JsonProperty("price")]
    public string Price { get; set; }

    [JsonProperty("position")]
    public int Position { get; set; }

    [JsonProperty("inventory_policy")]
    public string InventoryPolicy { get; set; }

    [JsonProperty("compare_at_price")]
    public string CompareAtPrice { get; set; }

    [JsonProperty("option1")]
    public string Option1 { get; set; }

    [JsonProperty("option2")]
    public string Option2 { get; set; }

    [JsonProperty("option3")]
    public string Option3 { get; set; }

    [JsonProperty("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonProperty("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [JsonProperty("taxable")]
    public bool Taxable { get; set; }

    [JsonProperty("barcode")]
    public string Barcode { get; set; }

    [JsonProperty("fulfillment_service")]
    public string FulfillmentService { get; set; }

    [JsonProperty("grams")]
    public int Grams { get; set; }

    [JsonProperty("inventory_management")]
    public string InventoryManagement { get; set; }

    [JsonProperty("requires_shipping")]
    public bool RequiresShipping { get; set; }

    [JsonProperty("sku")]
    public string Sku { get; set; }

    [JsonProperty("weight")]
    public decimal Weight { get; set; }

    [JsonProperty("weight_unit")]
    public string WeightUnit { get; set; }

    [JsonProperty("inventory_item_id")]
    public long InventoryItemId { get; set; }

    [JsonProperty("inventory_quantity")]
    public int InventoryQuantity { get; set; }

    [JsonProperty("old_inventory_quantity")]
    public int OldInventoryQuantity { get; set; }

    [JsonProperty("admin_graphql_api_id")]
    public string AdminGraphqlApiId { get; set; }

    [JsonProperty("image_id")]
    public long? ImageId { get; set; }
}
