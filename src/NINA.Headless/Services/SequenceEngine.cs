using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Headless.Services;

public class SequenceEngine {
    private readonly EquipmentManager _equip;
    private readonly ImageRelayService _relay;
    private readonly LiveStackingService _liveStack;
    private readonly PHD2Client _phd2;
    private readonly MeridianFlipService _meridianFlip;
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

    public SequenceEngine(EquipmentManager equip, ImageRelayService relay,
        LiveStackingService liveStack, PHD2Client phd2, MeridianFlipService meridianFlip,
        ILogger<SequenceEngine> logger) {
        _equip = equip;
        _relay = relay;
        _liveStack = liveStack;
        _phd2 = phd2;
        _meridianFlip = meridianFlip;
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
            Dither = Dither
        };
    }

    private async Task RunAsync(CancellationToken ct) {
        try {
            for (int i = Math.Max(0, CurrentItemIndex); i < Items.Count; i++) {
                ct.ThrowIfCancellationRequested();
                CurrentItemIndex = i;
                var item = Items[i];

                _logger.LogInformation("Sequence item {Index}/{Total}: {Name} ({Exposure}s x {Count})",
                    i + 1, Items.Count, item.Name, item.Exposure, item.Count);

                // Slew to target if coordinates provided
                if (item.Ra.HasValue && item.Dec.HasValue && _equip.Telescope != null) {
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

                // Capture frames
                int startFrame = (i == Math.Max(0, CurrentItemIndex)) ? CurrentFrameInItem : 0;
                for (int f = startFrame; f < item.Count; f++) {
                    ct.ThrowIfCancellationRequested();

                    // Check pause gate
                    await _pauseGate.WaitAsync(ct);
                    _pauseGate.Release();

                    // Meridian flip check (only if target has coordinates)
                    if (item.Ra.HasValue && item.Dec.HasValue
                        && _meridianFlip.Settings.Enabled
                        && _meridianFlip.ShouldFlipNow(item.Ra.Value)) {
                        _logger.LogInformation("Meridian flip due for target {Name} — executing", item.Name);
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

                    // Dither between frames (only after a successful capture and only if
                    // this isn't the very last frame of the very last item).
                    if (frameOk) {
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

        } catch (OperationCanceledException) {
            _logger.LogInformation("Sequence cancelled");
        } catch (Exception ex) {
            LastError = ex.Message;
            State = SequenceState.Idle;
            _logger.LogError(ex, "Sequence failed");
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
    /// Silently skips when conditions aren't met — never aborts the sequence.
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
                _logger.LogWarning("Dither settle timed out after {Sec}s — continuing sequence anyway",
                    Dither.SettleTimeout);
            }
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Dither command failed — continuing sequence without dither");
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
