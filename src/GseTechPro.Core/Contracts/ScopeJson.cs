using System.Text.Json;
using System.Text.Json.Serialization;

namespace GseTechPro.Core.Contracts;

/// <summary>
/// Canonical JSON (de)serialization for scope payloads. This is the single source of truth for the
/// snake_case wire shapes stored in <c>waveforms.channel_config_json</c>/<c>measurements_json</c>,
/// <c>reference_waveforms.expected_json</c>, and <c>guided_tests.scope_setup_json</c> (ADR-011).
/// <c>database</c>, <c>scope</c>, and <c>gse-domain-expert</c> must use these helpers (or these exact
/// options) rather than re-deriving the shape, so the JSON stays identical across owners.
///
/// Enum values are lowercase snake_case (<c>"peak_to_peak"</c>, <c>"duty_cycle"</c>, <c>"na"</c>),
/// matching the <c>[EnumMember]</c> strings in <see cref="GseTechPro.Core.Enums"/>.
/// </summary>
public static class ScopeJson
{
    /// <summary>Shared options: snake_case property names + snake_case string enums, case-insensitive reads.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        WriteIndented = false,
    };

    /// <summary>Serialize <c>reference_waveforms.expected_json</c>.</summary>
    public static string SerializeReferenceExpectations(ReferenceExpectations value) =>
        JsonSerializer.Serialize(value, Options);

    /// <summary>Parse <c>reference_waveforms.expected_json</c>; null/empty → null.</summary>
    public static ReferenceExpectations? DeserializeReferenceExpectations(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ReferenceExpectations>(json, Options);

    /// <summary>Serialize <c>guided_tests.scope_setup_json</c> (a capture request).</summary>
    public static string SerializeCaptureRequest(ScopeCaptureRequest value) =>
        JsonSerializer.Serialize(value, Options);

    /// <summary>Parse <c>guided_tests.scope_setup_json</c>; null/empty → null.</summary>
    public static ScopeCaptureRequest? DeserializeCaptureRequest(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ScopeCaptureRequest>(json, Options);

    /// <summary>Serialize <c>waveforms.channel_config_json</c> (an array of channel configs).</summary>
    public static string SerializeChannelConfigs(IEnumerable<ScopeChannelConfig> values) =>
        JsonSerializer.Serialize(values, Options);

    /// <summary>Parse <c>waveforms.channel_config_json</c>.</summary>
    public static IReadOnlyList<ScopeChannelConfig> DeserializeChannelConfigs(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? Array.Empty<ScopeChannelConfig>()
            : JsonSerializer.Deserialize<IReadOnlyList<ScopeChannelConfig>>(json, Options) ?? Array.Empty<ScopeChannelConfig>();

    /// <summary>Serialize <c>waveforms.measurements_json</c> (measurements + comparison verdicts).</summary>
    public static string SerializeMeasurements(IEnumerable<ScopeMeasurement> values) =>
        JsonSerializer.Serialize(values, Options);

    /// <summary>Parse <c>waveforms.measurements_json</c>.</summary>
    public static IReadOnlyList<ScopeMeasurement> DeserializeMeasurements(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? Array.Empty<ScopeMeasurement>()
            : JsonSerializer.Deserialize<IReadOnlyList<ScopeMeasurement>>(json, Options) ?? Array.Empty<ScopeMeasurement>();
}
