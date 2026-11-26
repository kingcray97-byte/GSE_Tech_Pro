using GseTechPro.Core.Enums;
using GseTechPro.Data;
using Xunit;

namespace GseTechPro.Data.Tests;

/// <summary>Vehicle catalog loading and filtering tests (Step 4b).</summary>
public sealed class VehicleCatalogTests
{
    [Fact]
    public async Task LoadAsync_LoadsAllFourRegions()
    {
        var catalog = new VehicleCatalog();
        await catalog.LoadAsync();

        // Should not throw for any region
        Assert.NotNull(catalog);
    }

    [Fact]
    public async Task GetYears_ReturnsNonEmptyNewestFirstDeduplicated()
    {
        var catalog = new VehicleCatalog();
        await catalog.LoadAsync();

        var years = catalog.GetYears();

        Assert.NotEmpty(years);

        // Newest first
        for (int i = 1; i < years.Count; i++)
        {
            Assert.True(years[i - 1].Year >= years[i].Year, $"Year at index {i-1} ({years[i-1].Year}) should be >= year at index {i} ({years[i].Year})");
        }

        // Deduplicated
        var yearValues = years.Select(y => y.Year).ToList();
        Assert.Equal(yearValues.Distinct().Count(), yearValues.Count);
    }

    [Fact]
    public async Task GetMakes_DomesticNullYear_ReturnsKnownMakes()
    {
        var catalog = new VehicleCatalog();
        await catalog.LoadAsync();

        var makes = catalog.GetMakes(VehicleRegion.Domestic, null);
        var makeNames = makes.Select(m => m.Name).ToList();

        Assert.Contains("Ford", makeNames);
        Assert.Contains("Chevrolet", makeNames);
    }

    [Fact]
    public async Task GetMakes_Domestic2025_ReturnsMakesOfferedIn2025()
    {
        var catalog = new VehicleCatalog();
        await catalog.LoadAsync();

        var makes = catalog.GetMakes(VehicleRegion.Domestic, 2025);
        var makeNames = makes.Select(m => m.Name).ToList();

        // Ford and Chevrolet are offered in 2025 per the seed data
        Assert.Contains("Ford", makeNames);
        Assert.Contains("Chevrolet", makeNames);
    }

    [Fact]
    public async Task GetModels_Domestic2025Ford_ReturnsFordModels()
    {
        var catalog = new VehicleCatalog();
        await catalog.LoadAsync();

        var models = catalog.GetModels(VehicleRegion.Domestic, 2025, "Ford");
        var modelNames = models.Select(m => m.Name).ToList();

        Assert.Contains("F-150", modelNames);
        Assert.Contains("Escape", modelNames);
    }

    [Fact]
    public async Task GetModels_NullYearNullMake_ReturnsAllModelsForRegion()
    {
        var catalog = new VehicleCatalog();
        await catalog.LoadAsync();

        var models = catalog.GetModels(VehicleRegion.Domestic, null, null);
        Assert.NotEmpty(models);

        // Should include models from multiple makes
        var modelNames = models.Select(m => m.Name).ToList();
        Assert.Contains("F-150", modelNames);      // Ford
        Assert.Contains("Silverado 1500", modelNames); // Chevrolet
    }

    [Fact]
    public async Task HeavyDutyBrands_ContainsExpectedBrands()
    {
        var catalog = new VehicleCatalog();
        await catalog.LoadAsync();

        Assert.Contains("Freightliner", catalog.HeavyDutyBrands);
        Assert.Contains("Kenworth", catalog.HeavyDutyBrands);
        Assert.Contains("Peterbilt", catalog.HeavyDutyBrands);
    }

    [Fact]
    public async Task RegionMapping_AllFourRegionsLoadWithoutError()
    {
        var catalog = new VehicleCatalog();
        await catalog.LoadAsync();

        // All four regions should load without throwing
        Assert.NotNull(catalog.GetMakes(VehicleRegion.Domestic, null));
        Assert.NotNull(catalog.GetMakes(VehicleRegion.Asian, null));
        Assert.NotNull(catalog.GetMakes(VehicleRegion.European, null));
        Assert.NotNull(catalog.GetMakes(VehicleRegion.HeavyDuty, null));
    }

    [Fact]
    public async Task GetModels_HeavyDuty_ReturnsTruckAndGseModels()
    {
        var catalog = new VehicleCatalog();
        await catalog.LoadAsync();

        // Truck models
        var freightlinerModels = catalog.GetModels(VehicleRegion.HeavyDuty, null, "Freightliner");
        Assert.Contains(freightlinerModels.Select(m => m.Name), n => n == "Cascadia");

        // GSE equipment models (no years in seed)
        var tugModels = catalog.GetModels(VehicleRegion.HeavyDuty, null, "TUG Technologies");
        Assert.NotEmpty(tugModels);
    }

    [Fact]
    public async Task LoadAsync_IsIdempotent()
    {
        var catalog = new VehicleCatalog();
        await catalog.LoadAsync();
        var years1 = catalog.GetYears().Count;

        // Reload should work and produce same results
        await catalog.LoadAsync();
        var years2 = catalog.GetYears().Count;

        Assert.Equal(years1, years2);
    }
}
