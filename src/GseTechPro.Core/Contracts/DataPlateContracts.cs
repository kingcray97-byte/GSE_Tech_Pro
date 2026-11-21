using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GseTechPro.Core.Models;

namespace GseTechPro.Core.Contracts;

/// <summary>
/// Part of a photo to read, as fractions of the image (0–1; x across, y down). Null region = whole photo.
/// </summary>
public sealed record OcrRegion(double X, double Y, double Width, double Height)
{
    /// <summary>Regions smaller than this (fraction of either side) are stray taps.</summary>
    public const double MinSize = 0.02;

    /// <summary>Box from two corners, clamped to the image; null when it is too small to read.</summary>
    public static OcrRegion? FromCorners(double x1, double y1, double x2, double y2)
    {
        static double C(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;
        double left = Math.Min(C(x1), C(x2)), right = Math.Max(C(x1), C(x2));
        double top = Math.Min(C(y1), C(y2)), bottom = Math.Max(C(y1), C(y2));
        if (right - left < MinSize || bottom - top < MinSize)
            return null;
        return new OcrRegion(left, top, right - left, bottom - top);
    }
}

/// <summary>What an OCR engine read: lines top to bottom, exactly as recognised (never corrected).</summary>
public sealed record TextRecognitionResult
{
    public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
    /// <summary>e.g. "Windows OCR (English (United States))" or "Simulator OCR".</summary>
    public string EngineName { get; init; } = "";
    public bool IsSimulated { get; init; }
}

/// <summary>Kind of value on a data plate.</summary>
public enum DataPlateFieldKind
{
    Manufacturer,
    Model,
    SerialNumber,
    PartNumber,
    ManufactureDate,
}

/// <summary>A labelled value found in the OCR text: a suggestion only, until the technician confirms it.</summary>
public sealed record DataPlateFieldCandidate(DataPlateFieldKind Kind, string Label, string Value, string SourceLine);

/// <summary>
/// A data-plate reading the technician has checked against the plate (Phase 7.3). Stored on the asset in
/// <c>assets.configuration_json</c> under <see cref="DataPlates.ConfigurationKey"/>. Values are what the
/// technician confirmed; <see cref="RawText"/> is what the OCR engine read, kept for audit.
/// </summary>
public sealed record DataPlateReading
{
    public int Version { get; init; } = 1;
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? SerialNumber { get; init; }
    public IReadOnlyList<string> PartNumbers { get; init; } = Array.Empty<string>();
    public string? ManufactureDate { get; init; }
    public string RawText { get; init; } = "";
    public string? PhotoAttachmentId { get; init; }
    public string? WorkOrderId { get; init; }
    public string Engine { get; init; } = "";
    /// <summary>Simulator readings never change the asset's serial number.</summary>
    public bool IsSimulated { get; init; }
    /// <summary>The asset's serial number before this reading replaced it (null when it was empty or unchanged).</summary>
    public string? PreviousSerialNumber { get; init; }
    public bool TechnicianConfirmed { get; init; } = true;
    public string ConfirmedAt { get; init; } = Clock.UtcNow;
}

/// <summary>
/// Deterministic data-plate rules (Phase 7.3): label parsing, look-alike character hints, and the JSON kept
/// on the asset. The AI model never reads IDs; these rules only <em>suggest</em> values and the technician
/// confirms or corrects every one before it is saved. Pure: no UI or OCR engine types.
/// </summary>
public static class DataPlates
{
    /// <summary>Key in <c>assets.configuration_json</c>.</summary>
    public const string ConfigurationKey = "data_plate";
    public const int MaxValueLength = 64;
    public const int MaxPartNumbers = 12;
    public const int MaxRawTextLength = 4000;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Longest labels first so "MFG DATE" is a date, not a manufacturer, and "PART NO" beats "PART".
    private static readonly (DataPlateFieldKind Kind, string Pattern)[] Labels =
    [
        (DataPlateFieldKind.ManufactureDate, @"MFG\.?\s*DATE|DATE\s+OF\s+(?:MFG|MANUFACTURE)|MFD|MANUFACTURE\s+DATE|BUILD\s+DATE|DATE\s+MFD"),
        (DataPlateFieldKind.SerialNumber, @"SERIAL\s*(?:NO\.?|NUMBER|NUM\.?|#)|SER\.?\s*(?:NO\.?|#)|S\s*/\s*N|S[I1l|]N|SERIAL|SN"),
        (DataPlateFieldKind.PartNumber, @"PART\s*(?:NO\.?|NUMBER|NUM\.?|#)|P\s*/\s*N|PN"),
        (DataPlateFieldKind.Model, @"MODEL\s*(?:NO\.?|NUMBER|#)|MODEL|MDL|MOD\."),
        (DataPlateFieldKind.Manufacturer, @"MANUFACTURED\s+BY|MANUFACTURER|MFG\.?\s*BY|MFR\.?|MFG\.?|MAKE"),
    ];

