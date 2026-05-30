using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Win32;

namespace NINA.Polaris.Services;

/// <summary>
/// Windows-only sibling of <see cref="Phd2GuiSessionService"/>. Where
/// xpra forwards an Xorg display on Linux, on Windows we host the
/// PHD2 GUI via TightVNC: TightVNC's Windows service captures the
/// desktop on 127.0.0.1:5900, the <c>/phd2-vnc-ws</c> bridge pipes
/// the raw RFB stream to a noVNC HTML5 client embedded in the GUIDE
/// tab.
///
/// Lifecycle: TightVNC installs as a Windows Service that the host
/// OS manages. Polaris doesn't spawn a process — it only verifies
/// the service exists, is running, and is listening on the loopback
/// port. Start/Stop buttons call <see cref="ServiceController"/>;
/// those calls require Polaris to be running elevated (admin), so
/// they cleanly fail with an actionable error otherwise.
///
/// Cross-platform compile: the BCL types touched here
/// (<see cref="ServiceController"/>, <see cref="Registry"/>) compile
/// on Linux but throw <see cref="PlatformNotSupportedException"/> at
/// runtime. Every method that reaches into them is annotated with
/// <see cref="SupportedOSPlatformAttribute"/> and only invoked behind
/// an <see cref="OperatingSystem.IsWindows"/> guard, so the Linux
/// build never trips the unsupported paths.
/// </summary>
public class Phd2VncSessionService : BackgroundService {
    private readonly IConfiguration _config;
    private readonly ILogger<Phd2VncSessionService> _logger;

    // ── Detection state (refreshed by ExecuteAsync) ──────────────────
    public bool TightVncInstalled { get; private set; }
    public string? TightVncVersion { get; private set; }
    public string? TightVncPath { get; private set; }
    public bool ServiceInstalled { get; private set; }
    public bool ServiceRunning { get; private set; }
    public bool Listening { get; private set; }
    public int Port { get; }
    public DateTime? LastHealthCheckAt { get; private set; }
    public string? LastError { get; private set; }

    public bool IsSupportedOs =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public string OperatingSystemDescription => RuntimeInformation.OSDescription;

    /// <summary>One-line reason the embedded GUI via VNC is unavailable
    /// on this host, or null when it should work. UI surfaces this in
    /// the GUIDE tab banner so the user gets a specific "why" instead
    /// of a generic "not supported".</summary>
    public string? UnsupportedReason {
        get {
            if (!IsSupportedOs)
                return $"Embedded PHD2 GUI via VNC requires Windows. {RuntimeInformation.OSDescription} is not supported (use xpra on Linux instead).";
            return null;
        }
    }

    /// <summary>Public-facing service name expected on the host. The
    /// TightVNC installer registers <c>tvnserver</c>. We don't try
    /// to match alternate VNC servers (UltraVNC, RealVNC) here —
    /// users running those just stop the TightVNC card from appearing
    /// and connect to their server directly; the bridge still works
    /// against whatever listens on the configured port.</summary>
    public const string ServiceName = "tvnserver";

    public Phd2VncSessionService(IConfiguration config,
                                 ILogger<Phd2VncSessionService> logger) {
        _config = config;
        _logger = logger;
        Port = _config.GetValue("Phd2Vnc:Port", 5900);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!IsSupportedOs) {
            _logger.LogInformation("Phd2VncSessionService: OS {Os} not supported (Windows only), service idle",
                RuntimeInformation.OSDescription);
            LastError = UnsupportedReason;
            return;
        }

        await RefreshDetectionAsync(stoppingToken);
        if (!TightVncInstalled) {
            _logger.LogInformation("Phd2VncSessionService: TightVNC not detected, install from https://www.tightvnc.com/download.php to enable embedded PHD2 GUI on Windows");
        }

