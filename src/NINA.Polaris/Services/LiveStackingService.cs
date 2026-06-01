using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>Async handler invoked once per integrated frame. Handlers
/// run sequentially inside the caller's await chain, a long-running
/// handler (e.g. an auto-focus run) naturally pauses the next capture
/// because the caller is awaiting AddFrameAsync. This is the
/// LiveStackTriggersService integration point (LSTR-1).</summary>
public delegate Task LiveStackFrameHandler(LiveStackFrameInfo info);

public record LiveStackFrameInfo(
    int FrameCount,        // count AFTER this integration
    IImageData Frame,      // the raw frame integrated (not the running stack)
    double MedianHfr,      // median HFR of stars detected in this frame
    int StarCount,
    DateTime At,
    double FrameSnr = 0,       // background SNR of the incoming frame
    double CumulativeSnr = 0); // SNR of the running-mean accumulator

/// <summary>
/// Where the per-frame stacking math runs.
/// <list type="bullet">
/// <item><b>Full</b> (default): the server runs the whole pipeline,
/// StarDetector + StarMatcher + AffineTransform + ImageResampler +
/// running-mean accumulator. Server holds the accumulated stack and
/// pushes it as the live preview. This is the historical behaviour
/// and stays the safe fallback.</item>
/// <item><b>MetricsOnly</b>: the server still runs StarDetector (so
/// the trigger orchestrator gets HFR/star count + the reference solve
/// on frame 1 still happens), but skips matching/warping/accumulating.
/// The raw frame is still relayed to clients via ImageRelayService;
/// a client-side WASM module is expected to do the actual stacking
/// and render its own preview. Used by the CLST offloading work,
/// see plan file.</item>
/// </list>
/// </summary>
public enum StackMode {
    Full,
    MetricsOnly
}

public class LiveStackingService {
    private readonly ImageRelayService _relay;
    // Optional: null in unit tests that don't exercise SaveFramesToDisk.
    // Production DI always supplies it because both singletons are
    // registered in Program.cs and the service constructor resolves
    // strictly via the registered graph.
    private readonly ImageWriterService? _writer;
    private readonly ILogger<LiveStackingService> _logger;
    private readonly StarDetector _detector = new() { MaxStars = 200 };
    private readonly object _lock = new();

    private float[]? _stackBuffer;
    private int[]? _countBuffer;
    private int _width;
    private int _height;
    private int _frameCount;
    private int _framesSavedToDisk;
    private List<DetectedStar>? _referenceStars;
    // Default: stacking is ON. Live stacking is the user's expected
    // behaviour the moment they point a camera at the sky — they
    // shouldn't have to click "Start" first. The toggle still exists
    // for the rare "I want raw passthrough, no stacking" case.
    private bool _isRunning = true;
    private DateTime? _startedAt;

    /// <summary>When true, every raw frame received via
    /// <see cref="AddFrameAsync"/> is also persisted to disk via
    /// <see cref="ImageWriterService.SaveImage"/> with imageType
    /// "LIGHT", landing in {rig}/lights/{target}/{filter}/{date}
    /// like a regular sequence capture. Default ON — most users
    /// want both the integrated preview AND an archive of the raw
    /// frames so they can re-stack offline in Siril / PixInsight
    /// later. UI checkbox in LIVE tab persists the choice per-rig
    /// via PUT /api/livestack/save-frames.</summary>
    public bool SaveFramesToDisk { get; set; } = true;

    /// <summary>When > 0, stacking auto-pauses after this many
    /// seconds elapsed since the first frame of the current stack
    /// (i.e. since the last Reset). 0 = run indefinitely. Frames
    /// arriving past the cap are still relayed to clients + saved
    /// to disk (when <see cref="SaveFramesToDisk"/> is on), but
    /// don't update the running mean. Reset clears the timer too.
    /// Set via PUT /api/livestack/max-duration.</summary>
    public int MaxDurationSeconds { get; set; }

