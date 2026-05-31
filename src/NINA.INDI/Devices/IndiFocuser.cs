using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.INDI.Client;

namespace NINA.INDI.Devices;

public class IndiFocuser : NINA.Image.Interfaces.IFocuser {
    private readonly IndiClient _client;
    private readonly ILogger _logger;

    public string DeviceName { get; }
    /// <summary>
    /// True only when the INDI client is up AND the device's per-device
    /// CONNECTION switch is in the CONNECT state. See
    /// <see cref="IndiCamera.IsConnected"/> for the rationale.
    /// </summary>
    public bool IsConnected
        => _client.IsConnected
           && _client.GetSwitch(DeviceName, "CONNECTION", "CONNECT");
    public int Position => (int)_client.GetNumber(DeviceName, "ABS_FOCUS_POSITION", "FOCUS_ABSOLUTE_POSITION");
    public double Temperature => _client.GetNumber(DeviceName, "FOCUS_TEMPERATURE", "TEMPERATURE");
    public int MaxPosition => (int)_client.GetNumber(DeviceName, "FOCUS_MAX", "FOCUS_MAX_VALUE");
    public bool IsMoving {
        get {
            var prop = _client.GetProperty(DeviceName, "ABS_FOCUS_POSITION");
            return prop?.State == Protocol.IndiPropertyState.Busy;
        }
    }

    public IndiFocuser(IndiClient client, string deviceName, ILogger? logger = null) {
        _client = client;
        DeviceName = deviceName;
        // Optional logger so existing call sites (and tests) that
        // don't have a logger handy keep compiling. When present, every
        // MoveAbsoluteAsync logs its inputs + the resulting INDI write
        // -- LogBufferLoggerProvider (DBGLOG-2) mirrors that into the
        // ring buffer so the operator can see exactly what was sent
        // when a crash happens.
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task ConnectAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "CONNECTION",
            new Dictionary<string, bool> { ["CONNECT"] = true, ["DISCONNECT"] = false }, ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "CONNECTION",
            new Dictionary<string, bool> { ["CONNECT"] = false, ["DISCONNECT"] = true }, ct);
    }

    public async Task MoveAbsoluteAsync(int position, CancellationToken ct = default) {
        // Snapshot state once so the diagnostic log + the guards
        // see the same values (Position can shift between reads when
        // the driver is mid-update).
        var pos = Position;
        var max = MaxPosition;
        var moving = IsMoving;
        var connected = IsConnected;
        _logger.LogInformation(
            "IndiFocuser MoveAbsoluteAsync '{Device}' requested={Requested} currentPos={Pos} max={Max} moving={Moving} connected={Connected}",
            DeviceName, position, pos, max, moving, connected);

        // Pre-flight: refuse to send to a disconnected device. Some
        // drivers (ZWO EAF) silently accept a number vector while
        // disconnected and only error out when the next operation
        // probes CONNECTION -- by then the UI thinks the move
        // succeeded when it never started.
        if (!connected) {
            throw new InvalidOperationException(
                $"Focuser '{DeviceName}' is not connected. Connect it from the RIGS tab before moving.");
        }
        // Also refuse moves while a previous move is in flight. The
        // EAF driver in particular re-interprets the second
        // FOCUS_ABSOLUTE_POSITION write mid-flight as a relative
        // command, which can over-shoot and trip the limit switch
        // (visible to the user as "disconnect on move").
        if (moving) {
            throw new InvalidOperationException(
                $"Focuser '{DeviceName}' is already moving. Wait for the current move to settle.");
        }
        // Clamp into the driver-reported travel range. Writing a value
        // outside [0, MaxPosition] is a frequent reason INDI focuser
        // drivers (ZWO EAF especially) flip CONNECTION.PARK off as an
        // error response -- from the UI it looks like "I tried to move
        // and the focuser disconnected". MaxPosition can come back as 0
        // before the driver populates FOCUS_MAX; in that case skip the
        // upper clamp and trust the caller.
        var target = position;
        if (max > 0) target = Math.Clamp(target, 0, max);
        else        target = Math.Max(0, target);
        // Short-circuit moves to the current position. Some EAF
        // firmware revisions treat "move to where you already are"
        // as an error condition and toggle CONNECTION off as the
        // response. Cheap to guard, expensive to recover from.
        if (target == pos) {
            _logger.LogInformation("IndiFocuser '{Device}' already at target {Target}, skipping", DeviceName, target);
            return;
        }
        _logger.LogInformation(
            "IndiFocuser '{Device}' sending ABS_FOCUS_POSITION FOCUS_ABSOLUTE_POSITION={Target}",
            DeviceName, target);
        try {
            await _client.SetNumberAsync(DeviceName, "ABS_FOCUS_POSITION",
                new Dictionary<string, double> { ["FOCUS_ABSOLUTE_POSITION"] = target }, ct);
        } catch (Exception ex) {
            _logger.LogWarning(ex,
                "IndiFocuser '{Device}' ABS_FOCUS_POSITION write FAILED (target={Target})",
                DeviceName, target);
            throw;
        }
        // Brief post-write probe: if CONNECTION dropped within 500ms
        // of the write, the driver crashed on our command and we want
        // that visible in the log. We're NOT going to retry or
        // reconnect -- only surface the diagnosis.
        try {
            await Task.Delay(500, ct);
            if (!IsConnected) {
                _logger.LogWarning(
                    "IndiFocuser '{Device}' DISCONNECTED within 500ms of move to {Target} (driver likely crashed on the command)",
                    DeviceName, target);
            }
        } catch (OperationCanceledException) {
            // request cancelled mid-wait; not the focuser's fault
        }
    }

    public async Task MoveRelativeAsync(int steps, CancellationToken ct = default) {
        // Compute the absolute target client-side and delegate to
        // MoveAbsoluteAsync, rather than going through the
        // FOCUS_MOTION + REL_FOCUS_POSITION two-step. Two reasons:
        //
        //  1) Race condition. Sending a switch (FOCUS_MOTION) and a
        //     number (REL_FOCUS_POSITION) back-to-back over the same
        //     INDI TCP stream lets the driver receive them in either
        //     order on its parser side. The ZWO EAF driver
        //     specifically has been observed to disconnect itself
        //     when the number arrives before the switch -- it
        //     interprets the unsigned step count as an absolute
        //     destination, sees it's invalid for the current
        //     direction state, and tears down CONNECTION as an error
        //     response.
        //
        //  2) Uniformity. Every IFocuser-compliant INDI driver
        //     handles ABS_FOCUS_POSITION the same way; REL_FOCUS_
        //     POSITION's interplay with FOCUS_MOTION is spec'd
        //     loosely and individual drivers differ. One code path
        //     means one bug to chase per quirky driver.
        var target = Position + steps;
        await MoveAbsoluteAsync(target, ct);
    }

    public async Task AbortAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "FOCUS_ABORT_MOTION",
            new Dictionary<string, bool> { ["ABORT"] = true }, ct);
    }
}
