using GseTechPro.Core.Contracts;
using GseTechPro.Devices.Can;
using GseTechPro.Devices.Protocols.J1939;
using Xunit;

namespace GseTechPro.Devices.Tests;

/// <summary>Phase 10C.2: bus table, Vector ASCII (.asc) and CSV logs, replay.</summary>
public sealed class CanCaptureFormatTests
{
    private static CanFrame Ext(uint id, double t, params byte[] data) =>
        new() { Id = id, IsExtended = true, Data = data, TimestampSeconds = t };

    private static CanFrame Std(uint id, double t, params byte[] data) =>
        new() { Id = id, IsExtended = false, Data = data, TimestampSeconds = t };

    [Fact]
    public void Table_KeepsOneRowPerId_WithCountPeriodAndChangedBytes()
    {
        var table = new CanBusTable();
        var id = J1939Identifier.Build(3, 0, 0, 0xF0, 0x04, 0x00);

        Assert.True(table.Apply(Ext(id, 0.00, 1, 2, 3)).IsNew);
        Assert.False(table.Apply(Ext(id, 0.02, 1, 9, 3)).IsNew);
        var (entry, _) = table.Apply(Ext(id, 0.04, 1, 9, 3));

        Assert.Equal(1, table.Count);
        Assert.Equal(3, table.TotalFrames);
        Assert.Equal(3, entry.Count);
        Assert.Equal(0.02, entry.PeriodSeconds!.Value, 6);
        Assert.Equal(0, entry.ChangedMask);

        table.Apply(Ext(id, 0.06, 1, 9, 7));
        Assert.Equal(0b100, entry.ChangedMask);
    }

    [Fact]
    public void Table_SeparatesStandardAndExtendedIds()
    {
        var table = new CanBusTable();
        table.Apply(Std(0x123, 0, 1));
        table.Apply(Ext(0x123, 0, 1));
        Assert.Equal(2, table.Count);
    }

    [Fact]
    public void Table_DecodesJ1939_PgnSourceAndKnownSpns()
    {
        var table = new CanBusTable();
        var frame = SimulatedCanBusMonitor.FramesBetween(0, 0.01)
            .First(f => J1939Identifier.Parse(f.Id).Pgn == J1939Pgn.ElectronicEngineController1);

        var (entry, _) = table.Apply(frame);

        Assert.Equal(J1939Pgn.ElectronicEngineController1, entry.Pgn);
        Assert.Equal((byte)0x00, entry.SourceAddress);
        Assert.Equal("EEC1", entry.PgnLabel);
        Assert.Contains(entry.Decoded, v => v.Spn == 190);
        Assert.All(entry.Decoded, v => Assert.True(v.Known));
    }

    [Fact]
    public void Table_StandardIds_HaveNoJ1939Decode()
    {
        var (entry, _) = new CanBusTable().Apply(Std(0x123, 0, 1, 2));
        Assert.Null(entry.Pgn);
        Assert.Empty(entry.Decoded);
        Assert.Equal("123", entry.IdText);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("CF004", true)]
    [InlineData("0xCF00400", true)]
    [InlineData("61444", true)]
    [InlineData("F004", true)]
    [InlineData("eec1", true)]
    [InlineData("SA 00", true)]
    [InlineData("engine speed", true)]
    [InlineData("SA 03", false)]
    [InlineData("FEEE", false)]
    public void Filter_MatchesIdPgnNameSourceOrSpn(string filter, bool expected)
    {
        var frame = SimulatedCanBusMonitor.FramesBetween(0, 0.01)
            .First(f => J1939Identifier.Parse(f.Id).Pgn == J1939Pgn.ElectronicEngineController1);
        var (entry, _) = new CanBusTable().Apply(frame);
        Assert.Equal(expected, CanBusTable.Matches(entry, filter));
    }

    [Fact]
    public void Asc_RoundTrips_ExtendedStandardAndRemoteFrames()
    {
        var frames = new[]
        {
            Ext(0x0CF00400, 0.020000, 0xFF, 0xFF, 0xFF, 0x40, 0x19, 0xFF, 0xFF, 0xFF),
            Std(0x123, 0.035000, 0x01, 0x02),
            new CanFrame { Id = 0x7DF, IsRemote = true, TimestampSeconds = 1.5 },
            Ext(0x18FEEE00, 2.0),
        };

        var text = new StringWriter();
        AscCanLog.Write(text, frames, new DateTimeOffset(2026, 11, 20, 14, 15, 0, TimeSpan.Zero), simulated: false, "test");
        var result = AscCanLog.Read(new StringReader(text.ToString()));

        Assert.False(result.IsSimulated);
        Assert.Equal(0, result.SkippedLines);
        Assert.Equal(frames.Length, result.Frames.Count);
        for (var i = 0; i < frames.Length; i++)
        {
            Assert.Equal(frames[i].Id, result.Frames[i].Id);
            Assert.Equal(frames[i].IsExtended, result.Frames[i].IsExtended);
            Assert.Equal(frames[i].IsRemote, result.Frames[i].IsRemote);
            Assert.Equal(frames[i].Data, result.Frames[i].Data);
            Assert.Equal(frames[i].TimestampSeconds, result.Frames[i].TimestampSeconds, 6);
        }
    }

