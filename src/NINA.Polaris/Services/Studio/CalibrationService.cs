using System.Collections.Concurrent;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;

namespace NINA.Polaris.Services.Studio;

/// <summary>
/// Apply previously-built master frames (bias / dark / flat) to a batch
/// of raw light frames. The classical pipeline is:
///
///   calibrated = (light − dark) / normalized_flat
///
/// where <c>normalized_flat = flat_corrected / mean(flat_corrected)</c> and
/// <c>flat_corrected = master_flat − (master_dark_flat ?? master_bias)</c>.
///
/// In words: subtract the dark (removes thermal signal + bias offset),
/// then divide by the flat (corrects vignetting + dust shadows).
/// Bias is only applied directly when no dark is provided, darks already
/// contain the bias signal, so subtracting both double-counts.
///
/// Auto-matching: for each light, the service picks the master whose
/// (gain, exposure) is closest for darks and (gain, filter) matches for
/// flats. Override IDs in the request let the user pin a specific master
/// regardless of what auto-match would have picked.
///
/// Output goes to {rig}/calibrated/{target}/{filter}/cal_{originalName}.fits
/// with a CALSTAT header recording which corrections were applied (B / D / F).
/// </summary>
public class CalibrationService {
    private readonly FrameLibraryService _library;
    private readonly ProfileService _profile;
    private readonly ILogger<CalibrationService> _logger;
    private readonly ConcurrentDictionary<string, CalibrationProgress> _jobs = new();

    public CalibrationService(FrameLibraryService library, ProfileService profile,
                              ILogger<CalibrationService> logger) {
        _library = library;
        _profile = profile;
        _logger = logger;
    }

    public record CalibrationRequest(
        List<string> LightPaths,
        // Override hooks, null = auto-match each light to nearest
        // master via the FrameLibrary index. Setting any of these
        // pins a specific master path for the whole batch (Stack
        // sub-tab populates these from its masterDarks/Flats/Biases
        // slots).
        // UNIF-3a: switched from int? id to string? path so the
        // caller doesn't need a library lookup before posting.
        string? MasterDarkPath,
        string? MasterFlatPath,
        string? MasterBiasPath);

    public string StartJob(CalibrationRequest req) {
        var jobId = Guid.NewGuid().ToString("N")[..8];
        _jobs[jobId] = new CalibrationProgress {
            JobId = jobId,
            InProgress = true,
            Total = req.LightPaths.Count,
            Stage = "queued"
        };
        _ = Task.Run(() => RunJob(jobId, req));
        return jobId;
    }

    public CalibrationProgress? GetStatus(string jobId)
        => _jobs.TryGetValue(jobId, out var p) ? p : null;

