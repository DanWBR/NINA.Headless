namespace NINA.Image.Interfaces;

/// <summary>
/// Common contract every focuser backend honours so EquipmentManager,
/// AutoFocusService, the meridian-flip resume hook, and the live-stack
/// triggers orchestrator stay backend-agnostic. Implementations:
/// <c>IndiFocuser</c> (any INDI-supported focuser, the common path),
/// <c>AscomComFocuser</c> (direct COM on Windows, MoonLite / Pegasus /
/// ZWO EAF natively without ASCOM Remote in the way).
///
/// <para>Properties that don't apply to a given device should return a
/// neutral value (zero / NaN). Temperature, in particular, is null for
/// focusers without an onboard probe.</para>
/// </summary>
public interface IFocuser {
    string DeviceName { get; }
    bool IsConnected { get; }
    /// <summary>Current absolute step position. Backends without
    /// absolute positioning report a synthetic position starting at
    /// some midpoint and updated by relative moves.</summary>
    int Position { get; }
    /// <summary>Maximum addressable step value. Used by the UI to
    /// clamp slider ranges and by auto-focus to refuse a sweep that
    /// would overshoot.</summary>
    int MaxPosition { get; }
    /// <summary>Onboard temperature probe reading in degrees Celsius,
    /// NaN when the focuser doesn't have a sensor.</summary>
    double Temperature { get; }
    bool IsMoving { get; }

    /// <summary>Which optional features the backend honours. The
    /// auto-focus orchestrator and Focuser card use this to decide
    /// whether to render the Sync / Reverse / Backlash controls --
    /// no point offering "Reverse direction" on a driver that doesn't
    /// implement <c>FOCUS_REVERSE_MOTION</c>.</summary>
    FocuserCapabilities Capabilities => FocuserCapabilities.Default;

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    Task MoveAbsoluteAsync(int position, CancellationToken ct = default);
    Task MoveRelativeAsync(int steps, CancellationToken ct = default);
    Task AbortAsync(CancellationToken ct = default);

    /// <summary>Force a DISCONNECT then CONNECT cycle on the device
    /// to recover from a wedged driver state. Default impl is just
    /// Disconnect + small wait + Connect, which is enough for most
    /// backends; INDI overrides with extra settle waits because the
    /// EAF in particular needs ~2s for the USB session to fully
    /// tear down before re-open will succeed.</summary>
    async Task ReconnectAsync(CancellationToken ct = default) {
        await DisconnectAsync(ct);
        await Task.Delay(1000, ct);
        await ConnectAsync(ct);
    }

    /// <summary>Tell the driver to redefine the focuser's current
    /// physical position AS the given absolute step value -- INDI
    /// <c>FOCUS_SYNC</c>. Used after manually reseating the focuser,
    /// or after a position-count drift bug, to realign the software
    /// counter with the mechanical truth. Throws by default; backends
    /// opt in. UI gates the button on <see cref="Capabilities"/>
    /// <c>.SupportsSync</c>.</summary>
    Task SyncAsync(int position, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Sync not supported by this focuser driver");

    /// <summary>Reverse the motor direction convention -- INDI
    /// <c>FOCUS_REVERSE_MOTION</c>. Needed when the focuser is
    /// mounted backwards relative to the optical train and "inward"
    /// in software actually moves the drawtube outward. Without this,
    /// auto-focus V-curves come out on the wrong axis. Throws by
    /// default.</summary>
    Task SetReverseAsync(bool reversed, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Direction reverse not supported by this focuser driver");

    /// <summary>Enable + configure driver-side backlash compensation
    /// -- INDI <c>FOCUS_BACKLASH_TOGGLE</c> + <c>FOCUS_BACKLASH_STEPS</c>.
    /// When enabled, the driver overshoots the target by N steps in
    /// the opposite direction, then reverses to land on it, taking the
    /// slack out of the gear train. Critical for accurate auto-focus
    /// on cheap focusers (gear lash up to 30-50 steps is common on
    /// stock 1.25" Crayfords). <paramref name="steps"/> is honoured
    /// when <paramref name="enabled"/> is true; otherwise ignored.
    /// Throws by default.</summary>
    Task SetBacklashAsync(bool enabled, int steps,
            CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Backlash compensation not supported by this focuser driver");
}

/// <summary>Optional-feature flags for <see cref="IFocuser"/>. UI
/// gates the matching controls; backends without a capability simply
/// hide the button instead of letting it throw at click time.</summary>
public record FocuserCapabilities(
    bool SupportsSync = false,
    bool SupportsReverse = false,
    bool SupportsBacklash = false,
    bool SupportsTemperature = false) {
    /// <summary>Conservative defaults -- nothing optional supported.
    /// Matches the historical IFocuser shape so existing backends
    /// that don't override <see cref="IFocuser.Capabilities"/> stay
    /// behaving identically.</summary>
    public static readonly FocuserCapabilities Default = new();
}
