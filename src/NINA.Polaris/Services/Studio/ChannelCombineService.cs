using System.Collections.Concurrent;
using System.Globalization;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;

namespace NINA.Polaris.Services.Studio;

/// <summary>
/// Combine N mono master frames (one per filter) into a single RGB or
/// LRGB FITS, optionally via a user-supplied PixelMath expression. This
/// is the channel-combine equivalent of PixInsight's ChannelCombination
/// + LRGBCombination + PixelMath: the missing step that lets the mono
/// LRGB workflow finish entirely inside Polaris.
///
/// Pipeline per job (same shape as BatchStackingService, intentionally,
/// so the operational feel is identical, jobId + ConcurrentDictionary
/// of progress records + final RescanAsync hook):
///
///   1. Load each input via FrameLibraryService → FITSReader. Validate
///      that all inputs share width × height; mismatch is fatal (per
///      filter masters from the same target should be identical).
///   2. REGISTER (default ON): cross-channel star alignment. Each
///      per-filter master came out of BatchStackingService aligned to
///      its own reference frame, master_R is NOT pixel-perfect with
///      master_L. Without this step the output has colour fringes on
///      every star + ghosting on extended nebulosity. We detect stars
///      on the chosen reference channel + each other input,
///      <see cref="StarMatcher.Match"/> for an affine, then resample
///      via <see cref="ImageResampler.ApplyTransform"/>. SigmaThreshold
///      is bumped to 7 because masters have much higher SNR than
///      single subs and the default 5σ would flood-detect halos.
///   3. NORMALIZE (default ON for RGB/LRGB): scale each channel's
///      median up to the largest median across all inputs. Compensates
///      for filters captured in different sessions with different sky
///      brightness backgrounds.
///   4. COMPOSE: per-mode.
///        RgbCompose, pack 3 mono planes into one plane-sequential
///          ushort[] of length w*h*3, write FITS with Channels=3.
///        LrgbCompose, RGB compose then luminance overlay via
///          <see cref="LrgbCombiner"/> (CC-2). v1 here throws until
///          that ships.
///        PixelMath, evaluate user expression per pixel via
///          <see cref="PixelMathEvaluator"/> (CC-3). v1 here throws
///          until that ships.
///   5. WRITE: <c>{rig}/integrated/{target}/composed/{prefix}_{target}_{stamp}.fits</c>
///      with custom FITS keywords recording the combine recipe so a
///      future open can show the user what happened (REGISTER, REGREF,
///      REG_{ch}, CHCOMBINE, NORMALIZE).
///   6. RESCAN: <see cref="FrameLibraryService.RescanAsync"/> so the
///      new composed master shows up in STUDIO + FILES without a
///      manual refresh.
/// </summary>
public class ChannelCombineService {
    private readonly FrameLibraryService _library;
    private readonly ProfileService _profile;
    private readonly ILogger<ChannelCombineService> _logger;
    private readonly ConcurrentDictionary<string, ChannelCombineProgress> _jobs = new();

    public ChannelCombineService(FrameLibraryService library, ProfileService profile,
                                 ILogger<ChannelCombineService> logger) {
        _library = library;
        _profile = profile;
        _logger = logger;
    }

    /// <summary>
    /// Mode discriminator. Strings (rather than enum) so the JSON
    /// surface stays explicit; lowercase to match the rest of the
    /// STUDIO REST contract.
    /// </summary>
    public static class Modes {
        public const string RgbCompose  = "rgb";
        public const string LrgbCompose = "lrgb";
        public const string PixelMath   = "pixelmath";
    }

    public record ChannelInput(string Variable, int FrameId);

    public record ChannelCombineRequest(
        string Mode,
        List<ChannelInput> ChannelMap,
        bool Register = true,
        bool Normalize = true,
        // LRGB-only:
        string? LrgbAlgo = "lab",                   // "lab" | "ratio"
        // PixelMath-only:
        List<string>? Expressions = null,           // 1 (mono out) or 3 (RGB out)
        bool MonoOutput = false,
        // Optional override; defaults to first input's target.
        string? TargetName = null);

