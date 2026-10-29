using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkIsolator.Core;

/// <summary>
/// Shared JSON options. Important: reading is case-insensitive, otherwise a file
/// written in camelCase cannot be read back into PascalCase properties ("empty" profiles).
/// </summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}
