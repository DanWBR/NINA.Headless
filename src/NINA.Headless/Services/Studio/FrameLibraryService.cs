using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using SkiaSharp;

namespace NINA.Headless.Services.Studio;

/// <summary>
/// Indexes captured frames (.fits) under the active profile's
/// <c>ImageOutputDir</c> so the STUDIO panel can browse, filter, and
/// open them without re-walking the disk on every list request.
///
/// Strategy:
///   - SQLite cache at {AppData}/NINA.Headless/studio/frames.db.
///   - On <see cref="RescanAsync"/>, walk the output dir recursively,
///     decode FITS headers only (skips the pixel block), upsert rows
///     keyed by absolute path.
///   - Rows removed when the underlying file no longer exists.
///   - Thumbnails generated on-demand by <see cref="GetThumbnailAsync"/>
///     and cached on disk at {AppData}/NINA.Headless/studio/thumbs/.
///
/// Why not just glob the dir every time? A session with 2000 frames
/// has ~120 MB of FITS headers — parsing them all on every UI refresh
/// stalls the browser. Cached metadata responds in &lt;50 ms.
/// </summary>
public class FrameLibraryService {
    private readonly ProfileService _profile;
    private readonly ILogger<FrameLibraryService> _logger;
    private readonly string _studioDir;
    private readonly string _dbPath;
    private readonly string _thumbDir;
    private readonly SemaphoreSlim _rescanLock = new(1, 1);

    // Background rescan progress (single one at a time).
    public RescanProgress Rescan { get; private set; } = new(false, 0, 0, null);

