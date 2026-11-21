using GseTechPro.Core.Contracts;
using Xunit;

namespace GseTechPro.Tests;

/// <summary>Phase 7.2: photo annotation rules and JSON.</summary>
public sealed class PhotoAnnotationsTests
{
    private static PhotoAnnotationShape Ink(params double[][] pts) =>
        new() { Kind = PhotoAnnotationKind.Ink, Points = pts };

    [Fact]
    public void RoundTrip_KeepsMarks_AndNoMarksIsNull()
    {
        var shapes = new[]
        {
            Ink([0.1, 0.1], [0.2, 0.25], [0.3, 0.2]),
            new PhotoAnnotationShape { Kind = PhotoAnnotationKind.Arrow, Color = "#FFD800", Points = [[0.5, 0.5], [0.7, 0.4]] },
            new PhotoAnnotationShape { Kind = PhotoAnnotationKind.Circle, Color = "#16c60c", Points = [[0.2, 0.6], [0.4, 0.9]] },
        };
        var json = PhotoAnnotations.Serialize(shapes);
        Assert.NotNull(json);
        Assert.Contains("\"kind\":\"arrow\"", json);

        var back = PhotoAnnotations.Deserialize(json);
        Assert.Equal(3, back.Count);
        Assert.Equal(PhotoAnnotationKind.Circle, back[2].Kind);
        Assert.Equal("#16C60C", back[2].Color); // palette spelling
        Assert.Equal(0.25, back[0].Points[1][1], 9);

        Assert.Null(PhotoAnnotations.Serialize([]));
        Assert.Empty(PhotoAnnotations.Deserialize(null));
        Assert.Empty(PhotoAnnotations.Deserialize("not json"));
    }

    [Fact]
    public void Normalize_ClampsDropsAndLimits()
    {
        var shapes = PhotoAnnotations.Normalize(
        [
            Ink([-0.5, 0.5], [1.5, 2.0]),                                         // clamped
            Ink([0.5, 0.5]),                                                     // one point: dropped
            Ink([double.NaN, 0.1], [0.2, 0.2]),                                  // one finite point left: dropped
            new PhotoAnnotationShape { Kind = PhotoAnnotationKind.Arrow, Points = [[0.1, 0.1], [0.2, 0.2], [0.9, 0.9]] }, // first + last
            new PhotoAnnotationShape { Kind = PhotoAnnotationKind.Ink, Color = "#123456", Width = 9, Points = [[0, 0], [1, 1]] }, // colour/width fixed
        ]);
        Assert.Equal(3, shapes.Count);
        Assert.Equal([0.0, 0.5], shapes[0].Points[0]);
        Assert.Equal([1.0, 1.0], shapes[0].Points[1]);
        Assert.Equal(2, shapes[1].Points.Count);
        Assert.Equal([0.9, 0.9], shapes[1].Points[1]);
        Assert.Equal(PhotoAnnotations.Palette[0], shapes[2].Color);
        Assert.Equal(0.05, shapes[2].Width, 9);

        var longInk = Ink(Enumerable.Range(0, 5000).Select(i => new[] { i / 5000.0, 0.5 }).ToArray());
        Assert.Equal(PhotoAnnotations.MaxPointsPerShape, PhotoAnnotations.Normalize([longInk])[0].Points.Count);
        Assert.Equal(PhotoAnnotations.MaxShapes,
            PhotoAnnotations.Normalize(Enumerable.Repeat(Ink([0, 0], [1, 1]), 600)).Count);

        Assert.Equal("1 mark", PhotoAnnotations.Summary(1));
        Assert.Equal("3 marks", PhotoAnnotations.Summary(3));
        Assert.Equal("", PhotoAnnotations.Summary(0));
    }
}
