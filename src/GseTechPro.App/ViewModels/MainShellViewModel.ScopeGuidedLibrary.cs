using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using GseTechPro.Data;
using Microsoft.UI.Xaml;

namespace GseTechPro.App.ViewModels;

/// <summary>A topic row in the guided test library ("Sensors · 1 test").</summary>
public sealed class GuidedTopicItem
{
    public GuidedTopicItem(GuidedTestTopic topic, int testCount)
    {
        Topic = topic;
        TestCount = testCount;
    }

    public GuidedTestTopic Topic { get; }
    public int TestCount { get; }
    public string Name => Topic.Name;
    public string DisplayLabel => $"{Topic.Name} · {Plural(TestCount)}";

    public override string ToString() => DisplayLabel;

    internal static string Plural(int n) => n == 1 ? "1 test" : $"{n} tests";
}

/// <summary>A subtopic row in the guided test library ("Crank &amp; cam position · 1 test").</summary>
public sealed class GuidedSubtopicItem
{
    public GuidedSubtopicItem(GuidedTestSubtopic subtopic, IReadOnlyList<SelectableGuidedTestItem> tests)
    {
        Subtopic = subtopic;
        Tests = tests;
    }

    public GuidedTestSubtopic Subtopic { get; }
    public IReadOnlyList<SelectableGuidedTestItem> Tests { get; }
    public string Name => Subtopic.Name;
    public string DisplayLabel => $"{Subtopic.Name} · {GuidedTopicItem.Plural(Tests.Count)}";

    public override string ToString() => DisplayLabel;
}

/// <summary>
/// Guided test library browser (Phase 10D.2): topic → subtopic → test, from our own grouping in
/// <c>guided_test_topics.json</c>, with a search across every test. Picking a test uses the existing
/// guided-test flow, so its safety warnings are shown first, then its scope setup is applied and
/// "Run guided test &amp; compare" captures and compares. Tests in the database but in no topic are
/// listed under "Other tests" so nothing is hidden; topic entries for tests that are not installed
/// are skipped (reported in the status line).
/// </summary>
public sealed partial class MainShellViewModel
{
    public const string OtherTestsTopicId = "topic-other";

    private IReadOnlyList<GuidedTestTopic>? _guidedTestTopics;

    public ObservableCollection<GuidedTopicItem> ScopeGuidedTopics { get; } = new();

    public ObservableCollection<GuidedSubtopicItem> ScopeGuidedSubtopics { get; } = new();

    /// <summary>Tests shown in the third column (the subtopic's tests, or search results).</summary>
    public ObservableCollection<SelectableGuidedTestItem> ScopeGuidedBrowserTests { get; } = new();

    [ObservableProperty]
    private GuidedTopicItem? _selectedScopeGuidedTopic;

    [ObservableProperty]
    private GuidedSubtopicItem? _selectedScopeGuidedSubtopic;

    [ObservableProperty]
    private SelectableGuidedTestItem? _selectedScopeGuidedBrowserTest;

    [ObservableProperty]
    private string _scopeGuidedSearchText = "";

    [ObservableProperty]
    private string _scopeGuidedLibraryStatusText = "Loading the guided test library…";

    /// <summary>Note for an empty subtopic (why there is no test yet).</summary>
    public string ScopeGuidedSubtopicNote => SelectedScopeGuidedSubtopic?.Subtopic.Note ?? "";

    public Visibility ScopeGuidedSubtopicNoteVisibility =>
        string.IsNullOrEmpty(ScopeGuidedSubtopicNote) || IsScopeGuidedSearching ? Visibility.Collapsed : Visibility.Visible;

    public bool IsScopeGuidedSearching => !string.IsNullOrWhiteSpace(ScopeGuidedSearchText);

    /// <summary>Header of the tests column.</summary>
    public string ScopeGuidedTestsHeader => IsScopeGuidedSearching
        ? $"Search results · {GuidedTopicItem.Plural(ScopeGuidedBrowserTests.Count)}"
        : SelectedScopeGuidedSubtopic is { } s ? s.Name : "Tests";

    /// <summary>Loads the topic file (once) and rebuilds the browser from the installed guided tests.</summary>
    public async Task RebuildScopeGuidedLibraryAsync()
    {
        if (_guidedTestTopics is null)
        {
            try
            {
                _guidedTestTopics = await GuidedTestTopicCatalog.LoadAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GSE Tech Pro] guided test topics notice: {ex.Message}");
                _guidedTestTopics = Array.Empty<GuidedTestTopic>();
            }
        }

        var byId = GuidedScopeTests.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var topics = new List<GuidedTestTopic>(_guidedTestTopics);

