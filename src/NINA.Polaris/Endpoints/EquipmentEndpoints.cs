using NINA.Polaris.Services;
using NINA.INDI.Client;

namespace NINA.Polaris.Endpoints;

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
                // Empty/null camera driver from old clients is treated
                // as the legacy default ("indi"), so untouched rig PUTs
                // don't accidentally clear the driver field.
                if (!string.IsNullOrWhiteSpace(update.CameraDriver))
                    r.CameraDriver = update.CameraDriver;
                r.Telescope = update.Telescope;
                if (!string.IsNullOrWhiteSpace(update.TelescopeDriver))
                    r.TelescopeDriver = update.TelescopeDriver;
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
                // FIELD-2: per-rig Bayer mosaic override. Treat empty /
                // whitespace as null ("Auto") so the UI <select> with
                // an empty-value default round-trips cleanly. Anything
                // else is normalised to upper-snake (RGGB/GBRG/...) and
                // validated downstream by LiveStackingService when it
                // tries to parse it to BayerPatternEnum.
                r.BayerPatternOverride = string.IsNullOrWhiteSpace(update.BayerPatternOverride)
                    ? null : update.BayerPatternOverride.Trim().ToUpperInvariant();
                // FIELD3-2: vertical-flip companion to the Bayer
                // override. Boolean copies through cleanly (default
                // false). Together with the Bayer override they cover
                // both SVBONY family symptoms: completely-wrong mosaic
                // (Bayer override) vs row-offset mosaic / checkerboard
                // (vertical flip).
                r.VerticalFlipImage = update.VerticalFlipImage;
                r.FocuserStepSize = update.FocuserStepSize;
                r.FocuserBacklashSteps = update.FocuserBacklashSteps;
                // Polar alignment (TPPA) tunables. Defensive: zero from
                // an old client should not nuke the defaults, clamp.
                if (update.PolarAlignSlewDegrees > 0)
                    r.PolarAlignSlewDegrees = update.PolarAlignSlewDegrees;
                if (update.PolarAlignExposureSec > 0)
                    r.PolarAlignExposureSec = update.PolarAlignExposureSec;
                if (update.PolarAlignSettleSeconds >= 0)
                    r.PolarAlignSettleSeconds = update.PolarAlignSettleSeconds;
                if (update.PolarAlignGain > 0)
                    r.PolarAlignGain = update.PolarAlignGain;
                // Slew & Center plate-solve tunables (per-rig).
                if (update.SlewCenterExposureSec > 0)
                    r.SlewCenterExposureSec = update.SlewCenterExposureSec;
                if (update.SlewCenterGain > 0)
                    r.SlewCenterGain = update.SlewCenterGain;
                r.FocalLengthMm = update.FocalLengthMm;
                // Telescope picker fields. Strings safe to set as-is
                // (empty string is the "no picker selection" sentinel).
                r.ApertureMm     = update.ApertureMm;
                r.TelescopeBrand = update.TelescopeBrand ?? "";
                r.TelescopeModel = update.TelescopeModel ?? "";
                r.AccessoryType  = update.AccessoryType  ?? "";
                r.AccessoryModel = update.AccessoryModel ?? "";
                // Default factor to 1.0 when the client omits it,
                // matches the no-accessory case.
                r.AccessoryFactor = update.AccessoryFactor > 0 ? update.AccessoryFactor : 1.0;
                r.RequiredBackspacingMm = update.RequiredBackspacingMm;
                r.GuiderFocalLengthMm = update.GuiderFocalLengthMm;
                // New guide-scope metadata fields (RIGS tab card).
                // Defensive: clamp aperture to a sane lower bound so
                // a stray zero doesn't blow up the f-ratio calc on the UI.
                if (update.GuiderApertureMm > 0) r.GuiderApertureMm = update.GuiderApertureMm;
                r.GuideTelescopeBrand = update.GuideTelescopeBrand;
                r.GuideTelescopeModel = update.GuideTelescopeModel;
                r.PHD2Host = update.PHD2Host;
                r.PHD2Port = update.PHD2Port;
                // PHD2 deep-integration fields. Defensive defaults so an
                // old client (pre-PH2X) PUT-ing a rig doesn't clobber the
                // new state with zero/null.
                if (update.PHD2ProfileId.HasValue) r.PHD2ProfileId = update.PHD2ProfileId;
                if (!string.IsNullOrWhiteSpace(update.PHD2AlgoPreset))
                    r.PHD2AlgoPreset = update.PHD2AlgoPreset;
                if (update.PHD2CalibrationStepMsOverride.HasValue)
                    r.PHD2CalibrationStepMsOverride = update.PHD2CalibrationStepMsOverride;
                r.PHD2AutoSyncOnRigSwitch = update.PHD2AutoSyncOnRigSwitch;
                if (update.PHD2CustomAlgoParams != null)
                    r.PHD2CustomAlgoParams = update.PHD2CustomAlgoParams;
                r.FilterOffsets = update.FilterOffsets ?? new();
                // Live-stack triggers (LSTR-2). Defensive null check
                // keeps old clients from clobbering the field.
                if (update.LiveStackTriggers != null)
                    r.LiveStackTriggers = update.LiveStackTriggers;
                // FW-1: Flat Wizard per-rig defaults. Same defensive
                // null-check, so a pre-FW client PUT-ing a rig keeps
                // the existing FlatWizard block untouched.
                if (update.FlatWizard != null)
                    r.FlatWizard = update.FlatWizard;
                // INDIROB-3: per-device pre-connect delays. Replace
                // wholesale when supplied (operator-driven full edit
                // of the table), preserve when null/missing so a
                // partial PUT from an older client doesn't wipe out
                // delays the user set. Strip zero-value entries server-
                // side so the stored dict only carries actual
                // configured delays.
                if (update.PreConnectDelayMsByDevice != null) {
                    r.PreConnectDelayMsByDevice = update.PreConnectDelayMsByDevice
                        .Where(kv => kv.Value > 0)
                        .ToDictionary(kv => kv.Key, kv => kv.Value);
                }
                // CLST-7: live-stack compute target override. "auto"
                // (default), "server", or "client". Empty/null from
                // old clients leaves the existing setting alone.
                if (!string.IsNullOrWhiteSpace(update.LiveStackComputeMode))
                    r.LiveStackComputeMode = update.LiveStackComputeMode.Trim().ToLowerInvariant();
                // VIDEO tab FOV / ROI persistence. -1 leaves the field
                // untouched (lets PUTs that only update other fields
                // skip ROI), 0 clears, positive sets. Mirrors the
                // nullable-int idiom we use elsewhere for partial PUTs.
                if (update.LastVideoRoiW.HasValue) r.LastVideoRoiW = Math.Max(0, update.LastVideoRoiW.Value);
                if (update.LastVideoRoiH.HasValue) r.LastVideoRoiH = Math.Max(0, update.LastVideoRoiH.Value);
                if (update.LastVideoRoiX.HasValue) r.LastVideoRoiX = Math.Max(0, update.LastVideoRoiX.Value);
                if (update.LastVideoRoiY.HasValue) r.LastVideoRoiY = Math.Max(0, update.LastVideoRoiY.Value);
                if (update.LastVideoRoiSize.HasValue) r.LastVideoRoiSize = Math.Max(0, update.LastVideoRoiSize.Value);
                if (!string.IsNullOrWhiteSpace(update.LastVideoRoiAspect))
                    r.LastVideoRoiAspect = update.LastVideoRoiAspect;
                // SNR-3: target signal-to-noise ratio for the LIVE
                // tab's ETA-to-target widget. nullable so a PUT that
                // doesn't include it (older client / form not yet
                // edited) doesn't clobber an existing target. Clamp
                // to 0..500 so a typo doesn't break the ETA math.
                if (update.TargetSnr.HasValue) {
                    var t = update.TargetSnr.Value;
                    r.TargetSnr = t > 0 && t <= 500 ? t : (double?)null;
                }
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
