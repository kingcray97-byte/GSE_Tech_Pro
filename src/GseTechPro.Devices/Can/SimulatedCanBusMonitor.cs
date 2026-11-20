using GseTechPro.Core.Contracts;
using GseTechPro.Devices.Protocols.J1939;

namespace GseTechPro.Devices.Can;

/// <summary>
/// Deterministic simulated J1939 bus for the CAN Capture screen (rule 4: simulator first). Every frame is flagged
/// <see cref="CanFrame.IsSimulated"/>. The periods and values are demo values, not specs for any equipment.
/// </summary>
public sealed class SimulatedCanBusMonitor : CanBusMonitorBase
{
    private double _nextPollSeconds;

    public override string Name => "Simulated J1939 bus";
    public override bool IsSimulated => true;

    protected override TimeSpan IdleDelay => TimeSpan.FromMilliseconds(10);

    /// <summary>One simulated broadcast: what it is, how often it repeats, and where in the cycle it starts.</summary>
    public sealed record SimulatedMessage(string Label, uint Id, bool IsExtended, double PeriodSeconds, double OffsetSeconds);

    /// <summary>The simulated schedule (engine ECU at SA 0x00, transmission at SA 0x03, one 11-bit body frame).</summary>
    public static IReadOnlyList<SimulatedMessage> Schedule { get; } = new[]
    {
        new SimulatedMessage("EEC1 (engine speed)", J1939Identifier.Build(3, 0, 0, 0xF0, 0x04, 0x00), true, 0.020, 0.000),
        new SimulatedMessage("EEC2 (accelerator)", J1939Identifier.Build(3, 0, 0, 0xF0, 0x03, 0x00), true, 0.050, 0.005),
        new SimulatedMessage("CCVS (vehicle speed)", J1939Identifier.Build(6, 0, 0, 0xFE, 0xF1, 0x00), true, 0.100, 0.010),
        new SimulatedMessage("EFL/P1 (oil pressure)", J1939Identifier.Build(6, 0, 0, 0xFE, 0xEF, 0x00), true, 0.500, 0.015),
        new SimulatedMessage("ET1 (coolant temp)", J1939Identifier.Build(6, 0, 0, 0xFE, 0xEE, 0x00), true, 1.000, 0.020),
        new SimulatedMessage("HOURS (engine hours)", J1939Identifier.Build(6, 0, 0, 0xFE, 0xE5, 0x00), true, 1.000, 0.025),
        new SimulatedMessage("ETC1 (transmission, SA 0x03)", J1939Identifier.Build(3, 0, 0, 0xF0, 0x02, 0x03), true, 0.100, 0.030),
        new SimulatedMessage("Body controller (11-bit 0x123)", 0x123, false, 0.250, 0.035),
    };

    /// <summary>
    /// All simulated frames with timestamps in [<paramref name="fromSeconds"/>, <paramref name="toSeconds"/>), in time
    /// order. Pure: the same window always gives the same frames.
    /// </summary>
    public static IReadOnlyList<CanFrame> FramesBetween(double fromSeconds, double toSeconds)
    {
        var frames = new List<CanFrame>();
        if (toSeconds <= fromSeconds)
            return frames;

        foreach (var message in Schedule)
        {
            var k = Math.Max(0, (long)Math.Ceiling((fromSeconds - message.OffsetSeconds) / message.PeriodSeconds - 1e-9));
            for (; ; k++)
            {
                var t = Math.Round(message.OffsetSeconds + k * message.PeriodSeconds, 6);
                if (t < fromSeconds)
                    continue;
                if (t >= toSeconds)
                    break;
                frames.Add(new CanFrame
                {
                    Id = message.Id,
                    IsExtended = message.IsExtended,
                    Data = Payload(message, t, k),
                    TimestampSeconds = t,
                    IsSimulated = true,
                });
            }
        }

        frames.Sort((a, b) => a.TimestampSeconds.CompareTo(b.TimestampSeconds));
        return frames;
    }

    protected override Task OpenAsync(CanMonitorOptions options, CancellationToken cancellationToken)
    {
        _nextPollSeconds = 0;
        return Task.CompletedTask;
    }

    protected override Task CloseAsync() => Task.CompletedTask;

    protected override int Poll(CancellationToken cancellationToken)
    {
        var now = Clock.Elapsed.TotalSeconds;
        var frames = FramesBetween(_nextPollSeconds, now);
        _nextPollSeconds = now;
        foreach (var frame in frames)
            Publish(frame);
        return frames.Count;
    }

    private static byte[] Payload(SimulatedMessage message, double t, long k)
    {
        var pgn = message.IsExtended ? J1939Identifier.Parse(message.Id).Pgn : 0u;
        var data = new byte[8];
        Array.Fill(data, (byte)0xFF); // J1939 "not available"

        switch (pgn)
        {
            case J1939Pgn.ElectronicEngineController1 when J1939Identifier.Parse(message.Id).Sa == 0x00:
            {
                // Engine speed (SPN 190, 0.125 rpm/bit, bytes 4–5): idles around 800 rpm with a slow swing.
                var rpm = 800 + 400 * Math.Sin(2 * Math.PI * t / 8.0);
                var raw = (ushort)Math.Round(rpm / 0.125);
                data[3] = (byte)(raw & 0xFF);
                data[4] = (byte)(raw >> 8);
                break;
            }
            case J1939Pgn.ElectronicEngineController2:
                data[1] = (byte)Math.Round((25 + 20 * Math.Sin(2 * Math.PI * t / 8.0)) / 0.4); // SPN 91, 0.4 %/bit
                break;
            case J1939Pgn.CruiseControlVehicleSpeed:
            {
                var raw = (ushort)Math.Round((8 + 4 * Math.Sin(2 * Math.PI * t / 20.0)) * 256); // SPN 84, 1/256 km/h per bit
                data[1] = (byte)(raw & 0xFF);
                data[2] = (byte)(raw >> 8);
                break;
            }
            case J1939Pgn.EngineFluidLevelPressure1:
                data[4] = (byte)Math.Round(300 / 4.0); // SPN 100 at the byte J1939LiveData reads, 4 kPa/bit — demo value only
                break;
            case J1939Pgn.EngineTemperature1:
                data[0] = (byte)(85 + 40); // SPN 110, 1 °C/bit, −40 offset — demo value only
                break;
            case J1939Pgn.EngineHoursRevolutions:
            {
                var raw = (uint)(1234.5 / 0.05 + k); // SPN 247, 0.05 h/bit
                data[0] = (byte)raw;
                data[1] = (byte)(raw >> 8);
                data[2] = (byte)(raw >> 16);
                data[3] = (byte)(raw >> 24);
                break;
            }
            default:
                data[0] = (byte)(k & 0xFF); // rolling counter so the monitor shows changing bytes
                break;
        }

        return data;
    }
}
