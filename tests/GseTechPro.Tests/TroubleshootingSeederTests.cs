using GseTechPro.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GseTechPro.Tests;

/// <summary>
/// Phase 4 verification: seeder idempotence, FK constraints, and upsert semantics.
/// </summary>
public sealed class TroubleshootingSeederTests : IAsyncLifetime
{
    private string _dbPath = "";
    private DataServices _services = null!;

    public async Task InitializeAsync()
    {
        _dbPath = NewDatabasePath("troubleshooting-seeder");
        var options = new DatabaseOptions { DatabasePath = _dbPath };
        _services = new DataServices(options);
        await _services.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        DeleteDatabase(_dbPath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SeedTwice_IsIdempotent_AndPreservesSlugIds()
    {
        // First seed
        var result1 = await _services.TroubleshootingSeeder.SeedAsync();
        Assert.True(result1.UpsertedCount > 0, "First seed should insert rows");

        // Record counts after first seed
        var modelsAfterFirst = await CountTable("equipment_models");
        var warningsAfterFirst = await CountTable("safety_warnings");
        var symptomsAfterFirst = await CountTable("symptoms");
        var testPointsAfterFirst = await CountTable("test_points");
        var treesAfterFirst = await CountTable("troubleshooting_trees");
        var stepsAfterFirst = await CountTable("troubleshooting_steps");

        // Second seed (idempotence check)
        var result2 = await _services.TroubleshootingSeeder.SeedAsync();

        // Counts should be stable on second run
        Assert.Equal(modelsAfterFirst, await CountTable("equipment_models"));
        Assert.Equal(warningsAfterFirst, await CountTable("safety_warnings"));
        Assert.Equal(symptomsAfterFirst, await CountTable("symptoms"));
        Assert.Equal(testPointsAfterFirst, await CountTable("test_points"));
        Assert.Equal(treesAfterFirst, await CountTable("troubleshooting_trees"));
        Assert.Equal(stepsAfterFirst, await CountTable("troubleshooting_steps"));

        // Verify slug ids are preserved (not regenerated as GUIDs)
        var modelIds = await GetColumnValues("equipment_models", "id");
        foreach (var id in modelIds)
        {
            // Slug ids should not be 36-char GUID format
            Assert.NotEqual(36, id.Length);
            // Should match kebab-case slug pattern
            Assert.Matches("^[a-z0-9][a-z0-9-]*$", id);
        }

        // Verify specific known slug ids exist (from the seed data)
        var hasKnownModel = await RowExists("equipment_models", "id", "eqm-tug-600i");
        Assert.True(hasKnownModel, "Expected seeded model 'eqm-tug-600i' to exist");

        // Report actual counts
        System.Console.WriteLine($"Seeded counts: models={modelsAfterFirst}, warnings={warningsAfterFirst}, symptoms={symptomsAfterFirst}, test_points={testPointsAfterFirst}, trees={treesAfterFirst}, steps={stepsAfterFirst}");
    }

    [Fact]
    public async Task UpsertSemantics_CorrectsEditedRow_OnReseed()
    {
        // Seed first time
        await _services.TroubleshootingSeeder.SeedAsync();

        // Get original value of a seeded row (use name column which exists on symptoms)
        var originalName = await GetColumnValue("symptoms", "name", "id", "sym-engine-no-crank");
        Assert.NotNull(originalName);

        // Edit the row directly in DB (simulate corruption or manual edit)
        await ExecuteNonQuery(
            "UPDATE symptoms SET name = 'CORRUPTED TEST VALUE' WHERE id = 'sym-engine-no-crank';");

        // Verify it was corrupted
        var corruptedValue = await GetColumnValue("symptoms", "name", "id", "sym-engine-no-crank");
        Assert.Equal("CORRUPTED TEST VALUE", corruptedValue);

        // Re-seed should correct it back to original
        await _services.TroubleshootingSeeder.SeedAsync();

        var restoredValue = await GetColumnValue("symptoms", "name", "id", "sym-engine-no-crank");
        // Re-seed should restore corrupted row to original value
        Assert.Equal(originalName, restoredValue);
    }

    [Fact]
    public async Task Reseed_PreservesApprovedStatus_AndRestoresAuthoredContent()
    {
        const string treeId = "tree-tug66028-no-crank";
        const string approvalTimestamp = "2026-11-16T18:00:00.0000000Z";

        await _services.TroubleshootingSeeder.SeedAsync();
        var originalTitle = await GetColumnValue("troubleshooting_trees", "title", "id", treeId);
        Assert.NotNull(originalTitle);

        // Simulate lead approval plus a local content edit that the next import should correct.
        await ExecuteNonQuery(
            $"UPDATE troubleshooting_trees SET status = 'approved', title = 'LOCALLY EDITED TITLE', " +
            $"updated_at = '{approvalTimestamp}' WHERE id = '{treeId}';");

        await _services.TroubleshootingSeeder.SeedAsync();

        Assert.Equal("approved", await GetColumnValue("troubleshooting_trees", "status", "id", treeId));
        Assert.Equal(approvalTimestamp, await GetColumnValue("troubleshooting_trees", "updated_at", "id", treeId));
        Assert.Equal(originalTitle, await GetColumnValue("troubleshooting_trees", "title", "id", treeId));
    }

    [Fact]
    public async Task TechnicianCreatedRows_AreNotTouchedByReseed()
    {
        // Seed first time
        await _services.TroubleshootingSeeder.SeedAsync();

        // Create a technician-created symptom with a different id (not in seed data)
        var techSymptomId = "tech-custom-symptom-" + Guid.NewGuid().ToString("N")[..8];
        await ExecuteNonQuery(
            $"INSERT INTO symptoms(id, name, category, is_standard, created_at, updated_at) VALUES ('{techSymptomId}', 'Technician custom symptom', 'custom', 0, datetime('now'), datetime('now'));");

        // Verify it was created
        var existsBefore = await RowExists("symptoms", "id", techSymptomId);
        Assert.True(existsBefore, "Technician-created row should exist before re-seed");

        // Re-seed
        await _services.TroubleshootingSeeder.SeedAsync();

        // Technician row should still exist unchanged
        var existsAfter = await RowExists("symptoms", "id", techSymptomId);
        Assert.True(existsAfter, "Technician-created row should survive re-seed");

        var name = await GetColumnValue("symptoms", "name", "id", techSymptomId);
        // Technician row content should be unchanged
        Assert.Equal("Technician custom symptom", name);
    }

    [Fact]
    public async Task SafetyWarningIds_IsCompactJsonArrayString()
    {
        await _services.TroubleshootingSeeder.SeedAsync();

        // Find a test point with safety warnings (test_points has the safety_warning_ids column)
        var testPointsWithWarnings = await GetColumnValues(
            "test_points", "id", "safety_warning_ids IS NOT NULL AND safety_warning_ids != '[]'");

        if (testPointsWithWarnings.Count > 0)
        {
            var warningIdsJson = await GetColumnValue("test_points", "safety_warning_ids", "id", testPointsWithWarnings[0]);
            Assert.NotNull(warningIdsJson);

            // Should be valid JSON array
            System.Text.Json.JsonDocument? doc = null;
            try
            {
                doc = System.Text.Json.JsonDocument.Parse(warningIdsJson);
            }
            catch (System.Text.Json.JsonException)
            {
                Assert.Fail($"safety_warning_ids is not valid JSON: {warningIdsJson}");
            }

            Assert.NotNull(doc);
            Assert.Equal(System.Text.Json.JsonValueKind.Array, doc.RootElement.ValueKind);
            Assert.True(doc.RootElement.GetArrayLength() > 0, "Expected at least one warning id");

            // Should be compact (no whitespace)
            Assert.DoesNotContain(" ", warningIdsJson);
            Assert.DoesNotContain("\n", warningIdsJson);
        }
    }

    [Fact]
    public async Task ForeignKeys_AreSatisfied()
    {
        await _services.TroubleshootingSeeder.SeedAsync();

        // Enable FK enforcement and check
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();

        try
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA foreign_keys = ON;";
                await cmd.ExecuteNonQueryAsync();
            }

            // Check each FK relationship with correct table names
            var fkChecks = new[]
            {
                ("test_points.equipment_model_id", 
                 "SELECT COUNT(*) FROM test_points tp LEFT JOIN equipment_models em ON tp.equipment_model_id = em.id WHERE em.id IS NULL;"),
                ("troubleshooting_trees.symptom_id", 
                 "SELECT COUNT(*) FROM troubleshooting_trees t LEFT JOIN symptoms s ON t.symptom_id = s.id WHERE s.id IS NULL AND t.symptom_id IS NOT NULL;"),
                ("troubleshooting_steps.tree_id", 
                 "SELECT COUNT(*) FROM troubleshooting_steps st LEFT JOIN troubleshooting_trees t ON st.tree_id = t.id WHERE t.id IS NULL;"),
                ("troubleshooting_steps.test_point_id", 
                 "SELECT COUNT(*) FROM troubleshooting_steps st LEFT JOIN test_points tp ON st.test_point_id = tp.id WHERE tp.id IS NULL AND st.test_point_id IS NOT NULL;")
            };

            foreach (var (fkName, query) in fkChecks)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = query;
                var orphanCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                if (orphanCount != 0)
                {
                    Assert.Fail($"FK violation found for {fkName}: {orphanCount} orphaned rows");
                }
            }

            System.Console.WriteLine("All FK constraints satisfied with PRAGMA foreign_keys=ON");
        }
        finally
        {
            connection.Close();
        }
    }

    // --- Helper methods ---

    private async Task<long> CountTable(string tableName)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<List<string>> GetColumnValues(string table, string column, string? whereClause = null)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        var sql = $"SELECT {column} FROM {table}";
        if (whereClause != null)
            sql += $" WHERE {whereClause}";
        cmd.CommandText = sql;

        var values = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }
        return values;
    }

    private async Task<string?> GetColumnValue(string table, string column, string whereColumn, string whereValue)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM {table} WHERE {whereColumn} = @value;";
        cmd.Parameters.AddWithValue("@value", whereValue);

        var result = await cmd.ExecuteScalarAsync();
        return result?.ToString();
    }

    private async Task<bool> RowExists(string table, string column, string value)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM {table} WHERE {column} = @value LIMIT 1;";
        cmd.Parameters.AddWithValue("@value", value);

        var result = await cmd.ExecuteScalarAsync();
        return result != null;
    }

    private async Task ExecuteNonQuery(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static string NewDatabasePath(string prefix)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gse-tech-pro-tests");
        Directory.CreateDirectory(tempDir);
        return Path.Combine(tempDir, $"{prefix}-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string dbPath)
    {
        try
        {
            SqliteConnection.ClearPool(new SqliteConnection($"Data Source={dbPath}"));
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal");
            if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm");
        }
        catch (IOException) { }
    }
}
