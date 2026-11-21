using GseTechPro.App.ViewModels;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Devices.Can;
using GseTechPro.Devices.Protocols.J1939;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 10C.3: PCS TCM codes found by proprietary SPN, on the DTC list and the CAN bus monitor.</summary>
public sealed class PcsTcmDtcTests
{
    private static async Task<MainShellViewModel> CreateInitializedViewModelAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-pcs-{Guid.NewGuid():N}", "gse.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var data = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory())
        {
            CanFactory = new CanBusMonitorFactory(() => "ECOM driver not installed (test)", Array.Empty<uint>),
        };
        await vm.InitializeAsync();
        return vm;
    }

    [Fact]
    public async Task ProprietarySpn_FindsThePcsCode_OnlyWhenNoExactRowExists()
    {
        var vm = await CreateInitializedViewModelAsync();

        var pcs = vm.FindManufacturerSpnEntry(522742);
        Assert.NotNull(pcs);
        Assert.Equal("PCS 72", pcs!.Code);
        Assert.Equal(ProtocolType.Manufacturer, pcs.Protocol);
        Assert.Contains("Technician's Guide v2.0", pcs.SourceCitation);

        // A standard SPN never falls back to a manufacturer row, and an unlisted proprietary SPN finds nothing.
        Assert.Null(vm.FindManufacturerSpnEntry(110));
        Assert.Null(vm.FindManufacturerSpnEntry(520192));
        Assert.Null(vm.FindProprietarySpnEntry(new DtcReadout
        {
            Code = "SPN 522742 FMI 31", Protocol = ProtocolType.OBD2, Spn = 522742, Fmi = 31, Status = DtcStatus.Active,
        }));
        Assert.NotNull(vm.FindProprietarySpnEntry(new DtcReadout
        {
            Code = "SPN 522742 FMI 31", Protocol = ProtocolType.J1939, Spn = 522742, Fmi = 31, Status = DtcStatus.Active,
        }));
        Assert.Null(vm.FindDtcLibraryEntry(ProtocolType.J1939, "SPN 522742 FMI 31"));
    }

    [Fact]
    public async Task BusMonitorRow_NamesThePcsCode_AndSaysItIsPcsOnly()
    {
        var vm = await CreateInitializedViewModelAsync();
        var table = new CanBusTable();
        foreach (var frame in SimulatedCanBusMonitor.FramesBetween(0, 1.1))
            table.Apply(frame);
        var dm1 = table.Entries.Single(e => e.Pgn == J1939Pgn.Dm1);

        var row = new CanBusRowViewModel(dm1, vm.FindManufacturerSpnEntry);

        Assert.Contains("Active faults: 1", row.DecodedText);
        Assert.Contains("lamps Amber", row.DecodedText);
        Assert.Contains("SPN 522742 FMI 31 OC 1 = PCS 72 Intermittent output shaft speed (if PCS TCM)", row.DecodedText);

        // Without the library the raw SPN/FMI is still shown, with no invented name.
        var bare = new CanBusRowViewModel(dm1);
        Assert.Contains("SPN 522742 FMI 31 OC 1", bare.DecodedText);
        Assert.DoesNotContain("PCS", bare.DecodedText);
    }
}
