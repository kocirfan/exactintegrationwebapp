using System;
using System.Text.Json;
using System.Text.Json.Serialization;

public class FlexibleGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string stringValue = reader.GetString();
            if (string.IsNullOrEmpty(stringValue) || stringValue.Equals("null", StringComparison.OrdinalIgnoreCase))
                return Guid.Empty;
            
            if (Guid.TryParse(stringValue, out var guid))
                return guid;
            
            throw new JsonException($"'{stringValue}' valid bir GUID değil");
        }
        else if (reader.TokenType == JsonTokenType.Null)
        {
            return Guid.Empty;
        }

        throw new JsonException($"Unexpected token: {reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}