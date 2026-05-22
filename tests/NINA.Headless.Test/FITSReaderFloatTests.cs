using NUnit.Framework;
using NINA.Image.FileFormat.FITS;

namespace NINA.Headless.Test;

/// <summary>
/// Pins the float-FITS decode path. PixInsight and Siril both store
/// stacked masters as IEEE single-precision (BITPIX=-32) with pixel
/// values in [0.0, 1.0]. An earlier version of <see cref="FITSReader"/>
/// cast each float directly to ushort, which truncated the entire
/// image to zero and rendered as a black canvas in the FILES preview.
///
/// The current implementation scans observed min/max and linearly
/// remaps the buffer into [0, 65535] so the auto-stretch downstream
/// has a usable signal regardless of which float convention the file
/// uses.
/// </summary>
[TestFixture]
public class FITSReaderFloatTests {

    [Test]
    public void Read_Float32_NormalisedRange_RemapsToFullUshortRange() {
        // 4×4 image, BITPIX=-32, values 0.0, 0.25, 0.5, 0.75, 1.0 spread
        // across the buffer. Without the rescale fix every pixel would
        // truncate to 0 and the asserts on max would fail.
        var fits = BuildFloatFits(4, 4, new float[] {
            0.0f,  0.25f, 0.5f,  0.75f,
            1.0f,  0.5f,  0.25f, 0.0f,
            0.75f, 0.5f,  0.25f, 1.0f,
            0.0f,  1.0f,  0.5f,  0.25f
        });

        var img = FITSReader.Read(new MemoryStream(fits));

        Assert.That(img.Properties.Width, Is.EqualTo(4));
        Assert.That(img.Properties.Height, Is.EqualTo(4));

        ushort min = ushort.MaxValue, max = 0;
        foreach (var p in img.Data) {
            if (p < min) min = p;
            if (p > max) max = p;
        }
        Assert.That(min, Is.EqualTo(0), "min should remap to zero");
        Assert.That(max, Is.EqualTo(65535), "max should remap to ushort.MaxValue");

        // The 0.5 sample should land near the middle of the output range
        // (linear remap of [0..1] → [0..65535]).
        Assert.That(img.Data[2], Is.InRange(32500, 33000),
            "0.5 sample should be near the middle after remap");
    }

    [Test]
    public void Read_Float32_NonNormalisedRange_StillRemapsCorrectly() {
        // ADU-range floats (think: integer-to-float export). Should
        // remap the same way — the auto-scale doesn't care what the
        // source range was, only that there's a finite [min, max].
        var fits = BuildFloatFits(2, 2, new float[] { 100f, 1000f, 5000f, 65535f });

        var img = FITSReader.Read(new MemoryStream(fits));

        Assert.That(img.Data[0], Is.EqualTo(0), "min sample → 0");
        Assert.That(img.Data[3], Is.EqualTo(65535), "max sample → 65535");
    }

    [Test]
    public void Read_Float32_AllConstant_ReturnsZeroBuffer() {
        // Constant buffer = zero range = nothing to scale into. The
        // sensible answer is all zeros, not a divide-by-zero crash.
        var fits = BuildFloatFits(2, 2, new float[] { 0.42f, 0.42f, 0.42f, 0.42f });

        var img = FITSReader.Read(new MemoryStream(fits));

        Assert.That(img.Data, Is.All.EqualTo((ushort)0));
    }

    [Test]
    public void Read_Float32_WithNaN_NaNsBecomeZeroAndDontCorruptRange() {
        // NaNs are common in stacks (rejection killed every contributor
        // for a hot pixel). They must not influence the range scan.
        var fits = BuildFloatFits(2, 2, new float[] {
            float.NaN, 0.5f,
            1.0f,      0.0f
        });

        var img = FITSReader.Read(new MemoryStream(fits));

        Assert.That(img.Data[0], Is.EqualTo(0), "NaN → 0");
        Assert.That(img.Data[3], Is.EqualTo(0), "min finite (0.0) → 0");
        Assert.That(img.Data[2], Is.EqualTo(65535), "max finite (1.0) → 65535");
    }

