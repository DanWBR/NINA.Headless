using NINA.Headless.Services;

namespace NINA.Headless.Endpoints;

public static class FlatDeviceEndpoints {
    public static void MapFlatDeviceEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/flatdevice");

        group.MapGet("/status", (EquipmentManager equip) => {
            if (equip.FlatDevice == null)
                return Results.Ok(new {
                    connected = false,
                    lightOn = false,
                    brightness = 0,
                    coverOpen = false,
                    coverMoving = false
                });

            return Results.Ok(new {
                connected = equip.FlatDevice.IsConnected,
                name = equip.FlatDevice.DeviceName,
                lightOn = equip.FlatDevice.IsLightOn,
                brightness = equip.FlatDevice.Brightness,
                coverOpen = equip.FlatDevice.IsCoverOpen,
                coverMoving = equip.FlatDevice.IsCoverMoving
            });
        });

        group.MapPost("/light", async (EquipmentManager equip, LightRequest request) => {
            if (equip.FlatDevice == null)
                return Results.BadRequest(new { error = "No flat device selected" });

            await equip.FlatDevice.SetLightAsync(request.On);
            return Results.Ok(new { lightOn = request.On });
        });

        group.MapPost("/brightness", async (EquipmentManager equip, BrightnessRequest request) => {
            if (equip.FlatDevice == null)
                return Results.BadRequest(new { error = "No flat device selected" });

            await equip.FlatDevice.SetBrightnessAsync(request.Brightness);
            return Results.Ok(new { brightness = request.Brightness });
        });

        group.MapPost("/cover/open", async (EquipmentManager equip) => {
            if (equip.FlatDevice == null)
                return Results.BadRequest(new { error = "No flat device selected" });

            await equip.FlatDevice.OpenCoverAsync();
            return Results.Ok(new { status = "opening" });
        });

        group.MapPost("/cover/close", async (EquipmentManager equip) => {
            if (equip.FlatDevice == null)
                return Results.BadRequest(new { error = "No flat device selected" });

            await equip.FlatDevice.CloseCoverAsync();
            return Results.Ok(new { status = "closing" });
        });

        group.MapPost("/select/{deviceName}", (EquipmentManager equip, string deviceName) => {
            equip.SelectFlatDevice(deviceName);
            return Results.Ok(new { selected = deviceName });
        });

        group.MapPost("/connect", async (EquipmentManager equip) => {
            if (equip.FlatDevice == null)
                return Results.BadRequest(new { error = "No flat device selected" });

            await equip.FlatDevice.ConnectAsync();
            return Results.Ok(new { status = "connected", device = equip.FlatDevice.DeviceName });
        });

        group.MapPost("/disconnect", async (EquipmentManager equip) => {
            if (equip.FlatDevice == null)
                return Results.BadRequest(new { error = "No flat device selected" });

            await equip.FlatDevice.DisconnectAsync();
            return Results.Ok(new { status = "disconnected" });
        });
    }

    public record LightRequest(bool On);
    public record BrightnessRequest(int Brightness);
}
