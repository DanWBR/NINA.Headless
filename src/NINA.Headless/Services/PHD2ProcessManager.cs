using System.Diagnostics;
using System.Net.Sockets;

namespace NINA.Headless.Services;

/// <summary>
/// Optional subprocess manager for PHD2. When the user configures a PHD2
/// executable path AND the configured PHD2 host is loopback, this service
/// can launch / detect / shut down PHD2 as a child process so the operator
/// never has to SSH in and run it manually.
///
/// Detection logic is intentionally simple:
///   - "running" = a TCP connect to <host>:<port> succeeds
///   - "we started it" = we hold a reference to a still-alive Process
///
/// If PHD2 was started by the user manually we will see it as running but
/// will refuse to shut it down via process kill — only via the PHD2 JSON-RPC
/// 'shutdown' command, which PHD2 handles cleanly.
/// </summary>
public class PHD2ProcessManager : IDisposable {
    private readonly IConfiguration _config;
    private readonly ILogger<PHD2ProcessManager> _logger;
    private Process? _process;

    public PHD2ProcessManager(IConfiguration config, ILogger<PHD2ProcessManager> logger) {
        _config = config;
        _logger = logger;
    }

    public string ExecutablePath => _config.GetValue("PHD2:ExecutablePath", GetDefaultPath())!;
    public string DefaultHost => _config.GetValue("PHD2:Host", "localhost")!;
    public int DefaultPort => _config.GetValue("PHD2:Port", 4400);
    public int InstanceNumber => _config.GetValue("PHD2:InstanceNumber", 1);

    public bool ExecutableConfigured =>
        !string.IsNullOrEmpty(ExecutablePath) && File.Exists(ExecutablePath);

    public bool WeStartedIt => _process != null && !_process.HasExited;

    /// <summary>Lightweight liveness check: try to open the event-server port.</summary>
    public async Task<bool> IsRunningAsync(string? host = null, int? port = null) {
        var h = host ?? DefaultHost;
        var p = port ?? DefaultPort;
        try {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await tcp.ConnectAsync(h, p, cts.Token);
            return tcp.Connected;
        } catch { return false; }
    }

    /// <summary>
    /// Launch PHD2 as a child process. No-op if it's already responding on
    /// the configured port. Returns true if PHD2 is reachable after the call,
    /// false if launch failed.
    /// </summary>
    public async Task<bool> LaunchAsync(CancellationToken ct = default) {
        if (await IsRunningAsync()) {
            _logger.LogInformation("PHD2 already running on {Host}:{Port}", DefaultHost, DefaultPort);
            return true;
        }

        if (!ExecutableConfigured) {
            _logger.LogWarning("PHD2 executable not configured (PHD2:ExecutablePath)");
            return false;
        }

        if (DefaultHost != "localhost" && DefaultHost != "127.0.0.1") {
            _logger.LogWarning("Refusing to launch — PHD2 host {Host} is not loopback", DefaultHost);
            return false;
        }

        try {
            var args = $"-i {InstanceNumber}";
            var psi = new ProcessStartInfo {
                FileName = ExecutablePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = Path.GetDirectoryName(ExecutablePath) ?? ""
            };
            _process = Process.Start(psi);
            if (_process == null) {
                _logger.LogError("Process.Start returned null for {Path}", ExecutablePath);
                return false;
            }
            _logger.LogInformation("Launched PHD2 (pid {Pid}, instance {Instance})", _process.Id, InstanceNumber);
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to launch PHD2 from {Path}", ExecutablePath);
            return false;
        }

        // Wait up to 30s for the event-server port to open
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline) {
            ct.ThrowIfCancellationRequested();
            if (await IsRunningAsync()) {
                _logger.LogInformation("PHD2 ready on {Host}:{Port}", DefaultHost, DefaultPort);
                return true;
            }
            await Task.Delay(1000, ct);
        }

        _logger.LogWarning("PHD2 launched but event server didn't come up within 30s");
        return false;
    }

    /// <summary>
    /// Try to shut PHD2 down. Preferred path is the JSON-RPC 'shutdown' command
    /// (caller passes in the connected PHD2Client). If PHD2 doesn't respond
    /// within <paramref name="gracefulTimeout"/> and we launched the process,
    /// we kill it. Returns true if PHD2 is no longer reachable after the call.
    /// </summary>
    public async Task<bool> ShutdownAsync(PHD2Client? client, TimeSpan? gracefulTimeout = null, CancellationToken ct = default) {
        var timeout = gracefulTimeout ?? TimeSpan.FromSeconds(10);
        if (client != null && client.IsConnected) {
            try {
                await client.ShutdownAsync(ct);
                _logger.LogInformation("Sent PHD2 shutdown RPC");
            } catch (Exception ex) {
                _logger.LogDebug(ex, "PHD2 shutdown RPC failed (will fall through to process kill)");
            }
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline) {
            if (!await IsRunningAsync()) {
                _logger.LogInformation("PHD2 gone");
                _process = null;
                return true;
            }
            await Task.Delay(500, ct);
        }

        if (WeStartedIt) {
            try {
                _logger.LogWarning("PHD2 didn't exit cleanly; killing pid {Pid}", _process!.Id);
                _process.Kill(entireProcessTree: true);
                _process = null;
                return true;
            } catch (Exception ex) {
                _logger.LogError(ex, "Failed to kill PHD2 process");
            }
        } else {
            _logger.LogWarning("PHD2 still running and we don't own its process — manual intervention needed");
        }
        return !await IsRunningAsync();
    }

    private static string GetDefaultPath() {
        if (OperatingSystem.IsWindows()) {
            var p86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var candidate = Path.Combine(p86, "PHDGuiding2", "phd2.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return OperatingSystem.IsWindows() ? "" : "/usr/bin/phd2";
    }

    public void Dispose() {
        // Don't kill the process on shutdown — the user may want to keep
        // guiding running across NINA Headless restarts.
        _process?.Dispose();
    }
}
