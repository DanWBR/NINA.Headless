using NUnit.Framework;
using NINA.Image.Editor;

namespace NINA.Polaris.Test.Editor;

/// <summary>
/// Pins the EditAutoTuner heuristic so a future "let me just tweak this
/// constant" doesn't silently regress the Auto button. Each test builds a
/// synthetic byte[] with a known histogram shape and asserts the sliders
/// land in the expected direction (sign + rough magnitude).
/// </summary>
[TestFixture]
public class EditAutoTunerTests {

    private const int W = 64;
    private const int H = 64;

    [Test]
    public void EmptyBuffer_ReturnsDefaults() {
        var s = EditAutoTuner.Compute(new byte[0], W, H, 1);
        Assert.That(s.Light.IsDefault, Is.True);
        Assert.That(s.Color, Is.Null);
    }

    [Test]
    public void Mono_ReturnsNullColor() {
        var s = EditAutoTuner.Compute(Solid(128, 1), W, H, 1);
        Assert.That(s.Color, Is.Null);
    }

    [Test]
    public void Rgb_ReturnsColor() {
        var s = EditAutoTuner.Compute(Solid(128, 3), W, H, 3);
        Assert.That(s.Color, Is.Not.Null);
    }

    [Test]
    public void NearTargetMid_LeavesExposureFlat() {
        // 0.18 in 0..1 -> ~46 in 0..255. Solid frame at the zone V target.
        var s = EditAutoTuner.Compute(Solid(46, 1), W, H, 1);
        Assert.That(s.Light.Exposure, Is.EqualTo(0).Within(0.1),
            "Frame already at target mid should not need exposure boost");
    }

    [Test]
    public void DarkFrame_BoostsExposureAndLiftsShadows() {
        // Solid frame at value 10 -> all percentiles at 10/255 ≈ 0.039,
        // safely below ShadowLiftThreshold (0.05) and well under TargetMid
        // (0.18). Should fire both exposure (positive) and shadows.
        var s = EditAutoTuner.Compute(Solid(10, 1), W, H, 1);
        Assert.That(s.Light.Exposure, Is.GreaterThan(0.5),
            "Dark frame should get a positive exposure nudge");
        Assert.That(s.Light.Shadows, Is.GreaterThan(0),
            "Dark frame with crushed p5 should lift Shadows");
    }

    [Test]
    public void BrightFrame_PullsHighlightsDown() {
        // Solid value 250 -> p99.5 sits well above the highlight clip
        // threshold (0.97). Highlights should go negative.
        var s = EditAutoTuner.Compute(Solid(250, 1), W, H, 1);
        Assert.That(s.Light.Highlights, Is.LessThan(0),
            "Blown highlights should trigger a Highlights cut");
    }

    [Test]
    public void DimFrame_DragsWhitesUp() {
        // Solid value 180 -> p99.5 ≈ 0.706, well below white-headroom
        // threshold (0.95). Whites should be positive to stretch toward white.
        var s = EditAutoTuner.Compute(Solid(180, 1), W, H, 1);
        Assert.That(s.Light.Whites, Is.GreaterThan(0),
            "Unused highlight headroom should drag Whites up");
    }

    [Test]
    public void CrushedShadows_DragBlacksDown() {
        // Mix of pixels: 5% sit at value 30 (>0.02 == BlackClipThreshold) and
        // the rest at value 200. p0.5 will land at 30/255 ≈ 0.117, well
        // above the threshold, so Blacks should go negative.
        var data = new byte[W * H];
        int n = data.Length;
        for (int i = 0; i < n; i++) data[i] = i < n / 10 ? (byte)30 : (byte)200;
        var s = EditAutoTuner.Compute(data, W, H, 1);
        Assert.That(s.Light.Blacks, Is.LessThan(0),
            "Pixels stuck above pure black should pull Blacks negative");
    }

    [Test]
    public void RgbAtMid_AppliesVibranceBias() {
        // BGR interleaved at value 46 each = lum ≈ 0.18. Sliders should be
        // mostly flat for Light but Vibrance should still get its bias bump.
        var s = EditAutoTuner.Compute(Solid(46, 3), W, H, 3);
        Assert.That(s.Color, Is.Not.Null);
        Assert.That(s.Color!.Vibrance, Is.GreaterThan(0),
            "RGB sources should always receive the vibrance bias");
        Assert.That(s.Color.Saturation, Is.EqualTo(0).Within(1e-6),
            "Plain Saturation stays at zero by design (vibrance covers it)");
    }

    [Test]
    public void WideContrastFrame_SkipsContrastBias() {
        // 50% pixels black, 50% pixels white => p005 ≈ 0 and p995 ≈ 1, so
        // the histogram already spans the full range. Auto-contrast bump
        // should self-suppress to avoid double-cooking the image.
        var data = new byte[W * H];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i < data.Length / 2 ? 0 : 255);
        var s = EditAutoTuner.Compute(data, W, H, 1);
        Assert.That(s.Light.Contrast, Is.EqualTo(0).Within(1e-6),
            "Already-wide histogram should skip the contrast bias");
    }

    // ---- helpers --------------------------------------------------------

    private static byte[] Solid(byte value, int channels) {
        var buf = new byte[W * H * channels];
        for (int i = 0; i < buf.Length; i++) buf[i] = value;
        return buf;
    }
}
