using System.Text;
using Microsoft.AspNetCore.Http;
using NINA.Headless.Services;
using NINA.Headless.Services.Studio;
using SkiaSharp;

namespace NINA.Headless.Endpoints;

/// <summary>
/// HTTP surface for the FILES tab. The endpoints are thin: every
/// operation that touches the disk routes through
/// <see cref="FileBrowserService"/> so safety + logging stay in one
/// place. The two non-trivial bits that *do* live here are:
///   - the preview routing (FITS → JPEG via FitsThumbnailer; raster
///     passthrough; TIFF decode via Skia; text truncation)
///   - the studio-root mutator (writes through to ProfileService.Save)
/// </summary>
public static class FilesEndpoints {
    public static void MapFilesEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/files");

        // --- Roots / list / stat -----------------------------------

        g.MapGet("/roots", (FileBrowserService svc) => Results.Ok(svc.ListRoots()));

        g.MapGet("/list", (FileBrowserService svc, string path, bool? hidden) => {
            try {
                var entries = svc.List(path, hidden ?? false);
                return Results.Ok(new { path, entries });
            } catch (UnauthorizedAccessException ex) {
                return Results.Json(new { error = ex.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            } catch (DirectoryNotFoundException ex) {
                return Results.NotFound(new { error = ex.Message });
            } catch (Exception ex) {
                return Results.Problem(ex.Message);
            }
        });

        g.MapGet("/stat", (FileBrowserService svc, string path) => {
            try {
                var entry = svc.Stat(path);
                return entry == null ? Results.NotFound() : Results.Ok(entry);
            } catch (UnauthorizedAccessException ex) {
                return Results.Json(new { error = ex.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            }
        });

        // --- Download (single file) --------------------------------

        // Streams the file straight from disk via FileStream so a 60MB
        // FITS doesn't sit in memory. Content-Disposition: attachment
        // tells the browser to save-as instead of trying to render.
        g.MapGet("/download", (FileBrowserService svc, string path) => {
            try {
                var stream = svc.OpenRead(path);
                var name = Path.GetFileName(path);
                var mime = FileBrowserService.GuessMime(Path.GetExtension(path));
                return Results.File(stream, mime, fileDownloadName: name);
            } catch (UnauthorizedAccessException ex) {
                return Results.Json(new { error = ex.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            } catch (FileNotFoundException ex) {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // --- Multi-download as streaming ZIP -----------------------

        // POST body: { paths: string[], rootForNames?: string }.
        // The response body is the ZIP archive itself; Kestrel writes
        // it incrementally as ZipArchive flushes entries.
        g.MapPost("/download-zip", async (FileBrowserService svc, HttpContext ctx,
                                          ZipRequest req, CancellationToken ct) => {
            if (req.Paths == null || req.Paths.Count == 0)
                return Results.BadRequest(new { error = "paths is required" });
            try {
                var fileName = (req.FileName ?? "polaris-files.zip");
                ctx.Response.ContentType = "application/zip";
                ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
                await svc.WriteZipAsync(req.Paths, ctx.Response.Body, req.RootForNames, ct);
                return Results.Empty;
            } catch (UnauthorizedAccessException ex) {
                return Results.Json(new { error = ex.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            } catch (FileNotFoundException ex) {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // --- Preview -----------------------------------------------

        // Per type: FITS → stretched JPEG via FitsThumbnailer; raster
        // formats pass through unchanged (browser decodes natively);
        // TIFF gets decoded via Skia to PNG; text gets the first
        // ~32 KB as text/plain. Unknown formats → 415.
        g.MapGet("/preview", async (FileBrowserService svc, string path,
                                    int? maxDim, CancellationToken ct) => {
            try {
                var full = svc.ResolveSafe(path, mustExist: true);
                if (!File.Exists(full))
                    return Results.NotFound(new { error = "Not a file" });
                var kind = FileBrowserService.ClassifyForPreview(full);
                var max  = maxDim ?? 1600;

                switch (kind) {
                    case PreviewKind.Fits: {
                        var jpeg = await Task.Run(()
                            => FitsThumbnailer.RenderJpegFromPath(full, maxDim: max, quality: 90), ct);
                        return Results.File(jpeg, "image/jpeg");
                    }
                    case PreviewKind.RasterPassthrough: {
                        var stream = svc.OpenRead(full);
                        return Results.File(stream,
                            FileBrowserService.GuessMime(Path.GetExtension(full)));
                    }
                    case PreviewKind.TiffDecode: {
                        var png = await Task.Run(() => DecodeRasterToPng(full, max), ct);
                        return png == null
                            ? Results.UnprocessableEntity(new { error = "TIFF decode failed" })
                            : Results.File(png, "image/png");
                    }
                    case PreviewKind.Text: {
                        var text = await ReadHeadAsync(full, maxBytes: 32 * 1024, ct);
                        return Results.Text(text, "text/plain", Encoding.UTF8);
                    }
                    default:
                        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
                }
            } catch (UnauthorizedAccessException ex) {
                return Results.Json(new { error = ex.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            } catch (FileNotFoundException ex) {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // 256 px thumbnail with on-disk cache keyed by path hash so a
        // grid of 200 FITS doesn't keep regenerating on every refresh.
        g.MapGet("/thumb", async (FileBrowserService svc, IWebHostEnvironment env,
                                  string path, CancellationToken ct) => {
            try {
                var full = svc.ResolveSafe(path, mustExist: true);
                var kind = FileBrowserService.ClassifyForPreview(full);
                if (kind != PreviewKind.Fits && kind != PreviewKind.RasterPassthrough
                    && kind != PreviewKind.TiffDecode)
                    return Results.NotFound();

                var cacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NINA.Headless", "files", "thumbs");
                Directory.CreateDirectory(cacheDir);
                var cachePath = Path.Combine(cacheDir, FileBrowserService.PathHash(full) + ".jpg");

                // Regenerate if the source is newer than the cache (the
                // user might have re-processed the file in the meantime).
                var srcMtime = File.GetLastWriteTimeUtc(full);
                if (File.Exists(cachePath)
                    && File.GetLastWriteTimeUtc(cachePath) >= srcMtime) {
                    return Results.File(cachePath, "image/jpeg");
                }

                byte[]? jpeg = kind switch {
                    PreviewKind.Fits             => await Task.Run(()
                        => FitsThumbnailer.RenderJpegFromPath(full, 256, 80), ct),
                    PreviewKind.RasterPassthrough => await Task.Run(()
                        => DecodeRasterToJpeg(full, 256), ct),
                    PreviewKind.TiffDecode       => await Task.Run(()
                        => DecodeRasterToJpeg(full, 256), ct),
                    _ => null
                };
                if (jpeg == null) return Results.NotFound();
                await File.WriteAllBytesAsync(cachePath, jpeg, ct);
                return Results.File(jpeg, "image/jpeg");
            } catch (UnauthorizedAccessException ex) {
                return Results.Json(new { error = ex.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            } catch {
                return Results.NotFound();
            }
        });

        // --- Mutations ---------------------------------------------

        g.MapPost("/copy", async (FileBrowserService svc, CopyMoveRequest req, CancellationToken ct) => {
            try {
                await svc.CopyAsync(req.Src, req.Dst, req.Overwrite, ct);
                return Results.Ok(new { ok = true });
            } catch (Exception ex) { return MapError(ex); }
        });

        g.MapPost("/move", async (FileBrowserService svc, CopyMoveRequest req, CancellationToken ct) => {
            try {
                await svc.MoveAsync(req.Src, req.Dst, req.Overwrite, ct);
                return Results.Ok(new { ok = true });
            } catch (Exception ex) { return MapError(ex); }
        });

        // Delete is the only mutator that requires an explicit
        // confirmed=true flag. The UI sets it after window.confirm().
        // Server-side guard so anything else hitting the API (curl,
        // a buggy client) can't blow away files by accident.
        g.MapPost("/delete", async (FileBrowserService svc, DeleteRequest req, CancellationToken ct) => {
            if (!req.Confirmed)
                return Results.Json(new { error = "confirmed=true is required" },
                    statusCode: StatusCodes.Status409Conflict);
            try {
                foreach (var path in req.Paths)
                    await svc.DeleteAsync(path, recursive: true, ct);
                return Results.Ok(new { ok = true, deleted = req.Paths.Count });
            } catch (Exception ex) { return MapError(ex); }
        });

        g.MapPost("/mkdir", async (FileBrowserService svc, MkdirRequest req) => {
            try {
                await svc.CreateFolderAsync(req.Parent, req.Name);
                return Results.Ok(new { ok = true });
            } catch (Exception ex) { return MapError(ex); }
        });

        g.MapPost("/rename", async (FileBrowserService svc, RenameRequest req) => {
            try {
                await svc.RenameAsync(req.Path, req.NewName);
                return Results.Ok(new { ok = true });
            } catch (Exception ex) { return MapError(ex); }
        });

        // --- Studio root setter -----------------------------------

        // Convenience endpoint: validates the path, writes through to
        // the profile's ImageOutputDir, saves the profile. The STUDIO
        // tab rescans on its next visit (it reads ImageOutputDir live).
        g.MapPost("/studio-root", (FileBrowserService svc, ProfileService profiles,
                                   StudioRootRequest req) => {
            try {
                var full = svc.ResolveSafe(req.Path, mustExist: true);
                if (!Directory.Exists(full))
                    return Results.BadRequest(new { error = "Path is not a directory" });
                profiles.Active.ImageOutputDir = full;
                profiles.Save();
                return Results.Ok(new { ok = true, imageOutputDir = full });
            } catch (Exception ex) { return MapError(ex); }
        });
    }

    private static IResult MapError(Exception ex) => ex switch {
        UnauthorizedAccessException uae => Results.Json(new { error = uae.Message },
            statusCode: StatusCodes.Status403Forbidden),
        ArgumentException ae => Results.BadRequest(new { error = ae.Message }),
        FileNotFoundException fnf => Results.NotFound(new { error = fnf.Message }),
        DirectoryNotFoundException dnf => Results.NotFound(new { error = dnf.Message }),
        IOException ioe => Results.Json(new { error = ioe.Message },
            statusCode: StatusCodes.Status409Conflict),
        _ => Results.Problem(ex.Message)
    };

    // --- Helpers for the preview endpoint --------------------------

    private static byte[]? DecodeRasterToPng(string path, int maxDim) {
        using var input = File.OpenRead(path);
        using var bmp = SKBitmap.Decode(input);
        if (bmp == null) return null;
        using var resized = ResizeIfLarger(bmp, maxDim);
        using var img = SKImage.FromBitmap(resized ?? bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[]? DecodeRasterToJpeg(string path, int maxDim) {
        using var input = File.OpenRead(path);
        using var bmp = SKBitmap.Decode(input);
        if (bmp == null) return null;
        using var resized = ResizeIfLarger(bmp, maxDim);
        using var img = SKImage.FromBitmap(resized ?? bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }

    private static SKBitmap? ResizeIfLarger(SKBitmap bmp, int maxDim) {
        var longSide = Math.Max(bmp.Width, bmp.Height);
        if (longSide <= maxDim) return null;
        var scale = (double)maxDim / longSide;
        var w = Math.Max(1, (int)Math.Round(bmp.Width * scale));
        var h = Math.Max(1, (int)Math.Round(bmp.Height * scale));
        return bmp.Resize(new SKImageInfo(w, h, bmp.ColorType, bmp.AlphaType),
            SKSamplingOptions.Default);
    }

    private static async Task<string> ReadHeadAsync(string path, int maxBytes, CancellationToken ct) {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buf = new byte[Math.Min(maxBytes, fs.Length)];
        var read = await fs.ReadAsync(buf, ct);
        // Skip a leading UTF-8 BOM if present so the editor doesn't
        // show a stray glyph at the top of the preview.
        var start = 0;
        if (read >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) start = 3;
        var truncated = read >= maxBytes;
        var text = Encoding.UTF8.GetString(buf, start, read - start);
        if (truncated) text += "\n\n--- truncated to first " + maxBytes + " bytes ---\n";
        return text;
    }

    // --- DTOs --------------------------------------------------------

    public record CopyMoveRequest(string Src, string Dst, bool Overwrite);
    public record DeleteRequest(List<string> Paths, bool Confirmed);
    public record MkdirRequest(string Parent, string Name);
    public record RenameRequest(string Path, string NewName);
    public record ZipRequest(List<string> Paths, string? RootForNames, string? FileName);
    public record StudioRootRequest(string Path);
}
