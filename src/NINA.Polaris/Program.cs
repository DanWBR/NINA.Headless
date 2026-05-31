using NINA.Polaris.Endpoints;
using NINA.Polaris.Middleware;
using NINA.Polaris.Services;
using NINA.Polaris.WebSocket;
using NINA.INDI.Client;
using Yarp.ReverseProxy.Forwarder;

// Sub-process entry: when the parent server spawns us with
// `--ascom-setup <ProgID>` we run the ASCOM SetupDialog and exit,
// skipping all of the HTTP/Kestrel boot. See AscomSetupRunner +
// AscomEndpoints for the rationale (driver AVE isolation — keeps a
// crashing ZWO ASCOM driver from killing the main API server).
if (args.Length >= 2
    && args[0] == "--ascom-setup"
    && OperatingSystem.IsWindows()) {
    return NINA.Polaris.AscomSetupRunner.Run(args[1]);
}

var builder = WebApplication.CreateBuilder(args);

// GX-10: HTTPS self-signed cert. Constructed eagerly here (not via DI)
// because Kestrel's ConfigureKestrel callback needs the cert *before*
// builder.Build() runs, and we want the SAME instance shared with the
// rest of the app so the Settings UI fingerprint matches what Kestrel
// is actually serving. Register the constructed singleton so endpoints
// + the status feed can pick it up by injection.
//
// GX-10b: defaults changed so port 5000 is the HTTPS-on-LAN port (what
// users actually want to type) and HTTP gets demoted to a loopback-only
// service port for the Relay tunnel + curl-from-the-host scripts. Net
// effect: any LAN device can ONLY reach Polaris via HTTPS, so WebGPU
// + multi-thread WASM "just work" on the URL the user naturally tries.
// Backwards-compat: legacy `Server:Http:Port` config still honoured;
// users can override either side or disable HTTPS entirely.
var httpsEnabled = builder.Configuration.GetValue("Server:Https:Enabled", true);
var httpEnabled  = builder.Configuration.GetValue("Server:Http:Enabled",  true);
var httpPort     = builder.Configuration.GetValue("Server:Http:Port",  5080);
var httpsPort    = builder.Configuration.GetValue("Server:Https:Port", 5000);
// Loopback-only HTTP keeps plaintext OFF the LAN. Power-users who
// need HTTP exposed to the LAN (legacy integrations, no-TLS-stack
// clients) flip Server:Http:Bind = "any".
var httpBindAny  = builder.Configuration.GetValue("Server:Http:Bind", "loopback")
                       .Equals("any", StringComparison.OrdinalIgnoreCase);
var certService = new NINA.Polaris.Services.SelfSignedCertService(
    builder.Configuration,
    Microsoft.Extensions.Logging.Abstractions.NullLogger<NINA.Polaris.Services.SelfSignedCertService>.Instance);
builder.Services.AddSingleton(certService);

builder.WebHost.ConfigureKestrel(options =>
{
    if (httpEnabled) {
        if (httpBindAny) options.ListenAnyIP(httpPort);
        else             options.ListenLocalhost(httpPort);
    }
    if (httpsEnabled) {
        var cert = certService.GetOrCreate();
        options.ListenAnyIP(httpsPort, listen => listen.UseHttps(cert));
    }
    // GX-9: the /api/onnx/save endpoint round-trips raw uint16 pixel
    // bytes for the post-inference image, RGB masters from a modern
    // OSC sensor land around 150 MB (e.g. 6240×4160×3×2). The default
    // 30 MB cap rejects anything bigger than ~2 MP RGB. Also affects
    // /api/editor/upload (user-supplied PNG/TIFF), /api/files/upload
    // (drag-drop into STUDIO library), and /api/onnx/save (the one
    // we actually hit first). 1 GB hard ceiling, generous enough to
    // cover a 16k×16k RGB master uncompressed without being unbounded.
    options.Limits.MaxRequestBodySize = 1L * 1024 * 1024 * 1024;
});

// GX-9: ASP.NET's multipart form parser has its own ceiling
// (FormOptions.MultipartBodyLengthLimit, default 128 MB) layered on
// top of Kestrel's request body limit. Both have to grow together,
// the parser hits its cap first and surfaces a less obvious error
// ("Multipart body length limit exceeded") before Kestrel sees the
// stream. Match the 1 GB Kestrel ceiling.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 1L * 1024 * 1024 * 1024;
    o.ValueLengthLimit = int.MaxValue;
});

// Services
// DBGLOG-1/2: ring buffer + ILogger provider that mirrors every
// server-side log call into it. Registered FIRST so other singletons
// that resolve ILogger<T> in their constructor get the bridged logger.
builder.Services.AddSingleton<NINA.Polaris.Services.Logging.LogService>();
builder.Logging.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(sp =>
    new NINA.Polaris.Services.Logging.LogBufferLoggerProvider(
        sp.GetRequiredService<NINA.Polaris.Services.Logging.LogService>()));
