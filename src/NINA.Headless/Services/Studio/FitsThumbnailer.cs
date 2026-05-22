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
/// Output is grayscale for single-plane (mono) input or RGB for
/// NAXIS=3 colour cubes (PixInsight / Siril / GraXpert export
/// convention). Bayer-pattern frames are still rendered as luminance
/// — full debayer is a STUDIO pipeline step.
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
        var w = img.Properties.Width;
        var h = img.Properties.Height;
        var bits = img.Properties.BitDepth;
        if (img.Properties.IsColor) {
            return RenderJpegFromRgbPlanes(img.Data, w, h, bits, maxDim, quality);
        }
        return RenderJpegFromBuffer(img.Data, w, h, bits, maxDim, quality);
    }

    /// <summary>
    /// Render directly from an in-memory grayscale ushort buffer.
    /// Used both by <see cref="RenderJpegFromPath"/> (mono branch) and
    /// by callers that already have decoded pixel data (live-stack
    /// preview, future XISF reader, etc.).
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

        // Pick the source bitmap for the final RGBA pass. When we
        // *do* need to resize, we own a fresh SKBitmap; otherwise we
        // draw straight from grayCopy. The `needsResize` flag drives
        // the dispose path below — aliasing two `using` variables to
        // the same SkiaSharp handle double-frees the native object,
        // which presented as silent all-black JPEG output.
        bool needsResize = newW != grayCopy.Width || newH != grayCopy.Height;
        SKBitmap? resized = needsResize
            ? grayCopy.Resize(
                new SKImageInfo(newW, newH, SKColorType.Gray8, SKAlphaType.Opaque),
                SKSamplingOptions.Default)
            : null;
        try {
            var drawSrc = resized ?? grayCopy;
            using var rgb = new SKBitmap(newW, newH, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(rgb)) canvas.DrawBitmap(drawSrc, 0, 0);
            using var data = rgb.Encode(SKEncodedImageFormat.Jpeg, quality);
            return data.ToArray();
        } finally {
            resized?.Dispose();
        }
    }

    /// <summary>
    /// Render an RGB colour preview from a plane-sequential ushort
    /// buffer (R plane, then G plane, then B plane — each
    /// width*height long). Each channel is auto-stretched
    /// independently so a stacked OSC integration looks natural
    /// (per-channel MTF is what most viewers do for FITS RGB cubes).
    /// </summary>
    public static byte[] RenderJpegFromRgbPlanes(ushort[] pixels, int width, int height,
                                                 int bitDepth, int maxDim = 256, int quality = 85) {
        int planeSize = width * height;
        if (pixels.Length < planeSize * 3)
            // Defensive — caller mis-claimed colour. Fall back to mono
            // so the user at least sees something instead of a crash.
            return RenderJpegFromBuffer(pixels, width, height, bitDepth, maxDim, quality);

        // Stretch each channel separately. The vendored AutoStretch
        // doesn't take a stride/offset, so we slice into temporary
        // buffers. The slices are short-lived; for a 24 MP frame we're
        // talking ~48 MB peak which the RPi handles fine.
        var r = new ushort[planeSize];
        var g = new ushort[planeSize];
        var b = new ushort[planeSize];
        Array.Copy(pixels, planeSize * 0, r, 0, planeSize);
        Array.Copy(pixels, planeSize * 1, g, 0, planeSize);
        Array.Copy(pixels, planeSize * 2, b, 0, planeSize);

        var rs = NINA.Image.ImageAnalysis.AutoStretch.Apply(r, width, height, bitDepth);
        var gs = NINA.Image.ImageAnalysis.AutoStretch.Apply(g, width, height, bitDepth);
        var bs = NINA.Image.ImageAnalysis.AutoStretch.Apply(b, width, height, bitDepth);

        // Interleave into RGBA8888 for Skia. Alpha is opaque.
        var rgba = new byte[planeSize * 4];
        for (int i = 0; i < planeSize; i++) {
            int o = i * 4;
            rgba[o + 0] = rs[i];     // R
            rgba[o + 1] = gs[i];     // G
            rgba[o + 2] = bs[i];     // B
            rgba[o + 3] = 255;       // A
        }

        using var color = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        unsafe {
            fixed (byte* p = rgba) {
                color.SetPixels((IntPtr)p);
            }
        }
        using var colorCopy = color.Copy();   // own the backing storage

        double scale = (double)maxDim / Math.Max(colorCopy.Width, colorCopy.Height);
        if (scale > 1) scale = 1;
        int newW = Math.Max(1, (int)Math.Round(colorCopy.Width * scale));
        int newH = Math.Max(1, (int)Math.Round(colorCopy.Height * scale));

        bool needsResize = newW != colorCopy.Width || newH != colorCopy.Height;
        SKBitmap? resized = needsResize
            ? colorCopy.Resize(
                new SKImageInfo(newW, newH, SKColorType.Rgba8888, SKAlphaType.Opaque),
                SKSamplingOptions.Default)
            : null;
        try {
            var src = resized ?? colorCopy;
            using var data = src.Encode(SKEncodedImageFormat.Jpeg, quality);
            return data.ToArray();
        } finally {
            resized?.Dispose();
        }
    }
}
