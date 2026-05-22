using System.Text;

namespace NINA.Headless.Services.Planetary;

/// <summary>
/// Writes a SER v3 file — the de-facto standard for planetary
/// astrophotography video recordings (AutoStakkert!, RegiStax, PIPP).
///
/// Layout:
///   [0..14)   FileID = "LUCAM-RECORDER" (14 ASCII)
///   [14..18)  LuID = 0 (uint32 LE, unused)
///   [18..22)  ColorID (uint32 LE) — Mono / BayerRGGB / RGB / etc.
///   [22..26)  LittleEndian flag (uint32 LE, 1 = LE, 0 = BE)
///   [26..30)  Width (uint32 LE)
///   [30..34)  Height (uint32 LE)
///   [34..38)  PixelDepthPerPlane (uint32 LE, 8 or 16)
///   [38..42)  FrameCount (uint32 LE) — patched on close
///   [42..82)  Observer (40 ASCII, null-padded)
///   [82..122) Instrument (40 ASCII)
///   [122..162) Telescope (40 ASCII)
///   [162..170) DateTimeUtc (int64 LE — .NET Ticks of UTC start)
///   [170..178) DateTimeUtcOffset (int64 LE — .NET Ticks of local start)
///   [178..)    Frame data, then optional timestamp trailer
///
/// Trailer (after all frames) is FrameCount × int64 LE of .NET Ticks
/// per frame. Polaris writes it on Dispose.
///
/// Spec: http://www.grischa-hahn.homepage.t-online.de/astro/ser/
/// </summary>
public sealed class SerFileWriter : IDisposable {
    public const int HeaderSize = 178;
    private const string FileId = "LUCAM-RECORDER";

    private readonly FileStream _fs;
    private readonly List<DateTime> _frameTimestamps = new();
    private readonly int _bytesPerFrame;
    private readonly DateTime _startUtc;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public int BitDepth { get; }
    public SerColorMode ColorMode { get; }
    public string Observer { get; }
    public string Instrument { get; }
    public string Telescope { get; }
    public int FrameCount => _frameTimestamps.Count;
    public long BytesWritten => _fs.Length;
    public string Path { get; }

    public SerFileWriter(string path, int width, int height, int bitDepth,
                          SerColorMode colorMode = SerColorMode.Mono,
                          string observer = "Polaris",
                          string instrument = "",
                          string telescope = "") {
        if (width <= 0 || height <= 0) throw new ArgumentException("width/height must be positive");
        if (bitDepth != 8 && bitDepth != 16) throw new ArgumentException("bitDepth must be 8 or 16");

        Path = path;
        Width = width;
        Height = height;
        BitDepth = bitDepth;
        ColorMode = colorMode;
        Observer = observer ?? "";
        Instrument = instrument ?? "";
        Telescope = telescope ?? "";

        int planes = colorMode is SerColorMode.Rgb or SerColorMode.Bgr ? 3 : 1;
        _bytesPerFrame = width * height * planes * (bitDepth / 8);
        _startUtc = DateTime.UtcNow;

        // Make sure the directory exists (caller may have just composed
        // the path off a target name).
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        WriteHeader(frameCount: 0);    // patched on Dispose
    }

    /// <summary>Append a frame. <paramref name="frameBytes"/> length must
    /// equal Width × Height × planes × (BitDepth / 8). For 16-bit, bytes
    /// are little-endian per the SER spec we wrote in the header.</summary>
    public void WriteFrame(byte[] frameBytes, DateTime? utc = null) {
        if (_disposed) throw new ObjectDisposedException(nameof(SerFileWriter));
        if (frameBytes.Length != _bytesPerFrame)
            throw new ArgumentException(
                $"Frame size mismatch: expected {_bytesPerFrame} bytes, got {frameBytes.Length}");
        _fs.Write(frameBytes, 0, frameBytes.Length);
        _frameTimestamps.Add(utc ?? DateTime.UtcNow);
    }

    /// <summary>Convenience overload for ushort[] payloads. Encodes
    /// little-endian to match the header flag.</summary>
    public void WriteFrame(ushort[] pixels, DateTime? utc = null) {
        if (BitDepth != 16) throw new InvalidOperationException("ushort overload requires BitDepth=16");
        var bytes = new byte[pixels.Length * 2];
        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
        WriteFrame(bytes, utc);
    }

    private void WriteHeader(int frameCount) {
        // Patching path: save current position so we can return to it
        // after rewriting the 178-byte header. On the very first call
        // (from the constructor) position is already 0, so we end up
        // sitting at byte 178 (= HeaderSize) which is exactly where
        // WriteFrame should start. No seek-back needed in that case.
        var pos = _fs.Position;
        if (pos != 0) _fs.Seek(0, SeekOrigin.Begin);
        using (var w = new BinaryWriter(_fs, Encoding.ASCII, leaveOpen: true)) {
            w.Write(Encoding.ASCII.GetBytes(FileId.PadRight(14)));
            w.Write((uint)0);                          // LuID
            w.Write((uint)ColorMode);
            w.Write((uint)1);                          // LittleEndian flag
            w.Write((uint)Width);
            w.Write((uint)Height);
            w.Write((uint)BitDepth);
            w.Write((uint)frameCount);
            w.Write(PadAscii(Observer,   40));
            w.Write(PadAscii(Instrument, 40));
            w.Write(PadAscii(Telescope,  40));
            w.Write(_startUtc.Ticks);                  // DateTimeUtc
            w.Write(_startUtc.ToLocalTime().Ticks);    // DateTimeUtcOffset
        }
        // Restore only when patching (pos was beyond the header).
        if (pos != 0) _fs.Seek(pos, SeekOrigin.Begin);
    }

    private static byte[] PadAscii(string s, int len) {
        var bytes = new byte[len];
        var src = Encoding.ASCII.GetBytes(s ?? "");
        Array.Copy(src, bytes, Math.Min(src.Length, len));
        return bytes;
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        try {
            // Trailer: frame timestamps as int64 little-endian, per spec.
            // Position cursor at end-of-data (cursor may already be there
            // after the last WriteFrame, but be safe).
            _fs.Seek(HeaderSize + (long)_bytesPerFrame * _frameTimestamps.Count, SeekOrigin.Begin);
            using (var w = new BinaryWriter(_fs, Encoding.ASCII, leaveOpen: true)) {
                foreach (var t in _frameTimestamps) w.Write(t.Ticks);
            }
            // Patch frame count in header.
            WriteHeader(_frameTimestamps.Count);
            _fs.Flush();
        } finally { _fs.Dispose(); }
    }
}

/// <summary>SER color modes per spec (values are wire-level — don't renumber).</summary>
public enum SerColorMode : uint {
    Mono       = 0,
    BayerRGGB  = 8,
    BayerGRBG  = 9,
    BayerGBRG  = 10,
    BayerBGGR  = 11,
    BayerCYYM  = 16,
    BayerYCMY  = 17,
    BayerYMCY  = 18,
    BayerMYYC  = 19,
    Rgb        = 100,
    Bgr        = 101
}
