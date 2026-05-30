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
    /// Auto-stretch using GraXpert's "15% Bg, 3 sigma" algorithm,
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

        // First pass: find the actual maximum value present in the
        // image. The "saturation" threshold for histogram exclusion
        // is normally topVal, but drivers that pack an N-bit sensor
        // into a 16-bit buffer often cap below 65535 (a 10-bit ZWO
        // sensor shifted into the high 6 bits saturates at 65472,
        // a 14-bit CMOS at 65520, etc). Excluding only pixels at
        // EXACTLY topVal in those cases lets the real saturation
        // wall into the sample, forces shadow ≈ saturation, MAD ≈ 0,
        // and the whole frame gets mapped to BLACK.
        //
        // Heuristic: only treat observedMax as the saturation point
        // when it's high enough to look like a real wall (>= 90% of
        // topVal). For normal scenes with an observedMax well below
        // topVal (a dim DSO peaking at 20000, a bright object at
        // 50000), the bright pixels are legitimate signal — NOT a
        // saturation wall — and excluding them would narrow the
        // sample distribution, raise the computed shadow, and crush
        // mid-tones to black. The previous version of this fix
        // applied the observedMax threshold unconditionally and hit
        // exactly that regression on high-contrast preview frames.
        ushort observedMax = 0;
        int limit = Math.Min(data.Length, pixelCount);
        for (int i = 0; i < limit; i++) {
            if (data[i] > observedMax) observedMax = data[i];
        }
        ushort wallThreshold = (ushort)(topVal * 0.9);
        ushort satThreshold = (observedMax >= wallThreshold && observedMax < topVal)
            ? observedMax : topVal;

        // Histogram + median, restricted to NON-saturated samples
        // (drop 0 and anything at the OBSERVED saturation point).
        // Black borders from a crop / dead pixel rows shouldn't
        // bias the background; nor should saturated highlights.
        var histogram = new int[65536];
        long sampleCount = 0;
        for (int i = 0; i < limit; i++) {
            ushort v = data[i];
            if (v == 0 || v >= satThreshold) continue;
            histogram[v]++;
            sampleCount++;
        }
        if (sampleCount == 0) {
            // Uniformly saturated (or uniformly zero) image. Set
            // white = the observed brightness so overexposed frames
            // render WHITE (intuitive) instead of falling to the
            // shader's default which would map every pixel through
            // the (1, 0.5, 1) identity and underexpose the visible
            // result when satThreshold < maxVal.
            double white = observedMax > 0
                ? Math.Clamp((double)observedMax / topVal, 0.001, 1.0)
                : 1.0;
            return new StretchParams(0, 0.5, white);
        }

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

        // MAD over the SAME restricted sample (matching the
        // satThreshold above — using topVal here would re-include
        // saturated pixels and pull MAD toward zero).
        var devHistogram = new int[65536];
        for (int i = 0; i < limit; i++) {
            ushort v = data[i];
            if (v == 0 || v >= satThreshold) continue;
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
