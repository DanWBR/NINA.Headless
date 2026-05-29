using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;

namespace NINA.Polaris.Services;

/// <summary>
/// Rectangular crop on a FITS file. Reads the source, slices the pixel
/// buffer to the requested ROI, and writes a sibling FITS named
/// `{stem}_crop.fits`. Mono (NAXIS=2) and RGB plane-sequential (NAXIS=3)
/// are both honoured.
///
/// Pure I/O — no dependency on SkiaSharp, ONNX, or any external binary.
/// Synchronous on the wire (caller awaits the response): even a 24 Mpx
/// RGB master takes &lt; 300 ms on a Pi 5 because the only real work is
/// `Buffer.BlockCopy` per row × channels.
///
/// Typical workflow: user opens a stacked master, sees the dark borders
/// from registration / stacking misalignment, drags a rectangle past
/// those borders, clicks Crop. The clean output is then fed into
/// GraXpert BGE / Decon / Denoise (which all reject border noise as
/// "background gradient" otherwise).
/// </summary>
public class CropService {
    private readonly ILogger<CropService> _logger;

    public CropService(ILogger<CropService> logger) {
        _logger = logger;
    }

    public sealed record CropResult(string OutputPath, int Width, int Height, int Channels);

    /// <summary>
    /// Read <paramref name="sourcePath"/>, crop to (<paramref name="x"/>,
    /// <paramref name="y"/>, <paramref name="width"/>, <paramref name="height"/>),
    /// write `{stem}_crop.fits` next to the source. Coords are in image
    /// pixel space, top-left origin. Throws ArgumentException on
    /// out-of-bounds ROI so the caller surfaces a clear error instead of
    /// silently producing a tiny black slice.
    /// </summary>
    public CropResult CropFits(string sourcePath, int x, int y, int width, int height) {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("sourcePath is required", nameof(sourcePath));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source FITS not found", sourcePath);
        if (width <= 0 || height <= 0)
            throw new ArgumentException(
                $"Crop must have positive size, got {width}×{height}");

        BaseImageData src;
        using (var fs = File.OpenRead(sourcePath)) {
            src = FITSReader.Read(fs);
        }

        int srcW = src.Properties.Width;
        int srcH = src.Properties.Height;
        int channels = src.Properties.Channels == 3 ? 3 : 1;

        // Clamp + validate. We intentionally reject ROIs that extend past
        // the image edge rather than silently truncating; a user who set
        // a 4000-wide crop on a 3840-wide image needs to know the picker
        // overshot, not get a smaller-than-requested output.
        if (x < 0 || y < 0 || x + width > srcW || y + height > srcH) {
            throw new ArgumentException(
                $"Crop ({x},{y} {width}×{height}) extends outside image " +
                $"bounds ({srcW}×{srcH})");
        }

        // Slice the buffer plane-by-plane. Plane-sequential layout means
        // pixel (px, py) on channel c sits at index (c*srcW*srcH) +
        // py*srcW + px. Copy row by row into the output plane.
        long outPlane = (long)width * height;
        long outTotal = outPlane * channels;
        var outPixels = new ushort[outTotal];
        for (int c = 0; c < channels; c++) {
            long srcPlaneBase = (long)c * srcW * srcH;
            long dstPlaneBase = (long)c * outPlane;
            for (int row = 0; row < height; row++) {
                long srcRow = srcPlaneBase + (long)(y + row) * srcW + x;
                long dstRow = dstPlaneBase + (long)row * width;
                Array.Copy(src.Data, srcRow, outPixels, dstRow, width);
            }
        }

        // Build new ImageProperties + preserve metadata (DATE-OBS, GAIN,
        // OBJECT, TELESCOPE, etc.) from the source so the cropped output
        // is still a usable scientific FITS, not a dimension-only blob.
        var newProps = src.Properties with {
            Width = width,
            Height = height
            // Channels is preserved automatically by the with-record copy
        };
        var dst = new BaseImageData(outPixels, newProps, src.MetaData);

        // Output path: sibling, suffix _crop.fits, replace any existing
        // file (idempotent re-runs).
        var dir = Path.GetDirectoryName(sourcePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var outPath = Path.Combine(dir, stem + "_crop.fits");

        FITSWriter.Write(dst, outPath, customKeywords: new[] {
            new KeyValuePair<string, string>("CROPSRCX", x.ToString()),
            new KeyValuePair<string, string>("CROPSRCY", y.ToString()),
            new KeyValuePair<string, string>("CROPSRCW", srcW.ToString()),
            new KeyValuePair<string, string>("CROPSRCH", srcH.ToString())
        });

        _logger.LogInformation(
            "Crop: {Src} ({SrcW}×{SrcH} ch={Ch}) → {Out} (x={X} y={Y} {W}×{H})",
            sourcePath, srcW, srcH, channels, outPath, x, y, width, height);

        return new CropResult(outPath, width, height, channels);
    }
}
