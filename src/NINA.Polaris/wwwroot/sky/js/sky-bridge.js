/*
 * sky-bridge.js — postMessage RPC between the Polaris main app and
 * the stellarium-web-engine sub-application.
 *
 * SWE-1 scope (this commit): WebGL detection + initial { type: "ready" }
 * handshake. No engine wired yet — SWE-2 plugs in StelWebEngine(),
 * SWE-3 wires data sources, SWE-4 handles observer/look-at/search RPC,
 * SWE-5 adds FOV-overlay + drag-to-frame messages.
 *
 * Message protocol (parent → iframe):
 *   { type: "set-observer", lat, lng }       (degrees)
 *   { type: "set-time", utc }                (epoch ms)
 *   { type: "look-at", raDeg, decDeg, fovDeg? }
 *   { type: "search", query }
 *   { type: "get-center" }                   (replies "center")
 *   { type: "set-fov-overlays", mount, target }
 *   { type: "set-drag-mode", mode }          ("free" | "fixed-target")
 *
 * Message protocol (iframe → parent):
 *   { type: "ready", version, webgl }
 *   { type: "search-result", query, result }
 *   { type: "center", raDeg, decDeg, fovDeg }
 *   { type: "map-click", raDeg, decDeg, objectName }
 *   { type: "webgl-unavailable" }
 *
 * The parent (Polaris app.js) installs a `message` listener on window
 * and forwards relevant calls; it also keeps the iframe handle so it
 * can postMessage targeted at this sub-app.
 *
 * This file is loaded by /sky/index.html and runs in the iframe's
 * own document. It only touches APIs available on the engine; nothing
 * here imports or depends on the main app's code.
 */