builder.Services.AddSingleton<ImageRelayService>();
builder.Services.AddSingleton<CameraStreamService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Planetary.VideoRecordingService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Planetary.PlanetaryStackerService>();
// LSTR-3: subscribes to LiveStackingService.FrameIntegrated at construction.
// Eagerly resolved alongside PHD2ProfileSyncService below so the
// subscription wires at startup, not on first /api/livestack/triggers/* hit.
builder.Services.AddSingleton<LiveStackTriggersService>();
// REFSUG-1: trend-based refocus suggestion. Listens to the same
// FrameIntegrated event as LSTR-3 but only when RefocusEnabled is
// OFF — covers manual-focuser users who cannot be auto-fired.
// Eager-resolved below alongside LSTR so the subscription is wired
// before the first /api/livestack/start hit.
builder.Services.AddSingleton<RefocusSuggestionService>();
// Auto-shows live camera feed while mount is slewing (no-op when any
// capture surface is active). Singleton + hosted service so the
// background poll loop runs.
builder.Services.AddSingleton<SlewPreviewService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SlewPreviewService>());
builder.Services.AddSingleton<LiveStackingService>();
builder.Services.AddSingleton<EquipmentManager>();
builder.Services.AddSingleton<SequenceEngine>();
builder.Services.AddSingleton<NINA.Polaris.Services.Sequencer.SequenceTemplateStore>();
builder.Services.AddSingleton<NINA.Polaris.Services.Sequencer.AdvancedSequenceEngine>();
builder.Services.AddSingleton<MosaicPlannerService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Plugins.PluginLoaderService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NINA.Polaris.Services.Plugins.PluginLoaderService>());
builder.Services.AddSingleton<SkyCatalogService>();
builder.Services.AddSingleton<NINA.Polaris.Services.PlateSolving.AstapSolver>();
builder.Services.AddSingleton<NINA.Polaris.Services.PlateSolving.PlateSolve3Solver>();
builder.Services.AddSingleton<NINA.Polaris.Services.PlateSolving.AstrometryNetOnlineSolver>();
builder.Services.AddSingleton<NINA.Polaris.Services.PlateSolving.AstrometryNetLocalSolver>();
builder.Services.AddSingleton<PlateSolveService>();
builder.Services.AddSingleton<SlewCenterService>();
builder.Services.AddSingleton<ProfileService>();
// AUTH-1: local-server auth (password + session store + rate limit).
// Middleware that consumes this is wired in AUTH-2.
builder.Services.AddSingleton<NINA.Polaris.Services.Auth.AuthService>();
// CLOCK-1: wraps `timedatectl set-time` so the browser can nudge the
// Pi's wall clock when the host is offline (no NTP) + has no RTC.
// Linux only; on Windows the service refuses gracefully + the UI
// banner explains.
builder.Services.AddSingleton<ClockSyncService>();
builder.Services.AddSingleton<ImageWriterService>();
builder.Services.AddSingleton<PHD2Client>();
builder.Services.AddSingleton<PHD2ProcessManager>();
builder.Services.AddHostedService<PHD2AutoStartService>();

// SIM-2: built-in equipment simulator (indi_simulator_* on Linux/Mac,
// Alpaca Omni Simulator on Windows). Both backends register; the
// orchestrator picks the supported one at startup via IsSupported.
// AutoStart service handles the launch-on-boot toggle + periodic
// health probe.
// Both backends register; SimulatorService picks the first one that
// reports IsSupported = true on the current OS. Order matters only
// when two backends claim the same OS (none do today). Backends not
// matching the host OS still construct but their IsSupported short-
// circuits Launch / Detect into safe no-ops.
builder.Services.AddSingleton<NINA.Polaris.Services.Simulator.ISimulatorBackend,
    NINA.Polaris.Services.Simulator.IndiSimulatorBackend>();
builder.Services.AddSingleton<NINA.Polaris.Services.Simulator.ISimulatorBackend,
    NINA.Polaris.Services.Simulator.AscomSimulatorBackend>();
