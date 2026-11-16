using Microsoft.Data.Sqlite;

namespace GseTechPro.Data;

/// <summary>Creates consistent online SQLite backups without stopping readers or writers.</summary>
public sealed class DatabaseBackup
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public DatabaseBackup(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <summary>
    /// Copies the live database to a .db.bak file and returns its full path. An explicit destination
    /// may overwrite an existing backup; dated default destinations are created under backups/.
    /// The native backup call runs on a thread-pool thread so callers do not block the UI thread.
    /// </summary>
    public async Task<string> BackupAsync(
        string? destinationPath = null,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(_connectionFactory.DatabasePath, ":memory:", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("An in-memory database has no default file-backup location.");

        var sourcePath = Path.GetFullPath(_connectionFactory.DatabasePath);
        var targetPath = Path.GetFullPath(string.IsNullOrWhiteSpace(destinationPath)
            ? CreateDefaultDestination(sourcePath)
            : destinationPath);

        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A backup cannot overwrite the live database.", nameof(destinationPath));

        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDirectory))
            Directory.CreateDirectory(targetDirectory);

        await using var source = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = targetPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        await using var destination = new SqliteConnection(destinationConnectionString);
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        await Task.Run(() => source.BackupDatabase(destination), cancellationToken).ConfigureAwait(false);
        return targetPath;
    }

    private static string CreateDefaultDestination(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidOperationException("The live database path has no parent directory.");
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfffffff");
        return Path.Combine(directory, "backups", $"{name}-{timestamp}.db.bak");
    }
}
