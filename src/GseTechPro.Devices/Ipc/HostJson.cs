using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GseTechPro.Core.Enums;

namespace GseTechPro.Devices.Ipc;

/// <summary>
/// Shared JSON configuration for the app↔host32 wire protocol. The client
/// (<see cref="HostConnection"/>) and the server (<see cref="HostServer"/>) MUST both use
/// <see cref="Options"/> so enums, casing, and null handling agree across the pipe.
///
/// Enums serialize as their <see cref="EnumMemberAttribute"/> values (e.g. <c>"J1939"</c>,
/// <c>"active"</c>) rather than integers, matching the DB wire values in ARCHITECTURE.md §2.2.
/// </summary>
public static class HostJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new ProtocolTypeConverter());
        options.Converters.Add(new DtcStatusConverter());
        return options;
    }
}

public sealed class ProtocolTypeConverter : EnumMemberJsonConverter<ProtocolType>
{
}

public sealed class DtcStatusConverter : EnumMemberJsonConverter<DtcStatus>
{
}

/// <summary>
/// Reads/writes an enum as its <see cref="EnumMemberAttribute"/> string (falling back to the member
/// name). Reads are case-insensitive and also accept the raw integer value for robustness.
/// </summary>
public abstract class EnumMemberJsonConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    private readonly Dictionary<string, TEnum> _nameToValue = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TEnum, string> _valueToName = new();

    protected EnumMemberJsonConverter()
    {
        foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = (TEnum)field.GetValue(null)!;
            var name = field.GetCustomAttribute<EnumMemberAttribute>()?.Value ?? field.Name;
            _valueToName[value] = name;
            if (!_nameToValue.ContainsKey(name))
                _nameToValue[name] = value;
        }
    }

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return (TEnum)Enum.ToObject(typeof(TEnum), reader.GetInt32());
            case JsonTokenType.String:
            {
                var text = reader.GetString();
                if (!string.IsNullOrEmpty(text) && _nameToValue.TryGetValue(text, out var named))
                    return named;
                if (Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed))
                    return parsed;
                throw new JsonException($"Unknown {typeof(TEnum).Name} value '{text}'.");
            }
            default:
                throw new JsonException($"Unexpected token {reader.TokenType} for {typeof(TEnum).Name}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(_valueToName.TryGetValue(value, out var name) ? name : value.ToString());
    }
}
