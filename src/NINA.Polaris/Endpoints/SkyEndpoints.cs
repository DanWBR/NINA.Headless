using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

public static class SkyEndpoints {
    public static void MapSkyEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/sky");

        group.MapPost("/slew-and-center", (SlewAndCenterRequest request,
            SlewCenterService slewCenter) => {
            var job = slewCenter.StartJob(request.Ra, request.Dec, request.ToleranceArcsec);
            return Results.Accepted(value: new {
                jobId = job.Id,
                target = new { request.Ra, request.Dec },
                toleranceArcsec = request.ToleranceArcsec,
                state = job.State.ToString().ToLowerInvariant()
            });
        });

        group.MapGet("/slew-and-center/{jobId}/status", (string jobId,
            SlewCenterService slewCenter) => {
            var job = slewCenter.GetJob(jobId);
            if (job == null)
                return Results.NotFound(new { error = "Job not found" });

            return Results.Ok(new {
                jobId = job.Id,
                state = job.State.ToString().ToLowerInvariant(),
                iteration = job.Iteration,
                targetRa = job.TargetRa,
                targetDec = job.TargetDec,
                actualRa = job.ActualRa,
                actualDec = job.ActualDec,
                errorArcsec = job.ErrorArcsec,
                rotation = job.Rotation,
                scale = job.Scale,
                error = job.Error
            });
        });

        group.MapPost("/slew-and-center/{jobId}/cancel", (string jobId,
            SlewCenterService slewCenter) => {
            slewCenter.CancelJob(jobId);
            return Results.Ok(new { jobId, state = "cancelled" });
        });

        group.MapGet("/catalog/search", (string query, SkyCatalogService catalog) => {
            var results = catalog.Search(query);
            return Results.Ok(new {
                query,
                results = results.Select(o => new {
                    name = o.Name,
                    ra = o.Ra,
                    dec = o.Dec,
                    raFormatted = o.RaFormatted,
                    decFormatted = o.DecFormatted,
                    magnitude = o.Magnitude,
                    type = o.Type,
                    commonName = o.CommonName,
                    aliases = o.Aliases
                })
            });
        });

        group.MapGet("/catalog/{name}", (string name, SkyCatalogService catalog) => {
            var obj = catalog.GetByName(name);
            if (obj == null)
                return Results.NotFound(new { error = "Object not found" });

            return Results.Ok(new {
                name = obj.Name,
                ra = obj.Ra,
                dec = obj.Dec,
                raFormatted = obj.RaFormatted,
                decFormatted = obj.DecFormatted,
                magnitude = obj.Magnitude,
                type = obj.Type,
                commonName = obj.CommonName,
                aliases = obj.Aliases
            });
        });

        group.MapGet("/fov", (IConfiguration config) => {
            var sw = config.GetValue("Optics:SensorWidthMm", 23.5);
            var sh = config.GetValue("Optics:SensorHeightMm", 15.7);
            var fl = config.GetValue("Optics:FocalLengthMm", 478.0);

            double fovW = 0, fovH = 0, scale = 0;
            if (fl > 0) {
                fovW = 2 * Math.Atan(sw / (2 * fl)) * (180.0 / Math.PI);
                fovH = 2 * Math.Atan(sh / (2 * fl)) * (180.0 / Math.PI);
                scale = (sw / 1000.0) / (fl / 1000.0) * 206.265;
            }

            return Results.Ok(new {
                sensorWidthMm = sw,
                sensorHeightMm = sh,
                focalLengthMm = fl,
                fovWidthDeg = Math.Round(fovW, 4),
                fovHeightDeg = Math.Round(fovH, 4),
                scaleArcsecPerPixel = Math.Round(scale, 3)
            });
        });

        group.MapGet("/solver/status", (PlateSolveService solver) => {
            return Results.Ok(new {
                available = solver.IsAvailable,
                primaryId = solver.PrimarySolver.Id,
                primaryName = solver.PrimarySolver.DisplayName,
                primaryAvailable = solver.PrimarySolver.IsAvailable,
                blindId = solver.BlindSolver?.Id,
                blindName = solver.BlindSolver?.DisplayName,
                blindAvailable = solver.BlindSolver?.IsAvailable ?? false,
                path = solver.SolverPath
            });
        });

