using GseTechPro.App.ViewModels;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Data;
using GseTechPro.Devices;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>
/// Found in the 10C.4 in-app check: inspection steps (no reading) showed "TODO: needs spec from manual",
/// which wrongly implied a missing spec. Reading steps keep the TODO marker.
/// </summary>
public sealed class GuidedInspectionStepTests
{
    private static MainShellViewModel CreateViewModel()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-guided-{Guid.NewGuid():N}", "gse.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        return new MainShellViewModel(new DataServices(new DatabaseOptions { DatabasePath = dbPath }), new DiagnosticAdapterFactory());
    }

    [Fact]
    public void InspectionStep_SaysNoMeasurement()
    {
        var vm = CreateViewModel();
        vm.CurrentGuidedStep = new GuidedStep
        {
            SessionId = "s", Available = true, TreeId = "t", StepId = "inspect", StepOrder = 1,
            Instruction = "Check the nameplate.", Question = "Is it a PCS transmission?",
        };
        Assert.Equal("No measurement for this step: answer PASS / FAIL.", vm.GuidedSpecSummaryText);

        vm.CurrentGuidedStep = vm.CurrentGuidedStep with { Question = null, TerminalResult = "Repair the feed." };
        Assert.Equal("No measurement for this step.", vm.GuidedSpecSummaryText);
    }

    [Fact]
    public void ReadingStepWithoutSpec_StillShowsTodo()
    {
        var vm = CreateViewModel();
        vm.CurrentGuidedStep = new GuidedStep
        {
            SessionId = "s", Available = true, TreeId = "t", StepId = "feed", StepOrder = 5,
            Instruction = "Measure the TCM feed.", Question = "Battery voltage present?",
            ReadingType = ReadingType.Voltage, TestPointId = "tp-tug66028-tcm-ignition-feed",
            SpecSource = "TODO: needs spec from manual",
        };
        Assert.Equal("TODO: needs spec from manual", vm.GuidedSpecSummaryText);
    }
}
