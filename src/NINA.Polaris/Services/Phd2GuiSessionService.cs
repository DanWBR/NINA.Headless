using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NINA.Polaris.Services;

/// <summary>
/// Manages a long-lived xpra session that hosts PHD2's GUI under a
/// detachable Xorg-dummy display, so users can interact with PHD2's
/// native window (Profile Wizard, Brain dialog, Guiding Assistant)
/// via xpra's HTML5 client embedded in the Polaris UI.
///
/// Linux-only feature. On Windows / macOS this service short-circuits
/// to <c>XpraInstalled = false</c> and all start/stop ops return
/// "not supported on this OS". The Settings/UI surface that explicitly.
///
/// Lifecycle:
/// - Eager (Phd2Gui:AutoStart=true): spawn the session shortly after
///   Polaris startup so the GUI tab is instant.
/// - Lazy (default): user clicks "Start session" in the UI; we spawn
///   xpra + PHD2 on demand.
///
/// xpra invocation (from PHD2 maintainer's recommendation in issue #683):
/// <code>
///   xpra start :100 --start=phd2 --html=on --bind-tcp=127.0.0.1:14600 \
///        --daemon=yes --systemd-run=no --no-pulseaudio
/// </code>
/// Bind on 127.0.0.1 only, Polaris reverse-proxies /phd2-gui/* to it,
/// so the public surface (auth, TLS via Relay) stays unified.
/// </summary>
public class Phd2GuiSessionService : BackgroundService {
    private readonly IConfiguration _config;
    private readonly ILogger<Phd2GuiSessionService> _logger;

    public bool XpraInstalled { get; private set; }
    public string? XpraVersion { get; private set; }
    public string? XpraPath { get; private set; }
    /// <summary>Path of the standalone INDI Control Panel binary
    /// (<c>indi_control_panel</c>, ships with <c>indi-bin</c>) when
    /// present on PATH, else null. The Polaris UI exposes a "Launch
    /// INDI Control Panel" button in the RIGS tab that runs this
    /// inside the existing xpra session so the user can edit per-
    /// property device settings (cooler ramps, focuser presets, etc.)
    /// that PHD2's Connect dialog doesn't surface. Set once at
    /// service init.</summary>
    public string? IndiControlPanelPath { get; private set; }
    public bool IndiControlPanelInstalled => !string.IsNullOrEmpty(IndiControlPanelPath);
    public bool SessionRunning { get; private set; }
    public int DisplayNumber { get; }
    public int BindPort { get; }
    public string OperatingSystem => Environment.OSVersion.Platform.ToString();
    public bool IsSupportedOs => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>True when the current CPU architecture has a working
    /// xpra + Xorg-dummy stack. ARMv7 32-bit (Raspberry Pi 2 / 3
    /// with 32-bit Raspbian) is excluded, xpra installs from apt but
    /// crashes at session start: the dummy Xorg driver is unreliable
    /// on 32-bit ARM and several Python/GTK deps don't behave. Pi 4+
    /// with the 64-bit Raspberry Pi OS is fine.
    ///
    /// Reported separately from <see cref="IsSupportedOs"/> so the UI
    /// can show the right "why this isn't available" message instead
    /// of a generic "not Linux".</summary>
    public bool IsSupportedArch =>
        RuntimeInformation.OSArchitecture != Architecture.Arm;

    /// <summary>One-line human-readable reason the embedded GUI is
    /// unavailable on this host, or null when it should work.
    /// UI surfaces this in the GUIDE / Settings banner so the user
    /// doesn't see a generic "click Start to begin" button on a
    /// platform that physically can't run xpra.</summary>
    public string? UnsupportedReason {
        get {
            if (!IsSupportedOs)
                return $"Embedded PHD2 GUI requires Linux + xpra. {RuntimeInformation.OSDescription} is not supported.";
            if (!IsSupportedArch)
                return "Embedded PHD2 GUI requires 64-bit Linux. xpra has known issues on 32-bit ARM " +
                       "(Raspberry Pi 2 / 3 with 32-bit Raspberry Pi OS). Upgrade to 64-bit Pi OS on a Pi 4 / 5, " +
                       "or use PHD2's native window via X11 forwarding / VNC.";
            return null;
        }
    }

