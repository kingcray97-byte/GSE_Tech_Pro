using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.Core.Contracts;
using Microsoft.UI.Xaml;

namespace GseTechPro.App.ViewModels;

/// <summary>A colour swatch in the annotation toolbar.</summary>
public sealed record AnnotationColorItem(string Hex, string Name, Microsoft.UI.Xaml.Media.SolidColorBrush? Brush)
{
    public static Windows.UI.Color ParseHex(string hex)
    {
        var h = hex.TrimStart('#');
        return Windows.UI.Color.FromArgb(0xFF,
            Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16));
    }
}

/// <summary>
/// Pen annotation on a saved photo (Phase 7.2): pen, arrow and circle in high-visibility colours,
/// undo and clear, saved as vector marks in <c>attachments.annotations_json</c>. The photo file is
/// never changed. The window's drawing surface reports pointer positions as fractions of the image
/// (0–1) to <see cref="BeginAnnotationStroke"/> / <see cref="ExtendAnnotationStroke"/> /
/// <see cref="EndAnnotationStroke"/>, and redraws when <see cref="AnnotationShapesChanged"/> fires.
/// </summary>
public sealed partial class MainShellViewModel
{
    public const string AnnotationToolPen = "Pen";
    public const string AnnotationToolArrow = "Arrow";
    public const string AnnotationToolCircle = "Circle";

    /// <summary>An arrow or circle shorter than this (fraction of the image) is a stray tap, not a mark.</summary>
    public const double MinShapeSize = 0.01;

    public IReadOnlyList<string> AnnotationTools { get; } = [AnnotationToolPen, AnnotationToolArrow, AnnotationToolCircle];

    public IReadOnlyList<string> AnnotationColors => PhotoAnnotations.Palette;

    [ObservableProperty]
    private string _selectedAnnotationTool = AnnotationToolPen;

    [ObservableProperty]
    private string _selectedAnnotationColor = PhotoAnnotations.Palette[0];

    private static readonly string[] AnnotationColorNames = ["Red", "Yellow", "Green", "Cyan", "White", "Black"];
    private IReadOnlyList<AnnotationColorItem>? _annotationColorItems;

    /// <summary>Swatches for the toolbar (brushes are made on first use, on the UI thread).</summary>
    public IReadOnlyList<AnnotationColorItem> AnnotationColorItems => _annotationColorItems ??= PhotoAnnotations.Palette
        .Select((hex, i) => new AnnotationColorItem(hex, AnnotationColorNames[i],
            ScopeMeasurementRowViewModel.CreateSafeBrush(AnnotationColorItem.ParseHex(hex))))
        .ToList();

