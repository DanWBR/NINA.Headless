using NINA.Headless.Services;

namespace NINA.Headless.Endpoints;

public static class GuiderEndpoints {
    public static void MapGuiderEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/guider");

        group.MapGet("/status", (PHD2Client phd2) => {
            if (!phd2.IsConnected)
                return Results.Ok(new {
                    connected = false,
                    appState = "Stopped"
                });

            return Results.Ok(new {
                connected = true,
                host = phd2.Host,
                port = phd2.Port,
                appState = phd2.AppState,
                guiding = phd2.IsGuiding,
                calibrating = phd2.IsCalibrating,
                paused = phd2.IsPaused,
                looping = phd2.IsLooping,
                settling = phd2.IsSettling,
                pixelScale = phd2.PixelScale,
                rmsRA = phd2.RmsRA,
                rmsDec = phd2.RmsDec,
                rmsTotal = phd2.RmsTotal,
                peakRA = phd2.PeakRA,
                peakDec = phd2.PeakDec,
                stepCount = phd2.RecentSteps.Count,
                lastAlert = phd2.LastAlert,
                lastAlertAt = phd2.LastAlertAt,
                lastSettleStatus = phd2.LastSettleStatus,
                calibration = phd2.Calibration
            });
        });

        group.MapGet("/equipment", async (PHD2Client phd2) => {
            if (!phd2.IsConnected)
                return Results.Ok(new { connected = false });
            var equip = await phd2.GetCurrentEquipmentAsync();
            return Results.Ok(new {
                connected = true,
                camera = equip?.Camera,
                mount = equip?.Mount,
                auxMount = equip?.AuxMount,
                ao = equip?.AO
            });
        });

        group.MapGet("/steps", (PHD2Client phd2, int? limit) => {
            var snapshot = phd2.SnapshotSteps();
            var take = limit.HasValue && limit.Value > 0 ? Math.Min(limit.Value, snapshot.Count) : snapshot.Count;
            var slice = snapshot.Skip(Math.Max(0, snapshot.Count - take)).Select(s => new {
                t = ((DateTimeOffset)s.Timestamp).ToUnixTimeMilliseconds(),
                ra = s.RaArcsec,
                dec = s.DecArcsec,
                snr = s.SNR
            });
            return Results.Ok(new { count = snapshot.Count, steps = slice });
        });

        group.MapPost("/connect", async (PHD2Client phd2, ConnectGuiderRequest? request) => {
            var host = string.IsNullOrWhiteSpace(request?.Host) ? "localhost" : request!.Host!;
            var port = request?.Port is > 0 ? request.Port!.Value : 4400;
            try {
                await phd2.ConnectAsync(host, port);
                return Results.Ok(new { status = "connected", host, port, appState = phd2.AppState });
            } catch (Exception ex) {
                return Results.Problem($"PHD2 connect failed: {ex.Message}");
            }
        });

        group.MapPost("/disconnect", async (PHD2Client phd2) => {
            await phd2.DisconnectAsync();
            return Results.Ok(new { status = "disconnected" });
        });

