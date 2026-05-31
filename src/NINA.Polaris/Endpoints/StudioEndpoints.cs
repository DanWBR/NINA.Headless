using NINA.Polaris.Services.Studio;

namespace NINA.Polaris.Endpoints;

public static class StudioEndpoints {
    public static void MapStudioEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/studio");

        // Force re-walk of the active profile's image output dir. Runs
        // in the background, progress is exposed via /rescan/status and
        // (later) broadcast on the status WebSocket.
        g.MapPost("/rescan", (FrameLibraryService svc) => {
            _ = Task.Run(() => svc.RescanAsync());
            return Results.Accepted(value: new { status = "started" });
        });

        g.MapGet("/rescan/status", (FrameLibraryService svc) => Results.Ok(svc.Rescan));

        // Paginated frame list. All query params optional. Empty strings
        // are treated as "no filter" by the service.
        g.MapGet("/frames", (FrameLibraryService svc,
            string? type, string? filter, string? target,
            string? dateFrom, string? dateTo,
            int? limit, int? offset) => {
            var q = new FrameQuery(
                Type:     type,
                Filter:   filter,
                Target:   target,
                DateFrom: dateFrom,
                DateTo:   dateTo,
                Limit:    limit  ?? 100,
                Offset:   offset ?? 0);
            return Results.Ok(svc.Query(q));
        });

        g.MapGet("/frames/{id:int}", (FrameLibraryService svc, int id) => {
            var row = svc.GetById(id);
            return row == null ? Results.NotFound() : Results.Ok(row);
        });

        // Returns a JPEG thumbnail (256 px max side). Generated on first
        // request, cached to disk thereafter.
        g.MapGet("/frames/{id:int}/thumb", async (FrameLibraryService svc, int id, CancellationToken ct) => {
            var path = await svc.GetThumbnailAsync(id, ct);
            return path == null ? Results.NotFound() : Results.File(path, "image/jpeg");
        });

        // Aggregate stats, total light frames, total exposure (h),
        // distinct targets / filters. Used by the toolbar header.
        g.MapGet("/stats", (FrameLibraryService svc) => Results.Ok(svc.GetStats()));

        // --- ST-2: viewer / stretch / stats / export ----------------

        // Stretched JPEG preview. Slider drags hit this many times per
        // second; FrameProcessingService keeps a small decoded-frame LRU
        // so the LUT pass is the only work per request. All stretch
        // params are optional, omit them to get the auto-stretch view.
        g.MapGet("/frames/{id:int}/preview", async (FrameProcessingService svc,
            int id, double? black, double? mid, double? white,
            int? max, int? quality, string? format, CancellationToken ct) => {
            var opts = new FrameProcessingService.StretchOptions(black, mid, white);
            var fmt = (format ?? "jpg").Trim().ToLowerInvariant();
            byte[]? bytes;
            string mime;
            if (fmt == "png") {
                bytes = await svc.RenderPngAsync(id, opts, max ?? 1600, ct);
                mime = "image/png";
            } else {
                bytes = await svc.RenderJpegAsync(id, opts, max ?? 1600, quality ?? 85, ct);
                mime = "image/jpeg";
            }
            return bytes == null ? Results.NotFound() : Results.File(bytes, mime);
        });

        // Black/mid/white the UI should preload sliders with for this
        // frame. Cheap to call (uses the LRU decoded buffer).
        g.MapGet("/frames/{id:int}/autostretch", (FrameProcessingService svc, int id) => {
            var p = svc.AutoStretchDefaults(id);
            return p == null ? Results.NotFound() : Results.Ok(new {
                black = p.Black, mid = p.Mid, white = p.White
            });
        });

        // Full statistics + star list. `stars=false` skips StarDetector
        // when the caller only wants histogram + numeric stats, useful
        // for the toolbar that wants a count badge but not the overlay.
        g.MapGet("/frames/{id:int}/stats", (FrameProcessingService svc, int id, bool? stars) => {
            var s = svc.ComputeStats(id, includeStars: stars ?? true);
            return s == null ? Results.NotFound() : Results.Ok(s);
        });

        // Export to {rig}/processed/{target}/. format = tif | png | jpg.
        // stretched=false on TIFF writes the original 16-bit linear data
        // so the user can re-process in PixInsight / Siril without our
        // stretch baked in. PNG/JPG always stretched (8-bit only).
        g.MapPost("/frames/{id:int}/export", async (FrameProcessingService svc,
            int id, string? format, double? black, double? mid, double? white,
            bool? stretched, CancellationToken ct) => {
            var opts = new FrameProcessingService.StretchOptions(black, mid, white);
            var path = await svc.ExportAsync(id, format ?? "tif", opts, stretched ?? true, ct);
            return path == null
                ? Results.NotFound()
                : Results.Ok(new { path });
        });

