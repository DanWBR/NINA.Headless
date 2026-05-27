using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using NINA.Core.Enum;
using NINA.Image.Interfaces;

namespace NINA.Image.FileFormat.FITS;

/// <summary>
/// Minimal-dependency FITS writer that produces files compatible with
/// PixInsight, ASTAP, AstroImageJ and other common downstream tools.
/// Pixels are written as signed Int16 with BZERO=32768 (the standard
/// trick to store unsigned 16-bit data in the signed format the FITS
/// spec requires).
///
/// Header set follows the keywords documented in the N.I.N.A. manual
/// (section 1.5.8 File Formats → FITS), broken down into:
///   STANDARD     (always present)
///   IMAGE        (exposure-related)
///   OBSERVER     (site lat/lon/elevation/name)
///   TARGET       (object name + planned coords)
///   CAMERA       (sensor, gain, binning, bayer)
///   TELESCOPE    (name, focal length/ratio, current pointing, pier side)
///   FILTER WHEEL (name, current filter)
///   FOCUSER      (name, position, step size, temperature)
///   ROTATOR      (name, angle, step size)
///   WEATHER      (cloud cover, dew, humidity, pressure, SQM, MPSAS, wind)
///
/// Headers are only emitted when the source ImageMetaData carries a
/// non-default value, so unconnected equipment doesn't leak placeholder
/// rows.
/// </summary>
public static class FITSWriter {
    public static void Write(IImageData imageData, string path,
        RotatorMetaData? rotator = null,
        string? observerName = null,
        string? observatoryName = null,
        string? siteName = null,
        IEnumerable<KeyValuePair<string, string>>? customKeywords = null) {

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        Write(imageData, fs, rotator, observerName, observatoryName, siteName, customKeywords);
    }

