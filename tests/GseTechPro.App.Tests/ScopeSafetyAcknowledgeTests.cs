using GseTechPro.App.ViewModels;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Scope;
using Microsoft.UI.Xaml;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Safety warnings are shown in full first; acknowledging shrinks them to a summary that stays on screen.</summary>
public sealed class ScopeSafetyAcknowledgeTests
{
    [Fact]
    public async Task Warnings_FullFirst_SummaryAfterAcknowledge_AndResetForANewTest()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gse-scope-safety-ack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var data = new DataServices(new DatabaseOptions { DatabasePath = Path.Combine(dir, "gse.db") });
        await data.InitializeAsync();
        await using var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(), dispatcherQueue: null,
            scopeDeviceFactory: new ScopeDeviceFactory());
        await vm.ReloadDatabaseRecordsAsync();
        vm.SelectToolbarAction("Scope");

        // No test: nothing to acknowledge.
        vm.AcknowledgeScopeSafetyWarnings();
        Assert.False(vm.AreScopeSafetyWarningsAcknowledged);
        Assert.Equal(Visibility.Collapsed, vm.ScopeSafetyWarningsFullVisibility);

        await vm.SelectGuidedScopeTestAsync("gt-crank-sensor");
        Assert.True(vm.HasScopeSafetyWarnings);
        int count = vm.ScopeSafetyWarnings.Count;
        Assert.Equal(Visibility.Visible, vm.ScopeSafetyWarningsFullVisibility);
        Assert.Equal(Visibility.Collapsed, vm.ScopeSafetyWarningsSummaryVisibility);

        vm.AcknowledgeScopeSafetyWarnings();
        Assert.True(vm.AreScopeSafetyWarningsAcknowledged);
        Assert.Equal(Visibility.Collapsed, vm.ScopeSafetyWarningsFullVisibility);
        // The summary stays on screen: the warnings are never simply hidden.
        Assert.Equal(Visibility.Visible, vm.ScopeSafetyWarningsSummaryVisibility);
        Assert.Equal(Visibility.Visible, vm.ScopeSafetyWarningsVisibility);
        Assert.StartsWith($"{count} safety warnings for 'Crank Sensor (CKP) Signal' read by ", vm.ScopeSafetyAcknowledgedText);
        // Acknowledging changes nothing in the warnings themselves.
        Assert.Equal(count, vm.ScopeSafetyWarnings.Count);

        vm.ShowScopeSafetyWarnings();
        Assert.Equal(Visibility.Visible, vm.ScopeSafetyWarningsFullVisibility);

        // A different test always shows its warnings in full again.
        vm.AcknowledgeScopeSafetyWarnings();
        await vm.SelectGuidedScopeTestAsync("gt-solenoid-current");
        Assert.False(vm.AreScopeSafetyWarningsAcknowledged);
        Assert.Equal("", vm.ScopeSafetyAcknowledgedText);
        Assert.Equal(Visibility.Visible, vm.ScopeSafetyWarningsFullVisibility);
    }

    [Fact]
    public async Task Capture_IsBlockedUntilWarningsAreRead_ManualSetupIsNot()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gse-scope-safety-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var data = new DataServices(new DatabaseOptions { DatabasePath = Path.Combine(dir, "gse.db") });
        await data.InitializeAsync();
        await using var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(), dispatcherQueue: null,
            scopeDeviceFactory: new ScopeDeviceFactory());
        await vm.ReloadDatabaseRecordsAsync();
        vm.SelectToolbarAction("Scope");

        // Manual setup (no guided test, no warnings list): Run works as before.
        await vm.RunScopeAsync();
        Assert.NotNull(vm.LastScopeCapture);
        var manual = vm.LastScopeCapture;

        // A guided test with warnings: every capture path is refused until they are read.
        await vm.SelectGuidedScopeTestAsync("gt-solenoid-current");
        Assert.True(vm.IsScopeCaptureBlockedBySafety());

        await vm.RunScopeAsync();
        Assert.Same(manual, vm.LastScopeCapture);
        Assert.Equal(MainShellViewModel.ScopeSafetyBlockedMessage, vm.ScopeStatusMessage);

        await vm.StartScopeCaptureAsync();
        await vm.RunGuidedScopeTestAsync();
        Assert.Same(manual, vm.LastScopeCapture);

        vm.IsScopeRepeatEnabled = true;
        await vm.RunScopeAsync();
        Assert.False(vm.IsScopeRepeatRunning);
        Assert.Same(manual, vm.LastScopeCapture);
        vm.IsScopeRepeatEnabled = false;

        await vm.RunScopeAutoSetupAsync();
        Assert.Same(manual, vm.LastScopeCapture);

        // After reading them, the guided run captures.
        vm.AcknowledgeScopeSafetyWarnings();
        Assert.False(vm.IsScopeCaptureBlockedBySafety());
        await vm.RunGuidedScopeTestAsync();
        Assert.NotSame(manual, vm.LastScopeCapture);

        // Opening the warnings again blocks again until they are confirmed again.
        vm.ShowScopeSafetyWarnings();
        Assert.True(vm.IsScopeCaptureBlockedBySafety());
    }
}