    // --- NAXIS=3 RGB cube --------------------------------------------

    [Test]
    public void Read_Float32_RgbCube_PopulatesAllThreeChannels() {
        // NAXIS=3, NAXIS3=3, 2×2 image. Plane order is R, G, B.
        // Auto-rescale runs across the whole buffer so a single global
        // [min, max] preserves channel balance — verify that here by
        // checking the relative ordering of the brightest sample in
        // each plane survives.
        var fits = BuildFloatRgbFits(2, 2, new float[] {
            // R plane (dim)
            0.1f, 0.2f, 0.3f, 0.4f,
            // G plane (mid)
            0.4f, 0.5f, 0.6f, 0.7f,
            // B plane (bright — pins the max)
            0.7f, 0.8f, 0.9f, 1.0f
        });

        var img = FITSReader.Read(new MemoryStream(fits));

        Assert.That(img.Properties.Width, Is.EqualTo(2));
        Assert.That(img.Properties.Height, Is.EqualTo(2));
        Assert.That(img.Properties.Channels, Is.EqualTo(3));
        Assert.That(img.Properties.IsColor, Is.True);
        Assert.That(img.Data.Length, Is.EqualTo(12), "buffer must hold all 3 planes");

        // Global remap: 0.1 → 0, 1.0 → 65535. So the R plane (max 0.4)
        // stays comparatively dim and the B plane (max 1.0) hits the
        // top. The relative ordering is the bit we care about for the
        // thumbnailer to render real colour.
        int rMax = 0, gMax = 0, bMax = 0;
        for (int i = 0; i < 4; i++) {
            if (img.Data[0 + i] > rMax) rMax = img.Data[0 + i];
            if (img.Data[4 + i] > gMax) gMax = img.Data[4 + i];
            if (img.Data[8 + i] > bMax) bMax = img.Data[8 + i];
        }
        Assert.That(rMax, Is.LessThan(gMax), "R plane should peak below G plane after global remap");
        Assert.That(gMax, Is.LessThan(bMax), "G plane should peak below B plane after global remap");
        Assert.That(bMax, Is.EqualTo(65535), "B plane's max sample (1.0) should hit the top");
    }

    [Test]
    public void Read_Float32_Naxis2_ChannelsDefaultsToOne() {
        // Make sure the new NAXIS=3 path doesn't break the existing
        // mono pipeline. NAXIS=2 must still produce a single-plane
        // buffer with Channels=1.
        var fits = BuildFloatFits(2, 2, new float[] { 0f, 0.5f, 0.5f, 1f });
        var img = FITSReader.Read(new MemoryStream(fits));
        Assert.That(img.Properties.Channels, Is.EqualTo(1));
        Assert.That(img.Properties.IsColor, Is.False);
        Assert.That(img.Data.Length, Is.EqualTo(4));
    }

    // --- Helpers ----------------------------------------------------

