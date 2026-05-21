using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Headless.Services;

/// <summary>
/// Resolves a celestial object name (e.g. "M31", "NGC 7000", "Moon",
/// "Jupiter", "22P/Kopff") to a representative thumbnail image URL.
///
/// Lookup order:
///   1. In-memory cache hit (fastest)
///   2. On-disk cache hit, TTL 30 days for found images, 1 day for misses
///   3. NASA Image Library (public domain, no API key) — preferred since
///      results are usually high-quality press images with credits.
///   4. Wikipedia REST summary endpoint (CC BY-SA, no API key) — fallback;
///      every Messier/NGC/named comet has a Wikipedia article whose
///      lead image is a decent thumbnail.
///
/// Returns <see cref="CelestialImage"/> with Available=false (and the
/// failure cached briefly) when neither source has anything — never
/// throws. UI then shows a placeholder.
/// </summary>
public class CelestialImageService {
    private static readonly HttpClient Http = new() {
        Timeout = TimeSpan.FromSeconds(8)
    };
    private static readonly TimeSpan FoundTtl  = TimeSpan.FromDays(30);
    private static readonly TimeSpan MissedTtl = TimeSpan.FromDays(1);
    // Bump when the lookup pipeline materially changes (e.g. relevance
    // filter added). Cached entries with an older version are ignored
    // and re-fetched, so users don't keep getting stale irrelevant hits.
    private const int CacheSchemaVersion = 4;

    private readonly ILogger<CelestialImageService> _logger;
    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, CelestialImage> _mem = new();

    public CelestialImageService(IConfiguration config, ILogger<CelestialImageService> logger) {
        _logger = logger;
        var baseDir = config.GetValue("Images:CacheDirectory",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA.Headless", "images"))!;
        _cacheDir = baseDir;
        Directory.CreateDirectory(_cacheDir);

        if (!Http.DefaultRequestHeaders.UserAgent.Any()) {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "NINA-Polaris/0.1 (https://github.com/DanWBR/nina-headless)");
        }
    }