    private static readonly Regex LabelRegex = new(
        @"(?<![A-Z0-9])(?:" + string.Join("|", Labels.Select((l, i) => $"(?<k{i}>{l.Pattern})")) + @")(?=[\s:#.\-=]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Finds labelled values ("S/N: 12345", "MODEL 660-28 SER NO 4711") in OCR lines. A label with nothing after
    /// it takes the next line when that line has no label of its own. Values are kept exactly as read (only
    /// spaces and separators around them are trimmed): look-alike characters are flagged, never "fixed".
    /// </summary>
    public static IReadOnlyList<DataPlateFieldCandidate> Parse(IReadOnlyList<string> lines)
    {
        var found = new List<DataPlateFieldCandidate>();
        for (int i = 0; i < lines.Count; i++)
        {
            string line = CollapseSpaces(lines[i]);
            var matches = LabelRegex.Matches(line);
            for (int m = 0; m < matches.Count; m++)
            {
                var match = matches[m];
                int end = m + 1 < matches.Count ? matches[m + 1].Index : line.Length;
                string value = TrimValue(line[(match.Index + match.Length)..end]);
                if (value.Length == 0 && m + 1 == matches.Count && i + 1 < lines.Count)
                {
                    string next = CollapseSpaces(lines[i + 1]);
                    if (next.Length > 0 && !LabelRegex.IsMatch(next))
                    {
                        value = TrimValue(next);
                        i++; // the next line was this label's value
                    }
                }
                if (value.Length == 0)
                    continue;
                if (value.Length > MaxValueLength)
                    value = value[..MaxValueLength];
                found.Add(new DataPlateFieldCandidate(KindOf(match), CollapseSpaces(match.Value).ToUpperInvariant(), value, line));
            }
        }
        return found;
    }

    private static DataPlateFieldKind KindOf(Match match)
    {
        for (int i = 0; i < Labels.Length; i++)
            if (match.Groups[$"k{i}"].Success)
                return Labels[i].Kind;
        return DataPlateFieldKind.Model;
    }

    private static string CollapseSpaces(string? s) =>
        Regex.Replace(s ?? "", @"\s+", " ").Trim();

    private static string TrimValue(string s) =>
        s.Trim().Trim(':', '#', '-', '=', '.', ',', ';', ' ').Trim();

    private static readonly (char A, char B)[] LookAlikes = [('0', 'O'), ('1', 'I'), ('5', 'S'), ('8', 'B'), ('2', 'Z'), ('6', 'G')];

    /// <summary>
    /// "Check 0 / O, 1 / I" when a value has a character OCR often confuses; "" otherwise. For IDs (model,
    /// serial, part number) any look-alike is flagged, since OCR can turn "TM-1" into "TM-I" with no digit
    /// left; for words (<paramref name="idField"/> false) only values mixing letters and digits are.
    /// The technician compares against the plate; nothing is changed automatically.
    /// </summary>
    public static string LookAlikeHint(string? value, bool idField = true)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        string v = value.ToUpperInvariant();
        if (!idField && (!v.Any(char.IsLetter) || !v.Any(char.IsDigit)))
            return "";
        var pairs = LookAlikes.Where(p => v.Contains(p.A) || v.Contains(p.B)).Select(p => $"{p.A} / {p.B}").ToList();
        return pairs.Count == 0 ? "" : "Check look-alike characters on the plate: " + string.Join(", ", pairs) + ".";
    }

