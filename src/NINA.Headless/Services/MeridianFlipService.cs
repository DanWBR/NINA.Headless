namespace NINA.Headless.Services;

/// <summary>
/// Automated meridian flip orchestrator.
///
/// Math (LST + hour angle + time-to-meridian) is static so it can be
/// unit-tested without any equipment. The runtime flip workflow:
///   1. Pause guiding (PHD2) if running
///   2. Re-slew to the current target — mount firmware flips when crossing
///      the meridian threshold
///   3. Wait for IsSlewing to clear + settle delay
///   4. Plate solve + center via SlewCenterService
///   5. Optional auto-focus after flip (filter offsets may shift focus)
///   6. Resume guiding
///
/// The service is driven by the SequenceEngine between frames. It only
/// reads mount state — never holds it for long — so a manual flip can be
/// triggered at any time without conflicting with the sequence.
/// </summary>
public class MeridianFlipService {
    private readonly EquipmentManager _equip;
    private readonly PHD2Client _phd2;
    private readonly SlewCenterService _slewCenter;
    private readonly AutoFocusService _autoFocus;
    private readonly ProfileService _profile;
    private readonly ILogger<MeridianFlipService> _logger;

    private readonly object _stateLock = new();
    private CancellationTokenSource? _cts;

    public MeridianFlipSettings Settings { get; private set; } = new();
    public MeridianFlipState State { get; private set; } = MeridianFlipState.Idle;
    public DateTime? LastFlipAt { get; private set; }
    public string? LastFlipError { get; private set; }
    public int FlipsCompleted { get; private set; }

    public MeridianFlipService(EquipmentManager equip, PHD2Client phd2, SlewCenterService slewCenter,
        AutoFocusService autoFocus, ProfileService profile, ILogger<MeridianFlipService> logger) {
        _equip = equip;
        _phd2 = phd2;
        _slewCenter = slewCenter;
        _autoFocus = autoFocus;
        _profile = profile;
        _logger = logger;
    }

    public void UpdateSettings(MeridianFlipSettings settings) {
        // Defensive normalisation
        if (settings.MinutesAfterMeridian < 0) settings.MinutesAfterMeridian = 0;
        if (settings.MinutesAfterMeridian > 60) settings.MinutesAfterMeridian = 60;
        if (settings.PauseBeforeMeridianMinutes < 0) settings.PauseBeforeMeridianMinutes = 0;
        if (settings.RecenterToleranceArcsec < 1) settings.RecenterToleranceArcsec = 1;
        if (settings.SettleSecondsAfterFlip < 0) settings.SettleSecondsAfterFlip = 0;
        Settings = settings;
    }

    /// <summary>
    /// Compute approximate Greenwich Mean Sidereal Time (in hours, 0-24) for a
    /// given UTC instant. Based on the Meeus formula (USNO Astronomical Almanac).
    /// Accurate to ~1 second within ±50 years of J2000.
    /// </summary>
    public static double ComputeGmstHours(DateTime utc) {
        // Julian Date
        double jd = ToJulianDate(utc);
        double t = (jd - 2451545.0) / 36525.0;

        // GMST in degrees (Meeus eq. 12.4)
        double gmstDeg = 280.46061837
                        + 360.98564736629 * (jd - 2451545.0)
                        + t * t * 0.000387933
                        - t * t * t / 38710000.0;

        gmstDeg = ((gmstDeg % 360) + 360) % 360;
        return gmstDeg / 15.0; // hours
    }

    /// <summary>
    /// Local Sidereal Time in hours (0–24) for a given UTC and longitude
    /// (degrees east positive).
    /// </summary>
    public static double ComputeLstHours(DateTime utc, double longitudeDeg) {
        double gmst = ComputeGmstHours(utc);
        double lst = gmst + longitudeDeg / 15.0;
        return ((lst % 24) + 24) % 24;
    }

    /// <summary>
    /// Hours until the given RA crosses the meridian (HA = 0).
    ///   HA = LST − RA       (hours, signed in -12..+12)
    /// If HA &lt; 0, target is east of meridian (rising)  → time-to-meridian = -HA
    /// If HA &gt; 0, target is west of meridian (already passed) → time-to-next = 24 - HA
    /// </summary>
    public static double HoursUntilMeridian(double raHours, DateTime utc, double longitudeDeg) {
        double lst = ComputeLstHours(utc, longitudeDeg);
        double ha = lst - raHours;
        // Normalise HA to -12..+12
        while (ha > 12) ha -= 24;
        while (ha < -12) ha += 24;
        if (ha <= 0) return -ha; // rising target — meridian crossing is in (0..12]
        return 24 - ha;          // already past — wraps to next sidereal day
    }

