using GseTechPro.Core.Contracts;
using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.Scope.Tests;

/// <summary>Phase 10D.1h: channel labels (display text only).</summary>
public sealed class ChannelLabelsTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   \t\r\n ", null)]
    [InlineData("  Crank   sensor ", "Crank sensor")]
    [InlineData("CAN-H\tpin 6", "CAN-H pin 6")]
    [InlineData("Line one\r\nline two", "Line one line two")]
    public void Normalize_CleansWhiteSpace(string? input, string? expected)
    {
        Assert.Equal(expected, ChannelLabels.Normalize(input));
    }

    [Fact]
    public void Normalize_CutsToMaxLength_WithoutSplittingSurrogatePair()
    {
        var longText = new string('x', 40);
        Assert.Equal(ChannelLabels.MaxLength, ChannelLabels.Normalize(longText)!.Length);

        // 23 ASCII chars then an emoji (surrogate pair) straddling the 24-char limit.
        var straddle = new string('a', ChannelLabels.MaxLength - 1) + "\U0001F527" + "tail";
        var cut = ChannelLabels.Normalize(straddle)!;
        Assert.Equal(new string('a', ChannelLabels.MaxLength - 1), cut);
        Assert.False(char.IsHighSurrogate(cut[^1]));
    }

    [Fact]
    public void DisplayName_IsLetterOrLetterAndLabel()
    {
        Assert.Equal("A", ChannelLabels.DisplayName(1, null));
        Assert.Equal("B", ChannelLabels.DisplayName(2, "   "));
        Assert.Equal("C Cam sensor", ChannelLabels.DisplayName(3, " Cam  sensor"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelLabels.DisplayName(0, "x"));
    }

    [Fact]
    public void ChannelConfigJson_LeavesOutNullLabel_AndRoundTripsLabel()
    {
        var unlabelled = ScopeJson.SerializeChannelConfigs([new ScopeChannelConfig { Channel = 1, VoltageRangeVolts = 5 }]);
        Assert.DoesNotContain("label", unlabelled);

        var json = ScopeJson.SerializeChannelConfigs([new ScopeChannelConfig { Channel = 2, VoltageRangeVolts = 5, Label = "CAN-L" }]);
        Assert.Contains("\"label\":\"CAN-L\"", json);
        Assert.Equal("CAN-L", Assert.Single(ScopeJson.DeserializeChannelConfigs(json)).Label);

        // Rows saved before labels existed still read, with no label.
        var old = ScopeJson.DeserializeChannelConfigs("[{\"channel\":1,\"voltage_range_volts\":5,\"coupling\":\"dc\",\"probe_multiplier\":1,\"current_scale_amps_per_volt\":0}]");
        Assert.Null(Assert.Single(old).Label);
    }
}
