using System.Collections.Concurrent;
using NINA.Image.FileFormat.FITS;
using NINA.Image.Interfaces;
using NINA.Polaris.Services.PlateSolving;

namespace NINA.Polaris.Services;

/// <summary>
/// "Slew &amp; Center" orchestrator. Given a target (RA, Dec), commands
/// the mount to slew, then iteratively plate-solves test exposures
/// and nudges the mount until the actual centre is within the
/// requested tolerance (default 30 arcsec).
///
/// Long-running by nature — slews take seconds, each plate solve
/// takes 3-30s depending on solver. Exposed through a job pattern:
/// <see cref="StartJob"/> returns immediately with a job id; the
/// job's state lives in <c>_jobs</c> and is broadcast to the UI by
/// <c>StatusStreamHandler</c>. <c>AbortJob(id)</c> cancels via the
/// job's CTS.
///
/// Consumed by the SKY tab "Go to" button, the meridian-flip post-flip
/// recentre step, and the LiveStack auto-recenter trigger.
/// </summary>
public class SlewCenterService {
    private readonly EquipmentManager _equip;
    private readonly PlateSolveService _solver;
    private readonly ProfileService _profiles;
    private readonly ILogger<SlewCenterService> _logger;

    private readonly ConcurrentDictionary<string, SlewCenterJob> _jobs = new();

    public SlewCenterService(EquipmentManager equip, PlateSolveService solver,
        ProfileService profiles, ILogger<SlewCenterService> logger) {
        _equip = equip;
        _solver = solver;
        _profiles = profiles;
        _logger = logger;
    }

    public SlewCenterJob StartJob(double ra, double dec, double toleranceArcsec = 30) {
        var job = new SlewCenterJob {
            Id = Guid.NewGuid().ToString("N"),
            TargetRa = ra,
            TargetDec = dec,
            ToleranceArcsec = toleranceArcsec,
            State = SlewCenterState.Pending,
            CreatedAt = DateTime.UtcNow
        };

        _jobs[job.Id] = job;

        job.Cts = new CancellationTokenSource();
        job.Task = Task.Run(() => RunJobAsync(job, job.Cts.Token));

        return job;
    }

    public SlewCenterJob? GetJob(string jobId) {
        return _jobs.TryGetValue(jobId, out var job) ? job : null;
    }

    public void CancelJob(string jobId) {
        if (_jobs.TryGetValue(jobId, out var job)) {
            job.Cts?.Cancel();
            job.State = SlewCenterState.Cancelled;
            // Also yank the mount itself. Just cancelling the CTS
            // unwinds the C# pipeline but leaves a SlewAsync that's
            // already in flight on the wire running to completion —
            // the user clicking Cancel almost always means STOP THE
            // SCOPE NOW, not "finish what you started, then stop
            // bothering with the plate solve". Best-effort: log and
            // swallow if the abort itself fails, the CTS path still
            // brings the orchestrator to rest.
            try { _equip.Telescope?.AbortSlewAsync(); }
            catch (Exception ex) {
                _logger.LogWarning(ex, "AbortSlew during CancelJob failed");
            }
        }
    }

