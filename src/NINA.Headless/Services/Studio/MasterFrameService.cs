using System.Collections.Concurrent;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;

namespace NINA.Headless.Services.Studio;

/// <summary>
/// Stack N raw calibration frames into a single master via mean, median,
/// or sigma-clipped mean.
///
/// Pipeline per job:
///   1. Load + validate all input FITS files. They must agree on
///      width / height / bit depth (a mismatch usually means the user
///      selected frames from two different rigs by mistake).
///   2. Allocate the output ushort[] (same size as a single frame).
///   3. Walk every pixel coordinate; gather the N values from the
///      loaded frames; reduce via the chosen <see cref="IntegrationMethod"/>;
///      write into the output.
///   4. Build a synthetic <c>BaseImageData</c> with IMAGETYP =
///      MASTER{TYPE}, NSUBS = N, INTMETH = method.
///   5. Write to {rig}/calibration/masters/master_{type}_{key}_{N}.fits.
///   6. Trigger a FrameLibraryService rescan so the new file shows
///      up in the browser.
///
/// Memory model: v1 loads every input pixel buffer into memory at
/// once. For a typical session (~20 frames × 20 MP × 2 B = ~800 MB)
/// this fits comfortably on a 4 GB RPi. Larger stacks should tile;
/// that's tracked as a follow-up.
/// </summary>
public class MasterFrameService {
    private readonly FrameLibraryService _library;
    private readonly ProfileService _profile;
    private readonly ILogger<MasterFrameService> _logger;
    private readonly ConcurrentDictionary<string, MasterProgress> _jobs = new();

    public MasterFrameService(FrameLibraryService library, ProfileService profile,
                              ILogger<MasterFrameService> logger) {
        _library = library;
        _profile = profile;
        _logger = logger;
    }

    /// <summary>Kick off integration in the background. Returns the
    /// job id the UI polls on /api/studio/masters/{id}/status.</summary>
    public string StartJob(IReadOnlyList<int> frameIds, MasterType type, IntegrationMethod method) {
        var jobId = Guid.NewGuid().ToString("N")[..8];
        var progress = new MasterProgress {
            JobId = jobId,
            InProgress = true,
            Total = frameIds.Count,
            Stage = "queued"
        };
        _jobs[jobId] = progress;
        _ = Task.Run(() => RunJob(jobId, frameIds, type, method));
        return jobId;
    }

    public MasterProgress? GetStatus(string jobId)
        => _jobs.TryGetValue(jobId, out var p) ? p : null;

