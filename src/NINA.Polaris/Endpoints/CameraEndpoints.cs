using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

public static class CameraEndpoints {
    public static void MapCameraEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/camera");

        group.MapPost("/capture", async (EquipmentManager equip, ImageRelayService relay,
            LiveStackingService liveStack, ImageWriterService imageWriter,
            CaptureRequest request) => {
            if (equip.Camera == null)
                return Results.BadRequest(new { error = "No camera selected" });

            try {
                if (request.Binning > 0)
                    await equip.Camera.SetBinningAsync(request.Binning, request.Binning);

                // Optional pre-capture filter swap. Honour only when the
                // request carried a non-empty string AND the wheel is
                // actually connected, otherwise silently keep whatever
                // filter is already in place.
                // Optional pre-capture filter swap. FilterWheel is null
                // when not selected/connected, same convention used
                // throughout EquipmentManager. We only swap on a non-empty
                // string in the request, so passing null/"" keeps the
                // wheel where it is.
                if (!string.IsNullOrWhiteSpace(request.Filter)
                    && equip.FilterWheel != null) {
                    try {
                        await equip.FilterWheel.SetFilterByNameAsync(request.Filter);
                    } catch {
                        // Don't fail the whole capture on a filter swap
                        // error, the user sees the wrong filter in
                        // stats and can abort if it matters.
                    }
                }

                var imageData = await equip.Camera.CaptureAsync(request.Exposure);

                // PREVIEW tab: opt-in disk save under {rig}/snaps/.
                // ImageWriterService is a no-op when ImageOutputDir is
                // empty so we don't need to gate on profile state here.
                if (request.SaveToDisk && imageData != null) {
                    if (!string.IsNullOrEmpty(request.Filter))
                        imageData.MetaData.Exposure.Filter = request.Filter;
                    imageWriter.SaveImage(imageData,
                        targetName: request.TargetName ?? "snap",
                        imageType: "SNAP",
                        gain: request.Gain);
                }

                // FeedLiveStack=false from the caller forces relay-only
                // even if the live stack is currently running — that's
                // how PREVIEW / FOCUS-Manual / Flat Wizard say "this
                // is a one-off test shot, don't pollute the stack +
                // don't trigger the auto-recenter reference solve".
                // Null = legacy behaviour (feed if running).
                var feedStack = request.FeedLiveStack ?? true;
                if (feedStack && liveStack.IsRunning)
                    await liveStack.AddFrameAsync(imageData!);
                else
                    await relay.RelayImageAsync(imageData!);

                var stats = imageData!.Statistics;
                // MFOC-1: Laplacian variance sharpness metric. Cheap
                // 3x3 convolution over a 256 px ROI in the centre of
                // the frame (FrameQualityAnalyzer caps the ROI to keep
                // this < 5 ms even on Pi 4). Consumed by the FOCUS
                // tab's Manual subtab loop, where it's a secondary
                // focus indicator for sparse / no-star scenes (lunar,
                // Bahtinov-on-a-bright-star) that defeat HFR.
                double laplacianVar = 0;
                try {
                    laplacianVar = NINA.Polaris.Services.Planetary
                        .FrameQualityAnalyzer.LaplacianVariance(
                            imageData.Data, imageData.Properties.Width,
                            imageData.Properties.Height, roiSize: 256);
                } catch { /* defensive: never fail the capture for a
                             secondary stats field */ }
                return Results.Ok(new {
                    status = "complete",
                    width = imageData.Properties.Width,
                    height = imageData.Properties.Height,
                    saved = request.SaveToDisk,
                    stats = new {
                        mean = stats.Mean,
                        median = stats.Median,
                        stdev = stats.StDev,
                        starCount = stats.StarCount,
                        hfr = stats.HFR,
                        laplacianVar = laplacianVar,
                        min = stats.Min,
                        max = stats.Max
                    }
                });
            } catch (OperationCanceledException) {
                return Results.Ok(new { status = "cancelled" });
            } catch (Exception ex) {
                return Results.Problem(ex.Message);
            }
        });

        group.MapPost("/abort", async (EquipmentManager equip) => {
            if (equip.Camera == null)
                return Results.BadRequest(new { error = "No camera selected" });

            await equip.Camera.AbortExposureAsync();
            return Results.Ok(new { status = "aborted" });
        });

