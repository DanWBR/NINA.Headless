using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

public static class FocuserEndpoints {
    public static void MapFocuserEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/focuser");

        group.MapGet("/status", (EquipmentManager equip) => {
            if (equip.Focuser == null)
                return Results.Ok(new {
                    connected = false,
                    position = 0,
                    temperature = (double?)null,
                    maxPosition = 0,
                    moving = false
                });

            var temp = equip.Focuser.Temperature;
            var caps = equip.Focuser.Capabilities;
            return Results.Ok(new {
                connected = true,
                position = equip.Focuser.Position,
                temperature = double.IsNaN(temp) ? (double?)null : temp,
                maxPosition = equip.Focuser.MaxPosition,
                moving = equip.Focuser.IsMoving,
                capabilities = new {
                    sync        = caps.SupportsSync,
                    reverse     = caps.SupportsReverse,
                    backlash    = caps.SupportsBacklash,
                    temperature = caps.SupportsTemperature
                }
            });
        });

        group.MapPost("/move/absolute", async (EquipmentManager equip, MoveAbsoluteRequest request) => {
            if (equip.Focuser == null)
                return Results.BadRequest(new { error = "No focuser selected" });

            await equip.Focuser.MoveAbsoluteAsync(request.Position);
            return Results.Ok(new { status = "moving", target = request.Position });
        });

        group.MapPost("/move/relative", async (EquipmentManager equip, MoveRelativeRequest request) => {
            if (equip.Focuser == null)
                return Results.BadRequest(new { error = "No focuser selected" });

            await equip.Focuser.MoveRelativeAsync(request.Steps);
            return Results.Ok(new { status = "moving", steps = request.Steps });
        });

        group.MapPost("/abort", async (EquipmentManager equip) => {
            if (equip.Focuser == null)
                return Results.BadRequest(new { error = "No focuser selected" });

            await equip.Focuser.AbortAsync();
            return Results.Ok(new { status = "stopped" });
        });