    /// <summary>When the current stack started (first frame after
    /// the most recent Reset). Null when no frame has been
    /// integrated yet. Used to drive the elapsed counter shown in
    /// the LIVE tab and the auto-pause check against
    /// <see cref="MaxDurationSeconds"/>.</summary>
    public DateTime? StartedAt => _startedAt;

    /// <summary>Seconds elapsed since the first frame of the current
    /// stack. 0 when no frame has been integrated yet.</summary>
    public double ElapsedSeconds =>
        _startedAt is { } t ? (DateTime.UtcNow - t).TotalSeconds : 0;

    /// <summary>True when <see cref="MaxDurationSeconds"/> is set and
    /// the elapsed time has crossed it. UI uses this to render a
    /// "complete" badge instead of "running" once the cap fires.</summary>
    public bool DurationCapReached =>
        MaxDurationSeconds > 0 && ElapsedSeconds >= MaxDurationSeconds;

    /// <summary>Counter of frames actually written to disk during
    /// the current live-stack session. Resets along with
    /// <see cref="FrameCount"/> in <see cref="Reset"/>. Exposed on
    /// the status payload so the UI can show "12 saved" next to
    /// the toggle as live confirmation that the writes are landing.</summary>
    public int FramesSavedToDisk => _framesSavedToDisk;

    // Frame-integrated subscribers (LSTR-1). Append-only list guarded
    // by _handlersLock for snapshotting; handlers awaited sequentially
    // inside AddFrameAsync so a slow handler (AF run, recenter) blocks
    // the caller and naturally pauses the next capture.
    private readonly List<LiveStackFrameHandler> _frameHandlers = new();
    private readonly object _handlersLock = new();

    public bool IsRunning => _isRunning;
    public int FrameCount => _frameCount;
    public int Width => _width;
    public int Height => _height;
    public double LastFrameMedianHfr { get; private set; }
    public int LastFrameStarCount { get; private set; }
    // SNR-4: background SNR per-frame + cumulative-stack. CumulativeSnr
    // is the headline number in the LIVE-tab "stack quality" widget —
    // it's the SNR of the running-mean accumulator, growing ~√N as
    // frames stack.
    public double LastFrameSnr { get; private set; }
    public double CumulativeSnr { get; private set; }
    /// <summary>Rolling history of (frameCount, cumulativeSnr) used
    /// by <see cref="SnrEtaCalculator"/> to fit the √N model + ETA.
    /// Capped at 50 entries — beyond that the fit is dominated by
    /// recent samples anyway and we'd just be paying memory for
    /// nothing.</summary>
    public IReadOnlyList<(int frame, double snr)> SnrHistory => _snrHistory;
    private readonly List<(int frame, double snr)> _snrHistory = new(50);
    /// <summary>Cached last ETA result. Recomputed each AddFrame so
    /// the WS broadcaster can serve it without re-fitting.</summary>
    public SnrEtaCalculator.EtaResult? LastEta { get; private set; }

    /// <summary>Where the per-frame math runs. Default <see cref="StackMode.Full"/>.
    /// Switched to <see cref="StackMode.MetricsOnly"/> by the WASM
    /// handshake (CLST-5) when a WASM-capable client is connected and
    /// the active rig hasn't forced server-side.</summary>
    public StackMode Mode { get; set; } = StackMode.Full;

    // LSPP-3+4: per-frame pre-processing. Settings read from the active
    // rig on every frame so live toggles take effect without a restart.
    // PreProcessor is the singleton (Program.cs); null in unit-test
    // doubles -- splice is no-op when null. Status fields broadcast via
    // the WS payload so the LIVE-tab UI can show counters in real time.
    private readonly LiveStackPreProcessor? _preProcessor;
    private readonly ProfileService? _profiles;
    public LiveStackPreProcStatus PreProcStatus { get; } = new();

