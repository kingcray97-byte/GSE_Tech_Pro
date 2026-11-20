using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Devices.Can.Ecom;

namespace GseTechPro.Devices.Can;

/// <summary>Lists the CAN monitors this PC can open (simulator first) and creates them.</summary>
public sealed class CanBusMonitorFactory
{
    public const string SimulatorKind = "simulator";
    public const string EcomKind = "ecom";

    private readonly Func<string?> _checkEcomLibrary;
    private readonly Func<IReadOnlyList<uint>> _findEcomSerials;

    public CanBusMonitorFactory()
        : this(EcomDriver.CheckLibrary, EcomDriver.FindSerialNumbers)
    {
    }

    /// <summary>Test seam: fake the ECOM driver check and device search.</summary>
    public CanBusMonitorFactory(Func<string?> checkEcomLibrary, Func<IReadOnlyList<uint>> findEcomSerials)
    {
        _checkEcomLibrary = checkEcomLibrary;
        _findEcomSerials = findEcomSerials;
    }

    public static CanMonitorDevice Simulator { get; } = new()
    {
        Kind = SimulatorKind,
        Name = "Simulated J1939 bus",
        IsSimulated = true,
    };

    /// <summary>
    /// The simulator, then each attached ECOM. With the driver present but nothing plugged in, one "first free ECOM"
    /// entry is listed; without the driver, one ECOM entry says why it cannot be used.
    /// </summary>
    public IReadOnlyList<CanMonitorDevice> Discover()
    {
        var devices = new List<CanMonitorDevice> { Simulator };
        var reason = _checkEcomLibrary();
        if (reason is not null)
        {
            devices.Add(new CanMonitorDevice { Kind = EcomKind, Name = "ECOM CAN adapter", UnavailableReason = reason });
            return devices;
        }

        var serials = _findEcomSerials();
        if (serials.Count == 0)
        {
            devices.Add(new CanMonitorDevice { Kind = EcomKind, Name = "ECOM CAN adapter (first free)", SerialNumber = 0 });
            return devices;
        }

        devices.AddRange(serials.Select(sn => new CanMonitorDevice
        {
            Kind = EcomKind,
            Name = $"ECOM S/N {sn}",
            SerialNumber = sn,
        }));
        return devices;
    }

    public ICanBusMonitor Create(CanMonitorDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.UnavailableReason is not null)
            throw new InvalidOperationException(device.UnavailableReason);

        return device.Kind switch
        {
            SimulatorKind => new SimulatedCanBusMonitor(),
            EcomKind => new EcomCanBusMonitor(device.SerialNumber),
            _ => throw new ArgumentException($"Unknown CAN monitor kind '{device.Kind}'.", nameof(device)),
        };
    }
}
