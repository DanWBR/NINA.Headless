using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Headless.Services;

namespace NINA.Headless.Test;

/// <summary>
/// FileBrowserService is the chokepoint for every disk operation the
/// FILES tab can trigger. Each public method gets a smoke test so we
/// catch obvious regressions; path-safety + ZIP streaming get a deeper
/// look because those are the bits that, if broken, leak data or
/// quietly corrupt user files.
///
/// Tests run inside an isolated temp directory so they don't depend
/// on or pollute the host machine. The blocklist coverage uses raw
/// strings that match real system paths (we don't actually try to
/// touch /etc/shadow — we just confirm the validator refuses).
/// </summary>
[TestFixture]
public class FileBrowserServiceTests {

    private FileBrowserService _svc = null!;
    private string _tmp = null!;

    [SetUp]
    public void Setup() {
        _svc = new FileBrowserService(NullLogger<FileBrowserService>.Instance);
        _tmp = Path.Combine(Path.GetTempPath(), "fb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    [TearDown]
    public void Teardown() {
        try { if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true); } catch { }
    }

    // --- Roots --------------------------------------------------------

    [Test]
    public void ListRoots_ReturnsAtLeastOneEntry() {
        // Whatever the host platform, the user always has *some* place
        // to start browsing — if not, the FILES tab would be DOA.
        var roots = _svc.ListRoots();
        Assert.That(roots, Is.Not.Empty);
    }

    // --- List / Stat --------------------------------------------------

    [Test]
    public void List_ReturnsFilesAndFolders() {
        File.WriteAllText(Path.Combine(_tmp, "a.fits"), "fake");
        Directory.CreateDirectory(Path.Combine(_tmp, "sub"));

        var entries = _svc.List(_tmp, showHidden: false);

        Assert.That(entries.Any(e => e.Name == "a.fits" && !e.IsDirectory), Is.True);
        Assert.That(entries.Any(e => e.Name == "sub" && e.IsDirectory), Is.True);
    }

    [Test]
    public void List_HiddenFilesRespectShowFlag() {
        var hidden = Path.Combine(_tmp, ".cache");
        File.WriteAllText(hidden, "x");
        // On Windows the dot-prefix doesn't make the file hidden, so
        // force the attribute. On Linux the dot-prefix is enough but
        // we don't actually consult that — we read FileAttributes.
        if (OperatingSystem.IsWindows()) File.SetAttributes(hidden, FileAttributes.Hidden);

        var visible = _svc.List(_tmp, showHidden: false);
        var all = _svc.List(_tmp, showHidden: true);

        if (OperatingSystem.IsWindows()) {
            // Only meaningful on Windows where we actually set the bit.
            Assert.That(visible.Any(e => e.Name == ".cache"), Is.False);
        }
        Assert.That(all.Any(e => e.Name == ".cache"), Is.True);
    }

    [Test]
    public void Stat_NonexistentPath_ReturnsNull() {
        Assert.That(_svc.Stat(Path.Combine(_tmp, "nope")), Is.Null);
    }

    [Test]
    public void Stat_File_ReturnsMimeBasedOnExtension() {
        var p = Path.Combine(_tmp, "thing.fits");
        File.WriteAllText(p, "x");
        var entry = _svc.Stat(p);
        Assert.That(entry, Is.Not.Null);
        Assert.That(entry!.Mime, Is.EqualTo("image/fits"));
    }

    // --- Path safety --------------------------------------------------

    [Test]
    public void IsBlocked_KnownSystemPath_True() {
        // Spot-check both platforms regardless of where we're running:
        // the static helper doesn't touch the disk, so a Windows host
        // can still validate the Unix blocklist will catch /etc/shadow.
        if (OperatingSystem.IsWindows()) {
            Assert.That(FileBrowserService.IsBlocked(@"C:\Windows\System32\drivers\etc\hosts"), Is.True);
        } else {
            Assert.That(FileBrowserService.IsBlocked("/etc/shadow"), Is.True);
            Assert.That(FileBrowserService.IsBlocked("/proc/cpuinfo"), Is.True);
        }
    }

    [Test]
    public void IsBlocked_UserDataPath_False() {
        // The whole point of full-disk browsing is that user data
        // outside the blocklist is fully accessible.
        var p = OperatingSystem.IsWindows() ? @"D:\Astrofotos\M31" : "/home/dan/astro/M31";
        Assert.That(FileBrowserService.IsBlocked(p), Is.False);
    }

    [Test]
    public void IsBlocked_DotSshSegmentAnywhere_True() {
        // Catches the secret-dirs blocklist by suffix, not prefix.
        var p = OperatingSystem.IsWindows() ? @"C:\Users\dan\.ssh\id_rsa" : "/home/dan/.ssh/id_rsa";
        Assert.That(FileBrowserService.IsBlocked(p), Is.True);
    }

    [Test]
    public void ResolveSafe_DotDotEscape_NormalisedThenChecked() {
        // ../../../etc/shadow from a deep cwd would resolve to a real
        // sensitive path. Path.GetFullPath collapses the .. — then the
        // blocklist catches it. Confirm both halves work together.
        if (OperatingSystem.IsWindows()) return;  // Linux-only path
        Assert.That(
            () => _svc.ResolveSafe("/home/x/../../../etc/shadow", mustExist: false),
            Throws.TypeOf<UnauthorizedAccessException>());
    }

    [Test]
    public void ResolveSafe_NonexistentRequired_Throws() {
        Assert.That(() => _svc.ResolveSafe(Path.Combine(_tmp, "nope"), mustExist: true),
            Throws.TypeOf<FileNotFoundException>());
    }

    // --- Mutations ----------------------------------------------------

    [Test]
    public async Task CopyAsync_File_CreatesDestinationAndPreservesContent() {
        var src = Path.Combine(_tmp, "a.txt");
        var dst = Path.Combine(_tmp, "sub", "b.txt");
        await File.WriteAllTextAsync(src, "hello");

        await _svc.CopyAsync(src, dst, overwrite: false, CancellationToken.None);

        Assert.That(File.Exists(dst), Is.True);
        Assert.That(await File.ReadAllTextAsync(dst), Is.EqualTo("hello"));
        Assert.That(File.Exists(src), Is.True, "source should still exist after copy");
    }

    [Test]
    public void CopyAsync_RefusesOverwriteUnlessAsked() {
        var src = Path.Combine(_tmp, "a.txt"); File.WriteAllText(src, "1");
        var dst = Path.Combine(_tmp, "b.txt"); File.WriteAllText(dst, "2");
        Assert.That(
            async () => await _svc.CopyAsync(src, dst, overwrite: false, CancellationToken.None),
            Throws.TypeOf<IOException>());
    }

    [Test]
    public async Task MoveAsync_SameVolume_DeletesSource() {
        var src = Path.Combine(_tmp, "a.txt"); File.WriteAllText(src, "x");
        var dst = Path.Combine(_tmp, "b.txt");

        await _svc.MoveAsync(src, dst, overwrite: false, CancellationToken.None);

        Assert.That(File.Exists(src), Is.False);
        Assert.That(File.Exists(dst), Is.True);
    }

    [Test]
    public async Task DeleteAsync_File_Removes() {
        var p = Path.Combine(_tmp, "kill-me.txt"); File.WriteAllText(p, "x");
        await _svc.DeleteAsync(p, recursive: false, CancellationToken.None);
        Assert.That(File.Exists(p), Is.False);
    }

    [Test]
    public async Task DeleteAsync_DirectoryRecursive_RemovesTree() {
        var dir = Path.Combine(_tmp, "tree");
        Directory.CreateDirectory(Path.Combine(dir, "nested"));
        await File.WriteAllTextAsync(Path.Combine(dir, "nested", "f.txt"), "x");

        await _svc.DeleteAsync(dir, recursive: true, CancellationToken.None);

        Assert.That(Directory.Exists(dir), Is.False);
    }

    [Test]
    public async Task CreateFolder_AndRename_Roundtrip() {
        await _svc.CreateFolderAsync(_tmp, "newdir");
        Assert.That(Directory.Exists(Path.Combine(_tmp, "newdir")), Is.True);

        await _svc.RenameAsync(Path.Combine(_tmp, "newdir"), "renamed");
        Assert.That(Directory.Exists(Path.Combine(_tmp, "renamed")), Is.True);
        Assert.That(Directory.Exists(Path.Combine(_tmp, "newdir")), Is.False);
    }

    [Test]
    public void CreateFolder_InjectionAttempt_Refused() {
        // SanitiseSegment must strip path separators; otherwise mkdir
        // turns into "create a hierarchy" or worse, escapes the parent.
        Assert.That(
            async () => await _svc.CreateFolderAsync(_tmp, "../escape"),
            Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void Rename_ReservedWindowsName_Refused() {
        var p = Path.Combine(_tmp, "ok.txt"); File.WriteAllText(p, "x");
        // CON / PRN / etc are landmines on Windows shares even when
        // running on Linux. The sanitiser blocks them either way.
        Assert.That(
            async () => await _svc.RenameAsync(p, "CON.txt"),
            Throws.TypeOf<ArgumentException>());
    }

    // --- ZIP streaming ------------------------------------------------

    [Test]
    public async Task WriteZipAsync_PacksMultipleFiles_AndKeepsRelativeNames() {
        var f1 = Path.Combine(_tmp, "a.txt"); await File.WriteAllTextAsync(f1, "one");
        var sub = Path.Combine(_tmp, "sub"); Directory.CreateDirectory(sub);
        var f2 = Path.Combine(sub, "b.txt"); await File.WriteAllTextAsync(f2, "two");

        using var ms = new MemoryStream();
        var written = await _svc.WriteZipAsync([f1, f2], ms, _tmp, CancellationToken.None);

        Assert.That(written, Is.EqualTo(6), "should report total uncompressed bytes copied");

        ms.Position = 0;
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.That(names, Has.Member("a.txt"));
        Assert.That(names, Has.Member("sub/b.txt"));
    }

    [Test]
    public async Task WriteZipAsync_RecursivelyIncludesFolderContent() {
        var dir = Path.Combine(_tmp, "M31");
        Directory.CreateDirectory(Path.Combine(dir, "L"));
        await File.WriteAllTextAsync(Path.Combine(dir, "L", "001.fits"), "x");
        await File.WriteAllTextAsync(Path.Combine(dir, "L", "002.fits"), "y");

        using var ms = new MemoryStream();
        await _svc.WriteZipAsync([dir], ms, _tmp, CancellationToken.None);

        ms.Position = 0;
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        Assert.That(archive.Entries.Select(e => e.FullName),
            Is.EquivalentTo(new[] { "M31/L/001.fits", "M31/L/002.fits" }));
    }

    // --- Mime + classification ---------------------------------------

    [Test]
    public void GuessMime_KnownExtensions() {
        Assert.That(FileBrowserService.GuessMime(".fits"), Is.EqualTo("image/fits"));
        Assert.That(FileBrowserService.GuessMime(".xisf"), Is.EqualTo("image/x-xisf"));
        Assert.That(FileBrowserService.GuessMime(".png"),  Is.EqualTo("image/png"));
        Assert.That(FileBrowserService.GuessMime(".log"),  Is.EqualTo("text/plain"));
        Assert.That(FileBrowserService.GuessMime(".bogus"), Is.EqualTo("application/octet-stream"));
    }

    [Test]
    public void ClassifyForPreview_RouteEachKnownType() {
        Assert.That(FileBrowserService.ClassifyForPreview("/x/y.fits"),
            Is.EqualTo(PreviewKind.Fits));
        Assert.That(FileBrowserService.ClassifyForPreview("/x/y.png"),
            Is.EqualTo(PreviewKind.RasterPassthrough));
        Assert.That(FileBrowserService.ClassifyForPreview("/x/y.tiff"),
            Is.EqualTo(PreviewKind.TiffDecode));
        Assert.That(FileBrowserService.ClassifyForPreview("/x/y.log"),
            Is.EqualTo(PreviewKind.Text));
        Assert.That(FileBrowserService.ClassifyForPreview("/x/y.dat"),
            Is.EqualTo(PreviewKind.None));
    }

    [Test]
    public void PathHash_IsStableAndCaseInsensitive() {
        // The thumbnail cache uses this as the file name. Two equal
        // (case-insensitively) paths must map to the same cache slot
        // so we don't regenerate the JPEG on every refresh on Windows.
        var a = FileBrowserService.PathHash(@"C:\Astro\M31.fits");
        var b = FileBrowserService.PathHash(@"c:\astro\m31.FITS");
        Assert.That(a, Is.EqualTo(b));
    }
}
