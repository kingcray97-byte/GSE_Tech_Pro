using Microsoft.Data.Sqlite;

namespace GseTechPro.Data;

/// <summary>Creates independently async-disposable connections to the application database.</summary>
public interface ISqliteConnectionFactory
{
    string DatabasePath { get; }

    /// <summary>Creates an unopened connection using the configured database path.</summary>
    SqliteConnection CreateConnection();

    /// <summary>Opens a connection and enables per-connection SQLite safety settings.</summary>
    Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.DatabasePath))
            throw new ArgumentException("A SQLite database path is required.", nameof(options));

        DatabasePath = options.DatabasePath;

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        };
        _connectionString = builder.ToString();

        if (!string.Equals(DatabasePath, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
        }
    }

    public string DatabasePath { get; }

    public SqliteConnection CreateConnection() => new(_connectionString);

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
