using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// REFSUG-1: trend-based refocus suggestion. Tests inject synthetic
/// HFR streams via <see cref="RefocusSuggestionService.InjectFrameForTest"/>
/// so the math is exercised without a live INDI camera or real star
/// detection. The same code path runs in production via the
/// LiveStackingService FrameIntegrated subscription.
/// </summary>
[TestFixture]
public class RefocusSuggestionServiceTests {

    // ---- Pure-helper tests (no DI, fast) ----

    [Test]
    public void LinearSlope_RisingHfr_PositiveSlope() {
        // HFR rising linearly: 2.0, 2.1, 2.2, 2.3, 2.4 → slope = 0.1
        var samples = new List<HfrSample>();
        for (int i = 0; i < 5; i++)
            samples.Add(new HfrSample(i + 1, 2.0 + 0.1 * i, 50, DateTime.UtcNow));
        Assert.That(RefocusSuggestionService.LinearSlope(samples, 5), Is.EqualTo(0.1).Within(1e-6));
    }

    [Test]
    public void LinearSlope_FlatHfr_ZeroSlope() {
        var samples = Enumerable.Range(1, 5)
            .Select(i => new HfrSample(i, 2.0, 50, DateTime.UtcNow)).ToList();
        Assert.That(RefocusSuggestionService.LinearSlope(samples, 5), Is.EqualTo(0).Within(1e-6));
    }

    [Test]
    public void LinearSlope_FallingHfr_NegativeSlope() {
        var samples = new List<HfrSample>();
        for (int i = 0; i < 5; i++)
            samples.Add(new HfrSample(i + 1, 3.0 - 0.2 * i, 50, DateTime.UtcNow));
        Assert.That(RefocusSuggestionService.LinearSlope(samples, 5), Is.EqualTo(-0.2).Within(1e-6));
    }

    [Test]
    public void PercentileHfr_5thPercentile_PicksNearMinimum() {
        // 20 samples with one outlier-low at 1.5, rest 2.0-2.5. 5th
        // percentile (idx 0 for 20 samples: ceil(0.05*20)-1 = 0) =
        // the minimum value.
        var samples = new List<HfrSample> { new(1, 1.5, 50, DateTime.UtcNow) };
        for (int i = 0; i < 19; i++)
            samples.Add(new HfrSample(i + 2, 2.0 + 0.025 * i, 50, DateTime.UtcNow));
        var p5 = RefocusSuggestionService.PercentileHfr(samples, 5, 20);
        Assert.That(p5, Is.EqualTo(1.5).Within(1e-6),
            "5th percentile of 20 samples picks the lowest entry");
    }

    [Test]
    public void RollingMean_LastN_AveragesCorrectly() {
        var samples = Enumerable.Range(1, 10)
            .Select(i => new HfrSample(i, (double)i, 50, DateTime.UtcNow))
            .ToList();
        // Last 5 = {6,7,8,9,10} mean = 8
        Assert.That(RefocusSuggestionService.RollingMean(samples, 5), Is.EqualTo(8).Within(1e-9));
    }

    // ---- End-to-end behaviour via InjectFrameForTest ----

    [Test]
    public void Warmup_BelowThreshold_NoSuggestion() {
        var svc = MakeService(out _);
        // Inject 10 stable samples (less than the 15-sample warmup
        // threshold). No suggestion should fire even if HFR drifts.
        var t = DateTime.UtcNow;
        for (int i = 1; i <= 10; i++) {
            svc.InjectFrameForTest(i, 5.0, 50, t.AddSeconds(i));
        }
        Assert.That(svc.CurrentStatus.Suggesting, Is.False,
            "Should not fire before the 15-sample warmup completes");
        Assert.That(svc.CurrentStatus.SampleCount, Is.EqualTo(10));
    }

