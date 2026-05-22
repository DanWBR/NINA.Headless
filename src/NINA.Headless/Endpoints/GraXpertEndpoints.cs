using Microsoft.AspNetCore.Http;
using NINA.Headless.Services;
using NINA.Headless.Services.External;

namespace NINA.Headless.Endpoints;

/// <summary>
/// HTTP surface for GraXpertService. Same shape as SirilEndpoints:
/// status + diagnostic for the Settings panel, run/jobs/cancel for
/// the actual processing. Single endpoint accepts any of the three
/// operations (BGE, Deconvolution, Denoising) via the request body.
/// </summary>
public static class GraXpertEndpoints {
    public static void MapGraXpertEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/graxpert");

        g.MapGet("/status", (GraXpertService gx) => Results.Ok(new {
            available = gx.IsAvailable,
            binaryPath = gx.BinaryPath,
            version = gx.Version,
            supportsDeconvolution = gx.SupportsDeconvolution,
            supportsDenoising = gx.SupportsDenoising
        }));

        g.MapGet("/diagnostic", (GraXpertService gx) => Results.Ok(new {
            binaryCandidates = gx.EnumerateBinaryCandidates()
        }));

        g.MapPost("/run", (GraXpertService gx, GraXpertRunRequest req) => {
            if (!gx.IsAvailable)
                return Results.Json(new { error = "GraXpert is not installed on this host" },
                    statusCode: StatusCodes.Status409Conflict);
            if (req.Paths == null || req.Paths.Count == 0)
                return Results.BadRequest(new { error = "paths is required" });

            var op = ParseOperation(req.Operation);
            if (op == null)
                return Results.BadRequest(new { error = $"Unknown operation: {req.Operation}" });
            if (op == GraXpertOperation.Deconvolution && !gx.SupportsDeconvolution)
                return Results.Json(new { error = "Deconvolution requires GraXpert v3.0+" },
                    statusCode: StatusCodes.Status409Conflict);
            if (op == GraXpertOperation.Denoising && !gx.SupportsDenoising)
                return Results.Json(new { error = "Denoising requires GraXpert v3.0+" },
                    statusCode: StatusCodes.Status409Conflict);

            var opts = new GraXpertOptions(
                Operation: op.Value,
                Correction: req.Correction ?? "Subtraction",
                Smoothing: req.Smoothing ?? 1.0,
                SaveBackground: req.SaveBackground ?? false,
                DeconStrength: req.DeconStrength ?? 0.5,
                DeconPsfSize: req.DeconPsfSize ?? 4.0,
                DenoiseStrength: req.DenoiseStrength ?? 0.5);
            var job = gx.StartBatch(new GraXpertBatchRequest(
                req.Paths, opts, req.Concurrency ?? 1));
            return Results.Accepted(value: new { jobId = job.JobId });
        });

        g.MapGet("/jobs/{jobId}", (GraXpertService gx, string jobId) => {
            var job = gx.GetJob(jobId);
            return job == null ? Results.NotFound() : Results.Ok(job);
        });

        g.MapGet("/jobs", (GraXpertService gx) => Results.Ok(gx.ActiveJobs));

        g.MapPost("/jobs/{jobId}/cancel", (GraXpertService gx, string jobId) => {
            var ok = gx.CancelJob(jobId);
            return ok ? Results.Ok(new { ok = true })
                      : Results.NotFound(new { error = "Job not found or already finished" });
        });

        g.MapPost("/redetect", (GraXpertService gx) => {
            gx.InvalidateVersionCache();
            return Results.Ok(new {
                available = gx.IsAvailable,
                binaryPath = gx.BinaryPath,
                version = gx.Version,
                supportsDeconvolution = gx.SupportsDeconvolution,
                supportsDenoising = gx.SupportsDenoising
            });
        });
    }

    private static GraXpertOperation? ParseOperation(string? s) {
        return (s ?? "").ToLowerInvariant() switch {
            "background-extraction" or "bge" or "" => GraXpertOperation.BackgroundExtraction,
            "deconvolution" or "decon"             => GraXpertOperation.Deconvolution,
            "denoising" or "denoise"               => GraXpertOperation.Denoising,
            _ => null
        };
    }

    public record GraXpertRunRequest(
        List<string> Paths,
        string? Operation,
        // Common
        int? Concurrency,
        string? AiVersion,
        // BGE
        string? Correction,
        double? Smoothing,
        bool? SaveBackground,
        // Decon
        double? DeconStrength,
        double? DeconPsfSize,
        // Denoise
        double? DenoiseStrength);
}
