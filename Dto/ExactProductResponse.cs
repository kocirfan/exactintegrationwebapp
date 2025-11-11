using System.Collections.Generic;
// using Newtonsoft.Json;
using System.Text.Json.Serialization;

public class ExactProductResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("processedCount")]
    public int ProcessedCount { get; set; }

    [JsonPropertyName("results")]
    public List<ExactProduct> Results { get; set; }
}