    [Test]
    public void RefocusEnabled_SkipsEverything() {
        var svc = MakeService(out var profiles);
        // Turn on the auto-refocus path (LSTR-3). The suggestion service
        // must NOT duplicate the advisory when LSTR-3 will auto-fire.
        profiles.UpdateEquipmentProfile(profiles.ActiveEquipmentProfile.Id,
            r => r.LiveStackTriggers.RefocusEnabled = true);
        var t = DateTime.UtcNow;
        // Inject a clearly bad stream that would otherwise fire.
        for (int i = 1; i <= 30; i++) {
            double hfr = 2.0 + 0.2 * i;   // strongly rising
            svc.InjectFrameForTest(i, hfr, 50, t.AddSeconds(i));
        }
        Assert.That(svc.CurrentStatus.Suggesting, Is.False,
            "RefocusEnabled = true must short-circuit the suggestion path");
    }

    [Test]
    public void StableHfr_NoSuggestion() {
        var svc = MakeService(out _);
        var t = DateTime.UtcNow;
        // 25 samples around 2.5 with tiny jitter (seeing-like). Slope
        // close to zero, magnitude close to baseline. Must not fire.
        var rnd = new Random(42);
        for (int i = 1; i <= 25; i++) {
            var jitter = (rnd.NextDouble() - 0.5) * 0.05;
            svc.InjectFrameForTest(i, 2.5 + jitter, 50, t.AddSeconds(i));
        }
        Assert.That(svc.CurrentStatus.Suggesting, Is.False,
            "Stable HFR with seeing-jitter must not trigger the suggestion");
    }

    [Test]
    public void RisingHfr_AboveBaseline_FiresSuggestion() {
        var svc = MakeService(out _);
        var t = DateTime.UtcNow;
        // First 20 samples: stable HFR around 2.0 (establishes baseline)
        for (int i = 1; i <= 20; i++) {
            svc.InjectFrameForTest(i, 2.0, 50, t.AddSeconds(i));
        }
        Assert.That(svc.CurrentStatus.Suggesting, Is.False,
            "Stable warmup must not fire");

        // Next 10 samples: clearly rising HFR, from 2.0 to 4.0. Slope
        // ~0.2/frame, rolling mean ~3.5 > 2.0*1.15 = 2.3, extrapolated
        // slope*5 = 1.0 > 0.3*2.0 = 0.6 → fires.
        for (int i = 21; i <= 30; i++) {
            double hfr = 2.0 + 0.2 * (i - 20);
            svc.InjectFrameForTest(i, hfr, 50, t.AddSeconds(i));
        }
        Assert.That(svc.CurrentStatus.Suggesting, Is.True,
            "Rising HFR meeting all 3 trigger conditions must fire");
        Assert.That(svc.CurrentStatus.Reason, Does.Contain("HFR rising"));
        Assert.That(svc.CurrentStatus.SuggestedAt, Is.Not.Null);
    }

    [Test]
    public void StarCountCrash_FiresSuggestion() {
        var svc = MakeService(out _);
        var t = DateTime.UtcNow;
        // First 20 stable samples with 100 stars
        for (int i = 1; i <= 20; i++) {
            svc.InjectFrameForTest(i, 2.0, 100, t.AddSeconds(i));
        }
        Assert.That(svc.CurrentStatus.Suggesting, Is.False);

        // Next samples: HFR stays stable but star count crashes to 30
        // (70% drop, well past the 30% threshold). Need enough frames
        // for the rolling-mean window (5) to fill with low-star samples.
        for (int i = 21; i <= 30; i++) {
            svc.InjectFrameForTest(i, 2.0, 30, t.AddSeconds(i));
        }
        var st = svc.CurrentStatus;
        Assert.That(st.Suggesting, Is.True,
            $"Star count crash should fire even with stable HFR. "
            + $"baselineStars={st.BaselineStarCount}, samples={st.SampleCount}");
        Assert.That(st.Reason, Does.Contain("Star count").Or.Contain("star count"));
    }

