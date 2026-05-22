namespace NINA.Image.ImageAnalysis;

public static class AutoStretch {
    /// <summary>
    /// Auto-stretch using median + MAD heuristics (PixInsight-style MTF).
    /// Black point clipped at median - 2.8·MAD, midtone targeted at 0.25.
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
    /// </summary>
    public static StretchParams ComputeAutoStretchParams(ushort[] data, int width, int height, int bitDepth = 16) {
        int pixelCount = width * height;
        if (data.Length == 0) return new StretchParams(0, 0.5, 1);

        var histogram = new int[65536];
        for (int i = 0; i < data.Length && i < pixelCount; i++) {
            histogram[data[i]]++;
        }

        long half = pixelCount / 2;
        long cumulative = 0;
        double median = 0;
        for (int i = 0; i < histogram.Length; i++) {
            cumulative += histogram[i];
            if (cumulative > half) {
                median = i;
                break;
            }
        }

        var devHistogram = new int[65536];
        for (int i = 0; i < data.Length && i < pixelCount; i++) {
            int dev = (int)Math.Abs(data[i] - median);
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
        double normalizedMAD = mad / maxVal;
        double shadow = Math.Max(0, normalizedMedian - 2.8 * normalizedMAD);
        double midtone = MTF(normalizedMedian - shadow, 0.25);
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
