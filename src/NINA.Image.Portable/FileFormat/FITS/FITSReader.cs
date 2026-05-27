using NINA.Core.Enum;
using NINA.Image.ImageData;

namespace NINA.Image.FileFormat.FITS;

public static class FITSReader {
    private const int BLOCK_SIZE = 2880;
    private const int CARD_SIZE = 80;

    public static BaseImageData Read(Stream stream) {
        var headers = ReadHeaders(stream);

        int bitpix = GetIntHeader(headers, "BITPIX", 16);
        int naxis = GetIntHeader(headers, "NAXIS", 2);
        int width = GetIntHeader(headers, "NAXIS1", 0);
        int height = GetIntHeader(headers, "NAXIS2", 0);
        // RGB cubes use NAXIS=3 + NAXIS3=3 (R/G/B planes). Anything
        // else collapses to a single plane (grayscale or just the
        // first plane of a multi-frame cube, close enough for v1).
        int planes = (naxis >= 3) ? GetIntHeader(headers, "NAXIS3", 1) : 1;
        if (planes != 1 && planes != 3) planes = 1;
        int bzero = GetIntHeader(headers, "BZERO", 0);
        double bscale = GetDoubleHeader(headers, "BSCALE", 1.0);
        string bayerPat = GetStringHeader(headers, "BAYERPAT", "");

        var bayerPattern = bayerPat.ToUpperInvariant() switch {
            "RGGB" => BayerPatternEnum.RGGB,
            "BGGR" => BayerPatternEnum.BGGR,
            "GBRG" => BayerPatternEnum.GBRG,
            "GRBG" => BayerPatternEnum.GRBG,
            _ => BayerPatternEnum.None
        };

        // Read the full buffer covering every plane. For grayscale this
        // is the existing width*height; for RGB it's 3× larger and
        // stored plane-sequentially (R first, then G, then B), the
        // FITS convention also used by PixInsight, Siril, and astropy.
        var pixels = ReadPixelData(stream, width, height * planes, bitpix, bzero, bscale);

        var props = new ImageProperties {
            Width = width,
            Height = height,
            BitDepth = Math.Abs(bitpix) > 16 ? 16 : Math.Abs(bitpix),
            IsBayered = bayerPattern != BayerPatternEnum.None,
            BayerPattern = bayerPattern,
            Channels = planes,
            // CCALB-0a: pick up WCS if the FITS was plate-solved and
            // re-stamped (AstapSolver does this after a successful
            // solve). Null when the source has no WCS block, which
            // is the common case for raw lights or un-solved masters.
            Wcs = WcsHeaders.Read(headers),
        };

        var metaData = ExtractMetaData(headers);
        return new BaseImageData(pixels, props, metaData);
    }

    public static BaseImageData Read(byte[] data) {
        using var ms = new MemoryStream(data);
        return Read(ms);
    }

    /// <summary>
    /// Read just the FITS header block, leaving the stream positioned
    /// at the start of the pixel data (which the caller is free to
    /// ignore). Used by the STUDIO frame index, parsing a 64 MB pixel
    /// block of every file just to read keywords is wasteful.
    /// </summary>
    public static Dictionary<string, FITSHeaderCard> ReadHeadersOnly(Stream stream) {
        return ReadHeaders(stream);
    }

    private static Dictionary<string, FITSHeaderCard> ReadHeaders(Stream stream) {
        var headers = new Dictionary<string, FITSHeaderCard>(StringComparer.OrdinalIgnoreCase);
        var block = new byte[BLOCK_SIZE];
        bool endFound = false;

        while (!endFound) {
            int bytesRead = stream.Read(block, 0, BLOCK_SIZE);
            if (bytesRead < BLOCK_SIZE) break;

            for (int i = 0; i < BLOCK_SIZE; i += CARD_SIZE) {
                var card = FITSHeaderCard.Parse(block.AsSpan(i, CARD_SIZE));
                if (card == null) continue;
                if (card.Keyword == "END") {
                    endFound = true;
                    break;
                }
                headers[card.Keyword] = card;
            }
        }

        return headers;
    }

