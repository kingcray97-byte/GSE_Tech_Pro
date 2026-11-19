using Microsoft.Data.Sqlite;

namespace GseTechPro.Data;

/// <summary>Result of startup initialization, including optional sqlite-vec availability.</summary>
public sealed record DatabaseInitializationResult(
    IReadOnlyList<int> AppliedMigrations,
    bool VectorSearchAvailable,
    IReadOnlyList<string> Notices);

/// <summary>Runs relational migrations, first-run seeds, and the optional vector-table probe.</summary>
public sealed class DatabaseInitializer
{
    private readonly Migrations.SqliteMigrator _migrator;
    private readonly IDbSeeder _seeder;
    private readonly IDtcLibrarySeeder _dtcLibrarySeeder;
    private readonly ITroubleshootingSeeder _troubleshootingSeeder;
    private readonly IScopeSeeder _scopeSeeder;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly DatabaseOptions _options;

    public DatabaseInitializer(
        ISqliteConnectionFactory connectionFactory,
        DatabaseOptions? options = null,
        IDbSeeder? seeder = null,
        IDtcLibrarySeeder? dtcLibrarySeeder = null,
        ITroubleshootingSeeder? troubleshootingSeeder = null,
        IScopeSeeder? scopeSeeder = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _options = options ?? new DatabaseOptions { DatabasePath = connectionFactory.DatabasePath };
        _migrator = new Migrations.SqliteMigrator(connectionFactory);
        _seeder = seeder ?? new SeedData(connectionFactory);
        _dtcLibrarySeeder = dtcLibrarySeeder ?? new DtcLibrarySeeder(connectionFactory);
        _troubleshootingSeeder = troubleshootingSeeder ?? new TroubleshootingSeeder(connectionFactory);
        _scopeSeeder = scopeSeeder ?? new ScopeSeeder(connectionFactory);
    }

    public async Task<DatabaseInitializationResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (await _migrator.HasPendingUpgradeAsync(cancellationToken).ConfigureAwait(false))
        {
            // A failed backup deliberately stops initialization so user data is never migrated unprotected.
            await new DatabaseBackup(_connectionFactory).BackupAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        var notices = new List<string>();
        var applied = await _migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
        foreach (var notice in _migrator.Notices)
            AddNotice(notices, notice);

        await _seeder.SeedAsync(cancellationToken).ConfigureAwait(false);
        await _dtcLibrarySeeder.SeedAsync(cancellationToken).ConfigureAwait(false);
        await _troubleshootingSeeder.SeedAsync(cancellationToken).ConfigureAwait(false);
        await _scopeSeeder.SeedAsync(cancellationToken).ConfigureAwait(false);

        var vectorAvailable = false;
        if (_options.EnableVectorSearch)
        {
            vectorAvailable = await TryCreateVectorTableAsync(
                message => AddNotice(notices, message), cancellationToken).ConfigureAwait(false);
        }

        return new DatabaseInitializationResult(applied, vectorAvailable, notices);
    }

    private async Task<bool> TryCreateVectorTableAsync(
        Action<string> notice,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Migration 3 normally creates this. Retrying here also repairs the intentionally skipped
        // vec-absent case without rerunning or editing any released migration.
        if (!await SqliteVecExtension.IsVec0AvailableAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            notice("sqlite-vec is unavailable; embeddings was not created. FTS5 search remains available.");
            return false;
        }

        try
        {
            await using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS embeddings USING vec0(
                  chunk_id INTEGER PRIMARY KEY,
                  embedding float[768]
                );
                """;
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SqliteException ex) when (SqliteVecExtension.IsMissingVec0(ex))
        {
            notice("sqlite-vec became unavailable while creating embeddings; FTS5 search remains available.");
            return false;
        }
    }

    private void AddNotice(List<string> notices, string message)
    {
        if (!notices.Contains(message, StringComparer.Ordinal))
            notices.Add(message);
        _options.LogNotice?.Invoke(message);
    }
}
