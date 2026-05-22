using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using NINA.Headless.Services;

namespace NINA.Headless.Services.External;

/// <summary>
/// Driver for the GraXpert CLI. Three operations are unified under
/// one service: background extraction (all versions), deconvolution
/// (v3.0+), and denoising (v3.0+). Each frame is processed by a
/// single subprocess call — GraXpert has no batch mode of its own,
/// so batches are sequential (or with bounded concurrency on beefy
/// hardware) at this layer.
///
/// Output naming convention so multiple operations on the same file
/// don't collide: input.fits → input_bge.fits / input_decon.fits /
/// input_denoise.fits. Encoded by <see cref="OutputSuffix"/>.
/// </summary>
public class GraXpertService {
    private readonly IConfiguration _config;
    private readonly ProfileService _profile;
    private readonly ILogger<GraXpertService> _logger;

    private readonly ConcurrentDictionary<string, GraXpertBatchJob> _jobs = new();
    private readonly object _versionLock = new();
    private string? _cachedVersion;
    private bool _versionChecked;

    public GraXpertService(IConfiguration config, ProfileService profile,
                            ILogger<GraXpertService> logger) {
        _config = config;
        _profile = profile;
        _logger = logger;
    }

    public string? BinaryPath => Locate();
    public bool IsAvailable => !string.IsNullOrEmpty(BinaryPath);

    /// <summary>Cached version probed via `graxpert --version`. Empty when missing.</summary>
    public string Version {
        get {
            lock (_versionLock) {
                if (_versionChecked) return _cachedVersion ?? "";
                _versionChecked = true;
                _cachedVersion = ProbeVersion();
                return _cachedVersion ?? "";
            }
        }
    }

    /// <summary>
    /// Decon + Denoise landed in GraXpert 3.0. Older builds only
    /// expose background extraction. The UI uses these flags to grey
    /// out the operations the user can't actually run.
    /// </summary>
    public bool SupportsDeconvolution => IsVersionAtLeast(3, 0);
    public bool SupportsDenoising     => IsVersionAtLeast(3, 0);

    public void InvalidateVersionCache() {
        lock (_versionLock) {
            _versionChecked = false;
            _cachedVersion = null;
        }
    }

    public IReadOnlyList<BinaryLocator.Candidate> EnumerateBinaryCandidates() =>
        BinaryLocator.Enumerate(_profile.Active.GraXpertPath,
            WindowsCandidates(), LinuxCandidates(), MacCandidates(), "graxpert");

    // --- Single-frame processing ------------------------------------

