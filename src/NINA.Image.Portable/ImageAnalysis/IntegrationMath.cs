namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Pixel-stack reduction methods used by STUDIO master-frame creation
/// (and downstream batch integration in ST-5). Each takes an N-element
/// span of pixel values from the same coordinate across N frames and
/// returns a single ushort.
///
/// Methods are deterministic and side-effect-free so they can be called
/// from a parallel inner loop in <c>MasterFrameService</c>.
///
/// Why ushort in / ushort out? FITS payloads come in as 16-bit unsigned
/// after BZERO/BSCALE normalisation, and master frames stay 16-bit so
/// the existing FITSWriter (BITPIX=16, BZERO=32768) can serialise them
/// without a format change.
/// </summary>
public static class IntegrationMath {

    /// <summary>Arithmetic mean (rounded to nearest, clamped 0..65535).</summary>
    public static ushort Mean(ReadOnlySpan<ushort> values) {
        if (values.Length == 0) return 0;
        long sum = 0;
        for (int i = 0; i < values.Length; i++) sum += values[i];
        return (ushort)Math.Clamp((sum + values.Length / 2) / values.Length, 0, 65535);
    }

    /// <summary>
    /// Median via partial sort. For odd N returns the middle element;
    /// for even N returns the average of the two middle elements.
    /// Allocates a scratch ushort[] per call — caller can pass a reused
    /// buffer to avoid that in hot loops.
    /// </summary>
    public static ushort Median(ReadOnlySpan<ushort> values, Span<ushort> scratch = default) {
        if (values.Length == 0) return 0;
        if (values.Length == 1) return values[0];
        Span<ushort> buf = scratch.Length >= values.Length ? scratch[..values.Length] : new ushort[values.Length];
        values.CopyTo(buf);
        buf.Sort();
        int n = buf.Length;
        if ((n & 1) == 1) return buf[n / 2];
        return (ushort)((buf[n / 2 - 1] + buf[n / 2] + 1) / 2);
    }

    /// <summary>
    /// Sigma-clipped mean. Iteratively rejects values further than
    /// <paramref name="sigmaLow"/>·σ below or <paramref name="sigmaHigh"/>·σ
    /// above the running mean (using sample stdev), then re-averages
    /// the survivors. Falls back to plain mean if all values get
    /// clipped (degenerate input).
    ///
    /// Defaults of (3, 3, 2) match the common PixInsight setting used
    /// for hot/cold pixel rejection on darks and flats.
    /// </summary>
    public static ushort SigmaClippedMean(ReadOnlySpan<ushort> values,
                                          double sigmaLow = 3.0, double sigmaHigh = 3.0,
                                          int iterations = 2) {
        if (values.Length == 0) return 0;
        if (values.Length <= 2) return Mean(values);  // sigma needs at least 3

        // Working list of "still in play" values. ushort -> double for
        // the stats so 65535 squared doesn't blow up the accumulator.
        var live = new List<double>(values.Length);
        for (int i = 0; i < values.Length; i++) live.Add(values[i]);

        for (int iter = 0; iter < iterations; iter++) {
            if (live.Count <= 2) break;
            double mean = 0;
            for (int i = 0; i < live.Count; i++) mean += live[i];
            mean /= live.Count;

            double sqSum = 0;
            for (int i = 0; i < live.Count; i++) {
                var d = live[i] - mean;
                sqSum += d * d;
            }
            double stdev = Math.Sqrt(sqSum / live.Count);
            if (stdev < 1e-9) break;  // identical pixels, no need to clip

            double lo = mean - sigmaLow * stdev;
            double hi = mean + sigmaHigh * stdev;
            var next = new List<double>(live.Count);
            for (int i = 0; i < live.Count; i++) {
                if (live[i] >= lo && live[i] <= hi) next.Add(live[i]);
            }
            if (next.Count == 0) break;
            if (next.Count == live.Count) break;  // converged, no further changes
            live = next;
        }

        double final = 0;
        for (int i = 0; i < live.Count; i++) final += live[i];
        final /= live.Count;
        return (ushort)Math.Clamp(Math.Round(final), 0, 65535);
    }
}
