using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// Math tests for the TPPA polar-axis fit (PolarAlignmentMath).
///
/// Strategy: synthesise 3 points that a known mount geometry WOULD
/// produce (perfect or with a deliberate offset), feed them through
/// ComputeError, and confirm the recovered errors match the input
/// within a sane tolerance.
///
/// For "perfect mount" tests we generate the points by rotating an
/// initial sky direction about the TRUE polar axis (in the topocentric
/// frame) by 30° increments. Since the rotation axis IS the celestial
/// pole, the recovered error should be ~0 in both axes.
///
/// For "known error" tests we rotate about a SHIFTED polar axis
/// (offset in alt + az by a known amount), then confirm the recovered
/// error matches that shift.
/// </summary>
[TestFixture]
public class PolarAlignmentMathTests {

    // Use a fixed reference epoch so LST is deterministic across runs.
    static readonly DateTime T0 = new(2026, 5, 23, 22, 0, 0, DateTimeKind.Utc);

    [Test]
    public void ComputeError_PerfectMountNorthernHemisphere_ResidualUnderTenArcsec() {
        // Mossoró-ish latitude flipped to north for this test (+45°).
        const double lat = 45.0;
        const double lon = -37.36;

        var pts = SynthesizePoints(
            startRaHours: 6.0, startDecDeg: 60.0,
            slewStepDeg: 30,
            latDeg: lat, longDeg: lon,
            // perfect mount → mount axis = true pole
            mountAzOffsetDeg: 0, mountAltOffsetDeg: 0);

        var (azErr, altErr) = PolarAlignmentMath.ComputeError(
            pts[0], pts[1], pts[2], lat, lon);

        Assert.That(Math.Abs(azErr), Is.LessThan(10.0),
            $"Az error {azErr:F1}\" should be near zero for a perfect mount");
        Assert.That(Math.Abs(altErr), Is.LessThan(10.0),
            $"Alt error {altErr:F1}\" should be near zero for a perfect mount");
    }

    [Test]
    public void ComputeError_PerfectMountSouthernHemisphere_ResidualUnderTenArcsec() {
        // Brazil — Mossoró-ish.
        const double lat = -5.18;
        const double lon = -37.36;

        var pts = SynthesizePoints(
            startRaHours: 14.0, startDecDeg: -60.0,
            slewStepDeg: 30,
            latDeg: lat, longDeg: lon,
            mountAzOffsetDeg: 0, mountAltOffsetDeg: 0);

        var (azErr, altErr) = PolarAlignmentMath.ComputeError(
            pts[0], pts[1], pts[2], lat, lon);

        Assert.That(Math.Abs(azErr), Is.LessThan(10.0),
            $"Southern hemisphere perfect mount: az err {azErr:F1}\"");
        Assert.That(Math.Abs(altErr), Is.LessThan(10.0),
            $"Southern hemisphere perfect mount: alt err {altErr:F1}\"");
    }

    [Test]
    public void ComputeError_KnownErrorNorth_RecoversInputWithinFiveArcsec() {
        const double lat = 45.0;
        const double lon = -73.0;

        // Inject a 120 arcsec east + 300 arcsec up offset of the
        // mount's mechanical pole relative to true celestial pole.
        double expectedAzErrSec = 120.0;
        double expectedAltErrSec = 300.0;

        var pts = SynthesizePoints(
            startRaHours: 6.0, startDecDeg: 60.0,
            slewStepDeg: 30,
            latDeg: lat, longDeg: lon,
            mountAzOffsetDeg: expectedAzErrSec / 3600.0,
            mountAltOffsetDeg: expectedAltErrSec / 3600.0);

        var (azErr, altErr) = PolarAlignmentMath.ComputeError(
            pts[0], pts[1], pts[2], lat, lon);

        // Tolerance: floating-point + LST quantisation accumulate to
        // a handful of arcsec, well below realistic mount precision.
        Assert.That(azErr, Is.EqualTo(expectedAzErrSec).Within(5.0),
            $"Az error: expected {expectedAzErrSec}\", got {azErr:F2}\"");
        Assert.That(altErr, Is.EqualTo(expectedAltErrSec).Within(5.0),
            $"Alt error: expected {expectedAltErrSec}\", got {altErr:F2}\"");
    }

    [Test]
    public void ComputeError_KnownErrorSouth_RecoversInputWithinFiveArcsec() {
        const double lat = -23.5;     // Tropic of Capricorn (Brazil)
        const double lon = -46.6;     // São Paulo

        double expectedAzErrSec = -180.0;   // mount tilted west of pole
        double expectedAltErrSec = -240.0;  // mount tilted below pole

        var pts = SynthesizePoints(
            startRaHours: 18.0, startDecDeg: -50.0,
            slewStepDeg: 30,
            latDeg: lat, longDeg: lon,
            mountAzOffsetDeg: expectedAzErrSec / 3600.0,
            mountAltOffsetDeg: expectedAltErrSec / 3600.0);

        var (azErr, altErr) = PolarAlignmentMath.ComputeError(
            pts[0], pts[1], pts[2], lat, lon);

        Assert.That(azErr, Is.EqualTo(expectedAzErrSec).Within(5.0),
            $"Southern hemisphere: expected az {expectedAzErrSec}\", got {azErr:F2}\"");
        Assert.That(altErr, Is.EqualTo(expectedAltErrSec).Within(5.0),
            $"Southern hemisphere: expected alt {expectedAltErrSec}\", got {altErr:F2}\"");
    }

