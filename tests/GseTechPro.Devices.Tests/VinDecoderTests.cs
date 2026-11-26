using GseTechPro.Core.Enums;
using Xunit;

namespace GseTechPro.Devices.Tests;

public class VinDecoderTests
{
    [Fact]
    public void Decode_ValidHondaVIN_ReturnsExpectedProfile()
    {
        var result = VinDecoder.Decode("1HGCM82633A004352");

        Assert.NotNull(result);
        Assert.Equal("Honda", result!.Make);
        Assert.Equal(VehicleRegion.Domestic, result.Region);
        Assert.Equal(2003, result.Year);
        Assert.Null(result.Engine);
    }

    [Fact]
    public void Decode_BadCheckDigit_ReturnsNull()
    {
        // Changed position 9 from '3' to '4', breaking the North-American check digit.
        Assert.Null(VinDecoder.Decode("1HGCM82634A004352"));
    }

    [Fact]
    public void Decode_UnknownWmiNonNorthAmerican_ReturnsNullMakeButProfile()
    {
        // 'S' = European, so check digit is skipped; WMI 'SZZ' is not in the dictionary.
        var result = VinDecoder.Decode("SZZCM82633A004352");

        Assert.NotNull(result);
        Assert.Null(result!.Make);
        Assert.Equal(VehicleRegion.European, result.Region);
        Assert.Equal(2003, result.Year);
        Assert.Null(result.Engine);
    }

    [Fact]
    public void Decode_InvalidLength_ReturnsNull()
    {
        Assert.Null(VinDecoder.Decode("1HGCM82633A00435")); // 16 chars
    }

    [Theory]
    [InlineData("1HGCM826I3A004352")] // 'I'
    [InlineData("1HGCM826O3A004352")] // 'O'
    [InlineData("1HGCM826Q3A004352")] // 'Q'
    public void Decode_InvalidCharacter_ReturnsNull(string vin)
    {
        Assert.Null(VinDecoder.Decode(vin));
    }

    [Theory]
    [InlineData("1HGCM8267AA004352", 2010)] // year code 'A'
    [InlineData("1HGCM82633A004352", 2003)] // year code '3'
    public void Decode_YearCode_DisambiguatesCorrectly(string vin, int expectedYear)
    {
        var result = VinDecoder.Decode(vin);

        Assert.NotNull(result);
        Assert.Equal(expectedYear, result!.Year);
    }

    [Fact]
    public void Decode_SimulatorVIN_ReturnsEuropeanWithNoMakeOrYear()
    {
        var result = VinDecoder.Decode("SIMULATED00000001");

        Assert.NotNull(result);
        Assert.Null(result!.Make);
        Assert.Equal(VehicleRegion.European, result.Region);
        Assert.Null(result.Year);
        Assert.Null(result.Engine);
    }

    [Fact]
    public void Decode_SevenPrefixedNorthAmericanVIN_ReturnsDomesticAndMake()
    {
        // '7' is a valid North-American WMI range (SAE). 7SA = Tesla.
        var result = VinDecoder.Decode("7SAFH2FH4J7123456");

        Assert.NotNull(result);
        Assert.Equal("Tesla", result!.Make);
        Assert.Equal(VehicleRegion.Domestic, result.Region);
        Assert.Equal(2018, result.Year);
    }

    [Fact]
    public void Decode_KiaWmi_ReturnsKia()
    {
        // KND is a Kia WMI and must not be shadowed by a duplicate Hyundai entry.
        var result = VinDecoder.Decode("KNDAB6K13G1234567");

        Assert.NotNull(result);
        Assert.Equal("Kia", result!.Make);
        Assert.Equal(VehicleRegion.Asian, result.Region);
        Assert.Equal(2016, result.Year);
    }
}
