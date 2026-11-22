using System.Globalization;
using System.Text.RegularExpressions;
using GseTechPro.Core.Units;

namespace GseTechPro.Core.Speech;

/// <summary>What a spoken phrase asks for (Phase 7.5b, guided troubleshooting step).</summary>
public enum VoiceCommandKind
{
    /// <summary>Not a command; shown back to the technician.</summary>
    Unknown,
    /// <summary>"next step": record what is entered and go on (same as the Record &amp; Next button).</summary>
    NextStep,
    /// <summary>"reading is 2,300 psi": a measured value; read back and confirmed before it is saved.</summary>
    Reading,
    /// <summary>"add note left hose is wet": a work-order note; shown back and confirmed before it is saved.</summary>
    AddNote,
    /// <summary>"what's the spec for …": a question for Tech Chat (answered with citations, never from voice).</summary>
    Ask,
    /// <summary>"yes" / "save it": confirms what was read back.</summary>
    Confirm,
    /// <summary>"no" / "cancel": throws away what was read back.</summary>
    Cancel,
}

/// <summary>One parsed phrase. <see cref="Unit"/> is a <see cref="UnitConverter"/> unit string when <see cref="Value"/> is set.</summary>
public sealed record VoiceCommand(VoiceCommandKind Kind, string Heard, double? Value = null, string? Unit = null, string? Text = null);

/// <summary>
/// Deterministic parser for spoken guided-step commands (Phase 7.5b). The transcript comes from the AI service's speech to
/// text, which writes numbers as digits ("Reading is 2300 psi."). Nothing here guesses: a reading needs a number <i>and</i> a
/// unit this app knows, a phrase that is not clearly a command is <see cref="VoiceCommandKind.Unknown"/>, and every reading
/// and note is read back and confirmed by the technician before anything is saved (ARCHITECTURE §4 rule: voice readings
/// are read back first).
/// </summary>
public static class VoiceCommands
{
    // Spoken unit words → UnitConverter unit. Longest first so "degrees fahrenheit" wins over "degrees".
    private static readonly (string Pattern, string Unit)[] Units =
    [
        (@"pounds?\s+per\s+square\s+inch|p\.?\s?s\.?\s?i\.?|psig?", UnitConverter.Psi),
        (@"kilo\s?pascals?|kpa", UnitConverter.KiloPascal),
        (@"bars?", UnitConverter.Bar),
        (@"degrees?\s+fahrenheit|°\s?f|deg(?:rees?)?\s?f|fahrenheit", UnitConverter.Fahrenheit),
        (@"degrees?\s+celsius|degrees?\s+centigrade|°\s?c|deg(?:rees?)?\s?c|celsius", UnitConverter.Celsius),
        (@"millivolts?|mv", "mV"),
        (@"volts?(?:\s+dc)?|vdc|v", UnitConverter.Volt),
        (@"milliamps?|ma", "mA"),
        (@"amps?|amperes?|a", UnitConverter.Ampere),
        (@"liters?\s+per\s+minute|litres?\s+per\s+minute|l\s?/\s?min|lpm", UnitConverter.LiterPerMinute),
        (@"hours?|hrs?", UnitConverter.Hour),
    ];

    private static readonly string UnitAlternation = string.Join("|", Units.Select((u, i) => $"(?<u{i}>{u.Pattern})"));

