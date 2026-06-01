using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Core.Utility;
using NINA.INDI.Protocol;

namespace NINA.INDI.Client;

public class IndiClient : IDisposable {
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private readonly IndiXmlParser _parser = new();
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _reconnectAttempt;
    private bool _autoReconnect;

    /// <summary>Diagnostic logger for write tracing. Defaults to
    /// NullLogger so existing call sites that don't pass one keep
    /// working unchanged. When set (NINA.Polaris wires it through),
    /// every Set*Async logs the exact XML being sent — DBGLOG-2
    /// mirrors that into the LOG panel, so the operator can see
    /// whether an INDI write actually went out and (by comparing with
    /// the device's PropertyChanged event right after) whether the
    /// driver acted on it.
    /// Named DiagLogger to avoid colliding with the existing
    /// <c>NINA.Core.Utility.Logger</c> static class that IndiClient
    /// already uses for connect/disconnect tracing.</summary>
    public ILogger DiagLogger { get; set; } = NullLogger.Instance;

    public string Host { get; }
    public int Port { get; }
    public bool IsConnected => _tcp?.Connected == true && _stream != null;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ReconnectBaseDelay { get; set; } = TimeSpan.FromSeconds(2);
    public int MaxReconnectAttempts { get; set; } = 10;

    public ConcurrentDictionary<string, ConcurrentDictionary<string, IndiProperty>> Devices { get; } = new();

    public event Action<string, IndiProperty>? PropertyChanged;
    public event Action<string>? DeviceFound;
    /// <summary>Fired when INDI reports the device shutting down via
    /// a wholesale &lt;delProperty&gt; (no name attribute) -- typically
    /// because the driver was unloaded from indi-web or indiserver
    /// was restarted with a different driver set.</summary>
    public event Action<string>? DeviceRemoved;
    public event Action<IndiBlobProperty>? BlobReceived;
    public event Action<string, string>? MessageReceived;
    public event Action? Disconnected;
    public event Action? Reconnected;
    public event Action<int>? ReconnectAttempt;

    public IndiClient(string host = "localhost", int port = 7624) {
        Host = host;
        Port = port;

        _parser.PropertyDefined += OnPropertyDefined;
        _parser.PropertyUpdated += OnPropertyUpdated;
        _parser.PropertyDeleted += OnPropertyDeleted;
        _parser.MessageReceived += (dev, msg) => MessageReceived?.Invoke(dev, msg);

        // Auto-CONFIG_LOAD watcher: when a device's CONNECTION switch
        // transitions to CONNECT=On, dispatch CONFIG_LOAD ~1.5s later so
        // the driver has time to advertise CONFIG_PROCESS. Replays the
        // settings the user persisted on previous sessions (slew rate,
        // tracking mode, gain, etc) without per-device endpoint patching.
        PropertyChanged += OnPropertyChangedForConfigAutoLoad;
    }

    private readonly ConcurrentDictionary<string, bool> _lastConnectionState = new();

    private void OnPropertyChangedForConfigAutoLoad(string device, IndiProperty prop) {
        if (!string.Equals(prop.Name, "CONNECTION", StringComparison.OrdinalIgnoreCase))
            return;
        if (prop is not IndiSwitchProperty sw) return;
        if (!sw.Values.TryGetValue("CONNECT", out var nowConnected)) return;

        var wasConnected = _lastConnectionState.GetValueOrDefault(device, false);
        _lastConnectionState[device] = nowConnected;

        if (nowConnected && !wasConnected) {
            // Fire-and-forget delayed CONFIG_LOAD. Best-effort -- if the
            // device doesn't have CONFIG_PROCESS the helper returns false
            // silently. 1500ms gives the driver time to define all of
            // its properties; tighter than that and CONFIG_PROCESS may
            // still be absent when we try.
            _ = Task.Run(async () => {
                try {
                    await Task.Delay(1500);
                    var loaded = await LoadDeviceConfigAsync(device);
                    if (loaded) {
                        DiagLogger.LogInformation(
                            "INDI CONFIG_LOAD auto-dispatched for '{Device}' on connect",
                            device);
                    }
                } catch (Exception ex) {
                    DiagLogger.LogDebug(ex,
                        "INDI auto-CONFIG_LOAD failed for '{Device}'", device);
                }
            });
        }
    }

