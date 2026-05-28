using System.Diagnostics;
using System.Globalization;

namespace NINA.Polaris.Services;

/// <summary>
/// CLOCK-1: lets the browser nudge the server's wall clock when the
/// host is offline (no NTP) and the Pi has no RTC backup battery. The
/// client sends its own UTC; the server applies it via
/// <c>timedatectl set-time</c>.
///
/// Linux only. The .deb postinst installs a polkit rule
/// (50-polaris-clock.rules) so the polaris service user can call
/// <c>org.freedesktop.timedate1.set-time</c> without a password
/// prompt. On Windows the service refuses + the UI tells the user to
/// use the OS clock settings or NTP instead.
///
/// Time changes are user-initiated only, never silent: jumping the
/// wall clock during a running sequence wrecks frame timestamps + can
/// confuse PHD2's settle / dither timing. The frontend surfaces a
/// chip when the skew crosses 30s and the user clicks Sync
/// explicitly.
/// </summary>
public class ClockSyncService {
    private readonly ILogger<ClockSyncService> _logger;

    public ClockSyncService(ILogger<ClockSyncService> logger) {
        _logger = logger;
    }

    /// <summary>True when the platform exposes a way to set the wall
    /// clock from a service. Linux + timedatectl only for v1.</summary>
    public bool IsSupported => OperatingSystem.IsLinux();

    /// <summary>Current server wall clock in UTC. Cheap; called by the
    /// 1Hz status stream.</summary>
    public DateTime ServerUtcNow() => DateTime.UtcNow;

    /// <summary>
    /// Set the system wall clock to the provided UTC. Returns a
    /// ClockSyncResult with the post-sync time and a sanity-check
    /// skew (computed from a final DateTime.UtcNow after the
    /// timedatectl call returns).
    ///
    /// Refuses on non-Linux, on missing timedatectl, and when the
    /// provided UTC is more than 10 years off (defends against the
    /// client clock being itself broken).
    /// </summary>
    public async Task<ClockSyncResult> SetUtcAsync(DateTime clientUtc, CancellationToken ct = default) {
        if (!IsSupported) {
            return ClockSyncResult.Fail("Clock sync is Linux-only. "
                + "On this platform use the OS clock settings or NTP.");
        }
        var sanity = Math.Abs((clientUtc - DateTime.UtcNow).TotalDays);
        if (sanity > 3650) {
            return ClockSyncResult.Fail("Refusing: client UTC is more "
                + $"than 10 years off ({sanity:F0} days). Check the "
                + "device clock first.");
        }

        // timedatectl wants ISO-ish "YYYY-MM-DD HH:MM:SS" in UTC. The
        // "+0000" suffix is silently ignored by some systemd versions,
        // so emit naked UTC + rely on the daemon's default UTC parse.
        var stamp = clientUtc.ToString("yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture);
        try {
            // Two-step: NTP can hold the clock, so disable it first if
            // running. Best-effort, ignore failure (most field Pis are
            // offline and never had NTP active).
            await RunAsync("timedatectl", "set-ntp false", ct,
                ignoreExit: true, timeoutMs: 5_000);
            var setResult = await RunAsync("timedatectl",
                $"set-time \"{stamp}\"", ct, timeoutMs: 10_000);
            if (setResult.ExitCode != 0) {
                _logger.LogWarning("timedatectl set-time failed: {Err}",
                    setResult.Stderr);
                return ClockSyncResult.Fail(
                    "timedatectl set-time failed: "
                    + (string.IsNullOrWhiteSpace(setResult.Stderr)
                        ? "unknown error (check polkit rule)"
                        : setResult.Stderr.Trim()));
            }
            // Confirm the change took. Round-trip skew should now be
            // tiny (sub-second + whatever drift happened during the
            // call itself).
            var after = DateTime.UtcNow;
            var newSkew = (after - clientUtc).TotalSeconds;
            _logger.LogInformation("Clock synced from client. "
                + "Old skew was substantial; post-sync residual {Skew:F2}s",
                newSkew);
            return new ClockSyncResult(
                Ok: true,
                Error: null,
                ServerUtcNow: after,
                ResidualSkewSeconds: newSkew);
        } catch (OperationCanceledException) {
            return ClockSyncResult.Fail("Cancelled");
        } catch (Exception ex) {
            _logger.LogError(ex, "Clock sync failed");
            return ClockSyncResult.Fail("Clock sync failed: " + ex.Message);
        }
    }

    private async Task<ProcessResult> RunAsync(string fileName, string args,
            CancellationToken ct, bool ignoreExit = false, int timeoutMs = 5_000) {
        var psi = new ProcessStartInfo {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Start();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try {
            await p.WaitForExitAsync(cts.Token);
        } catch (OperationCanceledException) {
            try { p.Kill(); } catch { }
            throw;
        }
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        if (!ignoreExit && p.ExitCode != 0) {
            _logger.LogDebug("{Cmd} {Args} -> exit {Code}, stderr: {Err}",
                fileName, args, p.ExitCode, stderr);
        }
        return new ProcessResult(p.ExitCode, stdout, stderr);
    }

    private record ProcessResult(int ExitCode, string Stdout, string Stderr);
}

public record ClockSyncResult(
    bool Ok,
    string? Error,
    DateTime ServerUtcNow,
    double ResidualSkewSeconds
) {
    public static ClockSyncResult Fail(string error) => new(
        Ok: false,
        Error: error,
        ServerUtcNow: DateTime.UtcNow,
        ResidualSkewSeconds: 0);
}
