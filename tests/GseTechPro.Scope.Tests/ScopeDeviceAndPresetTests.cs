using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using Xunit;

namespace GseTechPro.Scope.Tests;

public sealed class ScopeDeviceAndPresetTests
{
    [Fact]
    public async Task SimulatedScopeDevice_SameSeed_ProducesIdenticalSamplesAcrossRuns()
    {
        var sim1 = new SimulatedScopeDevice(seed: 98765, SimulatedSignalPreset.CrankSensor);
        var sim2 = new SimulatedScopeDevice(seed: 98765, SimulatedSignalPreset.CrankSensor);

        await sim1.ConnectAsync();
        await sim2.ConnectAsync();

        var request = SimulatedScopePresets.GetCanonicalRequest(SimulatedSignalPreset.CrankSensor);

        await sim1.StartCaptureAsync(request);
        await sim2.StartCaptureAsync(request);

        ScopeCapture cap1 = await sim1.ReadCaptureAsync();
        ScopeCapture cap2 = await sim2.ReadCaptureAsync();

        Assert.True(cap1.IsSimulated);
        Assert.True(cap2.IsSimulated);
        Assert.Equal(cap1.SampleCount, cap2.SampleCount);
        Assert.Equal(cap1.Channels[1], cap2.Channels[1]);
    }

    [Fact]
    public async Task SimulatedScopeDevice_CrankSensorPreset_MatchesCanonicalTableMeasurements()
    {
        // Canonical table: CH1, DC, 1x, 5 V, 5 ms/div, 1 MS/s, rising CH1 @ 2.0 V -> ~2.5 Vpp, ~200 Hz
        var sim = new SimulatedScopeDevice(seed: 12345);
        await sim.ConnectAsync();
        sim.ConfigurePreset(SimulatedSignalPreset.CrankSensor);

        var request = sim.GetPresetCaptureRequest();
        Assert.Equal(1, request.Channels[0].Channel);
        Assert.Equal(ScopeCoupling.Dc, request.Channels[0].Coupling);
        Assert.Equal(1.0, request.Channels[0].ProbeMultiplier);
        Assert.Equal(5.0, request.Channels[0].VoltageRangeVolts);
        Assert.Equal(0.005, request.TimebaseSecondsPerDivision);
        Assert.Equal(1_000_000, request.SampleRateHz);
        Assert.Equal(ScopeTriggerMode.Rising, request.TriggerMode);
        Assert.Equal(1, request.TriggerChannel);
        Assert.Equal(2.0, request.TriggerLevelVolts);

        await sim.StartCaptureAsync(request);
        var measurements = await sim.ComputeMeasurementsAsync(1);

        Assert.All(measurements, m => Assert.True(m.IsSimulated));

        var freq = GetByKind(measurements, MeasurementKind.Frequency);
        Assert.Equal(ScopeMeasurementStatus.Computed, freq.Status);
        Assert.InRange(freq.Value!.Value, 195.0, 205.0);

        var vpp = GetByKind(measurements, MeasurementKind.PeakToPeak);
        Assert.Equal(ScopeMeasurementStatus.Computed, vpp.Status);
        Assert.InRange(vpp.Value!.Value, 2.45, 2.55);
    }

    [Fact]
    public async Task SimulatedScopeDevice_InjectorCurrentPreset_MatchesCanonicalTableMeasurements()
    {
        // Canonical table: CH2, DC, clamp 10 A/V, 1 V, 1 ms/div, 1 MS/s, rising CH2 @ 0.5 V -> ~1.0 V peak (~10 A)
        var sim = new SimulatedScopeDevice();
        await sim.ConnectAsync();
        sim.ConfigurePreset(SimulatedSignalPreset.InjectorCurrent);

        var request = sim.GetPresetCaptureRequest();
        await sim.StartCaptureAsync(request);
        var measurements = await sim.ComputeMeasurementsAsync(2);

        var peak = GetByKind(measurements, MeasurementKind.Peak);
        Assert.Equal(ScopeMeasurementStatus.Computed, peak.Status);
        Assert.InRange(peak.Value!.Value, 0.98, 1.02);

        var current = GetByKind(measurements, MeasurementKind.Current);
        Assert.Equal(ScopeMeasurementStatus.Computed, current.Status);
        Assert.InRange(current.Value!.Value, 9.8, 10.2);
    }

