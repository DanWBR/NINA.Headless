using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using NUnit.Framework;
using NINA.Core.Enum;
using NINA.Image.FileFormat.FITS;
using NINA.Image.FileFormat.XISF;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Headless.Test;

[TestFixture]
public class XISFWriterTests {
    private string _tempDir = null!;

    [SetUp]
    public void SetUp() {
        _tempDir = Path.Combine(Path.GetTempPath(), "NinaXisfTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown() {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    private static FakeImageData MakeImage(int w = 32, int h = 24, ushort fill = 1000) {
        var pixels = new ushort[w * h];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = fill;
        return new FakeImageData(pixels, w, h);
    }

    private static (string xml, byte[] dataBlock) ReadXisf(string path) {
        var bytes = File.ReadAllBytes(path);
        // Signature
        Assert.That(Encoding.ASCII.GetString(bytes, 0, 8), Is.EqualTo("XISF0100"));
        // Header length (LE uint32)
        var hdrLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        var xml = Encoding.UTF8.GetString(bytes, 16, hdrLen).TrimEnd((char)0x20, '\0');
        var dataOffset = 16 + hdrLen;
        var data = bytes[dataOffset..];
        return (xml, data);
    }

    [Test]
    public void Write_ProducesValidXisfSignature() {
        var img = MakeImage();
        var path = Path.Combine(_tempDir, "sig.xisf");

        XISFWriter.Write(img, path, compress: false);

        var bytes = File.ReadAllBytes(path);
        Assert.That(bytes.Length, Is.GreaterThan(16));
        Assert.That(Encoding.ASCII.GetString(bytes, 0, 8), Is.EqualTo("XISF0100"));
        // Reserved 4 bytes should be zero
        Assert.That(bytes[12], Is.EqualTo(0));
        Assert.That(bytes[13], Is.EqualTo(0));
        Assert.That(bytes[14], Is.EqualTo(0));
        Assert.That(bytes[15], Is.EqualTo(0));
    }

    [Test]
    public void Write_XmlHeaderIsValidAndDescribesImage() {
        var img = MakeImage(64, 48);
        var path = Path.Combine(_tempDir, "valid.xisf");

        XISFWriter.Write(img, path, compress: false);

        var (xml, _) = ReadXisf(path);
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://www.pixinsight.com/xisf";
        var root = doc.Root;

        Assert.That(root, Is.Not.Null);
        Assert.That(root!.Name.LocalName, Is.EqualTo("xisf"));
        Assert.That(root.Attribute("version")?.Value, Is.EqualTo("1.0"));

        var image = root.Element(ns + "Image");
        Assert.That(image, Is.Not.Null);
        Assert.That(image!.Attribute("geometry")?.Value, Is.EqualTo("64:48:1"));
        Assert.That(image.Attribute("sampleFormat")?.Value, Is.EqualTo("UInt16"));
        Assert.That(image.Attribute("colorSpace")?.Value, Is.EqualTo("Gray"));
        Assert.That(image.Attribute("location")?.Value, Does.StartWith("attachment:"));
    }

    [Test]
    public void Write_UncompressedDataBlock_MatchesPixels() {
        var img = MakeImage(8, 8, fill: 0x1234);
        var path = Path.Combine(_tempDir, "raw.xisf");

        XISFWriter.Write(img, path, compress: false);

        var (xml, dataBlock) = ReadXisf(path);
        Assert.That(xml, Does.Not.Contain("compression="));
        // First two bytes should be 0x34, 0x12 (little-endian 0x1234)
        Assert.That(dataBlock[0], Is.EqualTo(0x34));
        Assert.That(dataBlock[1], Is.EqualTo(0x12));
        Assert.That(dataBlock.Length, Is.GreaterThanOrEqualTo(8 * 8 * 2));
    }

    [Test]
    public void Write_CompressedDataBlock_DeclaresLz4Attribute() {
        var img = MakeImage(64, 64, fill: 0);
        var path = Path.Combine(_tempDir, "lz4.xisf");

        XISFWriter.Write(img, path, compress: true);

        var (xml, dataBlock) = ReadXisf(path);
        Assert.That(xml, Does.Contain("compression=\"lz4:"));
        Assert.That(xml, Does.Contain($"compression=\"lz4:{64 * 64 * 2}\""));
        // Compressed should be smaller than raw 8KB for a buffer of all zeros
        Assert.That(dataBlock.Length, Is.LessThan(64 * 64 * 2));
    }

    [Test]
    public void Write_PixelOffset_AlignsToHeaderBlockBoundary() {
        var img = MakeImage();
        var path = Path.Combine(_tempDir, "aligned.xisf");

        XISFWriter.Write(img, path, compress: false);

        var (xml, _) = ReadXisf(path);
        // The location attribute encodes the byte offset where data starts
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://www.pixinsight.com/xisf";
        var loc = doc.Root!.Element(ns + "Image")!.Attribute("location")!.Value;
        var offset = int.Parse(loc.Split(':')[1]);
        // 16 bytes preamble + N * 4096 block header → offset must be (16 + 4096k)
        Assert.That(offset - 16, Is.GreaterThan(0));
        Assert.That((offset - 16) % 4096, Is.EqualTo(0));
    }

    [Test]
    public void Write_MetadataFieldsAppearAsFitsKeywords() {
        var img = MakeImage();
        img.MetaData.Camera.Name = "ZWO ASI2600MC";
        img.MetaData.Camera.Gain = 100;
        img.MetaData.Camera.BinX = 2;
        img.MetaData.Camera.BinY = 2;
        img.MetaData.Camera.Temperature = -10.5;
        img.MetaData.Telescope.Name = "WO GT81";
        img.MetaData.Telescope.FocalLength = 478;
        img.MetaData.Exposure.ExposureTime = 30;
        img.MetaData.Exposure.ImageType = "LIGHT";
        img.MetaData.Target.Name = "M31";
        img.MetaData.Observer.Latitude = -23.55;
        img.MetaData.Observer.Longitude = -46.63;

        var path = Path.Combine(_tempDir, "meta.xisf");
        XISFWriter.Write(img, path, compress: false);

        var (xml, _) = ReadXisf(path);
        Assert.That(xml, Does.Contain("CAMERAID"));
        Assert.That(xml, Does.Contain("ZWO ASI2600MC"));
        Assert.That(xml, Does.Contain("GAIN"));
        Assert.That(xml, Does.Contain("XBINNING"));
        Assert.That(xml, Does.Contain("CCD-TEMP"));
        Assert.That(xml, Does.Contain("TELESCOP"));
        Assert.That(xml, Does.Contain("FOCALLEN"));
        Assert.That(xml, Does.Contain("EXPTIME"));
        Assert.That(xml, Does.Contain("OBJECT"));
        Assert.That(xml, Does.Contain("M31"));
        Assert.That(xml, Does.Contain("SITELAT"));
        Assert.That(xml, Does.Contain("SITELONG"));
    }

    [Test]
    public void Write_BayerPattern_EmitsBayerKeyword() {
        var img = MakeImage();
        img.MetaData.Camera.BayerPattern = BayerPatternEnum.RGGB;
        var path = Path.Combine(_tempDir, "bayer.xisf");
        XISFWriter.Write(img, path, compress: false);

        var (xml, _) = ReadXisf(path);
        Assert.That(xml, Does.Contain("BAYERPAT"));
        Assert.That(xml, Does.Contain("RGGB"));
    }

    [Test]
    public void Write_NormalisesImageType() {
        var img = MakeImage();
        img.MetaData.Exposure.ImageType = "flat";
        var path = Path.Combine(_tempDir, "flat.xisf");
        XISFWriter.Write(img, path, compress: false);

        var (xml, _) = ReadXisf(path);
        Assert.That(xml, Does.Contain("imageType=\"Flat\""));
    }

    [Test]
    public void Write_LargeHeader_AutoExpandsBlockCount() {
        // Force a header that overflows one 4096 block by stuffing many custom keywords
        var img = MakeImage();
        var custom = new List<KeyValuePair<string, string>>();
        for (int i = 0; i < 200; i++) {
            custom.Add(new KeyValuePair<string, string>($"CUSTOM{i:000}",
                new string('X', 40)));
        }
        var path = Path.Combine(_tempDir, "big.xisf");

        XISFWriter.Write(img, path, customKeywords: custom, compress: false);

        var (xml, _) = ReadXisf(path);
        Assert.That(xml.Length, Is.GreaterThan(4096));
        var doc = XDocument.Parse(xml);   // must parse cleanly
        Assert.That(doc.Root, Is.Not.Null);
    }

    // Minimal fake to avoid pulling in IImageData implementations
    private class FakeImageData : IImageData {
        public ushort[] Data { get; }
        public ImageProperties Properties { get; }
        public ImageMetaData MetaData { get; } = new();
        public IImageStatistics Statistics => null!;
        public FakeImageData(ushort[] data, int w, int h) {
            Data = data;
            Properties = new ImageProperties { Width = w, Height = h, BitDepth = 16 };
        }
    }
}
