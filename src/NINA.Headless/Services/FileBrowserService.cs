using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NINA.Headless.Services;

/// <summary>
/// Single chokepoint for every filesystem operation the FILES tab can
/// trigger. Concentrating it here means:
///   - one place to enforce the blocklist + dupla-confirmation rule
///   - one place that logs destructive writes
///   - one place to swap the storage implementation later (e.g. WebDAV,
///     S3) without touching the endpoints or the UI
///
/// The server runs on the local LAN without authentication, so the
/// blocklist is the only guard against a hostile peer wiping a system
/// directory. It's a denylist (not an allowlist) because the user opted
/// for full-disk browsing — they keep frames on flash drives and
/// external SSDs that can't sit in a hardcoded sandbox.
///
/// Path safety pattern: every public method that touches a path runs
/// it through <see cref="ResolveSafe"/> first. That call (a) expands
/// to an absolute path, normalising any <c>..</c> segments, then
/// (b) checks the result against the platform blocklist. Any miss
/// throws <see cref="UnauthorizedAccessException"/>; the endpoints
/// map that to HTTP 403.
/// </summary>
public class FileBrowserService {
    private readonly ILogger<FileBrowserService> _logger;

    // Hardcoded blocklist of path prefixes that destructive operations
    // refuse outright and read operations refuse to list contents of.
    // The rule is conservative — false negatives (allowing a path we
    // should have blocked) are worse than false positives (annoying a
    // power user who can override via the local console anyway).
    private static readonly string[] WindowsBlocklist = [
        @"C:\Windows",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\ProgramData\Microsoft",
        @"C:\$Recycle.Bin",
        @"C:\System Volume Information"
    ];
    private static readonly string[] UnixBlocklist = [
        "/proc", "/sys", "/dev", "/boot", "/root",
        "/etc/shadow", "/etc/sudoers", "/etc/ssh"
    ];
    // Suffix-based extras (matched against the *final* path segment, not
    // a prefix). Catches user-home-relative secrets that vary by user.
    private static readonly string[] DangerousSuffixes = [
        ".ssh", ".aws", ".gnupg", ".config/gh"
    ];

    public FileBrowserService(ILogger<FileBrowserService> logger) {
        _logger = logger;
    }

    // --- Roots -------------------------------------------------------

    /// <summary>
    /// Enumerate the top-level entry points for browsing. On Windows
    /// that's the ready drive letters; on Unix it's <c>/</c> plus the
    /// usual external-mount directories and the user's home if they exist.
    /// </summary>
    public IReadOnlyList<DriveInfoDto> ListRoots() {
        if (OperatingSystem.IsWindows()) {
            var list = new List<DriveInfoDto>();
            foreach (var d in DriveInfo.GetDrives()) {
                if (!d.IsReady) continue;
                long? total = null, free = null;
                string label = "";
                string fmt = "";
                try {
                    total = d.TotalSize;
                    free  = d.AvailableFreeSpace;
                    label = d.VolumeLabel ?? "";
                    fmt   = d.DriveFormat;
                } catch {
                    // VolumeLabel/DriveFormat can throw on removable
                    // drives that just got yanked. Best-effort.
                }
                var display = string.IsNullOrEmpty(label) ? d.Name : $"{label} ({d.Name})";
                list.Add(new DriveInfoDto(d.Name, display, label, total, free, fmt));
            }
            return list;
        }

        // Unix: a curated list of candidate mount points. We only return
        // ones that actually exist so the UI doesn't show dead roots.
        var unix = new List<DriveInfoDto>();
        AddIfExists(unix, "/",            "Root /");
        AddIfExists(unix, "/home",        "Home /home");
        AddIfExists(unix, "/mnt",         "Mounts /mnt");
        AddIfExists(unix, "/media",       "Media /media");
        AddIfExists(unix, "/run/media",   "Removable /run/media");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            AddIfExists(unix, home,       $"~ ({Path.GetFileName(home)})");
        return unix;
    }

    private static void AddIfExists(List<DriveInfoDto> bag, string path, string display) {
        if (!Directory.Exists(path)) return;
        long? total = null, free = null;
        string fmt = "";
        try {
            var di = new DriveInfo(path);
            total = di.TotalSize;
            free  = di.AvailableFreeSpace;
            fmt   = di.DriveFormat;
        } catch { /* /proc and friends would throw — already filtered above */ }
        bag.Add(new DriveInfoDto(path, display, null, total, free, fmt));
    }