builder.Services.AddSingleton<NINA.Polaris.Services.Simulator.SimulatorService>();
builder.Services.AddHostedService<NINA.Polaris.Services.Simulator.SimulatorAutoStartService>();
// Listens to ProfileService.EquipmentProfileActivated; keep singleton so
// the event subscription survives request scopes.
builder.Services.AddSingleton<PHD2ProfileSyncService>();
builder.Services.AddSingleton<PHD2CalibrationOrchestrator>();
// xpra-hosted PHD2 GUI session (Linux only, service short-circuits on
// other OSes). Register as singleton AND hosted service so it shows up
// in DI for endpoint handlers + runs its background loop.
builder.Services.AddSingleton<Phd2GuiSessionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Phd2GuiSessionService>());
// PH2VNC-1: Windows sibling of Phd2GuiSessionService. Detects
// TightVNC, monitors its Windows service + listening port, and
// powers the GUIDE tab's "PHD2 GUI" iframe on Windows hosts via
// the noVNC HTML5 client + the /phd2-vnc-ws TCP bridge. Idle no-op
// on non-Windows so the Linux build incurs zero overhead.
builder.Services.AddSingleton<Phd2VncSessionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Phd2VncSessionService>());
// INDI-WEB-1: indi-web (indiwebmanager) lifecycle manager. Same
// dual-registration shape as Phd2GuiSession so endpoint handlers
// resolve the singleton AND the background loop (auto-start +
// health probe) runs.
builder.Services.AddSingleton<IndiWebManagerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IndiWebManagerService>());
// WIFI-1: NetworkManager-based WiFi mode switch (Hotspot ↔ Station).
// Same dual-registration shape as Phd2Gui / IndiWeb. Linux-only;
// gracefully short-circuits on Windows / macOS via IsSupportedOs.
builder.Services.AddSingleton<NetworkManagerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NetworkManagerService>());
// YARP direct forwarder, used by the /phd2-gui/* AND /indi-web/*
// reverse-proxies below to bridge browser ↔ embedded webapp.
// Includes WebSocket upgrade support, which xpra-html5 needs for
// the pixel stream and indi-web can use for live driver state.
builder.Services.AddHttpForwarder();
builder.Services.AddSingleton<AutoFocusService>();
builder.Services.AddSingleton<MeridianFlipService>();
builder.Services.AddSingleton<FlatWizardService>();
// PA-1: TPPA orchestrator. Singleton because it holds CurrentJob
// (consumed by StatusStreamHandler) + the in-flight CancellationTokenSource.
builder.Services.AddSingleton<PolarAlignmentService>();
// PA-6: TPPA target suggester. Pure read against the catalog + altitude
// helpers, no state, fine as a singleton.
builder.Services.AddSingleton<PolarTppaTargetService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Alpaca.AlpacaDiscovery>();
builder.Services.AddSingleton<NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache>();
builder.Services.AddSingleton<StellariumClient>();
builder.Services.AddSingleton<AltitudeService>();
builder.Services.AddSingleton<GeocodingService>();
builder.Services.AddSingleton<WeatherForecastService>();
builder.Services.AddSingleton<CelestialImageService>();
builder.Services.AddSingleton<CometEphemerisService>();
builder.Services.AddSingleton<TonightsBestService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Studio.FrameLibraryService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Studio.FrameProcessingService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Studio.MasterFrameService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Studio.CalibrationService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Studio.BatchStackingService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Studio.ChannelCombineService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Studio.ColorCalibrationService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Sky.ApassCatalog>();
// CAT-2: bundled expanded DSO catalog (NGC/IC/M/C/Arp/Sh2/HCG/AGC,
// ~14.5k objects in wwwroot/catalogs/dso/dso.db). SkyCatalogService
// delegates to it when IsAvailable, falls back to the hardcoded
// 150-object legacy list when missing.
builder.Services.AddSingleton<NINA.Polaris.Services.Sky.DsoCatalog>();
builder.Services.AddSingleton<NINA.Polaris.Services.Studio.FrameOperationsService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Editor.ImageEditService>();
builder.Services.AddSingleton<NINA.Polaris.Services.Editor.EditSidecarStore>();
builder.Services.AddSingleton<NINA.Polaris.Services.Onnx.OnnxModelRegistry>();
builder.Services.AddSingleton<NINA.Polaris.Services.Onnx.OnnxFileService>();
builder.Services.AddSingleton<FileBrowserService>();
builder.Services.AddSingleton<NINA.Polaris.Services.External.SirilService>();
builder.Services.AddSingleton<NINA.Polaris.Services.External.GraXpertService>();
builder.Services.AddSingleton<NINA.Polaris.Services.CropService>();
// Host CPU + memory sampler. AddResourceMonitoring wires the
// platform-specific provider (Job Objects on Windows, cgroups on
// Linux). HostMetricsService loops in the background, exposes the
// latest snapshot via the Latest property which StatusStreamHandler
// folds into the per-second WS broadcast.
builder.Services.AddResourceMonitoring();
builder.Services.AddSingleton<HostMetricsService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HostMetricsService>());
builder.Services.AddHostedService<MdnsService>();
// Server-pushed toast channel + boot-time auto-connect for INDI /
// Alpaca / active-rig equipment. The auto-connect service is gated
// on profile.AutoConnectOnStartup; if the toggle is off, RunAsync
// is never scheduled and there's zero runtime cost.
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddHostedService<HardwareAutoConnectService>();
builder.Services.AddSingleton<RelayClient>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RelayClient>());
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var host = config.GetValue("Indi:Host", "localhost")!;
    var port = config.GetValue("Indi:Port", 7624);
    return new IndiClient(host, port);
});

var app = builder.Build();

// Eagerly resolve PHD2ProfileSyncService so its constructor wires the
// ProfileService.EquipmentProfileActivated event subscription. Without
// this, the singleton would only be constructed on first /api/guider/*
// request and rig activations before that would skip auto-sync.
app.Services.GetRequiredService<PHD2ProfileSyncService>();
// Same eager-resolve rationale: LiveStackTriggersService subscribes to
// LiveStackingService frame events in its constructor. Without this
// the singleton would only be constructed when /api/livestack/triggers
// is hit, and any frames stacked before then would skip auto-refocus
// / auto-recenter evaluation.
app.Services.GetRequiredService<LiveStackTriggersService>();
// REFSUG-1: same eager-resolve rationale. RefocusSuggestionService
// hooks LiveStackingService.FrameIntegrated in its constructor and
// must be alive before the first live-stack frame arrives.
app.Services.GetRequiredService<RefocusSuggestionService>();

