using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Polaris.Services;

/// <summary>
/// The "simple" sequencer engine, flat list of <see cref="SequenceItem"/>s
/// (target, filter, exposure, count) executed in order. This is what
/// the AUTORUN tab drives. The tree-based <c>AdvancedSequencer</c>
/// lives under <c>Services/Sequencer/</c> and serves the ADV tab.
///
/// The engine coordinates the full capture loop: filter swap → camera
/// expose → save to disk → live-stack push → PHD2 dither (when
/// triggered) → meridian-flip check (delegating to
/// <see cref="MeridianFlipService"/>). State is exposed through
/// public properties + polled by <c>StatusStreamHandler</c> at 1 Hz
/// so the UI can render progress without subscribing per-frame.
///
/// Pause/Resume uses a <see cref="SemaphoreSlim"/> gate; Abort cancels
/// the run-task's <see cref="CancellationTokenSource"/>. State
/// transitions are protected only by the single-task nature of the
/// run, at most one capture is in flight at any time.
/// </summary>
public class SequenceEngine {
    private readonly EquipmentManager _equip;
    private readonly ImageRelayService _relay;
    private readonly LiveStackingService _liveStack;
    private readonly PHD2Client _phd2;
    private readonly MeridianFlipService _meridianFlip;
    private readonly ImageWriterService _imageWriter;
    private readonly ILogger<SequenceEngine> _logger;

    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _pauseGate = new(1, 1);
    private Task? _runTask;

    /// <summary>Counter of frames captured since last dither (across all items).</summary>
    private int _framesSinceDither;

    public List<SequenceItem> Items { get; private set; } = [];
    public SequenceState State { get; private set; } = SequenceState.Idle;
    public int CurrentItemIndex { get; private set; } = -1;
    public int CurrentFrameInItem { get; private set; }
    public int TotalFramesCompleted { get; private set; }
    public string? LastError { get; private set; }
    public DateTime? StartedAt { get; private set; }

    /// <summary>Dither configuration. Default: disabled.</summary>
    public DitherSettings Dither { get; set; } = new();

    /// <summary>How many dithers were issued in the current run (diagnostic).</summary>
    public int DithersIssued { get; private set; }

    /// <summary>End-of-run housekeeping (park, warm, etc). Default: nothing.</summary>
    public SequenceEndActions EndActions { get; set; } = new();

    private readonly NINA.Polaris.Services.External.GraXpertService _graXpert;
    private readonly FlatWizardService _flatWizard;

    public SequenceEngine(EquipmentManager equip, ImageRelayService relay,
        LiveStackingService liveStack, PHD2Client phd2, MeridianFlipService meridianFlip,
        ImageWriterService imageWriter,
        NINA.Polaris.Services.External.GraXpertService graXpert,
        FlatWizardService flatWizard,
        ILogger<SequenceEngine> logger) {
        _equip = equip;
        _relay = relay;
        _liveStack = liveStack;
        _phd2 = phd2;
        _meridianFlip = meridianFlip;
        _imageWriter = imageWriter;
        _graXpert = graXpert;
        _flatWizard = flatWizard;
        _logger = logger;
    }

    public void LoadSequence(List<SequenceItem> items) {
        if (State == SequenceState.Running)
            throw new InvalidOperationException("Cannot load sequence while running");

        Items = items;
        CurrentItemIndex = -1;
        CurrentFrameInItem = 0;
        TotalFramesCompleted = 0;
        LastError = null;
        State = SequenceState.Idle;
        _logger.LogInformation("Sequence loaded: {Count} items, {Frames} total frames",
            items.Count, items.Sum(i => i.Count));
    }

    public void Start() {
        if (State == SequenceState.Running) return;

        if (Items.Count == 0) {
            LastError = "No items in sequence";
            return;
        }

        _cts = new CancellationTokenSource();
        State = SequenceState.Running;
        StartedAt = DateTime.UtcNow;
        LastError = null;
        _framesSinceDither = 0;
        DithersIssued = 0;
        _imageWriter.ResetSessionCounter();

        if (_pauseGate.CurrentCount == 0)
            _pauseGate.Release();

        _runTask = Task.Run(() => RunAsync(_cts.Token));
        _logger.LogInformation("Sequence started (dither: {Enabled}, every {N} frames, {Px}px)",
            Dither.Enabled, Dither.EveryNFrames, Dither.Pixels);
    }

