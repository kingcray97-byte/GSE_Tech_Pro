using GseTechPro.App.Services;
using GseTechPro.App.ViewModels;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Data;
using GseTechPro.Devices;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>
/// Vehicle/VCI selection plan Step 9: the vehicle selector modal. The WinUI ContentDialog needs a XamlRoot, so the
/// cascade is tested through <see cref="VehicleSelectorModel"/> and the shell flow through the presenter hook.
/// </summary>
public sealed class VehicleSelectorTests
{
    /// <summary>In-memory catalog: 2023/2024; Ford (F-150, Mustang) in both, Chevrolet (Silverado) only in 2024.</summary>
    private sealed class FakeCatalog : IVehicleCatalog
    {
        public int LoadCalls { get; private set; }

        public Task LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCalls++;
            return Task.CompletedTask;
        }

        // Deliberately oldest-first so the model's descending sort is exercised.
        public IReadOnlyList<VehicleYear> GetYears() =>
            new[] { new VehicleYear { Year = 2023 }, new VehicleYear { Year = 2024 } };

        public IReadOnlyList<VehicleMake> GetMakes(VehicleRegion region, int? year = null)
        {
            if (region != VehicleRegion.Domestic)
                return Array.Empty<VehicleMake>();

            var names = year == 2024 ? new[] { "Ford", "Chevrolet" } : new[] { "Ford" };
            return names.Select(n => new VehicleMake { Name = n }).ToList();
        }

