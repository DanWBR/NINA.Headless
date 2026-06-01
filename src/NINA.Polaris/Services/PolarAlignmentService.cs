using System.Collections.Concurrent;
using NINA.Image.FileFormat.FITS;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>
/// TPPA (Three-Point Polar Alignment) orchestrator. Multi-phase state
/// machine that mirrors PHD2CalibrationOrchestrator in shape:
///   - StartJob spins a Task.Run(RunAsync) with a CancellationTokenSource
///   - Job state is broadcast via JobUpdated → StatusStreamHandler folds
///     into /ws/status under polarAlignment
///   - Abort cancels the CTS, RunAsync's finally lands at Phase.Cancelled
///
/// PA-1 lays the skeleton (enum + records + stubs). PA-2 fills in the
/// capture/slew/solve loop. PA-3 plugs in the polar-axis math. PA-5
/// adds the continuous Refinement mode (sliding-window solve loop
/// while the user adjusts knobs).
///
/// Refinement uses a separate CTS so the user can Stop refinement
/// without affecting any in-progress TPPA job (in practice TPPA must
/// complete before Refine becomes available, but the lifecycle plumbing
/// is independent in case we want to allow re-running TPPA from a
/// refinement state later).
/// </summary>
public class PolarAlignmentService {
    private readonly EquipmentManager _equip;
    private readonly PlateSolveService _plateSolve;
    private readonly ProfileService _profiles;
    private readonly NotificationService _notify;
    private readonly ILogger<PolarAlignmentService> _logger;

    private readonly ConcurrentDictionary<string, PolarAlignmentJob> _jobs = new();

    /// <summary>Most recent job, Idle when nothing has run yet. The WS
    /// broadcaster reads this. Set to a fresh job by StartJob; mutated
    /// in-place by RunAsync; preserved post-completion so the UI can
    /// keep showing the last computed error vector.</summary>
    public PolarAlignmentJob? CurrentJob { get; private set; }

    /// <summary>Fires on every phase transition + every new solved
    /// point. StatusStreamHandler subscribes so it can push an
    /// immediate WS frame instead of waiting for the next 1Hz tick.</summary>
    public event Action<PolarAlignmentJob>? JobUpdated;

    public PolarAlignmentService(EquipmentManager equip,
                                 PlateSolveService plateSolve,
                                 ProfileService profiles,
                                 NotificationService notify,
                                 ILogger<PolarAlignmentService> logger) {
        _equip = equip;
        _plateSolve = plateSolve;
        _profiles = profiles;
        _notify = notify;
        _logger = logger;
    }

    public PolarAlignmentJob StartJob(PolarAlignmentOptions opts) {
        // Refuse to start a second TPPA on top of a running one, the
        // mount can't be in two places at once. Refinement is gated
        // separately (see StartRefinement).
        if (CurrentJob != null && CurrentJob.IsActive) {
            throw new InvalidOperationException(
                "A polar-alignment job is already in progress. Abort it first.");
        }

        var job = new PolarAlignmentJob {
            Id = Guid.NewGuid().ToString("N"),
            Options = opts,
            Phase = PolarAlignmentPhase.Preflight,
            Mode = "tppa",
            StartedAt = DateTime.UtcNow
        };
        _jobs[job.Id] = job;
        CurrentJob = job;
        job.Cts = new CancellationTokenSource();
        job.Task = Task.Run(() => RunAsync(job, job.Cts.Token));
        return job;
    }

    public PolarAlignmentJob? GetJob(string id) =>
        _jobs.TryGetValue(id, out var j) ? j : null;

    public void Abort(string id) {
        if (_jobs.TryGetValue(id, out var j)) {
            j.Cts?.Cancel();
        }
    }

    /// <summary>Cancel whatever job is currently active (TPPA or
    /// refinement). Convenience for the UI "Stop everything" button.</summary>
    public void AbortCurrent() {
        var j = CurrentJob;
        if (j != null && j.IsActive) {
            j.Cts?.Cancel();
        }
    }

