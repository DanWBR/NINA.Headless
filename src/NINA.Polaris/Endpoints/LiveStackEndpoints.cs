using NINA.Image.ImageData;
using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

public static class LiveStackEndpoints {
    public static void MapLiveStackEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/livestack");

        group.MapPost("/start", (LiveStackingService stack) => {
            // Always begins a fresh stack (resets buffer). Use /resume
            // when the operator paused mid-session and wants to keep
            // building on the existing accumulator.
            stack.Start();
            return Results.Ok(new { status = "started" });
        });

        // Re-arm WITHOUT clearing the accumulator. Pair with /stop for
        // a true pause/resume workflow: user clicks Pause (frames keep
        // saving to disk but the running mean freezes), then clicks
        // Resume here to pick up where they left off. Falls through to
        // Start when there's nothing to resume from (frameCount == 0).
        group.MapPost("/resume", (LiveStackingService stack) => {
            stack.Resume();
            return Results.Ok(new { status = "resumed", frameCount = stack.FrameCount });
        });

        group.MapPost("/stop", (LiveStackingService stack) => {
            stack.Stop();
            return Results.Ok(new { status = "stopped", frameCount = stack.FrameCount });
        });

        // Start-with-prep: kicks off the optional one-shot autofocus
        // and/or recenter (gated by LiveStackTriggers.RefocusOnStart /
        // RecenterOnStart) BEFORE arming the stacker. Returns once both
        // prep ops settle. UI calls this when the operator clicks
        // Stack ON and at least one OnStart flag is true; for the
        // common case (no prep) /start is still the cheap path.
        group.MapPost("/start-with-prep", async (
            LiveStackingService stack,
            LiveStackTriggersService triggers,
            ProfileService profiles,
            ILogger<Program> logger) => {

            var cfg = profiles.ActiveEquipmentProfile?.LiveStackTriggers
                      ?? new LiveStackTriggers();
            var didRefocus = false;
            var didRecenter = false;
            string? prepError = null;
            try {
                if (cfg.RefocusOnStart) {
                    logger.LogInformation("Live stack OnStart: triggering autofocus");
                    await triggers.FireRefocusNowAsync();
                    didRefocus = true;
                }
                if (cfg.RecenterOnStart) {
                    logger.LogInformation("Live stack OnStart: triggering recenter");
                    await triggers.FireRecenterNowAsync();
                    didRecenter = true;
                }
            } catch (Exception ex) {
                // Don't block the stack on prep failures — the operator
                // wanted to stack regardless. Report what tripped so
                // the toast can warn but the stack still starts.
                prepError = ex.Message;
                logger.LogWarning(ex, "Live stack OnStart prep raised; stacking will start anyway");
            }
            stack.Start();
            return Results.Ok(new {
                status = "started",
                refocusFired = didRefocus,
                recenterFired = didRecenter,
                prepError
            });
        });

        group.MapPost("/reset", (LiveStackingService stack,
                                 LiveStackTriggersService triggers,
                                 RefocusSuggestionService refocusSuggest) => {
            stack.Reset();
            // Trigger state (last-refocus snapshot, reference RA/Dec, etc.)
            // is meaningless after a stack reset, clear it too so the
            // next first frame re-establishes the reference.
            triggers.ResetTriggerState();
            // REFSUG-1: same applies to the trend-based suggestion.
            // Without this the rolling window would carry samples
            // from a different target / focus state across the reset.
            refocusSuggest.Reset();
            return Results.Ok(new { status = "reset" });
        });

        group.MapGet("/status", (LiveStackingService stack) => {
            return Results.Ok(stack.GetStatus());
        });

        group.MapGet("/preview", (LiveStackingService stack, ImageRelayService relay, int? quality) => {
            var jpeg = relay.GetLatestJpeg(quality ?? 85);
            if (jpeg == null)
                return Results.NotFound(new { error = "No stacked image available" });
            return Results.File(jpeg, "image/jpeg");
        });

        // ----- LSTR-4: triggers settings + manual fires + status -----

        group.MapGet("/triggers/status", (LiveStackTriggersService triggers,
                                          ProfileService profiles) => Results.Ok(new {
            settings = profiles.ActiveEquipmentProfile.LiveStackTriggers,
            state = triggers.CurrentStatus
        }));

        group.MapPut("/triggers/settings", (LiveStackTriggers req,
                                            ProfileService profiles) => {
            var rig = profiles.ActiveEquipmentProfile;
            profiles.UpdateEquipmentProfile(rig.Id, r => r.LiveStackTriggers = req);
            return Results.Ok(new { saved = true });
        });

        group.MapPost("/triggers/refocus-now", async (LiveStackTriggersService triggers) => {
            await triggers.FireRefocusNowAsync();
            return Results.Ok(new { fired = true });
        });

