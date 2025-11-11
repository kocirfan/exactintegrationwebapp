using System;
using System.Collections.Generic;
using Newtonsoft.Json;

public class ShopifyProductResponse
{
    [JsonProperty("products")]
    public List<ShopifyProduct> Products { get; set; }
}