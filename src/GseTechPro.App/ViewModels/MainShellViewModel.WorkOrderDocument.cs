using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using Microsoft.UI.Xaml;

namespace GseTechPro.App.ViewModels;

/// <summary>One label/value row in an RO Viewer section (Shop Foreman Pro's grid rows).</summary>
public sealed record RoFieldRow(string Label, string Value);

/// <summary>One RO Viewer section: a navy tile (icon + title) beside its label/value rows.</summary>
public sealed record RoSection(string Title, string Glyph, IReadOnlyList<RoFieldRow> Rows);

/// <summary>One row of the Integrated Diagnostics grid: everything saved against the work order.</summary>
public sealed record DiagnosticRecordRow(
    string RecordType,
    string Glyph,
    string Origin,
    string DateTime,
    string Technician,
    string Note,
    string Preview,
    bool IsSimulated,
    string SortKey)
{
    public Visibility SimulatedBadgeVisibility => IsSimulated ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// Phase 10B.2: the work order as a Shop Foreman Pro document with two tabs.
/// RO Viewer = navy section tiles with label/value rows (GSE mapping: Vehicle → Equipment, Customer → Station).
/// Integrated Diagnostics = a record grid (readings, scope waveforms, repairs, outcome), a preview, and the record note.
/// Everything is read from data already loaded for the active work order; nothing is invented.
/// </summary>
public partial class MainShellViewModel
{
    private const string NotRecorded = "—";

    /// <summary>0 = RO Viewer, 1 = Integrated Diagnostics.</summary>
    [ObservableProperty]
    private int _workOrderDocumentTabIndex;

    public Visibility RoViewerVisibility => WorkOrderDocumentTabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IntegratedDiagnosticsVisibility => WorkOrderDocumentTabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;

    partial void OnWorkOrderDocumentTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(RoViewerVisibility));
        OnPropertyChanged(nameof(IntegratedDiagnosticsVisibility));
    }

    public ObservableCollection<RoSection> RoSections { get; } = new();

    public ObservableCollection<DiagnosticRecordRow> DiagnosticRecords { get; } = new();

    [ObservableProperty]
    private DiagnosticRecordRow? _selectedDiagnosticRecord;

    partial void OnSelectedDiagnosticRecordChanged(DiagnosticRecordRow? value)
    {
        OnPropertyChanged(nameof(DiagnosticRecordPreviewText));
        OnPropertyChanged(nameof(DiagnosticRecordNoteText));
    }

    public Visibility NoDiagnosticRecordsVisibility => DiagnosticRecords.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public string DiagnosticRecordPreviewText => SelectedDiagnosticRecord?.Preview
        ?? (ActiveWorkOrder is null
            ? "Open a work order to see its diagnostic records."
            : "Select a record to preview it.");

    public string DiagnosticRecordNoteText => SelectedDiagnosticRecord is null
        ? string.Empty
        : string.IsNullOrWhiteSpace(SelectedDiagnosticRecord.Note) ? "(No note on this record)" : SelectedDiagnosticRecord.Note;

    private bool _workOrderDocumentHooked;

    /// <summary>Rebuild the document whenever the active work order's records reload.</summary>
    private void HookWorkOrderDocument()
    {
        if (_workOrderDocumentHooked)
            return;
        _workOrderDocumentHooked = true;
        NotifyCollectionChangedEventHandler rebuild = (_, _) => RebuildWorkOrderDocument();
        WorkOrderReadings.CollectionChanged += rebuild;
        WorkOrderWaveforms.CollectionChanged += rebuild;
        WorkOrderRepairs.CollectionChanged += rebuild;
        Assets.CollectionChanged += rebuild;
    }

    /// <summary>Rebuilds both tabs from the active work order. Safe to call often (cheap, in-memory only).</summary>
    public void RebuildWorkOrderDocument()
    {
        HookWorkOrderDocument();
        var wo = ActiveWorkOrder;

        RoSections.Clear();
        foreach (var section in BuildRoSections(wo))
            RoSections.Add(section);

        var previousKey = SelectedDiagnosticRecord?.SortKey;
        DiagnosticRecords.Clear();
        if (wo is not null)
        {
            foreach (var row in BuildDiagnosticRecords(wo).Concat(BuildAttachmentRecords()).OrderByDescending(r => r.SortKey, StringComparer.Ordinal))
                DiagnosticRecords.Add(row);
        }
        SelectedDiagnosticRecord = DiagnosticRecords.FirstOrDefault(r => r.SortKey == previousKey) ?? DiagnosticRecords.FirstOrDefault();

        OnPropertyChanged(nameof(NoDiagnosticRecordsVisibility));
        OnPropertyChanged(nameof(DiagnosticRecordPreviewText));
        OnPropertyChanged(nameof(DiagnosticRecordNoteText));
    }

    private IEnumerable<RoSection> BuildRoSections(WorkOrder? wo)
    {
        var asset = wo is null
            ? null
            : Assets.FirstOrDefault(a => string.Equals(a.Id, wo.AssetId, StringComparison.OrdinalIgnoreCase));
        var model = asset?.ModelId is { } modelId
            ? GuidedEquipmentModels.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase))?.Model
            : null;
        _outcomesByWorkOrderId.TryGetValue(wo?.Id ?? string.Empty, out var outcome);

        yield return new RoSection("Repair Order", "", new[]
        {
            Row("Work Order Number", wo is null ? "No work order open" : WorkOrderBrowserRowViewModel.FormatWorkOrderNumber(wo)),
            Row("Status", wo is null ? null : StatusText(wo.Status)),
            Row("Priority", wo?.Priority),
            Row("Opened", FormatTimestamp(wo?.OpenedAt)),
            Row("Closed", FormatTimestamp(wo?.ClosedAt)),
            Row("Labor Hours", wo?.LaborHours?.ToString("0.0#", CultureInfo.CurrentCulture)),
        });

        yield return new RoSection("Equipment", "", new[]
        {
            Row("Asset Number", asset?.AssetNumber ?? (string.IsNullOrWhiteSpace(wo?.AssetId) ? null : wo!.AssetId)),
            Row("Name", asset?.Name),
            Row("Make", model?.Make),
            Row("Model", model?.Model),
            Row("Serial Number", asset?.SerialNumber),
            Row("Engine", JoinParts(model?.EngineMake, model?.EngineModel)),
            Row("Hour Meter In", (wo?.HoursAtOpen ?? asset?.HourMeter)?.ToString("N1", CultureInfo.CurrentCulture) is { } h ? $"{h} h" : null),
        });

        yield return new RoSection("Symptoms", "", new[]
        {
            Row("Complaint", wo?.ComplaintSummary),
            Row("Symptoms", wo?.SymptomsSummary),
        });

        yield return new RoSection("Diagnosis & Repair", "", new[]
        {
            Row("Diagnosis", wo?.DiagnosisSummary),
            Row("Repair Summary", wo?.RepairSummary),
            Row("Repairs Done", WorkOrderRepairs.Count == 0 ? null : string.Join("; ", WorkOrderRepairs.Select(r => r.Description))),
        });

        yield return new RoSection("Station", "", new[]
        {
            Row("Station / Location", asset?.LocationStation),
        });

        yield return new RoSection("Outcome", "", new[]
        {
            Row("Result", outcome is null ? (wo is null ? null : wo.Status == WorkOrderStatus.Closed ? "Closed" : "Open — not closed yet")
                : outcome.ComebackDetected ? $"{outcome.Result} (comeback confirmed)" : outcome.Result.ToString()),
            Row("Reason", outcome?.Reason),
        });

        yield return new RoSection("Technician", "", new[] { Row("Technician", SelectedTechnician) });

        yield return new RoSection("Shop Info", "", new[] { Row("Shop", ShopNameText) });
    }

    private IEnumerable<DiagnosticRecordRow> BuildDiagnosticRecords(WorkOrder wo)
    {
        foreach (var r in WorkOrderReadings)
        {
            var reading = r.Reading;
            var preview = $"{r.TargetName} ({r.ReadingTypeLabel})\nValue: {r.DisplayValueAndUnit}\nSpec: {r.SpecSummary}\nResult: {r.PassFailLabel}";
            yield return new DiagnosticRecordRow(
                "Reading", "", r.TargetName, FormatTimestamp(reading.EnteredAt) ?? NotRecorded,
                reading.EnteredBy ?? NotRecorded, reading.Notes ?? string.Empty, preview, reading.IsSimulated,
                reading.EnteredAt + "|r|" + reading.Id);
        }

        foreach (var w in WorkOrderWaveforms)
        {
            var preview = $"{w.GuidedTestName}\n{w.ChannelSummary}\n{w.TimebaseDisplay} · {w.SampleRateDisplay} · {w.SampleCountDisplay}\nResult: {w.OverallVerdictBadge}";
            yield return new DiagnosticRecordRow(
                "Scope Waveform", "", w.GuidedTestName, FormatTimestamp(w.Waveform.CreatedAt) ?? NotRecorded,
                NotRecorded, string.Empty, preview, w.IsSimulated,
                w.Waveform.CreatedAt + "|w|" + w.Id);
        }

        foreach (var repair in WorkOrderRepairs)
        {
            var preview = $"{repair.Description}\nSystem: {repair.System ?? NotRecorded}\nLabor: {(repair.LaborHours is { } l ? $"{l:0.0#} h" : NotRecorded)}";
            yield return new DiagnosticRecordRow(
                "Repair", "", repair.System ?? "Repair", FormatTimestamp(repair.PerformedAt) ?? NotRecorded,
                repair.PerformedBy ?? NotRecorded, repair.Description, preview, false,
                repair.PerformedAt + "|p|" + repair.Id);
        }

        if (_outcomesByWorkOrderId.TryGetValue(wo.Id, out var outcome))
        {
            var preview = $"Outcome: {outcome.Result}{(outcome.ComebackDetected ? " (comeback confirmed)" : "")}";
            yield return new DiagnosticRecordRow(
                "Outcome", "", "Close work order", FormatTimestamp(outcome.ClosedAt) ?? NotRecorded,
                outcome.ClosedBy ?? NotRecorded, outcome.Reason ?? string.Empty, preview, false,
                outcome.ClosedAt + "|o|" + outcome.Id);
        }
    }

    private static RoFieldRow Row(string label, string? value) =>
        new(label, string.IsNullOrWhiteSpace(value) ? NotRecorded : value);

    private static string? JoinParts(params string?[] parts)
    {
        var text = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return text.Length == 0 ? null : text;
    }

    private static string StatusText(WorkOrderStatus status) => status switch
    {
        WorkOrderStatus.Open => "Open",
        WorkOrderStatus.InProgress => "In Progress",
        WorkOrderStatus.WaitingParts => "Waiting Parts",
        WorkOrderStatus.Closed => "Closed",
        _ => status.ToString(),
    };

    /// <summary>Stored timestamps are UTC ISO-8601; show them in local time.</summary>
    private static string? FormatTimestamp(string? utc)
    {
        if (string.IsNullOrWhiteSpace(utc))
            return null;
        return DateTimeOffset.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            : utc;
    }
}
