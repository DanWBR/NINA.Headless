using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// REST surface for the POLAR sidebar tab. Mirrors the AutoFocus +
/// PHD2 calibration endpoints: POST to kick off a job (server stores
/// the resulting JobId, also surfaced via /ws/status under
/// polarAlignment), POST to abort, GET for one-off status polling
/// when the WS isn't connected.
///
/// Job state lifecycle:
///   Idle → (POST /start) → Preflight → MovingToPoint1 → SolvingPoint1
///        → MovingToPoint2 → SolvingPoint2 → MovingToPoint3
///        → SolvingPoint3 → Computing → SlewingHome → Ok
///   (any phase) → (POST /abort | CTS firing) → Cancelled
///
/// Refinement runs as a separate Mode="refine" loop, started by
/// POST /refine/start after a successful TPPA. Implemented in PA-5;
/// the routes exist from PA-1 forward so the front-end can wire
/// against a stable surface.
/// </summary>
public static class PolarAlignmentEndpoints {
    public static void MapPolarAlignmentEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/polar");

        group.MapPost("/start", (PolarAlignmentService svc,
                                 ProfileService profiles,
                                 StartPolarRequest? request) => {
            // Defaults pulled from the active rig's PolarAlign* fields
            // so the UI can post an empty body and get "the user's
            // saved settings." Explicit fields in the body override
            // per-call (e.g. retry with longer exposure).
            var rig = profiles.ActiveEquipmentProfile;
            var opts = new PolarAlignmentOptions(
                SlewStepDegrees: request?.SlewStepDegrees ?? rig.PolarAlignSlewDegrees,
                ExposureSeconds: request?.ExposureSeconds ?? rig.PolarAlignExposureSec,
                SettleSeconds:   request?.SettleSeconds   ?? rig.PolarAlignSettleSeconds,
                Gain:            request?.Gain            ?? rig.PolarAlignGain);
            try {
                var job = svc.StartJob(opts);
                return Results.Ok(new {
                    jobId = job.Id,
                    phase = job.Phase.ToString(),
                    options = job.Options
                });
            } catch (InvalidOperationException ex) {
                // Second-Start guard, UI should disable the button while
                // a job is running, but a race + a 409 is the right
                // server-side answer.
                return Results.Conflict(new { error = ex.Message });
            }
        });

        group.MapPost("/abort", (PolarAlignmentService svc) => {
            svc.AbortCurrent();
            return Results.Ok(new { aborted = true });
        });

        group.MapPost("/refine/start", (PolarAlignmentService svc) => {
            try {
                var job = svc.StartRefinement();
                return Results.Ok(new { jobId = job.Id, phase = job.Phase.ToString() });
            } catch (NotImplementedException ex) {
                // PA-5 will replace this exception with the real
                // refinement-job start. Until then the UI gets a
                // clear 501 so the button stays disabled.
                return Results.Json(new { error = ex.Message }, statusCode: 501);
            } catch (InvalidOperationException ex) {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        group.MapPost("/refine/stop", (PolarAlignmentService svc) => {
            svc.StopRefinement();
            return Results.Ok(new { stopped = true });
        });

        group.MapGet("/status", (PolarAlignmentService svc) => {
            var j = svc.CurrentJob;
            if (j == null) {
                return Results.Ok(new {
                    phase = PolarAlignmentPhase.Idle.ToString(),
                    points = Array.Empty<PolarPoint>(),
                    azErrorArcsec = 0.0,
                    altErrorArcsec = 0.0,
                    totalErrorArcsec = 0.0
                });
            }
            return Results.Ok(new {
                jobId = j.Id,
                phase = j.Phase.ToString(),
                mode = j.Mode,
                points = j.Points,
                azErrorArcsec = j.AzErrorArcsec,
                altErrorArcsec = j.AltErrorArcsec,
                totalErrorArcsec = j.TotalErrorArcsec,
                lastError = j.LastError,
                startedAt = j.StartedAt,
                completedAt = j.CompletedAt,
                isActive = j.IsActive
            });
        });

        // PA-6: best starting targets for TPPA "right now". Read-only,
        // pure compute against the catalog + altitude helper, cheap
        // (~5ms for 200 catalog entries). Optional `limit` lets the UI
        // ask for more or fewer chips.
        group.MapGet("/best-targets",
            (PolarTppaTargetService svc, int? limit) =>
                Results.Ok(svc.Suggest(limit.GetValueOrDefault(5))));

        // ─── RDPA-2: rudimentary single-target alignment routes ─────

        // POST /api/polar/rudimentary/start
        // Body: RudimentaryStartRequest. Fresh session: target +
        // optional slew + capture + solve. Returns the first
        // iteration's error vector. CurrentJob lives on so
        // subsequent /resolve calls iterate against the same target.
        group.MapPost("/rudimentary/start", async (
            PolarAlignmentService svc,
            ProfileService profiles,
            RudimentaryStartRequest? request,
            HttpContext ctx) => {

            if (request == null) {
                return Results.BadRequest(new { error = "missing body" });
            }
            // Defaults from active rig so the UI can post a minimal
            // body (just target + slew flag) and inherit exposure
            // settings.
            var rig = profiles.ActiveEquipmentProfile;
            var req = request with {
                ExposureSeconds = request.ExposureSeconds > 0
                    ? request.ExposureSeconds : rig.PolarAlignExposureSec,
                Gain = request.Gain > 0
                    ? request.Gain : rig.PolarAlignGain,
                SettleSeconds = request.SettleSeconds > 0
                    ? request.SettleSeconds : rig.PolarAlignSettleSeconds,
            };
            try {
                var result = await svc.StartRudimentaryAsync(req, ctx.RequestAborted);
                return Results.Ok(result);
            } catch (InvalidOperationException ex) {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // POST /api/polar/rudimentary/resolve
        // No body. Re-captures + solves at the current mount pointing
        // (no slew). Use this after the operator nudged the knobs.
        group.MapPost("/rudimentary/resolve", async (
            PolarAlignmentService svc,
            HttpContext ctx) => {
            try {
                var result = await svc.RudimentaryReSolveAsync(ctx.RequestAborted);
                return Results.Ok(result);
            } catch (InvalidOperationException ex) {
                // No active session — UI should disable the button.
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // POST /api/polar/rudimentary/abort
        // Alias for the generic /abort so the UI can be explicit
        // about which workflow is being cancelled. Identical effect.
        group.MapPost("/rudimentary/abort", (PolarAlignmentService svc) => {
            svc.AbortCurrent();
            return Results.Ok(new { aborted = true });
        });
    }
}

/// <summary>Optional per-call overrides. All fields nullable so an
/// empty POST body falls through to the active rig's saved values.</summary>
public record StartPolarRequest(
    int? SlewStepDegrees = null,
    double? ExposureSeconds = null,
    int? SettleSeconds = null,
    int? Gain = null);
