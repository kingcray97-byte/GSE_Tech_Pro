using System.Runtime.InteropServices;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;

namespace GseTechPro.Scope;

/// <summary>
/// Hantek 6074BE 4-channel automotive USB oscilloscope wrapper (ADR-011 §1/§3).
/// P/Invokes the x64 <c>HTHardDll.dll</c> and <c>HTSoftDll.dll</c> per the published headers in
/// <c>Reference/HT6074BE_SDK/HeadFiles_UTF8/</c>. Converts raw 8-bit/WORD ADC samples into
/// probe-corrected volts using the device's calibration readback and each channel's
/// <see cref="ScopeChannelConfig.ProbeMultiplier"/>.
/// </summary>
public sealed class Hantek6074BeDevice : IScopeDevice
{
    public const string HardDllName = "HTHardDll.dll";
    public const string SoftDllName = "HTSoftDll.dll";
    public const int MaxChannelCount = 4;
    public const int MaxSupportedDevices = 32;

    // DefMacro.h constants
    private const ushort CouplingDc = 0;
    private const ushort CouplingAc = 1;
    private const ushort TriggerModeEdge = 0;
    private const ushort TriggerSlopeRise = 0;
    private const ushort TriggerSlopeFall = 1;
    private const ushort YtNormal = 0;
    private const ushort DefaultLeverPos = 128; // Mid-scale zero level (0..255)
    private const ushort CalibrationLevelLen = 400; // NEW_CAL_LEVEL_LEN in DefMacro.h
    private const ushort AmpCalibrationLen = 578;   // Start.bas pAmpLevel length

    // Table 3 of SDK_HTHardDLL-EN.pdf: V/div per voltage base index (0..11) at 1:1 probe
    private static readonly double[] VoltDivTable =
    [
        0.002, // 0: 2 mV/div  (16 mV full-scale across 8 vertical divisions)
        0.005, // 1: 5 mV/div  (40 mV)
        0.010, // 2: 10 mV/div (80 mV)
        0.020, // 3: 20 mV/div (160 mV)
        0.050, // 4: 50 mV/div (400 mV)
        0.100, // 5: 100 mV/div (800 mV)
        0.200, // 6: 200 mV/div (1.6 V)
        0.500, // 7: 500 mV/div (4.0 V)
        1.000, // 8: 1 V/div    (8.0 V)
        2.000, // 9: 2 V/div    (16.0 V)
        5.000, // 10: 5 V/div   (40.0 V)
        10.00, // 11: 10 V/div  (80.0 V)
    ];

    // Table 1 of SDK_HTHardDLL-EN.pdf: timebase (s/div) per index (0..35)
    private static readonly double[] TimeDivTable =
    [
        2e-9, 5e-9, 10e-9, 20e-9, 50e-9, 100e-9, 200e-9, 500e-9,
        1e-6, 2e-6, 5e-6, 10e-6, 20e-6, 50e-6, 100e-6, 200e-6, 500e-6,
        1e-3, 2e-3, 5e-3, 10e-3, 20e-3, 50e-3, 100e-3, 200e-3, 500e-3,
        1.0, 2.0, 5.0, 10.0, 20.0, 50.0, 100.0, 200.0, 500.0, 1000.0,
    ];

    private readonly object _sync = new();
    private readonly ushort _deviceIndex;
    private readonly Dictionary<int, ScopeChannelConfig> _channelConfigs = new();
    private readonly ushort[] _calibrationLevels = new ushort[CalibrationLevelLen];
    private readonly ushort[] _ampCalibration = new ushort[AmpCalibrationLen];

    private volatile bool _isConnected;
    private volatile bool _stopRequested;
    private ScopeCapture? _lastCapture;
    private bool _lastCaptureTriggered = true;
    private bool _lastCaptureOutOfRange;