        // --- ST-3: master calibration frames -------------------------

        // Start a master-frame integration. UNIF-3a: payload is now
        // path-based (no FrameLibrary id required, fresh captures
        // can be stacked without waiting for a rescan). Body:
        //   { framePaths: ["...", ...],
        //     type: "Dark"|"Bias"|"Flat"|"DarkFlat",
        //     method: "Mean"|"Median"|"SigmaClippedMean" }
        // Returns { jobId } the UI polls.
        g.MapPost("/masters", (MasterFrameService svc, MasterRequest req) => {
            if (req.FramePaths == null || req.FramePaths.Count < 2)
                return Results.BadRequest(new { error = "Need at least 2 frames to integrate." });
            if (!Enum.TryParse<MasterType>(req.Type, true, out var type))
                return Results.BadRequest(new { error = $"Unknown master type '{req.Type}'." });
            if (!Enum.TryParse<IntegrationMethod>(req.Method, true, out var method))
                return Results.BadRequest(new { error = $"Unknown method '{req.Method}'." });
            var jobId = svc.StartJob(req.FramePaths, type, method);
            return Results.Accepted(value: new { jobId });
        });

        g.MapGet("/masters/{jobId}/status", (MasterFrameService svc, string jobId) => {
            var p = svc.GetStatus(jobId);
            return p == null ? Results.NotFound() : Results.Ok(p);
        });

        // --- ST-4: light frame calibration ---------------------------

        // Calibrate a batch of lights using auto-matched (or
        // explicitly-overridden) masters. The service applies
        // (light − dark) / normalised_flat and writes a CALSTAT
        // header listing which corrections were applied.
        g.MapPost("/calibrate", (CalibrationService svc, CalibrationService.CalibrationRequest req) => {
            if (req?.LightPaths == null || req.LightPaths.Count == 0)
                return Results.BadRequest(new { error = "Provide at least one light frame path." });
            var jobId = svc.StartJob(req);
            return Results.Accepted(value: new { jobId });
        });

        g.MapGet("/calibrate/{jobId}/status", (CalibrationService svc, string jobId) => {
            var p = svc.GetStatus(jobId);
            return p == null ? Results.NotFound() : Results.Ok(p);
        });

        // --- ST-5: batch stacking (offline integration) --------------

        // Align + integrate N calibrated lights into a single master_light
        // under {rig}/integrated/{target}/{filter}/. Body:
        //   { frameIds: [1,2,...], method: "Mean"|"Median"|"SigmaClippedMean" }
        g.MapPost("/integrate", (BatchStackingService svc,
                                 BatchStackingService.IntegrationRequest req) => {
            if (req?.FramePaths == null || req.FramePaths.Count < 2)
                return Results.BadRequest(new { error = "Need at least 2 frames to integrate." });
            var jobId = svc.StartJob(req);
            return Results.Accepted(value: new { jobId });
        });

        g.MapGet("/integrate/{jobId}/status", (BatchStackingService svc, string jobId) => {
            var p = svc.GetStatus(jobId);
            return p == null ? Results.NotFound() : Results.Ok(p);
        });