// CLST-5 + CLST-7: pick the live-stack compute target based on
//   (a) the active rig's LiveStackComputeMode override ("auto" /
//       "server" / "client") and
//   (b) how many connected image-stream clients have the WASM module
//       loaded.
// Re-evaluated on three triggers: relay's WasmCapableCountChanged
// (client connect/disconnect/capability change), profile activation
// (user switches rigs), and the PUT /api/equipment/rigs/{id} that
// edits the override (handled implicitly, the next event reads the
// fresh value off ProfileService.ActiveEquipmentProfile).
{
    var liveStack = app.Services.GetRequiredService<LiveStackingService>();
    var relay = app.Services.GetRequiredService<ImageRelayService>();
    var profiles = app.Services.GetRequiredService<ProfileService>();
    var liveStackLogger = app.Services.GetRequiredService<ILogger<LiveStackingService>>();

    void EvaluateMode(string trigger) {
        var rigOverride = (profiles.ActiveEquipmentProfile?.LiveStackComputeMode ?? "auto")
                          .Trim().ToLowerInvariant();
        var newMode = rigOverride switch {
            "server" => StackMode.Full,
            "client" => StackMode.MetricsOnly,
            _        => relay.WasmCapableClientCount > 0 ? StackMode.MetricsOnly : StackMode.Full
        };
        if (liveStack.Mode != newMode) {
            liveStack.Mode = newMode;
            liveStackLogger.LogInformation(
                "Live stacker mode -> {Mode} (trigger={Trigger}, rigOverride={Override}, wasmClients={Count})",
                newMode, trigger, rigOverride, relay.WasmCapableClientCount);
        }
    }
    relay.WasmCapableCountChanged += _ => EvaluateMode("client-handshake");
    profiles.EquipmentProfileActivated += _ => EvaluateMode("rig-switch");

    // Per-rig save-frames-to-disk toggle. The runtime flag on
    // LiveStackingService is the source of truth at frame-receive
    // time, the profile field is the persistence layer. Sync the
    // runtime flag now (so the boot rig wins) and on every rig
    // switch (so the new rig's policy applies immediately without
    // a Polaris restart).
    void ApplySaveFramesPolicy(string trigger) {
        // Default to ON when the field is missing from a legacy
        // profile (pre-default-true commit). Matches the new
        // service default so behaviour stays consistent regardless
        // of whether the profile was written before or after the
        // change.
        var enabled = profiles.ActiveEquipmentProfile?.LiveStackSaveFramesToDisk ?? true;
        if (liveStack.SaveFramesToDisk != enabled) {
            liveStack.SaveFramesToDisk = enabled;
            liveStackLogger.LogInformation(
                "Live stack SaveFramesToDisk -> {Enabled} (trigger={Trigger})",
                enabled, trigger);
        }
    }
    ApplySaveFramesPolicy("startup");
    profiles.EquipmentProfileActivated += _ => ApplySaveFramesPolicy("rig-switch");

    // Per-rig live-stack duration cap. 0 (default) = unlimited.
    // Same persistence pattern as the save-frames toggle.
    void ApplyDurationCap(string trigger) {
        var secs = profiles.ActiveEquipmentProfile?.LiveStackMaxDurationSeconds ?? 0;
        if (liveStack.MaxDurationSeconds != secs) {
            liveStack.MaxDurationSeconds = secs;
            liveStackLogger.LogInformation(
                "Live stack MaxDurationSeconds -> {Seconds}s (trigger={Trigger})",
                secs, trigger);
        }
    }
    ApplyDurationCap("startup");
    profiles.EquipmentProfileActivated += _ => ApplyDurationCap("rig-switch");
}

// SWE-3-bugfix: strip CSP for /sky/* responses. The ASP.NET dev-time
// browser refresh middleware injects a strict Content-Security-Policy
// header (no 'unsafe-eval', no 'wasm-unsafe-eval') into HTML responses.
// stellarium-web-engine's Emscripten runtime calls addFunction() during
// init, which internally uses `new Function(...)` to build callback
// trampolines, CSP blocks that and the engine never reaches onReady,
// so addDataSource never fires and the sky stays empty with no Network
// requests to skydata at all (matches the symptom we hit).
//
// Easiest correct fix: remove the CSP header entirely for the /sky/
// sub-app via Response.OnStarting (which runs AFTER all upstream
// middlewares have set their headers and BEFORE the body streams).
// The iframe is sandboxed by the parent's sandbox attribute already,
// so dropping CSP on /sky/ doesn't widen the attack surface, the
// surface is bounded by the iframe sandbox.
app.Use(async (ctx, next) => {
    if (ctx.Request.Path.StartsWithSegments("/sky")) {
        ctx.Response.OnStarting(() => {
            ctx.Response.Headers.Remove("Content-Security-Policy");
            ctx.Response.Headers.Remove("Content-Security-Policy-Report-Only");
            return Task.CompletedTask;
        });
    }
    await next();
});