    [Fact]
    public void Asc_UsesTheVectorLineLayout()
    {
        var line = AscCanLog.FormatFrame(Ext(0x0CF00400, 0.02, 0xAB, 0x01));
        Assert.Equal("   0.020000 1  CF00400x        Rx   d 2 AB 01", line);

        var text = new StringWriter();
        AscCanLog.Write(text, Array.Empty<CanFrame>(), DateTimeOffset.Now, simulated: false);
        var lines = text.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("date ", lines[0]);
        Assert.Equal("base hex  timestamps absolute", lines[1]);
        Assert.StartsWith("Begin Triggerblock", lines.First(l => l.StartsWith("Begin", StringComparison.Ordinal)));
        Assert.Equal("End TriggerBlock", lines[^1]);
    }

    [Fact]
    public void Asc_SimulatedMarker_SurvivesRoundTrip()
    {
        var text = new StringWriter();
        AscCanLog.Write(text, SimulatedCanBusMonitor.FramesBetween(0, 0.5), DateTimeOffset.Now, simulated: true);
        var result = AscCanLog.Read(new StringReader(text.ToString()));

        Assert.True(result.IsSimulated);
        Assert.NotEmpty(result.Frames);
        Assert.All(result.Frames, f => Assert.True(f.IsSimulated));
    }

    [Fact]
    public void Asc_Reader_SkipsLinesItCannotParse_AndReadsDecimalBase()
    {
        const string log = """
            date Fri Nov 20 02:15:00.000 pm 2026
            base dec  timestamps absolute
            Begin Triggerblock Fri Nov 20 02:15:00.000 pm 2026
               0.000000 Start of measurement
               0.100000 1  291             Rx   d 2 1 255
               0.200000 CANFD   1 Rx 123 1 0 8 8 00 00 00 00 00 00 00 00
               0.300000 1  ErrorFrame
            End TriggerBlock
            """;

        var result = AscCanLog.Read(new StringReader(log));

        var frame = Assert.Single(result.Frames);
        Assert.Equal(291u, frame.Id);
        Assert.Equal(new byte[] { 1, 255 }, frame.Data);
        Assert.Equal(2, result.SkippedLines);
    }

    [Fact]
    public void Csv_HasHeaderAndOneRowPerFrame()
    {
        var text = new StringWriter();
        CsvCanLog.Write(text, new[] { Ext(0x0CF00400, 0.02, 0xAB, 0x01), Std(0x123, 0.5) });
        var lines = text.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(CsvCanLog.Header, lines[0]);
        Assert.Equal("0.020000,0CF00400,1,0,2,AB01,0", lines[1]);
        Assert.Equal("0.500000,123,0,0,0,,0", lines[2]);
    }

    [Fact]
    public async Task Replay_PlaysEveryFrameInOrder_AndFinishes()
    {
        var frames = SimulatedCanBusMonitor.FramesBetween(0, 0.3);
        await using var replay = new ReplayCanBusMonitor(frames, "test.asc", isSimulated: true, speed: 10);
        await replay.StartAsync(new CanMonitorOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var played = new List<CanFrame>();
        while (played.Count < frames.Count)
            played.Add(await replay.Frames.ReadAsync(cts.Token));

        Assert.True(replay.IsFinished);
        Assert.True(replay.IsSimulated);
        Assert.Equal(frames.Select(f => f.TimestampSeconds), played.Select(f => f.TimestampSeconds));
    }

    [Fact]
    public void J1939Decode_SkipsNotAvailableAndErrorValues()
    {
        // EEC1: torque bytes 0xFF (not available) / 0xFE (error), engine speed 0x1A40 → 840 rpm.
        var values = J1939LiveData.Extract(J1939Pgn.ElectronicEngineController1,
            new byte[] { 0xFF, 0xFF, 0xFE, 0x40, 0x1A, 0xFF, 0xFF, 0xFF });

        var only = Assert.Single(values);
        Assert.Equal(190, only.Spn);
        Assert.Equal(840, only.Value, 3);

        // Engine speed 0xFFFF = not available.
        Assert.Empty(J1939LiveData.Extract(J1939Pgn.ElectronicEngineController1,
            new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }));
    }

    [Theory]
    [InlineData(0xFAul, 1, true)]
    [InlineData(0xFBul, 1, false)]
    [InlineData(0xFAFFul, 2, true)]
    [InlineData(0xFB00ul, 2, false)]
    [InlineData(0xFAFFFFFFul, 4, true)]
    [InlineData(0xFFFFFFFFul, 4, false)]
    public void J1939ValidRange_FollowsTheIndicatorRule(ulong raw, int bytes, bool valid) =>
        Assert.Equal(valid, J1939LiveData.IsValidRaw(raw, bytes));

    [Fact]
    public void Simulator_DecodesOnlyTheValuesItSends()
    {
        var table = new CanBusTable();
        foreach (var f in SimulatedCanBusMonitor.FramesBetween(0, 1.1))
            table.Apply(f);

        var decoded = table.Entries.SelectMany(e => e.Decoded).ToList();
        Assert.Contains(decoded, v => v.Spn == 100 && Math.Abs(v.Value - 300) < 0.01); // oil pressure 300 kPa (demo)
        Assert.Contains(decoded, v => v.Spn == 110 && Math.Abs(v.Value - 85) < 0.01);  // coolant 85 °C (demo)
        Assert.DoesNotContain(decoded, v => v.Spn is 512 or 513 or 92 or 174 or 175 or 98);
    }

    [Fact]
    public void Replay_RefusesZeroSpeed() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayCanBusMonitor(Array.Empty<CanFrame>(), "x", false, 0));
}
