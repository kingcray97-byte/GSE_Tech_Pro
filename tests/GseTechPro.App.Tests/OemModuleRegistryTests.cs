using GseTechPro.App.Services;
using GseTechPro.App.ViewModels;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Data;
using GseTechPro.Devices;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Step 5 (docs/PLAN-vci-vehicle-selection.md): OEM module registry, generic fallback and VM hook.</summary>
public sealed class OemModuleRegistryTests
{
    private sealed class FakeOemModule : IOemModule
    {
        public FakeOemModule(string id, params string[] makes)
        {
            Id = id;
            SupportedMakes = makes;
        }

        public string Id { get; }
        public VehicleRegion? Region => null;
        public IReadOnlyList<string> SupportedMakes { get; }
        public int LoadCount { get; private set; }

        public Task LoadAsync(VehicleProfile profile, IDiagnosticAdapter adapter, CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return Task.CompletedTask;
        }
    }

    private static VehicleProfile Profile(string? make) => new() { Make = make };

    [Fact]
    public void Resolve_SpecificModule_MatchesMakeCaseInsensitive()
    {
        var registry = new OemModuleRegistry();
        var specific = new FakeOemModule("freightliner.hd", "Freightliner");
        registry.Register(specific);
        registry.Register(new GenericOemModule());

        Assert.Same(specific, registry.Resolve(Profile("freightLINER")));
    }

    [Fact]
    public void Resolve_GenericModule_MatchesAnyNonNullProfile()
    {
        var registry = new OemModuleRegistry();
        var generic = new GenericOemModule();
        registry.Register(generic);

        Assert.Same(generic, registry.Resolve(Profile("Toyota")));
        Assert.Same(generic, registry.Resolve(Profile(null)));
    }

    [Fact]
    public void Resolve_NullProfile_ReturnsNull()
    {
        var registry = new OemModuleRegistry();
        registry.Register(new GenericOemModule());

        Assert.Null(registry.Resolve(null));
    }

    [Fact]
    public void Resolve_NoMatch_ReturnsNull()
    {
        var registry = new OemModuleRegistry();
        registry.Register(new FakeOemModule("freightliner.hd", "Freightliner"));

        Assert.Null(registry.Resolve(Profile("Toyota")));
    }

    [Fact]
    public void Resolve_FirstRegisteredMatchWins()
    {
        var registry = new OemModuleRegistry();
        var first = new FakeOemModule("a", "Ford");
        var second = new FakeOemModule("b", "Ford");
        registry.Register(first);
        registry.Register(second);
        registry.Register(new GenericOemModule());

        Assert.Same(first, registry.Resolve(Profile("Ford")));
        Assert.Equal(new[] { "a", "b", GenericOemModule.ModuleId }, registry.Modules.Select(m => m.Id));
    }

    [Fact]
    public void Register_SameId_ReplacesInPlace()
    {
        var registry = new OemModuleRegistry();
        var original = new FakeOemModule("a", "Ford");
        var other = new FakeOemModule("b", "Ford");
        var replacement = new FakeOemModule("a", "Ford");
        registry.Register(original);
        registry.Register(other);

        registry.Register(replacement);

        Assert.Equal(2, registry.Modules.Count);
        Assert.Same(replacement, registry.Modules[0]);
        Assert.Same(other, registry.Modules[1]);
        Assert.Same(replacement, registry.Resolve(Profile("Ford")));
    }

    [Fact]
    public void ResolvedGenericModule_HasEmptySupportedMakes()
    {
        var registry = new OemModuleRegistry();
        registry.Register(new FakeOemModule("freightliner.hd", "Freightliner"));
        registry.Register(new GenericOemModule());

        var resolved = registry.Resolve(Profile("Toyota"));

        Assert.NotNull(resolved);
        Assert.Equal(GenericOemModule.ModuleId, resolved!.Id);
        Assert.Empty(resolved.SupportedMakes);
        Assert.Null(resolved.Region);
    }

    private static async Task<MainShellViewModel> CreateViewModelAsync(IOemModuleRegistry? registry = null)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-oem-{Guid.NewGuid():N}.db");
        var data = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await data.InitializeAsync();
        return new MainShellViewModel(data, new DiagnosticAdapterFactory(), oemModuleRegistry: registry);
    }

    [Fact]
    public async Task ViewModel_DefaultRegistry_HasGenericModule()
    {
        await using var vm = await CreateViewModelAsync();

        Assert.Contains(vm.OemModules.Modules, m => m.Id == GenericOemModule.ModuleId);
        Assert.NotNull(vm.ResolveOemModule(Profile("Kenworth")));
    }

    [Fact]
    public async Task TryLoadOemModule_GenericFallback_ReturnsMessage()
    {
        await using var vm = await CreateViewModelAsync();

        var message = await vm.TryLoadOemModuleAsync(Profile("Kenworth"), null!, CancellationToken.None);
        Assert.Equal("No OEM module installed for Kenworth. Generic diagnostics loaded.", message);

        var unknown = await vm.TryLoadOemModuleAsync(Profile(null), null!, CancellationToken.None);
        Assert.Equal("No OEM module installed for this vehicle. Generic diagnostics loaded.", unknown);

        foreach (var blank in new[] { "", "   " })
        {
            var blankMessage = await vm.TryLoadOemModuleAsync(Profile(blank), null!, CancellationToken.None);
            Assert.Equal("No OEM module installed for this vehicle. Generic diagnostics loaded.", blankMessage);
        }
    }

    [Fact]
    public async Task TryLoadOemModule_SpecificModule_LoadsAndReturnsNull()
    {
        var registry = new OemModuleRegistry();
        var specific = new FakeOemModule("kenworth.hd", "Kenworth");
        registry.Register(specific);
        registry.Register(new GenericOemModule());
        await using var vm = await CreateViewModelAsync(registry);

        // A null adapter now skips LoadAsync (Step 9), so load against a real simulator adapter.
        var adapter = new GseTechPro.Devices.DiagnosticAdapterFactory()
            .CreateSimulator(GseTechPro.Core.Enums.ProtocolType.J1939, seed: null);
        await adapter.ConnectAsync();

        var message = await vm.TryLoadOemModuleAsync(Profile("Kenworth"), adapter, CancellationToken.None);

        Assert.Null(message);
        Assert.Equal(1, specific.LoadCount);

        // No adapter (manual selection while disconnected): module resolved, load skipped, no fallback text.
        var offline = await vm.TryLoadOemModuleAsync(Profile("Kenworth"), null, CancellationToken.None);

        Assert.Null(offline);
        Assert.Equal(1, specific.LoadCount);
    }
}
