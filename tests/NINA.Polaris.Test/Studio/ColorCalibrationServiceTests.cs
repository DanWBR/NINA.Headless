using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Sky;
using NINA.Polaris.Services.Studio;

namespace NINA.Polaris.Test.Studio;

/// <summary>
/// CCALB-1 + 2: pins the BG neutralisation and Manual color
/// calibration paths through ColorCalibrationService. Math edge
/// cases live in ColorCalibrationMathTests; this suite covers the
/// end-to-end service contract (job pattern, file output, FITS
/// header recipe).
/// </summary>
[TestFixture]
public class ColorCalibrationServiceTests {

    private string _tmpRoot = null!;
    private ProfileService _profile = null!;
    private FrameLibraryService _library = null!;
    private ColorCalibrationService _svc = null!;

    [SetUp]
    public void Setup() {
        _tmpRoot = Path.Combine(Path.GetTempPath(),
            "polaris-ccal-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpRoot);

        var cfg = new ConfigurationBuilder().Build();
        _profile = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        _profile.Active.ImageOutputDir = _tmpRoot;
        _profile.ActiveEquipmentProfile.Name = "TestRig";

        _library = new FrameLibraryService(_profile, cfg,
            NullLogger<FrameLibraryService>.Instance);
        // Real ApassCatalog pointed at an empty directory so its
        // IsAvailable returns false. PCC tests exercise this path
        // (the "catalog missing" error). BG + Manual tests don't
        // touch the catalog at all.
        var env = new FakeWebHostEnvironment(_tmpRoot);
        var catalog = new ApassCatalog(env, NullLogger<ApassCatalog>.Instance);
        _svc = new ColorCalibrationService(_library, _profile, catalog,
            NullLogger<ColorCalibrationService>.Instance);
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment {
        public FakeWebHostEnvironment(string root) {
            ContentRootPath = root;
            WebRootPath = root;
            ContentRootFileProvider = new NullFileProvider();
            WebRootFileProvider = new NullFileProvider();
        }
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "NINA.Polaris.Test";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
    }

    [TearDown]
    public void Teardown() {
        try { Directory.Delete(_tmpRoot, recursive: true); } catch { }
    }

    // ─── BG neutralisation (auto) ────────────────────────────────────

    [Test]
    public async Task BgNeutral_Auto_RecoversNeutralBackground() {
        // Background = R:200, G:500, B:300 (forced green/yellow cast).
        // After BG neutralisation, all three medians should land
        // within 1 ADU of each other (the dimmest of the three, R=200).
        var path = SeedRgbFits(64, 64,
            bgR: 200, bgG: 500, bgB: 300,
            starPeakR: 40000, starPeakG: 40000, starPeakB: 40000);
        await _library.RescanAsync();

        var jobId = _svc.StartJob(new ColorCalibrationService.ColorCalibrationRequest(
            FramePath: path, Mode: ColorCalibrationService.Modes.BgNeutral,
            BgSample: "auto"));
        var status = await WaitForJob(jobId, TimeSpan.FromSeconds(10));

        Assert.That(status.Stage, Is.EqualTo("done"),
            $"BG neutral failed: {status.Error}");
        Assert.That(status.OutputPath, Is.Not.Null);
        Assert.That(File.Exists(status.OutputPath!), Is.True);

        // Re-read the output and confirm the background channels are
        // now equal (within 5 ADU of each other; the histogram
        // median is integer-quantised so small drift is expected).
        using var fs = File.OpenRead(status.OutputPath!);
        var img = FITSReader.Read(fs);
        int n = img.Properties.Width * img.Properties.Height;
        // Sample pixel (0, 0) is in the background of the synthetic
        // frame; if BG neutralisation worked, R == G == B at that
        // pixel (within ushort rounding).
        ushort outR = img.Data[0];
        ushort outG = img.Data[n];
        ushort outB = img.Data[n * 2];
        Assert.That(outG, Is.EqualTo(outR).Within(5),
            $"G should equal R after neutralisation (R={outR}, G={outG}).");
        Assert.That(outB, Is.EqualTo(outR).Within(5),
            $"B should equal R after neutralisation (R={outR}, B={outB}).");
    }

    [Test]
    public async Task BgNeutral_OutputCarriesRecipeInFitsHeaders() {
        var path = SeedRgbFits(32, 32, bgR: 100, bgG: 300, bgB: 200,
            starPeakR: 0, starPeakG: 0, starPeakB: 0);
        await _library.RescanAsync();

        var jobId = _svc.StartJob(new ColorCalibrationService.ColorCalibrationRequest(
            FramePath: path, Mode: ColorCalibrationService.Modes.BgNeutral));
        var status = await WaitForJob(jobId, TimeSpan.FromSeconds(5));
        Assert.That(status.Stage, Is.EqualTo("done"));

        // Read headers and verify the recipe is preserved. The user
        // can audit "what did this file get?" in PixInsight without
        // a separate metadata sidecar.
        var headers = FITSReader.ReadHeadersOnly(File.OpenRead(status.OutputPath!));
        Assert.That(headers.ContainsKey("CCAL_MOD"), Is.True);
        Assert.That(headers["CCAL_MOD"].Value, Is.EqualTo("bg"));
        Assert.That(headers.ContainsKey("CCAL_OFR"), Is.True);
        Assert.That(headers.ContainsKey("CCAL_OFG"), Is.True);
        Assert.That(headers.ContainsKey("CCAL_OFB"), Is.True);
    }

    // ─── BG neutralisation (patch) ───────────────────────────────────

    [Test]
    public async Task BgNeutral_Patch_UsesSuppliedRoi() {
        // Patch mode forces the median sample to come from a specific
        // ROI rather than the whole-frame lowest-luminance heuristic.
        // The patch I pick has R=500 G=800 B=600; after neut all three
        // channels equal R's median (500) at that patch.
        var path = SeedRgbFits(64, 64, bgR: 500, bgG: 800, bgB: 600,
            starPeakR: 0, starPeakG: 0, starPeakB: 0);
        await _library.RescanAsync();

        var jobId = _svc.StartJob(new ColorCalibrationService.ColorCalibrationRequest(
            FramePath: path,
            Mode: ColorCalibrationService.Modes.BgNeutral,
            BgSample: "patch",
            BgPatch: new ColorCalibrationService.PatchRoi(X: 10, Y: 10, W: 10, H: 10)));
        var status = await WaitForJob(jobId, TimeSpan.FromSeconds(5));
        Assert.That(status.Stage, Is.EqualTo("done"),
            $"BG patch failed: {status.Error}");

        // OffsetR should be 0 (R is dimmest), OffsetG=300, OffsetB=100.
        Assert.That(status.OffsetR, Is.EqualTo(0).Within(1));
        Assert.That(status.OffsetG, Is.EqualTo(300).Within(5));
        Assert.That(status.OffsetB, Is.EqualTo(100).Within(5));
    }

    [Test]
    public void BgNeutral_PatchMode_WithoutPatch_Throws() {
        Assert.Throws<ArgumentException>(() => {
            _svc.StartJob(new ColorCalibrationService.ColorCalibrationRequest(
                FramePath: "missing.fits",
                Mode: ColorCalibrationService.Modes.BgNeutral,
                BgSample: "patch",
                BgPatch: null));
        });
    }

    // ─── Manual color calibration ────────────────────────────────────

    [Test]
    public async Task Manual_BgPlusWhite_NeutralisesBothBackgroundAndWhite() {
        // Two regions: a dark BG (R:200 G:500 B:300, green cast) and
        // a bright "white" reference patch (R:5000 G:8000 B:4000, also
        // colour-cast). Manual should neutralise both: after the run,
        // background pixels are equal across channels AND the white
        // patch is also equal across channels.
        var path = SeedTwoPatchRgbFits(64, 64,
            bgR: 200, bgG: 500, bgB: 300,
            whiteX: 32, whiteY: 32, whiteW: 16, whiteH: 16,
            whiteR: 5000, whiteG: 8000, whiteB: 4000);
        await _library.RescanAsync();

        var jobId = _svc.StartJob(new ColorCalibrationService.ColorCalibrationRequest(
            FramePath: path,
            Mode: ColorCalibrationService.Modes.Manual,
            BgSample: "auto",
            WhitePatch: new ColorCalibrationService.PatchRoi(X: 32, Y: 32, W: 16, H: 16)));
        var status = await WaitForJob(jobId, TimeSpan.FromSeconds(10));
        Assert.That(status.Stage, Is.EqualTo("done"),
            $"Manual failed: {status.Error}");

        using var fs = File.OpenRead(status.OutputPath!);
        var img = FITSReader.Read(fs);
        int n = img.Properties.Width * img.Properties.Height;

        // BG check at (0, 0): all channels close.
        ushort bgR = img.Data[0];
        ushort bgG = img.Data[n];
        ushort bgB = img.Data[n * 2];
        Assert.That(bgG, Is.EqualTo(bgR).Within(20),
            $"BG G={bgG} R={bgR}");
        Assert.That(bgB, Is.EqualTo(bgR).Within(20),
            $"BG B={bgB} R={bgR}");

        // White patch check at the centre pixel (32+8, 32+8):
        int idx = (40 * img.Properties.Width) + 40;
        ushort wR = img.Data[idx];
        ushort wG = img.Data[n + idx];
        ushort wB = img.Data[n * 2 + idx];
        Assert.That(wG, Is.EqualTo(wR).Within(200),
            $"White G={wG} R={wR}");
        Assert.That(wB, Is.EqualTo(wR).Within(200),
            $"White B={wB} R={wR}");
    }

    [Test]
    public void Manual_WithoutWhitePatch_Throws() {
        Assert.Throws<ArgumentException>(() => {
            _svc.StartJob(new ColorCalibrationService.ColorCalibrationRequest(
                FramePath: "missing.fits",
                Mode: ColorCalibrationService.Modes.Manual,
                WhitePatch: null));
        });
    }

    // ─── PCC math (ComputePccGains) ─────────────────────────────────

    [Test]
    public void Pcc_ComputeGains_ReferenceG2v_ReturnsIdentity() {
        // A single matched star with the reference B-V (~0.65) and
        // observed R = G = B should produce gains of (1, 1, 1):
        // the star already appears neutral, no correction needed.
        var phot = new NINA.Image.ImageAnalysis.StarPhotometer.StarPhotometry(
            X: 100, Y: 100, HFR: 2.5,
            FluxR: 10000, FluxG: 10000, FluxB: 10000,
            BackgroundR: 100, BackgroundG: 100, BackgroundB: 100,
            PixelCount: 30, ApertureRadius: 5, Saturated: false);
        var stars = new List<ColorCalibrationMath.CalibrationStar> {
            new(phot, ColorCalibrationMath.ReferenceBv),
        };
        var gains = ColorCalibrationMath.ComputePccGains(stars);
        Assert.That(gains[0], Is.EqualTo(1.0).Within(0.001));
        Assert.That(gains[1], Is.EqualTo(1.0).Within(0.001));
        Assert.That(gains[2], Is.EqualTo(1.0).Within(0.001));
    }

    [Test]
    public void Pcc_ComputeGains_ObservedTooBlue_BoostsRedDimsBlue() {
        // A G2V-coloured star (B-V=0.65) that we observed with too
        // much blue and too little red should yield gain_R > 1 and
        // gain_B < 1, pulling the image back toward neutral.
        var phot = new NINA.Image.ImageAnalysis.StarPhotometer.StarPhotometry(
            X: 100, Y: 100, HFR: 2.5,
            FluxR: 5000, FluxG: 10000, FluxB: 20000,   // way too blue
            BackgroundR: 0, BackgroundG: 0, BackgroundB: 0,
            PixelCount: 30, ApertureRadius: 5, Saturated: false);
        var stars = new List<ColorCalibrationMath.CalibrationStar> {
            new(phot, ColorCalibrationMath.ReferenceBv),
        };
        var gains = ColorCalibrationMath.ComputePccGains(stars);
        Assert.That(gains[0], Is.GreaterThan(1.0),
            $"Too-dim R should be boosted (gain > 1), got {gains[0]:F3}");
        Assert.That(gains[1], Is.EqualTo(1.0).Within(0.001));
        Assert.That(gains[2], Is.LessThan(1.0),
            $"Too-bright B should be dimmed (gain < 1), got {gains[2]:F3}");
    }

    [Test]
    public void Pcc_ComputeGains_SaturatedStarsIgnored() {
        // Mix of one saturated star (should be dropped) and three
        // valid neutral-coloured ones. Result should still be
        // identity gains.
        var saturated = new NINA.Image.ImageAnalysis.StarPhotometer.StarPhotometry(
            X: 0, Y: 0, HFR: 3, FluxR: 0, FluxG: 0, FluxB: 0,
            BackgroundR: 0, BackgroundG: 0, BackgroundB: 0,
            PixelCount: 0, ApertureRadius: 0, Saturated: true);
        var good = new NINA.Image.ImageAnalysis.StarPhotometer.StarPhotometry(
            X: 100, Y: 100, HFR: 2.5,
            FluxR: 10000, FluxG: 10000, FluxB: 10000,
            BackgroundR: 0, BackgroundG: 0, BackgroundB: 0,
            PixelCount: 30, ApertureRadius: 5, Saturated: false);
        var stars = new List<ColorCalibrationMath.CalibrationStar> {
            new(saturated, ColorCalibrationMath.ReferenceBv),
            new(good, ColorCalibrationMath.ReferenceBv),
            new(good, ColorCalibrationMath.ReferenceBv),
            new(good, ColorCalibrationMath.ReferenceBv),
        };
        var gains = ColorCalibrationMath.ComputePccGains(stars);
        Assert.That(gains[0], Is.EqualTo(1.0).Within(0.001));
        Assert.That(gains[2], Is.EqualTo(1.0).Within(0.001));
    }

    [Test]
    public void Pcc_ComputeGains_NoValidStars_Throws() {
        // All stars saturated, or all with zero flux: nothing to fit.
        var bad = new NINA.Image.ImageAnalysis.StarPhotometer.StarPhotometry(
            X: 0, Y: 0, HFR: 3, FluxR: 0, FluxG: 0, FluxB: 0,
            BackgroundR: 0, BackgroundG: 0, BackgroundB: 0,
            PixelCount: 0, ApertureRadius: 0, Saturated: true);
        var stars = new List<ColorCalibrationMath.CalibrationStar> {
            new(bad, 0.65), new(bad, 0.65),
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ColorCalibrationMath.ComputePccGains(stars));
        Assert.That(ex!.Message, Does.Contain("matched").IgnoreCase
            .Or.Contain("rejected").IgnoreCase);
    }

    // ─── PCC pre-flight gates ────────────────────────────────────────

    [Test]
    public async Task Photometric_NoWcs_FailsWithPlateSolveHint() {
        // Source has no WCS in the FITS headers. PCC should fail
        // loudly with a hint pointing the user at the Solve step
        // rather than silently producing garbage gains.
        var path = SeedRgbFits(32, 32, bgR: 100, bgG: 100, bgB: 100,
            starPeakR: 0, starPeakG: 0, starPeakB: 0);
        await _library.RescanAsync();

        var jobId = _svc.StartJob(new ColorCalibrationService.ColorCalibrationRequest(
            FramePath: path, Mode: ColorCalibrationService.Modes.Photometric));
        var status = await WaitForJob(jobId, TimeSpan.FromSeconds(5));

        Assert.That(status.Stage, Is.EqualTo("error"));
        Assert.That(status.Error, Does.Contain("WCS").IgnoreCase
            .Or.Contain("plate-solve").IgnoreCase
            .Or.Contain("plate solve").IgnoreCase);
    }

    // ─── input validation ───────────────────────────────────────────

    [Test]
    public async Task MonoInput_FailsWithClearError() {
        // Mono FITS: 1-channel. Color calibration is a no-op on mono;
        // the service should refuse loudly so the user knows to
        // combine first.
        var path = Path.Combine(_tmpRoot, "mono.fits");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pix = new ushort[32 * 32];
        Array.Fill(pix, (ushort)1000);
        FITSWriter.Write(new BaseImageData(pix,
            new ImageProperties { Width = 32, Height = 32, BitDepth = 16, Channels = 1 },
            new ImageMetaData {
                Target = new ImageMetaData.TargetInfo { Name = "X" },
                Exposure = new ImageMetaData.ExposureInfo {
                    Filter = "L", ImageType = "MASTERLIGHT" } }),
            path);
        await _library.RescanAsync();
        int id = FindIdByPath(path);

        var jobId = _svc.StartJob(new ColorCalibrationService.ColorCalibrationRequest(
            FramePath: path, Mode: ColorCalibrationService.Modes.BgNeutral));
        var status = await WaitForJob(jobId, TimeSpan.FromSeconds(5));

        Assert.That(status.Stage, Is.EqualTo("error"));
        Assert.That(status.Error, Does.Contain("3-channel").IgnoreCase
            .Or.Contain("combine").IgnoreCase);
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private string SeedRgbFits(int w, int h,
            ushort bgR, ushort bgG, ushort bgB,
            ushort starPeakR, ushort starPeakG, ushort starPeakB) {
        var path = Path.Combine(_tmpRoot, "TestRig", "integrated", "TGT", "composed",
            $"rgb_{Guid.NewGuid().ToString("N")[..6]}.fits");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        int n = w * h;
        var pix = new ushort[n * 3];
        for (int i = 0; i < n; i++) {
            pix[i] = bgR;
            pix[n + i] = bgG;
            pix[n * 2 + i] = bgB;
        }
        // Optional bright spot in the center so auto-BG (lowest 5%)
        // does not accidentally sample the star.
        if (starPeakR + starPeakG + starPeakB > 0) {
            for (int dy = -2; dy <= 2; dy++) {
                for (int dx = -2; dx <= 2; dx++) {
                    int x = w / 2 + dx, y = h / 2 + dy;
                    if (x < 0 || x >= w || y < 0 || y >= h) continue;
                    int i = y * w + x;
                    pix[i] = starPeakR;
                    pix[n + i] = starPeakG;
                    pix[n * 2 + i] = starPeakB;
                }
            }
        }
        FITSWriter.Write(new BaseImageData(pix,
            new ImageProperties { Width = w, Height = h, BitDepth = 16, Channels = 3 },
            new ImageMetaData {
                Target = new ImageMetaData.TargetInfo { Name = "TGT" },
                Exposure = new ImageMetaData.ExposureInfo {
                    Filter = "RGB", ImageType = "MASTERCOMP" } }),
            path);
        return path;
    }

    private string SeedTwoPatchRgbFits(int w, int h,
            ushort bgR, ushort bgG, ushort bgB,
            int whiteX, int whiteY, int whiteW, int whiteH,
            ushort whiteR, ushort whiteG, ushort whiteB) {
        var path = Path.Combine(_tmpRoot, "TestRig", "integrated", "TGT", "composed",
            $"rgb_{Guid.NewGuid().ToString("N")[..6]}.fits");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        int n = w * h;
        var pix = new ushort[n * 3];
        for (int i = 0; i < n; i++) {
            pix[i] = bgR;
            pix[n + i] = bgG;
            pix[n * 2 + i] = bgB;
        }
        // White-reference square.
        for (int yy = whiteY; yy < whiteY + whiteH && yy < h; yy++) {
            for (int xx = whiteX; xx < whiteX + whiteW && xx < w; xx++) {
                int i = yy * w + xx;
                pix[i] = whiteR;
                pix[n + i] = whiteG;
                pix[n * 2 + i] = whiteB;
            }
        }
        FITSWriter.Write(new BaseImageData(pix,
            new ImageProperties { Width = w, Height = h, BitDepth = 16, Channels = 3 },
            new ImageMetaData {
                Target = new ImageMetaData.TargetInfo { Name = "TGT" },
                Exposure = new ImageMetaData.ExposureInfo {
                    Filter = "RGB", ImageType = "MASTERCOMP" } }),
            path);
        return path;
    }

    private int FindIdAfterRescan(string path) {
        _library.RescanAsync().GetAwaiter().GetResult();
        return FindIdByPath(path);
    }

    private int FindIdByPath(string path) {
        var rows = _library.Query(new FrameQuery(
            Type: null, Filter: null, Target: null,
            DateFrom: null, DateTo: null, Limit: 1000, Offset: 0));
        foreach (var r in rows) if (r.Path == path) return r.Id;
        throw new InvalidOperationException($"Seeded {path} but rescan didn't index it.");
    }

    private async Task<ColorCalibrationProgress> WaitForJob(string jobId, TimeSpan timeout) {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline) {
            var s = _svc.GetStatus(jobId);
            if (s != null && !s.InProgress) return s;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Job {jobId} did not finish within {timeout}.");
    }
}
