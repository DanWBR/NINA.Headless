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

    var BRIDGE_VERSION = '0.3.1-swe3';

    // SWE-3: where the engine looks for HiPS data. Default is the
    // live Stellarium Web CloudFront — the same URL the official
    // stellarium-web.org app uses. Tiles are fetched lazily as the
    // user pans/zooms and the browser's HTTP cache keeps them
    // around between sessions, so after a few minutes of use the
    // sky is effectively local.
    //
    // A true offline mirror requires a custom HiPS crawler that
    // walks each component's Norder pyramid by HEALPix index (the
    // recursive wget/curl in scripts/fetch-stellarium-skydata.sh
    // can only grab the property files because CloudFront doesn't
    // serve directory listings). That ships as a future
    // enhancement when there's demand for fully air-gapped
    // observatory installs.
    //
    // Override by setting window.__skyDataBase BEFORE this script
    // loads — e.g. to point at a populated local mirror:
    //   window.__skyDataBase = 'data/skydata/';
    var SKYDATA_BASE = window.__skyDataBase
        || 'https://d3ufh70wg9uzo4.cloudfront.net/skydata/';

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
    // and update the on-screen status. Wrap in a load handler so the
    // canvas is definitely in the DOM.
    // -----------------------------------------------------------------
    window.addEventListener('load', function () {
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
            window.StelWebEngine({
                wasmFile: 'js/wasm/stellarium-web-engine.wasm',
                canvas: document.getElementById('stel-canvas'),
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
    });
})();
