using System.Reflection;
using System.Text;

namespace GseTechPro.Data.Repositories;

internal sealed class RepositoryMetadata<T> where T : class, new()
{
    public static RepositoryMetadata<T> Instance { get; } = new();

    private RepositoryMetadata()
    {
        Properties = typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is not null && p.SetMethod is not null && p.GetIndexParameters().Length == 0)
            .Select(p => new MappedProperty<T>(p, ToSnakeCase(p.Name)))
            .ToArray();

        if (Properties.Count == 0)
            throw new InvalidOperationException($"{typeof(T).FullName} has no mappable properties.");
        if (Properties.Select(p => p.Column).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Properties.Count)
            throw new InvalidOperationException($"{typeof(T).FullName} has duplicate mapped column names.");

        var lookup = new Dictionary<string, MappedProperty<T>>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in Properties)
        {
            lookup.Add(property.Column, property);
            if (!lookup.ContainsKey(property.Property.Name))
                lookup.Add(property.Property.Name, property);
        }
        Lookup = lookup;
    }

    public IReadOnlyList<MappedProperty<T>> Properties { get; }

    public IReadOnlyDictionary<string, MappedProperty<T>> Lookup { get; }

    public MappedProperty<T>? CreatedAt => Properties.SingleOrDefault(p => p.Column == "created_at");

    public MappedProperty<T>? DeletedAt => Properties.SingleOrDefault(p => p.Column == "deleted_at");

    public MappedProperty<T>? UpdatedAt => Properties.SingleOrDefault(p => p.Column == "updated_at");

    public MappedProperty<T> GetKey(string keyColumn) => Lookup.TryGetValue(keyColumn, out var key)
        ? key
        : throw new ArgumentException($"{typeof(T).Name} has no mapped key '{keyColumn}'.", nameof(keyColumn));

    private static string ToSnakeCase(string value)
    {
        var result = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (char.IsUpper(current) && i > 0 &&
                (!char.IsUpper(value[i - 1]) || (i + 1 < value.Length && char.IsLower(value[i + 1]))))
            {
                result.Append('_');
            }
            result.Append(char.ToLowerInvariant(current));
        }
        return result.ToString();
    }
}

internal sealed class MappedProperty<T>(PropertyInfo property, string column) where T : class, new()
{
    public PropertyInfo Property { get; } = property;

    public string Column { get; } = column;

    public object? Read(T entity) => Property.GetValue(entity);

    public void Write(T entity, object? value) => Property.SetValue(entity, value);
}
