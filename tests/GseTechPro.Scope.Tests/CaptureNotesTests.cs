using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.Scope.Tests;

/// <summary>Phase 10D.1i: capture notes (display text only).</summary>
public sealed class CaptureNotesTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("  \r\n\t ", null)]
    [InlineData("Idle, A on CKP  ", "Idle, A on CKP")]
    [InlineData("\r\n\r\nline 1\r\nline 2   \r\n\r\n", "line 1\nline 2")]
    [InlineData("old\rmac", "old\nmac")]
    [InlineData("bell\u0007 gone\tkept", "bell gone\tkept")]
    public void Normalize_CleansLineEndsAndControlCharacters(string? input, string? expected)
    {
        Assert.Equal(expected, CaptureNotes.Normalize(input));
    }

    [Fact]
    public void Normalize_CutsToMaxLength_WithoutSplittingSurrogatePair()
    {
        Assert.Equal(CaptureNotes.MaxLength, CaptureNotes.Normalize(new string('n', 5000))!.Length);

        var straddle = new string('a', CaptureNotes.MaxLength - 1) + "\U0001F527" + "tail";
        var cut = CaptureNotes.Normalize(straddle)!;
        Assert.Equal(CaptureNotes.MaxLength - 1, cut.Length);
        Assert.False(char.IsHighSurrogate(cut[^1]));
    }

    [Fact]
    public void Preview_IsFirstLine_MarkedWhenMoreOrCut()
    {
        Assert.Equal("", CaptureNotes.Preview(null));
        Assert.Equal("Engine idling", CaptureNotes.Preview("Engine idling"));
        Assert.Equal("Engine idling …", CaptureNotes.Preview("Engine idling\nB on CMP"));
        Assert.Equal("abcd…", CaptureNotes.Preview("abcdefghij", maxLength: 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => CaptureNotes.Preview("x", maxLength: 1));
    }
}
