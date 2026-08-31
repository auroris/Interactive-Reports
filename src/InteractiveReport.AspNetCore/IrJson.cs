using System.Text.Json;
using System.Text.Json.Serialization;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Owns the protocol's serializer settings so the engine never mutates the
/// host's global JSON options: camelCase properties, camelCase string enums
/// ("eq", "ncontains", "countDistinct"), nulls omitted. Integer enum input is
/// rejected: the serializer never emits numbers for enums, so a numeric value (for
/// example dir: 99) is foreign input that would otherwise pass deserialization as an
/// undefined enum member and be silently reinterpreted downstream.
/// </summary>
public static class IrJson
{
    /// <summary>Gets the shared, read-only-by-convention options used for Interactive Reports request and response documents.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new Int64StringJsonConverter(),
            new UInt64StringJsonConverter(),
            new DecimalStringJsonConverter(),
        },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