    public void Pause() {
        if (State != SequenceState.Running) return;

        if (_pauseGate.CurrentCount > 0)
            _pauseGate.Wait(0);

        State = SequenceState.Paused;
        _logger.LogInformation("Sequence paused at item {Index}, frame {Frame}",
            CurrentItemIndex, CurrentFrameInItem);
    }

    public void Resume() {
        if (State != SequenceState.Paused) return;

        State = SequenceState.Running;
        if (_pauseGate.CurrentCount == 0)
            _pauseGate.Release();

        _logger.LogInformation("Sequence resumed");
    }

    public void Stop() {
        if (State == SequenceState.Idle) return;

        _cts?.Cancel();

        if (State == SequenceState.Paused && _pauseGate.CurrentCount == 0)
            _pauseGate.Release();

        State = SequenceState.Idle;
        _logger.LogInformation("Sequence stopped");
    }

    public SequenceStatus GetStatus() {
        var totalFrames = Items.Sum(i => i.Count);
        var elapsed = StartedAt.HasValue ? DateTime.UtcNow - StartedAt.Value : TimeSpan.Zero;

        double estimatedRemainingSeconds = 0;
        if (TotalFramesCompleted > 0 && totalFrames > TotalFramesCompleted) {
            var avgFrameTime = elapsed.TotalSeconds / TotalFramesCompleted;
            estimatedRemainingSeconds = avgFrameTime * (totalFrames - TotalFramesCompleted);
        }

        return new SequenceStatus {
            State = State.ToString().ToLowerInvariant(),
            Items = Items.Select((item, i) => new SequenceItemStatus {
                Name = item.Name,
                Exposure = item.Exposure,
                Count = item.Count,
                Completed = i < CurrentItemIndex ? item.Count :
                            i == CurrentItemIndex ? CurrentFrameInItem : 0,
                IsActive = i == CurrentItemIndex && State == SequenceState.Running
            }).ToList(),
            CurrentItemIndex = CurrentItemIndex,
            CurrentFrameInItem = CurrentFrameInItem,
            TotalFrames = totalFrames,
            TotalFramesCompleted = TotalFramesCompleted,
            ElapsedSeconds = elapsed.TotalSeconds,
            EstimatedRemainingSeconds = estimatedRemainingSeconds,
            LastError = LastError,
            DithersIssued = DithersIssued,
            FramesSinceDither = _framesSinceDither,
            Dither = Dither,
            EndActions = EndActions
        };
    }

