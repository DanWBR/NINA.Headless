using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NINA.Polaris.Services;

/// <summary>
/// Manages a long-lived indi-web (a.k.a. indiwebmanager,
/// https://github.com/knro/indiwebmanager) process bound to
/// 127.0.0.1:8624. The Polaris frontend embeds it via an iframe at
/// <c>/indi-web/</c> (reverse-proxied in Program.cs) so users can
/// start/stop/enable INDI drivers from the same browser they use
/// for capture, without ssh-ing into the host to edit indiserver
/// command lines.
///
/// Why a separate service even though indi-web is "just a Python
/// webapp": it doesn't daemonize itself (no <c>--daemon</c> flag),
/// so we have to own the process lifecycle. Plus we want a single
/// place to gate the "auto-start on Polaris boot" toggle, the
/// install detection (so the UI can show a "pip install
/// indiwebmanager" banner when missing), and the TCP health probe
/// (so the iframe shows a clear "starting / running / down"
/// indicator instead of a generic browser-side fetch error).
///
/// Linux + macOS only. Windows technically can run indi-web via pip
/// + WSL, but indiserver itself is Linux/macOS so embedding the
/// driver-management UI without a working server is misleading. On
/// Windows the service short-circuits to <c>Installed = false</c>
/// and the UI surfaces "not supported on this OS".
///
/// Coexistence with <see cref="Simulator.SimulatorService"/>: both
/// want to own the indiserver process. When indi-web is the active
/// owner (running) the SimulatorService MUST route its start/stop
/// driver commands through indi-web's REST API instead of the
/// indiserver FIFO it normally talks to — otherwise the two will
/// race on the same FIFO and one of them loses. INDI-WEB-4 wires
/// that delegation; until then the user picks one or the other.
/// </summary>
public class IndiWebManagerService : BackgroundService {
    private readonly IConfiguration _config;
    private readonly ILogger<IndiWebManagerService> _logger;
    private Process? _process;

    /// <summary>True when <c>indi-web</c> is on PATH (or at the
    /// path explicitly configured via IndiWeb:ExecutablePath).</summary>
    public bool Installed { get; private set; }
    public string? Version { get; private set; }
    public string? ExecutablePath { get; private set; }

    /// <summary>True when something is listening on the bound port
    /// — refreshed by the 15 s health-probe loop.</summary>
    public bool Running { get; private set; }
    public int BindPort { get; }
    public string BindAddress { get; }
    public DateTime? LastHealthCheckAt { get; private set; }
    public string? LastError { get; private set; }

    public string OperatingSystem => Environment.OSVersion.Platform.ToString();
    public bool IsSupportedOs =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <summary>Human-readable reason indi-web is unavailable on
    /// this host, or null when it should work. The UI shows this in
    /// the Settings + RIGS banner so users on Windows don't see a
    /// stuck "click to start" button.</summary>
    public string? UnsupportedReason {
        get {
            if (!IsSupportedOs) {
                return $"INDI Web Manager requires Linux or macOS (indiserver is not packaged for Windows). " +
                       $"This host is {RuntimeInformation.OSDescription}.";
            }
            return null;
        }
    }

    public IndiWebManagerService(IConfiguration config,
                                  ILogger<IndiWebManagerService> logger) {
        _config = config;
        _logger = logger;
        BindPort = _config.GetValue("IndiWeb:Port", 8624);
        // Always loopback by default — indi-web has no auth, and the
        // user reaches it via Polaris's reverse-proxy (which IS
        // gated by the Relay's token if enabled). Letting it bind on
        // 0.0.0.0 would re-expose driver control to the LAN.
        BindAddress = _config.GetValue("IndiWeb:BindAddress", "127.0.0.1")!;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!IsSupportedOs) {
            _logger.LogInformation("IndiWebManagerService: OS {Os} not supported (Linux/macOS only), service idle",
                RuntimeInformation.OSDescription);
            LastError = UnsupportedReason;
            return;
        }

        await DetectAsync(stoppingToken);
        if (!Installed) {
            _logger.LogInformation(
                "IndiWebManagerService: indi-web not found, install via " +
                "'pip install indiwebmanager' to enable embedded INDI driver management");
        }

        // 3 s stagger after Polaris boot so PHD2 / simulator services
        // get out of the way before indi-web prints to stdout in the
        // log — keeps the startup banner readable.
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
        catch (TaskCanceledException) { return; }

