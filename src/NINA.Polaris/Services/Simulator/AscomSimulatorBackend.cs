using System.Diagnostics;
using System.Text.Json;

namespace NINA.Polaris.Services.Simulator;

/// <summary>
/// Windows backend: drives the Alpaca Omni Simulator
/// (https://github.com/ASCOMInitiative/ASCOMSimulators), a single
/// .exe that exposes camera, telescope, focuser, filter wheel,
/// dome, etc. as Alpaca HTTP devices on a local port. Polaris's
/// existing AlpacaClient + AlpacaDiscovery already know how to
/// consume those — this backend only owns the launch/shutdown +
/// install-detection lifecycle on top of the Omni Sim binary.
///
/// Important differences from <see cref="IndiSimulatorBackend"/>:
/// - Omni Sim doesn't support runtime per-driver add/remove the way
///   indiserver's FIFO does. <see cref="AddDeviceAsync"/> /
///   <see cref="RemoveDeviceAsync"/> are therefore "all-or-nothing"
///   no-ops here; the UI surface should hide per-device toggles on
///   Windows (the existing checkboxes still drive what the user
///   *expects* to see in RIGS, but they don't gate Omni Sim itself).
/// - Detection looks in two places: PATH (for users who added the
///   Omni Sim install dir manually) and the standard install
///   locations under Program Files.
/// - Health probe hits the Alpaca management API
///   <c>/management/v1/configureddevices</c> — if it answers,
///   the Omni Sim is up and Polaris's regular Alpaca code path will
///   discover its devices automatically.
/// </summary>
public class AscomSimulatorBackend : ISimulatorBackend, IDisposable {
    private readonly ILogger<AscomSimulatorBackend> _logger;
    private Process? _process;
    private int _lastPort = 32323;

    public string Kind => "ascom";
    public bool IsSupported => OperatingSystem.IsWindows();
    public string DownloadInstructionsUrl =>
        "https://github.com/ASCOMInitiative/ASCOMSimulators/releases";

    /// <summary>Standard places to look for the Omni Sim binary.
    /// Order matters: PATH first (a power user who customised the
    /// install), then the official installer locations.</summary>
    internal static readonly string[] WindowsCandidatePaths = [
        "AlpacaOmniSimulator.exe",                                              // PATH
        @"C:\Program Files\ASCOM\OmniSimulators\AlpacaOmniSimulator.exe",
        @"C:\Program Files (x86)\ASCOM\OmniSimulators\AlpacaOmniSimulator.exe",
        @"C:\ProgramData\ASCOM\OmniSimulators\AlpacaOmniSimulator.exe",
    ];

    /// <summary>Alpaca device-type names the Omni Sim exposes, mapped
    /// to our canonical <see cref="SimulatorDeviceTags"/>. The Omni
    /// Sim binary serves all of them simultaneously; this map just
    /// translates between the two namespaces for status reporting.</summary>
    private static readonly Dictionary<string, string> AlpacaTypeToTag =
        new(StringComparer.OrdinalIgnoreCase) {
            ["camera"]            = SimulatorDeviceTags.Ccd,
            ["telescope"]         = SimulatorDeviceTags.Telescope,
            ["focuser"]           = SimulatorDeviceTags.Focus,
            ["filterwheel"]       = SimulatorDeviceTags.Wheel,
            ["dome"]              = SimulatorDeviceTags.Dome,
            ["observingconditions"] = SimulatorDeviceTags.Weather,
            // Omni Sim doesn't have a separate "guide camera" device
            // — the regular Camera works in guide-cam role for PHD2.
        };

    public AscomSimulatorBackend(ILogger<AscomSimulatorBackend> logger) {
        _logger = logger;
    }

    public async Task<SimulatorInstall> DetectInstallAsync(CancellationToken ct = default) {
        if (!IsSupported) {
            return new SimulatorInstall(false, null, null, [],
                "ASCOM simulator backend only runs on Windows.");
        }

        // 1. Is there a binary on the host at all?
        var path = ResolveBinaryPath();
        if (path == null) {
            return new SimulatorInstall(false, null, null, [],
                "Alpaca Omni Simulator not found. Download from " +
                "https://github.com/ASCOMInitiative/ASCOMSimulators/releases " +
                "and install to the default location, or add to PATH.");
        }

        // 2. Already running? Query the management API to discover
        //    devices. This works whether WE launched it or the user
        //    started it manually (icon in the system tray).
        var devices = await ProbeConfiguredDevicesAsync(_lastPort, ct);
        var version = await ReadVersionAsync(path, ct);

        return new SimulatorInstall(
            Installed: true,
            Version: version,
            Path: path,
            // If Omni Sim isn't running yet we can't know which device
            // categories are enabled — list the supported tags so the
            // UI can render checkboxes anyway. Once launched + probed,
            // we narrow this to what /configureddevices reports.
            AvailableDevices: devices.Count > 0
                ? devices
                : AlpacaTypeToTag.Values.Distinct().ToList(),
            Error: null);
    }