        // INDI FOCUS_SYNC: redefine current physical position as the
        // given absolute step value WITHOUT moving the motor. Used
        // after manual drawtube reseating or counter-loss recovery.
        // Surfaces NotSupportedException as 501.
        group.MapPost("/sync", async (EquipmentManager equip, MoveAbsoluteRequest request) => {
            if (equip.Focuser == null)
                return Results.BadRequest(new { error = "No focuser selected" });
            try {
                await equip.Focuser.SyncAsync(request.Position);
                return Results.Ok(new { status = "synced", position = request.Position });
            } catch (NotSupportedException ex) {
                return Results.Json(new { error = ex.Message }, statusCode: 501);
            } catch (Exception ex) {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        // INDI FOCUS_REVERSE_MOTION: flip the inward/outward direction
        // convention. One-time setup for focusers mounted backwards
        // relative to the optical train.
        group.MapPost("/reverse", async (EquipmentManager equip, ReverseRequest request) => {
            if (equip.Focuser == null)
                return Results.BadRequest(new { error = "No focuser selected" });
            try {
                await equip.Focuser.SetReverseAsync(request.Reversed);
                return Results.Ok(new { status = "set", reversed = request.Reversed });
            } catch (NotSupportedException ex) {
                return Results.Json(new { error = ex.Message }, statusCode: 501);
            } catch (Exception ex) {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        // INDI FOCUS_BACKLASH_TOGGLE + FOCUS_BACKLASH_STEPS: enable
        // driver-side backlash compensation, configured with the
        // step count to overshoot then return. Critical for
        // auto-focus accuracy on cheap Crayfords with 30-50 step
        // gear lash.
        group.MapPost("/backlash", async (EquipmentManager equip, BacklashRequest request) => {
            if (equip.Focuser == null)
                return Results.BadRequest(new { error = "No focuser selected" });
            try {
                await equip.Focuser.SetBacklashAsync(request.Enabled, request.Steps);
                return Results.Ok(new {
                    status = "set",
                    enabled = request.Enabled,
                    steps = request.Steps
                });
            } catch (NotSupportedException ex) {
                return Results.Json(new { error = ex.Message }, statusCode: 501);
            } catch (Exception ex) {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        group.MapPost("/select/{deviceName}", (EquipmentManager equip, string deviceName, string? driver) => {
            // ?driver=indi (default) | ascom-com. Legacy clients omit
            // it and get INDI for backwards compatibility.
            try {
                equip.SelectFocuser(driver ?? "indi", deviceName);
                return Results.Ok(new { selected = deviceName, driver = driver ?? "indi" });
            } catch (NotSupportedException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Per-driver focuser discovery. INDI = device-name list from
        // the active connection. ASCOM = registered ProgIDs from the
        // local Windows registry. Empty on platforms / drivers that
        // can't enumerate.
        group.MapGet("/discover", (EquipmentManager equip, string? driver) => {
            var d = (driver ?? "indi").Trim().ToLowerInvariant();
            if (d == "ascom-com") {
                return Results.Ok(equip.GetAscomDrivers(
                    NINA.Ascom.Com.AscomComRegistry.DeviceType.Focuser));
            }
            if (d == "alpaca") {
                return Results.Ok(equip.GetDiscoveredFocusersFor("alpaca"));
            }
            return Results.Ok(equip.GetDeviceNames()
                .Select(n => new DiscoveredCamera(n, n, n))
                .ToList());
        });

        // Focuser driver catalogue. Mirrors /api/camera/drivers shape
        // so the frontend driver-source dropdown can render the same
        // way for every device type.
        group.MapGet("/drivers", (EquipmentManager equip) => {
            var alpacaCount = equip.GetDiscoveredFocusersFor("alpaca").Count;
            var list = new List<CameraDriverInfo> {
                new("indi", "INDI", Available: true,
                    Description: "Any focuser the running INDI server exposes."),
                new("alpaca", "Alpaca (ASCOM)", Available: alpacaCount > 0,
                    Description: alpacaCount > 0
                        ? $"ASCOM-over-HTTP focusers. {alpacaCount} discovered."
                        : "Run Alpaca Discover in RIGS first to populate this list."),
            };
            if (OperatingSystem.IsWindows()) {
                var n = equip.GetAscomDrivers(
                    NINA.Ascom.Com.AscomComRegistry.DeviceType.Focuser).Count;
                list.Add(new("ascom-com", "ASCOM (COM, direct)",
                    Available: n > 0,
                    Description: n > 0
                        ? $"Direct COM-interop. {n} driver(s) registered."
                        : "Install the ASCOM Platform + a focuser driver."));
            }
            return Results.Ok(list);
        });

        group.MapPost("/connect", async (EquipmentManager equip) => {
            if (equip.Focuser == null)
                return Results.BadRequest(new { error = "No focuser selected" });

            await equip.Focuser.ConnectAsync();
            return Results.Ok(new { status = "connected", device = equip.Focuser.DeviceName });
        });

        group.MapPost("/disconnect", async (EquipmentManager equip) => {
            if (equip.Focuser == null)
                return Results.BadRequest(new { error = "No focuser selected" });

            await equip.Focuser.DisconnectAsync();
            return Results.Ok(new { status = "disconnected" });
        });

        // Software-only EAF recovery: cycle the per-device CONNECTION
        // switch via DISCONNECT -> 2s wait -> CONNECT. Use this when
        // the EAF firmware is wedged after multiple move-crashes and
        // even tiny moves disconnect the device. If reconnect itself
        // fails, the toast tells the user to physically power-cycle
        // (USB unplug + replug) which is the only fix for hard
        // firmware wedges.
        group.MapPost("/reconnect", async (EquipmentManager equip) => {
            if (equip.Focuser == null)
                return Results.BadRequest(new { error = "No focuser selected" });
            try {
                await equip.Focuser.ReconnectAsync();
                return Results.Ok(new {
                    status = "reconnected",
                    device = equip.Focuser.DeviceName
                });
            } catch (Exception ex) {
                return Results.Json(new {
                    error = "Reconnect failed: " + ex.Message
                }, statusCode: 500);
            }
        });
    }

    public record MoveAbsoluteRequest(int Position);
    public record MoveRelativeRequest(int Steps);
    /// <summary>Body for POST /reverse. Required field.</summary>
    public record ReverseRequest(bool Reversed);
    /// <summary>Body for POST /backlash. Steps is honoured only when
    /// Enabled = true; passing 0 with Enabled = true is allowed
    /// (driver retains its previous value when supported).</summary>
    public record BacklashRequest(bool Enabled, int Steps);
}