    public string StartJob(ChannelCombineRequest req) {
        if (req == null) throw new ArgumentNullException(nameof(req));
        if (req.ChannelMap == null || req.ChannelMap.Count < 2) {
            throw new ArgumentException("ChannelMap must contain at least 2 entries.");
        }
        var jobId = Guid.NewGuid().ToString("N")[..8];
        _jobs[jobId] = new ChannelCombineProgress {
            JobId = jobId,
            InProgress = true,
            Mode = req.Mode,
            Stage = "queued",
            Total = req.ChannelMap.Count,
        };
        _ = Task.Run(() => RunJob(jobId, req));
        return jobId;
    }

    public ChannelCombineProgress? GetStatus(string jobId)
        => _jobs.TryGetValue(jobId, out var p) ? p : null;

    private void RunJob(string jobId, ChannelCombineRequest req) {
        try {
            // ── Phase 1: load + validate ─────────────────────────────
            _jobs[jobId] = _jobs[jobId] with { Stage = "loading", Done = 0 };
            var inputs = new List<LoadedChannel>(req.ChannelMap.Count);
            int? width = null, height = null;
            int bitDepth = 16;
            string target = "Unknown";

            for (int i = 0; i < req.ChannelMap.Count; i++) {
                var slot = req.ChannelMap[i];
                var row = _library.GetById(slot.FrameId);
                if (row == null || !File.Exists(row.Path)) {
                    throw new InvalidOperationException(
                        $"Frame id {slot.FrameId} (variable '{slot.Variable}') " +
                        $"not found in the library or missing on disk.");
                }
                using var fs = File.OpenRead(row.Path);
                var img = FITSReader.Read(fs);
                if (img.Properties.Channels != 1) {
                    throw new InvalidOperationException(
                        $"Input '{slot.Variable}' ({row.FileName}) is " +
                        $"{img.Properties.Channels}-channel; channel combine inputs " +
                        $"must be mono masters.");
                }
                if (width == null) {
                    width = img.Properties.Width;
                    height = img.Properties.Height;
                    bitDepth = img.Properties.BitDepth;
                    target = string.IsNullOrEmpty(row.Target) ? "Unknown" : row.Target;
                } else if (img.Properties.Width != width || img.Properties.Height != height) {
                    throw new InvalidOperationException(
                        $"Input '{slot.Variable}' is {img.Properties.Width}×{img.Properties.Height}, " +
                        $"expected {width}×{height} (matching first input).");
                }
                inputs.Add(new LoadedChannel(
                    Variable: slot.Variable,
                    Data: img.Data,
                    FileName: row.FileName));
                _jobs[jobId] = _jobs[jobId] with { Done = i + 1 };
            }

            int W = width!.Value;
            int H = height!.Value;

            // Apply target-name override AFTER the input loop so the
            // user can rename the output without affecting which target
            // the inputs come from.
            if (!string.IsNullOrWhiteSpace(req.TargetName)) target = req.TargetName!;

            // ── Phase 2: cross-channel registration (default ON) ─────
            // Identify which input acts as the reference. For LRGB the
            // L channel is the natural anchor (highest SNR + the
            // luminance source the user wants the colour aligned to);
            // for RGB and PixelMath we use the first input listed in
            // the channel map. The picker is deterministic so two
            // identical jobs produce identical outputs.
            int refIdx = PickReferenceIndex(req.Mode, inputs);
            var transforms = new AffineTransform?[inputs.Count];

            if (req.Register) {
                _jobs[jobId] = _jobs[jobId] with {
                    Stage = "registering", Done = 0, Total = inputs.Count,
                    ReferenceChannel = inputs[refIdx].Variable,
                };
                // SigmaThreshold=7 (vs default 5) because per-filter
                // masters have much higher SNR than single subs and the
                // default would flood-detect faint halos. MaxStarSize=80
                // (vs default 200) because integrated stars are tighter.
                var detector = new StarDetector { SigmaThreshold = 7.0, MaxStarSize = 80 };
                var refStars = detector.Detect(inputs[refIdx].Data, W, H);
                if (refStars.Count < 5) {
                    throw new InvalidOperationException(
                        $"Reference channel '{inputs[refIdx].Variable}' has only " +
                        $"{refStars.Count} detected stars after thresholding. " +
                        $"Channel combine needs at least 5 reference stars to align " +
                        $"the others. Disable Register if the masters are already " +
                        $"co-aligned (rare).");
                }

                for (int i = 0; i < inputs.Count; i++) {
                    if (i == refIdx) {
                        transforms[i] = AffineTransform.Identity;
                        continue;
                    }
                    var stars = detector.Detect(inputs[i].Data, W, H);
                    // maxSearchRadius bumped from 50 (single-sub default)
                    // to 100, cross-filter offset between masters can
                    // exceed 50 px when filter wheels carry the field
                    // around during a multi-night run.
                    var t = StarMatcher.Match(refStars, stars, maxSearchRadius: 100);
                    if (t == null) {
                        throw new InvalidOperationException(
                            $"Could not register channel '{inputs[i].Variable}' to " +
                            $"reference '{inputs[refIdx].Variable}': StarMatcher found " +
                            $"too few matched stars. Detected {stars.Count} stars in " +
                            $"this channel, {refStars.Count} in reference. Increase " +
                            $"exposure for this filter, or disable Register if the " +
                            $"masters are already co-aligned.");
                    }
                    transforms[i] = t;
                    var resampled = ImageResampler.ApplyTransform(inputs[i].Data, W, H, t);
                    inputs[i] = inputs[i] with { Data = resampled };
                    _jobs[jobId] = _jobs[jobId] with { Done = i + 1 };
                }
            } else {
                // Register OFF, identity transforms so the FITS keywords
                // still report what happened.
                for (int i = 0; i < transforms.Length; i++) transforms[i] = AffineTransform.Identity;
            }

            // ── Phase 3: optional per-channel normalization ──────────
            if (req.Normalize) {
                _jobs[jobId] = _jobs[jobId] with { Stage = "normalizing", Done = 0, Total = inputs.Count };
                var medians = new double[inputs.Count];
                for (int i = 0; i < inputs.Count; i++) {
                    medians[i] = ComputeMedian(inputs[i].Data);
                }
                double target_ = medians.Max();
                if (target_ > 0) {
                    for (int i = 0; i < inputs.Count; i++) {
                        if (medians[i] <= 0) continue;
                        double k = target_ / medians[i];
                        if (Math.Abs(k - 1.0) < 1e-3) {
                            _jobs[jobId] = _jobs[jobId] with { Done = i + 1 };
                            continue;
                        }
                        var d = inputs[i].Data;
                        var scaled = new ushort[d.Length];
                        for (int p = 0; p < d.Length; p++) {
                            scaled[p] = (ushort)Math.Clamp(d[p] * k, 0, 65535);
                        }
                        inputs[i] = inputs[i] with { Data = scaled };
                        _jobs[jobId] = _jobs[jobId] with { Done = i + 1 };
                    }
                }
            }

            // ── Phase 4: compose by mode ─────────────────────────────
            _jobs[jobId] = _jobs[jobId] with { Stage = "composing", Done = 0, Total = 1 };
            (ushort[] data, int channels) composed;
            string prefix;
            switch ((req.Mode ?? "").ToLowerInvariant()) {
                case Modes.RgbCompose:
                    composed = ComposeRgb(inputs, W, H);
                    prefix = "rgb";
                    break;
                case Modes.LrgbCompose:
                    throw new NotImplementedException(
                        "LrgbCompose ships in CC-2 (LrgbCombiner). Use RgbCompose for now.");
                case Modes.PixelMath:
                    throw new NotImplementedException(
                        "PixelMath ships in CC-3 (PixelMathEvaluator). Use RgbCompose for now.");
                default:
                    throw new ArgumentException(
                        $"Unknown combine mode '{req.Mode}'. Expected one of: rgb, lrgb, pixelmath.");
            }
            _jobs[jobId] = _jobs[jobId] with { Done = 1 };

            // ── Phase 5: write FITS ──────────────────────────────────
            _jobs[jobId] = _jobs[jobId] with { Stage = "writing" };
            var outPath = WriteOutput(composed.data, composed.channels, W, H, bitDepth,
                target, prefix, req, inputs, transforms);

            // ── Phase 6: reindex ─────────────────────────────────────
            _logger.LogInformation(
                "Channel combine {Job} ({Mode}): wrote {Path} ({Ch}-channel, {N} inputs, register={Reg})",
                jobId, req.Mode, outPath, composed.channels, inputs.Count, req.Register);
            _ = Task.Run(() => _library.RescanAsync());

            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false,
                Stage = "done",
                OutputPath = outPath,
                OutputChannels = composed.channels,
            };
        } catch (Exception ex) {
            _logger.LogError(ex, "Channel combine job {JobId} failed", jobId);
            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false,
                Stage = "error",
                Error = ex.Message,
            };
        }
    }

    // ── compose: RGB ─────────────────────────────────────────────────

    private static (ushort[] data, int channels) ComposeRgb(
            List<LoadedChannel> inputs, int W, int H) {
        // The UI guarantees a R/G/B mapping but we look variables up
        // by name (case-insensitive) so PixelMath callers that hand
        // off to ComposeRgb during transition get the same behaviour.
        var r = FindChannel(inputs, "R") ?? throw new InvalidOperationException(
            "RgbCompose requires a channel named 'R'.");
        var g = FindChannel(inputs, "G") ?? throw new InvalidOperationException(
            "RgbCompose requires a channel named 'G'.");
        var b = FindChannel(inputs, "B") ?? throw new InvalidOperationException(
            "RgbCompose requires a channel named 'B'.");
        int plane = W * H;
        var packed = new ushort[plane * 3];
        Buffer.BlockCopy(r, 0, packed, 0,                plane * sizeof(ushort));
        Buffer.BlockCopy(g, 0, packed, plane * sizeof(ushort),
                                                          plane * sizeof(ushort));
        Buffer.BlockCopy(b, 0, packed, plane * 2 * sizeof(ushort),
                                                          plane * sizeof(ushort));
        return (packed, 3);
    }

    private static ushort[]? FindChannel(List<LoadedChannel> inputs, string name) {
        foreach (var i in inputs) {
            if (string.Equals(i.Variable, name, StringComparison.OrdinalIgnoreCase)) {
                return i.Data;
            }
        }
        return null;
    }

    // ── write ────────────────────────────────────────────────────────

    private string WriteOutput(ushort[] data, int channels, int W, int H, int bitDepth,
            string target, string prefix,
            ChannelCombineRequest req,
            List<LoadedChannel> inputs,
            AffineTransform?[] transforms) {
        var rigName = _profile.ActiveEquipmentProfile?.Name ?? "Default";
        var outRoot = _profile.Active.ImageOutputDir
            ?? throw new InvalidOperationException("ImageOutputDir not set.");
        var dir = Path.Combine(outRoot, Sanitize(rigName), "integrated",
            Sanitize(target), "composed");
        Directory.CreateDirectory(dir);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var fileName = $"{prefix}_{Sanitize(target)}_{stamp}.fits";
        var outPath = Path.Combine(dir, fileName);
        int copy = 1;
        while (File.Exists(outPath)) {
            outPath = Path.Combine(dir,
                Path.GetFileNameWithoutExtension(fileName) + $"_{copy++}.fits");
        }

        var props = new ImageProperties {
            Width = W, Height = H, BitDepth = bitDepth,
            BayerPattern = NINA.Core.Enum.BayerPatternEnum.None,
            IsBayered = false,
            Channels = channels,
        };
        var meta = new ImageMetaData {
            CreationTime = DateTime.UtcNow,
            Camera   = new ImageMetaData.CameraInfo(),
            Telescope = new ImageMetaData.TelescopeInfo(),
            Observer = new ImageMetaData.ObserverInfo(),
            Target   = new ImageMetaData.TargetInfo { Name = target },
            Exposure = new ImageMetaData.ExposureInfo {
                Filter = channels == 3 ? "RGB" : "L",
                ImageType = "MASTERCOMP",
            },
        };
        var masterData = new BaseImageData(data, props, meta);

        var customKeywords = new List<KeyValuePair<string, string>> {
            new("CHCOMBINE", req.Mode),
            new("REGISTER", req.Register ? "T" : "F"),
            new("NORMLIZE", req.Normalize ? "T" : "F"),
            new("NCHANNEL", inputs.Count.ToString(CultureInfo.InvariantCulture)),
        };
        // REGREF is the variable name of the reference channel.
        if (req.Register) {
            int refIdx = PickReferenceIndex(req.Mode, inputs);
            customKeywords.Add(new("REGREF", inputs[refIdx].Variable));
        }
        // Per-channel headers: REG_<var> with the 6-tuple. Useful for
        // a future "show me what the combine did" debug view + makes
        // the recipe self-documenting in PixInsight's FITS Header tab.
        for (int i = 0; i < inputs.Count; i++) {
            var v = inputs[i].Variable;
            customKeywords.Add(new($"INP_{Truncate(v, 4)}", inputs[i].FileName));
            if (req.Register && transforms[i] != null) {
                var t = transforms[i]!;
                customKeywords.Add(new($"REG_{Truncate(v, 4)}",
                    $"{t.M00:F4},{t.M01:F4},{t.M10:F4},{t.M11:F4},{t.Tx:F2},{t.Ty:F2}"));
            }
        }

        FITSWriter.Write(masterData, outPath, customKeywords: customKeywords);
        return outPath;
    }

    // ── helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Pick which input is the registration / luminance reference.
    /// Deterministic so identical jobs produce identical outputs.
    /// </summary>
    private static int PickReferenceIndex(string? mode, List<LoadedChannel> inputs) {
        // LRGB: L is the natural anchor. Otherwise the first listed
        // channel wins; the UI orders R, G, B so RGB combines anchor
        // on R by default.
        if (string.Equals(mode, Modes.LrgbCompose, StringComparison.OrdinalIgnoreCase)) {
            for (int i = 0; i < inputs.Count; i++) {
                if (string.Equals(inputs[i].Variable, "L", StringComparison.OrdinalIgnoreCase)) {
                    return i;
                }
            }
        }
        return 0;
    }

    private static double ComputeMedian(ushort[] data) {
        if (data.Length == 0) return 0;
        // Histogram-based median over non-zero, non-saturated samples.
        // Matches the same exclusion rule AutoStretch uses, off-canvas
        // zeros from resampling shouldn't pull the median down.
        var hist = new int[65536];
        long count = 0;
        for (int i = 0; i < data.Length; i++) {
            ushort v = data[i];
            if (v == 0 || v == 65535) continue;
            hist[v]++;
            count++;
        }
        if (count == 0) return 0;
        long half = count / 2;
        long cum = 0;
        for (int i = 0; i < hist.Length; i++) {
            cum += hist[i];
            if (cum > half) return i;
        }
        return 0;
    }

    private static string Sanitize(string s) {
        if (string.IsNullOrWhiteSpace(s)) return "Unknown";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(' ', '_');
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "X"
            : (s.Length <= max ? s : s[..max]);

    private sealed record LoadedChannel(string Variable, ushort[] Data, string FileName);
}

public record ChannelCombineProgress {
    public string JobId { get; init; } = "";
    public bool InProgress { get; init; }
    public string Mode { get; init; } = "";
    public int Done { get; init; }
    public int Total { get; init; }
    public string Stage { get; init; } = "";
    public string? Error { get; init; }
    public string? OutputPath { get; init; }
    public int OutputChannels { get; init; }
    public string? ReferenceChannel { get; init; }
}
