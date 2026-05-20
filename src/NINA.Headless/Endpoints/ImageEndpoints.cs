using NINA.Headless.Services;
using NINA.Image.ImageAnalysis;

namespace NINA.Headless.Endpoints;

public static class ImageEndpoints {
    public static void MapImageEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/image");

        group.MapGet("/latest/preview", (ImageRelayService relay, int? quality) => {
            var jpeg = relay.GetLatestJpeg(quality ?? 85);
            if (jpeg == null)
                return Results.NotFound(new { error = "No image available" });

            return Results.File(jpeg, "image/jpeg");
        });

        group.MapGet("/latest/stats", (ImageRelayService relay, bool? withStars) => {
            var image = relay.GetLatestImage();
            if (image == null)
                return Results.NotFound(new { error = "No image available" });

            var pixels = image.PixelData.ToArray();
            var stats = ComputeFullStats(pixels, image.BitDepth);

            object? starsInfo = null;
            if (withStars == true) {
                var det = new StarDetector { SigmaThreshold = 5.0, MaxStars = 200 };
                var stars = det.Detect(pixels, image.Width, image.Height);
                var hfrs = stars.Select(s => s.HFR).OrderBy(h => h).ToList();
                double medianHfr = hfrs.Count > 0 ? hfrs[hfrs.Count / 2] : 0;
                double meanHfr = hfrs.Count > 0 ? hfrs.Average() : 0;
                starsInfo = new {
                    count = stars.Count,
                    medianHfr,
                    meanHfr,
                    minHfr = hfrs.Count > 0 ? hfrs.First() : 0,
                    maxHfr = hfrs.Count > 0 ? hfrs.Last() : 0
                };
            }

            return Results.Ok(new {
                width = image.Width,
                height = image.Height,
                bitDepth = image.BitDepth,
                bayerPattern = image.BayerPattern.ToString(),
                stats.mean, stats.median, stats.min, stats.max,
                stats.stddev, stats.mad,
                pixelCount = pixels.LongLength,
                stars = starsInfo
            });
        });

        group.MapGet("/latest/stars", (ImageRelayService relay, int? maxStars, double? sigma) => {
            var image = relay.GetLatestImage();
            if (image == null)
                return Results.NotFound(new { error = "No image available" });

            var det = new StarDetector {
                MaxStars = Math.Clamp(maxStars ?? 200, 10, 2000),
                SigmaThreshold = Math.Clamp(sigma ?? 5.0, 1.0, 20.0)
            };
            var pixels = image.PixelData.ToArray();
            var stars = det.Detect(pixels, image.Width, image.Height);

            return Results.Ok(new {
                width = image.Width,
                height = image.Height,
                count = stars.Count,
                stars = stars.Select(s => new {
                    x = s.X, y = s.Y, hfr = s.HFR, flux = s.Flux, peak = s.Peak
                })
            });
        });

        group.MapGet("/latest/histogram", (ImageRelayService relay, int? bins) => {
            var image = relay.GetLatestImage();
            if (image == null)
                return Results.NotFound(new { error = "No image available" });

            int binCount = Math.Clamp(bins ?? 256, 16, 4096);
            var pixels = image.PixelData.ToArray();
            int maxVal = (1 << image.BitDepth) - 1;
            if (maxVal <= 0) maxVal = 65535;
            var hist = new long[binCount];
            for (int i = 0; i < pixels.Length; i++) {
                int b = (int)((long)pixels[i] * (binCount - 1) / maxVal);
                if (b >= 0 && b < binCount) hist[b]++;
            }
            return Results.Ok(new {
                bins = binCount,
                maxVal,
                values = hist
            });
        });
    }

    private static (double mean, double median, int min, int max, double stddev, double mad)
        ComputeFullStats(ushort[] data, int bitDepth) {
        if (data.Length == 0) return (0, 0, 0, 0, 0, 0);

        long sum = 0;
        int mn = int.MaxValue, mx = int.MinValue;
        // Subsample for large images to keep this cheap (~1M samples cap)
        int step = Math.Max(1, data.Length / 1_000_000);
        long n = 0;

        // Full sum for mean (cheap)
        for (int i = 0; i < data.Length; i++) {
            int v = data[i];
            sum += v;
            if (v < mn) mn = v;
            if (v > mx) mx = v;
        }
        double mean = (double)sum / data.Length;

        // StdDev (full)
        double sumSq = 0;
        for (int i = 0; i < data.Length; i++) {
            double d = data[i] - mean;
            sumSq += d * d;
        }
        double stddev = Math.Sqrt(sumSq / data.Length);

        // Median via histogram (full data)
        var hist = new int[65536];
        for (int i = 0; i < data.Length; i++) hist[data[i]]++;
        long half = data.Length / 2;
        long cum = 0;
        int median = 0;
        for (int i = 0; i < hist.Length; i++) {
            cum += hist[i];
            if (cum > half) { median = i; break; }
        }

        // MAD via histogram of deviations
        var devHist = new int[65536];
        for (int i = 0; i < data.Length; i++) {
            int dev = Math.Abs(data[i] - median);
            if (dev < 65536) devHist[dev]++;
        }
        cum = 0;
        int mad = 0;
        for (int i = 0; i < devHist.Length; i++) {
            cum += devHist[i];
            if (cum > half) { mad = i; break; }
        }

        n = step; // touch step so analyzers don't complain
        return (mean, median, mn, mx, stddev, mad);
    }
}
