using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GseTechPro.App.Services;

/// <summary>
/// Shared JSON serializer configuration for the App ↔ local AI service HTTP boundary (ADR-008).
/// Uses <see cref="JsonNamingPolicy.SnakeCaseLower"/> for property names and serializes all enums
/// via their <see cref="EnumMemberAttribute.Value"/> wire strings (e.g. <c>"manual-page"</c>,
/// <c>"work-order"</c>, <c>"diagram_caption"</c>, <c>"na"</c>).
/// </summary>
public static class AiServiceJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new EnumMemberJsonConverterFactory());
        return options;
    }
}

/// <summary>
/// Creates an <see cref="AiEnumMemberJsonConverter{TEnum}"/> for any enum type (including nullable enums,
/// which <see cref="System.Text.Json"/> wraps automatically).
/// </summary>
public sealed class EnumMemberJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var converterType = typeof(AiEnumMemberJsonConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

/// <summary>
/// Reads and writes an enum using its <see cref="EnumMemberAttribute"/> wire value (falling back to
/// the member name). Reads are case-insensitive and also accept integer values for robustness.
/// </summary>
public sealed class AiEnumMemberJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private readonly Dictionary<string, TEnum> _nameToValue = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TEnum, string> _valueToName = new();

    public AiEnumMemberJsonConverter()
    {
        foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = (TEnum)field.GetValue(null)!;
            var wireName = field.GetCustomAttribute<EnumMemberAttribute>()?.Value ?? field.Name;
            _valueToName[value] = wireName;
            if (!_nameToValue.ContainsKey(wireName))
            {
                _nameToValue[wireName] = value;
            }
            if (!_nameToValue.ContainsKey(field.Name))
            {
                _nameToValue[field.Name] = value;
            }
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
                if (!string.IsNullOrEmpty(text) && _nameToValue.TryGetValue(text, out var mapped))
                {
                    return mapped;
                }
                if (Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed))
                {
                    return parsed;
                }
                throw new JsonException($"Unknown {typeof(TEnum).Name} wire value '{text}'.");
            }

            default:
                throw new JsonException($"Unexpected JSON token {reader.TokenType} when reading {typeof(TEnum).Name}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(_valueToName.TryGetValue(value, out var wireName) ? wireName : value.ToString());
    }
}
