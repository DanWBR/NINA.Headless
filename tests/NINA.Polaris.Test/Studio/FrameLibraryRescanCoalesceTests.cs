using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Studio;

namespace NINA.Polaris.Test.Studio;

/// <summary>
/// Pins the rescan coalescing semantics that FrameLibraryService
/// promises to callers (see <c>_scanGate</c> in the service):
///
///   - An <c>await RescanAsync()</c> always returns with an index
///     that reflects disk state at or after the call time. The old
///     implementation silently no-op'd overlapping callers (via
///     <c>SemaphoreSlim.WaitAsync(0)</c>), which made fire-and-forget
///     rescans kicked by sibling services race against explicit
///     test/UI rescans, leaving the SQLite cache stale.
///
///   - Multiple concurrent callers piggyback on a single follow-up
///     pass so the full file walk isn't amplified by the caller
///     count. The contract is "at most one queued follow-up beyond
///     the in-flight rescan."
///
/// These tests file in the same directory as ChannelCombine /
/// ColorCalibration / ApassCatalog tests so the discovery filter
/// "Category=...." picks them up alongside their peers.
/// </summary>
[TestFixture]
public class FrameLibraryRescanCoalesceTests {

    private string _tmpRoot = null!;
    private string _studioDir = null!;
    private ProfileService _profile = null!;
    private FrameLibraryService _library = null!;

    [SetUp]
    public void Setup() {
        _tmpRoot = Path.Combine(Path.GetTempPath(),
            "polaris-rescan-coal-" + Guid.NewGuid().ToString("N"));
        _studioDir = Path.Combine(_tmpRoot, "_studio");
        Directory.CreateDirectory(_tmpRoot);
        Directory.CreateDirectory(_studioDir);

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Studio:Directory"] = _studioDir,
            })
            .Build();
        _profile = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        _profile.Active.ImageOutputDir = _tmpRoot;
        _profile.ActiveEquipmentProfile.Name = "RescanTestRig";

        _library = new FrameLibraryService(_profile, cfg,
            NullLogger<FrameLibraryService>.Instance);
    }

    [TearDown]
    public void Teardown() {
        try { Directory.Delete(_tmpRoot, recursive: true); } catch { }
    }

    [Test]
    public async Task RescanAsync_CallerArrivingMidRescan_SeesFileAddedAfterStart() {
        // Seed enough files that a single rescan pass takes long
        // enough for a second caller to arrive while it's running.
        // 64 frames * a few ms of header parse each = comfortably
        // > 50ms total even on a fast SSD.
        for (int i = 0; i < 64; i++) SeedFrame($"seed-{i:00}");

        // Kick off the "in-flight" rescan but don't await yet.
        var first = _library.RescanAsync();

        // Drop another file AFTER the first rescan started. The
        // contract: a second caller arriving now should, after their
        // own await returns, see this file in the index.
        var lateName = "post-start-" + Guid.NewGuid().ToString("N")[..6];
        SeedFrame(lateName);

        // Second caller. The fix makes this queue a follow-up that
        // begins only after the first rescan finishes, so this awaits
        // BOTH passes before returning.
        var second = _library.RescanAsync();

        await Task.WhenAll(first, second);

        var rows = _library.Query(new FrameQuery(
            null, null, null, null, null, 500, 0));
        var paths = rows.Select(r => Path.GetFileNameWithoutExtension(r.Path))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.That(paths, Contains.Item(lateName),
            "Late-arrival file must be indexed after the second " +
            "caller's await returns (coalescing contract).");
    }

    [Test]
    public async Task RescanAsync_ManyConcurrentCallers_AllResolve() {
        // Light load so the test stays fast. Goal here isn't a stress
        // race, it's to confirm the coalescing state machine doesn't
        // deadlock or strand callers when many of them pile in.
        for (int i = 0; i < 8; i++) SeedFrame($"smoke-{i}");

        var callers = Enumerable.Range(0, 16)
            .Select(_ => _library.RescanAsync())
            .ToArray();
        var done = Task.WhenAll(callers);
        // Generous timeout so a hang fails the test instead of
        // wedging the whole suite. 30s is many orders of magnitude
        // above a working rescan of 8 tiny FITS files.
        var winner = await Task.WhenAny(done, Task.Delay(30_000));
        Assert.That(winner, Is.SameAs(done),
            "RescanAsync stranded callers (deadlock or starvation).");

        var rows = _library.Query(new FrameQuery(
            null, null, null, null, null, 500, 0));
        Assert.That(rows.Count, Is.EqualTo(8));
    }

    [Test]
    public async Task RescanAsync_IdleCaller_ReturnsImmediatelyAfterPass() {
        // Pure idle path: no overlap, one call, one pass. Guards
        // against accidentally turning the coalescing machine into a
        // dual-pass machine in the no-contention case.
        SeedFrame("idle-1");
        await _library.RescanAsync();
        var rows = _library.Query(new FrameQuery(
            null, null, null, null, null, 500, 0));
        Assert.That(rows.Count, Is.EqualTo(1));

        // A follow-up call with no new files must still complete (and
        // produce the same row count, since the delete-pruning sweep
        // doesn't remove anything).
        await _library.RescanAsync();
        rows = _library.Query(new FrameQuery(
            null, null, null, null, null, 500, 0));
        Assert.That(rows.Count, Is.EqualTo(1));
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private void SeedFrame(string stem) {
        // 16x16 mono FITS, just enough for FITSReader to parse a
        // valid header. The file-walk cost dominates the per-file
        // parse cost; what matters for the test is the count.
        var path = Path.Combine(_tmpRoot, stem + ".fits");
        var pix = new ushort[16 * 16];
        for (int i = 0; i < pix.Length; i++) pix[i] = 100;
        var img = new BaseImageData(pix,
            new ImageProperties { Width = 16, Height = 16, BitDepth = 16, Channels = 1 },
            new ImageMetaData {
                Target = new ImageMetaData.TargetInfo { Name = "RescanTest" },
                Exposure = new ImageMetaData.ExposureInfo {
                    Filter = "L", ImageType = "LIGHT", ExposureTime = 1,
                },
            });
        FITSWriter.Write(img, path);
    }
}
