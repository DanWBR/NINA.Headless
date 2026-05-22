using NINA.Headless.Services.Studio;

namespace NINA.Headless.Endpoints;

public static class StudioEndpoints {
    public static void MapStudioEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/studio");

        // Force re-walk of the active profile's image output dir. Runs
        // in the background — progress is exposed via /rescan/status and
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

        // Aggregate stats — total light frames, total exposure (h),
        // distinct targets / filters. Used by the toolbar header.
        g.MapGet("/stats", (FrameLibraryService svc) => Results.Ok(svc.GetStats()));

        // --- ST-2: viewer / stretch / stats / export ----------------

        // Stretched JPEG preview. Slider drags hit this many times per
        // second; FrameProcessingService keeps a small decoded-frame LRU
        // so the LUT pass is the only work per request. All stretch
        // params are optional — omit them to get the auto-stretch view.
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
        // when the caller only wants histogram + numeric stats — useful
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
    }
}