    public async Task<GraXpertResult> ProcessFrameAsync(string inputPath,
                                                         GraXpertOptions opts,
                                                         CancellationToken ct) {
        if (!IsAvailable)
            return new GraXpertResult("", null, opts.Operation, 0, "GraXpert not installed");
        if (!File.Exists(inputPath))
            return new GraXpertResult("", null, opts.Operation, 0,
                $"Input file not found: {inputPath}");

        // Block decon/denoise on old GraXpert installs — friendlier
        // than letting the subprocess fail with an obscure error.
        if (opts.Operation == GraXpertOperation.Deconvolution && !SupportsDeconvolution)
            return new GraXpertResult("", null, opts.Operation, 0,
                "Deconvolution requires GraXpert v3.0+");
        if (opts.Operation == GraXpertOperation.Denoising && !SupportsDenoising)
            return new GraXpertResult("", null, opts.Operation, 0,
                "Denoising requires GraXpert v3.0+");

        var outputPath = DefaultOutputPath(inputPath, opts.Operation);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var args = BuildArgs(inputPath, outputPath, opts);
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("FileOp GraXpert {Op} {In} -> {Out}",
            opts.Operation, inputPath, outputPath);

        try {
            using var proc = Process.Start(new ProcessStartInfo {
                FileName = BinaryPath!,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(inputPath) ?? Path.GetTempPath()
            });
            if (proc == null)
                return new GraXpertResult("", null, opts.Operation, 0, "Failed to start GraXpert");

            // Read stdout/stderr to avoid pipe-buffer deadlocks on
            // long runs. We don't parse anything — GraXpert doesn't
            // emit structured progress.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            await proc.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            sw.Stop();

            if (proc.ExitCode != 0) {
                var err = Truncate(stderr.Trim().Length > 0 ? stderr : stdout, 500);
                return new GraXpertResult("", null, opts.Operation,
                    sw.Elapsed.TotalSeconds, $"exit {proc.ExitCode}: {err}");
            }
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0) {
                return new GraXpertResult("", null, opts.Operation,
                    sw.Elapsed.TotalSeconds, "GraXpert reported success but no output file appeared");
            }

            string? bgPath = null;
            if (opts.Operation == GraXpertOperation.BackgroundExtraction && opts.SaveBackground) {
                var candidate = Path.ChangeExtension(outputPath, null) + "_bg" + Path.GetExtension(outputPath);
                if (File.Exists(candidate)) bgPath = candidate;
            }

            return new GraXpertResult(outputPath, bgPath, opts.Operation,
                sw.Elapsed.TotalSeconds, null);
        } catch (OperationCanceledException) {
            return new GraXpertResult("", null, opts.Operation,
                sw.Elapsed.TotalSeconds, "Cancelled");
        } catch (Exception ex) {
            _logger.LogError(ex, "GraXpert {Op} threw on {Path}", opts.Operation, inputPath);
            return new GraXpertResult("", null, opts.Operation,
                sw.Elapsed.TotalSeconds, ex.Message);
        }
    }

    // --- Batch processing (sequential or bounded concurrency) -------

    public GraXpertBatchJob StartBatch(GraXpertBatchRequest req, CancellationToken outerCt = default) {
        var jobId = Guid.NewGuid().ToString("N")[..8];
        var job = new GraXpertBatchJob {
            JobId = jobId,
            Operation = req.Options.Operation,
            Total = req.InputPaths.Count,
            Done = 0,
            Failed = 0,
            CurrentlyProcessing = new List<string>(),
            Results = new List<GraXpertResult>(),
            StartedAt = DateTime.UtcNow
        };
        _jobs[jobId] = job;

        _ = Task.Run(() => RunBatchAsync(job, req, outerCt), outerCt);
        return job;
    }

    public GraXpertBatchJob? GetJob(string jobId) =>
        _jobs.TryGetValue(jobId, out var j) ? j : null;

    public IReadOnlyList<GraXpertBatchJob> ActiveJobs =>
        _jobs.Values.Where(j => j.CompletedAt == null && !j.CancelRequested).ToList();

    public bool CancelJob(string jobId) {
        if (!_jobs.TryGetValue(jobId, out var j)) return false;
        if (j.CompletedAt != null) return false;
        j.CancelRequested = true;
        return true;
    }

    private async Task RunBatchAsync(GraXpertBatchJob job, GraXpertBatchRequest req,
                                      CancellationToken outerCt) {
        // GraXpert models are RAM-heavy (3-8 GB depending on op);
        // default Concurrency=1 keeps the RPi alive. Power users on
        // Windows mini PCs can crank it up.
        var concurrency = Math.Max(1, req.Concurrency);
        using var sem = new SemaphoreSlim(concurrency, concurrency);
        var tasks = new List<Task>();
        foreach (var input in req.InputPaths) {
            if (job.CancelRequested) break;
            await sem.WaitAsync(outerCt);
            tasks.Add(Task.Run(async () => {
                try {
                    if (job.CancelRequested) return;
                    lock (job) job.CurrentlyProcessing.Add(input);
                    var res = await ProcessFrameAsync(input, req.Options, outerCt);
                    lock (job) {
                        job.CurrentlyProcessing.Remove(input);
                        job.Results.Add(res);
                        if (string.IsNullOrEmpty(res.Error)) job.Done++;
                        else                                  job.Failed++;
                    }
                } finally {
                    sem.Release();
                }
            }, outerCt));
        }
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { /* batch cancel — partial Results survive */ }
        job.CompletedAt = DateTime.UtcNow;
    }

    // --- Arg building -----------------------------------------------

    /// <summary>Public so tests can pin the CLI string per operation.</summary>
    public string BuildArgs(string inputPath, string outputPath, GraXpertOptions opts) {
        // The -cli flag MUST come before subcommand flags; without
        // it GraXpert launches the GUI.
        var sb = new System.Text.StringBuilder();
        sb.Append($"\"{inputPath}\" -cli -cmd ");
        switch (opts.Operation) {
            case GraXpertOperation.BackgroundExtraction:
                sb.Append("background-extraction");
                sb.Append($" -output \"{StripExt(outputPath)}\"");
                sb.Append($" -correction {opts.Correction}");
                sb.Append(FormattableString.Invariant($" -smoothing {opts.Smoothing:0.##}"));
                if (opts.SaveBackground) sb.Append(" -bg");
                break;
            case GraXpertOperation.Deconvolution:
                sb.Append("deconvolution");
                sb.Append($" -output \"{StripExt(outputPath)}\"");
                sb.Append(FormattableString.Invariant($" -strength {opts.DeconStrength:0.##}"));
                sb.Append(FormattableString.Invariant($" -psfsize {opts.DeconPsfSize:0.##}"));
                break;
            case GraXpertOperation.Denoising:
                sb.Append("denoising");
                sb.Append($" -output \"{StripExt(outputPath)}\"");
                sb.Append(FormattableString.Invariant($" -strength {opts.DenoiseStrength:0.##}"));
                break;
        }
        if (!string.IsNullOrEmpty(opts.AiVersion))
            sb.Append($" -ai_version {opts.AiVersion}");
        return sb.ToString();
    }

    /// <summary>GraXpert appends its own extension; we strip ours so the resulting filename is what we want.</summary>
    private static string StripExt(string p) {
        var ext = Path.GetExtension(p);
        return string.IsNullOrEmpty(ext) ? p : p[..^ext.Length];
    }

    public static string OutputSuffix(GraXpertOperation op) => op switch {
        GraXpertOperation.BackgroundExtraction => "_bge",
        GraXpertOperation.Deconvolution        => "_decon",
        GraXpertOperation.Denoising            => "_denoise",
        _                                      => "_gx"
    };

    /// <summary>
    /// Default output path: same dir as input + suffix. The endpoints
    /// override this when the batch wants a dedicated dir (e.g.
    /// {rig}/bge/{target}/).
    /// </summary>
    public static string DefaultOutputPath(string inputPath, GraXpertOperation op) {
        var dir = Path.GetDirectoryName(inputPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var ext = Path.GetExtension(inputPath);
        // FITS is GraXpert's canonical output even when input was XISF/TIFF.
        if (string.IsNullOrEmpty(ext) || !IsImageExt(ext)) ext = ".fits";
        return Path.Combine(dir, stem + OutputSuffix(op) + ext);
    }

    private static bool IsImageExt(string ext) =>
        ext.Equals(".fits", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".fit",  StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".fts",  StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".xisf", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".tif",  StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".png",  StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);

    // --- Binary lookup ----------------------------------------------

    private string? Locate() =>
        BinaryLocator.Find(_profile.Active.GraXpertPath,
            WindowsCandidates(), LinuxCandidates(), MacCandidates(), "graxpert");

    private static string[] WindowsCandidates() {
        var p64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var p86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return [
            // Standard installer location
            Path.Combine(p64, "GraXpert", "GraXpert.exe"),
            Path.Combine(p64, "GraXpert", "GraXpert-win64.exe"),
            Path.Combine(p86, "GraXpert", "GraXpert.exe"),
            // Some users portable-extract under LocalAppData
            Path.Combine(localApp, "Programs", "GraXpert", "GraXpert.exe"),
            Path.Combine(localApp, "GraXpert", "GraXpert.exe")
        ];
    }

    private static string[] LinuxCandidates() => [
        "/usr/bin/graxpert",
        "/usr/local/bin/graxpert",
        "/opt/graxpert/graxpert",
        "/opt/GraXpert/GraXpert",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "GraXpert", "GraXpert"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "bin", "graxpert")
    ];

    private static string[] MacCandidates() => [
        "/Applications/GraXpert.app/Contents/MacOS/GraXpert",
        "/opt/homebrew/bin/graxpert",
        "/usr/local/bin/graxpert"
    ];

    private string? ProbeVersion() {
        var bin = BinaryPath;
        if (string.IsNullOrEmpty(bin)) return null;
        try {
            using var proc = Process.Start(new ProcessStartInfo {
                FileName = bin,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (proc == null) return null;
            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(5000)) {
                try { proc.Kill(true); } catch { }
                return null;
            }
            // GraXpert prints "GraXpert 3.0.2" or "v3.0.2" depending
            // on the build; match anything that looks like x.y(.z).
            var m = Regex.Match(stdout, @"(\d+\.\d+(?:\.\d+)?)");
            return m.Success ? m.Groups[1].Value : null;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "GraXpert version probe failed");
            return null;
        }
    }

    private bool IsVersionAtLeast(int major, int minor) {
        var v = Version;
        if (string.IsNullOrEmpty(v)) return false;
        var parts = v.Split('.');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mj))
            return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mn))
            return false;
        return mj > major || (mj == major && mn >= minor);
    }
}

// --- DTOs / records -------------------------------------------------

public enum GraXpertOperation {
    BackgroundExtraction,
    Deconvolution,
    Denoising
}

public sealed record GraXpertOptions(
    GraXpertOperation Operation = GraXpertOperation.BackgroundExtraction,
    string Correction = "Subtraction",
    double Smoothing = 1.0,
    bool SaveBackground = false,
    double DeconStrength = 0.5,
    double DeconPsfSize = 4.0,
    double DenoiseStrength = 0.5,
    string? AiVersion = null);

public sealed record GraXpertResult(string OutputPath, string? BackgroundPath,
                                     GraXpertOperation Operation,
                                     double ElapsedSeconds, string? Error);

public sealed record GraXpertBatchRequest(List<string> InputPaths,
                                           GraXpertOptions Options,
                                           int Concurrency = 1);

public class GraXpertBatchJob {
    public string JobId { get; set; } = "";
    public GraXpertOperation Operation { get; set; }
    public int Total { get; set; }
    public int Done { get; set; }
    public int Failed { get; set; }
    public List<string> CurrentlyProcessing { get; set; } = new();
    public List<GraXpertResult> Results { get; set; } = new();
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool CancelRequested { get; set; }
}
