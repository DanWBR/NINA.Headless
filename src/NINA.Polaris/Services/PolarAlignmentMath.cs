namespace NINA.Polaris.Services;

/// <summary>
/// Polar-axis fit for TPPA (Three-Point Polar Alignment).
///
/// Algorithm (Challis 1879, modernised; same shape as N.I.N.A. desktop,
/// SharpCap, and KStars use):
///
///   - Each plate-solved point gives the TRUE sky direction the
///     optical axis was pointing at instant t (RA/Dec).
///   - A perfectly polar-aligned mount, as it rotates in HA at the
///     sidereal rate, sweeps the optical axis along a small circle
///     of constant declination.
///   - A misaligned mount still produces a small circle on the
///     celestial sphere; the pole of that circle is the MOUNT's
///     rotation axis (not the celestial pole).
///   - Pick any 3 points along that small circle (mount tracking
///     ON for a few seconds, then we slew the mount in RA, then
///     a second short tracking interval — equivalently three
///     RA-offset samples). The unit vectors to those three points
///     lie on the plane of the small circle. The plane's normal is
///     the mount's polar axis direction.
///   - Subtracting the celestial pole direction (which depends on
///     hemisphere) gives the polar misalignment as a small (alt,
///     az) error vector.
///
/// This implementation works in the topocentric Alt/Az frame so
/// the error directly comes out as (azError, altError) in arcsec —
/// matching what the user needs to adjust on the mount knobs.
/// </summary>
public static class PolarAlignmentMath {
    /// <summary>Compute polar-axis offset from 3 plate-solved points.
    /// Returns (azErrorArcsec, altErrorArcsec). Positive azimuth means
    /// the mount's polar axis is east of true pole; positive altitude
    /// means it's higher than true pole. The UI's arrow points the
    /// direction the visual user-facing polar axis should NUDGE to
    /// reduce the error to zero.</summary>
    public static (double azErrSec, double altErrSec) ComputeError(
        PolarPoint p1, PolarPoint p2, PolarPoint p3,
        double siteLatDeg, double siteLongDeg) {

        // 1. Convert each (RA, Dec, time) → unit vector in the local
        //    Alt/Az topocentric frame at that instant.
        var v1 = RaDecToAltAzVector(p1.RaHours, p1.DecDeg, p1.AtUtc, siteLatDeg, siteLongDeg);
        var v2 = RaDecToAltAzVector(p2.RaHours, p2.DecDeg, p2.AtUtc, siteLatDeg, siteLongDeg);
        var v3 = RaDecToAltAzVector(p3.RaHours, p3.DecDeg, p3.AtUtc, siteLatDeg, siteLongDeg);

        // 2. Plane normal = (v2 - v1) × (v3 - v1). All three vectors
        //    sit on the small circle whose axis is the mount's rotation
        //    axis. The normal of THEIR plane IS that axis.
        var a = Sub(v2, v1);
        var b = Sub(v3, v1);
        var n = Normalize(Cross(a, b));

        // 3. The mount's polar axis vector in Alt/Az coordinates.
        //    Northern hemisphere: ideal axis points to Alt=lat, Az=0
        //    (north). Southern hemisphere: Alt=|lat|, Az=180 (south).
        //    We disambiguate which end of the n vector is the axis
        //    "north pole" by picking the one closer to the expected
        //    ideal axis direction (otherwise we'd report a 180°
        //    misalignment when the cross product happened to flip).
        var idealAxis = HemisphereIdealAxis(siteLatDeg);
        if (Dot(n, idealAxis) < 0) {
            n = V(-n.X, -n.Y, -n.Z);
        }

        // 4. Convert the mount-axis vector to (Alt, Az). Subtract from
        //    the ideal to get the residual error.
        var (mountAltDeg, mountAzDeg) = AltAzFromVector(n);

        double idealAltDeg = Math.Abs(siteLatDeg);
        double idealAzDeg = siteLatDeg >= 0 ? 0.0 : 180.0;

        double altErrDeg = mountAltDeg - idealAltDeg;
        double azErrDeg = NormalizeAzDelta(mountAzDeg - idealAzDeg);

        return (azErrDeg * 3600.0, altErrDeg * 3600.0);
    }

    /// <summary>Total angular error magnitude in arcsec — what the UI
    /// arrow's length encodes.</summary>
    public static double TotalErrorArcsec(double azErrSec, double altErrSec) {
        return Math.Sqrt(azErrSec * azErrSec + altErrSec * altErrSec);
    }

    // ---------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------

    private record struct Vec3(double X, double Y, double Z);

    private static Vec3 V(double x, double y, double z) => new(x, y, z);

