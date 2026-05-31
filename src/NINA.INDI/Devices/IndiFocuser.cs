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

    /// <summary>Force a DISCONNECT then CONNECT cycle on the device,
    /// with a brief settle wait. This is the software-only recovery
    /// for an EAF that's stuck after a driver crash (subsequent
    /// moves silently fail, even tiny ones). The INDI driver itself
    /// stays running; only the per-device CONNECTION switch is
    /// cycled, which forces the driver to re-handshake with the
    /// USB device and reset its internal state machine. Doesn't
    /// help when the EAF firmware itself is wedged -- in that case
    /// the operator needs to physically power-cycle the EAF (USB
    /// unplug + replug, or the device's power switch if present).</summary>
    public async Task ReconnectAsync(CancellationToken ct = default) {
        _logger.LogInformation(
            "IndiFocuser '{Device}' reconnect: sending DISCONNECT", DeviceName);
        await DisconnectAsync(ct);
        // Long enough for the driver to fully tear down the USB
        // session + release ASIO resources before we ask it to
        // re-open. Empirical: 500ms wasn't enough on a stressed
        // EAF, 2s reliably reconnects.
        await Task.Delay(2000, ct);
        _logger.LogInformation(
            "IndiFocuser '{Device}' reconnect: sending CONNECT", DeviceName);
        await ConnectAsync(ct);
        // Give the driver a moment to populate properties before
        // any caller probes IsConnected / Position.
        await Task.Delay(1000, ct);
        var nowConnected = IsConnected;
        _logger.LogInformation(
            "IndiFocuser '{Device}' reconnect complete: IsConnected={Connected}",
            DeviceName, nowConnected);
        if (!nowConnected) {
            throw new InvalidOperationException(
                $"Focuser '{DeviceName}' did not reconnect within 3s. " +
                "EAF firmware may be wedged -- try physically power-cycling the device " +
                "(unplug USB, wait 5s, plug back in) and then click Connect in the RIGS tab.");
        }
    }

    /// <summary>Maximum delta (in absolute steps) we'll send in a
    /// single ABS_FOCUS_POSITION write. Empirically derived from
    /// field reports: ZWO EAF firmware crashes (CCD driver disconnects
    /// the device + INDI removes it from the device list) when asked
    /// to move ~hundreds of steps in one shot. Splitting into chunks
    /// of this size with brief settle waits between each chunk keeps
    /// the driver alive even on multi-thousand-step jumps.
    ///
    /// Settable at runtime so operators can dial it down if even 50
    /// crashes their specific EAF firmware revision -- the relevant
    /// safe value is firmware-and-firmware-version-dependent, no
    /// single hardcoded value works across the fleet. Default 50 is
    /// conservative; the original 100 was too aggressive on at least
    /// one operator's hardware. Settings &lt; 1 are clamped to 1
    /// (effectively single-step) on use.</summary>
    public int SafeChunkSize { get; set; } = 50;

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
        // Decide between single-shot and chunked transport based on
        // the move size. Chunking is the ZWO EAF workaround: large
        // single ABS writes crash the driver, but a sequence of
        // small writes (with settle waits between each) walks the
        // motor to the same final position safely.
        var delta = target - pos;
        if (Math.Abs(delta) <= SafeChunkSize) {
            await SendSingleAbsoluteAsync(target, ct);
        } else {
            await SendChunkedAbsoluteAsync(pos, target, ct);
        }
    }

    /// <summary>Single ABS_FOCUS_POSITION write + post-write
    /// disconnect probe. Used for small moves that fit in one
    /// chunk; also called by SendChunkedAbsoluteAsync for each
    /// individual chunk.</summary>
    private async Task SendSingleAbsoluteAsync(int target, CancellationToken ct) {
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

    /// <summary>Walk the motor from <paramref name="startPos"/> to
    /// <paramref name="finalTarget"/> in <see cref="SafeChunkSize"/>-step
    /// chunks. Between each chunk: poll IsMoving until it clears
    /// (10s ceiling per chunk), check IsConnected, abort the whole
    /// sequence if the driver dies mid-walk. This is the workaround
    /// for ZWO EAF crashes on large single ABS writes.
    ///
    /// Wall-clock cost: each chunk takes ~SafeChunkSize / step_rate
    /// seconds to physically move, plus 250 ms poll cadence + 200 ms
    /// inter-chunk gap. A 1000-step move on a typical EAF (500 step/s
    /// rate) becomes 10 chunks * (~0.2s move + 0.45s overhead) ~ 6-7s
    /// total -- visibly slower than a hypothetical single write but
    /// the alternative is "driver crashes, motor stops mid-flight".</summary>
    private async Task SendChunkedAbsoluteAsync(int startPos, int finalTarget, CancellationToken ct) {
        var direction = Math.Sign(finalTarget - startPos);
        var totalDelta = Math.Abs(finalTarget - startPos);
        var chunks = (totalDelta + SafeChunkSize - 1) / SafeChunkSize;
        _logger.LogInformation(
            "IndiFocuser '{Device}' chunking move: {Start} -> {Target} ({Delta} steps in {Chunks} chunks of {ChunkSize})",
            DeviceName, startPos, finalTarget, totalDelta, chunks, SafeChunkSize);

        var current = startPos;
        var chunkIndex = 0;
        while (current != finalTarget) {
            ct.ThrowIfCancellationRequested();
            chunkIndex++;
            var remaining = Math.Abs(finalTarget - current);
            var chunkSize = Math.Min(remaining, SafeChunkSize);
            var chunkTarget = current + direction * chunkSize;

            _logger.LogInformation(
                "IndiFocuser '{Device}' chunk {Index}/{Total}: {Current} -> {ChunkTarget}",
                DeviceName, chunkIndex, chunks, current, chunkTarget);

            // Send the chunk write. SendSingleAbsoluteAsync handles
            // its own logging + post-write probe.
            await SendSingleAbsoluteAsync(chunkTarget, ct);

            // Driver crashed on this chunk -- abort the whole walk
            // with the chunk index in the message so the operator can
            // see how far we got before death.
            if (!IsConnected) {
                throw new InvalidOperationException(
                    $"Focuser '{DeviceName}' disconnected during chunk {chunkIndex}/{chunks} " +
                    $"(was moving {current} -> {chunkTarget}). Driver likely crashed on this size of move.");
            }

            // Wait for IsMoving to clear before issuing the next
            // chunk. Without this we'd queue chunk N+1 while the
            // motor is still processing chunk N, which the EAF
            // driver mis-parses as a relative command and overshoots.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (IsMoving && DateTime.UtcNow < deadline) {
                await Task.Delay(250, ct);
                if (!IsConnected) {
                    throw new InvalidOperationException(
                        $"Focuser '{DeviceName}' disconnected while settling after chunk {chunkIndex}/{chunks}.");
                }
            }
            if (IsMoving) {
                _logger.LogWarning(
                    "IndiFocuser '{Device}' chunk {Index} settle timeout (10s) -- aborting walk",
                    DeviceName, chunkIndex);
                throw new TimeoutException(
                    $"Focuser '{DeviceName}' chunk {chunkIndex}/{chunks} didn't settle within 10s.");
            }

            // Inter-chunk pause. Gives the driver firmware a moment
            // to flush its state machine before the next write.
            // Without this on the EAF the second chunk sometimes gets
            // mis-parsed even after IsMoving cleared.
            await Task.Delay(200, ct);

            current = Position;
        }
        _logger.LogInformation(
            "IndiFocuser '{Device}' chunked move complete: arrived at {Position}",
            DeviceName, Position);
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

    /// <summary>Capability advertisement based on what the driver
    /// actually exposes. Probes the live property table -- if
    /// <c>FOCUS_SYNC</c> isn't published the driver doesn't honour
    /// sync, regardless of what its INFO XML claims. Computed each
    /// access (cheap dictionary lookups against the per-device
    /// snapshot) so a hot-plug rig swap reflects immediately.</summary>
    public NINA.Image.Interfaces.FocuserCapabilities Capabilities
        => new(
            SupportsSync:        _client.GetProperty(DeviceName, "FOCUS_SYNC") != null,
            SupportsReverse:     _client.GetProperty(DeviceName, "FOCUS_REVERSE_MOTION") != null,
            SupportsBacklash:    _client.GetProperty(DeviceName, "FOCUS_BACKLASH_TOGGLE") != null,
            SupportsTemperature: _client.GetProperty(DeviceName, "FOCUS_TEMPERATURE") != null);

    /// <summary>Redefine current focuser position as <paramref name="position"/>
    /// via the INDI standard <c>FOCUS_SYNC</c> number vector. Useful
    /// after the operator manually moves the drawtube with the motor
    /// disengaged, or after recovering from a counter-loss event.
    /// Does NOT physically move the focuser -- only updates the
    /// driver's internal step counter.</summary>
    public async Task SyncAsync(int position, CancellationToken ct = default) {
        if (_client.GetProperty(DeviceName, "FOCUS_SYNC") == null) {
            throw new NotSupportedException(
                $"Focuser '{DeviceName}' does not expose FOCUS_SYNC -- driver doesn't support sync.");
        }
        var max = MaxPosition;
        var target = max > 0 ? Math.Clamp(position, 0, max) : Math.Max(0, position);
        _logger.LogInformation(
            "IndiFocuser '{Device}' FOCUS_SYNC FOCUS_SYNC_VALUE={Target}",
            DeviceName, target);
        await _client.SetNumberAsync(DeviceName, "FOCUS_SYNC",
            new Dictionary<string, double> { ["FOCUS_SYNC_VALUE"] = target }, ct);
    }

    /// <summary>Flip the motor-direction convention via INDI standard
    /// <c>FOCUS_REVERSE_MOTION</c> (OneOfMany switch with elements
    /// <c>ENABLED</c> / <c>DISABLED</c>). When enabled, the driver
    /// inverts every subsequent ABS / REL move so that "increase step
    /// count" actually retracts the drawtube. Persists in the driver
    /// config -- one-time setup, not toggled per move.</summary>
    public async Task SetReverseAsync(bool reversed, CancellationToken ct = default) {
        if (_client.GetProperty(DeviceName, "FOCUS_REVERSE_MOTION") == null) {
            throw new NotSupportedException(
                $"Focuser '{DeviceName}' does not expose FOCUS_REVERSE_MOTION -- driver doesn't support direction reverse.");
        }
        _logger.LogInformation(
            "IndiFocuser '{Device}' FOCUS_REVERSE_MOTION {State}",
            DeviceName, reversed ? "ENABLED" : "DISABLED");
        await _client.SetSwitchAsync(DeviceName, "FOCUS_REVERSE_MOTION",
            new Dictionary<string, bool> {
                ["INDI_ENABLED"]  = reversed,
                ["INDI_DISABLED"] = !reversed
            }, ct);
    }

    /// <summary>Configure driver-side backlash compensation via the
    /// INDI standard <c>FOCUS_BACKLASH_TOGGLE</c> + <c>FOCUS_BACKLASH_STEPS</c>
    /// pair. Driver overshoots the target by <paramref name="steps"/>
    /// in the opposite direction then reverses to land on it,
    /// removing gear-train slack. Most stock 1.25" Crayfords carry
    /// 30-50 steps of lash; the AutoFocus orchestrator wants this
    /// enabled to avoid double-counting steps on the return leg of
    /// the V-curve.</summary>
    public async Task SetBacklashAsync(bool enabled, int steps,
            CancellationToken ct = default) {
        if (_client.GetProperty(DeviceName, "FOCUS_BACKLASH_TOGGLE") == null) {
            throw new NotSupportedException(
                $"Focuser '{DeviceName}' does not expose FOCUS_BACKLASH_TOGGLE -- driver doesn't support backlash compensation.");
        }
        // Set the step count first so toggling ON doesn't briefly
        // apply a stale (likely zero) compensation. Drivers buffer
        // both writes and apply on the next move regardless of order,
        // but the explicit ordering is defensive against the rare
        // driver that applies the toggle immediately.
        if (steps > 0 && _client.GetProperty(DeviceName, "FOCUS_BACKLASH_STEPS") != null) {
            _logger.LogInformation(
                "IndiFocuser '{Device}' FOCUS_BACKLASH_STEPS FOCUS_BACKLASH_VALUE={Steps}",
                DeviceName, steps);
            await _client.SetNumberAsync(DeviceName, "FOCUS_BACKLASH_STEPS",
                new Dictionary<string, double> { ["FOCUS_BACKLASH_VALUE"] = steps }, ct);
        }
        _logger.LogInformation(
            "IndiFocuser '{Device}' FOCUS_BACKLASH_TOGGLE {State}",
            DeviceName, enabled ? "ENABLED" : "DISABLED");
        await _client.SetSwitchAsync(DeviceName, "FOCUS_BACKLASH_TOGGLE",
            new Dictionary<string, bool> {
                ["INDI_ENABLED"]  = enabled,
                ["INDI_DISABLED"] = !enabled
            }, ct);
    }
}
