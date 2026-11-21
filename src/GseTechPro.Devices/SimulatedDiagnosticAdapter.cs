using System.Diagnostics;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using GseTechPro.Core.Units;
using GseTechPro.Devices.Protocols;

namespace GseTechPro.Devices;

/// <summary>
/// SIMULATED diagnostic adapter. Every value this class produces — DTCs, PIDs, VIN, ECU list,
/// freeze frame — is DEMO/FAKE data used to exercise the UI and the device-abstraction contract
/// before real hardware is wired up in Phase 2. NONE of it is a real specification value; do not
/// treat any pressure/voltage/temperature/speed here as correct for any real vehicle or equipment.
///
/// Behavior:
/// <list type="bullet">
/// <item>Reproducible: streaming values are a slow sine (or ramp) plus seeded noise, keyed by a tick
/// counter so the sequence is the same for a given <c>seed</c> across runs. One-shot reads
/// (<c>ReadLiveDataAsync</c>/<c>ReadFreezeFrameAsync</c>) are deterministic per <c>(seed, pid)</c> —
/// they use their own seed-derived noise and a fixed sample time, so they never depend on stream
/// timing.</item>
/// <item>Async-honest: connect/stream use <c>Task.Delay</c>/<c>PeriodicTimer</c> and never block;
/// the streaming loop runs on a background task and honors a cancellation token.</item>
/// <item>Canonical SI units: pressure → Pa, temperature → °C, voltage → V, flow → L/min
/// (<see cref="UnitConverter"/> constants). Speed → m/s and engine speed → rpm (no unit dimension
/// in <see cref="UnitConverter"/>, reported literally).</item>
/// </list>
/// </summary>
public sealed class SimulatedDiagnosticAdapter : IDiagnosticAdapter
{
    private const double SamplePeriodSeconds = 0.1;

    /// <summary>
    /// Fixed sample time (seconds) used by one-shot reads (<see cref="ReadLiveDataAsync"/> and
    /// <see cref="ReadFreezeFrameAsync"/>). Kept at zero so a read's wave value is a stable snapshot
    /// rather than advancing with the streaming tick counter.
    /// </summary>
    private const double ReadSampleTimeSeconds = 0.0;

    private static readonly ProtocolType[] SimulatedProtocols =
    {
        ProtocolType.J1939,
        ProtocolType.J1708,
        ProtocolType.OBD2,
    };

    private readonly ProtocolType _protocol;
    private readonly int _seed;
    private readonly IReadOnlyList<SimPid> _pids;

    // Stream RNG: consumed only by the background StreamLoopAsync for per-tick PID noise. The
    // one-shot read path (ReadLiveDataAsync / ReadFreezeFrameAsync) never touches this, so a read's
    // value cannot depend on how many stream ticks have already consumed NextDouble().
    private readonly object _randomLock = new();
    private Random _random;

    // Deterministic noise for the one-shot read path, indexed by PID position in _pids. Derived once
    // from (seed, pidIndex) so reads are reproducible per seed and independent of stream timing.
    private readonly double[] _readNoiseUnits;

    private volatile bool _connected;
    private long _tick;              // simulation sample counter; stream time seconds = _tick * SamplePeriodSeconds
    private bool _cleared;           // set by ClearDTCsAsync (active/pending codes suppressed)

    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;

    /// <summary>Creates a simulator for one protocol.</summary>
    public SimulatedDiagnosticAdapter(ProtocolType protocol, int seed)
    {
        _protocol = protocol switch
        {
            ProtocolType.J1939 or ProtocolType.J1708 or ProtocolType.OBD2 => protocol,
            _ => throw new ArgumentOutOfRangeException(
                nameof(protocol), protocol, "Only J1939, J1708/J1587, and OBD II are simulated."),
        };
        _seed = seed;
        _random = new Random(seed);
        _pids = BuildPids(protocol);
        _readNoiseUnits = BuildReadNoiseUnits(seed, _pids.Count);
        Name = protocol switch
        {
            ProtocolType.J1939 => "Simulator (J1939)",
            ProtocolType.J1708 => "Simulator (J1708/J1587)",
            _ => "Simulator (OBD II J1979)",
        };
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool IsConnected => _connected;

    /// <inheritdoc />
    public bool IsSimulated => true;

    /// <inheritdoc />
    public IReadOnlyList<ProtocolType> SupportedProtocols => SimulatedProtocols;

    /// <summary>The protocol this instance currently simulates.</summary>
    public ProtocolType Protocol => _protocol;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_connected)
            return;

