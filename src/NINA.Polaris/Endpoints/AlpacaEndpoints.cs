using NINA.Polaris.Services.Alpaca;

namespace NINA.Polaris.Endpoints;

public static class AlpacaEndpoints {
    public static void MapAlpacaEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/alpaca");

        // ---- Discovery ----
        group.MapGet("/discover", async (AlpacaDiscovery disc, int? timeoutMs) => {
            var to = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs ?? 3000, 200, 15000));
            var servers = await disc.DiscoverServersAsync(to);
            return Results.Ok(new { count = servers.Count, servers });
        });

        // ---- Manual server query (skip discovery, useful behind NAT / for tests) ----
        group.MapGet("/devices", async (string host, int port) => {
            // Reject obvious garbage upfront so the user sees a clean
            // 400 with an actionable message instead of a generic 500
            // wrapping a SocketException. Common typo: "1270.0.0.1"
            // (extra zero) — DNS won't resolve, .NET surfaces a
            // System.Net.Sockets.SocketException which isn't useful
            // to a non-technical user. Same logic as the JS
            // _isValidHostOrIp helper, kept here as defence-in-depth
            // for callers that bypass the UI (curl / scripts / future
            // mobile clients).
            if (string.IsNullOrWhiteSpace(host)) {
                return Results.BadRequest(new { error = "Missing host." });
            }
            if (port < 1 || port > 65535) {
                return Results.BadRequest(new { error = $"Invalid port {port}; must be 1-65535." });
            }
            // System.Uri rejects malformed hostnames + Uri.CheckHostName
            // returns Unknown for "1270.0.0.1" etc.
            var hostKind = Uri.CheckHostName(host);
            if (hostKind == UriHostNameType.Unknown) {
                return Results.BadRequest(new {
                    error = $"'{host}' is not a valid IP address or hostname.",
                    hint = "Check for typos — '1270.0.0.1' is invalid (octet > 255); use '127.0.0.1'."
                });
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var url = $"http://{host}:{port}/management/v1/configureddevices";
            try {
                var resp = await http.GetFromJsonAsync<AlpacaResponse<List<AlpacaConfiguredDevice>>>(url);
                return Results.Ok(new {
                    host, port,
                    devices = resp?.Value ?? new List<AlpacaConfiguredDevice>()
                });
            } catch (HttpRequestException ex) {
                // Connection refused / DNS / timeout — server not reachable.
                // 502 Bad Gateway is the right status: our endpoint is up
                // but the upstream Alpaca server isn't.
                return Results.Json(new {
                    error = $"Could not reach Alpaca server at {host}:{port}.",
                    detail = ex.Message,
                    hint = "Confirm the host + port are correct and the Alpaca server is running."
                }, statusCode: 502);
            } catch (TaskCanceledException) {
                return Results.Json(new {
                    error = $"Timeout reading from {host}:{port}.",
                    hint = "Alpaca server is reachable but took longer than 5s to respond."
                }, statusCode: 504);
            } catch (System.Text.Json.JsonException ex) {
                return Results.Json(new {
                    error = $"Alpaca server at {host}:{port} returned invalid JSON.",
                    detail = ex.Message
                }, statusCode: 502);
            } catch (Exception ex) {
                return Results.Problem($"Unexpected error querying {host}:{port}: {ex.Message}");
            }
        });

        // ---- Camera info probe ----
        group.MapGet("/camera/info", async (string host, int port, int? device) => {
            var cam = new AlpacaCamera(host, port, device ?? 0);
            try {
                return Results.Ok(new {
                    name = await cam.GetNameAsync(),
                    description = await cam.GetDescriptionAsync(),
                    connected = await cam.GetConnectedAsync(),
                    width = await cam.GetCameraXSizeAsync(),
                    height = await cam.GetCameraYSizeAsync(),
                    pixelSizeX = await cam.GetPixelSizeXAsync(),
                    pixelSizeY = await cam.GetPixelSizeYAsync(),
                    coolerOn = await cam.GetCoolerOnAsync(),
                    ccdTemp = await cam.GetCcdTemperatureAsync(),
                    setTemp = await cam.GetSetCcdTemperatureAsync(),
                    binX = await cam.GetBinXAsync(),
                    maxBinX = await cam.GetMaxBinXAsync()
                });
            } catch (Exception ex) {
                return Results.Problem($"Alpaca camera query failed: {ex.Message}");
            }
        });

        group.MapPost("/camera/connect", async (string host, int port, int? device, bool connect) => {
            var cam = new AlpacaCamera(host, port, device ?? 0);
            try {
                await cam.SetConnectedAsync(connect);
                return Results.Ok(new { connected = await cam.GetConnectedAsync() });
            } catch (Exception ex) {
                return Results.Problem($"Alpaca camera connect failed: {ex.Message}");
            }
        });

        // ---- Telescope info probe ----
        group.MapGet("/telescope/info", async (string host, int port, int? device) => {
            var scope = new AlpacaTelescope(host, port, device ?? 0);
            try {
                return Results.Ok(new {
                    name = await scope.GetNameAsync(),
                    connected = await scope.GetConnectedAsync(),
                    ra = await scope.GetRightAscensionAsync(),
                    dec = await scope.GetDeclinationAsync(),
                    alt = await scope.GetAltitudeAsync(),
                    az = await scope.GetAzimuthAsync(),
                    slewing = await scope.GetSlewingAsync(),
                    atPark = await scope.GetAtParkAsync(),
                    tracking = await scope.GetTrackingAsync()
                });
            } catch (Exception ex) {
                return Results.Problem($"Alpaca telescope query failed: {ex.Message}");
            }
        });

        group.MapPost("/telescope/connect", async (string host, int port, int? device, bool connect) => {
            var scope = new AlpacaTelescope(host, port, device ?? 0);
            try {
                await scope.SetConnectedAsync(connect);
                return Results.Ok(new { connected = await scope.GetConnectedAsync() });
            } catch (Exception ex) {
                return Results.Problem($"Alpaca telescope connect failed: {ex.Message}");
            }
        });

        // ---- Focuser ----
        group.MapGet("/focuser/info", async (string host, int port, int? device) => {
            var f = new AlpacaFocuser(host, port, device ?? 0);
            try {
                return Results.Ok(new {
                    name = await f.GetNameAsync(),
                    connected = await f.GetConnectedAsync(),
                    position = await f.GetPositionAsync(),
                    maxStep = await f.GetMaxStepAsync(),
                    isMoving = await f.GetIsMovingAsync(),
                    temperature = await f.GetTemperatureAsync(),
                    absolute = await f.GetAbsoluteAsync()
                });
            } catch (Exception ex) { return Results.Problem($"Alpaca focuser query failed: {ex.Message}"); }
        });
        group.MapPost("/focuser/connect", async (string host, int port, int? device, bool connect) => {
            var f = new AlpacaFocuser(host, port, device ?? 0);
            try { await f.SetConnectedAsync(connect); return Results.Ok(new { connected = await f.GetConnectedAsync() }); }
            catch (Exception ex) { return Results.Problem($"Alpaca focuser connect failed: {ex.Message}"); }
        });
        group.MapPost("/focuser/move", async (string host, int port, int? device, int position) => {
            var f = new AlpacaFocuser(host, port, device ?? 0);
            try { await f.MoveAsync(position); return Results.Ok(new { moved = true, target = position }); }
            catch (Exception ex) { return Results.Problem($"Alpaca focuser move failed: {ex.Message}"); }
        });

        // ---- FilterWheel ----
        group.MapGet("/filterwheel/info", async (string host, int port, int? device) => {
            var fw = new AlpacaFilterWheel(host, port, device ?? 0);
            try {
                return Results.Ok(new {
                    name = await fw.GetNameAsync(),
                    connected = await fw.GetConnectedAsync(),
                    position = await fw.GetPositionAsync(),
                    names = await fw.GetNamesAsync(),
                    focusOffsets = await fw.GetFocusOffsetsAsync()
                });
            } catch (Exception ex) { return Results.Problem($"Alpaca filterwheel query failed: {ex.Message}"); }
        });
        group.MapPost("/filterwheel/connect", async (string host, int port, int? device, bool connect) => {
            var fw = new AlpacaFilterWheel(host, port, device ?? 0);
            try { await fw.SetConnectedAsync(connect); return Results.Ok(new { connected = await fw.GetConnectedAsync() }); }
            catch (Exception ex) { return Results.Problem($"Alpaca filterwheel connect failed: {ex.Message}"); }
        });
        group.MapPost("/filterwheel/position", async (string host, int port, int? device, int slot) => {
            var fw = new AlpacaFilterWheel(host, port, device ?? 0);
            try { await fw.SetPositionAsync(slot); return Results.Ok(new { position = slot }); }
            catch (Exception ex) { return Results.Problem($"Alpaca filterwheel position failed: {ex.Message}"); }
        });

        // ---- Rotator ----
        group.MapGet("/rotator/info", async (string host, int port, int? device) => {
            var r = new AlpacaRotator(host, port, device ?? 0);
            try {
                return Results.Ok(new {
                    name = await r.GetNameAsync(),
                    connected = await r.GetConnectedAsync(),
                    position = await r.GetPositionAsync(),
                    targetPosition = await r.GetTargetPositionAsync(),
                    isMoving = await r.GetIsMovingAsync(),
                    reverse = await r.GetReverseAsync()
                });
            } catch (Exception ex) { return Results.Problem($"Alpaca rotator query failed: {ex.Message}"); }
        });
        group.MapPost("/rotator/connect", async (string host, int port, int? device, bool connect) => {
            var r = new AlpacaRotator(host, port, device ?? 0);
            try { await r.SetConnectedAsync(connect); return Results.Ok(new { connected = await r.GetConnectedAsync() }); }
            catch (Exception ex) { return Results.Problem($"Alpaca rotator connect failed: {ex.Message}"); }
        });
        group.MapPost("/rotator/move", async (string host, int port, int? device, double degrees) => {
            var r = new AlpacaRotator(host, port, device ?? 0);
            try { await r.MoveAbsoluteAsync(degrees); return Results.Ok(new { target = degrees }); }
            catch (Exception ex) { return Results.Problem($"Alpaca rotator move failed: {ex.Message}"); }
        });

        // ---- Dome ----
        group.MapGet("/dome/info", async (string host, int port, int? device) => {
            var d = new AlpacaDome(host, port, device ?? 0);
            try {
                return Results.Ok(new {
                    name = await d.GetNameAsync(),
                    connected = await d.GetConnectedAsync(),
                    azimuth = await d.GetAzimuthAsync(),
                    atPark = await d.GetAtParkAsync(),
                    atHome = await d.GetAtHomeAsync(),
                    slewing = await d.GetSlewingAsync(),
                    shutterStatus = await d.GetShutterStatusAsync(),
                    slaved = await d.GetSlavedAsync()
                });
            } catch (Exception ex) { return Results.Problem($"Alpaca dome query failed: {ex.Message}"); }
        });
        group.MapPost("/dome/connect", async (string host, int port, int? device, bool connect) => {
            var d = new AlpacaDome(host, port, device ?? 0);
            try { await d.SetConnectedAsync(connect); return Results.Ok(new { connected = await d.GetConnectedAsync() }); }
            catch (Exception ex) { return Results.Problem($"Alpaca dome connect failed: {ex.Message}"); }
        });
        group.MapPost("/dome/shutter/{action}", async (string action, string host, int port, int? device) => {
            var d = new AlpacaDome(host, port, device ?? 0);
            try {
                if (action == "open") await d.OpenShutterAsync();
                else if (action == "close") await d.CloseShutterAsync();
                else return Results.BadRequest(new { error = "action must be open or close" });
                return Results.Ok(new { shutter = action });
            } catch (Exception ex) { return Results.Problem($"Alpaca dome shutter failed: {ex.Message}"); }
        });
        group.MapPost("/dome/park", async (string host, int port, int? device) => {
            var d = new AlpacaDome(host, port, device ?? 0);
            try { await d.ParkAsync(); return Results.Ok(new { parked = true }); }
            catch (Exception ex) { return Results.Problem($"Alpaca dome park failed: {ex.Message}"); }
        });
        group.MapPost("/dome/slew", async (string host, int port, int? device, double azimuth) => {
            var d = new AlpacaDome(host, port, device ?? 0);
            try { await d.SlewToAzimuthAsync(azimuth); return Results.Ok(new { azimuth }); }
            catch (Exception ex) { return Results.Problem($"Alpaca dome slew failed: {ex.Message}"); }
        });

        // ---- CoverCalibrator (flat panel) ----
        // Alpaca's modern flat-panel interface is "covercalibrator". Older ASCOM
        // drivers may expose a separate "Switch" device, those don't surface here.
        group.MapGet("/covercalibrator/info", async (string host, int port, int? device) => {
            var c = new AlpacaCoverCalibrator(host, port, device ?? 0);
            try {
                return Results.Ok(new {
                    name = await c.GetNameAsync(),
                    connected = await c.GetConnectedAsync(),
                    calibratorState = await c.GetCalibratorStateAsync(),
                    coverState = await c.GetCoverStateAsync(),
                    brightness = await c.GetBrightnessAsync(),
                    maxBrightness = await c.GetMaxBrightnessAsync()
                });
            } catch (Exception ex) { return Results.Problem($"Alpaca covercalibrator query failed: {ex.Message}"); }
        });
        group.MapPost("/covercalibrator/connect", async (string host, int port, int? device, bool connect) => {
            var c = new AlpacaCoverCalibrator(host, port, device ?? 0);
            try { await c.SetConnectedAsync(connect); return Results.Ok(new { connected = await c.GetConnectedAsync() }); }
            catch (Exception ex) { return Results.Problem($"Alpaca covercalibrator connect failed: {ex.Message}"); }
        });
        group.MapPost("/covercalibrator/cover/{action}", async (string action, string host, int port, int? device) => {
            var c = new AlpacaCoverCalibrator(host, port, device ?? 0);
            try {
                if (action == "open") await c.OpenCoverAsync();
                else if (action == "close") await c.CloseCoverAsync();
                else return Results.BadRequest(new { error = "action must be open or close" });
                return Results.Ok(new { cover = action });
            } catch (Exception ex) { return Results.Problem($"Alpaca covercalibrator cover failed: {ex.Message}"); }
        });
        group.MapPost("/covercalibrator/calibrator", async (string host, int port, int? device, int? brightness) => {
            var c = new AlpacaCoverCalibrator(host, port, device ?? 0);
            try {
                if (brightness.HasValue) await c.CalibratorOnAsync(brightness.Value);
                else await c.CalibratorOffAsync();
                return Results.Ok(new { brightness = brightness ?? 0, on = brightness.HasValue });
            } catch (Exception ex) { return Results.Problem($"Alpaca covercalibrator failed: {ex.Message}"); }
        });

        // ---- ObservingConditions (weather) ----
        group.MapGet("/observingconditions/info", async (string host, int port, int? device) => {
            var w = new AlpacaObservingConditions(host, port, device ?? 0);
            try {
                return Results.Ok(new {
                    name = await w.GetNameAsync(),
                    connected = await w.GetConnectedAsync(),
                    cloudCover = await w.GetCloudCoverAsync(),
                    dewPoint = await w.GetDewPointAsync(),
                    humidity = await w.GetHumidityAsync(),
                    pressure = await w.GetPressureAsync(),
                    rainRate = await w.GetRainRateAsync(),
                    skyBrightness = await w.GetSkyBrightnessAsync(),
                    skyQuality = await w.GetSkyQualityAsync(),
                    skyTemperature = await w.GetSkyTemperatureAsync(),
                    temperature = await w.GetTemperatureAsync(),
                    windDirection = await w.GetWindDirectionAsync(),
                    windGust = await w.GetWindGustAsync(),
                    windSpeed = await w.GetWindSpeedAsync()
                });
            } catch (Exception ex) { return Results.Problem($"Alpaca observingconditions query failed: {ex.Message}"); }
        });
        group.MapPost("/observingconditions/connect", async (string host, int port, int? device, bool connect) => {
            var w = new AlpacaObservingConditions(host, port, device ?? 0);
            try { await w.SetConnectedAsync(connect); return Results.Ok(new { connected = await w.GetConnectedAsync() }); }
            catch (Exception ex) { return Results.Problem($"Alpaca observingconditions connect failed: {ex.Message}"); }
        });
    }
}