        var listed = new HashSet<string>(
            topics.SelectMany(t => t.Subtopics).SelectMany(s => s.TestIds), StringComparer.Ordinal);
        var unsorted = GuidedScopeTests.Where(t => !listed.Contains(t.Id)).Select(t => t.Id).ToArray();
        if (unsorted.Length > 0)
        {
            topics.Add(new GuidedTestTopic(OtherTestsTopicId, "Other tests",
                [new GuidedTestSubtopic("sub-other", "Not yet sorted into a topic", unsorted, null)]));
        }

        var previousTopicId = SelectedScopeGuidedTopic?.Topic.Id;
        var previousSubtopicId = SelectedScopeGuidedSubtopic?.Subtopic.Id;

        ScopeGuidedTopics.Clear();
        foreach (var topic in topics)
        {
            int count = topic.Subtopics.SelectMany(s => s.TestIds).Where(byId.ContainsKey)
                .Distinct(StringComparer.Ordinal).Count();
            ScopeGuidedTopics.Add(new GuidedTopicItem(topic, count));
        }

        var missing = GuidedTestTopicCatalog.FindUnknownTestIds(_guidedTestTopics, byId.Keys);
        ScopeGuidedLibraryStatusText =
            $"{ScopeGuidedTopics.Count} topics · {GuidedTopicItem.Plural(GuidedScopeTests.Count)} installed"
            + (missing.Count > 0 ? $" · not installed: {string.Join(", ", missing)}" : "")
            + ". Pick a test: its safety warnings are shown first.";

        SelectedScopeGuidedTopic = ScopeGuidedTopics.FirstOrDefault(t => t.Topic.Id == previousTopicId)
                                   ?? ScopeGuidedTopics.FirstOrDefault();
        if (previousSubtopicId is not null)
        {
            SelectedScopeGuidedSubtopic = ScopeGuidedSubtopics.FirstOrDefault(s => s.Subtopic.Id == previousSubtopicId)
                                          ?? SelectedScopeGuidedSubtopic;
        }
    }

    partial void OnSelectedScopeGuidedTopicChanged(GuidedTopicItem? value)
    {
        ScopeGuidedSubtopics.Clear();
        if (value is not null)
        {
            var byId = GuidedScopeTests.ToDictionary(t => t.Id, StringComparer.Ordinal);
            foreach (var sub in value.Topic.Subtopics)
            {
                var tests = sub.TestIds
                    .Select(id => byId.TryGetValue(id, out var t) ? t : null)
                    .OfType<SelectableGuidedTestItem>()
                    .ToArray();
                ScopeGuidedSubtopics.Add(new GuidedSubtopicItem(sub, tests));
            }
        }

        SelectedScopeGuidedSubtopic = ScopeGuidedSubtopics.FirstOrDefault();
    }

    partial void OnSelectedScopeGuidedSubtopicChanged(GuidedSubtopicItem? value)
    {
        OnPropertyChanged(nameof(ScopeGuidedSubtopicNote));
        OnPropertyChanged(nameof(ScopeGuidedSubtopicNoteVisibility));
        if (!IsScopeGuidedSearching)
            FillScopeGuidedBrowserTests(value?.Tests ?? Array.Empty<SelectableGuidedTestItem>());
    }

    partial void OnScopeGuidedSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsScopeGuidedSearching));
        OnPropertyChanged(nameof(ScopeGuidedSubtopicNoteVisibility));
        if (!IsScopeGuidedSearching)
        {
            FillScopeGuidedBrowserTests(SelectedScopeGuidedSubtopic?.Tests ?? Array.Empty<SelectableGuidedTestItem>());
            return;
        }

        var terms = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Matches(SelectableGuidedTestItem t)
        {
            var hay = $"{t.Name} {t.System} {t.Description}";
            return terms.All(term => hay.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        FillScopeGuidedBrowserTests(GuidedScopeTests.Where(Matches).ToArray());
    }

    /// <summary>Picking a test in the browser uses the normal guided-test flow (safety warnings first).</summary>
    partial void OnSelectedScopeGuidedBrowserTestChanged(SelectableGuidedTestItem? value)
    {
        if (value is not null && !ReferenceEquals(value, SelectedGuidedTestItem))
            SelectedGuidedTestItem = value;
    }

    private void FillScopeGuidedBrowserTests(IEnumerable<SelectableGuidedTestItem> tests)
    {
        ScopeGuidedBrowserTests.Clear();
        foreach (var t in tests)
            ScopeGuidedBrowserTests.Add(t);
        SelectedScopeGuidedBrowserTest = SelectedGuidedTestItem is { } current
            ? ScopeGuidedBrowserTests.FirstOrDefault(t => t.Id == current.Id)
            : null;
        OnPropertyChanged(nameof(ScopeGuidedTestsHeader));
    }
}