    public async Task<bool> LaunchAsync(SimulatorLaunchRequest req, CancellationToken ct = default) {
        if (!IsSupported) return false;
        _lastPort = req.Port > 0 ? req.Port : 32323;

        // Already responding? Adopt it — the user might have started
        // the Omni Sim from the start menu before opening Polaris,
        // or a previous Polaris instance left it running.
        if (await IsRunningAsync(ct)) {
            _logger.LogInformation("Alpaca Omni Simulator already responding on port {Port}.", _lastPort);
            return true;
        }

        var path = ResolveBinaryPath();
        if (path == null) {
            _logger.LogWarning("Cannot launch — Alpaca Omni Simulator not installed.");
            return false;
        }

        try {
            // Omni Sim has a GUI (system tray icon) so UseShellExecute
            // = true gives the cleanest startup. CreateNoWindow has
            // no effect on a GUI app anyway.
            var psi = new ProcessStartInfo(path) {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized
            };
            _process = Process.Start(psi);
            if (_process == null) {
                _logger.LogError("Process.Start returned null for {Path}", path);
                return false;
            }
            _logger.LogInformation("Spawned Alpaca Omni Simulator (PID {Pid}) from {Path}",
                _process.Id, path);

            // Wait for the management endpoint to answer. First start
            // takes longer than indiserver because it's a full GUI app
            // — up to 5s.
            for (int i = 0; i < 50; i++) {
                if (await IsRunningAsync(ct)) return true;
                await Task.Delay(100, ct);
            }
            _logger.LogWarning("Omni Simulator started but isn't answering on port {Port} after 5s.", _lastPort);
            return false;
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to launch Alpaca Omni Simulator");
            return false;
        }
    }

    public async Task ShutdownAsync(CancellationToken ct = default) {
        var p = _process;
        if (p == null) return;
        try {
            if (!p.HasExited) {
                // Omni Sim is a GUI app — CloseMainWindow sends the
                // proper "click the X" message which lets it persist
                // settings before exiting. Force-kill after a short
                // timeout if it hangs.
                p.CloseMainWindow();
                if (!p.WaitForExit(3000)) {
                    _logger.LogWarning("Omni Simulator didn't exit within 3s, force-killing.");
                    p.Kill(entireProcessTree: true);
                }
            }
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Omni Simulator shutdown error (ignored)");
        } finally {
            p.Dispose();
            _process = null;
        }
        await Task.CompletedTask;
    }

    public async Task<bool> IsRunningAsync(CancellationToken ct = default) {
        // Probe via the management API instead of TCP. Difference
        // matters: a TCP-bound port doesn't guarantee Alpaca is
        // actually serving (the Omni Sim might be mid-startup). The
        // management endpoint returning JSON proves it's ready for
        // real client requests.
        try {
            var devices = await ProbeConfiguredDevicesAsync(_lastPort, ct);
            return devices.Count > 0;
        } catch {
            return false;
        }
    }

    /// <summary>SIM-8 doesn't translate cleanly to Omni Sim: all
    /// devices are exposed simultaneously by the binary; there's no
    /// per-driver add/remove. We return true (idempotent no-op) so
    /// the orchestrator doesn't surface a misleading failure when
    /// the UI thinks it added a device. The Windows UI hides the
    /// per-device toggles when <see cref="Kind"/> == "ascom".</summary>
    public Task<bool> AddDeviceAsync(string device, CancellationToken ct = default)
        => Task.FromResult(IsSupported);

    public Task<bool> RemoveDeviceAsync(string device, CancellationToken ct = default)
        => Task.FromResult(IsSupported);

    // --- helpers ---

    private static string? ResolveBinaryPath() {
        foreach (var candidate in WindowsCandidatePaths) {
            // Bare filename → use the PATH lookup, otherwise check
            // the literal path.
            if (!candidate.Contains(Path.DirectorySeparatorChar)) {
                var fromPath = IndiSimulatorBackend.WhichAsync(candidate).GetAwaiter().GetResult();
                if (!string.IsNullOrEmpty(fromPath)) return fromPath;
            } else if (File.Exists(candidate)) {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>Read the Omni Sim binary's FileVersionInfo to get
    /// the install version — quicker than spawning + parsing
    /// stdout, and doesn't need the binary to be running.</summary>
    internal static Task<string?> ReadVersionAsync(string path, CancellationToken ct = default) {
        try {
            var info = FileVersionInfo.GetVersionInfo(path);
            var v = info.ProductVersion ?? info.FileVersion;
            return Task.FromResult<string?>(string.IsNullOrEmpty(v) ? null : v);
        } catch {
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>Hit the Alpaca management API + map device types to
    /// our canonical tags. Empty list means "not running OR running
    /// but reports zero devices configured".</summary>
    private static async Task<List<string>> ProbeConfiguredDevicesAsync(int port, CancellationToken ct) {
        try {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
            var url = $"http://127.0.0.1:{port}/management/v1/configureddevices";
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return [];
            var json = await resp.Content.ReadAsStringAsync(ct);
            return ParseConfiguredDevices(json);
        } catch {
            return [];
        }
    }

    /// <summary>Parse the JSON body of <c>/management/v1/configureddevices</c>
    /// — public for tests. Shape:
    /// <code>
    /// { "Value": [
    ///   { "DeviceType": "Camera", "DeviceNumber": 0, ... },
    ///   { "DeviceType": "Telescope", ... }
    /// ]}
    /// </code>
    /// Returns the SET of canonical tags discovered, deduped.</summary>
    internal static List<string> ParseConfiguredDevices(string json) {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Value", out var arr)
                || arr.ValueKind != JsonValueKind.Array) return [];
            foreach (var item in arr.EnumerateArray()) {
                if (!item.TryGetProperty("DeviceType", out var t)) continue;
                var dt = t.GetString();
                if (dt != null && AlpacaTypeToTag.TryGetValue(dt, out var tag)) {
                    result.Add(tag);
                }
            }
        } catch { /* malformed JSON — return empty */ }
        return result.OrderBy(x => x).ToList();
    }

    public void Dispose() {
        try { ShutdownAsync().GetAwaiter().GetResult(); }
        catch { /* best-effort during disposal */ }
    }
}
