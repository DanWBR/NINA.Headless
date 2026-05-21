using System.Globalization;
using System.Text;
using System.Xml;
using K4os.Compression.LZ4;
using NINA.Core.Enum;
using NINA.Image.FileFormat.FITS;
using NINA.Image.Interfaces;

namespace NINA.Image.FileFormat.XISF;

/// <summary>
/// Writes images in PixInsight's XISF (Extensible Image Serialization Format)
/// 1.0 format. Compatible with PixInsight, Siril, ASTAP, AstroImageJ and the
/// growing list of tools that prefer XISF over FITS.
///
/// File layout (per the XISF 1.0 spec):
///   Bytes 0-7    : "XISF0100" signature
///   Bytes 8-11   : little-endian uint32 — XML header length in bytes
///   Bytes 12-15  : reserved (four 0x00)
///   Bytes 16..N  : UTF-8 XML header (padded with spaces to a multiple of
///                  4096 so attached binary blocks land aligned)
///   Bytes N+1..  : pixel data attachment(s), optionally LZ4-compressed
///
/// This implementation handles the common N.I.N.A. case: 16-bit unsigned
/// monochrome or Bayer-pattern data with optional LZ4 compression. Metadata
/// from ImageMetaData is mapped to FITSKeyword child elements so any
/// downstream tool that already understands the FITS spelling keeps working.
///
/// Public API mirrors FITSWriter for symmetry — see ImageWriterService.
/// </summary>
public static class XISFWriter {
    private const int HeaderBlockSize = 4096;

    public static void Write(IImageData imageData, string path,
        RotatorMetaData? rotator = null,
        string? observerName = null,
        string? observatoryName = null,
        string? siteName = null,
        IEnumerable<KeyValuePair<string, string>>? customKeywords = null,
        bool compress = true) {

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        Write(imageData, fs, rotator, observerName, observatoryName, siteName, customKeywords, compress);
    }

