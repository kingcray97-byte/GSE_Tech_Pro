using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;

namespace GseTechPro.App.Services;

/// <summary>
/// Built-in fallback OEM module: region-agnostic, handles any make (empty
/// <see cref="SupportedMakes"/>), and loads nothing. Registered last so the vehicle-selection flow
/// works end to end with no OEM add-on installed.
/// </summary>
public sealed class GenericOemModule : IOemModule
{
    public const string ModuleId = "gse.generic";

    public string Id => ModuleId;

    public VehicleRegion? Region => null;

    public IReadOnlyList<string> SupportedMakes { get; } = Array.Empty<string>();

    public Task LoadAsync(VehicleProfile profile, IDiagnosticAdapter adapter, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
