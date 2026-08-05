using System.Text.Json;
using System.Text.Json.Serialization;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// The protocol's serializer settings, self-contained so the engine never mutates the
/// host's global JSON options: camelCase properties, camelCase string enums
/// ("eq", "ncontains", "countDistinct"), nulls omitted.
/// </summary>
public static class IrJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
