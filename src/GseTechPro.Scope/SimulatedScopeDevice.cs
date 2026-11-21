using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;

namespace GseTechPro.Scope;

/// <summary>
/// Deterministic, seeded oscilloscope simulator implementing <see cref="IScopeDevice"/> (ADR-011).
/// Produces synthetic waveforms for the six Phase-6 automotive guided tests plus general Sine and
/// Square waves. Every capture and measurement produced by this class carries
/// <c>IsSimulated = true</c> provenance.
/// </summary>
public sealed class SimulatedScopeDevice : IScopeDevice
{
    public const string DefaultDeviceId = "sim-1";
    public const string DefaultDeviceName = "Simulator";
    public const int DefaultChannelCount = 4;
    public const int DefaultSeed = 12345;

    private readonly object _sync = new();
    private readonly Dictionary<int, ScopeChannelConfig> _channelConfigs = new();

    private SimulatedSignalPreset _preset;
    private bool _presetExplicitlySet;
    private volatile bool _isConnected;
    private volatile bool _isCapturing;
    private ScopeCapture? _lastCapture;
    private bool _lastCaptureTriggered = true;

    public SimulatedScopeDevice(
        int seed = DefaultSeed,
        SimulatedSignalPreset defaultPreset = SimulatedSignalPreset.Sine)
    {
        Seed = seed;
        _preset = defaultPreset;

        for (int ch = 1; ch <= DefaultChannelCount; ch++)
        {
            _channelConfigs[ch] = new ScopeChannelConfig
            {
                Channel = ch,
                VoltageRangeVolts = 5.0,
                Coupling = ScopeCoupling.Dc,
                ProbeMultiplier = 1.0,
                CurrentScaleAmpsPerVolt = 0.0,
            };
        }
    }

    /// <summary>Deterministic seed used for reproducible sample generation.</summary>
    public int Seed { get; }

    /// <summary>The active signal preset.</summary>
    public SimulatedSignalPreset CurrentPreset
    {
        get { lock (_sync) return _preset; }
    }

    /// <inheritdoc />
    public string Name => DefaultDeviceName;

    /// <inheritdoc />
    public ScopeDeviceKind Kind => ScopeDeviceKind.Simulator;

    /// <inheritdoc />
    public bool IsSimulated => true;

    /// <inheritdoc />
    public bool IsConnected => _isConnected;

    /// <summary>True while a capture operation is actively running.</summary>
    public bool IsCapturing => _isCapturing;

    /// <inheritdoc />
    public int ChannelCount => DefaultChannelCount;

    /// <summary>
    /// Selects a named simulator signal preset (e.g. <see cref="SimulatedSignalPreset.CrankSensor"/>)
    /// and applies its canonical channel configuration.
    /// </summary>
    public void ConfigurePreset(SimulatedSignalPreset preset)
    {
        var canonical = SimulatedScopePresets.GetCanonicalRequest(preset);
        lock (_sync)
        {
            _preset = preset;
            _presetExplicitlySet = true;
            foreach (var cfg in canonical.Channels)
            {
                _channelConfigs[cfg.Channel] = cfg;
            }
        }
    }

    /// <summary>
    /// Resolves a guided test name or id (e.g. "Crank sensor (CKP)") and configures the matching preset.
    /// </summary>
    public bool TryConfigurePreset(string? testNameOrId)
    {
        if (!SimulatedScopePresets.TryResolvePreset(testNameOrId, out var preset))
            return false;

        ConfigurePreset(preset);
        return true;
    }

    /// <summary>
    /// Clears an explicit preset lock so subsequent captures can auto-select a preset from the
    /// <see cref="ScopeCaptureRequest"/> or fall back to <paramref name="fallback"/>.
    /// </summary>
    public void ClearPresetOverride(SimulatedSignalPreset fallback = SimulatedSignalPreset.Sine)
    {
        lock (_sync)
        {
            _preset = fallback;
            _presetExplicitlySet = false;
        }
    }

