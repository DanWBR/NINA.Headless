using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NINA.Polaris.Services;

/// <summary>
/// PHD2 event server client.
/// Protocol: line-delimited JSON-RPC 2.0 over TCP (default port 4400).
/// Reference: https://github.com/OpenPHDGuiding/phd2/wiki/EventMonitoring
/// </summary>
public class PHD2Client : IDisposable {
    private readonly ILogger<PHD2Client> _logger;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private CancellationTokenSource? _cts;
    private Task? _readerTask;
    private int _nextId = 1;
    private readonly Dictionary<int, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly object _pendingLock = new();
    private readonly object _stepsLock = new();

    public string Host { get; private set; } = "localhost";
    public int Port { get; private set; } = 4400;
    public bool IsConnected => _tcp?.Connected == true;

    /// <summary>PHD2 AppState: Stopped, Selected, Calibrating, Guiding, LostLock, Paused, Looping</summary>
    public string AppState { get; private set; } = "Stopped";
    public double PixelScale { get; private set; }
    public bool IsGuiding => AppState == "Guiding";
    public bool IsCalibrating => AppState == "Calibrating";
    public bool IsPaused => AppState == "Paused";
    public bool IsLooping => AppState == "Looping";
    public bool IsSettling { get; private set; }
    public string? LastAlert { get; private set; }
    public DateTime? LastAlertAt { get; private set; }
    public string? LastSettleStatus { get; private set; }

    /// <summary>Ring buffer of recent guide steps (last 5 min at 1Hz ≈ 300 samples).</summary>
    public List<GuideStep> RecentSteps { get; } = new();
    public const int MaxSteps = 300;

    public double RmsRA { get; private set; }
    public double RmsDec { get; private set; }
    public double RmsTotal { get; private set; }
    public double PeakRA { get; private set; }
    public double PeakDec { get; private set; }

    /// <summary>Calibration data snapshot (after CalibrationComplete event).</summary>
    public CalibrationData? Calibration { get; private set; }

    // Events
    public event Action<string>? AppStateChanged;
    public event Action<GuideStep>? GuideStepReceived;
    public event Action<string>? Alert;
    public event Action<SettleResult>? Settled;

    public PHD2Client(ILogger<PHD2Client> logger) {
        _logger = logger;
    }

    public async Task ConnectAsync(string host = "localhost", int port = 4400, CancellationToken ct = default) {
        if (IsConnected) await DisconnectAsync();

        Host = host;
        Port = port;
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port, ct);
        _stream = _tcp.GetStream();
        _writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
        _reader = new StreamReader(_stream, Encoding.UTF8);
        _cts = new CancellationTokenSource();
        _readerTask = Task.Run(() => ReaderLoop(_cts.Token));

        _logger.LogInformation("PHD2 connected at {Host}:{Port}", host, port);

        // Fetch initial state (with short timeout, tolerate failures)
        try {
            var state = await CallAsync("get_app_state", timeoutMs: 3000, ct: ct);
            if (state.HasValue && state.Value.ValueKind == JsonValueKind.String) {
                AppState = state.Value.GetString() ?? "Stopped";
            }
        } catch (Exception ex) { _logger.LogWarning(ex, "get_app_state failed"); }

