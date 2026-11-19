using GseTechPro.Core;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using GseTechPro.Data;
using GseTechPro.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GseTechPro.Tests;

public class RecordingRepositoryTests : IAsyncLifetime
{
    private string _dbPath = "";
    private DataServices? _services;

    public Task InitializeAsync()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gse-tech-pro-tests");
        Directory.CreateDirectory(tempDir);
        _dbPath = Path.Combine(tempDir, $"recording-test-{Guid.NewGuid():N}.db");

        var options = new DatabaseOptions { DatabasePath = _dbPath };
        _services = new DataServices(options);
        _services.InitializeAsync().Wait();

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
    public async Task FreshDb_MigratesToVersion2_RecordingTablesExist()
    {
        // Verify both recording tables exist after migration
        var connection = await _services!.Connections.OpenAsync();
        try
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='recordings';";
                var count = await cmd.ExecuteScalarAsync();
                Assert.Equal(1, Convert.ToInt32(count));
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='recording_frames';";
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
    public async Task RecordingRepository_CreateSession_PersistsCorrectly()
    {
        var repo = _services!.Recordings;

        // Create a work order first (FK constraint)
        var assetRepo = _services.Assets;
        var woRepo = _services.WorkOrders;
        var asset = new Asset { Id = Guid.NewGuid().ToString("D"), AssetNumber = "REC-TEST-001", Name = "Recording Test" };
        await assetRepo.AddAsync(asset);
        var wo = new WorkOrder { Id = Guid.NewGuid().ToString("D"), AssetId = asset.Id, Status = WorkOrderStatus.Open };
        await woRepo.AddAsync(wo);

        // Create recording session with trigger config
        var session = new RecordingSession
        {
            WorkOrderId = wo.Id,
            AssetId = asset.Id,
            Kind = RecordingKind.LiveData,
            Source = RecordingSource.Real,
            AdapterName = "Simulator (J1939)",
            Protocol = ProtocolType.J1939,
            PidNames = new[] { "Engine Speed", "Coolant Temperature" },
            Trigger = new TriggerConfig
            {
                Kind = TriggerKind.OnPidThreshold,
                PidName = "Coolant Temperature",
                ThresholdValue = 105.0,
                ThresholdUnit = "°C",
                Comparison = ThresholdComparison.Above,
                PreTriggerSeconds = 3.0,
                PostTriggerSeconds = 10.0
            }
        };

        var created = await repo.CreateSessionAsync(session);

        // Verify row was persisted with correct fields
        Assert.NotNull(created.Id);
        Assert.Equal(wo.Id, created.WorkOrderId);
        Assert.Equal(asset.Id, created.AssetId);
        Assert.Equal(RecordingKind.LiveData, created.Kind);
        Assert.Equal(RecordingSource.Real, created.Source);
        Assert.Equal("Simulator (J1939)", created.AdapterName);
        Assert.Equal(ProtocolType.J1939, created.Protocol);
        Assert.Equal(2, created.PidNames.Count);
        Assert.Contains("Engine Speed", created.PidNames);
        Assert.NotNull(created.Trigger);
        Assert.Equal(TriggerKind.OnPidThreshold, created.Trigger.Kind);
    }

    [Fact]
    public async Task RecordingRepository_GetSessionById_RoundTrips()
    {
        var repo = _services!.Recordings;

        // Create a work order first (FK constraint)
        var assetRepo = _services.Assets;
        var woRepo = _services.WorkOrders;
        var asset = new Asset { Id = Guid.NewGuid().ToString("D"), AssetNumber = "REC-TEST-002", Name = "Recording Test 2" };
        await assetRepo.AddAsync(asset);
        var wo = new WorkOrder { Id = Guid.NewGuid().ToString("D"), AssetId = asset.Id, Status = WorkOrderStatus.Open };
        await woRepo.AddAsync(wo);

        // Create session with trigger
        var originalSession = new RecordingSession
        {
            WorkOrderId = wo.Id,
            Protocol = ProtocolType.J1939,
            PidNames = new[] { "Engine Speed" },
            Trigger = new TriggerConfig
            {
                Kind = TriggerKind.OnDtc,
                DtcCode = "SPN 94 FMI 3",
                PreTriggerSeconds = 5.0
            }
        };

        var created = await repo.CreateSessionAsync(originalSession);

        // Retrieve and verify round-trip
        var retrieved = await repo.GetSessionByIdAsync(created.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(created.Id, retrieved.Id);
        Assert.Equal(wo.Id, retrieved.WorkOrderId);
        Assert.Equal(ProtocolType.J1939, retrieved.Protocol);
        Assert.Single(retrieved.PidNames);
        Assert.Equal("Engine Speed", retrieved.PidNames[0]);
        Assert.NotNull(retrieved.Trigger);
        Assert.Equal(TriggerKind.OnDtc, retrieved.Trigger.Kind);
        Assert.Equal("SPN 94 FMI 3", retrieved.Trigger.DtcCode);
    }

    [Fact]
    public async Task RecordingFrameRepository_Append_PreservesIsSimulated()
    {
        var repo = _services!.Recordings;
        var frameRepo = _services.RecordingFrames;

        // Create a work order first (FK constraint)
        var assetRepo = _services.Assets;
        var woRepo = _services.WorkOrders;
        var asset = new Asset { Id = Guid.NewGuid().ToString("D"), AssetNumber = "REC-TEST-003", Name = "Recording Test 3" };
        await assetRepo.AddAsync(asset);
        var wo = new WorkOrder { Id = Guid.NewGuid().ToString("D"), AssetId = asset.Id, Status = WorkOrderStatus.Open };
        await woRepo.AddAsync(wo);

        // Create recording session
        var session = new RecordingSession
        {
            WorkOrderId = wo.Id,
            Protocol = ProtocolType.J1939,
            PidNames = new[] { "Engine Speed", "Battery Voltage" }
        };
        var created = await repo.CreateSessionAsync(session);

        // Append frames with mixed IsSimulated flags
        var frame1 = new RecordedFrameBatch
        {
            Sequence = 0,
            CapturedAt = Clock.UtcNow,
            Pids = new[]
            {
                new PidValue { Name = "Engine Speed", Value = 850.0, Unit = "rpm", IsSimulated = true },
                new PidValue { Name = "Battery Voltage", Value = 13.8, Unit = "V", IsSimulated = false }
            }
        };

        var frame2 = new RecordedFrameBatch
        {
            Sequence = 1,
            CapturedAt = Clock.UtcNow,
            Pids = new[]
            {
                new PidValue { Name = "Engine Speed", Value = 900.0, Unit = "rpm", IsSimulated = true },
                new PidValue { Name = "Battery Voltage", Value = 14.2, Unit = "V", IsSimulated = false }
            }
        };

        var appended = await frameRepo.AppendAsync(created.Id, new[] { frame1, frame2 });
        Assert.Equal(2, appended);

        // Read back and verify IsSimulated flags are preserved
        var window = await frameRepo.GetWindowAsync(new RecordingSeekRequest
        {
            RecordingId = created.Id,
            StartSequence = 0,
            MaxFrames = 10
        });

        Assert.Equal(2, window.Frames.Count);

        // Frame 0: Engine Speed simulated=true, Battery Voltage simulated=false
        var frame0Pids = window.Frames[0].Pids;
        var engineSpeed0 = frame0Pids.First(p => p.Name == "Engine Speed");
        var batteryVoltage0 = frame0Pids.First(p => p.Name == "Battery Voltage");
        Assert.True(engineSpeed0.IsSimulated);
        Assert.False(batteryVoltage0.IsSimulated);

        // Frame 1: same pattern
        var frame1Pids = window.Frames[1].Pids;
        var engineSpeed1 = frame1Pids.First(p => p.Name == "Engine Speed");
        var batteryVoltage1 = frame1Pids.First(p => p.Name == "Battery Voltage");
        Assert.True(engineSpeed1.IsSimulated);
        Assert.False(batteryVoltage1.IsSimulated);
    }

    [Fact]
    public async Task RecordingFrameRepository_GetWindow_HonorsMaxFrames()
    {
        var repo = _services!.Recordings;
        var frameRepo = _services.RecordingFrames;

        // Create a work order first (FK constraint)
        var assetRepo = _services.Assets;
        var woRepo = _services.WorkOrders;
        var asset = new Asset { Id = Guid.NewGuid().ToString("D"), AssetNumber = "REC-TEST-004", Name = "Recording Test 4" };
        await assetRepo.AddAsync(asset);
        var wo = new WorkOrder { Id = Guid.NewGuid().ToString("D"), AssetId = asset.Id, Status = WorkOrderStatus.Open };
        await woRepo.AddAsync(wo);

        // Create recording session
        var session = new RecordingSession
        {
            WorkOrderId = wo.Id,
            Protocol = ProtocolType.J1939,
            PidNames = new[] { "Engine Speed" }
        };
        var created = await repo.CreateSessionAsync(session);

        // Append 5 frames
        var frames = new List<RecordedFrameBatch>();
        for (long i = 0; i < 5; i++)
        {
            frames.Add(new RecordedFrameBatch
            {
                Sequence = i,
                CapturedAt = Clock.UtcNow,
                Pids = new[] { new PidValue { Name = "Engine Speed", Value = 800.0 + i * 10, Unit = "rpm" } }
            });
        }
        await frameRepo.AppendAsync(created.Id, frames);

        // Request only 2 frames starting at sequence 1
        var window = await frameRepo.GetWindowAsync(new RecordingSeekRequest
        {
            RecordingId = created.Id,
            StartSequence = 1,
            MaxFrames = 2
        });

        Assert.Equal(2, window.Frames.Count);
        Assert.Equal(1L, window.StartSequence);
        // Should not be at end of recording (there are more frames after)
        Assert.False(window.EndOfRecording);
    }

    [Fact]
    public async Task RecordingFrameRepository_GetWindow_EndOfRecordingAtTail()
    {
        var repo = _services!.Recordings;
        var frameRepo = _services.RecordingFrames;

        // Create a work order first (FK constraint)
        var assetRepo = _services.Assets;
        var woRepo = _services.WorkOrders;
        var asset = new Asset { Id = Guid.NewGuid().ToString("D"), AssetNumber = "REC-TEST-005", Name = "Recording Test 5" };
        await assetRepo.AddAsync(asset);
        var wo = new WorkOrder { Id = Guid.NewGuid().ToString("D"), AssetId = asset.Id, Status = WorkOrderStatus.Open };
        await woRepo.AddAsync(wo);

        // Create recording session
        var session = new RecordingSession
        {
            WorkOrderId = wo.Id,
            Protocol = ProtocolType.J1939,
            PidNames = new[] { "Engine Speed" }
        };
        var created = await repo.CreateSessionAsync(session);

        // Append 3 frames
        var frames = new List<RecordedFrameBatch>();
        for (long i = 0; i < 3; i++)
        {
            frames.Add(new RecordedFrameBatch
            {
                Sequence = i,
                CapturedAt = Clock.UtcNow,
                Pids = new[] { new PidValue { Name = "Engine Speed", Value = 800.0 + i * 10, Unit = "rpm" } }
            });
        }
        await frameRepo.AppendAsync(created.Id, frames);

        // Request all remaining frames from sequence 1 (should get 2 frames and be at end)
        var window = await frameRepo.GetWindowAsync(new RecordingSeekRequest
        {
            RecordingId = created.Id,
            StartSequence = 1,
            MaxFrames = 10
        });

        Assert.Equal(2, window.Frames.Count);
        // Should be at end of recording (no more frames after)
        Assert.True(window.EndOfRecording);
    }

    [Fact]
    public async Task CloseRecording_Completed_SetsStatusAndFrameCount()
    {
        var repo = _services!.Recordings;
        var frameRepo = _services.RecordingFrames;

        // Create a work order first (FK constraint)
        var assetRepo = _services.Assets;
        var woRepo = _services.WorkOrders;
        var asset = new Asset { Id = Guid.NewGuid().ToString("D"), AssetNumber = "REC-TEST-006", Name = "Recording Test 6" };
        await assetRepo.AddAsync(asset);
        var wo = new WorkOrder { Id = Guid.NewGuid().ToString("D"), AssetId = asset.Id, Status = WorkOrderStatus.Open };
        await woRepo.AddAsync(wo);

        // Create recording session
        var session = new RecordingSession
        {
            WorkOrderId = wo.Id,
            Protocol = ProtocolType.J1939,
            PidNames = new[] { "Engine Speed" }
        };
        var created = await repo.CreateSessionAsync(session);

        // Append 4 frames
        var frames = new List<RecordedFrameBatch>();
        for (long i = 0; i < 4; i++)
        {
            frames.Add(new RecordedFrameBatch
            {
                Sequence = i,
                CapturedAt = Clock.UtcNow,
                Pids = new[] { new PidValue { Name = "Engine Speed", Value = 800.0 + i * 10, Unit = "rpm" } }
            });
        }
        await frameRepo.AppendAsync(created.Id, frames);

        // Close as completed
        var closed = await repo.CloseRecordingAsync(created.Id, RecordingStatus.Completed);
        Assert.True(closed);

        // Verify status and frame count
        var retrieved = await repo.GetSessionByIdAsync(created.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(RecordingStatus.Completed, retrieved.Status);
        Assert.NotNull(retrieved.EndedAt);
        Assert.Equal(4, retrieved.FrameCount);
    }

    [Fact]
    public async Task CloseRecording_Interrupted_SetsStatus()
    {
        var repo = _services!.Recordings;

        // Create a work order first (FK constraint)
        var assetRepo = _services.Assets;
        var woRepo = _services.WorkOrders;
        var asset = new Asset { Id = Guid.NewGuid().ToString("D"), AssetNumber = "REC-TEST-007", Name = "Recording Test 7" };
        await assetRepo.AddAsync(asset);
        var wo = new WorkOrder { Id = Guid.NewGuid().ToString("D"), AssetId = asset.Id, Status = WorkOrderStatus.Open };
        await woRepo.AddAsync(wo);

        // Create recording session
        var session = new RecordingSession
        {
            WorkOrderId = wo.Id,
            Protocol = ProtocolType.J1939,
            PidNames = new[] { "Engine Speed" }
        };
        var created = await repo.CreateSessionAsync(session);

        // Close as interrupted (no frames appended)
        var closed = await repo.CloseRecordingAsync(created.Id, RecordingStatus.Interrupted);
        Assert.True(closed);

        // Verify status
        var retrieved = await repo.GetSessionByIdAsync(created.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(RecordingStatus.Interrupted, retrieved.Status);
        Assert.NotNull(retrieved.EndedAt);
    }

    [Fact]
    public async Task CloseRecording_AlreadyClosed_ReturnsFalse()
    {
        var repo = _services!.Recordings;

        // Create a work order first (FK constraint)
        var assetRepo = _services.Assets;
        var woRepo = _services.WorkOrders;
        var asset = new Asset { Id = Guid.NewGuid().ToString("D"), AssetNumber = "REC-TEST-008", Name = "Recording Test 8" };
        await assetRepo.AddAsync(asset);
        var wo = new WorkOrder { Id = Guid.NewGuid().ToString("D"), AssetId = asset.Id, Status = WorkOrderStatus.Open };
        await woRepo.AddAsync(wo);

        // Create recording session
        var session = new RecordingSession
        {
            WorkOrderId = wo.Id,
            Protocol = ProtocolType.J1939,
            PidNames = new[] { "Engine Speed" }
        };
        var created = await repo.CreateSessionAsync(session);

        // Close once
        var closedFirst = await repo.CloseRecordingAsync(created.Id, RecordingStatus.Completed);
        Assert.True(closedFirst);

        // Try to close again - should return false
        var closedSecond = await repo.CloseRecordingAsync(created.Id, RecordingStatus.Interrupted);
        Assert.False(closedSecond);
    }
}