        group.MapGet("/status", (EquipmentManager equip) => {
            if (equip.Camera == null)
                return Results.Ok(new {
                    connected = false,
                    state = "disconnected",
                    temperature = (double?)null,
                    coolerOn = false,
                    binX = 0, binY = 0
                });

            return Results.Ok(new {
                connected = equip.Camera.IsConnected,
                state = equip.Camera.State.ToString(),
                temperature = NanToNull(equip.Camera.Temperature),
                coolerOn = equip.Camera.CoolerOn,
                binX = equip.Camera.BinX,
                binY = equip.Camera.BinY,
                maxX = equip.Camera.MaxX,
                maxY = equip.Camera.MaxY,
                pixelSizeX = NanToNull(equip.Camera.PixelSizeX),
                pixelSizeY = NanToNull(equip.Camera.PixelSizeY),
                bitDepth = equip.Camera.BitDepth,
                whiteBalanceR = equip.Camera.WhiteBalanceR,
                whiteBalanceB = equip.Camera.WhiteBalanceB,
                capabilities = new {
                    cooler = equip.Camera.Capabilities.SupportsCooler,
                    binning = equip.Camera.Capabilities.SupportsBinning,
                    roi = equip.Camera.Capabilities.SupportsRoi,
                    iso = equip.Camera.Capabilities.SupportsIso,
                    bulb = equip.Camera.Capabilities.SupportsBulb,
                    videoStream = equip.Camera.Capabilities.SupportsVideoStream,
                    whiteBalance = equip.Camera.Capabilities.SupportsWhiteBalance
                }
            });
        });

        // Live R/B white-balance writes for OSC color cameras (ZWO/QHY
        // expose WB_R + WB_B under CCD_CONTROLS). 501 surfaces clearly
        // when the active camera doesn't expose WB, so the UI can hide
        // the slider when SupportsWhiteBalance is false instead of
        // showing it and silently failing on writes.
        group.MapPost("/white-balance", async (EquipmentManager equip, WhiteBalanceRequest req) => {
            if (equip.Camera == null)
                return Results.BadRequest(new { error = "No camera selected" });
            if (!equip.Camera.Capabilities.SupportsWhiteBalance)
                return Results.Json(new { error = "Camera does not support white balance" },
                    statusCode: 501);
            await equip.Camera.SetWhiteBalanceAsync(req.Red, req.Blue);
            return Results.Ok(new { red = req.Red, blue = req.Blue });
        });

        // VIDEO tab FOV / ROI selection. Planetary capture needs the
        // sensor cropped to a tight box around the planet (Jupiter is
        // ~50 arcsec at typical focal lengths so a 640 x 480 box at
        // sensor center is plenty), so the SER file stays small + fps
        // climbs. width=0 / height=0 clears the ROI and returns to
        // the full sensor.
        group.MapPost("/subframe", async (EquipmentManager equip, SubframeRequest req) => {
            if (equip.Camera == null)
                return Results.BadRequest(new { error = "No camera selected" });
            if (!equip.Camera.Capabilities.SupportsRoi)
                return Results.Json(new { error = "Camera does not support ROI / subframe" },
                    statusCode: 501);
            await equip.Camera.SetSubframeAsync(req.X, req.Y, req.Width, req.Height);
            return Results.Ok(new {
                x = req.X, y = req.Y,
                width = req.Width, height = req.Height,
                full = req.Width <= 0 || req.Height <= 0
            });
        });

        group.MapPost("/cooler", async (EquipmentManager equip, CoolerRequest request) => {
            if (equip.Camera == null)
                return Results.BadRequest(new { error = "No camera selected" });

            await equip.Camera.SetCoolerAsync(request.Enabled);
            if (request.TargetTemperature.HasValue)
                await equip.Camera.SetTemperatureAsync(request.TargetTemperature.Value);

            return Results.Ok(new { coolerOn = request.Enabled, target = request.TargetTemperature });
        });

