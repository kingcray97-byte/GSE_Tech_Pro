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
    private readonly DatabaseOptions _options;
    private readonly object _vecPathLock = new();
    private bool _vecPathResolved;
    private string? _vecExtensionPath;

    // Native loadability is cached per factory/application startup. A negative result must not
    // cause a file probe and a caught SqliteException on every repository connection.
    private int _vecUnavailable;

    public SqliteConnectionFactory(DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.DatabasePath))
            throw new ArgumentException("A SQLite database path is required.", nameof(options));

        _options = options;
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

            // vec0 must be registered before migration 3. Any failure is non-fatal and cached
            // for this factory lifetime; relational storage and FTS5 continue without it.
            if (_options.EnableVectorSearch && Volatile.Read(ref _vecUnavailable) == 0)
            {
                var extensionPath = ResolveVecExtensionPath();
                if (extensionPath is null)
                {
                    ReportVecUnavailable(
                        "sqlite-vec is unavailable: no trusted vec0.dll was found in the application " +
                        "directory or runtimes/win-x64/native. Vector search is disabled; FTS5 remains available.");
                }
                else
                {
                    await SqliteVecExtension.TryLoadAsync(
                        connection,
                        extensionPath,
                        ReportVecUnavailable,
                        cancellationToken).ConfigureAwait(false);
                }
            }

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

    private string? ResolveVecExtensionPath()
    {
        lock (_vecPathLock)
        {
            if (!_vecPathResolved)
            {
                _vecExtensionPath = SqliteVecExtension.ResolveExtensionPath(_options.SqliteVecExtensionPath);
                _vecPathResolved = true;
            }
            return _vecExtensionPath;
        }
    }

    private void ReportVecUnavailable(string message)
    {
        if (Interlocked.Exchange(ref _vecUnavailable, 1) == 0)
            _options.LogNotice?.Invoke(message);
    }
}