    public LiveStackingService(ImageRelayService relay,
                                ILogger<LiveStackingService> logger,
                                ImageWriterService? writer = null,
                                ProfileService? profiles = null,
                                LiveStackPreProcessor? preProcessor = null) {
        _relay = relay;
        _writer = writer;
        _logger = logger;
        _profiles = profiles;
        _preProcessor = preProcessor;
        // SNR-3: keep TargetSnr aligned with the active rig until the
        // user explicitly overrides via /api/livestack/target-snr.
        // ProfileService is optional in the ctor so the existing test
        // doubles (which instantiate without DI) keep working.
        if (profiles != null) {
            TargetSnr = profiles.ActiveEquipmentProfile?.TargetSnr;
            profiles.EquipmentProfileActivated += rig => {
                // Refresh only if no override is in place — the user's
                // session-level number sticks until they clear it.
                if (_targetSnrOverride == null) TargetSnr = rig?.TargetSnr;
                // LSPP-3: switching rigs invalidates the master cache
                // (different rig = different gain/binning likely).
                _preProcessor?.Reset();
                PreProcStatus.Reset();
            };
        }
    }
    private double? _targetSnrOverride;
    /// <summary>Called by the /api/livestack/target-snr endpoint to
    /// distinguish a session override from a rig-default refresh.</summary>
    public void SetTargetSnrOverride(double? value) {
        _targetSnrOverride = value;
        TargetSnr = value;
        RecomputeEta();
    }

    /// <summary>Subscribe to per-frame integration events. Handlers
    /// are awaited sequentially inside <see cref="AddFrameAsync"/>;
    /// a slow handler pauses the upstream capture loop. Returns an
    /// IDisposable that removes the subscription.</summary>
    public IDisposable SubscribeFrameIntegrated(LiveStackFrameHandler handler) {
        lock (_handlersLock) _frameHandlers.Add(handler);
        return new HandlerSub(this, handler);
    }

    private sealed class HandlerSub : IDisposable {
        private readonly LiveStackingService _svc;
        private readonly LiveStackFrameHandler _h;
        public HandlerSub(LiveStackingService svc, LiveStackFrameHandler h) { _svc = svc; _h = h; }
        public void Dispose() {
            lock (_svc._handlersLock) _svc._frameHandlers.Remove(_h);
        }
    }

    /// <summary>Clear the accumulator + reference + counters and
    /// start a fresh stack on the next incoming frame. Does NOT
    /// flip IsRunning off — stacking stays armed and the new
    /// stack begins immediately when the next frame arrives. Used
    /// when the user switches targets and wants to start over.</summary>
    public void Reset() {
        lock (_lock) {
            _stackBuffer = null;
            _countBuffer = null;
            _referenceStars = null;
            _frameCount = 0;
            _framesSavedToDisk = 0;
            _width = 0;
            _height = 0;
            _startedAt = null;
            LastFrameMedianHfr = 0;
            LastFrameStarCount = 0;
            LastFrameSnr = 0;
            CumulativeSnr = 0;
            _snrHistory.Clear();
            LastEta = null;
            // LSPP-3+4: target switch -> drop the master cache so the
            // next frame re-resolves with the new filter/exposure/gain.
            _preProcessor?.Reset();
            PreProcStatus.Reset();
            _logger.LogInformation("Live stacking reset");
        }
    }

    /// <summary>Arm stacking AND clear the current accumulator. The
    /// "fresh start" path — use when the operator wants to begin a
    /// new target / discard whatever was stacked before. Prefer
    /// <see cref="Resume"/> when the operator paused mid-session
    /// and wants to keep building on the existing stack.</summary>
    public void Start() {
        Reset();
        _isRunning = true;
        _logger.LogInformation("Live stacking started (buffer reset)");
    }

    /// <summary>Re-arm stacking WITHOUT clearing the accumulator. New
    /// frames continue to integrate into the running mean. Lets the
    /// operator pause (e.g. clouds rolled in), fix things, then pick
    /// up where they left off — typical workflow when wifi drops or
    /// you need to disconnect the laptop for a minute.</summary>
    public void Resume() {
        if (_frameCount == 0) {
            // Nothing to resume FROM — fall through to Start so the
            // first frame still establishes the reference. Avoids a
            // confusing state where Resume succeeds but the next
            // frame can't align to an empty buffer.
            Start();
            return;
        }
        _isRunning = true;
        _logger.LogInformation("Live stacking resumed at {Count} frames", _frameCount);
    }

