using System.Text.Json;
using GseTechPro.Core.Models;

namespace GseTechPro.Data.Repositories;

/// <summary>
/// Key/value settings in <c>app_settings</c> (value stored as JSON). Used for shop-wide settings such as
/// the AI routing options (ADR-013) and the administrator PIN hash.
/// </summary>
public sealed class AppSettingsRepository(ISqliteConnectionFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The raw JSON value, or null when the key is not set.</summary>
    public async Task<string?> GetJsonAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value_json FROM app_settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result as string;
    }

    /// <summary>The value, or <paramref name="fallback"/> when the key is missing or its JSON does not read as <typeparamref name="T"/>.</summary>
    public async Task<T> GetAsync<T>(string key, T fallback, CancellationToken cancellationToken = default)
    {
        var json = await GetJsonAsync(key, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return fallback;
        try
        {
            var value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return value is null ? fallback : value;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    /// <summary>Inserts or replaces the value.</summary>
    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO app_settings (key, value_json, updated_at) VALUES ($key, $value, $updated) " +
            "ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json, updated_at = excluded.updated_at;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", json);
        command.Parameters.AddWithValue("$updated", Clock.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
