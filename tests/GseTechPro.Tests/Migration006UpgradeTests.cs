using GseTechPro.Data;
using GseTechPro.Data.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GseTechPro.Tests;

/// <summary>
/// Phase 6 review: migration 006 rebuilds guided_tests / reference_waveforms / waveforms inside the migrator
/// transaction with foreign keys ON (SqliteConnectionFactory). These tests upgrade a populated version-5
/// database (the three tables in their 001 shape) and check that every row survives.
/// </summary>
public sealed class Migration006UpgradeTests : IAsyncLifetime
{
    private string _dir = "";
    private DataServices _services = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"gse-m006-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _services = new DataServices(new DatabaseOptions { DatabasePath = Path.Combine(_dir, "gse.db") });
        await _services.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    /// <summary>Turns the current database back into a version-5 one: the three scope tables in their 001 shape.</summary>
    private async Task DowngradeScopeTablesToV5Async(SqliteConnection c)
    {
        await Exec(c, """
            PRAGMA foreign_keys = OFF;
            DROP TABLE waveforms; DROP TABLE guided_tests; DROP TABLE reference_waveforms;
            CREATE TABLE guided_tests (
              id TEXT PRIMARY KEY, name TEXT NOT NULL, equipment_model_id TEXT REFERENCES equipment_models(id),
              system TEXT, description TEXT, procedure TEXT, safety_warning_ids TEXT, reference_waveform_id TEXT,
              created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE reference_waveforms (
              id TEXT PRIMARY KEY, name TEXT NOT NULL, equipment_model_id TEXT REFERENCES equipment_models(id),
              system TEXT, test_name TEXT, expected_json TEXT, data_path TEXT NOT NULL, source TEXT NOT NULL,
              citation TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE waveforms (
              id TEXT PRIMARY KEY, work_order_id TEXT NOT NULL REFERENCES work_orders(id), scope_device TEXT NOT NULL,
              channel_config_json TEXT NOT NULL, timebase REAL, sample_rate INTEGER, sample_count INTEGER,
              data_path TEXT NOT NULL, guided_test_id TEXT REFERENCES guided_tests(id),
              reference_waveform_id TEXT REFERENCES reference_waveforms(id), measurements_json TEXT,
              created_at TEXT NOT NULL, deleted_at TEXT);
            CREATE INDEX idx_waveforms_wo ON waveforms(work_order_id);
            DELETE FROM schema_migrations WHERE version = 6;
            PRAGMA foreign_keys = ON;
            """);
    }

    private static async Task Exec(SqliteConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(SqliteConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task<string> EnsureWorkOrderAsync(SqliteConnection c)
    {
        if (await Scalar(c, "SELECT id FROM work_orders LIMIT 1;") is string existing)
            return existing;
        const string ts = "2026-11-20T00:00:00Z";
        await Exec(c, $"""
            INSERT INTO assets (id, asset_number, created_at, updated_at)
              VALUES ('asset-m006', 'M006-1', '{ts}', '{ts}');
            INSERT INTO work_orders (id, asset_id, opened_at, created_at, updated_at)
              VALUES ('wo-m006', 'asset-m006', '{ts}', '{ts}', '{ts}');
            """);
        return "wo-m006";
    }

    [Fact]
    public async Task UpgradeFromV5_PreservesEveryScopeRow_AndAppliesTheAdr011Shape()
    {
        await using var c = await _services.Connections.OpenAsync();
        await DowngradeScopeTablesToV5Async(c);
        var wo = await EnsureWorkOrderAsync(c);
        const string ts = "2026-11-17T00:00:00Z";
        await Exec(c, $"""
            INSERT INTO reference_waveforms VALUES ('rw-1','Crank ref','eqm-tug-660-28','Engine','crank','{"{}"}','refs/rw-1.wfm','manual','Manual p.1','{ts}','{ts}');
            INSERT INTO guided_tests VALUES ('gt-1','Crank test','eqm-tug-660-28','Engine','d','p','["sw-lockout-tagout"]','rw-1','{ts}','{ts}');
            INSERT INTO waveforms VALUES ('wf-1','{wo}','simulator','[]',0.005,1000000,10000,'captures/wf-1.wfm','gt-1','rw-1','{"{}"}','{ts}',NULL);
            """);

        var applied = await new SqliteMigrator(_services.Connections).MigrateAsync();

        Assert.Equal(new[] { 6 }, applied);
        await using var after = await _services.Connections.OpenAsync();
        Assert.Equal("Crank ref", await Scalar(after, "SELECT name FROM reference_waveforms WHERE id='rw-1';"));
        Assert.Equal("rw-1", await Scalar(after, "SELECT reference_waveform_id FROM guided_tests WHERE id='gt-1';"));
        Assert.Equal(DBNull.Value, await Scalar(after, "SELECT scope_setup_json FROM guided_tests WHERE id='gt-1';"));
        Assert.Equal("captures/wf-1.wfm", await Scalar(after, "SELECT data_path FROM waveforms WHERE id='wf-1';"));
        // ADR-011: reference data_path is nullable now (no known-good capture yet).
        Assert.Equal(0L, await Scalar(after, "SELECT \"notnull\" FROM pragma_table_info('reference_waveforms') WHERE name='data_path';"));
        Assert.Equal(0L, await Scalar(after, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'scope_v6_%';"));
        Assert.Equal(0L, await Scalar(after, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task UpgradeFromV5_WithADanglingGuidedTestReference_StillMigrates()
    {
        // Version 1-5 guided_tests.reference_waveform_id had no foreign key, so a row could point at a reference
        // that does not exist. 006 adds the FK; with foreign keys ON that row must not abort the upgrade.
        await using var c = await _services.Connections.OpenAsync();
        await DowngradeScopeTablesToV5Async(c);
        const string ts = "2026-11-17T00:00:00Z";
        await Exec(c, $"INSERT INTO guided_tests VALUES ('gt-orphan','Old test',NULL,'Engine','d','p','[]','rw-missing','{ts}','{ts}');");

        var applied = await new SqliteMigrator(_services.Connections).MigrateAsync();

        Assert.Equal(new[] { 6 }, applied);
        await using var after = await _services.Connections.OpenAsync();
        Assert.Equal("Old test", await Scalar(after, "SELECT name FROM guided_tests WHERE id='gt-orphan';"));
        Assert.Equal(DBNull.Value, await Scalar(after, "SELECT reference_waveform_id FROM guided_tests WHERE id='gt-orphan';"));
        Assert.Equal(0L, await Scalar(after, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }
}
