using System.Collections.Concurrent;
using System.Runtime.Serialization;

namespace GseTechPro.Core.Enums;

/// <summary>
/// Deterministic codec between enum values and the string codes stored in SQLite TEXT columns.
/// Members carry an <see cref="EnumMemberAttribute"/> whose <c>Value</c> is the exact DB code
/// (e.g. <c>in_service</c>, <c>J1939</c>, <c>not_fixed</c>). Call <see cref="ToCode{TEnum}"/> when
/// writing to the DB and <see cref="Parse{TEnum}"/> when reading back, so enums round-trip
/// losslessly with the schema in ARCHITECTURE.md §2.
/// </summary>
public static class DbEnum
{
    /// <summary>Codec cache: for each enum type, the code → value and value → code maps.</summary>
    private static readonly ConcurrentDictionary<Type, (IReadOnlyDictionary<string, object> ByCode, IReadOnlyDictionary<object, string> ByName)> Cache = new();

    /// <summary>Returns the DB string code for an enum value (falls back to the member name).</summary>
    public static string ToCode<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var maps = GetMaps<TEnum>();
        return maps.ByName.TryGetValue(value, out var code) ? code : value.ToString();
    }

    /// <summary>
    /// Parses a DB string code back to an enum value. Accepts the exact code, the member name,
    /// or a case-insensitive member name. Throws <see cref="ArgumentOutOfRangeException"/> when unknown.
    /// </summary>
    public static TEnum Parse<TEnum>(string? code) where TEnum : struct, Enum
    {
        if (code is null)
            throw new ArgumentNullException(nameof(code));

        var maps = GetMaps<TEnum>();
        if (maps.ByCode.TryGetValue(code, out var value))
            return (TEnum)value;

        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(name, code, StringComparison.OrdinalIgnoreCase))
                return Enum.Parse<TEnum>(name);
        }

        throw new ArgumentOutOfRangeException(nameof(code), code, $"Unknown {typeof(TEnum).Name} code.");
    }

    /// <summary>
    /// Parses a DB string code back to an enum value without throwing. Returns <c>false</c> when
    /// <paramref name="code"/> is <see langword="null"/> or unrecognized (sets <paramref name="value"/>
    /// to <c>default(TEnum)</c>).
    /// </summary>
    public static bool TryParse<TEnum>(string? code, out TEnum value) where TEnum : struct, Enum
    {
        if (TryParse(typeof(TEnum), code, out var boxed))
        {
            value = (TEnum)boxed!;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Non-generic variant of <see cref="TryParse{TEnum}"/>; the boxed result is the matching enum
    /// value (typed as <c>object</c>) or <see langword="null"/> on failure.
    /// </summary>
    public static bool TryParse(Type enumType, string? code, out object? value)
    {
        if (enumType is null)
            throw new ArgumentNullException(nameof(enumType));
        if (!enumType.IsEnum)
            throw new ArgumentException($"Type {enumType.FullName} is not an enum.", nameof(enumType));

        value = null;
        if (code is null)
            return false;

        var maps = GetMaps(enumType);
        if (maps.ByCode.TryGetValue(code, out var exact))
        {
            value = exact;
            return true;
        }

        foreach (var name in Enum.GetNames(enumType))
        {
            if (string.Equals(name, code, StringComparison.OrdinalIgnoreCase))
            {
                value = Enum.Parse(enumType, name);
                return true;
            }
        }

        return false;
    }

    /// <summary>Builds (or returns the cached) codec maps for <typeparamref name="TEnum"/>.</summary>
    private static (IReadOnlyDictionary<string, object> ByCode, IReadOnlyDictionary<object, string> ByName) GetMaps<TEnum>()
        where TEnum : struct, Enum
        => GetMaps(typeof(TEnum));

    private static (IReadOnlyDictionary<string, object> ByCode, IReadOnlyDictionary<object, string> ByName) GetMaps(Type enumType)
        => Cache.GetOrAdd(enumType, static type => BuildMaps(type));

    private static (IReadOnlyDictionary<string, object> ByCode, IReadOnlyDictionary<object, string> ByName) BuildMaps(Type enumType)
    {
        var byCode = new Dictionary<string, object>(StringComparer.Ordinal);
        var byName = new Dictionary<object, string>();

        foreach (var name in Enum.GetNames(enumType))
        {
            var value = Enum.Parse(enumType, name);
            var field = enumType.GetField(name)!;
            var attr = (EnumMemberAttribute?)Attribute.GetCustomAttribute(field, typeof(EnumMemberAttribute));
            var code = attr?.Value ?? name;

            byCode[code] = value;
            byName[value] = code;
        }

        return (byCode, byName);
    }
}