        group.MapPost("/triggers/recenter-now", async (LiveStackTriggersService triggers) => {
            await triggers.FireRecenterNowAsync();
            return Results.Ok(new { fired = true });
        });

        // ----- LSPP-3: per-frame pre-processing settings + status -----
        //
        // GET returns both the persisted settings (so the UI can
        // hydrate the form on tab open) AND the runtime status
        // (counters of calibrated/fallback frames, names of the
        // masters that were resolved on first frame). The status
        // half is what the WS payload also broadcasts -- exposing it
        // via REST lets a fresh tab catch up without waiting for
        // the next 1Hz tick.
        group.MapGet("/preprocessing/settings", (ProfileService profiles) => {
            var rig = profiles.ActiveEquipmentProfile;
            return Results.Ok(rig?.LiveStackPreProcessing ?? new LiveStackPreProcSettings());
        });

        group.MapPut("/preprocessing/settings",
            (LiveStackPreProcSettings req, ProfileService profiles,
             LiveStackPreProcessor preProc) => {
                var rig = profiles.ActiveEquipmentProfile;
                if (rig == null) return Results.BadRequest(new { error = "no active rig" });
                profiles.UpdateEquipmentProfile(rig.Id, r => r.LiveStackPreProcessing = req);
                // Settings changed -> drop the master cache so the next
                // frame resolves with the new overrides (or the new
                // CalibrationEnabled flag).
                preProc.Reset();
                return Results.Ok(new { saved = true });
            });

        // ----- REFSUG-1: dismiss the refocus suggestion -----
        //
        // resetBaseline=true is the "I refocused" path, replaces the
        // baseline with the rolling mean so the next evaluation uses
        // the post-refocus HFR as the new good. false just clears the
        // chip without touching the baseline (rare; user wants to
        // acknowledge but trust the old reference).
        group.MapPost("/refocus-suggestion/dismiss",
            (RefocusSuggestionService suggest, DismissRefocusSuggestionRequest? req) => {
                suggest.Dismiss(resetBaseline: req?.ResetBaseline ?? true);
                return Results.NoContent();
            });

        group.MapGet("/refocus-suggestion/status",
            (RefocusSuggestionService suggest) => Results.Ok(suggest.CurrentStatus));

        // Toggle per-frame disk persistence. Updates both the runtime
        // flag on the service (takes effect on the very next frame)
        // and the active rig's LiveStackSaveFramesToDisk field (so
        // the choice survives Polaris restarts). Per-rig because
        // EAA-only rigs typically stay off, observatory rigs stay on.
        group.MapPut("/save-frames", (SaveFramesRequest req,
                                       LiveStackingService stack,
                                       ProfileService profiles) => {
            stack.SaveFramesToDisk = req.Enabled;
            var rig = profiles.ActiveEquipmentProfile;
            if (rig != null) {
                profiles.UpdateEquipmentProfile(rig.Id,
                    r => r.LiveStackSaveFramesToDisk = req.Enabled);
            }
            return Results.Ok(new { saved = true, enabled = req.Enabled });
        });

        // SNR-3: session-only target SNR override. The active rig's
        // TargetSnr is the persisted default; the LIVE tab can push
        // a different number here for one session without touching
        // the profile. POST { targetSnr: null } clears the override
        // so the rig's value takes over again.
        group.MapPut("/target-snr", (TargetSnrRequest req, LiveStackingService stack,
                                      ProfileService profiles) => {
            // Same range guard as the per-rig PUT in EquipmentEndpoints:
            // accept 0 < x ≤ 500, anything else → clear.
            double? clamped = null;
            if (req.TargetSnr.HasValue) {
                var t = req.TargetSnr.Value;
                if (t > 0 && t <= 500) clamped = t;
            }
            // Effective target: explicit override wins, otherwise fall
            // back to active rig.TargetSnr.
            stack.SetTargetSnrOverride(clamped ?? profiles.ActiveEquipmentProfile?.TargetSnr);
            return Results.Ok(new {
                saved = true,
                override_ = clamped,
                effective = stack.TargetSnr
            });
        });

        // Auto-pause duration cap, seconds. 0 = unlimited (default).
        // Negative values are clamped to 0. Same persistence pattern
        // as /save-frames — runtime + profile in one call.
        group.MapPut("/max-duration", (MaxDurationRequest req,
                                        LiveStackingService stack,
                                        ProfileService profiles) => {
            var secs = Math.Max(0, req.Seconds);
            stack.MaxDurationSeconds = secs;
            var rig = profiles.ActiveEquipmentProfile;
            if (rig != null) {
                profiles.UpdateEquipmentProfile(rig.Id,
                    r => r.LiveStackMaxDurationSeconds = secs);
            }
            return Results.Ok(new { saved = true, seconds = secs });
        });

