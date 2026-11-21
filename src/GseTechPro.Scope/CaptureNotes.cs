using System.Text;

namespace GseTechPro.Scope;

/// <summary>
/// Capture notes (Phase 10D.1i): the technician's free text for a scope capture (what was
/// connected, engine state, what was seen). Display text only; nothing reads meaning from it.
/// </summary>
public static class CaptureNotes
{
    /// <summary>Longest note kept with one capture.</summary>
    public const int MaxLength = 2000;

    /// <summary>
    /// Cleans typed notes: line endings become "\n", other control characters (except tab) are
    /// removed, trailing spaces on each line and blank lines at the ends are trimmed, and the
    /// text is cut to <see cref="MaxLength"/> characters without splitting a surrogate pair.
    /// Empty → null.
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var unified = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var sb = new StringBuilder(unified.Length);
        foreach (var line in unified.Split('\n'))
        {
            if (sb.Length > 0)
                sb.Append('\n');
            foreach (var ch in line)
            {
                if (ch == '\t' || !char.IsControl(ch))
                    sb.Append(ch);
            }
            while (sb.Length > 0 && sb[^1] is ' ' or '\t')
                sb.Length--;
        }

        var cleaned = sb.ToString().Trim('\n');
        if (cleaned.Length > MaxLength)
        {
            int cut = MaxLength;
            if (char.IsHighSurrogate(cleaned[cut - 1]))
                cut--;
            cleaned = cleaned[..cut].TrimEnd();
        }

        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    /// <summary>First line of the notes, cut to <paramref name="maxLength"/> with "…", for lists.</summary>
    public static string Preview(string? notes, int maxLength = 60)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);
        var clean = Normalize(notes);
        if (clean is null)
            return "";

        int newline = clean.IndexOf('\n');
        var first = newline >= 0 ? clean[..newline].TrimEnd() : clean;
        bool more = newline >= 0;
        if (first.Length > maxLength)
        {
            int cut = maxLength - 1;
            if (char.IsHighSurrogate(first[cut - 1]))
                cut--;
            return first[..cut].TrimEnd() + "…";
        }

        return more ? first + " …" : first;
    }
}
