using NINA.Headless.Services;
using NINA.Headless.Services.Sequencer;

namespace NINA.Headless.Endpoints;

public static class MosaicEndpoints {
    public static void MapMosaicEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/mosaic");

        // Plan only — returns panel coords + time estimate; the UI overlays this on Aladin
        g.MapPost("/plan", (MosaicRequest req, MosaicPlannerService planner) => {
            try {
                var plan = planner.Plan(req);
                return Results.Ok(plan);
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Plan + lower into an Advanced Sequencer document. Caller can either
        // load it into the engine immediately (loadIntoEngine=true) or get the
        // SequenceDocument back for hand-tuning before loading.
        g.MapPost("/to-sequence", (MosaicToSequenceRequest req,
            MosaicPlannerService planner, AdvancedSequenceEngine engine) => {
            try {
                var plan = planner.Plan(req.Mosaic);
                var doc = planner.ToSequenceDocument(plan,
                    req.ExposureSeconds, req.ExposureCount,
                    req.FilterName, req.Gain, req.Binning);
                if (req.LoadIntoEngine) engine.Load(doc);
                return Results.Ok(new {
                    plan,
                    document = doc,
                    loadedIntoEngine = req.LoadIntoEngine,
                    validation = req.LoadIntoEngine ? engine.Validate() : null
                });
            } catch (Exception ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    public record MosaicToSequenceRequest(
        MosaicRequest Mosaic,
        double ExposureSeconds,
        int ExposureCount,
        string? FilterName,
        int? Gain,
        int Binning,
        bool LoadIntoEngine);
}
