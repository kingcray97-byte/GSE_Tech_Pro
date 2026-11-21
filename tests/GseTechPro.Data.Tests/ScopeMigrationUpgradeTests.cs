using GseTechPro.Core.Enums;
using GseTechPro.Data.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GseTechPro.Data.Tests;

public sealed class ScopeMigrationUpgradeTests
{
    [Fact]
    public async Task Migration006_FromLegacyScopeShape_PreservesRowsAndAddsContract()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gse-tech-pro-tests");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, $"scope-upgrade-{Guid.NewGuid():N}.db");

        try
        {
            var services = new DataServices(new DatabaseOptions
            {
                DatabasePath = dbPath,
                EnableVectorSearch = false,
            });

            // Scope did not change in migrations 2-5. Applying 001 and recording history through 5
            // is therefore sufficient to exercise the exact legacy scope upgrade without vec0.
            await using (var connection = await services.Connections.OpenAsync())
            {
                await Execute(connection, await ReadMigration("GseTechPro.Data.Migrations.001_Initial.sql"));
                await Execute(connection, """
                    CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);
                    INSERT INTO schema_migrations(version, applied_at)
                    VALUES (1, '2026-01-01T00:00:00Z'), (2, '2026-01-01T00:00:00Z'),
                           (3, '2026-01-01T00:00:00Z'), (4, '2026-01-01T00:00:00Z'),
                           (5, '2026-01-01T00:00:00Z');

                    INSERT INTO assets(id, asset_number, created_at, updated_at)
                    VALUES ('legacy-asset', 'LEGACY-1', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                    INSERT INTO work_orders(id, asset_id, opened_at, created_at, updated_at)
                    VALUES ('legacy-wo', 'legacy-asset', '2026-01-01T00:00:00Z',
                            '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                    INSERT INTO reference_waveforms(
                        id, name, test_name, expected_json, data_path, source, created_at, updated_at)
                    VALUES (
                        'legacy-reference', 'Legacy reference', 'Legacy test', '{"measurements":[]}',
                        'reference_waveforms/legacy-reference.wfm', 'real_capture',
                        '2026-01-01T00:00:00Z', '2026-01-02T00:00:00Z');
                    INSERT INTO guided_tests(
                        id, name, description, procedure, safety_warning_ids, reference_waveform_id,
                        created_at, updated_at)
                    VALUES (
                        'legacy-guided', 'Legacy guided test', 'Keep this description', 'Keep this procedure',
                        '[]', 'legacy-reference', '2026-01-01T00:00:00Z', '2026-01-02T00:00:00Z');
                    INSERT INTO waveforms(
                        id, work_order_id, scope_device, channel_config_json, data_path,
                        guided_test_id, reference_waveform_id, created_at)
                    VALUES (
                        'legacy-waveform', 'legacy-wo', 'hantek', '[]', 'waveforms/legacy-waveform.wfm',
                        'legacy-guided', 'legacy-reference', '2026-01-01T00:00:00Z');
                    """);
            }

            var applied = await services.Migrator.MigrateAsync();
            // 6 rebuilds the scope tables; 7 adds waveforms.notes (10D.1i) on top.
            Assert.Equal(new[] { 6, 7 }, applied);
            Assert.Empty(await services.Migrator.MigrateAsync());

            var reference = await services.ReferenceWaveforms.GetByIdAsync("legacy-reference");
            Assert.NotNull(reference);
            Assert.Equal("reference_waveforms/legacy-reference.wfm", reference!.DataPath);
            Assert.Equal(WaveformSource.RealCapture, reference.Source);
            Assert.Equal("2026-01-02T00:00:00Z", reference.UpdatedAt);

            var guided = await services.GuidedTests.GetByIdAsync("legacy-guided");
            Assert.NotNull(guided);
            Assert.Equal("Keep this description", guided!.Description);
            Assert.Equal("Keep this procedure", guided.Procedure);
            Assert.Equal("legacy-reference", guided.ReferenceWaveformId);
            Assert.Null(guided.ScopeSetupJson);
            Assert.Equal("2026-01-02T00:00:00Z", guided.UpdatedAt);

            var waveform = await services.Waveforms.GetByIdAsync("legacy-waveform");
            Assert.NotNull(waveform);
            Assert.Equal("waveforms/legacy-waveform.wfm", waveform!.DataPath);
            Assert.Equal(ScopeDeviceKind.Hantek, waveform.ScopeDevice);
            Assert.Equal("legacy-guided", waveform.GuidedTestId);
            Assert.Equal("legacy-reference", waveform.ReferenceWaveformId);
            Assert.Null(waveform.Notes);

            await using (var connection = await services.Connections.OpenAsync())
            {
                Assert.Equal(0L, await Scalar(connection,
                    "SELECT \"notnull\" FROM pragma_table_info('reference_waveforms') WHERE name = 'data_path';"));
                Assert.Equal(1L, await Scalar(connection,
                    "SELECT COUNT(*) FROM pragma_table_info('guided_tests') WHERE name = 'scope_setup_json';"));
                Assert.Equal(1L, await Scalar(connection,
                    "SELECT COUNT(*) FROM pragma_table_info('waveforms') WHERE name = 'notes';"));
                Assert.Equal(1L, await Scalar(connection,
                    "SELECT COUNT(*) FROM pragma_foreign_key_list('guided_tests') " +
                    "WHERE \"table\" = 'reference_waveforms' AND \"from\" = 'reference_waveform_id';"));
                Assert.Equal(1L, await Scalar(connection,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_guided_tests_model';"));

                await using var check = connection.CreateCommand();
                check.CommandText = "PRAGMA foreign_key_check;";
                await using var reader = await check.ExecuteReaderAsync();
                Assert.False(await reader.ReadAsync(), "The upgraded scope graph must satisfy all foreign keys.");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal");
            if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm");
        }
    }

    private static async Task<string> ReadMigration(string resourceName)
    {
        await using var stream = typeof(SqliteMigrator).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing test migration resource '{resourceName}'.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static async Task Execute(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