    /// <summary>Kick off a continuous capture+solve refinement loop.
    /// Requires a completed TPPA job in CurrentJob (so we have a
    /// baseline of 3 solved points). Each iteration captures, plate-
    /// solves, evicts the oldest of the 3 baseline points, appends the
    /// fresh one, and recomputes (azErr, altErr). The error decreases
    /// as the user adjusts the mount knobs. Loop continues until
    /// StopRefinement() or external cancellation.</summary>
    public PolarAlignmentJob StartRefinement() {
        var baseline = CurrentJob;
        if (baseline == null || baseline.Phase != PolarAlignmentPhase.Ok || baseline.Points.Count < 3) {
            throw new InvalidOperationException(
                "Run TPPA first to establish a 3-point baseline before refining.");
        }
        if (_refineCts != null && !_refineCts.IsCancellationRequested) {
            throw new InvalidOperationException("Refinement already running.");
        }

        // Reuse the same Job object, Mode flips to "refine", Phase
        // becomes Refining, but the 3 baseline Points carry over so
        // we can substitute them as new samples arrive. UI gets the
        // continuity for free (same WS payload shape).
        baseline.Mode = "refine";
        baseline.Phase = PolarAlignmentPhase.Refining;
        baseline.CompletedAt = null;
        try { JobUpdated?.Invoke(baseline); } catch { }

        _refineCts = new CancellationTokenSource();
        _ = Task.Run(() => RunRefinementAsync(baseline, _refineCts.Token));
        return baseline;
    }

    public void StopRefinement() {
        _refineCts?.Cancel();
        _refineCts = null;
        // Land the job back at Ok so the UI's "completed" indicator
        // stays lit. The last-computed error vector is preserved.
        if (CurrentJob != null && CurrentJob.Phase == PolarAlignmentPhase.Refining) {
            CurrentJob.Mode = "tppa";
            CurrentJob.Phase = PolarAlignmentPhase.Ok;
            CurrentJob.CompletedAt = DateTime.UtcNow;
            try { JobUpdated?.Invoke(CurrentJob); } catch { }
        }
    }

    private CancellationTokenSource? _refineCts;

    private async Task RunRefinementAsync(PolarAlignmentJob job, CancellationToken ct) {
        var camera = _equip.Camera;
        var telescope = _equip.Telescope;
        if (camera == null || telescope == null) {
            _notify.Push("error", "Refine: camera or telescope disconnected.");
            return;
        }

        var userProfile = _profiles.Active;
        try {
            while (!ct.IsCancellationRequested) {
                // 1. Capture at the CURRENT mount position (no slew).
                IImageData image;
                try {
                    image = await camera.CaptureAsync(
                        job.Options.ExposureSeconds,
                        new CaptureOptions(Gain: job.Options.Gain, ImageType: "POLAR"),
                        ct);
                } catch (OperationCanceledException) { break; }
                catch (Exception ex) {
                    _logger.LogWarning(ex, "Refine: capture failed; retrying after settle");
                    try { await Task.Delay(job.Options.SettleSeconds * 1000, ct); } catch { break; }
                    continue;
                }
                if (image == null || image.Properties.Width <= 0) {
                    try { await Task.Delay(job.Options.SettleSeconds * 1000, ct); } catch { break; }
                    continue;
                }

                // 2. Plate solve.
                PlateSolveResult solve;
                try {
                    solve = await SolveOnceAsync(image, telescope, ct);
                } catch (OperationCanceledException) { break; }
                catch (Exception ex) {
                    _logger.LogDebug(ex, "Refine: solve threw, skipping iteration");
                    try { await Task.Delay(job.Options.SettleSeconds * 1000, ct); } catch { break; }
                    continue;
                }
                if (!solve.Success) {
                    _logger.LogDebug("Refine: solve failed ({Err}), skipping", solve.Error);
                    try { await Task.Delay(job.Options.SettleSeconds * 1000, ct); } catch { break; }
                    continue;
                }

                // 3. Sliding-window: evict oldest baseline point, append
                //    the fresh one. Re-index so they read 0/1/2 in
                //    order for the UI table.
                var newPoint = new PolarPoint(
                    Index: 2,
                    RaHours: solve.RaHours,
                    DecDeg: solve.DecDeg,
                    RotationDeg: solve.RotationDeg,
                    AtUtc: DateTime.UtcNow);
                if (job.Points.Count >= 3) {
                    job.Points.RemoveAt(0);
                }
                job.Points.Add(newPoint);
                for (int i = 0; i < job.Points.Count; i++) {
                    job.Points[i] = job.Points[i] with { Index = i };
                }

                // 4. Recompute error.
                if (job.Points.Count == 3) {
                    var (azErr, altErr) = PolarAlignmentMath.ComputeError(
                        job.Points[0], job.Points[1], job.Points[2],
                        userProfile.Latitude, userProfile.Longitude);
                    job.AzErrorArcsec = azErr;
                    job.AltErrorArcsec = altErr;
                    job.TotalErrorArcsec = PolarAlignmentMath.TotalErrorArcsec(azErr, altErr);
                }

                // 5. Push update + settle before next iteration.
                try { JobUpdated?.Invoke(job); } catch { }
                try { await Task.Delay(Math.Max(500, job.Options.SettleSeconds * 1000), ct); }
                catch { break; }
            }
        } catch (Exception ex) {
            _logger.LogError(ex, "Refine loop crashed");
            _notify.Push("error", "Refine loop crashed: " + ex.Message);
        }
        // Loop exit: land back at Ok so the Refine button is offered
        // again. (StopRefinement also handles this, this is the
        // fallback for organic loop exits.)
        if (job.Phase == PolarAlignmentPhase.Refining) {
            job.Phase = PolarAlignmentPhase.Ok;
            job.Mode = "tppa";
            job.CompletedAt = DateTime.UtcNow;
            try { JobUpdated?.Invoke(job); } catch { }
        }
    }

