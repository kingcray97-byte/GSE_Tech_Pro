using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.Scope.Tests;

/// <summary>Phase 10D.1k: stacked-view lane arithmetic (display only).</summary>
public sealed class ScopeViewLanesTests
{
    [Fact]
    public void Build_OverlayHasNoLanes_StackedIsChannelOrderThenMath()
    {
        Assert.Empty(ScopeViewLanes.Build(ScopeViewLayout.Overlay, [1, 2], includeMath: true));
        Assert.Equal([1, 3, 4], ScopeViewLanes.Build(ScopeViewLayout.Stacked, [4, 1, 3, 1], includeMath: false));
        Assert.Equal([2, ScopeViewLanes.MathLane], ScopeViewLanes.Build(ScopeViewLayout.Stacked, [2], includeMath: true));
        Assert.Empty(ScopeViewLanes.Build(ScopeViewLayout.Stacked, [], includeMath: false));
    }

    [Theory]
    [InlineData(0.0, 0, 2, 0.0)]      // top of lane 0
    [InlineData(600.0, 0, 2, 300.0)]  // bottom of lane 0
    [InlineData(300.0, 1, 2, 450.0)]  // mid of lane 1
    [InlineData(600.0, 3, 4, 600.0)]  // bottom of the last lane
    [InlineData(300.0, 0, 1, 300.0)]  // one lane = unchanged
    public void MapY_SqueezesIntoLane(double y, int lane, int count, double expected)
    {
        Assert.Equal(expected, ScopeViewLanes.MapY(y, lane, count, 600.0), 9);
    }

    [Fact]
    public void MapY_ClipsOutOfRangeToLaneEdges_AndRejectsBadLanes()
    {
        Assert.Equal(150.0, ScopeViewLanes.MapY(-50.0, 1, 4, 600.0), 9);   // top of lane 1
        Assert.Equal(300.0, ScopeViewLanes.MapY(900.0, 1, 4, 600.0), 9);   // bottom of lane 1
        Assert.Equal(225.0, ScopeViewLanes.MapY(double.NaN, 1, 4, 600.0), 9);
        Assert.Throws<ArgumentOutOfRangeException>(() => ScopeViewLanes.MapY(0, 2, 2, 600.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScopeViewLanes.MapY(0, 0, 0, 600.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScopeViewLanes.MapY(0, 0, 1, 0.0));
    }
}