    public async Task ConnectAsync(CancellationToken ct = default) {
        if (IsConnected) return;

        _tcp = new TcpClient {
            SendTimeout = (int)SendTimeout.TotalMilliseconds,
            ReceiveTimeout = 0, // read loop manages its own cancellation
            NoDelay = true
        };
        _tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(ConnectTimeout);

        try {
            await _tcp.ConnectAsync(Host, Port, connectCts.Token);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            _tcp.Dispose();
            _tcp = null;
            throw new TimeoutException($"Connection to {Host}:{Port} timed out after {ConnectTimeout.TotalSeconds}s");
        }

        _stream = _tcp.GetStream();
        _cts = new CancellationTokenSource();
        _reconnectAttempt = 0;
        _autoReconnect = true;

        Logger.Info($"Connected to INDI server at {Host}:{Port}");

        _readTask = Task.Run(() => ReadLoopAsync(_cts.Token), _cts.Token);

        // Clear stale device snapshot before issuing getProperties.
        // Without this, an auto-reconnect (or a manual reconnect after
        // the user restarted indiserver / unloaded drivers in indi-web)
        // would keep the OLD device list visible forever, because new
        // defXxxVector messages only ADD to Devices, never wipe it.
        // The freshly-issued getProperties below repopulates with
        // exactly what indiserver advertises right now.
        Devices.Clear();

        await SendAsync(IndiXmlWriter.GetProperties(), ct);
    }

    /// <summary>Force a full device-list resync without dropping the
    /// TCP connection. Clears the cached Devices snapshot + reissues
    /// <c>getProperties</c>. Use this when the user knows indiserver's
    /// driver set changed (e.g. unloaded a driver from indi-web) but
    /// the TCP socket is still up — the socket alone doesn't tell us
    /// the inventory changed, and well-behaved drivers don't always
    /// send the spec-mandated &lt;delProperty&gt; on shutdown.</summary>
    public async Task RefreshDevicesAsync(CancellationToken ct = default) {
        if (!IsConnected) throw new InvalidOperationException("INDI not connected");
        var oldCount = Devices.Count;
        Devices.Clear();
        DiagLogger.LogInformation("INDI RefreshDevicesAsync: cleared {Count} cached devices, re-issuing getProperties", oldCount);
        await SendAsync(IndiXmlWriter.GetProperties(), ct);
    }

    private async Task ReadLoopAsync(CancellationToken ct) {
        try {
            await _parser.ParseStreamAsync(_stream!, ct);
        } catch (Exception ex) when (!ct.IsCancellationRequested) {
            Logger.Warning($"INDI read loop lost connection: {ex.Message}");
        }

        if (!ct.IsCancellationRequested && _autoReconnect) {
            _ = Task.Run(() => AutoReconnectAsync());
        }
    }

    private async Task AutoReconnectAsync() {
        Logger.Info("INDI auto-reconnect started");
        Disconnected?.Invoke();

        CleanupConnection();

        while (_autoReconnect && _reconnectAttempt < MaxReconnectAttempts) {
            _reconnectAttempt++;
            var delay = TimeSpan.FromSeconds(
                Math.Min(ReconnectBaseDelay.TotalSeconds * Math.Pow(2, _reconnectAttempt - 1), 60));

            Logger.Info($"INDI reconnect attempt {_reconnectAttempt}/{MaxReconnectAttempts} in {delay.TotalSeconds:F0}s");
            ReconnectAttempt?.Invoke(_reconnectAttempt);

            await Task.Delay(delay);

            if (!_autoReconnect) break;

            try {
                await ConnectAsync();
                Logger.Info("INDI reconnected successfully");
                Reconnected?.Invoke();
                return;
            } catch (Exception ex) {
                Logger.Warning($"INDI reconnect attempt {_reconnectAttempt} failed: {ex.Message}");
                CleanupConnection();
            }
        }

        if (_reconnectAttempt >= MaxReconnectAttempts) {
            Logger.Error($"INDI gave up reconnecting after {MaxReconnectAttempts} attempts");
        }
    }

