using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Units;

/// <summary>
/// Deterministic, lossless conversion between canonical SI units and display units.
/// Canonical units (ARCHITECTURE.md §2.2 "canonical-unit rule"):
/// pressure → Pa, temperature → °C, distance → m, flow → L/min, voltage → V, current → A, duration → h.
///
/// Display units: pressure → psi (imperial) / bar (metric), temperature → °F / °C, distance → mi / km.
/// Flow, voltage, current, and hours are the same in both systems (L/min, V, A, h).
///
/// Round-trip guarantees (pure math, fixed constants):
///   UnitConverter.ToCanonical(UnitConverter.FromCanonical(v, Pascal, Psi), Psi) == v
///   UnitConverter.FromCanonical(UnitConverter.ToCanonical(v, Psi), Pascal, Psi) == v
///   UnitConverter.ToDisplay(UnitConverter.ToCanonical(80, Psi), Pascal, UnitSystem.Imperial) == (80, "psi")
///   UnitConverter.ToDisplay(UnitConverter.ToCanonical(100, Celsius), Celsius, UnitSystem.Imperial) == (212, "°F")
/// (within double precision; no test project is required for these — the xUnit suite will assert them.)
/// </summary>
public static class UnitConverter
{
    // --- Canonical unit names (source of truth for storage) ---
    public const string Pascal = "Pa";
    public const string Celsius = "°C";
    public const string Fahrenheit = "°F";
    public const string Meter = "m";
    public const string Kilometer = "km";
    public const string Mile = "mi";
    public const string Foot = "ft";
    public const string LiterPerMinute = "L/min";
    public const string Volt = "V";
    public const string Ampere = "A";
    public const string Hour = "h";

    // --- Additional display units ---
    public const string Psi = "psi";
    public const string Bar = "bar";
    public const string KiloPascal = "kPa";

    // --- Exact linear factors (display per canonical) ---
    private const double PsiPerPa = 1.0 / 6894.75729316836;
    private const double BarPerPa = 1.0 / 100000.0;
    private const double KiloPascalPerPa = 1.0 / 1000.0;
    private const double KilometerPerMeter = 1.0 / 1000.0;
    private const double MilePerMeter = 1.0 / 1609.344;
    private const double FootPerMeter = 1.0 / 0.3048;
    private const double FahrenheitPerCelsius = 9.0 / 5.0;
    private const double FahrenheitOffset = 32.0;

    /// <summary>A display unit definition relative to its canonical unit: display = canonical * Scale + Offset.</summary>
    private sealed record UnitDef(string Canonical, double Scale, double Offset);

    private static readonly IReadOnlyDictionary<string, UnitDef> Defs =
        new Dictionary<string, UnitDef>(StringComparer.Ordinal)
        {
            // Pressure (canonical Pa)
            [Pascal] = new(Pascal, 1.0, 0.0),
            [Psi] = new(Pascal, PsiPerPa, 0.0),
            [Bar] = new(Pascal, BarPerPa, 0.0),
            [KiloPascal] = new(Pascal, KiloPascalPerPa, 0.0),
            // Temperature (canonical °C)
            [Celsius] = new(Celsius, 1.0, 0.0),
            [Fahrenheit] = new(Celsius, FahrenheitPerCelsius, FahrenheitOffset),
            // Distance (canonical m)
            [Meter] = new(Meter, 1.0, 0.0),
            [Kilometer] = new(Meter, KilometerPerMeter, 0.0),
            [Mile] = new(Meter, MilePerMeter, 0.0),
            [Foot] = new(Meter, FootPerMeter, 0.0),
            // Flow, voltage, current, duration (canonical == display)
            [LiterPerMinute] = new(LiterPerMinute, 1.0, 0.0),
            [Volt] = new(Volt, 1.0, 0.0),
            [Ampere] = new(Ampere, 1.0, 0.0),
            [Hour] = new(Hour, 1.0, 0.0),
        };

    private static UnitDef GetDef(string unit)
    {
        if (unit is null)
            throw new ArgumentNullException(nameof(unit));
        if (!Defs.TryGetValue(unit, out var def))
            throw new ArgumentOutOfRangeException(nameof(unit), unit, $"Unknown unit.");
        return def;
    }

    /// <summary>True when the unit string is recognized.</summary>
    public static bool IsKnownUnit(string? unit) => unit is not null && Defs.ContainsKey(unit);

    /// <summary>The canonical (SI) unit for a given unit string (e.g. "psi" → "Pa", "°F" → "°C").</summary>
    public static string CanonicalUnit(string unit) => GetDef(unit).Canonical;

    /// <summary>Convert a value in <paramref name="fromUnit"/> to its canonical SI value.</summary>
    public static double ToCanonical(double value, string fromUnit)
    {
        var def = GetDef(fromUnit);
        return (value - def.Offset) / def.Scale;
    }

    /// <summary>Convert a canonical value to a specific display unit.</summary>
    public static double FromCanonical(double canonicalValue, string canonicalUnit, string toUnit)
    {
        var canonical = GetDef(canonicalUnit).Canonical;
        var to = GetDef(toUnit);
        if (!string.Equals(canonical, to.Canonical, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Cannot convert {canonicalUnit} ({canonical}) to {toUnit} ({to.Canonical}): different dimensions.");
        return canonicalValue * to.Scale + to.Offset;
    }

    /// <summary>The default display unit for a canonical unit under the given unit system.</summary>
    public static string DisplayUnit(string canonicalUnit, UnitSystem system)
    {
        var canonical = GetDef(canonicalUnit).Canonical;
        return (canonical, system) switch
        {
            (Pascal, UnitSystem.Imperial) => Psi,
            (Pascal, UnitSystem.Metric) => Bar,
            (Celsius, UnitSystem.Imperial) => Fahrenheit,
            (Celsius, UnitSystem.Metric) => Celsius,
            (Meter, UnitSystem.Imperial) => Mile,
            (Meter, UnitSystem.Metric) => Kilometer,
            _ => canonical, // flow, V, A, h (and anything unmapped) stay canonical
        };
    }

    /// <summary>Convert a canonical value to its default display unit for the system, returning (value, unit).</summary>
    public static (double Value, string Unit) ToDisplay(double canonicalValue, string canonicalUnit, UnitSystem system)
    {
        var unit = DisplayUnit(canonicalUnit, system);
        return (FromCanonical(canonicalValue, canonicalUnit, unit), unit);
    }
}
