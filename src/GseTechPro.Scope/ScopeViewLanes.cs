namespace GseTechPro.Scope;

/// <summary>How the scope graticule shows the channels (Phase 10D.1k, multi-view).</summary>
public enum ScopeViewLayout
{
    /// <summary>One view: every trace drawn over the full height (the classic scope view).</summary>
    Overlay,

    /// <summary>One view per enabled channel stacked top to bottom (math channel last), sharing the time axis.</summary>
    Stacked,
}

/// <summary>
/// Lane arithmetic for the stacked view (Phase 10D.1k). Pure display maths: traces keep their own
/// vertical scale and are squeezed into their lane; captures and measurements are never changed.
/// </summary>
public static class ScopeViewLanes
{
    /// <summary>Lane key used for the math channel M.</summary>
    public const int MathLane = 0;

    /// <summary>
    /// The lanes, top to bottom: enabled channels in channel order (1 = A … 4 = D), then the math
    /// channel when it is shown. Overlay has no lanes, so it returns an empty list.
    /// </summary>
    public static IReadOnlyList<int> Build(ScopeViewLayout layout, IEnumerable<int> enabledChannels, bool includeMath)
    {
        ArgumentNullException.ThrowIfNull(enabledChannels);
        if (layout != ScopeViewLayout.Stacked)
            return Array.Empty<int>();

        var lanes = enabledChannels
            .Where(c => c >= 1)
            .Distinct()
            .OrderBy(c => c)
            .ToList();
        if (includeMath)
            lanes.Add(MathLane);
        return lanes;
    }

    /// <summary>
    /// Maps a y coordinate drawn for the full plot height (0 = top, <paramref name="height"/> = bottom)
    /// into lane <paramref name="lane"/> of <paramref name="laneCount"/>. Values outside the plot are
    /// clipped to the lane edges so a trace never spills into its neighbour.
    /// </summary>
    public static double MapY(double y, int lane, int laneCount, double height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(laneCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(lane);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(lane, laneCount);
        if (!(height > 0) || !double.IsFinite(height))
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");

        double clipped = double.IsFinite(y) ? Math.Clamp(y, 0.0, height) : height / 2.0;
        double laneHeight = height / laneCount;
        return lane * laneHeight + clipped / laneCount;
    }
}