        group.MapGet("/solver/list", (PlateSolveService solver) => {
            return Results.Ok(solver.AllSolvers.Select(s => new {
                id = s.Id,
                name = s.DisplayName,
                available = s.IsAvailable,
                blind = s.SupportsBlindSolve
            }));
        });

        // ---- Catalog filters (Sky Atlas) ----

        group.MapGet("/catalog/types", (SkyCatalogService catalog) => {
            return Results.Ok(catalog.GetObjectTypes());
        });

        // CAT-4: list of distinct catalog sources present in the DB
        // (NGC / IC / M / C / Arp / Sh2 / HCG / AGC). Feeds the Atlas
        // panel's catalog dropdown. Empty list when running on the
        // legacy hardcoded fallback (no DsoCatalog).
        group.MapGet("/catalog/catalogs", (SkyCatalogService catalog) => {
            return Results.Ok(catalog.GetCatalogs());
        });

        group.MapGet("/catalog/filter", (string? query, string? type,
            string? catalogId, string? constellation,
            double? minMag, double? maxMag, double? minDec, double? maxDec,
            int? limit, SkyCatalogService catalog) => {
            var results = catalog.Filter(new CatalogFilter {
                Query = query, Type = type,
                // CAT-4: new optional filters. Old clients omit these
                // and behaviour matches the pre-CAT-4 endpoint.
                Catalog = catalogId, Constellation = constellation,
                MinMagnitude = minMag, MaxMagnitude = maxMag,
                MinDec = minDec, MaxDec = maxDec
            }, Math.Clamp(limit ?? 50, 1, 500));

            return Results.Ok(new {
                count = results.Count,
                results = results.Select(o => new {
                    name = o.Name, ra = o.Ra, dec = o.Dec,
                    raFormatted = o.RaFormatted, decFormatted = o.DecFormatted,
                    magnitude = o.Magnitude, type = o.Type,
                    commonName = o.CommonName, aliases = o.Aliases,
                    // CAT-4: new optional fields. Old UIs ignore them.
                    catalog = o.Catalog, catalogId = o.CatalogId,
                    constellation = o.Constellation, sizeArcmin = o.SizeArcmin
                })
            });
        });

        // CAT-4: cone search. Returns DSO catalog entries within
        // `radius` degrees of (`ra` hours, `dec` deg), optionally
        // mag-limited. Used for "what's in my FOV" overlays + future
        // Mosaic auto-suggest. Returns 503 when the expanded DB
        // isn't available (legacy fallback can't answer this).
        group.MapGet("/catalog/near", (double ra, double dec, double radius,
            double? maxMag, int? limit,
            NINA.Polaris.Services.Sky.DsoCatalog dso) => {
            if (!dso.IsAvailable) {
                return Results.Json(new {
                    error = "Expanded DSO catalog not available. " +
                            "Run `python scripts/build-dso-catalog.py` to populate it."
                }, statusCode: 503);
            }
            var hits = dso.QueryRegionAsync(ra, dec,
                Math.Max(0.01, radius), maxMag,
                Math.Clamp(limit ?? 200, 1, 1000)
            ).GetAwaiter().GetResult();
            return Results.Ok(new {
                count = hits.Count,
                results = hits.Select(o => new {
                    name = o.Name, ra = o.RaHours, dec = o.DecDeg,
                    magnitude = o.Magnitude, type = o.Type,
                    commonName = o.CommonName,
                    catalog = o.Catalog, catalogId = o.CatalogId,
                    constellation = o.Constellation,
                    sizeArcmin = o.SizeArcmin, aliases = o.Aliases
                })
            });
        });

        // ---- Altitude chart + night window ----