    private async Task RunAsync(CancellationToken ct) {
        try {
            for (int i = Math.Max(0, CurrentItemIndex); i < Items.Count; i++) {
                ct.ThrowIfCancellationRequested();
                CurrentItemIndex = i;
                var item = Items[i];

                // BIAS frames are zero-second exposures by definition. If the
                // UI somehow sent a non-zero exposure, clamp it, saves the
                // user from wasting time on an obvious mistake.
                var imageType = (item.ImageType ?? "LIGHT").Trim().ToUpperInvariant();
                if (imageType == "BIAS") item.Exposure = 0;
                bool isCalibration = imageType is "DARK" or "BIAS" or "FLAT" or "DARKFLAT";

                _logger.LogInformation("Sequence item {Index}/{Total}: {Name} ({Type} {Exposure}s x {Count})",
                    i + 1, Items.Count, item.Name, imageType, item.Exposure, item.Count);

                // Slew only for LIGHT frames with explicit coords. Calibration
                // frames either don't care where the scope is pointed (darks,
                // bias) or rely on an external flat panel (flat).
                if (!isCalibration
                    && item.Ra.HasValue && item.Dec.HasValue
                    && _equip.Telescope != null) {
                    _logger.LogInformation("Slewing to {Name} (RA={Ra:F4}, Dec={Dec:F4})",
                        item.Name, item.Ra, item.Dec);

                    try {
                        await _equip.Telescope.SlewAsync(item.Ra.Value, item.Dec.Value, ct);
                        await WaitForSlewComplete(ct);
                    } catch (OperationCanceledException) { throw; }
                    catch (Exception ex) {
                        _logger.LogWarning(ex, "Slew failed for {Name}, continuing with capture", item.Name);
                    }
                }

                // Set binning if specified
                if (item.Binning > 0 && _equip.Camera != null) {
                    try {
                        await _equip.Camera.SetBinningAsync(item.Binning, item.Binning, ct);
                    } catch (Exception ex) {
                        _logger.LogWarning(ex, "Set binning failed");
                    }
                }

                // FLAT + AutoExposure: ask the wizard to resolve the
                // exposure for this (filter, binning) before we enter
                // the capture loop. Try the trained cache first (fast
                // path; convergence skipped entirely). On miss, run the
                // binary search and write the result back; subsequent
                // sessions hit the fast path. On failure we keep
                // item.Exposure as-is (whatever the user typed) so the
                // run still produces something, with a warning log.
                if (imageType == "FLAT" && item.AutoExposure && _equip.Camera != null) {
                    var filterKey = item.Filter ?? "";
                    var binKey = Math.Max(1, item.Binning);
                    if (_flatWizard.TryGetTrainedExposure(filterKey, binKey, out var cachedExp)) {
                        _logger.LogInformation(
                            "Auto-flat: using trained exposure {Exp}s for filter '{F}' bin{B}",
                            cachedExp, filterKey, binKey);
                        item.Exposure = cachedExp;
                    } else {
                        _logger.LogInformation(
                            "Auto-flat: no trained exposure for filter '{F}' bin{B}, searching...",
                            filterKey, binKey);
                        try {
                            var found = await _flatWizard.AutoFindExposureAsync(
                                filterKey, binKey, ct: ct);
                            if (found.HasValue) {
                                item.Exposure = found.Value;
                            } else {
                                _logger.LogWarning(
                                    "Auto-flat search did not converge; keeping user exposure {Exp}s",
                                    item.Exposure);
                            }
                        } catch (OperationCanceledException) { throw; }
                        catch (Exception ex) {
                            _logger.LogWarning(ex,
                                "Auto-flat search threw; keeping user exposure {Exp}s",
                                item.Exposure);
                        }
                    }
                }

                // Capture frames
                int startFrame = (i == Math.Max(0, CurrentItemIndex)) ? CurrentFrameInItem : 0;
                for (int f = startFrame; f < item.Count; f++) {
                    ct.ThrowIfCancellationRequested();

                    // Check pause gate
                    await _pauseGate.WaitAsync(ct);
                    _pauseGate.Release();

                    // Meridian flip check, meaningful only for LIGHT frames
                    // pointed at a real target.
                    if (!isCalibration
                        && item.Ra.HasValue && item.Dec.HasValue
                        && _meridianFlip.Settings.Enabled
                        && _meridianFlip.ShouldFlipNow(item.Ra.Value)) {
                        _logger.LogInformation("Meridian flip due for target {Name}, executing", item.Name);
                        await _meridianFlip.ExecuteFlipAsync(item.Ra.Value, item.Dec.Value, ct);
                    }

                    CurrentFrameInItem = f;

                    if (_equip.Camera == null) {
                        LastError = "No camera connected";
                        _logger.LogError("Sequence aborted: no camera");
                        State = SequenceState.Idle;
                        return;
                    }

                    _logger.LogDebug("Capturing frame {Frame}/{Total} for {Name}",
                        f + 1, item.Count, item.Name);

                    bool frameOk = false;
                    try {
                        var imageData = await _equip.Camera.CaptureAsync(item.Exposure, ct);

                        // Populate exposure-level metadata before saving / relaying
                        imageData.MetaData.Exposure.ExposureTime = item.Exposure;
                        if (!string.IsNullOrEmpty(item.Filter))
                            imageData.MetaData.Exposure.Filter = item.Filter;
                        if (!string.IsNullOrEmpty(item.Name))
                            imageData.MetaData.Target.Name = item.Name;
                        if (item.Ra.HasValue) imageData.MetaData.Target.RightAscension = item.Ra.Value;
                        if (item.Dec.HasValue) imageData.MetaData.Target.Declination = item.Dec.Value;

                        // Persist to disk with extended FITS headers (no-op if no output dir).
                        // imageType controls the calibration/light subfolder split in BuildSubDir.
                        var savedPath = _imageWriter.SaveImage(imageData, targetName: item.Name,
                            imageType: imageType, gain: item.Gain);

                        // Auto-GraXpert BGE hook. Fire-and-forget so the
                        // next exposure doesn't wait on the ~10s BGE pass.
                        // Only LIGHT frames + only when the user opted in
                        // + only when GraXpert is actually installed. Decon
                        // and Denoise never auto-run, they hurt SNR on
                        // individual lights and are best on integrated
                        // masters; offered manually in STUDIO instead.
                        if (EndActions.AutoGraXpert
                            && !string.IsNullOrEmpty(savedPath)
                            && !isCalibration
                            && _graXpert.IsAvailable) {
                            var fileToProcess = savedPath!;
                            _ = Task.Run(async () => {
                                try {
                                    var opts = new NINA.Polaris.Services.External.GraXpertOptions(
                                        Operation: NINA.Polaris.Services.External.GraXpertOperation.BackgroundExtraction);
                                    var res = await _graXpert.ProcessFrameAsync(
                                        fileToProcess, opts, CancellationToken.None);
                                    if (!string.IsNullOrEmpty(res.Error)) {
                                        _logger.LogWarning("Auto-GraXpert failed for {Path}: {Err}",
                                            fileToProcess, res.Error);
                                    }
                                } catch (Exception ex) {
                                    _logger.LogWarning(ex, "Auto-GraXpert hook threw for {Path}", fileToProcess);
                                }
                            });
                        }

                        if (_liveStack.IsRunning) {
                            await _liveStack.AddFrameAsync(imageData, ct);
                        } else {
                            await _relay.RelayImageAsync(imageData, ct);
                        }

                        CurrentFrameInItem = f + 1;
                        TotalFramesCompleted++;
                        frameOk = true;
                    } catch (OperationCanceledException) { throw; }
                    catch (Exception ex) {
                        _logger.LogWarning(ex, "Frame {Frame} capture failed for {Name}, retrying once",
                            f + 1, item.Name);

                        // Single retry after brief pause
                        try {
                            await Task.Delay(2000, ct);
                            var imageData = await _equip.Camera.CaptureAsync(item.Exposure, ct);

                            if (_liveStack.IsRunning)
                                await _liveStack.AddFrameAsync(imageData, ct);
                            else
                                await _relay.RelayImageAsync(imageData, ct);

                            CurrentFrameInItem = f + 1;
                            TotalFramesCompleted++;
                            frameOk = true;
                        } catch (OperationCanceledException) { throw; }
                        catch (Exception retryEx) {
                            _logger.LogError(retryEx, "Retry also failed for frame {Frame}, skipping", f + 1);
                            LastError = $"Frame {f + 1} of {item.Name} failed: {retryEx.Message}";
                        }
                    }

                    // Dither between frames (only after a successful capture, only
                    // if this isn't the very last frame of the very last item, and
                    // only for LIGHT, dithering darks/flats would corrupt the
                    // calibration master and hammer the mount needlessly).
                    if (frameOk && !isCalibration) {
                        _framesSinceDither++;
                        bool moreFramesComing = (f + 1 < item.Count) || (i + 1 < Items.Count);
                        if (moreFramesComing) {
                            await MaybeDitherAsync(ct);
                        }
                    }
                }

                _logger.LogInformation("Completed item: {Name}", item.Name);
            }

            State = SequenceState.Idle;
            _logger.LogInformation("Sequence completed: {Frames} frames in {Elapsed}",
                TotalFramesCompleted,
                StartedAt.HasValue ? (DateTime.UtcNow - StartedAt.Value).ToString(@"hh\:mm\:ss") : "??");

            // Natural completion always fires the end-actions.
            await RunEndActionsAsync(triggeredByStop: false);

        } catch (OperationCanceledException) {
            _logger.LogInformation("Sequence cancelled");
            // Stop is a user action, only run housekeeping if the user opted in.
            if (EndActions.RunOnStop) {
                await RunEndActionsAsync(triggeredByStop: true);
            }
        } catch (Exception ex) {
            LastError = ex.Message;
            State = SequenceState.Idle;
            _logger.LogError(ex, "Sequence failed");
            // Failure: still try housekeeping so the rig isn't left tracking unattended.
            await RunEndActionsAsync(triggeredByStop: true);
        }
    }

