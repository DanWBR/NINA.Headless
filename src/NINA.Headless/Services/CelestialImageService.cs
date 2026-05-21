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
                if (cached != null && !IsExpired(cached)) {
                    _mem[slug] = cached;
                    return cached;
                }
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Bad cache file {Path}", path);
            }
        }

        // Live lookup. Try NASA first (usually richer images), then Wikipedia.
        CelestialImage result;
        try {
            result = await TryNasaAsync(name, ct);
            if (!result.Available) result = await TryWikipediaAsync(name, ct);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Image lookup failed for {Name}", name);
            result = CelestialImage.NotAvailable("Lookup failed");
        }

        result = result with { FetchedAt = DateTime.UtcNow };
        _mem[slug] = result;
        try {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result), ct);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Could not persist cache file {Path}", path);
        }
        return result;
    }

    // NASA Image Library — public, no API key. Returns a JSON envelope with
    // collection.items[]; each item has data[0] (metadata) and links[0]
    // (thumbnail href). We pick the highest-scoring item by simple
    // heuristic: NASA's own ordering is good enough most of the time.
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

        var first = items[0];
        string? thumb = null, full = null, title = null, credit = null;
        if (first.TryGetProperty("links", out var links) && links.GetArrayLength() > 0
            && links[0].TryGetProperty("href", out var href)) {
            thumb = href.GetString();
        }
        if (first.TryGetProperty("data", out var data) && data.GetArrayLength() > 0) {
            var d = data[0];
            if (d.TryGetProperty("title", out var t))      title  = t.GetString();
            if (d.TryGetProperty("photographer", out var p)) credit = p.GetString();
            if (string.IsNullOrEmpty(credit) && d.TryGetProperty("secondary_creator", out var sc))
                credit = sc.GetString();
            if (d.TryGetProperty("nasa_id", out var nid))
                full = $"https://images.nasa.gov/details/{nid.GetString()}";
        }
        if (string.IsNullOrEmpty(thumb)) return CelestialImage.NotAvailable("NASA no thumbnail");
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
        yield return name;
        var trimmed = name.Trim();
        if (trimmed.StartsWith("M", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 1
            && int.TryParse(trimmed.AsSpan(1), out var m)) {
            yield return $"Messier_{m}";
        }
        if (trimmed.StartsWith("NGC", StringComparison.OrdinalIgnoreCase)) {
            var num = trimmed.AsSpan(3).Trim().ToString();
            if (int.TryParse(num, out _)) yield return $"NGC_{num}";
        }
        if (trimmed.StartsWith("IC", StringComparison.OrdinalIgnoreCase)) {
            var num = trimmed.AsSpan(2).Trim().ToString();
            if (int.TryParse(num, out _)) yield return $"IC_{num}";
        }
    }

    private static bool IsExpired(CelestialImage img) {
        var ttl = img.Available ? FoundTtl : MissedTtl;
        return DateTime.UtcNow - img.FetchedAt > ttl;
    }

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
    DateTime FetchedAt) {

    public static CelestialImage NotAvailable(string error) =>
        new(false, null, null, null, null, null, error, DateTime.UtcNow);
}
