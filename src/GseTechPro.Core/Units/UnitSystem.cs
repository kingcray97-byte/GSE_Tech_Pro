using System.Runtime.Serialization;

namespace GseTechPro.Core.Units;

/// <summary>
/// Unit system for display. Canonical storage is always SI regardless of this; this enum only
/// controls how values are presented to the tech. DB values: 'imperial' | 'metric'.
/// </summary>
public enum UnitSystem
{
    [EnumMember(Value = "imperial")] Imperial,
    [EnumMember(Value = "metric")] Metric,
}
