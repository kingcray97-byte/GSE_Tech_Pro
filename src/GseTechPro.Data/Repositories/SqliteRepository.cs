using GseTechPro.Core.Models;
using Microsoft.Data.Sqlite;

namespace GseTechPro.Data.Repositories;

/// <summary>
/// SQLite repository using cached Core-record metadata. Values are always parameters; table and
/// column identifiers come from constructor/configuration metadata and are validated, never from SQL input.
/// </summary>
public class SqliteRepository<T> : IRepository<T> where T : class, new()
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly string _tableName;
    private readonly RepositoryMetadata<T> _metadata = RepositoryMetadata<T>.Instance;
    private readonly MappedProperty<T> _key;
    private readonly MappedProperty<T>? _createdAt;
    private readonly MappedProperty<T>? _deletedAt;
    private readonly MappedProperty<T>? _updatedAt;
    private readonly string _selectColumns;

    public SqliteRepository(
        ISqliteConnectionFactory connectionFactory,
        string tableName,
        string primaryKeyColumn = "id")
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _tableName = ValidateIdentifier(tableName, nameof(tableName));
        foreach (var property in _metadata.Properties)
            ValidateIdentifier(property.Column, nameof(primaryKeyColumn));

        _key = _metadata.GetKey(primaryKeyColumn);
        _createdAt = _metadata.CreatedAt;
        _deletedAt = _metadata.DeletedAt;
        _updatedAt = _metadata.UpdatedAt;
        _selectColumns = string.Join(", ", _metadata.Properties.Select(p => p.Column));
    }

    public async Task<T?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        var where = $"{_key.Column} = $key{LiveOnlyClause("AND")}";

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateSelectCommand(connection, where);
        command.Parameters.AddWithValue("$key", id);
        var rows = await ReadRowsAsync(command, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<IReadOnlyList<T>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var where = _deletedAt is null ? string.Empty : "deleted_at IS NULL";
        await using var command = CreateSelectCommand(connection, where);
        return await ReadRowsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task AddAsync(T entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var columns = string.Join(", ", _metadata.Properties.Select(p => p.Column));
        var parameters = string.Join(", ", _metadata.Properties.Select(p => ParameterName(p.Column)));

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO {_tableName} ({columns}) VALUES ({parameters});";
        foreach (var property in _metadata.Properties)
        {
            command.Parameters.AddWithValue(
                ParameterName(property.Column),
                DbValueConverter.ToDatabase(property.Read(entity)));
        }
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> UpdateAsync(T entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var updatedProperties = _metadata.Properties
            .Where(p => !ReferenceEquals(p, _key) && !ReferenceEquals(p, _createdAt))
            .ToArray();
        if (updatedProperties.Length == 0)
            throw new InvalidOperationException($"{_tableName} has no columns to update.");

        var assignments = string.Join(", ", updatedProperties.Select(p => $"{p.Column} = {ParameterName(p.Column)}"));
        var where = $"{_key.Column} = $repositoryKey{LiveOnlyClause("AND")}";

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {_tableName} SET {assignments} WHERE {where};";
        foreach (var property in updatedProperties)
        {
            var value = ReferenceEquals(property, _updatedAt) ? Clock.UtcNow : property.Read(entity);
            command.Parameters.AddWithValue(ParameterName(property.Column), DbValueConverter.ToDatabase(value));
        }
        command.Parameters.AddWithValue("$repositoryKey", DbValueConverter.ToDatabase(_key.Read(entity)));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 0;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (_deletedAt is null)
        {
            throw new NotSupportedException(
                $"{_tableName} has no deleted_at column in ARCHITECTURE.md section 2.2; destructive delete is not provided.");
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE {_tableName}
            SET deleted_at = $deletedAt
            WHERE {_key.Column} = $key AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$deletedAt", Clock.UtcNow);
        command.Parameters.AddWithValue("$key", id);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 0;
    }

    public async Task<IReadOnlyList<T>> FindAsync(
        string column,
        object? value,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(column))
            throw new ArgumentException("A mapped property or column is required.", nameof(column));
        if (!_metadata.Lookup.TryGetValue(column, out var property))
            throw new ArgumentException($"'{column}' is not a mapped column on {typeof(T).Name}.", nameof(column));

        var comparison = value is null ? $"{property.Column} IS NULL" : $"{property.Column} = $value";
        var where = comparison + LiveOnlyClause("AND");

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateSelectCommand(connection, where);
        if (value is not null)
            command.Parameters.AddWithValue("$value", DbValueConverter.ToDatabase(value));
        return await ReadRowsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private SqliteCommand CreateSelectCommand(SqliteConnection connection, string where)
    {
        var command = connection.CreateCommand();
        var whereClause = string.IsNullOrWhiteSpace(where) ? string.Empty : $" WHERE {where}";
        command.CommandText = $"SELECT {_selectColumns} FROM {_tableName}{whereClause} ORDER BY {_key.Column};";
        return command;
    }

    private string LiveOnlyClause(string prefix) => _deletedAt is null
        ? string.Empty
        : $" {prefix} deleted_at IS NULL";

    private static string ParameterName(string column) => "$" + column;

    private static string ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !(char.IsLetter(value[0]) || value[0] == '_') ||
            value.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
        {
            throw new ArgumentException($"'{value}' is not a safe SQLite identifier.", parameterName);
        }
        return value;
    }

    private async Task<List<T>> ReadRowsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ordinals = _metadata.Properties.Select(p => reader.GetOrdinal(p.Column)).ToArray();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var entity = new T();
            for (var i = 0; i < _metadata.Properties.Count; i++)
            {
                var property = _metadata.Properties[i];
                if (await reader.IsDBNullAsync(ordinals[i], cancellationToken).ConfigureAwait(false))
                {
                    if (!property.Property.PropertyType.IsValueType ||
                        Nullable.GetUnderlyingType(property.Property.PropertyType) is not null)
                    {
                        property.Write(entity, null);
                    }
                    continue;
                }

                var value = DbValueConverter.FromDatabase(
                    reader.GetValue(ordinals[i]),
                    property.Property.PropertyType);
                property.Write(entity, value);
            }
            rows.Add(entity);
        }
        return rows;
    }
}