    // --- Listing -----------------------------------------------------

    public IReadOnlyList<DirEntry> List(string path, bool showHidden) {
        var dir = ResolveSafe(path, mustExist: true);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException(dir);

        var entries = new List<DirEntry>();
        foreach (var d in Directory.EnumerateDirectories(dir)) {
            var info = new DirectoryInfo(d);
            var hidden = (info.Attributes & FileAttributes.Hidden) != 0;
            if (hidden && !showHidden) continue;
            entries.Add(new DirEntry(
                Name: info.Name,
                FullPath: info.FullName,
                IsDirectory: true,
                SizeBytes: 0,
                ModifiedUtc: SafeUtc(() => info.LastWriteTimeUtc),
                Mime: null,
                IsHidden: hidden,
                IsReadOnly: (info.Attributes & FileAttributes.ReadOnly) != 0));
        }
        foreach (var f in Directory.EnumerateFiles(dir)) {
            var info = new FileInfo(f);
            var hidden = (info.Attributes & FileAttributes.Hidden) != 0;
            if (hidden && !showHidden) continue;
            entries.Add(new DirEntry(
                Name: info.Name,
                FullPath: info.FullName,
                IsDirectory: false,
                SizeBytes: SafeLong(() => info.Length),
                ModifiedUtc: SafeUtc(() => info.LastWriteTimeUtc),
                Mime: GuessMime(info.Extension),
                IsHidden: hidden,
                IsReadOnly: (info.Attributes & FileAttributes.ReadOnly) != 0));
        }
        return entries;
    }

    public DirEntry? Stat(string path) {
        var p = ResolveSafe(path, mustExist: false);
        if (Directory.Exists(p)) {
            var info = new DirectoryInfo(p);
            return new DirEntry(info.Name, info.FullName, true, 0,
                SafeUtc(() => info.LastWriteTimeUtc), null,
                (info.Attributes & FileAttributes.Hidden) != 0,
                (info.Attributes & FileAttributes.ReadOnly) != 0);
        }
        if (File.Exists(p)) {
            var info = new FileInfo(p);
            return new DirEntry(info.Name, info.FullName, false,
                SafeLong(() => info.Length), SafeUtc(() => info.LastWriteTimeUtc),
                GuessMime(info.Extension),
                (info.Attributes & FileAttributes.Hidden) != 0,
                (info.Attributes & FileAttributes.ReadOnly) != 0);
        }
        return null;
    }

