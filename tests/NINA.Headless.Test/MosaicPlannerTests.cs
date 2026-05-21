using Microsoft.Extensions.Logging.Abstractions;
using NINA.Headless.Services;
using NINA.Headless.Services.Sequencer.Containers;
using NINA.Headless.Services.Sequencer.Instructions;
using NUnit.Framework;

namespace NINA.Headless.Test;

[TestFixture]
public class MosaicPlannerTests {
    private MosaicPlannerService _planner = null!;

    [SetUp]
    public void SetUp() {
        _planner = new MosaicPlannerService(NullLogger<MosaicPlannerService>.Instance);
    }

    [Test]
    public void Plan_2x2_Has4Panels() {
        var plan = _planner.Plan(new MosaicRequest {
            TargetName = "T", CentreRaHours = 12, CentreDecDeg = 0,
            Cols = 2, Rows = 2,
            PanelFovWidthDeg = 1.0, PanelFovHeightDeg = 1.0,
            OverlapPercent = 0, Serpentine = false
        });
        Assert.That(plan.Panels.Count, Is.EqualTo(4));
        Assert.That(plan.Centre.RaHours, Is.EqualTo(12));
    }

    [Test]
    public void Plan_NoOverlap_PanelsExactlyFovApart() {
        var plan = _planner.Plan(new MosaicRequest {
            CentreRaHours = 12, CentreDecDeg = 0,
            Cols = 3, Rows = 1,
            PanelFovWidthDeg = 1.0, PanelFovHeightDeg = 1.0,
            OverlapPercent = 0, Serpentine = false
        });
        // 3 panels in one row, no overlap, 1° FOV → step 1°
        // Dec = 0 → cos = 1, so RA hours difference = 1° / 15 = 0.0667
        var ra = plan.Panels.Select(p => p.RaHours).OrderBy(r => r).ToArray();
        Assert.That(ra[1] - ra[0], Is.EqualTo(1.0 / 15).Within(1e-9));
        Assert.That(ra[2] - ra[1], Is.EqualTo(1.0 / 15).Within(1e-9));
    }

    [Test]
    public void Plan_HighDec_AppliesCosineCorrection() {
        var plan = _planner.Plan(new MosaicRequest {
            CentreRaHours = 12, CentreDecDeg = 60,    // cos 60° = 0.5
            Cols = 2, Rows = 1,
            PanelFovWidthDeg = 1.0, PanelFovHeightDeg = 1.0,
            OverlapPercent = 0, Serpentine = false
        });
        var ra = plan.Panels.Select(p => p.RaHours).OrderBy(r => r).ToArray();
        // At dec=60°, RA hours step doubles relative to dec=0°
        Assert.That(ra[1] - ra[0], Is.EqualTo(2.0 / 15).Within(1e-9));
    }

    [Test]
    public void Plan_Serpentine_ReversesEvenRows() {
        var plan = _planner.Plan(new MosaicRequest {
            CentreRaHours = 12, CentreDecDeg = 0,
            Cols = 3, Rows = 2,
            PanelFovWidthDeg = 1.0, PanelFovHeightDeg = 1.0,
            OverlapPercent = 0, Serpentine = true
        });
        // Row 0: col 0, 1, 2; row 1: col 2, 1, 0
        Assert.That(plan.Panels[0].Col, Is.EqualTo(0));
        Assert.That(plan.Panels[1].Col, Is.EqualTo(1));
        Assert.That(plan.Panels[2].Col, Is.EqualTo(2));
        Assert.That(plan.Panels[3].Col, Is.EqualTo(2));
        Assert.That(plan.Panels[4].Col, Is.EqualTo(1));
        Assert.That(plan.Panels[5].Col, Is.EqualTo(0));
    }

    [Test]
    public void Plan_OverlapShrinksStep() {
        var noOverlap = _planner.Plan(new MosaicRequest {
            CentreRaHours = 12, CentreDecDeg = 0,
            Cols = 2, Rows = 1, PanelFovWidthDeg = 1, PanelFovHeightDeg = 1,
            OverlapPercent = 0, Serpentine = false
        });
        var with50 = _planner.Plan(new MosaicRequest {
            CentreRaHours = 12, CentreDecDeg = 0,
            Cols = 2, Rows = 1, PanelFovWidthDeg = 1, PanelFovHeightDeg = 1,
            OverlapPercent = 50, Serpentine = false
        });
        var stepNo = Math.Abs(noOverlap.Panels[1].RaHours - noOverlap.Panels[0].RaHours);
        var step50 = Math.Abs(with50.Panels[1].RaHours - with50.Panels[0].RaHours);
        Assert.That(step50, Is.EqualTo(stepNo / 2).Within(1e-9));
    }

    [Test]
    public void Plan_TimeEstimate_ScalesWithPanelCount() {
        var req = new MosaicRequest {
            CentreRaHours = 12, CentreDecDeg = 0,
            PanelFovWidthDeg = 1, PanelFovHeightDeg = 1,
            ExposureSeconds = 60, ExposureCount = 10,
            SlewOverheadSeconds = 30, PlateSolveSeconds = 20, PerFrameOverheadSeconds = 5
        };
        req.Cols = 1; req.Rows = 1;
        var p1 = _planner.Plan(req);
        req.Cols = 2; req.Rows = 2;
        var p4 = _planner.Plan(req);
        Assert.That(p4.EstimatedTotalSeconds, Is.EqualTo(p1.EstimatedTotalSeconds * 4).Within(1e-6));
    }

    [Test]
    public void ToSequenceDocument_GeneratesOneDsoPerPanel() {
        var plan = _planner.Plan(new MosaicRequest {
            TargetName = "M31",
            CentreRaHours = 0.7, CentreDecDeg = 41,
            Cols = 2, Rows = 2, PanelFovWidthDeg = 1, PanelFovHeightDeg = 1
        });
        var doc = _planner.ToSequenceDocument(plan, exposureSeconds: 60, exposureCount: 5,
            filterName: "L", gain: 100, binning: 1);

        var root = doc.Root as SequentialContainer;
        Assert.That(root, Is.Not.Null);
        Assert.That(root!.Items.Count, Is.EqualTo(4));
        foreach (var item in root.Items) {
            var dso = item as DeepSkyObjectContainer;
            Assert.That(dso, Is.Not.Null);
            Assert.That(dso!.Items.Count, Is.EqualTo(1));
            var take = dso.Items[0] as TakeExposureInstruction;
            Assert.That(take, Is.Not.Null);
            Assert.That(take!.Count, Is.EqualTo(5));
            Assert.That(take.ExposureSeconds, Is.EqualTo(60));
            Assert.That(take.Filter, Is.EqualTo("L"));
        }
    }

    [Test]
    public void Plan_InvalidInput_Throws() {
        Assert.Throws<ArgumentException>(() => _planner.Plan(new MosaicRequest { Cols = 0, Rows = 1 }));
        Assert.Throws<ArgumentException>(() => _planner.Plan(new MosaicRequest { Cols = 1, Rows = 1, PanelFovWidthDeg = -1 }));
    }
}