    /// <summary>
    /// Run the configured post-sequence actions. All failures are caught + logged;
    /// one broken action does not prevent the next from being tried. Uses a fresh
    /// cancellation token so a sequence-stop cannot cancel the cleanup itself.
    /// </summary>
    private async Task RunEndActionsAsync(bool triggeredByStop) {
        var ea = EndActions;
        if (ea == null) return;
        if (!ea.ParkMount && !ea.StopTracking && !ea.WarmCamera && !ea.DisconnectGuider) return;

        _logger.LogInformation("Running end-of-sequence actions (triggeredByStop={Stop})", triggeredByStop);
        using var ct = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        // Park supersedes stop-tracking, parking implies tracking off, and most
        // mounts refuse the explicit tracking-off command after they're parked.
        if (ea.ParkMount && _equip.Telescope != null) {
            try {
                _logger.LogInformation("End-action: parking mount");
                await _equip.Telescope.ParkAsync(ct.Token);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "End-action park failed");
            }
        } else if (ea.StopTracking && _equip.Telescope != null) {
            try {
                _logger.LogInformation("End-action: stopping tracking");
                await _equip.Telescope.SetTrackingAsync(false, ct.Token);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "End-action stop-tracking failed");
            }
        }

        if (ea.WarmCamera && _equip.Camera != null) {
            try {
                _logger.LogInformation("End-action: warming camera (cooler off)");
                await _equip.Camera.SetCoolerAsync(false, ct.Token);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "End-action warm-camera failed");
            }
        }

        if (ea.DisconnectGuider && _phd2.IsConnected) {
            try {
                _logger.LogInformation("End-action: stopping PHD2 guiding");
                await _phd2.StopAsync();
            } catch (Exception ex) {
                _logger.LogWarning(ex, "End-action stop-guider failed");
            }
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
    /// Issue a dither command via PHD2 if all preconditions are met and we've
    /// hit the configured frame cadence. Waits for SettleDone before returning.
    /// Silently skips when conditions aren't met, never aborts the sequence.
    /// </summary>
    private async Task MaybeDitherAsync(CancellationToken ct) {
        if (!Dither.Enabled) return;
        if (Dither.EveryNFrames <= 0) return;
        if (_framesSinceDither < Dither.EveryNFrames) return;

        if (!_phd2.IsConnected) {
            _logger.LogDebug("Dither skipped: PHD2 not connected");
            _framesSinceDither = 0;
            return;
        }

        if (!_phd2.IsGuiding) {
            _logger.LogDebug("Dither skipped: PHD2 not guiding (state={State})", _phd2.AppState);
            _framesSinceDither = 0;
            return;
        }

        _logger.LogInformation("Dithering {Px}px (after {N} frames, raOnly={RaOnly})",
            Dither.Pixels, _framesSinceDither, Dither.RaOnly);

        // Hook up SettleDone before we issue the dither to avoid race
        var settled = new TaskCompletionSource<SettleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSettled(SettleResult r) => settled.TrySetResult(r);
        _phd2.Settled += OnSettled;

        try {
            await _phd2.DitherAsync(
                pixels: Dither.Pixels,
                raOnly: Dither.RaOnly,
                settlePixels: Dither.SettlePixels,
                settleTime: Dither.SettleTime,
                settleTimeout: Dither.SettleTimeout);

            DithersIssued++;

            // Wait for SettleDone with a hard ceiling = configured timeout + 5s grace
            var maxWait = TimeSpan.FromSeconds(Dither.SettleTimeout + 5);
            using var settleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            settleCts.CancelAfter(maxWait);

            try {
                var result = await settled.Task.WaitAsync(settleCts.Token);
                if (result.Status == 0) {
                    _logger.LogInformation("Dither settled OK ({Total} frames, {Dropped} dropped)",
                        result.TotalFrames, result.DroppedFrames);
                } else {
                    _logger.LogWarning("Dither settle returned status {Status}: {Error}",
                        result.Status, result.Error);
                }
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                _logger.LogWarning("Dither settle timed out after {Sec}s, continuing sequence anyway",
                    Dither.SettleTimeout);
            }
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Dither command failed, continuing sequence without dither");
        } finally {
            _phd2.Settled -= OnSettled;
            _framesSinceDither = 0;
        }
    }
}