    public Stream OpenRead(string path) {
        var p = ResolveSafe(path, mustExist: true);
        if (!File.Exists(p)) throw new FileNotFoundException(p);
        // FileShare.Read lets the active capture session keep writing
        // while the UI downloads an older sibling — common during long
        // sequences when the user wants to peek at the previous frame.
        return new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    // --- Mutations ---------------------------------------------------

    public async Task CopyAsync(string src, string dst, bool overwrite, CancellationToken ct) {
        var s = ResolveSafe(src, mustExist: true);
        var d = ResolveSafeDestination(dst);
        if (Directory.Exists(s)) {
            await CopyDirectoryAsync(s, d, overwrite, ct);
        } else if (File.Exists(s)) {
            if (File.Exists(d) && !overwrite)
                throw new IOException($"Destination exists: {d}");
            Directory.CreateDirectory(Path.GetDirectoryName(d)!);
            await using var sIn = File.OpenRead(s);
            await using var sOut = File.Create(d);
            await sIn.CopyToAsync(sOut, ct);
        } else {
            throw new FileNotFoundException(s);
        }
        _logger.LogInformation("FileOp Copy {Src} -> {Dst} overwrite={Ow}", s, d, overwrite);
    }

    public async Task MoveAsync(string src, string dst, bool overwrite, CancellationToken ct) {
        var s = ResolveSafe(src, mustExist: true);
        var d = ResolveSafeDestination(dst);
        if (s == d) return;

        try {
            if (Directory.Exists(s)) {
                if (Directory.Exists(d)) {
                    if (!overwrite) throw new IOException($"Destination exists: {d}");
                    Directory.Delete(d, recursive: true);
                }
                Directory.Move(s, d);
            } else if (File.Exists(s)) {
                if (File.Exists(d)) {
                    if (!overwrite) throw new IOException($"Destination exists: {d}");
                    File.Delete(d);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(d)!);
                File.Move(s, d);
            } else {
                throw new FileNotFoundException(s);
            }
            _logger.LogInformation("FileOp Move {Src} -> {Dst} overwrite={Ow}", s, d, overwrite);
        } catch (IOException ex) when (IsCrossDeviceError(ex)) {
            // Fall back to copy+delete when Move would cross a volume
            // boundary. This is the common case when the user drags from
            // the internal SSD to an external USB drive.
            _logger.LogInformation("FileOp Move cross-volume: falling back to copy+delete {Src} -> {Dst}", s, d);
            await CopyAsync(s, d, overwrite, ct);
            await DeleteAsync(s, recursive: true, ct);
        }
    }

    public Task DeleteAsync(string path, bool recursive, CancellationToken ct) {
        var p = ResolveSafe(path, mustExist: true);
        if (Directory.Exists(p)) {
            Directory.Delete(p, recursive);
        } else if (File.Exists(p)) {
            File.Delete(p);
        } else {
            throw new FileNotFoundException(p);
        }
        _logger.LogInformation("FileOp Delete {Path} recursive={R}", p, recursive);
        return Task.CompletedTask;
    }

    public Task CreateFolderAsync(string parentPath, string name) {
        var parent = ResolveSafe(parentPath, mustExist: true);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);

        // Reject names that would punch out of the parent dir or alias
        // the OS-reserved cases. Path.GetFileName strips any directory
        // separators a malicious caller might inject.
        var safeName = SanitiseSegment(name);
        if (string.IsNullOrEmpty(safeName))
            throw new ArgumentException("Invalid folder name");

        var full = Path.Combine(parent, safeName);
        // Re-validate to make sure the joined path didn't escape via
        // some edge case Path.GetFileName missed.
        ResolveSafe(full, mustExist: false);
        Directory.CreateDirectory(full);
        _logger.LogInformation("FileOp Mkdir {Path}", full);
        return Task.CompletedTask;
    }

    public Task RenameAsync(string path, string newName) {
        var p = ResolveSafe(path, mustExist: true);
        var safeName = SanitiseSegment(newName);
        if (string.IsNullOrEmpty(safeName))
            throw new ArgumentException("Invalid name");
        var parent = Path.GetDirectoryName(p)
                     ?? throw new InvalidOperationException("Cannot rename a root");
        var target = Path.Combine(parent, safeName);
        ResolveSafe(target, mustExist: false);

        if (Directory.Exists(p)) Directory.Move(p, target);
        else if (File.Exists(p)) File.Move(p, target);
        else throw new FileNotFoundException(p);

        _logger.LogInformation("FileOp Rename {Src} -> {Dst}", p, target);
        return Task.CompletedTask;
    }

    // --- ZIP streaming -----------------------------------------------