app.UseDefaultFiles();
// CLST-2: the WASM AppBundle includes extensions ASP.NET Core's
// default FileExtensionContentTypeProvider doesn't know about
// (.dat for ICU data, .blat / .dll for Brotli-compressed managed
// assemblies). Without these mappings the static-file middleware
// returns 404, the browser's SRI check sees an empty body, and
// the dotnet runtime fails to boot with cascading "integrity
// checks failed" errors. ServeUnknownFileTypes scoped via a
// custom content-type map is cleaner than blanket allowing
// everything, keeps obscure extensions outside /js/wasm/ still 404.
var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypes.Mappings[".dat"] = "application/octet-stream";
contentTypes.Mappings[".blat"] = "application/octet-stream";
contentTypes.Mappings[".dll"] = "application/octet-stream";
contentTypes.Mappings[".pdb"] = "application/octet-stream";
contentTypes.Mappings[".webcil"] = "application/octet-stream";
contentTypes.Mappings[".wasm"] = "application/wasm";
contentTypes.Mappings[".br"] = "application/octet-stream";
contentTypes.Mappings[".gz"] = "application/octet-stream";
// SWE-3-bugfix: stellarium-web-engine HiPS tile pyramids ship
// as .eph (binary ephemeris) and the per-survey `properties`
// metadata files have NO extension at all. The default static
// middleware refuses both, silently 404s and the engine then
// renders an empty sky with no console error. Map .eph here and
// add a scoped ServeUnknownFileTypes pass below for the no-ext
// `properties` files inside /sky/data/skydata/.
contentTypes.Mappings[".eph"] = "application/octet-stream";
app.UseStaticFiles(new StaticFileOptions {
    ContentTypeProvider = contentTypes
});

// SWE-3-bugfix continued: second pass scoped to the Stellarium
// skydata directory only, with ServeUnknownFileTypes=true so the
// extensionless `properties` files (one per survey/landscape/
// skyculture) get a Content-Type and don't 404. Scoped to the
// skydata path so this never accidentally serves obscure
// extensionless files from elsewhere in wwwroot.
app.UseStaticFiles(new StaticFileOptions {
    RequestPath = "/sky/data",
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
        Path.Combine(builder.Environment.WebRootPath, "sky", "data")),
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream"
});

// AUTH-2: gate /api/*, /ws/*, /phd2-gui/*, /indi-web/*, /sky/*
// behind the bearer token issued by AuthService. /api/auth/* and
// /api/system/version are exempt. Loopback (127.0.0.1/::1) and the
// AuthEnabled=false toggle bypass too. The login page itself + every
// static asset (CSS, JS, images, fonts) live outside the gated
// prefixes so they load without a token, the JS then drives the
// status -> wizard/login/app boot flow.
//
// Order: AFTER UseStaticFiles (so wwwroot assets terminate first)
// and BEFORE UseWebSockets / the /sky+/phd2-gui reverse proxies
// / endpoint mapping (so gated requests bounce here with 401
// instead of hitting handlers).
//
// NOTE: the /sky CSP-strip middleware above also runs before this
// for path matching, but it only adds a Response.OnStarting hook
// and calls next() unconditionally, so we still catch /sky/ here.
// DBGLOG-3: HTTP request logger BEFORE the auth gate so 401s still
// produce an entry. Static assets + /api/logs* are skipped inside the
// middleware (no value, and avoids feedback loops).
app.UseMiddleware<NINA.Polaris.Middleware.RequestLoggingMiddleware>();

app.UseAuthMiddleware();

app.UseWebSockets();

// ----- PH2X-7: /phd2-gui/* reverse-proxy → xpra HTML5 client -----
// Same-origin proxy so the iframe's sessionStorage works and Polaris's
// outer auth layer (Relay tokens / LAN) covers PHD2 GUI access. xpra
// itself binds to 127.0.0.1 only, never exposed to the network directly.
//
// MapForwarder handles both static HTML5 client assets (HTML/JS/CSS
// stripped from the iframe URL) AND the WebSocket upgrade that streams
// PHD2's pixel updates.
var phd2GuiForwarder = app.Services.GetRequiredService<IHttpForwarder>();
var phd2GuiHttpClient = new HttpMessageInvoker(new SocketsHttpHandler {
    UseProxy = false,
    AllowAutoRedirect = false,
    AutomaticDecompression = System.Net.DecompressionMethods.None,
    UseCookies = false,
    EnableMultipleHttp2Connections = true,
    ActivityHeadersPropagator = new Yarp.ReverseProxy.Forwarder.ReverseProxyPropagator(
        System.Diagnostics.DistributedContextPropagator.Current),
    ConnectTimeout = TimeSpan.FromSeconds(5),
});
var phd2GuiTransform = HttpTransformer.Default;
app.Map("/phd2-gui/{**rest}", async (HttpContext ctx, Phd2GuiSessionService gui) => {
    if (!gui.IsSupportedOs || !gui.XpraInstalled) {
        ctx.Response.StatusCode = 501;
        await ctx.Response.WriteAsJsonAsync(new {
            error = "Embedded PHD2 GUI requires Linux + xpra installed on the Polaris host."
        });
        return;
    }
    if (!gui.SessionRunning) {
        ctx.Response.StatusCode = 503;
        await ctx.Response.WriteAsJsonAsync(new {
            error = "xpra session not running. POST /api/guider/gui-session/start to launch it."
        });
        return;
    }
    // Strip the /phd2-gui prefix in-place so xpra's HTML5 client
    // sees its own root paths (the HTML serves asset URLs like
    // /js/Client.js that need to resolve to upstream root, not
    // /phd2-gui/js/Client.js). Same approach as the indi-web proxy
    // below. Without this the iframe loads the xpra HTML shell but
    // every JS/CSS/WebSocket request 404s and the screen stays black.
    var rest = ctx.Request.Path.Value ?? "/";
    if (rest.StartsWith("/phd2-gui", StringComparison.OrdinalIgnoreCase)) {
        rest = rest["/phd2-gui".Length..];
        if (string.IsNullOrEmpty(rest)) rest = "/";
    }
    ctx.Request.Path = rest;
    var target = $"http://127.0.0.1:{gui.BindPort}";
    var err = await phd2GuiForwarder.SendAsync(ctx, target, phd2GuiHttpClient,
        ForwarderRequestConfig.Empty, phd2GuiTransform);
    if (err != ForwarderError.None) {
        ctx.Response.StatusCode = 502;
        await ctx.Response.WriteAsync($"xpra proxy error: {err}");
    }
});

