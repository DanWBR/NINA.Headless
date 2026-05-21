using NINA.Headless.Services.Sequencer;
using NINA.Headless.Services.Sequencer.Containers;
using NINA.Headless.Services.Sequencer.Instructions;

namespace NINA.Headless.Services;

/// <summary>
/// Computes mosaic panel layouts (an N×M grid of pointings around a centre
/// target) and exports the result either as a flat <see cref="MosaicPlan"/>
/// (for the UI to overlay on Aladin) or as a complete Advanced Sequencer
/// <see cref="SequenceDocument"/> the user can run end-to-end.
///
/// The math is intentionally simple — the per-panel FOV is treated as a
/// flat rectangle in equatorial coordinates with a cos(δ) correction on
/// RA. Good enough at typical FoVs (under a few degrees); for very wide
/// surveys near the poles a proper TAN projection is appropriate, which
/// belongs in a separate astrometry pass.
/// </summary>
public class MosaicPlannerService {
    private readonly ILogger<MosaicPlannerService> _logger;

    public MosaicPlannerService(ILogger<MosaicPlannerService> logger) {
        _logger = logger;
    }

    /// <summary>
    /// Compute the panel layout from a request. Returns the centre coords +
    /// per-panel coords + total session-time estimate.
    /// </summary>
    public MosaicPlan Plan(MosaicRequest req) {
        if (req.Cols <= 0 || req.Rows <= 0)
            throw new ArgumentException("Cols/Rows must be positive");
        if (req.PanelFovWidthDeg <= 0 || req.PanelFovHeightDeg <= 0)
            throw new ArgumentException("Per-panel FOV must be positive");

        // Effective step in degrees after overlap. 0.2 overlap → 80% step.
        var overlap = Math.Clamp(req.OverlapPercent, 0, 90) / 100.0;
        var stepW = req.PanelFovWidthDeg * (1 - overlap);
        var stepH = req.PanelFovHeightDeg * (1 - overlap);

        var cosDec = Math.Cos(req.CentreDecDeg * Math.PI / 180);
        if (Math.Abs(cosDec) < 1e-6) cosDec = 1e-6;

        var panels = new List<MosaicPanel>(req.Cols * req.Rows);
        // Build the grid centred on the requested coords. Rows go from top
        // (highest dec) to bottom; cols go either left→right or right→left
        // depending on the row index when serpentine is enabled — minimises
        // slew distance across the whole session.
        for (int r = 0; r < req.Rows; r++) {
            // Row offset in declination (positive = north, top row is north-most)
            var rowOffsetDeg = (req.Rows - 1) / 2.0 * stepH - r * stepH;
            var panelDec = req.CentreDecDeg + rowOffsetDeg;

            bool reversed = req.Serpentine && (r % 2 == 1);
            for (int cIdx = 0; cIdx < req.Cols; cIdx++) {
                int c = reversed ? (req.Cols - 1 - cIdx) : cIdx;
                var colOffsetDeg = c * stepW - (req.Cols - 1) / 2.0 * stepW;
                // RA step needs cos(dec) correction (RA arcs shrink toward the pole)
                var panelRaDeg = req.CentreRaHours * 15 + colOffsetDeg / cosDec;
                if (panelRaDeg < 0) panelRaDeg += 360;
                if (panelRaDeg >= 360) panelRaDeg -= 360;
                panels.Add(new MosaicPanel {
                    Index = panels.Count,
                    Row = r,
                    Col = c,
                    RaHours = panelRaDeg / 15,
                    DecDeg = panelDec,
                    Name = $"{req.TargetName} ({r + 1},{c + 1})"
                });
            }
        }

        // Time estimate: per-panel slew + center + (exposures × count + readout overhead)
        // Defaults err on the optimistic side; users tune via the exposure / count fields.
        var perFrameSeconds = req.ExposureSeconds + req.PerFrameOverheadSeconds;
        var perPanelSeconds = req.SlewOverheadSeconds
                            + req.PlateSolveSeconds
                            + req.ExposureCount * perFrameSeconds;
        var totalSeconds = panels.Count * perPanelSeconds;

        return new MosaicPlan {
            Centre = new MosaicPanel {
                RaHours = req.CentreRaHours, DecDeg = req.CentreDecDeg, Name = req.TargetName
            },
            Cols = req.Cols, Rows = req.Rows,
            PanelFovWidthDeg = req.PanelFovWidthDeg, PanelFovHeightDeg = req.PanelFovHeightDeg,
            OverlapPercent = req.OverlapPercent,
            Serpentine = req.Serpentine,
            Panels = panels,
            EstimatedTotalSeconds = totalSeconds
        };
    }

