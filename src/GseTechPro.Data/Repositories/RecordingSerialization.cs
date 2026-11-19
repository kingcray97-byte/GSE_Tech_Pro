using System.Text.Json;
using GseTechPro.Core.Contracts;

namespace GseTechPro.Data.Repositories;

/// <summary>
/// JSON stored in recording columns uses the app's existing wire conventions: camelCase property
/// names and case-insensitive reads. Keep serializer options shared so metadata and frames round-trip
/// identically, including every <see cref="PidValue.IsSimulated"/> flag.
/// </summary>
internal static class RecordingSerialization
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string SerializePidNames(IReadOnlyList<string> pidNames)
    {
        ArgumentNullException.ThrowIfNull(pidNames);
        return JsonSerializer.Serialize(pidNames, Options);
    }

    public static IReadOnlyList<string> DeserializePidNames(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? Array.Empty<string>()
            : JsonSerializer.Deserialize<List<string>>(json, Options) ?? [];

    public static string? SerializeTrigger(TriggerConfig? trigger) =>
        trigger is null ? null : JsonSerializer.Serialize(trigger, Options);

    public static TriggerConfig? DeserializeTrigger(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<TriggerConfig>(json, Options);

    public static string SerializePids(IReadOnlyList<PidValue> pids)
    {
        ArgumentNullException.ThrowIfNull(pids);
        return JsonSerializer.Serialize(pids, Options);
    }

    public static IReadOnlyList<PidValue> DeserializePids(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? Array.Empty<PidValue>()
            : JsonSerializer.Deserialize<List<PidValue>>(json, Options) ?? [];
}