    public async Task<CelestialImage> GetImageAsync(string name, CancellationToken ct = default) {
        if (string.IsNullOrWhiteSpace(name)) return CelestialImage.NotAvailable("Empty name");
        var slug = Slugify(name);
        if (_mem.TryGetValue(slug, out var hot) && !IsExpired(hot)) return hot;

        // Disk cache
        var path = Path.Combine(_cacheDir, slug + ".json");
        if (File.Exists(path)) {
            try {
                var cached = JsonSerializer.Deserialize<CelestialImage>(await File.ReadAllTextAsync(path, ct));
                if (cached != null && cached.SchemaVersion == CacheSchemaVersion && !IsExpired(cached)) {
                    _mem[slug] = cached;
                    return cached;
                }
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Bad cache file {Path}", path);
            }
        }

        // Live lookup. NASA Library is great for common names ("Carina
        // Nebula", "Andromeda Galaxy") but a mess for raw catalogue
        // codes and for major solar-system bodies — "M 4" matches an
        // STS-109 shuttle photo, "Moon" matches a Mineralogy Mapper
        // diagram. For both cases, prefer Wikipedia (which has a
        // reliable article + iconic lead image per entry) and use
        // NASA only as the fallback.
        CelestialImage result;
        try {
            if (LooksLikeCatalogueCode(name) || WikipediaFirstBodies.Contains(name.Trim())) {
                result = await TryWikipediaAsync(name, ct);
                if (!result.Available) result = await TryNasaAsync(name, ct);
            } else {
                result = await TryNasaAsync(name, ct);
                if (!result.Available) result = await TryWikipediaAsync(name, ct);
            }
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Image lookup failed for {Name}", name);
            result = CelestialImage.NotAvailable("Lookup failed");
        }

        // Download the thumbnail bytes too. With offline-mode in mind
        // (RPi at a dark-sky site without internet), having the actual
        // JPEG on disk lets the UI serve them via /api/sky/image/file/
        // {slug} instead of relying on NASA / Wikipedia CDNs at view
        // time. Best-effort: a failed download doesn't poison the
        // metadata entry, the URL is still usable when online.
        string? localExt = null;
        if (result.Available && !string.IsNullOrEmpty(result.ThumbnailUrl)) {
            localExt = await TryDownloadThumbAsync(result.ThumbnailUrl, slug, ct);
        }

        result = result with {
            FetchedAt     = DateTime.UtcNow,
            SchemaVersion = CacheSchemaVersion,
            LocalFileExt  = localExt,
            LocalUrl      = localExt != null ? $"/api/sky/image/file/{slug}" : null
        };
        _mem[slug] = result;
        try {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result), ct);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Could not persist cache file {Path}", path);
        }
        return result;
    }

    /// <summary>
    /// Iterate a set of names and warm the cache for each. Used by the
    /// prefetch endpoint to build a fully-offline catalogue of object
    /// thumbnails. Sequential (one request per second-ish, throttled by
    /// HTTP latency) to be polite to NASA / Wikipedia.
    /// </summary>
    public async Task<PrefetchSummary> PrefetchAsync(IEnumerable<string> names, CancellationToken ct = default) {
        var attempted = 0;
        var found = 0;
        var missing = 0;
        var alreadyCached = 0;
        var bytes = 0L;
        foreach (var n in names.Distinct(StringComparer.OrdinalIgnoreCase)) {
            if (ct.IsCancellationRequested) break;
            attempted++;
            var slug = Slugify(n);
            var beforePath = Path.Combine(_cacheDir, slug + ".json");
            var wasCached = File.Exists(beforePath);
            var r = await GetImageAsync(n, ct);
            if (r.Available) {
                found++;
                if (!wasCached && !string.IsNullOrEmpty(r.LocalFileExt)) {
                    var localPath = Path.Combine(_cacheDir, slug + r.LocalFileExt);
                    if (File.Exists(localPath)) bytes += new FileInfo(localPath).Length;
                } else if (wasCached) {
                    alreadyCached++;
                }
            } else {
                missing++;
            }
        }
        return new PrefetchSummary(attempted, found, missing, alreadyCached, bytes);
    }

    /// <summary>
    /// Path to the local JPEG/PNG for a slug, or null if we haven't
    /// downloaded one. Used by the static-file endpoint.
    /// </summary>
    public string? GetLocalFilePath(string slug) {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        // Whitelist: slug came from Slugify which is alphanum-only, so
        // no traversal risk; the extension comes from the cached entry
        // and is restricted to a known set below.
        _mem.TryGetValue(slug, out var entry);

        // If we have no in-memory entry, OR the in-memory entry is from
        // an older session and doesn't know about the downloaded file
        // (LocalFileExt = null), re-load the disk JSON. A previous run
        // may have populated it.
        if (entry == null || entry.LocalFileExt == null) {
            var jsonPath = Path.Combine(_cacheDir, slug + ".json");
            if (File.Exists(jsonPath)) {
                try {
                    var disk = JsonSerializer.Deserialize<CelestialImage>(File.ReadAllText(jsonPath));
                    if (disk != null) {
                        entry = disk;
                        _mem[slug] = disk;
                    }
                } catch { /* keep whatever _mem had */ }
            }
        }
        if (entry?.LocalFileExt == null) {
            _logger.LogInformation("GetLocalFilePath {Slug}: entry null or no LocalFileExt", slug);
            return null;
        }
        var allowed = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
        if (!allowed.Contains(entry.LocalFileExt.ToLowerInvariant())) {
            _logger.LogInformation("GetLocalFilePath {Slug}: ext {Ext} not allowed", slug, entry.LocalFileExt);
            return null;
        }
        var path = Path.Combine(_cacheDir, slug + entry.LocalFileExt);
        _logger.LogInformation("GetLocalFilePath {Slug}: checking {Path} (exists={Exists}, cacheDir={CacheDir})",
            slug, path, File.Exists(path), _cacheDir);
        if (File.Exists(path)) return path;

        // The in-memory entry says LocalFileExt=X but the file isn't
        // there at that extension. Could be a stale session entry from
        // before the disk download succeeded. Re-read the disk JSON and
        // try again with whatever extension it now records.
        var jsonReload = Path.Combine(_cacheDir, slug + ".json");
        if (File.Exists(jsonReload)) {
            try {
                var disk = JsonSerializer.Deserialize<CelestialImage>(File.ReadAllText(jsonReload));
                if (disk?.LocalFileExt != null
                    && allowed.Contains(disk.LocalFileExt.ToLowerInvariant())) {
                    _mem[slug] = disk;
                    var diskPath = Path.Combine(_cacheDir, slug + disk.LocalFileExt);
                    if (File.Exists(diskPath)) return diskPath;
                }
            } catch { /* fall through */ }
        }
        return null;
    }

    private async Task<string?> TryDownloadThumbAsync(string url, string slug, CancellationToken ct) {
        try {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var ct2 = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            var ext = ct2 switch {
                "image/png"  => ".png",
                "image/gif"  => ".gif",
                "image/webp" => ".webp",
                _            => ".jpg"
            };
            var path = Path.Combine(_cacheDir, slug + ext);
            await using var fs = File.Create(path);
            await resp.Content.CopyToAsync(fs, ct);
            return ext;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Thumb download failed for {Url}", url);
            return null;
        }
    }

    // NASA Image Library — public, no API key. Returns a JSON envelope
    // with collection.items[]; each item has data[0] (metadata) and
    // links[0] (thumbnail href). We scan the first page looking for the
    // first item whose title / description / keywords mention astronomy,
    // skipping unrelated historical archive hits (e.g. "M 22" matches a
    // Mercury-program astronaut report; "M 5" matches a rover assembly
    // photo). If nothing astronomical comes back, return not-available
    // so the Wikipedia fallback gets a shot.
    // Words that, when they appear in a NASA item's title or description,
    // strongly suggest the result IS the celestial object we asked for.
    // Deliberately doesn't include generic terms like "hubble", "webb",
    // "telescope" or "shuttle" — those match observatory launches and
    // historical archive items that aren't pictures of a sky target.
    private static readonly string[] AstroKeywords = new[] {
        "nebula", "galaxy", "cluster", "messier", "ngc ", "caldwell",
        "supernova", "starforming", "star-forming", "globular", "open cluster",
        "deep sky", "deep-sky", "milky way", "andromeda", "magellanic"
    };

    private async Task<CelestialImage> TryNasaAsync(string name, CancellationToken ct) {
        var url = "https://images-api.nasa.gov/search" +
                  $"?q={Uri.EscapeDataString(name)}" +
                  "&media_type=image";
        using var resp = await Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return CelestialImage.NotAvailable("NASA HTTP " + (int)resp.StatusCode);

        var stream = await resp.Content.ReadAsStreamAsync(ct);
        var doc    = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("collection", out var coll)
            || !coll.TryGetProperty("items", out var items)
            || items.GetArrayLength() == 0) {
            return CelestialImage.NotAvailable("NASA no results");
        }

        // Walk up to the first 12 results looking for one with an astro
        // keyword in title/description. Most catalogues (Carina, Beehive,
        // Andromeda, …) hit on the first item — the loop only matters
        // when NASA's relevance ranking pulls in historical archive items.
        var max = Math.Min(items.GetArrayLength(), 12);
        for (var i = 0; i < max; i++) {
            var item = items[i];
            string? thumb = null, full = null, title = null, description = null, credit = null;

            if (item.TryGetProperty("links", out var links) && links.GetArrayLength() > 0
                && links[0].TryGetProperty("href", out var href)) {
                thumb = href.GetString();
            }
            if (item.TryGetProperty("data", out var data) && data.GetArrayLength() > 0) {
                var d = data[0];
                if (d.TryGetProperty("title", out var t))       title       = t.GetString();
                if (d.TryGetProperty("description", out var ds)) description = ds.GetString();
                if (d.TryGetProperty("photographer", out var p)) credit = p.GetString();
                if (string.IsNullOrEmpty(credit) && d.TryGetProperty("secondary_creator", out var sc))
                    credit = sc.GetString();
                if (d.TryGetProperty("nasa_id", out var nid))
                    full = $"https://images.nasa.gov/details/{nid.GetString()}";
            }
            if (string.IsNullOrEmpty(thumb)) continue;
            if (!IsAstronomyRelated(title, description, name)) continue;

            return new CelestialImage(
                Available:     true,
                Source:        "NASA",
                ThumbnailUrl:  thumb,
                FullUrl:       full,
                Title:         title,
                Credit:        credit ?? "NASA",
                Error:         null,
                FetchedAt:     DateTime.UtcNow);
        }
        return CelestialImage.NotAvailable("NASA no astronomy match");
    }

    // Words whose presence in a NASA item's title strongly suggest it's
    // a science chart, schematic or data-product visualisation rather
    // than a photograph of the target. Reject these even if the
    // astronomy-keyword filter would otherwise let them through —
    // a "Mars Albedo and Water Vapour Signature" diagram is not what
    // an astrophotographer wants to see as a thumbnail of Mars.
    private static readonly string[] NonPhotoTitleHints = new[] {
        "diagram", "schematic", "chart", "plot", " graph", "spectrum",
        "spectra", "signature", "infographic", "data product", "map of",
        "concept art", "artist's concept", "artist concept", "artist impression"
    };

    private static bool IsAstronomyRelated(string? title, string? description, string queryName) {
        var hay = ((title ?? "") + " " + (description ?? "")).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(hay)) return false;

        // Hard reject: clearly a diagram / chart / data product.
        var titleLow = (title ?? "").ToLowerInvariant();
        foreach (var bad in NonPhotoTitleHints) {
            if (titleLow.Contains(bad)) return false;
        }

        // Quick win: the query itself appears in the title. NASA-titled
        // press images about a target nearly always mention the target by
        // name. Trim whitespace from both sides to handle "M 31" vs "M31".
        var q = queryName.Trim().ToLowerInvariant();
        if (q.Length > 2 && titleLow.Contains(q)) return true;
        // Otherwise insist on at least one astronomy keyword hit AND the
        // query name somewhere (title OR description).
        if (q.Length > 2 && !hay.Contains(q)) return false;
        foreach (var kw in AstroKeywords) {
            if (hay.Contains(kw)) return true;
        }
        return false;
    }

    // Wikipedia REST page summary. Tries the raw name first, then variants
    // with NGC/IC/Messier prefixes that mirror common article titles.
    private async Task<CelestialImage> TryWikipediaAsync(string name, CancellationToken ct) {
        foreach (var variant in WikipediaVariants(name)) {
            var url = $"https://en.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(variant)}";
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) continue;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("thumbnail", out var thumb)
                || !thumb.TryGetProperty("source", out var src)) continue;
            var title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : variant;
            var fullUrl = doc.RootElement.TryGetProperty("content_urls", out var cu)
                          && cu.TryGetProperty("desktop", out var dk)
                          && dk.TryGetProperty("page", out var pg) ? pg.GetString() : null;
            return new CelestialImage(
                Available:     true,
                Source:        "Wikipedia",
                ThumbnailUrl:  src.GetString()!,
                FullUrl:       fullUrl,
                Title:         title,
                Credit:        "Wikipedia (CC BY-SA)",
                Error:         null,
                FetchedAt:     DateTime.UtcNow);
        }
        return CelestialImage.NotAvailable("Wikipedia no results");
    }

    private static IEnumerable<string> WikipediaVariants(string name) {
        var trimmed = name.Trim();
        // For Messier catalogue codes try the unambiguous "Messier_N"
        // article BEFORE "M N" — Wikipedia's bare "M 22" / "M22" entries
        // are disambig pages or unrelated (M-22 road, M22 grenade, etc.).
        if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^M(essier)?\s*\d+$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)) {
            var num = System.Text.RegularExpressions.Regex.Match(trimmed, @"\d+").Value;
            if (!string.IsNullOrEmpty(num)) {
                yield return $"Messier_{num}";
            }
        }
        // NGC: prefer underscored form (matches actual article slug).
        if (trimmed.StartsWith("NGC", StringComparison.OrdinalIgnoreCase)) {
            var num = trimmed.AsSpan(3).Trim().ToString();
            if (int.TryParse(num, out _)) yield return $"NGC_{num}";
        }
        // IC: same idea.
        if (trimmed.StartsWith("IC", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 2) {
            var num = trimmed.AsSpan(2).Trim().ToString();
            if (int.TryParse(num, out _)) yield return $"IC_{num}";
        }
        // Caldwell: "Caldwell N" — Wikipedia uses that exact title.
        if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^C(aldwell)?\s*\d+$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)) {
            var num = System.Text.RegularExpressions.Regex.Match(trimmed, @"\d+").Value;
            if (!string.IsNullOrEmpty(num)) yield return $"Caldwell_{num}";
        }
        // Always fall back to the raw name as the last resort.
        yield return name;
    }

    private static bool IsExpired(CelestialImage img) {
        var ttl = img.Available ? FoundTtl : MissedTtl;
        return DateTime.UtcNow - img.FetchedAt > ttl;
    }

    private static bool LooksLikeCatalogueCode(string name) {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return false;
        // M N, Messier N, NGC N, IC N, Caldwell N, Sh2-N, Cr N, Mel N, etc.
        return System.Text.RegularExpressions.Regex.IsMatch(
            trimmed,
            @"^(M(essier)?|NGC|IC|Caldwell|C|Sh2|Cr|Mel|Abell)\s*-?\s*\d+",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    // Solar-system bodies whose NASA Image Library first-page results are
    // dominated by science illustrations and data products (the Moon's
    // first hit is the Moon Mineralogy Mapper's Albedo / Water-Signature
    // diagram, Jupiter's is occasionally a probe rendering, etc.).
    // Wikipedia lead images for these articles are reliably iconic photos,
    // so prefer Wikipedia and use NASA only as a fallback.
    private static readonly HashSet<string> WikipediaFirstBodies = new(StringComparer.OrdinalIgnoreCase) {
        "Moon", "Mercury", "Venus", "Earth", "Mars",
        "Jupiter", "Saturn", "Uranus", "Neptune", "Pluto",
        "Sun"
    };

    public static string Slugify(string name) {
        var chars = name.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c))
            .ToArray();
        var s = new string(chars);
        return string.IsNullOrEmpty(s) ? "unknown" : s;
    }
}

public record CelestialImage(
    bool Available,
    string? Source,
    string? ThumbnailUrl,
    string? FullUrl,
    string? Title,
    string? Credit,
    string? Error,
    DateTime FetchedAt,
    int SchemaVersion = 0,
    string? LocalFileExt = null,
    string? LocalUrl = null) {

    public static CelestialImage NotAvailable(string error) =>
        new(false, null, null, null, null, null, error, DateTime.UtcNow, 0, null, null);
}

public record PrefetchSummary(
    int AttemptedCount,
    int FoundCount,
    int MissingCount,
    int AlreadyCachedCount,
    long DownloadedBytes);
