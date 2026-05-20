using NINA.Headless.Services;

namespace NINA.Headless.Endpoints;

public static class WeatherEndpoints {
    public static void MapWeatherEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/weather");

        group.MapGet("/status", (EquipmentManager equip) => {
            if (equip.Weather == null)
                return Results.Ok(new {
                    connected = false,
                    safe = false
                });

            return Results.Ok(new {
                connected = equip.Weather.IsConnected,
                name = equip.Weather.DeviceName,
                temperature = Safe(equip.Weather.Temperature),
                humidity = Safe(equip.Weather.Humidity),
                dewPoint = Safe(equip.Weather.DewPoint),
                windSpeed = Safe(equip.Weather.WindSpeed),
                windGust = Safe(equip.Weather.WindGust),
                pressure = Safe(equip.Weather.Pressure),
                cloudCover = Safe(equip.Weather.CloudCover),
                rainRate = Safe(equip.Weather.RainRate),
                skyQuality = Safe(equip.Weather.SkyQuality),
                safe = equip.Weather.IsSafe
            });
        });

        group.MapPost("/refresh", async (EquipmentManager equip) => {
            if (equip.Weather == null)
                return Results.BadRequest(new { error = "No weather device selected" });

            await equip.Weather.RefreshAsync();
            return Results.Ok(new { status = "refreshing" });
        });

        group.MapPost("/select/{deviceName}", (EquipmentManager equip, string deviceName) => {
            equip.SelectWeather(deviceName);
            return Results.Ok(new { selected = deviceName });
        });

        group.MapPost("/connect", async (EquipmentManager equip) => {
            if (equip.Weather == null)
                return Results.BadRequest(new { error = "No weather device selected" });

            await equip.Weather.ConnectAsync();
            return Results.Ok(new { status = "connected", device = equip.Weather.DeviceName });
        });

        group.MapPost("/disconnect", async (EquipmentManager equip) => {
            if (equip.Weather == null)
                return Results.BadRequest(new { error = "No weather device selected" });

            await equip.Weather.DisconnectAsync();
            return Results.Ok(new { status = "disconnected" });
        });
    }

    static double? Safe(double v) => double.IsNaN(v) || double.IsInfinity(v) ? null : v;
}
