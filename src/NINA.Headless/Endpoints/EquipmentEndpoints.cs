using NINA.Headless.Services;
using NINA.INDI.Client;

namespace NINA.Headless.Endpoints;

public static class EquipmentEndpoints {
    public static void MapEquipmentEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/equipment");

        group.MapGet("/devices", (IndiClient client) => {
            var devices = new List<object>();
            foreach (var deviceName in client.GetDeviceNames()) {
                if (client.Devices.TryGetValue(deviceName, out var props)) {
                    var groups = props.Values
                        .Select(p => p.Group)
                        .Where(g => !string.IsNullOrEmpty(g))
                        .Distinct()
                        .ToList();

                    var driverInfo = props.Values.FirstOrDefault(p => p.Name == "DRIVER_INFO");
                    string? driverName = null;
                    string? driverInterface = null;
                    if (driverInfo is NINA.INDI.Protocol.IndiTextProperty tp) {
                        tp.Values.TryGetValue("DRIVER_NAME", out driverName);
                        tp.Values.TryGetValue("DRIVER_INTERFACE", out driverInterface);
                    }

                    devices.Add(new {
                        name = deviceName,
                        driver = driverName,
                        @interface = driverInterface,
                        propertyCount = props.Count,
                        groups
                    });
                }
            }
            return Results.Ok(new { devices });
        });

        group.MapGet("/status", (EquipmentManager equip) => {
            return Results.Ok(equip.GetEquipmentStatus());
        });

        // ---- Equipment profiles (rigs) ----

        group.MapGet("/rigs", (ProfileService profiles) => {
            var rigs = profiles.ListEquipmentProfiles();
            return Results.Ok(new {
                activeId = profiles.ActiveEquipmentProfile.Id,
                rigs
            });
        });

        group.MapGet("/rigs/active", (ProfileService profiles) => {
            return Results.Ok(profiles.ActiveEquipmentProfile);
        });

        group.MapPost("/rigs", (CreateRigRequest req, ProfileService profiles) => {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name required" });
            var rig = profiles.CreateEquipmentProfile(req.Name);
            return Results.Ok(rig);
        });

        group.MapPost("/rigs/clone", (CloneRigRequest req, ProfileService profiles) => {
            if (string.IsNullOrWhiteSpace(req.NewName))
                return Results.BadRequest(new { error = "NewName required" });
            var clone = profiles.CloneActiveRigAs(req.NewName);
            return Results.Ok(clone);
        });

        group.MapPut("/rigs/{id}", (string id, EquipmentProfile update, ProfileService profiles) => {
            var ok = profiles.UpdateEquipmentProfile(id, r => {
                r.Name = update.Name;
                r.Camera = update.Camera;
                r.Telescope = update.Telescope;
                r.Focuser = update.Focuser;
                r.FilterWheel = update.FilterWheel;
                r.Rotator = update.Rotator;
                r.FlatDevice = update.FlatDevice;
                r.Dome = update.Dome;
                r.Weather = update.Weather;
                r.CoolerTargetTemperature = update.CoolerTargetTemperature;
                r.DefaultGain = update.DefaultGain;
                r.DefaultOffset = update.DefaultOffset;
                r.DefaultBinning = update.DefaultBinning;
                r.FocuserStepSize = update.FocuserStepSize;
                r.FocuserBacklashSteps = update.FocuserBacklashSteps;
                r.FocalLengthMm = update.FocalLengthMm;
                r.PHD2Host = update.PHD2Host;
                r.PHD2Port = update.PHD2Port;
            });
            return ok ? Results.Ok(new { message = "Rig updated" })
                      : Results.NotFound(new { error = "Rig not found" });
        });

        group.MapPost("/rigs/{id}/activate", (string id, ProfileService profiles) => {
            var ok = profiles.ActivateEquipmentProfile(id);
            return ok ? Results.Ok(new { activeId = id })
                      : Results.NotFound(new { error = "Rig not found" });
        });

        group.MapDelete("/rigs/{id}", (string id, ProfileService profiles) => {
            var ok = profiles.DeleteEquipmentProfile(id);
            return ok ? Results.Ok(new { message = "Rig deleted" })
                      : Results.BadRequest(new { error = "Rig not found or last remaining" });
        });
    }

    public record CreateRigRequest(string Name);
    public record CloneRigRequest(string NewName);
}
