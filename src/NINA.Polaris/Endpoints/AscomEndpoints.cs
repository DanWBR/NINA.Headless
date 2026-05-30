using System.Diagnostics;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// Endpoints specific to the ASCOM Platform COM-interop adapter. The
/// per-device routes (/api/camera, /api/telescope, /api/focuser,
/// /api/filterwheel) already cover select / connect / disconnect for
/// every backend; this group is for ASCOM-only actions that don't
/// fit those (running the driver's SetupDialog, platform-presence
/// probes, etc.).
/// </summary>
public static class AscomEndpoints {
    public static void MapAscomEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/ascom");

        // True when the ASCOM Platform is installed AND at least one
        // driver of ANY device type is registered. Used by the RIGS UI
        // to decide whether to render "ASCOM (COM)" entries in the
        // driver-source dropdowns at all. Cheap registry probe, never
        // throws, returns false on non-Windows.
        group.MapGet("/status", () => {
            if (!OperatingSystem.IsWindows()) {
                return Results.Ok(new {
                    supported = false,
                    platformInstalled = false,
                    reason = "ASCOM COM-interop is Windows-only."
                });
            }
            return AscomStatus();
        });

        // Open the driver's modal SetupDialog in a CHILD PROCESS so a
        // buggy driver — looking at you, ZWO's VB6-era ASCOM wrapper
        // that throws AccessViolationException from inside the setup
        // form — can only take down the helper exe, NOT the main API
        // server. The previous in-process implementation relied on
        // LegacyCorruptedStateExceptionsPolicy via AppContext.SetSwitch
        // to catch CSEs, but .NET 10 fast-fails on AVE regardless in
        // several configurations, so each Setup click was a server-
        // restart roulette for the user.
        //
        // Blocks until the helper exits (user dismisses the dialog OR
        // the driver crashes). UI shows a spinner while in-flight.
        group.MapPost("/setup/{progId}", async (string progId) => {
            if (!OperatingSystem.IsWindows())
                return Results.BadRequest(new { error = "ASCOM is Windows-only." });
            if (!IsLegalProgId(progId))
                return Results.BadRequest(new {
                    progId,
                    error = "ProgID contains invalid characters."
                });
            try {
                var (exitCode, stderr) = await RunSetupHelperAsync(progId);
                if (exitCode == 0)
                    return Results.Ok(new { progId, opened = true });
                // Non-zero exit = driver-side failure OR child crash.
                // stderr carries the human-readable message we wrote
                // from AscomSetupRunner; if the child died via CSE
                // there's no stderr — fall back to a generic hint
                // that at least tells the user the server is fine.
                var msg = string.IsNullOrWhiteSpace(stderr)
                    ? $"SetupDialog subprocess exited with code {exitCode}. "
                      + "The driver likely crashed inside its setup form. "
                      + "Polaris is still running."
                    : stderr.Trim();
                return Results.BadRequest(new {
                    progId,
                    error = msg,
                    hint = "SetupDialog requires Polaris to run in an "
                         + "interactive Windows session (not as a service). "
                         + "If this keeps happening, use the ASCOM Platform's "
                         + "Profile Explorer to configure the driver directly "
                         + "— Polaris will pick up whatever it saves."
                });
            } catch (Exception ex) {
                // Failed to even spawn the helper (rare — missing
                // ProcessPath, security policy, etc).
                return Results.BadRequest(new {
                    progId,
                    error = "Could not launch the ASCOM setup helper: " + ex.Message
                });
            }
        });
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IResult AscomStatus() => Results.Ok(new {
        supported = true,
        platformInstalled = NINA.Ascom.Com.AscomComRegistry.IsPlatformInstalled(),
    });

    /// <summary>
    /// Spawn this same executable with <c>--ascom-setup &lt;ProgID&gt;</c>,
    /// capture stderr, return the exit code. Handles both the apphost
    /// packaged case (Environment.ProcessPath points at our
    /// <c>NINA.Polaris.exe</c>) and the dev <c>dotnet run</c> case
    /// (Environment.ProcessPath points at <c>dotnet.exe</c>, we have to
    /// prepend the entry-assembly DLL path so dotnet knows what to exec).
    /// </summary>
    private static async Task<(int ExitCode, string Stderr)> RunSetupHelperAsync(string progId) {
        var procPath = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "Environment.ProcessPath is unavailable — cannot relaunch self.");
        var entryDll = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        var procName = Path.GetFileNameWithoutExtension(procPath);
        var psi = new ProcessStartInfo {
            FileName = procPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        // `dotnet run` path: ProcessPath is dotnet.exe and we need to
        // ask it to exec our entry DLL. The apphost case (release
        // builds, self-contained publishes) has ProcessPath already
        // pointing at our exe so we just pass our flags directly.
        if (string.Equals(procName, "dotnet", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(entryDll)) {
            psi.ArgumentList.Add(entryDll);
        }
        psi.ArgumentList.Add("--ascom-setup");
        psi.ArgumentList.Add(progId);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null.");
        // Drain stderr + stdout concurrently with WaitForExit so a
        // chatty driver can't deadlock us by filling the OS pipe
        // buffer (4 KB on Windows by default).
        var stderrTask = p.StandardError.ReadToEndAsync();
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        var stderr = await stderrTask;
        _ = await stdoutTask;
        return (p.ExitCode, stderr);
    }

    /// <summary>
    /// Defensive whitelist for the ProgID we hand to the child
    /// process. ASCOM ProgIDs look like
    /// <c>ASCOM.ZWO_ASI_715MC.Camera</c> — alphanumerics, dots,
    /// underscores, hyphens. Stops anyone from sneaking shell-style
    /// args into the command line via the URL path segment.
    /// </summary>
    private static bool IsLegalProgId(string progId) {
        if (string.IsNullOrWhiteSpace(progId)) return false;
        if (progId.Length > 128) return false;
        foreach (var ch in progId) {
            if (!(char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-'))
                return false;
        }
        return true;
    }
}