    public static void Write(IImageData imageData, Stream destination,
        RotatorMetaData? rotator = null,
        string? observerName = null,
        string? observatoryName = null,
        string? siteName = null,
        IEnumerable<KeyValuePair<string, string>>? customKeywords = null) {

        var w = imageData.Properties.Width;
        var h = imageData.Properties.Height;
        var pixels = imageData.Data;
        var meta = imageData.MetaData;

        // RGB cubes get NAXIS=3 / NAXIS3=3; mono stays at NAXIS=2.
        // Anything other than 1 or 3 channels is clamped to 1, we
        // don't support exotic multi-plane FITS writes today, and
        // silently dropping is worse than honouring the convention
        // most downstream tools (PixInsight, Siril, astropy) expect.
        int channels = imageData.Properties.Channels == 3 ? 3 : 1;
        long expectedPixelCount = (long)w * h * channels;
        if (pixels.LongLength < expectedPixelCount) {
            throw new InvalidOperationException(
                $"FITSWriter: pixel buffer length {pixels.LongLength} < " +
                $"expected {expectedPixelCount} for {w}×{h}×{channels}");
        }

        var cards = new List<string>();

        // ---- Standard headers (must be first, in this order) ----
        Add(cards, "SIMPLE", "T", "FITS standard");
        Add(cards, "BITPIX", "16", "16-bit signed pixels");
        Add(cards, "NAXIS", channels == 3 ? "3" : "2");
        Add(cards, "NAXIS1", w.ToString(CultureInfo.InvariantCulture));
        Add(cards, "NAXIS2", h.ToString(CultureInfo.InvariantCulture));
        if (channels == 3) {
            Add(cards, "NAXIS3", "3", "RGB planes");
        }
        Add(cards, "BZERO", "32768", "Offset for unsigned 16-bit");
        Add(cards, "BSCALE", "1");
        Add(cards, "EXTEND", "T");
        AddStr(cards, "SWCREATE", "NINA.Polaris");
        AddStr(cards, "ROWORDER", "TOP-DOWN");

        // ---- Image / exposure ----
        AddStr(cards, "IMAGETYP", meta.Exposure.ImageType ?? "LIGHT");
        if (meta.Exposure.ExposureTime > 0) {
            Add(cards, "EXPOSURE", Fmt(meta.Exposure.ExposureTime), "Exposure (s)");
            Add(cards, "EXPTIME", Fmt(meta.Exposure.ExposureTime), "Exposure (s)");
        }
        var utc = meta.CreationTime.ToUniversalTime();
        var local = meta.CreationTime.ToLocalTime();
        AddStr(cards, "DATE-LOC", local.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture));
        AddStr(cards, "DATE-UTC", utc.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture));
        if (meta.Exposure.ExposureTime > 0) {
            var avg = utc.AddSeconds(meta.Exposure.ExposureTime / 2.0);
            AddStr(cards, "DATE-AVG", avg.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture));
        }

        // ---- Observer / site ----
        if (Math.Abs(meta.Observer.Latitude) > 0.0001)
            Add(cards, "SITELAT", Fmt(meta.Observer.Latitude), "Site latitude (deg)");
        if (Math.Abs(meta.Observer.Longitude) > 0.0001)
            Add(cards, "SITELONG", Fmt(meta.Observer.Longitude), "Site longitude (deg, +E)");
        if (meta.Observer.Elevation > 0)
            Add(cards, "SITEELEV", Fmt(meta.Observer.Elevation), "Site elevation (m)");
        AddStr(cards, "OBSERVER", observerName);
        AddStr(cards, "OBSERVAT", observatoryName);
        AddStr(cards, "SITENAME", siteName);

        // ---- Target ----
        if (!string.IsNullOrEmpty(meta.Target.Name)) {
            AddStr(cards, "OBJECT", meta.Target.Name);
            Add(cards, "OBJCTRA", Fmt(meta.Target.RightAscension * 15.0), "Target RA (deg)");
            Add(cards, "OBJCTDEC", Fmt(meta.Target.Declination), "Target Dec (deg)");
            if (Math.Abs(meta.Target.Rotation) > 0.001)
                Add(cards, "OBJCTROT", Fmt(meta.Target.Rotation), "Planned rotation (deg)");
        }

        // ---- Camera ----
        AddStr(cards, "CAMERAID", meta.Camera.Name);
        AddStr(cards, "INSTRUME", meta.Camera.Name);
        Add(cards, "XBINNING", meta.Camera.BinX.ToString(CultureInfo.InvariantCulture));
        Add(cards, "YBINNING", meta.Camera.BinY.ToString(CultureInfo.InvariantCulture));
        if (meta.Camera.Gain != 0)
            Add(cards, "GAIN", meta.Camera.Gain.ToString(CultureInfo.InvariantCulture));
        if (meta.Camera.Offset != 0)
            Add(cards, "OFFSET", meta.Camera.Offset.ToString(CultureInfo.InvariantCulture));
        if (meta.Camera.PixelSizeX > 0)
            Add(cards, "XPIXSZ", Fmt(meta.Camera.PixelSizeX), "Pixel size X (um)");
        if (meta.Camera.PixelSizeY > 0)
            Add(cards, "YPIXSZ", Fmt(meta.Camera.PixelSizeY), "Pixel size Y (um)");
        if (Math.Abs(meta.Camera.Temperature) > 0.001)
            Add(cards, "CCD-TEMP", Fmt(meta.Camera.Temperature), "Sensor temp (C)");
        if (meta.Camera.ReadoutMode != 0)
            Add(cards, "READOUTM", meta.Camera.ReadoutMode.ToString(CultureInfo.InvariantCulture));
        if (meta.Camera.BayerPattern != BayerPatternEnum.None)
            AddStr(cards, "BAYERPAT", meta.Camera.BayerPattern.ToString().ToUpperInvariant());

        // ---- Telescope ----
        AddStr(cards, "TELESCOP", meta.Telescope.Name);
        if (meta.Telescope.FocalLength > 0)
            Add(cards, "FOCALLEN", Fmt(meta.Telescope.FocalLength), "Focal length (mm)");
        if (meta.Telescope.FocalRatio > 0)
            Add(cards, "FOCRATIO", Fmt(meta.Telescope.FocalRatio), "Focal ratio (f/N)");
        // RA/DEC are hours / degrees in our ImageMetaData
        if (meta.Telescope.RightAscension != 0 || meta.Telescope.Declination != 0) {
            Add(cards, "RA", Fmt(meta.Telescope.RightAscension * 15.0), "Mount RA (deg)");
            Add(cards, "DEC", Fmt(meta.Telescope.Declination), "Mount Dec (deg)");
        }
        if (meta.Telescope.SideOfPier != PierSide.pierUnknown) {
            AddStr(cards, "PIERSIDE",
                meta.Telescope.SideOfPier == PierSide.pierEast ? "East" :
                meta.Telescope.SideOfPier == PierSide.pierWest ? "West" : "Unknown");
        }

        // ---- Filter wheel ----
        // FilterWheel.Filter is the live-capture path's authoritative
        // source; Exposure.Filter is what FITSReader populates from
        // the FILTER header on disk. Pipeline-internal writes
        // (CalibrationService, BatchStackingService, etc.) set
        // Exposure.Filter only, so fall back to that when the FW
        // bucket is empty, otherwise the calibrated / integrated
        // FITS silently lose their filter tag.
        AddStr(cards, "FWHEEL", meta.FilterWheel.Name);
        var filterTag = !string.IsNullOrEmpty(meta.FilterWheel.Filter)
            ? meta.FilterWheel.Filter
            : (meta.Exposure.Filter ?? "");
        AddStr(cards, "FILTER", filterTag);

        // ---- Focuser ----
        AddStr(cards, "FOCNAME", meta.Focuser.Name);
        if (meta.Focuser.Position != 0) {
            Add(cards, "FOCPOS", meta.Focuser.Position.ToString(CultureInfo.InvariantCulture), "Focuser position");
            Add(cards, "FOCUSPOS", meta.Focuser.Position.ToString(CultureInfo.InvariantCulture));
        }
        if (meta.Focuser.StepSize > 0)
            Add(cards, "FOCUSSZ", Fmt(meta.Focuser.StepSize), "Step size (um)");
        if (Math.Abs(meta.Focuser.Temperature) > 0.001) {
            Add(cards, "FOCTEMP", Fmt(meta.Focuser.Temperature), "Focuser temp (C)");
            Add(cards, "FOCUSTEM", Fmt(meta.Focuser.Temperature));
        }

        // ---- Rotator (optional, separate metadata bag) ----
        if (rotator != null) {
            AddStr(cards, "ROTNAME", rotator.Name);
            if (Math.Abs(rotator.Angle) > 0.001) {
                Add(cards, "ROTATOR", Fmt(rotator.Angle), "Rotator angle (deg)");
                Add(cards, "ROTATANG", Fmt(rotator.Angle));
            }
            if (rotator.StepSize > 0)
                Add(cards, "ROTSTPSZ", Fmt(rotator.StepSize));
        }

        // ---- Weather ----
        if (meta.Weather.Temperature != 0)
            Add(cards, "AMBTEMP", Fmt(meta.Weather.Temperature), "Ambient temp (C)");
        if (meta.Weather.Humidity != 0)
            Add(cards, "HUMIDITY", Fmt(meta.Weather.Humidity), "Humidity (%)");
        if (meta.Weather.DewPoint != 0)
            Add(cards, "DEWPOINT", Fmt(meta.Weather.DewPoint), "Dew point (C)");
        if (meta.Weather.Pressure != 0)
            Add(cards, "PRESSURE", Fmt(meta.Weather.Pressure), "Pressure (hPa)");
        if (meta.Weather.SkyBrightness != 0)
            Add(cards, "SKYBRGHT", Fmt(meta.Weather.SkyBrightness), "Sky brightness (lux)");
        if (meta.Weather.SkyQuality != 0)
            Add(cards, "MPSAS", Fmt(meta.Weather.SkyQuality), "Sky quality (mag/arcsec^2)");

        // ---- WCS (CCALB-0a) -------------------------------------
        // Emit the standard WCS keyword block when the source image
        // carries a plate-solved coordinate system. Downstream tools
        // (PCC, PixInsight, Siril) pick it up by reading the FITS
        // headers without re-solving. Emitted BEFORE the custom
        // keywords so user-provided overrides still win on conflict.
        if (imageData.Properties.Wcs != null) {
            var wcsCards = new List<KeyValuePair<string, string>>();
            WcsHeaders.Add(wcsCards, imageData.Properties.Wcs);
            foreach (var kv in wcsCards) AddStr(cards, kv.Key, kv.Value);
        }

        // ---- Custom user keywords (last so they can override anything) ----
        if (customKeywords != null) {
            foreach (var kv in customKeywords) {
                AddStr(cards, kv.Key, kv.Value);
            }
        }

        cards.Add("END".PadRight(80));
        while (cards.Count % 36 != 0) cards.Add(new string(' ', 80));

        var headerBytes = Encoding.ASCII.GetBytes(string.Concat(cards));
        destination.Write(headerBytes);

        // Pixel data, Int16 big-endian with BZERO=32768
        var buf = new byte[2];
        foreach (var px in pixels) {
            short signed = (short)(px - 32768);
            BinaryPrimitives.WriteInt16BigEndian(buf, signed);
            destination.Write(buf);
        }

        // Pad to 2880-byte block boundary
        var dataLen = (long)pixels.Length * 2;
        int pad = (int)((2880 - (dataLen % 2880)) % 2880);
        if (pad > 0) destination.Write(new byte[pad]);
    }

    // ---- Card formatting helpers ----

    private static void Add(List<string> cards, string key, string value, string? comment = null) {
        if (string.IsNullOrWhiteSpace(value)) return;
        var card = $"{key,-8}= {value,20}";
        if (!string.IsNullOrEmpty(comment)) card += " / " + comment;
        cards.Add(card.Length > 80 ? card.Substring(0, 80) : card.PadRight(80));
    }

    private static void AddStr(List<string> cards, string key, string? value) {
        if (string.IsNullOrWhiteSpace(value)) return;
        var escaped = value.Replace("'", "''");
        if (escaped.Length > 68) escaped = escaped.Substring(0, 68);
        var quoted = $"'{escaped}'";
        var card = $"{key,-8}= {quoted,-20}";
        cards.Add(card.Length > 80 ? card.Substring(0, 80) : card.PadRight(80));
    }

    private static string Fmt(double v) {
        // Strip trailing zeros but keep at least one decimal digit
        return v.ToString("0.######", CultureInfo.InvariantCulture);
    }
}

public class RotatorMetaData {
    public string Name { get; set; } = string.Empty;
    public double Angle { get; set; }
    public double StepSize { get; set; }
}
