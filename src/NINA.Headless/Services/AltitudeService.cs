namespace NINA.Headless.Services;

/// <summary>
/// Generates altitude/azimuth tracks for arbitrary RA/Dec targets across an
/// observation window, plus civil/nautical/astronomical twilight bracketing.
/// Reuses the LST math already proven by MeridianFlipService.
/// </summary>
public class AltitudeService {
    private readonly ProfileService _profile;

    public AltitudeService(ProfileService profile) {
        _profile = profile;
    }

    /// <summary>
    /// Sample altitude/azimuth of a target every <paramref name="stepMinutes"/>
    /// minutes from <paramref name="fromUtc"/> through <paramref name="toUtc"/>.
    /// </summary>
    public List<AltSample> ComputeTrack(double raHours, double decDeg,
        DateTime fromUtc, DateTime toUtc, int stepMinutes = 15) {
        var lat = _profile.Active.Latitude;
        var lon = _profile.Active.Longitude;
        var samples = new List<AltSample>();
        for (var t = fromUtc; t <= toUtc; t = t.AddMinutes(stepMinutes)) {
            var (alt, az) = RaDecToAltAz(raHours, decDeg, t, lat, lon);
            samples.Add(new AltSample {
                Utc = t,
                AltitudeDeg = alt,
                AzimuthDeg = az
            });
        }
        return samples;
    }

    /// <summary>
    /// For the night centered around <paramref name="nightUtc"/> (defaults to
    /// "now"), return the four twilight transitions (civil, nautical,
    /// astronomical) bracketed by sunset and sunrise. Times are approximate
    /// (low-precision sun position) but plenty good enough for plotting
    /// observability bands behind an altitude chart.
    /// </summary>
    public NightWindow ComputeNightWindow(DateTime? nightUtc = null) {
        var lat = _profile.Active.Latitude;
        var lon = _profile.Active.Longitude;
        // Pick the date that contains local noon — the night "belongs" to the
        // day on whose evening it begins.
        var anchor = nightUtc ?? DateTime.UtcNow;
        var localNoon = new DateTime(anchor.Year, anchor.Month, anchor.Day, 12, 0, 0, DateTimeKind.Utc)
            .AddHours(-lon / 15.0);

        return new NightWindow {
            Sunset                 = FindSunAltCrossing(localNoon, +1, 0,    lat, lon),
            CivilDuskUtc           = FindSunAltCrossing(localNoon, +1, -6,   lat, lon),
            NauticalDuskUtc        = FindSunAltCrossing(localNoon, +1, -12,  lat, lon),
            AstronomicalDuskUtc    = FindSunAltCrossing(localNoon, +1, -18,  lat, lon),
            AstronomicalDawnUtc    = FindSunAltCrossing(localNoon.AddHours(24), -1, -18, lat, lon),
            NauticalDawnUtc        = FindSunAltCrossing(localNoon.AddHours(24), -1, -12, lat, lon),
            CivilDawnUtc           = FindSunAltCrossing(localNoon.AddHours(24), -1, -6,  lat, lon),
            Sunrise                = FindSunAltCrossing(localNoon.AddHours(24), -1, 0,   lat, lon)
        };
    }

    // ---- Astronomical helpers ----

    /// <summary>
    /// Convert equatorial → horizontal coords for the given UTC + site.
    /// Returns (altitude, azimuth) in degrees. Azimuth measured east from north.
    /// </summary>
    public static (double altDeg, double azDeg) RaDecToAltAz(
        double raHours, double decDeg, DateTime utc, double latDeg, double lonDeg) {
        var lstHours = MeridianFlipService.ComputeLstHours(utc, lonDeg);
        var haHours = lstHours - raHours;
        var haRad = haHours * 15 * Math.PI / 180.0;
        var decRad = decDeg * Math.PI / 180.0;
        var latRad = latDeg * Math.PI / 180.0;

        var sinAlt = Math.Sin(decRad) * Math.Sin(latRad) +
                     Math.Cos(decRad) * Math.Cos(latRad) * Math.Cos(haRad);
        var alt = Math.Asin(Math.Clamp(sinAlt, -1, 1));

        var y = -Math.Cos(decRad) * Math.Cos(latRad) * Math.Sin(haRad);
        var x = Math.Sin(decRad) - Math.Sin(latRad) * sinAlt;
        var az = Math.Atan2(y, x);
        var azDeg = az * 180 / Math.PI;
        if (azDeg < 0) azDeg += 360;
        return (alt * 180 / Math.PI, azDeg);
    }

