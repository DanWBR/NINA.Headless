namespace NINA.Polaris.Services;

/// <summary>
/// Watches the live-stack frame stream and, when auto-refocus is OFF,
/// raises a "refocus suggested" advisory based on the rolling HFR
/// trend instead of a fixed threshold. The user can refocus by hand
/// (manual focuser) or trigger AF manually, then dismiss the chip.
///
/// Why this exists alongside <see cref="LiveStackTriggersService"/>:
/// LSTR-3 auto-fires the AF service when the user has wired a motor
/// AND enabled RefocusEnabled in the rig. That covers the "I trust
/// the motor" path. This service covers the "I have a manual focuser
/// (or have RefocusEnabled = false)" path, where Polaris cannot move
/// the focuser itself but can still tell the user when stars start
/// trending bad. Pure observational, no actuation.
///
/// Detection is by trend, not by threshold. We keep the last N HFR
/// samples (rolling window), compute the linear-regression slope vs
/// frame index, and fire only when the slope is positive AND the
/// rolling mean is meaningfully above the baseline (5th percentile
/// of the last 20 valid samples). This filters out seeing-driven
/// jitter (oscillates around a mean) while catching systematic drift
/// (trends in one direction).
///
/// Secondary signal: a >30% drop in detected star count fires
/// regardless of HFR. When focus drifts very far, the dimmer stars
/// dim out entirely and HFR is computed on a shrinking subset of
/// brighter stars, so HFR can look misleadingly stable while the
/// image visibly degrades.
///
/// Frame handler is awaited sequentially inside
/// <see cref="LiveStackingService.AddFrameAsync"/>, but the work
/// done here is microseconds (no I/O, no plate solve) so it doesn't
/// throttle the capture loop the way LSTR-3 can.
/// </summary>
public class RefocusSuggestionService : IDisposable {
    // --- Tunables. Constants because the user explicitly asked for
    //     automatic detection (no profile fields). Tweak from one
    //     place if real-world experience shows they need adjustment.

    /// <summary>Need this many valid samples before any evaluation
    /// happens. 10 is the minimum for a stable linear regression;
    /// 15 gives room for 1-2 bad samples without poisoning the slope.</summary>
    private const int WarmupSamples = 15;

    /// <summary>Total rolling window kept in memory. Bigger than the
    /// trend window so the baseline (5th percentile) has more data
    /// to draw from.</summary>
    private const int WindowSize = 30;

    /// <summary>Number of most recent samples used for the linear-regression
    /// slope test.</summary>
    private const int TrendSamples = 10;

    /// <summary>Last-N HFR average for the rolling-mean check.</summary>
    private const int RollingMeanSamples = 5;

    /// <summary>Last-N HFR average used for the auto-dismiss check.</summary>
    private const int RecoveryWindowSamples = 5;

    /// <summary>Baseline = Nth-percentile of last <see cref="BaselineWindow"/>
    /// samples. 5th percentile picks the best stable HFR while ignoring
    /// transient seeing spikes downward (rare but possible).</summary>
    private const int BaselinePercentile = 5;
    private const int BaselineWindow = 20;

    /// <summary>Rolling mean must exceed baseline by this fraction to
    /// even consider firing. 0.15 = 15% degraded.</summary>
    private const double FireRollingMeanRatio = 1.15;

    /// <summary>Slope test: extrapolated 5-frame change must exceed
    /// this fraction of baseline. Filters out gentle drift from real
    /// degradation.</summary>
    private const double FireExtrapolatedSlopeRatio = 0.30;

    /// <summary>Star count crash trigger: secondary signal. 0.70 = a
    /// 30% drop in star count vs baseline fires on its own.</summary>
    private const double StarCountCrashRatio = 0.70;

    /// <summary>Auto-clear when rolling mean falls back to within
    /// this fraction of baseline.</summary>
    private const double RecoveryRollingMeanRatio = 1.05;

    /// <summary>Need this many consecutive recovered samples before
    /// clearing. Avoids a single good frame yanking the chip away
    /// while the user is still working on focus.</summary>
    private const int RecoveryConsecutiveFrames = 3;