    /// <summary>
    /// Returns the canonical <see cref="ScopeCaptureRequest"/> for the specified (or current) preset.
    /// </summary>
    public ScopeCaptureRequest GetPresetCaptureRequest(SimulatedSignalPreset? preset = null) =>
        SimulatedScopePresets.GetCanonicalRequest(preset ?? CurrentPreset);

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _isConnected = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisconnectAsync()
    {
        _isConnected = false;
        _isCapturing = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ConfigureChannelAsync(
        int channel,
        ScopeChannelConfig config,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(config);
        ValidateChannelIndex(channel);

        lock (_sync)
        {
            _channelConfigs[channel] = config with { Channel = channel };
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StartCaptureAsync(
        ScopeCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        if (!_isConnected)
            throw new InvalidOperationException("Simulator scope is not connected. Call ConnectAsync first.");
        if (request.Channels is null || request.Channels.Count == 0)
            throw new ArgumentException("At least one channel must be enabled in ScopeCaptureRequest.", nameof(request));
        if (request.SampleRateHz < 1)
            throw new ArgumentOutOfRangeException(nameof(request), "SampleRateHz must be >= 1.");
        if (!double.IsFinite(request.TimebaseSecondsPerDivision) || request.TimebaseSecondsPerDivision <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(request), "TimebaseSecondsPerDivision must be a finite positive value.");

        foreach (var ch in request.Channels)
        {
            ValidateChannelIndex(ch.Channel);
        }

        int derivedSampleCount = request.SampleCount ?? (int)Math.Round(
            request.TimebaseSecondsPerDivision * 10.0 * request.SampleRateHz,
            MidpointRounding.AwayFromZero);

        int maxPerChannel = WaveformFile.MaxTotalSamples / Math.Max(1, request.Channels.Count);
        int sampleCount = Math.Clamp(derivedSampleCount, 2, maxPerChannel);

        SimulatedSignalPreset activePreset;
        lock (_sync)
        {
            foreach (var ch in request.Channels)
            {
                _channelConfigs[ch.Channel] = ch;
            }

            if (_presetExplicitlySet)
            {
                activePreset = _preset;
            }
            else
            {
                activePreset = SimulatedScopePresets.TryMatchFromCaptureRequest(request) ?? _preset;
            }
        }

        _isCapturing = true;

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var channels = new Dictionary<int, double[]>(request.Channels.Count);
            foreach (var chConfig in request.Channels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                channels[chConfig.Channel] = GenerateChannelSamples(
                    activePreset,
                    chConfig,
                    sampleCount,
                    request.SampleRateHz,
                    Seed);
            }

            bool triggered = EvaluateAndAlignTrigger(request, channels, sampleCount, request.SampleRateHz);

            var capture = new ScopeCapture
            {
                SampleCount = sampleCount,
                SampleRateHz = request.SampleRateHz,
                TimebaseSecondsPerDivision = request.TimebaseSecondsPerDivision,
                CapturedAt = Clock.UtcNow,
                IsSimulated = true,
                Channels = channels,
            };

            lock (_sync)
            {
                _lastCapture = capture;
                _lastCaptureTriggered = triggered;
                _isCapturing = false;
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task StopCaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _isCapturing = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ScopeCapture> ReadCaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_lastCapture is null)
                throw new InvalidOperationException("No capture is available. Call StartCaptureAsync first.");
            return Task.FromResult(_lastCapture);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ScopeMeasurement>> ComputeMeasurementsAsync(
        int channel,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateChannelIndex(channel);

        ScopeCapture capture;
        ScopeChannelConfig config;
        bool triggered;

        lock (_sync)
        {
            if (_lastCapture is null)
                throw new InvalidOperationException("No capture is available. Call StartCaptureAsync first.");
            capture = _lastCapture;
            triggered = _lastCaptureTriggered;
            config = _channelConfigs.TryGetValue(channel, out var c)
                ? c
                : new ScopeChannelConfig { Channel = channel, VoltageRangeVolts = 5.0 };
        }

        if (!capture.Channels.TryGetValue(channel, out var samples))
            throw new InvalidOperationException($"Channel {channel} was not part of the last capture.");

        return Task.Run<IReadOnlyList<ScopeMeasurement>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return WaveformMath.ComputeMeasurements(
                samples,
                capture.SampleRateHz,
                config,
                channel,
                isSimulated: true,
                noTrigger: !triggered,
                outOfRange: false);
        }, cancellationToken);
    }

    private void ValidateChannelIndex(int channel)
    {
        if (channel < 1 || channel > DefaultChannelCount)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"Channel must be between 1 and {DefaultChannelCount}.");
    }

    private static double[] GenerateChannelSamples(
        SimulatedSignalPreset preset,
        ScopeChannelConfig config,
        int sampleCount,
        int sampleRateHz,
        int seed)
    {
        int combinedSeed = HashCode.Combine(seed, (int)preset, config.Channel);
        var rng = new Random(combinedSeed);

        double[] samples = new double[sampleCount];
        double dt = 1.0 / sampleRateHz;
        double probeScale = double.IsFinite(config.ProbeMultiplier) && config.ProbeMultiplier > 0.0
            ? config.ProbeMultiplier
            : 1.0;

        for (int i = 0; i < sampleCount; i++)
        {
            double t = i * dt;
            double noise = (rng.NextDouble() - 0.5) * 2.0; // [-1, 1]
            double baseVolts = preset switch
            {
                SimulatedSignalPreset.CanBusFrames => SampleCanFrames(t, config.Channel, noise),
                SimulatedSignalPreset.BatteryCranking => SampleBatteryCranking(t, config, noise),
                _ => SamplePresetAtTime(preset, t, noise),
            };
            samples[i] = baseVolts * probeScale;
        }

        // AC coupling removes the DC mean component
        if (config.Coupling == ScopeCoupling.Ac && sampleCount > 0)
        {
            double sum = 0.0;
            for (int i = 0; i < sampleCount; i++)
                sum += samples[i];
            double mean = sum / sampleCount;
            for (int i = 0; i < sampleCount; i++)
                samples[i] -= mean;
        }

        return samples;
    }

    private static double SamplePresetAtTime(SimulatedSignalPreset preset, double t, double unitNoise) =>
        preset switch
        {
            SimulatedSignalPreset.CrankSensor => SampleCrankSensor(t, unitNoise),
            SimulatedSignalPreset.InjectorCurrent => SampleInjectorCurrent(t, unitNoise),
            SimulatedSignalPreset.AlternatorRipple => SampleAlternatorRipple(t, unitNoise),
            SimulatedSignalPreset.StarterCurrent => SampleStarterCurrent(t, unitNoise),
            SimulatedSignalPreset.CanSignalQuality => SampleCanSignal(t, unitNoise),
            SimulatedSignalPreset.SolenoidCurrent => SampleSolenoidCurrent(t, unitNoise),
            SimulatedSignalPreset.Square => SampleSquare(t),
            _ => SampleSine(t),
        };

    /// <summary>
    /// Crank sensor (CKP): 60-2 pulse train, ~2.5 Vpp (0.25 V .. 2.75 V), 200 Hz fundamental tooth rate.
    /// For full-revolution captures (>= 60 teeth), teeth 58 and 59 are omitted for the 60-2 reference gap.
    /// </summary>
    private static double SampleCrankSensor(double t, double unitNoise)
    {
        const double toothFreqHz = 200.0;
        const double vLow = 0.25;
        const double vHigh = 2.75;

        double toothPos = t * toothFreqHz;
        long toothIndex = (long)Math.Floor(toothPos);
        double phase = toothPos - toothIndex; // [0, 1)

        // 60-2 missing tooth gap on full revolutions beyond the first 12 teeth
        long slotInRev = ((toothIndex % 60) + 60) % 60;
        if (toothIndex >= 12 && (slotInRev == 58 || slotInRev == 59))
        {
            return vLow + unitNoise * 0.002;
        }

        // Clean trapezoidal tooth pulse (10% rise, 40% high dwell, 10% fall, 40% low dwell)
        double level;
        if (phase < 0.10)
            level = vLow + (vHigh - vLow) * (phase / 0.10);
        else if (phase < 0.50)
            level = vHigh;
        else if (phase < 0.60)
            level = vHigh - (vHigh - vLow) * ((phase - 0.50) / 0.10);
        else
            level = vLow;

        return Math.Clamp(level + unitNoise * 0.001, vLow, vHigh);
    }

    /// <summary>
    /// Injector current: peak-and-hold current pulse repeating at 250 Hz (4 ms period).
    /// Ramps to 1.0 V peak (10 A @ 10 A/V clamp), drops to 0.35 V hold (3.5 A), returns to 0.0 V.
    /// </summary>
    private static double SampleInjectorCurrent(double t, double unitNoise)
    {
        const double period = 0.004; // 4 ms (250 Hz)
        double phase = (t % period) / period;

        double v;
        if (phase < 0.05)
        {
            v = 0.0;
        }
        else if (phase < 0.15)
        {
            // Inductive ramp to 1.0 V peak (10 A)
            double u = (phase - 0.05) / 0.10;
            v = 1.0 * u;
        }
        else if (phase < 0.18)
        {
            // Peak dwell at 1.0 V
            v = 1.0;
        }
        else if (phase < 0.20)
        {
            // Transition from peak (1.0 V) to hold (0.35 V)
            double u = (phase - 0.18) / 0.02;
            v = 1.0 - 0.65 * u;
        }
        else if (phase < 0.45)
        {
            // Regulated hold plateau at 0.35 V (below vMid = 0.5 V so each pulse has 1 mid crossing)
            v = 0.35;
        }
        else if (phase < 0.48)
        {
            // Turn-off decay to 0.0 V
            double u = (phase - 0.45) / 0.03;
            v = 0.35 * (1.0 - u);
        }
        else
        {
            v = 0.0;
        }

        return Math.Clamp(v + unitNoise * 0.0005, 0.0, 1.0);
    }

    /// <summary>
    /// Alternator ripple: 300 Hz AC ripple, 0.20 Vpp (-0.10 V .. +0.10 V after AC coupling, riding on 14.2 V DC).
    /// </summary>
    private static double SampleAlternatorRipple(double t, double unitNoise)
    {
        const double dcBus = 14.2;
        const double rippleFreqHz = 300.0;
        const double amplitude = 0.10; // 0.20 Vpp

        double ac = amplitude * Math.Sin(2.0 * Math.PI * rippleFreqHz * t);
        return dcBus + ac + unitNoise * 0.0002;
    }

    /// <summary>
    /// Starter current: starter inrush peaking at 3.0 V (300 A @ 100 A/V clamp), decaying into
    /// cyclic compression pulses (oscillating across vMid = 1.5 V at 10 Hz).
    /// </summary>
    private static double SampleStarterCurrent(double t, double unitNoise)
    {
        if (t < 0.010)
        {
            return 0.0;
        }
        if (t < 0.030)
        {
            // Rapid inrush rise from 0.0 V to 3.0 V peak (300 A)
            double u = (t - 0.010) / 0.020;
            return 3.0 * u;
        }
        if (t < 0.045)
        {
            // Decay from 3.0 V inrush peak into cranking envelope
            double u = (t - 0.030) / 0.015;
            return 3.0 - 0.8 * u;
        }

        // Steady cranking compression pulses centered at 1.5 V (0.8 V .. 2.2 V) at 10 Hz
        const double compressionFreqHz = 10.0;
        double crankWave = 1.5 + 0.7 * Math.Sin(2.0 * Math.PI * compressionFreqHz * (t - 0.045));
        return Math.Clamp(crankWave + unitNoise * 0.001, 0.0, 3.0);
    }

    /// <summary>
    /// CAN signal quality: CAN frame switching between recessive (~2.55 V) and dominant (~0.05 V),
    /// 2.50 Vpp, with 125 kHz alternating bit pattern (250 kbps CAN bit rate) and clean trapezoidal edges.
    /// </summary>
    private static double SampleCanSignal(double t, double unitNoise)
    {
        const double bitPeriod = 4e-6; // 4 µs bit time (250 kbps)
        const double fullCycle = 2.0 * bitPeriod; // 8 µs period = 125 kHz
        const double vDominant = 0.05;
        const double vRecessive = 2.55;

        double phase = (t % fullCycle) / fullCycle;
        double v;
        if (phase < 0.05)
            v = vDominant + (vRecessive - vDominant) * (phase / 0.05);
        else if (phase < 0.50)
            v = vRecessive;
        else if (phase < 0.55)
            v = vRecessive - (vRecessive - vDominant) * ((phase - 0.50) / 0.05);
        else
            v = vDominant;

        return Math.Clamp(v + unitNoise * 0.0005, vDominant, vRecessive);
    }

    /// <summary>
    /// Simulated J1939 bus traffic (values are made up for the simulator, not real ECU data): EEC1 from SA 0x00, CCVS
    /// and ET1, each followed by 24 recessive bit times, repeating. Bits come from <see cref="CanSerialDecoder.EncodeFrame"/>
    /// so the stuffing and CRC are real. CAN-H (CH1) and CAN-L (CH2) at ISO 11898-2 nominal levels with short edges;
    /// other channels idle at 2.5 V.
    /// </summary>
    private static double SampleCanFrames(double t, int channel, double unitNoise)
    {
        const double bitTime = 4e-6; // 250 kbit/s
        var bits = SimulatedCanBits.Value;
        double pos = t / bitTime;
        int k = (int)pos % bits.Length;
        double frac = pos - Math.Floor(pos);
        bool dominant = !bits[k];
        bool prevDominant = !bits[(k + bits.Length - 1) % bits.Length];
        // 5 % of a bit for the edge (0.2 µs).
        double d = dominant == prevDominant ? (dominant ? 1 : 0)
            : frac < 0.05 ? (dominant ? frac / 0.05 : 1 - frac / 0.05) : (dominant ? 1 : 0);
        double v = channel switch
        {
            1 => 2.5 + 1.0 * d,
            2 => 2.5 - 1.0 * d,
            _ => 2.5,
        };
        return v + unitNoise * 0.01;
    }

    private static readonly Lazy<bool[]> SimulatedCanBits = new(() =>
    {
        var all = new List<bool>();
        void Idle(int n) { for (int i = 0; i < n; i++) all.Add(true); }
        Idle(24);
        all.AddRange(CanSerialDecoder.EncodeFrame(0x0CF00400, true, new byte[] { 0xF0, 0x7D, 0x7D, 0xE0, 0x2E, 0x00, 0xF0, 0x7D }));
        Idle(24);
        all.AddRange(CanSerialDecoder.EncodeFrame(0x18FEF100, true, new byte[] { 0xF7, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF }));
        Idle(24);
        all.AddRange(CanSerialDecoder.EncodeFrame(0x18FEEE00, true, new byte[] { 0x7D, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }));
        return all.ToArray();
    });

    /// <summary>
    /// Solenoid current: solenoid current ramp + hold repeating at 40 Hz (25 ms period),
    /// peaking at 1.50 V (15 A @ 10 A/V clamp).
    /// </summary>
    /// <summary>
    /// Battery / cranking (demo values, not specs): CH1 battery volts, any other channel the starter-cable
    /// current as clamp output volts (amps ÷ the channel's A/V scale; 1 mV/A when none is set).
    /// 0–0.8 s rest at 12.6 V; 0.8 s starter inrush (dip to about 8.4 V, about 620 A); cranking to 2.3 s
    /// at about 10.1 V / 210 A with a 6.7 Hz compression ripple; then running at about 14.2 V, −8 A (charging).
    /// </summary>
    private static double SampleBatteryCranking(double t, ScopeChannelConfig config, double unitNoise)
    {
        const double crankStart = 0.8, crankEnd = 2.3, settle = 0.3;
        double ripple = Math.Sin(2.0 * Math.PI * 6.7 * (t - crankStart));
        double volts, amps;
        if (t < crankStart)
        {
            volts = 12.6;
            amps = 0.0;
        }
        else if (t < crankEnd)
        {
            double s = t - crankStart;
            double inrush = Math.Exp(-s / 0.04); // starter inrush decays in tens of ms
            volts = 10.1 - 1.7 * inrush - 0.35 * ripple * (1.0 - inrush);
            amps = 210.0 + 410.0 * inrush + 60.0 * ripple * (1.0 - inrush);
        }
        else
        {
            double s = Math.Min(1.0, (t - crankEnd) / settle);
            volts = 10.1 + (14.2 - 10.1) * s;
            amps = 210.0 * (1.0 - s) - 8.0 * s;
        }

        if (config.Channel == 1)
            return volts + 0.02 * unitNoise;

        double scale = config.CurrentScaleAmpsPerVolt > 0.0 ? config.CurrentScaleAmpsPerVolt : 1000.0;
        return (amps + 3.0 * unitNoise) / scale;
    }

    private static double SampleSolenoidCurrent(double t, double unitNoise)
    {
        const double period = 0.025; // 25 ms (40 Hz)
        double phase = (t % period) / period;

        double v;
        if (phase < 0.05)
        {
            v = 0.0;
        }
        else if (phase < 0.25)
        {
            // Inductive ramp to 1.50 V peak (15 A)
            double u = (phase - 0.05) / 0.20;
            v = 1.50 * u;
        }
        else if (phase < 0.35)
        {
            // Peak at 1.50 V
            v = 1.50;
        }
        else if (phase < 0.65)
        {
            // Hold current plateau at 1.10 V (above vMid = 0.75 V)
            v = 1.10;
        }
        else if (phase < 0.75)
        {
            // De-energize ramp down to 0.0 V
            double u = (phase - 0.65) / 0.10;
            v = 1.10 * (1.0 - u);
        }
        else
        {
            v = 0.0;
        }

        return Math.Clamp(v + unitNoise * 0.0005, 0.0, 1.50);
    }

    private static double SampleSine(double t)
    {
        const double freqHz = 1000.0;
        const double amplitude = 2.5; // 5.0 Vpp
        return amplitude * Math.Sin(2.0 * Math.PI * freqHz * t);
    }

    private static double SampleSquare(double t)
    {
        const double freqHz = 1000.0;
        const double period = 1.0 / freqHz;
        const double vLow = 0.0;
        const double vHigh = 5.0;

        double phase = (t % period) / period;
        if (phase < 0.02)
            return vLow + (vHigh - vLow) * (phase / 0.02);
        if (phase < 0.50)
            return vHigh;
        if (phase < 0.52)
            return vHigh - (vHigh - vLow) * ((phase - 0.50) / 0.02);
        return vLow;
    }

    private static bool EvaluateAndAlignTrigger(
        ScopeCaptureRequest request,
        Dictionary<int, double[]> channels,
        int sampleCount,
        int sampleRateHz)
    {
        if (request.TriggerMode == ScopeTriggerMode.None)
            return true;

        int trigChannel = request.TriggerChannel >= 1 && channels.ContainsKey(request.TriggerChannel)
            ? request.TriggerChannel
            : channels.Keys.First();

        double[] trigSamples = channels[trigChannel];
        double level = request.TriggerLevelVolts;

        for (int i = 0; i < sampleCount - 1; i++)
        {
            double v0 = trigSamples[i];
            double v1 = trigSamples[i + 1];

            bool crossed = request.TriggerMode switch
            {
                ScopeTriggerMode.Rising => v0 <= level && v1 > level,
                ScopeTriggerMode.Falling => v0 >= level && v1 < level,
                _ => true,
            };

            if (crossed)
                return true;
        }

        return false;
    }
}