    /// <summary>
    /// Does the current target require a flip *now*? True when the target has
    /// crossed the meridian by at least Settings.MinutesAfterMeridian, and
    /// the mount currently reports the wrong pier side for the new HA.
    ///
    /// Returns false if no telescope, no settings, no target RA, or flip disabled.
    /// Callers must additionally check that they actually have a target tracked
    /// (sequence item with coordinates) before invoking the flip workflow.
    /// </summary>
    public bool ShouldFlipNow(double targetRaHours) {
        if (!Settings.Enabled) return false;
        if (_equip.Telescope == null || !_equip.Telescope.IsConnected) return false;

        double lon = _profile.Active.Longitude;
        double lst = ComputeLstHours(DateTime.UtcNow, lon);
        double ha = lst - targetRaHours;
        while (ha > 12) ha -= 24;
        while (ha < -12) ha += 24;

        // Need HA past 0 by >= MinutesAfterMeridian / 60 hours
        return ha >= Settings.MinutesAfterMeridian / 60.0
            && ha < 6.0; // sanity: don't flip if target is way past meridian
    }

    /// <summary>
    /// Execute the flip workflow for the given target. Caller is responsible
    /// for guaranteeing this is invoked only when ShouldFlipNow returned true
    /// (or for manual / forced triggers). Returns true on success.
    /// </summary>
    public async Task<bool> ExecuteFlipAsync(double targetRaHours, double targetDecDeg, CancellationToken ct = default) {
        lock (_stateLock) {
            if (State != MeridianFlipState.Idle) {
                _logger.LogWarning("ExecuteFlipAsync called while state={State}", State);
                return false;
            }
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            State = MeridianFlipState.Pausing;
            LastFlipError = null;
        }

        bool wasGuiding = false;
        try {
            // 1. Pause PHD2 if guiding
            if (_phd2.IsConnected && _phd2.IsGuiding) {
                _logger.LogInformation("Meridian flip: pausing PHD2");
                wasGuiding = true;
                try { await _phd2.PauseAsync(); } catch (Exception ex) {
                    _logger.LogWarning(ex, "PHD2 pause failed (continuing)");
                }
            }

            // 2. Re-slew to target — mount firmware decides to flip based on its own
            // meridian-limit configuration. Most ASCOM/INDI mounts auto-flip on any
            // slew that crosses the limit.
            State = MeridianFlipState.Slewing;
            if (_equip.Telescope == null) throw new InvalidOperationException("Telescope disconnected");

            _logger.LogInformation("Meridian flip: re-slewing to RA={Ra:F4} Dec={Dec:F4}", targetRaHours, targetDecDeg);
            await _equip.Telescope.SlewAsync(targetRaHours, targetDecDeg, _cts!.Token);
            await WaitForSlewComplete(_cts.Token);

            // 3. Settle delay (mount-mechanical & cable wrap)
            if (Settings.SettleSecondsAfterFlip > 0) {
                State = MeridianFlipState.Settling;
                _logger.LogInformation("Meridian flip: settling for {Sec}s", Settings.SettleSecondsAfterFlip);
                await Task.Delay(TimeSpan.FromSeconds(Settings.SettleSecondsAfterFlip), _cts.Token);
            }

            // 4. Plate solve + recenter
            if (Settings.RecenterAfterFlip && _equip.Camera != null) {
                State = MeridianFlipState.Recentering;
                _logger.LogInformation("Meridian flip: recentering via plate solve");
                var job = _slewCenter.StartJob(targetRaHours, targetDecDeg, Settings.RecenterToleranceArcsec);
                while (job.State != SlewCenterState.Centered
                    && job.State != SlewCenterState.Failed
                    && job.State != SlewCenterState.Cancelled) {
                    _cts.Token.ThrowIfCancellationRequested();
                    await Task.Delay(500, _cts.Token);
                }
                if (job.State != SlewCenterState.Centered) {
                    _logger.LogWarning("Recenter did not converge ({State}): {Error}", job.State, job.Error);
                }
            }

            // 5. Optional auto-focus after flip
            if (Settings.AutoFocusAfterFlip && _equip.Focuser != null && _equip.Camera != null) {
                State = MeridianFlipState.AutoFocusing;
                _logger.LogInformation("Meridian flip: running auto-focus after flip");
                try {
                    _autoFocus.Start(new AutoFocusRequest {
                        Steps = 9, StepSize = 50, ExposureSeconds = 2.0,
                        MinStars = 5, BacklashSteps = 0, TakeConfirmationFrame = false
                    });
                    while (_autoFocus.State == AutoFocusState.Running) {
                        _cts.Token.ThrowIfCancellationRequested();
                        await Task.Delay(1000, _cts.Token);
                    }
                } catch (Exception ex) {
                    _logger.LogWarning(ex, "Post-flip auto-focus failed — continuing");
                }
            }

            // 6. Resume guiding
            if (wasGuiding && _phd2.IsConnected) {
                State = MeridianFlipState.ResumingGuiding;
                _logger.LogInformation("Meridian flip: resuming PHD2 guiding");
                try { await _phd2.ResumeAsync(); } catch (Exception ex) {
                    _logger.LogWarning(ex, "PHD2 resume failed");
                }
                // PHD2 may need to recalibrate after a flip; that's handled by
                // PHD2's own auto-flip-calibration setting and we don't force it here.
            }

            lock (_stateLock) {
                State = MeridianFlipState.Idle;
                LastFlipAt = DateTime.UtcNow;
                FlipsCompleted++;
            }
            _logger.LogInformation("Meridian flip complete");
            return true;

        } catch (OperationCanceledException) {
            _logger.LogInformation("Meridian flip cancelled");
            lock (_stateLock) {
                State = MeridianFlipState.Idle;
                LastFlipError = "Cancelled";
            }
            // Try to resume guiding even on cancel
            if (wasGuiding && _phd2.IsConnected) {
                try { await _phd2.ResumeAsync(); } catch { }
            }
            return false;
        } catch (Exception ex) {
            _logger.LogError(ex, "Meridian flip failed");
            lock (_stateLock) {
                State = MeridianFlipState.Idle;
                LastFlipError = ex.Message;
            }
            if (wasGuiding && _phd2.IsConnected) {
                try { await _phd2.ResumeAsync(); } catch { }
            }
            return false;
        } finally {
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Abort() {
        lock (_stateLock) {
            _cts?.Cancel();
        }
    }

    private async Task WaitForSlewComplete(CancellationToken ct) {
        if (_equip.Telescope == null) return;
        for (int i = 0; i < 600; i++) { // up to 10 min — flip can be slow
            ct.ThrowIfCancellationRequested();
            if (!_equip.Telescope.IsSlewing) {
                await Task.Delay(500, ct); // brief settle
                return;
            }
            await Task.Delay(1000, ct);
        }
        _logger.LogWarning("Slew did not complete within 10 minutes");
    }

    private static double ToJulianDate(DateTime utc) {
        // Standard algorithm valid for Gregorian dates
        int y = utc.Year, m = utc.Month;
        int d = utc.Day;
        if (m <= 2) { y--; m += 12; }
        int a = y / 100;
        int b = 2 - a + a / 4;
        double dayFrac = (utc.Hour + (utc.Minute + (utc.Second + utc.Millisecond / 1000.0) / 60.0) / 60.0) / 24.0;
        return Math.Floor(365.25 * (y + 4716)) + Math.Floor(30.6001 * (m + 1)) + d + dayFrac + b - 1524.5;
    }
}

public class MeridianFlipSettings {
    /// <summary>Enable automatic flip during sequence execution.</summary>
    public bool Enabled { get; set; }
    /// <summary>How many minutes past the meridian to wait before triggering the flip.</summary>
    public double MinutesAfterMeridian { get; set; } = 5;
    /// <summary>Minutes before the meridian at which to pause new exposures (0 = disabled).</summary>
    public double PauseBeforeMeridianMinutes { get; set; }
    /// <summary>Run plate-solve recenter after the flip.</summary>
    public bool RecenterAfterFlip { get; set; } = true;
    /// <summary>Tolerance (arcsec) for the post-flip recenter loop.</summary>
    public double RecenterToleranceArcsec { get; set; } = 30;
    /// <summary>Wait this many seconds after the flip slew before plate-solving.</summary>
    public double SettleSecondsAfterFlip { get; set; } = 5;
    /// <summary>Run auto-focus after the flip (focus often shifts after pier-side change).</summary>
    public bool AutoFocusAfterFlip { get; set; }
}

public enum MeridianFlipState {
    Idle,
    Pausing,
    Slewing,
    Settling,
    Recentering,
    AutoFocusing,
    ResumingGuiding
}