    public FrameLibraryService(ProfileService profile, IConfiguration config,
                               ILogger<FrameLibraryService> logger) {
        _profile = profile;
        _logger = logger;
        var baseDir = config.GetValue("Studio:Directory",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA.Headless", "studio"))!;
        _studioDir = baseDir;
        _dbPath = Path.Combine(_studioDir, "frames.db");
        _thumbDir = Path.Combine(_studioDir, "thumbs");
        Directory.CreateDirectory(_studioDir);
        Directory.CreateDirectory(_thumbDir);
        EnsureSchema();
    }

    private string ConnString => $"Data Source={_dbPath}";

    private void EnsureSchema() {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS frames (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                path         TEXT NOT NULL UNIQUE,
                file_name    TEXT NOT NULL,
                image_type   TEXT,
                filter       TEXT,
                target       TEXT,
                exposure_sec REAL,
                gain         INTEGER,
                offset_val   INTEGER,
                width        INTEGER,
                height       INTEGER,
                bayer        TEXT,
                date_obs     TEXT,
                file_size    INTEGER,
                indexed_at   TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_frames_target ON frames(target);
            CREATE INDEX IF NOT EXISTS idx_frames_filter ON frames(filter);
            CREATE INDEX IF NOT EXISTS idx_frames_type   ON frames(image_type);
            CREATE INDEX IF NOT EXISTS idx_frames_date   ON frames(date_obs);
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Walk the active profile's image output dir for .fits files,
    /// decode headers only, upsert into the SQLite cache. Files that
    /// have disappeared from disk are removed.
    /// </summary>
    public async Task RescanAsync(CancellationToken ct = default) {
        if (!await _rescanLock.WaitAsync(0, ct)) {
            _logger.LogWarning("Rescan already in progress, skipping");
            return;
        }
        try {
            var root = _profile.Active.ImageOutputDir;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) {
                Rescan = new RescanProgress(false, 0, 0, "No image output dir configured");
                return;
            }

            var files = Directory.EnumerateFiles(root, "*.fits", SearchOption.AllDirectories).ToList();
            Rescan = new RescanProgress(true, 0, files.Count, null);

            using var c = new SqliteConnection(ConnString);
            await c.OpenAsync(ct);

            // Drop rows whose file no longer exists.
            using (var prune = c.CreateCommand()) {
                prune.CommandText = "SELECT path FROM frames";
                using var rdr = await prune.ExecuteReaderAsync(ct);
                var toDelete = new List<string>();
                while (await rdr.ReadAsync(ct)) {
                    var p = rdr.GetString(0);
                    if (!File.Exists(p)) toDelete.Add(p);
                }
                rdr.Close();
                foreach (var p in toDelete) {
                    using var del = c.CreateCommand();
                    del.CommandText = "DELETE FROM frames WHERE path = $p";
                    del.Parameters.AddWithValue("$p", p);
                    await del.ExecuteNonQueryAsync(ct);
                }
            }

            var i = 0;
            foreach (var path in files) {
                if (ct.IsCancellationRequested) break;
                try {
                    UpsertFrame(c, path);
                } catch (Exception ex) {
                    _logger.LogDebug(ex, "Failed to index {Path}", path);
                }
                i++;
                Rescan = Rescan with { Done = i };
            }
            Rescan = new RescanProgress(false, i, files.Count, null);
        } catch (Exception ex) {
            _logger.LogError(ex, "Rescan failed");
            Rescan = new RescanProgress(false, Rescan.Done, Rescan.Total, ex.Message);
        } finally {
            _rescanLock.Release();
        }
    }

    private void UpsertFrame(SqliteConnection c, string path) {
        var fi = new FileInfo(path);

        // Read FITS headers only. We don't need pixels for the index;
        // parsing the full pixel block on a 32MP image is wasteful here.
        using var fs = File.OpenRead(path);
        var headers = FITSReader.ReadHeadersOnly(fs);

        var imageType  = HeaderString(headers, "IMAGETYP", "LIGHT");
        var filter     = HeaderString(headers, "FILTER", "");
        var target     = HeaderString(headers, "OBJECT", "");
        var exposure   = HeaderDouble(headers, "EXPOSURE", HeaderDouble(headers, "EXPTIME", 0));
        var gain       = HeaderInt(headers, "GAIN", 0);
        var offset     = HeaderInt(headers, "OFFSET", 0);
        var width      = HeaderInt(headers, "NAXIS1", 0);
        var height     = HeaderInt(headers, "NAXIS2", 0);
        var bayer      = HeaderString(headers, "BAYERPAT", "");
        var dateObs    = HeaderString(headers, "DATE-OBS", "");

        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO frames (path, file_name, image_type, filter, target, exposure_sec,
                                gain, offset_val, width, height, bayer, date_obs,
                                file_size, indexed_at)
            VALUES ($path, $name, $type, $filter, $target, $exp,
                    $gain, $offset, $w, $h, $bayer, $dt, $sz, $idx)
            ON CONFLICT(path) DO UPDATE SET
                image_type=excluded.image_type,
                filter=excluded.filter,
                target=excluded.target,
                exposure_sec=excluded.exposure_sec,
                gain=excluded.gain,
                offset_val=excluded.offset_val,
                width=excluded.width,
                height=excluded.height,
                bayer=excluded.bayer,
                date_obs=excluded.date_obs,
                file_size=excluded.file_size,
                indexed_at=excluded.indexed_at;
        ";
        cmd.Parameters.AddWithValue("$path",   path);
        cmd.Parameters.AddWithValue("$name",   fi.Name);
        cmd.Parameters.AddWithValue("$type",   imageType);
        cmd.Parameters.AddWithValue("$filter", filter);
        cmd.Parameters.AddWithValue("$target", target);
        cmd.Parameters.AddWithValue("$exp",    exposure);
        cmd.Parameters.AddWithValue("$gain",   gain);
        cmd.Parameters.AddWithValue("$offset", offset);
        cmd.Parameters.AddWithValue("$w",      width);
        cmd.Parameters.AddWithValue("$h",      height);
        cmd.Parameters.AddWithValue("$bayer",  bayer);
        cmd.Parameters.AddWithValue("$dt",     dateObs);
        cmd.Parameters.AddWithValue("$sz",     fi.Length);
        cmd.Parameters.AddWithValue("$idx",    DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<FrameRow> Query(FrameQuery q) {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        var where = new List<string>();
        void P(string col, string param, object? val) {
            if (val == null) return;
            if (val is string s && string.IsNullOrEmpty(s)) return;
            where.Add($"{col} = {param}");
            cmd.Parameters.AddWithValue(param, val);
        }
        P("image_type", "$type",   q.Type);
        P("filter",     "$filter", q.Filter);
        P("target",     "$target", q.Target);
        if (!string.IsNullOrEmpty(q.DateFrom)) {
            where.Add("date_obs >= $df");
            cmd.Parameters.AddWithValue("$df", q.DateFrom);
        }
        if (!string.IsNullOrEmpty(q.DateTo)) {
            where.Add("date_obs <= $dt2");
            cmd.Parameters.AddWithValue("$dt2", q.DateTo);
        }
        var sql = "SELECT id, path, file_name, image_type, filter, target, exposure_sec, " +
                  "gain, offset_val, width, height, bayer, date_obs, file_size " +
                  "FROM frames";
        if (where.Count > 0) sql += " WHERE " + string.Join(" AND ", where);
        sql += " ORDER BY date_obs DESC LIMIT $limit OFFSET $offset";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$limit",  Math.Clamp(q.Limit, 1, 500));
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, q.Offset));
        var list = new List<FrameRow>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) {
            list.Add(new FrameRow(
                Id:          rdr.GetInt32(0),
                Path:        rdr.GetString(1),
                FileName:    rdr.GetString(2),
                ImageType:   rdr.IsDBNull(3) ? "" : rdr.GetString(3),
                Filter:      rdr.IsDBNull(4) ? "" : rdr.GetString(4),
                Target:      rdr.IsDBNull(5) ? "" : rdr.GetString(5),
                ExposureSec: rdr.IsDBNull(6) ? 0 : rdr.GetDouble(6),
                Gain:        rdr.IsDBNull(7) ? 0 : rdr.GetInt32(7),
                Offset:      rdr.IsDBNull(8) ? 0 : rdr.GetInt32(8),
                Width:       rdr.IsDBNull(9) ? 0 : rdr.GetInt32(9),
                Height:      rdr.IsDBNull(10) ? 0 : rdr.GetInt32(10),
                Bayer:       rdr.IsDBNull(11) ? "" : rdr.GetString(11),
                DateObs:     rdr.IsDBNull(12) ? "" : rdr.GetString(12),
                FileSize:    rdr.IsDBNull(13) ? 0 : rdr.GetInt64(13)
            ));
        }
        return list;
    }

    public FrameRow? GetById(int id) {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, path, file_name, image_type, filter, target, exposure_sec, " +
                          "gain, offset_val, width, height, bayer, date_obs, file_size FROM frames WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var rdr = cmd.ExecuteReader();
        if (!rdr.Read()) return null;
        return new FrameRow(
            Id:          rdr.GetInt32(0),
            Path:        rdr.GetString(1),
            FileName:    rdr.GetString(2),
            ImageType:   rdr.IsDBNull(3) ? "" : rdr.GetString(3),
            Filter:      rdr.IsDBNull(4) ? "" : rdr.GetString(4),
            Target:      rdr.IsDBNull(5) ? "" : rdr.GetString(5),
            ExposureSec: rdr.IsDBNull(6) ? 0 : rdr.GetDouble(6),
            Gain:        rdr.IsDBNull(7) ? 0 : rdr.GetInt32(7),
            Offset:      rdr.IsDBNull(8) ? 0 : rdr.GetInt32(8),
            Width:       rdr.IsDBNull(9) ? 0 : rdr.GetInt32(9),
            Height:      rdr.IsDBNull(10) ? 0 : rdr.GetInt32(10),
            Bayer:       rdr.IsDBNull(11) ? "" : rdr.GetString(11),
            DateObs:     rdr.IsDBNull(12) ? "" : rdr.GetString(12),
            FileSize:    rdr.IsDBNull(13) ? 0 : rdr.GetInt64(13)
        );
    }

    public StudioStats GetStats() {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*) total,
                   COALESCE(SUM(exposure_sec), 0) totalExposure,
                   COUNT(DISTINCT target) targets,
                   COUNT(DISTINCT filter) filters
            FROM frames WHERE image_type = 'LIGHT';
        ";
        using var rdr = cmd.ExecuteReader();
        if (!rdr.Read()) return new StudioStats(0, 0, 0, 0);
        return new StudioStats(
            TotalLights:        rdr.GetInt32(0),
            TotalExposureHours: rdr.GetDouble(1) / 3600.0,
            DistinctTargets:    rdr.GetInt32(2),
            DistinctFilters:    rdr.GetInt32(3));
    }