// ----- PH2VNC-2: /phd2-vnc-ws WebSocket → TightVNC TCP bridge -----
// noVNC speaks WebSocket; TightVNC speaks raw RFB over TCP. Standalone
// noVNC setups use the "websockify" Python proxy for this; we do it
// inline in C# (~60 lines) so there's no extra process to manage.
// AuthMiddleware gates this path so only authenticated Polaris users
// can reach the VNC server, even when TightVNC itself is bound to
// all interfaces (the docs walk the user through restricting that
// too, but the auth layer is the actual security boundary).
app.Map("/phd2-vnc-ws", async (HttpContext ctx, Phd2VncSessionService vnc,
                                ILoggerFactory loggers) => {
    var log = loggers.CreateLogger("Phd2VncBridge");
    if (!vnc.IsSupportedOs || !vnc.TightVncInstalled) {
        ctx.Response.StatusCode = 501;
        await ctx.Response.WriteAsJsonAsync(new {
            error = "Embedded PHD2 GUI via VNC requires Windows + TightVNC installed on the Polaris host."
        });
        return;
    }
    if (!vnc.ServiceRunning || !vnc.Listening) {
        ctx.Response.StatusCode = 503;
        await ctx.Response.WriteAsJsonAsync(new {
            error = "TightVNC service is not running or not listening on the loopback port. " +
                    "Open Settings → PHD2 Embedded GUI and start the service."
        });
        return;
    }
    if (!ctx.WebSockets.IsWebSocketRequest) {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("Expected WebSocket upgrade request");
        return;
    }

    // Negotiate subprotocol. Modern noVNC (1.0+) doesn't request any
    // subprotocol — empty WebSocketRequestedProtocols, SubProtocol
    // stays null, and the connection is binary by default which is
    // what RFB needs. Older noVNC + websockify-compat clients ask
    // for "binary" explicitly; honour that so the wire stays
    // compatible across versions.
    string? chosenProto = null;
    if (ctx.WebSockets.WebSocketRequestedProtocols.Contains("binary"))
        chosenProto = "binary";
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext {
        SubProtocol = chosenProto
    });
    log.LogInformation("PHD2 VNC bridge: WS accepted (subProto={Proto}), connecting to 127.0.0.1:{Port}",
        chosenProto ?? "(none)", vnc.Port);

    using var tcp = new System.Net.Sockets.TcpClient();
    try {
        await tcp.ConnectAsync(System.Net.IPAddress.Loopback, vnc.Port, ctx.RequestAborted);
        // Disable Nagle on the TCP side. RFB is latency-sensitive
        // (mouse moves + tiny pointer-events traffic) and Nagle
        // batches small writes for ~40ms, which on top of WS framing
        // overhead made the cursor lag visibly + occasionally caused
        // the RFB handshake to stall when initial bytes got held
        // in the kernel buffer waiting for batching.
        tcp.NoDelay = true;
    } catch (Exception ex) {
        log.LogWarning(ex, "PHD2 VNC bridge: TCP connect to TightVNC failed");
        // Service was running at probe time but we can't connect now,
        // race condition (user stopped TightVNC between probe and
        // WS upgrade). Close the WS with a code the client can read.
        await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.EndpointUnavailable,
            "TightVNC connection failed: " + ex.Message, ctx.RequestAborted);
        return;
    }
    var stream = tcp.GetStream();

    // Bidirectional pump. Each direction is its own task; first one
    // to complete wins and we tear the other down via the linked
    // CancellationTokenSource so neither leaks.
    using var pumpCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
    var ct = pumpCts.Token;
    long bytesWs2Tcp = 0, bytesTcp2Ws = 0;

    async Task PumpWsToTcp() {
        var buf = new byte[16 * 1024];
        try {
            while (!ct.IsCancellationRequested) {
                var r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) {
                    log.LogDebug("PHD2 VNC bridge: client sent Close frame, tearing down");
                    break;
                }
                if (r.Count == 0) continue;
                await stream.WriteAsync(buf.AsMemory(0, r.Count), ct);
                bytesWs2Tcp += r.Count;
            }
        } catch (OperationCanceledException) { /* normal teardown */ }
        catch (Exception ex) {
            // Was silent before — meant "TightVNC dropped us" looked
            // identical to "browser closed tab". Logged at Debug so
            // it doesn't spam Warning on every normal disconnect but
            // a tail -f when troubleshooting catches it.
            log.LogDebug(ex, "PHD2 VNC bridge: WS→TCP pump terminated by exception");
        }
    }
    async Task PumpTcpToWs() {
        var buf = new byte[16 * 1024];
        try {
            while (!ct.IsCancellationRequested) {
                var n = await stream.ReadAsync(buf, ct);
                if (n == 0) {
                    log.LogDebug("PHD2 VNC bridge: TightVNC closed TCP, tearing down");
                    break;
                }
                await ws.SendAsync(buf.AsMemory(0, n),
                    System.Net.WebSockets.WebSocketMessageType.Binary,
                    endOfMessage: true, ct);
                bytesTcp2Ws += n;
            }
        } catch (OperationCanceledException) { /* normal teardown */ }
        catch (Exception ex) {
            log.LogDebug(ex, "PHD2 VNC bridge: TCP→WS pump terminated by exception");
        }
    }

    var ws2tcp = PumpWsToTcp();
    var tcp2ws = PumpTcpToWs();
    var winner = await Task.WhenAny(ws2tcp, tcp2ws);
    pumpCts.Cancel();
    try { await Task.WhenAll(ws2tcp, tcp2ws); } catch { }
    log.LogInformation("PHD2 VNC bridge: session closed (ws→tcp={Tx}B, tcp→ws={Rx}B, first-done={Side})",
        bytesWs2Tcp, bytesTcp2Ws, winner == ws2tcp ? "ws" : "tcp");
});

