using GseTechPro.Core.Contracts;
using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.Scope.Tests;

/// <summary>Phase 10D.4: pressure transducer channel scaling.</summary>
public sealed class PressureTransducerTests
{
    // Example numbers for the tests only (a 0.5–4.5 V, 0–5000 psi transducer), not a spec of any part.
    private static readonly ScopePressureScaling Psi5000 = new()
    {
        Unit = "psi", ZeroOutputVolts = 0.5, ZeroPressure = 0, FullScaleOutputVolts = 4.5, FullScalePressure = 5000,
        Source = "Test transducer datasheet",
    };

    [Fact]
    public void Validate_NeedsUnitDistinctPointsAndSource()
    {
        Assert.Null(PressureTransducer.Validate(Psi5000));
        Assert.Contains("No pressure scaling", PressureTransducer.Validate(null));
        Assert.Contains("unit", PressureTransducer.Validate(Psi5000 with { Unit = "atm" }));
        Assert.Contains("all four", PressureTransducer.Validate(Psi5000 with { FullScalePressure = double.NaN }));
        Assert.Contains("output voltages must differ", PressureTransducer.Validate(Psi5000 with { FullScaleOutputVolts = 0.5 }));
        Assert.Contains("pressures must differ", PressureTransducer.Validate(Psi5000 with { FullScalePressure = 0 }));
        Assert.Contains("make/model", PressureTransducer.Validate(Psi5000 with { Source = " " }));
    }

    [Fact]
    public void ToPressure_IsTheLineThroughTheTwoPoints()
    {
        Assert.Equal(0.0, PressureTransducer.ToPressure(Psi5000, 0.5), 9);
        Assert.Equal(5000.0, PressureTransducer.ToPressure(Psi5000, 4.5), 9);
        Assert.Equal(2500.0, PressureTransducer.ToPressure(Psi5000, 2.5), 9);
        Assert.Equal(-625.0, PressureTransducer.ToPressure(Psi5000, 0.0), 9); // below the low point: extrapolated, flagged by Summarize

        var bar = new ScopePressureScaling { Unit = "bar", ZeroOutputVolts = 1, ZeroPressure = -1, FullScaleOutputVolts = 5, FullScalePressure = 9, Source = "x" };
        Assert.Equal(4.0, PressureTransducer.ToPressure(bar, 3), 9);
    }

    [Fact]
    public async Task Summarize_SimulatedTransducer_AndOutOfSpanCounts()
    {
        var device = new SimulatedScopeDevice();
        await device.ConnectAsync();
        device.ConfigurePreset(SimulatedSignalPreset.PressureTransducer);
        await device.StartCaptureAsync(SimulatedScopePresets.GetCanonicalRequest(SimulatedSignalPreset.PressureTransducer));
        var cap = await device.ReadCaptureAsync();

        var s = PressureTransducer.Summarize(Psi5000, cap.Channels[1]);
        Assert.Equal("psi", s.Unit);
        Assert.InRange(s.Min, -10, 10);        // 0.5 V at rest
        Assert.InRange(s.Max, 3850, 3980);     // about 3.63 V peak
        Assert.Equal(0, s.BelowSpanCount);
        Assert.Equal(0, s.AboveSpanCount);
        Assert.StartsWith("min ", s.Text);

        // 0 V (open circuit / no supply) and 5 V samples are counted, never hidden.
        var odd = new[] { 0.0, 0.0, 2.5, 5.0 };
        var o = PressureTransducer.Summarize(Psi5000, odd);
        Assert.Equal(2, o.BelowSpanCount);
        Assert.Equal(1, o.AboveSpanCount);
        Assert.Contains("check the transducer supply", o.Text);

        // A falling transducer (output drops as pressure rises): min/max and below/above follow the pressure.
        var falling = Psi5000 with { ZeroOutputVolts = 4.5, FullScaleOutputVolts = 0.5 };
        var f = PressureTransducer.Summarize(falling, odd);
        Assert.Equal(-625.0, f.Min, 6);
        Assert.Equal(5625.0, f.Max, 6);
        Assert.Equal(1, f.BelowSpanCount);
        Assert.Equal(2, f.AboveSpanCount);
    }

    [Fact]
    public void ChannelConfigJson_LeavesPressureOutWhenNull_AndRoundTripsIt()
    {
        var plain = ScopeJson.SerializeChannelConfigs([new ScopeChannelConfig { Channel = 1, VoltageRangeVolts = 5 }]);
        Assert.DoesNotContain("pressure", plain);

        var json = ScopeJson.SerializeChannelConfigs([new ScopeChannelConfig { Channel = 1, VoltageRangeVolts = 5, Pressure = Psi5000 }]);
        Assert.Contains("\"full_scale_pressure\":5000", json);
        var back = ScopeJson.DeserializeChannelConfigs(json).Single();
        Assert.Equal(Psi5000, back.Pressure);
    }
}
