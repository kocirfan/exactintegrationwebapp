using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExactOnline.Converters
{
    public class NumberToStringConverter : JsonConverter<object>
    {
        public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return reader.GetString();
                case JsonTokenType.Number:
                    if (reader.TryGetInt32(out int intValue))
                        return intValue.ToString();
                    if (reader.TryGetInt64(out long longValue))
                        return longValue.ToString();
                    if (reader.TryGetDouble(out double doubleValue))
                        return doubleValue.ToString();
                    break;
                case JsonTokenType.Null:
                    return null;
            }
            
            throw new JsonException($"Cannot convert token type '{reader.TokenType}' to string");
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            if (value == null)
                writer.WriteNullValue();
            else
                writer.WriteStringValue(value.ToString());
        }
    }
}