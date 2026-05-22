using NINA.Image.FileFormat.FITS;
using SkiaSharp;

namespace NINA.Headless.Services.Studio;

/// <summary>
/// Shared FITS-and-FITS-like-bitmap → JPEG path. Extracted from
/// <see cref="FrameLibraryService"/> so the new FILES tab can render
/// the same auto-stretched preview for any FITS file on disk —
/// including files that were never indexed by STUDIO (an arbitrary
/// master coming back from PixInsight, for example).
///
/// The output is grayscale, single-page JPEG. Bayer-pattern frames
/// are still rendered as luminance — same behaviour STUDIO has
/// always had; full debayer is a STUDIO pipeline step.
/// </summary>
public static class FitsThumbnailer {

    /// <summary>
    /// Read a FITS file from disk, auto-stretch, downsample to
    /// <paramref name="maxDim"/> px on the long side, encode JPEG.
    /// Returns the raw JPEG bytes — caller decides whether to cache
    /// them to disk or stream them straight to the response.
    /// </summary>
    public static byte[] RenderJpegFromPath(string fitsPath, int maxDim = 256, int quality = 85) {
        using var fs = File.OpenRead(fitsPath);
        var img = FITSReader.Read(fs);
        return RenderJpegFromBuffer(img.Data, img.Properties.Width, img.Properties.Height,
            img.Properties.BitDepth, maxDim, quality);
    }

    /// <summary>
    /// Render directly from an in-memory ushort buffer. Used both by
    /// <see cref="RenderJpegFromPath"/> and by callers that already
    /// have decoded pixel data (the live-stack preview path, future
    /// XISF reader, etc.).
    /// </summary>
    public static byte[] RenderJpegFromBuffer(ushort[] pixels, int width, int height,
                                              int bitDepth, int maxDim = 256, int quality = 85) {
        // Auto-stretch lives in NINA.Image (vendored portable copy).
        var stretched = NINA.Image.ImageAnalysis.AutoStretch.Apply(pixels, width, height, bitDepth);

        // Wrap the byte[] as a Gray8 SKBitmap, copy so we own the
        // backing storage, then resize. JPEG encoders are flaky with
        // Gray8 input, so the final step is a round-trip via Rgba8888.
        using var gray = new SKBitmap(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        unsafe {
            fixed (byte* p = stretched) {
                gray.SetPixels((IntPtr)p);
            }
        }

        using var grayCopy = gray.Copy();
        double scale = (double)maxDim / Math.Max(grayCopy.Width, grayCopy.Height);
        // Caller passes maxDim=int.MaxValue (or any larger-than-the-source
        // value) to skip downsampling — useful for full-res preview.
        if (scale > 1) scale = 1;
        int newW = Math.Max(1, (int)Math.Round(grayCopy.Width * scale));
        int newH = Math.Max(1, (int)Math.Round(grayCopy.Height * scale));

        using var resized = (newW == grayCopy.Width && newH == grayCopy.Height)
            ? grayCopy
            : grayCopy.Resize(
                new SKImageInfo(newW, newH, SKColorType.Gray8, SKAlphaType.Opaque),
                SKSamplingOptions.Default);

        using var rgb = new SKBitmap(newW, newH, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(rgb)) canvas.DrawBitmap(resized, 0, 0);
        using var data = rgb.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }
}
