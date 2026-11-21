using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace GseTechPro.App;

/// <summary>
/// Data-plate OCR box (Phase 7.3). While reading a data plate, a drag on the photo (pen, finger or mouse)
/// sets the part of the photo to read. Coordinates are photo pixels on the shared surface, stored as 0–1
/// fractions by the view model. The box is drawn on its own canvas so pen marks are left alone.
/// </summary>
public sealed partial class MainWindow
{
    private bool OnDataPlateRegionPointerPressed(PointerRoutedEventArgs e)
    {
        if (!ViewModel.IsReadingDataPlate)
            return false;
        CameraAnnotationCanvas.CapturePointer(e.Pointer);
        var (x, y) = ToFraction(e);
        ViewModel.BeginDataPlateRegion(x, y);
        e.Handled = true;
        return true;
    }

    private bool OnDataPlateRegionPointerMoved(PointerRoutedEventArgs e)
    {
        if (!ViewModel.IsReadingDataPlate)
            return false;
        if (e.Pointer.IsInContact)
        {
            var (x, y) = ToFraction(e);
            ViewModel.ExtendDataPlateRegion(x, y);
        }
        e.Handled = true;
        return true;
    }

    private bool OnDataPlateRegionPointerReleased(PointerRoutedEventArgs e)
    {
        if (!ViewModel.IsReadingDataPlate)
            return false;
        ViewModel.EndDataPlateRegion();
        CameraAnnotationCanvas.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        return true;
    }

    private void RedrawDataPlateRegion()
    {
        CameraRegionCanvas.Children.Clear();
        if (ViewModel.DataPlateRegion is not { } r)
            return;
        double w = CameraPhotoSurface.Width, h = CameraPhotoSurface.Height;
        double thickness = Math.Max(2, w * 0.004);
        // Yellow dashed box over a dark outline: readable on bright plates and dark paint alike.
        foreach (var (brush, dash) in new[] { (new SolidColorBrush(Colors.Black), false), (new SolidColorBrush(Colors.Yellow), true) })
        {
            var rect = new Rectangle
            {
                Width = r.Width * w,
                Height = r.Height * h,
                Stroke = brush,
                StrokeThickness = dash ? thickness : thickness * 2,
            };
            if (dash)
                rect.StrokeDashArray = new DoubleCollection { 4, 2 };
            Canvas.SetLeft(rect, r.X * w);
            Canvas.SetTop(rect, r.Y * h);
            CameraRegionCanvas.Children.Add(rect);
        }
    }
}