    /// <summary>Disarm stacking. Frames still flow through the relay
    /// + per-frame save path but no longer update the running mean.
    /// Pair with <see cref="Resume"/> to continue or
    /// <see cref="Start"/> to begin a new stack.</summary>
    public void Stop() {
        _isRunning = false;
        _logger.LogInformation("Live stacking stopped after {Count} frames", _frameCount);
    }

    public async Task AddFrameAsync(IImageData imageData, CancellationToken ct = default) {
        // Disk persistence runs INDEPENDENTLY of whether the stacker
        // is currently armed and INDEPENDENTLY of whether the
        // duration cap was reached — the user opted to keep raw
        // frames, so we should keep ALL of them. Stacking math
        // below short-circuits when disarmed / past cap, but the
        // archive doesn't.
        if (SaveFramesToDisk && _writer != null) {
            try {
                var savedPath = _writer.SaveImage(imageData, imageType: "LIGHT");
                if (savedPath != null) {
                    Interlocked.Increment(ref _framesSavedToDisk);
                    _logger.LogDebug("Live stack: saved frame to {Path}", savedPath);
                }
            } catch (Exception ex) {
                // Don't poison the stack pipeline because of a disk
                // hiccup. Log and continue; the next frame will retry.
                _logger.LogWarning(ex, "Live stack: failed to save frame to disk");
            }
        }

        if (!_isRunning) return;

        // Duration cap. Once the elapsed time crosses
        // MaxDurationSeconds, stop touching the accumulator —
        // further frames are saved to disk (above) and relayed to
        // clients, but the stacked preview holds steady at the
        // master that completed at the cap. Reset clears _startedAt
        // and the timer restarts on the next frame.
        if (DurationCapReached) {
            _logger.LogDebug("Live stack: duration cap reached ({Cap}s), skipping accumulation",
                MaxDurationSeconds);
            return;
        }

        var props = imageData.Properties;
        var data = imageData.Data;

        var mode = Mode;
        _logger.LogInformation("Live stack: processing frame {N} ({W}x{H}), mode={Mode}",
            _frameCount + 1, props.Width, props.Height, mode);

        // LSPP-4: per-frame pre-processing splice. Calibration runs
        // here on the server (or via the client when MetricsOnly is
        // chosen by the server-side stack -- either way the pixels
        // we feed into StarDetector below are the calibrated ones).
        // BGE is handled client-side ONLY (MetricsOnly) so the server
        // just tracks supportedThisSession for the WS payload.
        var preProcSettings = _profiles?.ActiveEquipmentProfile?.LiveStackPreProcessing
                              ?? new LiveStackPreProcSettings();
        PreProcStatus.BgeSupportedThisSession = (mode == StackMode.MetricsOnly);
        if (preProcSettings.CalibrationEnabled && _preProcessor != null) {
            var res = await _preProcessor.ApplyAsync(imageData, preProcSettings, ct);
            if (res.Success && (res.MasterDarkUsed != null
                                || res.MasterFlatUsed != null
                                || res.MasterBiasUsed != null)) {
                // Calibration applied successfully -- swap in the
                // calibrated pixels for the rest of the pipeline.
                data = res.Pixels;
                PreProcStatus.RecordCalibrationApplied(res);
            } else if (!res.Success) {
                // Math threw / master corrupted -- fall back to raw
                // pixels so the session continues. Operator sees the
                // counter increment via WS, and the warning lands in
                // the debug log via the helper.
                _logger.LogWarning(
                    "Live-stack calibration failed for frame {N}: {Err}",
                    _frameCount + 1, res.Error);
                PreProcStatus.RecordCalibrationFallback(res.Error);
            } else {
                // Success but no masters matched (auto-match empty).
                // Don't penalise the counter as fallback -- nothing
                // went wrong, there just wasn't anything to apply.
                PreProcStatus.RecordCalibrationNoMatch();
            }
        }

        // StarDetector runs in BOTH modes:
        //   - Full: feeds StarMatcher for alignment + provides HFR
        //   - MetricsOnly: trigger orchestrator (LSTR-3) needs HFR +
        //     star count even when stacking happens client-side
        var stars = _detector.Detect(data, props.Width, props.Height);
        _logger.LogDebug("Detected {Count} stars in frame", stars.Count);

        if (mode == StackMode.Full) {
            ushort[] alignedData;

            lock (_lock) {
                if (_frameCount == 0) {
                    // First frame: initialize buffers and set as reference.
                    // Stamp _startedAt so the elapsed counter + duration
                    // cap have something to reference. Reset clears
                    // both — the next first frame restarts the timer.
                    _startedAt = DateTime.UtcNow;
                    _width = props.Width;
                    _height = props.Height;
                    int pixelCount = _width * _height;
                    _stackBuffer = new float[pixelCount];
                    _countBuffer = new int[pixelCount];
                    _referenceStars = stars;
                    alignedData = data;
                } else {
                    if (props.Width != _width || props.Height != _height) {
                        _logger.LogWarning("Frame size mismatch: {W}x{H} vs {ExpW}x{ExpH}",
                            props.Width, props.Height, _width, _height);
                        return;
                    }

                    // Align to reference
                    var transform = StarMatcher.Match(_referenceStars!, stars);
                    if (transform == null) {
                        _logger.LogWarning("Alignment failed for frame {N}, skipping", _frameCount + 1);
                        return;
                    }

                    alignedData = ImageResampler.ApplyTransform(data, _width, _height, transform);
                    _logger.LogDebug("Frame aligned: dx={Tx:F1} dy={Ty:F1}", transform.Tx, transform.Ty);
                }

                // Accumulate into stack buffer (running average)
                for (int i = 0; i < alignedData.Length && i < _stackBuffer!.Length; i++) {
                    if (alignedData[i] > 0) {
                        _stackBuffer[i] += alignedData[i];
                        _countBuffer![i]++;
                    }
                }

                _frameCount++;
            }

            // Generate stacked result and relay to clients
            var stackedPixels = GetStackedResult();
            var stackedProps = new ImageProperties {
                Width = _width,
                Height = _height,
                BitDepth = props.BitDepth,
                IsBayered = props.IsBayered,
                BayerPattern = props.BayerPattern
            };

            var stackedImage = new BaseImageData(stackedPixels, stackedProps, imageData.MetaData);
            await _relay.RelayImageAsync(stackedImage, ct);
        } else {
            // MetricsOnly: bookkeep frame count + dimensions so triggers
            // and status broadcasts have something to render, but skip
            // the accumulator. The raw frame is still relayed via
            // ImageRelayService elsewhere in the capture path (see
            // SequenceEngine / ImageRelayService.RelayImageAsync from
            // the camera capture endpoint), the WASM client picks it
            // up from the existing /ws/image-stream raw mode.
            lock (_lock) {
                if (_frameCount == 0) {
                    _startedAt = DateTime.UtcNow;
                    _width = props.Width;
                    _height = props.Height;
                    _referenceStars = stars;
                }
                _frameCount++;
            }
        }

        // Compute median HFR from the already-detected stars (no extra
        // pixel pass). Falls back to 0 when no stars, handlers that
        // care about HFR should treat 0 as "no data this frame".
        // Computed in BOTH modes so trigger orchestrator (auto-AF based
        // on HFR degradation) still works in MetricsOnly mode.
        double medianHfr = 0;
        if (stars.Count > 0) {
            var sorted = stars.Select(s => s.HFR).Where(h => h > 0).OrderBy(h => h).ToList();
            if (sorted.Count > 0) medianHfr = sorted[sorted.Count / 2];
        }
        LastFrameMedianHfr = medianHfr;
        LastFrameStarCount = stars.Count;

        // SNR-4: per-frame + cumulative background SNR.
        // - LastFrameSnr is the snap-quality of the incoming frame.
        //   Cheap (one extra pixel pass that piggy-backs on the same
        //   median/MAD we already need for the stretch path).
        // - CumulativeSnr is the SNR of the running-mean accumulator.
        //   In Full mode we compute it from _accumulator; in
        //   MetricsOnly mode the WASM client tells us via
        //   InjectCumulativeSnr() below (no buffer here to inspect).
        try {
            LastFrameSnr = ComputeFrameSnr(imageData.Data);
            if (mode == StackMode.Full) {
                CumulativeSnr = ComputeCumulativeSnrFromAccumulator();
            }
            RecordSnrSample(_frameCount, CumulativeSnr);
            RecomputeEta();
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Live stack: SNR computation failed (non-fatal)");
        }

        _logger.LogInformation("Live stack: frame {N} added, {Stars} stars (HFR={Hfr:F2}, snr={Snr:F1} cum={Cum:F1}), mode={Mode}",
            _frameCount, stars.Count, medianHfr, LastFrameSnr, CumulativeSnr, mode);

        // Snapshot handlers + await sequentially. Any handler that
        // throws is logged + swallowed, one bad subscriber can't
        // poison the chain. Slow handlers (AF, recenter) pause the
        // upstream capture loop by extending this await.
        LiveStackFrameHandler[] handlers;
        lock (_handlersLock) handlers = _frameHandlers.ToArray();
        if (handlers.Length > 0) {
            var info = new LiveStackFrameInfo(_frameCount, imageData, medianHfr, stars.Count, DateTime.UtcNow,
                FrameSnr: LastFrameSnr, CumulativeSnr: CumulativeSnr);
            foreach (var h in handlers) {
                try { await h(info); }
                catch (Exception ex) {
                    _logger.LogWarning(ex, "LiveStack frame handler threw (continuing)");
                }
            }
        }
    }

