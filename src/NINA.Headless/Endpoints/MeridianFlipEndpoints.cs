using NINA.Headless.Services;

namespace NINA.Headless.Endpoints;

public static class MeridianFlipEndpoints {
    public static void MapMeridianFlipEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/meridianflip");

        group.MapGet("/settings", (MeridianFlipService mf) => Results.Ok(mf.Settings));

        group.MapPut("/settings", (MeridianFlipSettings settings, MeridianFlipService mf) => {
            mf.UpdateSettings(settings);
            return Results.Ok(mf.Settings);
        });

        group.MapGet("/status", (MeridianFlipService mf, EquipmentManager equip, ProfileService profile) => {
            double? timeToMeridianHours = null;
            double? hourAngle = null;
            double? lst = null;

            if (equip.Telescope != null && equip.Telescope.IsConnected) {
                var raHours = equip.Telescope.RightAscension;
                lst = MeridianFlipService.ComputeLstHours(DateTime.UtcNow, profile.Active.Longitude);
                hourAngle = lst.Value - raHours;
                while (hourAngle > 12) hourAngle -= 24;
                while (hourAngle < -12) hourAngle += 24;
                timeToMeridianHours = MeridianFlipService.HoursUntilMeridian(raHours, DateTime.UtcNow, profile.Active.Longitude);
            }

            return Results.Ok(new {
                state = mf.State.ToString().ToLowerInvariant(),
                settings = mf.Settings,
                flipsCompleted = mf.FlipsCompleted,
                lastFlipAt = mf.LastFlipAt,
                lastFlipError = mf.LastFlipError,
                lstHours = lst,
                hourAngleHours = hourAngle,
                timeToMeridianHours = timeToMeridianHours,
                timeToMeridianMinutes = timeToMeridianHours.HasValue ? timeToMeridianHours * 60 : null
            });
        });

        group.MapPost("/trigger", async (TriggerRequest request, MeridianFlipService mf) => {
            if (mf.State != MeridianFlipState.Idle)
                return Results.Conflict(new { error = $"Flip already in progress (state={mf.State})" });

            var ok = await mf.ExecuteFlipAsync(request.Ra, request.Dec);
            return Results.Ok(new { success = ok, state = mf.State.ToString().ToLowerInvariant() });
        });

        group.MapPost("/abort", (MeridianFlipService mf) => {
            mf.Abort();
            return Results.Ok(new { state = mf.State.ToString().ToLowerInvariant() });
        });
    }

    public record TriggerRequest(double Ra, double Dec);
}
