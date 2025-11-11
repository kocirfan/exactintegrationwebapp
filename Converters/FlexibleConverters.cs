using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExactOnline.Converters
{
    /// <summary>
    /// Flexible boolean converter - 0/1, "0"/"1", true/false, "true"/"false" kabul eder
    /// </summary>
    public class FlexibleBooleanConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.True:
                    return true;
                case JsonTokenType.False:
                    return false;
                case JsonTokenType.String:
                    var stringValue = reader.GetString();
                    if (bool.TryParse(stringValue, out bool boolResult))
                        return boolResult;
                    if (stringValue == "1" || stringValue?.ToLower() == "yes")
                        return true;
                    if (stringValue == "0" || stringValue?.ToLower() == "no")
                        return false;
                    return false;
                case JsonTokenType.Number:
                    return reader.GetInt32() != 0;
                default:
                    return false;
            }
        }

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        {
            writer.WriteBooleanValue(value);
        }
    }

    /// <summary>
    /// Nullable boolean için flexible converter
    /// </summary>
    public class FlexibleNullableBooleanConverter : JsonConverter<bool?>
    {
        public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null;

            switch (reader.TokenType)
            {
                case JsonTokenType.True:
                    return true;
                case JsonTokenType.False:
                    return false;
                case JsonTokenType.String:
                    var stringValue = reader.GetString();
                    if (string.IsNullOrWhiteSpace(stringValue))
                        return null;
                    if (bool.TryParse(stringValue, out bool boolResult))
                        return boolResult;
                    if (stringValue == "1" || stringValue?.ToLower() == "yes")
                        return true;
                    if (stringValue == "0" || stringValue?.ToLower() == "no")
                        return false;
                    return null;
                case JsonTokenType.Number:
                    return reader.GetInt32() != 0;
                default:
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
                writer.WriteBooleanValue(value.Value);
            else
                writer.WriteNullValue();
        }
    }

    /// <summary>
    /// Flexible Guid converter - boş string'leri null'a çevirir
    /// </summary>
    public class FlexibleGuidConverter : JsonConverter<Guid?>
    {
        public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null;

            if (reader.TokenType == JsonTokenType.String)
            {
                var stringValue = reader.GetString();
                
                if (string.IsNullOrWhiteSpace(stringValue))
                    return null;

                if (Guid.TryParse(stringValue, out Guid guid))
                    return guid;

                // Boş GUID gibi değerler
                if (stringValue == "00000000-0000-0000-0000-000000000000")
                    return null;

                return null;
            }

            return null;
        }

        public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
                writer.WriteStringValue(value.Value.ToString());
            else
                writer.WriteNullValue();
        }
    }

    /// <summary>
    /// Flexible int converter - string'leri de parse eder
    /// </summary>
    public class FlexibleIntConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    return reader.GetInt32();
                case JsonTokenType.String:
                    var stringValue = reader.GetString();
                    if (int.TryParse(stringValue, out int result))
                        return result;
                    return 0;
                default:
                    return 0;
            }
        }

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value);
        }
    }

    /// <summary>
    /// Flexible nullable int converter
    /// </summary>
    public class FlexibleNullableIntConverter : JsonConverter<int?>
    {
        public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null;

            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    return reader.GetInt32();
                case JsonTokenType.String:
                    var stringValue = reader.GetString();
                    if (string.IsNullOrWhiteSpace(stringValue))
                        return null;
                    if (int.TryParse(stringValue, out int result))
                        return result;
                    return null;
                default:
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
                writer.WriteNumberValue(value.Value);
            else
                writer.WriteNullValue();
        }
    }
}