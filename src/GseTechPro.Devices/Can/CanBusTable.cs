using System.Globalization;
using GseTechPro.Core.Contracts;
using GseTechPro.Devices.Protocols.J1939;

namespace GseTechPro.Devices.Can;

/// <summary>
/// One row of the bus monitor: the latest frame for an identifier, with count, period, which bytes changed, and
/// a J1939 decode for 29-bit ids (PGN, source address, known SPNs through <see cref="J1939LiveData"/>).
/// </summary>
public sealed class CanBusEntry
{
    internal CanBusEntry(uint id, bool isExtended)
    {
        Id = id;
        IsExtended = isExtended;
        if (isExtended)
        {
            var j = J1939Identifier.Parse(id);
            Pgn = j.Pgn;
            SourceAddress = (byte)j.Sa;
            Priority = (byte)j.Priority;
            PgnLabel = J1939PgnNames.Label(j.Pgn);
        }
    }

    public uint Id { get; }
    public bool IsExtended { get; }

    /// <summary>J1939 PGN (29-bit ids only).</summary>
    public uint? Pgn { get; }
    public byte? SourceAddress { get; }
    public byte? Priority { get; }

    /// <summary>Short PGN name, e.g. "EEC1", or null when we have no name for it.</summary>
    public string? PgnLabel { get; }

    public byte[] Data { get; private set; } = Array.Empty<byte>();
    public bool IsRemote { get; private set; }
    public long Count { get; private set; }
    public double FirstSeenSeconds { get; private set; }
    public double LastSeenSeconds { get; private set; }

    /// <summary>Smoothed time between frames, in seconds; null until the second frame.</summary>
    public double? PeriodSeconds { get; private set; }

    /// <summary>Bit i set = data byte i changed in the latest frame.</summary>
    public byte ChangedMask { get; private set; }

    public bool IsSimulated { get; private set; }

    /// <summary>Known SPN values from the latest frame (empty when the PGN is not in our decoder).</summary>
    public IReadOnlyList<J1939LiveDataValue> Decoded { get; private set; } = Array.Empty<J1939LiveDataValue>();

    /// <summary>
    /// For a single-frame DM1 (active) or DM2 (previously active) message: the lamp status and the DTCs it
    /// carries (SAE J1939-73 §5.7.1/§5.7.2). Null for every other PGN. Multi-packet DM1s arrive as TP.CM/TP.DT
    /// rows and are not reassembled here.
    /// </summary>
    public J1939DmMessage? DiagnosticMessage { get; private set; }

    public string IdText => IsExtended
        ? Id.ToString("X8", CultureInfo.InvariantCulture)
        : Id.ToString("X3", CultureInfo.InvariantCulture);

    public string DataText => IsRemote ? "(remote)" : string.Join(' ', Data.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    internal void Apply(CanFrame frame)
    {
        if (Count == 0)
        {
            FirstSeenSeconds = frame.TimestampSeconds;
            ChangedMask = 0;
        }
        else
        {
            var dt = frame.TimestampSeconds - LastSeenSeconds;
            if (dt >= 0)
                PeriodSeconds = PeriodSeconds is { } p ? p * 0.8 + dt * 0.2 : dt;

            byte mask = 0;
            var n = Math.Max(Data.Length, frame.Data.Length);
            for (var i = 0; i < n && i < 8; i++)
            {
                var before = i < Data.Length ? Data[i] : -1;
                var after = i < frame.Data.Length ? frame.Data[i] : -1;
                if (before != after)
                    mask |= (byte)(1 << i);
            }
            ChangedMask = mask;
        }

        Count++;
        LastSeenSeconds = frame.TimestampSeconds;
        Data = frame.Data;
        IsRemote = frame.IsRemote;
        IsSimulated = frame.IsSimulated;

        if (Pgn is { } pgn && !frame.IsRemote && J1939LiveData.IsKnownPgn(pgn))
            Decoded = J1939LiveData.Extract(pgn, frame.Data).Where(v => v.Known).ToArray();

        if (Pgn is J1939Pgn.Dm1 or J1939Pgn.Dm2 && !frame.IsRemote)
            DiagnosticMessage = DecodeDiagnosticMessage(frame.Data, Pgn == J1939Pgn.Dm2);
    }

    /// <summary>
    /// Decodes a DM1/DM2 payload and drops the placeholder DTCs J1939-73 uses for "no active fault"
    /// (SPN 0 / FMI 0) and padding (all bytes 0xFF), so only real faults are listed.
    /// </summary>
    public static J1939DmMessage DecodeDiagnosticMessage(ReadOnlySpan<byte> payload, bool isDm2)
    {
        var message = J1939DtcCodec.DecodeDmMessage(payload, isDm2);
        var real = new List<J1939Dtc>(message.Dtcs.Count);
        for (var i = 0; i < message.Dtcs.Count; i++)
        {
            var offset = 2 + i * 4;
            var raw = payload.Slice(offset, 4);
            if (raw[0] == 0xFF && raw[1] == 0xFF && raw[2] == 0xFF && raw[3] == 0xFF)
                continue;
            var dtc = message.Dtcs[i];
            if (dtc.Spn == 0 && dtc.Fmi == 0)
                continue;
            real.Add(dtc);
        }
        return message with { Dtcs = real };
    }
}

/// <summary>
/// The bus monitor's table: one <see cref="CanBusEntry"/> per (identifier, 11/29-bit). Not thread-safe: feed it
/// from one thread (the UI's batch pump).
/// </summary>
public sealed class CanBusTable
{
    private readonly Dictionary<(uint Id, bool Ext), CanBusEntry> _entries = new();