    private async Task RunAsync(PolarAlignmentJob job, CancellationToken ct) {
        // Track original mount position so we can slew back at the
        // end (cosmetic, TPPA already extracted the error vector by
        // then, but leaving the user 60° off where they expected is
        // surprising).
        double ra0 = 0, dec0 = 0;

        try {
            // 1. Preflight ----------------------------------------------------
            SetPhase(job, PolarAlignmentPhase.Preflight);
            _notify.Push("info", "Polar alignment starting…", 2500);

            var telescope = _equip.Telescope;
            var camera = _equip.Camera;
            if (telescope == null || !telescope.IsConnected) {
                Fail(job, "Telescope not connected.");
                return;
            }
            if (camera == null || !camera.IsConnected) {
                Fail(job, "Camera not connected.");
                return;
            }
            if (!telescope.IsTracking) {
                Fail(job, "Telescope must be tracking (sidereal) for TPPA. Enable tracking and retry.");
                return;
            }
            if (telescope.IsParked) {
                Fail(job, "Telescope is parked. Unpark before running polar alignment.");
                return;
            }

            ra0 = telescope.RightAscension;
            dec0 = telescope.Declination;

            // 2. Three solved points -----------------------------------------
            // Slew step measured in degrees; mount RA is in hours, so
            // convert via /15. Positive direction; meridian-aware
            // picker is TODO (see plan edge cases).
            double slewStepHours = job.Options.SlewStepDegrees / 15.0;

            for (int i = 0; i < 3; i++) {
                ct.ThrowIfCancellationRequested();

                double targetRa = NormalizeRaHours(ra0 + i * slewStepHours);

                // Slew. For i=0 this is usually a no-op (~0.01h drift
                // during the brief preflight) but we still call it to
                // make sure we know our exact pointing.
                SetPhase(job, MovingPhaseFor(i));
                _notify.Push("info", $"Polar align: slewing to point {i + 1}/3 (RA {targetRa:F3}h)", 2000);
                await telescope.SlewAsync(targetRa, dec0, ct);

                // Settle, mount stops shaking, INDI driver finishes
                // emitting EQUATORIAL_EOD_COORD updates.
                if (job.Options.SettleSeconds > 0) {
                    await Task.Delay(job.Options.SettleSeconds * 1000, ct);
                }

                // Capture + plate-solve.
                SetPhase(job, SolvingPhaseFor(i));
                var image = await camera.CaptureAsync(
                    job.Options.ExposureSeconds,
                    new CaptureOptions(Gain: job.Options.Gain, ImageType: "POLAR"),
                    ct);
                if (image == null || image.Properties.Width <= 0 || image.Properties.Height <= 0) {
                    Fail(job, $"Point {i + 1}: camera returned an empty frame.");
                    return;
                }

                var result = await SolveOnceAsync(image, telescope, ct);
                if (!result.Success) {
                    // One retry with doubled exposure, common rescue
                    // for marginal star count on the first attempt.
                    _logger.LogInformation(
                        "Polar align point {Index} first solve failed ({Err}); retrying with 2x exposure",
                        i + 1, result.Error);
                    var retryImage = await camera.CaptureAsync(
                        job.Options.ExposureSeconds * 2.0,
                        new CaptureOptions(Gain: job.Options.Gain, ImageType: "POLAR"),
                        ct);
                    if (retryImage != null && retryImage.Properties.Width > 0) {
                        result = await SolveOnceAsync(retryImage, telescope, ct);
                    }
                }
                if (!result.Success) {
                    Fail(job, $"Plate solve failed at point {i + 1}: {result.Error}. " +
                              $"Try increasing exposure or gain in rig settings.");
                    return;
                }

                job.Points.Add(new PolarPoint(
                    Index: i,
                    RaHours: result.RaHours,
                    DecDeg: result.DecDeg,
                    RotationDeg: result.RotationDeg,
                    AtUtc: DateTime.UtcNow));

                // Force a WS push so the UI's "Point N of 3 solved"
                // ticker updates immediately instead of waiting for the
                // next 1Hz tick.
                try { JobUpdated?.Invoke(job); } catch { }
            }

            // 3. Compute polar error ----------------------------------------
            SetPhase(job, PolarAlignmentPhase.Computing);
            var userProfile = _profiles.Active;
            if (userProfile.Latitude == 0 && userProfile.Longitude == 0) {
                Fail(job, "Site latitude/longitude not set. Configure your location in Settings before running polar alignment.");
                return;
            }
            var (azErr, altErr) = PolarAlignmentMath.ComputeError(
                job.Points[0], job.Points[1], job.Points[2],
                userProfile.Latitude, userProfile.Longitude);
            job.AzErrorArcsec = azErr;
            job.AltErrorArcsec = altErr;
            job.TotalErrorArcsec = PolarAlignmentMath.TotalErrorArcsec(azErr, altErr);

            // 4. Cosmetic slew home -----------------------------------------
            SetPhase(job, PolarAlignmentPhase.SlewingHome);
            try {
                await telescope.SlewAsync(ra0, dec0, ct);
            } catch (Exception ex) {
                // Don't fail the whole alignment if the home slew
                // hiccups, the user already has their error vector.
                _logger.LogWarning(ex, "Polar align: slew back to start failed");
            }

            // 5. Done -------------------------------------------------------
            job.CompletedAt = DateTime.UtcNow;
            SetPhase(job, PolarAlignmentPhase.Ok);
            _notify.Push("ok",
                "Polar alignment complete, see POLAR tab for error vector.", 5000);
        } catch (OperationCanceledException) {
            SetPhase(job, PolarAlignmentPhase.Cancelled);
            job.CompletedAt = DateTime.UtcNow;
            _notify.Push("warn", "Polar alignment cancelled.");
            // Best-effort slew home so the mount isn't stranded.
            try {
                if (_equip.Telescope != null && ra0 > 0)
                    await _equip.Telescope.SlewAsync(ra0, dec0, CancellationToken.None);
            } catch { /* shutdown, eat it */ }
        } catch (Exception ex) {
            _logger.LogError(ex, "Polar alignment RunAsync crashed");
            Fail(job, ex.Message);
        }
    }