    private void CleanupConnection() {
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
    }

    public async Task DisconnectAsync() {
        _autoReconnect = false;
        _cts?.Cancel();

        if (_readTask != null) {
            try { await _readTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        }

        CleanupConnection();
        Logger.Info("Disconnected from INDI server");
        Disconnected?.Invoke();
    }

    public async Task SendAsync(byte[] data, CancellationToken ct = default) {
        if (_stream == null) throw new InvalidOperationException("Not connected to INDI server");

        if (!await _sendLock.WaitAsync(SendTimeout, ct)) {
            throw new TimeoutException("INDI send lock timed out, previous send still in progress");
        }

        try {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(SendTimeout);
            await _stream.WriteAsync(data, sendCts.Token);
            await _stream.FlushAsync(sendCts.Token);
        } finally {
            _sendLock.Release();
        }
    }

    public async Task EnableBlobAsync(string device, CancellationToken ct = default) {
        await SendAsync(IndiXmlWriter.EnableBLOB(device), ct);
    }

    public async Task SetNumberAsync(string device, string property, Dictionary<string, double> values,
        CancellationToken ct = default) {
        LogIndiWrite("newNumberVector", device, property,
            string.Join(", ", values.Select(kv => kv.Key + "=" + kv.Value)));
        await SendAsync(IndiXmlWriter.NewNumberVector(device, property, values), ct);
    }

    public async Task SetSwitchAsync(string device, string property, Dictionary<string, bool> values,
        CancellationToken ct = default) {
        LogIndiWrite("newSwitchVector", device, property,
            string.Join(", ", values.Select(kv => kv.Key + "=" + (kv.Value ? "On" : "Off"))));
        await SendAsync(IndiXmlWriter.NewSwitchVector(device, property, values), ct);
    }

    public async Task SetTextAsync(string device, string property, Dictionary<string, string> values,
        CancellationToken ct = default) {
        LogIndiWrite("newTextVector", device, property,
            string.Join(", ", values.Select(kv => kv.Key + "=\"" + kv.Value + "\"")));
        await SendAsync(IndiXmlWriter.NewTextVector(device, property, values), ct);
    }

    // ====================================================================
    // Ack-based property writes (INDIROB-1, ported from NINA PINS
    // pattern at NINA.INDI/Devices/INDIDevice.cs:203-262 in that fork).
    //
    // INDI is fire-and-forget — the server never replies to a write
    // with a status code. Instead it echoes back a set*Vector whose
    // `state` attribute tells the client how the driver reacted:
    //
    //   state=Busy   driver accepted, operation in progress
    //   state=Ok     driver acted instantly
    //   state=Alert  driver rejected (message="..." explains why)
    //
    // The plain SetNumberAsync / SetSwitchAsync wrappers above return
    // as soon as the bytes are on the wire — fine for "stream to disk"
    // style writes but disastrous for slew / move / sync flows because
    // the caller has no way to know whether the driver even saw the
    // command. Worse: IsSlewing (which reads `prop.State == Busy`) can
    // be polled BEFORE the driver echoes anything back, returning
    // false (last Ok state) and making the slew appear instant.
    //
    // The *Ack variants below subscribe to PropertyChanged for one
    // shot before sending the write, then wait for the matching
    // device+property to come back with Busy/Ok (acknowledged) or
    // Alert (rejected). Timeout defaults to 5s — INDI drivers typically
    // ack in <100ms, so 5s is comfortable headroom for slow USB-serial
    // links or congested networks without making the user wait forever
    // when the driver is wedged.
    // ====================================================================

    public TimeSpan DefaultAckTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Per-device pre-connect delay table (INDIROB-3). Keyed
    /// by INDI device name; value is the number of milliseconds to
    /// sleep BEFORE the CONNECTION switch goes out. Useful for slow
    /// hardware that needs time to boot after a USB enumeration:
    /// ESP32-based mounts (Onstep, AM3 WiFi bridge), USB-serial
    /// adapters with chip-side init, focusers that wake the firmware
    /// only after USB power stabilises (~1-2s). Missing key or value 0
    /// = no delay. NINA.Polaris populates this from
    /// <c>EquipmentProfile.PreConnectDelayMsByDevice</c> on startup
    /// and whenever the active rig changes.</summary>
    public Dictionary<string, int> PreConnectDelaysMs { get; } = new();

    /// <summary>Connect a specific INDI device by sending the
    /// standard <c>CONNECTION</c> switch with CONNECT=true. INDIROB-2
    /// (from NINA PINS): before sending, check whether the driver is
    /// already connected at the server level. Some shared drivers
    /// (e.g. <c>indi_lx200generic</c> exposes both a telescope and a
    /// focuser interface from a single driver) won't reply to a
    /// redundant CONNECT, leaving us to wait 30s for an ack that
    /// never comes. Skipping the write when the switch is already
    /// true avoids that hang and is well-defined per the INDI spec.
    /// INDIROB-3: also honours <see cref="PreConnectDelaysMs"/> by
    /// sleeping briefly before the write when the device has a
    /// per-device delay configured.</summary>
    public async Task ConnectDeviceAsync(string device, CancellationToken ct = default) {
        if (GetSwitch(device, "CONNECTION", "CONNECT")) {
            DiagLogger.LogInformation(
                "INDI device '{Device}' CONNECTION.CONNECT already true — skipping redundant CONNECT (avoids 30s no-reply hang on shared drivers)",
                device);
            return;
        }
        if (PreConnectDelaysMs.TryGetValue(device, out var delayMs) && delayMs > 0) {
            DiagLogger.LogInformation(
                "INDI device '{Device}': waiting {DelayMs}ms before CONNECT (per-device pre-connect delay)",
                device, delayMs);
            await Task.Delay(delayMs, ct);
        }
        await SetSwitchAsync(device, "CONNECTION",
            new Dictionary<string, bool> { ["CONNECT"] = true, ["DISCONNECT"] = false }, ct);
    }

    /// <summary>Mirror of <see cref="ConnectDeviceAsync"/> for the
    /// disconnect side. Same shared-driver caveat: if the driver is
    /// already disconnected (or never connected), don't send a
    /// redundant DISCONNECT that will hang.</summary>
    public async Task DisconnectDeviceAsync(string device, CancellationToken ct = default) {
        if (GetSwitch(device, "CONNECTION", "DISCONNECT")) {
            DiagLogger.LogInformation(
                "INDI device '{Device}' CONNECTION.DISCONNECT already true — skipping redundant DISCONNECT",
                device);
            return;
        }
        await SetSwitchAsync(device, "CONNECTION",
            new Dictionary<string, bool> { ["CONNECT"] = false, ["DISCONNECT"] = true }, ct);
    }

    public Task<IndiAckResult> SetNumberAsyncAck(string device, string property,
        Dictionary<string, double> values, TimeSpan? timeout = null, CancellationToken ct = default)
        => SendAndAwaitAckAsync(device, property,
            send: () => SetNumberAsync(device, property, values, ct),
            timeout: timeout ?? DefaultAckTimeout, ct: ct);

    public Task<IndiAckResult> SetSwitchAsyncAck(string device, string property,
        Dictionary<string, bool> values, TimeSpan? timeout = null, CancellationToken ct = default)
        => SendAndAwaitAckAsync(device, property,
            send: () => SetSwitchAsync(device, property, values, ct),
            timeout: timeout ?? DefaultAckTimeout, ct: ct);

    public Task<IndiAckResult> SetTextAsyncAck(string device, string property,
        Dictionary<string, string> values, TimeSpan? timeout = null, CancellationToken ct = default)
        => SendAndAwaitAckAsync(device, property,
            send: () => SetTextAsync(device, property, values, ct),
            timeout: timeout ?? DefaultAckTimeout, ct: ct);

    /// <summary>Subscribe to PropertyChanged for one matching update,
    /// fire the write, then wait. Ack semantics:
    ///   first Busy or Ok arriving with matching device+property → Acknowledged
    ///   first Alert arriving with matching device+property      → Rejected
    ///   no matching update within timeout                       → TimedOut
    /// Multiple concurrent ack waits on the same property are allowed;
    /// each gets a private TaskCompletionSource. We DON'T enforce that
    /// the property must already exist in the device snapshot — some
    /// def*Vector / set*Vector cycles arrive interleaved during a fresh
    /// driver load, and the property is only added to Devices once the
    /// first def*Vector is parsed.
    ///
    /// Internal (not private) so unit tests can drive it with a
    /// no-op send and fire PropertyChanged manually — exercises the
    /// ack logic without needing a real INDI server. Production
    /// callers go through the typed Set*AsyncAck wrappers above.</summary>
    internal async Task<IndiAckResult> SendAndAwaitAckAsync(string device, string property,
        Func<Task> send, TimeSpan timeout, CancellationToken ct) {
        var tcs = new TaskCompletionSource<IndiAckResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnPropChanged(string evDevice, IndiProperty evProp) {
            if (!string.Equals(evDevice, device, StringComparison.Ordinal)) return;
            if (!string.Equals(evProp.Name, property, StringComparison.Ordinal)) return;
            switch (evProp.State) {
                case IndiPropertyState.Busy:
                case IndiPropertyState.Ok:
                    tcs.TrySetResult(new IndiAckResult(
                        Acknowledged: true, Rejected: false, TimedOut: false, AlertMessage: null));
                    break;
                case IndiPropertyState.Alert:
                    tcs.TrySetResult(new IndiAckResult(
                        Acknowledged: false, Rejected: true, TimedOut: false,
                        AlertMessage: evProp.Message));
                    break;
                // Idle = property re-defined / cleared; not an ack signal.
            }
        }

        PropertyChanged += OnPropChanged;
        try {
            await send();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            using var _ = timeoutCts.Token.Register(() => tcs.TrySetResult(new IndiAckResult(
                Acknowledged: false, Rejected: false, TimedOut: true, AlertMessage: null)));

            return await tcs.Task.ConfigureAwait(false);
        } finally {
            PropertyChanged -= OnPropChanged;
        }
    }

    // ====================================================================
    // CONFIG_PROCESS — INDI's standard "save / load / default" mechanism.
    // Every well-behaved INDI driver advertises a CONFIG_PROCESS switch
    // vector with elements CONFIG_LOAD, CONFIG_SAVE, CONFIG_DEFAULT (and
    // sometimes CONFIG_PURGE). Driving these lets Polaris persist
    // per-driver settings across reconnects WITHOUT having to track every
    // individual property ourselves — saved state lives in
    // ~/.indi/{driver}_config.xml under the indiserver process owner.
    //
    // Used by:
    //   - device ConnectAsync paths (auto-LOAD after a successful CONNECT
    //     so the user's tweaks survive reconnects and indiserver restarts)
    //   - IndiPropertiesEndpoints set handler (debounced SAVE after any
    //     property write so changes the user makes via the panel persist)
    //   - manual Save/Load/Default endpoints + UI buttons
    // ====================================================================

    /// <summary>Per-device debounce timers for CONFIG_SAVE. Keyed by
    /// device name. A new ScheduleConfigSaveDebounced call resets the
    /// timer so a flurry of property edits coalesces into a single
    /// SAVE write at the end. Tunable via ConfigSaveDebounce below.</summary>
    private readonly ConcurrentDictionary<string, Timer> _configSaveTimers = new();
    public TimeSpan ConfigSaveDebounce { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Send CONFIG_PROCESS=CONFIG_LOAD to the device. Returns
    /// false (without throwing) when the device doesn't advertise the
    /// property — some minimal drivers omit it, and we don't want a
    /// connect path to fail because of that.</summary>
    public async Task<bool> LoadDeviceConfigAsync(string device, CancellationToken ct = default) {
        return await SetConfigProcessAsync(device, "CONFIG_LOAD", ct);
    }

    /// <summary>Send CONFIG_PROCESS=CONFIG_SAVE. Same graceful-skip
    /// behavior as LoadDeviceConfigAsync.</summary>
    public async Task<bool> SaveDeviceConfigAsync(string device, CancellationToken ct = default) {
        return await SetConfigProcessAsync(device, "CONFIG_SAVE", ct);
    }

    /// <summary>Send CONFIG_PROCESS=CONFIG_DEFAULT (revert to driver
    /// defaults). Operator-initiated only — never called automatically.</summary>
    public async Task<bool> ResetDeviceConfigAsync(string device, CancellationToken ct = default) {
        return await SetConfigProcessAsync(device, "CONFIG_DEFAULT", ct);
    }

    private async Task<bool> SetConfigProcessAsync(string device, string element, CancellationToken ct) {
        var prop = GetProperty(device, "CONFIG_PROCESS") as IndiSwitchProperty;
        if (prop == null || prop.Values.Count == 0) {
            DiagLogger.LogDebug(
                "INDI CONFIG_PROCESS skipped for '{Device}': property not advertised by driver",
                device);
            return false;
        }
        // OneOfMany switch — build payload with only the requested
        // element ON, every other element explicitly OFF.
        var payload = prop.Values.Keys.ToDictionary(
            k => k,
            k => string.Equals(k, element, StringComparison.OrdinalIgnoreCase));
        if (!payload.ContainsValue(true)) {
            DiagLogger.LogWarning(
                "INDI CONFIG_PROCESS '{Element}' not in '{Device}' element list ({Have})",
                element, device, string.Join(", ", prop.Values.Keys));
            return false;
        }
        await SetSwitchAsync(device, "CONFIG_PROCESS", payload, ct);
        return true;
    }

    /// <summary>Queue a debounced CONFIG_SAVE for the device. Resets the
    /// timer on every call so rapid edits collapse into one write. Safe
    /// to call from any code path that mutates a property — including
    /// from inside SetSwitchAsync/SetNumberAsync handlers — because the
    /// actual save runs on a background thread with its own scope.</summary>
    public void ScheduleConfigSaveDebounced(string device) {
        if (string.IsNullOrEmpty(device)) return;
        // Defensive: never debounce a save triggered by CONFIG_PROCESS
        // itself, would recurse forever.
        var newTimer = new Timer(async _ => {
            try {
                await SaveDeviceConfigAsync(device);
            } catch (Exception ex) {
                DiagLogger.LogDebug(ex, "INDI CONFIG_SAVE failed for '{Device}'", device);
            }
        }, null, ConfigSaveDebounce, Timeout.InfiniteTimeSpan);
        if (_configSaveTimers.TryRemove(device, out var old)) {
            old.Dispose();
        }
        _configSaveTimers[device] = newTimer;
    }

    /// <summary>Shared logging path so every INDI write surfaces in the
    /// LOG panel with a uniform shape. Emits the property + element
    /// values AND a warning when the target property doesn't exist on
    /// the device's snapshot — the most common reason a write is
    /// silently dropped is that the driver doesn't advertise the
    /// property name we picked (e.g. <c>GEOGRAPHIC_COORD</c> vs. some
    /// driver-specific alias). INDI itself never replies to writes,
    /// so this warning is the only way to spot the mismatch without
    /// sniffing the wire.</summary>
    private void LogIndiWrite(string kind, string device, string property, string elementsLog) {
        var exists = Devices.TryGetValue(device, out var props) && props.ContainsKey(property);
        if (exists) {
            DiagLogger.LogInformation("INDI {Kind} → device='{Device}' property='{Property}' [{Elements}]",
                kind, device, property, elementsLog);
        } else {
            // Build a hint of which properties DO exist on this device so
            // the user can find the right name. Truncate the list — chatty
            // devices have 50+ properties.
            var hint = props == null
                ? "(device not announced)"
                : string.Join(", ", props.Keys.OrderBy(k => k).Take(20))
                  + (props.Count > 20 ? $", … ({props.Count - 20} more)" : "");
            DiagLogger.LogWarning(
                "INDI {Kind} → device='{Device}' property='{Property}' [{Elements}] -- " +
                "WARNING: property NOT in device snapshot; driver will silently drop the write. " +
                "Available properties on this device: {Hint}",
                kind, device, property, elementsLog, hint);
        }
    }

    public IndiProperty? GetProperty(string device, string name) {
        if (Devices.TryGetValue(device, out var props) && props.TryGetValue(name, out var prop))
            return prop;
        return null;
    }

    public double GetNumber(string device, string property, string element) {
        if (GetProperty(device, property) is IndiNumberProperty np &&
            np.Values.TryGetValue(element, out var elem))
            return elem.Value;
        return double.NaN;
    }

    public bool GetSwitch(string device, string property, string element) {
        if (GetProperty(device, property) is IndiSwitchProperty sp &&
            sp.Values.TryGetValue(element, out var val))
            return val;
        return false;
    }

    public IEnumerable<string> GetDeviceNames() => Devices.Keys;

    private void OnPropertyDefined(IndiProperty prop) {
        var deviceProps = Devices.GetOrAdd(prop.Device, _ => new());
        bool isNew = !Devices.ContainsKey(prop.Device);

        deviceProps[prop.Name] = prop;

        if (isNew) DeviceFound?.Invoke(prop.Device);
        PropertyChanged?.Invoke(prop.Device, prop);

        if (prop is IndiBlobProperty)
            Logger.Debug($"INDI BLOB property defined: {prop.Device}.{prop.Name}");
    }

    private void OnPropertyUpdated(IndiProperty prop) {
        var deviceProps = Devices.GetOrAdd(prop.Device, _ => new());

        if (deviceProps.TryGetValue(prop.Name, out var existing)) {
            MergeProperty(existing, prop);
            existing.State = prop.State;
            existing.Timestamp = prop.Timestamp;
            // Propagate the new update's message verbatim — including
            // null, so a recovered-from-Alert update clears the previous
            // error string. SetNumberAsyncAck reads this when raising.
            existing.Message = prop.Message;
        } else {
            deviceProps[prop.Name] = prop;
        }

        if (prop is IndiBlobProperty blob)
            BlobReceived?.Invoke(blob);

        PropertyChanged?.Invoke(prop.Device, prop);
    }

    /// <summary>Handle INDI &lt;delProperty&gt;. When <paramref name="name"/>
    /// is null/empty the whole device is being removed (driver
    /// unloaded). Otherwise only the named property of that specific
    /// device is dropped. The old impl ignored the device attribute
    /// entirely + tried to remove the same property name from every
    /// device, which silently no-op'd for device-scoped deletes -- the
    /// reason Polaris kept showing devices that the user had unloaded
    /// from the indi-web manager.</summary>
    private void OnPropertyDeleted(string device, string? name) {
        if (string.IsNullOrEmpty(device)) return;
        if (string.IsNullOrEmpty(name)) {
            // Whole-device removal. Drop the entire properties map +
            // fire DeviceRemoved so consumers (EquipmentManager,
            // property browser) can clear any cached references.
            if (Devices.TryRemove(device, out _)) {
                DiagLogger.LogInformation("INDI device removed: {Device}", device);
                DeviceRemoved?.Invoke(device);
            }
        } else if (Devices.TryGetValue(device, out var props)) {
            props.TryRemove(name, out _);
        }
    }

    private static void MergeProperty(IndiProperty existing, IndiProperty update) {
        switch (existing) {
            case IndiNumberProperty enp when update is IndiNumberProperty unp:
                foreach (var (k, v) in unp.Values) enp.Values[k] = v;
                break;
            case IndiTextProperty etp when update is IndiTextProperty utp:
                foreach (var (k, v) in utp.Values) etp.Values[k] = v;
                break;
            case IndiSwitchProperty esp when update is IndiSwitchProperty usp:
                foreach (var (k, v) in usp.Values) esp.Values[k] = v;
                break;
            case IndiLightProperty elp when update is IndiLightProperty ulp:
                foreach (var (k, v) in ulp.Values) elp.Values[k] = v;
                break;
            case IndiBlobProperty ebp when update is IndiBlobProperty ubp:
                foreach (var (k, v) in ubp.Values) ebp.Values[k] = v;
                break;
        }
    }

    public void Dispose() {
        _autoReconnect = false;
        _cts?.Cancel();
        CleanupConnection();
        _sendLock.Dispose();
    }
}
