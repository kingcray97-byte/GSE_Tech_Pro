using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.App.Services;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using Microsoft.UI.Xaml;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Manual Search › Import manuals: the technician picks PDF files or a folder, the AI service imports them
/// (POST /ingest, polled with GET /ingest/status) and the page lists what could not be read.
/// The AI service only reads the files; a copy is kept next to the database. Importing a manual that is already
/// in the library fills in its scanned pages (Phase 7.6) and changes nothing else.
/// </summary>
public partial class MainShellViewModel
{
    /// <summary>Most problem rows shown under the import panel (a 1,700-page manual can report many pages).</summary>
    public const int MaxManualImportProblemRows = 50;

    /// <summary>Set by MainWindow: file picker for one or more PDFs (null = cancelled).</summary>
    public Func<Task<IReadOnlyList<string>?>>? PickManualFilesAsync { get; set; }

    /// <summary>Set by MainWindow: folder picker (null = cancelled).</summary>
    public Func<Task<string?>>? PickManualFolderAsync { get; set; }

    /// <summary>How often the import job is polled. Tests shorten it.</summary>
    public TimeSpan ManualImportPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public ObservableCollection<string> ManualImportProblems { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualImportIdle))]
    [NotifyCanExecuteChangedFor(nameof(ImportManualFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportManualFolderCommand))]
    private bool _isManualImportRunning;

    public bool ManualImportIdle => !IsManualImportRunning;

    [ObservableProperty]
    private string _manualImportStatusText =
        "Import PDF service manuals so Tech Chat and Manual Search can cite them. Scanned pages are read by OCR.";

    [ObservableProperty]
    private double _manualImportProgress;

    public Visibility ManualImportProblemsVisibility =>
        ManualImportProblems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private bool CanStartManualImport() => !IsManualImportRunning;

    [RelayCommand(CanExecute = nameof(CanStartManualImport))]
    public async Task ImportManualFilesAsync()
    {
        var files = PickManualFilesAsync is null ? null : await PickManualFilesAsync();
        if (files is null || files.Count == 0)
            return;
        await ImportManualsAsync(files);
    }

    [RelayCommand(CanExecute = nameof(CanStartManualImport))]
    public async Task ImportManualFolderAsync()
    {
        var folder = PickManualFolderAsync is null ? null : await PickManualFolderAsync();
        if (string.IsNullOrWhiteSpace(folder))
            return;
        await ImportManualsAsync(new[] { folder });
    }

    /// <summary>Sends the paths to the AI service and follows the job until it ends.</summary>
    public async Task ImportManualsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var wanted = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count == 0 || IsManualImportRunning)
            return;

        IsManualImportRunning = true;
        ManualImportProgress = 0;
        ManualImportProblems.Clear();
        OnPropertyChanged(nameof(ManualImportProblemsVisibility));
        ManualImportStatusText = wanted.Count == 1
            ? $"Starting import of {Path.GetFileName(wanted[0].TrimEnd('\\', '/'))}…"
            : $"Starting import of {wanted.Count} items…";
        try
        {
            var jobId = await _aiClient.IngestAsync(new IngestRequest { Paths = wanted }, cancellationToken);
            IngestionStatus status;
            while (true)
            {
                status = await _aiClient.GetIngestStatusAsync(jobId, cancellationToken);
                ShowManualImportStatus(status);
                if (status.State is IngestionState.Succeeded or IngestionState.Failed or IngestionState.Cancelled)
                    break;
                await Task.Delay(ManualImportPollInterval, cancellationToken);
            }

            await ReloadManualsCatalogAsync();
            ManualImportStatusText = DescribeFinishedImport(status);
        }
        catch (AiServiceException ex)
        {
            ManualImportStatusText = $"Import refused: {ex.Error.Message}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ManualImportStatusText = "Stopped following the import. The AI service may still be importing.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            AiState.NotifyChatCallFailure(ex);
            ManualImportStatusText = "AI service is not reachable — start the local AI service to import manuals.";
        }
        finally
        {
            IsManualImportRunning = false;
        }
    }

    private void ShowManualImportStatus(IngestionStatus status)
    {
        ManualImportProgress = status.FilesTotal > 0 ? 100.0 * status.FilesDone / status.FilesTotal : 0;
        if (status.State is IngestionState.Pending or IngestionState.Running)
        {
            var file = string.IsNullOrWhiteSpace(status.CurrentFile) ? "" : $" — {status.CurrentFile}";
            ManualImportStatusText = status.FilesTotal == 0
                ? "Looking for PDF files…"
                : $"Importing {status.FilesDone} of {status.FilesTotal} file(s){file}. {status.ChunksCreated} passage(s) so far. Scanned pages take longer (OCR).";
        }

        ManualImportProblems.Clear();
        foreach (var line in DescribeManualImportProblems(status.Errors))
            ManualImportProblems.Add(line);
        OnPropertyChanged(nameof(ManualImportProblemsVisibility));
    }

    /// <summary>One line per problem, grouped so a manual with 80 unreadable pages gives one line per kind of
    /// problem, not 80 lines. Page numbers are listed (shortened past 12).</summary>
    public static IReadOnlyList<string> DescribeManualImportProblems(IReadOnlyList<IngestionError> errors)
    {
        var lines = new List<string>();
        foreach (var group in errors.GroupBy(e => (e.File, e.Message)))
        {
            var pages = group.Where(e => e.Page is not null).Select(e => e.Page!.Value).Distinct().OrderBy(p => p).ToList();
            var file = string.IsNullOrWhiteSpace(group.Key.File) ? "Import" : group.Key.File;
            if (pages.Count == 0)
            {
                lines.Add($"{file}: {group.Key.Message}");
                continue;
            }
            var shown = string.Join(", ", pages.Take(12));
            var more = pages.Count > 12 ? $" and {pages.Count - 12} more" : "";
            var label = pages.Count == 1 ? "page" : "pages";
            lines.Add($"{file}, {label} {shown}{more}: {group.Key.Message}");
            if (lines.Count >= MaxManualImportProblemRows)
                break;
        }
        return lines;
    }

    // ---- Manual library: what is imported, and Remove ----

    public ObservableCollection<ManualLibraryRowViewModel> ManualLibraryRows { get; } = new();

    public string ManualLibraryHeader => ManualLibraryRows.Count == 1
        ? "Manual library (1 manual)"
        : $"Manual library ({ManualLibraryRows.Count} manuals)";

    public Visibility NoManualsVisibility => ManualLibraryRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Set by MainWindow: asks the technician to confirm removing a manual (false = keep it).</summary>
    public Func<ManualLibraryRowViewModel, Task<bool>>? ConfirmRemoveManualAsync { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualLibraryStatusVisibility))]
    private string _manualLibraryStatusText = "";

    public Visibility ManualLibraryStatusVisibility =>
        string.IsNullOrWhiteSpace(ManualLibraryStatusText) ? Visibility.Collapsed : Visibility.Visible;

    private void RefreshManualLibraryRows()
    {
        ManualLibraryRows.Clear();
        foreach (var manual in ImportedManuals.OrderBy(m => m.Title, StringComparer.CurrentCultureIgnoreCase))
            ManualLibraryRows.Add(new ManualLibraryRowViewModel(manual));
        OnPropertyChanged(nameof(ManualLibraryHeader));
        OnPropertyChanged(nameof(NoManualsVisibility));
    }

    /// <summary>Takes a manual out of the library through the AI service after the technician confirms. Soft delete:
    /// its passages leave search and Tech Chat; the record and the stored PDF copy are kept, and importing the PDF again
    /// brings it back.</summary>
    public async Task RemoveManualAsync(ManualLibraryRowViewModel? row, CancellationToken cancellationToken = default)
    {
        if (row is null)
            return;
        if (IsManualImportRunning)
        {
            ManualLibraryStatusText = "Wait for the import to finish before removing a manual.";
            return;
        }
        if (ConfirmRemoveManualAsync is null || !await ConfirmRemoveManualAsync(row))
            return;

        try
        {
            var removed = await _aiClient.RemoveManualAsync(row.Id, cancellationToken);
            await ReloadManualsCatalogAsync();
            for (var i = ManualSearchResults.Count - 1; i >= 0; i--)
            {
                if (string.Equals(ManualSearchResults[i].ManualId, row.Id, StringComparison.OrdinalIgnoreCase))
                    ManualSearchResults.RemoveAt(i);
            }
            OnPropertyChanged(nameof(NoManualSearchResultsVisibility));
            var title = string.IsNullOrWhiteSpace(removed.Title) ? row.Title : removed.Title;
            ManualLibraryStatusText =
                $"Removed \"{title}\": {removed.ChunksRemoved} passage(s) no longer appear in search or Tech Chat. "
                + "The PDF copy is kept; import it again to bring it back.";
        }
        catch (AiServiceException ex) when (ex.Code == AiErrorCodes.NotFound)
        {
            await ReloadManualsCatalogAsync();
            ManualLibraryStatusText = $"\"{row.Title}\" was already removed.";
        }
        catch (AiServiceException ex)
        {
            ManualLibraryStatusText = $"Could not remove \"{row.Title}\": {ex.Error.Message}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            AiState.NotifyChatCallFailure(ex);
            ManualLibraryStatusText = "AI service is not reachable — start the local AI service to remove a manual.";
        }
    }

    private static string DescribeFinishedImport(IngestionStatus status)
    {
        var problems = status.Errors.Count == 0 ? "" : " See the list below for what could not be read.";
        return status.State switch
        {
            IngestionState.Succeeded when status.FilesTotal == 0 =>
                "No PDF files were found there." + problems,
            IngestionState.Succeeded when status.ChunksCreated == 0 && status.Errors.Count == 0 =>
                $"Done: {status.FilesDone} file(s) checked. Nothing new: every manual was already in the library.",
            IngestionState.Succeeded =>
                $"Done: {status.FilesDone} file(s) processed, {status.ChunksCreated} searchable passage(s) added." + problems,
            IngestionState.Cancelled =>
                $"Import cancelled after {status.FilesDone} of {status.FilesTotal} file(s)." + problems,
            _ => "Import failed: no manual could be imported." + problems,
        };
    }
}

/// <summary>One manual in the Manual Search library list.</summary>
public sealed class ManualLibraryRowViewModel
{
    public ManualLibraryRowViewModel(GseTechPro.Core.Models.Manual manual)
    {
        Manual = manual ?? throw new ArgumentNullException(nameof(manual));
    }

    public GseTechPro.Core.Models.Manual Manual { get; }

    public string Id => Manual.Id;

    public string Title => string.IsNullOrWhiteSpace(Manual.Title) ? "Untitled manual" : Manual.Title;

    /// <summary>"TUG · 660-28 · 1724 pages · imported 2026-11-20".</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Manual.Manufacturer)) parts.Add(Manual.Manufacturer!);
            if (!string.IsNullOrWhiteSpace(Manual.ModelFamily)) parts.Add(Manual.ModelFamily!);
            if (Manual.PageCount is > 0) parts.Add(Manual.PageCount == 1 ? "1 page" : $"{Manual.PageCount} pages");
            if (DateTimeOffset.TryParse(Manual.ImportedAt, out var at)) parts.Add($"imported {at.ToLocalTime():yyyy-MM-dd}");
            return string.Join(" · ", parts);
        }
    }
}
