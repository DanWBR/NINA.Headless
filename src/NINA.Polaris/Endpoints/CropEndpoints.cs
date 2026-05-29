using NINA.Polaris.Services;
using NINA.Polaris.Services.Studio;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// Single endpoint: POST /api/crop/run with a body that names one or
/// more source files + a rectangular ROI in image pixel space.
/// Returns the list of output paths (one per input, all sharing the
/// same ROI). The frame library gets a rescan after the writes so the
/// new files show up in STUDIO without a manual refresh.
///
/// Synchronous on the wire (no async-job dance like GraXpert): the
/// actual work is a buffer slice + a FITS write, totalling &lt; 1 s for
/// typical masters. If batch sizes ever climb large enough to feel
/// slow, this is the obvious place to add streaming progress.
/// </summary>
public static class CropEndpoints {
    public static void MapCropEndpoints(this IEndpointRouteBuilder app) {
        var g = app.MapGroup("/api/crop");

        g.MapPost("/run", async (
                CropService svc,
                FrameLibraryService library,
                CropRequest req) => {
            if (req.Paths == null || req.Paths.Length == 0)
                return Results.BadRequest(new { error = "paths is required" });
            if (req.Width <= 0 || req.Height <= 0)
                return Results.BadRequest(new {
                    error = "width and height must be positive"
                });

            var results = new List<object>();
            var failures = new List<object>();
            foreach (var path in req.Paths) {
                try {
                    var r = svc.CropFits(path, req.X, req.Y, req.Width, req.Height);
                    results.Add(new {
                        sourcePath = path,
                        outputPath = r.OutputPath,
                        width = r.Width,
                        height = r.Height,
                        channels = r.Channels
                    });
                } catch (Exception ex) {
                    failures.Add(new { sourcePath = path, error = ex.Message });
                }
            }

            // Reindex so the new _crop.fits siblings show up in STUDIO
            // + the FILES browser without the user hitting Refresh.
            // Same pattern Studio post-processing services use after
            // writing sibling files.
            if (results.Count > 0) {
                try { await library.RescanAsync(); } catch { /* best-effort */ }
            }

            return Results.Ok(new { results, failures });
        });
    }

    public record CropRequest(string[] Paths, int X, int Y, int Width, int Height);
}