    public AnnotationColorItem? SelectedAnnotationColorItem
    {
        get => AnnotationColorItems.FirstOrDefault(c => c.Hex == SelectedAnnotationColor);
        set
        {
            if (value is not null && value.Hex != SelectedAnnotationColor)
            {
                SelectedAnnotationColor = value.Hex;
                OnPropertyChanged();
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnnotationToolbarVisibility))]
    [NotifyPropertyChangedFor(nameof(AnnotateButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(DataPlateButtonVisibility))]
    private bool _isAnnotating;

    public Visibility AnnotationToolbarVisibility => IsAnnotating ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>"Annotate" shows for a saved photo that is on screen, while not already annotating.</summary>
    public Visibility AnnotateButtonVisibility =>
        !IsAnnotating && !IsReadingDataPlate && PendingPhoto is null && SelectedWorkOrderPhoto is { FileExists: true }
            ? Visibility.Visible : Visibility.Collapsed;

    private readonly List<PhotoAnnotationShape> _annotationShapes = new();
    private PhotoAnnotationKind _strokeKind;
    private List<double[]>? _strokePoints;
    private bool _annotationsDirty;

    /// <summary>Raised when the marks change; the argument is true when only the last mark changed (a stroke in progress).</summary>
    public event EventHandler<bool>? AnnotationShapesChanged;

    /// <summary>Marks to draw: the saved or edited ones, plus the stroke in progress.</summary>
    public IReadOnlyList<PhotoAnnotationShape> AnnotationShapesForDisplay
    {
        get
        {
            if (_strokePoints is null || _strokePoints.Count < 2)
                return _annotationShapes.ToList();
            return _annotationShapes.Append(CurrentStrokeShape()).ToList();
        }
    }

    public int AnnotationCount => _annotationShapes.Count;

    public bool HasUnsavedAnnotations => _annotationsDirty;

    private PhotoAnnotationShape CurrentStrokeShape() => new()
    {
        Kind = _strokeKind,
        Color = SelectedAnnotationColor,
        Width = PhotoAnnotations.DefaultWidth,
        Points = _strokePoints!.ToList(),
    };

    private void RaiseAnnotationsChanged(bool lastOnly)
    {
        OnPropertyChanged(nameof(AnnotationCount));
        AnnotationShapesChanged?.Invoke(this, lastOnly);
    }

    /// <summary>Shows a photo's saved marks (read-only until Annotate is tapped).</summary>
    private void LoadAnnotationsFor(WorkOrderPhotoRowViewModel? photo)
    {
        if (IsAnnotating && _annotationsDirty)
            CameraStatusText = "Annotation changes that were not saved were discarded.";
        IsAnnotating = false;
        _annotationsDirty = false;
        _strokePoints = null;
        _annotationShapes.Clear();
        if (photo is not null)
            _annotationShapes.AddRange(PhotoAnnotations.Deserialize(photo.Attachment.AnnotationsJson));
        OnPropertyChanged(nameof(AnnotateButtonVisibility));
        OnPropertyChanged(nameof(DataPlateButtonVisibility));
        RaiseAnnotationsChanged(lastOnly: false);
    }

    [RelayCommand]
    public void StartAnnotating()
    {
        if (PendingPhoto is not null)
        {
            CameraStatusText = "Save the new photo to the work order first, then annotate it.";
            return;
        }
        if (SelectedWorkOrderPhoto is not { FileExists: true })
        {
            CameraStatusText = "Pick a saved photo to annotate.";
            return;
        }
        if (IsReadingDataPlate)
        {
            CameraStatusText = "Finish or cancel the data-plate reading first.";
            return;
        }
        IsAnnotating = true;
        CameraStatusText = "Draw on the photo with pen, finger or mouse. The photo itself is never changed. Tap Save marks when done.";
    }

    public void BeginAnnotationStroke(double x, double y)
    {
        if (!IsAnnotating)
            return;
        _strokeKind = SelectedAnnotationTool switch
        {
            AnnotationToolArrow => PhotoAnnotationKind.Arrow,
            AnnotationToolCircle => PhotoAnnotationKind.Circle,
            _ => PhotoAnnotationKind.Ink,
        };
        var p = Clamp(x, y);
        _strokePoints = [p, p];
    }

    public void ExtendAnnotationStroke(double x, double y)
    {
        if (!IsAnnotating || _strokePoints is null)
            return;
        var p = Clamp(x, y);
        if (_strokeKind == PhotoAnnotationKind.Ink)
        {
            var last = _strokePoints[^1];
            if (Distance(last, p) < PhotoAnnotations.MinPointSpacing || _strokePoints.Count >= PhotoAnnotations.MaxPointsPerShape)
                return;
            if (_strokePoints.Count == 2 && Distance(_strokePoints[0], _strokePoints[1]) == 0)
                _strokePoints[1] = p; // replace the duplicate start point
            else
                _strokePoints.Add(p);
        }
        else
        {
            _strokePoints[1] = p; // arrow / circle: start and current point
        }
        RaiseAnnotationsChanged(lastOnly: true);
    }

    public void EndAnnotationStroke()
    {
        if (_strokePoints is null)
            return;
        var shape = CurrentStrokeShape();
        _strokePoints = null;
        double size = Distance(shape.Points[0], shape.Points[^1]);
        bool keep = shape.Kind == PhotoAnnotationKind.Ink
            ? shape.Points.Count >= 2 && shape.Points.Zip(shape.Points.Skip(1), Distance).Sum() >= MinShapeSize / 2
            : size >= MinShapeSize;
        if (keep && _annotationShapes.Count < PhotoAnnotations.MaxShapes)
        {
            _annotationShapes.Add(shape);
            _annotationsDirty = true;
        }
        else if (keep)
        {
            CameraStatusText = $"A photo can have at most {PhotoAnnotations.MaxShapes} marks.";
        }
        RaiseAnnotationsChanged(lastOnly: false);
    }

    [RelayCommand]
    public void UndoAnnotation()
    {
        if (!IsAnnotating || _annotationShapes.Count == 0)
            return;
        _annotationShapes.RemoveAt(_annotationShapes.Count - 1);
        _annotationsDirty = true;
        RaiseAnnotationsChanged(lastOnly: false);
    }

    [RelayCommand]
    public void ClearAnnotations()
    {
        if (!IsAnnotating || _annotationShapes.Count == 0)
            return;
        _annotationShapes.Clear();
        _annotationsDirty = true;
        RaiseAnnotationsChanged(lastOnly: false);
        CameraStatusText = "All marks removed from the screen. Save marks to keep the change, or Cancel to get them back.";
    }

    /// <summary>Drops unsaved changes and shows the saved marks again.</summary>
    [RelayCommand]
    public void CancelAnnotating()
    {
        _annotationsDirty = false;
        LoadAnnotationsFor(SelectedWorkOrderPhoto);
        CameraStatusText = "Annotation cancelled; the saved marks are shown.";
    }

    /// <summary>Saves the marks to the photo's attachment row (the photo file is not touched).</summary>
    [RelayCommand]
    public async Task<bool> SaveAnnotationsAsync()
    {
        if (!IsAnnotating || SelectedWorkOrderPhoto is not { } photo)
            return false;
        var updated = photo.Attachment with { AnnotationsJson = PhotoAnnotations.Serialize(_annotationShapes) };
        try
        {
            if (!await _dataServices.Attachments.UpdateAsync(updated))
            {
                CameraStatusText = "Could not save the marks: the photo record was not found.";
                return false;
            }
        }
        catch (Exception ex)
        {
            CameraStatusText = $"Could not save the marks: {ex.Message}";
            return false;
        }

        int n = _annotationShapes.Count;
        _annotationsDirty = false;
        IsAnnotating = false;
        await ReloadWorkOrderAttachmentsAsync(photo.Attachment.TargetId); // keeps the selection, reloads the marks
        CameraStatusText = n == 0 ? "Marks removed from the photo." : $"Saved {PhotoAnnotations.Summary(n)} on the photo.";
        return true;
    }

    private static double[] Clamp(double x, double y) =>
        [double.IsFinite(x) ? Math.Clamp(x, 0, 1) : 0, double.IsFinite(y) ? Math.Clamp(y, 0, 1) : 0];

    private static double Distance(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]));
}