    [Fact]
    public async Task SimulatedScopeDevice_AlternatorRipplePreset_MatchesCanonicalTableMeasurements()
    {
        // Canonical table: CH1, AC, 1x, 1 V, 10 ms/div, 100 kS/s, rising CH1 @ 0.05 V -> ~0.2 Vpp (DC removed by AC coupling)
        var sim = new SimulatedScopeDevice();
        await sim.ConnectAsync();
        sim.ConfigurePreset(SimulatedSignalPreset.AlternatorRipple);

        var request = sim.GetPresetCaptureRequest();
        await sim.StartCaptureAsync(request);
        var measurements = await sim.ComputeMeasurementsAsync(1);

        var vpp = GetByKind(measurements, MeasurementKind.PeakToPeak);
        Assert.Equal(ScopeMeasurementStatus.Computed, vpp.Status);
        Assert.InRange(vpp.Value!.Value, 0.19, 0.21);

        var peak = GetByKind(measurements, MeasurementKind.Peak);
        Assert.Equal(ScopeMeasurementStatus.Computed, peak.Status);
        Assert.InRange(peak.Value!.Value, 0.09, 0.11);

        var ripple = GetByKind(measurements, MeasurementKind.Ripple);
        Assert.Equal(ScopeMeasurementStatus.Computed, ripple.Status);
        Assert.InRange(ripple.Value!.Value, 0.065, 0.076);
    }

    [Fact]
    public async Task SimulatedScopeDevice_StarterCurrentPreset_MatchesCanonicalTableMeasurements()
    {
        // Canonical table: CH2, DC, clamp 100 A/V, 1 V, 100 ms/div, 100 kS/s, rising CH2 @ 0.2 V -> peak ~3 V (~300 A)
        var sim = new SimulatedScopeDevice();
        await sim.ConnectAsync();
        sim.ConfigurePreset(SimulatedSignalPreset.StarterCurrent);

        var request = sim.GetPresetCaptureRequest();
        await sim.StartCaptureAsync(request);
        var measurements = await sim.ComputeMeasurementsAsync(2);

        var peak = GetByKind(measurements, MeasurementKind.Peak);
        Assert.Equal(ScopeMeasurementStatus.Computed, peak.Status);
        Assert.InRange(peak.Value!.Value, 2.95, 3.05);

        var current = GetByKind(measurements, MeasurementKind.Current);
        Assert.Equal(ScopeMeasurementStatus.Computed, current.Status);
        Assert.InRange(current.Value!.Value, 295.0, 305.0);
    }

    [Fact]
    public async Task SimulatedScopeDevice_CanSignalQualityPreset_MatchesCanonicalTableMeasurements()
    {
        // Canonical table: CH1, DC, 1x, 5 V, 50 µs/div, 20 MS/s, rising CH1 @ 2.5 V -> recessive ~2.5 V / dominant ~0 V
        var sim = new SimulatedScopeDevice();
        await sim.ConnectAsync();
        sim.ConfigurePreset(SimulatedSignalPreset.CanSignalQuality);

        var request = sim.GetPresetCaptureRequest();
        await sim.StartCaptureAsync(request);
        var measurements = await sim.ComputeMeasurementsAsync(1);

        var vpp = GetByKind(measurements, MeasurementKind.PeakToPeak);
        Assert.Equal(ScopeMeasurementStatus.Computed, vpp.Status);
        Assert.InRange(vpp.Value!.Value, 2.45, 2.55);

        var freq = GetByKind(measurements, MeasurementKind.Frequency);
        Assert.Equal(ScopeMeasurementStatus.Computed, freq.Status);
        Assert.InRange(freq.Value!.Value, 120_000.0, 130_000.0);
    }

    [Fact]
    public async Task SimulatedScopeDevice_SolenoidCurrentPreset_MatchesCanonicalTableMeasurements()
    {
        // Canonical table: CH2, DC, clamp 10 A/V, 1 V, 10 ms/div, 100 kS/s, rising CH2 @ 0.5 V -> peak ~1.5 V (~15 A)
        var sim = new SimulatedScopeDevice();
        await sim.ConnectAsync();
        sim.ConfigurePreset(SimulatedSignalPreset.SolenoidCurrent);

        var request = sim.GetPresetCaptureRequest();
        await sim.StartCaptureAsync(request);
        var measurements = await sim.ComputeMeasurementsAsync(2);

        var peak = GetByKind(measurements, MeasurementKind.Peak);
        Assert.Equal(ScopeMeasurementStatus.Computed, peak.Status);
        Assert.InRange(peak.Value!.Value, 1.48, 1.52);

        var current = GetByKind(measurements, MeasurementKind.Current);
        Assert.Equal(ScopeMeasurementStatus.Computed, current.Status);
        Assert.InRange(current.Value!.Value, 14.8, 15.2);
    }