    public Hantek6074BeDevice(ushort deviceIndex = 0, string? displayName = null)
    {
        if (deviceIndex >= MaxSupportedDevices)
            throw new ArgumentOutOfRangeException(nameof(deviceIndex), deviceIndex, "Hantek device index must be 0..31.");

        _deviceIndex = deviceIndex;
        Name = string.IsNullOrWhiteSpace(displayName)
            ? $"Hantek 6074BE (#{deviceIndex})"
            : displayName;

        for (int i = 0; i < AmpCalibrationLen; i++)
            _ampCalibration[i] = 1024; // 1024 = 1.0x unity gain per Hantek SDK

        for (int ch = 1; ch <= MaxChannelCount; ch++)
        {
            _channelConfigs[ch] = new ScopeChannelConfig
            {
                Channel = ch,
                VoltageRangeVolts = 8.0,
                Coupling = ScopeCoupling.Dc,
                ProbeMultiplier = 1.0,
                CurrentScaleAmpsPerVolt = 0.0,
            };
        }
    }

    /// <summary>Zero-based Hantek SDK device slot (0..31).</summary>
    public ushort DeviceIndex => _deviceIndex;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ScopeDeviceKind Kind => ScopeDeviceKind.Hantek;

    /// <inheritdoc />
    public bool IsSimulated => false;

    /// <inheritdoc />
    public bool IsConnected => _isConnected;

    /// <inheritdoc />
    public int ChannelCount => MaxChannelCount;

    /// <summary>
    /// Discovers attached Hantek 6074BE oscilloscopes via <c>dsoHTSearchDevice</c>.
    /// Returns an empty list if <c>HTHardDll.dll</c> is not installed or no hardware is plugged in.
    /// </summary>
    public static IReadOnlyList<ScopeDeviceInfo> DiscoverDevices()
    {
        if (!IsSdkPresent())
            return Array.Empty<ScopeDeviceInfo>();

        try
        {
            short[] devInfo = new short[MaxSupportedDevices];
            for (int i = 0; i < devInfo.Length; i++)
                devInfo[i] = -1;

            ushort count = NativeMethods.dsoHTSearchDevice(devInfo);
            if (count == 0)
                return Array.Empty<ScopeDeviceInfo>();

            var found = new List<ScopeDeviceInfo>();
            for (ushort idx = 0; idx < MaxSupportedDevices; idx++)
            {
                if (devInfo[idx] >= 0 || NativeMethods.dsoHTDeviceConnect(idx) != 0)
                {
                    found.Add(new ScopeDeviceInfo
                    {
                        Id = $"hantek-{idx}",
                        Name = $"Hantek 6074BE (#{idx})",
                        Kind = ScopeDeviceKind.Hantek,
                        ChannelCount = MaxChannelCount,
                        Serial = $"HT6074BE-{idx:D2}",
                        IsSimulated = false,
                    });
                }
            }
            return found;
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or SEHException)
        {
            return Array.Empty<ScopeDeviceInfo>();
        }
    }

