using NINA.Headless.Services;

namespace NINA.Headless.Endpoints;

public static class LiveStackEndpoints {
    public static void MapLiveStackEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/livestack");

        group.MapPost("/start", (LiveStackingService stack) => {
            stack.Start();
            return Results.Ok(new { status = "started" });
        });

        group.MapPost("/stop", (LiveStackingService stack) => {
            stack.Stop();
            return Results.Ok(new { status = "stopped", frameCount = stack.FrameCount });
        });

        group.MapPost("/reset", (LiveStackingService stack, LiveStackTriggersService triggers) => {
            stack.Reset();
            // Trigger state (last-refocus snapshot, reference RA/Dec, etc.)
            // is meaningless after a stack reset — clear it too so the
            // next first frame re-establishes the reference.
            triggers.ResetTriggerState();
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
    }
}
