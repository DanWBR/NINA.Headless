namespace NINA.Polaris.Services;

/// <summary>
/// Per-rig auto re-focus + auto re-center policy applied during live
/// stacking. Each trigger threshold = 0 means "disabled" (so the
/// settings record stays a simple flat shape that round-trips cleanly
/// through JSON / EquipmentEndpoints PUT). Multiple triggers per axis
/// (refocus / recenter) are OR'd, first one to cross fires.
///
/// Hosted on <see cref="EquipmentProfile.LiveStackTriggers"/>;
/// <see cref="LiveStackTriggersService"/> reads it at startup + on rig
/// activation (via the <see cref="ProfileService.EquipmentProfileActivated"/>
/// event added in PH2X-2).
/// </summary>
public class LiveStackTriggers {
    // ----- Auto re-focus block -----

    /// <summary>Master switch. When false, every refocus trigger is a no-op.</summary>
    public bool RefocusEnabled { get; set; }

    /// <summary>Trigger refocus when integrated-frame counter has advanced by this much
    /// since the last refocus. 0 = disabled.</summary>
    public int RefocusEveryNFrames { get; set; }

    /// <summary>Trigger refocus when this many minutes have elapsed
    /// since the last refocus (UTC). 0 = disabled.</summary>
    public int RefocusEveryMinutes { get; set; }

    /// <summary>Trigger refocus when |Camera.Temperature - snapshotAtLastRefocus|
    /// crosses this threshold (°C). 0 = disabled. Sensor temperature
    /// for cooled cams; ambient where reported.</summary>
    public double RefocusTempDeltaC { get; set; }

    /// <summary>Trigger refocus when the integrated frame's median HFR
    /// is ≥ this % above the HFR measured immediately after the last
    /// successful AF run. 0 = disabled.</summary>
    public double RefocusHfrIncreasePercent { get; set; }

    /// <summary>Per-AF-run sweep configuration. Reused verbatim when the
    /// orchestrator calls <see cref="AutoFocusService.Start"/>.</summary>
    public AutoFocusRequest RefocusRequest { get; set; } = new() {
        Steps = 9, StepSize = 50, ExposureSeconds = 3, MinStars = 5, BacklashSteps = 0
    };

    // ----- Auto re-center block -----

    public bool RecenterEnabled { get; set; }

    /// <summary>Frames since last recenter. 0 = disabled.</summary>
    public int RecenterEveryNFrames { get; set; }

    /// <summary>Minutes since last recenter. 0 = disabled.</summary>
    public int RecenterEveryMinutes { get; set; }

    /// <summary>Recenter when a per-frame plate-solve detects drift
    /// ≥ this many arcsec from the reference RA/Dec. 0 = disabled.
    /// Warning: this means a plate-solve per frame, heavy on RPi 4.
    /// Default off; user opts in.</summary>
    public double RecenterDriftArcsec { get; set; }

    /// <summary>Convergence tolerance passed to <see cref="SlewCenterService.StartJob"/>.</summary>
    public double RecenterToleranceArcsec { get; set; } = 30;

    // ----- One-shot "before starting stack" prep -----

    /// <summary>When true, the LIVE tab's "Stack ON" handler triggers
    /// an auto-focus run before the first frame is accepted. Distinct
    /// from <see cref="RefocusEnabled"/> (which fires DURING the stack);
    /// this is a one-time warm-up so the operator doesn't have to
    /// manually click Auto Focus before clicking Stack. Skipped silently
    /// when no focuser is connected.</summary>
    public bool RefocusOnStart { get; set; }

    /// <summary>When true, the LIVE tab's "Stack ON" handler triggers
    /// a slew + plate-solve recenter on the current target before the
    /// first frame is accepted. Useful when the operator drove the
    /// mount manually and wants Polaris to re-center precisely before
    /// stacking commits to that pointing. Skipped silently when no
    /// mount target is set.</summary>
    public bool RecenterOnStart { get; set; }
}