public enum SequenceState { Idle, Running, Paused }

public class SequenceItem {
    public string Name { get; set; } = "";
    public double Exposure { get; set; } = 1.0;
    public int Gain { get; set; } = 100;
    public int Binning { get; set; } = 1;
    public int Count { get; set; } = 1;
    public string? Filter { get; set; }
    public double? Ra { get; set; }
    public double? Dec { get; set; }

    /// <summary>
    /// Frame classification: LIGHT (default), DARK, BIAS, FLAT, DARKFLAT.
    /// ImageWriterService.BuildSubDir already routes each type to its
    /// own folder; the engine uses it to (a) tag the saved file, (b)
    /// skip slew/dither/meridian-flip for calibration items, and (c)
    /// force exposure=0 for BIAS regardless of what the UI sent.
    /// </summary>
    public string ImageType { get; set; } = "LIGHT";

    /// <summary>
    /// When true and <see cref="ImageType"/> is FLAT, the engine asks
    /// <see cref="FlatWizardService.AutoFindExposureAsync"/> to
    /// binary-search the right exposure for this (filter, binning)
    /// before capturing <see cref="Count"/> frames. Trained-exposure
    /// cache short-circuits the search on subsequent runs.
    ///
    /// Lets users without a filter wheel (the Flat Wizard sub-tab is
    /// filter-wheel-gated) still benefit from auto-exposure flats by
    /// dropping a single FLAT item in AUTORUN with Auto = on.
    ///
    /// Ignored for every other ImageType; <see cref="Exposure"/> wins
    /// when this flag is false.
    /// </summary>
    public bool AutoExposure { get; set; }
}

