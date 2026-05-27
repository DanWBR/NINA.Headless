using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using System.Diagnostics;
using NINA.Image.Editor;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Editor;
using NINA.Polaris.Services.PlateSolving;
using NINA.Polaris.Services.Sky;
using NINA.Polaris.Services.Studio;

namespace NINA.Polaris.Test.E2E;

/// <summary>
/// End-to-end pipeline check against the real captures under
/// <c>test_data/</c> at the repo root. Exercises FrameLibrary →
/// MasterFrame → Calibration → BatchStacking → ChannelCombine →
/// ColorCalibration → ImageEditService against actual SHO mono and
/// OSC color sessions, with per-step timings logged to the test
/// output.
///
/// Marked <c>[Explicit]</c> + <c>Category=E2E</c> so the normal
/// <c>dotnet test</c> run skips it. Trigger explicitly with
/// <c>dotnet test --filter "FullyQualifiedName~RealDataPipeline"</c>
/// or <c>dotnet test --filter "Category=E2E"</c>.
///
/// Ordering matters: each test method writes outputs the next one
/// consumes (master frames, calibrated lights, integrated masters,
/// the SHO RGB composite). Run the fixture as a whole rather than
/// cherry-picking individual tests.
///
/// What this fixture does not exercise (intentional):
///   - GraXpert ONNX, browser-only pipeline.
///   - Editor sliders, those are Alpine.js UI; we exercise the
///     server-side ImageEditService.LoadAsync + RenderPreviewAsync
///     instead.
///
/// PCC (Step 09) is conditional: it runs only when ASTAP is on PATH
/// and an APASS DB has been populated under
/// <c>src/NINA.Polaris/wwwroot/catalogs/apass/apass.db</c> (via
/// <c>scripts/download-apass.py</c>). Otherwise the step is
/// <c>Inconclusive</c> with an actionable message.
/// </summary>
[TestFixture, Category("E2E"),
 Explicit("Runs against test_data/, takes minutes per test")]
public class RealDataPipelineTests {

    private const string E2eRigName = "E2E_TestRun";

    // How many lights to feed through calibration + integration per
    // filter. Capped to keep wall-clock under control, real test_data
    // sessions are 50-100 lights each at 20MP.
    private const int LightsPerFilterCap = 20;

    private string _testDataRoot = null!;
    private string _tempStudio   = null!;
    private ProfileService _profile = null!;
    private FrameLibraryService _library = null!;
    private MasterFrameService _masters = null!;
    private CalibrationService _calibrate = null!;
    private BatchStackingService _integrate = null!;
    private ChannelCombineService _combine = null!;
    private ColorCalibrationService _colorcal = null!;
    private ImageEditService _editor = null!;
    private PlateSolveService _platesolve = null!;
    private ApassCatalog _apass = null!;
    private IConfiguration _cfg = null!;

    [OneTimeSetUp]
    public void GlobalSetup() {
        _testDataRoot = FindTestDataRoot();
        _tempStudio = Path.Combine(Path.GetTempPath(),
            "polaris-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempStudio);

        // Override Studio:Directory so the SQLite cache + thumbs land
        // in the temp dir, not in %LocalAppData% where they would
        // pollute the dev's real cache.
        _cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Studio:Directory"] = _tempStudio
            })
            .Build();

        _profile = new ProfileService(_cfg, NullLogger<ProfileService>.Instance);
        _profile.Active.ImageOutputDir = _testDataRoot;
        _profile.ActiveEquipmentProfile.Name = E2eRigName;

        _library = new FrameLibraryService(_profile, _cfg,
            NullLogger<FrameLibraryService>.Instance);
        _masters = new MasterFrameService(_library, _profile,
            NullLogger<MasterFrameService>.Instance);
        _calibrate = new CalibrationService(_library, _profile,
            NullLogger<CalibrationService>.Instance);
        _integrate = new BatchStackingService(_library, _profile,
            NullLogger<BatchStackingService>.Instance);
        _combine = new ChannelCombineService(_library, _profile,
            NullLogger<ChannelCombineService>.Instance);

        // ApassCatalog points at the real bundled DB (populated by
        // scripts/download-apass.py against VizieR II/336/apass9). If
        // the DB is missing, Step 09 (PCC) gates itself Inconclusive.
        // We resolve the repo-root wwwroot path so the catalog the
        // production server uses is the catalog the fixture uses.
        var repoWebRoot = Path.Combine(
            Directory.GetParent(_testDataRoot)!.FullName,
            "src", "NINA.Polaris", "wwwroot");
        _apass = new ApassCatalog(
            new FakeWebHostEnvironment(repoWebRoot),
            NullLogger<ApassCatalog>.Instance);
        _colorcal = new ColorCalibrationService(_library, _profile, _apass,
            NullLogger<ColorCalibrationService>.Instance);
        _editor = new ImageEditService(_profile,
            NullLogger<ImageEditService>.Instance);