        // ----- CLST-6: persist a client-stacked result as FITS -----
        //
        // When live-stacking happens in the browser (server is in
        // MetricsOnly mode), the accumulated buffer never reaches the
        // server. This endpoint lets the client POST the running mean
        // up so we can write it as a FITS into the rig's integrated/
        // directory and surface it in STUDIO via FrameLibraryService.
        //
        // Wire format (kept simple, no multipart, no JSON-encoded
        // pixels):
        //   POST /api/livestack/upload-result
        //     ?width=W&height=H&bitDepth=16&target=NAME&frameCount=N
        //   Content-Type: application/octet-stream
        //   Body: uint16 LE pixels (width*height*2 bytes)
        group.MapPost("/upload-result", async (HttpContext ctx,
                                               ImageWriterService writer,
                                               ILoggerFactory loggerFactory) => {
            var log = loggerFactory.CreateLogger("LiveStack.UploadResult");
            try {
                var q = ctx.Request.Query;
                if (!int.TryParse(q["width"], out var width) || width <= 0 ||
                    !int.TryParse(q["height"], out var height) || height <= 0) {
                    return Results.BadRequest(new { error = "width + height query parameters required and must be positive integers" });
                }
                var bitDepth = int.TryParse(q["bitDepth"], out var bd) ? bd : 16;
                var target = q["target"].ToString();
                if (string.IsNullOrWhiteSpace(target)) target = "live-stack";
                var frameCount = int.TryParse(q["frameCount"], out var fc) ? fc : 0;

                // Read uint16 LE body. Cap at a sane size to avoid OOM if a
                // malicious client claims a huge frame.
                const long maxBytes = 512L * 1024 * 1024;  // 512 MB; > full-frame uint16
                var expected = (long)width * height * 2;
                if (expected > maxBytes) {
                    return Results.BadRequest(new { error = $"frame too large ({expected} bytes > {maxBytes})" });
                }

                using var ms = new MemoryStream(capacity: (int)Math.Min(expected, int.MaxValue));
                await ctx.Request.Body.CopyToAsync(ms);
                var bytes = ms.ToArray();
                if (bytes.Length != expected) {
                    return Results.BadRequest(new {
                        error = $"body size {bytes.Length} doesn't match width*height*2={expected}"
                    });
                }

                // Reinterpret as ushort[], same on-wire format the server
                // uses in raw-mode broadcasts, just travelling the other
                // direction now.
                var pixels = new ushort[width * height];
                Buffer.BlockCopy(bytes, 0, pixels, 0, bytes.Length);

                var props = new ImageProperties {
                    Width = width,
                    Height = height,
                    BitDepth = bitDepth
                };
                var image = new BaseImageData(pixels, props, new ImageMetaData {
                    Target = new ImageMetaData.TargetInfo { Name = target }
                });

                // imageType="MASTER" routes through ImageWriterService's
                // BuildSubDir to integrated/{target}/{filter}/, same place
                // STUDIO ST-5 batch stacks land. From there
                // FrameLibraryService picks it up on next rescan.
                var saved = writer.SaveImage(image, targetName: target,
                                              imageType: "MASTER", gain: 0);
                if (saved == null) {
                    return Results.Problem(
                        detail: "ImageOutputDir not configured on the active profile. " +
                                "Set the output directory in Settings → Files → Image Output before saving stacks.",
                        statusCode: 500);
                }
                return Results.Ok(new { savedPath = saved, frameCount });
            } catch (Exception ex) {
                // Without this catch any FITS writer / disk / permission
                // failure surfaces as an opaque "500 Internal Server
                // Error" on the client with the actual exception
                // buried in stderr. Echo the type + message so the
                // toast tells the user what to fix (read-only path,
                // path doesn't exist, etc.).
                log.LogError(ex, "Failed to save uploaded live stack");
                return Results.Problem(
                    detail: $"{ex.GetType().Name}: {ex.Message}",
                    statusCode: 500);
            }
        }).DisableAntiforgery();
    }

    /// <summary>Body of POST /api/livestack/refocus-suggestion/dismiss.
    /// resetBaseline defaults to true (the common case: user just
    /// refocused, take the new HFR as the reference).</summary>
    public record DismissRefocusSuggestionRequest(bool ResetBaseline = true);

    /// <summary>Body of PUT /api/livestack/save-frames. Mirrors the
    /// LIVE tab checkbox.</summary>
    public record SaveFramesRequest(bool Enabled);

    /// <summary>Body of PUT /api/livestack/max-duration. 0 =
    /// unlimited. The LIVE tab posts the user's "stack for N
    /// minutes" input here, converted to seconds.</summary>
    public record MaxDurationRequest(int Seconds);

    /// <summary>Body of PUT /api/livestack/target-snr. null clears
    /// the session-level override so the active rig's TargetSnr
    /// takes effect again.</summary>
    public record TargetSnrRequest(double? TargetSnr);
}
