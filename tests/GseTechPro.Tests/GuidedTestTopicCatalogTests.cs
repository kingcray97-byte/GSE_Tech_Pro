using System.Text;
using System.Text.Json;
using GseTechPro.Data;
using Xunit;

namespace GseTechPro.Tests;

/// <summary>Phase 10D.2: guided test library topics (our own grouping of the shipped guided tests).</summary>
public sealed class GuidedTestTopicCatalogTests
{
    private static Stream Json(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "data", "troubleshooting")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    [Fact]
    public async Task ShippedTopics_CoverEveryShippedTest_AndListOnlyShippedTests()
    {
        var topics = await GuidedTestTopicCatalog.LoadAsync();
        Assert.Equal(
            ["Charging & starting", "Sensors", "Actuators", "Hydraulics & pressure", "CAN / J1939 communications", "GSE / Heavy-duty"],
            topics.Select(t => t.Name));

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(RepoFile("data", "troubleshooting", "guided_tests.json")));
        var shippedIds = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToArray();

        Assert.Empty(GuidedTestTopicCatalog.FindUnknownTestIds(topics, shippedIds));
        var listed = topics.SelectMany(t => t.Subtopics).SelectMany(s => s.TestIds).ToHashSet();
        Assert.All(shippedIds, id => Assert.Contains(id, listed));

        // Empty subtopics say why, instead of pretending a test exists.
        Assert.All(topics.SelectMany(t => t.Subtopics).Where(s => s.TestIds.Count == 0),
            s => Assert.False(string.IsNullOrWhiteSpace(s.Note)));
    }

    [Fact]
    public async Task Parse_RejectsDuplicateIds_MissingNames_AndSilentEmptySubtopics()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => GuidedTestTopicCatalog.ParseAsync(Json("[]")));
        await Assert.ThrowsAsync<InvalidDataException>(() => GuidedTestTopicCatalog.ParseAsync(Json(
            """[{"id":"t","name":"T","subtopics":[{"id":"t","name":"S","test_ids":["gt-a"]}]}]""")));
        await Assert.ThrowsAsync<InvalidDataException>(() => GuidedTestTopicCatalog.ParseAsync(Json(
            """[{"id":"t","subtopics":[{"id":"s","name":"S","test_ids":["gt-a"]}]}]""")));
        await Assert.ThrowsAsync<InvalidDataException>(() => GuidedTestTopicCatalog.ParseAsync(Json(
            """[{"id":"t","name":"T","subtopics":[{"id":"s","name":"S","test_ids":[]}]}]""")));
        await Assert.ThrowsAsync<InvalidDataException>(() => GuidedTestTopicCatalog.ParseAsync(Json(
            """[{"id":"t","name":"T","subtopics":[]}]""")));
    }

    [Fact]
    public async Task Parse_TrimsAndDeduplicatesTestIds_AndFindsUnknownOnes()
    {
        var topics = await GuidedTestTopicCatalog.ParseAsync(Json(
            """[{"id":"t","name":" T ","subtopics":[{"id":"s","name":"S","test_ids":["gt-a"," gt-a ","gt-b"],"note":"  "}]}]"""));
        var sub = Assert.Single(Assert.Single(topics).Subtopics);
        Assert.Equal("T", topics[0].Name);
        Assert.Equal(["gt-a", "gt-b"], sub.TestIds);
        Assert.Null(sub.Note);
        Assert.Equal(["gt-b"], GuidedTestTopicCatalog.FindUnknownTestIds(topics, ["gt-a"]));
    }
}
