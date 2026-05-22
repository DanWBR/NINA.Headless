using NUnit.Framework;
using NINA.Image.ImageAnalysis;

namespace NINA.Headless.Test;

/// <summary>
/// Pins the per-pixel reduction methods used by STUDIO master-frame
/// integration. These are the heart of how darks, biases and flats
/// collapse N raw frames into a single calibration master — a silent
/// regression here corrupts every subsequent calibration run.
/// </summary>
[TestFixture]
public class IntegrationMathTests {

    // --- Mean -----------------------------------------------------

    [Test]
    public void Mean_TypicalValues_ReturnsArithmeticMean() {
        ushort[] vals = [100, 200, 300, 400, 500];
        Assert.That(IntegrationMath.Mean(vals), Is.EqualTo(300));
    }

    [Test]
    public void Mean_RoundsToNearest() {
        ushort[] vals = [10, 11];   // 10.5 -> 11
        Assert.That(IntegrationMath.Mean(vals), Is.EqualTo(11));
    }

    [Test]
    public void Mean_AllMax_DoesntOverflow() {
        var vals = new ushort[1000];
        Array.Fill(vals, (ushort)65535);
        Assert.That(IntegrationMath.Mean(vals), Is.EqualTo(65535));
    }

    [Test]
    public void Mean_Empty_Returns0() {
        Assert.That(IntegrationMath.Mean(Array.Empty<ushort>()), Is.EqualTo(0));
    }

    // --- Median ---------------------------------------------------

    [Test]
    public void Median_OddCount_ReturnsMiddleElement() {
        ushort[] vals = [50, 10, 30, 20, 40];
        Assert.That(IntegrationMath.Median(vals), Is.EqualTo(30));
    }

    [Test]
    public void Median_EvenCount_AveragesTwoMiddle() {
        ushort[] vals = [10, 20, 30, 40];
        Assert.That(IntegrationMath.Median(vals), Is.EqualTo(25));   // (20+30)/2
    }

    [Test]
    public void Median_RejectsOutlier() {
        // Classic dark-frame scenario: one hot pixel among 8 clean reads.
        ushort[] vals = [100, 102, 101, 99, 65535, 100, 98, 103];
        var med = IntegrationMath.Median(vals);
        Assert.That(med, Is.LessThan(500),
            "Median should ignore the single hot-pixel outlier");
    }

    [Test]
    public void Median_DoesntMutateInput() {
        ushort[] vals = [50, 10, 30, 20, 40];
        var copy = (ushort[])vals.Clone();
        _ = IntegrationMath.Median(vals);
        Assert.That(vals, Is.EqualTo(copy));
    }

    // --- SigmaClippedMean ----------------------------------------

    [Test]
    public void SigmaClippedMean_NoOutliers_MatchesPlainMean() {
        ushort[] vals = [100, 101, 99, 100, 102, 98, 100, 101];
        Assert.That(IntegrationMath.SigmaClippedMean(vals),
            Is.EqualTo(IntegrationMath.Mean(vals)).Within(1));
    }

    [Test]
    public void SigmaClippedMean_HotPixelRejected() {
        // 29 clean + 1 hot. With a small N like 10, a single extreme
        // outlier inflates the population σ so much that ±3σ still
        // covers it — basic sigma-clip is known-weak on tiny samples.
        // Real master-dark stacks are 20+ frames; the typical "hot
        // pixel reads 65535 in one sub" pattern is what we need to
        // reject reliably here.
        var vals = new List<ushort>();
        for (int i = 0; i < 29; i++) vals.Add((ushort)(100 + (i % 5)));
        vals.Add(65535);
        var clipped = IntegrationMath.SigmaClippedMean(vals.ToArray());
        Assert.That(clipped, Is.InRange((ushort)95, (ushort)110),
            "Hot pixel should be rejected; mean of clean values is ~102");
    }

    [Test]
    public void SigmaClippedMean_TwoOrFewerValues_FallsBackToMean() {
        // σ is undefined for n < 3; service-level guardrail.
        Assert.That(IntegrationMath.SigmaClippedMean(new ushort[] { 100, 200 }),
            Is.EqualTo(150));
    }

    [Test]
    public void SigmaClippedMean_IdenticalPixels_ReturnsThatValue() {
        var vals = new ushort[20];
        Array.Fill(vals, (ushort)42);
        Assert.That(IntegrationMath.SigmaClippedMean(vals), Is.EqualTo(42));
    }
}
