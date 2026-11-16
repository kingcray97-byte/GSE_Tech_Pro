using GseTechPro.Core.Units;
using Xunit;

namespace GseTechPro.Tests;

public class ReadingValueTests
{
    [Fact]
    public void FromEntered_PreservesEntryAndConvertsToCanonical()
    {
        var reading = ReadingValue.FromEntered(30.0, UnitConverter.Psi);
        
        // Entry is preserved
        Assert.Equal(30.0, reading.EnteredValue);
        Assert.Equal(UnitConverter.Psi, reading.EnteredUnit);
        
        // Canonical value is in Pa (exact: 30 * 6894.75729316836)
        Assert.Equal(UnitConverter.Pascal, reading.UnitCanonical);
        Assert.Equal(206842.7187950508, reading.ValueCanonical, precision: 6);
    }

    [Fact]
    public void Format_Imperial_RoundTripsEntry()
    {
        var reading = ReadingValue.FromEntered(30.0, UnitConverter.Psi);
        var formatted = reading.Format(UnitSystem.Imperial);
        
        // Should show the original entry value in psi
        Assert.Contains("psi", formatted);
        Assert.StartsWith("30", formatted);
    }

    [Fact]
    public void Format_Metric_ConvertsCorrectly()
    {
        var reading = ReadingValue.FromEntered(30.0, UnitConverter.Psi);
        var formatted = reading.Format(UnitSystem.Metric);
        
        // Should show converted value in bar (precise: 2.06842718795)
        Assert.Contains("bar", formatted);
        Assert.StartsWith("2.06842718795", formatted);
    }

    [Fact]
    public void ToDisplay_Imperial_RoundTripsEntry()
    {
        var reading = ReadingValue.FromEntered(80.0, UnitConverter.Psi);
        var (value, unit) = reading.ToDisplay(UnitSystem.Imperial);
        
        Assert.Equal(UnitConverter.Psi, unit);
        Assert.Equal(80.0, value, precision: 10);
    }

    [Fact]
    public void FromEntered_Temperature_RoundTrips()
    {
        var reading = ReadingValue.FromEntered(212.0, UnitConverter.Fahrenheit);
        
        // Canonical is °C (100)
        Assert.Equal(UnitConverter.Celsius, reading.UnitCanonical);
        Assert.Equal(100.0, reading.ValueCanonical, precision: 10);
        
        // Round-trip back to °F
        var (value, unit) = reading.ToDisplay(UnitSystem.Imperial);
        Assert.Equal(UnitConverter.Fahrenheit, unit);
        Assert.Equal(212.0, value, precision: 10);
    }

    [Fact]
    public void FromEntered_Distance_RoundTrips()
    {
        var reading = ReadingValue.FromEntered(100.0, UnitConverter.Mile);
        
        // Canonical is meters (160934.4)
        Assert.Equal(UnitConverter.Meter, reading.UnitCanonical);
        Assert.Equal(160934.4, reading.ValueCanonical, precision: 6);
        
        // Round-trip back to miles
        var (value, unit) = reading.ToDisplay(UnitSystem.Imperial);
        Assert.Equal(UnitConverter.Mile, unit);
        Assert.Equal(100.0, value, precision: 10);
    }

    [Fact]
    public void Format_UsesG12Formatting()
    {
        var reading = ReadingValue.FromEntered(30.0, UnitConverter.Psi);
        var formatted = reading.Format(UnitSystem.Imperial);
        
        // G12 format: no trailing zeros for whole numbers
        Assert.Equal("30 psi", formatted);
    }

    [Fact]
    public void Format_DecimalValue()
    {
        var reading = ReadingValue.FromEntered(2.5, UnitConverter.Bar);
        var formatted = reading.Format(UnitSystem.Metric);
        
        Assert.Contains("bar", formatted);
        Assert.StartsWith("2.5", formatted);
    }
}