(function () {
    'use strict';

    var BRIDGE_VERSION = '0.3.7-swe3';

    // -----------------------------------------------------------------
    // CRITICAL: stellarium-web-engine's emscripten layer can't resolve
    // relative URLs in addDataSource — see comment in
    // external/stellarium-web-engine/apps/simple-html/stellarium-web-engine.html
    // around its getBaseUrl(): "at the moment emscripten doesn't
    // support relative url properly". A relative 'data/skydata/' will
    // silently 404 against some internal base and the engine renders
    // an empty sky with no console error.
    //
    // Build the absolute URL of the directory holding /sky/index.html
    // and prepend it ourselves. window.location.href returns e.g.
    // "http://host:5000/sky/" (or "http://host:5000/sky/index.html");
    // strip the final segment and re-append a slash so the join with
    // 'data/skydata/' yields a clean absolute URL.
    // -----------------------------------------------------------------
    function skyBaseUrl() {
        var url = window.location.href.split('/');
        // Last segment is "" (when href ends with "/") or "index.html" —
        // either way we want everything *before* that final slash.
        url.pop();
        return url.join('/') + '/';
    }

    // SWE-3: where the engine looks for HiPS data. Default is the
    // bundled local copy under wwwroot/sky/data/skydata/ — the same
    // test-skydata tree that ships inside the stellarium-web-engine
    // submodule (apps/test-skydata/). ~4.6MB total, includes:
    //
    //   stars/        Hipparcos + Tycho HiPS pyramid (Norder 0-N .eph)
    //   dso/          NGC / IC / Messier DSO HiPS
    //   surveys/      milkyway + sso/moon + sso/sun image surveys
    //   landscapes/   guereins horizon panorama
    //   skycultures/  IAU western constellations + names
    //   mpcorb.dat    Minor Planet Center asteroid elements
    //   CometEls.txt  bright-comet orbital elements
    //   tle_satellite.jsonl.gz  satellite TLEs (optional)
    //
    // Earlier defaults pointed at https://d3ufh70wg9uzo4.cloudfront.net/skydata/
    // (the CloudFront bucket we found referenced in stellarium-web.org's
    // webpack chunk). That URL turned out to host the stellarium-web.org
    // SPA itself, not a HiPS data mirror — every probe came back with the
    // same 2894-byte SPA index.html, so the engine got HTML instead of
    // .eph tile bytes and silently rendered an empty sky.
    //
    // Override by setting window.__skyDataBase BEFORE this script
    // loads — e.g. to point at an external HiPS mirror:
    //   window.__skyDataBase = 'https://example.com/skydata/';
    //
    // NOTE: always resolved through skyBaseUrl() above so the engine
    // sees an absolute URL even when the caller passes a relative one.
    // Engine fetches break otherwise (emscripten relative-URL bug).
    var _skydataRaw = window.__skyDataBase || 'data/skydata/';
    var SKYDATA_BASE = /^https?:\/\//.test(_skydataRaw)
        ? _skydataRaw
        : skyBaseUrl() + _skydataRaw;

    // -----------------------------------------------------------------
    // WebGL detection.
    //
    // The engine ships in SWE-2; until then we just probe the canvas
    // ahead of time and warn the parent early if the browser has no
    // WebGL2. Probing both webgl2 (preferred — engine targets it) and
    // webgl (so we can later print a clearer "WebGL1 but engine needs 2"
    // message instead of a generic failure).
    // -----------------------------------------------------------------
    function detectWebGL() {
        // CRITICAL: don't probe the real #stel-canvas. Once a canvas
        // is associated with a graphics context, subsequent getContext
        // calls with a different `contextType` return null. The
        // engine's getContext('webgl2') would then come back undefined
        // and __glGenObject would crash with:
        //   Cannot read properties of undefined (reading 'createTexture')
        //
        // Use a throwaway off-DOM canvas for the capability probe.
        var probe = document.createElement('canvas');
        var gl2 = null, gl1 = null;
        try { gl2 = probe.getContext('webgl2'); } catch (e) { /* swallow */ }
        try { gl1 = probe.getContext('webgl') || probe.getContext('experimental-webgl'); }
        catch (e) { /* swallow */ }
        // Drop the throwaway — GC reclaims it once the function returns.
        return { webgl: !!gl1 || !!gl2, webgl2: !!gl2 };
    }

    function setStatus(text) {
        var el = document.getElementById('sky-status');
        if (!el) return;
        if (text === null) {
            el.style.display = 'none';
        } else {
            el.style.display = 'flex';
            var msg = el.querySelector('.msg');
            if (msg) msg.textContent = text;
        }
    }

    function postToParent(msg) {
        // window.parent is the Polaris main app. Same-origin (iframe
        // sandbox allows it), so we don't need to lock down targetOrigin
        // for security here — but use '*' so any embedding works (admin
        // tools, future relay deployments).
        try {
            window.parent.postMessage(msg, '*');
        } catch (e) {
            console.warn('[Sky] postMessage to parent failed', e);
        }
    }

    // -----------------------------------------------------------------
    // Incoming message router. SWE-1 only logs unknown messages so we
    // can see in DevTools that the parent is reaching us; real handlers
    // ship in SWE-4 / SWE-5.
    // -----------------------------------------------------------------
    window.addEventListener('message', function (ev) {
        var msg = ev.data;
        if (!msg || typeof msg !== 'object' || !msg.type) return;
        // Ignore messages we accidentally bounced to ourselves.
        if (msg.__from === 'sky-bridge') return;
        console.log('[Sky] received from parent:', msg.type, msg);
    });

    // -----------------------------------------------------------------
    // Init: detect WebGL, announce ready (or unavailable) to parent,
    // and update the on-screen status.
    //
    // Originally listened for window 'load', but Visual Studio's
    // BrowserLink injects long-lived SignalR connections
    // (/_vs/browserLink + negotiate?clientProtocol=2.1 + websocket
    // connect) that stay 'Pending' for the entire page lifetime in
    // dev mode. Some of those XHRs were holding the 'load' event back
    // and the bridge never reached its init — engine .js had loaded
    // (StelWebEngine on window) and engine WASM was cached, but
    // StelWebEngine({}) was never invoked, so onReady never fired,
    // __stel stayed undefined, and addDataSource was never called.
    // Console diagnostic confirmed: status panel still showed the
    // initial 'Loading sky engine…' HTML — setStatus(null) below
    // never ran.
    //
    // The canvas is in the DOM as soon as the parser passes <body>,
    // so DOMContentLoaded is enough. Run synchronously when the DOM
    // is already past 'loading' (covers the case where sky-bridge.js
    // gets cached and parses after DOMContentLoaded already fired).
    // -----------------------------------------------------------------
    function bootBridge() {
        var caps = detectWebGL();
        if (!caps.webgl2) {
            setStatus(
                caps.webgl
                    ? 'This browser has WebGL but no WebGL2 — the sky engine needs WebGL2. Try a recent Chrome / Firefox / Safari.'
                    : 'WebGL is not available — open Polaris from a desktop browser.');
            postToParent({ type: 'webgl-unavailable', webgl1: caps.webgl, __from: 'sky-bridge' });
            return;
        }

        // SWE-2: attempt to boot the WASM engine. The <script> tag
        // for js/wasm/stellarium-web-engine.js loads asynchronously
        // (with onerror logging a clear "build me" hint), so check
        // both whether it got injected AND whether StelWebEngine
        // arrived on the window.
        //
        // If the engine .js wasn't built yet (404), tell the parent
        // we're up but the engine is missing — the parent can show
        // a one-time toast asking the dev to run build-stellarium-web.sh
        // without breaking anything.
        if (typeof window.StelWebEngine !== 'function') {
            setStatus('Sky engine WASM not built yet — run scripts/build-stellarium-web.sh from the repo root.');
            postToParent({
                type: 'ready',
                version: BRIDGE_VERSION,
                webgl: true,
                webgl2: true,
                engineLoaded: false,
                engineMissing: true,
                __from: 'sky-bridge'
            });
            console.warn('[Sky] bridge ready v' + BRIDGE_VERSION + ' — engine missing');
            return;
        }

        // Engine .js is loaded. Boot it; it will fetch the .wasm
        // sidecar from the same directory. SWE-3 fills in the data
        // sources (stars / DSOs / surveys / etc.); for now the engine
        // initialises into an empty starfield, which is enough to
        // verify the WASM pipeline is alive.
        try {
            // Hide the loading-status panel as soon as we kick off
            // engine init — the engine starts drawing the atmosphere
            // + sun within a frame or two, and the placeholder text
            // would obscure that visual confirmation. onReady (below)
            // is a separate signal that the JS bridge can be called.
            setStatus(null);
            // wasmFile MUST be absolute. The engine .js is loaded from
            // /sky/js/wasm/stellarium-web-engine.js, and emscripten
            // resolves any relative wasmFile against that script's URL
            // — so 'js/wasm/stellarium-web-engine.wasm' becomes
            // /sky/js/wasm/js/wasm/stellarium-web-engine.wasm (path
            // duplicated) → silent 404 → onRuntimeInitialized never
            // fires → onReady never fires → __stel stays undefined.
            // Same emscripten-can't-do-relative-URLs trap that bit us
            // with addDataSource. skyBaseUrl() returns the absolute URL
            // of /sky/ so prepending it lands the wasm at the right
            // /sky/js/wasm/... path.
            // Persistent diagnostic hooks. emscripten swallows native
            // errors silently; without these, an asset-preload that
            // never resolves (runDependencies > 0 after preRun) leaves
            // the engine init pending forever with no observable error.
            // monitorRunDependencies fires on every addRunDependency /
            // removeRunDependency call, so a stuck >0 count surfaces
            // immediately in the console.
            var modulePromise = window.StelWebEngine({
                wasmFile: skyBaseUrl() + 'js/wasm/stellarium-web-engine.wasm',
                canvas: document.getElementById('stel-canvas'),
                print: function (s) { console.log('[Sky emcc stdout]', s); },
                printErr: function (s) { console.error('[Sky emcc stderr]', s); },
                onAbort: function (what) { console.error('[Sky emcc ABORT]', what); },
                monitorRunDependencies: function (left) {
                    console.log('[Sky emcc runDependencies] now=' + left);
                },
                onReady: function (stel) {
                    window.__stel = stel;       // exposed for SWE-4 RPC handlers

                    // SWE-3: register HiPS + auxiliary data sources.
                    // The engine fetches tiles lazily; URLs that 404
                    // (because the user hasn't run the fetch script
                    // yet) get logged in DevTools as failed requests
                    // but don't crash anything — the engine renders
                    // empty for those subsystems.
                    //
                    // Mirror the live Stellarium Web's data layout
                    // exactly so the same SkyCulture keys ("western"),
                    // landscape keys ("guereins"), and survey keys
                    // ("moon" / "sun") resolve identically.
                    try {
                        var core = stel.core;
                        core.stars.addDataSource({ url: SKYDATA_BASE + 'stars' });
                        core.skycultures.addDataSource({
                            url: SKYDATA_BASE + 'skycultures/western',
                            key: 'western'
                        });
                        core.dsos.addDataSource({ url: SKYDATA_BASE + 'dso' });
                        core.landscapes.addDataSource({
                            url: SKYDATA_BASE + 'landscapes/guereins',
                            key: 'guereins'
                        });
                        core.milkyway.addDataSource({
                            url: SKYDATA_BASE + 'surveys/milkyway'
                        });
                        core.minor_planets.addDataSource({
                            url: SKYDATA_BASE + 'mpcorb.dat',
                            key: 'mpc_asteroids'
                        });
                        core.planets.addDataSource({
                            url: SKYDATA_BASE + 'surveys/sso/moon',
                            key: 'moon'
                        });
                        core.planets.addDataSource({
                            url: SKYDATA_BASE + 'surveys/sso/sun',
                            key: 'sun'
                        });
                        console.log('[Sky] data sources registered (base: ' + SKYDATA_BASE + ')');
                    } catch (dsErr) {
                        // The engine's addDataSource API can change
                        // between versions — surface a clear error
                        // rather than letting the engine render an
                        // uninformative blank sky.
                        console.error('[Sky] addDataSource failed:', dsErr);
                    }

                    postToParent({
                        type: 'ready',
                        version: BRIDGE_VERSION,
                        webgl: true,
                        webgl2: true,
                        engineLoaded: true,
                        dataBase: SKYDATA_BASE,
                        __from: 'sky-bridge'
                    });
                    console.log('[Sky] engine onReady — bridge v' + BRIDGE_VERSION);
                }
            });
            // StelWebEngine returns Module.ready (a Promise). If init
            // never completes, the promise stays pending forever — but
            // if WASM compile/instantiate or _core_init rejects, the
            // promise carries the real error. Catch it so we see it.
            if (modulePromise && typeof modulePromise.then === 'function') {
                modulePromise.then(
                    function (mod) {
                        console.log('[Sky] Module.ready RESOLVED. Module state:');
                        console.log('  _core_init:', typeof mod._core_init);
                        console.log('  GL:', typeof mod.GL,
                                    'createContext:', typeof (mod.GL && mod.GL.createContext));
                        console.log('  getModule:', typeof mod.getModule);
                        console.log('  canvas attached:', !!mod.canvas);
                        console.log('  Module.core:', typeof mod.core,
                                    'Module.observer:', typeof mod.observer);
                        // If onRuntimeInitialized was supposed to set
                        // .core but didn't, it threw silently. Re-invoke
                        // it inside a try/catch so we see the error.
                        if (!mod.core && typeof mod.onRuntimeInitialized === 'function') {
                            console.log('[Sky] Module.core is undefined — onRuntimeInitialized failed.'
                                + ' Re-invoking under try/catch to capture the error…');
                            try {
                                mod.onRuntimeInitialized();
                                console.log('[Sky] manual re-invoke returned. Module.core now:',
                                    typeof mod.core);
                            } catch (e) {
                                console.error('[Sky] onRuntimeInitialized THREW:', e,
                                    '\n  stack:', e && e.stack);
                            }
                        }
                    },
                    function (err) { console.error('[Sky] Module.ready REJECTED:', err); }
                );
                // Catch any unhandled rejections from emscripten's
                // internal promise chain that don't surface through .then
                window.addEventListener('unhandledrejection', function (ev) {
                    console.error('[Sky] unhandledrejection:', ev.reason);
                });
                // Same for uncaught synchronous errors after the promise
                // resolved (engine onRuntimeInitialized lives in an async
                // tick triggered by the asset preload callback).
                window.addEventListener('error', function (ev) {
                    console.error('[Sky] window.onerror:', ev.message,
                        'at', ev.filename + ':' + ev.lineno + ':' + ev.colno,
                        ev.error && ev.error.stack);
                });
            }
            // Watchdog: if onReady doesn't fire in 10s, log a hint so
            // we don't sit forever wondering why the sky's blank.
            setTimeout(function () {
                if (!window.__stel) {
                    console.warn('[Sky] WATCHDOG: 10s elapsed and onReady never fired. '
                        + 'Check [Sky emcc runDependencies] logs above — '
                        + 'a stuck >0 value means an asset preload never resolved.');
                }
            }, 10000);
        } catch (e) {
            console.error('[Sky] StelWebEngine init failed:', e);
            setStatus('Sky engine init failed — see DevTools console.');
            postToParent({
                type: 'ready',
                version: BRIDGE_VERSION,
                webgl: true,
                webgl2: true,
                engineLoaded: false,
                engineMissing: false,
                engineInitError: String(e && e.message || e),
                __from: 'sky-bridge'
            });
        }
    }

    // Fire bootBridge() as soon as the DOM is ready. If the document is
    // already past 'loading' (sky-bridge.js cached + parsed after
    // DOMContentLoaded), run synchronously on the next microtask so we
    // don't lose the boot. Otherwise wait for DOMContentLoaded — the
    // canvas is in the DOM by then; window 'load' is the wrong signal
    // because dev-time injections (BrowserLink SignalR) can keep
    // pending XHRs open and indefinitely delay it.
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', bootBridge);
    } else {
        Promise.resolve().then(bootBridge);
    }
})();