        // 15s loop, same cadence as the xpra service. Refreshes
        // service state + TCP probe so the UI stays in sync without
        // polling endpoints.
        while (!stoppingToken.IsCancellationRequested) {
            try {
                if (TightVncInstalled && OperatingSystem.IsWindows()) {
                    RefreshServiceStateWin();
                    await ProbeListeningAsync(stoppingToken);
                }
                LastHealthCheckAt = DateTime.UtcNow;
            } catch (Exception ex) {
                _logger.LogDebug(ex, "TightVNC health probe failed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>Re-runs the install detection (registry + service +
    /// listening probe). Called by ExecuteAsync at startup and by the
    /// /api/guider/vnc-session/redetect endpoint when the user reports
    /// they just installed TightVNC.</summary>
    public async Task RefreshDetectionAsync(CancellationToken ct = default) {
        if (!OperatingSystem.IsWindows()) return;
        try {
            DetectInstallWin();
        } catch (Exception ex) {
            _logger.LogDebug(ex, "TightVNC install detection failed");
            TightVncInstalled = false;
        }
        if (TightVncInstalled) {
            try { RefreshServiceStateWin(); }
            catch (Exception ex) { _logger.LogDebug(ex, "TightVNC service-state probe failed"); }
            try { await ProbeListeningAsync(ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "TightVNC listening probe failed"); }
        }
    }

    [SupportedOSPlatform("windows")]
    private void DetectInstallWin() {
        // Multi-path probe ordered by reliability. Each probe is
        // wrapped so any single one failing (SecurityException from a
        // locked-down domain box, missing key, partial wipe, etc.)
        // falls through to the next instead of blowing up the whole
        // detection.
        //
        // Order of attack:
        //   1. HKLM\SOFTWARE\TightVNC\Server — canonical 64-bit
        //   2. HKLM\SOFTWARE\Wow6432Node\TightVNC\Server — 32-bit
        //      installer on 64-bit Windows
        //   3. HKLM 32-bit + 64-bit explicit RegistryView (catches
        //      WOW redirection quirks when the process's bitness
        //      doesn't match the installer's)
        //   4. HKCU per-user install (rare but supported by some
        //      TightVNC installer variants)
        //   5. ProgramFiles / ProgramFiles(x86) on-disk probe
        //   6. PATH scan for tvnserver.exe (last-resort)
        //
        // The Polaris user not having SOFTWARE\* read is what
        // motivated splitting this out — we just keep walking the
        // list until we find the exe.
        string? exePath =
            ReadInstallPathFromHkey(RegistryHive.LocalMachine, @"SOFTWARE\TightVNC\Server")
            ?? ReadInstallPathFromHkey(RegistryHive.LocalMachine, @"SOFTWARE\Wow6432Node\TightVNC\Server")
            ?? ReadInstallPathFromHkey(RegistryHive.LocalMachine, @"SOFTWARE\TightVNC\Server", RegistryView.Registry32)
            ?? ReadInstallPathFromHkey(RegistryHive.LocalMachine, @"SOFTWARE\TightVNC\Server", RegistryView.Registry64)
            ?? ReadInstallPathFromHkey(RegistryHive.CurrentUser,  @"SOFTWARE\TightVNC\Server")
            ?? ProbeProgramFiles()
            ?? ProbePath();

        if (exePath == null) {
            TightVncInstalled = false;
            TightVncPath = null;
            TightVncVersion = null;
            _logger.LogDebug("Phd2VncSessionService: TightVNC not found "
                + "(registry probes returned no path, Program Files + PATH "
                + "also turned up empty). Marking as not installed.");
            return;
        }

        TightVncPath = exePath;
        try {
            var fvi = FileVersionInfo.GetVersionInfo(exePath);
            TightVncVersion = fvi.ProductVersion ?? fvi.FileVersion ?? "unknown";
        } catch {
            TightVncVersion = "unknown";
        }
        TightVncInstalled = true;
        _logger.LogInformation("Phd2VncSessionService: detected TightVNC v{Ver} at {Path}",
            TightVncVersion, TightVncPath);
    }

    /// <summary>Read an InstallPath / Path value from a registry hive
    /// and return the resolved <c>tvnserver.exe</c> on disk, or null
    /// if any step fails. ALL exceptions are swallowed because this
    /// is a probe — a locked-down machine just falls through to the
    /// next probe in the chain instead of failing the whole detect.</summary>
    [SupportedOSPlatform("windows")]
    [System.Diagnostics.DebuggerNonUserCode]
    private static string? ReadInstallPathFromHkey(
            RegistryHive hive, string subkey, RegistryView view = RegistryView.Default) {
        try {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subkey);
            if (key == null) return null;
            var installPath = key.GetValue("InstallPath") as string
                           ?? key.GetValue("Path") as string;
            if (string.IsNullOrWhiteSpace(installPath)) return null;
            var candidate = Path.Combine(installPath, "tvnserver.exe");
            return File.Exists(candidate) ? candidate : null;
        } catch {
            // Security, IO, UnauthorizedAccess, the lot. Probe failed,
            // caller tries the next one.
            return null;
        }
    }

    /// <summary>Walk the canonical ProgramFiles / ProgramFiles(x86)
    /// install locations looking for tvnserver.exe. Reached when
    /// every registry probe came back empty — covers the case where
    /// TightVNC was installed normally but the polaris user can't
    /// read SOFTWARE\*.</summary>
    [SupportedOSPlatform("windows")]
    private static string? ProbeProgramFiles() {
        try {
            foreach (var root in new[] {
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetEnvironmentVariable("ProgramW6432"),
            }) {
                if (string.IsNullOrEmpty(root)) continue;
                var candidate = Path.Combine(root, "TightVNC", "tvnserver.exe");
                if (File.Exists(candidate)) return candidate;
            }
        } catch { }
        return null;
    }

    /// <summary>Last-resort: walk every directory in PATH looking
    /// for tvnserver.exe. Covers oddball portable installs where
    /// the user just dropped the binary somewhere and added it to
    /// PATH without running the proper installer.</summary>
    [SupportedOSPlatform("windows")]
    private static string? ProbePath() {
        try {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator,
                    StringSplitOptions.RemoveEmptyEntries)) {
                try {
                    var candidate = Path.Combine(dir.Trim(), "tvnserver.exe");
                    if (File.Exists(candidate)) return candidate;
                } catch { /* malformed PATH entry */ }
            }
        } catch { }
        return null;
    }

