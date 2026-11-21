using GseTechPro.Core.Contracts;
using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.Scope.Tests;

/// <summary>Phase 10D.3a: one-tap battery / cranking test.</summary>
public sealed class CrankingTestTests
{
    private static async Task<ScopeCapture> SimulatedCaptureAsync(bool withClamp)
    {
        var device = new SimulatedScopeDevice();
        await device.ConnectAsync();
        var request = SimulatedScopePresets.GetCanonicalRequest(SimulatedSignalPreset.BatteryCranking);
        if (!withClamp)
            request = request with { Channels = request.Channels.Where(c => c.Channel == 1).ToList() };
        Assert.Equal(SimulatedSignalPreset.BatteryCranking, SimulatedScopePresets.TryMatchFromCaptureRequest(request));
        await device.StartCaptureAsync(request);
        return await device.ReadCaptureAsync();
    }

    private static OneTapResultRow Row(CrankingTestResult r, string parameter) =>
        r.Rows.Single(x => x.Parameter == parameter);

    private static double Number(OneTapResultRow row) =>
        double.Parse(row.Value.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task SimulatedCrank_WithClamp_MeasuresVoltageCurrentAndTime()
    {
        var cap = await SimulatedCaptureAsync(withClamp: true);
        var amps = cap.Channels[2].Select(v => v * 1000.0).ToArray();
        var r = CrankingTest.Analyse(cap.Channels[1], amps, cap.SampleRateHz, new CrankingTestInputs(), isSimulated: true, 20.0, 1000.0);

        Assert.True(r.Success, r.Message);
        Assert.True(r.IsSimulated);
        Assert.Equal(0.8, r.CrankStartSeconds, 2);
        Assert.InRange(r.CrankEndSeconds, 2.3, 2.6);
        Assert.Equal(12.6, Number(Row(r, "Resting voltage")), 1);
        Assert.InRange(Number(Row(r, "Lowest voltage (inrush)")), 8.3, 8.6);
        Assert.InRange(Number(Row(r, "Cranking voltage")), 9.9, 10.4);
        Assert.InRange(Number(Row(r, "Peak current (inrush)")), 600, 640);
        Assert.InRange(Number(Row(r, "Cranking current")), 190, 230);
        Assert.InRange(Number(Row(r, "Voltage after cranking")), 14.1, 14.3);
        // No limit entered: measured, but no verdict anywhere.
        Assert.All(r.Rows, x => Assert.Equal(OneTapVerdict.None, x.Verdict));
        Assert.Contains("No minimum entered", Row(r, "Lowest cranking voltage").Comment);
        Assert.DoesNotContain(r.Rows, x => x.Parameter.EndsWith("over range"));
    }

    [Fact]
    public async Task SimulatedCrank_VoltageOnly_FindsCrankFromTheVoltageDrop()
    {
        var cap = await SimulatedCaptureAsync(withClamp: false);
        var r = CrankingTest.Analyse(cap.Channels[1], null, cap.SampleRateHz, new CrankingTestInputs(), isSimulated: true);

        Assert.True(r.Success, r.Message);
        Assert.Equal(0.8, r.CrankStartSeconds, 2);
        Assert.InRange(r.CrankEndSeconds, 2.3, 2.6);
        Assert.Equal("—", Row(r, "Current").Value);
        Assert.InRange(Number(Row(r, "Cranking time")), 1.5, 1.8);
    }

    [Fact]
    public async Task Verdict_OnlyWithACitedMinimum()
    {
        var cap = await SimulatedCaptureAsync(withClamp: true);
        var amps = cap.Channels[2].Select(v => v * 1000.0).ToArray();

        // Limit without a source: no verdict, and the comment says what is missing.
        var noSource = CrankingTest.Analyse(cap.Channels[1], amps, cap.SampleRateHz,
            new CrankingTestInputs { MinimumCrankingVolts = 9.0 }, true);
        Assert.Equal(OneTapVerdict.None, Row(noSource, "Lowest cranking voltage").Verdict);
        Assert.Contains("without its source", Row(noSource, "Lowest cranking voltage").Comment);

        var pass = CrankingTest.Analyse(cap.Channels[1], amps, cap.SampleRateHz,
            new CrankingTestInputs { MinimumCrankingVolts = 9.0, MinimumCrankingSource = "Test manual p.1" }, true);
        Assert.Equal(OneTapVerdict.Pass, Row(pass, "Lowest cranking voltage").Verdict);
        Assert.Contains("Test manual p.1", Row(pass, "Lowest cranking voltage").Comment);

        var fail = CrankingTest.Analyse(cap.Channels[1], amps, cap.SampleRateHz,
            new CrankingTestInputs { MinimumCrankingVolts = 11.0, MinimumCrankingSource = "Test manual p.1" }, true);
        Assert.Equal(OneTapVerdict.Fail, Row(fail, "Lowest cranking voltage").Verdict);

        // Battery rating: a comparison row, never a verdict.
        var rated = CrankingTest.Analyse(cap.Channels[1], amps, cap.SampleRateHz,
            new CrankingTestInputs { RatedCca = 800, RatingStandard = "SAE CCA", BatteryTemperatureC = 21, BatteryType = "AGM" }, true);
        var ratio = Row(rated, "Peak current / battery rating");
        Assert.InRange(Number(ratio), 75, 80);
        Assert.Equal(OneTapVerdict.None, ratio.Verdict);
        Assert.Equal("AGM", Row(rated, "Battery type").Value);
        Assert.Equal("21 °C", Row(rated, "Battery temperature").Value);
    }

    [Fact]
    public void NoCrank_TooSoon_AndRanPastEnd_GivePlainMessages()
    {
        const double rate = 1000;
        var flat = Enumerable.Repeat(12.6, 5000).ToArray();
        var none = CrankingTest.Analyse(flat, null, rate, new CrankingTestInputs(), false);
        Assert.False(none.Success);
        Assert.Contains("No crank found", none.Message);
        Assert.Empty(none.Rows);

        var early = Enumerable.Range(0, 5000).Select(i => i < 20 ? 12.6 : 10.0).ToArray();
        var tooSoon = CrankingTest.Analyse(early, null, rate, new CrankingTestInputs(), false);
        Assert.False(tooSoon.Success);
        Assert.Contains("too soon", tooSoon.Message);

        var still = Enumerable.Range(0, 5000).Select(i => i < 1000 ? 12.6 : 10.0).ToArray();
        var past = CrankingTest.Analyse(still, null, rate, new CrankingTestInputs(), false);
        Assert.True(past.Success, past.Message);
        Assert.Contains("still going", past.Rows.Single(x => x.Parameter == "Cranking time").Comment);
        Assert.DoesNotContain(past.Rows, x => x.Parameter == "Voltage after cranking");
    }

    [Fact]
    public void ClippedVoltage_IsReported()
    {
        const double rate = 1000;
        var v = Enumerable.Range(0, 5000).Select(i => i < 1000 ? 12.6 : i < 3000 ? 9.0 : 14.0).ToArray();
        var r = CrankingTest.Analyse(v, null, rate, new CrankingTestInputs(), false, voltageRangeVolts: 10.0);
        Assert.Contains(r.Rows, x => x.Parameter == "Voltage channel over range");
    }
}
