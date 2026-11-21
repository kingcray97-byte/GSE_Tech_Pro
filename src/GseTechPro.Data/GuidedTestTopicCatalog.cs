using System.Text.Json;
using System.Text.Json.Serialization;

namespace GseTechPro.Data;

/// <summary>A subtopic in the guided test library (e.g. "Crank &amp; cam position") and the tests it lists.</summary>
public sealed record GuidedTestSubtopic(string Id, string Name, IReadOnlyList<string> TestIds, string? Note);

/// <summary>A top-level topic in the guided test library (e.g. "Sensors").</summary>
public sealed record GuidedTestTopic(string Id, string Name, IReadOnlyList<GuidedTestSubtopic> Subtopics);

/// <summary>
/// Guided test library topics (Phase 10D.2): our own topic → subtopic → test grouping, read from
/// <c>data/troubleshooting/guided_test_topics.json</c> (owned by <c>gse-domain-expert</c>). A test may
/// appear under more than one subtopic; a subtopic may be empty with a note saying why. Display
/// grouping only: it never changes a test's setup, safety warnings or reference.
/// </summary>
public static class GuidedTestTopicCatalog
{
    public const string ResourceName = "GseTechPro.Data.Content.Troubleshooting.guided_test_topics.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    /// <summary>Reads and checks the shipped topic file (structure only; see <see cref="FindUnknownTestIds"/>).</summary>
    public static async Task<IReadOnlyList<GuidedTestTopic>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var stream = typeof(GuidedTestTopicCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' was not found.");
        return await ParseAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parses a topic file and checks its structure: ids unique, names present, test ids non-blank.</summary>
    public static async Task<IReadOnlyList<GuidedTestTopic>> ParseAsync(Stream json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        var rows = await JsonSerializer.DeserializeAsync<List<JsonTopic>>(json, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (rows is null || rows.Count == 0)
            throw new InvalidDataException("Guided test topics: the file has no topics.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var topics = new List<GuidedTestTopic>(rows.Count);
        foreach (var row in rows)
        {
            var topicId = RequireId(row.Id, ids, "topic");
            var topicName = Require(row.Name, $"topic '{topicId}' name");
            var subtopics = new List<GuidedTestSubtopic>();
            foreach (var sub in row.Subtopics ?? [])
            {
                var subId = RequireId(sub.Id, ids, $"subtopic in '{topicId}'");
                var subName = Require(sub.Name, $"subtopic '{subId}' name");
                var testIds = new List<string>();
                foreach (var testId in sub.TestIds ?? [])
                {
                    var clean = Require(testId, $"test id in '{subId}'");
                    if (!testIds.Contains(clean, StringComparer.Ordinal))
                        testIds.Add(clean);
                }

                var note = string.IsNullOrWhiteSpace(sub.Note) ? null : sub.Note.Trim();
                if (testIds.Count == 0 && note is null)
                    throw new InvalidDataException($"Guided test topics: subtopic '{subId}' has no tests and no note saying why.");
                subtopics.Add(new GuidedTestSubtopic(subId, subName, testIds, note));
            }

            if (subtopics.Count == 0)
                throw new InvalidDataException($"Guided test topics: topic '{topicId}' has no subtopics.");
            topics.Add(new GuidedTestTopic(topicId, topicName, subtopics));
        }

        return topics;
    }

    /// <summary>Test ids listed in the topics that are not in <paramref name="knownTestIds"/> (shown as missing, never invented).</summary>
    public static IReadOnlyList<string> FindUnknownTestIds(IEnumerable<GuidedTestTopic> topics, IEnumerable<string> knownTestIds)
    {
        var known = new HashSet<string>(knownTestIds, StringComparer.Ordinal);
        return topics
            .SelectMany(t => t.Subtopics)
            .SelectMany(s => s.TestIds)
            .Where(id => !known.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string RequireId(string? id, ISet<string> ids, string what)
    {
        var clean = Require(id, $"{what} id");
        if (!ids.Add(clean))
            throw new InvalidDataException($"Guided test topics: duplicate id '{clean}'.");
        return clean;
    }

    private static string Require(string? value, string what) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"Guided test topics: {what} is missing.")
            : value.Trim();

    private sealed class JsonTopic
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public List<JsonSubtopic>? Subtopics { get; set; }
    }

    private sealed class JsonSubtopic
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        [JsonPropertyName("test_ids")]
        public List<string>? TestIds { get; set; }
        public string? Note { get; set; }
    }
}
