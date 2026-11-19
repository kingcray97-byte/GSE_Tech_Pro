using System.Data;
using GseTechPro.Core.Models;
using Microsoft.Data.Sqlite;

namespace GseTechPro.Data.Migrations;

/// <summary>Small, forward-only migration runner for embedded, versioned SQL scripts.</summary>
public sealed class SqliteMigrator
{
    public const int LatestVersion = 6;
    public const int VectorMigrationVersion = 3;
    private const string InitialMigrationResource = "GseTechPro.Data.Migrations.001_Initial.sql";
    private const string RecordingsMigrationResource = "GseTechPro.Data.Migrations.002_Recordings.sql";
    private const string ManualsSearchMigrationResource = "GseTechPro.Data.Migrations.003_ManualsSearch.sql";
    private const string GseTroubleshootingMigrationResource = "GseTechPro.Data.Migrations.004_GseTroubleshooting.sql";
    private const string RepairHistoryMigrationResource = "GseTechPro.Data.Migrations.005_RepairHistory.sql";
    private const string ScopeMigrationResource = "GseTechPro.Data.Migrations.006_Scope.sql";

    private static readonly IReadOnlyList<EmbeddedMigration> Migrations =
    [
        new(1, InitialMigrationResource),
        new(2, RecordingsMigrationResource),
        new(3, ManualsSearchMigrationResource),
        new(4, GseTroubleshootingMigrationResource),
        new(5, RepairHistoryMigrationResource),
        new(6, ScopeMigrationResource),
    ];

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly List<string> _notices = new();

    /// <summary>Non-fatal migration conditions from the most recent MigrateAsync call.</summary>
    public IReadOnlyList<string> Notices => _notices.ToArray();

    public SqliteMigrator(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <summary>
    /// Returns whether an existing migration history has pending migrations. A new database returns
    /// false so its initial schema creation is not treated as an upgrade requiring a backup.
    /// </summary>
    public async Task<bool> HasPendingUpgradeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var historyExists = connection.CreateCommand())
        {
            historyExists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations';";
            var result = await historyExists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is null)
                return false;
        }

        var appliedVersions = await ReadAppliedVersionsAsync(connection, cancellationToken).ConfigureAwait(false);
        return Enumerable.Range(1, LatestVersion).Any(version => !appliedVersions.Contains(version));
    }

    /// <summary>
    /// Applies every pending migration in one transaction and returns the versions applied by this call.
    /// Released migration SQL is additive and is never edited in place.
    /// </summary>
    public async Task<IReadOnlyList<int>> MigrateAsync(CancellationToken cancellationToken = default)
    {
        _notices.Clear();
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var walCommand = connection.CreateCommand())
        {
            walCommand.CommandText = "PRAGMA journal_mode = WAL;";
            await walCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var historyCommand = connection.CreateCommand())
        {
            historyCommand.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                  version INTEGER PRIMARY KEY,
                  applied_at TEXT NOT NULL
                );
                """;
            await historyCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var appliedVersions = await ReadAppliedVersionsAsync(connection, cancellationToken).ConfigureAwait(false);
        var pending = Migrations.Where(m => !appliedVersions.Contains(m.Version)).ToArray();
        if (pending.Length == 0)
            return Array.Empty<int>();

        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            foreach (var migration in pending)
            {
                var sql = await ReadMigrationSqlAsync(migration.ResourceName, cancellationToken).ConfigureAwait(false);

                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    try
                    {
                        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (SqliteException ex) when (
                        migration.Version == VectorMigrationVersion && SqliteVecExtension.IsMissingVec0(ex))
                    {
                        // vec0 DDL is the only optional statement. The relational index preceding it
                        // remains valid, and version 3 is recorded so the migration stays forward-only.
                        // DatabaseInitializer retries CREATE embeddings whenever the module is present.
                        _notices.Add(
                            "sqlite-vec vec0 is unavailable; migration 3 skipped embeddings creation. " +
                            "Vector search is disabled and FTS5 remains available.");
                    }
                }

                await using (var record = connection.CreateCommand())
                {
                    record.Transaction = transaction;
                    record.CommandText = "INSERT INTO schema_migrations(version, applied_at) VALUES ($version, $appliedAt);";
                    record.Parameters.AddWithValue("$version", migration.Version);
                    record.Parameters.AddWithValue("$appliedAt", Clock.UtcNow);
                    await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return pending.Select(m => m.Version).ToArray();
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<HashSet<int>> ReadAppliedVersionsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var versions = new HashSet<int>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_migrations;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            versions.Add(reader.GetInt32(0));
        return versions;
    }

    private static async Task<string> ReadMigrationSqlAsync(
        string resourceName,
        CancellationToken cancellationToken)
    {
        var assembly = typeof(SqliteMigrator).Assembly;
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record EmbeddedMigration(int Version, string ResourceName);
}