    /// <summary>Low-precision sun position (good to ~1 arcmin).</summary>
    public static (double raHours, double decDeg) SunPosition(DateTime utc) {
        var jd = ToJulianDate(utc);
        var n = jd - 2451545.0;
        // Mean longitude
        var L = (280.460 + 0.9856474 * n) % 360;
        if (L < 0) L += 360;
        // Mean anomaly
        var g = ((357.528 + 0.9856003 * n) % 360) * Math.PI / 180;
        // Ecliptic longitude
        var lambda = (L + 1.915 * Math.Sin(g) + 0.020 * Math.Sin(2 * g)) * Math.PI / 180;
        // Obliquity
        var eps = (23.439 - 0.0000004 * n) * Math.PI / 180;
        // RA / Dec
        var ra = Math.Atan2(Math.Cos(eps) * Math.Sin(lambda), Math.Cos(lambda));
        var dec = Math.Asin(Math.Sin(eps) * Math.Sin(lambda));
        var raHours = ra * 12 / Math.PI;
        if (raHours < 0) raHours += 24;
        return (raHours, dec * 180 / Math.PI);
    }

    private static double ToJulianDate(DateTime utc) {
        int y = utc.Year, m = utc.Month;
        int d = utc.Day;
        if (m <= 2) { y--; m += 12; }
        int a = y / 100;
        int b = 2 - a + a / 4;
        double dayFrac = (utc.Hour + (utc.Minute + utc.Second / 60.0) / 60.0) / 24.0;
        return Math.Floor(365.25 * (y + 4716)) + Math.Floor(30.6001 * (m + 1)) + d + dayFrac + b - 1524.5;
    }

    /// <summary>
    /// Scan in 1-minute steps for when the sun crosses the given altitude
    /// boundary. <paramref name="direction"/> +1 = scanning forward
    /// (afternoon → night), -1 = backward (morning ← night).
    /// </summary>
    private static DateTime FindSunAltCrossing(DateTime startUtc, int direction,
        double targetAltDeg, double lat, double lon) {
        var step = TimeSpan.FromMinutes(direction);
        var t = startUtc;
        var (raH, decD) = SunPosition(t);
        var (prevAlt, _) = RaDecToAltAz(raH, decD, t, lat, lon);

        for (int i = 0; i < 14 * 60; i++) { // up to 14h search
            t = t.Add(step);
            (raH, decD) = SunPosition(t);
            var (alt, _) = RaDecToAltAz(raH, decD, t, lat, lon);
            // Crossing detection
            if ((prevAlt > targetAltDeg && alt <= targetAltDeg) ||
                (prevAlt < targetAltDeg && alt >= targetAltDeg)) {
                // Linear interpolation between the two minute boundaries
                var frac = (targetAltDeg - prevAlt) / (alt - prevAlt);
                return t.Add(-step).AddMinutes(direction * frac);
            }
            prevAlt = alt;
        }
        return t;
    }
}

public class AltSample {
    public DateTime Utc { get; set; }
    public double AltitudeDeg { get; set; }
    public double AzimuthDeg { get; set; }
}

public class NightWindow {
    public DateTime Sunset { get; set; }
    public DateTime CivilDuskUtc { get; set; }
    public DateTime NauticalDuskUtc { get; set; }
    public DateTime AstronomicalDuskUtc { get; set; }
    public DateTime AstronomicalDawnUtc { get; set; }
    public DateTime NauticalDawnUtc { get; set; }
    public DateTime CivilDawnUtc { get; set; }
    public DateTime Sunrise { get; set; }
}
