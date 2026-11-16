using GseTechPro.Core.Units;
using Xunit;

namespace GseTechPro.Tests;

public class UnitConverterTests
{
    [Fact]
    public void ToCanonical_Pressure_Psi()
    {
        // 1 psi = 6894.757... Pa
        var canonical = UnitConverter.ToCanonical(1.0, UnitConverter.Psi);
        Assert.Equal(6894.75729316836, canonical, precision: 6);

        // Round-trip back to psi
        var backToPsi = UnitConverter.FromCanonical(canonical, UnitConverter.Pascal, UnitConverter.Psi);
        Assert.Equal(1.0, backToPsi, precision: 10);
    }

    [Fact]
    public void ToCanonical_Pressure_Bar()
    {
        // 1 bar = 100000 Pa
        var canonical = UnitConverter.ToCanonical(1.0, UnitConverter.Bar);
        Assert.Equal(100000.0, canonical, precision: 6);

        // Round-trip back to bar
        var backToBar = UnitConverter.FromCanonical(canonical, UnitConverter.Pascal, UnitConverter.Bar);
        Assert.Equal(1.0, backToBar, precision: 10);
    }

    [Fact]
    public void ToCanonical_Pressure_30Psi()
    {
        // 30 psi in Pa (exact: 30 * 6894.75729316836)
        var canonical = UnitConverter.ToCanonical(30.0, UnitConverter.Psi);
        Assert.Equal(206842.7187950508, canonical, precision: 6);

        // Round-trip back to psi
        var backToPsi = UnitConverter.FromCanonical(canonical, UnitConverter.Pascal, UnitConverter.Psi);
        Assert.Equal(30.0, backToPsi, precision: 10);
    }

    [Theory]
    [InlineData(25.0, 77.0)]                 // 25°C = 77°F
    [InlineData(100.0, 212.0)]               // 100°C = 212°F (boiling)
    [InlineData(-40.0, -40.0)]               // -40 is the same in both
    public void ToCanonical_Temperature(double celsius, double fahrenheit)
    {
        var canonical = UnitConverter.ToCanonical(fahrenheit, UnitConverter.Fahrenheit);
        Assert.Equal(celsius, canonical, precision: 10);

        // Round-trip back to °F
        var backToF = UnitConverter.FromCanonical(canonical, UnitConverter.Celsius, UnitConverter.Fahrenheit);
        Assert.Equal(fahrenheit, backToF, precision: 10);
    }

    [Fact]
    public void ToCanonical_Distance_Mile()
    {
        // 1 mile = 1609.344 m
        var canonical = UnitConverter.ToCanonical(1.0, UnitConverter.Mile);
        Assert.Equal(1609.344, canonical, precision: 6);

        // Round-trip back to miles
        var backToMiles = UnitConverter.FromCanonical(canonical, UnitConverter.Meter, UnitConverter.Mile);
        Assert.Equal(1.0, backToMiles, precision: 10);
    }

    [Fact]
    public void ToCanonical_Distance_Kilometer()
    {
        // 1 km = 1000 m
        var canonical = UnitConverter.ToCanonical(1.0, UnitConverter.Kilometer);
        Assert.Equal(1000.0, canonical, precision: 6);

        // Round-trip back to km
        var backToKm = UnitConverter.FromCanonical(canonical, UnitConverter.Meter, UnitConverter.Kilometer);
        Assert.Equal(1.0, backToKm, precision: 10);
    }

    [Fact]
    public void ToCanonical_Distance_Foot()
    {
        // 1 foot = 0.3048 m
        var canonical = UnitConverter.ToCanonical(1.0, UnitConverter.Foot);
        Assert.Equal(0.3048, canonical, precision: 6);

        // Round-trip back to feet
        var backToFeet = UnitConverter.FromCanonical(canonical, UnitConverter.Meter, UnitConverter.Foot);
        Assert.Equal(1.0, backToFeet, precision: 10);
    }