    public static void Write(IImageData imageData, Stream destination,
        RotatorMetaData? rotator = null,
        string? observerName = null,
        string? observatoryName = null,
        string? siteName = null,
        IEnumerable<KeyValuePair<string, string>>? customKeywords = null,
        bool compress = true) {

        var w = imageData.Properties.Width;
        var h = imageData.Properties.Height;
        var pixels = imageData.Data;
        var meta = imageData.MetaData;

        // ---- 1. Convert pixel data to bytes (little-endian uint16) ----
        var rawBytes = new byte[pixels.Length * 2];
        Buffer.BlockCopy(pixels, 0, rawBytes, 0, rawBytes.Length);

        byte[] dataBytes;
        long uncompressedSize = rawBytes.Length;
        string? compressionAttr = null;
        if (compress) {
            var maxLen = LZ4Codec.MaximumOutputSize(rawBytes.Length);
            var buf = new byte[maxLen];
            var n = LZ4Codec.Encode(rawBytes, buf, LZ4Level.L00_FAST);
            dataBytes = new byte[n];
            Array.Copy(buf, dataBytes, n);
            compressionAttr = $"lz4:{uncompressedSize}";
        } else {
            dataBytes = rawBytes;
        }

        // ---- 2. Build XML header ----
        // Pixel data starts immediately after the padded header. Offset must
        // be known before writing the XML (since it goes in the `location`
        // attribute). The header has a 16-byte preamble (sig + len + reserved).
        // So we iteratively choose a header-block count that fits.
        var bayer = meta.Camera.BayerPattern;
        var colorSpace = bayer != BayerPatternEnum.None ? "Gray" : "Gray"; // PixInsight debayers on import for either
        var imageType = string.IsNullOrEmpty(meta.Exposure.ImageType) ? "Light" : NormaliseImageType(meta.Exposure.ImageType);

        // First attempt: assume the smallest possible header block (one page)
        int headerBlocks = 1;
        byte[] headerBytes;
        long pixelOffset;
        while (true) {
            pixelOffset = 16 + headerBlocks * HeaderBlockSize;
            var xml = BuildXml(w, h, pixelOffset, uncompressedSize, dataBytes.LongLength,
                compressionAttr, colorSpace, imageType,
                meta, rotator, observerName, observatoryName, siteName, customKeywords);
            headerBytes = Encoding.UTF8.GetBytes(xml);
            var needed = headerBytes.Length;
            if (needed <= headerBlocks * HeaderBlockSize) break;
            headerBlocks = (needed + HeaderBlockSize - 1) / HeaderBlockSize;
        }

        // ---- 3. Write file ----
        // Signature
        destination.Write(Encoding.ASCII.GetBytes("XISF0100"));
        // Header length (uint32 LE)
        Span<byte> u32 = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)(headerBlocks * HeaderBlockSize));
        destination.Write(u32);
        // Reserved 4 bytes
        destination.Write(new byte[4]);
        // XML header, padded with spaces to block size
        destination.Write(headerBytes);
        var padLen = headerBlocks * HeaderBlockSize - headerBytes.Length;
        if (padLen > 0) {
            var pad = new byte[padLen];
            for (int i = 0; i < pad.Length; i++) pad[i] = 0x20; // space
            destination.Write(pad);
        }
        // Pixel data
        destination.Write(dataBytes);
    }

    private static string BuildXml(int width, int height, long pixelOffset,
        long uncompressedSize, long compressedSize, string? compressionAttr,
        string colorSpace, string imageType,
        NINA.Image.ImageData.ImageMetaData meta,
        RotatorMetaData? rotator,
        string? observerName, string? observatoryName, string? siteName,
        IEnumerable<KeyValuePair<string, string>>? customKeywords) {

        var sb = new StringBuilder();
        var settings = new XmlWriterSettings {
            Encoding = new UTF8Encoding(false), // no BOM
            Indent = false,
            OmitXmlDeclaration = false
        };
        using (var xw = XmlWriter.Create(sb, settings)) {
            xw.WriteStartDocument();
            xw.WriteStartElement("xisf", "http://www.pixinsight.com/xisf");
            xw.WriteAttributeString("version", "1.0");
            xw.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            xw.WriteAttributeString("xsi", "schemaLocation", null,
                "http://www.pixinsight.com/xisf http://pixinsight.com/xisf/xisf-1.0.xsd");

            xw.WriteStartElement("Image");
            xw.WriteAttributeString("geometry", $"{width}:{height}:1");
            xw.WriteAttributeString("sampleFormat", "UInt16");
            xw.WriteAttributeString("bounds", "0:65535");
            xw.WriteAttributeString("colorSpace", colorSpace);
            xw.WriteAttributeString("imageType", imageType);
            xw.WriteAttributeString("location", $"attachment:{pixelOffset}:{compressedSize}");
            if (compressionAttr != null) xw.WriteAttributeString("compression", compressionAttr);

            // Native XISF properties — observation timestamp
            var utc = meta.CreationTime.ToUniversalTime();
            WriteProperty(xw, "Observation:Time:Start", "TimePoint",
                utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));

            // Location (Observer namespace)
            if (Math.Abs(meta.Observer.Latitude) > 0.0001)
                WriteProperty(xw, "Observation:Location:Latitude", "Float64", Fmt(meta.Observer.Latitude));
            if (Math.Abs(meta.Observer.Longitude) > 0.0001)
                WriteProperty(xw, "Observation:Location:Longitude", "Float64", Fmt(meta.Observer.Longitude));
            if (meta.Observer.Elevation > 0)
                WriteProperty(xw, "Observation:Location:Elevation", "Float64", Fmt(meta.Observer.Elevation));
            if (!string.IsNullOrEmpty(observerName))
                WriteProperty(xw, "Observer:Name", "String", observerName);
            if (!string.IsNullOrEmpty(siteName))
                WriteProperty(xw, "Observation:Location:Name", "String", siteName);

            if (!string.IsNullOrEmpty(meta.Target.Name))
                WriteProperty(xw, "Observation:Object:Name", "String", meta.Target.Name);

            // Instrument namespace
            if (meta.Exposure.ExposureTime > 0)
                WriteProperty(xw, "Instrument:ExposureTime", "Float32", Fmt(meta.Exposure.ExposureTime));
            if (!string.IsNullOrEmpty(meta.Camera.Name))
                WriteProperty(xw, "Instrument:Camera:Name", "String", meta.Camera.Name);
            if (meta.Camera.Gain != 0)
                WriteProperty(xw, "Instrument:Camera:Gain", "Float32", meta.Camera.Gain.ToString(CultureInfo.InvariantCulture));
            if (meta.Camera.BinX > 0) {
                WriteProperty(xw, "Instrument:Camera:XBinning", "Int32", meta.Camera.BinX.ToString(CultureInfo.InvariantCulture));
                WriteProperty(xw, "Instrument:Camera:YBinning", "Int32", meta.Camera.BinY.ToString(CultureInfo.InvariantCulture));
            }
            if (meta.Camera.PixelSizeX > 0)
                WriteProperty(xw, "Instrument:Sensor:XPixelSize", "Float32", Fmt(meta.Camera.PixelSizeX));
            if (meta.Camera.PixelSizeY > 0)
                WriteProperty(xw, "Instrument:Sensor:YPixelSize", "Float32", Fmt(meta.Camera.PixelSizeY));
            if (Math.Abs(meta.Camera.Temperature) > 0.001)
                WriteProperty(xw, "Instrument:Sensor:Temperature", "Float32", Fmt(meta.Camera.Temperature));
            if (!string.IsNullOrEmpty(meta.Telescope.Name))
                WriteProperty(xw, "Instrument:Telescope:Name", "String", meta.Telescope.Name);
            if (meta.Telescope.FocalLength > 0)
                WriteProperty(xw, "Instrument:Telescope:FocalLength", "Float32", Fmt(meta.Telescope.FocalLength / 1000.0)); // meters per XISF spec
            if (!string.IsNullOrEmpty(meta.FilterWheel.Filter))
                WriteProperty(xw, "Instrument:Filter:Name", "String", meta.FilterWheel.Filter);

            // FITSKeyword child elements — mirror FITSWriter for max compatibility
            WriteFitsKeyword(xw, "IMAGETYP", imageType.ToUpperInvariant(), "Type of exposure");
            if (meta.Exposure.ExposureTime > 0) {
                WriteFitsKeyword(xw, "EXPOSURE", Fmt(meta.Exposure.ExposureTime), "Exposure (s)");
                WriteFitsKeyword(xw, "EXPTIME", Fmt(meta.Exposure.ExposureTime), "Exposure (s)");
            }
            WriteFitsKeyword(xw, "DATE-LOC",
                meta.CreationTime.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture),
                null);
            WriteFitsKeyword(xw, "DATE-UTC",
                utc.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture), null);

            if (!string.IsNullOrEmpty(meta.Camera.Name)) {
                WriteFitsKeyword(xw, "CAMERAID", meta.Camera.Name, null);
                WriteFitsKeyword(xw, "INSTRUME", meta.Camera.Name, null);
            }
            if (meta.Camera.BinX > 0) {
                WriteFitsKeyword(xw, "XBINNING", meta.Camera.BinX.ToString(CultureInfo.InvariantCulture), null);
                WriteFitsKeyword(xw, "YBINNING", meta.Camera.BinY.ToString(CultureInfo.InvariantCulture), null);
            }
            if (meta.Camera.Gain != 0)
                WriteFitsKeyword(xw, "GAIN", meta.Camera.Gain.ToString(CultureInfo.InvariantCulture), null);
            if (meta.Camera.Offset != 0)
                WriteFitsKeyword(xw, "OFFSET", meta.Camera.Offset.ToString(CultureInfo.InvariantCulture), null);
            if (meta.Camera.PixelSizeX > 0)
                WriteFitsKeyword(xw, "XPIXSZ", Fmt(meta.Camera.PixelSizeX), "Pixel size X (um)");
            if (meta.Camera.PixelSizeY > 0)
                WriteFitsKeyword(xw, "YPIXSZ", Fmt(meta.Camera.PixelSizeY), "Pixel size Y (um)");
            if (Math.Abs(meta.Camera.Temperature) > 0.001)
                WriteFitsKeyword(xw, "CCD-TEMP", Fmt(meta.Camera.Temperature), "Sensor temp (C)");
            if (meta.Camera.BayerPattern != BayerPatternEnum.None)
                WriteFitsKeyword(xw, "BAYERPAT", meta.Camera.BayerPattern.ToString().ToUpperInvariant(), null);

            if (!string.IsNullOrEmpty(meta.Telescope.Name))
                WriteFitsKeyword(xw, "TELESCOP", meta.Telescope.Name, null);
            if (meta.Telescope.FocalLength > 0)
                WriteFitsKeyword(xw, "FOCALLEN", Fmt(meta.Telescope.FocalLength), "Focal length (mm)");
            if (meta.Telescope.FocalRatio > 0)
                WriteFitsKeyword(xw, "FOCRATIO", Fmt(meta.Telescope.FocalRatio), "Focal ratio (f/N)");
            if (meta.Telescope.RightAscension != 0 || meta.Telescope.Declination != 0) {
                WriteFitsKeyword(xw, "RA", Fmt(meta.Telescope.RightAscension * 15.0), "Mount RA (deg)");
                WriteFitsKeyword(xw, "DEC", Fmt(meta.Telescope.Declination), "Mount Dec (deg)");
            }

            if (!string.IsNullOrEmpty(meta.FilterWheel.Name))
                WriteFitsKeyword(xw, "FWHEEL", meta.FilterWheel.Name, null);
            if (!string.IsNullOrEmpty(meta.FilterWheel.Filter))
                WriteFitsKeyword(xw, "FILTER", meta.FilterWheel.Filter, null);

            if (!string.IsNullOrEmpty(meta.Focuser.Name)) {
                WriteFitsKeyword(xw, "FOCNAME", meta.Focuser.Name, null);
                if (meta.Focuser.Position != 0)
                    WriteFitsKeyword(xw, "FOCPOS", meta.Focuser.Position.ToString(CultureInfo.InvariantCulture), null);
                if (Math.Abs(meta.Focuser.Temperature) > 0.001)
                    WriteFitsKeyword(xw, "FOCTEMP", Fmt(meta.Focuser.Temperature), null);
            }

            if (rotator != null && !string.IsNullOrEmpty(rotator.Name)) {
                WriteFitsKeyword(xw, "ROTNAME", rotator.Name, null);
                if (Math.Abs(rotator.Angle) > 0.001)
                    WriteFitsKeyword(xw, "ROTATANG", Fmt(rotator.Angle), null);
            }

            if (meta.Weather.Temperature != 0)
                WriteFitsKeyword(xw, "AMBTEMP", Fmt(meta.Weather.Temperature), null);
            if (meta.Weather.Humidity != 0)
                WriteFitsKeyword(xw, "HUMIDITY", Fmt(meta.Weather.Humidity), null);
            if (meta.Weather.DewPoint != 0)
                WriteFitsKeyword(xw, "DEWPOINT", Fmt(meta.Weather.DewPoint), null);
            if (meta.Weather.Pressure != 0)
                WriteFitsKeyword(xw, "PRESSURE", Fmt(meta.Weather.Pressure), null);

            if (!string.IsNullOrEmpty(observerName))
                WriteFitsKeyword(xw, "OBSERVER", observerName, null);
            if (!string.IsNullOrEmpty(observatoryName))
                WriteFitsKeyword(xw, "OBSERVAT", observatoryName, null);
            if (!string.IsNullOrEmpty(siteName))
                WriteFitsKeyword(xw, "SITENAME", siteName, null);
            if (Math.Abs(meta.Observer.Latitude) > 0.0001)
                WriteFitsKeyword(xw, "SITELAT", Fmt(meta.Observer.Latitude), null);
            if (Math.Abs(meta.Observer.Longitude) > 0.0001)
                WriteFitsKeyword(xw, "SITELONG", Fmt(meta.Observer.Longitude), null);

            if (!string.IsNullOrEmpty(meta.Target.Name)) {
                WriteFitsKeyword(xw, "OBJECT", meta.Target.Name, null);
                WriteFitsKeyword(xw, "OBJCTRA", Fmt(meta.Target.RightAscension * 15.0), null);
                WriteFitsKeyword(xw, "OBJCTDEC", Fmt(meta.Target.Declination), null);
            }

            if (customKeywords != null) {
                foreach (var kv in customKeywords) {
                    WriteFitsKeyword(xw, kv.Key, kv.Value, null);
                }
            }

            WriteFitsKeyword(xw, "SWCREATE", "NINA.Headless", null);

            xw.WriteEndElement(); // </Image>

            // <Metadata> block (XISF spec recommends it for the creator stamp)
            xw.WriteStartElement("Metadata");
            WriteProperty(xw, "XISF:CreationTime", "TimePoint",
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            WriteProperty(xw, "XISF:CreatorApplication", "String", "NINA.Headless");
            xw.WriteEndElement(); // </Metadata>

            xw.WriteEndElement(); // </xisf>
            xw.WriteEndDocument();
        }
        return sb.ToString();
    }

    private static void WriteProperty(XmlWriter xw, string id, string type, string value) {
        xw.WriteStartElement("Property");
        xw.WriteAttributeString("id", id);
        xw.WriteAttributeString("type", type);
        xw.WriteAttributeString("value", value);
        xw.WriteEndElement();
    }

    private static void WriteFitsKeyword(XmlWriter xw, string name, string value, string? comment) {
        xw.WriteStartElement("FITSKeyword");
        xw.WriteAttributeString("name", name);
        xw.WriteAttributeString("value", value);
        if (!string.IsNullOrEmpty(comment)) xw.WriteAttributeString("comment", comment);
        xw.WriteEndElement();
    }

    private static string Fmt(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Match XISF spec's imageType enum (Light / Bias / Dark / Flat / etc.).</summary>
    private static string NormaliseImageType(string raw) {
        var t = raw.Trim().ToLowerInvariant();
        return t switch {
            "light" => "Light",
            "dark" => "Dark",
            "bias" => "Bias",
            "flat" => "Flat",
            "darkflat" => "DarkFlat",
            "masterlight" => "MasterLight",
            "masterdark" => "MasterDark",
            "masterbias" => "MasterBias",
            "masterflat" => "MasterFlat",
            _ => "Light"
        };
    }
}