    /// <summary>
    /// Stream a ZIP archive containing every source path. Folders are
    /// included recursively. <paramref name="commonRootForNames"/> is
    /// stripped from entry names so the archive opens with a sensible
    /// relative structure (otherwise the user gets full absolute paths
    /// inside the ZIP).
    ///
    /// Uses <see cref="ZipArchiveMode.Create"/> with leaveOpen=true on
    /// the destination so we don't accidentally close the response
    /// stream. The archive is flushed when this method returns.
    /// </summary>
    public async Task<long> WriteZipAsync(IEnumerable<string> sources, Stream destination,
                                          string? commonRootForNames, CancellationToken ct) {
        var resolvedSources = sources.Select(s => ResolveSafe(s, mustExist: true)).ToList();
        long totalBytes = 0;

        // ZipArchive's Create mode writes incrementally and does not
        // buffer the whole archive in memory. Good for ZIP-ing many GB
        // of FITS without OOMing the RPi.
        using (var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true)) {
            foreach (var src in resolvedSources) {
                ct.ThrowIfCancellationRequested();
                if (Directory.Exists(src)) {
                    var dirRoot = string.IsNullOrEmpty(commonRootForNames)
                        ? Path.GetDirectoryName(src) ?? ""
                        : commonRootForNames;
                    foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)) {
                        ct.ThrowIfCancellationRequested();
                        totalBytes += await AddFileToZipAsync(zip, file, dirRoot, ct);
                    }
                } else if (File.Exists(src)) {
                    var root = string.IsNullOrEmpty(commonRootForNames)
                        ? Path.GetDirectoryName(src) ?? ""
                        : commonRootForNames;
                    totalBytes += await AddFileToZipAsync(zip, src, root, ct);
                }
            }
        }
        _logger.LogInformation("FileOp Zip {Bytes} bytes across {Count} sources", totalBytes, resolvedSources.Count);
        return totalBytes;
    }

    private static async Task<long> AddFileToZipAsync(ZipArchive zip, string filePath,
                                                      string root, CancellationToken ct) {
        var rel = MakeRelative(root, filePath).Replace('\\', '/');
        var entry = zip.CreateEntry(rel, CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var fileStream = File.OpenRead(filePath);
        await fileStream.CopyToAsync(entryStream, ct);
        return fileStream.Length;
    }

    private static string MakeRelative(string root, string file) {
        if (string.IsNullOrEmpty(root)) return Path.GetFileName(file);
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (file.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            file.StartsWith(trimmed + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
            return file[(trimmed.Length + 1)..];
        }
        return Path.GetFileName(file);
    }

    // --- Path safety -------------------------------------------------

    /// <summary>
    /// Normalise + validate. Throws if the resolved path lives inside
    /// the platform blocklist. Returns the canonical absolute path the
    /// rest of the service should use.
    /// </summary>
    public string ResolveSafe(string userPath, bool mustExist) {
        if (string.IsNullOrWhiteSpace(userPath))
            throw new ArgumentException("Empty path", nameof(userPath));
        // Path.GetFullPath collapses .. segments and normalises
        // separators. We work on the canonical form everywhere.
        var full = Path.GetFullPath(userPath);
        if (mustExist && !File.Exists(full) && !Directory.Exists(full))
            throw new FileNotFoundException(full);
        if (IsBlocked(full))
            throw new UnauthorizedAccessException($"Path is blocked: {full}");
        return full;
    }

    private string ResolveSafeDestination(string userPath) {
        // Destination must not be blocked, but is allowed to not exist
        // yet — that's the entire point of copy/move.
        if (string.IsNullOrWhiteSpace(userPath))
            throw new ArgumentException("Empty destination", nameof(userPath));
        var full = Path.GetFullPath(userPath);
        if (IsBlocked(full))
            throw new UnauthorizedAccessException($"Destination is blocked: {full}");
        return full;
    }

    public static bool IsBlocked(string fullPath) {
        var list = OperatingSystem.IsWindows() ? WindowsBlocklist : UnixBlocklist;
        var cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (var prefix in list) {
            if (fullPath.Equals(prefix, cmp)) return true;
            if (fullPath.StartsWith(prefix + Path.DirectorySeparatorChar, cmp)) return true;
            if (fullPath.StartsWith(prefix + "/", cmp)) return true;
        }
        // Suffix-based: walk segments and reject if any matches a known
        // sensitive name. Catches per-user secrets at any depth.
        var segments = fullPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments) {
            foreach (var bad in DangerousSuffixes) {
                if (seg.Equals(bad.TrimStart('.'), cmp) || seg.Equals(bad, cmp)) return true;
            }
        }
        return false;
    }

    private static string SanitiseSegment(string name) {
        var t = (name ?? "").Trim();
        if (string.IsNullOrEmpty(t)) return "";
        // Reject anything with a path separator — caller wanted ONE
        // segment, not a relative path.
        if (t.Contains('/') || t.Contains('\\')) return "";
        // Strip control chars + the invalid-filename set.
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var ch in invalid) t = t.Replace(ch.ToString(), "");
        // Forbid reserved names on Windows even on Linux — pasting
        // CON.fits onto a samba share is a portability landmine.
        var stem = Path.GetFileNameWithoutExtension(t).ToUpperInvariant();
        string[] reserved = ["CON", "PRN", "AUX", "NUL",
            "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
            "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"];
        if (reserved.Contains(stem)) return "";
        // No leading dots that would make the entry vanish on Windows
        // GUI shells while still being browsable here. Allow `.foo`
        // though — common for dotfiles.
        return t.Trim('.', ' ').Length == 0 ? "" : t;
    }

    private static bool IsCrossDeviceError(IOException ex) {
        // .NET wraps EXDEV in a generic IOException; the HResult is the
        // only reliable discriminator. On Windows the equivalent is
        // ERROR_NOT_SAME_DEVICE (0x11) wrapped as HResult 0x80070011.
        const int LinuxEXDEV = 18;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
            return (ex.HResult & 0xFFFF) == LinuxEXDEV;
        }
        return (ex.HResult & 0xFFFF) == 0x11;
    }

    private static async Task CopyDirectoryAsync(string src, string dst, bool overwrite,
                                                 CancellationToken ct) {
        Directory.CreateDirectory(dst);
        foreach (var d in Directory.EnumerateDirectories(src)) {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(d);
            await CopyDirectoryAsync(d, Path.Combine(dst, name), overwrite, ct);
        }
        foreach (var f in Directory.EnumerateFiles(src)) {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(f);
            var target = Path.Combine(dst, name);
            if (File.Exists(target) && !overwrite)
                throw new IOException($"Destination exists: {target}");
            await using var sIn = File.OpenRead(f);
            await using var sOut = File.Create(target);
            await sIn.CopyToAsync(sOut, ct);
        }
    }

    private static long SafeLong(Func<long> f) { try { return f(); } catch { return 0; } }
    private static DateTime SafeUtc(Func<DateTime> f) { try { return f(); } catch { return DateTime.MinValue; } }

    // --- Mime ---------------------------------------------------------

    /// <summary>
    /// Minimal mime mapping. Anything not in this table maps to
    /// <c>application/octet-stream</c> — the browser will offer to
    /// download it, which is the right behaviour for unknown formats.
    /// </summary>
    public static string GuessMime(string extension) {
        var ext = (extension ?? "").ToLowerInvariant();
        return ext switch {
            ".fits" or ".fit" or ".fts" => "image/fits",
            ".xisf"                     => "image/x-xisf",
            ".png"                      => "image/png",
            ".jpg" or ".jpeg"           => "image/jpeg",
            ".tif" or ".tiff"           => "image/tiff",
            ".gif"                      => "image/gif",
            ".bmp"                      => "image/bmp",
            ".webp"                     => "image/webp",
            ".txt" or ".log"            => "text/plain",
            ".md"                       => "text/markdown",
            ".json"                     => "application/json",
            ".xml"                      => "application/xml",
            ".csv"                      => "text/csv",
            ".pdf"                      => "application/pdf",
            ".zip"                      => "application/zip",
            _                           => "application/octet-stream"
        };
    }

    /// <summary>
    /// Heuristic classification used by the preview endpoint to pick
    /// a renderer. Saves the endpoint from repeating the same switch.
    /// </summary>
    public static PreviewKind ClassifyForPreview(string path) {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch {
            ".fits" or ".fit" or ".fts" => PreviewKind.Fits,
            ".xisf"                     => PreviewKind.Fits,  // future: XISF reader; routed via the same JPEG path
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" => PreviewKind.RasterPassthrough,
            ".tif" or ".tiff"           => PreviewKind.TiffDecode,
            ".txt" or ".log" or ".md" or ".json" or ".xml" or ".csv" => PreviewKind.Text,
            _                           => PreviewKind.None
        };
    }

    /// <summary>Stable hash of a path for use as a thumbnail-cache filename.</summary>
    public static string PathHash(string fullPath) {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(fullPath.ToLowerInvariant()));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}

public enum PreviewKind { None, Fits, RasterPassthrough, TiffDecode, Text }

public sealed record DirEntry(
    string Name, string FullPath, bool IsDirectory,
    long SizeBytes, DateTime ModifiedUtc, string? Mime,
    bool IsHidden, bool IsReadOnly);

public sealed record DriveInfoDto(
    string Name, string DisplayName, string? VolumeLabel,
    long? TotalBytes, long? FreeBytes, string DriveFormat);
