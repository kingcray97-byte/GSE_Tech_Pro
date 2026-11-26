using System.Text.Json;
using System.Text.Json.Serialization;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;

namespace GseTechPro.Data;

/// <summary>
/// Vehicle year/make/model catalog loaded from seeded JSON files in data/vehicles/.
/// Implements IVehicleCatalog with thread-safe caching.
/// </summary>
public sealed class VehicleCatalog : IVehicleCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    // Cached data structures
    private Dictionary<VehicleRegion, RegionData> _regions;
    private List<int> _allYears;
    private bool _loaded;
    private readonly object _lock = new();

    public VehicleCatalog()
    {
        _regions = new Dictionary<VehicleRegion, RegionData>();
        _allYears = new List<int>();
    }

    /// <summary>The heavy-duty brands list from heavy_duty.json (empty if not loaded).</summary>
    public IReadOnlyList<string> HeavyDutyBrands => GetHeavyDutyBrands();

    private string ResourceNameForRegion(VehicleRegion region)
    {
        return region switch
        {
            VehicleRegion.Domestic => "GseTechPro.Data.Content.Vehicles.domestic.json",
            VehicleRegion.Asian => "GseTechPro.Data.Content.Vehicles.asian.json",
            VehicleRegion.European => "GseTechPro.Data.Content.Vehicles.european.json",
            VehicleRegion.HeavyDuty => "GseTechPro.Data.Content.Vehicles.heavy_duty.json",
            _ => throw new ArgumentOutOfRangeException(nameof(region), region, null)
        };
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var regionsToLoad = new[]
        {
            VehicleRegion.Domestic,
            VehicleRegion.Asian,
            VehicleRegion.European,
            VehicleRegion.HeavyDuty
        };

        var loadedRegions = new Dictionary<VehicleRegion, RegionData>();
        var allYears = new HashSet<int>();

        foreach (var region in regionsToLoad)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resourceName = ResourceNameForRegion(region);
            await using var stream = typeof(VehicleCatalog).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' was not found.");

            var regionData = await ParseRegionAsync(stream, cancellationToken).ConfigureAwait(false);
            loadedRegions[region] = regionData;

            // Collect all years from this region
            foreach (var year in regionData.Years)
            {
                allYears.Add(year);
            }
        }

        // Sort years newest first
        var sortedYears = allYears.OrderByDescending(y => y).ToList();

        lock (_lock)
        {
            _regions = loadedRegions;
            _allYears = sortedYears;
            _loaded = true;
        }
    }

    private async Task<RegionData> ParseRegionAsync(Stream json, CancellationToken cancellationToken)
    {
        var raw = await JsonSerializer.DeserializeAsync<JsonRegion>(json, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (raw is null)
            throw new InvalidDataException("Vehicle catalog: region file could not be parsed.");

        var years = raw.Years?.ToList() ?? new List<int>();
        var makes = new Dictionary<string, MakeData>();

        foreach (var make in raw.Makes ?? [])
        {
            if (string.IsNullOrWhiteSpace(make.Name))
                continue;

            makes[make.Name] = new MakeData
            {
                Name = make.Name.Trim(),
                Years = make.Years?.ToList() ?? new List<int>(),
                Models = make.Models?.Select(m => m.Trim()).Where(m => !string.IsNullOrEmpty(m)).ToList() ?? new List<string>()
            };
        }

        return new RegionData
        {
            Years = years,
            Makes = makes,
            HeavyDutyBrands = raw.HeavyDutyBrands?.Select(b => b.Trim()).Where(b => !string.IsNullOrEmpty(b)).ToList() ?? new List<string>()
        };
    }

    public IReadOnlyList<VehicleYear> GetYears()
    {
        lock (_lock)
        {
            if (!_loaded)
                throw new InvalidOperationException("Vehicle catalog has not been loaded. Call LoadAsync first.");

            return _allYears.Select(y => new VehicleYear { Year = y }).ToList();
        }
    }

    public IReadOnlyList<VehicleMake> GetMakes(VehicleRegion region, int? year = null)
    {
        lock (_lock)
        {
            if (!_loaded)
                throw new InvalidOperationException("Vehicle catalog has not been loaded. Call LoadAsync first.");

            if (!_regions.TryGetValue(region, out var data))
                return Array.Empty<VehicleMake>();

            var makes = data.Makes.Values.ToList();

            // Filter by year if specified (using make's years array)
            if (year.HasValue)
            {
                makes = makes.Where(m => m.Years.Contains(year.Value)).ToList();
            }

            return makes.Select(m => new VehicleMake { Name = m.Name }).ToList();
        }
    }

    public IReadOnlyList<VehicleModel> GetModels(VehicleRegion region, int? year = null, string? make = null)
    {
        lock (_lock)
        {
            if (!_loaded)
                throw new InvalidOperationException("Vehicle catalog has not been loaded. Call LoadAsync first.");

            if (!_regions.TryGetValue(region, out var data))
                return Array.Empty<VehicleModel>();

            var makes = data.Makes.Values.ToList();

            // Filter by year if specified (using make's years array)
            if (year.HasValue)
            {
                makes = makes.Where(m => m.Years.Contains(year.Value)).ToList();
            }

            // Filter by make if specified
            if (!string.IsNullOrEmpty(make))
            {
                makes = makes.Where(m => string.Equals(m.Name, make, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            // Collect all models from matching makes
            var models = new List<string>();
            foreach (var m in makes)
            {
                models.AddRange(m.Models);
            }

            return models.Select(name => new VehicleModel { Name = name }).ToList();
        }
    }

    private IReadOnlyList<string> GetHeavyDutyBrands()
    {
        lock (_lock)
        {
            if (!_loaded || !_regions.TryGetValue(VehicleRegion.HeavyDuty, out var data))
                return Array.Empty<string>();

            return data.HeavyDutyBrands;
        }
    }

    // Internal data structures
    private sealed class RegionData
    {
        public List<int> Years { get; set; } = new();
        public Dictionary<string, MakeData> Makes { get; set; } = new();
        public List<string> HeavyDutyBrands { get; set; } = new();
    }

    private sealed class MakeData
    {
        public string Name { get; set; } = "";
        public List<int> Years { get; set; } = new();
        public List<string> Models { get; set; } = new();
    }

    // JSON deserialization models
    private sealed class JsonRegion
    {
        [JsonPropertyName("schemaVersion")]
        public int? SchemaVersion { get; set; }
        public string? Region { get; set; }
        public List<int>? Years { get; set; }
        public List<JsonMake>? Makes { get; set; }
        public List<string>? HeavyDutyBrands { get; set; }
    }

    private sealed class JsonMake
    {
        public string? Name { get; set; }
        public List<int>? Years { get; set; }
        public List<string>? Models { get; set; }
        public string? Notes { get; set; }
    }
}
