using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using GseTechPro.Data;
using GseTechPro.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GseTechPro.Tests;

public class DataLayerTests : IAsyncLifetime
{
    private string _dbPath = "";
    private DataServices? _services;

    public Task InitializeAsync()
    {
        // Create a unique temp DB for each test run
        var tempDir = Path.Combine(Path.GetTempPath(), "gse-tech-pro-tests");
        Directory.CreateDirectory(tempDir);
        _dbPath = Path.Combine(tempDir, $"test-{Guid.NewGuid():N}.db");

        var options = new DatabaseOptions { DatabasePath = _dbPath };
        _services = new DataServices(options);
        
        // Initialize the database (run migrations + seed)
        _services.InitializeAsync().Wait();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        // Clean up temp DB file
        try
        {
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
            
            // Also delete WAL and SHM files
            var wal = _dbPath + "-wal";
            var shm = _dbPath + "-shm";
            if (File.Exists(wal)) File.Delete(wal);
            if (File.Exists(shm)) File.Delete(shm);
        }
        catch { /* ignore cleanup errors */ }
    }

    [Fact]
    public async Task MigrationIdempotency_RunTwice_NoError()
    {
        // Run migration again - should be idempotent
        var result = await _services!.Migrator.MigrateAsync();
        
        // No new migrations applied (already at latest)
        Assert.Empty(result);
    }

    [Fact]
    public async Task AssetRepository_CRUD()
    {
        var repo = _services!.Assets;

        // Create
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "TEST-001",
            Name = "Test Tug",
            Status = AssetStatus.InService,
        };
        await repo.AddAsync(asset);

        // Read by ID
        var fetched = await repo.GetByIdAsync(asset.Id);
        Assert.NotNull(fetched);
        Assert.Equal("TEST-001", fetched.AssetNumber);
        Assert.Equal("Test Tug", fetched.Name);

        // Update - create new object with updated name (Name is init-only)
        var updatedAsset = new Asset
        {
            Id = fetched.Id,
            AssetNumber = fetched.AssetNumber,
            Name = "Updated Test Tug",
            Status = fetched.Status,
        };
        var updated = await repo.UpdateAsync(updatedAsset);
        Assert.True(updated);

        var afterUpdate = await repo.GetByIdAsync(asset.Id);
        Assert.NotNull(afterUpdate);
        Assert.Equal("Updated Test Tug", afterUpdate!.Name);

        // Soft delete
        var deleted = await repo.DeleteAsync(asset.Id);
        Assert.True(deleted);

