using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using GseTechPro.Data;
using GseTechPro.Data.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GseTechPro.Tests;

public sealed class ScopeDataTests : IAsyncLifetime
{
    private string _dbPath = "";
    private DataServices? _services;

    public Task InitializeAsync()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gse-tech-pro-tests");
        Directory.CreateDirectory(tempDir);
        _dbPath = Path.Combine(tempDir, $"scope-data-{Guid.NewGuid():N}.db");

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

    [Fact]
    public async Task Migration006_FreshDatabase_ReachesLatestVersion()
    {
        Assert.Equal(7, SqliteMigrator.LatestVersion);

        await using var connection = await _services!.Connections.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(7, version);
    }

    [Fact]
    public async Task Migration006_AddsScopeTablesAndColumns()
    {
        await using var connection = await _services!.Connections.OpenAsync();

        // Check guided_tests table exists with scope_setup_json column
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*) FROM pragma_table_info('guided_tests') WHERE name = 'scope_setup_json';
                """;
            var count = Convert.ToInt32(await command.ExecuteScalarAsync());
            Assert.Equal(1, count);
        }

        // Check reference_waveforms table exists with expected_json column
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*) FROM pragma_table_info('reference_waveforms') WHERE name = 'expected_json';
                """;
            var count = Convert.ToInt32(await command.ExecuteScalarAsync());
            Assert.Equal(1, count);
        }

        // Check waveforms table has scope_device column
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*) FROM pragma_table_info('waveforms') WHERE name = 'scope_device';
                """;
            var count = Convert.ToInt32(await command.ExecuteScalarAsync());
            Assert.Equal(1, count);
        }

        // Check waveforms table has measurements_json column
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*) FROM pragma_table_info('waveforms') WHERE name = 'measurements_json';
                """;
            var count = Convert.ToInt32(await command.ExecuteScalarAsync());
            Assert.Equal(1, count);
        }
    }

    [Fact]
    public async Task ScopeSeeder_Idempotent_RunsTwiceWithoutDuplicates()
    {
        // Seed once (DataServices.InitializeAsync already seeds, but call explicitly to be sure)
        var firstResult = await _services!.ScopeSeeder.SeedAsync();
        Assert.Equal(6, firstResult.GuidedTests);
        Assert.Equal(6, firstResult.ReferenceWaveforms);

        // Verify counts after first seed
        var guidedCountAfterFirst = (await _services.GuidedTests.GetAllAsync()).Count;
        Assert.Equal(6, guidedCountAfterFirst);

        var refCountAfterFirst = (await _services.ReferenceWaveforms.GetAllAsync()).Count;
        Assert.Equal(6, refCountAfterFirst);

        // Seed again - should not add duplicates
        var secondResult = await _services.ScopeSeeder.SeedAsync();
        Assert.Equal(6, secondResult.GuidedTests);
        Assert.Equal(6, secondResult.ReferenceWaveforms);

        // Verify counts are still correct after re-seeding (no duplicates)
        var guidedCountAfterSecond = (await _services.GuidedTests.GetAllAsync()).Count;
        Assert.Equal(6, guidedCountAfterSecond);

        var refCountAfterSecond = (await _services.ReferenceWaveforms.GetAllAsync()).Count;
        Assert.Equal(6, refCountAfterSecond);

        // Verify data_path is empty for all seeded reference waveforms
        foreach (var reference in await _services.ReferenceWaveforms.GetAllAsync())
        {
            Assert.Equal("", reference.DataPath);
        }
    }

    [Fact]
    public async Task WaveformRepository_RoundTripsScopeDeviceAndMeasurementsJson()
    {
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetNumber = $"SCOPE-RT-{Guid.NewGuid():N}",
            Name = "Scope round-trip test asset"
        };
        await _services!.Assets.AddAsync(asset);

        var workOrder = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            AssetId = asset.Id,
            ComplaintSummary = "Scope persistence round-trip test",
            Status = WorkOrderStatus.Open
        };
        await _services.WorkOrders.AddAsync(workOrder);

        // Create waveform with scope device and channel config JSON
        var waveform = new Waveform
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = workOrder.Id,
            ScopeDevice = ScopeDeviceKind.Simulator,
            ChannelConfigJson = ScopeJson.SerializeChannelConfigs(new[]
            {
                new ScopeChannelConfig
                {
                    Channel = 1,
                    VoltageRangeVolts = 5,
                    Coupling = ScopeCoupling.Dc,
                    ProbeMultiplier = 1,
                },
            }),
            Timebase = 0.005,
            SampleRate = 1_000_000,
            SampleCount = 50_000,
        };

        await _services.Waveforms.InsertAsync(waveform);

        // Retrieve and verify round-trip
        var loaded = await _services.Waveforms.GetByIdAsync(waveform.Id);
        Assert.NotNull(loaded);
        Assert.Equal(ScopeDeviceKind.Simulator, loaded.ScopeDevice);
        Assert.Equal("simulator", DbEnum.ToCode(loaded.ScopeDevice));

        // Verify channel config round-trip
        var loadedConfigs = ScopeJson.DeserializeChannelConfigs(loaded.ChannelConfigJson);
        Assert.Single(loadedConfigs);
        Assert.Equal(1, loadedConfigs[0].Channel);
        Assert.Equal(ScopeCoupling.Dc, loadedConfigs[0].Coupling);

        // Update measurements JSON and verify round-trip
        var measurementsJson = ScopeJson.SerializeMeasurements(new[]
        {
            new ScopeMeasurement
            {
                Kind = MeasurementKind.PeakToPeak,
                Channel = 1,
                Value = 12.4,
                Unit = "V",
                Status = ScopeMeasurementStatus.Computed,
                IsSimulated = true,
                Verdict = ScopeVerdict.Na,
            },
        });

        Assert.True(await _services.Waveforms.UpdateMeasurementsAsync(waveform.Id, measurementsJson));

        loaded = await _services.Waveforms.GetByIdAsync(waveform.Id);
        Assert.NotNull(loaded);
        var loadedMeasurements = ScopeJson.DeserializeMeasurements(loaded.MeasurementsJson);
        Assert.Single(loadedMeasurements);
        Assert.Equal(MeasurementKind.PeakToPeak, loadedMeasurements[0].Kind);
        Assert.Equal(ScopeVerdict.Na, loadedMeasurements[0].Verdict);
    }
}