    [Fact]
    public async Task ScopeDeviceFactory_DiscoverAndOpenSimulator_AlwaysIncludesSim1AndWorksWithoutVendorDlls()
    {
        var factory = new ScopeDeviceFactory();
        IReadOnlyList<ScopeDeviceInfo> devices = await factory.DiscoverAsync();

        Assert.NotEmpty(devices);
        ScopeDeviceInfo simInfo = Assert.Single(devices, d => d.Kind == ScopeDeviceKind.Simulator);
        Assert.Equal("sim-1", simInfo.Id);
        Assert.Equal("Simulator", simInfo.Name);
        Assert.Equal(4, simInfo.ChannelCount);
        Assert.True(simInfo.IsSimulated);

        var scope = await factory.OpenAsync(simInfo);
        Assert.True(scope.IsSimulated);
        Assert.Equal(ScopeDeviceKind.Simulator, scope.Kind);
    }

    [Fact]
    public async Task HardwareWrappers_WhenVendorSdkOrDeviceAbsent_FailGracefullyWithoutCrashing()
    {
        var pico = new PicoScopeDevice();
        Assert.False(pico.IsSimulated);
        var picoEx = await Assert.ThrowsAsync<InvalidOperationException>(() => pico.ConnectAsync());
        Assert.Contains("PicoSDK", picoEx.Message, StringComparison.OrdinalIgnoreCase);

        if (!Hantek6074BeDevice.IsSdkPresent())
        {
            var hantek = new Hantek6074BeDevice(0);
            Assert.False(hantek.IsSimulated);
            var hantekEx = await Assert.ThrowsAsync<InvalidOperationException>(() => hantek.ConnectAsync());
            Assert.Contains("not found", hantekEx.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Hantek6074BeDevice_ConvertRawCountsToVolts_AppliesCalibrationAndProbeMultiplier()
    {
        // LeverPos = 128 -> zeroCode = 255 - 128 = 127
        // At 1.0 V/div, 10x probe, raw = 127 -> 0.0 V; raw = 255 -> (128 * 8 * 1.0 / 255) * 10.0 = 40.15686 V
        ushort[] raw = [127, 191, 255];
        var (volts, clipped) = Hantek6074BeDevice.ConvertRawCountsToVolts(
            raw,
            sampleCount: 3,
            leverPos: 128,
            voltDivVolts: 1.0,
            probeMultiplier: 10.0,
            amplitudeCalibrationFactor: 1.0);

        Assert.True(clipped); // 255 hits ADC ceiling
        Assert.Equal(0.0, volts[0], precision: 6);
        Assert.InRange(volts[1], 20.0, 20.15);
        Assert.InRange(volts[2], 40.1, 40.2);
    }

    [Fact]
    public async Task SimulatedScopeDevice_ProbeMultiplierAndNoTrigger_BehavesPerContract()
    {
        var sim = new SimulatedScopeDevice(seed: 42, SimulatedSignalPreset.Sine);
        await sim.ConnectAsync();

        // 10x probe on a ±2.5 V (5 Vpp) 1 kHz sine wave => ±25 V (50 Vpp)
        var request10x = new ScopeCaptureRequest
        {
            Channels =
            [
                new ScopeChannelConfig
                {
                    Channel = 1,
                    Coupling = ScopeCoupling.Dc,
                    ProbeMultiplier = 10.0,
                    VoltageRangeVolts = 50.0,
                },
            ],
            TimebaseSecondsPerDivision = 0.001,
            SampleRateHz = 100_000,
            TriggerMode = ScopeTriggerMode.Rising,
            TriggerChannel = 1,
            TriggerLevelVolts = 0.0,
        };

        await sim.StartCaptureAsync(request10x);
        var meas10x = await sim.ComputeMeasurementsAsync(1);
        var vpp10x = GetByKind(meas10x, MeasurementKind.PeakToPeak);
        Assert.Equal(ScopeMeasurementStatus.Computed, vpp10x.Status);
        Assert.InRange(vpp10x.Value!.Value, 49.5, 50.5);

        // Unreachable trigger level (500 V) => NoTrigger status
        var untrigRequest = request10x with { TriggerLevelVolts = 500.0 };
        await sim.StartCaptureAsync(untrigRequest);
        var untrigMeas = await sim.ComputeMeasurementsAsync(1);
        Assert.Equal(ScopeMeasurementStatus.NoTrigger, GetByKind(untrigMeas, MeasurementKind.Frequency).Status);
        Assert.Null(GetByKind(untrigMeas, MeasurementKind.Frequency).Value);
    }

    private static ScopeMeasurement GetByKind(IEnumerable<ScopeMeasurement> measurements, MeasurementKind kind) =>
        measurements.Single(m => m.Kind == kind);
}