        try {
            var scale = await CallAsync("get_pixel_scale", timeoutMs: 3000, ct: ct);
            if (scale.HasValue && scale.Value.ValueKind == JsonValueKind.Number) {
                PixelScale = scale.Value.GetDouble();
            }
        } catch (Exception ex) { _logger.LogWarning(ex, "get_pixel_scale failed"); }
    }

    public async Task DisconnectAsync() {
        _cts?.Cancel();
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        if (_readerTask != null) {
            try { await _readerTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        _tcp = null; _stream = null; _writer = null; _reader = null;

        // Fail any pending calls
        lock (_pendingLock) {
            foreach (var tcs in _pending.Values) tcs.TrySetException(new InvalidOperationException("Disconnected"));
            _pending.Clear();
        }

        AppState = "Stopped";
        IsSettling = false;
        _logger.LogInformation("PHD2 disconnected");
    }

    private async Task ReaderLoop(CancellationToken ct) {
        try {
            while (!ct.IsCancellationRequested && _reader != null) {
                string? line;
                try {
                    line = await _reader.ReadLineAsync(ct);
                } catch (OperationCanceledException) { break; }
                catch (IOException) { break; }
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    HandleMessage(root);
                } catch (JsonException ex) {
                    _logger.LogDebug("PHD2 ignored unparseable line: {Excerpt}", line.Substring(0, Math.Min(line.Length, 120)));
                    _logger.LogDebug(ex, "JSON parse error");
                }
            }
        } catch (Exception ex) {
            _logger.LogError(ex, "PHD2 reader loop crashed");
        } finally {
            // Mark disconnected (TCP probably broken)
            if (_tcp != null && !_tcp.Connected) {
                _logger.LogWarning("PHD2 TCP connection lost");
            }
        }
    }

    /// <summary>Public hook so unit tests can drive event handling without a live socket.</summary>
    public void HandleMessage(JsonElement root) {
        // JSON-RPC response: has "jsonrpc" + "id"
        if (root.TryGetProperty("jsonrpc", out _) && root.TryGetProperty("id", out var idElem) && idElem.ValueKind == JsonValueKind.Number) {
            var id = idElem.GetInt32();
            TaskCompletionSource<JsonElement?>? tcs = null;
            lock (_pendingLock) {
                if (_pending.TryGetValue(id, out tcs)) _pending.Remove(id);
            }
            if (tcs == null) return;

            if (root.TryGetProperty("error", out var err)) {
                var errMsg = err.TryGetProperty("message", out var em) ? em.GetString() : err.GetRawText();
                tcs.TrySetException(new InvalidOperationException($"PHD2 error: {errMsg}"));
            } else if (root.TryGetProperty("result", out var result)) {
                tcs.TrySetResult(result.Clone());
            } else {
                tcs.TrySetResult(null);
            }
            return;
        }

        // Event: has "Event"
        if (root.TryGetProperty("Event", out var evtName) && evtName.ValueKind == JsonValueKind.String) {
            HandleEvent(evtName.GetString() ?? "", root);
        }
    }

    /// <summary>Update AppState + fire the change event if it actually
    /// flipped. Centralised so every event that implies a state
    /// transition can route through one place.</summary>
    private void SetAppState(string newState) {
        if (AppState == newState) return;
        AppState = newState;
        AppStateChanged?.Invoke(newState);
    }

    private void HandleEvent(string eventName, JsonElement msg) {
        switch (eventName) {
            case "AppState":
                if (msg.TryGetProperty("State", out var st) && st.ValueKind == JsonValueKind.String) {
                    SetAppState(st.GetString() ?? "Stopped");
                }
                break;

            case "GuideStep": {
                var raRaw = TryGetDouble(msg, "RADistanceRaw");
                var decRaw = TryGetDouble(msg, "DECDistanceRaw");
                var pxScale = PixelScale > 0 ? PixelScale : 1.0;
                var step = new GuideStep {
                    Timestamp = DateTime.UtcNow,
                    RaPixels = raRaw,
                    DecPixels = decRaw,
                    RaArcsec = raRaw * pxScale,
                    DecArcsec = decRaw * pxScale,
                    SNR = TryGetDouble(msg, "SNR"),
                    Mass = TryGetDouble(msg, "StarMass"),
                    RaDuration = TryGetInt(msg, "RADuration"),
                    DecDuration = TryGetInt(msg, "DECDuration"),
                    RaDirection = TryGetString(msg, "RADirection"),
                    DecDirection = TryGetString(msg, "DECDirection")
                };
                AddStep(step);
                // A GuideStep arriving means PHD2 is actively guiding —
                // PHD2 doesn't reliably emit an AppState event after
                // every transition, so derive it from the step stream.
                // Without this, the UI showed "Stopped" + 0 samples
                // even though PHD2 was guiding fine in its own window.
                SetAppState("Guiding");
                GuideStepReceived?.Invoke(step);
                break;
            }

            case "Alert": {
                var alertMsg = TryGetString(msg, "Msg") ?? "";
                LastAlert = alertMsg;
                LastAlertAt = DateTime.UtcNow;
                _logger.LogWarning("PHD2 alert: {Msg}", alertMsg);
                Alert?.Invoke(alertMsg);
                break;
            }

            case "Settling":
                IsSettling = true;
                LastSettleStatus = "settling";
                break;

            case "SettleDone": {
                IsSettling = false;
                var statusCode = TryGetInt(msg, "Status");
                var result = new SettleResult {
                    Status = statusCode,
                    Error = statusCode != 0 ? TryGetString(msg, "Error") : null,
                    TotalFrames = TryGetInt(msg, "TotalFrames"),
                    DroppedFrames = TryGetInt(msg, "DroppedFrames")
                };
                LastSettleStatus = statusCode == 0 ? "done" : "failed";
                Settled?.Invoke(result);
                break;
            }

            case "CalibrationComplete":
                // Trigger a calibration data fetch in background
                _ = Task.Run(async () => {
                    try {
                        var cal = await CallAsync("get_calibration_data");
                        if (cal.HasValue) {
                            Calibration = ParseCalibration(cal.Value);
                        }
                    } catch (Exception ex) {
                        _logger.LogWarning(ex, "Failed to fetch calibration data");
                    }
                });
                break;

            // PHD2 doesn't always re-emit an AppState event after these
            // transitions (verified empirically — see
            // https://github.com/OpenPHDGuiding/phd2/wiki/EventMonitoring
            // for the canonical state mapping). Drive AppState directly
            // off the transition event so the UI stays in sync.
            case "StartGuiding":
                SetAppState("Guiding");
                break;
            case "GuidingStopped":
                SetAppState("Stopped");
                break;
            case "Paused":
                SetAppState("Paused");
                break;
            case "Resumed":
                SetAppState("Guiding");
                break;
            case "LoopingExposures":
                SetAppState("Looping");
                break;
            case "LoopingExposuresStopped":
                SetAppState("Stopped");
                break;
            case "StartCalibration":
                SetAppState("Calibrating");
                break;
            case "CalibrationFailed":
                SetAppState("Stopped");
                _logger.LogWarning("PHD2 calibration failed");
                break;
            case "StarSelected":
                // "Selected" = star locked, no longer Looping but not yet Guiding
                if (AppState == "Looping") SetAppState("Selected");
                break;
            case "StarLost":
            case "LockPositionLost":
                SetAppState("LostLock");
                break;

            case "Version":
                // Initial handshake
                break;

            default:
                _logger.LogDebug("PHD2 event (unhandled): {Event}", eventName);
                break;
        }
    }

    private void AddStep(GuideStep step) {
        lock (_stepsLock) {
            RecentSteps.Add(step);
            if (RecentSteps.Count > MaxSteps) RecentSteps.RemoveAt(0);

            // Recompute RMS / Peak across the buffer
            double sumRa2 = 0, sumDec2 = 0, peakRa = 0, peakDec = 0;
            foreach (var s in RecentSteps) {
                sumRa2 += s.RaArcsec * s.RaArcsec;
                sumDec2 += s.DecArcsec * s.DecArcsec;
                if (Math.Abs(s.RaArcsec) > peakRa) peakRa = Math.Abs(s.RaArcsec);
                if (Math.Abs(s.DecArcsec) > peakDec) peakDec = Math.Abs(s.DecArcsec);
            }
            var n = RecentSteps.Count;
            RmsRA = Math.Sqrt(sumRa2 / n);
            RmsDec = Math.Sqrt(sumDec2 / n);
            RmsTotal = Math.Sqrt((sumRa2 + sumDec2) / n);
            PeakRA = peakRa;
            PeakDec = peakDec;
        }
    }

    public void ClearStepHistory() {
        lock (_stepsLock) {
            RecentSteps.Clear();
            RmsRA = RmsDec = RmsTotal = PeakRA = PeakDec = 0;
        }
    }

    public List<GuideStep> SnapshotSteps() {
        lock (_stepsLock) {
            return new List<GuideStep>(RecentSteps);
        }
    }

    /// <summary>
    /// Send a JSON-RPC call. PHD2 expects positional params (JSON array).
    /// Returns null if the call has no result (void RPC).
    /// </summary>
    public async Task<JsonElement?> CallAsync(string method, object[]? @params = null, int timeoutMs = 10000, CancellationToken ct = default) {
        if (_writer == null) throw new InvalidOperationException("PHD2 not connected");

        int id;
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock) {
            id = _nextId++;
            _pending[id] = tcs;
        }

        object msg = @params != null && @params.Length > 0
            ? new { method, @params = @params, id }
            : (object)new { method, id };
        var json = JsonSerializer.Serialize(msg);

        try {
            await _writer.WriteLineAsync(json.AsMemory(), ct);
        } catch {
            lock (_pendingLock) _pending.Remove(id);
            throw;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        try {
            return await tcs.Task.WaitAsync(timeoutCts.Token);
        } catch (OperationCanceledException) {
            lock (_pendingLock) _pending.Remove(id);
            throw new TimeoutException($"PHD2 call '{method}' timed out after {timeoutMs}ms");
        }
    }

    // ----- High-level commands -----

    public Task<JsonElement?> StartGuidingAsync(double settlePixels = 1.5, int settleTime = 10, int settleTimeout = 40, bool recalibrate = false) {
        var settle = new { pixels = settlePixels, time = settleTime, timeout = settleTimeout };
        return CallAsync("guide", new object[] { settle, recalibrate });
    }

    public Task<JsonElement?> StopAsync() => CallAsync("stop_capture");

    public Task<JsonElement?> LoopAsync() => CallAsync("loop");

    public Task<JsonElement?> PauseAsync() => CallAsync("set_paused", new object[] { true });

    public Task<JsonElement?> ResumeAsync() => CallAsync("set_paused", new object[] { false });

    public Task<JsonElement?> DitherAsync(double pixels = 5.0, bool raOnly = false, double settlePixels = 1.5, int settleTime = 10, int settleTimeout = 40) {
        var settle = new { pixels = settlePixels, time = settleTime, timeout = settleTimeout };
        return CallAsync("dither", new object[] { pixels, raOnly, settle });
    }

    public Task<JsonElement?> SetExposureAsync(int milliseconds) =>
        CallAsync("set_exposure", new object[] { milliseconds });

    public Task<JsonElement?> AutoSelectStarAsync() => CallAsync("find_star");

    public Task<JsonElement?> ClearCalibrationAsync() => CallAsync("clear_calibration");

    // ---- High-level management (profile + equipment + exposure) ----

    /// <summary>List every PHD2 profile configured in the user's PHD2 install.</summary>
    public async Task<List<PHD2Profile>> GetProfilesAsync(CancellationToken ct = default) {
        var result = await CallAsync("get_profiles", ct: ct);
        var list = new List<PHD2Profile>();
        if (!result.HasValue || result.Value.ValueKind != JsonValueKind.Array) return list;
        foreach (var p in result.Value.EnumerateArray()) {
            list.Add(new PHD2Profile {
                Id = TryGetInt(p, "id"),
                Name = TryGetString(p, "name") ?? ""
            });
        }
        return list;
    }

    public async Task<PHD2Profile?> GetCurrentProfileAsync(CancellationToken ct = default) {
        var result = await CallAsync("get_profile", ct: ct);
        if (!result.HasValue || result.Value.ValueKind != JsonValueKind.Object) return null;
        return new PHD2Profile {
            Id = TryGetInt(result.Value, "id"),
            Name = TryGetString(result.Value, "name") ?? ""
        };
    }

    /// <summary>
    /// Switch PHD2 to a different profile. PHD2 requires equipment to be
    /// *disconnected* first — this method does that for you (idempotent: if
    /// already disconnected, the SetConnected(false) call is a no-op).
    /// </summary>
    public async Task SetProfileAsync(int profileId, CancellationToken ct = default) {
        try { await SetConnectedAsync(false, ct); } catch { /* may already be disconnected */ }
        await CallAsync("set_profile", new object[] { profileId }, ct: ct);
    }

    public async Task<bool> GetConnectedAsync(CancellationToken ct = default) {
        var r = await CallAsync("get_connected", ct: ct);
        return r.HasValue && r.Value.ValueKind == JsonValueKind.True;
    }

    /// <summary>Tell PHD2 to connect (or disconnect) all the equipment in the active profile.</summary>
    public Task SetConnectedAsync(bool connected, CancellationToken ct = default) =>
        CallAsync("set_connected", new object[] { connected }, ct: ct);

    public async Task<int> GetExposureAsync(CancellationToken ct = default) {
        var r = await CallAsync("get_exposure", ct: ct);
        return r.HasValue && r.Value.ValueKind == JsonValueKind.Number ? r.Value.GetInt32() : 0;
    }

    public Task SetExposureMsAsync(int milliseconds, CancellationToken ct = default) =>
        CallAsync("set_exposure", new object[] { milliseconds }, ct: ct);

    /// <summary>List of selectable exposure durations PHD2's UI offers (in ms).</summary>
    public async Task<List<int>> GetExposureDurationsAsync(CancellationToken ct = default) {
        var r = await CallAsync("get_exposure_durations", ct: ct);
        var list = new List<int>();
        if (!r.HasValue || r.Value.ValueKind != JsonValueKind.Array) return list;
        foreach (var v in r.Value.EnumerateArray())
            if (v.ValueKind == JsonValueKind.Number) list.Add(v.GetInt32());
        return list;
    }

    public async Task<string> GetDecGuideModeAsync(CancellationToken ct = default) {
        var r = await CallAsync("get_dec_guide_mode", ct: ct);
        return r.HasValue && r.Value.ValueKind == JsonValueKind.String ? r.Value.GetString() ?? "" : "";
    }

    /// <summary>"Auto" / "North" / "South" / "Off" — see PHD2 docs.</summary>
    public Task SetDecGuideModeAsync(string mode, CancellationToken ct = default) =>
        CallAsync("set_dec_guide_mode", new object[] { mode }, ct: ct);

    // ----- Algorithm parameter introspection + tuning -----
    // PHD2 exposes per-axis algorithm parameters (RA / Dec / Mount). The
    // parameter set depends on which algorithm is currently selected for
    // that axis in the PHD2 Brain. We don't try to guess — callers use
    // GetAlgoParamNamesAsync to discover the live param surface, then
    // GetAlgoParamAsync / SetAlgoParamAsync to read/write individual knobs.
    //
    // PHD2 returns a JSON-RPC error if the param doesn't exist for the
    // current algorithm — we surface that as null/empty rather than
    // throwing, so callers can apply presets safely even when the user
    // has a non-standard algorithm selected.

    /// <summary>
    /// Lists the algorithm parameter names PHD2 currently exposes for the
    /// given axis. Axis is typically "ra", "dec", or "Mount" — see PHD2 docs.
    /// Returns empty list if axis is invalid or PHD2 returns an error.
    /// </summary>
    public async Task<List<string>> GetAlgoParamNamesAsync(string axis, CancellationToken ct = default) {
        var list = new List<string>();
        try {
            var r = await CallAsync("get_algo_param_names", new object[] { axis }, ct: ct);
            if (!r.HasValue || r.Value.ValueKind != JsonValueKind.Array) return list;
            foreach (var v in r.Value.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String) list.Add(v.GetString() ?? "");
        } catch (Exception ex) {
            _logger.LogDebug(ex, "get_algo_param_names({Axis}) failed", axis);
        }
        return list;
    }

    /// <summary>
    /// Reads a single algorithm parameter value. Returns null if the
    /// parameter doesn't exist for the axis's current algorithm.
    /// </summary>
    public async Task<double?> GetAlgoParamAsync(string axis, string name, CancellationToken ct = default) {
        try {
            var r = await CallAsync("get_algo_param", new object[] { axis, name }, ct: ct);
            if (!r.HasValue || r.Value.ValueKind != JsonValueKind.Number) return null;
            return r.Value.GetDouble();
        } catch (Exception ex) {
            _logger.LogDebug(ex, "get_algo_param({Axis}, {Name}) failed", axis, name);
            return null;
        }
    }

    /// <summary>
    /// Sets a single algorithm parameter. Returns true on success.
    /// Returns false (with a debug log) if the parameter doesn't exist
    /// for the current algorithm — callers applying multi-param presets
    /// should treat per-param failures as non-fatal.
    /// </summary>
    public async Task<bool> SetAlgoParamAsync(string axis, string name, double value, CancellationToken ct = default) {
        try {
            await CallAsync("set_algo_param", new object[] { axis, name, value }, ct: ct);
            return true;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "set_algo_param({Axis}, {Name}, {Value}) failed", axis, name, value);
            return false;
        }
    }

    /// <summary>
    /// Flip calibration data (used post meridian-flip when guiding through
    /// a mount that doesn't auto-flip the calibration vectors).
    /// </summary>
    public Task FlipCalibrationAsync(CancellationToken ct = default) =>
        CallAsync("flip_calibration", ct: ct);

    /// <summary>Ask PHD2 to shut itself down gracefully (closes the window).</summary>
    public Task ShutdownAsync(CancellationToken ct = default) =>
        CallAsync("shutdown", ct: ct);

    /// <summary>
    /// Asks PHD2 which equipment it is currently using (guide camera, mount,
    /// AO, aux mount). Returns null if PHD2 isn't connected.
    /// </summary>
    public async Task<PHD2Equipment?> GetCurrentEquipmentAsync(CancellationToken ct = default) {
        if (!IsConnected) return null;
        try {
            var result = await CallAsync("get_current_equipment", ct: ct);
            if (!result.HasValue) return null;
            var root = result.Value;
            return new PHD2Equipment {
                Camera = ParseDevice(root, "camera"),
                Mount = ParseDevice(root, "mount"),
                AuxMount = ParseDevice(root, "aux_mount"),
                AO = ParseDevice(root, "AO")
            };
        } catch (Exception ex) {
            _logger.LogDebug(ex, "get_current_equipment failed");
            return null;
        }
    }

    private static PHD2Device? ParseDevice(JsonElement root, string key) {
        if (!root.TryGetProperty(key, out var dev) || dev.ValueKind != JsonValueKind.Object) return null;
        return new PHD2Device {
            Name = TryGetString(dev, "name") ?? "",
            Connected = dev.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True
        };
    }

    // ----- JSON helpers -----

    private static double TryGetDouble(JsonElement obj, string prop) {
        return obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : 0;
    }

    private static int TryGetInt(JsonElement obj, string prop) {
        return obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : 0;
    }

    private static string? TryGetString(JsonElement obj, string prop) {
        return obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static CalibrationData ParseCalibration(JsonElement obj) {
        return new CalibrationData {
            Calibrated = obj.TryGetProperty("calibrated", out var c) && c.ValueKind == JsonValueKind.True,
            XAngle = TryGetDouble(obj, "xAngle"),
            XRate = TryGetDouble(obj, "xRate"),
            YAngle = TryGetDouble(obj, "yAngle"),
            YRate = TryGetDouble(obj, "yRate"),
            Declination = TryGetDouble(obj, "declination")
        };
    }

    public void Dispose() {
        try { DisconnectAsync().Wait(2000); } catch { }
    }
}

