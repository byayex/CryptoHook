using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CryptoHook.Api.Models.Converters;

/// <summary>
/// JSON converter for BigInteger to serialize as string instead of object.
/// </summary>
public class BigIntegerStringConverter : JsonConverter<BigInteger>
{
    public override BigInteger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var stringValue = reader.GetString();
            if (BigInteger.TryParse(stringValue, out var result))
            {
                return result;
            }
        }
        else if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt64(out var longValue))
            {
                return new BigInteger(longValue);
            }
        }

        throw new JsonException($"Cannot convert {reader.TokenType} to BigInteger");
    }

    public override void Write(Utf8JsonWriter writer, BigInteger value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