    // "[the] [reading|value|it|that] [is|reads|of|was|=] <number> <unit>", or just "<number> <unit>".
    private static readonly Regex ReadingRegex = new(
        @"^(?:(?:the\s+)?(?:reading|value|it|that|i\s+(?:have|got|read)|i'm\s+reading|pressure|voltage|current|temperature)\s*(?:is|reads|of|was|at|=|:)?\s*)?" +
        @"(?<num>[-−]?\d{1,3}(?:,\d{3})+(?:\.\d+)?|[-−]?\d+(?:\.\d+)?)\s*(?:" + UnitAlternation + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NextRegex = new(
        @"^(?:ok(?:ay)?\s+)?(?:next(?:\s+step)?|go\s+(?:on|to\s+(?:the\s+)?next(?:\s+step)?)|continue|record\s+and\s+next)(?:\s+please)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NoteRegex = new(
        @"^(?:please\s+)?(?:add\s+(?:a\s+)?note|make\s+(?:a\s+)?note|note)\s*[:,\-]?\s*(?<text>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ConfirmRegex = new(
        @"^(?:yes|yeah|yep|correct|confirm(?:ed)?|that's\s+(?:right|correct)|save(?:\s+it)?|yes[,.\s]+(?:save(?:\s+it)?|that's\s+(?:right|correct)|correct|confirm))(?:\s+please)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CancelRegex = new(
        @"^(?:no|nope|cancel|wrong|that's\s+wrong|discard|clear(?:\s+it)?|no[,.\s]+(?:cancel|that's\s+wrong|wrong))(?:\s+please)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AskRegex = new(
        @"^(?:what|what's|whats|how|where|which|when|why|is|are|does|do|can|should|tell\s+me|show\s+me|look\s+up|find)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Most text a spoken note keeps.</summary>
    public const int MaxNoteLength = 500;

    public static VoiceCommand Parse(string? transcript)
    {
        string heard = Normalize(transcript);
        if (heard.Length == 0)
            return new VoiceCommand(VoiceCommandKind.Unknown, heard);
        string bare = heard.TrimEnd('.', '!', '?', ',', ';').Trim();

        if (ConfirmRegex.IsMatch(bare))
            return new VoiceCommand(VoiceCommandKind.Confirm, heard);
        if (CancelRegex.IsMatch(bare))
            return new VoiceCommand(VoiceCommandKind.Cancel, heard);
        if (NextRegex.IsMatch(bare))
            return new VoiceCommand(VoiceCommandKind.NextStep, heard);

        var note = NoteRegex.Match(bare);
        if (note.Success)
        {
            string text = note.Groups["text"].Value.Trim().TrimEnd('.');
            if (text.Length > MaxNoteLength)
                text = text[..MaxNoteLength];
            return text.Length == 0
                ? new VoiceCommand(VoiceCommandKind.Unknown, heard)
                : new VoiceCommand(VoiceCommandKind.AddNote, heard, Text: char.ToUpperInvariant(text[0]) + text[1..]);
        }

        // A question is never a reading, even when it contains a number and a unit ("is 2300 psi too high?").
        if (heard.EndsWith('?') || AskRegex.IsMatch(bare))
            return new VoiceCommand(VoiceCommandKind.Ask, heard, Text: heard);

        var reading = ReadingRegex.Match(bare);
        if (reading.Success && reading.Index == 0 && reading.Length == bare.Length - TrailingWords(bare, reading))
        {
            string num = reading.Groups["num"].Value.Replace(",", "").Replace('−', '-');
            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value))
            {
                for (int i = 0; i < Units.Length; i++)
                    if (reading.Groups[$"u{i}"].Success)
                        return new VoiceCommand(VoiceCommandKind.Reading, heard, value, Units[i].Unit);
            }
        }

        return new VoiceCommand(VoiceCommandKind.Unknown, heard);
    }

    /// <summary>"2,300 psi" — how a reading is read back (value as heard, unit as the app writes it).</summary>
    public static string Describe(double value, string unit) =>
        value.ToString(Math.Abs(value) >= 1000 ? "#,##0.###" : "0.###", CultureInfo.InvariantCulture) + " " + unit;

    /// <summary>"2300 psi" spoken back: digits and a spelled-out unit so the Windows voice says it plainly.</summary>
    public static string SpeakReading(double value, string unit)
    {
        string words = unit switch
        {
            UnitConverter.Psi => "P S I",
            UnitConverter.KiloPascal => "kilopascals",
            UnitConverter.Bar => "bar",
            UnitConverter.Fahrenheit => "degrees Fahrenheit",
            UnitConverter.Celsius => "degrees Celsius",
            UnitConverter.Volt => "volts",
            UnitConverter.Ampere => "amps",
            UnitConverter.LiterPerMinute => "liters per minute",
            UnitConverter.Hour => "hours",
            "mV" => "millivolts",
            "mA" => "milliamps",
            _ => unit,
        };
        return $"I heard {value.ToString("0.###", CultureInfo.InvariantCulture)} {words}. Say yes to save it, or no.";
    }

    private static string Normalize(string? transcript) =>
        Regex.Replace((transcript ?? "").Replace('’', '\''), @"\s+", " ").Trim();

    // Allow a short trailing tail after the unit ("at the battery", "on port G3"): up to 6 words of plain text.
    private static int TrailingWords(string bare, Match m)
    {
        string tail = bare[(m.Index + m.Length)..];
        if (tail.Length == 0)
            return 0;
        var words = tail.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool plain = words.Length <= 6 && words.All(w => Regex.IsMatch(w, @"^[A-Za-z][A-Za-z0-9\-]*$"));
        return plain ? tail.Length : -1;
    }
}
