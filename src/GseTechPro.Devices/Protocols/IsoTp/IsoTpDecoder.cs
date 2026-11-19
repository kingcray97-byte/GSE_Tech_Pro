namespace GseTechPro.Devices.Protocols.IsoTp;

/// <summary>
/// ISO 15765-2 (ISO-TP) transport framing over CAN. PCI (Protocol Control Information) is the first
/// byte of each frame; its high nibble selects the frame type:
/// <list type="bullet">
/// <item><b>Single Frame (SF)</b>: <c>0x0L</c>, L = payload length (1–7).</item>
/// <item><b>First Frame (FF)</b>: <c>0x1H</c>, H = high nibble of the 12-bit total length; second byte is
/// the low byte. Carries the first ≤6 payload bytes.</item>
/// <item><b>Consecutive Frame (CF)</b>: <c>0x2S</c>, S = sequence (1–15, wraps); 7 payload bytes.</item>
/// <item><b>Flow Control (FC)</b>: <c>0x3F</c>, F = flag (0 CTS, 1 wait, 2 overflow); bytes 2–3 = block
/// size and separation time. FC is a sender-side concern; the decoder ignores it.</item>
/// </list>
/// Layout per <b>ISO 15765-2</b> (single/multi-frame).
/// </summary>
public sealed class IsoTpDecoder
{
    public const int MaxSingleFramePayload = 7;
    public const int ConsecutiveFramePayload = 7;
    public const int MaxMessageLength = 4095; // 12-bit length field

    private byte[]? _buffer;
    private int _totalLength;
    private int _received;
    private int _expectedSequence;

    /// <summary>Feeds one ISO-TP frame (including its PCI byte); returns a completed payload, or null.</summary>
    public byte[]? Feed(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 1)
            return null;

        var pci = frame[0];
        switch (pci >> 4)
        {
            case 0x0: // Single frame
            {
                var length = pci & 0x0F;
                if (length == 0)
                    return null;
                var copyLength = Math.Min(length, frame.Length - 1);
                return frame.Slice(1, copyLength).ToArray();
            }

            case 0x1: // First frame
            {
                if (frame.Length < 2)
                    return null;
                _totalLength = ((pci & 0x0F) << 8) | frame[1];
                if (_totalLength <= 0 || _totalLength > MaxMessageLength)
                    return null;
                _buffer = new byte[_totalLength];
                _received = 0;
                var copyLength = Math.Min(_totalLength, frame.Length - 2);
                frame.Slice(2, copyLength).CopyTo(_buffer.AsSpan());
                _received = copyLength;
                _expectedSequence = 1;
                return null;
            }

            case 0x2: // Consecutive frame
            {
                if (_buffer is null)
                    return null;
                var sequence = pci & 0x0F;
                if (sequence != _expectedSequence)
                    return null; // out-of-order / lost frame: caller may retry or reset
                _expectedSequence = sequence == 0x0F ? 1 : sequence + 1;

                var copyLength = Math.Min(ConsecutiveFramePayload, _totalLength - _received);
                frame.Slice(1, copyLength).CopyTo(_buffer.AsSpan(_received));
                _received += copyLength;

                if (_received < _totalLength)
                    return null;

                var result = _buffer;
                Reset();
                return result;
            }

            default: // Flow control (0x3) — ignore
                return null;
        }
    }

    /// <summary>Clears in-flight reassembly state.</summary>
    public void Reset()
    {
        _buffer = null;
        _totalLength = 0;
        _received = 0;
        _expectedSequence = 0;
    }
}

/// <summary>Encodes an ISO-TP payload into single or multiple CAN frames (8 bytes each).</summary>
public static class IsoTpEncoder
{
    /// <summary>Splits <paramref name="payload"/> into a single frame, or a first frame + consecutive frames.</summary>
    public static IReadOnlyList<byte[]> Encode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > IsoTpDecoder.MaxMessageLength)
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length, "ISO-TP message exceeds 4095 bytes.");

        if (payload.Length <= IsoTpDecoder.MaxSingleFramePayload)
        {
            var single = new byte[payload.Length + 1];
            single[0] = (byte)payload.Length;
            payload.CopyTo(single.AsSpan(1));
            return new[] { single };
        }

        var frames = new List<byte[]>();

        var first = new byte[8];
        first[0] = (byte)(0x10 | ((payload.Length >> 8) & 0x0F));
        first[1] = (byte)(payload.Length & 0xFF);
        var firstData = Math.Min(6, payload.Length);
        payload.Slice(0, firstData).CopyTo(first.AsSpan(2));
        frames.Add(first);

        var sequence = 1;
        for (var offset = firstData; offset < payload.Length; offset += IsoTpDecoder.ConsecutiveFramePayload)
        {
            var consecutive = new byte[8];
            consecutive[0] = (byte)(0x20 | (sequence & 0x0F));
            var n = Math.Min(IsoTpDecoder.ConsecutiveFramePayload, payload.Length - offset);
            payload.Slice(offset, n).CopyTo(consecutive.AsSpan(1));
            frames.Add(consecutive);
            sequence = sequence == 0x0F ? 1 : sequence + 1;
        }

        return frames;
    }
}