        public IReadOnlyList<VehicleModel> GetModels(VehicleRegion region, int? year = null, string? make = null) =>
            (make switch
            {
                "Ford" => new[] { "F-150", "Mustang" },
                "Chevrolet" => new[] { "Silverado" },
                _ => Array.Empty<string>(),
            }).Select(n => new VehicleModel { Name = n }).ToList();
    }

    private sealed class SpecificModule : IOemModule
    {
        public int LoadCalls { get; private set; }
        public string Id => "test.ford";
        public VehicleRegion? Region => null;
        public IReadOnlyList<string> SupportedMakes { get; } = new[] { "Ford" };

        public Task LoadAsync(VehicleProfile profile, IDiagnosticAdapter adapter, CancellationToken cancellationToken = default)
        {
            LoadCalls++;
            return Task.CompletedTask;
        }
    }

    private static async Task<MainShellViewModel> CreateAsync(FakeCatalog? catalog = null, IOemModuleRegistry? registry = null)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-vehsel-{Guid.NewGuid():N}.db");
        var data = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await data.InitializeAsync();
        return new MainShellViewModel(
            data,
            new DiagnosticAdapterFactory(),
            oemModuleRegistry: registry,
            vehicleCatalog: catalog ?? new FakeCatalog());
    }

    /// <summary>Presenter that "taps" year / make / model by name and saves (what a user does in the dialog).</summary>
    private static Func<IVehicleCatalog, VehicleRegion, string?, IDiagnosticAdapter?, Task<VehicleProfile?>> PickAndSave(
        int year, string make, string model) =>
        async (catalog, region, system, _) =>
        {
            var m = new VehicleSelectorModel(catalog, region, system);
            await m.InitializeAsync();
            m.SelectedYearIndex = m.Years.IndexOf(year);
            m.SelectedMakeIndex = m.Makes.IndexOf(make);
            m.SelectedModelIndex = m.Models.IndexOf(model);
            return m.BuildProfile();
        };

    // ---------------------------------------------------------------- model (cascade + Save rules)

    [Fact]
    public async Task Model_Initialize_LoadsYearsNewestFirst()
    {
        var catalog = new FakeCatalog();
        var model = new VehicleSelectorModel(catalog, VehicleRegion.Domestic);

        await model.InitializeAsync();
        await model.InitializeAsync(); // idempotent

        Assert.Equal(new[] { 2024, 2023 }, model.Years);
        Assert.Equal(1, catalog.LoadCalls);
        Assert.Empty(model.Makes);
        Assert.Empty(model.Models);
        Assert.Equal(-1, model.SelectedYearIndex);
        Assert.False(model.IsSaveEnabled);
    }

    [Fact]
    public async Task Model_SelectingYear_BuildsMakes_AndResetsMakeAndModel()
    {
        var model = new VehicleSelectorModel(new FakeCatalog(), VehicleRegion.Domestic);
        await model.InitializeAsync();

        model.SelectedYearIndex = 0; // 2024
        Assert.Equal(new[] { "Ford", "Chevrolet" }, model.Makes);

        model.SelectedMakeIndex = 1; // Chevrolet
        Assert.Equal(new[] { "Silverado" }, model.Models);
        model.SelectedModelIndex = 0;
        Assert.True(model.IsSaveEnabled);

        model.SelectedYearIndex = 1; // 2023: only Ford
        Assert.Equal(new[] { "Ford" }, model.Makes);
        Assert.Equal(-1, model.SelectedMakeIndex);
        Assert.Equal(-1, model.SelectedModelIndex);
        Assert.Empty(model.Models);
        Assert.False(model.IsSaveEnabled);
    }

    [Fact]
    public async Task Model_SelectingMake_BuildsModels_AndResetsModel()
    {
        var model = new VehicleSelectorModel(new FakeCatalog(), VehicleRegion.Domestic);
        await model.InitializeAsync();
        model.SelectedYearIndex = 0;
        model.SelectedMakeIndex = 0; // Ford
        model.SelectedModelIndex = 1;
        Assert.Equal(new[] { "F-150", "Mustang" }, model.Models);

        model.SelectedMakeIndex = 1; // Chevrolet

        Assert.Equal(new[] { "Silverado" }, model.Models);
        Assert.Equal(-1, model.SelectedModelIndex);
        Assert.False(model.IsSaveEnabled);
    }

    [Fact]
    public async Task Model_SaveDisabledUntilAllThreeSelected_AndRaisesNotification()
    {
        var model = new VehicleSelectorModel(new FakeCatalog(), VehicleRegion.Domestic);
        await model.InitializeAsync();
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.False(model.IsSaveEnabled);
        model.SelectedYearIndex = 0;
        Assert.False(model.IsSaveEnabled);
        model.SelectedMakeIndex = 0;
        Assert.False(model.IsSaveEnabled);
        Assert.Null(model.BuildProfile());
        model.SelectedModelIndex = 0;
        Assert.True(model.IsSaveEnabled);

        Assert.Contains(nameof(VehicleSelectorModel.IsSaveEnabled), changed);
    }

    [Fact]
    public async Task Model_OutOfRangeIndex_IsTreatedAsNone()
    {
        var model = new VehicleSelectorModel(new FakeCatalog(), VehicleRegion.Domestic);
        await model.InitializeAsync();

        model.SelectedYearIndex = 99;

        Assert.Equal(-1, model.SelectedYearIndex);
        Assert.Empty(model.Makes);
    }

    [Fact]
    public async Task Model_BuildProfile_IsManualWithRegionAndNoVin()
    {
        var model = new VehicleSelectorModel(new FakeCatalog(), VehicleRegion.Domestic);
        await model.InitializeAsync();
        model.SelectedYearIndex = 0;
        model.SelectedMakeIndex = 0;
        model.SelectedModelIndex = 1;

        var profile = model.BuildProfile();

        Assert.NotNull(profile);
        Assert.Null(profile!.Vin);
        Assert.Equal("Ford", profile.Make);
        Assert.Equal("Mustang", profile.Model);
        Assert.Equal(2024, profile.Year);
        Assert.Equal(VehicleRegion.Domestic, profile.Region);
        Assert.Equal(VehicleProfileSource.Manual, profile.Source);
        Assert.Null(profile.Protocol);
    }

    [Theory]
    [InlineData(null, "Select Vehicle")]
    [InlineData("Engine", "Select Vehicle for Engine Diagnostics")]
    [InlineData("Truck", "Select Vehicle for Truck Diagnostics")]
    public void Title_NamesSystemWhenGiven(string? system, string expected)
    {
        Assert.Equal(expected, VehicleSelectorModel.BuildTitle(system));
        Assert.Equal(expected, new VehicleSelectorModel(new FakeCatalog(), VehicleRegion.HeavyDuty, system).Title);
    }

    [Fact]
    public async Task Model_WithRealSeedCatalog_CanSelectFirstYearMakeModel()
    {
        var model = new VehicleSelectorModel(new VehicleCatalog(), VehicleRegion.Domestic);
        await model.InitializeAsync();
        Assert.NotEmpty(model.Years);
        Assert.Equal(model.Years.OrderByDescending(y => y), model.Years);

        // Pick the first year that has a make with a model (the seed data is real, don't assume names).
        for (var y = 0; y < model.Years.Count && !model.IsSaveEnabled; y++)
        {
            model.SelectedYearIndex = y;
            for (var m = 0; m < model.Makes.Count && !model.IsSaveEnabled; m++)
            {
                model.SelectedMakeIndex = m;
                if (model.Models.Count > 0)
                    model.SelectedModelIndex = 0;
            }
        }

        Assert.True(model.IsSaveEnabled);
        Assert.Equal(VehicleProfileSource.Manual, model.BuildProfile()!.Source);
    }

    // ---------------------------------------------------------------- shell flow (OpenVehicleSelectorAsync)

    [Fact]
    public async Task Open_Domestic_SaveSetsActiveVehicleAsManual_AndBannerAndFallbackMessage()
    {
        await using var vm = await CreateAsync();
        vm.VehicleSelectorPresenter = PickAndSave(2024, "Ford", "F-150");

        await vm.OpenVehicleSelectorAsync(VehicleRegion.Domestic, null);

        var v = vm.ActiveVehicle;
        Assert.NotNull(v);
        Assert.Equal(VehicleProfileSource.Manual, v!.Source);
        Assert.Equal("Ford", v.Make);
        Assert.Equal("F-150", v.Model);
        Assert.Equal(2024, v.Year);
        Assert.Equal(VehicleRegion.Domestic, v.Region);
        Assert.Contains("Make: Ford", vm.ActiveVehicleHeaderLine);
        // Only the generic module is registered, so the fallback message follows the selection message.
        Assert.Equal(
            "Vehicle selected: Ford F-150 (2024). No OEM module installed for Ford. Generic diagnostics loaded.",
            vm.StatusBannerMessage);
    }

    [Fact]
    public async Task Open_Save_WithSpecificOemModule_LoadsModuleOnlyWhenAdapterPresent()
    {
        var module = new SpecificModule();
        var registry = new OemModuleRegistry();
        registry.Register(module);
        registry.Register(new GenericOemModule());
        await using var vm = await CreateAsync(registry: registry);
        vm.VehicleSelectorPresenter = PickAndSave(2024, "Ford", "F-150");

        await vm.OpenVehicleSelectorAsync(VehicleRegion.Domestic, null);

        // No adapter connected: LoadAsync is skipped, no fallback text because a specific module matched.
        Assert.Equal(0, module.LoadCalls);
        Assert.Equal("Vehicle selected: Ford F-150 (2024)", vm.StatusBannerMessage);

        var adapter = new DiagnosticAdapterFactory().CreateSimulator(ProtocolType.OBD2, seed: null);
        await adapter.ConnectAsync();
        typeof(MainShellViewModel)
            .GetField("_activeAdapter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(vm, adapter);
        vm.VehicleSelectorPresenter = PickAndSave(2024, "Ford", "Mustang");

        await vm.OpenVehicleSelectorAsync(VehicleRegion.Domestic, null);

        Assert.Equal(1, module.LoadCalls);
        Assert.Equal("Vehicle selected: Ford Mustang (2024)", vm.StatusBannerMessage);
    }

    [Fact]
    public async Task Open_Close_LeavesActiveVehicleAndBannerUnchanged()
    {
        await using var vm = await CreateAsync();
        var existing = new VehicleProfile { Vin = "EXISTINGVIN", Make = "Kenworth", Source = VehicleProfileSource.Auto };
        vm.SetActiveVehicle(existing);
        vm.StatusBannerMessage = "before";
        vm.VehicleSelectorPresenter = (_, _, _, _) => Task.FromResult<VehicleProfile?>(null); // Close

        await vm.OpenVehicleSelectorAsync(VehicleRegion.Domestic, null);

        Assert.Same(existing, vm.ActiveVehicle);
        Assert.Equal("before", vm.StatusBannerMessage);
    }

    [Fact]
    public async Task Open_IncompleteSelection_CannotSave()
    {
        await using var vm = await CreateAsync();
        vm.VehicleSelectorPresenter = async (catalog, region, system, _) =>
        {
            var m = new VehicleSelectorModel(catalog, region, system);
            await m.InitializeAsync();
            m.SelectedYearIndex = 0;
            m.SelectedMakeIndex = 0; // no model chosen
            Assert.False(m.IsSaveEnabled);
            return m.BuildProfile(); // the dialog cancels Save in this state; Result stays null
        };

        await vm.OpenVehicleSelectorAsync(VehicleRegion.Domestic, null);

        Assert.Null(vm.ActiveVehicle);
    }

    [Fact]
    public async Task Open_ClearsPendingRegionAndSystem_AfterSaveCloseAndFailure()
    {
        await using var vm = await CreateAsync();
        vm.ActiveTool = "OemDiagnostics";

        vm.VehicleSelectorPresenter = PickAndSave(2024, "Ford", "F-150");
        await vm.OnSidebarActionAsync("Domestic");
        Assert.Null(vm.PendingVehicleRegion);

        vm.VehicleSelectorPresenter = (_, _, _, _) => Task.FromResult<VehicleProfile?>(null);
        await vm.OnSidebarActionAsync("Asian");
        Assert.Null(vm.PendingVehicleRegion);

        vm.VehicleSelectorPresenter = (_, _, _, _) => throw new InvalidOperationException("boom");
        await vm.OnSidebarActionAsync("Engine");
        Assert.Null(vm.PendingHdSystem);
        Assert.Contains("Vehicle selection failed: boom", vm.StatusBannerMessage);
    }

    [Fact]
    public async Task Open_NoWindowAndNoPresenter_ShowsMessage_WithoutThrowing()
    {
        await using var vm = await CreateAsync();
        vm.ActiveTool = "OemDiagnostics";

        await vm.OnSidebarActionAsync("European");

        Assert.Equal(MainShellViewModel.VehicleSelectorUnavailableMessage, vm.StatusBannerMessage);
        Assert.Null(vm.ActiveVehicle);
        Assert.Null(vm.PendingVehicleRegion);
    }

    [Theory]
    [InlineData("Domestic", VehicleRegion.Domestic, null)]
    [InlineData("Asian", VehicleRegion.Asian, null)]
    [InlineData("European", VehicleRegion.European, null)]
    [InlineData("Engine", VehicleRegion.HeavyDuty, "Engine")]
    [InlineData("Trans", VehicleRegion.HeavyDuty, "Trans")]
    [InlineData("ABS", VehicleRegion.HeavyDuty, "ABS")]
    [InlineData("Truck", VehicleRegion.HeavyDuty, "Truck")]
    public async Task SidebarActions_OpenSelector_WithRegionAndSystem(string action, VehicleRegion region, string? system)
    {
        await using var vm = await CreateAsync();
        (VehicleRegion Region, string? System)? seen = null;
        vm.VehicleSelectorPresenter = (_, r, s, _) =>
        {
            seen = (r, s);
            return Task.FromResult<VehicleProfile?>(null);
        };

        await vm.OnSidebarActionAsync(action);

        Assert.Equal((region, system), seen);
    }

    [Fact]
    public async Task Open_LoadsCatalogOnce_AndExposesIt()
    {
        var catalog = new FakeCatalog();
        await using var vm = await CreateAsync(catalog);
        vm.VehicleSelectorPresenter = (_, _, _, _) => Task.FromResult<VehicleProfile?>(null);

        await vm.OpenVehicleSelectorAsync(VehicleRegion.Domestic, null);
        await vm.OpenVehicleSelectorAsync(VehicleRegion.Asian, null);
        await vm.EnsureVehicleCatalogLoadedAsync();

        Assert.Equal(1, catalog.LoadCalls);
        Assert.Same(catalog, vm.VehicleCatalog);
    }

    [Fact]
    public async Task Open_PassesActiveAdapterToSelector_NullWhenDisconnected()
    {
        await using var vm = await CreateAsync();
        IDiagnosticAdapter? seen = new DiagnosticAdapterFactory().CreateSimulator(ProtocolType.OBD2, seed: null);
        vm.VehicleSelectorPresenter = (_, _, _, adapter) =>
        {
            seen = adapter;
            return Task.FromResult<VehicleProfile?>(null);
        };

        await vm.OpenVehicleSelectorAsync(VehicleRegion.Domestic, null);

        Assert.Null(seen);
    }

    [Fact]
    public async Task Open_MissingModel_RendersUnknownNotBlank()
    {
        await using var vm = await CreateAsync();
        vm.VehicleSelectorPresenter = (_, _, _, _) =>
            Task.FromResult<VehicleProfile?>(new VehicleProfile { Make = "Ford", Year = null });

        await vm.OpenVehicleSelectorAsync(VehicleRegion.Domestic, null);

        Assert.StartsWith("Vehicle selected: Ford (Unknown)", vm.StatusBannerMessage);
    }

    // ---------------------------------------------------------------- TryLoadOemModuleAsync with null adapter

    [Fact]
    public async Task TryLoadOemModule_NullAdapter_GenericModule_ReturnsFallbackMessage()
    {
        await using var vm = await CreateAsync();

        var msg = await vm.TryLoadOemModuleAsync(new VehicleProfile { Make = "Toyota" }, adapter: null);

        Assert.Equal("No OEM module installed for Toyota. Generic diagnostics loaded.", msg);
    }

    [Fact]
    public async Task TryLoadOemModule_NullAdapter_NoModuleRegistered_ReturnsFallbackMessage()
    {
        await using var vm = await CreateAsync(registry: new OemModuleRegistry());

        var msg = await vm.TryLoadOemModuleAsync(new VehicleProfile { Make = null }, adapter: null);

        Assert.Equal("No OEM module installed for this vehicle. Generic diagnostics loaded.", msg);
    }
}