    private void RunJob(string jobId, CalibrationRequest req) {
        try {
            // Available masters in the library, by category. Used
            // only by the auto-match fallback when the caller didn't
            // pin a specific master path.
            var masters = LoadMasterIndex();
            _logger.LogInformation("Calibration job {Job}: {Lights} lights, masters available: " +
                "darks={D} flats={F} biases={B}",
                jobId, req.LightPaths.Count,
                masters.Darks.Count, masters.Flats.Count, masters.Biases.Count);

            // Cache decoded masters across lights. Keyed by absolute
            // path now that we're not id-driven.
            var loadedMasters = new Dictionary<string, BaseImageData>(StringComparer.OrdinalIgnoreCase);
            BaseImageData LoadMaster(string path) {
                if (loadedMasters.TryGetValue(path, out var cached)) return cached;
                if (!File.Exists(path))
                    throw new InvalidOperationException($"Master missing on disk: {path}");
                using var fs = File.OpenRead(path);
                var img = FITSReader.Read(fs);
                loadedMasters[path] = img;
                return img;
            }

            var flatCache = new Dictionary<(string flat, string? cal), (double[] norm, double mean)>();
            (double[] norm, double mean) GetNormalizedFlat(string flatPath, string? calibratorPath) {
                var key = (flatPath, calibratorPath);
                if (flatCache.TryGetValue(key, out var cached)) return cached;
                var flat = LoadMaster(flatPath);
                BaseImageData? cal = calibratorPath != null ? LoadMaster(calibratorPath) : null;
                var (norm, mean) = NormalizeFlat(flat, cal);
                flatCache[key] = (norm, mean);
                return (norm, mean);
            }

            var rigName = _profile.ActiveEquipmentProfile?.Name ?? "Default";
            var outRoot = _profile.Active.ImageOutputDir
                ?? throw new InvalidOperationException("ImageOutputDir not set.");

            int done = 0;
            int succeeded = 0;
            int failed = 0;
            string? firstError = null;

            for (int i = 0; i < req.LightPaths.Count; i++) {
                var lightPath = req.LightPaths[i];
                _jobs[jobId] = _jobs[jobId] with {
                    Stage = $"calibrating {i + 1}/{req.LightPaths.Count}",
                    Done = done
                };
                try {
                    CalibrateOne(lightPath, req, masters, LoadMaster, GetNormalizedFlat,
                                 outRoot, rigName);
                    succeeded++;
                } catch (Exception ex) {
                    failed++;
                    if (firstError == null) firstError = $"{Path.GetFileName(lightPath)}: {ex.Message}";
                    _logger.LogWarning(ex, "Calibration of frame {Path} failed", lightPath);
                }
                done++;
            }

            // Re-index so the new calibrated frames appear in the browser.
            _ = Task.Run(() => _library.RescanAsync());

            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false,
                Stage = "done",
                Done = done,
                Succeeded = succeeded,
                Failed = failed,
                Error = firstError
            };
        } catch (Exception ex) {
            _logger.LogError(ex, "Calibration job {JobId} failed", jobId);
            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false,
                Stage = "error",
                Error = ex.Message
            };
        }
    }

    private void CalibrateOne(
            string lightPath, CalibrationRequest req, MasterIndex masters,
            Func<string, BaseImageData> loadMaster,
            Func<string, string?, (double[] norm, double mean)> getNormFlat,
            string outRoot, string rigName) {
        if (!File.Exists(lightPath))
            throw new InvalidOperationException($"Light missing on disk: {lightPath}");
        using var fs = File.OpenRead(lightPath);
        var light = FITSReader.Read(fs);
        int W = light.Properties.Width;
        int H = light.Properties.Height;
        int gain = light.MetaData.Camera.Gain;
        double exposure = light.MetaData.Exposure.ExposureTime;
        string filter = light.MetaData.Exposure.Filter ?? "";
        // Target name comes from the FITS OBJECT header; previously
        // we pulled it from the FrameLibrary row which was just a
        // cache of the same header value. Direct read avoids the
        // library lookup entirely.
        string target = light.MetaData.Target.Name ?? "";
        if (string.IsNullOrEmpty(target)) target = "Unknown";

        // ---- Pick which masters to apply --------------------------
        // FrameLibrary helpers return FrameRow (with .Path); we
        // discard the row beyond .Path here so the downstream
        // pipeline stays path-only.
        string? darkPath = req.MasterDarkPath ?? FindNearestDark(masters.Darks, exposure, gain)?.Path;
        string? flatPath = req.MasterFlatPath ?? FindMatchingFlat(masters.Flats, filter, gain)?.Path;
        // Bias only matters if we don't have a dark, darks already
        // include the bias signal. If user explicitly passes a bias
        // path we honour it as a flat-calibrator override.
        string? biasPath = req.MasterBiasPath
            ?? (darkPath == null ? FindMatchingBias(masters.Biases, gain)?.Path : null);
        // Flat needs a calibration frame to subtract before normalising:
        // prefer master_dark_flat (matched on flat's exposure+gain),
        // fall back to master_bias.
        string? flatCalibrator = null;
        if (flatPath != null) {
            var flatMeta = loadMaster(flatPath).MetaData;
            string? darkFlatPath = FindNearestDark(masters.DarkFlats,
                flatMeta.Exposure.ExposureTime, flatMeta.Camera.Gain)?.Path;
            flatCalibrator = darkFlatPath ?? biasPath ?? req.MasterBiasPath;
        }

        if (darkPath == null && flatPath == null && biasPath == null) {
            throw new InvalidOperationException(
                $"No matching masters found for this light (gain={gain}, " +
                $"exposure={exposure}s, filter='{filter}').");
        }

        // ---- Pixel math (in-place on a copy) ----------------------
        var pixels = new ushort[light.Data.Length];
        var src = light.Data;
        ushort[]? darkPx = darkPath != null ? loadMaster(darkPath).Data : null;
        ushort[]? biasPx = (darkPath == null && biasPath != null) ? loadMaster(biasPath).Data : null;
        (double[] norm, double mean)? flat = flatPath != null
            ? getNormFlat(flatPath, flatCalibrator)
            : null;

        if (darkPx != null && darkPx.Length != pixels.Length)
            throw new InvalidOperationException("Master dark dimensions don't match light.");
        if (biasPx != null && biasPx.Length != pixels.Length)
            throw new InvalidOperationException("Master bias dimensions don't match light.");
        if (flat.HasValue && flat.Value.norm.Length != pixels.Length)
            throw new InvalidOperationException("Master flat dimensions don't match light.");

        Parallel.For(0, pixels.Length, idx => {
            double v = src[idx];
            if (darkPx != null) v -= darkPx[idx];
            else if (biasPx != null) v -= biasPx[idx];
            if (flat.HasValue) {
                var n = flat.Value.norm[idx];
                if (n > 1e-6) v /= n;
            }
            pixels[idx] = (ushort)Math.Clamp(Math.Round(v), 0, 65535);
        });

        // ---- Write output ----------------------------------------
        var filterFolder = string.IsNullOrEmpty(filter) ? "L" : filter;
        var dir = Path.Combine(outRoot, Sanitize(rigName), "calibrated",
            Sanitize(target), Sanitize(filterFolder));
        Directory.CreateDirectory(dir);
        var fileName = "cal_" + Path.GetFileName(lightPath);
        var outPath = Path.Combine(dir, fileName);
        int copy = 1;
        while (File.Exists(outPath)) {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            outPath = Path.Combine(dir, $"{stem}_{copy++}.fits");
        }

        // Carry every header from the light so OBJCTRA/DEC + camera +
        // observer metadata survives calibration, Siril and PixInsight
        // expect those untouched on the calibrated frame.
        var props = new ImageProperties {
            Width = W, Height = H, BitDepth = light.Properties.BitDepth,
            BayerPattern = light.Properties.BayerPattern,
            IsBayered = light.Properties.IsBayered
        };
        var calibrated = new BaseImageData(pixels, props, light.MetaData);
        // CALSTAT = letters describing what got applied; matches the
        // SBIG convention echoed by Siril and PixInsight.
        var calstat = (biasPx != null ? "B" : "") + (darkPx != null ? "D" : "") + (flat.HasValue ? "F" : "");
        var kw = new List<KeyValuePair<string, string>> {
            new("CALSTAT", calstat)
        };
        if (darkPath != null) kw.Add(new("MDARK", Path.GetFileName(darkPath)));
        if (flatPath != null) kw.Add(new("MFLAT", Path.GetFileName(flatPath)));
        if (biasPath != null && darkPath == null) kw.Add(new("MBIAS", Path.GetFileName(biasPath)));
        FITSWriter.Write(calibrated, outPath, customKeywords: kw);
    }

    // --- Helpers -------------------------------------------------

    private MasterIndex LoadMasterIndex() {
        // Pull from the SQLite cache. Filter by type, anything tagged
        // MASTER{X} via the ImageWriterService or the ST-3 master writer
        // qualifies. Frames captured before STUDIO indexed them as
        // IMAGETYP=LIGHT, those won't show up here, which is correct.
        var all = _library.Query(new FrameQuery(null, null, null, null, null, 500, 0));
        var index = new MasterIndex();
        foreach (var f in all) {
            switch (f.ImageType?.ToUpperInvariant()) {
                case "MASTERBIAS":     index.Biases.Add(f); break;
                case "MASTERDARK":     index.Darks.Add(f); break;
                case "MASTERFLAT":     index.Flats.Add(f); break;
                case "MASTERDARKFLAT": index.DarkFlats.Add(f); break;
            }
        }
        return index;
    }

    // UNIF-3a: return the whole FrameRow (caller grabs .Path) instead
    // of the int? id. The internal pipeline is path-driven now.
    private static FrameRow? FindNearestDark(IReadOnlyList<FrameRow> darks, double exposure, int gain) {
        if (darks.Count == 0) return null;
        FrameRow? best = null;
        double bestDelta = double.MaxValue;
        foreach (var d in darks) {
            if (d.Gain != gain) continue;
            var delta = Math.Abs(d.ExposureSec - exposure);
            if (delta < bestDelta) { bestDelta = delta; best = d; }
        }
        return best;
    }

    private static FrameRow? FindMatchingFlat(IReadOnlyList<FrameRow> flats, string filter, int gain) {
        if (flats.Count == 0) return null;
        return flats.FirstOrDefault(f =>
            f.Gain == gain &&
            string.Equals(f.Filter ?? "", filter ?? "", StringComparison.OrdinalIgnoreCase));
    }

    private static FrameRow? FindMatchingBias(IReadOnlyList<FrameRow> biases, int gain) {
        if (biases.Count == 0) return null;
        return biases.FirstOrDefault(b => b.Gain == gain);
    }

    /// <summary>Build the normalised flat: subtract a bias/dark-flat
    /// calibrator if available, divide by mean. Returns a per-pixel
    /// double[] (precision matters for the division) plus the mean
    /// for diagnostics.</summary>
    private static (double[] norm, double mean) NormalizeFlat(BaseImageData flat, BaseImageData? cal) {
        var n = flat.Data.Length;
        var corrected = new double[n];
        double sum = 0;
        if (cal != null && cal.Data.Length == n) {
            for (int i = 0; i < n; i++) {
                var v = (double)flat.Data[i] - cal.Data[i];
                if (v < 0) v = 0;
                corrected[i] = v;
                sum += v;
            }
        } else {
            for (int i = 0; i < n; i++) {
                corrected[i] = flat.Data[i];
                sum += flat.Data[i];
            }
        }
        var mean = sum / n;
        if (mean < 1) mean = 1;   // pathological flat; avoid divide-by-zero
        for (int i = 0; i < n; i++) corrected[i] /= mean;
        return (corrected, mean);
    }

    private static string Sanitize(string s) {
        if (string.IsNullOrWhiteSpace(s)) return "Unknown";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(' ', '_');
    }

    private record MasterIndex {
        public List<FrameRow> Biases    { get; } = new();
        public List<FrameRow> Darks     { get; } = new();
        public List<FrameRow> Flats     { get; } = new();
        public List<FrameRow> DarkFlats { get; } = new();
    }
}

public record CalibrationProgress {
    public string JobId { get; init; } = "";
    public bool InProgress { get; init; }
    public int Done { get; init; }
    public int Total { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public string Stage { get; init; } = "";
    public string? Error { get; init; }
}
