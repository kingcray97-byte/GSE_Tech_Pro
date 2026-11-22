using GseTechPro.Core.Speech;
using Xunit;

namespace GseTechPro.Tests;

/// <summary>Phase 7.5c: what a Windows voice says for a Tech Chat answer.</summary>
public sealed class SpokenAnswerTests
{
    [Fact]
    public void StripsMarkdown_SaysSuggestion_AndSource()
    {
        string said = SpokenAnswer.Prepare(
            "**Main relief** is set at `1500 psi` (see [the manual](manual://x)).",
            ["TUG 660-28 Maintenance Manual, page 78"]);
        Assert.Equal("AI suggestion. Main relief is set at 1500 psi (see the manual). Source: TUG 660-28 Maintenance Manual, page 78.", said);
    }

    [Fact]
    public void SafetyLinesComeFirst()
    {
        string said = SpokenAnswer.Prepare("Check the relief valve.\n> **WARNING:** Bleed off stored hydraulic pressure before opening any line.", isSuggestion: false);
        Assert.Equal("WARNING: Bleed off stored hydraulic pressure before opening any line. Check the relief valve.", said);
    }

    [Fact]
    public void ListsAndTables_AreSpokenAsPlainText()
    {
        string said = SpokenAnswer.Prepare("## Steps\n1. Key off\n- Read port G3\n| Port | Value |\n|---|---|\n| G3 | TODO |", isSuggestion: false);
        Assert.Equal("Steps. Key off. Read port G3. Port, Value. G3, TODO.", said);
    }

    [Fact]
    public void LongAnswer_IsCutAtASentence_ButSafetyIsKept()
    {
        string longBody = string.Concat(Enumerable.Repeat("This is one plain sentence of the answer. ", 60));
        string said = SpokenAnswer.Prepare("Lockout and tagout the unit first.\n" + longBody, ["Manual, page 1"]);
        Assert.StartsWith("AI suggestion. Lockout and tagout the unit first. This is one", said);
        Assert.Contains(SpokenAnswer.CutNote, said);
        Assert.EndsWith("Source: Manual, page 1.", said);
        Assert.True(said.Length < SpokenAnswer.MaxChars + 60);
        int beforeCut = said.IndexOf(SpokenAnswer.CutNote, StringComparison.Ordinal);
        Assert.EndsWith("answer.", said[..beforeCut].TrimEnd());
    }

    [Fact]
    public void EmptyAnswer_SaysNothing()
    {
        Assert.Equal("", SpokenAnswer.Prepare("  \n** **\n", ["Manual, page 1"]));
        Assert.Equal("", SpokenAnswer.Prepare(null));
    }

    [Fact]
    public void ManySources_AreCapped()
    {
        string said = SpokenAnswer.Prepare("Value is TODO.", ["A, page 1", "B, page 2", "a, page 1", "C, page 3", "D, page 4"], isSuggestion: false);
        Assert.Equal("Value is TODO. Sources: A, page 1; B, page 2; C, page 3. And more on screen.", said);
    }
}