    /// <summary>Checks whether <c>HTHardDll.dll</c> can be resolved on this system.</summary>
    public static bool IsSdkPresent()
    {
        try
        {
            string localPath = Path.Combine(AppContext.BaseDirectory, HardDllName);
            if (File.Exists(localPath) && NativeLibrary.TryLoad(localPath, out nint handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }

            if (NativeLibrary.TryLoad(HardDllName, typeof(Hantek6074BeDevice).Assembly, null, out nint sysHandle))
            {
                NativeLibrary.Free(sysHandle);
                return true;
            }
        }
        catch
        {
            // Never throw from a probe check
        }

        return false;
    }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isConnected)
            return Task.CompletedTask;

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsSdkPresent())
            {
                throw new InvalidOperationException(
                    $"Hantek 6074BE device not found: '{HardDllName}' (x64) is not installed.");
            }

            try
            {
                short[] devInfo = new short[MaxSupportedDevices];
                for (int i = 0; i < devInfo.Length; i++)
                    devInfo[i] = -1;

                _ = NativeMethods.dsoHTSearchDevice(devInfo);
                if (NativeMethods.dsoHTDeviceConnect(_deviceIndex) == 0)
                {
                    throw new InvalidOperationException(
                        $"Hantek 6074BE device not found at index {_deviceIndex}. Check USB connection and driver.");
                }

                if (NativeMethods.dsoInitHard(_deviceIndex) == 0)
                {
                    throw new InvalidOperationException(
                        $"Hantek 6074BE at index {_deviceIndex} failed hardware initialization (dsoInitHard).");
                }

                _ = NativeMethods.dsoHTADCCHModGain(_deviceIndex, MaxChannelCount);

                lock (_sync)
                {
                    _ = NativeMethods.dsoHTReadCalibrationData(_deviceIndex, _calibrationLevels, CalibrationLevelLen);
                    _ = NativeMethods.dsoHTRDAmpCali(_deviceIndex, _ampCalibration, AmpCalibrationLen);
                    _isConnected = true;
                }
            }
            catch (Exception ex) when (
                ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or SEHException)
            {
                throw new InvalidOperationException(
                    $"Hantek 6074BE device not found or SDK failed to load: {ex.Message}", ex);
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task DisconnectAsync()
    {
        lock (_sync)
        {
            _stopRequested = true;
            _isConnected = false;
        }
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
            throw new InvalidOperationException("Hantek 6074BE is not connected. Call ConnectAsync first.");
        if (request.Channels is null || request.Channels.Count == 0)
            throw new ArgumentException("At least one channel must be enabled in ScopeCaptureRequest.", nameof(request));
        if (request.SampleRateHz < 1)
            throw new ArgumentOutOfRangeException(nameof(request), "SampleRateHz must be >= 1.");
        if (!double.IsFinite(request.TimebaseSecondsPerDivision) || request.TimebaseSecondsPerDivision <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(request), "TimebaseSecondsPerDivision must be a finite positive value.");

        foreach (var ch in request.Channels)
            ValidateChannelIndex(ch.Channel);

        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _stopRequested = false;

            if (NativeMethods.dsoHTDeviceConnect(_deviceIndex) == 0)
            {
                _isConnected = false;
                throw new InvalidOperationException("Hantek 6074BE device not found (unplugged before capture).");
            }

            int derivedSamples = request.SampleCount ?? (int)Math.Round(
                request.TimebaseSecondsPerDivision * 10.0 * request.SampleRateHz,
                MidpointRounding.AwayFromZero);

            int maxPerChannel = WaveformFile.MaxTotalSamples / Math.Max(1, request.Channels.Count);
            int requestedSampleCount = Math.Clamp(derivedSamples, 512, maxPerChannel);
            // HTHardDll.h line 25: nBufferLen must be an integer multiple of 512
            uint alignedBufferLen = (uint)(((requestedSampleCount + 511) / 512) * 512);

            ushort timeDivIndex = SelectTimeDivIndex(request.TimebaseSecondsPerDivision);
            ushort trigChannelZeroBased = (ushort)Math.Clamp(request.TriggerChannel - 1, 0, MaxChannelCount - 1);
            ushort trigSlope = request.TriggerMode == ScopeTriggerMode.Falling ? TriggerSlopeFall : TriggerSlopeRise;

            var relay = new HantekRelayControl
            {
                bCHEnable = new uint[MaxChannelCount],
                nCHVoltDIV = new ushort[MaxChannelCount],
                nCHCoupling = new ushort[MaxChannelCount],
                bCHBWLimit = new uint[MaxChannelCount],
                nTrigSource = trigChannelZeroBased,
                bTrigFilt = 0,
                nALT = 0,
            };

            ushort chMask = 0;
            double[] channelVoltDivs = new double[MaxChannelCount];
            double[] channelProbeScales = new double[MaxChannelCount];

            lock (_sync)
            {
                foreach (var chCfg in request.Channels)
                {
                    _channelConfigs[chCfg.Channel] = chCfg;
                }

                for (int i = 0; i < MaxChannelCount; i++)
                {
                    int chNum = i + 1;
                    bool enabled = request.Channels.Any(c => c.Channel == chNum);
                    var cfg = _channelConfigs[chNum];

                    double probeScale = double.IsFinite(cfg.ProbeMultiplier) && cfg.ProbeMultiplier > 0.0
                        ? cfg.ProbeMultiplier
                        : 1.0;
                    double bncRange = Math.Max(0.016, cfg.VoltageRangeVolts / probeScale);
                    ushort voltDivIdx = SelectVoltDivIndex(bncRange);

                    relay.bCHEnable[i] = enabled ? 1u : 0u;
                    relay.nCHVoltDIV[i] = voltDivIdx;
                    relay.nCHCoupling[i] = cfg.Coupling == ScopeCoupling.Ac ? CouplingAc : CouplingDc;
                    relay.bCHBWLimit[i] = 0u;

                    channelVoltDivs[i] = VoltDivTable[voltDivIdx];
                    channelProbeScales[i] = probeScale;

                    if (enabled)
                        chMask |= (ushort)(1 << i);
                }
            }

            // Same zero level the samples are converted with (calibrated per channel and V/div), so the trigger
            // threshold lands where the displayed voltage says it does.
            ushort trigPos = ComputeTriggerLevelCounts(
                request.TriggerLevelVolts,
                channelVoltDivs[trigChannelZeroBased],
                channelProbeScales[trigChannelZeroBased],
                GetCalibratedLeverPos(trigChannelZeroBased, relay.nCHVoltDIV[trigChannelZeroBased]));

            var control = new HantekControlData
            {
                nCHSet = chMask == 0 ? (ushort)0x0F : chMask,
                nTimeDIV = timeDivIndex,
                nTriggerSource = trigChannelZeroBased,
                nHTriggerPos = 50,
                nVTriggerPos = trigPos,
                nTriggerSlope = trigSlope,
                nBufferLen = alignedBufferLen,
                nReadDataLen = alignedBufferLen,
                nAlreadyReadLen = 0,
                nALT = 0,
                nETSOpen = 0,
                nDriverCode = 0,
                nLastAddress = 0,
                nFPGAVersion = NativeMethods.dsoGetFPGAVersion(_deviceIndex),
            };

            _ = NativeMethods.dsoHTSetSampleRate(_deviceIndex, YtNormal, ref relay, ref control);
            // The hardware rate follows the time/div step, not the requested rate (SDK_HTHardDLL-EN, Table 1).
            // Stamp the capture with what the device reports so frequency/timing measurements are right.
            int actualSampleRateHz = ResolveSampleRateHz(TryGetDeviceSampleRate(_deviceIndex), request.SampleRateHz);
            _ = NativeMethods.dsoHTSetCHAndTrigger(_deviceIndex, ref relay, timeDivIndex);
            _ = NativeMethods.dsoHTSetRamAndTrigerControl(_deviceIndex, timeDivIndex, control.nCHSet, trigChannelZeroBased, 0);
            _ = NativeMethods.dsoHTSetHTriggerLength(_deviceIndex, ref control, MaxChannelCount);

            for (ushort ch = 0; ch < MaxChannelCount; ch++)
            {
                ushort leverPos = GetCalibratedLeverPos(ch, relay.nCHVoltDIV[ch]);
                _ = NativeMethods.dsoHTSetCHPos(_deviceIndex, relay.nCHVoltDIV[ch], leverPos, ch, MaxChannelCount);
            }

            _ = NativeMethods.dsoHTSetVTriggerLevel(_deviceIndex, trigPos, 4);
            _ = NativeMethods.dsoHTSetTrigerMode(_deviceIndex, TriggerModeEdge, trigSlope, CouplingDc);

            // Bit 0: Auto trigger (when TriggerMode == None), Bit 2: stop after this collect (4)
            ushort startControl = request.TriggerMode == ScopeTriggerMode.None
                ? (ushort)(0x01 | 0x04)
                : (ushort)0x04;

            if (NativeMethods.dsoHTStartCollectData(_deviceIndex, startControl) == 0)
                throw new InvalidOperationException("Hantek 6074BE failed to start data collection (dsoHTStartCollectData).");

            _ = NativeMethods.dsoHTStartTrigger(_deviceIndex);

            // Poll dsoHTGetState until bit 1 (0x02 = collection finished) is set or timeout
            bool triggered = false;
            bool finished = false;
            var deadline = DateTime.UtcNow + CaptureTimeout(request.TimebaseSecondsPerDivision);

            while (DateTime.UtcNow < deadline && !_stopRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (NativeMethods.dsoHTDeviceConnect(_deviceIndex) == 0)
                {
                    _isConnected = false;
                    throw new InvalidOperationException("Hantek 6074BE unplugged during capture.");
                }

                ushort state = NativeMethods.dsoHTGetState(_deviceIndex);
                if ((state & 0x01) != 0)
                    triggered = true;
                if ((state & 0x02) != 0)
                {
                    finished = true;
                    break;
                }

                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            }

            if (!finished && !_stopRequested)
            {
                // Force trigger so block read completes and gets marked NoTrigger if needed
                _ = NativeMethods.dsoHTForceTrigger(_deviceIndex);
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }

            ushort[] ch1Raw = new ushort[alignedBufferLen];
            ushort[] ch2Raw = new ushort[alignedBufferLen];
            ushort[] ch3Raw = new ushort[alignedBufferLen];
            ushort[] ch4Raw = new ushort[alignedBufferLen];

            if (NativeMethods.dsoHTGetData(_deviceIndex, ch1Raw, ch2Raw, ch3Raw, ch4Raw, ref control) == 0)
            {
                _isConnected = false;
                throw new InvalidOperationException("Failed to read capture buffer from Hantek 6074BE (dsoHTGetData).");
            }

            ushort[][] rawArrays = [ch1Raw, ch2Raw, ch3Raw, ch4Raw];
            var resultChannels = new Dictionary<int, double[]>(request.Channels.Count);
            bool anyOutOfRange = false;

            foreach (var chCfg in request.Channels)
            {
                int chIdx = chCfg.Channel - 1;
                ushort voltDivIdx = relay.nCHVoltDIV[chIdx];
                ushort leverPos = GetCalibratedLeverPos((ushort)chIdx, voltDivIdx);
                double ampScale = GetAmplitudeCalibrationFactor(chIdx, voltDivIdx);

                var (volts, clipped) = ConvertRawCountsToVolts(
                    rawArrays[chIdx],
                    requestedSampleCount,
                    leverPos,
                    channelVoltDivs[chIdx],
                    channelProbeScales[chIdx],
                    ampScale);

                resultChannels[chCfg.Channel] = volts;
                if (clipped)
                    anyOutOfRange = true;
            }

            var capture = new ScopeCapture
            {
                SampleCount = requestedSampleCount,
                SampleRateHz = actualSampleRateHz,
                TimebaseSecondsPerDivision = request.TimebaseSecondsPerDivision,
                CapturedAt = Clock.UtcNow,
                IsSimulated = false,
                Channels = resultChannels,
            };

            lock (_sync)
            {
                _lastCapture = capture;
                _lastCaptureTriggered = request.TriggerMode == ScopeTriggerMode.None || triggered;
                _lastCaptureOutOfRange = anyOutOfRange;
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task StopCaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _stopRequested = true;
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
        bool outOfRange;

        lock (_sync)
        {
            if (_lastCapture is null)
                throw new InvalidOperationException("No capture is available. Call StartCaptureAsync first.");
            capture = _lastCapture;
            triggered = _lastCaptureTriggered;
            outOfRange = _lastCaptureOutOfRange;
            config = _channelConfigs.TryGetValue(channel, out var c)
                ? c
                : new ScopeChannelConfig { Channel = channel, VoltageRangeVolts = 8.0 };
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
                isSimulated: false,
                noTrigger: !triggered,
                outOfRange: outOfRange);
        }, cancellationToken);
    }

    /// <summary>
    /// Converts raw Hantek 8-bit/WORD ADC counts (0..255) to probe-corrected volts using the
    /// SDK formula <c>(raw - (255 - leverPos)) * 8 * VoltDiv / 255 * ampCalibration * ProbeMultiplier</c>.
    /// </summary>
    public static (double[] Volts, bool Clipped) ConvertRawCountsToVolts(
        ReadOnlySpan<ushort> rawCounts,
        int sampleCount,
        ushort leverPos,
        double voltDivVolts,
        double probeMultiplier,
        double amplitudeCalibrationFactor = 1.0)
    {
        int count = Math.Min(sampleCount, rawCounts.Length);
        double[] volts = new double[count];
        double zeroCode = 255.0 - leverPos;
        double scalePerCount = (8.0 * voltDivVolts / 255.0)
            * (double.IsFinite(amplitudeCalibrationFactor) && amplitudeCalibrationFactor > 0.0 ? amplitudeCalibrationFactor : 1.0)
            * (double.IsFinite(probeMultiplier) && probeMultiplier > 0.0 ? probeMultiplier : 1.0);

        bool clipped = false;
        for (int i = 0; i < count; i++)
        {
            ushort raw = (ushort)(rawCounts[i] & 0xFF);
            if (raw == 0 || raw == 255)
                clipped = true;

            volts[i] = (raw - zeroCode) * scalePerCount;
        }

        return (volts, clipped);
    }

    private ushort GetCalibratedLeverPos(ushort chZeroBased, ushort voltDivIndex)
    {
        lock (_sync)
        {
            int idx = chZeroBased * 12 + voltDivIndex;
            if (idx >= 0 && idx < _calibrationLevels.Length)
            {
                ushort cal = _calibrationLevels[idx];
                if (cal is > 32 and < 224)
                    return cal;
            }
        }
        return DefaultLeverPos;
    }

    private double GetAmplitudeCalibrationFactor(int chZeroBased, ushort voltDivIndex)
    {
        lock (_sync)
        {
            int idx = chZeroBased * 12 + voltDivIndex;
            if (idx >= 0 && idx < _ampCalibration.Length)
            {
                ushort amp = _ampCalibration[idx];
                if (amp is > 512 and < 2048)
                    return amp / 1024.0;
            }
        }
        return 1.0;
    }

    /// <summary>
    /// Sample rate to stamp on a capture: the rate the device reports after <c>dsoHTSetSampleRate</c>, or the
    /// requested rate when the SDK gives nothing usable (old DLL without the export, zero, NaN).
    /// </summary>
    internal static int ResolveSampleRateHz(double? deviceReportedHz, int requestedHz)
    {
        if (deviceReportedHz is double hz && double.IsFinite(hz) && hz >= 1.0 && hz <= int.MaxValue)
            return (int)Math.Round(hz, MidpointRounding.AwayFromZero);
        return requestedHz;
    }

    /// <summary>
    /// How long to wait for a block capture: one 10-division sweep, twice over, plus 1 s of USB/trigger margin,
    /// never less than the old 2 s and capped at 60 s. A fixed 2 s cut off slow timebases (1 s/div = 10 s sweep).
    /// </summary>
    internal static TimeSpan CaptureTimeout(double timebaseSecondsPerDivision)
    {
        double sweep = double.IsFinite(timebaseSecondsPerDivision) && timebaseSecondsPerDivision > 0
            ? timebaseSecondsPerDivision * 10.0
            : 0.0;
        return TimeSpan.FromSeconds(Math.Clamp(sweep * 2.0 + 1.0, 2.0, 60.0));
    }

    private static double? TryGetDeviceSampleRate(ushort deviceIndex)
    {
        try
        {
            return NativeMethods.dsoGetSampleRate(deviceIndex);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or SEHException)
        {
            return null;
        }
    }

    private static ushort SelectVoltDivIndex(double bncFullScaleRangeVolts)
    {
        // Full scale across 8 vertical divisions = 8 * VoltDiv
        double targetVoltDiv = Math.Max(0.002, bncFullScaleRangeVolts / 8.0);
        for (ushort i = 0; i < VoltDivTable.Length; i++)
        {
            if (VoltDivTable[i] >= targetVoltDiv)
                return i;
        }
        return (ushort)(VoltDivTable.Length - 1);
    }

    private static ushort SelectTimeDivIndex(double timebaseSecondsPerDivision)
    {
        ushort bestIndex = 17; // default 1 ms/div
        double bestDiff = double.MaxValue;
        for (ushort i = 0; i < TimeDivTable.Length; i++)
        {
            double diff = Math.Abs(TimeDivTable[i] - timebaseSecondsPerDivision);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    private static ushort ComputeTriggerLevelCounts(
        double triggerLevelVolts,
        double voltDiv,
        double probeMultiplier,
        ushort leverPos)
    {
        double bncTriggerVolts = triggerLevelVolts / (probeMultiplier > 0.0 ? probeMultiplier : 1.0);
        double offsetCounts = (bncTriggerVolts * 255.0) / (8.0 * Math.Max(0.002, voltDiv));
        int pos = (int)Math.Round((255 - leverPos) + offsetCounts, MidpointRounding.AwayFromZero);
        return (ushort)Math.Clamp(pos, 0, 255);
    }

    private static void ValidateChannelIndex(int channel)
    {
        if (channel < 1 || channel > MaxChannelCount)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"Channel must be between 1 and {MaxChannelCount}.");
    }

    // ---- Native structures and P/Invoke declarations from HTHardDll.h / HTSoftDll.h / DefMacro.h ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct HantekRelayControl
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxChannelCount)]
        public uint[] bCHEnable;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxChannelCount)]
        public ushort[] nCHVoltDIV;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxChannelCount)]
        public ushort[] nCHCoupling;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxChannelCount)]
        public uint[] bCHBWLimit;

        public ushort nTrigSource;
        public uint bTrigFilt;
        public ushort nALT;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HantekControlData
    {
        public ushort nCHSet;
        public ushort nTimeDIV;
        public ushort nTriggerSource;
        public ushort nHTriggerPos;
        public ushort nVTriggerPos;
        public ushort nTriggerSlope;
        public uint nBufferLen;
        public uint nReadDataLen;
        public uint nAlreadyReadLen;
        public ushort nALT;
        public ushort nETSOpen;
        public ushort nDriverCode;
        public uint nLastAddress;
        public ushort nFPGAVersion;
    }

    internal static class NativeMethods
    {
        [DllImport(HardDllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern ushort dsoHTSearchDevice([Out] short[] pDevInfo);

        [DllImport(HardDllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern ushort dsoHTDeviceConnect(ushort nDeviceIndex);

        [DllImport(HardDllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern ushort dsoInitHard(ushort nDeviceIndex);

        [DllImport(HardDllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern ushort dsoGetFPGAVersion(ushort nDeviceIndex);

        // HTHardDll.h: DLL_API FLOAT WINAPI dsoGetSampleRate(WORD DeviceIndex);
        [DllImport(HardDllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern float dsoGetSampleRate(ushort deviceIndex);

        [DllImport(HardDllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern ushort dsoHTADCCHModGain(ushort nDeviceIndex, ushort nCHMod);

        [DllImport(HardDllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern ushort dsoHTReadCalibrationData(ushort nDeviceIndex, [Out] ushort[] pLevel, ushort nLen);

        [DllImport(HardDllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern ushort dsoHTRDAmpCali(ushort nDeviceIndex, [Out] ushort[] pLevel, ushort nLen);

        [DllImport(HardDllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern ushort dsoHTSetSampleRate(
            ushort nDeviceIndex,
            ushort nYTFormat,
            ref HantekRelayControl pRelayControl,
            ref HantekControlData pControl);

        [DllImport(HardDllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern ushort dsoHTSetCHAndTrigger(
            ushort nDeviceIndex,
            ref HantekRelayControl pRelayControl,
            ushort nTimeDIV);

        [DllImport(HardDllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern ushort dsoHTSetRamAndTrigerControl(
            ushort nDeviceIndex,
            ushort nTimeDiv,
            ushort nCHset,
            ushort nTrigerSource,
            ushort nPeak);

        [DllImport(HardDllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern ushort dsoHTSetCHPos(
            ushort nDeviceIndex,
            ushort nVoltDIV,
            ushort nPos,
            ushort nCH,
            ushort nCHMode);

        [DllImport(HardDllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern ushort dsoHTSetVTriggerLevel(
            ushort nDeviceIndex,
            ushort nPos,
            ushort nSensitivity);

        [DllImport(HardDllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern ushort dsoHTSetHTriggerLength(
            ushort nDeviceIndex,
            ref HantekControlData pControl,
            ushort nCHMod);

        [DllImport(HardDllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern ushort dsoHTSetTrigerMode(
            ushort nDeviceIndex,
            ushort nTriggerMode,
            ushort nTriggerSlope,
            ushort nTriggerCouple);

        [DllImport(HardDllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern ushort dsoHTStartCollectData(ushort nDeviceIndex, ushort nStartControl);

        [DllImport(HardDllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern ushort dsoHTStartTrigger(ushort nDeviceIndex);

        [DllImport(HardDllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern ushort dsoHTForceTrigger(ushort nDeviceIndex);

        [DllImport(HardDllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern ushort dsoHTGetState(ushort nDeviceIndex);

        [DllImport(HardDllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern ushort dsoHTGetData(
            ushort nDeviceIndex,
            [Out] ushort[] pCH1Data,
            [Out] ushort[] pCH2Data,
            [Out] ushort[] pCH3Data,
            [Out] ushort[] pCH4Data,
            ref HantekControlData pControl);

        [DllImport(SoftDllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern uint dsoSFGetSampleRate(ushort nTimeDIV, ushort nALT, ushort nCHState, ushort nCH);
    }
}
