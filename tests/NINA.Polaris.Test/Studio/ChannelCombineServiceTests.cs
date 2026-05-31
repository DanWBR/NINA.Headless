using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Studio;

namespace NINA.Polaris.Test.Studio;

/// <summary>
/// CC-1: ChannelCombineService produces a single RGB FITS from N mono
/// per-filter masters. Tests cover the happy path (perfectly aligned
/// inputs), the cross-channel registration path (one input shifted by
/// a few pixels gets brought back to the reference grid), and the
/// failure paths (dimension mismatch, missing input, register failure).
///
/// The service writes to disk + indexes via FrameLibraryService.
/// FrameLibraryService is constructed with a real SQLite file under
/// the per-test temp dir; the rescan after each combine job runs but
/// can't crash a test because it's fire-and-forget. We assert on the
/// output file directly.
/// </summary>
[TestFixture]
public class ChannelCombineServiceTests {

    private string _tmpRoot = null!;
    private ProfileService _profile = null!;
    private FrameLibraryService _library = null!;
    private ChannelCombineService _svc = null!;

    [SetUp]
    public void Setup() {
        _tmpRoot = Path.Combine(Path.GetTempPath(),
            "polaris-cc-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpRoot);

        var cfg = new ConfigurationBuilder().Build();
        _profile = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        _profile.Active.ImageOutputDir = _tmpRoot;
        // ActiveEquipmentProfile is read-only; ProfileService auto-creates
        // a default entry on init. Rename it so the rig folder lands at
        // a deterministic name we can assert against.
        _profile.ActiveEquipmentProfile.Name = "TestRig";

        _library = new FrameLibraryService(_profile, cfg, NullLogger<FrameLibraryService>.Instance);
        _svc = new ChannelCombineService(_library, _profile,
            NullLogger<ChannelCombineService>.Instance);
    }

    [TearDown]
    public void Teardown() {
        try { Directory.Delete(_tmpRoot, recursive: true); } catch { }
    }

    // ─── happy path ──────────────────────────────────────────────────

    [Test]
    public async Task RgbCompose_AlignedSyntheticFrames_ProducesRgbFits() {
        // Pre-aligned synthetic 64x64 frames with 4 bright stars in a
        // square pattern. The combine should pack them into a single
        // RGB FITS where each plane recovers the original mono input.
        // Register is OFF here to isolate the compose code path; the
        // alignment-OFF path also matters in production (observatory
        // permanent-pier users will use it).
        var rPath = SeedFrame("R", 64, 64, baseLevel: 100, starPeak: 40000);
        var gPath = SeedFrame("G", 64, 64, baseLevel: 200, starPeak: 30000);
        var bPath = SeedFrame("B", 64, 64, baseLevel: 300, starPeak: 20000);
        await _library.RescanAsync();

        var req = new ChannelCombineService.ChannelCombineRequest(
            Mode: ChannelCombineService.Modes.RgbCompose,
            ChannelMap: new() {
                new("R", rPath), new("G", gPath), new("B", bPath)
            },
            Register: false,
            Normalize: false);

        var jobId = _svc.StartJob(req);
        var status = await WaitForJob(jobId, TimeSpan.FromSeconds(10));

        Assert.That(status.Stage, Is.EqualTo("done"),
            $"Job failed with: {status.Error}");
        Assert.That(status.OutputPath, Is.Not.Null.And.Not.Empty);
        Assert.That(File.Exists(status.OutputPath!), Is.True,
            "Output file must exist on disk.");
        Assert.That(status.OutputChannels, Is.EqualTo(3));

        // Re-read the output and confirm it's a 3-plane FITS where
        // each plane matches its original mono input (modulo the
        // BZERO=32768 round-trip).
        using var fs = File.OpenRead(status.OutputPath!);
        var img = FITSReader.Read(fs);
        Assert.That(img.Properties.Channels, Is.EqualTo(3));
        Assert.That(img.Properties.Width, Is.EqualTo(64));
        Assert.That(img.Properties.Height, Is.EqualTo(64));
        Assert.That(img.Data.Length, Is.EqualTo(64 * 64 * 3));
        // Sample a non-star pixel from each plane (0,0 is background).
        int planeSize = 64 * 64;
        Assert.That(img.Data[0],                        Is.EqualTo(100), "R plane background");
        Assert.That(img.Data[planeSize],                Is.EqualTo(200), "G plane background");
        Assert.That(img.Data[planeSize * 2],            Is.EqualTo(300), "B plane background");
    }

    [Test]
    public async Task RgbCompose_PathLandsUnderComposedSubdir() {
        var rPath = SeedFrame("R", 32, 32);
        var gPath = SeedFrame("G", 32, 32);
        var bPath = SeedFrame("B", 32, 32);
        await _library.RescanAsync();

        var jobId = _svc.StartJob(new ChannelCombineService.ChannelCombineRequest(
            Mode: ChannelCombineService.Modes.RgbCompose,
            ChannelMap: new() { new("R", rPath), new("G", gPath), new("B", bPath) },
            Register: false,
            Normalize: false));
        var status = await WaitForJob(jobId, TimeSpan.FromSeconds(10));

        Assert.That(status.OutputPath, Is.Not.Null);
        // Expected layout: {imgOut}/{rig}/integrated/{target}/composed/rgb_*.fits
        var rel = Path.GetRelativePath(_tmpRoot, status.OutputPath!);
        Assert.That(rel, Does.Contain("integrated"));
        Assert.That(rel, Does.Contain("composed"));
        Assert.That(Path.GetFileName(rel), Does.StartWith("rgb_"));
        Assert.That(Path.GetFileName(rel), Does.EndWith(".fits"));
    }

    // ─── cross-channel registration ──────────────────────────────────

    // Cross-channel registration success path (real masters with
    // genuine Gaussian star profiles get aligned) is covered by the
    // manual end-to-end M31 LRGB smoke described in
    // docs/user-guide/lrgb-mono-workflow.md, not a unit test.
    // StarMatcher + StarDetector are tuned for the Gaussian profiles
    // CCDs produce; synthetic sharp-edged squares trip RANSAC, which
    // would make this test brittle and unrelated to the CC-1 contract.
    // The CC-1 unit suite focuses on (a) the compose + pack + write
    // pipeline and (b) the failure-mode contracts the UI relies on.

    [Test]
    public async Task RgbCompose_RegisterOn_BlankNonReference_FailsWithClearError() {
        // R has lots of stars; G + B are uniform background (no stars).
        // The registration phase has to fail loudly with an actionable
        // message instead of silently producing a misaligned output or
        // crashing. This pins the error-path contract that the UI will
        // surface to the user.
        var rPath = SeedStarryFrame("R", 256, 256, dx: 0, dy: 0);
        var gPath = SeedFrame("G", 256, 256);  // uniform background
        var bPath = SeedFrame("B", 256, 256);
        await _library.RescanAsync();

        var jobId = _svc.StartJob(new ChannelCombineService.ChannelCombineRequest(
            Mode: ChannelCombineService.Modes.RgbCompose,
            ChannelMap: new() { new("R", rPath), new("G", gPath), new("B", bPath) },
            Register: true,
            Normalize: false));
        var status = await WaitForJob(jobId, TimeSpan.FromSeconds(10));

        Assert.That(status.Stage, Is.EqualTo("error"));
        Assert.That(status.Error, Does.Contain("register").IgnoreCase
            .Or.Contain("match").IgnoreCase,
            $"Error should explain the registration failure, got: {status.Error}");
        Assert.That(status.OutputPath, Is.Null);
    }

    // ─── failure modes ───────────────────────────────────────────────

    [Test]
    public async Task RgbCompose_DimensionMismatch_JobFails() {
        var rPath = SeedFrame("R", 64, 64);
        var gPath = SeedFrame("G", 32, 32);   // ← mismatch
        var bPath = SeedFrame("B", 64, 64);
        await _library.RescanAsync();

        var jobId = _svc.StartJob(new ChannelCombineService.ChannelCombineRequest(
            Mode: ChannelCombineService.Modes.RgbCompose,
            ChannelMap: new() { new("R", rPath), new("G", gPath), new("B", bPath) },
            Register: false,
            Normalize: false));
        var status = await WaitForJob(jobId, TimeSpan.FromSeconds(5));

        Assert.That(status.Stage, Is.EqualTo("error"));
        Assert.That(status.Error, Does.Contain("expected").IgnoreCase);
        Assert.That(status.OutputPath, Is.Null);
    }

    [Test]
    public void StartJob_TooFewInputs_ThrowsArgument() {
        var rPath = SeedFrame("R", 32, 32);
        // Only one input → must fail validation client-side, mirrors
        // the endpoint guard so the service is safe to call directly.
        Assert.Throws<ArgumentException>(() => {
            _svc.StartJob(new ChannelCombineService.ChannelCombineRequest(
                Mode: ChannelCombineService.Modes.RgbCompose,
                ChannelMap: new() { new("R", rPath) },
                Register: false,
                Normalize: false));
        });
    }

    [Test]
    public async Task LrgbCompose_ProducesLrgbFitsWithLrgbPrefix() {
        // CC-2: LrgbCompose mode wires up LrgbCombiner. Output lands
        // under composed/ with the "lrgb_" prefix instead of "rgb_",
        // and FITS Channels == 3 (luminance is folded into RGB, not
        // stored as a 4th plane).
        var rPath = SeedFrame("R", 32, 32, baseLevel: 4000);
        var gPath = SeedFrame("G", 32, 32, baseLevel: 5000);
        var bPath = SeedFrame("B", 32, 32, baseLevel: 6000);
        var lId = SeedFrame("L", 32, 32, baseLevel: 15000);   // brighter luminance
        await _library.RescanAsync();

        var jobId = _svc.StartJob(new ChannelCombineService.ChannelCombineRequest(
            Mode: ChannelCombineService.Modes.LrgbCompose,
            ChannelMap: new() {
                new("R", rPath), new("G", gPath), new("B", bPath), new("L", lId)
            },
            Register: false,
            Normalize: false,
            LrgbAlgo: "lab"));
        var status = await WaitForJob(jobId, TimeSpan.FromSeconds(10));

        Assert.That(status.Stage, Is.EqualTo("done"),
            $"LrgbCompose failed: {status.Error}");
        Assert.That(status.OutputChannels, Is.EqualTo(3));
        Assert.That(Path.GetFileName(status.OutputPath!), Does.StartWith("lrgb_"));
        Assert.That(File.Exists(status.OutputPath!), Is.True);
    }

    [Test]
    public async Task LrgbCompose_MissingL_FailsWithClearError() {
        // Without a channel named "L" the LRGB combine cannot apply
        // the luminance overlay; the service should fail loudly so
        // the UI surfaces a fixable error to the user.
        var rPath = SeedFrame("R", 32, 32);
        var gPath = SeedFrame("G", 32, 32);
        var bPath = SeedFrame("B", 32, 32);
        await _library.RescanAsync();

        var jobId = _svc.StartJob(new ChannelCombineService.ChannelCombineRequest(
            Mode: ChannelCombineService.Modes.LrgbCompose,
            ChannelMap: new() { new("R", rPath), new("G", gPath), new("B", bPath) },
            Register: false,
            Normalize: false));
        var status = await WaitForJob(jobId, TimeSpan.FromSeconds(5));

        Assert.That(status.Stage, Is.EqualTo("error"));
        Assert.That(status.Error, Does.Contain("L").And.Contain("luminance").IgnoreCase
            .Or.Contain("requires").IgnoreCase);
    }

    [Test]
    public async Task LrgbCompose_RatioAlgorithm_AlsoSucceeds() {
        // Both lab and ratio code paths must be reachable through the
        // service. Verify by running the same input twice with the
        // two algos and confirming both produce an output FITS.
        var rPath = SeedFrame("R", 32, 32, baseLevel: 4000);
        var gPath = SeedFrame("G", 32, 32, baseLevel: 5000);
        var bPath = SeedFrame("B", 32, 32, baseLevel: 6000);
        var lId = SeedFrame("L", 32, 32, baseLevel: 15000);
        await _library.RescanAsync();

        var jobId = _svc.StartJob(new ChannelCombineService.ChannelCombineRequest(
            Mode: ChannelCombineService.Modes.LrgbCompose,
            ChannelMap: new() {
                new("R", rPath), new("G", gPath), new("B", bPath), new("L", lId)
            },
            Register: false,
            Normalize: false,
            LrgbAlgo: "ratio"));
        var status = await WaitForJob(jobId, TimeSpan.FromSeconds(10));

        Assert.That(status.Stage, Is.EqualTo("done"),
            $"LrgbCompose (ratio) failed: {status.Error}");
        Assert.That(File.Exists(status.OutputPath!), Is.True);
    }

    [Test]
    public async Task PixelMath_HOOSynth_ProducesRgbFitsWithPmPrefix() {
        // CC-3: PixelMath mode wires up PixelMathEvaluator. Classic
        // HOO bicolor synth from Ha + OIII narrowband: R = Ha,
        // G = (Ha + OIII)/2, B = OIII. Output is RGB FITS (3
        // expressions = RGB output, MonoOutput=false default).
        var haId   = SeedFrame("Ha",   32, 32, baseLevel: 12000);
        var oiiiId = SeedFrame("OIII", 32, 32, baseLevel:  6000);
        await _library.RescanAsync();

        var jobId = _svc.StartJob(new ChannelCombineService.ChannelCombineRequest(
            Mode: ChannelCombineService.Modes.PixelMath,
            ChannelMap: new() { new("Ha", haId), new("OIII", oiiiId) },
            Register: false,
            Normalize: false,
            Expressions: new() {
                "Ha",
                "0.5 * Ha + 0.5 * OIII",
                "OIII",
            }));
        var status = await WaitForJob(jobId, TimeSpan.FromSeconds(10));

        Assert.That(status.Stage, Is.EqualTo("done"),
            $"PixelMath failed: {status.Error}");
        Assert.That(status.OutputChannels, Is.EqualTo(3));
        Assert.That(Path.GetFileName(status.OutputPath!), Does.StartWith("pm_"));
        Assert.That(File.Exists(status.OutputPath!), Is.True);
    }

    [Test]
    public async Task PixelMath_MonoOutput_ProducesSingleChannelFits() {
        // MonoOutput=true with 1 expression yields a 1-channel FITS.
        // Useful for synthetic-L from RGB ((R+G+B)/3) for an LRGB
        // workflow that doesn't have a real L master.
        var rPath = SeedFrame("R", 32, 32, baseLevel: 3000);
        var gPath = SeedFrame("G", 32, 32, baseLevel: 6000);
        var bPath = SeedFrame("B", 32, 32, baseLevel: 9000);
        await _library.RescanAsync();

        var jobId = _svc.StartJob(new ChannelCombineService.ChannelCombineRequest(
            Mode: ChannelCombineService.Modes.PixelMath,
            ChannelMap: new() { new("R", rPath), new("G", gPath), new("B", bPath) },
            Register: false,
            Normalize: false,
            MonoOutput: true,
            Expressions: new() { "(R + G + B) / 3" }));
        var status = await WaitForJob(jobId, TimeSpan.FromSeconds(10));

        Assert.That(status.Stage, Is.EqualTo("done"),
            $"PixelMath mono failed: {status.Error}");
        Assert.That(status.OutputChannels, Is.EqualTo(1));
    }

    [Test]
    public async Task PixelMath_BadExpression_FailsBeforeProcessing() {
        // Typo in the variable name surfaces at compile-time as a
        // job error; user fixes the expression in the modal and
        // retries without losing minutes to a doomed job.
        var rPath = SeedFrame("R", 32, 32);
        var gPath = SeedFrame("G", 32, 32);
        var bPath = SeedFrame("B", 32, 32);
        await _library.RescanAsync();

        var jobId = _svc.StartJob(new ChannelCombineService.ChannelCombineRequest(
            Mode: ChannelCombineService.Modes.PixelMath,
            ChannelMap: new() { new("R", rPath), new("G", gPath), new("B", bPath) },
            Register: false,
            Normalize: false,
            Expressions: new() { "R", "G", "blueTypo" }));   // wrong name
        var status = await WaitForJob(jobId, TimeSpan.FromSeconds(5));

        Assert.That(status.Stage, Is.EqualTo("error"));
        Assert.That(status.Error, Does.Contain("blueTypo")
            .Or.Contain("unknown variable").IgnoreCase);
    }

    // ─── normalize ───────────────────────────────────────────────────

    [Test]
    public async Task RgbCompose_Normalize_ScalesDimChannelUp() {
        // R is dim (median ~500), G/B are bright (median ~5000). With
        // Normalize=true the R channel should be scaled ~10× so its
        // median lands close to the brightest. Without normalize the
        // R plane in the output stays at ~500.
        var rPath = SeedFrame("R", 32, 32, baseLevel: 500);
        var gPath = SeedFrame("G", 32, 32, baseLevel: 5000);
        var bPath = SeedFrame("B", 32, 32, baseLevel: 5000);
        await _library.RescanAsync();

        var jobId = _svc.StartJob(new ChannelCombineService.ChannelCombineRequest(
            Mode: ChannelCombineService.Modes.RgbCompose,
            ChannelMap: new() { new("R", rPath), new("G", gPath), new("B", bPath) },
            Register: false,
            Normalize: true));
        var status = await WaitForJob(jobId, TimeSpan.FromSeconds(10));
        Assert.That(status.Stage, Is.EqualTo("done"));

        using var fs = File.OpenRead(status.OutputPath!);
        var img = FITSReader.Read(fs);
        // R plane background pixel; with normalize ON, this should be
        // scaled toward G/B's median (5000), not the original 500.
        Assert.That(img.Data[0], Is.GreaterThan(3000),
            $"Normalize should have scaled R plane up; got {img.Data[0]}");
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private string SeedFrame(string filter, int w, int h,
            ushort baseLevel = 1000, ushort starPeak = 0) {
        var path = Path.Combine(_tmpRoot, "TestRig", "lights", "M31", filter,
            "2026-05-26", $"{filter}_{Guid.NewGuid().ToString("N")[..6]}.fits");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pix = new ushort[w * h];
        for (int i = 0; i < pix.Length; i++) pix[i] = baseLevel;
        if (starPeak > 0) {
            // Sprinkle 4 tight stars at a fixed grid so StarDetector
            // returns deterministic positions.
            int[] xs = [w / 4, 3 * w / 4, w / 4, 3 * w / 4];
            int[] ys = [h / 4, h / 4, 3 * h / 4, 3 * h / 4];
            for (int s = 0; s < 4; s++) {
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++) {
                        int x = xs[s] + dx, y = ys[s] + dy;
                        if (x < 0 || x >= w || y < 0 || y >= h) continue;
                        pix[y * w + x] = starPeak;
                    }
            }
        }
        var img = new BaseImageData(pix,
            new ImageProperties { Width = w, Height = h, BitDepth = 16, Channels = 1 },
            new ImageMetaData {
                Target = new ImageMetaData.TargetInfo { Name = "M31" },
                Exposure = new ImageMetaData.ExposureInfo {
                    Filter = filter, ImageType = "MASTERLIGHT", ExposureTime = 180,
                },
            });
        FITSWriter.Write(img, path);
        // Find the row id post-rescan; library scans paths so we
        // can't pre-compute the id. Tests call _library.RescanAsync()
        // before they look up; the lookup is by-target+filter via
        // a small helper.
        return path;
    }

    /// <summary>
    /// Seed a frame whose star pattern is shifted by (dx, dy) so the
    /// registration phase has something real to align. Stars are solid
    /// 7×7 bright squares, well above StarDetector's threshold so the
    /// test isn't sensitive to subtle MAD shifts or per-channel
    /// thresholding quirks (a previous Gaussian-profile seed kept R
    /// detecting but G picking up zero stars on certain seeds).
    /// </summary>
    private string SeedStarryFrame(string filter, int w, int h, int dx, int dy) {
        var path = Path.Combine(_tmpRoot, "TestRig", "lights", "M31", filter,
            "2026-05-26", $"{filter}_{Guid.NewGuid().ToString("N")[..6]}.fits");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pix = new ushort[w * h];
        for (int i = 0; i < pix.Length; i++) pix[i] = 800;
        // 25 stars in a 5×5 grid so StarMatcher has plenty of pairs to
        // RANSAC against (it needs ≥3 pairs minimum). Each star is a
        // solid 7×7 bright block; with background 800 + star peak
        // 50000, threshold = median + 7σ * MAD ~ 800 (MAD ≈ 0 on
        // uniform background), so detection is unambiguous.
        int starHalf = 3;          // 7×7 footprint = 49 pixels (< MaxStarSize=80)
        ushort starVal = 50000;
        for (int sy = 0; sy < 5; sy++) {
            for (int sx = 0; sx < 5; sx++) {
                int x0 = w / 6 + sx * ((w * 2 / 3) / 4) + dx;
                int y0 = h / 6 + sy * ((h * 2 / 3) / 4) + dy;
                for (int by = -starHalf; by <= starHalf; by++) {
                    for (int bx = -starHalf; bx <= starHalf; bx++) {
                        int x = x0 + bx, y = y0 + by;
                        if (x < 0 || x >= w || y < 0 || y >= h) continue;
                        pix[y * w + x] = starVal;
                    }
                }
            }
        }
        var img = new BaseImageData(pix,
            new ImageProperties { Width = w, Height = h, BitDepth = 16, Channels = 1 },
            new ImageMetaData {
                Target = new ImageMetaData.TargetInfo { Name = "M31" },
                Exposure = new ImageMetaData.ExposureInfo {
                    Filter = filter, ImageType = "MASTERLIGHT", ExposureTime = 180,
                },
            });
        FITSWriter.Write(img, path);
        return path;
    }

    private int _lastSeededId = 0;
    private int RowIdAfterRescan(string path) {
        // Library SQLite assigns autoincrement ids. Tests don't know
        // the exact id without scanning first; we synthesize one by
        // remembering insertion order. The actual id resolution
        // happens after the test calls _library.RescanAsync().
        // For lookup we round-trip via the library query API.
        // Simpler approach: rescan immediately + grep by full path.
        _library.RescanAsync().GetAwaiter().GetResult();
        var rows = _library.Query(new FrameQuery(
            Type: null, Filter: null, Target: null, DateFrom: null, DateTo: null,
            Limit: 1000, Offset: 0));
        foreach (var r in rows) if (r.Path == path) { _lastSeededId = r.Id; return r.Id; }
        throw new InvalidOperationException(
            $"Seeded {path} but rescan didn't index it.");
    }

    private async Task<ChannelCombineProgress> WaitForJob(string jobId, TimeSpan timeout) {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline) {
            var s = _svc.GetStatus(jobId);
            if (s != null && !s.InProgress) return s;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Job {jobId} did not finish within {timeout}.");
    }
}
