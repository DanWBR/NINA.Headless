using NINA.Headless.Services;

namespace NINA.Headless.Endpoints;

public static class RotatorEndpoints {
    public static void MapRotatorEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/rotator");

        group.MapGet("/status", (EquipmentManager equip) => {
            if (equip.Rotator == null)
                return Results.Ok(new {
                    connected = false,
                    position = 0.0,
                    moving = false,
                    reversed = false
                });

            var pos = equip.Rotator.Position;
            return Results.Ok(new {
                connected = equip.Rotator.IsConnected,
                name = equip.Rotator.DeviceName,
                position = double.IsNaN(pos) ? 0.0 : pos,
                moving = equip.Rotator.IsMoving,
                reversed = equip.Rotator.IsReversed
            });
        });

        group.MapPost("/move", async (EquipmentManager equip, MoveRotatorRequest request) => {
            if (equip.Rotator == null)
                return Results.BadRequest(new { error = "No rotator selected" });

            await equip.Rotator.MoveToAsync(request.Angle);
            return Results.Ok(new { status = "moving", target = request.Angle });
        });

        group.MapPost("/reverse", async (EquipmentManager equip, ReverseRequest request) => {
            if (equip.Rotator == null)
                return Results.BadRequest(new { error = "No rotator selected" });

            await equip.Rotator.ReverseAsync(request.Reversed);
            return Results.Ok(new { reversed = request.Reversed });
        });

        group.MapPost("/abort", async (EquipmentManager equip) => {
            if (equip.Rotator == null)
                return Results.BadRequest(new { error = "No rotator selected" });

            await equip.Rotator.AbortAsync();
            return Results.Ok(new { status = "stopped" });
        });

        group.MapPost("/select/{deviceName}", (EquipmentManager equip, string deviceName) => {
            equip.SelectRotator(deviceName);
            return Results.Ok(new { selected = deviceName });
        });

        group.MapPost("/connect", async (EquipmentManager equip) => {
            if (equip.Rotator == null)
                return Results.BadRequest(new { error = "No rotator selected" });

            await equip.Rotator.ConnectAsync();
            return Results.Ok(new { status = "connected", device = equip.Rotator.DeviceName });
        });

        group.MapPost("/disconnect", async (EquipmentManager equip) => {
            if (equip.Rotator == null)
                return Results.BadRequest(new { error = "No rotator selected" });

            await equip.Rotator.DisconnectAsync();
            return Results.Ok(new { status = "disconnected" });
        });
    }

    public record MoveRotatorRequest(double Angle);
    public record ReverseRequest(bool Reversed);
}