        // Simulated negotiation latency; a real adapter would open a channel + select protocol here.
        await Task.Delay(50, cancellationToken).ConfigureAwait(false);

        // Reset simulation state so reconnecting replays the same deterministic sequence.
        lock (_randomLock)
            _random = new Random(_seed);
        Interlocked.Exchange(ref _tick, 0);
        _cleared = false;
        _connected = true;

        _streamCts = new CancellationTokenSource();
        _streamTask = Task.Run(() => StreamLoopAsync(_streamCts.Token));

        Debug.WriteLine($"[{Name}] Connected (SIMULATED).");
    }

    /// <inheritdoc />
    public async Task DisconnectAsync()
    {
        if (!_connected)
            return;

        _connected = false;
        _streamCts?.Cancel();

        if (_streamTask is not null)
        {
            try { await _streamTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        _streamCts?.Dispose();
        _streamCts = null;
        _streamTask = null;

        Debug.WriteLine($"[{Name}] Disconnected (SIMULATED).");
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DtcReadout>> ReadDTCsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dtcs = BuildDtcList();
        if (_cleared)
            dtcs = dtcs.Where(d => d.Status == DtcStatus.Stored).ToList();
        return Task.FromResult<IReadOnlyList<DtcReadout>>(dtcs);
    }

    /// <inheritdoc />
    public Task ClearDTCsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Simulated: real clear requires UI confirmation (enforced by the caller) and is logged
        // on the host in Phase 2. Here we just drop active/pending, leaving stored history.
        _cleared = true;
        Debug.WriteLine($"[{Name}] Clear DTCs requested (SIMULATED — active/pending cleared, stored retained).");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PidValue>> ReadLiveDataAsync(IReadOnlyList<string> pidNames, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var wanted = BuildNameFilter(pidNames);
        var values = new List<PidValue>();
        for (int i = 0; i < _pids.Count; i++)
        {
            var pid = _pids[i];
            if (wanted is not null && !wanted.Contains(pid.Name))
                continue;
            values.Add(BuildReadPidValue(pid, i));
        }
        return Task.FromResult<IReadOnlyList<PidValue>>(values);
    }

    /// <inheritdoc />
    public Task<FreezeFrameData?> ReadFreezeFrameAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var names = FreezeFrameNames;
        var pids = new List<PidValue>();
        for (int i = 0; i < _pids.Count; i++)
        {
            var pid = _pids[i];
            if (!names.Contains(pid.Name))
                continue;
            pids.Add(BuildReadPidValue(pid, i));
        }
        return Task.FromResult<FreezeFrameData?>(new FreezeFrameData { CapturedAt = Clock.UtcNow, Pids = pids });
    }

    /// <inheritdoc />
    public Task<VehicleInfo> GetVehicleInfoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new VehicleInfo
        {
            Vin = "SIMULATED00000001", // clearly fake; 17 chars for display only
            Protocol = _protocol,
            Ecus = BuildEcus(),
        });
    }

    /// <inheritdoc />
    public event EventHandler<LiveDataUpdateEventArgs>? LiveDataReceived;

    private static HashSet<string>? BuildNameFilter(IReadOnlyList<string> pidNames)
    {
        if (pidNames is null || pidNames.Count == 0)
            return null; // null => the adapter's full default set

        return pidNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task StreamLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(SamplePeriodSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!_connected)
                    break;

                var t = Interlocked.Read(ref _tick) * SamplePeriodSeconds;
                foreach (var pid in _pids)
                    RaiseLiveData(BuildPidValue(pid, t));

                Interlocked.Increment(ref _tick);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on disconnect.
        }
    }

    private void RaiseLiveData(PidValue value)
    {
        var handler = LiveDataReceived;
        if (handler is null)
            return;

        foreach (EventHandler<LiveDataUpdateEventArgs> subscriber in handler.GetInvocationList())
        {
            try { subscriber(this, new LiveDataUpdateEventArgs(value)); }
            catch (Exception ex)
            {
                // A misbehaving UI subscriber must not kill the stream.
                Debug.WriteLine($"[{Name}] LiveData subscriber threw: {ex.Message}");
            }
        }
    }

    private PidValue BuildPidValue(SimPid pid, double t)
    {
        // Stream path: pull the next noise sample from the shared stream RNG (under the lock).
        double noiseUnit = 0;
        if (pid.NoiseAmplitude > 0)
        {
            lock (_randomLock)
                noiseUnit = _random.NextDouble();
        }

        return BuildPidValue(pid, t, noiseUnit);
    }

    /// <summary>
    /// One-shot read path: value at a fixed sample time with noise derived deterministically from
    /// <c>(seed, pidIndex)</c>. Deliberately does not consult the streaming tick counter or the
    /// stream RNG, so a read is reproducible for a given seed no matter how many stream ticks have
    /// elapsed (or whether streaming is running at all).
    /// </summary>
    private PidValue BuildReadPidValue(SimPid pid, int pidIndex) =>
        BuildPidValue(pid, ReadSampleTimeSeconds, _readNoiseUnits[pidIndex]);

    private static PidValue BuildPidValue(SimPid pid, double t, double noiseUnit)
    {
        var raw = ComputeValue(pid, t, noiseUnit);
        var value = Math.Round(raw, pid.Decimals, MidpointRounding.AwayFromZero);
        return new PidValue { Name = pid.Name, Value = value, Unit = pid.Unit, Timestamp = Clock.UtcNow, IsSimulated = true };
    }

    private static double ComputeValue(SimPid pid, double t, double noiseUnit)
    {
        double v = pid.Wave switch
        {
            SimWave.Ramp => pid.Base + pid.Amplitude * t,
            SimWave.Constant => pid.Base,
            _ => pid.Base + pid.Amplitude * Math.Sin(2 * Math.PI * t / pid.PeriodSeconds + pid.PhaseRadians),
        };

        if (pid.NoiseAmplitude > 0)
            v += (noiseUnit - 0.5) * pid.NoiseAmplitude;

        if (pid.Min is double min) v = Math.Max(min, v);
        if (pid.Max is double max) v = Math.Min(max, v);
        return v;
    }

    private static double[] BuildReadNoiseUnits(int seed, int count)
    {
        var rng = new Random(seed);
        var units = new double[count];
        for (int i = 0; i < count; i++)
            units[i] = rng.NextDouble();
        return units;
    }

    private IReadOnlyList<DtcReadout> BuildDtcList() => (_protocol switch
    {
        ProtocolType.J1939 => J1939Dtcs,
        ProtocolType.J1708 => J1708Dtcs,
        _ => Obd2Dtcs,
    }).Select(d => d with
    {
        // Regenerate the J1939 code string from its structured SPN/FMI fields via the same canonical
        // formatter the real J1939 decoders use, so simulated and real DTCs share one code format
        // (data/dtc/README.md §1). J1708/OBD2 literals below are already written in canonical form.
        Code = d.Protocol == ProtocolType.J1939 && d.Spn is int spn && d.Fmi is int fmi
            ? DtcCodes.J1939(spn, fmi)
            : d.Code,
        IsSimulated = true,
    }).ToList();

    private IReadOnlyList<EcuInfo> BuildEcus() => _protocol switch
    {
        ProtocolType.J1939 => new[]
        {
            new EcuInfo { Id = "SA 0x00", Description = "Engine #1 (SIMULATED)" },
            new EcuInfo { Id = "SA 0x03", Description = "Transmission #1 (SIMULATED)" },
        },
        ProtocolType.J1708 => new[]
        {
            new EcuInfo { Id = "MID 128", Description = "Engine (SIMULATED)" },
            new EcuInfo { Id = "MID 130", Description = "Transmission (SIMULATED)" },
        },
        _ => new[]
        {
            new EcuInfo { Id = "ECM", Description = "Engine Control Module (SIMULATED)" },
            new EcuInfo { Id = "TCM", Description = "Transmission Control Module (SIMULATED)" },
        },
    };

    private static readonly string[] FreezeFrameNames =
    {
        "Engine Speed", "Engine RPM", "Coolant Temperature", "Vehicle Speed",
        "Boost Pressure", "Turbo Boost Pressure", "Intake Manifold Pressure",
    };

    // ---- SIMULATED DTC sets (red = Active, blue = Pending, gray = Stored). Standard code
    // identifiers used purely as demo data; descriptions are illustrative, not spec values. ----

    private static readonly IReadOnlyList<DtcReadout> J1939Dtcs = new[]
    {
        new DtcReadout { Code = "SPN 94 FMI 3", Protocol = ProtocolType.J1939, Spn = 94, Fmi = 3, Status = DtcStatus.Active, Description = "Fuel Delivery Pressure: Voltage Above Normal (SIMULATED)", Severity = "High" },
        new DtcReadout { Code = "SPN 110 FMI 0", Protocol = ProtocolType.J1939, Spn = 110, Fmi = 0, Status = DtcStatus.Pending, Description = "Engine Coolant Temperature: High (SIMULATED)", Severity = "Medium" },
        new DtcReadout { Code = "SPN 175 FMI 4", Protocol = ProtocolType.J1939, Spn = 175, Fmi = 4, Status = DtcStatus.Stored, Description = "Engine Oil Temperature: Voltage Below Normal (SIMULATED)", Severity = "Low" },
        new DtcReadout { Code = "SPN 639 FMI 9", Protocol = ProtocolType.J1939, Spn = 639, Fmi = 9, Status = DtcStatus.Active, Description = "J1939 Network #1: Abnormal Update Rate (SIMULATED)", Severity = "High" },
        // Proprietary PCS TCM SPN (code 72, PCS 4LHD Technician's Guide v2.0 p.50). FMI 31 is a demo choice; the
        // guide gives no FMI, so the app matches this row to data/dtc/pcs.json by SPN only.
        new DtcReadout { Code = "SPN 522742 FMI 31", Protocol = ProtocolType.J1939, Spn = 522742, Fmi = 31, Status = DtcStatus.Stored, Description = "Transmission #1 proprietary SPN (SIMULATED)" },
    };

    private static readonly IReadOnlyList<DtcReadout> J1708Dtcs = new[]
    {
        new DtcReadout { Code = "MID 128 PID 94 FMI 3", Protocol = ProtocolType.J1708, Status = DtcStatus.Active, Description = "Fuel Pressure Sensor: Voltage Above Normal (SIMULATED)", Severity = "High" },
        new DtcReadout { Code = "MID 128 PID 110 FMI 0", Protocol = ProtocolType.J1708, Status = DtcStatus.Pending, Description = "Coolant Temperature: High (SIMULATED)", Severity = "Medium" },
        new DtcReadout { Code = "MID 136 PID 84 FMI 9", Protocol = ProtocolType.J1708, Status = DtcStatus.Stored, Description = "Road Speed: Abnormal Update (SIMULATED)", Severity = "Low" },
    };

    private static readonly IReadOnlyList<DtcReadout> Obd2Dtcs = new[]
    {
        new DtcReadout { Code = "P0230", Protocol = ProtocolType.OBD2, Status = DtcStatus.Active, Description = "Fuel Pump Primary Circuit Malfunction (SIMULATED)", Severity = "High" },
        new DtcReadout { Code = "P0118", Protocol = ProtocolType.OBD2, Status = DtcStatus.Pending, Description = "Engine Coolant Temperature Circuit High (SIMULATED)", Severity = "Medium" },
        new DtcReadout { Code = "P0442", Protocol = ProtocolType.OBD2, Status = DtcStatus.Stored, Description = "Evaporative Emission System Leak (Small) (SIMULATED)", Severity = "Low" },
        new DtcReadout { Code = "P0300", Protocol = ProtocolType.OBD2, Status = DtcStatus.Active, Description = "Random/Multiple Cylinder Misfire Detected (SIMULATED)", Severity = "High" },
    };

    // ---- SIMULATED PID sets. Values are slow sine/ramp + seeded noise, in canonical SI units. ----

    private static IReadOnlyList<SimPid> BuildPids(ProtocolType protocol) => protocol switch
    {
        ProtocolType.J1939 => J1939Pids,
        ProtocolType.J1708 => J1708Pids,
        _ => Obd2Pids,
    };

    private static readonly IReadOnlyList<SimPid> J1939Pids = new[]
    {
        new SimPid { Name = "Engine Speed",           Unit = "rpm",  Base = 850,     Amplitude = 140,    PeriodSeconds = 6,   NoiseAmplitude = 8,    Decimals = 0, Min = 600,   Max = 2400 },
        new SimPid { Name = "Coolant Temperature",    Unit = UnitConverter.Celsius, Base = 86, Amplitude = 4,  PeriodSeconds = 40,  NoiseAmplitude = 0.5,  Decimals = 1, Min = 70,    Max = 95 },
        new SimPid { Name = "Battery Voltage",        Unit = UnitConverter.Volt,    Base = 13.9, Amplitude = 0.25, PeriodSeconds = 9, NoiseAmplitude = 0.05, Decimals = 2, Min = 13.2,  Max = 14.6 },
        new SimPid { Name = "Boost Pressure",         Unit = UnitConverter.Pascal,  Base = 115_000, Amplitude = 25_000, PeriodSeconds = 5, NoiseAmplitude = 1500, Decimals = 0, Min = 95_000, Max = 180_000 },
        new SimPid { Name = "Vehicle Speed",          Unit = "m/s",  Base = 7,       Amplitude = 6,     PeriodSeconds = 20,  NoiseAmplitude = 0.3,  Decimals = 1, Min = 0,     Max = 30 },
        new SimPid { Name = "Fuel Rate",              Unit = UnitConverter.LiterPerMinute, Base = 0.06, Amplitude = 0.04, PeriodSeconds = 8, NoiseAmplitude = 0.005, Decimals = 3, Min = 0, Max = 0.3 },
        new SimPid { Name = "Engine Oil Pressure",    Unit = UnitConverter.Pascal,  Base = 420_000, Amplitude = 50_000, PeriodSeconds = 11, NoiseAmplitude = 8000, Decimals = 0, Min = 200_000, Max = 650_000 },
        new SimPid { Name = "Intake Manifold Temp",   Unit = UnitConverter.Celsius, Base = 48, Amplitude = 9,  PeriodSeconds = 30,  NoiseAmplitude = 0.6,  Decimals = 1, Min = 30,    Max = 75 },
        new SimPid { Name = "Engine Hours",           Unit = UnitConverter.Hour,    Base = 12_450, Amplitude = 1.0 / 3600.0, PeriodSeconds = 0, NoiseAmplitude = 0, Decimals = 2, Min = 0, Max = null, Wave = SimWave.Ramp },
    };

    private static readonly IReadOnlyList<SimPid> J1708Pids = new[]
    {
        new SimPid { Name = "Engine RPM",             Unit = "rpm",  Base = 800,     Amplitude = 150,    PeriodSeconds = 6,   NoiseAmplitude = 8,    Decimals = 0, Min = 600,   Max = 2300 },
        new SimPid { Name = "Coolant Temperature",    Unit = UnitConverter.Celsius, Base = 84, Amplitude = 4.5, PeriodSeconds = 38, NoiseAmplitude = 0.5,  Decimals = 1, Min = 68,    Max = 96 },
        new SimPid { Name = "Battery Voltage",        Unit = UnitConverter.Volt,    Base = 13.7, Amplitude = 0.3,  PeriodSeconds = 8,  NoiseAmplitude = 0.06, Decimals = 2, Min = 12.8,  Max = 14.5 },
        new SimPid { Name = "Turbo Boost Pressure",   Unit = UnitConverter.Pascal,  Base = 112_000, Amplitude = 26_000, PeriodSeconds = 5, NoiseAmplitude = 1600, Decimals = 0, Min = 90_000, Max = 190_000 },
        new SimPid { Name = "Vehicle Speed",          Unit = "m/s",  Base = 8,       Amplitude = 7,     PeriodSeconds = 22,  NoiseAmplitude = 0.3,  Decimals = 1, Min = 0,     Max = 32 },
        new SimPid { Name = "Fuel Rate",              Unit = UnitConverter.LiterPerMinute, Base = 0.07, Amplitude = 0.045, PeriodSeconds = 8, NoiseAmplitude = 0.006, Decimals = 3, Min = 0, Max = 0.35 },
        new SimPid { Name = "Engine Oil Pressure",    Unit = UnitConverter.Pascal,  Base = 400_000, Amplitude = 48_000, PeriodSeconds = 12, NoiseAmplitude = 9000, Decimals = 0, Min = 180_000, Max = 620_000 },
        new SimPid { Name = "Percent Engine Load",    Unit = "%",    Base = 40,      Amplitude = 20,    PeriodSeconds = 14,  NoiseAmplitude = 2,    Decimals = 0, Min = 0,     Max = 100 },
    };

    private static readonly IReadOnlyList<SimPid> Obd2Pids = new[]
    {
        new SimPid { Name = "Engine RPM",             Unit = "rpm",  Base = 820,     Amplitude = 130,    PeriodSeconds = 7,   NoiseAmplitude = 8,    Decimals = 0, Min = 650,   Max = 2500 },
        new SimPid { Name = "Coolant Temperature",    Unit = UnitConverter.Celsius, Base = 88, Amplitude = 3.5, PeriodSeconds = 35, NoiseAmplitude = 0.4,  Decimals = 1, Min = 70,    Max = 99 },
        new SimPid { Name = "Battery Voltage",        Unit = UnitConverter.Volt,    Base = 13.8, Amplitude = 0.2,  PeriodSeconds = 10, NoiseAmplitude = 0.04, Decimals = 2, Min = 13.0,  Max = 14.7 },
        new SimPid { Name = "Intake Manifold Pressure", Unit = UnitConverter.Pascal, Base = 98_000, Amplitude = 22_000, PeriodSeconds = 5, NoiseAmplitude = 1200, Decimals = 0, Min = 60_000, Max = 160_000 },
        new SimPid { Name = "Vehicle Speed",          Unit = "m/s",  Base = 6,       Amplitude = 5.5,   PeriodSeconds = 18,  NoiseAmplitude = 0.25, Decimals = 1, Min = 0,     Max = 28 },
        new SimPid { Name = "Fuel Rate",              Unit = UnitConverter.LiterPerMinute, Base = 0.055, Amplitude = 0.035, PeriodSeconds = 9, NoiseAmplitude = 0.004, Decimals = 3, Min = 0, Max = 0.25 },
        new SimPid { Name = "Throttle Position",      Unit = "%",    Base = 22,      Amplitude = 14,    PeriodSeconds = 6,   NoiseAmplitude = 1,    Decimals = 0, Min = 0,     Max = 100 },
        new SimPid { Name = "Engine Load",            Unit = "%",    Base = 35,      Amplitude = 18,    PeriodSeconds = 13,  NoiseAmplitude = 1.5,  Decimals = 0, Min = 0,     Max = 100 },
    };

    private enum SimWave { Sine, Ramp, Constant }

    private sealed record SimPid
    {
        public required string Name { get; init; }
        public required string Unit { get; init; }
        public double Base { get; init; }
        public double Amplitude { get; init; }
        public double PeriodSeconds { get; init; } = 10;
        public double PhaseRadians { get; init; }
        public double NoiseAmplitude { get; init; }
        public int Decimals { get; init; } = 2;
        public SimWave Wave { get; init; } = SimWave.Sine;
        public double? Min { get; init; }
        public double? Max { get; init; }
    }
}