    /// <summary>Write a temp FITS, call the plate solver, delete the
    /// FITS regardless of outcome. Caller decides what to do with
    /// failures (retry / fail the job).</summary>
    private async Task<PlateSolveResult> SolveOnceAsync(
        IImageData image, ITelescope telescope, CancellationToken ct) {
        var path = WriteTempFits(image);
        try {
            var opts = new PlateSolveOptions {
                // RA hint in hours, Dec hint in degrees. Helps every
                // solver narrow the search, especially ASTAP.
                HintRa = telescope.RightAscension,
                HintDec = telescope.Declination,
                SearchRadiusDeg = 30,
                ScaleArcsecPerPixel = ComputePixelScaleHint(),
                FovDeg = 0  // let the solver derive from pixel scale + image size
            };
            return await _plateSolve.SolveAsync(path, opts, ct);
        } finally {
            try { File.Delete(path); } catch { /* housekeeping */ }
        }
    }

    private static PolarAlignmentPhase MovingPhaseFor(int index) => index switch {
        0 => PolarAlignmentPhase.MovingToPoint1,
        1 => PolarAlignmentPhase.MovingToPoint2,
        _ => PolarAlignmentPhase.MovingToPoint3,
    };

    private static PolarAlignmentPhase SolvingPhaseFor(int index) => index switch {
        0 => PolarAlignmentPhase.SolvingPoint1,
        1 => PolarAlignmentPhase.SolvingPoint2,
        _ => PolarAlignmentPhase.SolvingPoint3,
    };

