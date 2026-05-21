using CosineKitty;

namespace NINA.Headless.Services;

/// <summary>
/// Ranks celestial objects worth observing tonight from the observer's
/// location. Sources:
///   - DSOs: SkyCatalogService (≈200 Messier/Caldwell/NGC objects)
///   - Moon: AstronomyEngine
///   - Planets: AstronomyEngine (Mercury through Neptune; Pluto excluded
///     since it's effectively never bright enough for the use case)
///   - Comets: postponed to TB-4 follow-up (need orbital-element propagator)
///
/// "Tonight" = window from astronomical dusk (sun -18°) to astronomical
/// dawn at the observer's longitude, or a ±6 h fallback if the sun
/// never sets that far (high latitude summer).
///
/// Scoring is a simple composite:
///   - DSO base: (6 - clamp(mag, -2, 12)) so brighter rises
///   - Planet base: same formula but the planet's apparent magnitude
///   - Moon always present, base 50 + illumination bonus
///   - + (peakAlt / 90) * 20: rewards transit altitude
///   - Filter: only keep things with peak altitude ≥ 30° during the
///     night window (10° for the Moon — it's always interesting),
///     and dimmer than mag 10 cuts.
///
/// The "FitsCameraFov" flag is computed when a camera is connected and
/// the active rig has a focal length, comparing the object's major /
/// minor axes against the camera FOV in arcminutes.
/// </summary>
public class TonightsBestService {
    private readonly SkyCatalogService    _catalog;
    private readonly AltitudeService      _altitude;
    private readonly EquipmentManager     _equip;
    private readonly ProfileService       _profile;
    private readonly ILogger<TonightsBestService> _logger;

    public TonightsBestService(
            SkyCatalogService catalog,
            AltitudeService altitude,
            EquipmentManager equip,
            ProfileService profile,
            ILogger<TonightsBestService> logger) {
        _catalog  = catalog;
        _altitude = altitude;
        _equip    = equip;
        _profile  = profile;
        _logger   = logger;
    }

    public TonightsBestResult Compute(int limit = 30) {
        var lat = _profile.Active.Latitude;
        var lng = _profile.Active.Longitude;
        var nowUtc = DateTime.UtcNow;

        // Night window: astro dusk → astro dawn. Fallback to ±6 h if the
        // sun never reaches -18° (polar summer).
        var night = _altitude.ComputeNightWindow(nowUtc);
        var nightStart = night.AstronomicalDuskUtc;
        var nightEnd   = night.AstronomicalDawnUtc;
        if (nightEnd <= nightStart) {
            nightStart = nowUtc.AddHours(-6);
            nightEnd   = nowUtc.AddHours( 6);
        }

        var fov = ComputeCameraFov();
        var items = new List<TonightCandidate>();

        // --- DSOs ---
        foreach (var dso in _catalog.AllObjects) {
            // Coarse magnitude gate before doing the (expensive) altitude track.
            if (dso.Magnitude > 10) continue;
            var (peakAlt, peakUtc) = PeakAltitude(dso.Ra, dso.Dec, nightStart, nightEnd, stepMinutes: 30);
            if (peakAlt < 30) continue;
            var (curAlt, curAz) = AltitudeService.RaDecToAltAz(dso.Ra, dso.Dec, nowUtc, lat, lng);
            var score = (int)Math.Round((6 - Math.Clamp(dso.Magnitude, -2, 12)) * 8 + peakAlt / 90.0 * 20);
            items.Add(new TonightCandidate(
                Category:        "Dso",
                Name:            dso.Name,
                CommonName:      dso.CommonName,
                Type:            dso.Type,
                RaHours:         dso.Ra,
                DecDeg:          dso.Dec,
                Magnitude:       dso.Magnitude,
                Size:            null,
                SizeMajorArcmin: null,
                SizeMinorArcmin: null,
                CurrentAltDeg:   Math.Round(curAlt, 1),
                CurrentAzDeg:    Math.Round(curAz,  1),
                PeakAltDeg:      Math.Round(peakAlt, 1),
                PeakUtc:         peakUtc,
                Score:           score,
                FitsCameraFov:   null,                  // catalog has no size for now
                CameraFovWidthArcmin:  fov?.WidthArcmin,
                CameraFovHeightArcmin: fov?.HeightArcmin
            ));
        }

        // --- Solar-system bodies via AstronomyEngine ---
        var observer = new Observer(lat, lng, _profile.Active.Altitude);
        var time = new AstroTime(nowUtc);

        // Moon — always include if above horizon at peak.
        AddSolarSystem("Moon", Body.Moon, "Moon", observer, time, nightStart, nightEnd, lat, lng, fov, items,
            minPeakAlt: 10, baseBoost: 50);

        // Planets
        foreach (var (label, body) in PlanetSet()) {
            AddSolarSystem(label, body, "Planet", observer, time, nightStart, nightEnd, lat, lng, fov, items,
                minPeakAlt: 15, baseBoost: 0);
        }

        // Sort + cap
        var ordered = items.OrderByDescending(i => i.Score).Take(limit).ToList();
        return new TonightsBestResult(
            ComputedAtUtc:     nowUtc,
            NightStartUtc:     nightStart,
            NightEndUtc:       nightEnd,
            ObserverLat:       lat,
            ObserverLon:       lng,
            CameraFovWidthArcmin:  fov?.WidthArcmin,
            CameraFovHeightArcmin: fov?.HeightArcmin,
            Items:             ordered);
    }