    public DateTime? LastHealthCheckAt { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>True when a <c>phd2</c> or <c>phd2.bin</c> process is
    /// currently alive on the host. Refreshed by the 15 s health-probe
    /// loop alongside the xpra TCP probe. Lets the UI distinguish
    /// "xpra session up, PHD2 inside" from "xpra session up but PHD2
    /// is missing" (the second happens when xpra's <c>--start=phd2</c>
    /// fails silently, or when PHD2 crashes inside the session). Both
    /// cases would otherwise show the same green pill, which is what
    /// triggered users to need shell access to debug.</summary>
    public bool Phd2Running { get; private set; }

    public Phd2GuiSessionService(IConfiguration config,
                                 ILogger<Phd2GuiSessionService> logger) {
        _config = config;
        _logger = logger;
        DisplayNumber = _config.GetValue("Phd2Gui:DisplayNumber", 100);
        BindPort      = _config.GetValue("Phd2Gui:BindPort", 14600);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!IsSupportedOs) {
            _logger.LogInformation("Phd2GuiSessionService: OS {Os} not supported (Linux only), service idle",
                RuntimeInformation.OSDescription);
            LastError = UnsupportedReason;
            return;
        }
        if (!IsSupportedArch) {
            // Don't even try to detect xpra on 32-bit ARM, install
            // succeeds via apt but session-start crashes; better to
            // surface the limitation cleanly upfront than to let the
            // user click Start and get a confusing process-died log.
            _logger.LogInformation("Phd2GuiSessionService: architecture {Arch} not supported (xpra unstable on 32-bit ARM), service idle",
                RuntimeInformation.OSArchitecture);
            LastError = UnsupportedReason;
            return;
        }

        await DetectXpraAsync(stoppingToken);
        if (!XpraInstalled) {
            _logger.LogInformation("Phd2GuiSessionService: xpra not found, install via 'sudo apt install xpra' to enable embedded PHD2 GUI");
        }

        // 3-second stagger so PHD2AutoStartService gets a head start (it may
        // be launching PHD2 itself; if we beat it, the xpra session would
        // launch its own PHD2 and we'd end up with two).
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
        catch (TaskCanceledException) { return; }

        var autoStart = _config.GetValue("Phd2Gui:AutoStart", false);
        if (XpraInstalled && autoStart) {
            _logger.LogInformation("Phd2GuiSessionService: AutoStart enabled, starting xpra session");
            try { await StartSessionAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "Auto-start of xpra session failed"); }
        }

