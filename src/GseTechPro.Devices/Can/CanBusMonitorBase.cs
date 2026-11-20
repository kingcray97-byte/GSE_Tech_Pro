using System.Diagnostics;
using System.Threading.Channels;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;

namespace GseTechPro.Devices.Can;

/// <summary>
/// Shared plumbing for <see cref="ICanBusMonitor"/>: bounded frame channel (drop-oldest, counted), a monotonic
/// clock, and one background receive loop. Subclasses open/close the adapter and poll it.
/// </summary>
public abstract class CanBusMonitorBase : ICanBusMonitor
{
    private readonly object _gate = new();
    private Channel<CanFrame> _channel = CreateChannel(1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _dropped;

    protected Stopwatch Clock { get; } = new();

    public abstract string Name { get; }
    public abstract bool IsSimulated { get; }
    public virtual IReadOnlyList<int> SupportedBitrates => CanMonitorOptions.StandardBitrates;

    public bool IsRunning { get; private set; }
    public ChannelReader<CanFrame> Frames => _channel.Reader;
    public long DroppedFrameCount => Interlocked.Read(ref _dropped);

    public event EventHandler<CanBusErrorEvent>? BusError;

    public async Task StartAsync(CanMonitorOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!SupportedBitrates.Contains(options.Bitrate))
            throw new ArgumentOutOfRangeException(nameof(options), options.Bitrate,
                $"{Name} does not support {options.Bitrate} bit/s. Supported: {string.Join(", ", SupportedBitrates)}.");
        if (options.BufferCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(options), options.BufferCapacity, "Buffer capacity must be at least 1.");

        await StopAsync().ConfigureAwait(false);
        await OpenAsync(options, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _channel = CreateChannel(options.BufferCapacity);
            Interlocked.Exchange(ref _dropped, 0);
            _cts = new CancellationTokenSource();
            Clock.Restart();
            IsRunning = true;
            var token = _cts.Token;
            _loop = Task.Factory.StartNew(
                () => ReceiveLoopAsync(token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            if (!IsRunning)
                return;
            IsRunning = false;
            _cts?.Cancel();
            loop = _loop;
            _loop = null;
        }

        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _cts?.Dispose();
        _cts = null;
        Clock.Stop();
        await CloseAsync().ConfigureAwait(false);
        _channel.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>Opens the adapter (listen-only). Throw with a technician-readable message on failure.</summary>
    protected abstract Task OpenAsync(CanMonitorOptions options, CancellationToken cancellationToken);

    /// <summary>Closes the adapter. Must not throw.</summary>
    protected abstract Task CloseAsync();

    /// <summary>Reads whatever the adapter has, calling <see cref="Publish"/> per frame. Returns how many frames were read.</summary>
    protected abstract int Poll(CancellationToken cancellationToken);

    /// <summary>Wait between empty polls.</summary>
    protected virtual TimeSpan IdleDelay => TimeSpan.FromMilliseconds(1);

    protected void Publish(CanFrame frame)
    {
        var writer = _channel.Writer;
        // Drop-oldest by hand so the drop is counted.
        while (!writer.TryWrite(frame))
        {
            if (!_channel.Reader.TryRead(out _))
                return; // channel completed
            Interlocked.Increment(ref _dropped);
        }
    }

    protected void RaiseBusError(int code, string message) =>
        BusError?.Invoke(this, new CanBusErrorEvent
        {
            Code = code,
            Message = message,
            TimestampSeconds = Clock.Elapsed.TotalSeconds,
        });

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            int read;
            try
            {
                read = Poll(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                RaiseBusError(-1, $"{Name}: receive failed — {ex.Message}");
                read = 0;
                await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
            }

            if (read == 0)
                await Task.Delay(IdleDelay, token).ConfigureAwait(false);
        }
    }

    private static Channel<CanFrame> CreateChannel(int capacity) =>
        Channel.CreateBounded<CanFrame>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite fails when full; Publish drops the oldest and counts it
            SingleReader = false,
            SingleWriter = true,
        });
}
