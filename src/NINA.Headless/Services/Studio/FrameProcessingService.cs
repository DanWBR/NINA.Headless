using System.Collections.Concurrent;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
// `NINA.Image` (our namespace) shadows `SixLabors.ImageSharp.Image`, so
// alias the ImageSharp class explicitly.
using IsImage = SixLabors.ImageSharp.Image;

namespace NINA.Headless.Services.Studio;

/// <summary>
/// On-demand image processing for STUDIO's single-frame viewer:
///   - render stretched JPEG/PNG previews (caller-supplied black/mid/white)
///   - compute full statistics + star detection
///   - export TIFF/PNG/JPEG to the {rig}/processed/{target}/ tree
///
/// Slider drags hit /preview many times per second, so the decoded FITS
/// pixel buffer is kept in a small in-memory LRU keyed by frame id. The
/// stretch itself is just an LUT pass — cheap enough that we don't bother
/// caching the rendered bytes.
/// </summary>
public class FrameProcessingService {
    private readonly FrameLibraryService _library;
    private readonly ProfileService _profile;
    private readonly ILogger<FrameProcessingService> _logger;

    // Tiny LRU — decoded FITS buffers are big (64MP × 2 bytes = 128 MB).
    // Four entries keeps slider drags responsive when the user is
    // alt-tabbing between two or three frames, no more.
    private const int CacheCapacity = 4;
    private readonly object _cacheLock = new();
    private readonly LinkedList<CachedFrame> _cache = new();

    public FrameProcessingService(FrameLibraryService library, ProfileService profile,
                                  ILogger<FrameProcessingService> logger) {
        _library = library;
        _profile = profile;
        _logger = logger;
    }

    private record CachedFrame(int Id, BaseImageData Data);

    private BaseImageData? LoadCached(int frameId) {
        lock (_cacheLock) {
            for (var n = _cache.First; n != null; n = n.Next) {
                if (n.Value.Id == frameId) {
                    _cache.Remove(n);
                    _cache.AddFirst(n);
                    return n.Value.Data;
                }
            }
        }

        var row = _library.GetById(frameId);
        if (row == null || !File.Exists(row.Path)) return null;

        BaseImageData decoded;
        try {
            using var fs = File.OpenRead(row.Path);
            decoded = FITSReader.Read(fs);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "FITS decode failed for frame {Id} ({Path})", frameId, row.Path);
            return null;
        }

