using GseTechPro.App.ViewModels;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using GseTechPro.Core.Units;
using GseTechPro.Data;
using GseTechPro.Devices;
using Microsoft.UI.Xaml;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 10B.2: the work order document (RO Viewer section tiles + Integrated Diagnostics record grid).</summary>
public sealed class WorkOrderDocumentTests
{
    private static async Task<(MainShellViewModel Vm, DataServices Data)> CreateViewModelAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-wodoc-{Guid.NewGuid():N}.db");
        var dataServices = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await dataServices.InitializeAsync();
        return (new MainShellViewModel(dataServices, new DiagnosticAdapterFactory()), dataServices);
    }

    [Fact]
    public async Task NoWorkOrder_RoViewerShowsPlaceholder_AndNoRecords()
    {
        var (vm, _) = await CreateViewModelAsync();
        await using var _vm = vm;

        vm.RebuildWorkOrderDocument();

        Assert.Equal(
            new[] { "Repair Order", "Equipment", "Symptoms", "Diagnosis & Repair", "Station", "Outcome", "Technician", "Shop Info" },
            vm.RoSections.Select(s => s.Title));
        Assert.Equal("No work order open", vm.RoSections[0].Rows[0].Value);
        // Empty values show a dash, never an invented value
        Assert.All(vm.RoSections[1].Rows, r => Assert.Equal("—", r.Value));
        Assert.Empty(vm.DiagnosticRecords);
        Assert.Equal(Visibility.Visible, vm.NoDiagnosticRecordsVisibility);
        Assert.Equal("Open a work order to see its diagnostic records.", vm.DiagnosticRecordPreviewText);
    }

    [Fact]
    public async Task Tabs_SwitchBetweenRoViewerAndIntegratedDiagnostics()
    {
        var (vm, _) = await CreateViewModelAsync();
        await using var _vm = vm;

        Assert.Equal(Visibility.Visible, vm.RoViewerVisibility);
        Assert.Equal(Visibility.Collapsed, vm.IntegratedDiagnosticsVisibility);

        vm.WorkOrderDocumentTabIndex = 1;

        Assert.Equal(Visibility.Collapsed, vm.RoViewerVisibility);
        Assert.Equal(Visibility.Visible, vm.IntegratedDiagnosticsVisibility);
    }

    [Fact]
    public async Task ActiveWorkOrder_FillsRepairOrderSection_AndListsSavedReadings()
    {
        var (vm, data) = await CreateViewModelAsync();
        await using var _vm = vm;

        await vm.CreateActiveWorkOrderAsync();
        var wo = vm.ActiveWorkOrder!;

        var repairOrder = vm.RoSections.Single(s => s.Title == "Repair Order");
        Assert.Equal(WorkOrderBrowserRowViewModel.FormatWorkOrderNumber(wo), repairOrder.Rows.Single(r => r.Label == "Work Order Number").Value);
        Assert.NotEqual("—", repairOrder.Rows.Single(r => r.Label == "Status").Value);

        var reading = new Reading
        {
            WorkOrderId = wo.Id,
            ReadingType = ReadingType.Voltage,
            ChannelComponent = "Battery",
            ValueCanonical = 13.8,
            UnitCanonical = UnitConverter.Volt,
            EnteredValue = 13.8,
            EnteredUnit = UnitConverter.Volt,
            SpecSource = "TODO: needs spec from manual",
            PassFail = PassFail.NotApplicable,
            Source = ReadingSource.Scan,
            IsSimulated = true,
            Notes = "bench check",
        };
        await data.Readings.AddAsync(reading);
        await vm.ReloadWorkOrderReadingsAsync();

        var row = Assert.Single(vm.DiagnosticRecords);
        Assert.Equal("Reading", row.RecordType);
        Assert.True(row.IsSimulated);
        Assert.Equal(Visibility.Visible, row.SimulatedBadgeVisibility);
        Assert.Equal(Visibility.Collapsed, vm.NoDiagnosticRecordsVisibility);

        // The first record is selected, so the preview and note show it
        Assert.Same(row, vm.SelectedDiagnosticRecord);
        Assert.Equal(row.Preview, vm.DiagnosticRecordPreviewText);
        Assert.Equal("bench check", vm.DiagnosticRecordNoteText);
    }
}