    /// <summary>Wrap an RA value back into [0, 24) hours. Adding
    /// slewStepHours can push past 24h near RA=23h.</summary>
    private static double NormalizeRaHours(double ra) {
        var r = ra % 24.0;
        return r < 0 ? r + 24.0 : r;
    }

    /// <summary>Best-effort pixel-scale hint for the plate solver,
    /// computed from camera pixel size + rig main focal length. The
    /// solver derives the real scale from the FITS header too, but
    /// the hint narrows search radius (especially on ASTAP) and is
    /// REQUIRED by PlateSolve3. Returns 0 when either input is
    /// missing, the solver chain handles the unknown-scale case.</summary>
    private double ComputePixelScaleHint() {
        var cam = _equip.Camera;
        if (cam == null) return 0;
        var rig = _profiles.ActiveEquipmentProfile;
        if (rig.FocalLengthMm <= 0) return 0;
        // PixelSizeX is in microns. arcsec/pixel = pixelSize_um * 206.265 / focalLength_mm.
        var px = cam.PixelSizeX;
        if (double.IsNaN(px) || px <= 0) return 0;
        return px * 206.265 / rig.FocalLengthMm;
    }

    private void SetPhase(PolarAlignmentJob job, PolarAlignmentPhase phase) {
        job.Phase = phase;
        try { JobUpdated?.Invoke(job); }
        catch (Exception ex) { _logger.LogDebug(ex, "JobUpdated handler threw"); }
    }

    private void Fail(PolarAlignmentJob job, string error) {
        job.LastError = error;
        job.Phase = PolarAlignmentPhase.Failed;
        job.CompletedAt = DateTime.UtcNow;
        _logger.LogWarning("Polar alignment failed: {Error}", error);
        try { JobUpdated?.Invoke(job); } catch { }
        _notify.Push("error", "Polar alignment failed: " + error);
    }

    /// <summary>Write an IImageData to a freshly-created temp FITS so
    /// the plate solver (which takes a file path, not a buffer) can
    /// consume it. Caller is responsible for deleting the file.
    /// Lives here rather than in ImageWriterService because that
    /// service writes to the configured ImageOutputDir using session
    /// metadata; for polar alignment we want a throwaway temp file.</summary>
    internal static string WriteTempFits(IImageData image) {
        var path = Path.Combine(Path.GetTempPath(),
            "polaris-polar-" + Guid.NewGuid().ToString("N") + ".fits");
        FITSWriter.Write(image, path);
        return path;
    }

    // ─── RDPA-2: Rudimentary single-target polar alignment ──────────
    //
    // Different workflow from TPPA: the user picks ONE bright visible
    // target, Polaris slews + captures + solves once, then reports the
    // pointing error attributed to polar misalignment. The user walks
    // to the mount, nudges azimuth/altitude knobs, and clicks
    // "re-solve" — repeat until happy. No 3-point sweep, no auto
    // convergence threshold (the user decides).
    //
    // Reuses the same PolarAlignmentJob + WS broadcast plumbing so the
    // UI handler can render either mode without branching on Mode at
    // the transport layer. New phases are added below to disambiguate
    // the iteration progress without polluting the TPPA enum values.

