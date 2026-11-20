using GseTechPro.Core.Contracts;

namespace GseTechPro.Devices.Can;

/// <summary>
/// Plays a recorded capture back through the same <see cref="GseTechPro.Core.Abstractions.ICanBusMonitor"/> interface,
/// at its recorded timing (times <see cref="Speed"/>). Frames keep their recorded <see cref="CanFrame.IsSimulated"/> flag.
/// </summary>
public sealed class ReplayCanBusMonitor : CanBusMonitorBase
{
    private readonly IReadOnlyList<CanFrame> _frames;
    private readonly string _name;
    private int _next;
    private double _origin;

    public ReplayCanBusMonitor(IReadOnlyList<CanFrame> frames, string name, bool isSimulated, double speed = 1.0)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (speed <= 0 || double.IsNaN(speed))
            throw new ArgumentOutOfRangeException(nameof(speed), speed, "Replay speed must be positive.");
        _frames = frames;
        _name = name;
        IsSimulated = isSimulated;
        Speed = speed;
    }

    public override string Name => $"Replay: {_name}";
    public override bool IsSimulated { get; }

    public double Speed { get; }

    /// <summary>True once every recorded frame has been played.</summary>
    public bool IsFinished => _next >= _frames.Count;

    public int FrameCount => _frames.Count;

    /// <summary>Replay accepts any bit rate: nothing is opened.</summary>
    public override IReadOnlyList<int> SupportedBitrates => CanMonitorOptions.StandardBitrates;

    protected override TimeSpan IdleDelay => TimeSpan.FromMilliseconds(5);

    protected override Task OpenAsync(CanMonitorOptions options, CancellationToken cancellationToken)
    {
        _next = 0;
        _origin = _frames.Count > 0 ? _frames[0].TimestampSeconds : 0;
        return Task.CompletedTask;
    }

    protected override Task CloseAsync() => Task.CompletedTask;

    protected override int Poll(CancellationToken cancellationToken)
    {
        var playedUntil = _origin + Clock.Elapsed.TotalSeconds * Speed;
        var count = 0;
        while (_next < _frames.Count && _frames[_next].TimestampSeconds <= playedUntil)
        {
            Publish(_frames[_next]);
            _next++;
            count++;
        }
        return count;
    }
}