        // Periodic health check (every 15s), refreshes SessionRunning
        // so the UI indicator stays accurate without polling.
        while (!stoppingToken.IsCancellationRequested) {
            try {
                if (XpraInstalled) await ProbeHealthAsync(stoppingToken);
            } catch (Exception ex) {
                _logger.LogDebug(ex, "xpra health probe failed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task DetectXpraAsync(CancellationToken ct) {
        try {
            var which = await RunCommandAsync("which", "xpra", ct);
            if (string.IsNullOrWhiteSpace(which.stdout)) return;
            XpraPath = which.stdout.Trim();

            var ver = await RunCommandAsync("xpra", "--version", ct);
            // `xpra --version` prints "xpra v6.0" or "xpra version 6.0", grab first token after "v"
            var line = (ver.stdout + " " + ver.stderr).Trim();
            var idx = line.IndexOf('v');
            XpraVersion = idx >= 0 ? line[(idx + 1)..].Split(new[] { ' ', '\n', '\r' })[0] : "unknown";
            XpraInstalled = true;
            _logger.LogInformation("Phd2GuiSessionService: detected xpra v{Ver} at {Path}", XpraVersion, XpraPath);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "xpra detection failed");
            XpraInstalled = false;
        }
        // Also probe for the standalone INDI Control Panel binary.
        // Ships with the `indi-bin` apt package; absence is normal on
        // hosts that only run indi-web or a non-INDI stack, so the
        // failure is silent and the UI just hides the launch button.
        try {
            var which = await RunCommandAsync("which", "indi_control_panel", ct);
            if (!string.IsNullOrWhiteSpace(which.stdout)) {
                IndiControlPanelPath = which.stdout.Trim();
                _logger.LogInformation("Phd2GuiSessionService: detected indi_control_panel at {Path}",
                    IndiControlPanelPath);
            }
        } catch (Exception ex) {
            _logger.LogDebug(ex, "indi_control_panel detection failed");
        }
    }

    /// <summary>
    /// Launches a fresh instance of <c>indi_control_panel</c> as a child
    /// window inside the existing xpra display, so it shows up in the
    /// same HTML5 client iframe the user already uses for the PHD2 GUI.
    /// No-op (returns false + sets LastError) when xpra isn't running,
    /// when the binary isn't installed, or when the OS isn't Linux.
    /// </summary>
    public async Task<bool> LaunchIndiControlPanelAsync(CancellationToken ct = default) {
        if (!IsSupportedOs)        { LastError = "OS not supported"; return false; }
        if (!XpraInstalled)        { LastError = "xpra not installed"; return false; }
        if (!IndiControlPanelInstalled) {
            LastError = "indi_control_panel not installed (apt install indi-bin)";
            return false;
        }
        if (!await ProbeHealthAsync(ct)) {
            LastError = "xpra session not running, start the embedded GUI first";
            return false;
        }
        var args = $"control :{DisplayNumber} start-child indi_control_panel";
        _logger.LogInformation("Launching indi_control_panel inside xpra session :{Display}",
            DisplayNumber);
        var res = await RunCommandAsync("xpra", args, ct, timeoutMs: 10000);
        if (res.exitCode != 0) {
            LastError = $"xpra start-child indi_control_panel exited {res.exitCode}: {res.stderr}";
            _logger.LogWarning("{Error}", LastError);
            return false;
        }
        LastError = null;
        return true;
    }

    public async Task<bool> StartSessionAsync(CancellationToken ct = default) {
        if (!IsSupportedOs) { LastError = "OS not supported"; return false; }
        if (!XpraInstalled) { LastError = "xpra not installed"; return false; }
        if (await ProbeHealthAsync(ct)) {
            _logger.LogDebug("StartSessionAsync: already running");
            return true;
        }
        var args = string.Join(' ', new[] {
            "start",
            $":{DisplayNumber}",
            "--start=phd2",
            "--html=on",
            $"--bind-tcp=127.0.0.1:{BindPort}",
            "--daemon=yes",
            "--systemd-run=no",
            "--no-pulseaudio"
        });
        _logger.LogInformation("Spawning xpra: {Args}", args);
        var res = await RunCommandAsync("xpra", args, ct, timeoutMs: 30000);
        if (res.exitCode != 0) {
            LastError = $"xpra start exited {res.exitCode}: {res.stderr}";
            _logger.LogWarning("{Error}", LastError);
            return false;
        }
        // xpra forks into the background, give it a moment then probe
        for (int i = 0; i < 20; i++) {
            try { await Task.Delay(500, ct); } catch (TaskCanceledException) { return false; }
            if (await ProbeHealthAsync(ct)) {
                _logger.LogInformation("xpra session on :{Display} listening on 127.0.0.1:{Port}",
                    DisplayNumber, BindPort);
                LastError = null;
                return true;
            }
        }
        LastError = "xpra start succeeded but TCP probe never responded";
        return false;
    }

    public async Task<bool> StopSessionAsync(CancellationToken ct = default) {
        if (!IsSupportedOs || !XpraInstalled) return false;
        var res = await RunCommandAsync("xpra", $"stop :{DisplayNumber}", ct, timeoutMs: 15000);
        if (res.exitCode != 0) {
            LastError = $"xpra stop exited {res.exitCode}: {res.stderr}";
            return false;
        }
        SessionRunning = false;
        LastError = null;
        return true;
    }

    public async Task<bool> RestartSessionAsync(CancellationToken ct = default) {
        await StopSessionAsync(ct);
        // Brief wait for xpra to release the port.
        try { await Task.Delay(2000, ct); } catch (TaskCanceledException) { return false; }
        return await StartSessionAsync(ct);
    }

    /// <summary>TCP probe, returns true if something is listening on 127.0.0.1:BindPort.</summary>
    private async Task<bool> ProbeHealthAsync(CancellationToken ct) {
        try {
            using var tcp = new TcpClient();
            var connect = tcp.ConnectAsync("127.0.0.1", BindPort, ct).AsTask();
            var timeout = Task.Delay(500, ct);
            var winner = await Task.WhenAny(connect, timeout);
            LastHealthCheckAt = DateTime.UtcNow;
            SessionRunning = winner == connect && tcp.Connected;
        } catch {
            SessionRunning = false;
        }
        // Refresh Phd2Running alongside the xpra probe. Cheap, single
        // pgrep call, no need to be conditional on SessionRunning.
        try {
            var res = await RunCommandAsync("pgrep", "-x phd2.bin", ct, timeoutMs: 2000);
            Phd2Running = res.exitCode == 0 && !string.IsNullOrWhiteSpace(res.stdout);
            if (!Phd2Running) {
                // Some distros register the binary as just 'phd2' (no .bin suffix)
                var alt = await RunCommandAsync("pgrep", "-x phd2", ct, timeoutMs: 2000);
                Phd2Running = alt.exitCode == 0 && !string.IsNullOrWhiteSpace(alt.stdout);
            }
        } catch {
            Phd2Running = false;
        }
        return SessionRunning;
    }

    /// <summary>
    /// Spawn a PHD2 process inside the already-running xpra session.
    /// Used by the UI's "Relaunch PHD2" button when the user sees the
    /// xpra session is alive but PHD2 is not (xpra's <c>--start=phd2</c>
    /// failed silently or PHD2 crashed mid-session). Does NOT restart
    /// xpra, so any other windows the user opened in the session stay.
    /// </summary>
    public async Task<bool> RelaunchPhd2Async(CancellationToken ct = default) {
        if (!IsSupportedOs) { LastError = "OS not supported"; return false; }
        if (!XpraInstalled) { LastError = "xpra not installed"; return false; }
        if (!await ProbeHealthAsync(ct)) {
            // No xpra session to talk to. Caller should start one first.
            LastError = "xpra session not running, call /start before /relaunch-phd2";
            return false;
        }
        // First, kill any orphan phd2 still hanging around. Otherwise
        // xpra start-child will spawn a second one and we get two phd2
        // processes both fighting for port 4400.
        try { await RunCommandAsync("pkill", "-x phd2.bin", ct, timeoutMs: 3000); } catch { }
        try { await RunCommandAsync("pkill", "-x phd2", ct, timeoutMs: 3000); } catch { }
        try { await Task.Delay(800, ct); } catch (TaskCanceledException) { return false; }

        var args = $"control :{DisplayNumber} start-child phd2";
        _logger.LogInformation("Relaunching PHD2 inside xpra session :{Display}", DisplayNumber);
        var res = await RunCommandAsync("xpra", args, ct, timeoutMs: 10000);
        if (res.exitCode != 0) {
            LastError = $"xpra start-child phd2 exited {res.exitCode}: {res.stderr}";
            _logger.LogWarning("{Error}", LastError);
            return false;
        }
        // Give PHD2 up to 15 s to register a process so the UI can pin
        // success without polling.
        for (int i = 0; i < 30; i++) {
            try { await Task.Delay(500, ct); } catch (TaskCanceledException) { return false; }
            await ProbeHealthAsync(ct);
            if (Phd2Running) {
                LastError = null;
                _logger.LogInformation("PHD2 relaunch confirmed (process visible)");
                return true;
            }
        }
        LastError = "PHD2 relaunch dispatched to xpra but no phd2 process appeared";
        return false;
    }

    private static async Task<(int exitCode, string stdout, string stderr)>
        RunCommandAsync(string file, string args, CancellationToken ct, int timeoutMs = 5000) {
        var psi = new ProcessStartInfo {
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        var waitTask = p.WaitForExitAsync(ct);
        var winner = await Task.WhenAny(waitTask, Task.Delay(timeoutMs, ct));
        if (winner != waitTask) {
            try { p.Kill(true); } catch { }
            return (-1, "", "Process timed out");
        }
        return (p.ExitCode, await stdoutTask, await stderrTask);
    }
}