    /// <summary>
    /// Start a rudimentary alignment session. Resets CurrentJob to a
    /// fresh job in Rudimentary mode, optionally slews to the target,
    /// captures one frame, plate-solves, and computes the error vector.
    /// The job stays alive after this returns so subsequent
    /// RudimentaryReSolveAsync calls can append to the iteration history.
    /// </summary>
    public async Task<RudimentaryStepResult> StartRudimentaryAsync(
        RudimentaryStartRequest req, CancellationToken ct) {

        if (CurrentJob != null && CurrentJob.IsActive) {
            throw new InvalidOperationException(
                "A polar-alignment job is already in progress. Abort it first.");
        }

        var job = new PolarAlignmentJob {
            Id = Guid.NewGuid().ToString("N"),
            Options = new PolarAlignmentOptions(
                ExposureSeconds: req.ExposureSeconds,
                Gain: req.Gain,
                SettleSeconds: req.SettleSeconds),
            Mode = "rudimentary",
            Phase = PolarAlignmentPhase.Preflight,
            StartedAt = DateTime.UtcNow,
            TargetRaHours = req.TargetRaHours,
            TargetDecDeg = req.TargetDecDeg,
            TargetName = req.TargetName,
        };
        _jobs[job.Id] = job;
        CurrentJob = job;
        job.Cts = new CancellationTokenSource();

        try { JobUpdated?.Invoke(job); } catch { }
        return await DoRudimentaryStepAsync(job, slew: req.SlewToTarget, ct);
    }

    /// <summary>
    /// Run another capture+solve at the current mount position (no
    /// slew). Requires a prior StartRudimentaryAsync that established
    /// the target. Each call appends to the iteration history so the
    /// UI sparkline can show convergence.
    /// </summary>
    public async Task<RudimentaryStepResult> RudimentaryReSolveAsync(CancellationToken ct) {
        var job = CurrentJob;
        if (job == null || job.Mode != "rudimentary" || job.TargetRaHours == null) {
            throw new InvalidOperationException(
                "No active rudimentary alignment session. Call /start first.");
        }
        // Don't bring up a brand-new CTS; reuse the start one so the
        // /abort endpoint cancels in-flight resolves too.
        if (job.Cts == null || job.Cts.IsCancellationRequested) {
            job.Cts = new CancellationTokenSource();
        }
        return await DoRudimentaryStepAsync(job, slew: false, ct);
    }

