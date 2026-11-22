using GseTechPro.Core.Speech;
using GseTechPro.Core.Units;
using Xunit;

namespace GseTechPro.Tests;

/// <summary>Phase 7.5b: spoken guided-step commands. Transcripts are what the real LFM3-Omni wrote in the 7.5a check.</summary>
public sealed class VoiceCommandsTests
{
    [Theory]
    [InlineData("Reading is 2300 psi.", 2300, UnitConverter.Psi)]
    [InlineData("Reading is 185 psi.", 185, UnitConverter.Psi)]
    [InlineData("Reading 12.4 volts.", 12.4, UnitConverter.Volt)]
    [InlineData("Reading is 12.4 volts at the battery.", 12.4, UnitConverter.Volt)]
    [InlineData("reading is 2,300 PSI", 2300, UnitConverter.Psi)]
    [InlineData("2300 psi", 2300, UnitConverter.Psi)]
    [InlineData("The pressure is 17.5 bar", 17.5, UnitConverter.Bar)]
    [InlineData("It reads 210 degrees Fahrenheit", 210, UnitConverter.Fahrenheit)]
    [InlineData("I got 85 °C", 85, UnitConverter.Celsius)]
    [InlineData("Reading is 350 amps", 350, UnitConverter.Ampere)]
    [InlineData("Reading is 1,250.5 kPa on port G3", 1250.5, UnitConverter.KiloPascal)]
    [InlineData("reading is -3 degrees celsius", -3, UnitConverter.Celsius)]
    [InlineData("Reading is 40 liters per minute", 40, UnitConverter.LiterPerMinute)]
    public void Readings_NeedANumberAndAKnownUnit(string heard, double value, string unit)
    {
        var c = VoiceCommands.Parse(heard);
        Assert.Equal(VoiceCommandKind.Reading, c.Kind);
        Assert.Equal(value, c.Value!.Value, 6);
        Assert.Equal(unit, c.Unit);
    }

    [Theory]
    [InlineData("Reading is twenty three hundred")] // no digits
    [InlineData("Reading is 2300")] // no unit: never guessed
    [InlineData("2300")]
    [InlineData("Reading is 2300 psi and the hose is leaking badly at the lower fitting near the frame rail")] // long tail
    public void NotAReading(string heard) => Assert.NotEqual(VoiceCommandKind.Reading, VoiceCommands.Parse(heard).Kind);

    [Theory]
    [InlineData("What is the spec for the main relief valve pressure?")]
    [InlineData("What's the torque on the lift cylinder pin bolt?")]
    [InlineData("What pressure should the hydraulic pump make?")]
    [InlineData("Is 2300 psi too high?")] // a question is never a reading
    [InlineData("What is the main relief valve pressure on the 600I?")]
    public void Questions_GoToTechChat(string heard)
    {
        var c = VoiceCommands.Parse(heard);
        Assert.Equal(VoiceCommandKind.Ask, c.Kind);
        Assert.Equal(heard, c.Text);
    }

    [Theory]
    [InlineData("Add note: Left lift cylinder hose is wet at the lower fitting.", "Left lift cylinder hose is wet at the lower fitting")]
    [InlineData("add a note hydraulic tank level low", "Hydraulic tank level low")]
    [InlineData("Note, replaced the fuse", "Replaced the fuse")]
    public void Notes(string heard, string text)
    {
        var c = VoiceCommands.Parse(heard);
        Assert.Equal(VoiceCommandKind.AddNote, c.Kind);
        Assert.Equal(text, c.Text);
    }

    [Theory]
    [InlineData("Next step.", VoiceCommandKind.NextStep)]
    [InlineData("next", VoiceCommandKind.NextStep)]
    [InlineData("Go to the next step please", VoiceCommandKind.NextStep)]
    [InlineData("Yes. Save it.", VoiceCommandKind.Confirm)]
    [InlineData("Yes, save it.", VoiceCommandKind.Confirm)]
    [InlineData("yes", VoiceCommandKind.Confirm)]
    [InlineData("Correct.", VoiceCommandKind.Confirm)]
    [InlineData("No.", VoiceCommandKind.Cancel)]
    [InlineData("Cancel", VoiceCommandKind.Cancel)]
    [InlineData("No, that's wrong.", VoiceCommandKind.Cancel)]
    [InlineData("", VoiceCommandKind.Unknown)]
    [InlineData("Add note", VoiceCommandKind.Unknown)] // a note needs words
    [InlineData("The lift is slow today", VoiceCommandKind.Unknown)]
    public void SimpleCommands(string heard, VoiceCommandKind kind) => Assert.Equal(kind, VoiceCommands.Parse(heard).Kind);

    [Fact]
    public void ReadBack_IsPlain()
    {
        Assert.Equal("2,300 psi", VoiceCommands.Describe(2300, UnitConverter.Psi));
        Assert.Equal("12.4 V", VoiceCommands.Describe(12.4, UnitConverter.Volt));
        Assert.Equal("I heard 2300 P S I. Say yes to save it, or no.", VoiceCommands.SpeakReading(2300, UnitConverter.Psi));
        Assert.Contains("12.4 volts", VoiceCommands.SpeakReading(12.4, UnitConverter.Volt));
    }
}