    /// <summary>Drop samples whose HFR is &lt;= this. Zero means star
    /// detector reported "no measurable HFR" for this frame.</summary>
    private const double MinValidHfr = 0.01;

    /// <summary>Drop samples with fewer detected stars than this.
    /// Clouds, satellite trails, alignment misses produce garbage
    /// samples that would poison the regression.</summary>
    private const int MinValidStarCount = 5;

    /// <summary>Toast cooldown. Multiple consecutive trigger evaluations
    /// during one suggestion-cycle should not spam the user.</summary>
    private static readonly TimeSpan ToastCooldown = TimeSpan.FromSeconds(60);

    // --- Dependencies + subscription

    private readonly LiveStackingService _stack;
    private readonly ProfileService _profiles;
    private readonly NotificationService _notifications;
    private readonly ILogger<RefocusSuggestionService> _logger;
    private readonly IDisposable _frameSub;
    private readonly object _lock = new();

    // --- Rolling sample state

    private readonly Queue<HfrSample> _samples = new();
    private double _baselineHfr;
    private double _baselineStarCount;
    private int _consecutiveRecoveredSamples;
    private DateTime _lastToastAt = DateTime.MinValue;

    // --- Current advisory state (snapshot in RefocusSuggestionStatus)

    private bool _suggesting;
    private string? _reason;
    private double _currentHfr;
    private double _slopePerFrame;
    private int _framesSinceBaseline;
    private DateTime? _suggestedAt;

    public RefocusSuggestionService(LiveStackingService stack,
                                    ProfileService profiles,
                                    NotificationService notifications,
                                    ILogger<RefocusSuggestionService> logger) {
        _stack = stack;
        _profiles = profiles;
        _notifications = notifications;
        _logger = logger;
        _frameSub = _stack.SubscribeFrameIntegrated(OnFrameIntegratedAsync);
        // Rig switch invalidates everything: different scope, different
        // focuser, different "good" focus point.
        _profiles.EquipmentProfileActivated += _ => Reset();
    }

    public RefocusSuggestionStatus CurrentStatus {
        get {
            lock (_lock) {
                return new RefocusSuggestionStatus {
                    Suggesting = _suggesting,
                    Reason = _reason,
                    BaselineHfr = SafeDouble(_baselineHfr),
                    BaselineStarCount = _baselineStarCount,
                    CurrentHfr = SafeDouble(_currentHfr),
                    SlopePerFrame = SafeDouble(_slopePerFrame),
                    FramesSinceBaseline = _framesSinceBaseline,
                    SampleCount = _samples.Count,
                    SuggestedAt = _suggestedAt
                };
            }
        }
    }

    public event Action<RefocusSuggestionStatus>? StatusChanged;

    /// <summary>Dismiss the advisory. <paramref name="resetBaseline"/> = true
    /// means "I just refocused", replace the baseline with the rolling
    /// mean so the next trigger evaluation uses the new 'good' as
    /// reference. false means "stop showing this", keep the existing
    /// baseline (rarely useful, but kept symmetric).</summary>
    public void Dismiss(bool resetBaseline) {
        lock (_lock) {
            if (resetBaseline && _samples.Count > 0) {
                // Use the rolling mean as the new baseline. The user
                // just refocused so the current HFR IS the new good.
                _baselineHfr = RollingMean(_samples, RollingMeanSamples);
                _baselineStarCount = RollingMeanStars(_samples, RollingMeanSamples);
                _samples.Clear();   // start fresh from the new good
            }
            _suggesting = false;
            _reason = null;
            _suggestedAt = null;
            _consecutiveRecoveredSamples = 0;
            _lastToastAt = DateTime.MinValue;
        }
        Notify();
    }

    /// <summary>Full state reset. Used on rig activation, on
    /// <see cref="LiveStackingService"/> reset (via the public
    /// /api/livestack/reset endpoint chain).</summary>
    public void Reset() {
        lock (_lock) {
            _samples.Clear();
            _baselineHfr = 0;
            _baselineStarCount = 0;
            _consecutiveRecoveredSamples = 0;
            _suggesting = false;
            _reason = null;
            _currentHfr = 0;
            _slopePerFrame = 0;
            _framesSinceBaseline = 0;
            _suggestedAt = null;
            _lastToastAt = DateTime.MinValue;
        }
        Notify();
    }

