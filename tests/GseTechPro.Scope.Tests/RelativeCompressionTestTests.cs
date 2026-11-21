using GseTechPro.Core.Contracts;
using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.Scope.Tests;

/// <summary>Phase 10D.3b: one-tap relative compression test.</summary>
public sealed class RelativeCompressionTestTests
{
    private static readonly RelativeCompressionInputs FourCylFourStroke = new() { Cylinders = 4, StrokesPerCycle = 4 };

    private static async Task<ScopeCapture> SimulatedAsync(SimulatedSignalPreset preset, bool withClamp = true)
    {
        var device = new SimulatedScopeDevice();
        await device.ConnectAsync();
        device.ConfigurePreset(preset);
        var request = SimulatedScopePresets.GetCanonicalRequest(preset);
        if (!withClamp)
            request = request with { Channels = request.Channels.Where(c => c.Channel == 1).ToList() };
        await device.StartCaptureAsync(request);
        return await device.ReadCaptureAsync();
    }

    private static double[] Amps(ScopeCapture cap) => cap.Channels[2].Select(v => v * 1000.0).ToArray();

    [Fact]
    public async Task WeakCylinder_IsFound_WithClamp_AndCrankingSpeedFromTheCompressions()
    {
        var cap = await SimulatedAsync(SimulatedSignalPreset.RelativeCompression);
        var r = RelativeCompressionTest.Analyse(cap.Channels[1], Amps(cap), cap.SampleRateHz, FourCylFourStroke, true);

        Assert.True(r.Success, r.Message);
        Assert.True(r.IsSimulated);
        Assert.Equal(4, r.PositionPercent.Count);
        Assert.Equal(0, r.CompressionsMeasured % 4);
        Assert.True(r.CompressionsMeasured >= 20, $"{r.CompressionsMeasured}");
        Assert.InRange(r.CrankingRpm, 195, 205); // simulator cranks at 200 rpm
        var sorted = r.PositionPercent.OrderBy(p => p).ToArray();
        Assert.InRange(sorted[0], 66, 74);       // the simulator's weak cylinder (70 %)
        Assert.All(sorted.Skip(1), p => Assert.InRange(p, 94, 100.0001));
        Assert.Equal(100.0, sorted[^1], 6);

        // The weakest position follows the simulator's firing sequence: three ahead of it are 0.97, 0.99, 1.0.
        int weak = Array.IndexOf(r.PositionPercent.ToArray(), sorted[0]);
        Assert.InRange(r.PositionPercent[(weak + 1) % 4], 98, 100.0001);

        var spread = r.Rows.Single(x => x.Parameter == "Spread (strongest − weakest)");
        Assert.Equal(OneTapVerdict.None, spread.Verdict);
        Assert.Contains("No maximum entered", spread.Comment);
        Assert.Contains("not necessarily cylinder 1", r.Rows.Single(x => x.Parameter == "Position 1").Comment);
    }

    [Fact]
    public async Task VoltageOnly_FindsTheSameWeakPosition()
    {
        var withClamp = await SimulatedAsync(SimulatedSignalPreset.RelativeCompression);
        var a = RelativeCompressionTest.Analyse(withClamp.Channels[1], Amps(withClamp), withClamp.SampleRateHz, FourCylFourStroke, true);

        var cap = await SimulatedAsync(SimulatedSignalPreset.RelativeCompression, withClamp: false);
        var v = RelativeCompressionTest.Analyse(cap.Channels[1], null, cap.SampleRateHz, FourCylFourStroke, true);

        Assert.True(v.Success, v.Message);
        Assert.InRange(v.PositionPercent.Min(), 62, 78);
        Assert.Contains("battery voltage dips", v.Rows.Single(x => x.Parameter == "Compressions measured").Comment);
        Assert.Equal(Array.IndexOf(a.PositionPercent.ToArray(), a.PositionPercent.Min()),
                     Array.IndexOf(v.PositionPercent.ToArray(), v.PositionPercent.Min()));
    }

    [Fact]
    public async Task Verdict_OnlyWithACitedMaximum_AndTwoStrokeHalvesNothingButSpeed()
    {
        var cap = await SimulatedAsync(SimulatedSignalPreset.RelativeCompression);
        var amps = Amps(cap);
        OneTapResultRow Spread(RelativeCompressionInputs i) =>
            RelativeCompressionTest.Analyse(cap.Channels[1], amps, cap.SampleRateHz, i, true)
                .Rows.Single(x => x.Parameter == "Spread (strongest − weakest)");

        Assert.Equal(OneTapVerdict.None, Spread(FourCylFourStroke with { MaximumSpreadPercent = 50 }).Verdict);
        Assert.Equal(OneTapVerdict.Pass, Spread(FourCylFourStroke with { MaximumSpreadPercent = 50, MaximumSpreadSource = "Test manual p.2" }).Verdict);
        var fail = Spread(FourCylFourStroke with { MaximumSpreadPercent = 10, MaximumSpreadSource = "Test manual p.2" });
        Assert.Equal(OneTapVerdict.Fail, fail.Verdict);
        Assert.Contains("Test manual p.2", fail.Comment);

        var four = RelativeCompressionTest.Analyse(cap.Channels[1], amps, cap.SampleRateHz, FourCylFourStroke, true);
        var two = RelativeCompressionTest.Analyse(cap.Channels[1], amps, cap.SampleRateHz, FourCylFourStroke with { StrokesPerCycle = 2 }, true);
        Assert.Equal(four.CrankingRpm / 2.0, two.CrankingRpm, 6);
        Assert.Equal(four.PositionPercent, two.PositionPercent);
    }

    [Fact]
    public async Task MissingInputs_ShortCrank_AndEngineThatStarts_GivePlainMessages()
    {
        var cap = await SimulatedAsync(SimulatedSignalPreset.RelativeCompression);
        var amps = Amps(cap);
        Assert.Contains("number of cylinders",
            RelativeCompressionTest.Analyse(cap.Channels[1], amps, cap.SampleRateHz, new RelativeCompressionInputs { StrokesPerCycle = 4 }, true).Message);
        Assert.Contains("4-stroke or 2-stroke",
            RelativeCompressionTest.Analyse(cap.Channels[1], amps, cap.SampleRateHz, new RelativeCompressionInputs { Cylinders = 4 }, true).Message);

        // The battery test's engine starts after 1.5 s: too few compressions for 8 cylinders (two cycles = 16).
        var started = await SimulatedAsync(SimulatedSignalPreset.BatteryCranking);
        var r = RelativeCompressionTest.Analyse(started.Channels[1], Amps(started), started.SampleRateHz,
            new RelativeCompressionInputs { Cylinders = 8, StrokesPerCycle = 4 }, true);
        Assert.False(r.Success);
        Assert.Contains("at least 16", r.Message);
        Assert.Empty(r.Rows);

        var flat = Enumerable.Repeat(12.6, 50_000).ToArray();
        Assert.Contains("No crank found", RelativeCompressionTest.Analyse(flat, null, 10_000, FourCylFourStroke, false).Message);
    }
}