    private void RunJob(string jobId, IReadOnlyList<int> frameIds, MasterType type, IntegrationMethod method) {
        try {
            // ---- Phase 1: load all inputs ---------------------------
            _jobs[jobId] = _jobs[jobId] with { Stage = "loading", Done = 0 };
            var loaded = new List<BaseImageData>(frameIds.Count);
            int? width = null, height = null, bitDepth = null;
            double sumExposure = 0;
            int gain = 0;
            string filter = "";

            for (int i = 0; i < frameIds.Count; i++) {
                var row = _library.GetById(frameIds[i]);
                if (row == null || !File.Exists(row.Path))
                    throw new InvalidOperationException($"Frame {frameIds[i]} is missing on disk.");
                using var fs = File.OpenRead(row.Path);
                var img = FITSReader.Read(fs);
                if (width == null) {
                    width = img.Properties.Width;
                    height = img.Properties.Height;
                    bitDepth = img.Properties.BitDepth;
                    gain = img.MetaData.Camera.Gain;
                    filter = img.MetaData.Exposure.Filter ?? "";
                } else {
                    if (img.Properties.Width != width || img.Properties.Height != height) {
                        throw new InvalidOperationException(
                            $"Frame {row.FileName} is {img.Properties.Width}×{img.Properties.Height}, " +
                            $"expected {width}×{height}. Frames must agree.");
                    }
                }
                sumExposure += img.MetaData.Exposure.ExposureTime;
                loaded.Add(img);
                _jobs[jobId] = _jobs[jobId] with { Done = i + 1 };
            }

            // ---- Phase 2: integrate per-pixel -----------------------
            _jobs[jobId] = _jobs[jobId] with { Stage = "integrating", Done = 0, Total = height!.Value };
            int W = width!.Value;
            int H = height!.Value;
            int N = loaded.Count;
            var output = new ushort[W * H];

            // Pre-snapshot the pixel buffers so the inner loop hits a
            // dense ushort[][] (loaded[k].Data is a property access).
            var stacks = new ushort[N][];
            for (int k = 0; k < N; k++) stacks[k] = loaded[k].Data;

            // One scratch buffer per parallel partition (median sorts
            // in-place). The Parallel.For overload returns a per-thread
            // local that gets reused across iterations of the same
            // worker, avoiding per-row allocation.
            int rowsDone = 0;
            Parallel.For(0, H, () => new ushort[N], (y, _, scratch) => {
                int rowOff = y * W;
                for (int x = 0; x < W; x++) {
                    int idx = rowOff + x;
                    for (int k = 0; k < N; k++) scratch[k] = stacks[k][idx];
                    output[idx] = method switch {
                        IntegrationMethod.Mean   => IntegrationMath.Mean(scratch),
                        IntegrationMethod.Median => IntegrationMath.Median(scratch),
                        IntegrationMethod.SigmaClippedMean
                                                 => IntegrationMath.SigmaClippedMean(scratch),
                        _                        => IntegrationMath.Mean(scratch)
                    };
                }
                // Atomic progress bump — the UI polls at ~1 Hz so we
                // don't need to throttle here; the contention cost on a
                // 32-MP image is negligible compared to the per-pixel
                // integration work.
                var done = System.Threading.Interlocked.Increment(ref rowsDone);
                _jobs[jobId] = _jobs[jobId] with { Done = done };
                return scratch;
            }, _ => { });

            // ---- Phase 3: write master FITS -------------------------
            _jobs[jobId] = _jobs[jobId] with { Stage = "writing" };

            var rigName = _profile.ActiveEquipmentProfile?.Name ?? "Default";
            var outRoot = _profile.Active.ImageOutputDir;
            if (string.IsNullOrWhiteSpace(outRoot))
                throw new InvalidOperationException("ImageOutputDir not set.");
            var dir = Path.Combine(outRoot, Sanitize(rigName), "calibration", "masters");
            Directory.CreateDirectory(dir);

            var key = type switch {
                MasterType.Bias     => $"g{gain}",
                MasterType.Dark     => $"{sumExposure / N:0.##}s_g{gain}",
                MasterType.DarkFlat => $"{sumExposure / N:0.##}s_g{gain}",
                MasterType.Flat     => $"{(string.IsNullOrEmpty(filter) ? "L" : filter)}_g{gain}",
                _                   => "master"
            };
            var fileName = $"master_{type.ToString().ToLowerInvariant()}_{key}_x{N}.fits";
            foreach (var c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');
            var outPath = Path.Combine(dir, fileName);
            int copy = 1;
            while (File.Exists(outPath))
                outPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(fileName) + $"_{copy++}.fits");

            var props = new ImageProperties {
                Width = W, Height = H, BitDepth = bitDepth!.Value,
                BayerPattern = loaded[0].Properties.BayerPattern,
                IsBayered = loaded[0].Properties.IsBayered
            };
            // Carry over key headers from the first input so the master
            // looks "real" to PixInsight / Siril (same camera, gain,
            // average exposure per sub).
            var meta = new ImageMetaData {
                CreationTime = DateTime.UtcNow,
                Camera   = loaded[0].MetaData.Camera,
                Telescope = loaded[0].MetaData.Telescope,
                Observer = loaded[0].MetaData.Observer,
                Target   = loaded[0].MetaData.Target,
                Exposure = new ImageMetaData.ExposureInfo {
                    ExposureTime = sumExposure / N,
                    Filter       = filter,
                    ImageType    = MasterImageType(type)
                }
            };
            var masterData = new BaseImageData(output, props, meta);

            var customKeywords = new List<KeyValuePair<string, string>> {
                new("NSUBS",   N.ToString()),
                new("INTMETH", method.ToString())
            };
            FITSWriter.Write(masterData, outPath, customKeywords: customKeywords);

            _logger.LogInformation("Master {Type} written: {Path} (n={N}, method={Method})",
                type, outPath, N, method);

            // Drop the master into the library cache so it shows up in
            // the browser immediately. Best-effort — if the index walk
            // is busy the next user-triggered rescan will pick it up.
            _ = Task.Run(() => _library.RescanAsync());

            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false,
                Stage = "done",
                OutputPath = outPath
            };
        } catch (Exception ex) {
            _logger.LogError(ex, "Master frame job {JobId} failed", jobId);
            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false,
                Stage = "error",
                Error = ex.Message
            };
        }
    }

    private static string MasterImageType(MasterType type) => type switch {
        MasterType.Bias     => "MASTERBIAS",
        MasterType.Dark     => "MASTERDARK",
        MasterType.Flat     => "MASTERFLAT",
        MasterType.DarkFlat => "MASTERDARKFLAT",
        _                   => "MASTER"
    };

    private static string Sanitize(string s) {
        if (string.IsNullOrWhiteSpace(s)) return "Unknown";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(' ', '_');
    }
}

public enum MasterType { Bias, Dark, Flat, DarkFlat }
public enum IntegrationMethod { Mean, Median, SigmaClippedMean }

public record MasterProgress {
    public string JobId { get; init; } = "";
    public bool InProgress { get; init; }
    public int Done { get; init; }
    public int Total { get; init; }
    public string Stage { get; init; } = "";          // queued | loading | integrating | writing | done | error
    public string? Error { get; init; }
    public string? OutputPath { get; init; }
}
