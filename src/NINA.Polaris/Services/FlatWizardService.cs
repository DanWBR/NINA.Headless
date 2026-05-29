using System.Text.Json;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>
/// Automates flat-field acquisition: for each selected filter, perform a
/// binary search on exposure time until the median pixel value falls within
/// a tolerance band around the target ADU, then capture a configurable
/// number of flat frames at that exposure. Trained exposures are persisted
/// per (filter, binning) tuple so the next session can skip the search.
///
/// Flow:
///   1. For each filter in the request, move the filter wheel to it.
///   2. Look up a previously-trained exposure for that filter+binning;
///      use it as the initial guess. Otherwise start at midpoint of
///      [minExp, maxExp].
///   3. Capture, measure median, decide:
///        median &gt; target+tol  → exposure too long  → halve upper bound
///        median &lt; target-tol  → exposure too short → double lower bound
///        else → converged; save trained exposure.
///   4. Capture N flat frames at the converged exposure and route them
///      through ImageWriterService so they're persisted with IMAGETYP=FLAT.
/// </summary>
public class FlatWizardService {
    private readonly EquipmentManager _equip;
    private readonly ImageWriterService _imageWriter;
    private readonly ProfileService _profile;
    private readonly ILogger<FlatWizardService> _logger;

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private readonly object _stateLock = new();

    public FlatWizardState State { get; private set; } = FlatWizardState.Idle;
    public FlatWizardProgress Progress { get; private set; } = new();
    public string? LastError { get; private set; }

    /// <summary>Per-binning, per-filter trained exposure cache (seconds).</summary>
    public Dictionary<string, double> TrainedExposures { get; private set; } = new();

    private string _trainedExposuresPath = "";