        group.MapGet("/altitude", (double ra, double dec, int? stepMinutes,
            AltitudeService alt, ProfileService profile) => {
            // Default window: current night (sunset to sunrise) at the
            // observer's longitude. If no profile lat/lon yet, fall back to
            // ±6h around now so the chart still draws something useful.
            var window = alt.ComputeNightWindow();
            DateTime from, to;
            if (Math.Abs(profile.Active.Latitude) > 0.01 || Math.Abs(profile.Active.Longitude) > 0.01) {
                from = window.Sunset;
                to = window.Sunrise;
            } else {
                from = DateTime.UtcNow.AddHours(-6);
                to = DateTime.UtcNow.AddHours(6);
            }

            var step = Math.Clamp(stepMinutes ?? 15, 1, 120);
            var track = alt.ComputeTrack(ra, dec, from, to, step);

            return Results.Ok(new {
                target = new { ra, dec },
                fromUtc = from,
                toUtc = to,
                stepMinutes = step,
                samples = track,
                twilight = new {
                    sunset = window.Sunset,
                    civilDusk = window.CivilDuskUtc,
                    nauticalDusk = window.NauticalDuskUtc,
                    astronomicalDusk = window.AstronomicalDuskUtc,
                    astronomicalDawn = window.AstronomicalDawnUtc,
                    nauticalDawn = window.NauticalDawnUtc,
                    civilDawn = window.CivilDawnUtc,
                    sunrise = window.Sunrise
                }
            });
        });

        // Resolve a celestial object name to a thumbnail image URL (NASA
        // Image Library, falling back to Wikipedia). Caches per-name on
        // disk for 30 days. Returns { available: false } when neither
        // provider has anything, never 500s.
        group.MapGet("/image", async (string name, CelestialImageService svc, CancellationToken ct) => {
            if (string.IsNullOrWhiteSpace(name)) {
                return Results.BadRequest(new { error = "name is required" });
            }
            var img = await svc.GetImageAsync(name, ct);
            return Results.Ok(img);
        });

        // Serve a cached thumbnail blob from disk. Pairs with the
        // pre-fetch endpoint to give the UI a fully-offline catalogue:
        // the UI can prefer /api/sky/image/file/{slug} over the remote
        // URL and never hit NASA / Wikipedia at view time.
        group.MapGet("/image/file/{slug}", (string slug, CelestialImageService svc) => {
            var path = svc.GetLocalFilePath(slug);
            if (path == null) return Results.NotFound();
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var mime = ext switch {
                ".png"  => "image/png",
                ".gif"  => "image/gif",
                ".webp" => "image/webp",
                _       => "image/jpeg"
            };
            return Results.File(path, mime);
        });

        // Walk the local DSO catalogue + Moon + planets + curated comets
        // and warm the on-disk image cache for each. After this runs,
        // the Tonight tab works fully offline. Sequential lookups,
        // takes a couple of minutes on first run, then is instant.
        group.MapPost("/image/prefetch", async (
            CelestialImageService imgs,
            SkyCatalogService catalog,
            CometEphemerisService comets,
            CancellationToken ct) => {
            var names = new List<string>();
            // DSOs: prefer the common name (better hit rate on NASA),
            // also include the catalogue code so both cache files exist
            // and the frontend's two-shot lookup always finds something.
            foreach (var dso in catalog.AllObjects) {
                if (!string.IsNullOrEmpty(dso.CommonName)) names.Add(dso.CommonName);
                names.Add(dso.Name);
            }
            names.AddRange(new[] {
                "Moon",
                "Mercury", "Venus", "Mars", "Jupiter", "Saturn", "Uranus", "Neptune"
            });
            foreach (var c in comets.AllComets) names.Add(c.Name);
            var summary = await imgs.PrefetchAsync(names, ct);
            return Results.Ok(summary);
        });

        // Ranked list of objects best positioned for observation tonight
        // from the active profile's location. Includes DSOs (from the
        // local catalog) + Moon + visible planets.
        group.MapGet("/tonights-best", (int? limit, TonightsBestService svc) => {
            var n = Math.Clamp(limit ?? 30, 1, 100);
            var result = svc.Compute(n);
            return Results.Ok(result);
        });
    }

    public record SlewAndCenterRequest(double Ra, double Dec, double ToleranceArcsec = 30.0);
}
