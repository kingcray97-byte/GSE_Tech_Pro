using System.Buffers.Binary;
using System.IO.Compression;
using GseTechPro.Core.Models;
using GseTechPro.Core.Abstractions;

namespace GseTechPro.Devices.Camera;

/// <summary>
/// Simulator camera (Phase 7.1, rule 4): every photo is our own drawn test card (no third-party
/// artwork), different for each shot so photos can be told apart, always marked simulated.
/// </summary>
public sealed class SimulatedCameraSource : ICameraSource
{
    public const string SimulatorId = "camera-sim";
    public const int Width = 640;
    public const int Height = 480;

    public static CameraSourceInfo SimulatorInfo { get; } = new(SimulatorId, "Simulator camera", IsSimulated: true);

    private int _shot;

    public CameraSourceInfo Info => SimulatorInfo;

    public Task<CapturedPhoto> CapturePhotoAsync(CancellationToken cancellationToken = default)
    {
        int shot = Interlocked.Increment(ref _shot);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rgb = DrawTestCard(shot);
            return new CapturedPhoto
            {
                Bytes = PngEncoder.EncodeRgb(rgb, Width, Height),
                MimeType = "image/png",
                Extension = ".png",
                Width = Width,
                Height = Height,
                IsSimulated = true,
                TakenAt = Clock.UtcNow,
                SourceName = Info.Name,
            };
        }, cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Grey gradient, colour bars, a plate-like panel with a grid, and a row of blocks that encode the
    /// shot number in binary (so each photo differs). RGB, 3 bytes per pixel, top row first.
    /// </summary>
    public static byte[] DrawTestCard(int shot)
    {
        var px = new byte[Width * Height * 3];
        void Set(int x, int y, byte r, byte g, byte b)
        {
            int i = (y * Width + x) * 3;
            px[i] = r; px[i + 1] = g; px[i + 2] = b;
        }

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                byte v = (byte)(60 + 120 * (x + y) / (Width + Height));
                Set(x, y, v, v, v);
            }
        }

        // Colour bars along the top.
        byte[][] bars =
        [
            [230, 230, 230], [230, 230, 40], [40, 230, 230], [40, 200, 40],
            [220, 40, 220], [220, 40, 40], [40, 40, 220], [20, 20, 20],
        ];
        int barW = Width / bars.Length;
        for (int y = 0; y < 80; y++)
            for (int x = 0; x < Width; x++)
            {
                var c = bars[Math.Min(bars.Length - 1, x / barW)];
                Set(x, y, c[0], c[1], c[2]);
            }

        // A brushed-metal "plate" with a dark border and grid lines.
        for (int y = 140; y < 360; y++)
            for (int x = 120; x < 520; x++)
            {
                bool border = x < 126 || x >= 514 || y < 146 || y >= 354;
                bool grid = (x - 126) % 64 == 0 || (y - 146) % 52 == 0;
                byte v = border ? (byte)30 : grid ? (byte)110 : (byte)(185 + ((x * 7 + y * 3) % 17));
                Set(x, y, v, v, (byte)Math.Min(255, v + 8));
            }

        // Shot number in binary: 12 blocks, filled = 1.
        for (int bit = 0; bit < 12; bit++)
        {
            bool on = ((shot >> (11 - bit)) & 1) == 1;
            int x0 = 80 + bit * 40;
            for (int y = 400; y < 440; y++)
                for (int x = x0; x < x0 + 32; x++)
                    Set(x, y, on ? (byte)250 : (byte)40, on ? (byte)170 : (byte)40, on ? (byte)20 : (byte)40);
        }

        return px;
    }
}

/// <summary>Minimal PNG writer (8-bit RGB, zlib deflate), so the simulator needs no image library.</summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] EncodeRgb(byte[] rgb, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        if (width <= 0 || height <= 0 || rgb.Length != width * height * 3)
            throw new ArgumentException("Pixel buffer does not match width × height × 3.", nameof(rgb));

        using var ms = new MemoryStream();
        ms.Write(Signature);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // colour type: RGB
        WriteChunk(ms, "IHDR", ihdr);

        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            {
                int stride = width * 3;
                for (int y = 0; y < height; y++)
                {
                    z.WriteByte(0); // filter: none
                    z.Write(rgb, y * stride, stride);
                }
            }
            WriteChunk(ms, "IDAT", raw.ToArray());
        }

        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32(typeBytes, data);
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}

/// <summary>Camera factory with only the simulator (tests, and machines with no camera API). The App adds the tablet camera.</summary>
public sealed class SimulatedCameraSourceFactory : ICameraSourceFactory
{
    public Task<IReadOnlyList<CameraSourceInfo>> DiscoverAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CameraSourceInfo>>([SimulatedCameraSource.SimulatorInfo]);

    public Task<ICameraSource> OpenAsync(CameraSourceInfo info, CancellationToken cancellationToken = default) =>
        info.Id == SimulatedCameraSource.SimulatorId
            ? Task.FromResult<ICameraSource>(new SimulatedCameraSource())
            : throw new InvalidOperationException($"Camera '{info.Name}' is not available here.");
}