    [Test]
    public void AutoDismiss_WhenHfrRecovers() {
        var svc = MakeService(out _);
        var t = DateTime.UtcNow;
        // Drive the service into a suggesting state with rising HFR
        for (int i = 1; i <= 20; i++) {
            svc.InjectFrameForTest(i, 2.0, 50, t.AddSeconds(i));
        }
        for (int i = 21; i <= 30; i++) {
            svc.InjectFrameForTest(i, 2.0 + 0.2 * (i - 20), 50, t.AddSeconds(i));
        }
        Assert.That(svc.CurrentStatus.Suggesting, Is.True);

        // Now HFR recovers (user refocused manually). Feed enough
        // samples for the rolling mean (last 5) to flush the old
        // high-HFR samples AND for the consecutive-recovered counter
        // to reach 3. Need at least 5 (to flush the window) + 3
        // (consecutive recovered) = 8 frames.
        for (int i = 31; i <= 45; i++) {
            svc.InjectFrameForTest(i, 2.0, 50, t.AddSeconds(i));
        }
        Assert.That(svc.CurrentStatus.Suggesting, Is.False,
            "Suggestion should auto-clear once HFR recovers to within 5% of baseline");
    }

    [Test]
    public void ManualDismiss_WithBaselineReset_ClearsSuggestion() {
        var svc = MakeService(out _);
        var t = DateTime.UtcNow;
        for (int i = 1; i <= 20; i++) {
            svc.InjectFrameForTest(i, 2.0, 50, t.AddSeconds(i));
        }
        for (int i = 21; i <= 30; i++) {
            svc.InjectFrameForTest(i, 2.0 + 0.2 * (i - 20), 50, t.AddSeconds(i));
        }
        Assert.That(svc.CurrentStatus.Suggesting, Is.True);
        var oldBaseline = svc.CurrentStatus.BaselineHfr;

        svc.Dismiss(resetBaseline: true);
        Assert.That(svc.CurrentStatus.Suggesting, Is.False);
        // Baseline should be replaced by the (recent rolling) elevated HFR
        Assert.That(svc.CurrentStatus.BaselineHfr, Is.Not.EqualTo(oldBaseline),
            "resetBaseline=true should replace the baseline with the post-refocus HFR");
    }

    [Test]
    public void Reset_ClearsAllState() {
        var svc = MakeService(out _);
        var t = DateTime.UtcNow;
        for (int i = 1; i <= 20; i++) {
            svc.InjectFrameForTest(i, 2.0, 50, t.AddSeconds(i));
        }
        svc.Reset();
        var st = svc.CurrentStatus;
        Assert.That(st.Suggesting, Is.False);
        Assert.That(st.BaselineHfr, Is.Null);
        Assert.That(st.SampleCount, Is.EqualTo(0));
    }

    [Test]
    public void GarbageSamples_AreDropped() {
        var svc = MakeService(out _);
        var t = DateTime.UtcNow;
        // Feed 20 garbage samples (zero HFR, low star count). None
        // should land in the rolling window.
        for (int i = 1; i <= 20; i++) {
            svc.InjectFrameForTest(i, 0.0, 0, t.AddSeconds(i));
        }
        Assert.That(svc.CurrentStatus.SampleCount, Is.EqualTo(0),
            "Samples with HFR=0 or low star count are dropped from the window");
    }

    [Test]
    public void FirstFrame_ResetsState() {
        var svc = MakeService(out _);
        var t = DateTime.UtcNow;
        // Pre-load some samples
        for (int i = 2; i <= 10; i++) {
            svc.InjectFrameForTest(i, 2.0, 50, t.AddSeconds(i));
        }
        Assert.That(svc.CurrentStatus.SampleCount, Is.EqualTo(9));
        // Frame 1 marks a fresh live-stack session and clears state
        svc.InjectFrameForTest(1, 2.0, 50, t);
        Assert.That(svc.CurrentStatus.SampleCount, Is.EqualTo(1),
            "Frame 1 should reset state, then buffer the new sample");
    }

    // ---- Test fixture helpers ----

    private static RefocusSuggestionService MakeService(out ProfileService profiles) {
        var tempDir = Path.Combine(Path.GetTempPath(), "polaris-refsug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Profiles:Directory"] = tempDir
            })
            .Build();
        var relay = new ImageRelayService(NullLogger<ImageRelayService>.Instance);
        var stack = new LiveStackingService(relay, NullLogger<LiveStackingService>.Instance);
        profiles = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        var notifications = new NotificationService();
        return new RefocusSuggestionService(stack, profiles, notifications,
            NullLogger<RefocusSuggestionService>.Instance);
    }
}
