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
    DateTime At);

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

    /// <summary>Where the per-frame math runs. Default <see cref="StackMode.Full"/>.
    /// Switched to <see cref="StackMode.MetricsOnly"/> by the WASM
    /// handshake (CLST-5) when a WASM-capable client is connected and
    /// the active rig hasn't forced server-side.</summary>
    public StackMode Mode { get; set; } = StackMode.Full;

    public LiveStackingService(ImageRelayService relay,
                                ILogger<LiveStackingService> logger,
                                ImageWriterService? writer = null) {
        _relay = relay;
        _writer = writer;
        _logger = logger;
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
            _logger.LogInformation("Live stacking reset");
        }
    }

    /// <summary>Arm stacking (no-op if already armed) AND clear the
    /// current accumulator. Kept for backwards compatibility — the
    /// service is armed by default at startup, callers should
    /// prefer <see cref="Reset"/> when all they want is to clear
    /// the buffer.</summary>
    public void Start() {
        Reset();
        _isRunning = true;
        _logger.LogInformation("Live stacking started");
    }

    /// <summary>Disarm stacking. Frames still flow through the relay
    /// + per-frame save path but no longer update the running mean.
    /// Rare — the typical workflow leaves stacking on permanently
    /// and uses <see cref="Reset"/> to switch targets.</summary>
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

        _logger.LogInformation("Live stack: frame {N} added, {Stars} stars (HFR={Hfr:F2}), mode={Mode}",
            _frameCount, stars.Count, medianHfr, mode);

        // Snapshot handlers + await sequentially. Any handler that
        // throws is logged + swallowed, one bad subscriber can't
        // poison the chain. Slow handlers (AF, recenter) pause the
        // upstream capture loop by extending this await.
        LiveStackFrameHandler[] handlers;
        lock (_handlersLock) handlers = _frameHandlers.ToArray();
        if (handlers.Length > 0) {
            var info = new LiveStackFrameInfo(_frameCount, imageData, medianHfr, stars.Count, DateTime.UtcNow);
            foreach (var h in handlers) {
                try { await h(info); }
                catch (Exception ex) {
                    _logger.LogWarning(ex, "LiveStack frame handler threw (continuing)");
                }
            }
        }
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
            DurationCapReached = DurationCapReached
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
    }
}
