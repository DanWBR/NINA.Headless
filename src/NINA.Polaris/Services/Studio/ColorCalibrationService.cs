using System.Collections.Concurrent;
using System.Globalization;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Polaris.Services.Sky;

namespace NINA.Polaris.Services.Studio;

/// <summary>
/// Siril-style color calibration on an RGB FITS master. Three modes,
/// all writing a sibling FITS so the calibration step is non-
/// destructive and slots cleanly between channel combine (CC-1..3)
/// and AI cleanup (GraXpert ONNX) in the mono workflow:
///
///   BgNeutral    Background neutralisation. Subtract per-channel
///                offsets so a chosen background region (auto = the
///                darkest 5% of pixels by luminance; patch = a
///                user-drawn ROI) becomes neutral grey.
///   Manual       BG neutralisation + white-reference scaling.
///                User picks a second patch containing a known-white
///                target (G2V star, galaxy core, neutral nebula
///                region); the service computes per-channel gains
///                that bring the mean of the white patch to neutral.
///   Photometric  Plate-solved + catalog-driven. Photometric Color
///                Calibration (PCC) the Siril way; ships in CCALB-3.
///                Throws NotImplementedException in CCALB-1 / 2.
///
/// Mirrors the job/progress shape of <see cref="ChannelCombineService"/>
/// so the STUDIO modal's progress block, polling cadence, and UI
/// state map across without per-tool work.
/// </summary>
public class ColorCalibrationService {
    private readonly FrameLibraryService _library;
    private readonly ProfileService _profile;
    private readonly ApassCatalog _catalog;
    private readonly ILogger<ColorCalibrationService> _logger;
    private readonly ConcurrentDictionary<string, ColorCalibrationProgress> _jobs = new();

    public ColorCalibrationService(FrameLibraryService library, ProfileService profile,
                                   ApassCatalog catalog,
                                   ILogger<ColorCalibrationService> logger) {
        _library = library;
        _profile = profile;
        _catalog = catalog;
        _logger = logger;
    }

    public static class Modes {
        public const string BgNeutral   = "bg";
        public const string Manual      = "manual";
        public const string Photometric = "pcc";
    }

    /// <summary>
    /// Patch ROI in image pixel coordinates. (X, Y) is the top-left
    /// corner; W, H are the side lengths. Validated and clamped to
    /// the frame bounds at request time.
    /// </summary>
    public record PatchRoi(int X, int Y, int W, int H);

    // UNIF-3a: switched FrameId -> FramePath. Stack sub-tab passes
    // the master integrated FITS path directly; no library lookup
    // needed.
    public record ColorCalibrationRequest(
        string FramePath,
        string Mode,                       // see Modes.*
        // Sample mode for BG step:
        //   "auto"  -> lowest-luminance 5% of pixels
        //   "patch" -> use BgPatch (must be non-null)
        string BgSample = "auto",
        PatchRoi? BgPatch = null,
        // Manual mode also needs a white-reference patch:
        PatchRoi? WhitePatch = null);

    public string StartJob(ColorCalibrationRequest req) {
        if (req == null) throw new ArgumentNullException(nameof(req));
        if (string.Equals(req.Mode, Modes.Manual, StringComparison.OrdinalIgnoreCase)
            && req.WhitePatch == null) {
            throw new ArgumentException(
                "Manual color calibration requires a white-reference patch.");
        }
        if (string.Equals(req.BgSample, "patch", StringComparison.OrdinalIgnoreCase)
            && req.BgPatch == null) {
            throw new ArgumentException(
                "BgSample='patch' requires a BgPatch ROI.");
        }
        var jobId = Guid.NewGuid().ToString("N")[..8];
        _jobs[jobId] = new ColorCalibrationProgress {
            JobId = jobId,
            Mode = req.Mode,
            InProgress = true,
            Stage = "queued",
        };
        _ = Task.Run(() => RunJob(jobId, req));
        return jobId;
    }

    public ColorCalibrationProgress? GetStatus(string jobId)
        => _jobs.TryGetValue(jobId, out var p) ? p : null;

