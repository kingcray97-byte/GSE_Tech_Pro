namespace GseTechPro.Core.Units;

/// <summary>
/// A reading in canonical form plus the tech's original entry. Mirrors the canonical-unit rule:
/// <see cref="ValueCanonical"/>/<see cref="UnitCanonical"/> are SI and the source of truth;
/// <see cref="EnteredValue"/>/<see cref="EnteredUnit"/> preserve exactly what the tech typed or said.
/// Display formatting is a pure function of <see cref="UnitSystem"/>.
/// </summary>
public sealed record ReadingValue
{
    /// <summary>Canonical (SI) value.</summary>
    public double ValueCanonical { get; init; }

    /// <summary>The SI unit <see cref="ValueCanonical"/> is expressed in (Pa, °C, m, L/min, V, A, h).</summary>
    public string UnitCanonical { get; init; } = UnitConverter.Pascal;

    /// <summary>Exactly what the tech entered, if any (preserved verbatim).</summary>
    public double? EnteredValue { get; init; }

    /// <summary>The unit the tech entered in, if any.</summary>
    public string? EnteredUnit { get; init; }

    /// <summary>Convert the canonical value to the display unit for the given system.</summary>
    public (double Value, string Unit) ToDisplay(UnitSystem system) =>
        UnitConverter.ToDisplay(ValueCanonical, UnitCanonical, system);

    /// <summary>Format as a human-readable string, e.g. "2 068.43 kPa" or "300 psi".</summary>
    public string Format(UnitSystem system)
    {
        var (value, unit) = ToDisplay(system);
        return $"{value:G12} {unit}";
    }

    /// <summary>
    /// Build a canonical reading from an entered value+unit. Converts to SI and keeps the entry.
    /// Round-trip: <c>ReadingValue.FromEntered(80, "psi").ToDisplay(UnitSystem.Imperial)</c> == (80, "psi").
    /// </summary>
    public static ReadingValue FromEntered(double enteredValue, string enteredUnit)
    {
        var canonical = UnitConverter.ToCanonical(enteredValue, enteredUnit);
        var canonicalUnit = UnitConverter.CanonicalUnit(enteredUnit);
        return new ReadingValue
        {
            ValueCanonical = canonical,
            UnitCanonical = canonicalUnit,
            EnteredValue = enteredValue,
            EnteredUnit = enteredUnit,
        };
    }
}
