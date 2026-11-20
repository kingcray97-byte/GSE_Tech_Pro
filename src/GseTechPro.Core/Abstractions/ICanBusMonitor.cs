using System.Threading.Channels;
using GseTechPro.Core.Contracts;

namespace GseTechPro.Core.Abstractions;

/// <summary>
/// Listen-only CAN bus monitor (Phase 10C.1/10C.2): the source for the CAN Capture screen (bus monitor, record,
/// <c>.asc</c>/CSV export). Implementations: the simulator, and the ECOM CAN-to-USB adapter.
/// <para>
/// <b>Receive only, by design.</b> There is no transmit call on this interface, and real adapters open the
/// controller in listen-only mode (no ACK, no error frames), so watching a GSE bus can never change it.
/// </para>
/// </summary>
public interface ICanBusMonitor : IAsyncDisposable
{
    /// <summary>Human-readable name, e.g. "ECOM S/N 12345" or "Simulated J1939 bus".</summary>
    string Name { get; }

    /// <summary>True when frames are simulated; the UI badges them and they are never saved as real captures.</summary>
    bool IsSimulated { get; }

    bool IsRunning { get; }

    /// <summary>Bit rates the adapter supports, in bit/s.</summary>
    IReadOnlyList<int> SupportedBitrates { get; }

    /// <summary>Received frames, oldest first. Bounded: when the reader falls behind, the oldest frames are dropped and counted.</summary>
    ChannelReader<CanFrame> Frames { get; }

    /// <summary>Frames dropped because the reader fell behind (since the last start).</summary>
    long DroppedFrameCount { get; }

    /// <summary>Bus or adapter errors (bus off, error passive, overrun...). Raised on a background thread.</summary>
    event EventHandler<CanBusErrorEvent>? BusError;

    /// <summary>Opens the adapter in listen-only mode at <see cref="CanMonitorOptions.Bitrate"/> and starts receiving.</summary>
    Task StartAsync(CanMonitorOptions options, CancellationToken cancellationToken = default);

    /// <summary>Stops receiving and closes the adapter. Idempotent.</summary>
    Task StopAsync();
}
