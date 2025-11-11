using System;
using System.Collections.Generic;
using Newtonsoft.Json;
public class ShopifyOption
{
    [JsonProperty("id")]
    public long Id { get; set; }

    [JsonProperty("product_id")]
    public long ProductId { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("position")]
    public int Position { get; set; }

    [JsonProperty("values")]
    public List<string> Values { get; set; }
}
