using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using GseTechPro.Data;
using GseTechPro.Data.Migrations;
using GseTechPro.Data.Repositories;
using GseTechPro.Data.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GseTechPro.Tests;

public sealed class RepairHistoryDataTests : IAsyncLifetime
{
    private string _dbPath = "";
    private DataServices? _services;

    public Task InitializeAsync()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gse-tech-pro-tests");
        Directory.CreateDirectory(tempDir);
        _dbPath = Path.Combine(tempDir, $"repairhist-{Guid.NewGuid():N}.db");

        var options = new DatabaseOptions { DatabasePath = _dbPath };
        _services = new DataServices(options);
        _services.InitializeAsync().Wait();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        try
        {
            SqliteConnection.ClearPool(new SqliteConnection($"Data Source={_dbPath}"));
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
            if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
        }
        catch { /* ignore cleanup errors */ }
    }

    // ── Migration 005 tests ────────────────────────────────────────────────

    [Fact]
    public async Task FreshDatabase_ReachesVersionSix()
    {
        Assert.Equal(6, SqliteMigrator.LatestVersion);

        await using var connection = await _services!.Connections.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(6, version);
    }

    [Fact]
    public async Task Migration005_AddsWorkOrderNumberColumnAndIndexes()
    {
        await using var connection = await _services!.Connections.OpenAsync();

        // Check that the number column exists on work_orders
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*) FROM pragma_table_info('work_orders') WHERE name = 'number';
                """;
            var count = Convert.ToInt32(await command.ExecuteScalarAsync());
            Assert.Equal(1, count);
        }

        // Check that idx_wo_number exists (partial unique index)
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='idx_wo_number';";
            var count = Convert.ToInt32(await command.ExecuteScalarAsync());
            Assert.Equal(1, count);
        }

        // Check that idx_wo_closed exists
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='idx_wo_closed';";
            var count = Convert.ToInt32(await command.ExecuteScalarAsync());
            Assert.Equal(1, count);
        }
    }

    [Fact]
    public async Task Migration005_Idempotent_RunningTwiceAppliesNothing()
    {
        var result = await _services!.Migrator.MigrateAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task Migration005_ExistingRowsPreservedAndNumberColumnAvailable()
    {
        // Verify that work orders created before migration 5 (with null numbers)
        // are still readable and the new number column is available.

        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "UPGRADE-TEST",
            Name = "Upgrade Test Asset"
        };
        await _services!.Assets.AddAsync(asset);

        // Create a work order without a number (simulating pre-migration data)
        var wo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Open,
            ComplaintSummary = "Pre-numbering work order"
        };
        await _services.WorkOrders.AddAsync(wo);

        // Verify the pre-migration work order is still readable with null number
        var fetchedWo = await _services.WorkOrders.GetByIdAsync(wo.Id);
        Assert.NotNull(fetchedWo);
        Assert.Equal("Pre-numbering work order", fetchedWo.ComplaintSummary);
        Assert.Null(fetchedWo.Number);

        // Verify the number column is now available and writable
        var updatedWo = new WorkOrder
        {
            Id = fetchedWo.Id,
            AssetId = fetchedWo.AssetId,
            Status = fetchedWo.Status,
            ComplaintSummary = fetchedWo.ComplaintSummary,
            Number = "000999"
        };
        await _services.WorkOrders.UpdateAsync(updatedWo);

        var finalWo = await _services.WorkOrders.GetByIdAsync(wo.Id);
        Assert.NotNull(finalWo);
        Assert.Equal("000999", finalWo.Number);
    }

    // ── Repository round-trip tests ────────────────────────────────────────

    [Fact]
    public async Task DiagnosisRepository_AddGetByIdFindAsync_RoundTrip()
    {
        var repo = new DiagnosisRepository(_services!.Connections);

        // Create work order first (FK constraint)
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "DIAG-TEST",
            Name = "Diagnosis Test"
        };
        await _services.Assets.AddAsync(asset);

        var wo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Open
        };
        await _services.WorkOrders.AddAsync(wo);

        var diagnosis = new Diagnosis
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = wo.Id,
            Statement = "Hydraulic pump worn out",
            System = "hydraulics",
            Confidence = 0.95,
            SourcesJson = "[\"manual:page42\"]"
        };

        await repo.AddAsync(diagnosis);

        // GetById
        var fetched = await repo.GetByIdAsync(diagnosis.Id);
        Assert.NotNull(fetched);
        Assert.Equal("Hydraulic pump worn out", fetched.Statement);
        Assert.Equal("hydraulics", fetched.System);
        Assert.Equal(0.95, fetched.Confidence);

        // FindAsync by work_order_id
        var found = await repo.FindAsync("work_order_id", wo.Id);
        Assert.Single(found);
        Assert.Equal(diagnosis.Id, found[0].Id);
    }

    [Fact]
    public async Task ComebackRepository_AddGetByIdFindAsync_RoundTrip()
    {
        var repo = new ComebackRepository(_services!.Connections);

        // Create work orders first (FK constraints)
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "CB-REPO-TEST",
            Name = "Comeback Repo Test"
        };
        await _services.Assets.AddAsync(asset);

        var originalWo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed
        };
        await _services.WorkOrders.AddAsync(originalWo);

        var comebackWo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Open
        };
        await _services.WorkOrders.AddAsync(comebackWo);

        var comeback = new Comeback
        {
            Id = Guid.NewGuid().ToString("D"),
            OriginalWorkOrderId = originalWo.Id,
            ComebackWorkOrderId = comebackWo.Id,
            MatchReason = "Shared system 'hydraulics'.",
            WindowDays = 14,
            WindowHours = 200.0,
            Auto = true,
            Confirmed = false
        };

        await repo.AddAsync(comeback);

        var fetched = await repo.GetByIdAsync(comeback.Id);
        Assert.NotNull(fetched);
        Assert.Equal(originalWo.Id, fetched.OriginalWorkOrderId);
        Assert.Equal(comebackWo.Id, fetched.ComebackWorkOrderId);
        Assert.False(fetched.Confirmed);
    }

    [Fact]
    public async Task RecommendationRepository_AddGetByIdFindAsync_RoundTrip()
    {
        var repo = new RecommendationRepository(_services!.Connections);

        // Create work order and repair first (FK constraints)
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "RECO-TEST",
            Name = "Recommendation Test"
        };
        await _services.Assets.AddAsync(asset);

        var wo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Open
        };
        await _services.WorkOrders.AddAsync(wo);

        var repair = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = wo.Id,
            Description = "Replaced pump",
            System = "hydraulics"
        };
        await _services.Repairs.AddAsync(repair);

        var recommendation = new Recommendation
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = wo.Id,
            CandidateRepairId = repair.Id,
            SimilarityScore = 0.85,
            SuccessRate = 0.90,
            EvidenceJson = "{\"fixed\":18,\"total\":20}",
            Rank = 1,
            Presented = true,
            Accepted = false
        };

        await repo.AddAsync(recommendation);

        var fetched = await repo.GetByIdAsync(recommendation.Id);
        Assert.NotNull(fetched);
        Assert.Equal(0.85, fetched.SimilarityScore);
        Assert.Equal(0.90, fetched.SuccessRate);
        Assert.True(fetched.Presented);
    }

    // ── Soft-delete tests ──────────────────────────────────────────────────

    [Fact]
    public async Task DiagnosisRepository_SoftDeleteWorks()
    {
        var repo = new DiagnosisRepository(_services!.Connections);

        // Create work order first (FK constraint)
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "DIAG-DEL-TEST",
            Name = "Diagnosis Delete Test"
        };
        await _services.Assets.AddAsync(asset);

        var wo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Open
        };
        await _services.WorkOrders.AddAsync(wo);

        var diagnosis = new Diagnosis
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = wo.Id,
            Statement = "To be deleted"
        };
        await repo.AddAsync(diagnosis);

        // Verify it exists
        Assert.NotNull(await repo.GetByIdAsync(diagnosis.Id));

        // Soft delete
        var deleted = await repo.DeleteAsync(diagnosis.Id);
        Assert.True(deleted);

        // GetById should return null for soft-deleted
        var afterDelete = await repo.GetByIdAsync(diagnosis.Id);
        Assert.Null(afterDelete);
    }

    [Fact]
    public async Task ComebackRepository_DeleteThrowsNotSupportedException()
    {
        var repo = new ComebackRepository(_services!.Connections);

        // comebacks table has no deleted_at column, so DeleteAsync should throw
        await Assert.ThrowsAsync<NotSupportedException>(() => repo.DeleteAsync("any-id"));
    }

    [Fact]
    public async Task RecommendationRepository_DeleteThrowsNotSupportedException()
    {
        var repo = new RecommendationRepository(_services!.Connections);

        // recommendations table has no deleted_at column, so DeleteAsync should throw
        await Assert.ThrowsAsync<NotSupportedException>(() => repo.DeleteAsync("any-id"));
    }

    // ── WorkOrder.Number round-trip tests ──────────────────────────────────

    [Fact]
    public async Task WorkOrder_NumberRoundTrip_SetValue()
    {
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "NUM-TEST",
            Name = "Number Test"
        };
        await _services!.Assets.AddAsync(asset);

        var wo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Open,
            Number = "000001"
        };
        await _services.WorkOrders.AddAsync(wo);

        var fetched = await _services.WorkOrders.GetByIdAsync(wo.Id);
        Assert.NotNull(fetched);
        Assert.Equal("000001", fetched.Number);
    }

    [Fact]
    public async Task WorkOrder_NumberRoundTrip_NullValue()
    {
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "NUM-NULL-TEST",
            Name = "Null Number Test"
        };
        await _services!.Assets.AddAsync(asset);

        var wo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Open,
            Number = null
        };
        await _services.WorkOrders.AddAsync(wo);

        var fetched = await _services.WorkOrders.GetByIdAsync(wo.Id);
        Assert.NotNull(fetched);
        Assert.Null(fetched.Number);
    }

    // ── AssignNextNumberAsync tests ────────────────────────────────────────

    [Fact]
    public async Task AssignNextNumber_StartsAt000001()
    {
        var number = await _services!.WorkOrders.AssignNextNumberAsync();
        Assert.Equal("000001", number);
    }

    [Fact]
    public async Task AssignNextNumber_IncrementsSequentially()
    {
        // First call returns 000001
        var first = await _services!.WorkOrders.AssignNextNumberAsync();
        Assert.Equal("000001", first);

        // Create a work order with that number
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "INC-TEST",
            Name = "Increment Test"
        };
        await _services.Assets.AddAsync(asset);

        var wo1 = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Open,
            Number = first
        };
        await _services.WorkOrders.AddAsync(wo1);

        // Second call returns 000002
        var second = await _services.WorkOrders.AssignNextNumberAsync();
        Assert.Equal("000002", second);
    }

    [Fact]
    public async Task AssignNextNumber_IgnoresNullAndNonNumericValues()
    {
        // Create work orders with null and non-numeric numbers
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "IGN-TEST",
            Name = "Ignore Test"
        };
        await _services!.Assets.AddAsync(asset);

        // Work order with null number
        var woNull = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Open,
            Number = null
        };
        await _services.WorkOrders.AddAsync(woNull);

        // Work order with non-numeric number (legacy)
        var woLegacy = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Open,
            Number = "LEGACY-123"
        };
        await _services.WorkOrders.AddAsync(woLegacy);

        // Next number should still be 000001 (ignores null and non-numeric)
        var next = await _services.WorkOrders.AssignNextNumberAsync();
        Assert.Equal("000001", next);
    }

    [Fact]
    public async Task AssignNextNumber_UniqueConstraintEnforced()
    {
        // Create a work order with number 000001
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "UNIQ-TEST",
            Name = "Unique Test"
        };
        await _services!.Assets.AddAsync(asset);

        var wo1 = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Open,
            Number = "000001"
        };
        await _services.WorkOrders.AddAsync(wo1);

        // Try to create another work order with the same number - should fail
        var wo2 = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Open,
            Number = "000001"
        };

        await Assert.ThrowsAsync<SqliteException>(() => _services.WorkOrders.AddAsync(wo2));
    }

    // ── Comeback detection tests ───────────────────────────────────────────

    [Fact]
    public async Task ComebackDetection_SameAssetSharedSystemWithinWindow_Detected()
    {
        var service = new ComebackDetectionService(_services!.Connections);

        // Create asset
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "CB-TEST",
            Name = "Comeback Test"
        };
        await _services.Assets.AddAsync(asset);

        // Original work order (closed 10 days ago)
        var originalWo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            ClosedAt = DateTimeOffset.UtcNow.AddDays(-10).ToString("o")
        };
        await _services.WorkOrders.AddAsync(originalWo);

        // Repair on original WO with system "hydraulics"
        var originalRepair = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = originalWo.Id,
            Description = "Replaced hydraulic pump",
            System = "hydraulics"
        };
        await _services.Repairs.AddAsync(originalRepair);

        // Outcome for original WO
        var outcome = new Outcome
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = originalWo.Id,
            Result = OutcomeResult.Fixed,
            ClosedAt = originalWo.ClosedAt!
        };
        await _services.Outcomes.AddAsync(outcome);

        // Closing work order (closed today) with same system
        var closingWo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            ClosedAt = DateTimeOffset.UtcNow.ToString("o")
        };
        await _services.WorkOrders.AddAsync(closingWo);

        var closingRepair = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = closingWo.Id,
            Description = "Hydraulic pump failed again",
            System = "hydraulics"
        };
        await _services.Repairs.AddAsync(closingRepair);

        // Detect comebacks for the closing WO
        var matches = await service.DetectAsync(closingWo.Id);
        Assert.Single(matches);
        Assert.Equal(originalWo.Id, matches[0].OriginalWorkOrderId);
        Assert.Equal(closingWo.Id, matches[0].ComebackWorkOrderId);
        Assert.Contains("hydraulics", matches[0].MatchReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ComebackDetection_NoSharedSignal_NotDetected()
    {
        var service = new ComebackDetectionService(_services!.Connections);

        // Create asset
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "CB-NO-SIGNAL",
            Name = "No Signal Test"
        };
        await _services.Assets.AddAsync(asset);

        // Original work order with system "electrical"
        var originalWo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            ClosedAt = DateTimeOffset.UtcNow.AddDays(-5).ToString("o")
        };
        await _services.WorkOrders.AddAsync(originalWo);

        var originalRepair = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = originalWo.Id,
            Description = "Fixed wiring",
            System = "electrical"
        };
        await _services.Repairs.AddAsync(originalRepair);

        // Closing work order with different system "hydraulics"
        var closingWo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            ClosedAt = DateTimeOffset.UtcNow.ToString("o")
        };
        await _services.WorkOrders.AddAsync(closingWo);

        var closingRepair = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = closingWo.Id,
            Description = "Hydraulic leak",
            System = "hydraulics"
        };
        await _services.Repairs.AddAsync(closingRepair);

        // No shared signal → no comeback detected
        var matches = await service.DetectAsync(closingWo.Id);
        Assert.Empty(matches);
    }

    [Fact]
    public async Task ComebackDetection_OutsideDayWindow_NotDetected()
    {
        var service = new ComebackDetectionService(_services!.Connections);

        // Create asset
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "CB-DAY-WINDOW",
            Name = "Day Window Test"
        };
        await _services.Assets.AddAsync(asset);

        // Original work order closed 30 days ago (outside default 14-day window)
        var originalWo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            ClosedAt = DateTimeOffset.UtcNow.AddDays(-30).ToString("o")
        };
        await _services.WorkOrders.AddAsync(originalWo);

        var originalRepair = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = originalWo.Id,
            Description = "Replaced pump",
            System = "hydraulics"
        };
        await _services.Repairs.AddAsync(originalRepair);

        // Closing work order today with same system
        var closingWo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            ClosedAt = DateTimeOffset.UtcNow.ToString("o")
        };
        await _services.WorkOrders.AddAsync(closingWo);

        var closingRepair = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = closingWo.Id,
            Description = "Pump failed again",
            System = "hydraulics"
        };
        await _services.Repairs.AddAsync(closingRepair);

        // Outside day window → no comeback detected
        var matches = await service.DetectAsync(closingWo.Id);
        Assert.Empty(matches);
    }

    [Fact]
    public async Task ComebackDetection_ClosingWorkOrderNeverAMatch()
    {
        var service = new ComebackDetectionService(_services!.Connections);

        // Create asset
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "CB-SELF",
            Name = "Self Match Test"
        };
        await _services.Assets.AddAsync(asset);

        // Only one work order on this asset - it should not match itself
        var wo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            ClosedAt = DateTimeOffset.UtcNow.ToString("o")
        };
        await _services.WorkOrders.AddAsync(wo);

        var repair = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = wo.Id,
            Description = "Replaced pump",
            System = "hydraulics"
        };
        await _services.Repairs.AddAsync(repair);

        // Detect should return empty (no prior work orders)
        var matches = await service.DetectAsync(wo.Id);
        Assert.Empty(matches);
    }

    [Fact]
    public async Task ComebackDetection_DaysOnlyBranch()
    {
        var service = new ComebackDetectionService(_services!.Connections);

        // Create asset with no hour meter (days-only branch)
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "CB-DAYS-ONLY",
            Name = "Days Only Test"
        };
        await _services.Assets.AddAsync(asset);

        // Original work order (no hours_at_open)
        var originalWo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            ClosedAt = DateTimeOffset.UtcNow.AddDays(-7).ToString("o")
        };
        await _services.WorkOrders.AddAsync(originalWo);

        var originalRepair = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = originalWo.Id,
            Description = "Replaced pump",
            System = "hydraulics"
        };
        await _services.Repairs.AddAsync(originalRepair);

        // Closing work order (no hours_at_open)
        var closingWo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            ClosedAt = DateTimeOffset.UtcNow.ToString("o")
        };
        await _services.WorkOrders.AddAsync(closingWo);

        var closingRepair = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = closingWo.Id,
            Description = "Pump failed again",
            System = "hydraulics"
        };
        await _services.Repairs.AddAsync(closingRepair);

        // Days-only branch should detect the comeback
        var matches = await service.DetectAsync(closingWo.Id);
        Assert.Single(matches);
    }

    [Fact]
    public async Task ComebackDetection_HoursOnlyBranch()
    {
        var service = new ComebackDetectionService(_services!.Connections);

        // Create asset with hour meter (hours-only branch)
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "CB-HOURS-ONLY",
            Name = "Hours Only Test",
            HourMeter = 950.0
        };
        await _services.Assets.AddAsync(asset);

        // Original work order (has closed_at so it's returned by ReadPriorClosedWorkOrdersAsync,
        // but we test the hours-only branch by having no day window constraint)
        var originalWo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            ClosedAt = DateTimeOffset.UtcNow.AddDays(-7).ToString("o"),
            HoursAtOpen = 800.0
        };
        await _services.WorkOrders.AddAsync(originalWo);

        var originalRepair = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = originalWo.Id,
            Description = "Replaced pump",
            System = "hydraulics"
        };
        await _services.Repairs.AddAsync(originalRepair);

        // Closing work order (no closed_at → days-only branch not taken; hours only)
        var closingWo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            HoursAtOpen = 900.0
        };
        await _services.WorkOrders.AddAsync(closingWo);

        var closingRepair = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = closingWo.Id,
            Description = "Pump failed again",
            System = "hydraulics"
        };
        await _services.Repairs.AddAsync(closingRepair);

        // Hours-only branch: asset at 950, original at 800 → 150 elapsed (within default 200-hour window)
        var matches = await service.DetectAsync(closingWo.Id);
        Assert.Single(matches);
    }

    // ── Confirm flow tests ─────────────────────────────────────────────────

    [Fact]
    public async Task ComebackConfirm_RecordDetectedThenMarkConfirmed()
    {
        var service = new ComebackDetectionService(_services!.Connections);

        // Create a detected comeback match (manually, since we're testing the confirm flow)
        var originalWoId = Guid.NewGuid().ToString("D");
        var closingWoId = Guid.NewGuid().ToString("D");

        // Set up work orders and repairs for detection context
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "CONFIRM-TEST",
            Name = "Confirm Test"
        };
        await _services.Assets.AddAsync(asset);

        // Original WO with repair and outcome
        var originalWo = new WorkOrder
        {
            Id = originalWoId,
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            ClosedAt = DateTimeOffset.UtcNow.AddDays(-10).ToString("o")
        };
        await _services.WorkOrders.AddAsync(originalWo);

        var originalRepair = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = originalWoId,
            Description = "Replaced pump",
            System = "hydraulics"
        };
        await _services.Repairs.AddAsync(originalRepair);

        var outcome = new Outcome
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = originalWoId,
            Result = OutcomeResult.Fixed,
            ClosedAt = originalWo.ClosedAt!
        };
        await _services.Outcomes.AddAsync(outcome);

        // Closing WO with repair
        var closingWo = new WorkOrder
        {
            Id = closingWoId,
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            ClosedAt = DateTimeOffset.UtcNow.ToString("o")
        };
        await _services.WorkOrders.AddAsync(closingWo);

        var closingRepair = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = closingWoId,
            Description = "Pump failed again",
            System = "hydraulics"
        };
        await _services.Repairs.AddAsync(closingRepair);

        // Detect the comeback
        var matches = await service.DetectAsync(closingWoId);
        Assert.Single(matches);

        // Record detected (unconfirmed)
        var recorded = await service.RecordDetectedAsync(matches[0]);
        Assert.False(recorded.Confirmed);

        // Verify outcome not yet marked
        var outcomeBefore = await _services.Outcomes.GetByIdAsync(outcome.Id);
        Assert.NotNull(outcomeBefore);
        Assert.False(outcomeBefore.ComebackDetected);

        // Mark confirmed
        var confirmed = await service.MarkConfirmedAsync(recorded.Id);
        Assert.True(confirmed);

        // Verify comeback is now confirmed
        var comebackRepo = new ComebackRepository(_services.Connections);
        var fetchedComeback = await comebackRepo.GetByIdAsync(recorded.Id);
        Assert.NotNull(fetchedComeback);
        Assert.True(fetchedComeback.Confirmed);

        // Verify outcome is now marked with comeback_detected=1
        var outcomeAfter = await _services.Outcomes.GetByIdAsync(outcome.Id);
        Assert.NotNull(outcomeAfter);
        Assert.True(outcomeAfter.ComebackDetected);
    }

    [Fact]
    public async Task ComebackConfirm_ConfirmAsyncOneStep()
    {
        var service = new ComebackDetectionService(_services!.Connections);

        // Set up work orders and repairs for detection context
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "CONFIRM-ONESTEP",
            Name = "One Step Confirm Test"
        };
        await _services.Assets.AddAsync(asset);

        // Original WO with repair and outcome
        var originalWoId = Guid.NewGuid().ToString("D");
        var originalWo = new WorkOrder
        {
            Id = originalWoId,
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            ClosedAt = DateTimeOffset.UtcNow.AddDays(-10).ToString("o")
        };
        await _services.WorkOrders.AddAsync(originalWo);

        var originalRepair = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = originalWoId,
            Description = "Replaced pump",
            System = "hydraulics"
        };
        await _services.Repairs.AddAsync(originalRepair);

        var outcome = new Outcome
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = originalWoId,
            Result = OutcomeResult.Fixed,
            ClosedAt = originalWo.ClosedAt!
        };
        await _services.Outcomes.AddAsync(outcome);

        // Closing WO with repair
        var closingWoId = Guid.NewGuid().ToString("D");
        var closingWo = new WorkOrder
        {
            Id = closingWoId,
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            ClosedAt = DateTimeOffset.UtcNow.ToString("o")
        };
        await _services.WorkOrders.AddAsync(closingWo);

        var closingRepair = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = closingWoId,
            Description = "Pump failed again",
            System = "hydraulics"
        };
        await _services.Repairs.AddAsync(closingRepair);

        // Confirm in one step (skip RecordDetectedAsync)
        var confirmedComeback = await service.ConfirmAsync(
            originalWoId,
            closingWoId,
            ComebackDetectionService.DefaultWindowDays,
            ComebackDetectionService.DefaultWindowHours,
            "Shared system 'hydraulics'.");

        Assert.True(confirmedComeback.Confirmed);
        Assert.Equal(originalWoId, confirmedComeback.OriginalWorkOrderId);
        Assert.Equal(closingWoId, confirmedComeback.ComebackWorkOrderId);

        // Verify outcome is marked with comeback_detected=1
        var outcomeAfter = await _services.Outcomes.GetByIdAsync(outcome.Id);
        Assert.NotNull(outcomeAfter);
        Assert.True(outcomeAfter.ComebackDetected);
    }

    // ── Regression tests (added by tester agent) ───────────────────────────

    [Fact]
    public async Task ComebackDetection_ProtocolNormalization_WhitespaceAndCase()
    {
        var service = new ComebackDetectionService(_services!.Connections);

        // Create asset
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "CB-PROTO-NORM",
            Name = "Protocol Normalization Test"
        };
        await _services.Assets.AddAsync(asset);

        // Original work order (closed 5 days ago)
        var originalWoId = Guid.NewGuid().ToString("D");
        var originalWo = new WorkOrder
        {
            Id = originalWoId,
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            ClosedAt = DateTimeOffset.UtcNow.AddDays(-5).ToString("o")
        };
        await _services.WorkOrders.AddAsync(originalWo);

        // Seed DTC with clean protocol via repository
        var originalDtc = new Dtc
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = originalWoId,
            Code = "P0301",
            Protocol = ProtocolType.J1939
        };
        await _services.Dtcs.AddAsync(originalDtc);

        // Closing work order (closed today)
        var closingWoId = Guid.NewGuid().ToString("D");
        var closingWo = new WorkOrder
        {
            Id = closingWoId,
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            ClosedAt = DateTimeOffset.UtcNow.ToString("o")
        };
        await _services.WorkOrders.AddAsync(closingWo);

        // Seed DTC with raw " j1939 " protocol (leading/trailing spaces) via direct SQL.
        // The Dtc model exposes protocol as an enum, so we bypass it to test normalization.
        var closingDtcId = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow.ToString("o");
        await using (var connection = await _services.Connections.OpenAsync())
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO dtcs (id, work_order_id, code, protocol, observed_at, created_at, updated_at)
                VALUES ($id, $woId, 'P0301', ' j1939 ', $now, $now, $now);
                """;
            cmd.Parameters.AddWithValue("$id", closingDtcId);
            cmd.Parameters.AddWithValue("$woId", closingWoId);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync();
        }

        // Detect comebacks — should find one match via normalized DTC protocol/code
        var matches = await service.DetectAsync(closingWoId);
        Assert.Single(matches);
        Assert.Equal(originalWoId, matches[0].OriginalWorkOrderId);
        Assert.Contains("P0301", matches[0].MatchReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ComebackDetection_HourMeterPrecedence_ClosingWoHoursUsed()
    {
        var service = new ComebackDetectionService(_services!.Connections);

        // Create asset with live hour meter at 1100 (would compute 300 elapsed if used)
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "CB-HOUR-PREC",
            Name = "Hour Meter Precedence Test",
            HourMeter = 1100.0
        };
        await _services.Assets.AddAsync(asset);

        // Original work order (closed 7 days ago, hours_at_open=800)
        var originalWoId = Guid.NewGuid().ToString("D");
        var originalWo = new WorkOrder
        {
            Id = originalWoId,
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            ClosedAt = DateTimeOffset.UtcNow.AddDays(-7).ToString("o"),
            HoursAtOpen = 800.0
        };
        await _services.WorkOrders.AddAsync(originalWo);

        var originalRepair = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = originalWoId,
            Description = "Replaced pump",
            System = "hydraulics"
        };
        await _services.Repairs.AddAsync(originalRepair);

        // Closing work order (closed today, hours_at_open=900)
        var closingWoId = Guid.NewGuid().ToString("D");
        var closingWo = new WorkOrder
        {
            Id = closingWoId,
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            ClosedAt = DateTimeOffset.UtcNow.ToString("o"),
            HoursAtOpen = 900.0
        };
        await _services.WorkOrders.AddAsync(closingWo);

        var closingRepair = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = closingWoId,
            Description = "Pump failed again",
            System = "hydraulics"
        };
        await _services.Repairs.AddAsync(closingRepair);

        // Should detect: elapsed = 900 - 800 = 100 hours (within default 200-hour window).
        // The old buggy path would use live asset meter (1100) and compute 300, wrongly rejecting.
        var matches = await service.DetectAsync(closingWoId);
        Assert.Single(matches);
    }

    [Fact]
    public async Task ComebackDetection_HourMeterFallback_WhenClosingWoHoursNull()
    {
        var service = new ComebackDetectionService(_services!.Connections);

        // Create asset with live hour meter at 950
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "CB-HOUR-FALLBACK",
            Name = "Hour Meter Fallback Test",
            HourMeter = 950.0
        };
        await _services.Assets.AddAsync(asset);

        // Original work order (closed 7 days ago, hours_at_open=800)
        var originalWoId = Guid.NewGuid().ToString("D");
        var originalWo = new WorkOrder
        {
            Id = originalWoId,
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            ClosedAt = DateTimeOffset.UtcNow.AddDays(-7).ToString("o"),
            HoursAtOpen = 800.0
        };
        await _services.WorkOrders.AddAsync(originalWo);

        var originalRepair = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = originalWoId,
            Description = "Replaced pump",
            System = "hydraulics"
        };
        await _services.Repairs.AddAsync(originalRepair);

        // Closing work order (closed today, hours_at_open=NULL → fallback to live meter)
        var closingWoId = Guid.NewGuid().ToString("D");
        var closingWo = new WorkOrder
        {
            Id = closingWoId,
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            ClosedAt = DateTimeOffset.UtcNow.ToString("o")
            // HoursAtOpen intentionally null → fallback to asset's live meter (950)
        };
        await _services.WorkOrders.AddAsync(closingWo);

        var closingRepair = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = closingWoId,
            Description = "Pump failed again",
            System = "hydraulics"
        };
        await _services.Repairs.AddAsync(closingRepair);

        // Should detect: elapsed = 950 (live) - 800 = 150 hours (within default 200-hour window).
        var matches = await service.DetectAsync(closingWoId);
        Assert.Single(matches);
    }

    [Fact]
    public async Task ComebackConfirm_DuplicatePrevention_IdempotentRecordAndConfirm()
    {
        var service = new ComebackDetectionService(_services!.Connections);

        // Set up work orders and repairs for detection context
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = "CB-DUP-PREVENT",
            Name = "Duplicate Prevention Test"
        };
        await _services.Assets.AddAsync(asset);

        // Original WO with repair and outcome
        var originalWoId = Guid.NewGuid().ToString("D");
        var originalWo = new WorkOrder
        {
            Id = originalWoId,
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            ClosedAt = DateTimeOffset.UtcNow.AddDays(-10).ToString("o")
        };
        await _services.WorkOrders.AddAsync(originalWo);

        var originalRepair = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = originalWoId,
            Description = "Replaced pump",
            System = "hydraulics"
        };
        await _services.Repairs.AddAsync(originalRepair);

        var outcome = new Outcome
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = originalWoId,
            Result = OutcomeResult.Fixed,
            ClosedAt = originalWo.ClosedAt!
        };
        await _services.Outcomes.AddAsync(outcome);

        // Closing WO with repair
        var closingWoId = Guid.NewGuid().ToString("D");
        var closingWo = new WorkOrder
        {
            Id = closingWoId,
            AssetId = asset.Id,
            Status = WorkOrderStatus.Closed,
            ClosedAt = DateTimeOffset.UtcNow.ToString("o")
        };
        await _services.WorkOrders.AddAsync(closingWo);

        var closingRepair = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = closingWoId,
            Description = "Pump failed again",
            System = "hydraulics"
        };
        await _services.Repairs.AddAsync(closingRepair);

        // Detect the comeback
        var matches = await service.DetectAsync(closingWoId);
        Assert.Single(matches);

        // Record detected twice — should return same ID both times
        var recorded1 = await service.RecordDetectedAsync(matches[0]);
        var recorded2 = await service.RecordDetectedAsync(matches[0]);
        Assert.Equal(recorded1.Id, recorded2.Id);

        // Confirm twice via MarkConfirmedAsync — should be idempotent
        var confirmed1 = await service.MarkConfirmedAsync(recorded1.Id);
        var confirmed2 = await service.MarkConfirmedAsync(recorded1.Id);
        Assert.True(confirmed1);
        Assert.True(confirmed2);

        // Verify only one comeback row exists for this pair
        var comebackRepo = new ComebackRepository(_services.Connections);
        var allComebacks = await comebackRepo.FindAsync("original_work_order_id", originalWoId);
        Assert.Single(allComebacks);
        Assert.Equal(recorded1.Id, allComebacks[0].Id);
        Assert.True(allComebacks[0].Confirmed);

        // Verify outcome is marked with comeback_detected=1 (mirrored once)
        var outcomeAfter = await _services.Outcomes.GetByIdAsync(outcome.Id);
        Assert.NotNull(outcomeAfter);
        Assert.True(outcomeAfter.ComebackDetected);
    }
}
