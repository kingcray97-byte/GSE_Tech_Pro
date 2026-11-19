namespace GseTechPro.Devices.Protocols.J1939;

/// <summary>
/// A completed multi-frame J1939 message reassembled from Transport Protocol (BAM/CMDT) frames.
/// </summary>
public readonly record struct J1939Reassembled(
    uint Pgn,
    byte SourceAddress,
    byte DestinationAddress,
    byte[] Data);

/// <summary>
/// Reassembles multi-frame J1939 messages from the Transport Protocol, defined in
/// <b>SAE J1939-21</b> §5.10 (Transport Protocol Functions).
///
/// Two message types cooperate:
/// <list type="bullet">
/// <item><b>TP.CM</b> (PGN 60416): [control, sizeLo, sizeHi, numPackets, 0xFF, pgnLo, pgnMid, pgnHi].
/// Control byte 0x20 = BAM (Broadcast Announce Message), 0x10 = RTS (Request to Send),
/// 0x11 = CTS, 0x13 = End-of-Message ACK, 0xFF = Abort.</item>
/// <item><b>TP.DT</b> (PGN 60160): [sequence, 7 payload bytes]. Sequence runs 1..numPackets.</item>
/// </list>
///
/// The receiver keys in-flight transfers by (source address, destination address); the destination
/// is the broadcast address (0xFF) for BAM and the peer address for connection-mode (RTS/CTS). This
/// handles both modes: the tool listens for BAM broadcasts (the normal DM1/DM2 case) and also
/// collects connection-mode data packets after an RTS announcement.
/// </summary>
public sealed class J1939TransportProtocol
{
    private readonly Dictionary<ulong, Transfer> _transfers = new();

    /// <summary>Feeds one CAN frame (29-bit id + data); returns a completed message when one finishes.</summary>
    public J1939Reassembled? Feed(uint rawIdentifier, ReadOnlySpan<byte> data)
    {
        var id = J1939Identifier.Parse(rawIdentifier);

        switch (id.Pgn)
        {
            case J1939Pgn.TransportConnectionManagement:
                HandleConnectionManagement(id, data);
                return null;

            case J1939Pgn.TransportDataTransfer:
                return HandleDataTransfer(id, data);

            default:
                return null;
        }
    }

    /// <summary>Drops all in-flight transfers (e.g. on disconnect).</summary>
    public void Reset() => _transfers.Clear();

    private void HandleConnectionManagement(J1939Identifier id, ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
            return;

        var control = data[0];
        // 0x20 = BAM, 0x10 = RTS. CTS (0x11)/EOM-ACK (0x13)/Abort (0xFF) are sender-side concerns.
        if (control is not (0x20 or 0x10))
            return;

        var totalSize = data[1] | (data[2] << 8);
        var numPackets = data[3];
        var pgn = (uint)(data[5] | (data[6] << 8) | (data[7] << 16));

        if (totalSize <= 0 || totalSize > 1785) // J1939-21 limits a message to 1785 bytes (255 × 7)
            return;

        _transfers[Key((byte)id.Sa, (byte)id.Ps)] = new Transfer(pgn, (byte)id.Sa, (byte)id.Ps, totalSize, numPackets);
    }

    private J1939Reassembled? HandleDataTransfer(J1939Identifier id, ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
            return null;

        if (!_transfers.TryGetValue(Key((byte)id.Sa, (byte)id.Ps), out var transfer))
            return null;

        var sequence = data[0];
        if (sequence == 0)
            return null;

        var offset = (sequence - 1) * Transfer.PayloadPerPacket;
        if (offset >= transfer.TotalSize)
            return null;

        var copyLength = Math.Min(Transfer.PayloadPerPacket, transfer.TotalSize - offset);
        data.Slice(1, copyLength).CopyTo(transfer.Buffer.AsSpan(offset, copyLength));
        transfer.BytesReceived += copyLength;

        if (transfer.BytesReceived < transfer.TotalSize)
            return null;

        _transfers.Remove(Key(transfer.SourceAddress, transfer.DestinationAddress));
        return new J1939Reassembled(transfer.Pgn, transfer.SourceAddress, transfer.DestinationAddress, transfer.Buffer);
    }

    private static ulong Key(byte sourceAddress, byte destinationAddress) =>
        ((ulong)sourceAddress << 8) | destinationAddress;

    private sealed class Transfer
    {
        public const int PayloadPerPacket = 7;

        public Transfer(uint pgn, byte sourceAddress, byte destinationAddress, int totalSize, int numPackets)
        {
            Pgn = pgn;
            SourceAddress = sourceAddress;
            DestinationAddress = destinationAddress;
            TotalSize = totalSize;
            NumPackets = numPackets;
            Buffer = new byte[totalSize];
        }

        public uint Pgn { get; }
        public byte SourceAddress { get; }
        public byte DestinationAddress { get; }
        public int TotalSize { get; }
        public int NumPackets { get; }
        public byte[] Buffer { get; }
        public int BytesReceived { get; set; }
    }
}