    public FlatWizardService(EquipmentManager equip, ImageWriterService imageWriter,
        ProfileService profile, ILogger<FlatWizardService> logger, IConfiguration config) {
        _equip = equip;
        _imageWriter = imageWriter;
        _profile = profile;
        _logger = logger;
        var dir = config.GetValue("FlatWizard:TrainedExposuresDir",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA.Polaris"))!;
        Directory.CreateDirectory(dir);
        _trainedExposuresPath = Path.Combine(dir, "trained-flats.json");
        LoadTrainedExposures();
    }

    public void Start(FlatWizardRequest request) {
        lock (_stateLock) {
            if (State == FlatWizardState.Running)
                throw new InvalidOperationException("Flat wizard already running");
            if (_equip.Camera == null)
                throw new InvalidOperationException("No camera connected");
            if (request.Filters == null || request.Filters.Count == 0)
                throw new ArgumentException("At least one filter must be specified");
            if (request.MinExposure <= 0 || request.MaxExposure <= request.MinExposure)
                throw new ArgumentException("MinExposure must be > 0 and < MaxExposure");
            if (request.TargetAdu <= 0)
                throw new ArgumentException("TargetAdu must be > 0");

            _cts = new CancellationTokenSource();
            State = FlatWizardState.Running;
            LastError = null;
            Progress = new FlatWizardProgress {
                StartedAt = DateTime.UtcNow,
                TotalFilters = request.Filters.Count,
                TotalFramesPerFilter = request.FramesPerFilter
            };
        }

        _runTask = Task.Run(() => RunAsync(request, _cts!.Token));
        _logger.LogInformation("Flat wizard started: {N} filters, {F} frames each, target {Adu} ADU",
            request.Filters.Count, request.FramesPerFilter, request.TargetAdu);
    }

    public void Abort() {
        lock (_stateLock) {
            if (State != FlatWizardState.Running) return;
            _cts?.Cancel();
        }
    }

    /// <summary>
    /// Public lookup into the trained-exposures cache. Returns true and
    /// the cached exposure if a previous wizard run (or auto-find from
    /// the sequence engine) converged on this (filter, binning). Used
    /// by AUTORUN's "Auto" flat-exposure toggle to skip the search
    /// when a recent value is available.
    /// </summary>
    public bool TryGetTrainedExposure(string filter, int binning, out double exposureSec) {
        var key = $"{filter ?? ""}_bin{Math.Max(1, binning)}";
        return TrainedExposures.TryGetValue(key, out exposureSec);
    }

    /// <summary>
    /// Run the binary-search half of the wizard for a single
    /// (filter, binning) pair without capturing the N flat frames
    /// afterwards. Returns the converged exposure (seconds) on
    /// success, null when convergence fails (bracket collapsed,
    /// camera missing, etc). On success the result is also written
    /// to the trained-exposures cache so the next call hits the
    /// short path via <see cref="TryGetTrainedExposure"/>.
    ///
    /// Intended for the AUTORUN sequence engine when the user picked
    /// "Auto" for a FLAT item's exposure: lookup → if miss, call
    /// this once before the capture loop, then capture N frames at
    /// the returned value. This stays out of the wizard's main
    /// state machine (does not flip <see cref="State"/>) so the
    /// AUTORUN run owns its own progress reporting.
    /// </summary>
    public async Task<double?> AutoFindExposureAsync(
        string filter, int binning,
        int targetAdu = 30000, double tolerance = 0.05,
        double minExposure = 0.1, double maxExposure = 30.0,
        int maxIterations = 10,
        CancellationToken ct = default) {
        var camera = _equip.Camera ?? throw new InvalidOperationException("No camera connected");
        binning = Math.Max(1, binning);

        // Honour the same trained-exposure seed the main wizard uses, so
        // the second invocation in a session converges in 1-2 iterations
        // instead of 5-8.
        var key = $"{filter ?? ""}_bin{binning}";
        double exposure = TrainedExposures.TryGetValue(key, out var trained)
            ? trained
            : (minExposure + maxExposure) / 2;
        double lo = minExposure;
        double hi = maxExposure;

        try { await camera.SetBinningAsync(binning, binning, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Auto-flat: set binning {B} failed", binning); }

        for (int attempt = 0; attempt < maxIterations; attempt++) {
            ct.ThrowIfCancellationRequested();
            _logger.LogDebug("Auto-flat search iter {I} for {Filter} bin{B}: trying {Exp}s",
                attempt + 1, filter, binning, exposure);

            var img = await camera.CaptureAsync(exposure, ct);
            img.MetaData.Exposure.ImageType = "FLAT";
            var median = ComputeMedian(img);

            var lower = targetAdu * (1 - tolerance);
            var upper = targetAdu * (1 + tolerance);
            if (median >= lower && median <= upper) {
                _logger.LogInformation(
                    "Auto-flat converged at {Exp}s for {Filter} bin{B} (median={Med}, target={Tgt})",
                    exposure, filter, binning, median, targetAdu);
                TrainedExposures[key] = exposure;
                SaveTrainedExposures();
                return exposure;
            }

            if (median > upper) { hi = exposure; exposure = (lo + exposure) / 2; }
            else                { lo = exposure; exposure = (exposure + hi) / 2; }
            exposure = Math.Clamp(exposure, minExposure, maxExposure);

            if (Math.Abs(hi - lo) < 0.001) {
                _logger.LogWarning("Auto-flat bracket collapsed for {Filter} bin{B} (last median {Med})",
                    filter, binning, median);
                return null;
            }
        }

        _logger.LogWarning("Auto-flat did not converge for {Filter} bin{B} after {N} iterations",
            filter, binning, maxIterations);
        return null;
    }

    private async Task RunAsync(FlatWizardRequest request, CancellationToken ct) {
        var camera = _equip.Camera!;
        var fw = _equip.FilterWheel;
        var binning = Math.Max(1, request.Binning);
        var maxVal = (1 << camera.BitDepth) - 1;
        if (maxVal <= 0) maxVal = 65535;

        try {
            for (int fi = 0; fi < request.Filters.Count; fi++) {
                ct.ThrowIfCancellationRequested();
                var filterName = request.Filters[fi];
                Progress = Progress with { CurrentFilterIndex = fi, CurrentFilter = filterName };

                // 1. Switch filter
                if (fw != null && !string.IsNullOrEmpty(filterName)) {
                    _logger.LogInformation("Flat wizard: switching to filter {F}", filterName);
                    try { await fw.SetFilterByNameAsync(filterName, ct); }
                    catch (Exception ex) {
                        _logger.LogWarning(ex, "Filter switch failed for {F}, using current filter", filterName);
                    }
                }

                // 2. Apply binning
                try { await camera.SetBinningAsync(binning, binning, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "Set binning {B} failed", binning); }

                // 3. Find converged exposure via binary search
                var key = $"{filterName}_bin{binning}";
                double exposure = TrainedExposures.TryGetValue(key, out var trained)
                    ? trained
                    : (request.MinExposure + request.MaxExposure) / 2;
                double lo = request.MinExposure;
                double hi = request.MaxExposure;
                bool converged = false;

                for (int attempt = 0; attempt < request.MaxSearchIterations; attempt++) {
                    ct.ThrowIfCancellationRequested();
                    Progress = Progress with { Phase = "searching", SearchAttempt = attempt + 1, CurrentExposure = exposure };
                    _logger.LogDebug("Flat search iter {I}: trying {Exp}s", attempt + 1, exposure);

                    var img = await camera.CaptureAsync(exposure, ct);
                    img.MetaData.Exposure.ImageType = "FLAT";
                    var median = ComputeMedian(img);
                    Progress = Progress with { LastMedian = median };

                    var lower = request.TargetAdu * (1 - request.Tolerance);
                    var upper = request.TargetAdu * (1 + request.Tolerance);
                    if (median >= lower && median <= upper) {
                        _logger.LogInformation("Converged at {Exp}s (median={Med}, target={Tgt})",
                            exposure, median, request.TargetAdu);
                        converged = true;
                        TrainedExposures[key] = exposure;
                        SaveTrainedExposures();
                        break;
                    }

                    if (median > upper) {
                        hi = exposure;
                        exposure = (lo + exposure) / 2;
                    } else { // median < lower
                        lo = exposure;
                        exposure = (exposure + hi) / 2;
                    }
                    exposure = Math.Clamp(exposure, request.MinExposure, request.MaxExposure);

                    if (Math.Abs(hi - lo) < 0.001) {
                        _logger.LogWarning("Search collapsed without converging (last median {Med})", median);
                        break;
                    }
                }

                if (!converged) {
                    Progress.FilterResults.Add(new FlatWizardFilterResult {
                        Filter = filterName, Converged = false, FinalExposure = exposure
                    });
                    continue;
                }

                // 4. Capture N flat frames at the converged exposure
                int saved = 0;
                for (int n = 0; n < request.FramesPerFilter; n++) {
                    ct.ThrowIfCancellationRequested();
                    Progress = Progress with { Phase = "capturing", FramesCaptured = n };
                    var img = await camera.CaptureAsync(exposure, ct);
                    img.MetaData.Exposure.ImageType = "FLAT";
                    img.MetaData.Exposure.ExposureTime = exposure;
                    if (!string.IsNullOrEmpty(filterName))
                        img.MetaData.Exposure.Filter = filterName;
                    var path = _imageWriter.SaveImage(img, targetName: "Flat", imageType: "FLAT");
                    if (path != null) saved++;
                }

                Progress.FilterResults.Add(new FlatWizardFilterResult {
                    Filter = filterName, Converged = true, FinalExposure = exposure, FramesCaptured = saved
                });
            }

            lock (_stateLock) { State = FlatWizardState.Idle; }
            _logger.LogInformation("Flat wizard complete");

        } catch (OperationCanceledException) {
            lock (_stateLock) { State = FlatWizardState.Idle; LastError = "Cancelled"; }
            _logger.LogInformation("Flat wizard cancelled");
        } catch (Exception ex) {
            lock (_stateLock) { State = FlatWizardState.Idle; LastError = ex.Message; }
            _logger.LogError(ex, "Flat wizard failed");
        }
    }

    /// <summary>O(n) median via histogram, same trick as ImageStatistics.</summary>
    private static double ComputeMedian(IImageData img) {
        var data = img.Data;
        if (data.Length == 0) return 0;
        var hist = new int[65536];
        for (int i = 0; i < data.Length; i++) hist[data[i]]++;
        long half = data.Length / 2;
        long cum = 0;
        for (int i = 0; i < hist.Length; i++) {
            cum += hist[i];
            if (cum > half) return i;
        }
        return 0;
    }

    private void LoadTrainedExposures() {
        try {
            if (!File.Exists(_trainedExposuresPath)) return;
            var json = File.ReadAllText(_trainedExposuresPath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, double>>(json);
            if (loaded != null) TrainedExposures = loaded;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to load trained flats from {Path}", _trainedExposuresPath);
        }
    }

    private void SaveTrainedExposures() {
        try {
            var json = JsonSerializer.Serialize(TrainedExposures,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_trainedExposuresPath, json);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to save trained flats");
        }
    }
}

public enum FlatWizardState { Idle, Running }

public class FlatWizardRequest {
    public List<string> Filters { get; set; } = new();
    public int FramesPerFilter { get; set; } = 20;
    public int TargetAdu { get; set; } = 30000;
    public double Tolerance { get; set; } = 0.05; // ±5%
    public double MinExposure { get; set; } = 0.1;
    public double MaxExposure { get; set; } = 30.0;
    public int Binning { get; set; } = 1;
    public int MaxSearchIterations { get; set; } = 10;
}

public record FlatWizardProgress {
    public DateTime StartedAt { get; init; }
    public int TotalFilters { get; init; }
    public int CurrentFilterIndex { get; init; }
    public string CurrentFilter { get; init; } = "";
    public string Phase { get; init; } = "idle";
    public int SearchAttempt { get; init; }
    public double CurrentExposure { get; init; }
    public double LastMedian { get; init; }
    public int TotalFramesPerFilter { get; init; }
    public int FramesCaptured { get; init; }
    public List<FlatWizardFilterResult> FilterResults { get; init; } = new();
}

public class FlatWizardFilterResult {
    public string Filter { get; set; } = "";
    public bool Converged { get; set; }
    public double FinalExposure { get; set; }
    public int FramesCaptured { get; set; }
}
