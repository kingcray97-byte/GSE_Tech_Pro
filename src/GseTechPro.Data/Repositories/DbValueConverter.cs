using System.Globalization;
using System.Reflection;
using GseTechPro.Core.Enums;

namespace GseTechPro.Data.Repositories;

internal static class DbValueConverter
{
    private static readonly MethodInfo ToCodeDefinition = typeof(DbEnum)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(DbEnum.ToCode) && m.IsGenericMethodDefinition);

    public static object ToDatabase(object? value)
    {
        if (value is null)
            return DBNull.Value;

        var type = Nullable.GetUnderlyingType(value.GetType()) ?? value.GetType();
        if (type.IsEnum)
        {
            return ToCodeDefinition.MakeGenericMethod(type).Invoke(null, [value])
                ?? throw new InvalidOperationException($"Could not encode enum {type.FullName}.");
        }

        return value switch
        {
            bool boolean => boolean ? 1L : 0L,
            DateTimeOffset timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
            _ => value,
        };
    }

    public static object? FromDatabase(object value, Type destinationType)
    {
        var type = Nullable.GetUnderlyingType(destinationType) ?? destinationType;
        if (type.IsEnum)
        {
            var code = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (DbEnum.TryParse(type, code, out var parsed))
                return parsed;

            // Forward compatibility: a newer tablet may write an enum code this build does not
            // know. Preserve the row and degrade to the Core enum's zero value instead of making
            // an entire repository query fail. RecordingSource.Simulated = 0 is deliberately the
            // conservative unknown-value default, so unrecognized provenance is treated as simulated.
            return Activator.CreateInstance(type);
        }

        if (type == typeof(string))
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        if (type == typeof(bool))
            return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
        if (type == typeof(double))
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (type == typeof(float))
            return Convert.ToSingle(value, CultureInfo.InvariantCulture);
        if (type == typeof(int))
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        if (type == typeof(long))
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        if (type == typeof(short))
            return Convert.ToInt16(value, CultureInfo.InvariantCulture);
        if (type == typeof(DateTimeOffset))
            return DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);

        return value;
    }
}