    private async Task RunJobAsync(SlewCenterJob job, CancellationToken ct) {
        const int maxIterations = 5;
        const double solveExposure = 5.0;

        try {
            if (_equip.Telescope == null) {
                job.Error = "No telescope connected";
                job.State = SlewCenterState.Failed;
                return;
            }

            if (_equip.Camera == null) {
                job.Error = "No camera connected for plate solving";
                job.State = SlewCenterState.Failed;
                return;
            }

            // Don't bail upfront if no plate solver is available — the
            // user explicitly asked to slew, and they value the mount
            // physically moving to the target far more than they value
            // the centering pass. So perform a single Slew step first,
            // then fail with the same diagnostic IF (and only if) we
            // were about to attempt a solve.
            //
            // Surface the same multi-solver diagnostic in the failure
            // path so the user still gets actionable install / API-key
            // guidance — just AFTER the mount has moved.
            string solverUnavailableError = null;
            if (!_solver.IsAvailable) {
                var lines = _solver.AllSolvers.Select(s =>
                    "  • " + s.DisplayName + " — "
                    + (s.IsAvailable ? "ready"
                       : s is AstrometryNetOnlineSolver
                           ? "needs PlateSolve:AstrometryApiKey in appsettings"
                           : "binary not found"));
                solverUnavailableError =
                    "Slew completed. Centering skipped — no plate solver available:\n"
                    + string.Join("\n", lines)
                    + "\nTip: install a solver or use Slew Only to skip this message.";
            }

            if (solverUnavailableError != null) {
                // Slew once, then short-circuit to Failed with the
                // diagnostic — bypasses the iteration loop entirely
                // because every iteration relies on a working solver.
                job.State = SlewCenterState.Slewing;
                _logger.LogInformation("Slew-only fallback: slewing to RA={Ra:F4} Dec={Dec:F4} (no plate solver)",
                    job.TargetRa, job.TargetDec);
                try {
                    await _equip.Telescope.SlewAsync(job.TargetRa, job.TargetDec, ct);
                    await WaitForSlewComplete(ct);
                } catch (Exception slewEx) {
                    job.Error = "Slew failed: " + slewEx.Message;
                    job.State = SlewCenterState.Failed;
                    return;
                }
                job.Error = solverUnavailableError;
                job.State = SlewCenterState.Failed;
                return;
            }

            for (int i = 0; i < maxIterations; i++) {
                ct.ThrowIfCancellationRequested();
                job.Iteration = i + 1;

                // Step 1: Slew
                job.State = SlewCenterState.Slewing;
                _logger.LogInformation("Slew-and-center iteration {I}: slewing to RA={Ra:F4} Dec={Dec:F4}",
                    i + 1, job.TargetRa, job.TargetDec);

                await _equip.Telescope.SlewAsync(job.TargetRa, job.TargetDec, ct);
                await WaitForSlewComplete(ct);

                ct.ThrowIfCancellationRequested();

                // Step 2: Capture short exposure for plate solving
                job.State = SlewCenterState.Capturing;
                _logger.LogInformation("Capturing {Exp}s solve frame", solveExposure);

                var imageData = await _equip.Camera.CaptureAsync(solveExposure, ct);

                var tempFits = Path.Combine(Path.GetTempPath(),
                    $"nina_solve_{job.Id}_{i}.fits");

                FITSWriter.Write(imageData, tempFits);

                ct.ThrowIfCancellationRequested();

                // Step 3: Plate solve
                job.State = SlewCenterState.Solving;
                _logger.LogInformation("Plate solving...");

                var solveResult = await _solver.SolveAsync(tempFits, new PlateSolveOptions {
                    HintRa = job.TargetRa,
                    HintDec = job.TargetDec,
                    SearchRadiusDeg = 10
                }, ct);

                try { File.Delete(tempFits); } catch { }

                if (!solveResult.Success) {
                    _logger.LogWarning("Plate solve failed on iteration {I}: {Error}",
                        i + 1, solveResult.Error);
                    job.Error = "Solve failed: " + solveResult.Error;

                    if (i == maxIterations - 1) {
                        job.State = SlewCenterState.Failed;
                        return;
                    }
                    continue;
                }

                // Step 4: Calculate error
                var errorArcsec = AngularSeparationArcsec(
                    job.TargetRa, job.TargetDec,
                    solveResult.RaHours, solveResult.DecDeg);

                job.ActualRa = solveResult.RaHours;
                job.ActualDec = solveResult.DecDeg;
                job.ErrorArcsec = errorArcsec;
                job.Rotation = solveResult.RotationDeg;
                job.Scale = solveResult.ScaleArcsecPerPixel;

                _logger.LogInformation(
                    "Solve result: RA={Ra:F4}h Dec={Dec:F4}°, error={Err:F1}\" (tolerance={Tol:F0}\")",
                    solveResult.RaHours, solveResult.DecDeg, errorArcsec, job.ToleranceArcsec);

                // Auto-update the active rig's focal length from the solve.
                // Only runs once per job (on the first successful solve we have
                // a reliable scale) and skipped if the camera doesn't report a
                // pixel size or the derived value is wildly different (>50%
                // off — likely a misidentification of the field).
                if (job.DerivedFocalLengthMm == null) {
                    TryUpdateFocalLengthFromSolve(solveResult.ScaleArcsecPerPixel, job);
                }

                // Step 5: Check convergence
                if (errorArcsec <= job.ToleranceArcsec) {
                    job.State = SlewCenterState.Centered;
                    _logger.LogInformation("Centered! Error {Err:F1}\" within tolerance {Tol:F0}\"",
                        errorArcsec, job.ToleranceArcsec);
                    return;
                }

                // Step 6: Sync mount and prepare for next iteration
                job.State = SlewCenterState.Syncing;
                _logger.LogInformation("Syncing mount at RA={Ra:F4} Dec={Dec:F4}",
                    solveResult.RaHours, solveResult.DecDeg);

                await _equip.Telescope.SyncAsync(solveResult.RaHours, solveResult.DecDeg, ct);
            }

            job.State = SlewCenterState.Failed;
            job.Error = $"Did not converge after {maxIterations} iterations (last error: {job.ErrorArcsec:F1}\")";

        } catch (OperationCanceledException) {
            job.State = SlewCenterState.Cancelled;
            _logger.LogInformation("Slew-and-center cancelled");
        } catch (Exception ex) {
            job.State = SlewCenterState.Failed;
            job.Error = ex.Message;
            _logger.LogError(ex, "Slew-and-center failed");
        }
    }

