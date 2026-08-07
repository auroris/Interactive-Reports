using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// JSON numbers are parsed as IEEE-754 doubles by JavaScript. Int64/UInt64 values and
/// decimal values can therefore change before the report renderer sees them. The wire
/// protocol carries those CLR types as invariant strings; column metadata still says
/// "number", so clients can format them numerically without sacrificing their digits.
/// </summary>
internal sealed class Int64StringJsonConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => long.Parse(reader.GetString()!, NumberStyles.Integer, CultureInfo.InvariantCulture),
            JsonTokenType.Number => reader.GetInt64(),
            _ => throw new JsonException("Expected a 64-bit integer as a JSON string or number."),
        };

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}

internal sealed class UInt64StringJsonConverter : JsonConverter<ulong>
{
    public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => ulong.Parse(reader.GetString()!, NumberStyles.Integer, CultureInfo.InvariantCulture),
            JsonTokenType.Number => reader.GetUInt64(),
            _ => throw new JsonException("Expected an unsigned 64-bit integer as a JSON string or number."),
        };

    public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}

internal sealed class DecimalStringJsonConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => decimal.Parse(reader.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture),
            JsonTokenType.Number => reader.GetDecimal(),
            _ => throw new JsonException("Expected a decimal as a JSON string or number."),
        };

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}
