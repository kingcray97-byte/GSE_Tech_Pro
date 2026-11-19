using System.Runtime.InteropServices;
using System.Text;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;

namespace GseTechPro.Scope;

/// <summary>
/// PicoScope 4-channel automotive oscilloscope P/Invoke skeleton against the published PicoSDK
/// <c>ps5000a</c> API (ADR-011 §1/§3).
/// <para>
/// Per ADR-011 §1, PicoSDK headers/DLLs are not in <c>Reference/</c>; this wrapper compiles cleanly
/// against the published PicoSDK signatures and checks for <c>ps5000a.dll</c> before loading,
/// returning a clear error from <see cref="ConnectAsync"/> until hardware verification is wired.
/// </para>
/// </summary>
public sealed class PicoScopeDevice : IScopeDevice
{
    public const string DllName = "ps5000a.dll";
    public const int DefaultChannelCount = 4;

    private readonly object _sync = new();
    private readonly Dictionary<int, ScopeChannelConfig> _channelConfigs = new();
    private readonly string? _requestedSerial;

    private short _handle;
    private volatile bool _isConnected;

    public PicoScopeDevice(string? serial = null, string? displayName = null)
    {
        _requestedSerial = serial;
        Name = string.IsNullOrWhiteSpace(displayName)
            ? "PicoScope 5000a / Automotive"
            : displayName;

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

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ScopeDeviceKind Kind => ScopeDeviceKind.PicoScope;

    /// <inheritdoc />
    public bool IsSimulated => false;

    /// <inheritdoc />
    public bool IsConnected => _isConnected;

    /// <inheritdoc />
    public int ChannelCount => DefaultChannelCount;

    /// <summary>
    /// Checks whether <c>ps5000a.dll</c> is present in the application directory before any P/Invoke
    /// is attempted. Never loads a vendor DLL unless the file exists.
    /// </summary>
    public static bool IsSdkPresent()
    {
        string localDll = Path.Combine(AppContext.BaseDirectory, DllName);
        return File.Exists(localDll);
    }

    /// <summary>
    /// Enumerates attached PicoScope units when <c>ps5000a.dll</c> is present. Returns an empty list
    /// when the PicoSDK DLL is absent (ADR-011 §1).
    /// </summary>
    public static IReadOnlyList<ScopeDeviceInfo> DiscoverDevices()
    {
        if (!IsSdkPresent())
            return Array.Empty<ScopeDeviceInfo>();

        try
        {
            short count = 0;
            short serialLen = 256;
            var serials = new StringBuilder(serialLen);
            uint status = NativeMethods.ps5000aEnumerateUnits(out count, serials, ref serialLen);
            if (status != 0 || count <= 0)
                return Array.Empty<ScopeDeviceInfo>();

            string[] tokens = serials.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var list = new List<ScopeDeviceInfo>(tokens.Length);
            foreach (string serial in tokens)
            {
                list.Add(new ScopeDeviceInfo
                {
                    Id = $"picoscope-{serial}",
                    Name = $"PicoScope ({serial})",
                    Kind = ScopeDeviceKind.PicoScope,
                    ChannelCount = DefaultChannelCount,
                    Serial = serial,
                    IsSimulated = false,
                });
            }
            return list;
        }
        catch
        {
            return Array.Empty<ScopeDeviceInfo>();
        }
    }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSdkPresent())
        {
            throw new InvalidOperationException(
                $"PicoSDK ({DllName}) is not present / not yet wired for hardware capture (ADR-011 §1). Use the Simulator device.");
        }

