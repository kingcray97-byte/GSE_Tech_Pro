using GseTechPro.Core.Contracts;
using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.Scope.Tests;

/// <summary>Phase 10D.3c: one-tap cylinder balance from a crank sensor.</summary>
public sealed class CylinderBalanceTestTests
{
    private static readonly CylinderBalanceInputs Engine = new()
    {
        Cylinders = 4, StrokesPerCycle = 4, WheelTeeth = 60, MissingTeeth = 2,
    };

    private static async Task<ScopeCapture> SimulatedAsync()
    {
        var device = new SimulatedScopeDevice();
        await device.ConnectAsync();
        device.ConfigurePreset(SimulatedSignalPreset.CylinderBalance);
        await device.StartCaptureAsync(SimulatedScopePresets.GetCanonicalRequest(SimulatedSignalPreset.CylinderBalance));
        return await device.ReadCaptureAsync();
    }

    [Fact]
    public async Task WeakCylinder_IsFound_FromCrankSpeed()
    {
        var cap = await SimulatedAsync();
        var r = CylinderBalanceTest.Analyse(cap.Channels[1], cap.SampleRateHz, Engine, true, 20.0);

        Assert.True(r.Success, r.Message);
        Assert.True(r.IsSimulated);
        Assert.InRange(r.MeanRpm, 795, 805);         // simulator idles at 800 rpm
        Assert.Equal(0, r.FiringsMeasured % 4);
        Assert.True(r.FiringsMeasured >= 40, $"{r.FiringsMeasured}");
        var sorted = r.PositionPercent.OrderBy(p => p).ToArray();
        Assert.InRange(sorted[0], 54, 66);           // simulator's weak cylinder: 60 %
        Assert.All(sorted.Skip(1), p => Assert.InRange(p, 92, 100.0001));
        Assert.InRange(double.Parse(r.Rows.Single(x => x.Parameter == "Speed change per firing").Value.Split(' ')[0],
            System.Globalization.CultureInfo.InvariantCulture), 20, 32); // ±15 rpm swing × mean height
        Assert.Contains("not necessarily cylinder 1", r.Rows.Single(x => x.Parameter == "Position 1").Comment);
        Assert.Equal(OneTapVerdict.None, r.Rows.Single(x => x.Parameter.StartsWith("Spread")).Verdict);
        Assert.DoesNotContain(r.Rows, x => x.Parameter.EndsWith("over range"));
    }

    [Fact]
    public async Task WrongEngineOrWheelInputs_AreReported_NotMeasured()
    {
        var cap = await SimulatedAsync();
        double rate = cap.SampleRateHz;
        var v = cap.Channels[1];

        // 6 cylinders 4-stroke would fire 3 times per revolution; the signal has 2.
        Assert.Contains("Check the cylinders",
            CylinderBalanceTest.Analyse(v, rate, Engine with { Cylinders = 6 }, true).Message);
        // 2-stroke 4 cylinders would fire 4 times per revolution.
        Assert.Contains("Check the cylinders",
            CylinderBalanceTest.Analyse(v, rate, Engine with { StrokesPerCycle = 2 }, true).Message);
        // Gap present but no missing teeth entered.
        Assert.Contains("0 missing teeth",
            CylinderBalanceTest.Analyse(v, rate, Engine with { MissingTeeth = 0 }, true).Message);
        // Required inputs.
        Assert.Contains("tooth count", CylinderBalanceTest.Analyse(v, rate, Engine with { WheelTeeth = 0 }, true).Message);
        Assert.Contains("number of cylinders", CylinderBalanceTest.Analyse(v, rate, Engine with { Cylinders = 0 }, true).Message);
        Assert.Contains("4-stroke or 2-stroke", CylinderBalanceTest.Analyse(v, rate, Engine with { StrokesPerCycle = 3 }, true).Message);
    }

    [Fact]
    public async Task Verdict_OnlyWithACitedMaximum()
    {
        var cap = await SimulatedAsync();
        OneTapResultRow Spread(CylinderBalanceInputs i) =>
            CylinderBalanceTest.Analyse(cap.Channels[1], cap.SampleRateHz, i, true).Rows.Single(x => x.Parameter.StartsWith("Spread"));

        Assert.Equal(OneTapVerdict.None, Spread(Engine with { MaximumSpreadPercent = 50 }).Verdict);
        Assert.Equal(OneTapVerdict.Pass, Spread(Engine with { MaximumSpreadPercent = 50, MaximumSpreadSource = "Test manual p.3" }).Verdict);
        Assert.Equal(OneTapVerdict.Fail, Spread(Engine with { MaximumSpreadPercent = 20, MaximumSpreadSource = "Test manual p.3" }).Verdict);
    }

    [Fact]
    public void NoSignal_SteadySpeed_AndShortCapture_GivePlainMessages()
    {
        Assert.Contains("No crank sensor signal",
            CylinderBalanceTest.Analyse(Enumerable.Repeat(0.3, 10_000).ToArray(), 100_000, Engine, false).Message);

        // A perfectly even 60-tooth wheel with no gap (0 missing): speed never changes per firing.
        const double rate = 100_000;
        var even = Enumerable.Range(0, 200_000).Select(i => (i / rate * 800.0) % 1.0 < 0.5 ? 2.75 : 0.25).ToArray();
        Assert.Contains("too steady",
            CylinderBalanceTest.Analyse(even, rate, Engine with { MissingTeeth = 0 }, false).Message);

        var shortCap = even.Take(5_000).ToArray(); // 50 ms: 40 teeth
        Assert.Contains("two revolutions",
            CylinderBalanceTest.Analyse(shortCap, rate, Engine with { MissingTeeth = 0 }, false).Message);
    }
}