    [Test]
    public void TotalErrorArcsec_IsEuclidean() {
        Assert.That(PolarAlignmentMath.TotalErrorArcsec(60, 80), Is.EqualTo(100).Within(1e-9));
        Assert.That(PolarAlignmentMath.TotalErrorArcsec(0, 0), Is.EqualTo(0).Within(1e-9));
        Assert.That(PolarAlignmentMath.TotalErrorArcsec(-30, 40), Is.EqualTo(50).Within(1e-9));
    }

    [Test]
    public void ComputeError_DegenerateColinearPoints_DoesNotThrow() {
        // 3 plate-solved points all at the same position (mount didn't
        // actually move — e.g. slew failed silently). Cross product
        // degenerates to zero vector; Normalize returns it as-is and
        // the dot-product disambiguation falls through. Result is
        // garbage but the function MUST NOT throw — RunAsync's
        // error-handling owns the "we got nonsense" decision.
        var p = new PolarPoint(0, 6.0, 60.0, 0.0, T0);
        var pts = new[] {
            p,
            p with { Index = 1, AtUtc = T0.AddSeconds(30) },
            p with { Index = 2, AtUtc = T0.AddSeconds(60) }
        };

        Assert.DoesNotThrow(() => {
            var _ = PolarAlignmentMath.ComputeError(pts[0], pts[1], pts[2], 45.0, 0.0);
        });
    }

    // -----------------------------------------------------------------
    // Synthesizer — generates the 3 plate-solved points that a real
    // run on a (mountAzOffset, mountAltOffset)-misaligned mount would
    // produce, starting from (startRa, startDec) and slewing in
    // mount-RA by slewStepDeg between samples.
    //
    // Strategy:
    //   1. Compute the mount's mechanical pole direction in the
    //      Alt/Az frame (= ideal pole + small offset).
    //   2. Compute the topocentric Alt/Az unit vector for the
    //      starting (RA, Dec) at T0.
    //   3. For each i ∈ {0, 1, 2}, rotate that vector around the
    //      mount pole by (i * slewStepDeg). That simulates the mount
    //      stepping in its own RA.
    //   4. Convert the rotated vector back to (RA, Dec) at the
    //      sample's UTC instant.
    //
    // Result: 3 PolarPoint records ready to feed ComputeError.
    // -----------------------------------------------------------------
    private static PolarPoint[] SynthesizePoints(
        double startRaHours, double startDecDeg,
        double slewStepDeg,
        double latDeg, double longDeg,
        double mountAzOffsetDeg, double mountAltOffsetDeg) {

        // Mount pole direction in (east, north, up) Alt/Az frame.
        var mountPole = HemisphereIdealAxis(latDeg);
        // Apply small offset by rotating about east axis (alt offset)
        // then about up axis (az offset). For small offsets this is
        // numerically equivalent to the true tilt.
        //
        // Sign conventions:
        //   - Positive alt offset → mount pole HIGHER than ideal (the
        //     user needs to lower it). In the north the ideal pole is
        //     in +Y/+Z (north-up); rotating around east (+X) by +deg
        //     moves +Y toward +Z, raising altitude. In the south the
        //     ideal pole is in -Y/+Z (south-up); the SAME rotation
        //     moves -Y toward -Z, LOWERING altitude. So flip the
        //     rotation sign for the southern hemisphere to keep the
        //     test's "+alt offset = higher" convention.
        //   - Positive az offset → mount pole tilted east of ideal.
        //     Rotating around up axis (+Z) by +deg moves +Y toward
        //     -X (azimuth decreases since az grows clockwise from
        //     north through east). Flip rotation sign so "+az
        //     offset = east-of-ideal" matches the math's azErr sign.
        double altRotDeg = latDeg >= 0 ? mountAltOffsetDeg : -mountAltOffsetDeg;
        mountPole = RotateAroundAxis(mountPole, (1, 0, 0), altRotDeg);
        mountPole = RotateAroundAxis(mountPole, (0, 0, 1), -mountAzOffsetDeg);

        // Starting sky position as Alt/Az unit vector at T0.
        var startVec = SkyToAltAzVector(startRaHours, startDecDeg, T0, latDeg, longDeg);

        var pts = new PolarPoint[3];
        for (int i = 0; i < 3; i++) {
            var t = T0.AddSeconds(i * 60.0);  // ~1 min per slew+settle+solve
            // Rotate the sky vector around the MOUNT pole by i steps.
            var rotated = RotateAroundAxis(startVec, mountPole, i * slewStepDeg);
            // Convert back to (RA, Dec) at this instant.
            var (raH, decD) = AltAzVectorToRaDec(rotated, t, latDeg, longDeg);
            pts[i] = new PolarPoint(i, raH, decD, 0.0, t);
        }
        return pts;
    }