        // GetById should return null for soft-deleted
        var afterDelete = await repo.GetByIdAsync(asset.Id);
        Assert.Null(afterDelete);
    }

    [Fact]
    public async Task WorkOrderRepository_CRUD()
    {
        var woRepo = _services!.WorkOrders;
        var assetRepo = _services.Assets;

        // Create an asset first (FK constraint)
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "WO-TEST-001",
            Name = "Work Order Test Asset",
        };
        await assetRepo.AddAsync(asset);

        // Create work order
        var wo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Open,
            ComplaintSummary = "Engine overheating",
        };
        await woRepo.AddAsync(wo);

        // Read by ID
        var fetched = await woRepo.GetByIdAsync(wo.Id);
        Assert.NotNull(fetched);
        Assert.Equal("Engine overheating", fetched.ComplaintSummary);
        Assert.Equal(WorkOrderStatus.Open, fetched.Status);

        // Update status to closed - create new object (Status and RepairSummary are init-only)
        var updatedWo = new WorkOrder
        {
            Id = fetched.Id,
            AssetId = fetched.AssetId,
            Status = WorkOrderStatus.Closed,
            ComplaintSummary = fetched.ComplaintSummary,
            RepairSummary = "Replaced thermostat",
        };
        var updated = await woRepo.UpdateAsync(updatedWo);
        Assert.True(updated);

        var afterUpdate = await woRepo.GetByIdAsync(wo.Id);
        Assert.NotNull(afterUpdate);
        Assert.Equal(WorkOrderStatus.Closed, afterUpdate!.Status);
    }

    [Fact]
    public async Task ReadingRepository_CRUD()
    {
        var readingRepo = _services!.Readings;
        var woRepo = _services.WorkOrders;
        var assetRepo = _services.Assets;

        // Create asset and work order (FK constraints)
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "READING-TEST-001",
        };
        await assetRepo.AddAsync(asset);

        var wo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
        };
        await woRepo.AddAsync(wo);

        // Create reading with enum values
        var reading = new Reading
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = wo.Id,
            ReadingType = ReadingType.HydraulicPressure,
            ValueCanonical = 206842.713,
            UnitCanonical = "Pa",
            EnteredValue = 30.0,
            EnteredUnit = "psi",
            PassFail = PassFail.Pass,
            Source = ReadingSource.Manual,
        };
        await readingRepo.AddAsync(reading);

        // Read back and verify enum round-trip
        var fetched = await readingRepo.GetByIdAsync(reading.Id);
        Assert.NotNull(fetched);
        Assert.Equal(ReadingType.HydraulicPressure, fetched.ReadingType);
        Assert.Equal(PassFail.Pass, fetched.PassFail);
        Assert.Equal(ReadingSource.Manual, fetched.Source);
        Assert.Equal(206842.713, fetched.ValueCanonical, precision: 6);
    }

    [Fact]
    public async Task EnumRoundTrip_DtcStatus()
    {
        var dtcRepo = _services!.Dtcs;
        var woRepo = _services.WorkOrders;
        var assetRepo = _services.Assets;

        // Create asset and work order (FK constraints)
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "DTC-TEST-001",
        };
        await assetRepo.AddAsync(asset);

        var wo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
        };
        await woRepo.AddAsync(wo);

        // Create DTC with Active status
        var dtc = new Dtc
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = wo.Id,
            Code = "SPN 94 FMI 3",
            Protocol = ProtocolType.J1939,
            Spn = 94,
            Fmi = 3,
            Status = DtcStatus.Active,
        };
        await dtcRepo.AddAsync(dtc);

        // Read back and verify enum round-trip
        var fetched = await dtcRepo.GetByIdAsync(dtc.Id);
        Assert.NotNull(fetched);
        Assert.Equal(DtcStatus.Active, fetched.Status);
    }

    [Fact]
    public async Task Fts5Trigger_InsertAndQuery()
    {
        // Insert a manual chunk and verify it's searchable via FTS5
        var connection = await _services!.Connections.OpenAsync();
        
        try
        {
            // First, create a manual (FK constraint)
            var manualId = Guid.NewGuid().ToString("D");
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO manuals (id, title, file_path, file_hash, imported_at, created_at, updated_at)
                    VALUES ($id, 'Test Manual', '/tmp/test.pdf', 'hash123', $now, $now, $now);
                    """;
                cmd.Parameters.AddWithValue("$id", manualId);
                cmd.Parameters.AddWithValue("$now", Clock.UtcNow);
                await cmd.ExecuteNonQueryAsync();
            }

            // Insert a chunk with searchable text
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO manual_chunks (guid, manual_id, page_start, page_end, chunk_kind, heading, chunk_text, created_at, updated_at)
                    VALUES ($guid, $manualId, 1, 1, 'text', 'Hydraulic Pressure Test', 
                            'To test hydraulic pressure, connect the gauge to port P1 and measure at full load.',
                            $now, $now);
                    """;
                cmd.Parameters.AddWithValue("$guid", Guid.NewGuid().ToString("D"));
                cmd.Parameters.AddWithValue("$manualId", manualId);
                cmd.Parameters.AddWithValue("$now", Clock.UtcNow);
                await cmd.ExecuteNonQueryAsync();
            }

            // Query via FTS5 - should find the chunk
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT COUNT(*) FROM manual_chunks_fts WHERE manual_chunks_fts MATCH 'hydraulic pressure';
                    """;
                var count = await cmd.ExecuteScalarAsync();
                Assert.Equal(1, Convert.ToInt32(count));
            }

            // Query with different term - should not find it
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT COUNT(*) FROM manual_chunks_fts WHERE manual_chunks_fts MATCH 'transmission';
                    """;
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
    public async Task Fts5Trigger_UpdateKeepsSync()
    {
        var connection = await _services!.Connections.OpenAsync();
        
        try
        {
            // Create manual and chunk
            var manualId = Guid.NewGuid().ToString("D");
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO manuals (id, title, file_path, file_hash, imported_at, created_at, updated_at)
                    VALUES ($id, 'Test Manual 2', '/tmp/test2.pdf', 'hash456', $now, $now, $now);
                    """;
                cmd.Parameters.AddWithValue("$id", manualId);
                cmd.Parameters.AddWithValue("$now", Clock.UtcNow);
                await cmd.ExecuteNonQueryAsync();
            }

            var chunkGuid = Guid.NewGuid().ToString("D");
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO manual_chunks (guid, manual_id, page_start, page_end, chunk_kind, heading, chunk_text, created_at, updated_at)
                    VALUES ($guid, $manualId, 1, 1, 'text', 'Original Heading', 'Original text about engines.', $now, $now);
                    """;
                cmd.Parameters.AddWithValue("$guid", chunkGuid);
                cmd.Parameters.AddWithValue("$manualId", manualId);
                cmd.Parameters.AddWithValue("$now", Clock.UtcNow);
                await cmd.ExecuteNonQueryAsync();
            }

            // Verify original is searchable
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM manual_chunks_fts WHERE manual_chunks_fts MATCH 'engines';";
                var count = await cmd.ExecuteScalarAsync();
                Assert.Equal(1, Convert.ToInt32(count));
            }

            // Update the chunk text
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    UPDATE manual_chunks SET chunk_text = 'Updated text about hydraulics.', heading = 'New Heading' WHERE guid = $guid;
                    """;
                cmd.Parameters.AddWithValue("$guid", chunkGuid);
                await cmd.ExecuteNonQueryAsync();
            }

            // Old term should no longer match
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM manual_chunks_fts WHERE manual_chunks_fts MATCH 'engines';";
                var count = await cmd.ExecuteScalarAsync();
                Assert.Equal(0, Convert.ToInt32(count));
            }

            // New term should match
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM manual_chunks_fts WHERE manual_chunks_fts MATCH 'hydraulics';";
                var count = await cmd.ExecuteScalarAsync();
                Assert.Equal(1, Convert.ToInt32(count));
            }
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task DatabaseBackup_ProducesValidFile()
    {
        var backupPath = Path.Combine(Path.GetTempPath(), $"gse-backup-{Guid.NewGuid():N}.db.bak");
        
        try
        {
            await _services!.Backup.BackupAsync(backupPath);

            // Backup file should exist and be non-empty
            Assert.True(File.Exists(backupPath));
            var fileInfo = new FileInfo(backupPath);
            Assert.True(fileInfo.Length > 0, "Backup file should not be empty");

            // Verify it's a valid SQLite database by opening it
            using (var conn = new SqliteConnection($"Data Source={backupPath}"))
            {
                await conn.OpenAsync();
                
                // Check that schema_migrations table exists in backup
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_migrations';";
                    var count = await cmd.ExecuteScalarAsync();
                    Assert.Equal(1, Convert.ToInt32(count));
                }
            }
        }
        finally
        {
            // Best-effort cleanup; ignore if file is still locked (Windows)
            try
            {
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
            }
            catch (IOException)
            {
                // File still in use by another process - leave it for OS cleanup
            }
        }
    }

    [Fact]
    public async Task FindAsync_ByColumn()
    {
        var repo = _services!.Assets;

        // Create two assets with different numbers
        var asset1 = new Asset { Id = Guid.NewGuid().ToString("D"), AssetNumber = "FIND-001", Name = "Find Test 1" };
        var asset2 = new Asset { Id = Guid.NewGuid().ToString("D"), AssetNumber = "FIND-002", Name = "Find Test 2" };
        await repo.AddAsync(asset1);
        await repo.AddAsync(asset2);

        // Find by asset_number column
        var found = await repo.FindAsync("asset_number", "FIND-001");
        Assert.Single(found);
        Assert.Equal("FIND-001", found[0].AssetNumber);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllLiveRows()
    {
        var repo = _services!.Assets;

        // Create multiple assets
        for (int i = 0; i < 3; i++)
        {
            await repo.AddAsync(new Asset
            {
                Id = Guid.NewGuid().ToString("D"),
                AssetNumber = $"ALL-{i:D3}",
                Name = $"All Test {i}"
            });
        }

        var all = await repo.GetAllAsync();
        
        // Should have at least our 3 test assets (plus any seeded)
        Assert.True(all.Count >= 3);
    }
}
