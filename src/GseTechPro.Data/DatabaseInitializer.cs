using Microsoft.Data.Sqlite;

namespace GseTechPro.Data;

/// <summary>Result of startup initialization, including optional sqlite-vec availability.</summary>
public sealed record DatabaseInitializationResult(
    IReadOnlyList<int> AppliedMigrations,
    bool VectorSearchAvailable,
    IReadOnlyList<string> Notices);

/// <summary>Runs relational migrations, first-run seed data, and the optional vector-table probe.</summary>
public sealed class DatabaseInitializer
{
    private readonly Migrations.SqliteMigrator _migrator;
    private readonly IDbSeeder _seeder;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly DatabaseOptions _options;

    public DatabaseInitializer(
        ISqliteConnectionFactory connectionFactory,
        DatabaseOptions? options = null,
        IDbSeeder? seeder = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _options = options ?? new DatabaseOptions { DatabasePath = connectionFactory.DatabasePath };
        _migrator = new Migrations.SqliteMigrator(connectionFactory);
        _seeder = seeder ?? new SeedData(connectionFactory);
    }

    public async Task<DatabaseInitializationResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (await _migrator.HasPendingUpgradeAsync(cancellationToken).ConfigureAwait(false))
        {
            // A failed backup deliberately stops initialization so user data is never migrated unprotected.
            await new DatabaseBackup(_connectionFactory).BackupAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        var applied = await _migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await _seeder.SeedAsync(cancellationToken).ConfigureAwait(false);

        var notices = new List<string>();
        var vectorAvailable = false;
        if (_options.EnableVectorSearch)
        {
            vectorAvailable = await TryCreateVectorTableAsync(notices.Add, cancellationToken)
                .ConfigureAwait(false);
        }

        return new DatabaseInitializationResult(applied, vectorAvailable, notices);
    }

    private async Task<bool> TryCreateVectorTableAsync(
        Action<string> notice,
        CancellationToken cancellationToken)
    {
        // TODO Phase 3: bundle sqlite-vec with both the .NET data layer and Python AI service.
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'embeddings';";
            var result = await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is not null)
                return true;
        }

        var extensionPath = ResolveExtensionPath();
        var extensionsEnabled = false;
        try
        {
            connection.EnableExtensions(true);
            extensionsEnabled = true;

            await using (var load = connection.CreateCommand())
            {
                load.CommandText = "SELECT load_extension($extensionPath);";
                load.Parameters.AddWithValue("$extensionPath", extensionPath);
                await load.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var create = connection.CreateCommand())
            {
                create.CommandText = """
                    CREATE VIRTUAL TABLE IF NOT EXISTS embeddings USING vec0(
                      chunk_id INTEGER PRIMARY KEY,
                      embedding float[768]
                    );
                    """;
                await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex) when (ex is SqliteException
            or InvalidOperationException
            or DllNotFoundException
            or EntryPointNotFoundException
            or NotSupportedException)
        {
            var message = $"sqlite-vec is unavailable; vector search is disabled for this run. {ex.Message}";
            notice(message);
            _options.LogNotice?.Invoke(message);
            return false;
        }
        finally
        {
            if (extensionsEnabled)
                connection.EnableExtensions(false);
        }
    }

    private string ResolveExtensionPath()
    {
        if (!string.IsNullOrWhiteSpace(_options.SqliteVecExtensionPath))
            return _options.SqliteVecExtensionPath;

        var bundledCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "vec0.dll"),
            Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "vec0.dll"),
        };
        return bundledCandidates.FirstOrDefault(File.Exists) ?? "vec0";
    }
}