    // Replicates PolarAlignmentMath.HemisphereIdealAxis since it's private.
    private static (double X, double Y, double Z) HemisphereIdealAxis(double latDeg) {
        double altDeg = Math.Abs(latDeg);
        double azDeg = latDeg >= 0 ? 0.0 : 180.0;
        double altRad = altDeg * Math.PI / 180.0;
        double azRad = azDeg * Math.PI / 180.0;
        double cosAlt = Math.Cos(altRad);
        return (cosAlt * Math.Sin(azRad), cosAlt * Math.Cos(azRad), Math.Sin(altRad));
    }

    // Rodrigues' formula: rotate v around unit axis k by theta degrees.
    private static (double X, double Y, double Z) RotateAroundAxis(
        (double X, double Y, double Z) v, (double X, double Y, double Z) k, double thetaDeg) {

        // Normalize axis.
        double km = Math.Sqrt(k.X * k.X + k.Y * k.Y + k.Z * k.Z);
        if (km > 0) k = (k.X / km, k.Y / km, k.Z / km);

        double theta = thetaDeg * Math.PI / 180.0;
        double c = Math.Cos(theta), s = Math.Sin(theta), oc = 1 - c;
        double dot = k.X * v.X + k.Y * v.Y + k.Z * v.Z;
        return (
            v.X * c + (k.Y * v.Z - k.Z * v.Y) * s + k.X * dot * oc,
            v.Y * c + (k.Z * v.X - k.X * v.Z) * s + k.Y * dot * oc,
            v.Z * c + (k.X * v.Y - k.Y * v.X) * s + k.Z * dot * oc);
    }

    private static (double X, double Y, double Z) SkyToAltAzVector(
        double raHours, double decDeg, DateTime utc, double latDeg, double longDeg) {
        double lstH = LocalSiderealHours(utc, longDeg);
        double haDeg = (lstH - raHours) * 15.0;
        double haRad = haDeg * Math.PI / 180.0;
        double decRad = decDeg * Math.PI / 180.0;
        double latRad = latDeg * Math.PI / 180.0;
        double sinAlt = Math.Sin(decRad) * Math.Sin(latRad)
                      + Math.Cos(decRad) * Math.Cos(latRad) * Math.Cos(haRad);
        double altRad = Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0));
        double sinAz = -Math.Cos(decRad) * Math.Sin(haRad);
        double cosAz = Math.Sin(decRad) * Math.Cos(latRad)
                     - Math.Cos(decRad) * Math.Sin(latRad) * Math.Cos(haRad);
        double azRad = Math.Atan2(sinAz, cosAz);
        if (azRad < 0) azRad += 2 * Math.PI;
        double cosAlt = Math.Cos(altRad);
        return (cosAlt * Math.Sin(azRad), cosAlt * Math.Cos(azRad), Math.Sin(altRad));
    }

    private static (double raHours, double decDeg) AltAzVectorToRaDec(
        (double X, double Y, double Z) v, DateTime utc, double latDeg, double longDeg) {
        double altRad = Math.Asin(Math.Clamp(v.Z, -1.0, 1.0));
        double azRad = Math.Atan2(v.X, v.Y);
        if (azRad < 0) azRad += 2 * Math.PI;
        double latRad = latDeg * Math.PI / 180.0;

        double sinDec = Math.Sin(altRad) * Math.Sin(latRad)
                      + Math.Cos(altRad) * Math.Cos(latRad) * Math.Cos(azRad);
        double decRad = Math.Asin(Math.Clamp(sinDec, -1.0, 1.0));
        double sinHa = -Math.Sin(azRad) * Math.Cos(altRad) / Math.Cos(decRad);
        double cosHa = (Math.Sin(altRad) - Math.Sin(latRad) * Math.Sin(decRad))
                     / (Math.Cos(latRad) * Math.Cos(decRad));
        double haRad = Math.Atan2(sinHa, cosHa);
        double haHours = haRad * 12.0 / Math.PI;

        double lstH = LocalSiderealHours(utc, longDeg);
        double raHours = lstH - haHours;
        raHours = ((raHours % 24.0) + 24.0) % 24.0;
        return (raHours, decRad * 180.0 / Math.PI);
    }

    // Same LST formula as PolarAlignmentMath uses internally (Meeus 12.4).
    private static double LocalSiderealHours(DateTime utc, double longDeg) {
        double jd = utc.ToOADate() + 2415018.5;
        double t = (jd - 2451545.0) / 36525.0;
        double gmstDeg = 280.46061837
                      + 360.98564736629 * (jd - 2451545.0)
                      + 0.000387933 * t * t
                      - (t * t * t) / 38710000.0;
        gmstDeg = ((gmstDeg % 360.0) + 360.0) % 360.0;
        double lmstDeg = (gmstDeg + longDeg + 360.0) % 360.0;
        return lmstDeg / 15.0;
    }
}
