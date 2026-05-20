using NUnit.Framework;
using NINA.Headless.Services;

namespace NINA.Headless.Test;

[TestFixture]
public class MeridianFlipServiceTests {

    // ---- GMST / LST math ----

    [Test]
    public void ComputeGmstHours_AtJ2000_MatchesReference() {
        // J2000.0 epoch = 2000-01-01 12:00:00 UTC
        // Reference GMST at J2000 ≈ 18.697374558 hours (USNO Astronomical Almanac)
        var j2000 = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var gmst = MeridianFlipService.ComputeGmstHours(j2000);

        Assert.That(gmst, Is.EqualTo(18.6974).Within(0.001));
    }

    [Test]
    public void ComputeGmstHours_IsInRange0To24() {
        var random = new Random(42);
        for (int i = 0; i < 20; i++) {
            // Random instant within ±20 years of J2000
            var dt = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)
                .AddDays(random.NextDouble() * 14600 - 7300);

            var gmst = MeridianFlipService.ComputeGmstHours(dt);

            Assert.That(gmst, Is.GreaterThanOrEqualTo(0).And.LessThan(24),
                $"GMST out of range for {dt:o}");
        }
    }

    [Test]
    public void ComputeLstHours_AtGreenwich_EqualsGmst() {
        var utc = new DateTime(2024, 6, 15, 22, 30, 0, DateTimeKind.Utc);
        var gmst = MeridianFlipService.ComputeGmstHours(utc);
        var lst = MeridianFlipService.ComputeLstHours(utc, 0);
        Assert.That(lst, Is.EqualTo(gmst).Within(0.0001));
    }

    [Test]
    public void ComputeLstHours_AtPositiveLongitude_IsAheadOfGmst() {
        var utc = new DateTime(2024, 6, 15, 22, 30, 0, DateTimeKind.Utc);
        var gmst = MeridianFlipService.ComputeGmstHours(utc);

        // +60° east → +4 hours
        var lst = MeridianFlipService.ComputeLstHours(utc, 60);

        var expected = (gmst + 4 + 24) % 24;
        Assert.That(lst, Is.EqualTo(expected).Within(0.001));
    }

    [Test]
    public void ComputeLstHours_AtNegativeLongitude_IsBehindGmst() {
        var utc = new DateTime(2024, 6, 15, 22, 30, 0, DateTimeKind.Utc);
        var gmst = MeridianFlipService.ComputeGmstHours(utc);

        // -75° west → -5 hours
        var lst = MeridianFlipService.ComputeLstHours(utc, -75);

        var expected = (gmst - 5 + 48) % 24;
        Assert.That(lst, Is.EqualTo(expected).Within(0.001));
    }

    // ---- HoursUntilMeridian ----

    [Test]
    public void HoursUntilMeridian_TargetOnMeridian_IsApproximatelyZero() {
        var utc = new DateTime(2024, 6, 15, 22, 30, 0, DateTimeKind.Utc);
        var lst = MeridianFlipService.ComputeLstHours(utc, 0);

        // Set target RA = LST  →  on meridian right now
        var hours = MeridianFlipService.HoursUntilMeridian(lst, utc, 0);

        // Target is exactly on the meridian → either 0 (rising case clamps to 0) or 24 (just past).
        Assert.That(hours, Is.EqualTo(0).Within(0.001).Or.EqualTo(24).Within(0.001));
    }

    [Test]
    public void HoursUntilMeridian_TargetEastByThreeHours_ReturnsThreeHours() {
        var utc = new DateTime(2024, 6, 15, 22, 30, 0, DateTimeKind.Utc);
        var lst = MeridianFlipService.ComputeLstHours(utc, 0);

        // Target RA = LST + 3 → target is 3h east of meridian, will transit in 3h
        var targetRa = (lst + 3) % 24;
        var hours = MeridianFlipService.HoursUntilMeridian(targetRa, utc, 0);

        Assert.That(hours, Is.EqualTo(3).Within(0.01));
    }

    [Test]
    public void HoursUntilMeridian_TargetWestByTwoHours_ReturnsTwentyTwoHours() {
        var utc = new DateTime(2024, 6, 15, 22, 30, 0, DateTimeKind.Utc);
        var lst = MeridianFlipService.ComputeLstHours(utc, 0);

        // Target RA = LST - 2 → target already 2h past meridian
        var targetRa = (lst - 2 + 24) % 24;
        var hours = MeridianFlipService.HoursUntilMeridian(targetRa, utc, 0);

        // 24 - HA where HA = 2 → 22 hours until next transit
        Assert.That(hours, Is.EqualTo(22).Within(0.01));
    }

    [Test]
    public void HoursUntilMeridian_AlwaysInZeroTo24Range() {
        var utc = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        for (double ra = 0; ra < 24; ra += 0.5) {
            var hours = MeridianFlipService.HoursUntilMeridian(ra, utc, 0);
            Assert.That(hours, Is.GreaterThanOrEqualTo(0).And.LessThanOrEqualTo(24),
                $"Out of range for RA={ra}");
        }
    }

    // ---- Settings ----

    [Test]
    public void Settings_Defaults_AreSensible() {
        var s = new MeridianFlipSettings();
        Assert.That(s.Enabled, Is.False);
        Assert.That(s.MinutesAfterMeridian, Is.EqualTo(5));
        Assert.That(s.RecenterAfterFlip, Is.True);
        Assert.That(s.RecenterToleranceArcsec, Is.EqualTo(30));
        Assert.That(s.SettleSecondsAfterFlip, Is.EqualTo(5));
        Assert.That(s.AutoFocusAfterFlip, Is.False);
    }
}