    private void AddSolarSystem(string name, Body body, string category,
                                Observer observer, AstroTime time,
                                DateTime nightStart, DateTime nightEnd,
                                double lat, double lng,
                                CameraFov? fov,
                                List<TonightCandidate> items,
                                double minPeakAlt, int baseBoost) {
        try {
            // Apparent equatorial coords (RA in hours, Dec in deg). Use
            // EquatorEpoch.OfDate so the position aligns with the
            // observation epoch rather than J2000.
            var eq = Astronomy.Equator(body, time, observer, EquatorEpoch.OfDate, Aberration.Corrected);
            var ra  = eq.ra;
            var dec = eq.dec;

            var (peakAlt, peakUtc) = PeakAltitudeBody(body, observer, nightStart, nightEnd, stepMinutes: 30);
            if (peakAlt < minPeakAlt) return;
            var (curAlt, curAz) = AltitudeService.RaDecToAltAz(ra, dec, DateTime.UtcNow, lat, lng);

            double? mag = null;
            try {
                var illum = Astronomy.Illumination(body, time);
                mag = illum.mag;
            } catch { /* Some bodies (Sun-only) don't compute; safe to skip */ }

            double scoreBase = mag.HasValue
                ? (4 - Math.Clamp(mag.Value, -13, 12)) * 6
                : 30;
            var score = (int)Math.Round(baseBoost + scoreBase + peakAlt / 90.0 * 20);

            items.Add(new TonightCandidate(
                Category:        category,
                Name:            name,
                CommonName:      null,
                Type:            category,
                RaHours:         ra,
                DecDeg:          dec,
                Magnitude:       mag.HasValue ? Math.Round(mag.Value, 2) : null,
                Size:            null,
                SizeMajorArcmin: null,
                SizeMinorArcmin: null,
                CurrentAltDeg:   Math.Round(curAlt, 1),
                CurrentAzDeg:    Math.Round(curAz,  1),
                PeakAltDeg:      Math.Round(peakAlt, 1),
                PeakUtc:         peakUtc,
                Score:           score,
                FitsCameraFov:   null,
                CameraFovWidthArcmin:  fov?.WidthArcmin,
                CameraFovHeightArcmin: fov?.HeightArcmin
            ));
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Skipping body {Name} (AstronomyEngine error)", name);
        }
    }

    private static IEnumerable<(string Name, Body Body)> PlanetSet() {
        yield return ("Mercury", Body.Mercury);
        yield return ("Venus",   Body.Venus);
        yield return ("Mars",    Body.Mars);
        yield return ("Jupiter", Body.Jupiter);
        yield return ("Saturn",  Body.Saturn);
        yield return ("Uranus",  Body.Uranus);
        yield return ("Neptune", Body.Neptune);
    }

    private (double peakAlt, DateTime peakUtc) PeakAltitude(double ra, double dec,
                                                            DateTime from, DateTime to,
                                                            int stepMinutes) {
        var track = _altitude.ComputeTrack(ra, dec, from, to, stepMinutes);
        if (track.Count == 0) return (-90, from);
        var best = track.OrderByDescending(s => s.AltitudeDeg).First();
        return (best.AltitudeDeg, best.Utc);
    }

    private (double peakAlt, DateTime peakUtc) PeakAltitudeBody(Body body, Observer observer,
                                                                DateTime from, DateTime to,
                                                                int stepMinutes) {
        double peak = -90;
        DateTime peakAt = from;
        for (var t = from; t <= to; t = t.AddMinutes(stepMinutes)) {
            var time = new AstroTime(t);
            var eq = Astronomy.Equator(body, time, observer, EquatorEpoch.OfDate, Aberration.Corrected);
            var horiz = Astronomy.Horizon(time, observer, eq.ra, eq.dec, Refraction.Normal);
            if (horiz.altitude > peak) { peak = horiz.altitude; peakAt = t; }
        }
        return (peak, peakAt);
    }

    private CameraFov? ComputeCameraFov() {
        var cam = _equip.Camera;
        if (cam == null || !cam.IsConnected) return null;
        double pixX, pixY; int mx, my;
        try {
            pixX = cam.PixelSizeX;
            pixY = cam.PixelSizeY;
            mx   = cam.MaxX;
            my   = cam.MaxY;
        } catch { return null; }
        if (pixX <= 0 || pixY <= 0 || mx <= 0 || my <= 0) return null;

        var sensorWmm = mx * pixX / 1000.0;
        var sensorHmm = my * pixY / 1000.0;
        var focalMm   = _profile.Active.FocalLengthMm;
        if (focalMm <= 0) return null;

        var fovWdeg = 2 * Math.Atan(sensorWmm / (2 * focalMm)) * (180.0 / Math.PI);
        var fovHdeg = 2 * Math.Atan(sensorHmm / (2 * focalMm)) * (180.0 / Math.PI);
        return new CameraFov(
            WidthArcmin:  Math.Round(fovWdeg  * 60, 1),
            HeightArcmin: Math.Round(fovHdeg  * 60, 1));
    }

    private record CameraFov(double WidthArcmin, double HeightArcmin);
}

// ---------- DTOs ----------

public record TonightsBestResult(
    DateTime ComputedAtUtc,
    DateTime NightStartUtc,
    DateTime NightEndUtc,
    double ObserverLat,
    double ObserverLon,
    double? CameraFovWidthArcmin,
    double? CameraFovHeightArcmin,
    IReadOnlyList<TonightCandidate> Items);

public record TonightCandidate(
    string Category,
    string Name,
    string? CommonName,
    string? Type,
    double RaHours,
    double DecDeg,
    double? Magnitude,
    string? Size,
    double? SizeMajorArcmin,
    double? SizeMinorArcmin,
    double CurrentAltDeg,
    double CurrentAzDeg,
    double PeakAltDeg,
    DateTime PeakUtc,
    int Score,
    bool? FitsCameraFov,
    double? CameraFovWidthArcmin,
    double? CameraFovHeightArcmin);
