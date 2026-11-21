using GseTechPro.Core.Contracts;

namespace GseTechPro.Scope;

/// <summary>Which wire (or wire pair) the decoder reads. Dominant is the logical 0 on the bus.</summary>
public enum CanSignalSource
{
    /// <summary>CAN-H on one channel: dominant drives it high.</summary>
    CanHigh,
    /// <summary>CAN-L on one channel: dominant drives it low.</summary>
    CanLow,
    /// <summary>CAN-H on the first channel minus CAN-L on the second: dominant is the larger difference.</summary>
    Differential,
}

/// <summary>What to decode: channel(s), wire, and bit rate. The bit rate is chosen by the technician (for J1939 it is set
/// by the network: 250 kbit/s per J1939-11/-15 or 500 kbit/s per J1939-14); it is never guessed from the trace.</summary>
public sealed record CanDecodeSettings(int Channel, CanSignalSource Source, int BitRate, int SecondChannel = 2)
{
    /// <summary>Standard CAN bit rates offered in the picker.</summary>
    public static IReadOnlyList<int> StandardBitRates { get; } = new[] { 125_000, 250_000, 500_000, 1_000_000 };

    /// <summary>Where in the bit the level is read (fraction of the bit time). 75 % is a common CAN sample point.</summary>
    public const double SamplePoint = 0.75;
}

/// <summary>One decoded CAN 2.0A/B data or remote frame (ISO 11898-1), or a frame that failed a check.</summary>
public sealed record ScopeCanFrame(
    int Number,
    double StartSeconds,
    uint Identifier,
    bool IsExtended,
    bool IsRemote,
    int Dlc,
    byte[] Data,
    bool CrcOk,
    bool Acknowledged,
    string? Error)
{
    public bool IsOk => Error is null;

    public string IdentifierHex => IsExtended ? Identifier.ToString("X8") : Identifier.ToString("X3");

    public string DataHex => string.Join(' ', Data.Select(b => b.ToString("X2")));
}

/// <summary>Result of decoding one capture: frames in time order, or a plain error.</summary>
public sealed record CanDecodeResult(IReadOnlyList<ScopeCanFrame> Frames, bool IsSimulated, string? Error)
{
    public bool IsOk => Error is null;

    public int ErrorFrameCount => Frames.Count(f => !f.IsOk);

    public static CanDecodeResult Fail(bool isSimulated, string error) => new(Array.Empty<ScopeCanFrame>(), isSimulated, error);
}

/// <summary>
/// Decodes CAN frames from a scope trace (Phase 10D.1 serial decoding, our own implementation of the public
/// ISO 11898-1 frame format). The trace is turned into dominant/recessive with a threshold halfway between the
/// trace's own low and high levels, a frame starts at a recessive→dominant edge after at least 10 recessive bit times
/// (bus idle), bit timing re-synchronises on every recessive→dominant edge, stuff bits are removed and checked, and
/// the CRC-15 is checked. Frames cut off by the end of the capture are reported as such; a frame already running at
/// the start of the capture is skipped (no idle before it).
/// </summary>
public static class CanSerialDecoder
{
    /// <summary>Recessive bit times needed before a start-of-frame (EOF 7 + intermission 3).</summary>
    public const int IdleBitsBeforeFrame = 10;

    public const int MaxFrames = 2_000;

    public static CanDecodeResult Decode(ScopeCapture capture, CanDecodeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(settings);
        string chName = MathChannelDefinition.ChannelName(settings.Channel);
        if (!capture.Channels.TryGetValue(settings.Channel, out var a) || a.Length == 0)
            return CanDecodeResult.Fail(capture.IsSimulated, $"Channel {chName} is not in this capture.");

        double[] signal;
        if (settings.Source == CanSignalSource.Differential)
        {
            if (!capture.Channels.TryGetValue(settings.SecondChannel, out var b) || b.Length == 0)
                return CanDecodeResult.Fail(capture.IsSimulated,
                    $"Channel {MathChannelDefinition.ChannelName(settings.SecondChannel)} (CAN-L) is not in this capture.");
            int n = Math.Min(a.Length, b.Length);
            signal = new double[n];
            for (int i = 0; i < n; i++) signal[i] = a[i] - b[i];
        }
        else
        {
            signal = a;
        }

        bool dominantHigh = settings.Source != CanSignalSource.CanLow;
        return Decode(signal, capture.SampleRateHz, settings.BitRate, dominantHigh, capture.IsSimulated);
    }

