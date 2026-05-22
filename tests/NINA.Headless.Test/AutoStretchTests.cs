using NUnit.Framework;
using NINA.Image.ImageAnalysis;

namespace NINA.Headless.Test;

/// <summary>
/// Pins the contract of the AutoStretch overloads the STUDIO viewer's
/// stretch sliders depend on. ApplyManual is the hot path on every
/// slider drag — these guard the LUT math against regressions that
/// would silently break preview rendering.
/// </summary>
[TestFixture]
public class AutoStretchTests {

    [Test]
    public void ApplyManual_BlackAndWhiteSameAsAuto_ProducesIdenticalOutput() {
        var data = SyntheticBackground(width: 64, height: 64, level: 5000);
        var auto = AutoStretch.Apply(data, 64, 64);
        var p = AutoStretch.ComputeAutoStretchParams(data, 64, 64);
        var manual = AutoStretch.ApplyManual(data, 64, 64, p.Black, p.Mid, p.White);
        Assert.That(manual, Is.EqualTo(auto));
    }

    [Test]
    public void ApplyManual_FullRange_ClampsCorrectly() {
        var data = new ushort[] { 0, 16384, 32768, 49152, 65535 };
        var result = AutoStretch.ApplyManual(data, 5, 1, 0.0, 0.5, 1.0);
        Assert.That(result[0], Is.EqualTo(0),   "min input maps to 0");
        Assert.That(result[4], Is.EqualTo(255), "max input maps to 255");
    }

    [Test]
    public void ApplyManual_NarrowWindow_StretchesContrast() {
        // Pixels at 0.3 and 0.7 normalised, narrow window [0.25, 0.75]
        var data = new ushort[] { (ushort)(0.3 * 65535), (ushort)(0.7 * 65535) };
        var stretched = AutoStretch.ApplyManual(data, 2, 1, 0.25, 0.5, 0.75);
        // Linear midpoint (mid=0.5) inside the [0.25..0.75] window
        // maps 0.3 -> 0.1, 0.7 -> 0.9 of output range
        Assert.That(stretched[0], Is.LessThan(50));
        Assert.That(stretched[1], Is.GreaterThan(205));
    }

    [Test]
    public void ApplyManual_WhiteBelowBlack_DoesntDivideByZero() {
        // Caller passing an inverted window shouldn't crash.
        var data = new ushort[] { 30000 };
        Assert.DoesNotThrow(() => AutoStretch.ApplyManual(data, 1, 1, 0.8, 0.5, 0.2));
    }

    [Test]
    public void ApplyManual_MidtoneShiftDarkens() {
        // Same window, lower midtone = more shadow stretch = brighter mid pixels.
        var data = new ushort[] { 32768 }; // 0.5 normalised
        var low  = AutoStretch.ApplyManual(data, 1, 1, 0.0, 0.1, 1.0);
        var high = AutoStretch.ApplyManual(data, 1, 1, 0.0, 0.9, 1.0);
        Assert.That(low[0], Is.GreaterThan(high[0]),
            "Low midtone should brighten mid-grey pixels");
    }

    [Test]
    public void ComputeAutoStretchParams_BackgroundFrame_ReturnsLowBlack() {
        // A background-dominated frame should produce a black point above
        // 0 (so noise is clipped) but well below 1.
        var data = SyntheticBackground(64, 64, level: 5000);
        var p = AutoStretch.ComputeAutoStretchParams(data, 64, 64);
        Assert.That(p.Black, Is.GreaterThan(0).And.LessThan(0.5));
        Assert.That(p.Mid,   Is.GreaterThan(0).And.LessThan(0.5));
        Assert.That(p.White, Is.EqualTo(1.0));
    }

    private static ushort[] SyntheticBackground(int width, int height, int level) {
        var arr = new ushort[width * height];
        var rnd = new Random(42);
        for (int i = 0; i < arr.Length; i++) {
            // background level + small Gaussian-ish noise
            arr[i] = (ushort)Math.Clamp(level + rnd.Next(-300, 300), 0, 65535);
        }
        return arr;
    }
}
