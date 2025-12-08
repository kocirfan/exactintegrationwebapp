using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ExactOnline.Converters
{
    /// <summary>
    /// Exact Online'ın özel tarih formatını (/Date(1632300153387)/) çözen converter
    /// </summary>
    public class ExactOnlineDateTimeConverter : JsonConverter<DateTime?>
    {
        private static readonly Regex DateRegex = new Regex(@"\/Date\((-?\d+)\)\/", RegexOptions.Compiled);

        public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                var dateString = reader.GetString();
                
                if (string.IsNullOrWhiteSpace(dateString))
                {
                    return null;
                }

                // /Date(1632300153387)/ formatını parse et
                var match = DateRegex.Match(dateString);
                if (match.Success)
                {
                    var milliseconds = long.Parse(match.Groups[1].Value);
                    var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    return epoch.AddMilliseconds(milliseconds);
                }

                // Normal ISO tarih formatı varsa onu da dene
                if (DateTime.TryParse(dateString, out var normalDate))
                {
                    return normalDate;
                }
            }

            return null;
        }

        public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
            {
                // Exact Online formatında yaz
                var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var milliseconds = (long)(value.Value.ToUniversalTime() - epoch).TotalMilliseconds;
                writer.WriteStringValue($"/Date({milliseconds})/");
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }

    /// <summary>
    /// Nullable olmayan DateTime için converter
    /// </summary>
    public class ExactOnlineDateTimeRequiredConverter : JsonConverter<DateTime>
{
    private static readonly Regex DateRegex = new Regex(@"\/Date\((-?\d+)\)\/", RegexOptions.Compiled);

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            throw new JsonException("DateTime alanı null olamaz");
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var dateString = reader.GetString();
            
            if (string.IsNullOrWhiteSpace(dateString))
            {
                throw new JsonException("DateTime alanı boş olamaz");
            }

            // /Date(1632300153387)/ formatını parse et
            var match = DateRegex.Match(dateString);
            if (match.Success)
            {
                var milliseconds = long.Parse(match.Groups[1].Value);
                var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                return epoch.AddMilliseconds(milliseconds);
            }

            // Normal ISO tarih formatı varsa onu da dene
            if (DateTime.TryParse(dateString, out var normalDate))
            {
                return normalDate;
            }
        }

        throw new JsonException($"DateTime parse edilemedi: {reader.GetString()}");
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var milliseconds = (long)(value.ToUniversalTime() - epoch).TotalMilliseconds;
        writer.WriteStringValue($"/Date({milliseconds})/");
    }
}
}