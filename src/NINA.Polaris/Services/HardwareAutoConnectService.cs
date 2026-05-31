using NINA.INDI.Client;
using NINA.Polaris.Services.Alpaca;

namespace NINA.Polaris.Services;

/// <summary>
/// Boot-time hosted service that, when <c>profile.AutoConnectOnStartup</c>
/// is enabled, dials INDI, runs an Alpaca local-network discovery, and
/// then re-binds + connects every device the active rig has a saved
/// selection for. Each step pushes a <see cref="NotificationService"/>
/// entry so the browser surfaces a toast, the user lands on the page
/// with hardware already connected and a feed of "what just happened
/// before you got here."
///
/// Modeled after <see cref="PHD2AutoStartService"/>: stagger ~2s after
/// host startup so we don't fight other hosted services for CPU /
/// network on cold boot, then run the whole sequence in fire-and-forget
/// Task.Run so we never block IHostedService.StartAsync. Single attempt
/// per boot, if INDI isn't up yet, the user does it manually from the
/// Rigs tab. Replicating the PHD2 retry loop here would race with the
/// user clicking Connect.
/// </summary>
public class HardwareAutoConnectService : IHostedService {
    private readonly IConfiguration _config;
    private readonly IndiClient _indiClient;
    private readonly AlpacaDiscovery _alpaca;
    private readonly EquipmentManager _equip;
    private readonly ProfileService _profiles;
    private readonly NotificationService _notify;
    private readonly PHD2Client _phd2;
    private readonly ILogger<HardwareAutoConnectService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _runner;