    /// <summary>
    /// Build a minimal, valid FITS byte stream for a single image HDU
    /// with the given float pixel buffer. Headers are padded with
    /// spaces to 80 chars; the header block + data block are each
    /// padded to 2880-byte multiples per the spec.
    /// </summary>
    private static byte[] BuildFloatFits(int width, int height, float[] pixels) {
        Assert.That(pixels.Length, Is.EqualTo(width * height),
            "pixel buffer size must equal width*height");

        var headerCards = new List<string> {
            FormatCard("SIMPLE", "T"),
            FormatCard("BITPIX", "-32"),
            FormatCard("NAXIS",  "2"),
            FormatCard("NAXIS1", width.ToString()),
            FormatCard("NAXIS2", height.ToString()),
            new string(' ', 80 - 3) + "END",     // "END" right-padded, but
        };
        // Replace the placeholder END card with a proper one (keyword left-aligned).
        headerCards[^1] = "END" + new string(' ', 77);

        var headerBlock = new byte[2880];
        for (int i = 0; i < headerCards.Count; i++) {
            var bytes = System.Text.Encoding.ASCII.GetBytes(headerCards[i]);
            Array.Copy(bytes, 0, headerBlock, i * 80, Math.Min(80, bytes.Length));
        }
        // Fill remainder of header block with spaces.
        for (int i = headerCards.Count * 80; i < 2880; i++) headerBlock[i] = (byte)' ';

        // Pixel data: big-endian floats, padded to 2880-byte multiple.
        int dataBytes = pixels.Length * 4;
        int paddedDataBytes = ((dataBytes + 2879) / 2880) * 2880;
        var dataBlock = new byte[paddedDataBytes];
        for (int i = 0; i < pixels.Length; i++) {
            var le = BitConverter.GetBytes(pixels[i]);   // little-endian on Intel
            dataBlock[i * 4 + 0] = le[3];
            dataBlock[i * 4 + 1] = le[2];
            dataBlock[i * 4 + 2] = le[1];
            dataBlock[i * 4 + 3] = le[0];
        }

        var combined = new byte[headerBlock.Length + dataBlock.Length];
        Buffer.BlockCopy(headerBlock, 0, combined, 0, headerBlock.Length);
        Buffer.BlockCopy(dataBlock,   0, combined, headerBlock.Length, dataBlock.Length);
        return combined;
    }

    /// <summary>
    /// Same as BuildFloatFits but emits NAXIS=3 with NAXIS3=3. Pixels
    /// are plane-sequential: first all of R, then all of G, then all
    /// of B. Each plane is <paramref name="width"/>×<paramref name="height"/>
    /// long, so the buffer is 3× the size.
    /// </summary>
    private static byte[] BuildFloatRgbFits(int width, int height, float[] pixels) {
        Assert.That(pixels.Length, Is.EqualTo(width * height * 3),
            "pixel buffer must be width*height*3 for a 3-plane cube");

        var headerCards = new List<string> {
            FormatCard("SIMPLE", "T"),
            FormatCard("BITPIX", "-32"),
            FormatCard("NAXIS",  "3"),
            FormatCard("NAXIS1", width.ToString()),
            FormatCard("NAXIS2", height.ToString()),
            FormatCard("NAXIS3", "3"),
            "END" + new string(' ', 77)
        };

        var headerBlock = new byte[2880];
        for (int i = 0; i < headerCards.Count; i++) {
            var bytes = System.Text.Encoding.ASCII.GetBytes(headerCards[i]);
            Array.Copy(bytes, 0, headerBlock, i * 80, Math.Min(80, bytes.Length));
        }
        for (int i = headerCards.Count * 80; i < 2880; i++) headerBlock[i] = (byte)' ';

        int dataBytes = pixels.Length * 4;
        int paddedDataBytes = ((dataBytes + 2879) / 2880) * 2880;
        var dataBlock = new byte[paddedDataBytes];
        for (int i = 0; i < pixels.Length; i++) {
            var le = BitConverter.GetBytes(pixels[i]);
            dataBlock[i * 4 + 0] = le[3];
            dataBlock[i * 4 + 1] = le[2];
            dataBlock[i * 4 + 2] = le[1];
            dataBlock[i * 4 + 3] = le[0];
        }

        var combined = new byte[headerBlock.Length + dataBlock.Length];
        Buffer.BlockCopy(headerBlock, 0, combined, 0, headerBlock.Length);
        Buffer.BlockCopy(dataBlock,   0, combined, headerBlock.Length, dataBlock.Length);
        return combined;
    }

    private static string FormatCard(string keyword, string value) {
        // FITS card: 8-char keyword, "= ", 20-char right-justified value,
        // padded to 80 chars total.
        var kw = keyword.PadRight(8);
        var val = value.PadLeft(20);
        return (kw + "= " + val).PadRight(80);
    }
}
