using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

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
            if (!phd2.IsConnected) {
                // Match the connected-but-empty shape so the UI doesn't
                // have to special-case "not connected" — empty list +
                // null current is the right thing to show.
                return Results.Ok(new {
                    current = (PHD2Profile?)null,
                    profiles = System.Array.Empty<PHD2Profile>(),
                    warning = "PHD2 not connected"
                });
            }
            try {
                var profiles = await phd2.GetProfilesAsync();
                var current = await phd2.GetCurrentProfileAsync();
                return Results.Ok(new { current, profiles });
            } catch (Exception ex) {
                // PHD2 can transiently reject get_profile{,s} when busy
                // (mid-calibration, equipment in flux). Don't turn that
                // into a 500 — the frontend polls this on every WS
                // false→true transition + on user actions, and a 500
                // surfaces as a scary "Failed to load PHD2 profiles"
                // toast even though the connection is fine. Return
                // empty + warning instead so the dropdown shows blank
                // and self-heals on the next successful fetch.
                return Results.Ok(new {
                    current = (PHD2Profile?)null,
                    profiles = System.Array.Empty<PHD2Profile>(),
                    warning = ex.Message
                });
            }
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

        // Detected install info — the UI uses this on startup to either show
        // "PHD2 detected at <path>" or "PHD2 not installed — download here".
        group.MapGet("/install-info", (PHD2ProcessManager pm, IConfiguration config) => {
            var configured = config.GetValue<string?>("PHD2:ExecutablePath");
            var resolved = pm.ExecutablePath;
            var installed = pm.ExecutableConfigured;
            var os = OperatingSystem.IsWindows() ? "windows"
                  : OperatingSystem.IsMacOS() ? "macos"
                  : "linux";
            return Results.Ok(new {
                installed,
                resolvedPath = resolved,
                configuredPath = configured,
                autoStart = config.GetValue("PHD2:AutoStart", false),
                host = pm.DefaultHost,
                port = pm.DefaultPort,
                instanceNumber = pm.InstanceNumber,
                downloadUrl = PHD2ProcessManager.GetDownloadUrl(),
                os,
                searchedPaths = PHD2ProcessManager.EnumerateCandidatePaths().ToArray()
            });
        });

        // Toggle PHD2:AutoStart at runtime. Persisted via ProfileService so
        // the choice survives restarts. Takes effect on next app start (and
        // the user can /process/launch right now for the current session).
        group.MapPost("/auto-start/{enabled:bool}", (bool enabled, ProfileService profiles) => {
            profiles.Active.PHD2AutoStart = enabled;
            profiles.Save();
            return Results.Ok(new { autoStart = enabled });
        });

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

        // ----- PH2X-3: rig <-> PHD2 profile sync -----

        // Force a sync of a specific rig (or the active rig if rigId
        // omitted) to PHD2. Used by the UI button "Sync now" and as a
        // manual recovery after the user creates a profile in PHD2's GUI.
        group.MapPost("/profile/sync", async (SyncProfileRequest? req,
                                              PHD2ProfileSyncService sync,
                                              ProfileService profiles) => {
            var rigId = req?.RigId;
            var rig = string.IsNullOrEmpty(rigId)
                ? profiles.ActiveEquipmentProfile
                : profiles.ListEquipmentProfiles().FirstOrDefault(r => r.Id == rigId);
            if (rig == null) return Results.NotFound(new { error = "Rig not found" });
            var result = await sync.SyncRigToProfileAsync(rig, CancellationToken.None);
            return Results.Ok(result);
        });

        // Read-only — what's the last sync status? UI shows the indicator
        // chip (ok/error/missing-profile) based on this.
        group.MapGet("/profile/sync/status", (PHD2ProfileSyncService sync) =>
            Results.Ok(sync.CurrentStatus));

        // ----- PH2X-4: Smart calibration orchestrator -----

        // Kick a fresh PHD2 calibration. Body is SmartCalibrateOptions
        // (all fields optional with sensible defaults — see record).
        group.MapPost("/calibrate/smart", (SmartCalibrateOptions? opts,
                                          PHD2CalibrationOrchestrator orch) => {
            var job = orch.StartJob(opts ?? new SmartCalibrateOptions());
            return Results.Accepted($"/api/guider/calibrate/smart/{job.Id}",
                new { jobId = job.Id, phase = job.State.ToString() });
        });

        group.MapGet("/calibrate/smart/{jobId}", (string jobId,
                                                  PHD2CalibrationOrchestrator orch) => {
            var job = orch.GetJob(jobId);
            if (job == null) return Results.NotFound(new { error = "Job not found" });
            return Results.Ok(new {
                id = job.Id,
                phase = job.State.ToString(),
                pixelScale = job.PixelScale,
                calibrationStepMs = job.CalibrationStepMs,
                calibration = job.Calibration,
                error = job.Error,
                lastAlert = job.LastAlert,
                warnings = job.Warnings,
                startedAt = job.StartedAt,
                completedAt = job.CompletedAt,
                done = job.State == CalibrationPhase.Ok || job.State == CalibrationPhase.Fail
            });
        });

        group.MapPost("/calibrate/smart/{jobId}/abort", (string jobId,
                                                         PHD2CalibrationOrchestrator orch) => {
            orch.Abort(jobId);
            return Results.Ok(new { aborted = true });
        });

        // ----- PH2X-5: Algorithm presets + live param tuning -----

        // Built-in presets table (Default / Reactive / Smooth). UI populates
        // the preset pill from this. "Custom" is implicit — a rig with a
        // populated PHD2CustomAlgoParams bag.
        group.MapGet("/algo-presets", () => Results.Ok(new {
            names = PHD2AlgoPresets.BuiltinNames,
            presets = PHD2AlgoPresets.BuiltinNames.Select(n => {
                var p = PHD2AlgoPresets.GetBuiltin(n)!;
                return new {
                    name = p.Name,
                    description = p.Description,
                    @params = p.Params.Select(x => new { axis = x.Axis, name = x.Name, value = x.Value })
                };
            })
        }));

        // Apply a preset to the live PHD2 + persist as the active rig's
        // PHD2AlgoPreset. Works even when PHD2 is mid-guiding (preset
        // tweaks take effect on the next correction).
        group.MapPost("/algo-preset/{name}", async (string name,
                                                    PHD2Client phd2,
                                                    ProfileService profiles) => {
            if (!phd2.IsConnected)
                return Results.BadRequest(new { error = "PHD2 not connected" });
            var rig = profiles.ActiveEquipmentProfile;
            // "Custom" → apply the per-rig bag; built-in → apply curated table.
            var warnings = new List<string>();
            if (string.Equals(name, PHD2AlgoPresets.CustomPresetName, StringComparison.OrdinalIgnoreCase)) {
                foreach (var kv in rig.PHD2CustomAlgoParams) {
                    var sep = kv.Key.IndexOf(':');
                    if (sep <= 0) continue;
                    var ok = await phd2.SetAlgoParamAsync(kv.Key[..sep], kv.Key[(sep + 1)..], kv.Value);
                    if (!ok) warnings.Add($"Skipped {kv.Key}");
                }
            } else {
                var preset = PHD2AlgoPresets.GetBuiltin(name);
                if (preset == null) return Results.BadRequest(new { error = $"Unknown preset '{name}'" });
                foreach (var p in preset.Params) {
                    var ok = await phd2.SetAlgoParamAsync(p.Axis, p.Name, p.Value);
                    if (!ok) warnings.Add($"Skipped {p.Axis}/{p.Name}");
                }
            }
            profiles.UpdateEquipmentProfile(rig.Id, r => r.PHD2AlgoPreset = name);
            return Results.Ok(new { applied = name, warnings });
        });

        // Read live algorithm-parameter values from PHD2 for both axes.
        // UI's Advanced disclosure lists these knobs with current values.
        group.MapGet("/algo-params", async (PHD2Client phd2) => {
            if (!phd2.IsConnected) return Results.Ok(new { connected = false });
            var axes = new[] { "ra", "dec" };
            var result = new Dictionary<string, Dictionary<string, double?>>();
            foreach (var axis in axes) {
                var names = await phd2.GetAlgoParamNamesAsync(axis);
                var bag = new Dictionary<string, double?>();
                foreach (var n in names) bag[n] = await phd2.GetAlgoParamAsync(axis, n);
                result[axis] = bag;
            }
            return Results.Ok(new { connected = true, axes = result });
        });

        // Set a single live algo param + persist into the rig's custom bag
        // (and flip preset to "Custom" so the user knows they've diverged
        // from a built-in).
        group.MapPut("/algo-params", async (AlgoParamRequest req,
                                            PHD2Client phd2,
                                            ProfileService profiles) => {
            if (!phd2.IsConnected) return Results.BadRequest(new { error = "PHD2 not connected" });
            if (string.IsNullOrEmpty(req.Axis) || string.IsNullOrEmpty(req.Name))
                return Results.BadRequest(new { error = "axis + name required" });
            var ok = await phd2.SetAlgoParamAsync(req.Axis, req.Name, req.Value);
            if (!ok) return Results.BadRequest(new {
                error = $"PHD2 rejected {req.Axis}/{req.Name} — algorithm may not expose it" });
            var rig = profiles.ActiveEquipmentProfile;
            profiles.UpdateEquipmentProfile(rig.Id, r => {
                r.PHD2CustomAlgoParams[$"{req.Axis}:{req.Name}"] = req.Value;
                r.PHD2AlgoPreset = PHD2AlgoPresets.CustomPresetName;
            });
            return Results.Ok(new { applied = true });
        });

        // ----- PH2X-6: xpra-hosted PHD2 GUI session lifecycle -----

        group.MapGet("/gui-session/status", (Phd2GuiSessionService gui) => Results.Ok(new {
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            supportedOs = gui.IsSupportedOs,
            xpraInstalled = gui.XpraInstalled,
            xpraVersion = gui.XpraVersion,
            xpraPath = gui.XpraPath,
            sessionRunning = gui.SessionRunning,
            displayNumber = gui.DisplayNumber,
            bindPort = gui.BindPort,
            lastHealthCheckAt = gui.LastHealthCheckAt,
            lastError = gui.LastError,
            // Hint URL the UI iframes — points to the Polaris reverse-proxy
            // so it stays same-origin (sessionStorage works there).
            embedUrl = "/phd2-gui/"
        }));

        group.MapPost("/gui-session/start", async (Phd2GuiSessionService gui) => {
            if (!gui.IsSupportedOs)
                return Results.Json(new { error = "Embedded PHD2 GUI requires Linux + xpra" },
                    statusCode: 501);
            if (!gui.XpraInstalled)
                return Results.Json(new { error = "xpra not installed. Run: sudo apt install xpra xserver-xorg-video-dummy" },
                    statusCode: 501);
            var ok = await gui.StartSessionAsync();
            return Results.Ok(new { running = ok, error = ok ? null : gui.LastError });
        });

        group.MapPost("/gui-session/stop", async (Phd2GuiSessionService gui) => {
            if (!gui.IsSupportedOs || !gui.XpraInstalled)
                return Results.Json(new { error = "Not supported" }, statusCode: 501);
            var ok = await gui.StopSessionAsync();
            return Results.Ok(new { stopped = ok, error = ok ? null : gui.LastError });
        });

        group.MapPost("/gui-session/restart", async (Phd2GuiSessionService gui) => {
            if (!gui.IsSupportedOs || !gui.XpraInstalled)
                return Results.Json(new { error = "Not supported" }, statusCode: 501);
            var ok = await gui.RestartSessionAsync();
            return Results.Ok(new { running = ok, error = ok ? null : gui.LastError });
        });
    }

    public record ConnectGuiderRequest(string? Host, int? Port);
    public record GuideRequest(double? SettlePixels, int? SettleTime, int? SettleTimeout, bool? Recalibrate);
    public record DitherRequest(double? Pixels, bool? RaOnly, double? SettlePixels, int? SettleTime, int? SettleTimeout);
    public record SyncProfileRequest(string? RigId);
    public record AlgoParamRequest(string Axis, string Name, double Value);
}