        // PlateSolveService picks up ASTAP via the default Windows /
        // Linux path lookup inside AstapSolver. The 2-arg constructor
        // wires every backend (ASTAP / PS3 / astrometry.net online +
        // local) with no-op loggers; Step 09 picks the configured
        // primary (ASTAP by default).
        _platesolve = new PlateSolveService(_cfg,
            NullLogger<PlateSolveService>.Instance);

        Log($"test_data root: {_testDataRoot}");
        Log($"temp studio:    {_tempStudio}");
        Log($"rig name:       {E2eRigName}");
        Log($"APASS DB:       {_apass.DbPath} (available={_apass.IsAvailable})");
        Log($"Plate solver:   {_platesolve.PrimarySolver.DisplayName} " +
            $"at {_platesolve.SolverPath} (available={_platesolve.IsAvailable})");
        Log($"output:         {_testDataRoot}/{E2eRigName}/...");
    }

    [OneTimeTearDown]
    public void GlobalTeardown() {
        try { _editor?.Dispose(); } catch { }
        // Opt-in to preserve outputs for visual inspection. Set
        // POLARIS_E2E_KEEP_OUTPUT=1 in the environment and the rig
        // dir under test_data/ survives the run, including masters,
        // calibrated lights, integrated MASTERLIGHTs, SHO composed
        // RGB, BG-neutralised RGB, PCC output, and the synthetic
        // pcc-synthetic-rgb-*.fits / *_pcc.fits Step 09 generates.
        // Default: clean up so a re-run starts fresh.
        var keep = Environment.GetEnvironmentVariable("POLARIS_E2E_KEEP_OUTPUT");
        var rigDir = Path.Combine(_testDataRoot, E2eRigName);
        if (!string.IsNullOrEmpty(keep) && keep != "0") {
            Log($"POLARIS_E2E_KEEP_OUTPUT set, preserving outputs at: {rigDir}");
            if (Directory.Exists(_tempStudio)) {
                try { Directory.Delete(_tempStudio, recursive: true); } catch { }
            }
            return;
        }
        // Sweep the rig output dir created under test_data so a re-run
        // starts clean. Leave the source captures intact.
        if (Directory.Exists(rigDir)) {
            try { Directory.Delete(rigDir, recursive: true); }
            catch (Exception ex) {
                Log($"Could not clean rig output dir: {ex.Message}");
            }
        }
        if (Directory.Exists(_tempStudio)) {
            try { Directory.Delete(_tempStudio, recursive: true); } catch { }
        }
    }

    // ─── tests ───────────────────────────────────────────────────────

    [Test, Order(1)]
    public async Task Step01_Rescan_FindsAllTestFrames() {
        var sw = Stopwatch.StartNew();
        await _library.RescanAsync();
        Log($"Rescan: {sw.Elapsed.TotalSeconds:0.0}s");

        var all = _library.Query(new FrameQuery(null, null, null, null, null, 5000, 0));
        Log($"  Indexed {all.Count} frames");
        var byType = all.GroupBy(f => f.ImageType)
                        .Select(g => $"{g.Key}={g.Count()}")
                        .OrderBy(s => s);
        Log($"  By type:   {string.Join(", ", byType)}");
        var byFilter = all.Where(f => f.ImageType == "Light")
                          .GroupBy(f => f.Filter ?? "")
                          .Select(g => $"{(string.IsNullOrEmpty(g.Key) ? "(none)" : g.Key)}={g.Count()}")
                          .OrderBy(s => s);
        Log($"  By filter: {string.Join(", ", byFilter)}");
        var byTarget = all.Where(f => f.ImageType == "Light")
                          .GroupBy(f => f.Target ?? "")
                          .Select(g => $"{g.Key}={g.Count()}")
                          .OrderBy(s => s);
        Log($"  By target: {string.Join(", ", byTarget)}");

        Assert.That(all.Count, Is.GreaterThan(300),
            "Expected hundreds of frames across mono+color test_data");
    }

    [Test, Order(2)]
    public async Task Step02_Mono_M16_Ha_FullPipeline() {
        await RunMonoFilterPipeline("H");
    }

    [Test, Order(3)]
    public async Task Step03_Mono_M16_OIII_FullPipeline() {
        await RunMonoFilterPipeline("O");
    }

    [Test, Order(4)]
    public async Task Step04_Mono_M16_SII_FullPipeline() {
        await RunMonoFilterPipeline("S");
    }

    [Test, Order(5)]
    public async Task Step05_Mono_M16_SHO_Combine() {
        // Find the three per-filter integrated masters from Steps 2-4
        // and PixelMath them into a single Hubble SHO RGB.
        await _library.RescanAsync();
        var masters = _library.Query(new FrameQuery(
            Type: "MASTERLIGHT", Filter: null, Target: "M 16",
            DateFrom: null, DateTo: null, Limit: 50, Offset: 0));
        var haMaster   = masters.FirstOrDefault(m => m.Filter == "H");
        var oiiiMaster = masters.FirstOrDefault(m => m.Filter == "O");
        var siiMaster  = masters.FirstOrDefault(m => m.Filter == "S");
        if (haMaster == null || oiiiMaster == null || siiMaster == null) {
            Assert.Inconclusive(
                "Need per-filter masters from Steps 02-04. " +
                $"Found Ha={haMaster?.Id.ToString() ?? "missing"}, " +
                $"OIII={oiiiMaster?.Id.ToString() ?? "missing"}, " +
                $"SII={siiMaster?.Id.ToString() ?? "missing"}");
        }

        Log($"Combining masters Ha={haMaster!.Id} OIII={oiiiMaster!.Id} SII={siiMaster!.Id}");
        var req = new ChannelCombineService.ChannelCombineRequest(
            Mode: ChannelCombineService.Modes.PixelMath,
            ChannelMap: new() {
                new("Ha",   haMaster.Id),
                new("OIII", oiiiMaster.Id),
                new("SII",  siiMaster.Id),
            },
            Register: true,
            Normalize: true,
            // Hubble SHO palette: R=SII, G=Ha, B=OIII
            Expressions: new() { "SII", "Ha", "OIII" });
        var jobId = _combine.StartJob(req);
        var status = await WaitFor(() => _combine.GetStatus(jobId),
            TimeSpan.FromMinutes(15), "Combine SHO");
        Assert.That(status.Stage, Is.EqualTo("done"),
            $"Combine failed: {status.Error}");
        Assert.That(status.OutputChannels, Is.EqualTo(3));
        Log($"  -> RGB master {status.OutputPath} " +
            $"({new FileInfo(status.OutputPath!).Length / 1024 / 1024} MB)");
    }

    [Test, Order(6)]
    public async Task Step06_Color_NGC5746_FullPipeline() {
        await _library.RescanAsync();
        // OSC calibration frames live under test_data/color/NGC 5746/
        // but flats/darks/biases don't carry OBJECT in their FITS
        // headers, so we match on path prefix instead of Target.
        var targetSlug = Path.DirectorySeparatorChar + "NGC 5746" +
                         Path.DirectorySeparatorChar;
        var lights = _library.Query(new FrameQuery(
                Type: "Light", Filter: null, Target: "NGC 5746",
                DateFrom: null, DateTo: null, Limit: 500, Offset: 0))
            .Take(LightsPerFilterCap).ToList();
        var darks  = _library.Query(new FrameQuery(
                Type: "Dark", Filter: null, Target: null,
                DateFrom: null, DateTo: null, Limit: 500, Offset: 0))
            .Where(f => f.Path.Contains(targetSlug))
            .Take(10).ToList();
        var flats  = _library.Query(new FrameQuery(
                Type: "Flat", Filter: null, Target: null,
                DateFrom: null, DateTo: null, Limit: 500, Offset: 0))
            .Where(f => f.Path.Contains(targetSlug))
            .Take(10).ToList();
        var biases = _library.Query(new FrameQuery(
                Type: "Bias", Filter: null, Target: null,
                DateFrom: null, DateTo: null, Limit: 500, Offset: 0))
            .Where(f => f.Path.Contains(targetSlug))
            .Take(20).ToList();
        Log($"NGC 5746: lights={lights.Count} darks={darks.Count} " +
            $"flats={flats.Count} biases={biases.Count}");

        var darkId = await CreateMaster(MasterType.Dark, darks);
        var flatId = await CreateMaster(MasterType.Flat, flats);
        var biasId = await CreateMaster(MasterType.Bias, biases);

        await Calibrate(lights.Select(l => l.Id).ToList(), darkId, flatId, biasId);

        var calibrated = await WaitForCalibratedAsync(
            filter: null, target: "NGC 5746",
            expectedAtLeast: lights.Count / 2);
        Log($"Calibrated frames indexed: {calibrated.Count}");
        Assume.That(calibrated.Count, Is.GreaterThan(2));

        await Integrate(calibrated.Select(f => f.Id).ToList(), "NGC 5746");
    }

    [Test, Order(7)]
    public async Task Step07_ColorCalibration_BG_On_M16_RGB() {
        await _library.RescanAsync();
        // The SHO combine output lives under integrated/M_16/composed/.
        var rgb = _library.Query(new FrameQuery(
                null, null, "M 16", null, null, 500, 0))
            .Where(f => f.Path.Contains(
                Path.DirectorySeparatorChar + "composed" +
                Path.DirectorySeparatorChar))
            .OrderByDescending(f => f.Id)
            .FirstOrDefault();
        if (rgb == null) {
            Assert.Inconclusive("Run Step05 first to produce an RGB master");
        }

        Log($"Color cal BG on {rgb!.Path}");
        var req = new ColorCalibrationService.ColorCalibrationRequest(
            FrameId: rgb.Id,
            Mode: ColorCalibrationService.Modes.BgNeutral,
            BgSample: "auto");
        var jobId = _colorcal.StartJob(req);
        var status = await WaitFor(() => _colorcal.GetStatus(jobId),
            TimeSpan.FromMinutes(5), "ColorCal BG");
        Assert.That(status.Stage, Is.EqualTo("done"),
            $"Color cal failed: {status.Error}");
        Log($"  -> {status.OutputPath}");
        Log($"     offsets R={status.OffsetR:0.0} G={status.OffsetG:0.0} B={status.OffsetB:0.0}");
        Log($"     gains   R={status.GainR:0.000} G={status.GainG:0.000} B={status.GainB:0.000}");
    }

    [Test, Order(8)]
    public async Task Step08_Editor_LoadAndRender_OnIntegratedMaster() {
        await _library.RescanAsync();
        // Prefer a 3-channel master (SHO composed) so the editor's
        // RGB path gets exercised; fall back to any MASTERLIGHT.
        var candidates = _library.Query(new FrameQuery(
            Type: "MASTERLIGHT", Filter: null, Target: null,
            DateFrom: null, DateTo: null, Limit: 50, Offset: 0));
        var master = candidates.FirstOrDefault(m =>
                m.Path.Contains(Path.DirectorySeparatorChar + "composed" +
                                Path.DirectorySeparatorChar))
            ?? candidates.FirstOrDefault();
        if (master == null) {
            Assume.That(master, Is.Not.Null,
                "No master available, run earlier steps first");
            return;
        }

        Log($"Editor: load {master.Path}");
        var swLoad = Stopwatch.StartNew();
        var session = await _editor.LoadAsync(master.Path);
        Log($"  Load: {swLoad.Elapsed.TotalSeconds:0.0}s " +
            $"-> session={session?.SessionId} " +
            $"({session?.Width}x{session?.Height}x{session?.Channels})");
        Assert.That(session, Is.Not.Null);

        var swRender = Stopwatch.StartNew();
        var edits = EditParams.Defaults with {
            Light = new LightParams(Exposure: 0.3, Contrast: 0.2),
        };
        var bytes = await _editor.RenderPreviewAsync(
            session!.SessionId, edits, maxDim: 1600, quality: 85);
        Log($"  Render: {swRender.Elapsed.TotalSeconds:0.0}s " +
            $"-> JPEG {bytes?.Length} bytes");
        Assert.That(bytes, Is.Not.Null);
        Assert.That(bytes!.Length, Is.GreaterThan(1000));

        var swHist = Stopwatch.StartNew();
        var hist = await _editor.ComputeHistogramAsync(session.SessionId, edits);
        Log($"  Histogram: {swHist.Elapsed.TotalSeconds:0.0}s " +
            $"-> {hist?.Length} bins");
        Assert.That(hist, Is.Not.Null);

        _editor.Release(session.SessionId);
    }

    [Test, Order(9)]
    public async Task Step09_PCC_OnSHO_RGB_WithRealAstapAndApass() {
        // Gate: ASTAP binary present + APASS DB populated. Without
        // either, mark Inconclusive (not Failed) so devs who haven't
        // set up the offline catalog or solver don't see red.
        if (!_platesolve.IsAvailable) {
            Assert.Inconclusive(
                "ASTAP not detected. Install from https://www.hnsky.org/astap.htm " +
                "+ a star database (D50 / H17 / H18).");
        }
        if (!_apass.IsAvailable) {
            Assert.Inconclusive(
                $"APASS DB not populated at {_apass.DbPath}. " +
                "Run: python scripts/download-apass.py");
        }
        Log($"PCC preflight: ASTAP={_platesolve.SolverPath}");
        Log($"PCC preflight: APASS={_apass.DbPath} " +
            $"({_apass.StarCount:N0} stars)");

        // The Polaris pipeline through Step 05 produces a SHO RGB
        // master but BatchStackingService currently emits an
        // integrated master that ASTAP cannot plate-solve (~500 stars
        // detected, zero quads match the catalog — likely a
        // cross-frame alignment smearing bug; tracked as a separate
        // task). Until that lands, validate the PCC chain against a
        // known-plate-solvable input: the Siril-stacked Ha master
        // shipped in test_data, replicated into a synthetic 3-channel
        // RGB so PCC has something to compute gains on.
        var siril = Path.Combine(_testDataRoot,
            "mono", "M 16", "H", "result_H_3060s.fit");
        if (!File.Exists(siril)) {
            Assert.Inconclusive($"Missing reference fixture: {siril}");
        }

        // Build a synthetic 3-channel RGB from the Siril Ha master.
        // PCC computes per-channel gains from catalog B-V index; since
        // all three planes are identical here, the resulting gains
        // should land close to unity. The point of the test is to
        // exercise the full chain (ASTAP solve -> WCS stamp ->
        // catalog cone-search -> per-star photometry -> gain fit ->
        // FITS write), not the specific gain values.
        NINA.Image.ImageData.BaseImageData mono;
        using (var fs = File.OpenRead(siril)) {
            mono = NINA.Image.FileFormat.FITS.FITSReader.Read(fs);
        }
        int w = mono.Properties.Width;
        int h = mono.Properties.Height;
        var rgb3 = new ushort[w * h * 3];
        Array.Copy(mono.Data, 0, rgb3, 0,             w * h);
        Array.Copy(mono.Data, 0, rgb3, w * h,         w * h);
        Array.Copy(mono.Data, 0, rgb3, w * h * 2,     w * h);

        var rigDir = Path.Combine(_testDataRoot, E2eRigName);
        Directory.CreateDirectory(rigDir);
        var pccSrc = Path.Combine(rigDir,
            "pcc-synthetic-rgb-" + Guid.NewGuid().ToString("N")[..8] + ".fits");
        var rgbImg = new NINA.Image.ImageData.BaseImageData(rgb3,
            mono.Properties with { Channels = 3 },
            mono.MetaData);
        NINA.Image.FileFormat.FITS.FITSWriter.Write(rgbImg, pccSrc);
        Log($"PCC source (synthetic 3-channel from Siril Ha): {pccSrc}");

        // ── Plate-solve the synthetic RGB ─────────────────────────
        var swSolve = Stopwatch.StartNew();
        var solveResult = await _platesolve.SolveAsync(pccSrc,
            new PlateSolveOptions {
                HintRa = 18.31,        // hours
                HintDec = -13.78,      // degrees
                SearchRadiusDeg = 5,
                FovDeg = 2.0,
            });
        Log($"  Plate-solve: {swSolve.Elapsed.TotalSeconds:0.0}s " +
            $"-> success={solveResult.Success} solver={solveResult.SolverUsed}");
        Assert.That(solveResult.Success, Is.True,
            $"Plate-solve of Siril reference failed: {solveResult.Error}");
        Log($"     RA={solveResult.RaHours:0.0000}h " +
            $"Dec={solveResult.DecDeg:+0.0000;-0.0000}deg " +
            $"scale={solveResult.ScaleArcsecPerPixel:0.00}\"/px " +
            $"rot={solveResult.RotationDeg:0.0}deg");

        // AstapSolver's proxy path wrote WCS back into the synthetic
        // 3-channel FITS. Reindex so FrameLibrary sees the updated
        // headers, then resolve the row id PCC needs. The global
        // query caps at 500 rows ordered by date_obs DESC; our
        // synthetic master has no DATE-OBS so it sorts to the bottom
        // and falls outside the window when test_data has many older
        // captures. Query by image_type instead so we only see
        // calibration outputs.
        await _library.RescanAsync();
        FrameRow? rgbSolved = null;
        foreach (var type in new[] { "Light", "MASTERLIGHT", "MASTERCAL", "MASTER" }) {
            rgbSolved = _library.Query(new FrameQuery(
                    Type: type, Filter: null, Target: null,
                    DateFrom: null, DateTo: null, Limit: 500, Offset: 0))
                .FirstOrDefault(f => string.Equals(f.Path, pccSrc,
                    StringComparison.OrdinalIgnoreCase));
            if (rgbSolved != null) break;
        }
        if (rgbSolved == null) {
            // Last-resort path probe: re-rescan once more and search
            // unfiltered. If still missing we have a real indexing
            // bug, fail loud with the path so we can grep for it.
            await _library.RescanAsync();
            var allRows = _library.Query(new FrameQuery(
                null, null, null, null, null, 500, 0));
            Log($"  rescan saw {allRows.Count} rows; sample paths:");
            foreach (var r in allRows.Take(5)) Log($"    {r.ImageType}: {r.Path}");
            Assert.Fail($"Synthetic RGB master not indexed after rescan: {pccSrc}");
        }

        // ── Run PCC against the plate-solved synthetic RGB ────────
        var swPcc = Stopwatch.StartNew();
        var pccJobId = _colorcal.StartJob(
            new ColorCalibrationService.ColorCalibrationRequest(
                FrameId: rgbSolved.Id,
                Mode: ColorCalibrationService.Modes.Photometric,
                BgSample: "auto"));
        var pcc = await WaitFor(() => _colorcal.GetStatus(pccJobId),
            TimeSpan.FromMinutes(5), "ColorCal PCC");
        Log($"  PCC: {swPcc.Elapsed.TotalSeconds:0.0}s " +
            $"-> stage={pcc.Stage}");
        Assert.That(pcc.Stage, Is.EqualTo("done"),
            $"PCC failed: {pcc.Error}");
        Log($"     -> {pcc.OutputPath}");
        Log($"     matched stars: {pcc.MatchedStars}");
        Log($"     offsets R={pcc.OffsetR:0.0} G={pcc.OffsetG:0.0} B={pcc.OffsetB:0.0}");
        Log($"     gains   R={pcc.GainR:0.000} G={pcc.GainG:0.000} B={pcc.GainB:0.000}");

        Assert.That(pcc.OutputPath, Is.Not.Null.And.Not.Empty);
        Assert.That(File.Exists(pcc.OutputPath!), Is.True);
        // M 16 field is dense; APASS DR9 capped at Vmag <= 13 covers
        // a few hundred stars in a 2° FOV. Demand at least 10
        // matched (the PCC code requires >=5 to fit a gain at all,
        // but 10 is the threshold below which the result is too
        // noisy to be useful).
        Assert.That(pcc.MatchedStars, Is.GreaterThanOrEqualTo(10),
            "Expected at least 10 matched APASS stars in M 16 field");
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private async Task RunMonoFilterPipeline(string filter) {
        await _library.RescanAsync();
        // Lights: filter + target. Flats: filter only (the FITS header
        // for flats does not carry OBJECT in NINA's default writer, so
        // a Target= filter would miss them). Darks + biases: type
        // only, plus an exposure match to defend against accidentally
        // pulling a flat-dark when present.
        var lights = _library.Query(new FrameQuery(
                Type: "Light", Filter: filter, Target: "M 16",
                DateFrom: null, DateTo: null, Limit: 500, Offset: 0))
            .Take(LightsPerFilterCap).ToList();
        var flats  = _library.Query(new FrameQuery(
                Type: "Flat", Filter: filter, Target: null,
                DateFrom: null, DateTo: null, Limit: 500, Offset: 0))
            // Mono flats live under test_data/mono/... so any flat
            // with matching filter is the right one for these tests.
            .Where(f => f.Path.Contains(Path.DirectorySeparatorChar
                                        + "mono"
                                        + Path.DirectorySeparatorChar))
            .Take(10).ToList();
        var darks  = _library.Query(new FrameQuery(
                Type: "Dark", Filter: null, Target: null,
                DateFrom: null, DateTo: null, Limit: 500, Offset: 0))
            .Where(f => f.Path.Contains(Path.DirectorySeparatorChar
                                        + "mono"
                                        + Path.DirectorySeparatorChar))
            .Where(f => Math.Abs(f.ExposureSec - 60) < 1)
            .Take(10).ToList();
        var biases = _library.Query(new FrameQuery(
                Type: "Bias", Filter: null, Target: null,
                DateFrom: null, DateTo: null, Limit: 500, Offset: 0))
            .Where(f => f.Path.Contains(Path.DirectorySeparatorChar
                                        + "mono"
                                        + Path.DirectorySeparatorChar))
            .Take(40).ToList();
        Log($"Mono {filter}: lights={lights.Count} darks={darks.Count} " +
            $"flats={flats.Count} biases={biases.Count}");
        Assume.That(lights.Count, Is.GreaterThanOrEqualTo(2),
            $"Need at least 2 lights for filter {filter}");

        var darkId = await CreateMaster(MasterType.Dark, darks);
        var flatId = await CreateMaster(MasterType.Flat, flats);
        var biasId = await CreateMaster(MasterType.Bias, biases);

        await Calibrate(lights.Select(l => l.Id).ToList(),
            darkId, flatId, biasId);

        var calibrated = await WaitForCalibratedAsync(
            filter: filter, target: "M 16",
            expectedAtLeast: lights.Count / 2);
        Log($"Calibrated {filter} frames indexed: {calibrated.Count}");
        Assume.That(calibrated.Count, Is.GreaterThan(2));

        await Integrate(calibrated.Select(f => f.Id).ToList(),
            $"M 16 {filter}");
    }

    private async Task<List<FrameRow>> WaitForCalibratedAsync(
            string? filter, string target, int expectedAtLeast) {
        // CalibrationService also fires a background rescan that
        // races our explicit one (same lock-non-blocking semantics
        // as masters). Poll until calibrated frames show up.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        List<FrameRow> calibrated = new();
        // Path layout: {testDataRoot}/{rig}/calibrated/{sanitizedTarget}/[filter|L]/*
        var targetSlug = SanitizeTargetSlug(target);
        var targetDir = Path.Combine(_testDataRoot, E2eRigName,
                                     "calibrated", targetSlug);
        while (DateTime.UtcNow < deadline) {
            await _library.RescanAsync();
            // Anchor on the per-target calibrated subdir so we don't
            // pull rows from sibling targets (e.g. M16/H lingering
            // after Step02-04 when Step06 runs against NGC 5746).
            calibrated = _library.Query(new FrameQuery(
                    Type: "Light", Filter: filter, Target: null,
                    DateFrom: null, DateTo: null, Limit: 500, Offset: 0))
                .Where(f => f.Path.StartsWith(targetDir,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (calibrated.Count >= expectedAtLeast) break;
            await Task.Delay(500);
        }
        if (calibrated.Count == 0) {
            // Diagnostics on failure: peek at disk to distinguish
            // "calibration wrote nothing" from "rescan missed it".
            if (Directory.Exists(targetDir)) {
                var onDisk = Directory.GetFiles(targetDir, "*.fit*",
                    SearchOption.AllDirectories);
                Log($"  calibrated dir has {onDisk.Length} files on disk " +
                    $"(but 0 indexed). First few: " +
                    string.Join(", ", onDisk.Take(3)
                        .Select(p => Path.GetFileName(p))));
            } else {
                Log($"  calibrated dir does not exist on disk: {targetDir}");
            }
        }
        return calibrated;
    }

    private static string SanitizeTargetSlug(string target) {
        // Mirror CalibrationService.Sanitize: invalid path chars are
        // replaced and spaces become underscores. Test_data uses
        // "M 16" + "NGC 5746" both of which need this transform.
        if (string.IsNullOrWhiteSpace(target)) return "Unknown";
        var slug = target;
        foreach (var c in Path.GetInvalidFileNameChars())
            slug = slug.Replace(c, '_');
        return slug.Replace(' ', '_');
    }

    private async Task<int> CreateMaster(MasterType type,
                                          IReadOnlyList<FrameRow> frames) {
        Assume.That(frames.Count, Is.GreaterThan(2),
            $"Master {type} needs at least 3 input frames");
        Log($"Master {type}: integrating {frames.Count} frames " +
            $"({(frames[0].FileSize * frames.Count) / 1024 / 1024} MB raw)");
        var sw = Stopwatch.StartNew();
        var jobId = _masters.StartJob(
            frames.Select(f => f.Id).ToList(),
            type, IntegrationMethod.SigmaClippedMean);
        var status = await WaitFor(() => _masters.GetStatus(jobId),
            TimeSpan.FromMinutes(15), $"Master {type}");
        Assert.That(status.Stage, Is.EqualTo("done"),
            $"Master {type} failed: {status.Error}");
        Assert.That(File.Exists(status.OutputPath!), Is.True);
        Log($"  -> {status.OutputPath} " +
            $"({new FileInfo(status.OutputPath!).Length / 1024 / 1024} MB) " +
            $"in {sw.Elapsed.TotalSeconds:0.0}s");

        // Master service kicks off its own fire-and-forget rescan and
        // FrameLibrary.RescanAsync silently no-ops if a rescan is
        // already in flight (the SemaphoreSlim is non-blocking). Loop
        // until the new master shows up, otherwise we race the
        // background rescan and the query returns null.
        var masterImageType = type switch {
            MasterType.Bias     => "MASTERBIAS",
            MasterType.Dark     => "MASTERDARK",
            MasterType.Flat     => "MASTERFLAT",
            MasterType.DarkFlat => "MASTERDARKFLAT",
            _                   => "MASTER",
        };
        var rescanDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        FrameRow? row = null;
        while (DateTime.UtcNow < rescanDeadline) {
            await _library.RescanAsync();
            row = _library.Query(new FrameQuery(
                    Type: masterImageType, Filter: null, Target: null,
                    DateFrom: null, DateTo: null, Limit: 500, Offset: 0))
                .FirstOrDefault(r => r.Path == status.OutputPath);
            if (row != null) break;
            await Task.Delay(250);
        }
        Assert.That(row, Is.Not.Null,
            $"Master {type} not indexed after rescan");
        return row!.Id;
    }

    private async Task Calibrate(IReadOnlyList<int> lightIds,
                                 int? masterDarkId, int? masterFlatId,
                                 int? masterBiasId) {
        Log($"Calibrate: {lightIds.Count} lights " +
            $"(dark={masterDarkId} flat={masterFlatId} bias={masterBiasId})");
        var sw = Stopwatch.StartNew();
        var req = new CalibrationService.CalibrationRequest(
            lightIds.ToList(), masterDarkId, masterFlatId, masterBiasId);
        var jobId = _calibrate.StartJob(req);
        var status = await WaitFor(() => _calibrate.GetStatus(jobId),
            TimeSpan.FromMinutes(30), "Calibrate");
        Assert.That(status.Stage, Is.EqualTo("done"),
            $"Calibration failed: {status.Error}");
        Log($"  -> succeeded={status.Succeeded}/{status.Total} " +
            $"failed={status.Failed} in {sw.Elapsed.TotalSeconds:0.0}s");
        Assert.That(status.Succeeded, Is.GreaterThan(0));
    }

    private async Task<int> Integrate(IReadOnlyList<int> calibratedIds,
                                       string label) {
        Log($"Integrate {label}: {calibratedIds.Count} frames");
        var sw = Stopwatch.StartNew();
        var req = new BatchStackingService.IntegrationRequest(
            calibratedIds.ToList(), "SigmaClippedMean");
        var jobId = _integrate.StartJob(req);
        var status = await WaitFor(() => _integrate.GetStatus(jobId),
            TimeSpan.FromMinutes(45), $"Integrate {label}");
        Assert.That(status.Stage, Is.EqualTo("done"),
            $"Integration failed: {status.Error}");
        Assert.That(File.Exists(status.OutputPath!), Is.True);
        var sizeMb = new FileInfo(status.OutputPath!).Length / 1024 / 1024;
        Log($"  -> {status.OutputPath} ({sizeMb} MB) " +
            $"combined={status.Combined}/{calibratedIds.Count} frames " +
            $"dropped={status.Dropped} " +
            $"in {sw.Elapsed.TotalSeconds:0.0}s");
        // Same race as masters, see CreateMaster, retry until the
        // freshly-written MASTERLIGHT shows up in the SQLite index.
        var rescanDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        FrameRow? row = null;
        while (DateTime.UtcNow < rescanDeadline) {
            await _library.RescanAsync();
            row = _library.Query(new FrameQuery(
                    Type: "MASTERLIGHT", Filter: null, Target: null,
                    DateFrom: null, DateTo: null, Limit: 500, Offset: 0))
                .FirstOrDefault(r => r.Path == status.OutputPath);
            if (row != null) break;
            await Task.Delay(250);
        }
        return row?.Id
            ?? throw new InvalidOperationException(
                "Integrated master not indexed after rescan");
    }

    private static string FindTestDataRoot() {
        // Walk up from the test-execution directory looking for a
        // top-level test_data folder. Falls back to repo discovery via
        // the .git marker. Inconclusive (not failure) if neither
        // works, so a dev without test_data on disk doesn't see a
        // confusing red test.
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null) {
            var candidate = Path.Combine(dir.FullName, "test_data");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        Assert.Inconclusive(
            "test_data not found above " +
            TestContext.CurrentContext.TestDirectory);
        return null!;
    }

    private static async Task<TStatus> WaitFor<TStatus>(
            Func<TStatus?> poll, TimeSpan timeout, string label)
            where TStatus : class {
        var start = Stopwatch.StartNew();
        var deadline = DateTime.UtcNow + timeout;
        string? lastStage = null;
        while (DateTime.UtcNow < deadline) {
            var s = poll();
            if (s != null) {
                var stageProp = s.GetType().GetProperty("Stage");
                var inProgressProp = s.GetType().GetProperty("InProgress");
                var stage = stageProp?.GetValue(s) as string;
                if (stage != lastStage) {
                    Log($"  {label}: stage={stage} (+{start.Elapsed.TotalSeconds:0.0}s)");
                    lastStage = stage;
                }
                if (inProgressProp?.GetValue(s) is false) {
                    return s;
                }
            }
            await Task.Delay(500);
        }
        throw new TimeoutException(
            $"{label} did not finish in {timeout}");
    }

    private static void Log(string msg) {
        TestContext.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment {
        public FakeWebHostEnvironment(string root) {
            ContentRootPath = root;
            WebRootPath = root;
            ContentRootFileProvider = new NullFileProvider();
            WebRootFileProvider = new NullFileProvider();
        }
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "NINA.Polaris.Test.E2E";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
    }
}