    private void RunJob(string jobId, ColorCalibrationRequest req) {
        try {
            // ── Phase 1: load + validate ─────────────────────────────
            _jobs[jobId] = _jobs[jobId] with { Stage = "loading" };
            if (string.IsNullOrWhiteSpace(req.FramePath) || !File.Exists(req.FramePath)) {
                throw new InvalidOperationException(
                    $"Frame missing on disk: {req.FramePath}");
            }
            BaseImageData img;
            using (var fs = File.OpenRead(req.FramePath)) {
                img = FITSReader.Read(fs);
            }
            if (img.Properties.Channels != 3) {
                throw new InvalidOperationException(
                    $"Color calibration requires a 3-channel RGB FITS (got {img.Properties.Channels} channels). " +
                    $"Run Channel combine on per-filter masters first.");
            }
            int W = img.Properties.Width, H = img.Properties.Height;

            // ── Phase 2: compute per-channel offsets + gains by mode ─
            _jobs[jobId] = _jobs[jobId] with { Stage = "computing" };
            double[] offsets = new double[3];
            double[] gains   = new double[] { 1, 1, 1 };
            string prefix;

            switch ((req.Mode ?? "").ToLowerInvariant()) {
                case Modes.BgNeutral:
                    // BG-only mode: preserve background brightness at
                    // the dimmest channel's level (zeroBackground=false).
                    offsets = ColorCalibrationMath.ComputeBgOffsets(
                        img.Data, W, H, req.BgSample, req.BgPatch,
                        zeroBackground: false);
                    prefix = "bgneu";
                    break;
                case Modes.Manual:
                    // Manual mode: push BG to zero across channels so
                    // the white-reference gain step keeps BG neutral.
                    offsets = ColorCalibrationMath.ComputeBgOffsets(
                        img.Data, W, H, req.BgSample, req.BgPatch,
                        zeroBackground: true);
                    gains = ColorCalibrationMath.ComputeWhiteGains(
                        img.Data, W, H, req.WhitePatch!, offsets);
                    prefix = "ccal";
                    break;
                case Modes.Photometric: {
                    var pcc = RunPhotometric(img, W, H);
                    offsets = pcc.offsets;
                    gains = pcc.gains;
                    _jobs[jobId] = _jobs[jobId] with {
                        MatchedStars = pcc.matchedCount };
                    prefix = "pcc";
                    break;
                }
                default:
                    throw new ArgumentException(
                        $"Unknown color-calibration mode '{req.Mode}'. " +
                        "Expected one of: bg, manual, pcc.");
            }

            // ── Phase 3: apply per-pixel ─────────────────────────────
            _jobs[jobId] = _jobs[jobId] with { Stage = "applying" };
            int planeSize = W * H;
            var output = new ushort[planeSize * 3];
            for (int c = 0; c < 3; c++) {
                int baseIdx = c * planeSize;
                double off = offsets[c];
                double g = gains[c];
                for (int i = 0; i < planeSize; i++) {
                    double v = (img.Data[baseIdx + i] - off) * g;
                    output[baseIdx + i] = (ushort)Math.Clamp(v, 0, 65535);
                }
            }

            // ── Phase 4: write FITS ──────────────────────────────────
            _jobs[jobId] = _jobs[jobId] with { Stage = "writing" };
            var srcTarget = img.MetaData.Target.Name;
            if (string.IsNullOrEmpty(srcTarget)) srcTarget = "Unknown";
            var outPath = WriteOutput(output, W, H, img.Properties.BitDepth,
                req.FramePath, srcTarget, prefix, req, offsets, gains,
                img.Properties.Wcs);

            // ── Phase 5: reindex ─────────────────────────────────────
            _logger.LogInformation(
                "Color calibration {Job} ({Mode}): wrote {Path} " +
                "(offsets R={Or:F1} G={Og:F1} B={Ob:F1}, gains R={Gr:F3} G={Gg:F3} B={Gb:F3})",
                jobId, req.Mode, outPath, offsets[0], offsets[1], offsets[2],
                gains[0], gains[1], gains[2]);
            _ = Task.Run(() => _library.RescanAsync());

            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false,
                Stage = "done",
                OutputPath = outPath,
                OffsetR = offsets[0], OffsetG = offsets[1], OffsetB = offsets[2],
                GainR = gains[0], GainG = gains[1], GainB = gains[2],
            };
        } catch (Exception ex) {
            _logger.LogError(ex, "Color calibration job {JobId} failed", jobId);
            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false,
                Stage = "error",
                Error = ex.Message,
            };
        }
    }

    private string WriteOutput(ushort[] data, int W, int H, int bitDepth,
            string sourcePath, string target, string prefix,
            ColorCalibrationRequest req, double[] offsets, double[] gains,
            NINA.Image.FileFormat.FITS.WcsInfo? wcs = null) {
        // Sibling FITS: same directory as the source, suffix appended
        // to the stem. Keeps the calibrated output next to the
        // un-calibrated source so a diff is one-folder away (same
        // convention the GraXpert AI step uses for its outputs).
        var dir = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrEmpty(dir)) dir = ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var outBase = Path.Combine(dir, stem + "_" + prefix);
        var outPath = outBase + ".fits";
        int copy = 1;
        while (File.Exists(outPath)) outPath = outBase + "_" + (++copy) + ".fits";

        var props = new ImageProperties {
            Width = W, Height = H, BitDepth = bitDepth,
            BayerPattern = NINA.Core.Enum.BayerPatternEnum.None,
            IsBayered = false,
            Channels = 3,
            // CCALB-0a: pass plate-solve coords through. Color
            // calibration does not move pixels, so the source's WCS
            // remains valid on the output.
            Wcs = wcs,
        };
        var meta = new ImageMetaData {
            CreationTime = DateTime.UtcNow,
            Camera   = new ImageMetaData.CameraInfo(),
            Telescope = new ImageMetaData.TelescopeInfo(),
            Observer = new ImageMetaData.ObserverInfo(),
            Target   = new ImageMetaData.TargetInfo {
                Name = string.IsNullOrEmpty(target) ? "Unknown" : target,
            },
            Exposure = new ImageMetaData.ExposureInfo {
                Filter = "RGB",
                ImageType = "MASTERCAL",
            },
        };
        var masterData = new BaseImageData(data, props, meta);

        var customKeywords = new List<KeyValuePair<string, string>> {
            new("CCAL_MOD", req.Mode),
            new("CCAL_OFR", Fmt(offsets[0])),
            new("CCAL_OFG", Fmt(offsets[1])),
            new("CCAL_OFB", Fmt(offsets[2])),
            new("CCAL_GNR", Fmt(gains[0])),
            new("CCAL_GNG", Fmt(gains[1])),
            new("CCAL_GNB", Fmt(gains[2])),
            new("CCAL_SRC", Path.GetFileName(sourcePath)),
        };

        FITSWriter.Write(masterData, outPath, customKeywords: customKeywords);
        return outPath;
    }

    private static string Fmt(double d)
        => d.ToString("0.####", CultureInfo.InvariantCulture);

    // ── PCC orchestration ────────────────────────────────────────────

    /// <summary>
    /// Run the full Photometric Color Calibration pipeline on a 3-
    /// channel master. Stages: WCS check, catalog check, star detect,
    /// per-channel photometry, catalog query, star match, gain fit,
    /// BG neutralisation (so the final output also has a neutral
    /// background). Returns (offsets, gains, matchedCount) so the
    /// caller's apply step is unchanged from the BG and Manual modes.
    /// </summary>
    private (double[] offsets, double[] gains, int matchedCount) RunPhotometric(
            BaseImageData img, int W, int H) {
        // ── 1. Pre-flight: WCS in FITS ────────────────────────────────
        var wcs = img.Properties.Wcs;
        if (wcs == null) {
            throw new InvalidOperationException(
                "PCC: source FITS has no WCS (plate-solve) headers. " +
                "Solve the source first via STUDIO -> Solve, or re-run " +
                "the integration with plate-solve enabled.");
        }
        // ── 2. Pre-flight: catalog available ──────────────────────────
        if (!_catalog.IsAvailable) {
            throw new InvalidOperationException(
                "PCC: APASS catalog is not installed. Run " +
                "`python scripts/download-apass.py` on the server " +
                "(~80 MB download), then retry.");
        }

        // ── 3. Detect stars + measure per-channel photometry ──────────
        int n = W * H;
        // Star detection runs on the green channel only (highest SNR
        // for a typical broadband filter set + matches Siril's
        // approach). Slice out the G plane into its own buffer.
        var greenPlane = new ushort[n];
        Array.Copy(img.Data, n, greenPlane, 0, n);
        var detector = new StarDetector {
            SigmaThreshold = 7.0,   // masters have high SNR, raise from default 5
            MaxStarSize = 80,
        };
        var stars = detector.Detect(greenPlane, W, H);
        if (stars.Count < 5) {
            throw new InvalidOperationException(
                $"PCC: only {stars.Count} stars detected on the green " +
                "channel. PCC needs at least 5; check focus + exposure.");
        }
        var phots = StarPhotometer.MeasureRgb(img.Data, W, H, stars);

        // ── 4. Catalog cone search ────────────────────────────────────
        // Field-of-view radius: half the diagonal in degrees. The
        // pixel scale isn't |CD22| / |CD11| alone — those are only
        // the diagonal entries of the rotation+scale matrix, and at
        // rotations near 90° both go to zero while the off-diagonal
        // (CD12/CD21) carries the actual scale. Use the column
        // norms so rotation drops out:
        //   xScale = sqrt(CD11² + CD21²)   (deg per pixel along X)
        //   yScale = sqrt(CD12² + CD22²)   (deg per pixel along Y)
        // Pad ~20% so a slight rotation slop doesn't trim catalog
        // stars from the cone.
        double xScale = Math.Sqrt(wcs.CD11 * wcs.CD11 + wcs.CD21 * wcs.CD21);
        double yScale = Math.Sqrt(wcs.CD12 * wcs.CD12 + wcs.CD22 * wcs.CD22);
        double fovDegH = xScale * W;
        double fovDegV = yScale * H;
        double radius = 1.2 * Math.Sqrt(fovDegV * fovDegV + fovDegH * fovDegH) / 2.0;
        var catalogTask = _catalog.QueryRegionAsync(
            wcs.RaDeg, wcs.DecDeg, radius, magLimit: 13.0);
        var catalogStars = catalogTask.GetAwaiter().GetResult();

        // ── 5. Match catalog stars to detected stars ──────────────────
        // For each catalog star with valid B-V, project to pixel space
        // and find the nearest detected star within 3 px.
        var matched = new List<ColorCalibrationMath.CalibrationStar>();
        const double matchRadiusPx = 3.0;
        foreach (var c in catalogStars) {
            if (c.Bv == null) continue;
            var (px, py) = wcs.RaDecToPixel(c.Ra, c.Dec);
            if (double.IsNaN(px) || double.IsNaN(py)) continue;
            // Find nearest detected star within radius.
            StarPhotometer.StarPhotometry? best = null;
            double bestDist2 = matchRadiusPx * matchRadiusPx;
            foreach (var p in phots) {
                if (p.Saturated) continue;
                double dx = p.X - px;
                double dy = p.Y - py;
                double d2 = dx * dx + dy * dy;
                if (d2 <= bestDist2) {
                    bestDist2 = d2;
                    best = p;
                }
            }
            if (best != null) {
                matched.Add(new ColorCalibrationMath.CalibrationStar(
                    Photometry: best, Bv: c.Bv.Value));
            }
        }
        if (matched.Count < 5) {
            throw new InvalidOperationException(
                $"PCC: only {matched.Count} catalog stars matched to " +
                $"detected stars (need >= 5). Detected {phots.Count} stars " +
                $"in image, found {catalogStars.Count} catalog stars in FOV " +
                "with valid B-V. Increase plate-solve accuracy, increase " +
                "exposure, or fall back to Manual mode.");
        }

        // ── 6. Fit per-channel gains ──────────────────────────────────
        var gains = ColorCalibrationMath.ComputePccGains(matched);

        // ── 7. BG offsets via auto-detect so the final output is also
        //      background-neutral. PCC fixes star colours, BG neut
        //      fixes the sky pedestal; both are needed for a clean
        //      output.
        var offsets = ColorCalibrationMath.ComputeBgOffsets(
            img.Data, W, H, "auto", null, zeroBackground: true);

        _logger.LogInformation(
            "PCC: {Matched} matched stars, gains R={Gr:F3} G=1 B={Gb:F3}, " +
            "BG offsets R={Or:F1} G={Og:F1} B={Ob:F1}",
            matched.Count, gains[0], gains[2], offsets[0], offsets[1], offsets[2]);

        return (offsets, gains, matched.Count);
    }
}

public record ColorCalibrationProgress {
    public string JobId { get; init; } = "";
    public string Mode { get; init; } = "";
    public bool InProgress { get; init; }
    public string Stage { get; init; } = "";
    public string? Error { get; init; }
    public string? OutputPath { get; init; }
    public double OffsetR { get; init; }
    public double OffsetG { get; init; }
    public double OffsetB { get; init; }
    public double GainR { get; init; } = 1;
    public double GainG { get; init; } = 1;
    public double GainB { get; init; } = 1;
    public int MatchedStars { get; init; }   // PCC only
}
