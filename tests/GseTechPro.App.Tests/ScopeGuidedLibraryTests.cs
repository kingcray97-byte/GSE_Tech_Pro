using GseTechPro.App.ViewModels;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Scope;
using Microsoft.UI.Xaml;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 10D.2: guided test library browser on the scope screen.</summary>
public sealed class ScopeGuidedLibraryTests
{
    [Fact]
    public async Task Browser_TopicSubtopicTest_PicksTestWithSafetyFirst_AndSearchFindsAcrossTopics()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gse-scope-library-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var data = new DataServices(new DatabaseOptions { DatabasePath = Path.Combine(dir, "gse.db") });
        await data.InitializeAsync();
        await using var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(), dispatcherQueue: null,
            scopeDeviceFactory: new ScopeDeviceFactory());
        await vm.ReloadDatabaseRecordsAsync();
        vm.SelectToolbarAction("Scope");
        await vm.ReloadScopeCatalogAsync();

        // Our six topics; every installed test is in one, so there is no "Other tests" topic.
        Assert.Equal(6, vm.ScopeGuidedTopics.Count);
        Assert.DoesNotContain(vm.ScopeGuidedTopics, t => t.Topic.Id == MainShellViewModel.OtherTestsTopicId);
        Assert.Equal("Charging & starting · 2 tests", vm.ScopeGuidedTopics[0].DisplayLabel);
        Assert.Contains("6 tests installed", vm.ScopeGuidedLibraryStatusText);
        Assert.DoesNotContain("not installed", vm.ScopeGuidedLibraryStatusText);

        // Topic > subtopic > test.
        vm.SelectedScopeGuidedTopic = vm.ScopeGuidedTopics.Single(t => t.Name == "Sensors");
        Assert.Equal("Crank & cam position", Assert.Single(vm.ScopeGuidedSubtopics).Name);
        var crank = Assert.Single(vm.ScopeGuidedBrowserTests);
        Assert.Equal("gt-crank-sensor", crank.Id);

        vm.SelectedScopeGuidedBrowserTest = crank;
        Assert.Same(crank, vm.SelectedGuidedTestItem);
        for (int i = 0; i < 50 && vm.SelectedGuidedTest?.Id != "gt-crank-sensor"; i++)
            await Task.Delay(20);
        Assert.Equal("gt-crank-sensor", vm.SelectedGuidedTest?.Id);
        Assert.True(vm.HasScopeSafetyWarnings);

        // An empty subtopic says why instead of listing a made-up test.
        vm.SelectedScopeGuidedTopic = vm.ScopeGuidedTopics.Single(t => t.Name == "Hydraulics & pressure");
        vm.SelectedScopeGuidedSubtopic = vm.ScopeGuidedSubtopics.Single(s => s.Name == "Pressure transducers");
        Assert.Empty(vm.ScopeGuidedBrowserTests);
        Assert.Contains("No test yet", vm.ScopeGuidedSubtopicNote);
        Assert.Equal(Visibility.Visible, vm.ScopeGuidedSubtopicNoteVisibility);

        // The solenoid test is listed under both Actuators and Hydraulics.
        vm.SelectedScopeGuidedSubtopic = vm.ScopeGuidedSubtopics.Single(s => s.Name == "Hydraulic valve solenoids");
        Assert.Equal("gt-solenoid-current", Assert.Single(vm.ScopeGuidedBrowserTests).Id);

        // Search covers every test, whatever topic is open.
        vm.ScopeGuidedSearchText = "current";
        Assert.Equal(
            ["gt-injector-current", "gt-solenoid-current", "gt-starter-current"],
            vm.ScopeGuidedBrowserTests.Select(t => t.Id).OrderBy(x => x));
        Assert.StartsWith("Search results · 3 tests", vm.ScopeGuidedTestsHeader);
        Assert.Equal(Visibility.Collapsed, vm.ScopeGuidedSubtopicNoteVisibility);

        vm.ScopeGuidedSearchText = "";
        Assert.Equal("gt-solenoid-current", Assert.Single(vm.ScopeGuidedBrowserTests).Id);
    }
}
