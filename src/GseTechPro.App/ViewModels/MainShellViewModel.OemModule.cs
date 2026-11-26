using GseTechPro.App.Services;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;

namespace GseTechPro.App.ViewModels;

// Step 5 of docs/PLAN-vci-vehicle-selection.md: OEM module hook.
public sealed partial class MainShellViewModel
{
    /// <summary>Registry of OEM modules (specific first, generic fallback last).</summary>
    public IOemModuleRegistry OemModules { get; }

    private static IOemModuleRegistry CreateDefaultOemModuleRegistry()
    {
        var registry = new OemModuleRegistry();
        registry.Register(new GenericOemModule());
        return registry;
    }

    /// <summary>Resolves the first registered OEM module that can handle <paramref name="profile"/>, or null.</summary>
    public IOemModule? ResolveOemModule(VehicleProfile profile) => OemModules.Resolve(profile);

    /// <summary>
    /// Resolves and loads the OEM module for <paramref name="profile"/>. Returns the fallback message
    /// when no make-specific module exists (resolved is null or generic); returns null when a specific
    /// module was loaded.
    /// </summary>
    public async Task<string?> TryLoadOemModuleAsync(
        VehicleProfile profile,
        IDiagnosticAdapter? adapter,
        CancellationToken ct = default)
    {
        var module = ResolveOemModule(profile);
        // Without an adapter (manual selection while disconnected) there is nothing to load onto; the
        // fallback message below still applies.
        if (module is not null && adapter is not null)
            await module.LoadAsync(profile, adapter, ct).ConfigureAwait(false);

        if (module is null || module.SupportedMakes.Count == 0)
            return $"No OEM module installed for {(string.IsNullOrWhiteSpace(profile.Make) ? "this vehicle" : profile.Make)}. Generic diagnostics loaded.";

        return null;
    }
}