    [Fact]
    public void ToDisplay_Pressure_Imperial_GivesPsi()
    {
        // Use exact canonical value for 30 psi to avoid rounding issues
        var canonicalFor30Psi = 30.0 * 6894.75729316836;
        var (value, unit) = UnitConverter.ToDisplay(canonicalFor30Psi, UnitConverter.Pascal, UnitSystem.Imperial);
        Assert.Equal(UnitConverter.Psi, unit);
        Assert.Equal(30.0, value, precision: 10);
    }

    [Fact]
    public void ToDisplay_Pressure_Metric_GivesBar()
    {
        var (value, unit) = UnitConverter.ToDisplay(206842.713, UnitConverter.Pascal, UnitSystem.Metric);
        Assert.Equal(UnitConverter.Bar, unit);
        Assert.Equal(2.06842713, value, precision: 6);
    }

    [Fact]
    public void ToDisplay_Temperature_Imperial_GivesFahrenheit()
    {
        var (value, unit) = UnitConverter.ToDisplay(100.0, UnitConverter.Celsius, UnitSystem.Imperial);
        Assert.Equal(UnitConverter.Fahrenheit, unit);
        Assert.Equal(212.0, value, precision: 6);
    }

    [Fact]
    public void ToDisplay_Temperature_Metric_GivesCelsius()
    {
        var (value, unit) = UnitConverter.ToDisplay(100.0, UnitConverter.Celsius, UnitSystem.Metric);
        Assert.Equal(UnitConverter.Celsius, unit);
        Assert.Equal(100.0, value, precision: 6);
    }

    [Fact]
    public void ToDisplay_Distance_Imperial_GivesMiles()
    {
        var (value, unit) = UnitConverter.ToDisplay(160934.4, UnitConverter.Meter, UnitSystem.Imperial);
        Assert.Equal(UnitConverter.Mile, unit);
        Assert.Equal(100.0, value, precision: 6);
    }

    [Fact]
    public void ToDisplay_Distance_Metric_GivesKilometers()
    {
        var (value, unit) = UnitConverter.ToDisplay(160934.4, UnitConverter.Meter, UnitSystem.Metric);
        Assert.Equal(UnitConverter.Kilometer, unit);
        Assert.Equal(160.9344, value, precision: 6);
    }

    [Fact]
    public void CanonicalUnit_ReturnsCorrectCanonical()
    {
        Assert.Equal(UnitConverter.Pascal, UnitConverter.CanonicalUnit(UnitConverter.Psi));
        Assert.Equal(UnitConverter.Celsius, UnitConverter.CanonicalUnit(UnitConverter.Fahrenheit));
        Assert.Equal(UnitConverter.Meter, UnitConverter.CanonicalUnit(UnitConverter.Mile));
        Assert.Equal(UnitConverter.Volt, UnitConverter.CanonicalUnit(UnitConverter.Volt));
    }

    [Fact]
    public void IsKnownUnit_RecognizesAllUnits()
    {
        Assert.True(UnitConverter.IsKnownUnit(UnitConverter.Pascal));
        Assert.True(UnitConverter.IsKnownUnit(UnitConverter.Psi));
        Assert.True(UnitConverter.IsKnownUnit(UnitConverter.Bar));
        Assert.True(UnitConverter.IsKnownUnit(UnitConverter.Celsius));
        Assert.True(UnitConverter.IsKnownUnit(UnitConverter.Fahrenheit));
        Assert.True(UnitConverter.IsKnownUnit(UnitConverter.Meter));
        Assert.True(UnitConverter.IsKnownUnit(UnitConverter.Kilometer));
        Assert.True(UnitConverter.IsKnownUnit(UnitConverter.Mile));
        Assert.True(UnitConverter.IsKnownUnit(UnitConverter.Volt));
        Assert.False(UnitConverter.IsKnownUnit("bogus"));
        Assert.False(UnitConverter.IsKnownUnit(null));
    }

    [Fact]
    public void UnknownUnit_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UnitConverter.ToCanonical(1.0, "unknown"));
    }
}
