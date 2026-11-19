using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using Xunit;

namespace GseTechPro.Scope.Tests;

public sealed class WaveformComparerTests
{
    [Fact]
    public void CompareMeasurements_NominalPlusMinusTolerance_EvaluatesPassAndFailCorrectly()
    {
        var measurements = new[]
        {
            new ScopeMeasurement
            {
                Kind = MeasurementKind.Frequency,
                Channel = 1,
                Value = 198.5,
                Unit = "Hz",
                Status = ScopeMeasurementStatus.Computed,
            },
            new ScopeMeasurement
            {
                Kind = MeasurementKind.Ripple,
                Channel = 1,
                Value = 0.35,
                Unit = "V",
                Status = ScopeMeasurementStatus.Computed,
            },
        };

        var expectations = new ReferenceExpectations
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
                    Kind = MeasurementKind.Ripple,
                    Channel = 1,
                    Nominal = 0.20,
                    Tolerance = 0.05,
                    Unit = "V",
                },
            ],
        };

        ScopeComparisonResult result = WaveformComparer.CompareMeasurements(measurements, expectations, "ref-1");

        Assert.Equal("ref-1", result.ReferenceWaveformId);
        Assert.Equal(ScopeVerdict.Fail, result.Overall);

        var freq = result.Measurements.Single(m => m.Kind == MeasurementKind.Frequency);
        Assert.Equal(ScopeVerdict.Pass, freq.Verdict);
        Assert.Equal(200.0, freq.ExpectedNominal);
        Assert.Equal(5.0, freq.ExpectedTolerance);
        Assert.Equal("Hz", freq.ExpectedUnit);

        var ripple = result.Measurements.Single(m => m.Kind == MeasurementKind.Ripple);
        Assert.Equal(ScopeVerdict.Fail, ripple.Verdict);
        Assert.Equal(0.20, ripple.ExpectedNominal);
        Assert.Equal(0.05, ripple.ExpectedTolerance);
    }

    [Fact]
    public void CompareMeasurements_MinMaxInclusiveBounds_EvaluatesPassAndFail()
    {
        var measurements = new[]
        {
            new ScopeMeasurement
            {
                Kind = MeasurementKind.PeakToPeak,
                Channel = 1,
                Value = 11.0, // exact lower inclusive bound
                Unit = "V",
                Status = ScopeMeasurementStatus.Computed,
            },
            new ScopeMeasurement
            {
                Kind = MeasurementKind.Current,
                Channel = 2,
                Value = 15.0, // exact upper inclusive bound
                Unit = "A",
                Status = ScopeMeasurementStatus.Computed,
            },
        };

        var expectations = new ReferenceExpectations
        {
            Measurements =
            [
                new MeasurementExpectation
                {
                    Kind = MeasurementKind.PeakToPeak,
                    Channel = 1,
                    Min = 11.0,
                    Max = 15.0,
                    Unit = "V",
                },
                new MeasurementExpectation
                {
                    Kind = MeasurementKind.Current,
                    Channel = 2,
                    Min = 8.0,
                    Max = 15.0,
                    Unit = "A",
                },
            ],
        };

        ScopeComparisonResult result = WaveformComparer.CompareMeasurements(measurements, expectations, "ref-minmax");

        Assert.Equal(ScopeVerdict.Pass, result.Overall);
        Assert.All(result.Measurements, m => Assert.Equal(ScopeVerdict.Pass, m.Verdict));
    }

    [Fact]
    public void CompareMeasurements_UnitMismatch_Todo_NotComputed_AndMissingExpectation_AllYieldNa()
    {
        var measurements = new[]
        {
            // (a) No expectation for RiseTime
            new ScopeMeasurement
            {
                Kind = MeasurementKind.RiseTime,
                Channel = 1,
                Value = 0.0001,
                Unit = "s",
                Status = ScopeMeasurementStatus.Computed,
            },
            // (b) Expectation is IsTodo
            new ScopeMeasurement
            {
                Kind = MeasurementKind.Current,
                Channel = 2,
                Value = 10.0,
                Unit = "A",
                Status = ScopeMeasurementStatus.Computed,
            },
            // (c) Measurement is not Computed (FlatSignal)
            new ScopeMeasurement
            {
                Kind = MeasurementKind.Frequency,
                Channel = 1,
                Value = null,
                Unit = "Hz",
                Status = ScopeMeasurementStatus.FlatSignal,
            },
            // (d) Unit mismatch ("mV" vs "V")
            new ScopeMeasurement
            {
                Kind = MeasurementKind.Peak,
                Channel = 1,
                Value = 5.0,
                Unit = "V",
                Status = ScopeMeasurementStatus.Computed,
            },
        };

        var expectations = new ReferenceExpectations
        {
            Measurements =
            [
                new MeasurementExpectation
                {
                    Kind = MeasurementKind.Current,
                    Channel = 2,
                    IsTodo = true,
                },
                new MeasurementExpectation
                {
                    Kind = MeasurementKind.Frequency,
                    Channel = 1,
                    Nominal = 200.0,
                    Tolerance = 10.0,
                    Unit = "Hz",
                },
                new MeasurementExpectation
                {
                    Kind = MeasurementKind.Peak,
                    Channel = 1,
                    Nominal = 5000.0,
                    Tolerance = 100.0,
                    Unit = "mV",
                },
            ],
        };

        ScopeComparisonResult result = WaveformComparer.CompareMeasurements(measurements, expectations, "ref-na");

        Assert.Equal(ScopeVerdict.Na, result.Overall);
        Assert.All(result.Measurements, m => Assert.Equal(ScopeVerdict.Na, m.Verdict));

        var todoCurrent = result.Measurements.Single(m => m.Kind == MeasurementKind.Current);
        Assert.Equal(WaveformComparer.TodoReason, todoCurrent.Detail);
    }
}