    /// <summary>RA/Dec at time t → unit vector in topocentric Alt/Az
    /// frame. X = east, Y = north, Z = up.</summary>
    private static Vec3 RaDecToAltAzVector(double raHours, double decDeg,
                                            DateTime atUtc, double latDeg, double longDeg) {
        // Local Sidereal Time at the observer at instant t.
        double lstHours = LocalSiderealHours(atUtc, longDeg);

        // Hour Angle = LST - RA (both in hours).
        double haHours = lstHours - raHours;
        double haDeg = haHours * 15.0;
        double haRad = haDeg * Math.PI / 180.0;
        double decRad = decDeg * Math.PI / 180.0;
        double latRad = latDeg * Math.PI / 180.0;

        // Standard equatorial → horizontal transform (e.g. Meeus eq. 13.6).
        double sinAlt = Math.Sin(decRad) * Math.Sin(latRad)
                      + Math.Cos(decRad) * Math.Cos(latRad) * Math.Cos(haRad);
        double altRad = Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0));

        // Azimuth measured from north, increasing eastward (typical
        // astronomy convention — N=0°, E=90°, S=180°, W=270°).
        double sinAz = -Math.Cos(decRad) * Math.Sin(haRad);
        double cosAz = Math.Sin(decRad) * Math.Cos(latRad)
                     - Math.Cos(decRad) * Math.Sin(latRad) * Math.Cos(haRad);
        double azRad = Math.Atan2(sinAz, cosAz);
        if (azRad < 0) azRad += 2 * Math.PI;

        // Unit vector in (east, north, up) coordinates.
        double cosAlt = Math.Cos(altRad);
        return V(
            cosAlt * Math.Sin(azRad),
            cosAlt * Math.Cos(azRad),
            Math.Sin(altRad));
    }

    /// <summary>Vector → (altDeg, azDeg). Inverse of RaDecToAltAzVector
    /// once you already have the topocentric Cartesian.</summary>
    private static (double altDeg, double azDeg) AltAzFromVector(Vec3 v) {
        double altRad = Math.Asin(Math.Clamp(v.Z, -1.0, 1.0));
        double azRad = Math.Atan2(v.X, v.Y);
        if (azRad < 0) azRad += 2 * Math.PI;
        return (altRad * 180.0 / Math.PI, azRad * 180.0 / Math.PI);
    }

    /// <summary>Direction of the ideal polar axis at this latitude,
    /// as a (east, north, up) unit vector. Northern: tilted toward
    /// north up at altitude=lat; southern: toward south up at
    /// altitude=|lat|.</summary>
    private static Vec3 HemisphereIdealAxis(double latDeg) {
        double altDeg = Math.Abs(latDeg);
        double azDeg = latDeg >= 0 ? 0.0 : 180.0;
        double altRad = altDeg * Math.PI / 180.0;
        double azRad = azDeg * Math.PI / 180.0;
        double cosAlt = Math.Cos(altRad);
        return V(
            cosAlt * Math.Sin(azRad),
            cosAlt * Math.Cos(azRad),
            Math.Sin(altRad));
    }

    /// <summary>Local Sidereal Time at the given UTC instant +
    /// observer longitude (east positive, degrees). Returned in
    /// hours [0, 24). Meeus formula 12.4 — good to a few seconds
    /// over decades, far better than TPPA needs.</summary>
    private static double LocalSiderealHours(DateTime utc, double longDeg) {
        // Julian Date — DateTime.ToOADate() returns days since 1899-12-30 12:00 UT.
        double jd = utc.ToOADate() + 2415018.5;
        double t = (jd - 2451545.0) / 36525.0;

        // Greenwich Mean Sidereal Time at 0h UT of the date.
        double gmstDeg = 280.46061837
                      + 360.98564736629 * (jd - 2451545.0)
                      + 0.000387933 * t * t
                      - (t * t * t) / 38710000.0;
        gmstDeg = ((gmstDeg % 360.0) + 360.0) % 360.0;

        double lmstDeg = (gmstDeg + longDeg + 360.0) % 360.0;
        return lmstDeg / 15.0;
    }

    /// <summary>Wrap az delta into (-180, +180] so the magnitude makes
    /// sense as a small correction value.</summary>
    private static double NormalizeAzDelta(double deg) {
        var d = ((deg + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
        return d;
    }

    private static Vec3 Sub(Vec3 a, Vec3 b) => V(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static Vec3 Cross(Vec3 a, Vec3 b) => V(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);
    private static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static Vec3 Normalize(Vec3 v) {
        double m = Math.Sqrt(Dot(v, v));
        return m > 0 ? V(v.X / m, v.Y / m, v.Z / m) : v;
    }
}