    private async Task<RudimentaryStepResult> DoRudimentaryStepAsync(
        PolarAlignmentJob job, bool slew, CancellationToken externalCt) {

        // Combine the caller's ct with the job's CTS so /abort works.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            externalCt, job.Cts!.Token);
        var ct = linked.Token;

        try {
            // Preflight: camera + (optional) telescope + site lat/lon.
            SetPhase(job, PolarAlignmentPhase.Preflight);
            var camera = _equip.Camera;
            var telescope = _equip.Telescope;
            var profile = _profiles.Active;

            if (camera == null || !camera.IsConnected) {
                return FailRudimentary(job, "Camera not connected.");
            }
            if (profile.Latitude == 0 && profile.Longitude == 0) {
                return FailRudimentary(job,
                    "Site latitude/longitude not set. Configure your location in Settings.");
            }
            if (slew) {
                if (telescope == null || !telescope.IsConnected) {
                    return FailRudimentary(job,
                        "Telescope not connected. Disable 'Slew to target' if your mount is manual.");
                }
                if (telescope.IsParked) {
                    return FailRudimentary(job,
                        "Telescope is parked. Unpark before running alignment.");
                }
            }

            // Optionally slew.
            if (slew && telescope != null && job.TargetRaHours.HasValue && job.TargetDecDeg.HasValue) {
                SetPhase(job, PolarAlignmentPhase.RudimentarySlewing);
                _notify.Push("info",
                    $"Rudimentary align: slewing to {(job.TargetName ?? "target")}", 2500);
                await telescope.SlewAsync(job.TargetRaHours.Value, job.TargetDecDeg.Value, ct);
                if (job.Options.SettleSeconds > 0) {
                    await Task.Delay(job.Options.SettleSeconds * 1000, ct);
                }
            }

            // Capture.
            SetPhase(job, PolarAlignmentPhase.RudimentaryCapturing);
            var image = await camera.CaptureAsync(
                job.Options.ExposureSeconds,
                new CaptureOptions(Gain: job.Options.Gain, ImageType: "POLAR"),
                ct);
            if (image == null || image.Properties.Width <= 0 || image.Properties.Height <= 0) {
                return FailRudimentary(job, "Camera returned an empty frame.");
            }

            // Plate solve, with one retry on doubled exposure (same
            // rescue TPPA uses for marginal star count on first try).
            SetPhase(job, PolarAlignmentPhase.RudimentarySolving);
            var solve = await SolveOnceAsync(image, telescope!, ct);
            if (!solve.Success) {
                _logger.LogInformation(
                    "Rudimentary: first solve failed ({Err}), retrying with 2x exposure",
                    solve.Error);
                var retry = await camera.CaptureAsync(
                    job.Options.ExposureSeconds * 2.0,
                    new CaptureOptions(Gain: job.Options.Gain, ImageType: "POLAR"),
                    ct);
                if (retry != null && retry.Properties.Width > 0) {
                    solve = await SolveOnceAsync(retry, telescope!, ct);
                }
            }
            if (!solve.Success) {
                return FailRudimentary(job,
                    $"Plate solve failed: {solve.Error}. Increase exposure or gain in rig settings.");
            }

            // Compute the single-target polar error.
            var (azErr, altErr) = PolarAlignmentMath.ComputeErrorSingleTarget(
                targetRaHours: job.TargetRaHours!.Value,
                targetDecDeg: job.TargetDecDeg!.Value,
                solvedRaHours: solve.RaHours,
                solvedDecDeg: solve.DecDeg,
                siteLatDeg: profile.Latitude,
                siteLongDeg: profile.Longitude,
                utcNow: DateTime.UtcNow);
            var total = PolarAlignmentMath.TotalErrorArcsec(azErr, altErr);

            job.SolvedRaHours = solve.RaHours;
            job.SolvedDecDeg = solve.DecDeg;
            job.AzErrorArcsec = azErr;
            job.AltErrorArcsec = altErr;
            job.TotalErrorArcsec = total;
            job.History.Add(new RudimentaryIteration(total, DateTime.UtcNow));
            // Cap history at 20 entries so a long session doesn't leak
            // memory in the WS payload.
            while (job.History.Count > 20) job.History.RemoveAt(0);

            SetPhase(job, PolarAlignmentPhase.Ok);
            return new RudimentaryStepResult(
                Ok: true, Error: null,
                SolvedRaHours: solve.RaHours, SolvedDecDeg: solve.DecDeg,
                AzErrorArcsec: azErr, AltErrorArcsec: altErr,
                TotalErrorArcsec: total,
                IterationCount: job.History.Count);
        } catch (OperationCanceledException) {
            SetPhase(job, PolarAlignmentPhase.Cancelled);
            return new RudimentaryStepResult(false, "Cancelled by user",
                0, 0, 0, 0, 0, job.History.Count);
        } catch (Exception ex) {
            _logger.LogError(ex, "Rudimentary alignment step crashed");
            return FailRudimentary(job, ex.Message);
        }
    }

    private RudimentaryStepResult FailRudimentary(PolarAlignmentJob job, string error) {
        job.LastError = error;
        job.Phase = PolarAlignmentPhase.Failed;
        try { JobUpdated?.Invoke(job); } catch { }
        _notify.Push("error", "Polar alignment: " + error);
        return new RudimentaryStepResult(false, error,
            0, 0, 0, 0, 0, job.History.Count);
    }
}

/// <summary>RDPA-2: kick off a fresh rudimentary session. Target is
/// the bright object the user picked from the catalog dropdown or
/// typed in by RA/Dec. SlewToTarget=false skips the GoTo (for manual
/// mounts or when the operator already pointed by hand).</summary>
public record RudimentaryStartRequest(
    double TargetRaHours,
    double TargetDecDeg,
    string? TargetName,
    bool SlewToTarget = true,
    double ExposureSeconds = 3.0,
    int Gain = 100,
    int SettleSeconds = 2);

/// <summary>Outcome of a single rudimentary iteration (Start or
/// Re-solve). The job lives on in CurrentJob with TargetRaHours +
/// History populated so the UI can render the sparkline.</summary>
public record RudimentaryStepResult(
    bool Ok,
    string? Error,
    double SolvedRaHours,
    double SolvedDecDeg,
    double AzErrorArcsec,
    double AltErrorArcsec,
    double TotalErrorArcsec,
    int IterationCount);

