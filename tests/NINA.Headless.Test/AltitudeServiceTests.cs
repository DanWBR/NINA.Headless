using NUnit.Framework;
using NINA.Headless.Services;

namespace NINA.Headless.Test;

[TestFixture]
public class AltitudeServiceTests {

    // ---- RaDecToAltAz ----

    [Test]
    public void RaDecToAltAz_PolarisFromMidLatitude_HasAltitudeNearLatitude() {
        // Polaris: RA ≈ 2.53h, Dec ≈ +89.26°. From any northern site,
        // altitude of Polaris ≈ site latitude.
        var lat = 45.0;
        var lon = 0.0;
        var utc = new DateTime(2024, 6, 15, 22, 0, 0, DateTimeKind.Utc);
        var (alt, _) = AltitudeService.RaDecToAltAz(2.53, 89.26, utc, lat, lon);
        Assert.That(alt, Is.EqualTo(lat).Within(1.0));
    }

    [Test]
    public void RaDecToAltAz_OnMeridian_AzimuthIsZeroOrOneEighty() {
        // Construct an instant where LST == RA (target on meridian).
        var utc = new DateTime(2024, 3, 21, 18, 0, 0, DateTimeKind.Utc);
        var lon = 0.0;
        var lst = MeridianFlipService.ComputeLstHours(utc, lon);
        var raHours = lst;
        // For a northern site looking south, on-meridian = azimuth 180°.
        // For a Dec north of latitude, target transits north → azimuth 0°.
        var lat = 40.0;
        var dec = 20.0; // south of zenith for lat=40
        var (_, az) = AltitudeService.RaDecToAltAz(raHours, dec, utc, lat, lon);
        Assert.That(az, Is.EqualTo(180).Within(2));
    }

    [Test]
    public void RaDecToAltAz_TargetBelowHorizon_HasNegativeAltitude() {
        var lat = 45.0;
        var lon = 0.0;
        var utc = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        // Target at RA 0h Dec -60° (deep southern) from northern lat 45 → always below horizon
        var (alt, _) = AltitudeService.RaDecToAltAz(0, -60, utc, lat, lon);
        Assert.That(alt, Is.LessThan(0));
    }

    [Test]
    public void RaDecToAltAz_ReturnsAltInValidRange() {
        // Sanity: every test point's altitude in [-90, 90] and az in [0, 360)
        var rng = new Random(1);
        for (int i = 0; i < 30; i++) {
            var ra = rng.NextDouble() * 24;
            var dec = (rng.NextDouble() - 0.5) * 180;
            var lat = (rng.NextDouble() - 0.5) * 180;
            var lon = (rng.NextDouble() - 0.5) * 360;
            var utc = new DateTime(2024, 1, 1).AddHours(rng.NextDouble() * 24 * 365);
            var (alt, az) = AltitudeService.RaDecToAltAz(ra, dec, utc, lat, lon);
            Assert.That(alt, Is.InRange(-90.0, 90.0));
            Assert.That(az, Is.InRange(0.0, 360.0));
        }
    }

    // ---- Sun position (low precision) ----

    [Test]
    public void SunPosition_AtVernalEquinox_RaNearZero() {
        // Vernal equinox ~ Mar 20 03:00 UTC 2024 → sun RA ≈ 0h, Dec ≈ 0°
        var utc = new DateTime(2024, 3, 20, 3, 6, 0, DateTimeKind.Utc);
        var (ra, dec) = AltitudeService.SunPosition(utc);
        Assert.That(ra, Is.EqualTo(0).Within(0.2).Or.EqualTo(24).Within(0.2));
        Assert.That(dec, Is.EqualTo(0).Within(0.5));
    }

    [Test]
    public void SunPosition_AtJuneSolstice_DecNearPlus23() {
        // June solstice ~ Jun 20 2024 → sun dec ≈ +23.4°
        var utc = new DateTime(2024, 6, 20, 20, 51, 0, DateTimeKind.Utc);
        var (_, dec) = AltitudeService.SunPosition(utc);
        Assert.That(dec, Is.EqualTo(23.4).Within(0.5));
    }

    [Test]
    public void SunPosition_AtDecemberSolstice_DecNearMinus23() {
        var utc = new DateTime(2024, 12, 21, 9, 21, 0, DateTimeKind.Utc);
        var (_, dec) = AltitudeService.SunPosition(utc);
        Assert.That(dec, Is.EqualTo(-23.4).Within(0.5));
    }
}