    /// <summary>
    /// Lower a <see cref="MosaicPlan"/> + per-frame settings into an Advanced
    /// Sequencer document: one DeepSkyObjectContainer per panel inside a
    /// SequentialContainer. Each panel slews + plate-solves, then takes
    /// <paramref name="exposureCount"/> exposures.
    /// </summary>
    public SequenceDocument ToSequenceDocument(MosaicPlan plan,
        double exposureSeconds, int exposureCount,
        string? filterName = null, int? gain = null, int binning = 1) {

        var root = new SequentialContainer { Name = $"Mosaic — {plan.Centre.Name}" };
        foreach (var panel in plan.Panels) {
            var dso = new DeepSkyObjectContainer {
                Name = panel.Name,
                Target = panel.Name,
                RaHours = panel.RaHours,
                DecDeg = panel.DecDeg,
                CenterOnStart = true,
                Items = new() {
                    new TakeExposureInstruction {
                        Name = $"Lights × {exposureCount}",
                        ExposureSeconds = exposureSeconds,
                        Count = exposureCount,
                        Filter = filterName,
                        Gain = gain,
                        Binning = binning,
                        TargetName = panel.Name,
                        ImageType = "LIGHT"
                    }
                }
            };
            root.Items.Add(dso);
        }

        return new SequenceDocument {
            Name = $"Mosaic — {plan.Centre.Name} ({plan.Cols}×{plan.Rows})",
            Description = $"Auto-generated mosaic: {plan.Panels.Count} panels, " +
                          $"{plan.OverlapPercent}% overlap, " +
                          (plan.Serpentine ? "serpentine" : "row-major") + " order",
            Root = root
        };
    }
}

public class MosaicRequest {
    /// <summary>Target display name (used in panel names + sequence title).</summary>
    public string TargetName { get; set; } = "Target";
    public double CentreRaHours { get; set; }
    public double CentreDecDeg { get; set; }

    public int Cols { get; set; } = 2;
    public int Rows { get; set; } = 2;

    /// <summary>Per-panel FOV — typically computed from the rig's sensor + focal length.</summary>
    public double PanelFovWidthDeg { get; set; } = 1.0;
    public double PanelFovHeightDeg { get; set; } = 1.0;

    /// <summary>Overlap between adjacent panels (0..90). 20% is a common safe value.</summary>
    public double OverlapPercent { get; set; } = 20;

    /// <summary>If true, alternating rows reverse the column order to minimise slew distance.</summary>
    public bool Serpentine { get; set; } = true;

    // ---- Time-estimate inputs (used when producing a session estimate) ----
    public double ExposureSeconds { get; set; } = 60;
    public int ExposureCount { get; set; } = 10;
    public int SlewOverheadSeconds { get; set; } = 30;
    public int PlateSolveSeconds { get; set; } = 20;
    public int PerFrameOverheadSeconds { get; set; } = 5;
}

public class MosaicPanel {
    public int Index { get; set; }
    public int Row { get; set; }
    public int Col { get; set; }
    public double RaHours { get; set; }
    public double DecDeg { get; set; }
    public string Name { get; set; } = "";
}

public class MosaicPlan {
    public MosaicPanel Centre { get; set; } = new();
    public int Cols { get; set; }
    public int Rows { get; set; }
    public double PanelFovWidthDeg { get; set; }
    public double PanelFovHeightDeg { get; set; }
    public double OverlapPercent { get; set; }
    public bool Serpentine { get; set; }
    public List<MosaicPanel> Panels { get; set; } = new();
    public double EstimatedTotalSeconds { get; set; }
}
