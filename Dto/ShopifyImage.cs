using System;
using System.Collections.Generic;
using Newtonsoft.Json;
public class ShopifyImage
{
    [JsonProperty("id")]
    public long Id { get; set; }

    [JsonProperty("alt")]
    public string Alt { get; set; }

    [JsonProperty("position")]
    public int Position { get; set; }

    [JsonProperty("product_id")]
    public long ProductId { get; set; }

    [JsonProperty("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonProperty("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [JsonProperty("admin_graphql_api_id")]
    public string AdminGraphqlApiId { get; set; }

    [JsonProperty("width")]
    public int Width { get; set; }

    [JsonProperty("height")]
    public int Height { get; set; }

    [JsonProperty("src")]
    public string Src { get; set; }

    [JsonProperty("variant_ids")]
    public List<long> VariantIds { get; set; }
}
