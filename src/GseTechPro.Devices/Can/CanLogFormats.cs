using System.Globalization;
using System.Text;
using GseTechPro.Core.Contracts;

namespace GseTechPro.Devices.Can;

/// <summary>
/// Vector ASCII (<c>.asc</c>) CAN log, the widely used published text format. We write one channel, absolute
/// timestamps in seconds, hex ids (<c>x</c> suffix = 29-bit), <c>Rx d &lt;dlc&gt; &lt;bytes&gt;</c> or <c>Rx r</c> for remote frames:
/// <code>
/// date Fri Nov 20 02:15:00.000 pm 2026
/// base hex  timestamps absolute
/// no internal events logged
/// // GSE Tech Pro CAN capture
/// Begin Triggerblock Fri Nov 20 02:15:00.000 pm 2026
///    0.000000 Start of measurement
///    0.020000 1  CF00400x        Rx   d 8 FF FF FF 40 19 FF FF FF
/// End TriggerBlock
/// </code>
/// A <c>// simulated</c> comment marks a capture from the simulator so it is never mistaken for a real one.
/// </summary>
public static class AscCanLog
{
    public const string SimulatedMarker = "// simulated";

    public static void Write(TextWriter writer, IEnumerable<CanFrame> frames, DateTimeOffset startedAt, bool simulated, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(frames);
        var date = FormatDate(startedAt);
        writer.WriteLine($"date {date}");
        writer.WriteLine("base hex  timestamps absolute");
        writer.WriteLine("no internal events logged");
        writer.WriteLine("// GSE Tech Pro CAN capture" + (string.IsNullOrWhiteSpace(title) ? "" : $": {Sanitize(title)}"));
        if (simulated)
            writer.WriteLine(SimulatedMarker);
        writer.WriteLine($"Begin Triggerblock {date}");
        writer.WriteLine("   0.000000 Start of measurement");
        foreach (var frame in frames)
            writer.WriteLine(FormatFrame(frame));
        writer.WriteLine("End TriggerBlock");
    }

    public static string FormatFrame(CanFrame frame)
    {
        var sb = new StringBuilder(80);
        sb.Append(frame.TimestampSeconds.ToString("0.000000", CultureInfo.InvariantCulture).PadLeft(11));
        sb.Append(" 1  ");
        var id = frame.Id.ToString("X", CultureInfo.InvariantCulture) + (frame.IsExtended ? "x" : "");
        sb.Append(id.PadRight(16));
        sb.Append("Rx   ");
        if (frame.IsRemote)
        {
            sb.Append("r");
        }
        else
        {
            sb.Append("d ").Append(frame.Dlc.ToString(CultureInfo.InvariantCulture));
            foreach (var b in frame.Data)
                sb.Append(' ').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>Reads frames from an <c>.asc</c> log. Lines that are not CAN frames on a channel are skipped.</summary>
    public static AscReadResult Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var frames = new List<CanFrame>();
        var simulated = false;
        var skipped = 0;
        var hexBase = true;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                if (trimmed.StartsWith(SimulatedMarker, StringComparison.OrdinalIgnoreCase))
                    simulated = true;
                continue;
            }
            if (trimmed.StartsWith("base ", StringComparison.OrdinalIgnoreCase))
            {
                hexBase = !trimmed.Contains(" dec", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!char.IsDigit(trimmed[0]))
                continue;

            if (TryParseFrame(trimmed, hexBase, simulated, out var frame))
                frames.Add(frame);
            else if (!trimmed.Contains("Start of measurement", StringComparison.OrdinalIgnoreCase))
                skipped++;
        }

        if (simulated)
        {
            for (var i = 0; i < frames.Count; i++)
                frames[i] = frames[i] with { IsSimulated = true };
        }
        frames.Sort((a, b) => a.TimestampSeconds.CompareTo(b.TimestampSeconds));
        return new AscReadResult(frames, simulated, skipped);
    }

    private static bool TryParseFrame(string line, bool hexBase, bool simulated, out CanFrame frame)
    {
        frame = null!;
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // time channel id dir d|r [dlc bytes...]
        if (parts.Length < 5)
            return false;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
            return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return false; // CAN FD, error frames, events: not a classic channel frame

        var idText = parts[2];
        var extended = idText.EndsWith('x') || idText.EndsWith('X');
        if (extended)
            idText = idText[..^1];
        var idStyle = hexBase ? NumberStyles.HexNumber : NumberStyles.Integer;
        if (!uint.TryParse(idText, idStyle, CultureInfo.InvariantCulture, out var id))
            return false;
        if (!extended && id > 0x7FF)
            extended = true;
        if (id > 0x1FFF_FFFF)
            return false;

        var kind = parts[4];
        if (kind is "r" or "R")
        {
            frame = new CanFrame { Id = id, IsExtended = extended, IsRemote = true, TimestampSeconds = t, IsSimulated = simulated };
            return true;
        }
        if (kind is not ("d" or "D") || parts.Length < 6)
            return false;
        if (!int.TryParse(parts[5], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var dlc) || dlc < 0 || dlc > 8)
            return false;
        if (parts.Length < 6 + dlc)
            return false;

        var data = new byte[dlc];
        var byteStyle = hexBase ? NumberStyles.HexNumber : NumberStyles.Integer;
        for (var i = 0; i < dlc; i++)
        {
            if (!byte.TryParse(parts[6 + i], byteStyle, CultureInfo.InvariantCulture, out data[i]))
                return false;
        }

        frame = new CanFrame { Id = id, IsExtended = extended, Data = data, TimestampSeconds = t, IsSimulated = simulated };
        return true;
    }

    private static string FormatDate(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        var ampm = local.Hour < 12 ? "am" : "pm";
        var hour12 = local.Hour % 12 == 0 ? 12 : local.Hour % 12;
        return string.Create(CultureInfo.InvariantCulture,
            $"{local:ddd MMM dd} {hour12:00}:{local:mm:ss.fff} {ampm} {local:yyyy}");
    }

    private static string Sanitize(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
}

public sealed record AscReadResult(IReadOnlyList<CanFrame> Frames, bool IsSimulated, int SkippedLines);

/// <summary>CSV export of a CAN capture: one row per frame (invariant culture, hex id and bytes).</summary>
public static class CsvCanLog
{
    public const string Header = "time_s,id_hex,extended,remote,dlc,data_hex,simulated";

    public static void Write(TextWriter writer, IEnumerable<CanFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine(Header);
        foreach (var f in frames)
        {
            writer.Write(f.TimestampSeconds.ToString("0.000000", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(f.Id.ToString(f.IsExtended ? "X8" : "X3", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(f.IsExtended ? "1" : "0");
            writer.Write(',');
            writer.Write(f.IsRemote ? "1" : "0");
            writer.Write(',');
            writer.Write(f.Dlc.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(Convert.ToHexString(f.Data));
            writer.Write(',');
            writer.WriteLine(f.IsSimulated ? "1" : "0");
        }
    }
}