        group.MapPost("/select/{deviceName}", (EquipmentManager equip, string deviceName, string? driver) => {
            // Default driver is "indi" so existing clients (which only
            // pass the device name) keep working untouched. DSLR / Alpaca
            // callers add ?driver=canon-edsdk etc.
            try {
                equip.SelectCamera(driver ?? "indi", deviceName);
                return Results.Ok(new {
                    selected = deviceName,
                    driver = driver ?? "indi"
                });
            } catch (NotSupportedException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Available camera driver kinds for the current host. Always
        // includes 'indi'; vendor SDK entries are listed only on the
        // platforms that can support them, with an `available` flag
        // for whether the native dependency is actually present.
        group.MapGet("/drivers", (EquipmentManager equip)
            => Results.Ok(equip.GetAvailableCameraDrivers()));

        // Per-driver camera discovery. For INDI: the device-name
        // list from the active connection. For vendor SDKs: the
        // SDK-specific enumeration call. Empty list when no cameras
        // are connected (or when the driver isn't supported on this
        // OS), never throws.
        group.MapGet("/discover", (EquipmentManager equip, string? driver)
            => Results.Ok(equip.GetDiscoveredCamerasFor(driver ?? "indi")));

        group.MapPost("/connect", async (EquipmentManager equip) => {
            if (equip.Camera == null)
                return Results.BadRequest(new { error = "No camera selected. Use POST /api/camera/select/{name} first" });

            await equip.Camera.ConnectAsync();
            return Results.Ok(new { status = "connected", device = equip.Camera.DeviceName });
        });

        group.MapPost("/disconnect", async (EquipmentManager equip) => {
            if (equip.Camera == null)
                return Results.BadRequest(new { error = "No camera selected" });

            await equip.Camera.DisconnectAsync();
            return Results.Ok(new { status = "disconnected" });
        });

        // ----- Video stream (continuous frame feed) -----
        // Auto-picks native CCD_VIDEO_STREAM mode when the camera
        // supports it; falls back to a tight server-side capture loop
        // for any other backend. Frames bypass FITS save + stats and go
        // straight to the existing /ws/image-stream channel.

        group.MapPost("/stream/start", (EquipmentManager equip,
                                        CameraStreamService stream,
                                        StreamStartRequest? request) => {
            if (equip.Camera == null)
                return Results.BadRequest(new { error = "No camera connected" });
            try {
                stream.Start(new StreamConfig(
                    ExposureSeconds: request?.Exposure ?? 0.1,
                    Gain: request?.Gain,
                    BinX: request?.Binning ?? 1,
                    BinY: request?.Binning ?? 1,
                    ForceLoop: request?.ForceLoop ?? false));
                return Results.Ok(new {
                    running = true,
                    mode = stream.Mode,
                    supportsNative = equip.Camera.Capabilities.SupportsVideoStream
                });
            } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapPost("/stream/stop", async (CameraStreamService stream) => {
            await stream.StopAsync();
            return Results.Ok(new { running = false, frames = stream.FrameCount });
        });

        group.MapGet("/stream/status", (CameraStreamService stream, EquipmentManager equip) => Results.Ok(new {
            running = stream.IsRunning,
            mode = stream.Mode,
            exposure = stream.ExposureSeconds,
            gain = stream.Gain,
            binX = stream.BinX,
            binY = stream.BinY,
            frames = stream.FrameCount,
            fps = stream.Fps,
            startedAt = stream.IsRunning ? stream.StartedAt : (DateTime?)null,
            lastFrameAt = stream.IsRunning ? stream.LastFrameAt : (DateTime?)null,
            lastError = stream.LastError,
            supportsNative = equip.Camera?.Capabilities.SupportsVideoStream ?? false
        }));
    }

    static double? NanToNull(double v) => double.IsNaN(v) ? null : v;

    /// <summary>
    /// Capture-request body. <see cref="SaveToDisk"/> + <see cref="TargetName"/>
    /// are the PREVIEW-tab additions: when SaveToDisk is true the
    /// handler also runs ImageWriterService.SaveImage with imageType
    /// = "SNAP" (which BuildSubDir routes into {rig}/snaps/{filter}_{date}/).
    /// </summary>
    public record CaptureRequest(
        double Exposure = 1.0,
        int Gain = 100,
        int Binning = 1,
        string? Filter = null,
        bool SaveToDisk = false,
        string? TargetName = null,
        // null = legacy "feed if running" (LIVE tab default).
        // true  = always feed (sequence engine, explicit live capture).
        // false = never feed (PREVIEW snap, FOCUS Manual test shot,
        //         Bahtinov / flat-wizard / anything that's not a
        //         science frame). Without this, with the always-on
        //         stacking we now default to, a tap on PREVIEW would
        //         silently push a junk frame into the live stack and
        //         fire the auto-recenter plate solve.
        bool? FeedLiveStack = null);
    public record CoolerRequest(bool Enabled, double? TargetTemperature = null);

    /// <summary>White-balance body. Range is driver-specific, ZWO/QHY
    /// typically 0..100 with 50 = neutral; UI bounds the slider to that
    /// per default and lets the user push outside.</summary>
    public record WhiteBalanceRequest(double Red, double Blue);
    public record SubframeRequest(int X, int Y, int Width, int Height);

    /// <summary>Start-stream body. ForceLoop=true skips native streaming
    /// even when the camera supports it (debugging the fallback).</summary>
    public record StreamStartRequest(
        double? Exposure = null,
        int? Gain = null,
        int? Binning = null,
        bool? ForceLoop = null);
}
