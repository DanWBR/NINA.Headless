using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Headless.Services;
using NINA.Headless.Services.External;

namespace NINA.Headless.Test;

/// <summary>
/// Pins the pure (non-process-launching) parts of GraXpertService:
///   - CLI arg-builder per operation (BGE/Decon/Denoise)
///   - Output suffix + default path conventions
///   - Binary candidate enumeration for the Settings diagnostic
///   - Job state machine init + cancel flag
///
/// Tests that actually launch the graxpert binary would require
/// the install + a real FITS — covered by the end-to-end
/// verification section of the plan, not this file.
/// </summary>
[TestFixture]
public class GraXpertServiceTests {

    private GraXpertService _gx = null!;
    private ProfileService _profile = null!;

    [SetUp]
    public void Setup() {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        _profile = new ProfileService(config, NullLogger<ProfileService>.Instance);
        _gx = new GraXpertService(config, _profile, NullLogger<GraXpertService>.Instance);
    }

    // --- Output naming ----------------------------------------------

    [Test]
    public void OutputSuffix_DistinctPerOperation() {
        // Suffixes are part of the contract — once shipped the user
        // depends on _bge/_decon/_denoise to spot which file is
        // which in the FILES tab. Pin the values so a refactor
        // doesn't silently change them.
        Assert.That(GraXpertService.OutputSuffix(GraXpertOperation.BackgroundExtraction),
            Is.EqualTo("_bge"));
        Assert.That(GraXpertService.OutputSuffix(GraXpertOperation.Deconvolution),
            Is.EqualTo("_decon"));
        Assert.That(GraXpertService.OutputSuffix(GraXpertOperation.Denoising),
            Is.EqualTo("_denoise"));
    }

    [Test]
    public void DefaultOutputPath_AppendsSuffixToStem() {
        // Sibling next to input, suffix on the stem, original extension
        // preserved when it's an image format we know.
        var input = Path.Combine("C:", "astro", "M81", "light_001.fits");
        var output = GraXpertService.DefaultOutputPath(input, GraXpertOperation.BackgroundExtraction);
        Assert.That(Path.GetFileName(output), Is.EqualTo("light_001_bge.fits"));
        Assert.That(Path.GetDirectoryName(output), Is.EqualTo(Path.GetDirectoryName(input)));
    }

    [Test]
    public void DefaultOutputPath_UnknownExtension_FallsBackToFits() {
        // GraXpert's canonical output is FITS; a hypothetical input
        // with no extension (or a non-image extension) should still
        // produce a .fits sibling.
        var input = Path.Combine("/tmp", "snapshot");
        var output = GraXpertService.DefaultOutputPath(input, GraXpertOperation.Denoising);
        Assert.That(Path.GetExtension(output), Is.EqualTo(".fits"));
        Assert.That(Path.GetFileNameWithoutExtension(output), Is.EqualTo("snapshot_denoise"));
    }

    // --- BuildArgs --------------------------------------------------

    [Test]
    public void BuildArgs_Bge_IncludesCorrectionAndSmoothing() {
        var args = _gx.BuildArgs("input.fits", "/out/input_bge.fits",
            new GraXpertOptions(
                Operation: GraXpertOperation.BackgroundExtraction,
                Correction: "Subtraction",
                Smoothing: 0.7));

        // -cli before -cmd, -cmd before per-op flags. Without -cli
        // GraXpert launches the GUI and the call hangs.
        var cliIdx = args.IndexOf("-cli");
        var cmdIdx = args.IndexOf("-cmd");
        Assert.That(cliIdx, Is.GreaterThan(0));
        Assert.That(cmdIdx, Is.GreaterThan(cliIdx));
        Assert.That(args, Does.Contain("background-extraction"));
        Assert.That(args, Does.Contain("-correction Subtraction"));
        Assert.That(args, Does.Contain("-smoothing 0.7"));
        // Output is stripped of extension so GraXpert's own append
        // produces the right filename.
        Assert.That(args, Does.Contain("input_bge"));
        Assert.That(args, Does.Not.Contain("input_bge.fits\""),
            "Output should be passed without extension");
    }

    [Test]
    public void BuildArgs_Decon_IncludesStrengthAndPsfSize() {
        var args = _gx.BuildArgs("/in/master.fits", "/out/master_decon.fits",
            new GraXpertOptions(
                Operation: GraXpertOperation.Deconvolution,
                DeconStrength: 0.35,
                DeconPsfSize: 3.5));

        Assert.That(args, Does.Contain("deconvolution"));
        Assert.That(args, Does.Contain("-strength 0.35"));
        Assert.That(args, Does.Contain("-psfsize 3.5"));
    }

