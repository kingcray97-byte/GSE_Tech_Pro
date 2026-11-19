using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using GseTechPro.Data.Migrations;
using GseTechPro.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GseTechPro.Data.Tests;

/// <summary>Focused Phase 6 smoke coverage; the comprehensive suite belongs to tester.</summary>
public sealed class ScopeDataSmokeTests : IAsyncLifetime
{
    private string _dbPath = "";
    private DataServices _services = null!;

    public async Task InitializeAsync()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gse-tech-pro-tests");
        Directory.CreateDirectory(tempDir);
        _dbPath = Path.Combine(tempDir, $"scope-data-{Guid.NewGuid():N}.db");
        _services = new DataServices(new DatabaseOptions
        {
            DatabasePath = _dbPath,
            EnableVectorSearch = false,
        });

        var initialized = await _services.InitializeAsync();
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, initialized.AppliedMigrations);
        Assert.Equal(6, SqliteMigrator.LatestVersion);
    }

    [Fact]
    public async Task ScopeSeedAndRepositories_RoundTrip_AndReseedWithoutDuplicates()
    {
        Assert.Empty(await _services.Migrator.MigrateAsync());
        Assert.Equal(6, (await _services.ReferenceWaveforms.GetAllAsync()).Count);
        Assert.Equal(6, (await _services.GuidedTests.GetAllAsync()).Count);
        Assert.Equal(6, await ScalarLong(
            "SELECT COUNT(*) FROM reference_waveforms WHERE data_path IS NULL;"));
        Assert.Equal(6, await ScalarLong(
            "SELECT COUNT(*) FROM reference_waveforms WHERE source = 'manual';"));

        var reference = await _services.ReferenceWaveforms.GetByIdAsync("rw-crank-sensor")
            ?? throw new InvalidOperationException("Missing crank reference.");
        Assert.Equal(WaveformSource.Manual, reference.Source);
        Assert.Equal("", reference.DataPath);
        var expectations = ScopeJson.DeserializeReferenceExpectations(reference.ExpectedJson);
        Assert.NotNull(expectations);
        Assert.Equal(2, expectations!.Measurements.Count);
        Assert.All(expectations.Measurements, item => Assert.True(item.IsTodo));
        IReferenceWaveformRepository typedReferences = _services.ReferenceWaveforms;
        Assert.Equal("", (await typedReferences.GetByIdAsync(reference.Id))?.DataPath);

        var linked = await _services.GuidedTests.GetByIdWithReferenceAsync("gt-crank-sensor")
            ?? throw new InvalidOperationException("Missing crank guided test.");
        Assert.Equal(reference.Id, linked.ReferenceWaveform?.Id);
        var setup = ScopeJson.DeserializeCaptureRequest(linked.GuidedTest.ScopeSetupJson);
        Assert.NotNull(setup);
        Assert.Equal(1_000_000, setup!.SampleRateHz);
        Assert.Equal(ScopeTriggerMode.Rising, setup.TriggerMode);
        Assert.Contains("sw-lockout-tagout", linked.GuidedTest.SafetyWarningIds);
        Assert.Equal(6, (await _services.GuidedTests.GetByModelAsync(null)).Count);

        var reseeded = await _services.ScopeSeeder.SeedAsync();
        Assert.Equal(6, reseeded.GuidedTests);
        Assert.Equal(6, reseeded.ReferenceWaveforms);
        Assert.Equal(6, await ScalarLong("SELECT COUNT(*) FROM guided_tests;"));
        Assert.Equal(6, await ScalarLong("SELECT COUNT(*) FROM reference_waveforms;"));

        var asset = new Asset { AssetNumber = $"SCOPE-{Guid.NewGuid():N}", Name = "Scope smoke asset" };
        await _services.Assets.AddAsync(asset);
        var workOrder = new WorkOrder { AssetId = asset.Id, ComplaintSummary = "Scope persistence smoke" };
        await _services.WorkOrders.AddAsync(workOrder);

        var waveform = new Waveform
        {
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
            DataPath = $"waveforms/{Guid.NewGuid():D}.wfm",
            GuidedTestId = linked.GuidedTest.Id,
            ReferenceWaveformId = reference.Id,
        };
        await _services.Waveforms.InsertAsync(waveform);
        Assert.Equal("simulator", await ScalarText(
            "SELECT scope_device FROM waveforms WHERE id = $id;", ("$id", waveform.Id)));

        var loaded = await _services.Waveforms.GetByIdAsync(waveform.Id)
            ?? throw new InvalidOperationException("Missing saved waveform.");
        Assert.Equal(ScopeDeviceKind.Simulator, loaded.ScopeDevice);
        Assert.Equal(1, ScopeJson.DeserializeChannelConfigs(loaded.ChannelConfigJson).Single().Channel);
        Assert.Equal(waveform.Id, (await _services.Waveforms.GetByWorkOrderAsync(workOrder.Id)).Single().Id);

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
        loaded = await _services.Waveforms.GetByIdAsync(waveform.Id)
            ?? throw new InvalidOperationException("Missing updated waveform.");
        Assert.Equal(ScopeVerdict.Na,
            ScopeJson.DeserializeMeasurements(loaded.MeasurementsJson).Single().Verdict);

        Assert.True(await _services.Waveforms.SoftDeleteAsync(waveform.Id));
        Assert.False(await _services.Waveforms.SoftDeleteAsync(waveform.Id));
        Assert.Null(await _services.Waveforms.GetByIdAsync(waveform.Id));
        Assert.Empty(await _services.Waveforms.GetByWorkOrderAsync(workOrder.Id));
        Assert.False(await _services.Waveforms.UpdateMeasurementsAsync(waveform.Id, null));
        Assert.Equal(0, await ForeignKeyViolationCount());

        var secondStartup = await _services.InitializeAsync();
        Assert.Empty(secondStartup.AppliedMigrations);
        Assert.Equal(6, await ScalarLong("SELECT COUNT(*) FROM guided_tests;"));
        Assert.Equal(6, await ScalarLong("SELECT COUNT(*) FROM reference_waveforms;"));
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
        if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
        return Task.CompletedTask;
    }

    private async Task<long> ScalarLong(string sql, params (string Name, object Value)[] parameters)
    {
        var value = await Scalar(sql, parameters);
        return Convert.ToInt64(value);
    }

    private async Task<string?> ScalarText(string sql, params (string Name, object Value)[] parameters) =>
        Convert.ToString(await Scalar(sql, parameters));

    private async Task<object?> Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await _services.Connections.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync();
    }

    private async Task<int> ForeignKeyViolationCount()
    {
        await using var connection = await _services.Connections.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync()) count++;
        return count;
    }
}