        var autoStart = _config.GetValue("IndiWeb:AutoStart", false);
        if (Installed && autoStart) {
            _logger.LogInformation("IndiWebManagerService: AutoStart enabled, launching indi-web");
            try { await StartAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "Auto-start of indi-web failed"); }
        }

        // Periodic health check (15 s) keeps Running fresh for the
        // UI status pill. Also catches the case where the user (or
        // an OOM killer) killed the indi-web process out of band —
        // we'll notice within 15 s and surface it.
        while (!stoppingToken.IsCancellationRequested) {
            try {
                if (Installed) await ProbeHealthAsync(stoppingToken);
            } catch (Exception ex) {
                _logger.LogDebug(ex, "indi-web health probe failed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task DetectAsync(CancellationToken ct) {
        try {
            // Explicit path wins over PATH lookup so an operator
            // running indi-web from a venv can point us at the
            // absolute binary. Otherwise just `which indi-web`.
            var explicitPath = _config.GetValue<string?>("IndiWeb:ExecutablePath", null);
            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) {
                ExecutablePath = explicitPath;
            } else {
                var which = await RunCommandAsync("which", "indi-web", ct);
                if (string.IsNullOrWhiteSpace(which.stdout)) {
                    Installed = false;
                    return;
                }
                ExecutablePath = which.stdout.Trim();
            }

            // indi-web has a `--version` flag in current builds, but
            // older releases don't, so fall back to "pip show
            // indiwebmanager" if the binary doesn't print one.
            var ver = await RunCommandAsync(ExecutablePath, "--version", ct);
            Version = (ver.stdout + " " + ver.stderr).Trim();
            if (string.IsNullOrEmpty(Version) || ver.exitCode != 0) {
                var pip = await RunCommandAsync("pip", "show indiwebmanager", ct);
                var line = pip.stdout
                    .Split('\n')
                    .FirstOrDefault(l => l.StartsWith("Version:",
                        StringComparison.OrdinalIgnoreCase));
                Version = line?["Version:".Length..].Trim() ?? "unknown";
            }
            Installed = true;
            _logger.LogInformation("IndiWebManagerService: detected indi-web {Ver} at {Path}",
                Version, ExecutablePath);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "indi-web detection failed");
            Installed = false;
        }
    }

    public async Task<bool> StartAsync(CancellationToken ct = default) {
        if (!IsSupportedOs) { LastError = "OS not supported"; return false; }
        if (!Installed) { LastError = "indi-web not installed"; return false; }
        if (await ProbeHealthAsync(ct)) {
            _logger.LogDebug("StartAsync: already running");
            return true;
        }

        // Unlike xpra, indi-web runs in the foreground; we own the
        // process and have to keep the handle around. Redirect
        // stdout/stderr to /dev/null-ish (no UseShellExecute, no
        // RedirectStandardOutput → child writes to our terminal,
        // visible in the systemd journal when Polaris runs as a
        // unit). Working dir = home so any default conf files land
        // somewhere predictable.
        var args = $"--port {BindPort} --host {BindAddress}";
        _logger.LogInformation("Spawning indi-web: {Path} {Args}", ExecutablePath, args);
        try {
            var psi = new ProcessStartInfo {
                FileName = ExecutablePath!,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile),
            };
            _process = Process.Start(psi);
            if (_process == null) {
                LastError = "Process.Start returned null";
                return false;
            }
            _logger.LogInformation("indi-web pid {Pid}", _process.Id);
        } catch (Exception ex) {
            LastError = $"Failed to launch indi-web: {ex.Message}";
            _logger.LogWarning(ex, "indi-web launch failed");
            return false;
        }

        // Wait up to 20 s for the HTTP server to come up. Bottle (the
        // framework indi-web uses) prints its "running on..." banner
        // after maybe a second of startup; allow generous slack for
        // slow Pi hardware.
        for (int i = 0; i < 40; i++) {
            try { await Task.Delay(500, ct); } catch (TaskCanceledException) { return false; }
            if (_process?.HasExited == true) {
                LastError = $"indi-web exited prematurely (code {_process.ExitCode})";
                _logger.LogWarning("{Error}", LastError);
                return false;
            }
            if (await ProbeHealthAsync(ct)) {
                _logger.LogInformation("indi-web listening on {Host}:{Port}",
                    BindAddress, BindPort);
                LastError = null;
                return true;
            }
        }

        LastError = "indi-web started but TCP probe never responded";
        return false;
    }

    public Task<bool> StopAsync(CancellationToken ct = default) {
        if (!IsSupportedOs) return Task.FromResult(false);
        if (_process == null || _process.HasExited) {
            Running = false;
            return Task.FromResult(true);
        }
        try {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
            Running = false;
            _process = null;
            LastError = null;
            return Task.FromResult(true);
        } catch (Exception ex) {
            LastError = $"Failed to stop indi-web: {ex.Message}";
            _logger.LogWarning(ex, "indi-web stop failed");
            return Task.FromResult(false);
        }
    }

    public async Task<bool> RestartAsync(CancellationToken ct = default) {
        await StopAsync(ct);
        try { await Task.Delay(1500, ct); } catch (TaskCanceledException) { return false; }
        return await StartAsync(ct);
    }

    /// <summary>TCP probe — true if something is listening on
    /// BindAddress:BindPort. Cheap (single connect + 500 ms cap)
    /// so we can call it from the 15 s health loop without making
    /// the log noisy.</summary>
    private async Task<bool> ProbeHealthAsync(CancellationToken ct) {
        try {
            using var tcp = new TcpClient();
            var connect = tcp.ConnectAsync(BindAddress, BindPort, ct).AsTask();
            var timeout = Task.Delay(500, ct);
            var winner = await Task.WhenAny(connect, timeout);
            LastHealthCheckAt = DateTime.UtcNow;
            Running = winner == connect && tcp.Connected;
            return Running;
        } catch {
            Running = false;
            return false;
        }
    }

    private static async Task<(int exitCode, string stdout, string stderr)>
        RunCommandAsync(string file, string args, CancellationToken ct,
                        int timeoutMs = 5000) {
        var psi = new ProcessStartInfo {
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = new Process { StartInfo = psi };
        try {
            p.Start();
        } catch {
            return (-1, "", "");
        }
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
