using GseTechPro.App.ViewModels;
using GseTechPro.Core.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace GseTechPro.App;

/// <summary>
/// Camera pen annotation surface (Phase 7.2). The photo and the canvas share one grid sized to the
/// photo's pixels inside a Viewbox, so canvas coordinates are photo pixels: dividing by the surface
/// size gives the 0–1 fractions the view model stores. Pen, touch and mouse all draw.
/// </summary>
public sealed partial class MainWindow
{
    private bool _annotationHooked;
    private UIElement? _inProgressMark;

    private void HookAnnotationSurface()
    {
        if (_annotationHooked)
            return;
        _annotationHooked = true;
        ViewModel.AnnotationShapesChanged += (_, lastOnly) => RedrawAnnotations(lastOnly);
    }

    private (double X, double Y) ToFraction(PointerRoutedEventArgs e)
    {
        Point p = e.GetCurrentPoint(CameraAnnotationCanvas).Position;
        double w = Math.Max(1, CameraPhotoSurface.Width), h = Math.Max(1, CameraPhotoSurface.Height);
        return (p.X / w, p.Y / h);
    }

    private void OnAnnotationPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.IsAnnotating)
            return;
        CameraAnnotationCanvas.CapturePointer(e.Pointer);
        var (x, y) = ToFraction(e);
        ViewModel.BeginAnnotationStroke(x, y);
        e.Handled = true;
    }

    private void OnAnnotationPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.IsAnnotating || !e.Pointer.IsInContact)
            return;
        var (x, y) = ToFraction(e);
        ViewModel.ExtendAnnotationStroke(x, y);
        e.Handled = true;
    }

    private void OnAnnotationPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.IsAnnotating)
            return;
        ViewModel.EndAnnotationStroke();
        CameraAnnotationCanvas.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    /// <summary>Redraws the marks; while a stroke is in progress only that mark is replaced.</summary>
    private void RedrawAnnotations(bool lastOnly)
    {
        var shapes = ViewModel.AnnotationShapesForDisplay;
        if (lastOnly && shapes.Count > 0)
        {
            if (_inProgressMark is not null)
                CameraAnnotationCanvas.Children.Remove(_inProgressMark);
            _inProgressMark = BuildMark(shapes[^1]);
            CameraAnnotationCanvas.Children.Add(_inProgressMark);
            return;
        }

        _inProgressMark = null;
        CameraAnnotationCanvas.Children.Clear();
        foreach (var s in shapes)
            CameraAnnotationCanvas.Children.Add(BuildMark(s));
    }

    private UIElement BuildMark(PhotoAnnotationShape s)
    {
        double w = CameraPhotoSurface.Width, h = CameraPhotoSurface.Height;
        var brush = new SolidColorBrush(AnnotationColorItem.ParseHex(s.Color));
        double thickness = Math.Max(2, s.Width * w);
        Point P(double[] p) => new(p[0] * w, p[1] * h);

        switch (s.Kind)
        {
            case PhotoAnnotationKind.Circle:
            {
                var a = P(s.Points[0]);
                var b = P(s.Points[^1]);
                var ellipse = new Ellipse
                {
                    Width = Math.Abs(b.X - a.X),
                    Height = Math.Abs(b.Y - a.Y),
                    Stroke = brush,
                    StrokeThickness = thickness,
                };
                Canvas.SetLeft(ellipse, Math.Min(a.X, b.X));
                Canvas.SetTop(ellipse, Math.Min(a.Y, b.Y));
                return ellipse;
            }
            case PhotoAnnotationKind.Arrow:
            {
                var tail = P(s.Points[0]);
                var head = P(s.Points[^1]);
                double angle = Math.Atan2(head.Y - tail.Y, head.X - tail.X);
                double len = thickness * 4;
                Point Wing(double off) => new(head.X - len * Math.Cos(angle + off), head.Y - len * Math.Sin(angle + off));
                var group = new Canvas();
                group.Children.Add(new Line
                {
                    X1 = tail.X, Y1 = tail.Y, X2 = head.X, Y2 = head.Y,
                    Stroke = brush, StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round,
                });
                group.Children.Add(new Polygon
                {
                    Points = new PointCollection { head, Wing(0.45), Wing(-0.45) },
                    Fill = brush,
                });
                return group;
            }
            default:
            {
                var line = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = thickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                var pts = new PointCollection();
                foreach (var p in s.Points)
                    pts.Add(P(p));
                line.Points = pts;
                return line;
            }
        }
    }
}