    // --- Frame handler

    private Task OnFrameIntegratedAsync(LiveStackFrameInfo info) {
        try {
            EvaluateFrame(info);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "RefocusSuggestion: frame evaluation crashed (non-fatal)");
        }
        return Task.CompletedTask;
    }

    /// <summary>Test surface. Drives one synthetic frame through the
    /// evaluation pipeline without going through LiveStackingService
    /// (which would require constructing IImageData with real stars).
    /// Same code path as <see cref="OnFrameIntegratedAsync"/>.</summary>
    public void InjectFrameForTest(int frameCount, double medianHfr, int starCount, DateTime at) {
        EvaluateFrame(new LiveStackFrameInfo(frameCount, null!, medianHfr, starCount, at));
    }

    private void EvaluateFrame(LiveStackFrameInfo info) {
        // First frame of a stack: clear state. LiveStackingService
        // increments _frameCount to 1 on the first integrated frame
        // (see AddFrameAsync), so frame 1 marks a fresh session.
        if (info.FrameCount == 1) {
            Reset();
        }

        // Skip when LSTR-3 already covers refocus, would duplicate
        // the advisory layer.
        var cfg = _profiles.ActiveEquipmentProfile.LiveStackTriggers;
        if (cfg.RefocusEnabled) {
            // While auto-fire is on the suggestion never applies, also
            // clear any stale chip from before the user enabled it.
            if (_suggesting) {
                lock (_lock) {
                    _suggesting = false;
                    _reason = null;
                    _suggestedAt = null;
                }
                Notify();
            }
            return;
        }

        // Drop garbage samples
        if (info.MedianHfr <= MinValidHfr) return;
        if (info.StarCount < MinValidStarCount) return;

        var sample = new HfrSample(info.FrameCount, info.MedianHfr, info.StarCount, info.At);
        bool stateChanged;
        bool fireToast = false;
        string? toastReason = null;

        lock (_lock) {
            _samples.Enqueue(sample);
            while (_samples.Count > WindowSize) _samples.Dequeue();

            _currentHfr = info.MedianHfr;
            _framesSinceBaseline = _samples.Count;

            // Warmup gate. No evaluation, just buffer samples.
            if (_samples.Count < WarmupSamples) {
                stateChanged = false;
            } else {
                // Establish baseline if we don't have one yet (first
                // evaluation after warmup).
                if (_baselineHfr <= 0) {
                    _baselineHfr = PercentileHfr(_samples, BaselinePercentile, BaselineWindow);
                    _baselineStarCount = MedianStars(_samples, BaselineWindow);
                }

                stateChanged = EvaluateTriggers(out fireToast, out toastReason);
            }
        }

        if (stateChanged) {
            Notify();
            if (fireToast && toastReason != null) {
                _notifications.Push("warn",
                    $"Refocus suggested: {toastReason}. Open FOCUS to refocus manually.",
                    ttlMs: 8000);
            }
        }
    }

    /// <summary>Returns true when anything observable changed
    /// (suggesting state, reason, baseline, slope). Called under
    /// <see cref="_lock"/>.</summary>
    private bool EvaluateTriggers(out bool fireToast, out string? toastReason) {
        fireToast = false;
        toastReason = null;

        var arr = _samples.ToArray();

        // Linear regression of HFR vs frame index on the last
        // TrendSamples points. Slope > 0 = HFR rising = focus
        // degrading. Slope < 0 = HFR falling = focus recovering.
        var slope = LinearSlope(arr, TrendSamples);
        _slopePerFrame = slope;
        var rollingMean = RollingMean(_samples, RollingMeanSamples);
        var recoveryMean = RollingMean(_samples, RecoveryWindowSamples);
        var rollingStars = RollingMeanStars(_samples, RollingMeanSamples);

        // ---- Auto-dismiss path: if we WERE suggesting and BOTH the
        //      HFR rolling mean AND the star-count rolling mean are
        //      back to within tolerance of baseline for several
        //      consecutive samples, clear it. Requiring both avoids
        //      the failure mode where a star-count-only trigger fires
        //      then immediately auto-clears because HFR never moved.
        if (_suggesting) {
            bool hfrRecovered = recoveryMean <= _baselineHfr * RecoveryRollingMeanRatio;
            bool starsRecovered = _baselineStarCount <= 0
                || rollingStars >= _baselineStarCount * StarCountCrashRatio;
            if (hfrRecovered && starsRecovered) {
                _consecutiveRecoveredSamples++;
                if (_consecutiveRecoveredSamples >= RecoveryConsecutiveFrames) {
                    _suggesting = false;
                    _reason = null;
                    _suggestedAt = null;
                    _consecutiveRecoveredSamples = 0;
                    _lastToastAt = DateTime.MinValue;
                    _logger.LogInformation(
                        "RefocusSuggestion: auto-cleared (HFR recovered to {Cur:F2} vs baseline {Base:F2}, stars to {Stars:F0})",
                        recoveryMean, _baselineHfr, rollingStars);
                    return true;
                }
                return true;   // state changed (consecutive counter)
            } else {
                _consecutiveRecoveredSamples = 0;
            }
            // Still suggesting, update slope + currentHfr but no fire
            return true;
        }

        // ---- Primary trigger: HFR trend + magnitude
        var hfrRising = slope > 0;
        var meanAboveBaseline = rollingMean > _baselineHfr * FireRollingMeanRatio;
        var extrapolatedMeaningful = slope * 5 > FireExtrapolatedSlopeRatio * _baselineHfr;

        if (hfrRising && meanAboveBaseline && extrapolatedMeaningful) {
            var pctOver = (rollingMean / _baselineHfr - 1.0) * 100.0;
            var reason = $"HFR rising {pctOver:F0}% over {TrendSamples} frames";

            // Boost confidence text when stars are also dimming
            if (_baselineStarCount > 0 && rollingStars < _baselineStarCount * StarCountCrashRatio) {
                reason = $"HFR rising {pctOver:F0}% AND star count dropping";
            }

            return RaiseSuggestion(reason, out fireToast, out toastReason);
        }

        // ---- Secondary trigger: star count crash without HFR trigger.
        //      Covers very-out-of-focus where HFR computes on a
        //      shrinking set of bright stars and looks stable.
        if (_baselineStarCount > 0 && rollingStars < _baselineStarCount * StarCountCrashRatio) {
            var pctDrop = (1.0 - rollingStars / _baselineStarCount) * 100.0;
            var reason = $"Star count dropped {pctDrop:F0}% (focus or transparency)";
            return RaiseSuggestion(reason, out fireToast, out toastReason);
        }

        return false;
    }

    private bool RaiseSuggestion(string reason, out bool fireToast, out string? toastReason) {
        _suggesting = true;
        _reason = reason;
        _suggestedAt = DateTime.UtcNow;
        _consecutiveRecoveredSamples = 0;
        // Toast cooldown: only one per ToastCooldown window even if
        // the suggestion state flickers (it shouldn't because we keep
        // _suggesting=true, but defence in depth).
        var now = DateTime.UtcNow;
        if (now - _lastToastAt >= ToastCooldown) {
            _lastToastAt = now;
            fireToast = true;
            toastReason = reason;
        } else {
            fireToast = false;
            toastReason = null;
        }
        _logger.LogInformation("RefocusSuggestion: firing — {Reason}", reason);
        return true;
    }

    private void Notify() {
        try { StatusChanged?.Invoke(CurrentStatus); }
        catch (Exception ex) { _logger.LogDebug(ex, "StatusChanged handler threw"); }
    }

    public void Dispose() { _frameSub.Dispose(); }

    // ---------- Pure helpers (static, easy to unit-test) ----------

    /// <summary>Linear regression slope of HFR vs (zero-indexed) sample
    /// position. Uses only the last <paramref name="window"/> samples.
    /// Returns 0 when there are fewer than 2 samples in window.</summary>
    public static double LinearSlope(IReadOnlyList<HfrSample> samples, int window) {
        var n = Math.Min(window, samples.Count);
        if (n < 2) return 0;
        var start = samples.Count - n;

        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        for (int i = 0; i < n; i++) {
            double x = i;
            double y = samples[start + i].Hfr;
            sumX += x;
            sumY += y;
            sumXY += x * y;
            sumXX += x * x;
        }
        double denom = n * sumXX - sumX * sumX;
        if (Math.Abs(denom) < 1e-12) return 0;
        return (n * sumXY - sumX * sumY) / denom;
    }

    /// <summary>Mean HFR of the last <paramref name="n"/> samples.
    /// Caller must hold the lock (the queue isn't thread-safe).</summary>
    public static double RollingMean(IEnumerable<HfrSample> samples, int n) {
        var arr = samples.ToArray();
        var k = Math.Min(n, arr.Length);
        if (k == 0) return 0;
        double sum = 0;
        for (int i = arr.Length - k; i < arr.Length; i++) sum += arr[i].Hfr;
        return sum / k;
    }

    /// <summary>Mean star count of the last <paramref name="n"/> samples.</summary>
    public static double RollingMeanStars(IEnumerable<HfrSample> samples, int n) {
        var arr = samples.ToArray();
        var k = Math.Min(n, arr.Length);
        if (k == 0) return 0;
        double sum = 0;
        for (int i = arr.Length - k; i < arr.Length; i++) sum += arr[i].StarCount;
        return sum / k;
    }

    /// <summary>P-th percentile HFR over the last <paramref name="window"/>
    /// samples. Used to set the baseline (P=5 = "best 5% of recent
    /// HFR, robust to a few bad samples").</summary>
    public static double PercentileHfr(IEnumerable<HfrSample> samples, int percentile, int window) {
        var arr = samples.ToArray();
        var k = Math.Min(window, arr.Length);
        if (k == 0) return 0;
        var slice = new double[k];
        for (int i = 0; i < k; i++) slice[i] = arr[arr.Length - k + i].Hfr;
        Array.Sort(slice);
        // Nearest-rank: ceil(P/100 * N) - 1
        var idx = (int)Math.Ceiling(percentile / 100.0 * k) - 1;
        if (idx < 0) idx = 0;
        if (idx >= k) idx = k - 1;
        return slice[idx];
    }

    /// <summary>Median star count over the last window samples.</summary>
    public static double MedianStars(IEnumerable<HfrSample> samples, int window) {
        var arr = samples.ToArray();
        var k = Math.Min(window, arr.Length);
        if (k == 0) return 0;
        var slice = new int[k];
        for (int i = 0; i < k; i++) slice[i] = arr[arr.Length - k + i].StarCount;
        Array.Sort(slice);
        return slice[k / 2];
    }

    private static double? SafeDouble(double v) {
        if (double.IsNaN(v) || double.IsInfinity(v)) return null;
        if (v == 0) return null;
        return v;
    }
}

