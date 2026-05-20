using NINA.Headless.Services;

namespace NINA.Headless.Endpoints;

public static class DomeEndpoints {
    public static void MapDomeEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/dome");

        group.MapGet("/status", (EquipmentManager equip) => {
            if (equip.Dome == null)
                return Results.Ok(new {
                    connected = false,
                    azimuth = 0.0,
                    moving = false,
                    parked = false,
                    slaved = false,
                    shutter = "unknown"
                });

            var az = equip.Dome.Azimuth;
            return Results.Ok(new {
                connected = equip.Dome.IsConnected,
                name = equip.Dome.DeviceName,
                azimuth = double.IsNaN(az) ? 0.0 : az,
                moving = equip.Dome.IsMoving,
                parked = equip.Dome.IsParked,
                slaved = equip.Dome.IsSlaved,
                shutter = equip.Dome.ShutterStatus.ToString()
            });
        });

        group.MapPost("/slew", async (EquipmentManager equip, SlewDomeRequest request) => {
            if (equip.Dome == null)
                return Results.BadRequest(new { error = "No dome selected" });

            await equip.Dome.SlewToAzimuthAsync(request.Azimuth);
            return Results.Ok(new { status = "slewing", target = request.Azimuth });
        });

        group.MapPost("/shutter/open", async (EquipmentManager equip) => {
            if (equip.Dome == null)
                return Results.BadRequest(new { error = "No dome selected" });

            await equip.Dome.OpenShutterAsync();
            return Results.Ok(new { status = "opening" });
        });

        group.MapPost("/shutter/close", async (EquipmentManager equip) => {
            if (equip.Dome == null)
                return Results.BadRequest(new { error = "No dome selected" });

            await equip.Dome.CloseShutterAsync();
            return Results.Ok(new { status = "closing" });
        });

        group.MapPost("/park", async (EquipmentManager equip) => {
            if (equip.Dome == null)
                return Results.BadRequest(new { error = "No dome selected" });

            await equip.Dome.ParkAsync();
            return Results.Ok(new { status = "parking" });
        });

        group.MapPost("/unpark", async (EquipmentManager equip) => {
            if (equip.Dome == null)
                return Results.BadRequest(new { error = "No dome selected" });

            await equip.Dome.UnparkAsync();
            return Results.Ok(new { status = "unparking" });
        });

        group.MapPost("/abort", async (EquipmentManager equip) => {
            if (equip.Dome == null)
                return Results.BadRequest(new { error = "No dome selected" });

            await equip.Dome.AbortAsync();
            return Results.Ok(new { status = "stopped" });
        });

        group.MapPost("/select/{deviceName}", (EquipmentManager equip, string deviceName) => {
            equip.SelectDome(deviceName);
            return Results.Ok(new { selected = deviceName });
        });

        group.MapPost("/connect", async (EquipmentManager equip) => {
            if (equip.Dome == null)
                return Results.BadRequest(new { error = "No dome selected" });

            await equip.Dome.ConnectAsync();
            return Results.Ok(new { status = "connected", device = equip.Dome.DeviceName });
        });

        group.MapPost("/disconnect", async (EquipmentManager equip) => {
            if (equip.Dome == null)
                return Results.BadRequest(new { error = "No dome selected" });

            await equip.Dome.DisconnectAsync();
            return Results.Ok(new { status = "disconnected" });
        });
    }

    public record SlewDomeRequest(double Azimuth);
}