    public long TotalFrames { get; private set; }

    public int Count => _entries.Count;

    public IEnumerable<CanBusEntry> Entries => _entries.Values;

    /// <summary>Applies one frame; returns its row and whether the row is new.</summary>
    public (CanBusEntry Entry, bool IsNew) Apply(CanFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var key = (frame.Id, frame.IsExtended);
        var isNew = !_entries.TryGetValue(key, out var entry);
        if (isNew)
        {
            entry = new CanBusEntry(frame.Id, frame.IsExtended);
            _entries[key] = entry;
        }
        entry!.Apply(frame);
        TotalFrames++;
        return (entry, isNew);
    }

    public void Clear()
    {
        _entries.Clear();
        TotalFrames = 0;
    }

    /// <summary>
    /// Filter text match: blank matches everything; otherwise the id in hex, the PGN (decimal or hex), the PGN name,
    /// "SA xx" / source address in hex, or a decoded SPN name contains the text (case-insensitive).
    /// </summary>
    public static bool Matches(CanBusEntry entry, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;
        var f = filter.Trim();
        var cmp = StringComparison.OrdinalIgnoreCase;
        var idQuery = f.StartsWith("0x", cmp) && f.Length > 2 ? f[2..] : f;
        if (entry.IdText.Contains(idQuery, cmp))
            return true;
        if (entry.Pgn is { } pgn &&
            (pgn.ToString(CultureInfo.InvariantCulture) == f ||
             pgn.ToString("X4", CultureInfo.InvariantCulture).Equals(f.Replace("0x", "", cmp), cmp)))
            return true;
        if (entry.PgnLabel?.Contains(f, cmp) == true)
            return true;
        if (entry.SourceAddress is { } sa &&
            (f.Equals($"SA {sa:X2}", cmp) || f.Equals($"SA 0x{sa:X2}", cmp) || f.Equals($"SA{sa:X2}", cmp)))
            return true;
        if (entry.DiagnosticMessage is { } dm &&
            dm.Dtcs.Any(d => f.Equals($"SPN {d.Spn}", cmp) || f.Equals(d.Spn.ToString(CultureInfo.InvariantCulture), cmp)))
            return true;
        return entry.Decoded.Any(v => v.Name.Contains(f, cmp));
    }
}

/// <summary>Short names for the PGNs our J1939 code knows (SAE J1939-71/-73/-21/-81 acronyms).</summary>
public static class J1939PgnNames
{
    private static readonly Dictionary<uint, string> Names = new()
    {
        [J1939Pgn.AddressClaimed] = "Address Claimed",
        [J1939Pgn.Request] = "Request",
        [J1939Pgn.Acknowledgement] = "Acknowledgement",
        [J1939Pgn.TransportConnectionManagement] = "TP.CM",
        [J1939Pgn.TransportDataTransfer] = "TP.DT",
        [J1939Pgn.ElectronicEngineController1] = "EEC1",
        [J1939Pgn.ElectronicEngineController2] = "EEC2",
        [J1939Pgn.EngineTemperature1] = "ET1",
        [J1939Pgn.EngineFluidLevelPressure1] = "EFL/P1",
        [J1939Pgn.CruiseControlVehicleSpeed] = "CCVS",
        [J1939Pgn.EngineHoursRevolutions] = "HOURS",
        [J1939Pgn.AmbientConditions] = "AMB",
        [J1939Pgn.VehicleElectricalPower] = "VEP",
        [J1939Pgn.InletExhaustConditions] = "IC1",
        [J1939Pgn.FuelEconomy] = "LFE",
        [J1939Pgn.Dm1] = "DM1",
        [J1939Pgn.Dm2] = "DM2",
        [J1939Pgn.Dm3] = "DM3",
        [J1939Pgn.Dm11] = "DM11",
        [61442] = "ETC1",
    };

    public static string? Label(uint pgn) => Names.TryGetValue(pgn, out var name) ? name : null;
}