    private async Task WaitForSlewComplete(CancellationToken ct) {
        if (_equip.Telescope == null) return;
        for (int i = 0; i < 300; i++) {
            ct.ThrowIfCancellationRequested();
            if (!_equip.Telescope.IsSlewing) return;
            await Task.Delay(1000, ct);
        }
        _logger.LogWarning("Slew did not complete within 5 minutes");
    }

    /// <summary>
    /// Compute focal length from the plate-solve scale + camera pixel size and
    /// push it into the active rig. Skipped silently when:
    /// - no camera connected / camera doesn't report pixel size
    /// - scale is non-positive
    /// - derived value is &gt;50% off from the current rig value (likely a
    ///   misidentification — don't clobber the user's setting on bad data)
    ///
    /// Formula (standard plate-scale relation):
    ///   scale (arcsec/px) = pixel_size (um) / focal_length (mm) × 206.265
    ///   →  focal_length (mm) = pixel_size (um) × 206.265 / scale (arcsec/px)
    /// </summary>
    private void TryUpdateFocalLengthFromSolve(double scaleArcsecPerPx, SlewCenterJob job) {
        if (scaleArcsecPerPx <= 0) return;
        if (_equip.Camera == null) return;

        var pixelSizeUm = _equip.Camera.PixelSizeX;
        if (pixelSizeUm <= 0 || double.IsNaN(pixelSizeUm)) {
            _logger.LogDebug("Camera does not report PixelSizeX — skipping focal-length auto-update");
            return;
        }

        var derived = pixelSizeUm * 206.265 / scaleArcsecPerPx;
        job.DerivedFocalLengthMm = derived;

        var rig = _profiles.ActiveEquipmentProfile;
        var previous = rig.FocalLengthMm;

        // Sanity check: refuse if more than 50% different
        if (previous > 0) {
            var ratio = derived / previous;
            if (ratio < 0.5 || ratio > 1.5) {
                _logger.LogWarning(
                    "Plate solve suggests focal length {New:F0}mm but rig has {Old:F0}mm " +
                    "(ratio {Ratio:F2}) — refusing to auto-update; please verify manually",
                    derived, previous, ratio);
                return;
            }
        }

        if (Math.Abs(derived - previous) < 1.0) {
            _logger.LogDebug("Focal length already accurate ({FL:F0}mm) — no update", derived);
            return;
        }

        _profiles.UpdateEquipmentProfile(rig.Id, r => r.FocalLengthMm = derived);
        _logger.LogInformation(
            "Auto-updated active rig '{Rig}' focal length: {Old:F0}mm → {New:F0}mm " +
            "(from solve: {Px:F2}um/px × 206.265 / {Scale:F2}\"/px)",
            rig.Name, previous, derived, pixelSizeUm, scaleArcsecPerPx);
    }


    private static double AngularSeparationArcsec(double ra1Hours, double dec1Deg,
        double ra2Hours, double dec2Deg) {
        var ra1Rad = ra1Hours * 15.0 * Math.PI / 180.0;
        var dec1Rad = dec1Deg * Math.PI / 180.0;
        var ra2Rad = ra2Hours * 15.0 * Math.PI / 180.0;
        var dec2Rad = dec2Deg * Math.PI / 180.0;

        var cosSep = Math.Sin(dec1Rad) * Math.Sin(dec2Rad) +
                     Math.Cos(dec1Rad) * Math.Cos(dec2Rad) * Math.Cos(ra1Rad - ra2Rad);

        cosSep = Math.Clamp(cosSep, -1.0, 1.0);
        var sepRad = Math.Acos(cosSep);

        return sepRad * 180.0 / Math.PI * 3600.0;
    }
}

public class SlewCenterJob {
    public string Id { get; set; } = "";
    public double TargetRa { get; set; }
    public double TargetDec { get; set; }
    public double ToleranceArcsec { get; set; }
    public SlewCenterState State { get; set; }
    public int Iteration { get; set; }
    public double? ActualRa { get; set; }
    public double? ActualDec { get; set; }
    public double? ErrorArcsec { get; set; }
    public double? Rotation { get; set; }
    public double? Scale { get; set; }
    /// <summary>Focal length (mm) derived from the first successful solve in this job, if any.</summary>
    public double? DerivedFocalLengthMm { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }

    internal CancellationTokenSource? Cts { get; set; }
    internal Task? Task { get; set; }
}

public enum SlewCenterState {
    Pending,
    Slewing,
    Capturing,
    Solving,
    Syncing,
    Centered,
    Failed,
    Cancelled
}