    /// <summary>Legacy thin wrapper retained for the few external
    /// call sites (tests) that still reach for it. Internally
    /// equivalent to <see cref="ReadInstallPathFromHkey"/> minus the
    /// post-open value extraction — returns the raw key handle so
    /// callers can probe arbitrary values themselves.</summary>
    [SupportedOSPlatform("windows")]
    [System.Diagnostics.DebuggerNonUserCode]
    private static RegistryKey? SafeOpenHklm(string subkey) {
        try { return Registry.LocalMachine.OpenSubKey(subkey); }
        catch (System.Security.SecurityException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (IOException) { return null; }
        catch { return null; }
    }

    [SupportedOSPlatform("windows")]
    private void RefreshServiceStateWin() {
        try {
            using var sc = new ServiceController(ServiceName);
            // Accessing Status throws InvalidOperationException when the
            // service doesn't exist — catch it as "not installed".
            var status = sc.Status;
            ServiceInstalled = true;
            ServiceRunning = status == ServiceControllerStatus.Running;
        } catch (InvalidOperationException) {
            ServiceInstalled = false;
            ServiceRunning = false;
        }
    }

    /// <summary>TCP probe against the local TightVNC server. 500 ms
    /// timeout is plenty for loopback — anything slower means the
    /// service is listening but the port handler is wedged, which the
    /// user needs to see as "not listening" so they restart the
    /// service.</summary>
    private async Task ProbeListeningAsync(CancellationToken ct) {
        try {
            using var tcp = new TcpClient();
            var connect = tcp.ConnectAsync(IPAddress.Loopback, Port, ct).AsTask();
            var timeout = Task.Delay(500, ct);
            var winner = await Task.WhenAny(connect, timeout);
            Listening = winner == connect && tcp.Connected;
        } catch {
            Listening = false;
        }
    }

    /// <summary>Start the TightVNC Windows service. Requires Polaris
    /// to be running elevated (admin). Returns false + sets LastError
    /// on permission denial so the UI can surface the actionable
    /// "rerun Polaris as admin or start the service via services.msc"
    /// hint.</summary>
    public async Task<bool> StartServiceAsync(CancellationToken ct = default) {
        if (!OperatingSystem.IsWindows()) { LastError = "Not supported on this OS"; return false; }
        if (!TightVncInstalled) { LastError = "TightVNC not installed"; return false; }
        return await Task.Run(() => {
            // Re-check inside the Task.Run lambda so the platform
            // analyzer recognizes the guard at this call site too.
            return OperatingSystem.IsWindows() && TryControlServiceWin(start: true);
        }, ct);
    }

    /// <summary>Stop the TightVNC Windows service. Same admin
    /// requirement as <see cref="StartServiceAsync"/>.</summary>
    public async Task<bool> StopServiceAsync(CancellationToken ct = default) {
        if (!OperatingSystem.IsWindows()) { LastError = "Not supported on this OS"; return false; }
        if (!TightVncInstalled) { LastError = "TightVNC not installed"; return false; }
        return await Task.Run(() => {
            return OperatingSystem.IsWindows() && TryControlServiceWin(start: false);
        }, ct);
    }

    [SupportedOSPlatform("windows")]
    private bool TryControlServiceWin(bool start) {
        try {
            using var sc = new ServiceController(ServiceName);
            var target = start
                ? ServiceControllerStatus.Running
                : ServiceControllerStatus.Stopped;
            if (sc.Status == target) {
                LastError = null;
                return true;
            }
            if (start) sc.Start();
            else sc.Stop();
            sc.WaitForStatus(target, TimeSpan.FromSeconds(10));
            ServiceRunning = sc.Status == ServiceControllerStatus.Running;
            ServiceInstalled = true;
            LastError = null;
            return sc.Status == target;
        } catch (System.ComponentModel.Win32Exception ex) when ((uint)ex.NativeErrorCode == 0x80004005 || ex.NativeErrorCode == 5) {
            // ERROR_ACCESS_DENIED (5) — Polaris not elevated.
            LastError = "Access denied. Run Polaris as administrator, " +
                        "or start/stop the TightVNC service via services.msc.";
            return false;
        } catch (InvalidOperationException) {
            LastError = $"Service '{ServiceName}' not installed.";
            ServiceInstalled = false;
            return false;
        } catch (Exception ex) {
            LastError = ex.Message;
            return false;
        }
    }
}