    [Test]
    public void BuildArgs_Denoise_IncludesStrengthOnly() {
        var args = _gx.BuildArgs("/in/master.fits", "/out/master_denoise.fits",
            new GraXpertOptions(
                Operation: GraXpertOperation.Denoising,
                DenoiseStrength: 0.42));

        Assert.That(args, Does.Contain("denoising"));
        Assert.That(args, Does.Contain("-strength 0.42"));
        // Denoising shouldn't carry BGE-only flags into its arg list.
        Assert.That(args, Does.Not.Contain("-correction"));
        Assert.That(args, Does.Not.Contain("-smoothing"));
        Assert.That(args, Does.Not.Contain("-psfsize"));
    }

    [Test]
    public void BuildArgs_SaveBackground_AppendsBgFlag() {
        var args = _gx.BuildArgs("input.fits", "/out/input_bge.fits",
            new GraXpertOptions(
                Operation: GraXpertOperation.BackgroundExtraction,
                SaveBackground: true));
        Assert.That(args, Does.Contain(" -bg"));
    }

    [Test]
    public void BuildArgs_AiVersion_AppendedWhenSet() {
        var args = _gx.BuildArgs("input.fits", "/out/input_bge.fits",
            new GraXpertOptions(
                Operation: GraXpertOperation.BackgroundExtraction,
                AiVersion: "1.1"));
        Assert.That(args, Does.Contain("-ai_version 1.1"));
    }

    // --- Locator + version probe ------------------------------------

    [Test]
    public void EnumerateBinaryCandidates_AlwaysHasEntries() {
        var list = _gx.EnumerateBinaryCandidates();
        Assert.That(list, Is.Not.Null);
        Assert.That(list.Count, Is.GreaterThan(0));
    }

    [Test]
    public void SupportsDeconvolution_RequiresV3Plus() {
        // Without a binary on disk, Version returns "" and the
        // feature flag must be false (would otherwise silently let
        // the user submit decon requests that the CLI rejects).
        if (_gx.IsAvailable) Assert.Ignore("GraXpert installed; cannot test the missing-version path");
        Assert.That(_gx.SupportsDeconvolution, Is.False);
        Assert.That(_gx.SupportsDenoising, Is.False);
    }

    // --- Job lifecycle ----------------------------------------------

    [Test]
    public void StartBatch_RecordsTotalAndInitializesProgress() {
        // Doesn't actually launch GraXpert (the subprocess will fail
        // in the unavailable case and the job will mark failures),
        // but the immediate return must reflect the requested batch
        // size so the UI can render the progress bar right away.
        if (_gx.IsAvailable) Assert.Ignore("GraXpert installed; would launch real processes");
        var job = _gx.StartBatch(new GraXpertBatchRequest(
            new List<string> { "/tmp/a.fits", "/tmp/b.fits" },
            new GraXpertOptions(Operation: GraXpertOperation.BackgroundExtraction)));
        Assert.That(job.Total, Is.EqualTo(2));
        Assert.That(job.Done, Is.EqualTo(0));
        Assert.That(job.Operation, Is.EqualTo(GraXpertOperation.BackgroundExtraction));
        Assert.That(_gx.GetJob(job.JobId), Is.SameAs(job));
    }

    [Test]
    public void CancelJob_UnknownId_ReturnsFalse() {
        Assert.That(_gx.CancelJob("nope"), Is.False);
    }

    [Test]
    public void GetJob_UnknownId_ReturnsNull() {
        Assert.That(_gx.GetJob("nope"), Is.Null);
    }

    // --- Error-path of ProcessFrameAsync ----------------------------

    [Test]
    public async Task ProcessFrameAsync_WhenNotAvailable_ReturnsErrorResult() {
        // Defensive — endpoints already gate on IsAvailable but the
        // service must fail gracefully even if called bare.
        if (_gx.IsAvailable) Assert.Ignore("GraXpert installed on test host");
        var res = await _gx.ProcessFrameAsync("/tmp/whatever.fits",
            new GraXpertOptions(Operation: GraXpertOperation.BackgroundExtraction),
            CancellationToken.None);
        Assert.That(res.Error, Is.Not.Null);
        Assert.That(res.OutputPath, Is.EqualTo(""));
    }

    [Test]
    public async Task ProcessFrameAsync_InputMissing_ReturnsErrorResult() {
        // Even if GraXpert were installed, we should pre-check the
        // input so the user gets a clear error instead of a CLI exit
        // code. Without the install + missing input, the IsAvailable
        // branch fires first; this test still asserts the response
        // shape (Error set, OutputPath empty).
        var res = await _gx.ProcessFrameAsync("/no/such/file.fits",
            new GraXpertOptions(Operation: GraXpertOperation.BackgroundExtraction),
            CancellationToken.None);
        Assert.That(res.Error, Is.Not.Null);
        Assert.That(res.OutputPath, Is.EqualTo(""));
    }
}