        // --- CC-1: channel combine (RGB / LRGB / PixelMath) -----------
        // Combine N per-filter mono masters into one RGB or LRGB FITS.
        // The mono workflow's last missing step before AI cleanup and
        // editor; replaces a PixInsight round-trip for ChannelCombination
        // + LRGBCombination + PixelMath. See
        // docs/user-guide/lrgb-mono-workflow.md for the user-facing flow.
        //
        // Body:
        //   { mode: "rgb"|"lrgb"|"pixelmath",
        //     channelMap: [{ variable: "R", frameId: 42 }, ...],
        //     register: true,    (cross-channel star alignment, default ON)
        //     normalize: true,   (per-channel scale to common median)
        //     lrgbAlgo: "lab"|"ratio",   (LRGB only)
        //     expressions: ["..."],      (PixelMath only)
        //     monoOutput: false,         (PixelMath only)
        //     targetName: "..." }        (optional, defaults to first input's target)
        g.MapPost("/combine", (ChannelCombineService svc,
                               ChannelCombineService.ChannelCombineRequest req) => {
            if (req?.ChannelMap == null || req.ChannelMap.Count < 2) {
                return Results.BadRequest(new {
                    error = "channelMap must contain at least 2 entries."
                });
            }
            try {
                var jobId = svc.StartJob(req);
                return Results.Accepted(value: new { jobId });
            } catch (ArgumentException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        g.MapGet("/combine/{jobId}", (ChannelCombineService svc, string jobId) => {
            var p = svc.GetStatus(jobId);
            return p == null ? Results.NotFound() : Results.Ok(p);
        });

        // --- CCALB-1/2/3: Siril-style color calibration ---------------
        // Calibrate an RGB FITS so the background is neutral grey
        // (BgNeutral) and / or the chosen reference is neutral white
        // (Manual), or fit per-channel gains against catalog star
        // photometry (Photometric / PCC, ships in CCALB-3). Output
        // is always a sibling FITS so the original stays intact.
        //
        // Body:
        //   { frameId: 42, mode: "bg"|"manual"|"pcc",
        //     bgSample: "auto"|"patch",
        //     bgPatch:    { x, y, w, h } | null,
        //     whitePatch: { x, y, w, h } | null }
        g.MapPost("/colorcal", (ColorCalibrationService svc,
                                ColorCalibrationService.ColorCalibrationRequest req) => {
            if (req == null || string.IsNullOrWhiteSpace(req.FramePath)) {
                return Results.BadRequest(new { error = "framePath required." });
            }
            try {
                var jobId = svc.StartJob(req);
                return Results.Accepted(value: new { jobId });
            } catch (ArgumentException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        g.MapGet("/colorcal/{jobId}", (ColorCalibrationService svc, string jobId) => {
            var p = svc.GetStatus(jobId);
            return p == null ? Results.NotFound() : Results.Ok(p);
        });

        // PCC pre-flight: tells the UI whether the bundled APASS
        // catalog is present so the modal can render an "OK" badge
        // (or a "run the download script" hint) before the user
        // commits to running PCC.
        g.MapGet("/colorcal/catalog-status", (NINA.Polaris.Services.Sky.ApassCatalog cat) => {
            return Results.Ok(new {
                available = cat.IsAvailable,
                dbPath = cat.DbPath,
                starCount = cat.IsAvailable ? cat.StarCount : 0,
                source = "APASS DR10",
            });
        });

        // --- ST-6: debayer + background extraction -------------------

        // Debayer an OSC frame to a single-channel luminance plane.
        // Writes a new FITS under {rig}/processed/{target}/. Fails if
        // the source isn't a Bayered raw.
        g.MapPost("/frames/{id:int}/debayer",
            async (FrameOperationsService svc, int id, CancellationToken ct) => {
            var path = await svc.DebayerAsync(id, ct);
            return path == null
                ? Results.BadRequest(new { error = "Frame is not Bayered, or source missing." })
                : Results.Ok(new { path });
        });

        // Subtract a 2D-polynomial background gradient from the frame.
        // samplesX/samplesY/polyDegree are optional knobs.
        g.MapPost("/frames/{id:int}/bgextract",
            async (FrameOperationsService svc, int id,
                   int? samplesX, int? samplesY, int? polyDegree, CancellationToken ct) => {
            var path = await svc.RemoveGradientAsync(id, samplesX, samplesY, polyDegree, ct);
            return path == null
                ? Results.NotFound()
                : Results.Ok(new { path });
        });

        // --- ST-7: post-processing (noise reduction / sharpen) ------

        // Gaussian blur as light noise-reduction. radius in pixels;
        // default 2 (subtle smoothing), max 8.
        g.MapPost("/frames/{id:int}/nr",
            async (FrameOperationsService svc, int id, int? radius, CancellationToken ct) => {
            var path = await svc.NoiseReductionAsync(id, radius, ct);
            return path == null ? Results.NotFound() : Results.Ok(new { path });
        });

        // Unsharp-mask sharpening. amount = boost factor (typical 1.0
        // moderate, 2-3 aggressive). radius = blur kernel pixels.
        // threshold = min ADU difference to apply boost (keeps noise
        // floor calm).
        g.MapPost("/frames/{id:int}/sharpen",
            async (FrameOperationsService svc, int id,
                   double? amount, int? radius, int? threshold, CancellationToken ct) => {
            var path = await svc.SharpenAsync(id, amount, radius, threshold, ct);
            return path == null ? Results.NotFound() : Results.Ok(new { path });
        });
    }

    // POST body for /masters. Kept in the endpoints file (not the
    // service) because it's purely an API contract, and Enum names go
    // over the wire as strings to keep the JS side legible.
    // UNIF-3a: path-based contract. The Stack sub-tab posts absolute
    // paths straight from the user's slot assignments.
    public record MasterRequest(List<string> FramePaths, string Type, string Method);
}
