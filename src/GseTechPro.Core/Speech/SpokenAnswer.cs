using System.Text;
using System.Text.RegularExpressions;

namespace GseTechPro.Core.Speech;

/// <summary>
/// Turns a Tech Chat answer into words for a Windows voice (Phase 7.5c). Speaking is a courtesy for gloved, eyes-busy
/// work: the full answer, its citations and its "AI suggestion" badge stay on screen. What is spoken:
/// <list type="number">
/// <item>"AI suggestion." for model answers (the same label the bubble shows);</item>
/// <item>safety lines (warning, caution, danger, lockout, stored pressure …) first, so a cut-short answer never drops them;</item>
/// <item>the rest of the answer with markdown removed, cut at a sentence end after about <see cref="MaxChars"/> characters
///   ("The rest is on screen." is added when cut);</item>
/// <item>the sources ("Source: TUG 660-28 Maintenance Manual, page 78."), so the technician knows where the value came from.</item>
/// </list>
/// Nothing is reworded or summarized: values are spoken exactly as written.
/// </summary>
public static class SpokenAnswer
{
    /// <summary>About one minute of speech.</summary>
    public const int MaxChars = 900;

    public const string CutNote = "The rest is on screen.";

    private static readonly Regex SafetyLine = new(
        @"\b(warning|caution|danger|safety|lockout|tagout|loto|stored\s+(hydraulic\s+)?pressure|high\s+voltage|bleed\s+(off|down)|depressuri[sz]e|aircraft)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MdLink = new(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex MdEmphasis = new(@"(\*\*|__|\*|`+|~~)", RegexOptions.Compiled);
    private static readonly Regex MdLinePrefix = new(@"^\s*(#{1,6}\s+|>\s*|[-*+•]\s+|\d+[.)]\s+)", RegexOptions.Compiled);
    private static readonly Regex TableRule = new(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex SentenceEnd = new(@"[.!?](?=\s|$)", RegexOptions.Compiled);

    /// <summary>The words to speak for one assistant answer, or "" when there is nothing to say.</summary>
    public static string Prepare(string? text, IEnumerable<string>? sources = null, bool isSuggestion = true)
    {
        var lines = (text ?? "")
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(CleanLine)
            .Where(l => l.Length > 0)
            .ToList();

        var src = (sources ?? [])
            .Select(s => Spaces.Replace(s ?? "", " ").Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (lines.Count == 0)
            return "";

        // Safety lines first (kept whole, never cut), then the rest in order.
        var safety = lines.Where(l => SafetyLine.IsMatch(l)).ToList();
        var rest = lines.Where(l => !SafetyLine.IsMatch(l)).ToList();

        var sb = new StringBuilder();
        if (isSuggestion)
            sb.Append("AI suggestion. ");
        foreach (var l in safety)
            sb.Append(EndSentence(l)).Append(' ');

        string body = string.Join(" ", rest.Select(EndSentence));
        int room = Math.Max(0, MaxChars - sb.Length);
        if (body.Length > room)
        {
            body = CutAtSentence(body, room);
            sb.Append(body.Length > 0 ? body + " " : "").Append(CutNote).Append(' ');
        }
        else if (body.Length > 0)
        {
            sb.Append(body).Append(' ');
        }

        if (src.Count == 1)
            sb.Append("Source: ").Append(EndSentence(src[0]));
        else if (src.Count > 1)
            sb.Append("Sources: ").Append(EndSentence(string.Join("; ", src.Take(3)))).Append(src.Count > 3 ? " And more on screen." : "");

        return sb.ToString().Trim();
    }

    private static string CleanLine(string line)
    {
        if (TableRule.IsMatch(line))
            return "";
        string l = MdLink.Replace(line, "$1");
        l = MdLinePrefix.Replace(l, "");
        l = MdEmphasis.Replace(l, "");
        if (l.Contains('|'))
            l = string.Join(", ", l.Split('|').Select(c => c.Trim()).Where(c => c.Length > 0));
        return Spaces.Replace(l, " ").Trim();
    }

    private static string EndSentence(string s)
    {
        s = s.TrimEnd();
        return s.Length == 0 || ".!?:".Contains(s[^1]) ? s : s + ".";
    }

    /// <summary>The longest run of whole sentences that fits in <paramref name="max"/> characters ("" if the first is longer).</summary>
    private static string CutAtSentence(string body, int max)
    {
        int cut = 0;
        foreach (Match m in SentenceEnd.Matches(body))
        {
            if (m.Index + 1 > max)
                break;
            cut = m.Index + 1;
        }
        return body[..cut].Trim();
    }
}
