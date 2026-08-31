using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// JSON numbers are parsed as IEEE-754 doubles by JavaScript. Int64, UInt64, and
/// decimal values can therefore change before the report renderer sees them. The wire
/// protocol carries those CLR types as invariant strings; column metadata still says
/// "number", so clients can format them numerically without sacrificing their digits.
/// </summary>
internal sealed class Int64StringJsonConverter : JsonConverter<long>
{
    /// <summary>
    /// Reads a signed 64-bit integer from a JSON string or number token.
    /// </summary>
    /// <param name="reader">The JSON reader positioned at the integer token.</param>
    /// <param name="typeToConvert">The target CLR type requested by the serializer.</param>
    /// <param name="options">The serializer options for the current operation.</param>
    /// <returns>The 64-bit integer result.</returns>
    /// <exception cref="JsonException">Thrown when the current token is neither a string nor a number.</exception>
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => long.Parse(reader.GetString()!, NumberStyles.Integer, CultureInfo.InvariantCulture),
            JsonTokenType.Number => reader.GetInt64(),
            _ => throw new JsonException("Expected a 64-bit integer as a JSON string or number."),
        };

    /// <summary>
    /// Writes a signed 64-bit integer as invariant text to preserve exact digits in JavaScript clients.
    /// </summary>
    /// <param name="writer">The JSON writer that receives the string token.</param>
    /// <param name="value">The integer to serialize.</param>
    /// <param name="options">The serializer options for the current operation.</param>
    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}

/// <summary>Reads unsigned 64-bit integers from strings or numbers and writes them as invariant strings to preserve every digit in JavaScript clients.</summary>
internal sealed class UInt64StringJsonConverter : JsonConverter<ulong>
{
    /// <summary>
    /// Reads an unsigned 64-bit integer from a JSON string or number token.
    /// </summary>
    /// <param name="reader">The JSON reader positioned at the integer token.</param>
    /// <param name="typeToConvert">The target CLR type requested by the serializer.</param>
    /// <param name="options">The serializer options for the current operation.</param>
    /// <returns>The parsed unsigned 64-bit integer.</returns>
    /// <exception cref="JsonException">Thrown when the current token is neither a string nor a number.</exception>
    public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => ulong.Parse(reader.GetString()!, NumberStyles.Integer, CultureInfo.InvariantCulture),
            JsonTokenType.Number => reader.GetUInt64(),
            _ => throw new JsonException("Expected an unsigned 64-bit integer as a JSON string or number."),
        };

    /// <summary>
    /// Writes an unsigned 64-bit integer as invariant text to preserve exact digits in JavaScript clients.
    /// </summary>
    /// <param name="writer">The JSON writer that receives the string token.</param>
    /// <param name="value">The integer to serialize.</param>
    /// <param name="options">The serializer options for the current operation.</param>
    public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}

/// <summary>Reads decimals from strings or numbers and writes them as invariant strings to prevent JavaScript rounding.</summary>
internal sealed class DecimalStringJsonConverter : JsonConverter<decimal>
{
    /// <summary>
    /// Reads a decimal from a JSON string or number token.
    /// </summary>
    /// <param name="reader">The JSON reader positioned at the decimal token.</param>
    /// <param name="typeToConvert">The target CLR type requested by the serializer.</param>
    /// <param name="options">The serializer options for the current operation.</param>
    /// <returns>The decimal result.</returns>
    /// <exception cref="JsonException">Thrown when the current token is neither a string nor a number.</exception>
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => decimal.Parse(reader.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture),
            JsonTokenType.Number => reader.GetDecimal(),
            _ => throw new JsonException("Expected a decimal as a JSON string or number."),
        };

    /// <summary>
    /// Writes a decimal as invariant text to preserve its exact value in JavaScript clients.
    /// </summary>
    /// <param name="writer">The JSON writer that receives the string token.</param>
    /// <param name="value">The decimal to serialize.</param>
    /// <param name="options">The serializer options for the current operation.</param>
    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}