    /// <summary>Trims a confirmed value; empty becomes null.</summary>
    public static string? CleanValue(string? value)
    {
        string v = CollapseSpaces(value);
        if (v.Length == 0)
            return null;
        return v.Length > MaxValueLength ? v[..MaxValueLength] : v;
    }

    /// <summary>Part numbers from a multi-line box: one per line (commas also split), trimmed, duplicates and blanks dropped.</summary>
    public static IReadOnlyList<string> SplitPartNumbers(string? text) =>
        (text ?? "")
            .Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(CleanValue)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxPartNumbers)
            .ToList();

    /// <summary>
    /// Returns <paramref name="configurationJson"/> with the reading stored under <see cref="ConfigurationKey"/>,
    /// keeping every other key. Throws <see cref="FormatException"/> when the existing JSON is not an object,
    /// so it is never overwritten.
    /// </summary>
    public static string MergeInto(string? configurationJson, DataPlateReading reading)
    {
        JsonObject root;
        if (string.IsNullOrWhiteSpace(configurationJson))
        {
            root = new JsonObject();
        }
        else
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(configurationJson);
            }
            catch (JsonException ex)
            {
                throw new FormatException("The asset's configuration is not valid JSON.", ex);
            }
            root = parsed as JsonObject ?? throw new FormatException("The asset's configuration is not a JSON object.");
        }

        var clean = reading with
        {
            Manufacturer = CleanValue(reading.Manufacturer),
            Model = CleanValue(reading.Model),
            SerialNumber = CleanValue(reading.SerialNumber),
            ManufactureDate = CleanValue(reading.ManufactureDate),
            PartNumbers = reading.PartNumbers.Select(CleanValue).OfType<string>().Take(MaxPartNumbers).ToList(),
            RawText = reading.RawText.Length > MaxRawTextLength ? reading.RawText[..MaxRawTextLength] : reading.RawText,
        };
        root[ConfigurationKey] = JsonSerializer.SerializeToNode(clean, Options);
        return root.ToJsonString();
    }

    /// <summary>The confirmed reading on an asset, or null when there is none or it cannot be read.</summary>
    public static DataPlateReading? Read(string? configurationJson)
    {
        if (string.IsNullOrWhiteSpace(configurationJson))
            return null;
        try
        {
            if (JsonNode.Parse(configurationJson) is not JsonObject root || root[ConfigurationKey] is not JsonObject plate)
                return null;
            return plate.Deserialize<DataPlateReading>(Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One-line summary: "Model 660 · S/N 12345 · 2 part numbers".</summary>
    public static string Summary(DataPlateReading reading)
    {
        var parts = new List<string>();
        if (reading.Manufacturer is { Length: > 0 } mfg) parts.Add(mfg);
        if (reading.Model is { Length: > 0 } model) parts.Add($"Model {model}");
        if (reading.SerialNumber is { Length: > 0 } sn) parts.Add($"S/N {sn}");
        if (reading.PartNumbers.Count == 1) parts.Add($"P/N {reading.PartNumbers[0]}");
        else if (reading.PartNumbers.Count > 1) parts.Add($"{reading.PartNumbers.Count} part numbers");
        if (reading.ManufactureDate is { Length: > 0 } date) parts.Add($"Built {date}");
        var sb = new StringBuilder(parts.Count == 0 ? "No values" : string.Join(" · ", parts));
        if (reading.IsSimulated)
            sb.Insert(0, "SIMULATED ");
        return sb.ToString();
    }
}
