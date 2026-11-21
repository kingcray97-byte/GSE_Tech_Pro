using System.Text;

namespace GseTechPro.Scope;

/// <summary>
/// Channel labels (Phase 10D.1h): the short name a technician gives a scope channel
/// ("Crank sensor", "CAN-H"). Labels are display text only; they are never used to choose
/// a test, a range or a verdict.
/// </summary>
public static class ChannelLabels
{
    /// <summary>Longest label kept, so it fits on the 200 px channel tile.</summary>
    public const int MaxLength = 24;

    /// <summary>
    /// Cleans a typed label: control characters (tabs, new lines) become spaces, runs of
    /// white space collapse to one, ends are trimmed, and the result is cut to
    /// <see cref="MaxLength"/> characters (without splitting a surrogate pair). Empty → null.
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var sb = new StringBuilder(text.Length);
        bool lastWasSpace = false;
        foreach (var ch in text)
        {
            bool isSpace = char.IsWhiteSpace(ch) || char.IsControl(ch);
            if (isSpace)
            {
                if (!lastWasSpace && sb.Length > 0)
                    sb.Append(' ');
                lastWasSpace = true;
                continue;
            }

            sb.Append(ch);
            lastWasSpace = false;
        }

        var cleaned = sb.ToString().TrimEnd();
        if (cleaned.Length > MaxLength)
        {
            int cut = MaxLength;
            if (char.IsHighSurrogate(cleaned[cut - 1]))
                cut--;
            cleaned = cleaned[..cut].TrimEnd();
        }

        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// Channel name for headers and lists: the letter alone ("A") or the letter and the
    /// label ("A Crank sensor").
    /// </summary>
    public static string DisplayName(int channel, string? label)
    {
        if (channel < 1 || channel > 26)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "Channel must be 1-based.");

        var letter = ((char)('A' + channel - 1)).ToString();
        var clean = Normalize(label);
        return clean is null ? letter : $"{letter} {clean}";
    }
}