// ----- INDI-WEB-2: /indi-web/* reverse-proxy → indi-web (Bottle webapp) -----
// Same shape as /phd2-gui/* above: same-origin proxy so the iframe
// gets indi-web's HTML / JS / XHR / WebSocket without CORS dance,
// and Polaris's outer auth layer (Relay tokens / LAN-only) covers
// driver management. indi-web binds to 127.0.0.1 only — never
// directly exposed to the network even when Polaris listens on
// 0.0.0.0.
var indiWebForwarder = app.Services.GetRequiredService<IHttpForwarder>();
var indiWebHttpClient = new HttpMessageInvoker(new SocketsHttpHandler {
    UseProxy = false,
    AllowAutoRedirect = false,
    AutomaticDecompression = System.Net.DecompressionMethods.None,
    UseCookies = false,
    EnableMultipleHttp2Connections = true,
    ActivityHeadersPropagator = new Yarp.ReverseProxy.Forwarder.ReverseProxyPropagator(
        System.Diagnostics.DistributedContextPropagator.Current),
    ConnectTimeout = TimeSpan.FromSeconds(5),
});
// Default transformer leaves headers / body untouched. We strip
// the /indi-web prefix from the request path manually below
// (HttpContext.Request.Path) before calling SendAsync — indi-web
// returns asset URLs like /static/app.css that need to resolve
// to the upstream root, not /indi-web/static/app.css.
var indiWebTransform = HttpTransformer.Default;
app.Map("/indi-web/{**rest}", async (HttpContext ctx, IndiWebManagerService svc) => {
    if (!svc.IsSupportedOs) {
        ctx.Response.StatusCode = 501;
        await ctx.Response.WriteAsJsonAsync(new {
            error = svc.UnsupportedReason ?? "Not supported on this OS",
        });
        return;
    }
    if (!svc.Installed) {
        ctx.Response.StatusCode = 501;
        await ctx.Response.WriteAsJsonAsync(new {
            error = "indi-web not installed. Run: pip install indiwebmanager",
        });
        return;
    }
    if (!svc.Running) {
        ctx.Response.StatusCode = 503;
        await ctx.Response.WriteAsJsonAsync(new {
            error = "indi-web not running. POST /api/indi/web/start to launch it.",
        });
        return;
    }
    // Strip the /indi-web prefix in-place so indi-web sees its
    // own root paths. PathBase grows / Path shrinks; the forwarder
    // uses Path verbatim for the upstream request.
    var rest = ctx.Request.Path.Value ?? "/";
    if (rest.StartsWith("/indi-web", StringComparison.OrdinalIgnoreCase)) {
        rest = rest["/indi-web".Length..];
        if (string.IsNullOrEmpty(rest)) rest = "/";
    }
    ctx.Request.Path = rest;
    var target = $"http://{svc.BindAddress}:{svc.BindPort}";
    var err = await indiWebForwarder.SendAsync(ctx, target, indiWebHttpClient,
        ForwarderRequestConfig.Empty, indiWebTransform);
    if (err != ForwarderError.None) {
        ctx.Response.StatusCode = 502;
        await ctx.Response.WriteAsync($"indi-web proxy error: {err}");
    }
});

