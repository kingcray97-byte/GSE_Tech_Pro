using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;

namespace GseTechPro.App.Services;

/// <summary>
/// Ordered <see cref="IOemModuleRegistry"/>. Registration order is significant (first match wins);
/// re-registering an existing <see cref="IOemModule.Id"/> replaces it in place.
/// </summary>
public sealed class OemModuleRegistry : IOemModuleRegistry
{
    private readonly List<IOemModule> _modules = new();
    private readonly object _gate = new();

    public IReadOnlyList<IOemModule> Modules
    {
        get
        {
            lock (_gate)
                return _modules.ToArray();
        }
    }

    public void Register(IOemModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        lock (_gate)
        {
            var index = _modules.FindIndex(m => string.Equals(m.Id, module.Id, StringComparison.Ordinal));
            if (index >= 0)
                _modules[index] = module;
            else
                _modules.Add(module);
        }
    }

    public IOemModule? Resolve(VehicleProfile? profile)
    {
        if (profile is null)
            return null;

        IOemModule[] snapshot;
        lock (_gate)
            snapshot = _modules.ToArray();

        foreach (var module in snapshot)
        {
            if (module.CanHandle(profile))
                return module;
        }

        return null;
    }
}