    // ===== SNR-4 helpers =========================================
    //
    // TargetSnr + ExposureSecondsHint are caller-set knobs (the LIVE
    // tab pushes them via /api/livestack/target-snr + the capture
    // endpoint hands us the last exposure). Both nullable: when null
    // the ETA computation returns null and the UI shows "—".

    /// <summary>Target SNR for the ETA widget. Frontend sets via the
    /// LIVE tab's override input (which itself defaults to the
    /// active rig's TargetSnr profile field). Null = no target →
    /// no ETA computed.</summary>
    public double? TargetSnr { get; set; }

    /// <summary>Average exposure time of recent frames, seconds.
    /// Used by ETA to convert frames-remaining into time-remaining.
    /// Capture endpoints push the last exposure here so the ETA
    /// reflects the actual sub length being shot.</summary>
    public double AverageExposureSec { get; set; } = 1.0;

    /// <summary>MetricsOnly mode bridge: the WASM client side
    /// computes cumulativeSnr on its accumulator and posts it back
    /// via the existing 'client-stack-progress' WS message. The
    /// ImageStreamHandler consumes the message and forwards via
    /// this method so the WS broadcast + ETA work the same as in
    /// Full mode. Frame-side per-frame snr also flows here so the
    /// LIVE / PREVIEW UIs render consistent numbers.</summary>
    public void InjectClientStackMetrics(int frameCount, double frameSnr, double cumulativeSnr) {
        InjectClientStackMetrics(frameCount, frameSnr, cumulativeSnr,
            bgeProcessed: null, bgeFallback: null, bgeError: null);
    }