    private static ushort[] ReadPixelData(Stream stream, int width, int height, int bitpix, int bzero, double bscale) {
        long pixelCount = (long)width * height;
        var pixels = new ushort[pixelCount];

        int bytesPerPixel = Math.Abs(bitpix) / 8;
        var rawData = new byte[pixelCount * bytesPerPixel];

        int totalRead = 0;
        while (totalRead < rawData.Length) {
            int read = stream.Read(rawData, totalRead, rawData.Length - totalRead);
            if (read == 0) break;
            totalRead += read;
        }

        switch (bitpix) {
            case 8:
                for (int i = 0; i < pixelCount; i++) {
                    pixels[i] = (ushort)(rawData[i] * bscale + bzero);
                }
                break;
            case 16:
                for (int i = 0; i < pixelCount; i++) {
                    short val = (short)((rawData[i * 2] << 8) | rawData[i * 2 + 1]); // big-endian
                    pixels[i] = (ushort)(val * bscale + bzero);
                }
                break;
            case 32:
                for (int i = 0; i < pixelCount; i++) {
                    int val = (rawData[i * 4] << 24) | (rawData[i * 4 + 1] << 16) |
                              (rawData[i * 4 + 2] << 8) | rawData[i * 4 + 3];
                    double scaled = val * bscale + bzero;
                    pixels[i] = (ushort)Math.Clamp(scaled, 0, 65535);
                }
                break;
            case -32: // IEEE single-precision float
                ReadFloatPixels(rawData, pixels, pixelCount, bzero, bscale, bytesPerSample: 4);
                break;
            case -64: // IEEE double-precision float
                ReadFloatPixels(rawData, pixels, pixelCount, bzero, bscale, bytesPerSample: 8);
                break;
        }

        return pixels;
    }

    /// <summary>
    /// Read float pixel data and auto-scale to the ushort range. Float
    /// FITS files arrive in two distinct conventions and we don't get
    /// to know which up front:
    ///   - Normalised stacks (PixInsight, Siril) store values in
    ///     [0.0, 1.0]. A naive `(ushort)val` clamps every pixel to 0
    ///     and renders the whole image black, the regression that
    ///     surfaced first when opening a stacked master from the
    ///     FILES tab.
    ///   - Unscaled integer-to-float conversions store values in
    ///     roughly [0, 65535] and the naive cast happens to work.
    /// The fix is to scan the observed min/max in a first pass and
    /// linearly remap to [0, 65535] in a second pass. AutoStretch later
    /// applies the usual MTF on top, so non-linear curves in the source
    /// (HDR composites with a long tail) still display correctly.
    /// NaN / infinity pixels (very common in stacks where the rejection
    /// killed every contributing frame) are treated as zero, both for
    /// the range scan and the final write.
    /// </summary>
    private static void ReadFloatPixels(byte[] rawData, ushort[] pixels, long pixelCount,
                                        int bzero, double bscale, int bytesPerSample) {
        // First pass: gather a tight min/max over finite samples only.
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (long i = 0; i < pixelCount; i++) {
            double val = ReadFloatAt(rawData, i * bytesPerSample, bytesPerSample);
            val = val * bscale + bzero;
            if (!double.IsFinite(val)) continue;
            if (val < min) min = val;
            if (val > max) max = val;
        }
        if (!double.IsFinite(min) || !double.IsFinite(max) || max <= min) {
            // Degenerate input: constant or all-NaN buffer. Output stays
            // zero, there's nothing meaningful to show anyway.
            Array.Clear(pixels, 0, pixels.Length);
            return;
        }

        double range = max - min;
        double scale65k = 65535.0 / range;

        // Second pass: rescale + write. We could fold these into one
        // loop but the two-pass form is clearer and the extra walk is
        // negligible against the I/O cost.
        for (long i = 0; i < pixelCount; i++) {
            double val = ReadFloatAt(rawData, i * bytesPerSample, bytesPerSample);
            val = val * bscale + bzero;
            if (!double.IsFinite(val)) { pixels[i] = 0; continue; }
            double mapped = (val - min) * scale65k;
            // Clamp guards against floating-point noise that nudges the
            // top end one ULP past max.
            pixels[i] = (ushort)Math.Clamp(mapped, 0.0, 65535.0);
        }
    }

