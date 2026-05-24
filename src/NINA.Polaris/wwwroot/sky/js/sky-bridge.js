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

    var BRIDGE_VERSION = '0.6.6-swe5';

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
    // SWE-4: postMessage RPC handlers — observer, time, look-at,
    // search, get-center. All map onto stellarium-web-engine's
    // documented JS surface (apps/simple-html/tests.js is the canonical
    // reference, verified against the actual upstream submodule).
    //
    // Engine uses radians everywhere; we convert at the boundary so the
    // parent app stays in degrees and JS Dates. stel.D2R is the constant.
    // -----------------------------------------------------------------

    // Look up an object by name. Engine accepts "M31", "NAME M31",
    // "NAME Sun", "Jupiter", etc. Returns null if not found.
    function skyFindObject(query) {
        if (!window.__stel) return null;
        var stel = window.__stel;
        // Try a few common name variants the engine knows.
        var candidates = [query, 'NAME ' + query];
        // M-, NGC-, IC- catalogs are recognised by the engine literally;
        // try the canonical form first so 'M31' resolves before 'NAME M31'.
        for (var i = 0; i < candidates.length; i++) {
            var obj = stel.getObj(candidates[i]);
            if (obj) return obj;
        }
        return null;
    }

    // Extract RA/Dec (degrees, ICRS-J2000) + magnitude from an engine
    // object. Mirrors what the upstream selected-object-info component
    // does. Returns { raDeg, decDeg, magnitude, name } or null.
    function skyObjectInfo(obj) {
        if (!obj || !window.__stel) return null;
        var stel = window.__stel;
        try {
            var obs = stel.core.observer;
            // ICRF gives RA/Dec without aberration; CIRS is current
            // intermediate (apparent). UI prefers ICRS so it matches
            // catalog values shown elsewhere in Polaris.
            var icrf = obj.getInfo('radec', obs);
            var radec = stel.c2s(icrf);
            var raDeg = stel.anp(radec[0]) / stel.D2R;
            var decDeg = stel.anpm(radec[1]) / stel.D2R;
            var vmag = null;
            try { vmag = obj.getInfo('VMAG'); } catch (e) { /* not all objects have it */ }
            var name = (obj.designations && obj.designations()[0]) || '';
            name = name.replace(/^NAME /, '');
            return { raDeg: raDeg, decDeg: decDeg, magnitude: vmag, name: name };
        } catch (e) {
            console.warn('[Sky] skyObjectInfo failed:', e);
            return null;
        }
    }

    // Aim the camera at an RA/Dec (ICRS, degrees). Mirrors the
    // sw_helpers.setSweObjAsSelection pattern — preferred path is
    // pointAndLock on a named object so the engine follows it as time
    // advances. Falls back to setting observer.yaw/pitch directly when
    // we don't have an object to lock to.
    function skyLookAt(raDeg, decDeg, fovDeg, objHint) {
        if (!window.__stel) return false;
        var stel = window.__stel;
        try {
            if (typeof fovDeg === 'number' && fovDeg > 0) {
                stel.core.fov = fovDeg * stel.D2R;
            }
            if (objHint) {
                // pointAndLock keeps the object centred as time advances.
                if (typeof stel.pointAndLock === 'function') {
                    stel.pointAndLock(objHint);
                    stel.core.selection = objHint;
                    return true;
                }
            }
            // Direct RA/Dec → altaz via spherical trig. Mirrors the
            // inverse of skyGetCenter — bypasses the minified-build's
            // unreliable stel.convertFrame so look-at actually lands
            // where we asked it to.
            var obs = stel.core.observer;
            var phi = obs.latitude;
            var mjd = obs.utc;
            var lng = obs.longitude;
            if (![phi, mjd, lng].every(function (v) {
                return typeof v === 'number' && isFinite(v);
            })) return false;

            // LST via Greenwich apparent sidereal time (same formula
            // as skyGetCenter, inverted direction).
            var T = mjd - 51544.5;
            var gstFrac = (T * 1.00273790935 + 0.7790572732640) % 1;
            if (gstFrac < 0) gstFrac += 1;
            var lst = 2 * Math.PI * gstFrac + lng;

            var raRad  = raDeg  * stel.D2R;
            var decRad = decDeg * stel.D2R;
            var H = lst - raRad;
            var sinH = Math.sin(H), cosH = Math.cos(H);
            var sinDec = Math.sin(decRad), cosDec = Math.cos(decRad);
            var sinPhi = Math.sin(phi),    cosPhi = Math.cos(phi);

            var sinAlt = sinDec * sinPhi + cosDec * cosPhi * cosH;
            if (sinAlt >  1) sinAlt =  1;
            if (sinAlt < -1) sinAlt = -1;
            var alt = Math.asin(sinAlt);
            var cosAlt = Math.cos(alt);
            if (Math.abs(cosAlt) < 1e-12) cosAlt = 1e-12;

            var sinA = -sinH * cosDec / cosAlt;
            var cosA = (sinDec - sinAlt * sinPhi) / (cosAlt * cosPhi);
            var A = Math.atan2(sinA, cosA);

            // Normalise azimuth to [0, 2π).
            A = ((A % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);

            obs.yaw = A;
            obs.pitch = alt;
            return true;
        } catch (e) {
            console.warn('[Sky] skyLookAt failed:', e);
            return false;
        }
    }

    // Read the current centre of the map back as RA/Dec ICRS + fov.
    // Uses observer.yaw/pitch (altaz) → OBSERVED → CIRS → ICRF.
    // Compute view-centre RA/Dec from observer altaz + LST via direct
    // spherical trig. Avoids the engine's convertFrame chain which is
    // intermittently flaky in this minified build (silently returns
    // null/NaN). All inputs are direct numeric attributes on the
    // observer — same source the parallactic-angle calc already uses.
    //
    //   sin(δ) = sin(alt)·sin(φ) + cos(alt)·cos(φ)·cos(A_north)
    //   sin(H) = −sin(A_north)·cos(alt) / cos(δ)
    //   cos(H) = (sin(alt) − sin(δ)·sin(φ)) / (cos(δ)·cos(φ))
    //   RA   = LST − H
    //
    // where A_north is azimuth measured from north going east. The
    // stellarium engine appears to use that same convention (yaw=0
    // points north). LST derived via Greenwich apparent sidereal time
    // from observer.utc (MJD) plus observer.longitude.
    function skyGetCenter() {
        if (!window.__stel) return null;
        var stel = window.__stel;
        try {
            var obs = stel.core.observer;
            var phi = obs.latitude;
            var A   = obs.yaw;
            var alt = obs.pitch;
            var mjd = obs.utc;
            var lng = obs.longitude;
            var fov = stel.core.fov;
            if (![phi, A, alt, mjd, lng, fov].every(function (v) {
                return typeof v === 'number' && isFinite(v);
            })) return null;

            var sinPhi = Math.sin(phi), cosPhi = Math.cos(phi);
            var sinAlt = Math.sin(alt), cosAlt = Math.cos(alt);
            var sinA   = Math.sin(A),   cosA   = Math.cos(A);

            var sinDec = sinAlt * sinPhi + cosAlt * cosPhi * cosA;
            // Clamp to safe domain for asin.
            if (sinDec >  1) sinDec =  1;
            if (sinDec < -1) sinDec = -1;
            var dec = Math.asin(sinDec);
            var cosDec = Math.cos(dec);
            if (Math.abs(cosDec) < 1e-12) cosDec = 1e-12;

            var sinH = -sinA * cosAlt / cosDec;
            var cosH = (sinAlt - sinDec * sinPhi) / (cosDec * cosPhi);
            var H = Math.atan2(sinH, cosH);

            // LST: Greenwich apparent sidereal time from MJD + longitude.
            //   gst (rad) = 2π · fracpart((mjd − 51544.5)·1.00273790935 + 0.7790572732640)
            var T = mjd - 51544.5;
            var gstFrac = (T * 1.00273790935 + 0.7790572732640) % 1;
            if (gstFrac < 0) gstFrac += 1;
            var gst = 2 * Math.PI * gstFrac;
            var lst = gst + lng;

            var raRad = lst - H;
            // Normalise RA to [0, 2π).
            raRad = ((raRad % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);

            var raDeg  = raRad / stel.D2R;
            var decDeg = dec   / stel.D2R;
            var fovDeg = fov   / stel.D2R;
            if (!isFinite(raDeg) || !isFinite(decDeg) || !isFinite(fovDeg)) {
                return null;
            }
            return { raDeg: raDeg, decDeg: decDeg, fovDeg: fovDeg };
        } catch (e) {
            console.warn('[Sky] skyGetCenter failed:', e);
            return null;
        }
    }

    // -----------------------------------------------------------------
    // SWE-5: FOV overlays. One shared layer holds two GeoJSON objects
    // (mount + target rectangles) plus an optional mosaic grid.
    // Re-created on each set-fov-overlays message because the engine's
    // geojson object doesn't seem to expose a stable mutate-data API —
    // remove old, add fresh. Cheap (≤ few polygons).
    // -----------------------------------------------------------------
    var __skyFovLayer = null;
    var __skyFovObjs = { mount: null, target: null, mosaic: null };

    function skyEnsureFovLayer() {
        if (__skyFovLayer || !window.__stel) return __skyFovLayer;
        try {
            __skyFovLayer = window.__stel.createLayer({
                id: 'polaris-fov', z: 10, visible: true
            });
        } catch (e) {
            console.warn('[Sky] createLayer failed:', e);
        }
        return __skyFovLayer;
    }

    // Build a 4-corner rectangle on the celestial sphere from
    // (centreRA, centreDec, widthDeg, heightDeg, rotationDeg).
    // Tangent-plane approximation is sufficient for camera-scale FOVs
    // (< 10°). Returns 5 [ra, dec] pairs (closed polygon — first ==
    // last per the GeoJSON spec).
    function skyFovRect(raDeg, decDeg, wDeg, hDeg, rotDeg) {
        var rot = (rotDeg || 0) * Math.PI / 180;
        var cosR = Math.cos(rot), sinR = Math.sin(rot);
        var hw = wDeg / 2, hh = hDeg / 2;
        // Tangent-plane corners (E is +ra-axis, N is +dec-axis).
        var local = [
            [-hw, -hh], [+hw, -hh], [+hw, +hh], [-hw, +hh]
        ];
        var cosDec = Math.cos(decDeg * Math.PI / 180);
        // Avoid /0 right at the pole — cap.
        if (Math.abs(cosDec) < 1e-3) cosDec = 1e-3;
        var ring = local.map(function (p) {
            var x = p[0] * cosR - p[1] * sinR;
            var y = p[0] * sinR + p[1] * cosR;
            return [raDeg + x / cosDec, decDeg + y];
        });
        ring.push(ring[0].slice()); // close
        return ring;
    }

    function skyFovGeoJson(centre, color, glow) {
        var raDeg = centre.raDeg, decDeg = centre.decDeg;
        var w = centre.widthDeg, h = centre.heightDeg;
        var rot = centre.rotationDeg || 0;
        var ring = skyFovRect(raDeg, decDeg, w, h, rot);
        // Engine geojson parser only knows: stroke, fill, stroke-width,
        // stroke-opacity, fill-opacity, stroke-glow. Plus title +
        // text-anchor + text-offset on Point features.
        var props = {
            stroke: color,
            'stroke-width': 2,
            'stroke-opacity': 1,
            'stroke-glow': true,
            fill: color,
            'fill-opacity': 0.0
        };
        // Crosshair: two LineString features through the centre, edge
        // midpoint to edge midpoint, matching the rotation of the
        // rectangle. Built with the same tangent-plane helper so the
        // dec scaling stays consistent with the perimeter ring.
        var crossH = skyFovRect(raDeg, decDeg, w, 0, rot);
        var crossV = skyFovRect(raDeg, decDeg, 0, h, rot);
        var hLine = [crossH[0], crossH[1]];
        var vLine = [crossV[0], crossV[1]];
        var crossProps = {
            stroke: color,
            'stroke-width': 1,
            'stroke-opacity': 0.35,
            'stroke-glow': false
        };
        // ASIAIR-style edge labels. Use Point features with title +
        // text-offset (engine renders title text near the point).
        //   - "Scope" at the corner ring[0] (bottom-left of unrotated)
        //   - "Rotation XXX.X°" at the midpoint of the left edge
        //   - "W° × H°" at the midpoint of the bottom edge
        var rotPositive = ((rot % 360) + 360) % 360;
        var rotText = 'Rotation ' + rotPositive.toFixed(1) + '°';
        var sizeText = w.toFixed(2) + '° × ' + h.toFixed(2) + '°';
        // Midpoints of two edges (averaged corners). ring is the
        // closed-polygon list [p0,p1,p2,p3,p0].
        var midLeft = [(ring[0][0] + ring[3][0]) / 2, (ring[0][1] + ring[3][1]) / 2];
        var midBottom = [(ring[0][0] + ring[1][0]) / 2, (ring[0][1] + ring[1][1]) / 2];
        var labelStyle = {
            fill: color,
            'fill-opacity': 1,
            'stroke-opacity': 0
        };
        function labelFeature(coord, text, anchor) {
            var p = Object.assign({}, labelStyle, {
                title: text,
                'text-anchor': anchor || 'center',
                'text-offset': [0, 0]
            });
            return { type: 'Feature', properties: p,
                geometry: { type: 'Point', coordinates: coord } };
        }
        return {
            type: 'FeatureCollection',
            features: [
                { type: 'Feature', properties: props,
                  geometry: { type: 'Polygon', coordinates: [ring] } },
                { type: 'Feature', properties: crossProps,
                  geometry: { type: 'LineString', coordinates: hLine } },
                { type: 'Feature', properties: crossProps,
                  geometry: { type: 'LineString', coordinates: vLine } },
                labelFeature(ring[0], 'Scope', 'bottom'),
                labelFeature(midLeft, rotText, 'bottom'),
                labelFeature(midBottom, sizeText, 'top')
            ]
        };
    }

    // SWE-5: target FOV as a screen-anchored CSS box (#sky-target-fov
    // in the iframe HTML). Sized by the ratio of camera FOV to the
    // engine's current core.fov, mapped to the canvas pixel dimensions.
    // Always at viewport centre — that's the whole point ("the planning
    // rectangle is wherever the user is looking right now").
    // Parallactic angle q (degrees) computed directly from observer
    // altaz + latitude — no convertFrame, no LST, no RA/Dec round-
    // trip needed. The engine's observer.yaw/pitch (altaz of the view
    // centre) is always populated and accessible without going through
    // SweObj string allocation.
    //
    // Formula from spherical trig:
    //   q = atan2( sin(A) · cos(φ),
    //              sin(φ) · cos(alt) − sin(alt) · cos(φ) · cos(A) )
    //
    // where:
    //   A   = observer.yaw   (azimuth, radians)
    //   alt = observer.pitch (altitude, radians)
    //   φ   = observer.latitude
    //
    // Sign convention depends on which way the engine measures
    // azimuth. We negate at the end if the box rotates the wrong way
    // — easy fix from console.
    function skyParallacticAngleDeg() {
        try {
            var stel = window.__stel;
            if (!stel || !stel.core || !stel.core.observer) return 0;
            var obs = stel.core.observer;
            var phi = obs.latitude;
            var A   = obs.yaw;
            var alt = obs.pitch;
            if (typeof phi !== 'number' || !isFinite(phi)) return 0;
            if (typeof A   !== 'number' || !isFinite(A))   return 0;
            if (typeof alt !== 'number' || !isFinite(alt)) return 0;
            var sinA = Math.sin(A), cosA = Math.cos(A);
            var sinPhi = Math.sin(phi), cosPhi = Math.cos(phi);
            var sinAlt = Math.sin(alt), cosAlt = Math.cos(alt);
            var q = Math.atan2(
                sinA * cosPhi,
                sinPhi * cosAlt - sinAlt * cosPhi * cosA
            );
            return q / stel.D2R;
        } catch (e) {
            console.warn('[Sky] parallactic angle calc failed:', e);
            return 0;
        }
    }

    function skyUpdateTargetFovBox(target) {
        __lastTargetFov = target || null;
        var el = document.getElementById('sky-target-fov');
        if (!el) return;
        if (!target || !(target.widthDeg > 0)) {
            el.style.display = 'none';
            return;
        }
        var canvas = document.getElementById('stel-canvas');
        if (!canvas) { el.style.display = 'none'; return; }
        var viewW = canvas.clientWidth || canvas.width;
        var viewH = canvas.clientHeight || canvas.height;
        var engineFovDeg = 60; // safe default if read fails
        try {
            if (window.__stel && window.__stel.core
                && typeof window.__stel.core.fov === 'number'
                && isFinite(window.__stel.core.fov)) {
                engineFovDeg = window.__stel.core.fov / window.__stel.D2R;
            }
        } catch (e) { /* fall through to default */ }
        if (engineFovDeg <= 0) engineFovDeg = 60;
        // Engine fov is the viewport's *narrower* dimension worth of
        // sky in degrees. Use the shorter screen edge as the reference
        // to get px-per-deg, then size the box from camera FOV.
        var refPx = Math.min(viewW, viewH);
        var pxPerDeg = refPx / engineFovDeg;
        var wPx = target.widthDeg * pxPerDeg;
        var hPx = target.heightDeg * pxPerDeg;
        // SWE-5: rotation has two sources composed together:
        //   1. The CAMERA's roll angle from the plate solve / rotator
        //      (target.rotationDeg from the parent — usually 0 when no
        //      rotator + no solve).
        //   2. The PARALLACTIC angle at the view centre — this is the
        //      angle between celestial north and the engine's "screen
        //      up" direction. Without it, the rectangle stays aligned
        //      with screen up but the surrounding sky rotates around it
        //      as the user drags. With it, the rectangle stays glued
        //      to the celestial frame the same way an equatorial-mount
        //      camera does.
        var cameraRollDeg = target.rotationDeg || 0;
        var parallacticDeg = skyParallacticAngleDeg();
        var rotDeg = cameraRollDeg + parallacticDeg;
        el.style.width = wPx + 'px';
        el.style.height = hPx + 'px';
        el.style.transform = 'translate(-50%, -50%) rotate(' + rotDeg.toFixed(2) + 'deg)';
        el.style.display = 'block';

        // SWE-5: ASIAIR-style edge labels. Rotation reads positive in
        // [0, 360) for consistency with how the ASIAIR / N.I.N.A. solver
        // report frame angles.
        var rotPositive = ((rotDeg % 360) + 360) % 360;
        var rotLabel = document.getElementById('sky-target-rotlabel');
        if (rotLabel) rotLabel.textContent = 'Rotation ' + rotPositive.toFixed(1) + '°';
        var sizeLabel = document.getElementById('sky-target-sizelabel');
        if (sizeLabel) sizeLabel.textContent =
            target.widthDeg.toFixed(2) + '° × ' + target.heightDeg.toFixed(2) + '°';

        console.log('[Sky] target FOV box: ' + wPx.toFixed(0) + '×' + hPx.toFixed(0)
            + 'px (engineFov=' + engineFovDeg.toFixed(2) + '°, cam='
            + target.widthDeg.toFixed(2) + '°, rot=' + rotDeg.toFixed(1)
            + '° = cam ' + cameraRollDeg.toFixed(1) + '° + parallactic ' + parallacticDeg.toFixed(1) + '°)');
    }

    function skyRemoveObj(slot) {
        var obj = __skyFovObjs[slot];
        if (!obj || !__skyFovLayer) return;
        try {
            if (typeof __skyFovLayer.remove === 'function') {
                __skyFovLayer.remove(obj);
            }
        } catch (e) { /* engine may not support remove — swallow */ }
        __skyFovObjs[slot] = null;
    }

    function skySetFovOverlays(mount, target, mosaic) {
        var stel = window.__stel;
        if (!stel) return;
        skyEnsureFovLayer();
        if (!__skyFovLayer) return;
        // Mount stays as a celestial-anchored geojson (engine renders
        // it where the scope is pointing, even if user drags away).
        // Mosaic likewise.
        // Target: SCREEN-anchored — pure CSS overlay sized by camera
        // FOV ratio to engine fov. Always at viewport centre, always
        // visible, doesn't need any engine round-trip.
        skyRemoveObj('mount');
        skyRemoveObj('mosaic');
        console.log('[Sky] set-fov-overlays mount=', mount, 'target=', target);
        try {
            if (mount && mount.widthDeg > 0) {
                __skyFovObjs.mount = stel.createObj('geojson', {
                    data: skyFovGeoJson(mount, '#1e40af', false)  // dark blue
                });
                __skyFovLayer.add(__skyFovObjs.mount);
                console.log('[Sky] mount FOV rect created at RA=' + mount.raDeg.toFixed(2)
                    + '° Dec=' + mount.decDeg.toFixed(2) + '° size=' + mount.widthDeg.toFixed(2)
                    + '°×' + mount.heightDeg.toFixed(2) + '°');
            }
            // Update the screen-anchored target FOV CSS box.
            skyUpdateTargetFovBox(target);
            if (mosaic && mosaic.tiles && mosaic.tiles.length) {
                // Mosaic grid: one polygon per tile, yellow.
                var features = mosaic.tiles.map(function (t) {
                    var ring = skyFovRect(t.raDeg, t.decDeg,
                        t.widthDeg, t.heightDeg, t.rotationDeg || 0);
                    return {
                        type: 'Feature',
                        properties: {
                            stroke: '#facc15', 'stroke-width': 1,
                            'stroke-opacity': 0.7,
                            fill: '#facc15', 'fill-opacity': 0.03
                        },
                        geometry: { type: 'Polygon', coordinates: [ring] }
                    };
                });
                __skyFovObjs.mosaic = stel.createObj('geojson', {
                    data: { type: 'FeatureCollection', features: features }
                });
                __skyFovLayer.add(__skyFovObjs.mosaic);
            }
        } catch (e) {
            console.warn('[Sky] set-fov-overlays failed:', e);
        }
    }

    // -----------------------------------------------------------------
    // SWE-5: emit 'center' to the parent whenever the user drags the
    // sky (changes observer.yaw/pitch). The engine fires stel.change()
    // for any property update; we filter to observer pose changes and
    // throttle to ~10 Hz so the parent can keep its target FOV rect
    // anchored to the new map centre without flooding postMessage.
    // -----------------------------------------------------------------
    function skyInstallChangeHook() {
        var stel = window.__stel;
        if (!stel || typeof stel.change !== 'function') return;
        var lastEmit = 0;
        var lastFov = -1;
        var lastYaw = NaN, lastPitch = NaN;
        try {
            // Listen on every change; the engine emits a lot of these
            // (planets, atmosphere, etc.) so dedup ourselves by reading
            // the values we actually care about and bailing when they
            // haven't moved meaningfully. The minified build attr names
            // aren't always stable, so a broad filter is more reliable
            // than `if (attr === 'core.fov')`.
            stel.change(function (obj, attr) {
                var core = stel.core;
                if (!core) return;
                var fov = core.fov;
                var obs = core.observer;
                if (!obs) return;
                var yaw = obs.yaw, pitch = obs.pitch;
                var fovChanged = isFinite(fov) && fov !== lastFov;
                var poseChanged = (yaw !== lastYaw) || (pitch !== lastPitch);
                if (!fovChanged && !poseChanged) return;
                if (fovChanged) lastFov = fov;
                if (poseChanged) { lastYaw = yaw; lastPitch = pitch; }
                // Resize the target box on any fov change AND re-rotate
                // it for the new parallactic angle on any pose change.
                if (__lastTargetFov) skyUpdateTargetFovBox(__lastTargetFov);
                var now = Date.now();
                if (now - lastEmit < 100) return;
                lastEmit = now;
                postToParent({
                    type: 'center',
                    center: skyGetCenter(),
                    fromDrag: true,
                    __from: 'sky-bridge'
                });
            });
        } catch (e) {
            console.warn('[Sky] change-hook install failed:', e);
        }
        // Window resize also requires re-measuring the target box —
        // canvas dimensions changed.
        window.addEventListener('resize', function () {
            if (__lastTargetFov) skyUpdateTargetFovBox(__lastTargetFov);
        });
    }

    // Cache the most recently received target so engine fov / window
    // resize callbacks can re-render the box without an extra
    // postMessage round-trip from the parent.
    var __lastTargetFov = null;

    // -----------------------------------------------------------------
    // Incoming message router (parent → iframe).
    // -----------------------------------------------------------------
    window.addEventListener('message', function (ev) {
        var msg = ev.data;
        if (!msg || typeof msg !== 'object' || !msg.type) return;
        if (msg.__from === 'sky-bridge') return;  // our own echo
        // Hold messages that arrive before the engine is ready. SWE-4
        // callers like Polaris's 30s sky ticker fire immediately on
        // page load and would otherwise no-op against a null __stel.
        if (!window.__stel) {
            (window.__skyPendingIn = window.__skyPendingIn || []).push(msg);
            return;
        }
        skyHandleMessage(msg);
    });

    function skyHandleMessage(msg) {
        var stel = window.__stel;
        switch (msg.type) {
            case 'set-observer':
                if (typeof msg.lat === 'number') stel.core.observer.latitude = msg.lat * stel.D2R;
                if (typeof msg.lng === 'number') stel.core.observer.longitude = msg.lng * stel.D2R;
                if (typeof msg.elevation === 'number') stel.core.observer.elevation = msg.elevation;
                break;
            case 'set-time':
                if (typeof msg.utc === 'number') {
                    stel.core.observer.utc = stel.date2MJD(new Date(msg.utc));
                }
                break;
            case 'look-at':
                var obj = msg.objectName ? skyFindObject(msg.objectName) : null;
                skyLookAt(msg.raDeg, msg.decDeg, msg.fovDeg, obj);
                break;
            case 'search':
                var found = skyFindObject(msg.query);
                postToParent({
                    type: 'search-result',
                    query: msg.query,
                    result: found ? skyObjectInfo(found) : null,
                    __from: 'sky-bridge'
                });
                break;
            case 'get-center':
                postToParent({
                    type: 'center',
                    requestId: msg.requestId || null,
                    center: skyGetCenter(),
                    __from: 'sky-bridge'
                });
                break;
            case 'select-clear':
                try { stel.core.selection = 0; } catch (e) { /* swallow */ }
                break;
            case 'set-fov-overlays':
                // SWE-5: mount FOV (blue), target FOV (red dashed),
                // optional mosaic grid (yellow). Each side is null to
                // clear that overlay.
                skySetFovOverlays(msg.mount || null, msg.target || null, msg.mosaic || null);
                break;
            default:
                console.log('[Sky] unknown message type:', msg.type);
        }
    }

    // Drain queued parent messages once the engine is ready.
    function skyDrainPending() {
        if (!window.__stel || !window.__skyPendingIn) return;
        var queue = window.__skyPendingIn;
        window.__skyPendingIn = [];
        for (var i = 0; i < queue.length; i++) {
            try { skyHandleMessage(queue[i]); }
            catch (e) { console.warn('[Sky] queued msg replay failed:', e); }
        }
    }

    // -----------------------------------------------------------------
    // Canvas click → map-click. Converts pixel coords to RA/Dec via
    // the engine's pick API if available, falling back to projecting
    // through observer altaz when not. Either path gives the parent
    // the celestial coordinate under the mouse so it can populate the
    // Slew & Center workflow.
    // -----------------------------------------------------------------
    document.addEventListener('click', function (ev) {
        if (!window.__stel) return;
        var canvas = document.getElementById('stel-canvas');
        if (!canvas || ev.target !== canvas) return;
        var rect = canvas.getBoundingClientRect();
        var x = ev.clientX - rect.left;
        var y = ev.clientY - rect.top;
        var stel = window.__stel;
        try {
            var hit = null, objName = null;
            // Engine sometimes exposes core.pick(x,y) for hit-testing.
            if (typeof stel.core.pick === 'function') {
                hit = stel.core.pick(x, y);
                if (hit) {
                    var info = skyObjectInfo(hit);
                    if (info) {
                        objName = info.name;
                        postToParent({
                            type: 'map-click',
                            raDeg: info.raDeg, decDeg: info.decDeg,
                            objectName: objName,
                            __from: 'sky-bridge'
                        });
                        return;
                    }
                }
            }
            // No object under cursor — return the sky coord at the click.
            // Engine helper screenToCanonical / convertFrame chain isn't
            // documented stably across builds; emit the screen coord + the
            // current map centre so the parent can fall back to it.
            var centre = skyGetCenter();
            postToParent({
                type: 'map-click',
                raDeg: centre ? centre.raDeg : null,
                decDeg: centre ? centre.decDeg : null,
                objectName: null,
                screenX: x, screenY: y,
                __from: 'sky-bridge'
            });
        } catch (e) {
            console.warn('[Sky] click handler failed:', e);
        }
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
            // onReady extracted into a named function so we can call it
            // manually from the Module.ready.then() handler below if the
            // engine's "if (Module.onReady) Module.onReady(Module)" line
            // doesn't fire on its own (which is the case in this build —
            // Module.core gets set inside onRuntimeInitialized but the
            // onReady invocation that follows never reaches us).
            function onEngineReady(stel) {
                if (window.__stel) return;       // idempotent — engine may try twice
                window.__stel = stel;            // exposed for SWE-4 RPC handlers

                // SWE-5: turn off atmosphere (Polaris is a planning
                // tool — user wants to see stars 24/7, not the engine's
                // simulated daytime sky tint). Landscape stays ON so
                // the guereins horizon panorama still anchors below-
                // horizon directions visually. Refraction off so
                // RA/Dec ↔ altaz conversions match the catalog values
                // shown elsewhere in Polaris.
                //
                // Side benefit of atmosphere off: it was the painter
                // that multiplied feature colour by brightness, which
                // was washing the FOV rectangles out. With it off the
                // #1e40af mount blue and #ef4444 target red render at
                // full saturation.
                try {
                    if (stel.core.atmosphere) stel.core.atmosphere.visible = false;
                    if (stel.core.observer) stel.core.observer.refraction = false;
                } catch (envErr) {
                    console.warn('[Sky] could not disable atmosphere/refraction:', envErr);
                }

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
                console.log('[Sky] engine onReady fired — bridge v' + BRIDGE_VERSION);
                // SWE-5: install the change-hook that emits 'center'
                // when the user drags the sky. Must run AFTER stel is
                // populated; before that stel.change is undefined.
                skyInstallChangeHook();
                // SWE-4: flush any RPC messages the parent queued while
                // we were initialising (Polaris's first set-observer /
                // set-time tick typically fires within the first second
                // of page load, well before the engine onReady completes).
                skyDrainPending();
                // SWE-5: emit an initial 'center' so the parent can
                // seed its skyTarget unconditionally — without this,
                // the change-hook never fires unless observer.yaw/pitch
                // mutate (which they don't on a fresh boot before any
                // look-at), so skyTarget stays null and the red target
                // rectangle is never drawn. Retry a few times in case
                // the engine's coords aren't valid right at onReady.
                var initialCentreTries = 0;
                var emitInitialCentre = function () {
                    var c = skyGetCenter();
                    if (c) {
                        postToParent({
                            type: 'center',
                            center: c,
                            fromDrag: true,
                            __from: 'sky-bridge'
                        });
                        console.log('[Sky] initial centre emitted: RA='
                            + c.raDeg.toFixed(2) + '° Dec=' + c.decDeg.toFixed(2)
                            + '° FOV=' + c.fovDeg.toFixed(2) + '°');
                        return;
                    }
                    initialCentreTries++;
                    if (initialCentreTries < 10) {
                        setTimeout(emitInitialCentre, 100);
                    } else {
                        console.warn('[Sky] gave up trying to emit initial centre after 10 tries');
                    }
                };
                setTimeout(emitInitialCentre, 50);
            }

            var modulePromise = window.StelWebEngine({
                wasmFile: skyBaseUrl() + 'js/wasm/stellarium-web-engine.wasm',
                canvas: document.getElementById('stel-canvas'),
                print: function (s) { console.log('[Sky emcc stdout]', s); },
                printErr: function (s) { console.error('[Sky emcc stderr]', s); },
                onAbort: function (what) { console.error('[Sky emcc ABORT]', what); },
                monitorRunDependencies: function (left) {
                    console.log('[Sky emcc runDependencies] now=' + left);
                },
                onReady: onEngineReady
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
                        console.log('  Module.onReady:', typeof mod.onReady,
                                    'window.__stel:', typeof window.__stel);
                        // Workaround: in this minified build of
                        // stellarium-web-engine the trailing
                        // "if (Module.onReady) Module.onReady(Module)"
                        // inside onRuntimeInitialized doesn't fire even
                        // when Module.core is set (verified by
                        // diagnostic). Drive it ourselves with the
                        // resolved Module — onEngineReady is idempotent
                        // so it's a no-op if the engine ever does call
                        // us first.
                        if (mod.core && !window.__stel) {
                            console.log('[Sky] Module.core set but our onReady never fired. '
                                + 'Driving it manually with the resolved Module.');
                            try { onEngineReady(mod); }
                            catch (e) { console.error('[Sky] manual onEngineReady THREW:', e,
                                '\n  stack:', e && e.stack); }
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