    /// <summary>LSPP-5: extended overload that also lets the client
    /// report per-session BGE counters back to the server. When the
    /// browser runs BGE per-frame (MetricsOnly + bgeEnabled), it
    /// posts the running counters here so the WS broadcast can
    /// mirror them to every other connected browser + the LIVE-tab
    /// status badge stays in sync. Null params on the legacy
    /// 3-arg overload keep the existing CLST-5 wire format working
    /// untouched.</summary>
    public void InjectClientStackMetrics(int frameCount, double frameSnr, double cumulativeSnr,
                                          int? bgeProcessed, int? bgeFallback, string? bgeError) {
        if (Mode != StackMode.MetricsOnly) return;
        // Defensive: only update when the WASM client's frameCount is
        // not behind ours (it lags by ≤1 due to async dispatch). A
        // stale message shouldn't rewrite history.
        if (frameCount < _frameCount - 1) return;
        if (double.IsFinite(frameSnr) && frameSnr >= 0) LastFrameSnr = frameSnr;
        if (double.IsFinite(cumulativeSnr) && cumulativeSnr >= 0) {
            CumulativeSnr = cumulativeSnr;
            RecordSnrSample(frameCount, cumulativeSnr);
            RecomputeEta();
        }
        if (bgeProcessed.HasValue || bgeFallback.HasValue) {
            PreProcStatus.InjectClientBgeMetrics(
                processed: bgeProcessed ?? 0,
                fallback:  bgeFallback  ?? 0,
                error: bgeError);
        }
    }

