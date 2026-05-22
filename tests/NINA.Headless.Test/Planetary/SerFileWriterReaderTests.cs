using NUnit.Framework;
using NINA.Headless.Services.Planetary;

namespace NINA.Headless.Test.Planetary;

[TestFixture]
public class SerFileWriterReaderTests {
    private string _tempDir = null!;

    [SetUp]
    public void SetUp() {
        _tempDir = Path.Combine(Path.GetTempPath(), "polaris-ser-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Test]
    public void RoundTrip_16bitMono_PreservesPixels() {
        var path = Path.Combine(_tempDir, "mono16.ser");
        const int w = 8, h = 6;
        var frame = new ushort[w * h];
        for (int i = 0; i < frame.Length; i++) frame[i] = (ushort)(i * 257);  // 0, 257, 514, …

        using (var writer = new SerFileWriter(path, w, h, bitDepth: 16, SerColorMode.Mono,
            observer: "TestObs", instrument: "TestInst", telescope: "TestScope")) {
            writer.WriteFrame(frame, DateTime.UtcNow);
            writer.WriteFrame(frame, DateTime.UtcNow);
            writer.WriteFrame(frame, DateTime.UtcNow);
            Assert.That(writer.FrameCount, Is.EqualTo(3));
        }

        using var reader = new SerFileReader(path);
        Assert.That(reader.Width, Is.EqualTo(w));
        Assert.That(reader.Height, Is.EqualTo(h));
        Assert.That(reader.BitDepth, Is.EqualTo(16));
        Assert.That(reader.ColorMode, Is.EqualTo(SerColorMode.Mono));
        Assert.That(reader.FrameCount, Is.EqualTo(3));
        Assert.That(reader.Observer,   Is.EqualTo("TestObs"));
        Assert.That(reader.Instrument, Is.EqualTo("TestInst"));
        Assert.That(reader.Telescope,  Is.EqualTo("TestScope"));

        var read = reader.ReadFrameAsUshort(0);
        Assert.That(read, Has.Length.EqualTo(frame.Length));
        Assert.That(read, Is.EqualTo(frame));

        // All three frames identical in this test, but check that
        // reading frame 2 actually seeks to a different offset.
        var read2 = reader.ReadFrameAsUshort(2);
        Assert.That(read2, Is.EqualTo(frame));
    }

    [Test]
    public void Constructor_RejectsInvalidParams() {
        var path = Path.Combine(_tempDir, "bad.ser");
        Assert.Throws<ArgumentException>(() => new SerFileWriter(path, 0, 100, 16));
        Assert.Throws<ArgumentException>(() => new SerFileWriter(path, 100, 0, 16));
        Assert.Throws<ArgumentException>(() => new SerFileWriter(path, 100, 100, 12));  // not 8/16
    }

    [Test]
    public void WriteFrame_WrongSize_Throws() {
        var path = Path.Combine(_tempDir, "wrongsize.ser");
        using var writer = new SerFileWriter(path, 10, 10, 16);
        var tooSmall = new ushort[50];
        Assert.Throws<ArgumentException>(() => writer.WriteFrame(tooSmall));
    }

    [Test]
    public void Reader_RejectsNonSerFile() {
        var path = Path.Combine(_tempDir, "garbage.bin");
        File.WriteAllBytes(path, Enumerable.Range(0, 200).Select(i => (byte)i).ToArray());
        Assert.Throws<InvalidDataException>(() => new SerFileReader(path));
    }

    [Test]
    public void TimestampTrailer_RecordsRequestedUtc() {
        var path = Path.Combine(_tempDir, "ts.ser");
        var t1 = new DateTime(2026, 5, 22, 22, 30, 0, DateTimeKind.Utc);
        var t2 = t1.AddSeconds(1);
        var t3 = t1.AddSeconds(2);

        using (var writer = new SerFileWriter(path, 4, 4, 16)) {
            writer.WriteFrame(new ushort[16], t1);
            writer.WriteFrame(new ushort[16], t2);
            writer.WriteFrame(new ushort[16], t3);
        }
        using var reader = new SerFileReader(path);
        Assert.That(reader.TimestampOf(0), Is.EqualTo(t1));
        Assert.That(reader.TimestampOf(1), Is.EqualTo(t2));
        Assert.That(reader.TimestampOf(2), Is.EqualTo(t3));
    }

    [Test]
    public void Reader_BayerPattern_ReportsCorrectColorMode() {
        var path = Path.Combine(_tempDir, "bayer.ser");
        using (var writer = new SerFileWriter(path, 10, 10, 16, SerColorMode.BayerRGGB)) {
            writer.WriteFrame(new ushort[100]);
        }
        using var reader = new SerFileReader(path);
        Assert.That(reader.ColorMode, Is.EqualTo(SerColorMode.BayerRGGB));
    }

    [Test]
    public void Writer_CreatesDirectoryIfMissing() {
        // Sub-path that doesn't exist yet — writer should mkdir -p.
        var path = Path.Combine(_tempDir, "deep", "subdir", "video.ser");
        using (var writer = new SerFileWriter(path, 4, 4, 16)) {
            writer.WriteFrame(new ushort[16]);
        }
        Assert.That(File.Exists(path), Is.True);
    }
}
