using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// REST surface for the embedded indi-web manager (a.k.a.
/// indiwebmanager). The same shape as
/// <c>/api/guider/gui-session/*</c> for the xpra-hosted PHD2 GUI —
/// status, start, stop, restart — so the frontend can poll a
/// single "is the embedded driver-management UI ready" indicator
/// and dispatch lifecycle commands without reimplementing
/// transport for each service.
///
/// Wired in Program.cs via <see cref="MapIndiWebEndpoints"/>. The
/// actual reverse-proxy that serves the iframe content lives in
/// Program.cs as well (<c>app.Map("/indi-web/{**rest}")</c>), this
/// file is only the control plane.
/// </summary>
public static class IndiWebEndpoints {
    public static void MapIndiWebEndpoints(this IEndpointRouteBuilder app) {
        var group = app.MapGroup("/api/indi/web");

        group.MapGet("/status", (IndiWebManagerService svc) => Results.Ok(new {
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            supportedOs = svc.IsSupportedOs,
            installed = svc.Installed,
            version = svc.Version,
            executablePath = svc.ExecutablePath,
            running = svc.Running,
            bindAddress = svc.BindAddress,
            bindPort = svc.BindPort,
            lastHealthCheckAt = svc.LastHealthCheckAt,
            lastError = svc.LastError,
            unsupportedReason = svc.UnsupportedReason,
            // Hint URL the UI iframes, points to the Polaris reverse
            // proxy so the iframe stays same-origin (Bottle session
            // cookies + any XHR indi-web makes to itself work).
            embedUrl = "/indi-web/",
        }));

        group.MapPost("/start", async (IndiWebManagerService svc) => {
            if (!svc.IsSupportedOs) {
                return Results.Json(
                    new { error = svc.UnsupportedReason ?? "Not supported on this OS" },
                    statusCode: 501);
            }
            if (!svc.Installed) {
                return Results.Json(
                    new { error = "indi-web not installed. Run: pip install indiweb" },
                    statusCode: 501);
            }
            var ok = await svc.StartAsync();
            return Results.Ok(new { running = ok, error = ok ? null : svc.LastError });
        });

        group.MapPost("/stop", async (IndiWebManagerService svc) => {
            if (!svc.IsSupportedOs || !svc.Installed) {
                return Results.Json(new { error = "Not supported" }, statusCode: 501);
            }
            var ok = await svc.StopAsync();
            return Results.Ok(new { stopped = ok, error = ok ? null : svc.LastError });
        });

        group.MapPost("/restart", async (IndiWebManagerService svc) => {
            if (!svc.IsSupportedOs || !svc.Installed) {
                return Results.Json(new { error = "Not supported" }, statusCode: 501);
            }
            var ok = await svc.RestartAsync();
            return Results.Ok(new { running = ok, error = ok ? null : svc.LastError });
        });

        // ----- INDI Control Panel (indi_control_panel) inside the
        // shared xpra display ---------------------------------------------
        // Reuses the Phd2GuiSessionService (the xpra display owner) to
        // spawn a second window in the same session. The RIGS tab's
        // "INDI Control Panel" sub-tab iframes the same /phd2-gui/
        // proxy and the user gets the rich INDI properties tree.
        // Status surfaces the three gating bits the UI needs: OS
        // support, xpra running, indi_control_panel installed.
        var cpGroup = app.MapGroup("/api/indi/cp");

        cpGroup.MapGet("/status", (Phd2GuiSessionService gui) => Results.Ok(new {
            supportedOs = gui.IsSupportedOs,
            supportedArch = gui.IsSupportedArch,
            xpraInstalled = gui.XpraInstalled,
            xpraVersion = gui.XpraVersion,
            sessionRunning = gui.SessionRunning,
            indiControlPanelInstalled = gui.IndiControlPanelInstalled,
            indiControlPanelPath = gui.IndiControlPanelPath,
            embedUrl = "/phd2-gui/",
            unsupportedReason = gui.UnsupportedReason,
            lastError = gui.LastError,
        }));

        cpGroup.MapPost("/launch", async (Phd2GuiSessionService gui) => {
            if (!gui.IsSupportedOs) {
                return Results.Json(
                    new { error = gui.UnsupportedReason ?? "Not supported on this OS" },
                    statusCode: 501);
            }
            if (!gui.XpraInstalled) {
                return Results.Json(
                    new { error = "xpra not installed (apt install xpra)" },
                    statusCode: 501);
            }
            if (!gui.IndiControlPanelInstalled) {
                return Results.Json(
                    new { error = "indi_control_panel not installed (apt install indi-bin)" },
                    statusCode: 501);
            }
            var ok = await gui.LaunchIndiControlPanelAsync();
            return Results.Ok(new { launched = ok, error = ok ? null : gui.LastError });
        });
    }
}
