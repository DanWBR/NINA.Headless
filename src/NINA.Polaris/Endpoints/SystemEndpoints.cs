using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

public static class SystemEndpoints {
    public static void MapSystemEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/system");

        group.MapGet("/geocode", async (string query, int? limit, GeocodingService geo) => {
            if (string.IsNullOrWhiteSpace(query))
                return Results.BadRequest(new { error = "query parameter required" });
            try {
                var results = await geo.SearchAsync(query, limit ?? 5);
                return Results.Ok(new {
                    query,
                    count = results.Count,
                    results
                });
            } catch (TimeoutException ex) {
                return Results.Problem(ex.Message, statusCode: 504);
            } catch (InvalidOperationException ex) {
                return Results.Problem(ex.Message, statusCode: 502);
            }
        });

        group.MapGet("/relay", (RelayClient relay) => Results.Ok(new {
            state = relay.State.ToString().ToLowerInvariant(),
            hostname = relay.AssignedHostname,
            lastError = relay.LastError
        }));

        group.MapGet("/status", (EquipmentManager equip) => {
            var process = Process.GetCurrentProcess();
            // PA-7: surface the auto-incrementing 0.1.{days}.{seconds/2}
            // version that NINA.Polaris.csproj computes at build time.
            // GetExecutingAssembly() returns this DLL, the AssemblyVersion
            // and InformationalVersion attributes are both set to the
            // same VersionPrefix in csproj. UI banner reads `version`.
            var asm = Assembly.GetExecutingAssembly();
            var asmVer = asm.GetName().Version?.ToString() ?? "0.0.0.0";
            var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? asmVer;
            // PA-7b: belt-and-suspenders, the csproj sets
            // IncludeSourceRevisionInInformationalVersion=false, but
            // some SDK versions / source-link configurations still
            // append "+{git-sha}". Strip anything past '+' so the UI
            // badge stays compact (the build hash is recoverable from
            // git log when needed).
            var plus = infoVer.IndexOf('+');
            if (plus > 0) infoVer = infoVer.Substring(0, plus);
            return Results.Ok(new {
                version = infoVer,
                versionParts = asmVer,
                platform = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                memoryMb = process.WorkingSet64 / (1024 * 1024),
                uptime = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).ToString(@"d\.hh\:mm\:ss"),
                dotnetVersion = RuntimeInformation.FrameworkDescription,
                equipment = equip.GetEquipmentStatus()
            });
        });

        // GX-10: surface the HTTPS listener + cert metadata so the
        // Settings UI can render a "use this URL for WebGPU" banner +
        // a fingerprint the user can verify against Chrome's
        // cert-details dialog. Doesn't include anything sensitive
        // (the cert is self-signed; the PFX stays on the server).
        group.MapGet("/https-info",
            (IConfiguration cfg,
             SelfSignedCertService certSvc,
             HttpRequest req) => {
            var httpsEnabled = cfg.GetValue("Server:Https:Enabled", true);
            var httpPort  = cfg.GetValue("Server:Http:Port",  5000);
            var httpsPort = cfg.GetValue("Server:Https:Port", 5001);
            // Suggest concrete URLs the client can click on by mixing
            // the SAN-list names with the configured ports. We surface
            // the host the request came in on first (most relevant),
            // then a couple of LAN-friendly aliases.
            var sans = certSvc.SanEntries();
            string Decorate(string host, bool secure) {
                // IPv6 addresses need brackets in URL form. ":" present
                // and not at end → IPv6 literal.
                if (host.Contains(":") && !host.EndsWith(":")) host = "[" + host + "]";
                var port = secure ? httpsPort : httpPort;
                var defaultPort = secure ? 443 : 80;
                return (secure ? "https://" : "http://") + host
                    + (port == defaultPort ? "" : ":" + port);
            }
            return Results.Ok(new {
                httpsEnabled,
                httpPort,
                httpsPort,
                fingerprint = httpsEnabled ? certSvc.Fingerprint : null,
                // GX-12q2: SHA-256 is what modern browsers actually show
                // in their cert-details dialog. SHA-1 kept for legacy
                // tooling that might still query it.
                fingerprintSha256 = httpsEnabled ? certSvc.Fingerprint256 : null,
                // Names baked into the cert. Client picks the one
                // that matches what they typed into the address bar.
                hostnames = sans,
                requestHost = req.Host.Host,
                // Convenience: ready-to-click URLs for the top few hosts.
                exampleHttpUrls  = sans.Take(6).Select(s => Decorate(s, false)).ToArray(),
                exampleHttpsUrls = httpsEnabled
                    ? sans.Take(6).Select(s => Decorate(s, true)).ToArray()
                    : Array.Empty<string>()
            });
        });

        // GX-12q: Download the server's public certificate (PEM-encoded
        // DER, the format that Windows / macOS / iOS / Linux all accept
        // in their "import a root CA" dialogs). Stream it as
        // application/x-x509-ca-cert with a Content-Disposition so the
        // browser pops the save-or-install dialog instead of rendering
        // text. Public bytes only, the PFX with the private key
        // stays on the server and is never exposed.
        group.MapGet("/server-cert", (SelfSignedCertService certSvc) => {
            var cert = certSvc.GetOrCreate();
            var derBytes = cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert);
            // PEM wrapper makes desktop "double-click to install" work
            // reliably on every OS; raw DER would also work but Windows
            // sometimes opens it in Notepad instead of certmgr.
            var b64 = Convert.ToBase64String(derBytes,
                Base64FormattingOptions.InsertLineBreaks);
            var pem = "-----BEGIN CERTIFICATE-----\n"
                + b64 + "\n-----END CERTIFICATE-----\n";
            var bytes = System.Text.Encoding.ASCII.GetBytes(pem);
            return Results.File(bytes, "application/x-x509-ca-cert",
                fileDownloadName: "polaris-root.crt");
        });

        // Profiles
        group.MapGet("/profiles", (ProfileService profiles) => {
            var list = profiles.ListProfiles();
            return Results.Ok(new {
                active = profiles.Active.Name,
                profiles = list
            });
        });

        group.MapGet("/profile", (ProfileService profiles) => {
            return Results.Ok(profiles.Active);
        });

        group.MapPut("/profile", (UserProfile update, ProfileService profiles) => {
            profiles.UpdateSettings(p => {
                p.Latitude = update.Latitude;
                p.Longitude = update.Longitude;
                p.Altitude = update.Altitude;
                p.SensorWidthMm = update.SensorWidthMm;
                p.SensorHeightMm = update.SensorHeightMm;
                p.FocalLengthMm = update.FocalLengthMm;
                p.SensorPixelsX = update.SensorPixelsX;
                p.SensorPixelsY = update.SensorPixelsY;
                p.DefaultExposure = update.DefaultExposure;
                p.DefaultGain = update.DefaultGain;
                p.DefaultBinning = update.DefaultBinning;
                p.IndiHost = update.IndiHost;
                p.IndiPort = update.IndiPort;
                p.AutoConnectOnStartup = update.AutoConnectOnStartup;
                p.AstapPath = update.AstapPath;
                p.SolveToleranceArcsec = update.SolveToleranceArcsec;
                p.ImageOutputDir = update.ImageOutputDir;
                p.ImageNamePattern = update.ImageNamePattern;
                p.ImageFormat = update.ImageFormat;
                p.PreferAdvancedSequencer = update.PreferAdvancedSequencer;
                // External-tool path overrides. Empty/null = auto-detect.
                p.SirilPath = update.SirilPath;
                p.SirilScriptsDir = update.SirilScriptsDir;
                p.GraXpertPath = update.GraXpertPath;
                p.GraXpertBgeSmoothing = update.GraXpertBgeSmoothing;
                p.GraXpertBgeCorrection = update.GraXpertBgeCorrection
                                              ?? p.GraXpertBgeCorrection;
                p.GraXpertDeconStrength = update.GraXpertDeconStrength;
                p.GraXpertDeconPsfSize = update.GraXpertDeconPsfSize;
                p.GraXpertDenoiseStrength = update.GraXpertDenoiseStrength;
                // GX-1b: ONNX in-browser inference settings.
                p.OnnxModelsPath = update.OnnxModelsPath ?? p.OnnxModelsPath;
                p.OnnxLicenseAcknowledged = update.OnnxLicenseAcknowledged;
                p.OnnxDefaultDenoiseVersion = update.OnnxDefaultDenoiseVersion
                                                  ?? p.OnnxDefaultDenoiseVersion;
                p.OnnxPreferCli = update.OnnxPreferCli;
            });
            return Results.Ok(new { message = "Profile saved" });
        });

        group.MapPost("/profile/save-as", (SaveAsRequest request, ProfileService profiles) => {
            profiles.SaveAs(request.Name);
            return Results.Ok(new { message = $"Profile saved as '{request.Name}'" });
        });

        group.MapPost("/profile/load/{id}", (string id, ProfileService profiles) => {
            if (profiles.LoadProfile(id))
                return Results.Ok(new { message = "Profile loaded", name = profiles.Active.Name });
            return Results.NotFound(new { error = "Profile not found" });
        });

        // CLOCK-1: client-driven wall-clock sync. The /clock GET is
        // cheap status (used by Settings + the activity-bar chip);
        // POST /clock/sync writes the client's UTC into the system
        // clock via timedatectl (Linux only, polkit-allowed for the
        // polaris user). Both are gated by AuthMiddleware like every
        // other /api/* route.
        group.MapGet("/clock", (ClockSyncService clock) => {
            return Results.Ok(new {
                serverUtcNow = clock.ServerUtcNow().ToString("o"),
                supported = clock.IsSupported
            });
        });

        group.MapPost("/clock/sync", async (ClockSyncService clock,
                ClockSyncRequest req, CancellationToken ct) => {
            if (req == null || string.IsNullOrWhiteSpace(req.ClientUtc)) {
                return Results.BadRequest(new { error = "clientUtc is required (ISO-8601)" });
            }
            if (!DateTime.TryParse(req.ClientUtc, null,
                    System.Globalization.DateTimeStyles.RoundtripKind
                    | System.Globalization.DateTimeStyles.AssumeUniversal
                    | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsed)) {
                return Results.BadRequest(new { error = "clientUtc must be ISO-8601" });
            }
            // Short-circuit on platforms that physically can't do it
            // (Windows / macOS). 501 Not Implemented is the right code:
            // the route exists but the host can't honour it. Previously
            // returned 500 which surfaced as a generic crash in the
            // browser console.
            if (!clock.IsSupported) {
                return Results.Json(new {
                    ok = false,
                    error = "Clock sync is Linux-only on this host. "
                          + "Use the OS clock settings or enable NTP."
                }, statusCode: 501);
            }
            var result = await clock.SetUtcAsync(parsed, ct);
            if (!result.Ok) {
                return Results.Json(new {
                    ok = false,
                    error = result.Error,
                    serverUtcNow = result.ServerUtcNow.ToString("o")
                }, statusCode: 500);
            }
            return Results.Ok(new {
                ok = true,
                serverUtcNow = result.ServerUtcNow.ToString("o"),
                residualSkewSeconds = result.ResidualSkewSeconds
            });
        });

        // Legacy settings (redirect to profile)
        group.MapGet("/settings", (ProfileService profiles) => {
            var p = profiles.Active;
            return Results.Ok(new {
                observatoryLatitude = p.Latitude,
                observatoryLongitude = p.Longitude,
                observatoryAltitude = p.Altitude,
                sensorWidthMm = p.SensorWidthMm,
                sensorHeightMm = p.SensorHeightMm,
                focalLengthMm = p.FocalLengthMm,
                imageFormat = p.ImageFormat,
                plateSolver = "ASTAP",
                indiHost = p.IndiHost,
                indiPort = p.IndiPort
            });
        });
    }

    record SaveAsRequest(string Name);
    record ClockSyncRequest(string ClientUtc);
}