        lock (_cacheLock) {
            _cache.AddFirst(new CachedFrame(frameId, decoded));
            while (_cache.Count > CacheCapacity) _cache.RemoveLast();
        }
        return decoded;
    }

    public void Invalidate(int frameId) {
        lock (_cacheLock) {
            for (var n = _cache.First; n != null; n = n.Next) {
                if (n.Value.Id == frameId) { _cache.Remove(n); break; }
            }
        }
    }

    /// <summary>Stretch parameters used for a preview / export. Any field
    /// left null is auto-computed.</summary>
    public record StretchOptions(double? Black, double? Mid, double? White);

    /// <summary>Render a stretched preview as JPEG bytes. maxSize caps the
    /// long side in pixels (default 1600, plenty for a browser viewer).</summary>
    public async Task<byte[]?> RenderJpegAsync(int frameId, StretchOptions opts,
                                               int maxSize = 1600, int quality = 85,
                                               CancellationToken ct = default) {
        var img = LoadCached(frameId);
        if (img == null) return null;
        var stretched = ApplyStretch(img, opts);
        using var image = IsImage.LoadPixelData<L8>(stretched, img.Properties.Width, img.Properties.Height);
        ResizeIfNeeded(image, maxSize);
        await using var ms = new MemoryStream();
        await image.SaveAsJpegAsync(ms, new JpegEncoder { Quality = quality }, ct);
        return ms.ToArray();
    }

    /// <summary>Render a stretched preview as 8-bit PNG bytes.</summary>
    public async Task<byte[]?> RenderPngAsync(int frameId, StretchOptions opts,
                                              int maxSize = 1600, CancellationToken ct = default) {
        var img = LoadCached(frameId);
        if (img == null) return null;
        var stretched = ApplyStretch(img, opts);
        using var image = IsImage.LoadPixelData<L8>(stretched, img.Properties.Width, img.Properties.Height);
        ResizeIfNeeded(image, maxSize);
        await using var ms = new MemoryStream();
        await image.SaveAsPngAsync(ms, ct);
        return ms.ToArray();
    }

    /// <summary>Compute the auto-stretch defaults the UI should seed
    /// sliders with — black/mid/white normalised 0..1 — without applying
    /// or rendering anything.</summary>
    public AutoStretch.StretchParams? AutoStretchDefaults(int frameId) {
        var img = LoadCached(frameId);
        if (img == null) return null;
        return AutoStretch.ComputeAutoStretchParams(
            img.Data, img.Properties.Width, img.Properties.Height, img.Properties.BitDepth);
    }

    /// <summary>Full statistics + detected-star summary for the frame.
    /// The star list is capped at 500 entries — that's what StarDetector
    /// returns by default and it's more than enough for a viewer overlay.</summary>
    public FrameStats? ComputeStats(int frameId, bool includeStars = true) {
        var img = LoadCached(frameId);
        if (img == null) return null;
        var s = ImageStatistics.Create(img);
        var stars = new List<DetectedStarDto>();
        double hfrAvg = 0;
        if (includeStars) {
            try {
                var detected = new StarDetector { MaxStars = 500 }.Detect(
                    img.Data, img.Properties.Width, img.Properties.Height);
                if (detected.Count > 0) {
                    hfrAvg = detected.Average(d => d.HFR);
                    foreach (var d in detected) {
                        stars.Add(new DetectedStarDto(d.X, d.Y, d.HFR, d.Peak, d.Flux));
                    }
                }
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Star detection failed for frame {Id}", frameId);
            }
        }
        return new FrameStats(
            Width:    s.Width,
            Height:   s.Height,
            Mean:     s.Mean,
            Median:   s.Median,
            StDev:    s.StDev,
            Mad:      s.MAD,
            Min:      s.Min,
            Max:      s.Max,
            StarCount: stars.Count,
            HfrAvg:   hfrAvg,
            Histogram: BuildHistogram(img.Data, img.Properties.BitDepth, 256),
            Stars:    stars);
    }

    /// <summary>Export the frame to {rig}/processed/{target}/ as TIFF
    /// (16-bit, no stretch — preserves dynamic range), PNG (8-bit
    /// stretched), or JPEG (8-bit stretched). Returns the absolute path
    /// of the written file.</summary>
    public async Task<string?> ExportAsync(int frameId, string format, StretchOptions opts,
                                           bool stretched = true, CancellationToken ct = default) {
        var row = _library.GetById(frameId);
        if (row == null) return null;
        var img = LoadCached(frameId);
        if (img == null) return null;

        var profile = _profile.Active;
        var outRoot = profile.ImageOutputDir;
        if (string.IsNullOrWhiteSpace(outRoot)) return null;

        var rigName = _profile.ActiveEquipmentProfile?.Name ?? "Default";
        var target = string.IsNullOrEmpty(row.Target) ? "Unknown" : row.Target;
        var processedDir = Path.Combine(outRoot, Sanitize(rigName), "processed", Sanitize(target));
        Directory.CreateDirectory(processedDir);

        var fmt = (format ?? "tif").Trim().ToLowerInvariant();
        var ext = fmt switch {
            "tif" or "tiff" => ".tif",
            "png" => ".png",
            "jpg" or "jpeg" => ".jpg",
            _ => ".tif"
        };
        var baseName = Path.GetFileNameWithoutExtension(row.FileName);
        var outPath = Path.Combine(processedDir, $"{baseName}{ext}");
        int copy = 1;
        while (File.Exists(outPath)) outPath = Path.Combine(processedDir, $"{baseName}_{copy++}{ext}");

        try {
            switch (fmt) {
                case "tif":
                case "tiff": {
                    if (stretched) {
                        var bytes = ApplyStretch(img, opts);
                        using var image = IsImage.LoadPixelData<L8>(bytes, img.Properties.Width, img.Properties.Height);
                        await image.SaveAsTiffAsync(outPath, ct);
                    } else {
                        // 16-bit linear TIFF — preserves the full dynamic
                        // range so the user can re-process in PixInsight
                        // / Photoshop / Siril without baking in our stretch.
                        using var image = LoadAs16BitLinear(img);
                        await image.SaveAsTiffAsync(outPath, ct);
                    }
                    break;
                }
                case "png": {
                    var bytes = ApplyStretch(img, opts);
                    using var image = IsImage.LoadPixelData<L8>(bytes, img.Properties.Width, img.Properties.Height);
                    await image.SaveAsPngAsync(outPath, ct);
                    break;
                }
                case "jpg":
                case "jpeg": {
                    var bytes = ApplyStretch(img, opts);
                    using var image = IsImage.LoadPixelData<L8>(bytes, img.Properties.Width, img.Properties.Height);
                    await image.SaveAsJpegAsync(outPath, new JpegEncoder { Quality = 92 }, ct);
                    break;
                }
                default:
                    return null;
            }
            _logger.LogInformation("Exported frame {Id} -> {Path}", frameId, outPath);
            return outPath;
        } catch (Exception ex) {
            _logger.LogError(ex, "Export failed for frame {Id}", frameId);
            return null;
        }
    }

    // --- internals ---

    private static byte[] ApplyStretch(BaseImageData img, StretchOptions opts) {
        var w = img.Properties.Width;
        var h = img.Properties.Height;
        var bits = img.Properties.BitDepth;

        if (opts.Black == null || opts.Mid == null || opts.White == null) {
            var auto = AutoStretch.ComputeAutoStretchParams(img.Data, w, h, bits);
            var b = opts.Black ?? auto.Black;
            var m = opts.Mid   ?? auto.Mid;
            var wp = opts.White ?? auto.White;
            return AutoStretch.ApplyManual(img.Data, w, h, b, m, wp, bits);
        }
        return AutoStretch.ApplyManual(img.Data, w, h, opts.Black.Value, opts.Mid.Value, opts.White.Value, bits);
    }

    private static void ResizeIfNeeded<TPixel>(Image<TPixel> image, int maxSize)
            where TPixel : unmanaged, IPixel<TPixel> {
        if (image.Width <= maxSize && image.Height <= maxSize) return;
        image.Mutate(x => x.Resize(new ResizeOptions {
            Size = new Size(maxSize, maxSize),
            Mode = ResizeMode.Max
        }));
    }

    private static Image<L16> LoadAs16BitLinear(BaseImageData img) {
        // ImageSharp expects little-endian L16 — same memory layout as
        // ushort[] on every platform we target.
        var byteSpan = System.Runtime.InteropServices.MemoryMarshal.AsBytes(img.Data.AsSpan()).ToArray();
        return IsImage.LoadPixelData<L16>(byteSpan, img.Properties.Width, img.Properties.Height);
    }

    private static int[] BuildHistogram(ushort[] data, int bitDepth, int bins) {
        var h = new int[bins];
        double maxVal = (1 << bitDepth) - 1;
        for (int i = 0; i < data.Length; i++) {
            int bin = (int)(data[i] / maxVal * (bins - 1));
            if (bin < 0) bin = 0;
            else if (bin >= bins) bin = bins - 1;
            h[bin]++;
        }
        return h;
    }

    private static string Sanitize(string s) {
        if (string.IsNullOrWhiteSpace(s)) return "Unknown";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(' ', '_');
    }
}

public record DetectedStarDto(double X, double Y, double Hfr, double Peak, double Flux);

public record FrameStats(
    int Width, int Height,
    double Mean, double Median, double StDev, double Mad,
    int Min, int Max,
    int StarCount, double HfrAvg,
    int[] Histogram,
    IReadOnlyList<DetectedStarDto> Stars);