public record GuideStep {
    public DateTime Timestamp { get; init; }
    public double RaPixels { get; init; }
    public double DecPixels { get; init; }
    public double RaArcsec { get; init; }
    public double DecArcsec { get; init; }
    public double SNR { get; init; }
    public double Mass { get; init; }
    public int RaDuration { get; init; }
    public int DecDuration { get; init; }
    public string? RaDirection { get; init; }
    public string? DecDirection { get; init; }
}

public record SettleResult {
    public int Status { get; init; }
    public string? Error { get; init; }
    public int TotalFrames { get; init; }
    public int DroppedFrames { get; init; }
}

public record CalibrationData {
    public bool Calibrated { get; init; }
    public double XAngle { get; init; }
    public double XRate { get; init; }
    public double YAngle { get; init; }
    public double YRate { get; init; }
    public double Declination { get; init; }
}

public record PHD2Equipment {
    public PHD2Device? Camera { get; init; }
    public PHD2Device? Mount { get; init; }
    public PHD2Device? AuxMount { get; init; }
    public PHD2Device? AO { get; init; }
}

public record PHD2Device {
    public string Name { get; init; } = "";
    public bool Connected { get; init; }
}

public record PHD2Profile {
    public int Id { get; init; }
    public string Name { get; init; } = "";
}
