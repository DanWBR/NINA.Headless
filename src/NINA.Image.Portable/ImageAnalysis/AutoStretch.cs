namespace NINA.Image.ImageAnalysis;

public static class AutoStretch {
    // GX-12: defaults aligned with GraXpert's "15% Bg, 3 sigma" preset.
    // Empirically gives a nicer dark-grey background that doesn't crush
    // the faint structure of nebulae/galaxies while still presenting
    // pleasant star contrast. Old PixInsight-ish "0.25 / 2.8" defaults
    // produced thumbnails that looked muddy on most masters; users had
    // to manually re-stretch every preview.
    public const double DefaultTargetBg = 0.15;   // GraXpert: bg
    public const double DefaultSigma    = 3.0;    // GraXpert: sigma

    /// <summary>
    /// Auto-stretch using GraXpert's "15% Bg, 3 sigma" algorithm —
    /// sigma-clipped median + MAD on non-saturated samples, MTF mapping
    /// median to a 15% target background. Drop-in default for the
    /// FILES / STUDIO previews.
    /// </summary>
    public static byte[] Apply(ushort[] data, int width, int height, int bitDepth = 16) {
        var p = ComputeAutoStretchParams(data, width, height, bitDepth);
        return ApplyManual(data, width, height, p.Black, p.Mid, p.White, bitDepth);
    }

    /// <summary>
    /// Apply an explicit MTF stretch with caller-chosen black / mid / white
    /// points (each normalised 0..1). Used by the STUDIO viewer so slider
    /// drags don't require re-computing stats every frame.
    ///
    /// midtone is the midtone *balance* (target normalised value the
    /// midpoint maps to). 0.5 = linear; &lt;0.5 stretches shadows (typical
    /// for DSO); &gt;0.5 compresses shadows.
    /// </summary>
    public static byte[] ApplyManual(ushort[] data, int width, int height,
                                     double black, double mid, double white, int bitDepth = 16) {
        int pixelCount = width * height;
        var result = new byte[pixelCount];
        if (data.Length == 0) return result;

        black = Math.Clamp(black, 0.0, 1.0);
        white = Math.Clamp(white, 0.0, 1.0);
        if (white <= black) white = Math.Min(1.0, black + 1e-6);
        mid = Math.Clamp(mid, 0.001, 0.999);

        double maxVal = (1 << bitDepth) - 1;
        var lut = new byte[65536];
        for (int i = 0; i < 65536; i++) {
            double normalized = i / maxVal;
            double clipped = Math.Clamp((normalized - black) / (white - black), 0, 1);
            double stretched = MTF(clipped, mid);
            lut[i] = (byte)(stretched * 255);
        }

        for (int i = 0; i < data.Length && i < pixelCount; i++) {
            result[i] = lut[data[i]];
        }
        return result;
    }

    /// <summary>
    /// Compute the auto-stretch parameters (black/mid/white, all normalised
    /// 0..1) without applying them. Used by the STUDIO viewer to seed
    /// sliders with sensible defaults before the user starts tweaking.
    ///
    /// GX-12: ports GraXpert's stretch.py algorithm (see
    /// <c>graxpert/stretch.py:calculate_mtf_stretch_parameters_for_channel</c>).
    /// Two material differences vs the prior PixInsight-style heuristic:
    ///
    ///   1. Saturated pixels (== 0 and == max) are excluded from the
    ///      median/MAD sample. Without this, an image with lots of
    ///      hot pixels or black borders skews the background estimate.
    ///   2. New defaults: <c>sigma=3</c> (was 2.8) and target
    ///      background = 15% (was 25%). The lower bg gives a darker,
    ///      higher-contrast preview that matches what GraXpert ships.
    ///
    /// Optional <paramref name="targetBg"/> + <paramref name="sigma"/>
    /// let callers pick a different preset
    /// (10% Bg 3σ / 20% Bg 3σ / 30% Bg 2σ are the other GraXpert
    /// shipped options).
    /// </summary>
    public static StretchParams ComputeAutoStretchParams(
            ushort[] data, int width, int height, int bitDepth = 16,
            double? targetBg = null, double? sigma = null) {
        int pixelCount = width * height;
        if (data.Length == 0) return new StretchParams(0, 0.5, 1);

        double bgArg    = Math.Clamp(targetBg ?? DefaultTargetBg, 0.01, 0.99);
        double sigmaArg = Math.Max(0.5, sigma ?? DefaultSigma);

        int maxVal16  = (1 << bitDepth) - 1;
        ushort topVal = (ushort)Math.Min(maxVal16, 65535);

        // Histogram + median, restricted to NON-saturated samples
        // (drop 0 and the bit-depth max). Black borders from a
        // crop / dead pixel rows shouldn't bias the background.
        var histogram = new int[65536];
        long sampleCount = 0;
        for (int i = 0; i < data.Length && i < pixelCount; i++) {
            ushort v = data[i];
            if (v == 0 || v >= topVal) continue;
            histogram[v]++;
            sampleCount++;
        }
        if (sampleCount == 0) return new StretchParams(0, 0.5, 1);

        long half = sampleCount / 2;
        long cumulative = 0;
        double median = 0;
        for (int i = 0; i < histogram.Length; i++) {
            cumulative += histogram[i];
            if (cumulative > half) {
                median = i;
                break;
            }
        }

        // MAD over the same restricted sample.
        var devHistogram = new int[65536];
        for (int i = 0; i < data.Length && i < pixelCount; i++) {
            ushort v = data[i];
            if (v == 0 || v >= topVal) continue;
            int dev = (int)Math.Abs(v - median);
            if (dev < 65536) devHistogram[dev]++;
        }
        cumulative = 0;
        double mad = 0;
        for (int i = 0; i < devHistogram.Length; i++) {
            cumulative += devHistogram[i];
            if (cumulative > half) {
                mad = i;
                break;
            }
        }

        double maxVal = (1 << bitDepth) - 1;
        double normalizedMedian = median / maxVal;
        double normalizedMAD    = mad / maxVal;
        // shadow_clipping = clamp(median - sigma * MAD, 0, 1)
        double shadow = Math.Clamp(
            normalizedMedian - sigmaArg * normalizedMAD, 0.0, 1.0);
        // midtone = MTF((median - shadow) / (1 - shadow), bg)
        double denom = Math.Max(1e-9, 1.0 - shadow);
        double midtone = MTF((normalizedMedian - shadow) / denom, bgArg);
        return new StretchParams(shadow, midtone, 1.0);
    }

    public record StretchParams(double Black, double Mid, double White);

    private static double MTF(double x, double midtone) {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        if (midtone <= 0) return 1;
        if (midtone >= 1) return 0;
        return (midtone - 1.0) * x / ((2.0 * midtone - 1.0) * x - midtone);
    }
}