    public static CanDecodeResult Decode(double[] x, double sampleRateHz, int bitRate, bool dominantHigh, bool isSimulated = false)
    {
        ArgumentNullException.ThrowIfNull(x);
        if (bitRate <= 0)
            return CanDecodeResult.Fail(isSimulated, "Choose the bus bit rate.");
        if (!double.IsFinite(sampleRateHz) || sampleRateHz <= 0)
            return CanDecodeResult.Fail(isSimulated, "The capture has no valid sample rate.");
        double samplesPerBit = sampleRateHz / bitRate;
        if (samplesPerBit < 4)
            return CanDecodeResult.Fail(isSimulated,
                $"Sample rate too low to decode {bitRate / 1000.0:0.#} kbit/s: need at least {4.0 * bitRate / 1e6:0.#} MS/s (4 samples per bit); use a shorter timebase.");

        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var v in x)
        {
            if (!double.IsFinite(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        if (!double.IsFinite(min) || max - min < 0.2)
            return CanDecodeResult.Fail(isSimulated, "No bus activity: the trace has no dominant/recessive switching.");

        // Logic level per sample with hysteresis: true = dominant.
        double mid = (max + min) / 2, hyst = (max - min) * 0.1;
        var dom = new bool[x.Length];
        bool state = dominantHigh ? x[0] > mid : x[0] < mid;
        for (int i = 0; i < x.Length; i++)
        {
            double v = double.IsFinite(x[i]) ? x[i] : mid;
            double d = dominantHigh ? v - mid : mid - v;
            if (d > hyst) state = true;
            else if (d < -hyst) state = false;
            dom[i] = state;
        }

        var frames = new List<ScopeCanFrame>();
        int idx = 0;
        int recessiveRun = 0; // samples of recessive before idx
        while (idx < dom.Length && frames.Count < MaxFrames)
        {
            if (!dom[idx])
            {
                recessiveRun++;
                idx++;
                continue;
            }
            bool idle = recessiveRun >= IdleBitsBeforeFrame * samplesPerBit - samplesPerBit / 2;
            if (!idle)
            {
                // Dominant without bus idle before it: inside a frame we did not see start, or a stray glitch. Skip it.
                while (idx < dom.Length && dom[idx]) idx++;
                recessiveRun = 0;
                continue;
            }

            var frame = ReadFrame(dom, idx, samplesPerBit, sampleRateHz, frames.Count + 1, out int endIdx);
            frames.Add(frame);
            idx = Math.Max(endIdx, idx + 1);
            // A good frame ends with ACK delimiter + 7 EOF bits, all recessive: they count towards the next idle.
            recessiveRun = frame.IsOk ? (int)(8 * samplesPerBit) : 0;
        }

        return new CanDecodeResult(frames, isSimulated, null);
    }

    /// <summary>Reads one frame starting at the SOF edge; returns the sample index where reading stopped.</summary>
    private static ScopeCanFrame ReadFrame(bool[] dom, int sofIdx, double spb, double sampleRateHz, int number, out int endIdx)
    {
        double bitStart = sofIdx;
        var bits = new List<bool>(160); // destuffed logic bits, true = 1 (recessive)
        int sameCount = 0;
        bool lastBit = true;
        bool stuffing = true;
        int crcEndIndex = -1; // index in bits after the CRC sequence
        string? error = null;
        int rawBits = 0;

        bool? ReadRawBit()
        {
            // Re-synchronise: a recessive→dominant edge within ±half a bit of the expected start moves the bit start.
            int from = (int)Math.Max(0, bitStart - spb / 2), to = (int)Math.Min(dom.Length - 1, bitStart + spb / 2);
            for (int i = Math.Max(1, from); i <= to; i++)
                if (dom[i] && !dom[i - 1]) { bitStart = i; break; }
            int s = (int)Math.Round(bitStart + spb * CanDecodeSettings.SamplePoint);
            if (s >= dom.Length) return null;
            bool level = dom[s];
            bitStart += spb;
            rawBits++;
            return !level; // logic 1 = recessive
        }

        // Fields after SOF, read bit by bit with destuffing until the CRC sequence ends.
        uint id = 0;
        bool ide = false, rtr = false;
        int dlc = 0, dataBits = 0;
        while (true)
        {
            var raw = ReadRawBit();
            if (raw is not bool bit)
            {
                error = "Cut off by the end of the capture.";
                break;
            }
            if (stuffing && sameCount == 5)
            {
                if (bit == lastBit) { error = $"Stuff error at bit {bits.Count}."; break; }
                lastBit = bit;
                sameCount = 1;
                continue; // stuff bit removed
            }
            if (bit == lastBit) sameCount++; else { sameCount = 1; lastBit = bit; }
            bits.Add(bit);

            int n = bits.Count;
            if (n == 1 && bit) { error = "Start of frame is not dominant."; break; }
            if (n == 14) // SOF + 11 ID + RTR/SRR + IDE
            {
                ide = bits[13];
                if (!ide) rtr = bits[12];
            }
            if (!ide && n == 19) // + r0 + DLC4
            {
                id = ToUInt(bits, 1, 11);
                dlc = (int)ToUInt(bits, 15, 4);
                dataBits = rtr ? 0 : Math.Min(dlc, 8) * 8;
            }
            if (ide && n == 39) // SOF, ID-A 11, SRR, IDE, ID-B 18, RTR, r1, r0, DLC 4
            {
                id = (ToUInt(bits, 1, 11) << 18) | ToUInt(bits, 14, 18);
                rtr = bits[32];
                dlc = (int)ToUInt(bits, 35, 4);
                dataBits = rtr ? 0 : Math.Min(dlc, 8) * 8;
            }
            int headerBits = ide ? 39 : 19;
            if (n >= headerBits && n == headerBits + dataBits + 15)
            {
                crcEndIndex = n;
                stuffing = false;
                if (sameCount == 5)
                {
                    // A stuff bit can follow the last CRC bit.
                    var stuff = ReadRawBit();
                    if (stuff is null) error = "Cut off by the end of the capture.";
                    else if (stuff == lastBit) error = "Stuff error after the CRC.";
                }
                break;
            }
        }

        byte[] data = Array.Empty<byte>();
        bool crcOk = false, ack = false;
        if (crcEndIndex > 0 && error is null)
        {
            int headerBits = ide ? 39 : 19;
            data = new byte[dataBits / 8];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)ToUInt(bits, headerBits + i * 8, 8);
            uint crc = ToUInt(bits, headerBits + dataBits, 15);
            crcOk = Crc15(bits, headerBits + dataBits) == crc;

            // Trailer, not stuffed: CRC delimiter (1), ACK slot (0 when a node acknowledged), ACK delimiter (1), EOF 7×1.
            var crcDelim = ReadRawBit();
            var ackSlot = ReadRawBit();
            var ackDelim = ReadRawBit();
            if (crcDelim is null || ackSlot is null || ackDelim is null)
                error = "Cut off by the end of the capture.";
            else
            {
                ack = ackSlot == false;
                if (!crcOk) error = "CRC mismatch.";
                else if (crcDelim != true || ackDelim != true) error = "Form error (CRC or ACK delimiter not recessive).";
                else
                {
                    for (int i = 0; i < 7; i++)
                    {
                        var eof = ReadRawBit();
                        if (eof is null) { error = "Cut off by the end of the capture."; break; }
                        if (eof != true) { error = "Form error in end of frame."; break; }
                    }
                }
            }
        }
        else if (error is null)
        {
            error = "Frame could not be read.";
        }

        // Continue after this frame: at least to where we stopped reading.
        endIdx = (int)Math.Min(dom.Length, Math.Ceiling(bitStart));
        if (error is not null && !error.StartsWith("Cut off", StringComparison.Ordinal))
        {
            // After a bad frame skip to the next stretch of idle; the main loop requires idle before the next SOF.
            while (endIdx < dom.Length && dom[endIdx]) endIdx++;
        }
        return new ScopeCanFrame(number, sofIdx / sampleRateHz, id, ide, rtr, dlc, data, crcOk, ack, error);
    }

    private static uint ToUInt(List<bool> bits, int start, int count)
    {
        uint v = 0;
        for (int i = 0; i < count; i++) v = (v << 1) | (bits[start + i] ? 1u : 0u);
        return v;
    }

    /// <summary>CAN CRC-15 (polynomial 0x4599) over the first <paramref name="count"/> destuffed bits (SOF through data).</summary>
    public static uint Crc15(IReadOnlyList<bool> bits, int count)
    {
        uint crc = 0;
        for (int i = 0; i < count; i++)
        {
            bool next = bits[i] != (((crc >> 14) & 1) == 1);
            crc = (crc << 1) & 0x7FFF;
            if (next) crc ^= 0x4599;
        }
        return crc;
    }

    /// <summary>
    /// Builds the bus bit sequence (true = recessive) of one frame including stuff bits, CRC, delimiters, ACK slot and
    /// EOF. Used by the simulator and tests; an <paramref name="acknowledged"/> frame has a dominant ACK slot.
    /// </summary>
    public static List<bool> EncodeFrame(uint identifier, bool extended, byte[] data, bool acknowledged = true, bool remote = false)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > 8) throw new ArgumentException("CAN 2.0 carries at most 8 data bytes.", nameof(data));
        var b = new List<bool> { false }; // SOF
        void Put(uint v, int count) { for (int i = count - 1; i >= 0; i--) b.Add(((v >> i) & 1) == 1); }
        if (extended)
        {
            Put(identifier >> 18, 11);
            b.Add(true); // SRR
            b.Add(true); // IDE
            Put(identifier & 0x3FFFF, 18);
            b.Add(remote);
            b.Add(false); // r1
            b.Add(false); // r0
        }
        else
        {
            Put(identifier, 11);
            b.Add(remote);
            b.Add(false); // IDE
            b.Add(false); // r0
        }
        Put((uint)data.Length, 4);
        if (!remote) foreach (var d in data) Put(d, 8);
        Put(Crc15(b, b.Count), 15);

        var stuffed = new List<bool>(b.Count + 30);
        int same = 0;
        bool last = true;
        foreach (var bit in b)
        {
            if (same == 5)
            {
                stuffed.Add(!last);
                last = !last;
                same = 1;
            }
            stuffed.Add(bit);
            if (bit == last) same++; else { same = 1; last = bit; }
        }
        if (same == 5) stuffed.Add(!last); // a stuff bit can follow the last CRC bit

        stuffed.Add(true); // CRC delimiter
        stuffed.Add(!acknowledged); // ACK slot
        stuffed.Add(true); // ACK delimiter
        for (int i = 0; i < 7; i++) stuffed.Add(true); // EOF
        return stuffed;
    }
}
