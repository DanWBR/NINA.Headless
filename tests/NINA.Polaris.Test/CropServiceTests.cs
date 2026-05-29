using Microsoft.Extensions.Logging.Abstractions;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the CropService contract. Every test writes a synthetic FITS
/// to a temp dir, crops it, reads the result, and asserts on pixel
/// values + dimensions. No SkiaSharp involved — pure FITSReader /
/// FITSWriter / Array.Copy.
/// </summary>
[TestFixture]
public class CropServiceTests {

    private string _tmpDir = "";
    private CropService _svc = null!;

    [SetUp]
    public void SetUp() {
        _tmpDir = Path.Combine(Path.GetTempPath(), "polaris-crop-" + Guid.NewGuid());
        Directory.CreateDirectory(_tmpDir);
        _svc = new CropService(NullLogger<CropService>.Instance);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    [Test]
    public void CropFits_Mono_ProducesExpectedRegion() {
        // 10x10 frame where pixel value = y*10 + x. Crop to (3,4,4,3)
        // should give a 4x3 frame where output[r][c] = (4+r)*10 + (3+c).
        var src = MakeMono(10, 10, (x, y) => (ushort)(y * 10 + x));
        var path = WriteFits(src, "src.fits");

        var r = _svc.CropFits(path, x: 3, y: 4, width: 4, height: 3);

        Assert.That(r.Width, Is.EqualTo(4));
        Assert.That(r.Height, Is.EqualTo(3));
        Assert.That(r.Channels, Is.EqualTo(1));
        Assert.That(File.Exists(r.OutputPath), Is.True);
        Assert.That(r.OutputPath.EndsWith("_crop.fits"), Is.True);

        var got = ReadFits(r.OutputPath);
        Assert.That(got.Properties.Width, Is.EqualTo(4));
        Assert.That(got.Properties.Height, Is.EqualTo(3));
        for (int row = 0; row < 3; row++) {
            for (int col = 0; col < 4; col++) {
                var expected = (ushort)((4 + row) * 10 + (3 + col));
                Assert.That(got.Data[row * 4 + col], Is.EqualTo(expected),
                    $"mismatch at ({col},{row})");
            }
        }
    }

    [Test]
    public void CropFits_Rgb_PreservesPlanes() {
        // 8x8 RGB: plane 0 = 100 + y*8 + x, plane 1 = 200 + ..., plane 2 = 300 + ...
        var src = MakeRgb(8, 8,
            (x, y) => (ushort)(100 + y * 8 + x),
            (x, y) => (ushort)(200 + y * 8 + x),
            (x, y) => (ushort)(300 + y * 8 + x));
        var path = WriteFits(src, "rgb.fits");

        var r = _svc.CropFits(path, x: 2, y: 1, width: 3, height: 2);

        Assert.That(r.Channels, Is.EqualTo(3));
        var got = ReadFits(r.OutputPath);
        Assert.That(got.Properties.Width, Is.EqualTo(3));
        Assert.That(got.Properties.Height, Is.EqualTo(2));
        Assert.That(got.Properties.Channels, Is.EqualTo(3));

        int outPlane = 3 * 2;
        for (int row = 0; row < 2; row++) {
            for (int col = 0; col < 3; col++) {
                int srcY = 1 + row, srcX = 2 + col;
                int srcLinear = srcY * 8 + srcX;
                int dstLinear = row * 3 + col;
                Assert.That(got.Data[dstLinear],
                    Is.EqualTo((ushort)(100 + srcLinear)),
                    $"R plane mismatch at ({col},{row})");
                Assert.That(got.Data[outPlane + dstLinear],
                    Is.EqualTo((ushort)(200 + srcLinear)),
                    $"G plane mismatch at ({col},{row})");
                Assert.That(got.Data[2 * outPlane + dstLinear],
                    Is.EqualTo((ushort)(300 + srcLinear)),
                    $"B plane mismatch at ({col},{row})");
            }
        }
    }

    [Test]
    public void CropFits_OutOfBounds_Throws() {
        var src = MakeMono(10, 10, (x, y) => 0);
        var path = WriteFits(src, "src.fits");

        Assert.Throws<ArgumentException>(() =>
            _svc.CropFits(path, x: 5, y: 5, width: 10, height: 10));
        Assert.Throws<ArgumentException>(() =>
            _svc.CropFits(path, x: -1, y: 0, width: 4, height: 4));
    }

    [Test]
    public void CropFits_ZeroSize_Throws() {
        var src = MakeMono(10, 10, (x, y) => 0);
        var path = WriteFits(src, "src.fits");

        Assert.Throws<ArgumentException>(() =>
            _svc.CropFits(path, x: 0, y: 0, width: 0, height: 5));
    }

    [Test]
    public void CropFits_MissingFile_Throws() {
        Assert.Throws<FileNotFoundException>(() =>
            _svc.CropFits(Path.Combine(_tmpDir, "nope.fits"),
                x: 0, y: 0, width: 10, height: 10));
    }

    [Test]
    public void CropFits_FullImage_RoundTrips() {
        // Crop to the same size as the source — output should match
        // pixel-for-pixel. Acts as a regression check on the slicing
        // math (off-by-one in row stride would break this immediately).
        var src = MakeMono(16, 16, (x, y) => (ushort)(x * 257 + y));
        var path = WriteFits(src, "full.fits");

        var r = _svc.CropFits(path, x: 0, y: 0, width: 16, height: 16);
        var got = ReadFits(r.OutputPath);

        Assert.That(got.Properties.Width, Is.EqualTo(16));
        Assert.That(got.Properties.Height, Is.EqualTo(16));
        Assert.That(got.Data.Length, Is.EqualTo(16 * 16));
        for (int i = 0; i < 16 * 16; i++) {
            Assert.That(got.Data[i], Is.EqualTo(src.Data[i]),
                $"pixel {i} round-trip mismatch");
        }
    }

    // ---- helpers ----------------------------------------------------

    private static BaseImageData MakeMono(int w, int h, Func<int, int, ushort> pixel) {
        var data = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                data[y * w + x] = pixel(x, y);
        var props = new ImageProperties {
            Width = w, Height = h, BitDepth = 16, Channels = 1
        };
        return new BaseImageData(data, props);
    }

    private static BaseImageData MakeRgb(int w, int h,
            Func<int, int, ushort> r, Func<int, int, ushort> g, Func<int, int, ushort> b) {
        var data = new ushort[w * h * 3];
        int plane = w * h;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                data[y * w + x] = r(x, y);
                data[plane + y * w + x] = g(x, y);
                data[2 * plane + y * w + x] = b(x, y);
            }
        var props = new ImageProperties {
            Width = w, Height = h, BitDepth = 16, Channels = 3
        };
        return new BaseImageData(data, props);
    }

    private string WriteFits(BaseImageData img, string name) {
        var path = Path.Combine(_tmpDir, name);
        FITSWriter.Write(img, path);
        return path;
    }

    private static BaseImageData ReadFits(string path) {
        using var fs = File.OpenRead(path);
        return FITSReader.Read(fs);
    }
}