/// <summary>
/// Per-run actions executed once the sequence finishes (or is stopped,
/// if <see cref="RunOnStop"/> is true). All actions are best-effort:
/// a failure on one does not skip the rest, we log and move on.
/// </summary>
public class SequenceEndActions {
    public bool ParkMount { get; set; }
    public bool StopTracking { get; set; }
    public bool WarmCamera { get; set; }
    public bool DisconnectGuider { get; set; }
    /// <summary>If true, end-actions also fire when the user hits Stop. Default false.</summary>
    public bool RunOnStop { get; set; }

    /// <summary>
    /// Per-frame hook (not strictly an end-action, lives here so it
    /// shares the Autorun panel UI). When true and GraXpert is
    /// installed, every saved LIGHT frame is shipped to GraXpert for
    /// background-extraction in a fire-and-forget Task. Calibration
    /// frames are skipped. The next exposure does not wait on the
    /// ~10 s BGE pass, explicit performance > purity trade-off.
    /// </summary>
    public bool AutoGraXpert { get; set; }
}

public class SequenceStatus {
    public string State { get; set; } = "idle";
    public List<SequenceItemStatus> Items { get; set; } = [];
    public int CurrentItemIndex { get; set; }
    public int CurrentFrameInItem { get; set; }
    public int TotalFrames { get; set; }
    public int TotalFramesCompleted { get; set; }
    public double ElapsedSeconds { get; set; }
    public double EstimatedRemainingSeconds { get; set; }
    public string? LastError { get; set; }
    public int DithersIssued { get; set; }
    public int FramesSinceDither { get; set; }
    public DitherSettings? Dither { get; set; }
    public SequenceEndActions? EndActions { get; set; }
}

public class SequenceItemStatus {
    public string Name { get; set; } = "";
    public double Exposure { get; set; }
    public int Count { get; set; }
    public int Completed { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>
/// Dithering configuration for a sequence run. The engine asks PHD2 to dither
/// after every <see cref="EveryNFrames"/> successfully-captured frames, and
/// waits for SettleDone before continuing.
/// </summary>
public class DitherSettings {
    public bool Enabled { get; set; }
    /// <summary>Random pixel offset (passed to PHD2 'dither' as amount).</summary>
    public double Pixels { get; set; } = 5.0;
    /// <summary>Trigger a dither after every N successfully-captured frames.</summary>
    public int EveryNFrames { get; set; } = 1;
    /// <summary>Only dither in RA (useful for mounts with sloppy Dec backlash).</summary>
    public bool RaOnly { get; set; }
    /// <summary>Settle distance tolerance in pixels.</summary>
    public double SettlePixels { get; set; } = 1.5;
    /// <summary>Minimum settled time in seconds.</summary>
    public int SettleTime { get; set; } = 10;
    /// <summary>Hard timeout for settling, in seconds.</summary>
    public int SettleTimeout { get; set; } = 40;
}