    /// <summary>
    /// Generate (or return cached) thumbnail JPEG for a frame, 256 px on
    /// the long side. Auto-stretched grayscale — good enough for browse.
    /// </summary>
    public async Task<string?> GetThumbnailAsync(int frameId, CancellationToken ct = default) {
        var row = GetById(frameId);
        if (row == null || !File.Exists(row.Path)) return null;
        var cachePath = Path.Combine(_thumbDir, $"{frameId}.jpg");
        if (File.Exists(cachePath)) return cachePath;
        try {
            // Decode + stretch + encode is CPU-bound; push it onto the
            // thread pool so the request thread isn't blocked. Skia's
            // encoder is sync, so wrapping in Task.Run is the cleanest
            // way to keep the endpoint async.
            await Task.Run(() => GenerateThumbnail(row, cachePath), ct);
            return cachePath;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Thumbnail generation failed for frame {Id}", frameId);
            return null;
        }
    }

    private static void GenerateThumbnail(FrameRow row, string cachePath) {
        // Heavy lifting now lives in FitsThumbnailer so the FILES tab
        // can reuse the exact same renderer for paths it has never
        // indexed (a master FITS dropped in any folder, etc).
        var jpeg = FitsThumbnailer.RenderJpegFromPath(row.Path, maxDim: 256, quality: 85);
        File.WriteAllBytes(cachePath, jpeg);
    }

    // --- Header helpers ---
    private static string HeaderString(IDictionary<string, FITSHeaderCard> h, string key, string def) =>
        h.TryGetValue(key, out var c) ? c.Value?.Trim().Trim('\'').Trim() ?? def : def;
    private static int HeaderInt(IDictionary<string, FITSHeaderCard> h, string key, int def) =>
        h.TryGetValue(key, out var c) && int.TryParse(c.Value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
    private static double HeaderDouble(IDictionary<string, FITSHeaderCard> h, string key, double def) =>
        h.TryGetValue(key, out var c) && double.TryParse(c.Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
}

public record RescanProgress(bool InProgress, int Done, int Total, string? Error);
public record FrameQuery(string? Type, string? Filter, string? Target,
                         string? DateFrom, string? DateTo, int Limit, int Offset);
public record FrameRow(int Id, string Path, string FileName,
                       string ImageType, string Filter, string Target,
                       double ExposureSec, int Gain, int Offset,
                       int Width, int Height, string Bayer,
                       string DateObs, long FileSize);
public record StudioStats(int TotalLights, double TotalExposureHours,
                          int DistinctTargets, int DistinctFilters);
