using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ExactOnline.Converters
{
    public class ExactDateTimeConverter : JsonConverter<DateTime?>
    {
        private static readonly Regex DateRegex = new Regex(@"/Date\((-?\d+)\)/", RegexOptions.Compiled);

        public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                var stringValue = reader.GetString();

                // Boş string kontrolü
                if (string.IsNullOrWhiteSpace(stringValue))
                {
                    return null;
                }

                // /Date(1234567890000)/ formatı kontrolü
                var match = DateRegex.Match(stringValue);
                if (match.Success)
                {
                    var milliseconds = long.Parse(match.Groups[1].Value);
                    return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).DateTime;
                }

                // Normal ISO 8601 format kontrolü
                if (DateTime.TryParse(stringValue, out var dateTime))
                {
                    return dateTime;
                }

                // Parse edilemezse null dön
                return null;
            }

            // Beklenmeyen format - null dön
            return null;
        }

        public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
            {
                writer.WriteStringValue(value.Value.ToString("o")); // ISO 8601 formatında yaz
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }
}
