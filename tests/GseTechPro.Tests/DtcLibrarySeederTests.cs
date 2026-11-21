using GseTechPro.Core.Enums;
using GseTechPro.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GseTechPro.Tests;

public class DtcLibrarySeederTests : IAsyncLifetime
{
    private string _dbPath = "";
    private DataServices? _services;

    public Task InitializeAsync()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gse-tech-pro-tests");
        Directory.CreateDirectory(tempDir);
        _dbPath = Path.Combine(tempDir, $"dtc-seed-test-{Guid.NewGuid():N}.db");

        var options = new DatabaseOptions { DatabasePath = _dbPath };
        _services = new DataServices(options);
        // Initialize without seeding (we'll seed manually in tests)
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            var wal = _dbPath + "-wal";
            var shm = _dbPath + "-shm";
            if (File.Exists(wal)) File.Delete(wal);
            if (File.Exists(shm)) File.Delete(shm);
        }
        catch { /* ignore cleanup errors */ }
    }

    [Fact]
    public async Task CleanImport_InsertsAll575Rows()
    {
        // Run migrations first
        var result = await _services!.Migrator.MigrateAsync();
        Assert.NotEmpty(result);

        // Seed the DTC library
        var seedResult = await _services.DtcLibrarySeeder.SeedAsync();

        // Verify counts
        Assert.Equal(575, seedResult.InsertedCount);
        Assert.Equal(0, seedResult.AlreadyPresentCount);
        Assert.Equal(575, seedResult.SourceRowCount);

        // Verify database has 575 rows
        var connection = await _services.Connections.OpenAsync();
        try
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM dtc_library;";
                var count = await cmd.ExecuteScalarAsync();
                Assert.Equal(575, Convert.ToInt32(count));
            }

            // Verify 575 distinct (protocol, code) pairs
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(DISTINCT protocol || '|' || code) FROM dtc_library;";
                var count = await cmd.ExecuteScalarAsync();
                Assert.Equal(575, Convert.ToInt32(count));
            }

            // Verify protocol counts: J1939=214, J1708=124, OBD2=202
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM dtc_library WHERE protocol='J1939';";
                var count = await cmd.ExecuteScalarAsync();
                Assert.Equal(214, Convert.ToInt32(count));
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM dtc_library WHERE protocol='J1708';";
                var count = await cmd.ExecuteScalarAsync();
                Assert.Equal(124, Convert.ToInt32(count));
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM dtc_library WHERE protocol='OBD2';";
                var count = await cmd.ExecuteScalarAsync();
                Assert.Equal(202, Convert.ToInt32(count));
            }

            // Phase 10C.3: PCS TCM codes (data/dtc/pcs.json) — manufacturer rows, each cited,
            // proprietary J1939 SPNs only, no FMI (the PCS guides give none).
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM dtc_library WHERE protocol='manufacturer' AND code LIKE 'PCS %';";
                Assert.Equal(35, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT COUNT(*) FROM dtc_library WHERE protocol='manufacturer' AND (
                        source_citation IS NULL OR source_citation NOT LIKE '%PCS%p.%' OR fmi IS NOT NULL
                        OR (spn IS NOT NULL AND (spn < 520192 OR spn > 524287)));
                    """;
                Assert.Equal(0, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(DISTINCT spn), COUNT(spn) FROM dtc_library WHERE protocol='manufacturer';";
                await using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(27, reader.GetInt32(1));
                Assert.Equal(reader.GetInt32(1), reader.GetInt32(0)); // one PCS code per SPN
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT spn FROM dtc_library WHERE protocol='manufacturer' AND code='PCS 72';";
                Assert.Equal(522742, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
            }

            // Verify all severity values are null (embedded library has no severity)
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM dtc_library WHERE severity IS NOT NULL;";
                var count = await cmd.ExecuteScalarAsync();
                Assert.Equal(0, Convert.ToInt32(count));
            }
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task IdempotentImport_DoesNotOverwriteExistingRows()
    {
        // Run migrations first
        var result = await _services!.Migrator.MigrateAsync();
        Assert.NotEmpty(result);

        // Seed the DTC library initially
        var seedResult1 = await _services.DtcLibrarySeeder.SeedAsync();
        Assert.Equal(575, seedResult1.InsertedCount);

        // Capture an existing row's id and timestamps, then edit its description
        var connection = await _services.Connections.OpenAsync();
        try
        {
            string? editedId = null;
            string? originalCreatedAt = null;
            string? originalUpdatedAt = null;

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT id, created_at, updated_at FROM dtc_library LIMIT 1;";
                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    editedId = reader.GetString(0);
                    originalCreatedAt = reader.GetString(1);
                    originalUpdatedAt = reader.GetString(2);
                }
            }

            Assert.NotNull(editedId);

            // Edit the description to a sentinel value
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE dtc_library SET description='EDITED SENTINEL' WHERE id=$id;";
                cmd.Parameters.AddWithValue("$id", editedId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Run seed again - should report 0 inserted, 575 already present
            var seedResult2 = await _services.DtcLibrarySeeder.SeedAsync();
            Assert.Equal(0, seedResult2.InsertedCount);
            Assert.Equal(575, seedResult2.AlreadyPresentCount);

            // Verify the edited row is unchanged (no overwrite)
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT id, description, created_at, updated_at FROM dtc_library WHERE id=$id;";
                cmd.Parameters.AddWithValue("$id", editedId);
                await using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(editedId, reader.GetString(0));
                Assert.Equal("EDITED SENTINEL", reader.GetString(1));
                Assert.Equal(originalCreatedAt, reader.GetString(2));
                Assert.Equal(originalUpdatedAt, reader.GetString(3));
            }

            // Verify no duplicates were created (still 575 rows)
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM dtc_library;";
                var count = await cmd.ExecuteScalarAsync();
                Assert.Equal(575, Convert.ToInt32(count));
            }
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }
}
