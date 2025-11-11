using System;
using System.Collections.Generic;
using Newtonsoft.Json;
public class ShopifyProduct
{
    [JsonProperty("id")]
    public long Id { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; }

    [JsonProperty("body_html")]
    public string BodyHtml { get; set; }

    [JsonProperty("vendor")]
    public string Vendor { get; set; }

    [JsonProperty("product_type")]
    public string ProductType { get; set; }

    [JsonProperty("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonProperty("handle")]
    public string Handle { get; set; }

    [JsonProperty("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [JsonProperty("published_at")]
    public DateTime? PublishedAt { get; set; }

    [JsonProperty("template_suffix")]
    public string TemplateSuffix { get; set; }

    [JsonProperty("published_scope")]
    public string PublishedScope { get; set; }

    [JsonProperty("tags")]
    public string Tags { get; set; }

    [JsonProperty("status")]
    public string Status { get; set; }

    [JsonProperty("admin_graphql_api_id")]
    public string AdminGraphqlApiId { get; set; }

    [JsonProperty("variants")]
    public List<ShopifyVariant> Variants { get; set; }

    [JsonProperty("options")]
    public List<ShopifyOption> Options { get; set; }

    [JsonProperty("images")]
    public List<ShopifyImage> Images { get; set; }

    [JsonProperty("image")]
    public ShopifyImage Image { get; set; }
}
