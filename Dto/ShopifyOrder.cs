using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

public class ShopifyOrder
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("admin_graphql_api_id")]
    public string AdminGraphqlApiId { get; set; }

    [JsonPropertyName("app_id")]
    public long? AppId { get; set; }

    [JsonPropertyName("browser_ip")]
    public string BrowserIp { get; set; }

    [JsonPropertyName("buyer_accepts_marketing")]
    public bool BuyerAcceptsMarketing { get; set; }

    [JsonPropertyName("contact_email")]
    public string ContactEmail { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; }

    [JsonPropertyName("current_total_price")]
    public string CurrentTotalPrice { get; set; }

    [JsonPropertyName("financial_status")]
    public string FinancialStatus { get; set; }

    [JsonPropertyName("fulfillment_status")]
    public string FulfillmentStatus { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("order_number")]
    public long OrderNumber { get; set; }

    [JsonPropertyName("order_status_url")]
    public string OrderStatusUrl { get; set; }

    [JsonPropertyName("subtotal_price")]
    public string SubtotalPrice { get; set; }

    [JsonPropertyName("total_price")]
    public string TotalPrice { get; set; }

    [JsonPropertyName("total_tax")]
    public string TotalTax { get; set; }

    [JsonPropertyName("line_items")]
    public List<ShopifyLineItem> LineItems { get; set; }

    [JsonPropertyName("customer")]
    public ShopifyCustomer Customer { get; set; }

    [JsonPropertyName("shipping_address")]
    public ShopifyAddress ShippingAddress { get; set; }

    [JsonPropertyName("billing_address")]
    public ShopifyAddress BillingAddress { get; set; }

    [JsonPropertyName("shipping_lines")]
    public List<ShopifyShippingLine> ShippingLines { get; set; }

    [JsonPropertyName("current_total_discounts")]
    public string current_total_discounts { get; set; }

    [JsonPropertyName("total_line_items_price")]
    public string total_line_items_price { get; set; }

    [JsonPropertyName("current_subtotal_price")]
    public string current_subtotal_price { get; set; } 
    
    [JsonPropertyName("current_total_tax")]
    public string current_total_tax { get; set; }

    [JsonPropertyName("note_attributes")]
    public List<ShopifyNoteAttribute> NoteAttributes { get; set; } = new();

    [JsonPropertyName("discount_applications")]
    public List<ShopifyDiscountApplication> DiscountApplications { get; set; } = new();
}

public class ShopifyLineItem
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

     [JsonPropertyName("total_discount")]
    public string? TotalDiscount { get; set; } 

    [JsonPropertyName("title")]
    
    public string Title { get; set; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    [JsonPropertyName("sku")]
    public string Sku { get; set; }

    [JsonPropertyName("price")]
    public string Price { get; set; }

    [JsonPropertyName("variant_id")]
    public long VariantId { get; set; }

    [JsonPropertyName("product_id")]
    public long ProductId { get; set; }

     [JsonPropertyName("discount_allocations")]
    public List<DiscountAllocation> DiscountAllocations { get; set; } = new();
}

public class DiscountAllocation
{
    [JsonPropertyName("amount")]
    public string Amount { get; set; }
    
    [JsonPropertyName("discount_application_index")]
    public int DiscountApplicationIndex { get; set; }
}

public class ShopifyCustomer
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("first_name")]
    public string FirstName { get; set; }

    [JsonPropertyName("last_name")]
    public string LastName { get; set; }

    [JsonPropertyName("email")]
    public string Email { get; set; }

    [JsonPropertyName("default_address")]
    public ShopifyAddress DefaultAddress { get; set; }

    public List<ShopifyMetafield> Metafields { get; set; }
}
public class ShopifyMetafield
{
    public string Id { get; set; }
    public string Namespace { get; set; }
    public string Key { get; set; }
    public string Value { get; set; }
    public string Type { get; set; }
}
public class ShopifyAddress
{
    [JsonPropertyName("first_name")]
    public string FirstName { get; set; }

    [JsonPropertyName("last_name")]
    public string LastName { get; set; }

    [JsonPropertyName("address1")]
    public string Address1 { get; set; }

    [JsonPropertyName("address2")]
    public string Address2 { get; set; }

    [JsonPropertyName("city")]
    public string City { get; set; }

    [JsonPropertyName("zip")]
    public string Zip { get; set; }

    [JsonPropertyName("country")]
    public string Country { get; set; }

    [JsonPropertyName("country_code")]
    public string CountryCode { get; set; }
}

public class ShopifyShippingLine
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; }

    [JsonPropertyName("price")]
    public string Price { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; }
}

public class ShopifyNoteAttribute
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("value")]
    public string Value { get; set; }
}

public class ShopifyDiscountApplication
{
    [JsonPropertyName("target_type")]
    public string TargetType { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("value")]
    public string Value { get; set; }

    [JsonPropertyName("value_type")]
    public string ValueType { get; set; }

    [JsonPropertyName("allocation_method")]
    public string AllocationMethod { get; set; }

    [JsonPropertyName("target_selection")]
    public string TargetSelection { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; }
}