// Equipment endpoints
app.MapEquipmentEndpoints();
app.MapCameraEndpoints();
app.MapVideoEndpoints();
app.MapTelescopeEndpoints();
app.MapFocuserEndpoints();
app.MapFilterWheelEndpoints();
// ASCOM Platform-specific (SetupDialog, platform-presence probe).
// Per-device select/connect/discover are already handled by the
// per-device endpoint groups above with ?driver=ascom-com.
app.MapAscomEndpoints();
app.MapRotatorEndpoints();
app.MapFlatDeviceEndpoints();
app.MapDomeEndpoints();
app.MapWeatherEndpoints();
app.MapGuiderEndpoints();
app.MapSimulatorEndpoints();
app.MapIndiWebEndpoints();
// WIFI-3: hotspot ↔ station mode switch (Linux + NetworkManager only)
app.MapNetworkEndpoints();
app.MapAutoFocusEndpoints();
// MFOC-3: Bahtinov mask analyser endpoint, lives under the same
// /api/focus group as future manual-assist sub-features (donut
// metric, gaussian FWHM fit, ...).
app.MapFocusEndpoints();
app.MapMeridianFlipEndpoints();
// AUTH-1: /api/auth/{status,setup,login,logout,change-password,
// disable,enable}. Mapped here; AuthMiddleware (AUTH-2) exempts the
// whole /api/auth/* prefix so these are reachable without a token.
app.MapAuthEndpoints();
// DBGLOG-4: /api/logs/* (gated by AuthMiddleware along with the
// rest of /api/*). The middleware skip-list for /api/logs* only
// blocks the http-request-logging entry, NOT the auth gate.
NINA.Polaris.Endpoints.LogsEndpoints.MapLogsEndpoints(app);
app.MapPolarAlignmentEndpoints();
app.MapFlatWizardEndpoints();
app.MapAlpacaEndpoints();
app.MapStellariumEndpoints();
app.MapSequenceEndpoints();
app.MapAdvancedSequenceEndpoints();
app.MapMosaicEndpoints();
app.MapPluginEndpoints();
app.MapSkyEndpoints();
app.MapSystemEndpoints();
app.MapImageEndpoints();
app.MapStudioEndpoints();
app.MapEditorEndpoints();
app.MapOnnxEndpoints();
app.MapFilesEndpoints();
app.MapSirilEndpoints();
app.MapGraXpertEndpoints();
app.MapCropEndpoints();

// GX-1: kick off an initial walk of the configured Onnx:ModelsPath
// so /api/onnx/manifest is populated before the first browser request.
// Hash compute stays lazy (RescanAsync only stat-walks; SHA-256 runs
// on first /manifest GET).
_ = Task.Run(async () => {
    try { await app.Services.GetRequiredService<NINA.Polaris.Services.Onnx.OnnxModelRegistry>().RescanAsync(); }
    catch (Exception ex) { app.Logger.LogWarning(ex, "OnnxModelRegistry initial scan failed"); }
});

// Live stacking + INDI
app.MapLiveStackEndpoints();
app.MapIndiEndpoints();

// WebSocket streams
app.Map("/ws/image-stream", ImageStreamHandler.Handle);
app.Map("/ws/status", StatusStreamHandler.Handle);
// Remote terminal, gated by Terminal:Enabled in appsettings. The
// handler itself returns 403 when disabled so a curious client can
// still see why the endpoint exists.
app.Map("/ws/terminal", TerminalSocketHandler.Handle);

// GX-10: surface where to actually reach the server. Logs the HTTP
// + HTTPS endpoints at startup so the user (and the docs/screenshots)
// can copy the right URL into a remote browser without guessing.
// The cert fingerprint goes to the log too so a security-paranoid
// user can verify what Chrome shows matches what Polaris generated.
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Polaris.Startup");
if (httpsEnabled) {
    startupLogger.LogInformation("HTTPS listening on https://*:{Port}  (cert fingerprint {Fp})",
        httpsPort, certService.Fingerprint);
    startupLogger.LogInformation("HTTPS is the LAN entry point, use one of: {Names}",
        string.Join(", ", certService.SanEntries().Take(8)));
}
if (httpEnabled) {
    var bind = httpBindAny ? "*" : "127.0.0.1 (loopback only)";
    startupLogger.LogInformation("HTTP  listening on http://{Bind}:{Port} {Note}",
        bind, httpPort,
        httpBindAny
            ? "(LAN-exposed, Server:Http:Bind=any)"
            : "(loopback only, used by Relay tunnel + host-local scripts)");
}
if (!httpsEnabled && !httpEnabled) {
    startupLogger.LogWarning("Both HTTP and HTTPS are disabled, Polaris will not accept any requests.");
}

app.Run();

// Reached when the host shuts down cleanly (Ctrl-C, SIGTERM,
// IHostApplicationLifetime.StopApplication). Returning 0 here is
// what gives top-level Main its `int` return type — required
// because the --ascom-setup helper path above returns 1/2 on
// driver-side failures, and the C# compiler insists every code
// path of an `int`-returning Main produces an int.
return 0;