    public HardwareAutoConnectService(
        IConfiguration config,
        IndiClient indiClient,
        AlpacaDiscovery alpaca,
        EquipmentManager equip,
        ProfileService profiles,
        NotificationService notify,
        PHD2Client phd2,
        ILogger<HardwareAutoConnectService> logger) {
        _config = config;
        _indiClient = indiClient;
        _alpaca = alpaca;
        _equip = equip;
        _profiles = profiles;
        _notify = notify;
        _phd2 = phd2;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        var enabled = _profiles.Active.AutoConnectOnStartup
                   || _config.GetValue("AutoConnect:OnStartup", false);
        if (!enabled) {
            _logger.LogDebug("Hardware auto-connect disabled (toggle in Settings or set AutoConnect:OnStartup=true)");
            return Task.CompletedTask;
        }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runner = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        _cts?.Cancel();
        if (_runner != null) {
            try { await _runner.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken); } catch { }
        }
    }

    private async Task RunAsync(CancellationToken ct) {
        try {
            // Stagger so PHD2AutoStartService + SimulatorAutoStartService
            // (also 2-3s staggers) don't all hammer the network at once.
            await Task.Delay(TimeSpan.FromSeconds(3), ct);

            // -------- INDI --------
            bool indiOk = await TryConnectIndiAsync(ct);

            // -------- Alpaca (independent, runs even if INDI fails) --------
            await TryDiscoverAlpacaAsync(ct);

            // -------- PHD2 (independent, runs even if INDI/Alpaca fail) --
            // PHD2 lives in its own process (often on the same host as
            // Polaris) and is reachable over a local TCP socket. Dial it
            // the same way we dial INDI: short timeout, single attempt,
            // toast the outcome. If PHD2AutoStartService just spawned
            // it 2-3s earlier this catches the freshly-bound port; if
            // PHD2 isn't running the user wires it manually from GUIDE.
            await TryConnectPhd2Async(ct);

            // -------- Active rig equipment --------
            // Equipment selections in the rig might point at INDI device
            // names; without INDI they can't be bound. Skip silently when
            // INDI is down. (Alpaca-only rigs would need their own path,
            // not in scope yet; today every Select* binds via _indiClient.)
            if (indiOk) {
                await TryConnectActiveRigAsync(ct);
            }
        } catch (OperationCanceledException) {
            // shutdown
        } catch (Exception ex) {
            _logger.LogError(ex, "Hardware auto-connect crashed");
            _notify.Push("error", "Auto-connect crashed: " + ex.Message);
        }
    }

    private async Task<bool> TryConnectIndiAsync(CancellationToken ct) {
        if (_indiClient.IsConnected) {
            _notify.Push("ok", $"INDI already connected ({_indiClient.Host}:{_indiClient.Port})");
            return true;
        }
        // Retry with backoff. The original single-attempt path raced
        // with IndiWebManagerService -- on a Pi 2 boot it takes 10-20 s
        // for indi-web to spawn indiserver, and our 8 s timeout fell
        // squarely inside that gap, surfacing as the noisy
        // "Connection refused" toast every cold start.
        //
        // 10 attempts × ~6 s each (1 s connect timeout + 5 s sleep) =
        // up to ~60 s wall clock. Short connect timeout per attempt
        // keeps the loop responsive: each "indiserver not up yet"
        // probe fails fast (refused → instant) so the 5 s sleep
        // dominates and the loop sleeps gracefully waiting for the
        // socket to open. First few attempts are silent (would spam
        // toasts every retry); only the first user-visible
        // notification ("Connecting…") fires once, then either the
        // success or final-give-up toast.
        const int MaxAttempts = 10;
        const int SleepSeconds = 5;
        Exception? lastError = null;
        _notify.Push("info", $"Connecting INDI {_indiClient.Host}:{_indiClient.Port}…", 2500);
        for (int attempt = 1; attempt <= MaxAttempts; attempt++) {
            if (ct.IsCancellationRequested) return false;
            try {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
                await _indiClient.ConnectAsync(timeoutCts.Token);
                // Devices populate asynchronously as getProperties responses
                // come in. Give the server up to 2s to enumerate before we
                // try to bind rig devices, without this the device list is
                // empty and every Select* lookup misses.
                for (int i = 0; i < 20 && _indiClient.Devices.Count == 0; i++) {
                    await Task.Delay(100, ct);
                }
                _notify.Push("ok",
                    $"INDI connected ({_indiClient.Host}:{_indiClient.Port}) · {_indiClient.Devices.Count} device(s)");
                return true;
            } catch (Exception ex) {
                lastError = ex;
                // Log at Debug for the silent retries so a noisy log
                // doesn't bury actual problems; the final give-up
                // surfaces as an Information entry below.
                _logger.LogDebug(ex,
                    "INDI connect attempt {Attempt}/{Max} to {Host}:{Port} failed",
                    attempt, MaxAttempts, _indiClient.Host, _indiClient.Port);
                if (attempt < MaxAttempts) {
                    try { await Task.Delay(TimeSpan.FromSeconds(SleepSeconds), ct); }
                    catch (OperationCanceledException) { return false; }
                }
            }
        }
        _logger.LogInformation(lastError,
            "Auto-connect to INDI {Host}:{Port} failed after {Attempts} attempts",
            _indiClient.Host, _indiClient.Port, MaxAttempts);
        _notify.Push("warn",
            $"INDI unavailable at {_indiClient.Host}:{_indiClient.Port} after {MaxAttempts} retries, connect manually from Rigs.");
        return false;
    }

    private async Task TryConnectPhd2Async(CancellationToken ct) {
        if (_phd2.IsConnected) {
            _notify.Push("ok", $"PHD2 already connected ({_phd2.Host}:{_phd2.Port})");
            return;
        }
        // Honour the per-rig PHD2 host/port if a rig is active and has
        // them set; otherwise fall back to the PHD2Client defaults.
        var rig = _profiles.ActiveEquipmentProfile;
        var host = !string.IsNullOrWhiteSpace(rig?.PHD2Host) ? rig!.PHD2Host : "localhost";
        var port = rig?.PHD2Port > 0 ? rig.PHD2Port : 4400;
        try {
            _notify.Push("info", $"Connecting PHD2 {host}:{port}…", 2500);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            await _phd2.ConnectAsync(host, port, timeoutCts.Token);
            _notify.Push("ok", $"PHD2 connected ({host}:{port})");
        } catch (Exception ex) {
            _logger.LogInformation(ex, "Auto-connect to PHD2 {Host}:{Port} failed", host, port);
            _notify.Push("warn", $"PHD2 unavailable at {host}:{port}, connect manually from Guide.");
        }
    }

    private async Task TryDiscoverAlpacaAsync(CancellationToken ct) {
        try {
            _notify.Push("info", "Discovering Alpaca devices on local network…", 2500);
            var servers = await _alpaca.DiscoverServersAsync(TimeSpan.FromSeconds(3));
            int deviceCount = servers.Sum(s => s.Devices?.Count ?? 0);
            if (servers.Count == 0) {
                _notify.Push("info", "No Alpaca devices found on the LAN.");
            } else {
                _notify.Push("ok",
                    $"Alpaca: {servers.Count} server(s), {deviceCount} device(s) discovered.");
            }
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Alpaca discovery failed");
            _notify.Push("warn", "Alpaca discovery failed: " + ex.Message);
        }
    }

    private async Task TryConnectActiveRigAsync(CancellationToken ct) {
        var rig = _profiles.ActiveEquipmentProfile;
        if (rig == null) {
            _notify.Push("info", "No active rig, skipping equipment auto-connect.");
            return;
        }

        var available = new HashSet<string>(_indiClient.GetDeviceNames(), StringComparer.OrdinalIgnoreCase);

        // Each entry: friendly name shown in the toast + saved device
        // name from the rig + the bind+connect callback. Camera and
        // Telescope honour driver override; the rest are INDI-only
        // today so we don't pass a driver.
        var devices = new (string Label, string? Name, Func<string, Task> Bind)[] {
            ("Camera",       rig.Camera,      async name => { var c = _equip.SelectCamera(rig.CameraDriver ?? "indi", name);    await c.ConnectAsync(ct); }),
            ("Mount",        rig.Telescope,   async name => { var t = _equip.SelectTelescope(rig.TelescopeDriver ?? "indi", name); await t.ConnectAsync(ct); }),
            ("Focuser",      rig.Focuser,     async name => { var f = _equip.SelectFocuser(name);                                await f.ConnectAsync(ct); }),
            ("Filter wheel", rig.FilterWheel, async name => { var w = _equip.SelectFilterWheel(name);                            await w.ConnectAsync(ct); }),
            ("Rotator",      rig.Rotator,     async name => { var r = _equip.SelectRotator(name);                                await r.ConnectAsync(ct); }),
            ("Flat panel",   rig.FlatDevice,  async name => { var p = _equip.SelectFlatDevice(name);                             await p.ConnectAsync(ct); }),
            ("Dome",         rig.Dome,        async name => { var d = _equip.SelectDome(name);                                   await d.ConnectAsync(ct); }),
            ("Weather",      rig.Weather,     async name => { var w = _equip.SelectWeather(name);                                await w.ConnectAsync(ct); }),
        };

        int connected = 0, missing = 0, failed = 0;
        foreach (var (label, name, bind) in devices) {
            if (string.IsNullOrWhiteSpace(name)) continue;

            // For INDI-backed devices, validate the saved name still
            // exists on the live server before attempting connect. For
            // non-INDI camera/mount drivers (e.g. canon-edsdk), trust
            // the binder, the available[] set is INDI-only.
            bool isIndi = label switch {
                "Camera" => (rig.CameraDriver ?? "indi") == "indi",
                "Mount"  => (rig.TelescopeDriver ?? "indi") == "indi",
                _        => true,
            };
            if (isIndi && !available.Contains(name)) {
                _notify.Push("warn", $"{label} '{name}' not present on INDI server.");
                missing++;
                continue;
            }

            try {
                await bind(name);
                _notify.Push("ok", $"{label} connected: {name}");
                connected++;
            } catch (Exception ex) {
                _logger.LogInformation(ex, "Auto-connect of {Label} '{Name}' failed", label, name);
                _notify.Push("warn", $"{label} '{name}' failed: {ex.Message}");
                failed++;
            }
        }

        if (connected == 0 && missing == 0 && failed == 0) {
            _notify.Push("info", "Active rig has no saved device selections, pick devices in Rigs.");
        } else {
            _notify.Push("ok",
                $"Rig '{rig.Name}': {connected} connected, {missing} missing, {failed} failed.");
        }
    }
}