        // ADR-011 §1: PicoSDK is not in Reference/ and is not exercised against hardware in Phase 6.
        throw new InvalidOperationException(
            $"PicoSDK ({DllName}) hardware verification is not yet wired in Phase 6 (ADR-011 §1); verify signatures against PicoSDK headers before hardware use.");
    }

    /// <inheritdoc />
    public Task DisconnectAsync()
    {
        lock (_sync)
        {
            if (_handle > 0 && IsSdkPresent())
            {
                try { _ = NativeMethods.ps5000aCloseUnit(_handle); }
                catch { /* ignore on close */ }
            }
            _handle = 0;
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
        if (channel < 1 || channel > DefaultChannelCount)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"Channel must be between 1 and {DefaultChannelCount}.");

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

        throw new InvalidOperationException(
            "PicoScope device is not connected (PicoSDK not present / not yet wired).");
    }

    /// <inheritdoc />
    public Task StopCaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isConnected && _handle > 0 && IsSdkPresent())
        {
            try { _ = NativeMethods.ps5000aStop(_handle); }
            catch { /* ignore */ }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ScopeCapture> ReadCaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(
            "No capture is available from PicoScope (PicoSDK not present / not yet wired).");
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ScopeMeasurement>> ComputeMeasurementsAsync(
        int channel,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(
            "No capture is available from PicoScope (PicoSDK not present / not yet wired).");
    }

    // ---- PicoSDK ps5000a P/Invoke skeleton (ADR-011 §1) ----
    // TODO: verify against PicoSDK header before hardware use
    internal static class NativeMethods
    {
        // TODO: verify against PicoSDK header before hardware use
        [DllImport(DllName, CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, ExactSpelling = true)]
        public static extern uint ps5000aEnumerateUnits(
            out short count,
            StringBuilder serials,
            ref short serialLth);

        // TODO: verify against PicoSDK header before hardware use
        [DllImport(DllName, CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, ExactSpelling = true)]
        public static extern uint ps5000aOpenUnit(
            out short handle,
            string? serial,
            int resolution);

        // TODO: verify against PicoSDK header before hardware use
        [DllImport(DllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern uint ps5000aCloseUnit(short handle);

        // TODO: verify against PicoSDK header before hardware use
        [DllImport(DllName, CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, ExactSpelling = true)]
        public static extern uint ps5000aGetUnitInfo(
            short handle,
            StringBuilder @string,
            short stringLength,
            out short requiredSize,
            uint info);

        // TODO: verify against PicoSDK header before hardware use
        [DllImport(DllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern uint ps5000aSetChannel(
            short handle,
            int channel,
            short enabled,
            int type,
            int range,
            float analogueOffset);

        // TODO: verify against PicoSDK header before hardware use
        [DllImport(DllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern uint ps5000aSetSimpleTrigger(
            short handle,
            short enable,
            int source,
            short threshold,
            int direction,
            uint delay,
            short autoTriggerMs);

        // TODO: verify against PicoSDK header before hardware use
        [DllImport(DllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern uint ps5000aGetTimebase2(
            short handle,
            uint timebase,
            int noSamples,
            out float timeIntervalNanoseconds,
            out int maxSamples,
            uint segmentIndex);

        // TODO: verify against PicoSDK header before hardware use
        [DllImport(DllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern uint ps5000aRunBlock(
            short handle,
            int noOfPreTriggerSamples,
            int noOfPostTriggerSamples,
            uint timebase,
            out int timeIndisposedMs,
            uint segmentIndex,
            nint lpReady,
            nint pParameter);

        // TODO: verify against PicoSDK header before hardware use
        [DllImport(DllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern uint ps5000aIsReady(
            short handle,
            out short ready);

        // TODO: verify against PicoSDK header before hardware use
        [DllImport(DllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern uint ps5000aSetDataBuffer(
            short handle,
            int channel,
            short[] buffer,
            int bufferLth,
            uint segmentIndex,
            int mode);

        // TODO: verify against PicoSDK header before hardware use
        [DllImport(DllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern uint ps5000aGetValues(
            short handle,
            uint startIndex,
            ref uint noOfSamples,
            uint downSampleRatio,
            int downSampleRatioMode,
            uint segmentIndex,
            out short overflow);

        // TODO: verify against PicoSDK header before hardware use
        [DllImport(DllName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        public static extern uint ps5000aStop(short handle);
    }
}
