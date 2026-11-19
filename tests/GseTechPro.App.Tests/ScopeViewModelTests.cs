using GseTechPro.App.ViewModels;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Scope;
using Microsoft.UI.Xaml;
using Xunit;

namespace GseTechPro.App.Tests;

public sealed class ScopeViewModelTests
{
    [Fact]
    public async Task GuidedTests_ScopeSetupJson_RoundTripsThroughScopeJson_AndShowsSafetyWarningsFirst()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"gse-scope-roundtrip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "gse-scope.db");

        try
        {
            var dataServices = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
            await dataServices.InitializeAsync();

            var scopeFactory = new ScopeDeviceFactory();
            await using var vm = new MainShellViewModel(
                dataServices,
                new DiagnosticAdapterFactory(),
                dispatcherQueue: null,
                scopeDeviceFactory: scopeFactory);

            await vm.ReloadDatabaseRecordsAsync();
            vm.SelectToolbarAction("Scope");

            Assert.True(vm.IsScopeViewVisible);
            Assert.Equal(Visibility.Visible, vm.ScopeViewVisibility);
            Assert.Equal(Visibility.Collapsed, vm.ToolPlaceholderVisibility);

            // All 6 seeded guided tests are loaded
            Assert.Equal(6, vm.GuidedScopeTests.Count);

            foreach (var testItem in vm.GuidedScopeTests)
            {
                // (a) Guided test's scope_setup_json round-trips through ScopeJson
                Assert.False(string.IsNullOrWhiteSpace(testItem.ScopeSetupJson));
                var deserialized = ScopeJson.DeserializeCaptureRequest(testItem.ScopeSetupJson);
                Assert.NotNull(deserialized);
                Assert.NotEmpty(deserialized!.Channels);

                var reserialized = ScopeJson.SerializeCaptureRequest(deserialized);
                var roundTripped = ScopeJson.DeserializeCaptureRequest(reserialized);
                Assert.NotNull(roundTripped);
                Assert.Equal(deserialized.SampleRateHz, roundTripped!.SampleRateHz);
                Assert.Equal(deserialized.TimebaseSecondsPerDivision, roundTripped.TimebaseSecondsPerDivision, 9);
                Assert.Equal(deserialized.TriggerMode, roundTripped.TriggerMode);
                Assert.Equal(deserialized.TriggerChannel, roundTripped.TriggerChannel);
                Assert.Equal(deserialized.TriggerLevelVolts, roundTripped.TriggerLevelVolts, 6);
                Assert.Equal(deserialized.Channels.Count, roundTripped.Channels.Count);
                Assert.Equal(deserialized.Channels[0].Channel, roundTripped.Channels[0].Channel);
                Assert.Equal(deserialized.Channels[0].VoltageRangeVolts, roundTripped.Channels[0].VoltageRangeVolts, 6);
                Assert.Equal(deserialized.Channels[0].Coupling, roundTripped.Channels[0].Coupling);
                Assert.Equal(deserialized.Channels[0].ProbeMultiplier, roundTripped.Channels[0].ProbeMultiplier, 6);
                Assert.Equal(deserialized.Channels[0].CurrentScaleAmpsPerVolt, roundTripped.Channels[0].CurrentScaleAmpsPerVolt, 6);

                // Selecting the test resolves safety warnings FIRST and applies scope_setup_json to UI
                await vm.SelectGuidedScopeTestAsync(testItem.Id);

                Assert.True(vm.HasScopeSafetyWarnings);
                Assert.Equal(Visibility.Visible, vm.ScopeSafetyWarningsVisibility);
                Assert.NotEmpty(vm.ScopeSafetyWarnings);
                Assert.All(vm.ScopeSafetyWarnings, sw => Assert.False(string.IsNullOrWhiteSpace(sw.Text)));

                var builtFromUi = vm.BuildScopeCaptureRequest();
                Assert.Equal(deserialized.SampleRateHz, builtFromUi.SampleRateHz);
                Assert.Equal(deserialized.TimebaseSecondsPerDivision, builtFromUi.TimebaseSecondsPerDivision, 9);
                Assert.Equal(deserialized.TriggerMode, builtFromUi.TriggerMode);
                Assert.Equal(deserialized.TriggerChannel, builtFromUi.TriggerChannel);
                Assert.Equal(deserialized.TriggerLevelVolts, builtFromUi.TriggerLevelVolts, 6);
                Assert.Single(builtFromUi.Channels);
                Assert.Equal(deserialized.Channels[0].Channel, builtFromUi.Channels[0].Channel);
                Assert.Equal(deserialized.Channels[0].Coupling, builtFromUi.Channels[0].Coupling);
                Assert.Equal(deserialized.Channels[0].CurrentScaleAmpsPerVolt, builtFromUi.Channels[0].CurrentScaleAmpsPerVolt, 6);
            }

            // Solenoid/Valve Current test includes stored hydraulic pressure warning first (rank 0)
            await vm.SelectGuidedScopeTestAsync("gt-solenoid-current");
            Assert.Equal(SafetyWarningCategory.HydraulicPressure, vm.ScopeSafetyWarnings[0].Category);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task SimulatedCaptureForPresets_ProducesNonEmptyComparisonResult_WithOverallVerdictNaForTodoReferences()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"gse-scope-compare-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "gse-scope.db");

        try
        {
            var dataServices = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
            await dataServices.InitializeAsync();

            var scopeFactory = new ScopeDeviceFactory();
            await using var vm = new MainShellViewModel(
                dataServices,
                new DiagnosticAdapterFactory(),
                dispatcherQueue: null,
                scopeDeviceFactory: scopeFactory);

            await vm.ReloadDatabaseRecordsAsync();
            await vm.DiscoverScopeDevicesAsync();

            // Simulator is always discovered and marked IsSimulated
            Assert.NotEmpty(vm.AvailableScopeDevices);
            Assert.Contains(vm.AvailableScopeDevices, d => d.Kind == ScopeDeviceKind.Simulator && d.IsSimulated);

            // (b) Run guided tests against the simulator and verify non-empty ScopeComparisonResult + Overall == Na for TODO references
            string[] testIds =
            [
                "gt-crank-sensor",
                "gt-injector-current",
                "gt-alternator-ripple",
                "gt-starter-current",
                "gt-can-signal-quality",
                "gt-solenoid-current",
            ];

            foreach (var testId in testIds)
            {
                await vm.RunGuidedScopeTestAsync(testId);

                Assert.NotNull(vm.LastScopeCapture);
                Assert.True(vm.LastScopeCapture!.IsSimulated);
                Assert.True(vm.IsScopeSimulated);
                Assert.Equal(Visibility.Visible, vm.ScopeSimulatedBadgeVisibility);

                Assert.NotNull(vm.LastScopeComparisonResult);
                Assert.NotEmpty(vm.LastScopeComparisonResult!.Measurements);
                Assert.Equal(ScopeVerdict.Na, vm.LastScopeComparisonResult.Overall);
                Assert.Equal(ScopeVerdict.Na, vm.ScopeOverallVerdict);
                Assert.Equal("na", vm.ScopeOverallVerdictCode);
                Assert.Contains(WaveformComparer.TodoReason, vm.ScopeOverallVerdictDisplay);

                // Every measurement row carries SIMULATED provenance and never fabricates pass/fail on TODO expectations
                Assert.NotEmpty(vm.ScopeMeasurements);
                Assert.All(vm.ScopeMeasurements, row =>
                {
                    Assert.True(row.IsSimulated);
                    Assert.Equal(Visibility.Visible, row.SimulatedBadgeVisibility);
                    Assert.Equal(ScopeVerdict.Na, row.Verdict);
                    Assert.Equal("na", row.VerdictCode);
                });

                Assert.Contains(
                    vm.ScopeMeasurements,
                    row => row.IsTodoExpectation && row.ToleranceOrReasonDisplay == WaveformComparer.TodoReason);

                // Off-UI-thread decimated points are populated for the active channel
                int activeCh = vm.ScopeChannels.First(c => c.IsEnabled).Channel;
                var rawPoints = activeCh == 1 ? vm.ScopeChannel1RawPoints : vm.ScopeChannel2RawPoints;
                Assert.NotEmpty(rawPoints);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task SaveScopeCaptureToWorkOrder_WritesWfmAndPersistsWaveformWithSimulatorDeviceCode_AndReloadsFromDisk()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"gse-scope-save-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "gse-scope.db");

        try
        {
            var dataServices = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
            await dataServices.InitializeAsync();

            var scopeFactory = new ScopeDeviceFactory();
            await using var vm = new MainShellViewModel(
                dataServices,
                new DiagnosticAdapterFactory(),
                dispatcherQueue: null,
                scopeDeviceFactory: scopeFactory);

            await vm.ReloadDatabaseRecordsAsync();
            await vm.CreateActiveWorkOrderAsync();
            Assert.NotNull(vm.ActiveWorkOrder);

            // Run Crank Sensor guided test on the simulator
            await vm.RunGuidedScopeTestAsync("gt-crank-sensor");
            Assert.NotNull(vm.LastScopeCapture);

            // (c) Save capture to work order -> produces Waveform with scope_device code "simulator"
            var savedWaveform = await vm.SaveScopeCaptureToWorkOrderAsync();
            Assert.NotNull(savedWaveform);
            Assert.Equal(vm.ActiveWorkOrder!.Id, savedWaveform!.WorkOrderId);
            Assert.Equal(ScopeDeviceKind.Simulator, savedWaveform.ScopeDevice);
            Assert.Equal("simulator", DbEnum.ToCode(savedWaveform.ScopeDevice));
            Assert.Equal("gt-crank-sensor", savedWaveform.GuidedTestId);
            Assert.Equal("rw-crank-sensor", savedWaveform.ReferenceWaveformId);
            Assert.Equal($"waveforms/{savedWaveform.Id}.wfm", savedWaveform.DataPath);

            // Verify binary .wfm file exists on disk and round-trips via WaveformFile
            var fullWfmPath = vm.ResolveScopeDataFilePath(savedWaveform.DataPath);
            Assert.True(File.Exists(fullWfmPath));
            var diskCapture = await WaveformFile.ReadAsync(fullWfmPath);
            Assert.Equal(vm.LastScopeCapture!.SampleCount, diskCapture.SampleCount);
            Assert.Equal(vm.LastScopeCapture.SampleRateHz, diskCapture.SampleRateHz);
            Assert.True(diskCapture.Channels.ContainsKey(1));

            // Verify DB row in DataServices.Waveforms.GetByWorkOrderAsync
            var dbWaveforms = await dataServices.Waveforms.GetByWorkOrderAsync(vm.ActiveWorkOrder.Id);
            var persisted = Assert.Single(dbWaveforms);
            Assert.Equal(savedWaveform.Id, persisted.Id);
            Assert.Equal(ScopeDeviceKind.Simulator, persisted.ScopeDevice);
            Assert.Equal("simulator", DbEnum.ToCode(persisted.ScopeDevice));

            var persistedConfigs = ScopeJson.DeserializeChannelConfigs(persisted.ChannelConfigJson);
            var ch1Config = Assert.Single(persistedConfigs);
            Assert.Equal(1, ch1Config.Channel);
            Assert.Equal(5.0, ch1Config.VoltageRangeVolts, 6);

            var persistedMeasurements = ScopeJson.DeserializeMeasurements(persisted.MeasurementsJson);
            Assert.NotEmpty(persistedMeasurements);
            Assert.All(persistedMeasurements, m =>
            {
                Assert.True(m.IsSimulated);
                Assert.Equal(ScopeVerdict.Na, m.Verdict);
            });

            // Verify Saved Waveform Library in ViewModel lists the saved capture and reloads it from .wfm
            var libraryRow = Assert.Single(vm.WorkOrderWaveforms);
            Assert.Equal("simulator", libraryRow.ScopeDeviceCode);
            Assert.True(libraryRow.IsSimulated);

            // Clear current capture, then reload from the saved waveform library row
            vm.LastScopeCapture = null;
            Assert.False(vm.HasScopeCapture);

            await vm.LoadSavedWaveformAsync(libraryRow);
            Assert.True(vm.HasScopeCapture);
            Assert.NotNull(vm.LastScopeCapture);
            Assert.Equal(diskCapture.SampleCount, vm.LastScopeCapture!.SampleCount);
            Assert.NotEmpty(vm.ScopeChannel1RawPoints);
            Assert.NotEmpty(vm.ScopeMeasurements);
            Assert.Equal(ScopeVerdict.Na, vm.ScopeOverallVerdict);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task ReferenceOverlay_ZoomPan_Cursors_AndNumericExpectationVerdicts_WorkEndToEnd()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"gse-scope-overlay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "gse-scope.db");

        try
        {
            var dataServices = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
            await dataServices.InitializeAsync();

            await using var vm = new MainShellViewModel(
                dataServices,
                new DiagnosticAdapterFactory(),
                dispatcherQueue: null,
                scopeDeviceFactory: new ScopeDeviceFactory());

            await vm.ReloadDatabaseRecordsAsync();

            // Write a reference .wfm file under reference_waveforms/rw-custom-crank.wfm
            var refRelativePath = "reference_waveforms/rw-custom-crank.wfm";
            var refFullPath = vm.ResolveScopeDataFilePath(refRelativePath);
            var simForRef = new SimulatedScopeDevice();
            await simForRef.ConnectAsync();
            simForRef.ConfigurePreset(SimulatedSignalPreset.CrankSensor);
            await simForRef.StartCaptureAsync(simForRef.GetPresetCaptureRequest());
            var refCapture = await simForRef.ReadCaptureAsync();
            await WaveformFile.WriteAsync(refFullPath, refCapture);

            var customRef = new Core.Models.ReferenceWaveform
            {
                Id = "rw-custom-crank",
                Name = "Custom CKP Reference with Overlay",
                System = "engine",
                TestName = "Crank sensor",
                ExpectedJson = ScopeJson.SerializeReferenceExpectations(new ReferenceExpectations
                {
                    Measurements =
                    [
                        new MeasurementExpectation
                        {
                            Kind = MeasurementKind.Frequency,
                            Channel = 1,
                            Nominal = 200.0,
                            Tolerance = 5.0,
                            Unit = "Hz",
                        },
                        new MeasurementExpectation
                        {
                            Kind = MeasurementKind.PeakToPeak,
                            Channel = 1,
                            Min = 2.40,
                            Max = 2.60,
                            Unit = "V",
                        },
                    ],
                }),
                DataPath = refRelativePath,
                Source = WaveformSource.RealCapture,
                Citation = "Shop Capture Bench #1",
            };
            await dataServices.ReferenceWaveforms.AddAsync(customRef);

            var customTest = new Core.Models.GuidedTest
            {
                Id = "gt-custom-crank",
                Name = "Crank Sensor Custom Reference Test",
                System = "engine",
                SafetyWarningIds = "[\"sw-lockout-tagout\"]",
                ReferenceWaveformId = customRef.Id,
                ScopeSetupJson = ScopeJson.SerializeCaptureRequest(
                    SimulatedScopePresets.GetCanonicalRequest(SimulatedSignalPreset.CrankSensor)),
            };
            await dataServices.GuidedTests.AddAsync(customTest);

            await vm.ReloadScopeCatalogAsync();
            await vm.RunGuidedScopeTestAsync("gt-custom-crank");

            // Reference .wfm overlay loaded and decimated
            Assert.True(vm.HasReferenceScopeCapture);
            Assert.Equal(Visibility.Visible, vm.ScopeReferenceOverlayVisibility);
            Assert.NotEmpty(vm.ScopeReferenceOverlayRawPoints);

            // Numeric expectations pass against the simulated CKP signal (~200 Hz, ~2.5 Vpp)
            Assert.Equal(ScopeVerdict.Pass, vm.ScopeOverallVerdict);
            Assert.Equal("pass", vm.ScopeOverallVerdictCode);

            var freqRow = Assert.Single(vm.ScopeMeasurements, m => m.Kind == MeasurementKind.Frequency);
            Assert.Equal(ScopeVerdict.Pass, freqRow.Verdict);
            Assert.Contains("Nominal 200 ± 5 Hz", freqRow.ToleranceOrReasonDisplay);

            var vppRow = Assert.Single(vm.ScopeMeasurements, m => m.Kind == MeasurementKind.PeakToPeak);
            Assert.Equal(ScopeVerdict.Pass, vppRow.Verdict);
            Assert.Contains("Range 2.4 – 2.6 V", vppRow.ToleranceOrReasonDisplay);

            // Zoom & Pan + Cursors update readouts cleanly
            vm.ScopeZoomIn();
            Assert.Equal(2.0, vm.ScopeZoomFactor);
            vm.ScopePanPercent = 30.0;
            await vm.RefreshScopeWaveformRenderAsync();
            Assert.NotEmpty(vm.ScopeChannel1RawPoints);

            vm.ScopeCursorAPercent = 20.0;
            vm.ScopeCursorBPercent = 80.0;
            Assert.Contains("Cursor A (CH1):", vm.ScopeCursorALabel);
            Assert.Contains("Cursor B (CH1):", vm.ScopeCursorBLabel);
            Assert.Contains("Rulers (CH1): Δt =", vm.ScopeCursorDeltaLabel);

            vm.ScopeResetZoom();
            Assert.Equal(1.0, vm.ScopeZoomFactor);
            Assert.Equal(0.0, vm.ScopePanPercent);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }
}