        group.MapPost("/guide", async (PHD2Client phd2, GuideRequest? request) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            try {
                await phd2.StartGuidingAsync(
                    settlePixels: request?.SettlePixels ?? 1.5,
                    settleTime: request?.SettleTime ?? 10,
                    settleTimeout: request?.SettleTimeout ?? 40,
                    recalibrate: request?.Recalibrate ?? false);
                return Results.Ok(new { status = "guide_started" });
            } catch (Exception ex) {
                return Results.Problem(ex.Message);
            }
        });

        group.MapPost("/stop", async (PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            try {
                await phd2.StopAsync();
                return Results.Ok(new { status = "stopped" });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/loop", async (PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            try {
                await phd2.LoopAsync();
                return Results.Ok(new { status = "looping" });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/pause", async (PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            try {
                await phd2.PauseAsync();
                return Results.Ok(new { status = "paused" });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/resume", async (PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            try {
                await phd2.ResumeAsync();
                return Results.Ok(new { status = "resumed" });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/dither", async (PHD2Client phd2, DitherRequest? request) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            try {
                await phd2.DitherAsync(
                    pixels: request?.Pixels ?? 5.0,
                    raOnly: request?.RaOnly ?? false,
                    settlePixels: request?.SettlePixels ?? 1.5,
                    settleTime: request?.SettleTime ?? 10,
                    settleTimeout: request?.SettleTimeout ?? 40);
                return Results.Ok(new { status = "dither_requested" });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/exposure/{ms:int}", async (int ms, PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            try {
                await phd2.SetExposureAsync(ms);
                return Results.Ok(new { exposure = ms });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/find-star", async (PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            try {
                await phd2.AutoSelectStarAsync();
                return Results.Ok(new { status = "find_star" });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/clear-calibration", async (PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            try {
                await phd2.ClearCalibrationAsync();
                return Results.Ok(new { status = "calibration_cleared" });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/clear-history", (PHD2Client phd2) => {
            phd2.ClearStepHistory();
            return Results.Ok(new { status = "cleared" });
        });

        // ---- Profile management ----

        group.MapGet("/profiles", async (PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            try {
                var profiles = await phd2.GetProfilesAsync();
                var current = await phd2.GetCurrentProfileAsync();
                return Results.Ok(new { current, profiles });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/profile/{id:int}", async (int id, PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            try {
                await phd2.SetProfileAsync(id);
                return Results.Ok(new { status = "profile_switched", profileId = id });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        // ---- Equipment connect/disconnect (PHD2's own equipment) ----

        group.MapGet("/equipment/connected", async (PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.Ok(new { connected = false });
            try { return Results.Ok(new { connected = await phd2.GetConnectedAsync() }); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/equipment/connect", async (PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            try { await phd2.SetConnectedAsync(true); return Results.Ok(new { connected = true }); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/equipment/disconnect", async (PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            try { await phd2.SetConnectedAsync(false); return Results.Ok(new { connected = false }); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        // ---- Exposure ----

        group.MapGet("/exposure", async (PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            try {
                var current = await phd2.GetExposureAsync();
                var available = await phd2.GetExposureDurationsAsync();
                return Results.Ok(new { current, available });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/exposure/set/{ms:int}", async (int ms, PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            try {
                await phd2.SetExposureMsAsync(ms);
                return Results.Ok(new { exposure = ms });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        // ---- Dec guide mode ----

        group.MapGet("/dec-mode", async (PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            try { return Results.Ok(new { mode = await phd2.GetDecGuideModeAsync() }); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/dec-mode/{mode}", async (string mode, PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            if (mode is not ("Auto" or "North" or "South" or "Off"))
                return Results.BadRequest(new { error = "mode must be Auto/North/South/Off" });
            try { await phd2.SetDecGuideModeAsync(mode); return Results.Ok(new { mode }); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        // ---- Process lifecycle (launch / shutdown PHD2 itself) ----

        group.MapGet("/process/status", async (PHD2ProcessManager pm) => Results.Ok(new {
            executableConfigured = pm.ExecutableConfigured,
            executablePath = pm.ExecutablePath,
            running = await pm.IsRunningAsync(),
            weStartedIt = pm.WeStartedIt
        }));

        group.MapPost("/process/launch", async (PHD2ProcessManager pm) => {
            try {
                var ok = await pm.LaunchAsync();
                return Results.Ok(new { launched = ok, running = await pm.IsRunningAsync() });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        group.MapPost("/process/shutdown", async (PHD2ProcessManager pm, PHD2Client phd2) => {
            try {
                var ok = await pm.ShutdownAsync(phd2);
                return Results.Ok(new { stopped = ok });
            } catch (Exception ex) { return Results.Problem(ex.Message); }
        });
    }

    public record ConnectGuiderRequest(string? Host, int? Port);
    public record GuideRequest(double? SettlePixels, int? SettleTime, int? SettleTimeout, bool? Recalibrate);
    public record DitherRequest(double? Pixels, bool? RaOnly, double? SettlePixels, int? SettleTime, int? SettleTimeout);
}