    private double ComputeFrameSnr(ushort[] data) {
        // Quick passes for median + MAD. ImageStatistics.Create does
        // the same work but allocates an ImageStatistics object and
        // a 65536-int histogram twice — for the live stack we run
        // per-frame so we keep it lean: a single histogram + the
        // existing helper to extract median, then a second use of
        // the same histogram for MAD. The 65536 int histogram is ~0.25
        // MB which is fine.
        if (data == null || data.Length == 0) return 0;
        var hist = new int[65536];
        for (int i = 0; i < data.Length; i++) hist[data[i]]++;
        long half = data.Length / 2;
        long cum = 0;
        int median = 0;
        for (int i = 0; i < hist.Length; i++) {
            cum += hist[i];
            if (cum > half) { median = i; break; }
        }
        // MAD via a second histogram of |v − median|.
        var devHist = new int[65536];
        for (int i = 0; i < data.Length; i++) {
            int d = Math.Abs(data[i] - median);
            if (d < devHist.Length) devHist[d]++;
        }
        cum = 0;
        int mad = 0;
        for (int i = 0; i < devHist.Length; i++) {
            cum += devHist[i];
            if (cum > half) { mad = i; break; }
        }
        return ImageStatistics.ComputeBackgroundSnr(data, median, mad);
    }

    private double ComputeCumulativeSnrFromAccumulator() {
        // Reconstruct the current running-mean stack on the fly from
        // _stackBuffer / _countBuffer (same math as GetStackedResult
        // but inlined so we don't allocate an extra ushort[]). For
        // small / medium frames this is ~40 ms on a Pi 4 — runs
        // once per integration which is well within budget.
        lock (_lock) {
            if (_stackBuffer == null || _countBuffer == null) return 0;
            var n = _stackBuffer.Length;
            // Build the stacked ushort[] view, then drop it into the
            // same background SNR computation we use per-frame so the
            // two numbers are directly comparable.
            var stacked = new ushort[n];
            for (int i = 0; i < n; i++) {
                if (_countBuffer[i] > 0)
                    stacked[i] = (ushort)Math.Clamp(_stackBuffer[i] / _countBuffer[i], 0, 65535);
            }
            return ComputeFrameSnr(stacked);
        }
    }

    private void RecordSnrSample(int frame, double snr) {
        if (frame <= 0 || !double.IsFinite(snr) || snr < 0) return;
        // Deduplicate identical frame numbers (defensive — shouldn't
        // happen but a duplicate WS message from the WASM client
        // could in theory arrive).
        if (_snrHistory.Count > 0 && _snrHistory[_snrHistory.Count - 1].frame == frame) {
            _snrHistory[_snrHistory.Count - 1] = (frame, snr);
            return;
        }
        _snrHistory.Add((frame, snr));
        if (_snrHistory.Count > 50) _snrHistory.RemoveAt(0);
    }

