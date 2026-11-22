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