public record HfrSample(int FrameCount, double Hfr, int StarCount, DateTime At);

public class RefocusSuggestionStatus {
    /// <summary>True while a suggestion is active.</summary>
    public bool Suggesting { get; init; }
    /// <summary>Human-readable explanation of why the suggestion fired
    /// (e.g. "HFR rising 18% over 10 frames"). Null when not suggesting.</summary>
    public string? Reason { get; init; }
    /// <summary>Best stable HFR seen in the recent window (5th-percentile
    /// over last 20). Null = no baseline yet (warmup not complete).</summary>
    public double? BaselineHfr { get; init; }
    /// <summary>Most recent valid frame's HFR.</summary>
    public double? CurrentHfr { get; init; }
    /// <summary>Linear regression slope (HFR / frame) over the last 10
    /// valid samples. Positive = degrading.</summary>
    public double? SlopePerFrame { get; init; }
    /// <summary>Sample count in the rolling window (== <see cref="SampleCount"/>;
    /// kept for backward compatibility with planned UI).</summary>
    public int FramesSinceBaseline { get; init; }
    /// <summary>Total samples buffered (0..WindowSize=30).</summary>
    public int SampleCount { get; init; }
    /// <summary>When the current suggestion first fired (UTC). Null
    /// when not suggesting.</summary>
    public DateTime? SuggestedAt { get; init; }
    /// <summary>Diagnostic: baseline star count established at the
    /// end of warmup. Used by the secondary trigger
    /// (star-count crash).</summary>
    public double BaselineStarCount { get; init; }
}