    private void RecomputeEta() {
        if (!TargetSnr.HasValue) { LastEta = null; return; }
        LastEta = SnrEtaCalculator.Estimate(_snrHistory, TargetSnr.Value, AverageExposureSec);
    }

    public ushort[] GetStackedResult() {
        lock (_lock) {
            if (_stackBuffer == null) return [];

            var result = new ushort[_stackBuffer.Length];
            for (int i = 0; i < _stackBuffer.Length; i++) {
                if (_countBuffer![i] > 0) {
                    result[i] = (ushort)Math.Clamp(_stackBuffer[i] / _countBuffer[i], 0, 65535);
                }
            }
            return result;
        }
    }

    public StackStatus GetStatus() {
        return new StackStatus {
            IsRunning = _isRunning,
            FrameCount = _frameCount,
            Width = _width,
            Height = _height,
            ReferenceStarCount = _referenceStars?.Count ?? 0,
            Mode = Mode.ToString().ToLowerInvariant(),
            SaveFramesToDisk = SaveFramesToDisk,
            FramesSavedToDisk = _framesSavedToDisk,
            MaxDurationSeconds = MaxDurationSeconds,
            StartedAt = _startedAt,
            ElapsedSeconds = ElapsedSeconds,
            DurationCapReached = DurationCapReached,
            // SNR-4 surface for the WS broadcaster. EtaSeconds /
            // EtaFrames are null when SnrEtaCalculator returned null
            // (low confidence / no target set / target already met).
            LastFrameSnr = LastFrameSnr,
            CumulativeSnr = CumulativeSnr,
            TargetSnr = TargetSnr,
            EtaFrames = LastEta?.RemainingFrames,
            EtaSeconds = LastEta?.RemainingSeconds,
            EtaConfidence = LastEta?.Confidence
        };
    }

    public class StackStatus {
        public bool IsRunning { get; set; }
        public int FrameCount { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int ReferenceStarCount { get; set; }
        /// <summary>"full" or "metricsonly". UI uses this for the
        /// compute-location chip + the "Save current stack" button
        /// gating (only meaningful when a WASM client is actually
        /// doing the accumulation).</summary>
        public string Mode { get; set; } = "full";
        /// <summary>Mirrors <see cref="LiveStackingService.SaveFramesToDisk"/>
        /// so the UI checkbox reflects the live state across
        /// browser tabs (it is also persisted to the user profile
        /// in <see cref="LiveStackEndpoints"/>).</summary>
        public bool SaveFramesToDisk { get; set; }
        /// <summary>How many raw frames landed in lights/ during the
        /// current session. Shown next to the toggle as live
        /// confirmation that the writes are actually working.</summary>
        public int FramesSavedToDisk { get; set; }
        /// <summary>Per-stack auto-pause cap, seconds. 0 = unlimited
        /// (default). Persisted per-rig.</summary>
        public int MaxDurationSeconds { get; set; }
        /// <summary>UTC timestamp of the first frame in the current
        /// stack, or null when no frames have been integrated yet.</summary>
        public DateTime? StartedAt { get; set; }
        /// <summary>Seconds elapsed since StartedAt. 0 when null.
        /// Snapshot at the moment GetStatus was called; the UI
        /// re-renders it on every status broadcast (~1 Hz).</summary>
        public double ElapsedSeconds { get; set; }
        /// <summary>True when MaxDurationSeconds > 0 and elapsed
        /// crossed it. UI surfaces a "Stack complete" badge and
        /// stops the spinning indicator.</summary>
        public bool DurationCapReached { get; set; }
        // SNR-4: SNR + ETA payload. nullable on ETA fields because
        // SnrEtaCalculator returns null when the fit confidence is
        // below threshold or the target isn't configured.
        public double LastFrameSnr { get; set; }
        public double CumulativeSnr { get; set; }
        public double? TargetSnr { get; set; }
        public int? EtaFrames { get; set; }
        public double? EtaSeconds { get; set; }
        public double? EtaConfidence { get; set; }
    }
}
