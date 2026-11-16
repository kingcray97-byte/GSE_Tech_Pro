namespace GseTechPro.Core.Models;

/// <summary>
/// Base for tables whose primary key is a TEXT UUIDv4 (<c>id</c>). Consumers get a stable,
/// unique <see cref="Id"/> for free so rows can be created without remembering to generate a GUID.
/// Tables with composite keys, integer rowids, or a foreign-key primary key do not inherit this.
/// </summary>
public abstract record EntityBase
{
    /// <summary>UUIDv4 in "D" format (lowercase, hyphenated).</summary>
    public string Id { get; init; } = System.Guid.NewGuid().ToString("D");
}

/// <summary>ISO-8601 UTC timestamps as required by the schema (<c>TEXT ISO-8601 UTC</c>).</summary>
public static class Clock
{
    /// <summary>
    /// Current UTC time in round-trip ISO-8601 form, e.g. <c>2026-11-15T15:43:00.0000000Z</c>.
    /// The format is pinned to the <c>Z</c> suffix (not <c>+00:00</c>) so that lexicographic string
    /// sorting of timestamp columns (<c>opened_at</c>, <c>observed_at</c>, …) is always chronological.
    /// </summary>
    /// <remarks>// TODO: pin this format with a unit test so a future edit can't regress the suffix.</remarks>
    public static string UtcNow => DateTime.UtcNow.ToString("O");
}