    /// <summary>
    /// FITS stores floats and doubles in big-endian byte order. .NET's
    /// BitConverter is host-endian, so reverse the bytes before decoding.
    /// </summary>
    private static double ReadFloatAt(byte[] data, long offset, int bytesPerSample) {
        if (bytesPerSample == 4) {
            Span<byte> bytes = stackalloc byte[4] {
                data[offset + 3], data[offset + 2], data[offset + 1], data[offset]
            };
            return BitConverter.ToSingle(bytes);
        } else {
            Span<byte> bytes = stackalloc byte[8] {
                data[offset + 7], data[offset + 6], data[offset + 5], data[offset + 4],
                data[offset + 3], data[offset + 2], data[offset + 1], data[offset]
            };
            return BitConverter.ToDouble(bytes);
        }
    }

    private static ImageMetaData ExtractMetaData(Dictionary<string, FITSHeaderCard> headers) {
        var meta = new ImageMetaData();

        meta.Camera.Name = GetStringHeader(headers, "INSTRUME", "");
        meta.Camera.Temperature = GetDoubleHeader(headers, "CCD-TEMP", 0);
        meta.Camera.Gain = GetIntHeader(headers, "GAIN", 0);
        meta.Camera.Offset = GetIntHeader(headers, "OFFSET", 0);
        meta.Camera.BinX = (short)GetIntHeader(headers, "XBINNING", 1);
        meta.Camera.BinY = (short)GetIntHeader(headers, "YBINNING", 1);
        meta.Camera.PixelSizeX = GetDoubleHeader(headers, "XPIXSZ", 0);
        meta.Camera.PixelSizeY = GetDoubleHeader(headers, "YPIXSZ", 0);

        meta.Telescope.Name = GetStringHeader(headers, "TELESCOP", "");
        meta.Telescope.FocalLength = GetDoubleHeader(headers, "FOCALLEN", 0);
        meta.Telescope.RightAscension = GetDoubleHeader(headers, "RA", 0);
        meta.Telescope.Declination = GetDoubleHeader(headers, "DEC", 0);

        meta.Observer.Latitude = GetDoubleHeader(headers, "SITELAT", 0);
        meta.Observer.Longitude = GetDoubleHeader(headers, "SITELONG", 0);
        meta.Observer.Elevation = GetDoubleHeader(headers, "SITEELEV", 0);

        meta.Target.Name = GetStringHeader(headers, "OBJECT", "");
        meta.Exposure.ExposureTime = GetDoubleHeader(headers, "EXPTIME", 0);
        meta.Exposure.Filter = GetStringHeader(headers, "FILTER", "");
        meta.Exposure.ImageType = GetStringHeader(headers, "IMAGETYP", "LIGHT");

        // FITSWriter reads FILTER from meta.FilterWheel.Filter (live
        // capture path populates that field), so mirror the FILTER
        // header into both buckets here. Without this the read/write
        // roundtrip used by CalibrationService / MasterFrameService /
        // BatchStackingService silently drops the filter tag.
        meta.FilterWheel.Filter = meta.Exposure.Filter;
        meta.FilterWheel.Name = GetStringHeader(headers, "FWHEEL",
            meta.FilterWheel.Name);

        return meta;
    }

    private static int GetIntHeader(Dictionary<string, FITSHeaderCard> headers, string key, int defaultValue) {
        if (headers.TryGetValue(key, out var card) && int.TryParse(card.Value, out int val)) return val;
        return defaultValue;
    }

    private static double GetDoubleHeader(Dictionary<string, FITSHeaderCard> headers, string key, double defaultValue) {
        if (headers.TryGetValue(key, out var card) && double.TryParse(card.Value,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double val))
            return val;
        return defaultValue;
    }

    private static string GetStringHeader(Dictionary<string, FITSHeaderCard> headers, string key, string defaultValue) {
        if (headers.TryGetValue(key, out var card)) return card.Value;
        return defaultValue;
    }
}
