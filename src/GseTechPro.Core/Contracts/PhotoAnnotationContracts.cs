using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GseTechPro.Core.Contracts;

/// <summary>Kind of mark drawn on a photo (Phase 7.2).</summary>
public enum PhotoAnnotationKind
{
    /// <summary>Free-hand ink: any number of points.</summary>
    Ink,
    /// <summary>Straight arrow: tail, then head.</summary>
    Arrow,
    /// <summary>Ellipse inside the box from the first to the second point.</summary>
    Circle,
}

/// <summary>
/// One mark on a photo. Points are fractions of the image (x across, y down, 0–1), so marks stay in
/// place at any display size; <see cref="Width"/> is the line width as a fraction of the image width.
/// </summary>
public sealed record PhotoAnnotationShape
{
    public PhotoAnnotationKind Kind { get; init; }
    /// <summary>"#RRGGBB" from <see cref="PhotoAnnotations.Palette"/>.</summary>
    public string Color { get; init; } = PhotoAnnotations.Palette[0];
    public double Width { get; init; } = PhotoAnnotations.DefaultWidth;
    /// <summary>[x, y] pairs, each 0–1.</summary>
    public IReadOnlyList<double[]> Points { get; init; } = Array.Empty<double[]>();
}

/// <summary>All marks on one photo; stored as JSON in <c>attachments.annotations_json</c>. The photo file is never changed.</summary>
public sealed record PhotoAnnotationSet
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<PhotoAnnotationShape> Shapes { get; init; } = Array.Empty<PhotoAnnotationShape>();
}

/// <summary>Rules and JSON for photo annotations (Phase 7.2). Pure: no UI types.</summary>
public static class PhotoAnnotations
{
    /// <summary>High-visibility colours that read in sunlight: red, yellow, green, cyan, white, black.</summary>
    public static IReadOnlyList<string> Palette { get; } = ["#E81123", "#FFD800", "#16C60C", "#00B7FF", "#FFFFFF", "#000000"];

    public const double DefaultWidth = 0.006;
    public const int MaxShapes = 500;
    public const int MaxPointsPerShape = 2000;

    /// <summary>Points closer than this (fraction of the image) to the previous one are not added to ink.</summary>
    public const double MinPointSpacing = 0.002;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>JSON for the column, or null when there are no marks (so "no annotations" stays NULL).</summary>
    public static string? Serialize(IEnumerable<PhotoAnnotationShape> shapes)
    {
        var clean = Normalize(shapes);
        return clean.Count == 0 ? null : JsonSerializer.Serialize(new PhotoAnnotationSet { Shapes = clean }, Options);
    }

    /// <summary>Marks from the column. Null, empty or unreadable JSON gives no marks (the photo still opens).</summary>
    public static IReadOnlyList<PhotoAnnotationShape> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<PhotoAnnotationShape>();
        try
        {
            var set = JsonSerializer.Deserialize<PhotoAnnotationSet>(json, Options);
            return set is null ? Array.Empty<PhotoAnnotationShape>() : Normalize(set.Shapes);
        }
        catch (JsonException)
        {
            return Array.Empty<PhotoAnnotationShape>();
        }
    }

    /// <summary>
    /// Keeps only usable marks: points clamped to the image, non-finite points dropped, ink with at
    /// least 2 points, arrows and circles with exactly their 2 points (first and last), a colour from
    /// the palette, a sane width, at most <see cref="MaxPointsPerShape"/> points and <see cref="MaxShapes"/> marks.
    /// </summary>
    public static IReadOnlyList<PhotoAnnotationShape> Normalize(IEnumerable<PhotoAnnotationShape>? shapes)
    {
        var result = new List<PhotoAnnotationShape>();
        if (shapes is null)
            return result;
        foreach (var s in shapes)
        {
            if (s?.Points is null)
                continue;
            var pts = s.Points
                .Where(p => p is { Length: >= 2 } && double.IsFinite(p[0]) && double.IsFinite(p[1]))
                .Select(p => new[] { Math.Clamp(p[0], 0.0, 1.0), Math.Clamp(p[1], 0.0, 1.0) })
                .ToList();
            if (pts.Count < 2)
                continue;
            if (s.Kind is PhotoAnnotationKind.Arrow or PhotoAnnotationKind.Circle)
                pts = [pts[0], pts[^1]];
            else if (pts.Count > MaxPointsPerShape)
                pts = Decimate(pts, MaxPointsPerShape);

            string color = Palette.FirstOrDefault(c => string.Equals(c, s.Color, StringComparison.OrdinalIgnoreCase)) ?? Palette[0];
            double width = double.IsFinite(s.Width) && s.Width > 0 ? Math.Clamp(s.Width, 0.001, 0.05) : DefaultWidth;
            result.Add(new PhotoAnnotationShape { Kind = s.Kind, Color = color, Width = width, Points = pts });
            if (result.Count == MaxShapes)
                break;
        }
        return result;
    }

    /// <summary>"3 marks", "1 mark" or "".</summary>
    public static string Summary(int count) =>
        count == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"{count} mark{(count == 1 ? "" : "s")}");

    private static List<double[]> Decimate(List<double[]> pts, int max)
    {
        var outPts = new List<double[]>(max);
        double step = (pts.Count - 1) / (double)(max - 1);
        for (int i = 0; i < max; i++)
            outPts.Add(pts[(int)Math.Round(i * step)]);
        return outPts;
    }
}
