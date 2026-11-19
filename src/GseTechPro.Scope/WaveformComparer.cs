using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;

namespace GseTechPro.Scope;

/// <summary>
/// Pure, deterministic reference comparison engine implementing ADR-011 §5.
/// </summary>
public static class WaveformComparer
{
    public const string TodoReason = "reference TODO — needs spec from manual";

    /// <summary>
    /// Compares computed waveform measurements against reference expectations per ADR-011 §5.
    /// </summary>
    public static ScopeComparisonResult CompareMeasurements(
        IReadOnlyList<ScopeMeasurement> measurements,
        ReferenceExpectations? expectations,
        string? referenceWaveformId = null)
    {
        ArgumentNullException.ThrowIfNull(measurements);

        var expectedList = expectations?.Measurements ?? Array.Empty<MeasurementExpectation>();
        var compared = new ScopeMeasurement[measurements.Count];

        bool anyFail = false;
        bool anyPass = false;

        for (int i = 0; i < measurements.Count; i++)
        {
            var m = measurements[i];
            var exp = FindExpectation(expectedList, m.Kind, m.Channel);

            ScopeMeasurement updated = EvaluateSingle(m, exp);
            compared[i] = updated;

            if (updated.Verdict == ScopeVerdict.Fail)
                anyFail = true;
            else if (updated.Verdict == ScopeVerdict.Pass)
                anyPass = true;
        }

        ScopeVerdict overall = anyFail
            ? ScopeVerdict.Fail
            : anyPass
                ? ScopeVerdict.Pass
                : ScopeVerdict.Na;

        return new ScopeComparisonResult
        {
            ReferenceWaveformId = referenceWaveformId,
            Measurements = compared,
            Overall = overall,
        };
    }

    private static MeasurementExpectation? FindExpectation(
        IReadOnlyList<MeasurementExpectation> expectations,
        MeasurementKind kind,
        int channel)
    {
        for (int i = 0; i < expectations.Count; i++)
        {
            var e = expectations[i];
            if (e.Kind == kind && e.Channel == channel)
                return e;
        }
        return null;
    }

    private static ScopeMeasurement EvaluateSingle(ScopeMeasurement m, MeasurementExpectation? exp)
    {
        // (a) No expectation for that kind + channel
        if (exp is null)
        {
            return m with
            {
                Verdict = ScopeVerdict.Na,
                ExpectedNominal = null,
                ExpectedTolerance = null,
                ExpectedMin = null,
                ExpectedMax = null,
                ExpectedUnit = null,
            };
        }

        var populated = m with
        {
            ExpectedNominal = exp.Nominal,
            ExpectedTolerance = exp.Tolerance,
            ExpectedMin = exp.Min,
            ExpectedMax = exp.Max,
            ExpectedUnit = exp.Unit,
        };

        // (b) Expectation is marked is_todo
        if (exp.IsTodo)
        {
            return populated with
            {
                Verdict = ScopeVerdict.Na,
                Detail = string.IsNullOrWhiteSpace(m.Detail) ? TodoReason : m.Detail,
            };
        }

        // (c) Measurement itself is not Computed (or value is missing/non-finite)
        if (m.Status != ScopeMeasurementStatus.Computed || m.Value is not double val || !double.IsFinite(val))
        {
            return populated with
            {
                Verdict = ScopeVerdict.Na,
            };
        }

        // (d) Unit mismatch (case-sensitive canonical unit check: Hz, V, %, s, A)
        if (string.IsNullOrWhiteSpace(exp.Unit) || !string.Equals(exp.Unit, m.Unit, StringComparison.Ordinal))
        {
            return populated with
            {
                Verdict = ScopeVerdict.Na,
                Detail = string.IsNullOrWhiteSpace(m.Detail)
                    ? $"Unit mismatch: measurement is '{m.Unit}', expectation is '{exp.Unit}'."
                    : m.Detail,
            };
        }

        // Range form 1: nominal ± tolerance (inclusive)
        if (exp.Nominal is double nominal && exp.Tolerance is double tolerance)
        {
            if (!double.IsFinite(nominal) || !double.IsFinite(tolerance) || tolerance < 0.0)
            {
                return populated with { Verdict = ScopeVerdict.Na };
            }

            double minBound = nominal - tolerance;
            double maxBound = nominal + tolerance;
            ScopeVerdict verdict = (val >= minBound && val <= maxBound)
                ? ScopeVerdict.Pass
                : ScopeVerdict.Fail;

            return populated with { Verdict = verdict };
        }

        // Range form 2: [min, max] (inclusive)
        if (exp.Min is double min && exp.Max is double max)
        {
            if (!double.IsFinite(min) || !double.IsFinite(max) || max < min)
            {
                return populated with { Verdict = ScopeVerdict.Na };
            }

            ScopeVerdict verdict = (val >= min && val <= max)
                ? ScopeVerdict.Pass
                : ScopeVerdict.Fail;

            return populated with { Verdict = verdict };
        }

        return populated with { Verdict = ScopeVerdict.Na };
    }
}