/// <summary>One entry in the rudimentary iteration history. The UI
/// renders these as a sparkline so the user can see the error trend
/// downward (convergence) vs flat / increasing (still need work).</summary>
public record RudimentaryIteration(double TotalErrorArcsec, DateTime AtUtc);

/// <summary>User-supplied TPPA options. All fields have sensible defaults
/// from the active rig's profile, the UI typically passes the rig
/// values verbatim, but the orchestrator accepts overrides so a
/// follow-up "tighten alignment" run can use different exposure /
/// gain without writing them back to the profile.</summary>
public record PolarAlignmentOptions(
    int SlewStepDegrees = 30,
    double ExposureSeconds = 3.0,
    int SettleSeconds = 2,
    int Gain = 100);

/// <summary>One solved point in a TPPA run. The triple of these gets
/// fed into PolarAlignmentMath.ComputeError to derive the mount's
/// polar-axis offset.</summary>
public record PolarPoint(
    int Index,
    double RaHours,
    double DecDeg,
    double RotationDeg,
    DateTime AtUtc);

public class PolarAlignmentJob {
    public string Id { get; set; } = "";
    public PolarAlignmentOptions Options { get; set; } = new();
    public PolarAlignmentPhase Phase { get; set; } = PolarAlignmentPhase.Idle;
    public List<PolarPoint> Points { get; set; } = new();
    public double AzErrorArcsec { get; set; }
    public double AltErrorArcsec { get; set; }
    public double TotalErrorArcsec { get; set; }
    public string? LastError { get; set; }
    /// <summary>"tppa" for the initial 3-point run, "refine" for the
    /// continuous loop, "rudimentary" for the single-target iterative
    /// workflow (RDPA). Drives UI labelling.</summary>
    public string Mode { get; set; } = "tppa";
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    internal CancellationTokenSource? Cts { get; set; }
    internal Task? Task { get; set; }

    /// <summary>RDPA-2: target the user picked for rudimentary mode.
    /// Null in TPPA mode. Persists across re-solve iterations so the
    /// math + sky markers always reference the same intended point.</summary>
    public double? TargetRaHours { get; set; }
    public double? TargetDecDeg { get; set; }
    public string? TargetName { get; set; }

    /// <summary>RDPA-2: most recent plate-solved pointing. Used by
    /// the sky-map markers (target vs actual) and the canvas arrow.</summary>
    public double? SolvedRaHours { get; set; }
    public double? SolvedDecDeg { get; set; }

    /// <summary>RDPA-2: rolling history of (totalError, at) per
    /// re-solve. UI renders a small sparkline so user can see the
    /// trend converge (or not) across knob adjustments. Capped at
    /// 20 entries in the service.</summary>
    public List<RudimentaryIteration> History { get; set; } = new();

    /// <summary>True while RunAsync is still chewing through phases.
    /// Used by the second-StartJob guard.</summary>
    public bool IsActive => Phase != PolarAlignmentPhase.Idle
                         && Phase != PolarAlignmentPhase.Ok
                         && Phase != PolarAlignmentPhase.Failed
                         && Phase != PolarAlignmentPhase.Cancelled;
}

public enum PolarAlignmentPhase {
    Idle,
    Preflight,
    MovingToPoint1,
    SolvingPoint1,
    MovingToPoint2,
    SolvingPoint2,
    MovingToPoint3,
    SolvingPoint3,
    Computing,
    /// <summary>Cleanup slew back to the user's original RA/Dec so the
    /// mount isn't left 60° off where they expected. Cosmetic, TPPA
    /// has already produced the error vector at this point.</summary>
    SlewingHome,
    Ok,
    Failed,
    Cancelled,
    /// <summary>PA-5: continuous capture+solve loop while the user
    /// adjusts the mount knobs.</summary>
    Refining,
    /// <summary>RDPA-2: rudimentary single-target alignment phases.
    /// Lumped under one mode but split into 3 sub-phases so the WS
    /// payload + UI status pill can show "Slewing to target" vs
    /// "Capturing" vs "Solving" without ambiguity.</summary>
    RudimentarySlewing,
    RudimentaryCapturing,
    RudimentarySolving,
}
