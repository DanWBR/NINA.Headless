function ninaApp() {
    return {
        tab: 'home',
        nightMode: false,

        // Live View
        exposure: 30,
        gain: 100,
        binning: '1',
        liveActive: false,
        looping: false,
        capturing: false,
        stats: { starCount: '--', hfr: null, mean: null },
        currentTime: '--:--:--',
        cameraTemp: null,
        sessionCaptures: 0,
        imageHistory: [],

        // Live stacking
        liveStackEnabled: false,
        liveStackFrames: 0,

        // Mount
        mount: {
            ra: null, dec: null, alt: null, az: null,
            tracking: false, slewing: false, parked: false,
            pierSide: 'unknown', connected: false
        },

        // Focus
        focusPosition: 0,
        focusStep: 50,
        focusTemp: null,
        focusMoving: false,
        focusConnected: false,

        // Filter Wheel
        filterWheel: {
            connected: false,
            position: 0,
            currentFilter: '',
            filters: [],
            moving: false
        },
        selectedFilterWheel: null,

        // Sky
        skySearch: '',
        skyTarget: null,
        skyResults: [],
        skyShowResults: false,
        slewCenterJobId: null,
        slewCenterStatus: null,
        _slewCenterTimer: null,
        fov: { width: 2.82, height: 1.88 },

        // Sequence
        sequence: [],
        seqState: 'idle',
        seqStatus: null,
        _seqPollTimer: null,

        // Settings
        settings: {
            indiHost: 'localhost',
            indiPort: 7624,
            latitude: 0, longitude: 0, altitude: 0,
            sensorWidth: 23.5, sensorHeight: 15.7, focalLength: 478,
            imageFormat: 'fits',
            stellariumHost: 'localhost',
            stellariumPort: 8090,
            preferAdvancedSequencer: false
        },

        // Connection state
        indiConnected: false,
        serverReachable: true,
        devices: [],
        selectedCamera: null,
        selectedTelescope: null,
        selectedFocuser: null,

        // WebSocket state
        statusWs: null,
        imageWs: null,
        _statusWsAttempt: 0,
        _imageWsAttempt: 0,
        _statusWsTimer: null,
        _imageWsTimer: null,

        // Toast notifications
        toasts: [],
        _toastId: 0,

        // Equipment rigs (multi-rig profile support)
        rigs: [],
        activeRigId: null,
        rigModalOpen: false,
        newRigName: '',

        // Equipment tab state
        equipCameraChoice: '',
        equipMountChoice: '',
        equipFocuserChoice: '',
        equipFilterChoice: '',
        equipRotatorChoice: '',
        equipFlatChoice: '',
        equipDomeChoice: '',
        equipWeatherChoice: '',
        equipCoolerTarget: -10,
        equipRotatorTarget: 0,
        equipFlatBrightness: 128,
        equipDomeTarget: 0,
        equipCameraInfo: { coolerOn: false, binX: 0, binY: 0, bitDepth: 0 },

        // Rotator
        rotator: { connected: false, name: '', position: null, moving: false, reversed: false },

        // Flat Panel
        flatDevice: { connected: false, name: '', lightOn: false, brightness: 0, coverOpen: false, coverMoving: false },

        // Dome
        dome: { connected: false, name: '', azimuth: null, moving: false, parked: false, slaved: false, shutter: 'Unknown' },

        // Weather
        weather: {
            connected: false, name: '', safe: false,
            temperature: null, humidity: null, dewPoint: null,
            windSpeed: null, windGust: null, pressure: null,
            cloudCover: null, rainRate: null, skyQuality: null
        },

        // Guider (PHD2)
        guider: {
            connected: false, host: 'localhost', port: 4400,
            appState: 'Stopped', guiding: false, calibrating: false,
            paused: false, looping: false, settling: false,
            pixelScale: 0, rmsRA: 0, rmsDec: 0, rmsTotal: 0,
            peakRA: 0, peakDec: 0, stepCount: 0,
            lastAlert: null, lastSettleStatus: null,
            recentSteps: []
        },
        guiderHost: 'localhost',
        guiderPort: 4400,
        guiderSettlePixels: 1.5,
        guiderSettleTime: 10,
        guiderSettleTimeout: 40,
        guiderDitherPx: 5.0,
        guiderDitherRaOnly: false,
        guideChartW: 600,
        guideChartH: 160,
        guideChartScale: 2.0, // arcsec range each direction (auto-expands)
        guiderEquipment: { camera: null, mount: null, auxMount: null, ao: null },

        // PHD2 management state
        phd2Profiles: [],
        phd2SelectedProfileId: 0,
        phd2Exposure: 1000,
        phd2ExposureOptions: [],
        phd2DecMode: 'Auto',
        phd2EquipmentConnected: false,
        phd2Process: { executableConfigured: false, executablePath: '', running: false, weStartedIt: false },
        phd2Install: null,      // /install-info response: { installed, resolvedPath, downloadUrl, os, ... }
        phd2AutoStart: false,   // bound to checkbox, posted to /api/guider/auto-start/{bool}

        // Advanced Sequencer state
        advSeq: {
            doc: { name: 'Untitled', root: null },
            state: 'Idle', lastError: null, abortReason: null,
            errors: [],
            types: [],              // [{type, category, kind}]
            selectedId: null
        },
        advSeqDirty: false,

        // Equipment source picker (INDI vs Alpaca/ASCOM)
        equipSource: 'indi',
        alpaca: {
            discovering: false,
            servers: [],        // [{ host, port, serverName, manufacturer, serverVersion, devices: [...] }]
            connected: {},      // key: host:port:type:num → true
            manualHost: '',
            manualPort: 11111
        },

        // Mosaic planner
        mosaicOpen: false,
        mosaic: {
            req: {
                targetName: 'Target', centreRaHours: 0, centreDecDeg: 0,
                cols: 2, rows: 2,
                panelFovWidthDeg: 1.0, panelFovHeightDeg: 1.0,
                overlapPercent: 20, serpentine: true,
                exposureSeconds: 60, exposureCount: 10,
                slewOverheadSeconds: 30, plateSolveSeconds: 20, perFrameOverheadSeconds: 5
            },
            filterName: '',
            plan: null
        },

        // Sky Atlas filters + altitude chart
        showAtlasFilters: false,
        atlasFilter: { type: '', minMag: null, maxMag: null, minDec: null, maxDec: null },
        atlasResults: [],
        atlasTypes: [],
        altitudeData: null,

        // d3-celestial Sky Viewer (offline, BSD-3-Clause).
        // skyMode = 'horizontal' shows the live sky as seen from the observer's
        // location at the current UTC time (horizon, alt-az); 'equatorial'
        // shows the full celestial sphere (RA/Dec) for planning targets that
        // are currently below the horizon.
        _celestialReady: false,
        _fovLayerId: null,
        _skyTicker: null,                // setInterval handle for datetime refresh
        skyMode: 'horizontal',           // 'horizontal' | 'equatorial'
        skyClock: '',                    // displayed in the toolbar (HH:MM:SS UTC)
        aladinFov: 90,                  // initial FOV in degrees (wide field)
        aladinShowFov: true,             // toggle camera-FOV overlay

        // OpenSeadragon image viewer
        imageViewerOpen: false,
        _osdViewer: null,

        // First-run location setup modal
        showLocationSetup: false,
        locSetup: {
            lat: null, lon: null, alt: 0,
            locating: false, error: null,
            searchQuery: '', searching: false, searchResults: []
        },

        // Full image statistics + histogram
        fullStats: null,
        histogramData: null,

        // Star annotation + visual overlays
        showStarOverlay: false,
        showCrosshair: true,
        showGrid: false,
        showPixelReadout: false,
        lastStars: null,        // { width, height, stars: [{x,y,hfr,...}] }
        hoverPixel: null,       // { x, y, adu, rgb } in source-image coords

        // Manual stretch controls
        stretchAuto: true,
        stretchBlack: 0.0,    // 0..1 normalised (0% = black point at min)
        stretchWhite: 1.0,    // 0..1 normalised (100% = white point at max)
        stretchMid: 0.25,     // MTF midtones coefficient
        _lastRawFrame: null,  // cache: { pixels, width, height, bitDepth, bayerPattern }
        showStretchPanel: false,

        // Temperature history (sensor temp samples for chart)
        tempHistory: [],     // [{t: msEpoch, temp: °C, power: %}]
        _tempLastSample: 0,

        // Chart.js instances (created lazily when canvas is visible)
        _charts: { guide: null, af: null, hfr: null, temp: null, hist: null, alt: null },

        // Auto-Focus
        autoFocus: {
            state: 'idle',
            currentSampleIndex: -1,
            steps: 0,
            lastHfr: 0,
            lastStarCount: 0,
            points: [],
            bestPosition: null,
            bestHfr: null,
            success: null
        },
        afParams: {
            steps: 9,
            stepSize: 50,
            exposureSeconds: 2.0,
            minStars: 5,
            backlashSteps: 0
        },
        afChartW: 600,
        afChartH: 180,

        // Dither settings (mirrors server-side DitherSettings)
        ditherSettings: {
            enabled: false,
            pixels: 5.0,
            everyNFrames: 1,
            raOnly: false,
            settlePixels: 1.5,
            settleTime: 10,
            settleTimeout: 40
        },
        seqDitherExpanded: false,
        _ditherSaveTimer: null,

        // Meridian flip
        mfSettings: {
            enabled: false,
            minutesAfterMeridian: 5,
            pauseBeforeMeridianMinutes: 0,
            recenterAfterFlip: true,
            recenterToleranceArcsec: 30,
            settleSecondsAfterFlip: 5,
            autoFocusAfterFlip: false
        },
        mfState: 'idle',
        mfFlipsCompleted: 0,
        mfLastFlipError: null,
        mfTimeToMeridianMinutes: null,
        mfHourAngleHours: null,
        mfLstHours: null,
        seqMfExpanded: false,
        _mfSaveTimer: null,

        // In-flight request tracking
        _pending: {},
        _previewFetching: false,
        _imageClientId: null,

        init() {
            this.updateClock();
            setInterval(() => this.updateClock(), 1000);
            this.updateFov();

            const saved = localStorage.getItem('nina-settings');
            if (saved) {
                try { Object.assign(this.settings, JSON.parse(saved)); } catch (e) { }
            }

            const nightSaved = localStorage.getItem('nina-night-mode');
            if (nightSaved === 'true') {
                this.nightMode = true;
                document.documentElement.setAttribute('data-theme', 'night');
            }

            this.$watch('settings', () => {
                this.updateFov();
                this.saveSettings();
            });

            this.connectStatusWs();
            this.connectImageWs();
            this.loadSettingsFromServer();
            this.loadDitherSettings();
            this.loadMfSettings();
            this.loadAtlasTypes();
            this.loadRigs();
            this.fetchPhd2ProcessStatus();
            this.fetchPhd2InstallInfo();
        },

        // --- Network helpers ---

        // Fetch with timeout and deduplication
        async apiFetch(url, options = {}) {
            const method = options.method || 'GET';
            const key = method + ' ' + url;

            // Deduplicate: if same request is already in flight, return its promise
            if (this._pending[key]) return this._pending[key];

            const timeout = options.timeout || 15000;
            const controller = new AbortController();
            const timer = setTimeout(() => controller.abort(), timeout);

            const promise = fetch(url, {
                ...options,
                signal: controller.signal
            }).then(resp => {
                clearTimeout(timer);
                delete this._pending[key];

                if (!this.serverReachable) {
                    this.serverReachable = true;
                    this.toast('Server reconnected', 'ok');
                }

                if (!resp.ok) {
                    return resp.text().then(body => {
                        throw new ApiError(resp.status, body);
                    });
                }
                return resp;
            }).catch(err => {
                clearTimeout(timer);
                delete this._pending[key];

                if (err.name === 'AbortError') {
                    this.serverReachable = false;
                    throw new Error('Request timed out');
                }
                if (err instanceof TypeError && err.message.includes('fetch')) {
                    this.serverReachable = false;
                }
                throw err;
            });

            this._pending[key] = promise;
            return promise;
        },

        // POST JSON shorthand
        async apiPost(url, body = null, opts = {}) {
            const options = {
                method: 'POST',
                ...opts
            };
            if (body !== null) {
                options.headers = { 'Content-Type': 'application/json', ...(opts.headers || {}) };
                options.body = JSON.stringify(body);
            }
            return this.apiFetch(url, options);
        },

        // GET JSON shorthand
        async apiGet(url, opts = {}) {
            const resp = await this.apiFetch(url, opts);
            return resp.json();
        },

        // Toast notification (auto-dismiss)
        toast(message, type = 'info', duration = 4000) {
            const id = ++this._toastId;
            this.toasts.push({ id, message, type });
            if (this.toasts.length > 5) this.toasts.shift();
            setTimeout(() => {
                this.toasts = this.toasts.filter(t => t.id !== id);
            }, duration);
        },

        dismissToast(id) {
            this.toasts = this.toasts.filter(t => t.id !== id);
        },

        // --- WebSocket with exponential backoff + jitter ---

        connectStatusWs() {
            if (this._statusWsTimer) {
                clearTimeout(this._statusWsTimer);
                this._statusWsTimer = null;
            }
            if (this.statusWs && this.statusWs.readyState <= 1) {
                this.statusWs.close();
            }

            const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
            const ws = new WebSocket(`${protocol}//${location.host}/ws/status`);

            ws.onopen = () => {
                this._statusWsAttempt = 0;
                this.serverReachable = true;
            };

            ws.onmessage = (evt) => {
                try {
                    this.handleStatusMessage(JSON.parse(evt.data));
                } catch (e) { }
            };

            ws.onclose = () => {
                this.scheduleReconnect('status');
            };

            ws.onerror = () => { };

            this.statusWs = ws;
        },

        connectImageWs() {
            if (this._imageWsTimer) {
                clearTimeout(this._imageWsTimer);
                this._imageWsTimer = null;
            }
            if (this.imageWs && this.imageWs.readyState <= 1) {
                this.imageWs.close();
            }

            const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
            const ws = new WebSocket(`${protocol}//${location.host}/ws/image-stream`);

            ws.binaryType = 'arraybuffer';

            ws.onopen = () => {
                this._imageWsAttempt = 0;
                // Request JPEG mode (universally supported); send {"mode":"raw"} for LZ4+uint16
                ws.send(JSON.stringify({ mode: 'jpeg' }));
            };

            ws.onmessage = (evt) => {
                if (typeof evt.data === 'string') {
                    // Welcome or control message
                    try {
                        const msg = JSON.parse(evt.data);
                        if (msg.type === 'connected') {
                            this._imageClientId = msg.clientId;
                        }
                    } catch (e) { }
                    return;
                }
                this.handleImageFrame(evt.data);
            };

            ws.onclose = () => {
                this.scheduleReconnect('image');
            };

            ws.onerror = () => { };

            this.imageWs = ws;
        },

        scheduleReconnect(type) {
            const isStatus = type === 'status';
            const attempt = isStatus ? this._statusWsAttempt : this._imageWsAttempt;

            // Exponential backoff: 1s, 2s, 4s, 8s, 16s, 30s max
            const baseDelay = Math.min(1000 * Math.pow(2, attempt), 30000);
            // Add jitter: +/- 20%
            const jitter = baseDelay * 0.2 * (Math.random() * 2 - 1);
            const delay = Math.max(500, baseDelay + jitter);

            if (isStatus) {
                this._statusWsAttempt++;
                this._statusWsTimer = setTimeout(() => this.connectStatusWs(), delay);
            } else {
                this._imageWsAttempt++;
                this._imageWsTimer = setTimeout(() => this.connectImageWs(), delay);
            }
        },

        // Render a received binary image frame to the live canvas.
        // In JPEG mode, the frame is a raw JPEG blob — draw via Image element.
        // In raw mode, the frame is: [4B headerLen][header][LZ4 compressed uint16 pixels].
        handleImageFrame(arrayBuffer) {
            this.liveActive = true;

            // Detect format: JPEG files always start with 0xFF 0xD8 (SOI marker)
            const view = new Uint8Array(arrayBuffer);
            if (view.length >= 2 && view[0] === 0xFF && view[1] === 0xD8) {
                this._renderJpegFrame(arrayBuffer);
            } else {
                this._renderRawFrame(arrayBuffer);
            }
        },

        // JPEG mode: create blob URL, draw to canvas via Image element
        _renderJpegFrame(arrayBuffer) {
            const blob = new Blob([arrayBuffer], { type: 'image/jpeg' });
            const url = URL.createObjectURL(blob);

            const img = new Image();
            img.onload = () => {
                const canvas = document.getElementById('liveCanvas');
                if (!canvas) { URL.revokeObjectURL(url); return; }

                // Resize canvas to match image aspect ratio within container
                const container = canvas.parentElement;
                const containerW = container.clientWidth;
                const containerH = container.clientHeight;
                const scale = Math.min(containerW / img.width, containerH / img.height, 1);
                canvas.width = Math.round(img.width * scale);
                canvas.height = Math.round(img.height * scale);

                const ctx = canvas.getContext('2d');
                ctx.imageSmoothingEnabled = true;
                ctx.imageSmoothingQuality = 'high';
                ctx.drawImage(img, 0, 0, canvas.width, canvas.height);

                URL.revokeObjectURL(url);

                // Repaint overlays after each render
                this.redrawOverlay();
            };
            img.onerror = () => URL.revokeObjectURL(url);
            img.src = url;
        },

        // Compute shadow/scale either from manual sliders or auto-stretch (median+MAD)
        _computeStretchParams(pixels, maxVal) {
            if (!this.stretchAuto) {
                const shadow = this.stretchBlack * maxVal;
                const white = Math.max(shadow + 1, this.stretchWhite * maxVal);
                return { shadow, scaleFactor: 1.0 / (white - shadow) };
            }
            // Subsample for speed
            const step = Math.max(1, Math.floor(pixels.length / 200000));
            const sample = new Float32Array(Math.floor(pixels.length / step));
            for (let i = 0, j = 0; j < sample.length; i += step, j++) sample[j] = pixels[i];
            const sorted = sample.slice().sort();
            const median = sorted[Math.floor(sorted.length * 0.5)];
            const deviations = Float32Array.from(sorted, v => Math.abs(v - median)).sort();
            const mad = deviations[Math.floor(deviations.length * 0.5)] * 1.4826;
            const shadow = Math.max(0, median - 2.8 * mad);
            return { shadow, scaleFactor: maxVal > shadow ? 1.0 / (maxVal - shadow) : 1.0 };
        },

        // Re-render the cached last frame with current stretch settings.
        // Called when sliders move.
        applyManualStretch() {
            const f = this._lastRawFrame;
            if (!f) return;
            const { shadow, scaleFactor } = this._computeStretchParams(f.pixels, f.maxVal);
            this._tryRenderWebGL(f.pixels, f.width, f.height, f.bitDepth, f.bayerPattern, shadow, scaleFactor);
        },

        // ----- WebGL renderer (debayer + MTF stretch on GPU) -----
        // State held on the Alpine instance so it survives across frames.
        // _gl, _glProgram, _glLocs, _glTexture, _glCanvas

        _initWebGL() {
            if (this._gl) return true;
            const canvas = document.getElementById('liveCanvas');
            if (!canvas) return false;
            const gl = canvas.getContext('webgl2', { antialias: false, premultipliedAlpha: false });
            if (!gl) {
                console.info('WebGL2 not available, falling back to CPU stretch');
                return false;
            }

            // Vertex shader: clip-space quad
            const vs = `#version 300 es
                in vec2 a_pos;
                out vec2 v_uv;
                void main() {
                    v_uv = vec2((a_pos.x + 1.0) * 0.5, 1.0 - (a_pos.y + 1.0) * 0.5);
                    gl_Position = vec4(a_pos, 0.0, 1.0);
                }`;

            // Fragment shader: sample uint16 R channel + optional 2x2 debayer + MTF stretch.
            // Bayer pattern encoding (matches server-side BayerPatternEnum):
            //   0 = None (mono), 1 = RGGB, 2 = BGGR, 3 = GRBG, 4 = GBRG
            const fs = `#version 300 es
                precision highp float;
                precision highp usampler2D;
                uniform usampler2D u_tex;
                uniform vec2 u_texSize;
                uniform float u_shadow;
                uniform float u_scale;
                uniform float u_mtf;   // typically 0.25
                uniform int u_bayer;   // 0=mono 1=RGGB 2=BGGR 3=GRBG 4=GBRG
                in vec2 v_uv;
                out vec4 fragColor;

                float fetch(vec2 uv) {
                    ivec2 p = ivec2(uv * u_texSize);
                    p = clamp(p, ivec2(0), ivec2(u_texSize) - ivec2(1));
                    return float(texelFetch(u_tex, p, 0).r);
                }

                float stretch(float v) {
                    float n = max(0.0, (v - u_shadow) * u_scale);
                    n = clamp(n, 0.0, 1.0);
                    // MTF: y = (m*x) / ((2m - 1)*x - m + 1) for m∈(0,1)
                    return (u_mtf * n) / ((u_mtf - 1.0) * n - u_mtf + 1.0 + 1e-12);
                }

                void main() {
                    if (u_bayer == 0) {
                        float v = fetch(v_uv);
                        float s = stretch(v);
                        fragColor = vec4(s, s, s, 1.0);
                        return;
                    }
                    // Simple half-resolution debayer: read a 2x2 superpixel and
                    // average the two greens. Works for any pattern.
                    vec2 px = vec2(1.0) / u_texSize;
                    // Snap UV to top-left of 2x2 cell
                    vec2 base = floor(v_uv * (u_texSize * 0.5)) * 2.0 / u_texSize;
                    float p00 = fetch(base);
                    float p10 = fetch(base + vec2(px.x, 0.0));
                    float p01 = fetch(base + vec2(0.0, px.y));
                    float p11 = fetch(base + vec2(px.x, px.y));
                    float r, g, b;
                    if (u_bayer == 1) { r = p00; g = 0.5 * (p10 + p01); b = p11; }       // RGGB
                    else if (u_bayer == 2) { b = p00; g = 0.5 * (p10 + p01); r = p11; }   // BGGR
                    else if (u_bayer == 3) { g = 0.5 * (p00 + p11); r = p10; b = p01; }   // GRBG
                    else /* 4 GBRG */      { g = 0.5 * (p00 + p11); b = p10; r = p01; }
                    fragColor = vec4(stretch(r), stretch(g), stretch(b), 1.0);
                }`;

            const compile = (type, src) => {
                const sh = gl.createShader(type);
                gl.shaderSource(sh, src);
                gl.compileShader(sh);
                if (!gl.getShaderParameter(sh, gl.COMPILE_STATUS)) {
                    console.error('Shader compile error:', gl.getShaderInfoLog(sh));
                    return null;
                }
                return sh;
            };
            const vsObj = compile(gl.VERTEX_SHADER, vs);
            const fsObj = compile(gl.FRAGMENT_SHADER, fs);
            if (!vsObj || !fsObj) return false;
            const prog = gl.createProgram();
            gl.attachShader(prog, vsObj);
            gl.attachShader(prog, fsObj);
            gl.linkProgram(prog);
            if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) {
                console.error('Program link error:', gl.getProgramInfoLog(prog));
                return false;
            }

            // Fullscreen quad
            const buf = gl.createBuffer();
            gl.bindBuffer(gl.ARRAY_BUFFER, buf);
            gl.bufferData(gl.ARRAY_BUFFER,
                new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
            const aPos = gl.getAttribLocation(prog, 'a_pos');
            gl.enableVertexAttribArray(aPos);
            gl.vertexAttribPointer(aPos, 2, gl.FLOAT, false, 0, 0);

            this._gl = gl;
            this._glProgram = prog;
            this._glLocs = {
                tex: gl.getUniformLocation(prog, 'u_tex'),
                texSize: gl.getUniformLocation(prog, 'u_texSize'),
                shadow: gl.getUniformLocation(prog, 'u_shadow'),
                scale: gl.getUniformLocation(prog, 'u_scale'),
                mtf: gl.getUniformLocation(prog, 'u_mtf'),
                bayer: gl.getUniformLocation(prog, 'u_bayer')
            };
            this._glTexture = gl.createTexture();
            console.info('WebGL2 renderer initialised');
            return true;
        },

        _tryRenderWebGL(pixels, width, height, bitDepth, bayerPattern, shadow, scaleFactor) {
            if (!this._initWebGL()) return false;
            const gl = this._gl;
            const canvas = document.getElementById('liveCanvas');
            if (!canvas) return false;

            // Size canvas to container while preserving aspect ratio
            const container = canvas.parentElement;
            const scale = Math.min(container.clientWidth / width, container.clientHeight / height, 1);
            canvas.width = Math.round(width * scale);
            canvas.height = Math.round(height * scale);

            gl.viewport(0, 0, canvas.width, canvas.height);
            gl.useProgram(this._glProgram);

            // Upload pixel data as R16UI texture
            gl.bindTexture(gl.TEXTURE_2D, this._glTexture);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
            try {
                gl.texImage2D(gl.TEXTURE_2D, 0, gl.R16UI, width, height, 0,
                    gl.RED_INTEGER, gl.UNSIGNED_SHORT, pixels);
            } catch (e) {
                console.warn('R16UI texture upload failed:', e);
                return false;
            }

            gl.activeTexture(gl.TEXTURE0);
            gl.uniform1i(this._glLocs.tex, 0);
            gl.uniform2f(this._glLocs.texSize, width, height);
            gl.uniform1f(this._glLocs.shadow, shadow);
            gl.uniform1f(this._glLocs.scale, scaleFactor);
            gl.uniform1f(this._glLocs.mtf, this.stretchMid || 0.25);
            gl.uniform1i(this._glLocs.bayer, bayerPattern | 0);

            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            this.redrawOverlay();
            return true;
        },

        // Raw LZ4 mode: parse header, decompress, auto-stretch, render (WebGL when possible)
        _renderRawFrame(arrayBuffer) {
            const dv = new DataView(arrayBuffer);
            if (arrayBuffer.byteLength < 24) return; // too small

            const headerLen = dv.getInt32(0, true); // little-endian
            const width = dv.getInt32(4, true);
            const height = dv.getInt32(8, true);
            const bitDepth = dv.getInt32(12, true);
            const bayerPattern = dv.getInt32(16, true);
            const uncompressedSize = dv.getInt32(20, true);

            // LZ4 decompression requires lz4.min.js — fallback to REST JPEG if unavailable
            if (typeof LZ4 === 'undefined') {
                // No LZ4 library loaded: fetch latest preview via REST as fallback
                this._fetchPreviewFallback();
                return;
            }

            const compressedData = new Uint8Array(arrayBuffer, 4 + headerLen);
            const decompressed = new Uint8Array(uncompressedSize);

            try {
                LZ4.decompress(compressedData, decompressed);
            } catch (e) {
                console.warn('LZ4 decompress failed, using JPEG fallback');
                this._fetchPreviewFallback();
                return;
            }

            // Convert to uint16 array
            const pixels = new Uint16Array(decompressed.buffer);
            const maxVal = (1 << bitDepth) - 1;

            // Cache the raw frame so manual-stretch slider changes can re-render
            // without waiting for the next capture
            this._lastRawFrame = { pixels, width, height, bitDepth, bayerPattern, maxVal };

            const { shadow, scaleFactor } = this._computeStretchParams(pixels, maxVal);

            // Try WebGL2 path first (GPU does debayer + stretch in microseconds)
            if (this._tryRenderWebGL(pixels, width, height, bitDepth, bayerPattern, shadow, scaleFactor)) {
                return;
            }

            // Render to canvas
            const canvas = document.getElementById('liveCanvas');
            if (!canvas) return;

            const container = canvas.parentElement;
            const containerW = container.clientWidth;
            const containerH = container.clientHeight;
            const scale = Math.min(containerW / width, containerH / height, 1);
            canvas.width = Math.round(width * scale);
            canvas.height = Math.round(height * scale);

            const ctx = canvas.getContext('2d');
            const imgData = ctx.createImageData(width, height);
            const data = imgData.data;

            for (let i = 0; i < pixels.length; i++) {
                const normalized = Math.max(0, (pixels[i] - shadow) * scaleFactor);
                // MTF stretch curve
                const mtf = normalized > 0 ? (normalized * 0.25) / ((0.25 - 1) * normalized + 1) : 0;
                const val = Math.min(255, Math.round(mtf * 255));
                const j = i * 4;
                data[j] = val;
                data[j + 1] = val;
                data[j + 2] = val;
                data[j + 3] = 255;
            }

            // If canvas is scaled, draw via offscreen canvas
            if (scale < 1) {
                const offscreen = document.createElement('canvas');
                offscreen.width = width;
                offscreen.height = height;
                offscreen.getContext('2d').putImageData(imgData, 0, 0);
                ctx.imageSmoothingEnabled = true;
                ctx.imageSmoothingQuality = 'high';
                ctx.drawImage(offscreen, 0, 0, canvas.width, canvas.height);
            } else {
                ctx.putImageData(imgData, 0, 0);
            }

            this.redrawOverlay();
        },

        // Fallback: fetch JPEG preview via REST endpoint
        _fetchPreviewFallback() {
            if (this._previewFetching) return;
            this._previewFetching = true;

            fetch('/api/image/latest/preview')
                .then(resp => {
                    if (!resp.ok) throw new Error('No preview');
                    return resp.blob();
                })
                .then(blob => {
                    const url = URL.createObjectURL(blob);
                    const img = new Image();
                    img.onload = () => {
                        const canvas = document.getElementById('liveCanvas');
                        if (canvas) {
                            const container = canvas.parentElement;
                            const scale = Math.min(container.clientWidth / img.width, container.clientHeight / img.height, 1);
                            canvas.width = Math.round(img.width * scale);
                            canvas.height = Math.round(img.height * scale);
                            const ctx = canvas.getContext('2d');
                            ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
                            this._drawCrosshair(ctx, canvas.width, canvas.height);
                        }
                        URL.revokeObjectURL(url);
                        this._previewFetching = false;
                    };
                    img.onerror = () => { URL.revokeObjectURL(url); this._previewFetching = false; };
                    img.src = url;
                })
                .catch(() => { this._previewFetching = false; });
        },

        // Draw a subtle crosshair in the center of the preview
        _drawCrosshair(ctx, w, h) {
            const cx = w / 2, cy = h / 2;
            const len = Math.min(w, h) * 0.03;
            ctx.save();
            ctx.strokeStyle = 'rgba(255, 50, 50, 0.5)';
            ctx.lineWidth = 1;
            ctx.beginPath();
            ctx.moveTo(cx - len, cy); ctx.lineTo(cx + len, cy);
            ctx.moveTo(cx, cy - len); ctx.lineTo(cx, cy + len);
            ctx.stroke();
            ctx.restore();
        },

        // --- Clock and FOV ---

        updateClock() {
            this.currentTime = new Date().toLocaleTimeString('en-GB');
            // Always keep the Home tab's UTC clock alive too — the Sky-tab
            // ticker only fires when that tab is open, but the Home hero
            // wants the time even on first paint.
            this._updateSkyClock();
        },

        updateFov() {
            const { sensorWidth: sw, sensorHeight: sh, focalLength: fl } = this.settings;
            if (fl > 0) {
                this.fov.width = 2 * Math.atan(sw / (2 * fl)) * (180 / Math.PI);
                this.fov.height = 2 * Math.atan(sh / (2 * fl)) * (180 / Math.PI);
            }
        },

        saveSettings() {
            localStorage.setItem('nina-settings', JSON.stringify(this.settings));
            this.saveSettingsToServer();
        },

        async loadSettingsFromServer() {
            try {
                const data = await this.apiGet('/api/system/profile');
                if (data) {
                    this.settings.indiHost = data.indiHost || 'localhost';
                    this.settings.indiPort = data.indiPort || 7624;
                    this.settings.latitude = data.latitude || 0;
                    this.settings.longitude = data.longitude || 0;
                    this.settings.altitude = data.altitude || 0;
                    this.settings.sensorWidth = data.sensorWidthMm || 23.5;
                    this.settings.sensorHeight = data.sensorHeightMm || 15.7;
                    this.settings.focalLength = data.focalLengthMm || 478;
                    this.settings.imageFormat = data.imageFormat || 'fits';
                    this.settings.preferAdvancedSequencer = !!data.preferAdvancedSequencer;
                    // First time the app boots, honour the user's preferred sequencer flavour.
                    if (!this._sequencerTabBootHandled) {
                        this._sequencerTabBootHandled = true;
                        if (this.settings.preferAdvancedSequencer && this.tab === 'home') {
                            // Don't ambush the user — only switch from the initial 'live' tab
                            // and only if they explicitly opted in. Pre-fetch the doc so the
                            // Adv tab is responsive when they navigate there.
                            this.loadAdvSeq();
                        }
                    }
                    this.updateFov();
                    this._maybeShowLocationSetup();
                }
            } catch (e) { }
        },

        // Show the first-run location modal when both lat and lon are still
        // zero AND the user hasn't already dismissed it once.
        _maybeShowLocationSetup() {
            const isUnset = Math.abs(this.settings.latitude || 0) < 0.01
                         && Math.abs(this.settings.longitude || 0) < 0.01;
            const dismissed = localStorage.getItem('nina-location-prompted') === '1';
            if (isUnset && !dismissed) {
                // Pre-fill the modal with current values (zeros), wait one tick
                this.$nextTick(() => {
                    this.locSetup = {
                        lat: this.settings.latitude || null,
                        lon: this.settings.longitude || null,
                        alt: this.settings.altitude || 0,
                        locating: false, error: null,
                        searchQuery: '', searching: false, searchResults: []
                    };
                    this.showLocationSetup = true;
                });
            }
        },

        async geocodeAddress() {
            const q = (this.locSetup.searchQuery || '').trim();
            if (!q) return;
            this.locSetup.searching = true;
            this.locSetup.error = null;
            this.locSetup.searchResults = [];
            try {
                const data = await this.apiGet(
                    `/api/system/geocode?query=${encodeURIComponent(q)}&limit=8`);
                this.locSetup.searchResults = data.results || [];
                if (this.locSetup.searchResults.length === 0) {
                    this.locSetup.error = 'No matches found';
                }
            } catch (e) {
                this.locSetup.error = 'Address search failed: ' + (e.message || 'unknown');
            } finally {
                this.locSetup.searching = false;
            }
        },

        pickGeocodeResult(r) {
            this.locSetup.lat = +r.latitude.toFixed(4);
            this.locSetup.lon = +r.longitude.toFixed(4);
            this.locSetup.error = null;
            this.locSetup.searchResults = [];
            this.locSetup.searchQuery = r.displayName;
        },

        useBrowserGeolocation() {
            if (!navigator.geolocation) {
                this.locSetup.error = 'Browser geolocation API not available';
                return;
            }
            this.locSetup.locating = true;
            this.locSetup.error = null;
            navigator.geolocation.getCurrentPosition(
                (pos) => {
                    this.locSetup.lat = +pos.coords.latitude.toFixed(4);
                    this.locSetup.lon = +pos.coords.longitude.toFixed(4);
                    if (pos.coords.altitude != null) {
                        this.locSetup.alt = Math.round(pos.coords.altitude);
                    }
                    this.locSetup.locating = false;
                },
                (err) => {
                    this.locSetup.locating = false;
                    this.locSetup.error = err.message || 'Geolocation request denied';
                },
                { enableHighAccuracy: false, timeout: 10000, maximumAge: 60000 }
            );
        },

        async saveLocationAndDismiss() {
            const lat = parseFloat(this.locSetup.lat);
            const lon = parseFloat(this.locSetup.lon);
            if (isNaN(lat) || isNaN(lon) || Math.abs(lat) > 90 || Math.abs(lon) > 180) {
                this.locSetup.error = 'Latitude must be -90..90 and longitude -180..180';
                return;
            }
            this.settings.latitude = lat;
            this.settings.longitude = lon;
            this.settings.altitude = parseFloat(this.locSetup.alt) || 0;
            this.saveSettings();
            await this.saveSettingsToServer();
            localStorage.setItem('nina-location-prompted', '1');
            this.showLocationSetup = false;
            this.toast(`Location saved: ${lat.toFixed(2)}°, ${lon.toFixed(2)}°`, 'ok');
        },

        // remember=true means "don't ask again until they clear localStorage"
        dismissLocationSetup(remember) {
            this.showLocationSetup = false;
            if (remember) {
                localStorage.setItem('nina-location-prompted', '1');
            }
        },

        // ---- d3-celestial Sky Viewer (offline, BSD-3) ----
        // Renders Hipparcos catalogue (mag ≤ 6), Stellarium constellation lines,
        // Milky Way contours, and a Messier+NGC DSO overlay. Everything is
        // bundled under /js/lib/celestial — zero network required.

        initSkyViewer() {
            const el = document.getElementById('celestial-map');
            if (!el || el.clientWidth === 0 || el.clientHeight === 0) {
                setTimeout(() => this.initSkyViewer(), 100);
                return;
            }
            if (typeof Celestial === 'undefined') {
                setTimeout(() => this.initSkyViewer(), 200);
                return;
            }
            if (this._celestialReady) {
                // Use the full container width — CSS clips the over-tall
                // SVG vertically so the circle spans edge-to-edge.
                const size = Math.max(300, el.clientWidth);
                try { Celestial.resize({ width: size }); } catch {}
                return;
            }
            this._buildCelestial(el);
        },

        // Tears down + rebuilds the celestial widget. Called when the user
        // switches projection (live ↔ equatorial) or when the observer
        // location changes via Settings.
        //
        // d3-celestial keeps a fair bit of state on its global Celestial
        // object (projection, transform, current center, zoom level, the
        // SVG/canvas refs it injected). Calling display() a second time
        // on the same container after just `innerHTML = ''` tends to leave
        // the new render in a broken state — the map ends up blank when
        // switching between live ↔ equatorial because the cached transform
        // doesn't get fully reset. Replacing the entire container div
        // forces Celestial to do a clean re-init.
        rebuildSky() {
            this._stopSkyTicker();
            const old = document.getElementById('celestial-map');
            if (!old) return;
            const parent = old.parentElement;
            if (!parent) return;
            const fresh = document.createElement('div');
            fresh.id = 'celestial-map';
            fresh.className = old.className;
            parent.replaceChild(fresh, old);
            this._celestialReady = false;
            // Defer the build one frame so the new div is fully attached
            // and laid out before Celestial measures it.
            requestAnimationFrame(() => this._buildCelestial(fresh));
        },

        _buildCelestial(el) {
            try {
                const isLive = this.skyMode === 'horizontal';
                const lat = this.settings.latitude  || 0;
                const lng = this.settings.longitude || 0;

                // Use the full container width so the circle spans
                // edge-to-edge horizontally. CSS clips the over-tall SVG
                // vertically (overflow:hidden + flex centring), showing
                // only the middle band of the sphere.
                const renderSize = Math.max(300, el.clientWidth);

                // Compute the initial centre so that whatever point we want
                // to be "in the middle of the visible viewport" is passed
                // to Celestial at display() time. Calling rotate() AFTER
                // display() is racy and silently fails near the celestial
                // poles, so we set centre up-front and avoid the exact
                // pole singularity by clamping to ±89.5°.
                const initialCentre = this._computeInitialCentre(lat, lng);

                Celestial.display({
                    container: 'celestial-map',
                    datapath: '/js/lib/celestial/data/',
                    width: renderSize,
                    // Stereographic for both modes; the projection is the same,
                    // what changes is the *frame* (equatorial vs horizontal)
                    // and the centre.
                    projection: 'stereographic',
                    // Always use the 'horizontal' frame so drag rotation
                    // pivots around the local zenith — the celestial-pole
                    // axis (the d3-celestial default) makes drag feel
                    // wildly off-axis everywhere except near the pole
                    // itself. The "Equatorial" toggle no longer changes
                    // the rotation axis; it just changes which graticule
                    // is emphasised and whether the live time ticker runs.
                    transform: 'horizontal',
                    follow: 'center',
                    center: initialCentre,
                    // In horizontal mode we centre on the zenith for that lat/lng
                    // at the current UTC time, then rotate so North is up.
                    geopos: [lat, lng],
                    location: false,
                    zoomlevel: null,
                    zoomextend: 10,
                    interactive: true,
                    form: false,
                    controls: true,
                    advanced: false,
                    disableAnimations: true,
                    background: { fill: '#0b1226', stroke: '#1f2a44', opacity: 1 },
                    stars: { show: true, limit: 6, colors: true,
                             propername: true, propernameType: 'name',
                             propernameStyle: { fill: '#bcd', font: '11px sans-serif', align: 'left', baseline: 'top' },
                             propernameLimit: 2.5,
                             size: 7, exponent: -0.28, data: 'stars.6.json' },
                    dsos: { show: true, limit: 6, names: true,
                            namesType: 'name', nameLimit: 6,
                            namesStyle: { fill: '#cca', font: '10px sans-serif', align: 'left', baseline: 'top' },
                            data: 'dsos.bright.json' },
                    constellations: { show: true,
                                      names: true, namesType: 'iau',
                                      nameStyle: { fill: '#cce', align: 'center', baseline: 'middle', font: '12px sans-serif', opacity: 0.7 },
                                      lines: true,
                                      lineStyle: { stroke: '#cccccc', width: 1.2, opacity: 0.45 } },
                    mw: { show: true, style: { fill: '#ffffff', opacity: 0.12 } },
                    lines: {
                        graticule: { show: true, stroke: '#506080', width: 0.6, opacity: 0.5,
                                     lon: { pos: ['center'], fill: '#aac', font: '10px sans-serif' },
                                     lat: { pos: ['center'], fill: '#aac', font: '10px sans-serif' } },
                        equatorial: { show: !isLive, stroke: '#aaffaa', width: 1, opacity: 0.4 },
                        ecliptic:   { show: true, stroke: '#ffcc66', width: 1, opacity: 0.4 },
                        // Horizon shown in BOTH modes now — in live mode it's the
                        // observer's actual horizon for the current UTC time; in
                        // equatorial mode it still anchors to the geopos so the
                        // user can tell which part of the sky is reachable from
                        // their location right now.
                        horizon:    { show: true, stroke: '#ff5566', width: 1.5, dash: [4, 4], opacity: 0.5 },
                        galactic:   { show: false }
                    },
                    // Filled below-horizon region (the "ground"). Opacity 0.5 so
                    // the sky underneath stays partly visible (you can still see
                    // which constellations are below the horizon, just dimmed).
                    horizon: { show: true, stroke: '#ff5566', fill: '#0a0a14', opacity: 0.5 },
                    // Daylight overlay disabled — d3-celestial's built-in
                    // daytime sky tint is a bright blue that washes out the
                    // stars + constellation lines. For an astrophotography
                    // planning view we always want a night-mode render even
                    // if the sun is up, so the user can find their target now.
                    daylight: { show: false }
                });

                if (isLive) {
                    // Push the current UTC date into Celestial so horizon,
                    // sun and moon positions are accurate. Star RA/Dec don't
                    // change with time so we don't need to re-render those.
                    try { Celestial.date(new Date()); } catch {}
                }
                // Re-apply the centre after display() — belt-and-braces
                // against any internal reset during initial draw. Wrapped
                // in a microtask so it runs after Celestial finishes its
                // synchronous initialisation.
                Promise.resolve().then(() => {
                    try { Celestial.rotate({ center: initialCentre }); } catch {}
                });
                if (isLive) this._startSkyTicker(lat, lng);

                this.setSkyFov();
                this.updateSkyCameraFov();

                // Click-to-pick. In equatorial mode the invert gives RA/Dec
                // directly; in horizontal we still invert to whatever frame
                // Celestial is currently rotated to (so the user can pick a
                // star they see on screen and it ends up with the right
                // celestial coords).
                const svg = el.querySelector('svg');
                if (svg) {
                    svg.addEventListener('click', (e) => {
                        const rect = svg.getBoundingClientRect();
                        const coords = Celestial.mapProjection.invert([e.clientX - rect.left, e.clientY - rect.top]);
                        if (!coords || isNaN(coords[0])) return;
                        let raHours = coords[0] / 15;
                        if (raHours < 0) raHours += 24;
                        const decDeg = coords[1];
                        this.skyTarget = {
                            name: `RA ${raHours.toFixed(3)}h Dec ${decDeg.toFixed(2)}°`,
                            ra: raHours, dec: decDeg, type: 'click', magnitude: ''
                        };
                    });
                }

                window.addEventListener('resize', () => {
                    const size = Math.max(300, el.clientWidth);
                    try { Celestial.resize({ width: size }); } catch {}
                });

                this._celestialReady = true;
                this._updateSkyClock();
                console.log(`d3-celestial ready (${this.skyMode} mode)`);
            } catch (e) {
                console.error('d3-celestial init failed', e);
            }
        },

        // Local-sidereal-time-aware zenith centring. At the observer's location,
        // the zenith's RA = LST and Dec = latitude.
        _centreOnZenith(lat, lng, when) {
            const lstHours = this._localSiderealTime(when, lng);
            const raDeg = (lstHours * 15) % 360;
            try { Celestial.rotate({ center: [raDeg, lat, 0] }); } catch {}
        },

        // Returns the [azimuthDeg, altitudeDeg, orientationDeg] tuple to use
        // as the projection centre on initial load. Both modes use the
        // 'horizontal' transform now, so the centre is always horizontal
        // coords:
        //   - mount connected → mount's current Az/Alt (RA/Dec converted)
        //   - no mount → local zenith
        // Altitude is clamped to ±89.5° because d3.geo's stereographic
        // rotate hits a singularity at exactly ±90° and silently no-ops.
        _computeInitialCentre(lat, lng /*, isLive */) {
            const mountRa = this.mount?.ra;
            const mountDec = this.mount?.dec;
            const mountConnected = this.mount?.connected && mountRa != null && mountDec != null;

            if (mountConnected) {
                const [az, alt] = this._equatorialToHorizontal(mountRa, mountDec, lat, lng, new Date());
                return [az, Math.max(-89.5, Math.min(89.5, alt)), 0];
            }
            // Default to the zenith. Azimuth is somewhat arbitrary right
            // at the pole-of-projection, but 0 (north) gives a sensible
            // compass orientation.
            return [0, 89.5, 0];
        },

        // Convert equatorial (RA in hours, Dec in degrees) to horizontal
        // (azimuth measured east of north, altitude above horizon) for the
        // given observer location and UTC instant. Uses the standard
        // spherical astronomy formulas from Meeus, ch. 13.
        _equatorialToHorizontal(raHours, decDeg, latDeg, lngDeg, when) {
            const lstHours = this._localSiderealTime(when, lngDeg);
            const haDeg = ((lstHours - raHours) * 15 + 360) % 360;
            const haRad = haDeg * Math.PI / 180;
            const decRad = decDeg * Math.PI / 180;
            const latRad = latDeg * Math.PI / 180;
            const sinAlt = Math.sin(decRad) * Math.sin(latRad)
                         + Math.cos(decRad) * Math.cos(latRad) * Math.cos(haRad);
            const alt = Math.asin(Math.max(-1, Math.min(1, sinAlt)));
            const cosAlt = Math.cos(alt);
            // Handle the alt≈90° degenerate case (right at zenith → azimuth
            // undefined; pick north).
            if (Math.abs(cosAlt) < 1e-9) return [0, alt * 180 / Math.PI];
            const sinA = -Math.cos(decRad) * Math.sin(haRad) / cosAlt;
            const cosA = (Math.sin(decRad) - Math.sin(latRad) * sinAlt)
                       / (Math.cos(latRad) * cosAlt);
            const az = Math.atan2(sinA, cosA);
            return [((az * 180 / Math.PI) + 360) % 360, alt * 180 / Math.PI];
        },

        // Meeus low-precision LST (good to a few seconds — fine for orientation).
        _localSiderealTime(utc, longitudeDeg) {
            const jd = utc.getTime() / 86400000 + 2440587.5;
            const t = (jd - 2451545.0) / 36525;
            let gmst = 280.46061837 + 360.98564736629 * (jd - 2451545.0)
                     + 0.000387933 * t * t - (t * t * t) / 38710000;
            gmst = ((gmst % 360) + 360) % 360;
            const lst = (gmst + longitudeDeg + 360) % 360;
            return lst / 15;
        },

        _startSkyTicker(lat, lng) {
            // Refresh the sky every 30 s — stars drift ~0.125° in that window
            // which is barely visible at FOV ≥ 30°. Adjust if you want it
            // smoother (5 s is plenty cheap, but burns more CPU).
            //
            // We deliberately do NOT re-centre on the zenith here — that would
            // override whatever the user panned to. If a mount is connected
            // and reporting coordinates, follow it; otherwise just advance the
            // date layer so star/planet positions stay current.
            this._stopSkyTicker();
            this._skyTicker = setInterval(() => {
                if (this.tab !== 'sky') return; // pause when hidden
                const now = new Date();
                try { Celestial.date(now); } catch {}
                const mountRa = this.mount?.ra;
                const mountDec = this.mount?.dec;
                if (this.mount?.connected && mountRa != null && mountDec != null) {
                    // In live (horizontal) mode the centre is [az, alt], so
                    // convert from the mount's equatorial coords on the fly.
                    // This also tracks the mount across the sky as time
                    // advances (alt/az of a fixed RA/Dec drift with sidereal
                    // time).
                    const [az, alt] = this._equatorialToHorizontal(mountRa, mountDec, lat, lng, now);
                    const altClamped = Math.max(-89.5, Math.min(89.5, alt));
                    try { Celestial.rotate({ center: [az, altClamped, 0] }); } catch {}
                }
                this._updateSkyClock();
            }, 30_000);
            this._updateSkyClock();
        },

        _stopSkyTicker() {
            if (this._skyTicker) { clearInterval(this._skyTicker); this._skyTicker = null; }
        },

        _updateSkyClock() {
            const d = new Date();
            const pad = n => n.toString().padStart(2, '0');
            this.skyClock = `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:${pad(d.getUTCSeconds())}`;
        },

        setSkyFov() {
            if (!this._celestialReady) return;
            // d3-celestial 'zoomlevel' isn't a public FOV setter; the cleanest
            // way to set field is to reconfigure with a new 'center' that
            // implies a zoom. We use Celestial.zoomBy with a heuristic
            // converting degrees → zoom multiplier (max FOV ~180° → zoom 1).
            const fov = Math.max(1, Math.min(180, this.aladinFov || 90));
            try {
                const targetZoom = Math.max(1, 180 / fov);
                Celestial.zoomBy(targetZoom / Celestial.zoomBy());
            } catch {}
        },

        skyGoToMount() {
            if (!this._celestialReady) return;
            const ra  = this.mount?.ra  ?? (this.skyTarget?.ra)  ?? 0;
            const dec = this.mount?.dec ?? (this.skyTarget?.dec) ?? 0;
            const lat = this.settings.latitude  || 0;
            const lng = this.settings.longitude || 0;
            try {
                const [az, alt] = this._equatorialToHorizontal(ra, dec, lat, lng, new Date());
                const altClamped = Math.max(-89.5, Math.min(89.5, alt));
                Celestial.rotate({ center: [az, altClamped, 0] });
            } catch {}
        },

        updateSkyCameraFov() {
            if (!this._celestialReady) return;
            // Camera-FOV rectangle is drawn as a custom JSON object added to the
            // map (d3-celestial supports user-supplied GeoJSON layers).
            // Clear any previous overlay then add the new one centred on the
            // current target.
            if (!this.aladinShowFov || !this.skyTarget) {
                if (this._fovLayerId) try { Celestial.remove(this._fovLayerId); } catch {}
                this._fovLayerId = null;
                Celestial.redraw();
                return;
            }
            const ra  = this.skyTarget.ra ?? this.skyTarget.raHours;
            const dec = this.skyTarget.dec ?? this.skyTarget.decDeg;
            if (ra == null || dec == null) return;
            const w = this.fov.width / 2;
            const h = this.fov.height / 2;
            const cosDec = Math.cos(dec * Math.PI / 180) || 1e-6;
            const raDeg = ra * 15;
            const ring = [
                [raDeg - w / cosDec, dec - h], [raDeg + w / cosDec, dec - h],
                [raDeg + w / cosDec, dec + h], [raDeg - w / cosDec, dec + h],
                [raDeg - w / cosDec, dec - h]
            ];
            const geo = {
                type: 'FeatureCollection',
                features: [{ type: 'Feature', properties: { name: 'FOV' },
                             geometry: { type: 'LineString', coordinates: ring } }]
            };
            try {
                Celestial.add({
                    type: 'line',
                    callback: (err, json) => Celestial.container.selectAll('.fov').remove(),
                    redraw: () => {
                        Celestial.container.selectAll('.fov').remove();
                        Celestial.container.selectAll('.fov')
                            .data(geo.features).enter().append('path')
                            .attr('class', 'fov')
                            .attr('d', Celestial.map(geo))
                            .style('stroke', '#22c55e').style('stroke-width', 2)
                            .style('fill', 'none');
                    }
                });
                Celestial.redraw();
            } catch (e) { console.warn('FOV overlay failed', e); }
        },

        // Pixel readout: convert mouse event coords to source-image coords +
        // read raw ADU when available, otherwise sample the canvas RGB.
        onPreviewMouseMove(evt) {
            if (!this.showPixelReadout) {
                this.hoverPixel = null;
                return;
            }
            const live = document.getElementById('liveCanvas');
            if (!live || !live.width) { this.hoverPixel = null; return; }
            const rect = live.getBoundingClientRect();
            const cx = evt.clientX - rect.left;
            const cy = evt.clientY - rect.top;
            if (cx < 0 || cy < 0 || cx >= rect.width || cy >= rect.height) {
                this.hoverPixel = null;
                return;
            }
            // Mouse → canvas pixel coords
            const canvasX = Math.floor(cx * live.width / rect.width);
            const canvasY = Math.floor(cy * live.height / rect.height);

            // Source-image coords (raw mode) or just canvas coords (JPEG mode)
            let srcX = canvasX, srcY = canvasY, adu = null, rgb = null;
            if (this._lastRawFrame) {
                const f = this._lastRawFrame;
                srcX = Math.floor(canvasX * f.width / live.width);
                srcY = Math.floor(canvasY * f.height / live.height);
                if (srcX >= 0 && srcX < f.width && srcY >= 0 && srcY < f.height) {
                    adu = f.pixels[srcY * f.width + srcX];
                }
            } else {
                // JPEG path: read RGB from canvas at the displayed pixel
                try {
                    const gl = this._gl;
                    if (gl) {
                        // WebGL renders aren't readable via 2D getImageData; skip
                    } else {
                        const ctx = live.getContext('2d');
                        const p = ctx.getImageData(canvasX, canvasY, 1, 1).data;
                        rgb = `RGB ${p[0]},${p[1]},${p[2]}`;
                    }
                } catch (e) { /* security or context type mismatch */ }
            }
            this.hoverPixel = { x: srcX, y: srcY, adu, rgb };
        },

        // ---- Visual overlays (stars, crosshair, grid, pixel readout) ----

        async toggleStarOverlay() {
            this.showStarOverlay = !this.showStarOverlay;
            if (this.showStarOverlay) {
                try {
                    this.lastStars = await this.apiGet('/api/image/latest/stars?maxStars=300');
                } catch (e) {
                    this.toast('Star detection failed: ' + e.message, 'error');
                    this.showStarOverlay = false;
                    return;
                }
            }
            this.redrawOverlay();
        },

        // Re-paint all overlays into the overlayCanvas, sized to match liveCanvas
        redrawOverlay() {
            const live = document.getElementById('liveCanvas');
            const ovr = document.getElementById('overlayCanvas');
            if (!live || !ovr) return;
            if (ovr.width !== live.width || ovr.height !== live.height) {
                ovr.width = live.width || 1;
                ovr.height = live.height || 1;
            }
            const ctx = ovr.getContext('2d');
            ctx.clearRect(0, 0, ovr.width, ovr.height);
            if (this.showStarOverlay && this.lastStars) this._drawStarsOnOverlay(ctx, ovr.width, ovr.height);
            if (this.showCrosshair) this._drawCrosshairOnOverlay(ctx, ovr.width, ovr.height);
            if (this.showGrid) this._drawGridOnOverlay(ctx, ovr.width, ovr.height);
        },

        _drawStarsOnOverlay(ctx, w, h) {
            if (!this.lastStars) return;
            const sx = w / this.lastStars.width;
            const sy = h / this.lastStars.height;
            ctx.strokeStyle = 'rgba(255, 235, 59, 0.85)';
            ctx.lineWidth = 1;
            ctx.font = '10px sans-serif';
            ctx.fillStyle = 'rgba(255, 235, 59, 0.85)';
            for (const s of this.lastStars.stars) {
                const cx = s.x * sx;
                const cy = s.y * sy;
                const r = Math.max(4, s.hfr * 1.4 * Math.min(sx, sy));
                ctx.beginPath();
                ctx.arc(cx, cy, r, 0, 2 * Math.PI);
                ctx.stroke();
            }
            // Annotate brightest 20 stars with HFR value
            const top = (this.lastStars.stars || []).slice(0, 20);
            for (const s of top) {
                ctx.fillText(s.hfr.toFixed(2), s.x * sx + 6, s.y * sy - 6);
            }
        },

        _drawCrosshairOnOverlay(ctx, w, h) {
            const cx = w / 2, cy = h / 2;
            const len = Math.min(w, h) * 0.04;
            ctx.strokeStyle = 'rgba(255, 80, 80, 0.6)';
            ctx.lineWidth = 1;
            ctx.beginPath();
            ctx.moveTo(cx - len, cy); ctx.lineTo(cx + len, cy);
            ctx.moveTo(cx, cy - len); ctx.lineTo(cx, cy + len);
            ctx.stroke();
        },

        _drawGridOnOverlay(ctx, w, h) {
            ctx.strokeStyle = 'rgba(255, 255, 255, 0.18)';
            ctx.lineWidth = 1;
            for (let i = 1; i < 3; i++) {
                ctx.beginPath();
                ctx.moveTo(0, h * i / 3); ctx.lineTo(w, h * i / 3);
                ctx.moveTo(w * i / 3, 0); ctx.lineTo(w * i / 3, h);
                ctx.stroke();
            }
        },

        // ---- Image history thumbnails ----

        // Snapshot the current live canvas to a small JPEG dataURL
        _captureThumbnail() {
            try {
                const src = document.getElementById('liveCanvas');
                if (!src || src.width === 0 || src.height === 0) return null;
                const tw = 100;
                const th = Math.round(tw * src.height / src.width);
                const off = document.createElement('canvas');
                off.width = tw; off.height = th;
                const ctx = off.getContext('2d');
                ctx.drawImage(src, 0, 0, tw, th);
                return off.toDataURL('image/jpeg', 0.65);
            } catch (e) {
                console.warn('thumbnail capture failed:', e);
                return null;
            }
        },

        clearImageHistory() {
            this.imageHistory = [];
            this.$nextTick(() => this.updateHfrChart());
        },

        openHistoryItem(item) {
            // No full image archive (yet) — best we can do is open OSD with the
            // current latest preview if the user clicked the most recent thumb,
            // otherwise show the cached thumbnail.
            if (item === this.imageHistory[0]) {
                this.openImageViewer();
                return;
            }
            if (item.thumb) {
                // Show standalone thumb in OSD modal
                this.imageViewerOpen = true;
                this.$nextTick(() => {
                    if (this._osdViewer) { try { this._osdViewer.destroy(); } catch (e) {} }
                    this._osdViewer = OpenSeadragon({
                        id: 'osd-viewer',
                        tileSources: { type: 'image', url: item.thumb },
                        showNavigationControl: false,
                        showNavigator: false,
                        visibilityRatio: 0.5,
                        minZoomImageRatio: 0.5,
                        maxZoomPixelRatio: 4.0,
                        animationTime: 0.3
                    });
                });
            }
        },

        // ---- Image statistics + histogram ----

        async loadFullStats(withStars = false) {
            try {
                const url = '/api/image/latest/stats' + (withStars ? '?withStars=true' : '');
                const [stats, hist] = await Promise.all([
                    this.apiGet(url),
                    this.apiGet('/api/image/latest/histogram?bins=256').catch(() => null)
                ]);
                this.fullStats = stats;
                this.histogramData = hist;
                this.$nextTick(() => this.updateHistChart());
            } catch (e) {
                this.toast('Failed to load image stats: ' + e.message, 'error');
            }
        },

        updateHistChart() {
            if (!this.histogramData) return;
            const c = this._ensureChart('histChart', 'hist', 'bar', () => ({
                type: 'bar',
                data: {
                    labels: [],
                    datasets: [{
                        label: 'pixels',
                        data: [],
                        backgroundColor: '#64b5f6',
                        borderColor: '#64b5f6',
                        borderWidth: 0,
                        barPercentage: 1.0,
                        categoryPercentage: 1.0
                    }]
                },
                options: {
                    responsive: true, maintainAspectRatio: false, animation: false,
                    plugins: { legend: { display: false } },
                    scales: {
                        x: { display: false },
                        y: { type: 'logarithmic', display: false, beginAtZero: true }
                    }
                }
            }));
            if (!c) return;
            const values = this.histogramData.values || [];
            c.data.labels = values.map((_, i) => i);
            c.data.datasets[0].data = values.map(v => Math.max(1, v));
            c.update('none');
        },

        // ---- OpenSeadragon image viewer ----
        openImageViewer() {
            this.imageViewerOpen = true;
            this.$nextTick(() => this._initOsdViewer());
        },

        closeImageViewer() {
            this.imageViewerOpen = false;
            if (this._osdViewer) {
                try { this._osdViewer.destroy(); } catch (e) { }
                this._osdViewer = null;
            }
        },

        reloadImageViewer() {
            if (this._osdViewer) {
                this._osdViewer.open({
                    type: 'image',
                    url: '/api/image/latest/preview?t=' + Date.now()
                });
            }
        },

        _initOsdViewer() {
            if (typeof OpenSeadragon === 'undefined') {
                console.warn('OpenSeadragon not loaded');
                this.toast('Image viewer library not ready', 'error');
                return;
            }
            if (this._osdViewer) {
                try { this._osdViewer.destroy(); } catch (e) { }
                this._osdViewer = null;
            }
            this._osdViewer = OpenSeadragon({
                id: 'osd-viewer',
                tileSources: {
                    type: 'image',
                    url: '/api/image/latest/preview?t=' + Date.now()
                },
                showNavigationControl: false,
                showNavigator: true,
                navigatorPosition: 'BOTTOM_RIGHT',
                navigatorHeight: '90px',
                navigatorWidth: '120px',
                visibilityRatio: 0.5,
                minZoomImageRatio: 0.8,
                maxZoomPixelRatio: 4.0,
                gestureSettingsMouse: { clickToZoom: false },
                gestureSettingsTouch: { clickToZoom: false },
                animationTime: 0.4,
                springStiffness: 8
            });
        },

        // When a sky target is selected via search, also re-center the celestial
        // map on it. The FOV overlay redraws automatically since it reads from
        // this.skyTarget on each updateSkyCameraFov() call.
        _goToSelectedTarget() {
            if (!this._celestialReady || !this.skyTarget) return;
            const ra = this.skyTarget.ra ?? this.skyTarget.raHours;
            const dec = this.skyTarget.dec ?? this.skyTarget.decDeg;
            if (ra == null || dec == null) return;
            try { Celestial.rotate({ center: [ra * 15, dec, 0] }); } catch {}
            this.updateSkyCameraFov();
        },

        async loadMfSettings() {
            try {
                const data = await this.apiGet('/api/meridianflip/settings');
                if (data) {
                    this.mfSettings = {
                        enabled: !!data.enabled,
                        minutesAfterMeridian: data.minutesAfterMeridian ?? 5,
                        pauseBeforeMeridianMinutes: data.pauseBeforeMeridianMinutes ?? 0,
                        recenterAfterFlip: data.recenterAfterFlip !== false,
                        recenterToleranceArcsec: data.recenterToleranceArcsec ?? 30,
                        settleSecondsAfterFlip: data.settleSecondsAfterFlip ?? 5,
                        autoFocusAfterFlip: !!data.autoFocusAfterFlip
                    };
                }
            } catch (e) { }
        },

        saveMfSettings() {
            if (this._mfSaveTimer) clearTimeout(this._mfSaveTimer);
            this._mfSaveTimer = setTimeout(async () => {
                try {
                    await this.apiPost('/api/meridianflip/settings', null, {
                        method: 'PUT',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify(this.mfSettings)
                    });
                } catch (e) {
                    this.toast('Failed to save meridian flip settings', 'error');
                }
            }, 400);
        },

        async abortMeridianFlip() {
            try {
                await this.apiPost('/api/meridianflip/abort');
                this.toast('Meridian flip aborted', 'warn');
            } catch (e) { this.toast('Abort failed', 'error'); }
        },

        formatMinutes(min) {
            if (min === null || min === undefined) return '--';
            if (min < 0) min = 0;
            const h = Math.floor(min / 60);
            const m = Math.floor(min % 60);
            if (h > 0) return `${h}h ${m}m`;
            return `${m}m`;
        },

        // ---- Chart.js helpers ----

        // Common dark theme for all charts
        _chartTheme() {
            return {
                color: '#aaa',
                grid: 'rgba(255,255,255,0.08)',
                tick: '#888',
                titleColor: '#ddd'
            };
        },

        _ensureChart(refName, key, type, makeConfig) {
            if (this._charts[key]) return this._charts[key];
            if (typeof Chart === 'undefined') return null;
            const canvas = this.$refs[refName];
            if (!canvas) return null;
            this._charts[key] = new Chart(canvas, makeConfig());
            return this._charts[key];
        },

        // Guider chart: RA (red) + Dec (blue) vs sample index
        updateGuideChart() {
            const t = this._chartTheme();
            const c = this._ensureChart('guideChart', 'guide', 'line', () => ({
                type: 'line',
                data: {
                    labels: [],
                    datasets: [
                        { label: 'RA', data: [], borderColor: '#e57373', backgroundColor: 'transparent',
                          tension: 0.2, pointRadius: 0, borderWidth: 1.5 },
                        { label: 'Dec', data: [], borderColor: '#64b5f6', backgroundColor: 'transparent',
                          tension: 0.2, pointRadius: 0, borderWidth: 1.5 }
                    ]
                },
                options: {
                    responsive: true, maintainAspectRatio: false,
                    animation: false,
                    plugins: { legend: { display: false } },
                    scales: {
                        x: { display: false, grid: { color: t.grid } },
                        y: { ticks: { color: t.tick, font: { size: 10 } }, grid: { color: t.grid },
                             title: { display: true, text: 'arcsec', color: t.tick, font: { size: 10 } } }
                    }
                }
            }));
            if (!c) return;
            const steps = this.guider.recentSteps || [];
            c.data.labels = steps.map((_, i) => i);
            c.data.datasets[0].data = steps.map(s => s.ra);
            c.data.datasets[1].data = steps.map(s => s.dec);
            c.update('none');
        },

        // Auto-Focus V-curve: HFR vs Position, scatter + fit overlay
        updateAfChart() {
            const t = this._chartTheme();
            const c = this._ensureChart('afChart', 'af', 'scatter', () => ({
                type: 'scatter',
                data: {
                    datasets: [
                        { label: 'Samples', data: [], pointBackgroundColor: '#64b5f6', pointRadius: 5 },
                        { label: 'Fit', data: [], showLine: true, borderColor: '#aaa',
                          borderDash: [4, 3], backgroundColor: 'transparent', pointRadius: 0, borderWidth: 1 },
                        { label: 'Best', data: [], pointBackgroundColor: '#4caf50', pointRadius: 8,
                          pointStyle: 'crossRot', pointBorderWidth: 2 }
                    ]
                },
                options: {
                    responsive: true, maintainAspectRatio: false, animation: false,
                    plugins: { legend: { display: false } },
                    scales: {
                        x: { ticks: { color: t.tick, font: { size: 10 } }, grid: { color: t.grid },
                             title: { display: true, text: 'Focuser position', color: t.tick, font: { size: 10 } } },
                        y: { beginAtZero: true, ticks: { color: t.tick, font: { size: 10 } }, grid: { color: t.grid },
                             title: { display: true, text: 'HFR', color: t.tick, font: { size: 10 } } }
                    }
                }
            }));
            if (!c) return;
            const pts = this.autoFocus.points || [];
            c.data.datasets[0].data = pts.map(p => ({ x: p.position, y: p.hfr }));
            // Generate fitted parabola curve if we have a best position
            if (this.autoFocus.bestPosition && pts.length >= 3) {
                const bestX = this.autoFocus.bestPosition;
                const bestY = this.autoFocus.bestHfr || 0;
                const minP = Math.min(...pts.map(p => p.position));
                const maxP = Math.max(...pts.map(p => p.position));
                const halfRange = Math.max(bestX - minP, maxP - bestX, 1);
                const leftMax = pts.filter(p => p.position < bestX).reduce((m, p) => Math.max(m, p.hfr), 0);
                const rightMax = pts.filter(p => p.position > bestX).reduce((m, p) => Math.max(m, p.hfr), 0);
                const a = Math.max(0, (Math.max(leftMax, rightMax) - bestY) / (halfRange * halfRange));
                const fit = [];
                for (let i = 0; i <= 40; i++) {
                    const x = minP + (maxP - minP) * i / 40;
                    fit.push({ x, y: a * (x - bestX) * (x - bestX) + bestY });
                }
                c.data.datasets[1].data = fit;
                c.data.datasets[2].data = [{ x: bestX, y: bestY }];
            } else {
                c.data.datasets[1].data = [];
                c.data.datasets[2].data = [];
            }
            c.update('none');
        },

        // HFR History: HFR + StarCount on two y-axes, indexed by image #
        updateHfrChart() {
            const t = this._chartTheme();
            const c = this._ensureChart('hfrChart', 'hfr', 'line', () => ({
                type: 'line',
                data: {
                    labels: [],
                    datasets: [
                        { label: 'HFR', data: [], borderColor: '#ffb74d', backgroundColor: 'transparent',
                          yAxisID: 'y', tension: 0.2, pointRadius: 2, borderWidth: 1.5 },
                        { label: 'Stars', data: [], borderColor: '#81c784', backgroundColor: 'transparent',
                          yAxisID: 'y1', tension: 0.2, pointRadius: 2, borderWidth: 1.5 }
                    ]
                },
                options: {
                    responsive: true, maintainAspectRatio: false, animation: false,
                    plugins: { legend: { labels: { color: t.color, font: { size: 10 } } } },
                    scales: {
                        x: { ticks: { color: t.tick, font: { size: 10 } }, grid: { color: t.grid } },
                        y: { position: 'left', beginAtZero: true,
                             ticks: { color: '#ffb74d', font: { size: 10 } }, grid: { color: t.grid },
                             title: { display: true, text: 'HFR', color: '#ffb74d', font: { size: 10 } } },
                        y1: { position: 'right', beginAtZero: true,
                              ticks: { color: '#81c784', font: { size: 10 } }, grid: { display: false },
                              title: { display: true, text: 'Stars', color: '#81c784', font: { size: 10 } } }
                    }
                }
            }));
            if (!c) return;
            // imageHistory is newest-first → reverse for chronological order
            const hist = (this.imageHistory || []).slice().reverse();
            c.data.labels = hist.map((_, i) => i + 1);
            c.data.datasets[0].data = hist.map(h => parseFloat(h.hfr) || 0);
            c.data.datasets[1].data = hist.map(h => parseInt(h.stars) || 0);
            c.update('none');
        },

        // Temperature chart: sensor temp + cooler power vs time
        updateTempChart() {
            const t = this._chartTheme();
            const c = this._ensureChart('tempChart', 'temp', 'line', () => ({
                type: 'line',
                data: {
                    labels: [],
                    datasets: [
                        { label: 'Temp', data: [], borderColor: '#64b5f6', backgroundColor: 'transparent',
                          yAxisID: 'y', tension: 0.3, pointRadius: 0, borderWidth: 1.5 },
                        { label: 'Power', data: [], borderColor: '#ef5350', backgroundColor: 'transparent',
                          yAxisID: 'y1', tension: 0.3, pointRadius: 0, borderWidth: 1, borderDash: [3, 2] }
                    ]
                },
                options: {
                    responsive: true, maintainAspectRatio: false, animation: false,
                    plugins: { legend: { display: false } },
                    scales: {
                        x: { display: false },
                        y: { position: 'left', ticks: { color: '#64b5f6', font: { size: 9 } }, grid: { color: t.grid },
                             title: { display: true, text: '°C', color: '#64b5f6', font: { size: 9 } } },
                        y1: { position: 'right', min: 0, max: 100, ticks: { color: '#ef5350', font: { size: 9 } },
                              grid: { display: false },
                              title: { display: true, text: '%', color: '#ef5350', font: { size: 9 } } }
                    }
                }
            }));
            if (!c) return;
            const samples = this.tempHistory;
            c.data.labels = samples.map((_, i) => i);
            c.data.datasets[0].data = samples.map(s => s.temp);
            c.data.datasets[1].data = samples.map(s => s.power);
            c.update('none');
        },

        // ---- Equipment rigs ----

        async loadRigs() {
            try {
                const data = await this.apiGet('/api/equipment/rigs');
                this.rigs = data.rigs || [];
                this.activeRigId = data.activeId;
                // Pre-fill the device choice dropdowns with the active rig's
                // selections so the user doesn't have to re-select.
                const active = this.rigs.find(r => r.id === this.activeRigId);
                if (active) this._applyRigToChoices(active);
            } catch (e) { /* server may be unreachable on first load */ }
        },

        _applyRigToChoices(rig) {
            this.equipCameraChoice = rig.camera || '';
            this.equipMountChoice = rig.telescope || '';
            this.equipFocuserChoice = rig.focuser || '';
            this.equipFilterChoice = rig.filterWheel || '';
            this.equipRotatorChoice = rig.rotator || '';
            this.equipFlatChoice = rig.flatDevice || '';
            this.equipDomeChoice = rig.dome || '';
            this.equipWeatherChoice = rig.weather || '';
            if (rig.coolerTargetTemperature != null) this.equipCoolerTarget = rig.coolerTargetTemperature;
            if (rig.focuserStepSize) this.focusStep = rig.focuserStepSize;
            if (rig.focalLengthMm) {
                this.settings.focalLength = rig.focalLengthMm;
                this.updateFov();
            }
            if (rig.phd2Host) this.guiderHost = rig.phd2Host;
            if (rig.phd2Port) this.guiderPort = rig.phd2Port;
        },

        async switchRig(id) {
            try {
                await this.apiPost(`/api/equipment/rigs/${id}/activate`);
                this.activeRigId = id;
                const r = this.rigs.find(x => x.id === id);
                if (r) {
                    this._applyRigToChoices(r);
                    this.toast(`Switched to rig: ${r.name}`, 'ok');
                }
            } catch (e) {
                this.toast('Switch rig failed: ' + e.message, 'error');
            }
        },

        async createNewRig() {
            if (!this.newRigName) return;
            try {
                const rig = await this.apiPost('/api/equipment/rigs', { name: this.newRigName });
                this.rigs.push(rig);
                this.newRigName = '';
                this.toast(`Created rig: ${rig.name}`, 'ok');
            } catch (e) { this.toast('Create failed', 'error'); }
        },

        async cloneActiveRig() {
            const name = prompt('Name for the new rig (copy of active):');
            if (!name) return;
            try {
                const rig = await this.apiPost('/api/equipment/rigs/clone', { newName: name });
                this.rigs.push(rig);
                this.toast(`Cloned to: ${rig.name}`, 'ok');
            } catch (e) { this.toast('Clone failed', 'error'); }
        },

        async renameRig(id, newName) {
            const rig = this.rigs.find(r => r.id === id);
            if (!rig) return;
            rig.name = newName;
            await this.saveRig(rig);
        },

        // Debounced PUT — covers focal length / cooler target / etc. inline edits
        _rigSaveTimers: {},
        saveRig(rig) {
            if (!rig?.id) return;
            if (this._rigSaveTimers[rig.id]) clearTimeout(this._rigSaveTimers[rig.id]);
            this._rigSaveTimers[rig.id] = setTimeout(async () => {
                try {
                    await this.apiPost(`/api/equipment/rigs/${rig.id}`, null, {
                        method: 'PUT',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify(rig)
                    });
                    // If saving the active rig, push focal length into the
                    // settings cache so updateFov() picks it up immediately.
                    if (rig.id === this.activeRigId) {
                        this.settings.focalLength = rig.focalLengthMm;
                        this.updateFov();
                    }
                } catch (e) {
                    this.toast('Failed to save rig: ' + e.message, 'error');
                }
            }, 400);
        },

        // ---- Per-rig filter offsets ----
        setFilterOffset(rig, filterName, valueStr) {
            const v = parseInt(valueStr, 10);
            if (isNaN(v)) return;
            rig.filterOffsets = rig.filterOffsets || {};
            rig.filterOffsets[filterName] = v;
            this.saveRig(rig);
        },
        addFilterOffset(rig) {
            const nameInput = document.getElementById('newFilter-' + rig.id);
            const offsetInput = document.getElementById('newOffset-' + rig.id);
            const name = nameInput?.value?.trim();
            const offset = parseInt(offsetInput?.value, 10);
            if (!name) { this.toast('Filter name required', 'warn'); return; }
            if (isNaN(offset)) { this.toast('Offset must be an integer', 'warn'); return; }
            rig.filterOffsets = rig.filterOffsets || {};
            rig.filterOffsets[name] = offset;
            this.saveRig(rig);
            nameInput.value = '';
            offsetInput.value = '';
        },
        removeFilterOffset(rig, filterName) {
            if (!rig.filterOffsets) return;
            delete rig.filterOffsets[filterName];
            this.saveRig(rig);
        },

        async deleteRig(id) {
            if (this.rigs.length <= 1) {
                this.toast('Cannot delete the last rig', 'warn');
                return;
            }
            if (!confirm('Delete this rig? This cannot be undone.')) return;
            try {
                await this.apiPost(`/api/equipment/rigs/${id}`, null, { method: 'DELETE' });
                this.rigs = this.rigs.filter(r => r.id !== id);
                if (this.activeRigId === id) this.activeRigId = this.rigs[0]?.id;
                this.toast('Rig deleted', 'warn');
            } catch (e) { this.toast('Delete failed', 'error'); }
        },

        async saveCurrentSelectionsToRig() {
            const rig = this.rigs.find(r => r.id === this.activeRigId);
            if (!rig) return;
            const updated = {
                ...rig,
                camera: this.equipCameraChoice || rig.camera,
                telescope: this.equipMountChoice || rig.telescope,
                focuser: this.equipFocuserChoice || rig.focuser,
                filterWheel: this.equipFilterChoice || rig.filterWheel,
                rotator: this.equipRotatorChoice || rig.rotator,
                flatDevice: this.equipFlatChoice || rig.flatDevice,
                dome: this.equipDomeChoice || rig.dome,
                weather: this.equipWeatherChoice || rig.weather,
                coolerTargetTemperature: this.equipCoolerTarget,
                focuserStepSize: this.focusStep,
                focalLengthMm: this.settings.focalLength,
                phd2Host: this.guiderHost,
                phd2Port: this.guiderPort
            };
            try {
                await this.apiPost(`/api/equipment/rigs/${rig.id}`, null, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(updated)
                });
                Object.assign(rig, updated);
                this.toast(`Saved selections to "${rig.name}"`, 'ok');
            } catch (e) { this.toast('Save failed: ' + e.message, 'error'); }
        },

        async loadDitherSettings() {
            try {
                const data = await this.apiGet('/api/sequence/dither');
                if (data) {
                    this.ditherSettings = {
                        enabled: !!data.enabled,
                        pixels: data.pixels ?? 5.0,
                        everyNFrames: data.everyNFrames ?? 1,
                        raOnly: !!data.raOnly,
                        settlePixels: data.settlePixels ?? 1.5,
                        settleTime: data.settleTime ?? 10,
                        settleTimeout: data.settleTimeout ?? 40
                    };
                }
            } catch (e) { /* server may not be reachable yet */ }
        },

        // Debounced PUT — fires 400ms after the last edit
        saveDitherSettings() {
            if (this._ditherSaveTimer) clearTimeout(this._ditherSaveTimer);
            this._ditherSaveTimer = setTimeout(async () => {
                try {
                    await this.apiPost('/api/sequence/dither', null, {
                        method: 'PUT',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify(this.ditherSettings)
                    });
                } catch (e) {
                    this.toast('Failed to save dither settings', 'error');
                }
            }, 400);
        },

        async saveSettingsToServer() {
            try {
                await this.apiPost('/api/system/profile', null, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        latitude: this.settings.latitude,
                        longitude: this.settings.longitude,
                        altitude: this.settings.altitude,
                        sensorWidthMm: this.settings.sensorWidth,
                        sensorHeightMm: this.settings.sensorHeight,
                        focalLengthMm: this.settings.focalLength,
                        indiHost: this.settings.indiHost,
                        indiPort: this.settings.indiPort,
                        defaultExposure: this.exposure,
                        defaultGain: this.gain,
                        defaultBinning: parseInt(this.binning),
                        imageFormat: this.settings.imageFormat,
                        preferAdvancedSequencer: this.settings.preferAdvancedSequencer
                    })
                });
            } catch (e) { }
        },

        toggleNightMode() {
            this.nightMode = !this.nightMode;
            document.documentElement.setAttribute('data-theme', this.nightMode ? 'night' : 'dark');
            localStorage.setItem('nina-night-mode', this.nightMode.toString());
        },

        // --- INDI Connection ---

        async connectIndi() {
            this.saveSettings();
            try {
                const resp = await this.apiPost('/api/indi/connect', {
                    host: this.settings.indiHost,
                    port: this.settings.indiPort
                });
                if (resp.ok) {
                    this.indiConnected = true;
                    this.toast('Connected to INDI server', 'ok');
                    setTimeout(() => this.refreshDevices(), 2000);
                }
            } catch (e) {
                this.indiConnected = false;
                // Prefer the structured 'detail' the backend ships in the JSON body;
                // fall back to the raw error string for non-JSON failures.
                let msg = e.message;
                try {
                    const body = JSON.parse(e.body || '{}');
                    if (body.detail) msg = body.detail;
                } catch { /* not JSON; keep raw message */ }
                this.toast('INDI connection failed: ' + msg, 'error');
            }
        },

        async disconnectIndi() {
            try { await this.apiPost('/api/indi/disconnect'); } catch (e) { }
            this.indiConnected = false;
            this.toast('Disconnected from INDI', 'warn');
        },

        async refreshDevices() {
            try {
                const data = await this.apiGet('/api/equipment/devices');
                this.devices = data.devices || [];
            } catch (e) {
                this.toast('Failed to refresh devices', 'error');
            }
        },

        // --- Camera ---

        async selectCamera(name) {
            try {
                await this.apiPost(`/api/camera/select/${encodeURIComponent(name)}`);
                await this.apiPost('/api/camera/connect');
                this.selectedCamera = name;
                this.toast('Camera connected: ' + name, 'ok');
            } catch (e) {
                this.toast('Camera connection failed: ' + e.message, 'error');
            }
        },

        async capture() {
            this.capturing = true;
            try {
                const resp = await this.apiPost('/api/camera/capture', {
                    exposure: this.exposure,
                    gain: this.gain,
                    binning: parseInt(this.binning),
                    filter: null
                }, { timeout: (this.exposure + 30) * 1000 });

                const data = await resp.json();
                this.liveActive = true;
                this.sessionCaptures++;
                if (data.stats) {
                    this.stats.starCount = data.stats.starCount;
                    this.stats.hfr = data.stats.hfr?.toFixed(2);
                    this.stats.mean = data.stats.mean?.toFixed(0);
                }
                this.imageHistory.unshift({
                    id: 'h-' + Date.now() + '-' + Math.random().toString(36).slice(2, 7),
                    time: new Date().toLocaleTimeString('en-GB'),
                    exposure: this.exposure,
                    gain: this.gain,
                    filter: this.filterWheel.connected ? this.filterWheel.currentFilter : null,
                    stars: data.stats?.starCount || '--',
                    hfr: data.stats?.hfr?.toFixed(2) || '--',
                    thumb: this._captureThumbnail()
                });
                if (this.imageHistory.length > 50) this.imageHistory.pop();
                // Refresh HFR history chart
                this.$nextTick(() => this.updateHfrChart());
                if (this.looping) {
                    this.capture();
                }
            } catch (e) {
                if (this.looping) {
                    this.toast('Capture error, retrying...', 'warn');
                    setTimeout(() => {
                        if (this.looping) this.capture();
                    }, 2000);
                } else {
                    this.toast('Capture failed: ' + e.message, 'error');
                }
            } finally {
                if (!this.looping) this.capturing = false;
            }
        },

        async loopCapture() {
            this.looping = true;
            await this.capture();
        },

        async stopCapture() {
            this.looping = false;
            this.capturing = false;
            try { await this.apiPost('/api/camera/abort'); } catch (e) { }
        },

        async setCooler(enabled, temp) {
            try {
                await this.apiPost('/api/camera/cooler', {
                    enabled, targetTemperature: temp || null
                });
            } catch (e) {
                this.toast('Cooler command failed', 'error');
            }
        },

        // --- Live Stacking ---

        async toggleLiveStack() {
            this.liveStackEnabled = !this.liveStackEnabled;
            try {
                if (this.liveStackEnabled) {
                    await this.apiPost('/api/livestack/start');
                    this.toast('Live stacking started', 'ok');
                } else {
                    await this.apiPost('/api/livestack/stop');
                    this.toast('Live stacking stopped', 'warn');
                }
            } catch (e) {
                this.toast('Live stack toggle failed', 'error');
            }
        },

        async resetLiveStack() {
            try {
                await this.apiPost('/api/livestack/reset');
                this.liveStackFrames = 0;
            } catch (e) {
                this.toast('Live stack reset failed', 'error');
            }
        },

        // --- Telescope/Mount ---

        async selectTelescope(name) {
            try {
                await this.apiPost(`/api/telescope/select/${encodeURIComponent(name)}`);
                await this.apiPost('/api/telescope/connect');
                this.selectedTelescope = name;
                this.mount.connected = true;
                this.toast('Mount connected: ' + name, 'ok');
            } catch (e) {
                this.toast('Mount connection failed: ' + e.message, 'error');
            }
        },

        async mountMove(direction) {
            const dirMap = { n: 'north', s: 'south', e: 'east', w: 'west', stop: 'stop' };
            try {
                await this.apiPost(`/api/telescope/move/${dirMap[direction] || direction}`);
            } catch (e) {
                this.toast('Mount move failed', 'error');
            }
        },

        async mountStop() {
            try { await this.apiPost('/api/telescope/abort'); } catch (e) { }
        },

        async parkMount() {
            try {
                await this.apiPost('/api/telescope/park');
                this.toast('Parking mount...', 'info');
            } catch (e) {
                this.toast('Park failed', 'error');
            }
        },

        async unparkMount() {
            try {
                await this.apiPost('/api/telescope/unpark');
                this.toast('Unparking mount...', 'info');
            } catch (e) {
                this.toast('Unpark failed', 'error');
            }
        },

        async toggleTracking() {
            try {
                await this.apiPost('/api/telescope/tracking', { enabled: !this.mount.tracking });
            } catch (e) {
                this.toast('Tracking toggle failed', 'error');
            }
        },

        async slewTo(ra, dec) {
            try {
                await this.apiPost('/api/telescope/slew', { ra, dec });
                this.toast('Slewing...', 'info');
            } catch (e) {
                this.toast('Slew failed: ' + e.message, 'error');
            }
        },

        // --- Focuser ---

        async selectFocuser(name) {
            try {
                await this.apiPost(`/api/focuser/select/${encodeURIComponent(name)}`);
                await this.apiPost('/api/focuser/connect');
                this.selectedFocuser = name;
                this.focusConnected = true;
                this.toast('Focuser connected: ' + name, 'ok');
            } catch (e) {
                this.toast('Focuser connection failed: ' + e.message, 'error');
            }
        },

        async focusMove(steps) {
            try {
                await this.apiPost('/api/focuser/move/relative', { steps });
            } catch (e) {
                this.toast('Focus move failed', 'error');
            }
        },

        async focusMoveTo(position) {
            try {
                await this.apiPost('/api/focuser/move/absolute', { position });
            } catch (e) {
                this.toast('Focus move failed', 'error');
            }
        },

        async focusAbort() {
            try { await this.apiPost('/api/focuser/abort'); } catch (e) { }
        },

        async startAutoFocus() {
            try {
                await this.apiPost('/api/autofocus/start', {
                    steps: this.afParams.steps,
                    stepSize: this.afParams.stepSize,
                    exposureSeconds: this.afParams.exposureSeconds,
                    minStars: this.afParams.minStars,
                    backlashSteps: this.afParams.backlashSteps,
                    takeConfirmationFrame: true
                });
                this.toast('Auto-focus started', 'ok');
            } catch (e) {
                this.toast('AF start failed: ' + e.message, 'error');
            }
        },
        async abortAutoFocus() {
            try {
                await this.apiPost('/api/autofocus/abort');
                this.toast('Auto-focus abort requested', 'warn');
            } catch (e) { this.toast('AF abort failed', 'error'); }
        },

        // ---- AF chart helpers ----
        get afChartXRange() {
            const pts = this.autoFocus.points || [];
            if (pts.length === 0) return { min: 0, max: 1 };
            let lo = Infinity, hi = -Infinity;
            for (const p of pts) { if (p.position < lo) lo = p.position; if (p.position > hi) hi = p.position; }
            if (this.autoFocus.bestPosition) {
                if (this.autoFocus.bestPosition < lo) lo = this.autoFocus.bestPosition;
                if (this.autoFocus.bestPosition > hi) hi = this.autoFocus.bestPosition;
            }
            if (hi === lo) hi = lo + 1;
            const pad = (hi - lo) * 0.05;
            return { min: lo - pad, max: hi + pad };
        },
        get afChartHfrMax() {
            const pts = this.autoFocus.points || [];
            let max = 1;
            for (const p of pts) { if (p.hfr > max) max = p.hfr; }
            return max * 1.15;
        },
        get afChartHasFit() {
            return this.autoFocus.bestPosition !== null && this.autoFocus.bestPosition !== undefined;
        },
        afPointX(pos) {
            const r = this.afChartXRange;
            return ((pos - r.min) / (r.max - r.min)) * this.afChartW;
        },
        afPointY(hfr) {
            const max = this.afChartHfrMax;
            const clamped = Math.max(0, Math.min(max, hfr));
            // hfr=0 → bottom (y=h); hfr=max → top (y=0)
            return this.afChartH - (clamped / max) * this.afChartH;
        },
        // Draw fitted parabola sampled at 30 x-values across the range
        buildAfFitPath() {
            const result = this.autoFocus;
            // We don't get a/b/c on the status stream — derive from points if absent.
            // For the live chart we just draw a smooth quadratic going through best
            // position (vertex) and the two extreme samples.
            if (!result || !result.bestPosition || (result.points || []).length < 3) return '';
            const pts = result.points;
            const minP = pts[0].position, maxP = pts[pts.length - 1].position;
            const bestX = result.bestPosition;
            const bestY = result.bestHfr || 0;
            // Solve a*(x-bestX)^2 + bestY = sample HFR using extremes
            // Use the average of two extreme samples to estimate "a"
            const leftHfr = pts.reduce((m, p) => p.position < bestX && p.hfr > m ? p.hfr : m, 0);
            const rightHfr = pts.reduce((m, p) => p.position > bestX && p.hfr > m ? p.hfr : m, 0);
            const extremeHfr = Math.max(leftHfr, rightHfr);
            const halfRange = Math.max(bestX - minP, maxP - bestX, 1);
            const a = Math.max(0, (extremeHfr - bestY) / (halfRange * halfRange));

            const steps = 40;
            const r = this.afChartXRange;
            let d = '';
            for (let i = 0; i <= steps; i++) {
                const x = r.min + (r.max - r.min) * i / steps;
                const y = a * (x - bestX) * (x - bestX) + bestY;
                const sx = this.afPointX(x).toFixed(1);
                const sy = this.afPointY(y).toFixed(1);
                d += (i === 0 ? 'M' : 'L') + sx + ',' + sy + ' ';
            }
            return d.trim();
        },

        // --- Filter Wheel ---

        async selectFilterWheel(name) {
            try {
                await this.apiPost(`/api/filterwheel/select/${encodeURIComponent(name)}`);
                await this.apiPost('/api/filterwheel/connect');
                this.selectedFilterWheel = name;
                this.filterWheel.connected = true;
                this.toast('Filter wheel connected: ' + name, 'ok');
            } catch (e) {
                this.toast('Filter wheel connection failed: ' + e.message, 'error');
            }
        },

        async setFilter(filterName) {
            try {
                await this.apiPost(`/api/filterwheel/filter/${encodeURIComponent(filterName)}`);
                this.toast('Moving to filter: ' + filterName, 'info');
            } catch (e) {
                this.toast('Filter change failed: ' + e.message, 'error');
            }
        },

        async setFilterPosition(slot) {
            try {
                await this.apiPost(`/api/filterwheel/position/${slot}`);
            } catch (e) {
                this.toast('Filter change failed', 'error');
            }
        },

        // --- Equipment tab connect/disconnect ---

        async equipConnectCamera() {
            if (!this.equipCameraChoice) return;
            try {
                await this.apiPost(`/api/camera/select/${encodeURIComponent(this.equipCameraChoice)}`);
                await this.apiPost('/api/camera/connect');
                this.selectedCamera = this.equipCameraChoice;
                this.toast('Camera connected: ' + this.equipCameraChoice, 'ok');
                this.pollCameraInfo();
            } catch (e) {
                this.toast('Camera connection failed: ' + e.message, 'error');
            }
        },

        async equipDisconnectCamera() {
            try {
                await this.apiPost('/api/camera/disconnect');
                this.selectedCamera = null;
                this.cameraTemp = null;
                this.equipCameraInfo = { coolerOn: false, binX: 0, binY: 0, bitDepth: 0 };
                this.toast('Camera disconnected', 'warn');
            } catch (e) {
                this.toast('Camera disconnect failed: ' + e.message, 'error');
            }
        },

        async equipConnectMount() {
            if (!this.equipMountChoice) return;
            try {
                await this.apiPost(`/api/telescope/select/${encodeURIComponent(this.equipMountChoice)}`);
                await this.apiPost('/api/telescope/connect');
                this.selectedTelescope = this.equipMountChoice;
                this.mount.connected = true;
                this.toast('Mount connected: ' + this.equipMountChoice, 'ok');
            } catch (e) {
                this.toast('Mount connection failed: ' + e.message, 'error');
            }
        },

        async equipDisconnectMount() {
            try {
                await this.apiPost('/api/telescope/disconnect');
                this.selectedTelescope = null;
                this.mount.connected = false;
                this.mount.tracking = false;
                this.mount.slewing = false;
                this.mount.parked = false;
                this.toast('Mount disconnected', 'warn');
            } catch (e) {
                this.toast('Mount disconnect failed: ' + e.message, 'error');
            }
        },

        async equipConnectFocuser() {
            if (!this.equipFocuserChoice) return;
            try {
                await this.apiPost(`/api/focuser/select/${encodeURIComponent(this.equipFocuserChoice)}`);
                await this.apiPost('/api/focuser/connect');
                this.selectedFocuser = this.equipFocuserChoice;
                this.focusConnected = true;
                this.toast('Focuser connected: ' + this.equipFocuserChoice, 'ok');
            } catch (e) {
                this.toast('Focuser connection failed: ' + e.message, 'error');
            }
        },

        async equipDisconnectFocuser() {
            try {
                await this.apiPost('/api/focuser/disconnect');
                this.selectedFocuser = null;
                this.focusConnected = false;
                this.focusPosition = 0;
                this.focusTemp = null;
                this.toast('Focuser disconnected', 'warn');
            } catch (e) {
                this.toast('Focuser disconnect failed: ' + e.message, 'error');
            }
        },

        async equipConnectFilter() {
            if (!this.equipFilterChoice) return;
            try {
                await this.apiPost(`/api/filterwheel/select/${encodeURIComponent(this.equipFilterChoice)}`);
                await this.apiPost('/api/filterwheel/connect');
                this.selectedFilterWheel = this.equipFilterChoice;
                this.filterWheel.connected = true;
                this.toast('Filter wheel connected: ' + this.equipFilterChoice, 'ok');
            } catch (e) {
                this.toast('Filter wheel connection failed: ' + e.message, 'error');
            }
        },

        async equipDisconnectFilter() {
            try {
                await this.apiPost('/api/filterwheel/disconnect');
                this.selectedFilterWheel = null;
                this.filterWheel.connected = false;
                this.filterWheel.filters = [];
                this.filterWheel.currentFilter = '';
                this.toast('Filter wheel disconnected', 'warn');
            } catch (e) {
                this.toast('Filter wheel disconnect failed: ' + e.message, 'error');
            }
        },

        // --- Rotator ---
        async equipConnectRotator() {
            if (!this.equipRotatorChoice) return;
            try {
                await this.apiPost(`/api/rotator/select/${encodeURIComponent(this.equipRotatorChoice)}`);
                await this.apiPost('/api/rotator/connect');
                this.rotator.connected = true;
                this.rotator.name = this.equipRotatorChoice;
                this.toast('Rotator connected: ' + this.equipRotatorChoice, 'ok');
            } catch (e) {
                this.toast('Rotator connection failed: ' + e.message, 'error');
            }
        },
        async equipDisconnectRotator() {
            try {
                await this.apiPost('/api/rotator/disconnect');
                this.rotator = { connected: false, name: '', position: null, moving: false, reversed: false };
                this.toast('Rotator disconnected', 'warn');
            } catch (e) {
                this.toast('Rotator disconnect failed: ' + e.message, 'error');
            }
        },
        async rotatorMoveTo() {
            try {
                await this.apiPost('/api/rotator/move', { angle: this.equipRotatorTarget });
                this.toast(`Rotator moving to ${this.equipRotatorTarget}°`, 'ok');
            } catch (e) {
                this.toast('Rotator move failed: ' + e.message, 'error');
            }
        },
        async rotatorAbort() {
            try {
                await this.apiPost('/api/rotator/abort');
                this.toast('Rotator stopped', 'warn');
            } catch (e) { this.toast('Rotator abort failed', 'error'); }
        },
        async rotatorToggleReverse() {
            try {
                const newVal = !this.rotator.reversed;
                await this.apiPost('/api/rotator/reverse', { reversed: newVal });
                this.rotator.reversed = newVal;
            } catch (e) { this.toast('Rotator reverse failed', 'error'); }
        },

        // --- Flat Panel ---
        async equipConnectFlat() {
            if (!this.equipFlatChoice) return;
            try {
                await this.apiPost(`/api/flatdevice/select/${encodeURIComponent(this.equipFlatChoice)}`);
                await this.apiPost('/api/flatdevice/connect');
                this.flatDevice.connected = true;
                this.flatDevice.name = this.equipFlatChoice;
                this.toast('Flat panel connected: ' + this.equipFlatChoice, 'ok');
            } catch (e) {
                this.toast('Flat panel connection failed: ' + e.message, 'error');
            }
        },
        async equipDisconnectFlat() {
            try {
                await this.apiPost('/api/flatdevice/disconnect');
                this.flatDevice = { connected: false, name: '', lightOn: false, brightness: 0, coverOpen: false, coverMoving: false };
                this.toast('Flat panel disconnected', 'warn');
            } catch (e) {
                this.toast('Flat panel disconnect failed: ' + e.message, 'error');
            }
        },
        async flatToggleLight() {
            try {
                const newVal = !this.flatDevice.lightOn;
                await this.apiPost('/api/flatdevice/light', { on: newVal });
                this.flatDevice.lightOn = newVal;
            } catch (e) { this.toast('Flat light failed', 'error'); }
        },
        async flatSetBrightness() {
            try {
                await this.apiPost('/api/flatdevice/brightness', { brightness: this.equipFlatBrightness });
                this.toast(`Brightness set to ${this.equipFlatBrightness}`, 'ok');
            } catch (e) { this.toast('Brightness set failed', 'error'); }
        },
        async flatOpenCover() {
            try {
                await this.apiPost('/api/flatdevice/cover/open');
                this.toast('Opening cover', 'ok');
            } catch (e) { this.toast('Cover open failed', 'error'); }
        },
        async flatCloseCover() {
            try {
                await this.apiPost('/api/flatdevice/cover/close');
                this.toast('Closing cover', 'ok');
            } catch (e) { this.toast('Cover close failed', 'error'); }
        },

        // --- Dome ---
        async equipConnectDome() {
            if (!this.equipDomeChoice) return;
            try {
                await this.apiPost(`/api/dome/select/${encodeURIComponent(this.equipDomeChoice)}`);
                await this.apiPost('/api/dome/connect');
                this.dome.connected = true;
                this.dome.name = this.equipDomeChoice;
                this.toast('Dome connected: ' + this.equipDomeChoice, 'ok');
            } catch (e) {
                this.toast('Dome connection failed: ' + e.message, 'error');
            }
        },
        async equipDisconnectDome() {
            try {
                await this.apiPost('/api/dome/disconnect');
                this.dome = { connected: false, name: '', azimuth: null, moving: false, parked: false, slaved: false, shutter: 'Unknown' };
                this.toast('Dome disconnected', 'warn');
            } catch (e) {
                this.toast('Dome disconnect failed: ' + e.message, 'error');
            }
        },
        async domeSlew() {
            try {
                await this.apiPost('/api/dome/slew', { azimuth: this.equipDomeTarget });
                this.toast(`Dome slewing to ${this.equipDomeTarget}°`, 'ok');
            } catch (e) { this.toast('Dome slew failed', 'error'); }
        },
        async domeOpenShutter() {
            try { await this.apiPost('/api/dome/shutter/open'); this.toast('Opening shutter', 'ok'); }
            catch (e) { this.toast('Shutter open failed', 'error'); }
        },
        async domeCloseShutter() {
            try { await this.apiPost('/api/dome/shutter/close'); this.toast('Closing shutter', 'ok'); }
            catch (e) { this.toast('Shutter close failed', 'error'); }
        },
        async domePark() {
            try { await this.apiPost('/api/dome/park'); this.toast('Dome parking', 'ok'); }
            catch (e) { this.toast('Dome park failed', 'error'); }
        },
        async domeUnpark() {
            try { await this.apiPost('/api/dome/unpark'); this.toast('Dome unparking', 'ok'); }
            catch (e) { this.toast('Dome unpark failed', 'error'); }
        },
        async domeAbort() {
            try { await this.apiPost('/api/dome/abort'); this.toast('Dome stopped', 'warn'); }
            catch (e) { this.toast('Dome abort failed', 'error'); }
        },

        // --- Weather ---
        async equipConnectWeather() {
            if (!this.equipWeatherChoice) return;
            try {
                await this.apiPost(`/api/weather/select/${encodeURIComponent(this.equipWeatherChoice)}`);
                await this.apiPost('/api/weather/connect');
                this.weather.connected = true;
                this.weather.name = this.equipWeatherChoice;
                this.toast('Weather connected: ' + this.equipWeatherChoice, 'ok');
            } catch (e) {
                this.toast('Weather connection failed: ' + e.message, 'error');
            }
        },
        async equipDisconnectWeather() {
            try {
                await this.apiPost('/api/weather/disconnect');
                this.weather = {
                    connected: false, name: '', safe: false,
                    temperature: null, humidity: null, dewPoint: null,
                    windSpeed: null, windGust: null, pressure: null,
                    cloudCover: null, rainRate: null, skyQuality: null
                };
                this.toast('Weather disconnected', 'warn');
            } catch (e) {
                this.toast('Weather disconnect failed: ' + e.message, 'error');
            }
        },
        async weatherRefresh() {
            try { await this.apiPost('/api/weather/refresh'); this.toast('Weather refreshing', 'ok'); }
            catch (e) { this.toast('Weather refresh failed', 'error'); }
        },

        // --- Guider (PHD2) ---
        async guiderConnect() {
            try {
                await this.apiPost('/api/guider/connect', { host: this.guiderHost, port: this.guiderPort });
                this.guider.connected = true;
                this.guider.host = this.guiderHost;
                this.guider.port = this.guiderPort;
                this.toast(`PHD2 connected at ${this.guiderHost}:${this.guiderPort}`, 'ok');
                this.fetchGuiderEquipment();
            } catch (e) {
                this.toast('PHD2 connect failed: ' + e.message, 'error');
            }
        },

        async fetchGuiderEquipment() {
            try {
                const e = await this.apiGet('/api/guider/equipment');
                if (e && e.connected) {
                    this.guiderEquipment = {
                        camera: e.camera || null,
                        mount: e.mount || null,
                        auxMount: e.auxMount || null,
                        ao: e.ao || null
                    };
                }
            } catch (err) { /* ignore */ }
            // Pull the full management state alongside (parallel, best-effort)
            await Promise.allSettled([
                this.fetchPhd2Profiles(),
                this.fetchPhd2Exposure(),
                this.fetchPhd2DecMode(),
                this.fetchPhd2EquipmentConnected()
            ]);
        },

        // ---- PHD2 management ----

        async fetchPhd2ProcessStatus() {
            try {
                const s = await this.apiGet('/api/guider/process/status');
                this.phd2Process = s;
            } catch (e) { /* ignore */ }
        },

        async fetchPhd2InstallInfo() {
            try {
                const info = await this.apiGet('/api/guider/install-info');
                this.phd2Install = info;
                this.phd2AutoStart = !!info.autoStart;
            } catch (e) { /* ignore */ }
        },

        async setPhd2AutoStart(enabled) {
            try {
                await this.apiPost('/api/guider/auto-start/' + (enabled ? 'true' : 'false'));
                this.toast(enabled ? 'PHD2 will auto-start on next boot' : 'PHD2 auto-start disabled', 'ok');
            } catch (e) {
                this.toast('Could not save auto-start preference: ' + e.message, 'error');
                this.phd2AutoStart = !enabled; // revert
            }
        },

        async launchPhd2() {
            this.toast('Launching PHD2…', 'ok');
            try {
                const r = await this.apiPost('/api/guider/process/launch');
                if (r.running) {
                    this.toast('PHD2 is up — connecting…', 'ok');
                    // Wait a beat then try the JSON-RPC connect
                    setTimeout(() => this.guiderConnect(), 1500);
                } else {
                    this.toast('PHD2 launched but event server did not come up', 'warn');
                }
            } catch (e) { this.toast('Launch failed: ' + e.message, 'error'); }
        },

        async shutdownPhd2() {
            if (!confirm('Shut down PHD2? Any ongoing guiding will stop.')) return;
            try {
                await this.apiPost('/api/guider/process/shutdown');
                this.guider.connected = false;
                this.phd2EquipmentConnected = false;
                this.toast('PHD2 shut down', 'warn');
            } catch (e) { this.toast('Shutdown failed: ' + e.message, 'error'); }
        },

        async fetchPhd2Profiles() {
            try {
                const r = await this.apiGet('/api/guider/profiles');
                this.phd2Profiles = r.profiles || [];
                this.phd2SelectedProfileId = r.current?.id || 0;
            } catch (e) { /* PHD2 may not be ready yet */ }
        },

        async setPhd2Profile(id) {
            try {
                await this.apiPost(`/api/guider/profile/${id}`);
                this.phd2SelectedProfileId = parseInt(id);
                this.toast('PHD2 profile switched', 'ok');
                // Equipment is disconnected by SetProfileAsync — refresh
                await this.fetchPhd2EquipmentConnected();
            } catch (e) { this.toast('Profile switch failed: ' + e.message, 'error'); }
        },

        async fetchPhd2Exposure() {
            try {
                const r = await this.apiGet('/api/guider/exposure');
                this.phd2Exposure = r.current || 1000;
                this.phd2ExposureOptions = r.available || [];
            } catch (e) { /* ignore */ }
        },

        async setPhd2Exposure(ms) {
            try {
                await this.apiPost(`/api/guider/exposure/set/${ms}`);
                this.phd2Exposure = parseInt(ms);
            } catch (e) { this.toast('Exposure set failed', 'error'); }
        },

        async fetchPhd2DecMode() {
            try {
                const r = await this.apiGet('/api/guider/dec-mode');
                if (r.mode) this.phd2DecMode = r.mode;
            } catch (e) { /* ignore */ }
        },

        async setPhd2DecMode(mode) {
            try {
                await this.apiPost(`/api/guider/dec-mode/${mode}`);
                this.phd2DecMode = mode;
            } catch (e) { this.toast('Dec mode set failed', 'error'); }
        },

        async fetchPhd2EquipmentConnected() {
            try {
                const r = await this.apiGet('/api/guider/equipment/connected');
                this.phd2EquipmentConnected = !!r.connected;
            } catch (e) { /* ignore */ }
        },

        async connectPhd2Equipment() {
            try {
                await this.apiPost('/api/guider/equipment/connect');
                this.phd2EquipmentConnected = true;
                this.toast('PHD2 equipment connected', 'ok');
                this.fetchGuiderEquipment();
            } catch (e) { this.toast('Connect equipment failed: ' + e.message, 'error'); }
        },

        async disconnectPhd2Equipment() {
            try {
                await this.apiPost('/api/guider/equipment/disconnect');
                this.phd2EquipmentConnected = false;
                this.toast('PHD2 equipment disconnected', 'warn');
            } catch (e) { this.toast('Disconnect failed: ' + e.message, 'error'); }
        },
        async guiderDisconnect() {
            try {
                await this.apiPost('/api/guider/disconnect');
                this.guider.connected = false;
                this.guider.appState = 'Stopped';
                this.guider.guiding = false;
                this.guider.recentSteps = [];
                this.toast('PHD2 disconnected', 'warn');
            } catch (e) { this.toast('PHD2 disconnect failed', 'error'); }
        },
        async guiderStart() {
            try {
                await this.apiPost('/api/guider/guide', {
                    settlePixels: this.guiderSettlePixels,
                    settleTime: this.guiderSettleTime,
                    settleTimeout: this.guiderSettleTimeout,
                    recalibrate: false
                });
                this.toast('Guiding started', 'ok');
            } catch (e) { this.toast('Start guide failed: ' + e.message, 'error'); }
        },
        async guiderStop() {
            try { await this.apiPost('/api/guider/stop'); this.toast('Guiding stopped', 'warn'); }
            catch (e) { this.toast('Stop failed', 'error'); }
        },
        async guiderLoop() {
            try { await this.apiPost('/api/guider/loop'); this.toast('Looping exposures', 'ok'); }
            catch (e) { this.toast('Loop failed', 'error'); }
        },
        async guiderPause() {
            try { await this.apiPost('/api/guider/pause'); this.toast('Guiding paused', 'warn'); }
            catch (e) { this.toast('Pause failed', 'error'); }
        },
        async guiderResume() {
            try { await this.apiPost('/api/guider/resume'); this.toast('Guiding resumed', 'ok'); }
            catch (e) { this.toast('Resume failed', 'error'); }
        },
        async guiderDither() {
            try {
                await this.apiPost('/api/guider/dither', {
                    pixels: this.guiderDitherPx,
                    raOnly: this.guiderDitherRaOnly,
                    settlePixels: this.guiderSettlePixels,
                    settleTime: this.guiderSettleTime,
                    settleTimeout: this.guiderSettleTimeout
                });
                this.toast(`Dither ${this.guiderDitherPx}px requested`, 'ok');
            } catch (e) { this.toast('Dither failed: ' + e.message, 'error'); }
        },
        async guiderFindStar() {
            try { await this.apiPost('/api/guider/find-star'); this.toast('Auto-selecting star', 'ok'); }
            catch (e) { this.toast('Find star failed', 'error'); }
        },
        async guiderClearHistory() {
            try {
                await this.apiPost('/api/guider/clear-history');
                this.guider.recentSteps = [];
                this.toast('Guide history cleared', 'ok');
            } catch (e) { this.toast('Clear failed', 'error'); }
        },

        // Compute SVG polyline points string for the guider chart.
        // axis: 'ra' or 'dec'. Maps RA/Dec arcsec → y-coordinate in chart space.
        // Returns '' (empty polyline) when there's no data.
        buildGuidePath(axis) {
            const steps = this.guider.recentSteps || [];
            if (steps.length === 0) return '';
            const w = this.guideChartW, h = this.guideChartH;
            const scale = Math.max(this.guideChartScale, 0.5);
            const n = Math.max(steps.length, 1);
            const pts = [];
            for (let i = 0; i < steps.length; i++) {
                const x = (i / Math.max(n - 1, 1)) * w;
                const v = axis === 'ra' ? steps[i].ra : steps[i].dec;
                // clamp and map: +scale → top (y=0), -scale → bottom (y=h)
                const clamped = Math.max(-scale, Math.min(scale, v));
                const y = (h / 2) - (clamped / scale) * (h / 2);
                pts.push(x.toFixed(1) + ',' + y.toFixed(1));
            }
            return pts.join(' ');
        },

        async pollCameraInfo() {
            try {
                const data = await this.apiGet('/api/camera/status');
                if (data.connected) {
                    this.equipCameraInfo = {
                        coolerOn: data.coolerOn || false,
                        binX: data.binX || 0,
                        binY: data.binY || 0,
                        bitDepth: data.bitDepth || 0
                    };
                    if (data.temperature !== null && data.temperature !== undefined) {
                        this.cameraTemp = data.temperature;
                    }
                }
            } catch (e) { }
        },

        // --- Sky ---

        // ---- Sky Atlas filters ----

        async loadAtlasTypes() {
            try {
                this.atlasTypes = await this.apiGet('/api/sky/catalog/types');
            } catch (e) { }
        },

        async atlasSearch() {
            const params = new URLSearchParams();
            if (this.atlasFilter.type) params.set('type', this.atlasFilter.type);
            if (this.atlasFilter.minMag != null) params.set('minMag', this.atlasFilter.minMag);
            if (this.atlasFilter.maxMag != null) params.set('maxMag', this.atlasFilter.maxMag);
            if (this.atlasFilter.minDec != null) params.set('minDec', this.atlasFilter.minDec);
            if (this.atlasFilter.maxDec != null) params.set('maxDec', this.atlasFilter.maxDec);
            try {
                const data = await this.apiGet('/api/sky/catalog/filter?' + params.toString());
                this.atlasResults = data.results || [];
            } catch (e) {
                this.toast('Atlas search failed: ' + e.message, 'error');
            }
        },

        resetAtlasFilters() {
            this.atlasFilter = { type: '', minMag: null, maxMag: null, minDec: null, maxDec: null };
            this.atlasResults = [];
        },

        // ---- Altitude chart ----

        async loadAltitudeChart() {
            if (!this.skyTarget) return;
            try {
                this.altitudeData = await this.apiGet(
                    `/api/sky/altitude?ra=${this.skyTarget.ra}&dec=${this.skyTarget.dec}&stepMinutes=15`);
                this.$nextTick(() => this.updateAltChart());
            } catch (e) {
                this.toast('Altitude calc failed: ' + e.message, 'error');
            }
        },

        updateAltChart() {
            if (!this.altitudeData) return;
            const t = this._chartTheme();
            // Build twilight band annotation rectangles via dataset background?
            // Simpler: shade the whole panel via a second dataset that fills to 0.
            const c = this._ensureChart('altChart', 'alt', 'line', () => ({
                type: 'line',
                data: {
                    labels: [],
                    datasets: [
                        { label: 'Altitude', data: [], borderColor: '#64b5f6',
                          backgroundColor: 'transparent', tension: 0.25,
                          pointRadius: 0, borderWidth: 1.5 }
                    ]
                },
                options: {
                    responsive: true, maintainAspectRatio: false, animation: false,
                    plugins: { legend: { display: false } },
                    scales: {
                        x: { ticks: { color: t.tick, font: { size: 9 } }, grid: { color: t.grid } },
                        y: { min: -10, max: 90, ticks: { color: t.tick, font: { size: 10 } },
                             grid: { color: t.grid },
                             title: { display: true, text: 'Altitude (°)', color: t.tick, font: { size: 10 } } }
                    }
                }
            }));
            if (!c) return;
            const samples = this.altitudeData.samples || [];
            c.data.labels = samples.map(s => {
                const d = new Date(s.utc);
                return d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
            });
            c.data.datasets[0].data = samples.map(s => s.altitudeDeg);
            c.update('none');
        },

        async getFromStellarium() {
            const host = this.settings.stellariumHost || 'localhost';
            const port = this.settings.stellariumPort || 8090;
            try {
                const t = await this.apiGet(`/api/stellarium/target?host=${encodeURIComponent(host)}&port=${port}`);
                if (!t) { this.toast('Stellarium: no object selected', 'warn'); return; }
                this.skyTarget = {
                    name: t.name,
                    ra: t.raHours,
                    dec: t.decDeg,
                    type: t.type || 'Stellarium',
                    magnitude: t.magnitude != null ? t.magnitude.toFixed(2) : '',
                    raFormatted: this.formatRA(t.raHours),
                    decFormatted: this.formatDec(t.decDeg)
                };
                this.skyShowResults = false;
                this._goToSelectedTarget();
                this.toast('Loaded from Stellarium: ' + t.name, 'ok');
            } catch (e) {
                this.toast('Stellarium fetch failed: ' + e.message, 'error');
            }
        },

        async searchSky() {
            if (!this.skySearch.trim()) return;
            try {
                const data = await this.apiGet(
                    `/api/sky/catalog/search?query=${encodeURIComponent(this.skySearch)}`);
                this.skyResults = data.results || [];
                if (this.skyResults.length === 1) {
                    this.selectSkyTarget(this.skyResults[0]);
                    this.skyShowResults = false;
                } else if (this.skyResults.length > 1) {
                    this.skyShowResults = true;
                } else {
                    this.skyTarget = null;
                    this.skyShowResults = false;
                    this.toast('No objects found for "' + this.skySearch + '"', 'warn');
                }
            } catch (e) {
                this.toast('Sky search failed', 'error');
            }
        },

        selectSkyTarget(obj) {
            this.skyTarget = obj;
            this.skyShowResults = false;
            this._goToSelectedTarget();
        },

        async slewAndCenter() {
            if (!this.skyTarget) return;
            try {
                const resp = await this.apiPost('/api/sky/slew-and-center', {
                    ra: this.skyTarget.ra,
                    dec: this.skyTarget.dec,
                    toleranceArcsec: 30
                });
                const data = await resp.json();
                this.slewCenterJobId = data.jobId;
                this.slewCenterStatus = { state: 'pending', iteration: 0 };
                this.toast('Slew & center started', 'ok');
                this.startSlewCenterPolling();
            } catch (e) {
                this.toast('Slew & center failed: ' + e.message, 'error');
            }
        },

        startSlewCenterPolling() {
            this.stopSlewCenterPolling();
            this._slewCenterTimer = setInterval(() => this.pollSlewCenter(), 2000);
        },

        stopSlewCenterPolling() {
            if (this._slewCenterTimer) {
                clearInterval(this._slewCenterTimer);
                this._slewCenterTimer = null;
            }
        },

        async pollSlewCenter() {
            if (!this.slewCenterJobId) return;
            try {
                const data = await this.apiGet(
                    `/api/sky/slew-and-center/${this.slewCenterJobId}/status`);
                this.slewCenterStatus = data;

                if (data.state === 'centered') {
                    this.stopSlewCenterPolling();
                    this.toast(`Centered! Error: ${data.errorArcsec?.toFixed(1)}"`, 'ok', 6000);
                    this.slewCenterJobId = null;
                } else if (data.state === 'failed') {
                    this.stopSlewCenterPolling();
                    this.toast('Centering failed: ' + (data.error || 'unknown'), 'error', 6000);
                    this.slewCenterJobId = null;
                } else if (data.state === 'cancelled') {
                    this.stopSlewCenterPolling();
                    this.slewCenterJobId = null;
                }
            } catch (e) { }
        },

        async cancelSlewCenter() {
            if (!this.slewCenterJobId) return;
            try {
                await this.apiPost(`/api/sky/slew-and-center/${this.slewCenterJobId}/cancel`);
                this.stopSlewCenterPolling();
                this.slewCenterJobId = null;
                this.slewCenterStatus = null;
                this.toast('Slew & center cancelled', 'warn');
            } catch (e) { }
        },

        addToSequence() {
            if (!this.skyTarget) return;
            const item = {
                name: this.skyTarget.name,
                exposure: this.exposure,
                gain: this.gain,
                binning: parseInt(this.binning),
                count: 10,
                filter: null,
                ra: parseFloat(this.skyTarget.ra) || null,
                dec: parseFloat(this.skyTarget.dec) || null
            };
            this.sequence.push(item);
            this.syncSequenceToServer();
        },

        // --- Sequence ---

        addSequenceItem() {
            this.sequence.push({
                name: 'Target ' + (this.sequence.length + 1),
                exposure: this.exposure,
                gain: this.gain,
                binning: parseInt(this.binning),
                count: 10,
                filter: null,
                ra: null, dec: null
            });
        },

        removeSequenceItem(index) {
            if (this.seqState === 'running') {
                this.toast('Cannot modify while running', 'warn');
                return;
            }
            this.sequence.splice(index, 1);
        },

        async syncSequenceToServer() {
            try {
                await this.apiPost('/api/sequence', this.sequence);
            } catch (e) {
                this.toast('Failed to sync sequence', 'error');
            }
        },

        async loadSequenceFromServer() {
            try {
                const data = await this.apiGet('/api/sequence');
                if (data.items) this.sequence = data.items;
                this.seqState = data.state || 'idle';
            } catch (e) { }
        },

        async startSequence() {
            try {
                await this.apiPost('/api/sequence', this.sequence);
                await this.apiPost('/api/sequence/start');
                this.seqState = 'running';
                this.toast('Sequence started', 'ok');
            } catch (e) {
                this.toast('Start failed: ' + e.message, 'error');
            }
        },

        async pauseSequence() {
            try {
                if (this.seqState === 'paused') {
                    await this.apiPost('/api/sequence/resume');
                    this.seqState = 'running';
                    this.toast('Sequence resumed', 'ok');
                } else {
                    await this.apiPost('/api/sequence/pause');
                    this.seqState = 'paused';
                    this.toast('Sequence paused', 'info');
                }
            } catch (e) {
                this.toast('Pause/resume failed', 'error');
            }
        },

        async stopSequence() {
            try {
                await this.apiPost('/api/sequence/stop');
                this.seqState = 'idle';
                this.toast('Sequence stopped', 'warn');
            } catch (e) {
                this.toast('Stop failed', 'error');
            }
        },

        startSeqPolling() {
            this.stopSeqPolling();
            this._seqPollTimer = setInterval(() => this.pollSeqStatus(), 2000);
        },

        stopSeqPolling() {
            if (this._seqPollTimer) {
                clearInterval(this._seqPollTimer);
                this._seqPollTimer = null;
            }
        },

        async pollSeqStatus() {
            try {
                const status = await this.apiGet('/api/sequence/status');
                this.seqStatus = status;
                this.seqState = status.state || 'idle';
                if (status.state === 'idle' && this._seqPollTimer) {
                    this.stopSeqPolling();
                    if (status.totalFramesCompleted > 0 && !status.lastError) {
                        this.toast('Sequence completed!', 'ok', 6000);
                    }
                }
            } catch (e) { }
        },

        seqProgress() {
            if (!this.seqStatus || !this.seqStatus.totalFrames) return 0;
            return Math.round((this.seqStatus.totalFramesCompleted / this.seqStatus.totalFrames) * 100);
        },

        formatTime(seconds) {
            if (!seconds || seconds <= 0) return '--:--';
            const h = Math.floor(seconds / 3600);
            const m = Math.floor((seconds % 3600) / 60);
            const s = Math.floor(seconds % 60);
            if (h > 0) return `${h}h ${m.toString().padStart(2, '0')}m`;
            return `${m}m ${s.toString().padStart(2, '0')}s`;
        },

        // --- Status WebSocket handler ---

        handleStatusMessage(msg) {
            if (msg.type !== 'status') return;

            const eq = msg.equipment || {};

            if (eq.indi) {
                this.indiConnected = eq.indi.connected;
            }
            if (eq.camera) {
                this.cameraTemp = eq.camera.temperature;
                this.selectedCamera = eq.camera.name;
                this.equipCameraInfo = {
                    coolerOn: eq.camera.coolerOn || false,
                    binX: eq.camera.binX || 0,
                    binY: eq.camera.binY || 0,
                    bitDepth: eq.camera.bitDepth || 0,
                    sensorWidthMm: eq.camera.sensorWidthMm || 0,
                    sensorHeightMm: eq.camera.sensorHeightMm || 0,
                    pixelSizeUm: eq.camera.pixelSizeX || 0,
                    maxX: eq.camera.maxX || 0,
                    maxY: eq.camera.maxY || 0
                };
                // Auto-derive sensor dimensions from the connected camera. These
                // drive the FOV calculation and are only fallback-stored on the
                // profile when there's no live camera.
                if (eq.camera.sensorWidthMm > 0 && eq.camera.sensorHeightMm > 0) {
                    if (Math.abs(this.settings.sensorWidth - eq.camera.sensorWidthMm) > 0.05 ||
                        Math.abs(this.settings.sensorHeight - eq.camera.sensorHeightMm) > 0.05) {
                        this.settings.sensorWidth = eq.camera.sensorWidthMm;
                        this.settings.sensorHeight = eq.camera.sensorHeightMm;
                        this.updateFov();
                    }
                }
                // Sample temperature history at most once every 5s
                const now = Date.now();
                if (eq.camera.temperature !== null && eq.camera.temperature !== undefined
                    && now - this._tempLastSample > 5000) {
                    this.tempHistory.push({
                        t: now,
                        temp: eq.camera.temperature,
                        power: eq.camera.coolerOn ? (eq.camera.coolerPower || 0) : 0
                    });
                    if (this.tempHistory.length > 120) this.tempHistory.shift(); // ~10 min @ 5s
                    this._tempLastSample = now;
                }
            }
            if (eq.telescope) {
                Object.assign(this.mount, {
                    ra: eq.telescope.ra, dec: eq.telescope.dec,
                    alt: eq.telescope.alt, az: eq.telescope.az,
                    tracking: eq.telescope.tracking, slewing: eq.telescope.slewing,
                    parked: eq.telescope.parked, pierSide: eq.telescope.pierSide,
                    connected: eq.telescope.connected
                });
                this.selectedTelescope = eq.telescope.name;
            }
            if (eq.focuser) {
                this.focusPosition = eq.focuser.position;
                this.focusTemp = eq.focuser.temperature;
                this.focusMoving = eq.focuser.moving;
                this.focusConnected = true;
                this.selectedFocuser = eq.focuser.name;
            }
            if (eq.filterWheel) {
                this.filterWheel = {
                    connected: true,
                    position: eq.filterWheel.position,
                    currentFilter: eq.filterWheel.currentFilter,
                    filters: eq.filterWheel.filters || [],
                    moving: eq.filterWheel.moving
                };
                this.selectedFilterWheel = eq.filterWheel.name;
            }
            if (eq.rotator) {
                this.rotator = {
                    connected: eq.rotator.connected,
                    name: eq.rotator.name,
                    position: eq.rotator.position,
                    moving: eq.rotator.moving,
                    reversed: eq.rotator.reversed
                };
            }
            if (eq.flatDevice) {
                this.flatDevice = {
                    connected: eq.flatDevice.connected,
                    name: eq.flatDevice.name,
                    lightOn: eq.flatDevice.lightOn,
                    brightness: eq.flatDevice.brightness,
                    coverOpen: eq.flatDevice.coverOpen,
                    coverMoving: eq.flatDevice.coverMoving
                };
            }
            if (eq.dome) {
                this.dome = {
                    connected: eq.dome.connected,
                    name: eq.dome.name,
                    azimuth: eq.dome.azimuth,
                    moving: eq.dome.moving,
                    parked: eq.dome.parked,
                    slaved: eq.dome.slaved,
                    shutter: eq.dome.shutter
                };
            }
            if (eq.weather) {
                this.weather = {
                    connected: eq.weather.connected,
                    name: eq.weather.name,
                    safe: eq.weather.safe,
                    temperature: eq.weather.temperature,
                    humidity: eq.weather.humidity,
                    dewPoint: eq.weather.dewPoint,
                    windSpeed: eq.weather.windSpeed,
                    windGust: eq.weather.windGust,
                    pressure: eq.weather.pressure,
                    cloudCover: eq.weather.cloudCover,
                    rainRate: eq.weather.rainRate,
                    skyQuality: eq.weather.skyQuality
                };
            }
            if (msg.meridianFlip) {
                const mf = msg.meridianFlip;
                this.mfState = mf.state || 'idle';
                this.mfFlipsCompleted = mf.flipsCompleted || 0;
                this.mfLastFlipError = mf.lastFlipError || null;
                this.mfLstHours = mf.lstHours;
                this.mfHourAngleHours = mf.hourAngleHours;
                this.mfTimeToMeridianMinutes = mf.timeToMeridianMinutes;
                // Sync server-side settings back (in case another client edited)
                if (mf.settings) {
                    this.mfSettings.enabled = !!mf.settings.enabled;
                    // Don't overwrite other fields the user might be editing right now
                }
            }
            if (msg.autoFocus) {
                const af = msg.autoFocus;
                this.autoFocus = {
                    state: af.state || 'idle',
                    currentSampleIndex: af.currentSampleIndex ?? -1,
                    steps: af.steps || 0,
                    lastHfr: af.lastHfr || 0,
                    lastStarCount: af.lastStarCount || 0,
                    points: af.points || [],
                    bestPosition: af.bestPosition ?? null,
                    bestHfr: af.bestHfr ?? null,
                    success: af.success ?? null
                };
            }
            if (msg.guider) {
                const g = msg.guider;
                if (!g.connected) {
                    if (this.guider.connected) {
                        // server-side disconnect (PHD2 crashed?)
                        this.guider.connected = false;
                        this.guider.appState = 'Stopped';
                        this.guider.guiding = false;
                        this.guider.recentSteps = [];
                    }
                } else {
                    this.guider = {
                        connected: true,
                        host: g.host || this.guider.host,
                        port: g.port || this.guider.port,
                        appState: g.appState || 'Stopped',
                        guiding: g.guiding || false,
                        calibrating: g.calibrating || false,
                        paused: g.paused || false,
                        looping: g.looping || false,
                        settling: g.settling || false,
                        pixelScale: g.pixelScale || 0,
                        rmsRA: g.rmsRA || 0,
                        rmsDec: g.rmsDec || 0,
                        rmsTotal: g.rmsTotal || 0,
                        peakRA: g.peakRA || 0,
                        peakDec: g.peakDec || 0,
                        stepCount: g.stepCount || 0,
                        lastAlert: g.lastAlert || null,
                        lastSettleStatus: g.lastSettleStatus || null,
                        recentSteps: g.recentSteps || []
                    };
                    // Auto-expand chart scale based on peak (with floor of 2")
                    const need = Math.max(this.guider.peakRA, this.guider.peakDec, 1.0) * 1.2;
                    if (need > this.guideChartScale) this.guideChartScale = Math.ceil(need);
                }
            }
            if (msg.liveStack) {
                this.liveStackEnabled = msg.liveStack.isRunning;
                this.liveStackFrames = msg.liveStack.frameCount;
            }
            if (msg.sequence) {
                this.seqStatus = msg.sequence;
                var newState = msg.sequence.state || 'idle';
                var oldState = this.seqState;
                this.seqState = newState;

                if (oldState === 'running' && newState === 'idle' &&
                    msg.sequence.totalFramesCompleted > 0 && !msg.sequence.lastError) {
                    this.toast('Sequence completed!', 'ok', 6000);
                }
            }

            // Refresh charts once per status frame (1Hz) — only if their canvas
            // is currently in the DOM, otherwise Chart.js skips silently.
            this.$nextTick(() => {
                if (this.guider.connected && this.tab === 'guide') this.updateGuideChart();
                if ((this.autoFocus.points || []).length > 0 && this.tab === 'focus') this.updateAfChart();
                if (this.tempHistory.length >= 2 && this.tab === 'equip') this.updateTempChart();
            });
        },

        // --- Formatters ---

        formatRA(hours) {
            if (hours == null || isNaN(hours)) return '--h --m --s';
            const h = Math.floor(hours);
            const m = Math.floor((hours - h) * 60);
            const s = ((hours - h) * 60 - m) * 60;
            return `${h}h ${m.toString().padStart(2, '0')}m ${s.toFixed(1).padStart(4, '0')}s`;
        },

        formatDec(degrees) {
            if (degrees == null || isNaN(degrees)) return "--° --' --\"";
            const sign = degrees >= 0 ? '+' : '-';
            const abs = Math.abs(degrees);
            const d = Math.floor(abs);
            const m = Math.floor((abs - d) * 60);
            const s = ((abs - d) * 60 - m) * 60;
            return `${sign}${d}° ${m.toString().padStart(2, '0')}' ${s.toFixed(0).padStart(2, '0')}"`;
        },

        formatDeg(val, digits) {
            if (val == null || isNaN(val)) return '--';
            return val.toFixed(digits || 1) + '°';
        },

        // ============================================================
        //                   Advanced Sequencer
        // ============================================================
        // Fields shadowed in the entity JSON we don't want to expose in the
        // generic property editor (they're handled elsewhere or read-only).
        _advSeqHiddenFields: new Set(['id', '$type', 'type', 'name', 'description', 'items',
                                       'triggers', 'conditions', 'status', 'error', 'startedAt', 'finishedAt']),

        async loadAdvSeq() {
            try {
                const [doc, types] = await Promise.all([
                    this.apiGet('/api/sequencer/document'),
                    this.apiGet('/api/sequencer/types')
                ]);
                this.advSeq.types = types;
                this._advSeqRehydrate(doc.document);
                this.advSeq.state = doc.state;
                this.advSeq.lastError = doc.lastError;
                this.advSeq.abortReason = doc.abortReason;
                // Poll while running so the UI shows live status
                if (this._advSeqPoll) clearInterval(this._advSeqPoll);
                this._advSeqPoll = setInterval(() => this._advSeqRefresh(), 2000);
                // Init Sortable.js after the tree DOM lands (next tick)
                setTimeout(() => this._advSeqInitSortable(), 100);
            } catch (e) { this.toast('Adv seq load failed: ' + e.message, 'error'); }
        },

        _advSeqInitSortable() {
            if (typeof Sortable === 'undefined') return;
            document.querySelectorAll('.advseq-tree .tree-children').forEach(el => {
                if (el._sortable) return;
                el._sortable = Sortable.create(el, {
                    animation: 150,
                    handle: '.tree-type-badge',
                    onEnd: (evt) => {
                        // Find parent container in our model by the first child id, then
                        // reorder its items[] in place.
                        const ids = Array.from(el.querySelectorAll(':scope > .tree-node[data-id]'))
                            .map(n => n.dataset.id);
                        if (ids.length === 0) return;
                        const found = this.advSeqFindParent(ids[0]);
                        if (!found) return;
                        const parent = found.parent;
                        if (!parent.items) return;
                        parent.items = ids.map(id => parent.items.find(c => c.id === id)).filter(Boolean);
                        this.advSeqDirty = true;
                    }
                });
            });
        },

        async _advSeqRefresh() {
            if (this.tab !== 'seqadv') return;
            try {
                const doc = await this.apiGet('/api/sequencer/document');
                this.advSeq.state = doc.state;
                this.advSeq.lastError = doc.lastError;
                this.advSeq.abortReason = doc.abortReason;
                // If the server is running, mirror its live status into the local tree
                if (doc.state === 'Running' && doc.document?.root) {
                    this._advSeqMergeStatus(this.advSeq.doc.root, doc.document.root);
                }
            } catch (e) { /* keep last state on transient errors */ }
        },

        _advSeqMergeStatus(local, remote) {
            if (!local || !remote) return;
            local.status = remote.status; local.error = remote.error;
            local.startedAt = remote.startedAt; local.finishedAt = remote.finishedAt;
            if (local.items && remote.items) {
                for (let i = 0; i < local.items.length && i < remote.items.length; i++)
                    this._advSeqMergeStatus(local.items[i], remote.items[i]);
            }
        },

        _advSeqRehydrate(doc) {
            // The server emits $type at the top level of every entity; copy it
            // into a plain 'type' field for Alpine binding (Alpine can't bind to keys with $).
            const fix = (e) => {
                if (!e) return e;
                if (e['$type'] && !e.type) e.type = e['$type'];
                if (!e.id) e.id = 'ent-' + Math.random().toString(36).slice(2);
                (e.items || []).forEach(fix);
                (e.triggers || []).forEach(fix);
                (e.conditions || []).forEach(fix);
                return e;
            };
            this.advSeq.doc = doc;
            if (this.advSeq.doc.root) fix(this.advSeq.doc.root);
        },

        advSeqCategories() {
            return [...new Set((this.advSeq.types || []).map(t => t.category))];
        },
        advSeqByCategory(cat) {
            return (this.advSeq.types || []).filter(t => t.category === cat);
        },

        advSeqWalk(node, cb) {
            if (!node) return;
            cb(node);
            (node.items || []).forEach(c => this.advSeqWalk(c, cb));
            (node.triggers || []).forEach(c => this.advSeqWalk(c, cb));
            (node.conditions || []).forEach(c => this.advSeqWalk(c, cb));
        },
        advSeqFind(id, root) {
            let found = null;
            this.advSeqWalk(root || this.advSeq.doc.root, n => { if (n.id === id) found = n; });
            return found;
        },
        advSeqFindParent(id, root) {
            let parent = null;
            const walk = (n) => {
                for (const list of [n.items || [], n.triggers || [], n.conditions || []]) {
                    for (const c of list) {
                        if (c.id === id) { parent = { parent: n, list, child: c }; return; }
                        walk(c);
                        if (parent) return;
                    }
                }
            };
            walk(root || this.advSeq.doc.root);
            return parent;
        },
        advSeqSelectedNode() {
            return this.advSeq.selectedId ? this.advSeqFind(this.advSeq.selectedId) : null;
        },
        advSeqSelectedIsContainer() {
            const n = this.advSeqSelectedNode();
            if (!n) return false;
            return ['Sequential', 'Parallel', 'DeepSkyObject', 'Templated'].includes(n.type);
        },
        advSeqSelect(id) {
            this.advSeq.selectedId = id;
        },

        advSeqAddToSelected(t) {
            const target = this.advSeqSelectedNode();
            if (!target) { this.toast('Select a container first', 'warn'); return; }
            const child = this._advSeqNewEntity(t);
            const bucket = t.kind === 'Trigger' ? 'triggers'
                         : t.kind === 'Condition' ? 'conditions'
                         : 'items';
            (target[bucket] = target[bucket] || []).push(child);
            this.advSeq.selectedId = child.id;
            this.advSeqDirty = true;
            setTimeout(() => this._advSeqInitSortable(), 50);
        },

        _advSeqNewEntity(t) {
            return {
                id: 'ent-' + Math.random().toString(36).slice(2),
                type: t.type, $type: t.type,
                name: t.type,
                items: ['Sequential', 'Parallel', 'DeepSkyObject', 'Templated'].includes(t.type) ? [] : undefined,
                triggers: ['Sequential', 'Parallel', 'DeepSkyObject', 'Templated'].includes(t.type) ? [] : undefined,
                conditions: ['Sequential', 'Parallel', 'DeepSkyObject', 'Templated'].includes(t.type) ? [] : undefined
            };
        },

        advSeqDeleteSelected() {
            if (!this.advSeq.selectedId || this.advSeq.selectedId === this.advSeq.doc.root.id) return;
            const found = this.advSeqFindParent(this.advSeq.selectedId);
            if (!found) return;
            const idx = found.list.indexOf(found.child);
            if (idx >= 0) found.list.splice(idx, 1);
            this.advSeq.selectedId = null;
            this.advSeqDirty = true;
        },

        advSeqEditableFields() {
            const n = this.advSeqSelectedNode();
            if (!n) return [];
            const out = [];
            for (const k of Object.keys(n)) {
                if (this._advSeqHiddenFields.has(k)) continue;
                const v = n[k];
                if (Array.isArray(v)) continue;
                if (typeof v === 'object' && v !== null) continue;
                let kind = 'string';
                if (typeof v === 'boolean') kind = 'bool';
                else if (typeof v === 'number') kind = 'number';
                out.push({ key: k, kind });
            }
            return out;
        },

        advSeqRenderTree(node, depth) {
            // Renders as innerHTML for speed. Click handlers wired via x-on:click on the
            // wrapper using event delegation.
            if (!node) return '';
            const sel = node.id === this.advSeq.selectedId ? 'selected' : '';
            const status = 'status-' + (node.status || 'Idle');
            const kid = (node.items || []).map(c => this.advSeqRenderTree(c, depth + 1)).join('');
            const trig = (node.triggers || []).map(t => `<div class="tree-aux"><span class="tree-aux-label">Trigger:</span> ${this._advSeqLeaf(t)}</div>`).join('');
            const cond = (node.conditions || []).map(c => `<div class="tree-aux"><span class="tree-aux-label">Cond:</span> ${this._advSeqLeaf(c)}</div>`).join('');
            const errHtml = node.error ? `<span class="tree-error">⚠ ${this._esc(node.error)}</span>` : '';
            return `
                <div class="tree-node ${sel} ${status}" data-id="${node.id}" onclick="event.stopPropagation(); window.__alpineRoot.advSeqSelect('${node.id}')">
                    <span class="tree-type-badge">${node.type}</span>
                    <span class="tree-name">${this._esc(node.name || node.type)}</span>
                    ${errHtml}
                </div>
                ${trig}
                ${cond}
                ${kid ? `<div class="tree-children">${kid}</div>` : ''}
            `;
        },
        _advSeqLeaf(n) {
            const sel = n.id === this.advSeq.selectedId ? 'selected' : '';
            return `<span class="tree-node ${sel} status-${n.status || 'Idle'}" data-id="${n.id}" style="display:inline-flex"
                     onclick="event.stopPropagation(); window.__alpineRoot.advSeqSelect('${n.id}')">
                <span class="tree-type-badge">${n.type}</span>
                <span class="tree-name">${this._esc(n.name || n.type)}</span>
              </span>`;
        },
        _esc(s) { return (s || '').replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c])); },

        async advSeqStart() {
            await this.advSeqSaveDoc();
            const r = await this.apiPost('/api/sequencer/start');
            this.advSeq.state = r.state;
            this.advSeq.lastError = r.error;
            if (r.error) this.toast('Start failed: ' + r.error, 'error');
        },
        async advSeqStop() {
            await this.apiPost('/api/sequencer/stop');
        },
        async advSeqValidate() {
            const r = await this.apiPost('/api/sequencer/validate');
            this.advSeq.errors = r.errors || [];
            this.toast(this.advSeq.errors.length === 0 ? 'No issues' : (this.advSeq.errors.length + ' issue(s)'),
                this.advSeq.errors.length === 0 ? 'ok' : 'warn');
        },
        async advSeqSaveDoc() {
            const payload = this._advSeqPrepareForServer(this.advSeq.doc);
            const r = await this.apiPost('/api/sequencer/document', payload);
            this.advSeq.errors = r.validation || [];
            this.advSeqDirty = false;
        },
        _advSeqPrepareForServer(doc) {
            // Strip the helper 'type' duplicate and runtime fields, write $type back
            const clean = (e) => {
                if (!e) return e;
                const out = {};
                for (const [k, v] of Object.entries(e)) {
                    if (['type', 'status', 'error', 'startedAt', 'finishedAt'].includes(k)) continue;
                    if (Array.isArray(v)) {
                        out[k] = v.map(clean);
                    } else if (typeof v === 'object' && v !== null) {
                        out[k] = clean(v);
                    } else {
                        out[k] = v;
                    }
                }
                if (e.type && !out['$type']) out['$type'] = e.type;
                return out;
            };
            return { ...doc, root: clean(doc.root) };
        },
        advSeqDownloadJson() {
            const blob = new Blob([JSON.stringify(this._advSeqPrepareForServer(this.advSeq.doc), null, 2)],
                { type: 'application/json' });
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = (this.advSeq.doc.name || 'sequence') + '.json';
            a.click();
            URL.revokeObjectURL(url);
        },
        async advSeqUploadJson(ev) {
            const f = ev.target.files[0]; if (!f) return;
            const text = await f.text();
            try {
                const r = await fetch('/api/sequencer/document/json', {
                    method: 'POST', headers: {'Content-Type': 'application/json'}, body: text
                });
                if (!r.ok) throw new Error('upload failed: ' + r.status);
                this.toast('Sequence loaded from file', 'ok');
                await this.loadAdvSeq();
            } catch (e) { this.toast(e.message, 'error'); }
            ev.target.value = '';
        },

        // ============================================================
        //                 Alpaca / ASCOM Remote
        // ============================================================
        async discoverAlpaca() {
            this.alpaca.discovering = true;
            try {
                const r = await this.apiGet('/api/alpaca/discover?timeoutMs=3000');
                this.alpaca.servers = (r.servers || []).map(s => ({
                    host: s.host, port: s.port,
                    serverName: s.serverName, manufacturer: s.manufacturer,
                    manufacturerVersion: s.manufacturerVersion,
                    devices: s.devices || null, _probe: null
                }));
                if (this.alpaca.servers.length === 0) {
                    this.toast('No Alpaca servers found. Try manual entry or check that ASCOM Remote Server is running.', 'warn');
                } else {
                    this.toast(`Found ${this.alpaca.servers.length} Alpaca server(s)`, 'ok');
                    // Auto-enumerate devices on each discovered server
                    for (const srv of this.alpaca.servers) await this.alpacaQueryServer(srv);
                }
            } catch (e) {
                this.toast('Alpaca discovery failed: ' + e.message, 'error');
            } finally {
                this.alpaca.discovering = false;
            }
        },

        async alpacaQueryServer(srv) {
            try {
                const r = await this.apiGet(`/api/alpaca/devices?host=${encodeURIComponent(srv.host)}&port=${srv.port}`);
                srv.devices = r.devices || [];
            } catch (e) {
                this.toast(`Could not list devices on ${srv.host}:${srv.port}: ${e.message}`, 'error');
            }
        },

        async alpacaQueryManual() {
            if (!this.alpaca.manualHost || !this.alpaca.manualPort) return;
            // Add or update the manual server in the list, then enumerate
            let srv = this.alpaca.servers.find(s => s.host === this.alpaca.manualHost && s.port === this.alpaca.manualPort);
            if (!srv) {
                srv = { host: this.alpaca.manualHost, port: this.alpaca.manualPort,
                        serverName: 'Manual entry', devices: null };
                this.alpaca.servers.push(srv);
            }
            await this.alpacaQueryServer(srv);
        },

        alpacaIsConnected(srv, d) {
            return !!this.alpaca.connected[`${srv.host}:${srv.port}:${d.deviceType}:${d.deviceNumber}`];
        },

        // Maps Alpaca's DeviceType (PascalCase, e.g. "FilterWheel") to the
        // URL slug we registered under /api/alpaca/{slug}/...
        _alpacaPathFor(deviceType) {
            const t = (deviceType || '').toLowerCase();
            return ({
                camera: 'camera',
                telescope: 'telescope',
                focuser: 'focuser',
                filterwheel: 'filterwheel',
                rotator: 'rotator',
                dome: 'dome',
                covercalibrator: 'covercalibrator',
                observingconditions: 'observingconditions'
            })[t] || null;
        },

        async alpacaConnectDevice(srv, d) {
            const path = this._alpacaPathFor(d.deviceType);
            if (!path) {
                this.toast(`No connect endpoint for "${d.deviceType}" yet`, 'warn');
                return;
            }
            const already = this.alpacaIsConnected(srv, d);
            const targetConnect = !already;
            try {
                const r = await this.apiPost(
                    `/api/alpaca/${path}/connect?host=${encodeURIComponent(srv.host)}&port=${srv.port}&device=${d.deviceNumber}&connect=${targetConnect}`);
                this.alpaca.connected[`${srv.host}:${srv.port}:${d.deviceType}:${d.deviceNumber}`] = !!r.connected;
                this.toast(`${d.deviceName} ${r.connected ? 'connected' : 'disconnected'}`, 'ok');
            } catch (e) {
                this.toast(`Connect failed: ${e.message}`, 'error');
            }
        },

        async alpacaProbe(srv, d) {
            const path = this._alpacaPathFor(d.deviceType);
            if (!path) {
                srv._probe = `No probe endpoint for ${d.deviceType} yet.`;
                return;
            }
            try {
                const info = await this.apiGet(
                    `/api/alpaca/${path}/info?host=${encodeURIComponent(srv.host)}&port=${srv.port}&device=${d.deviceNumber}`);
                srv._probe = JSON.stringify(info, null, 2);
            } catch (e) {
                srv._probe = 'Probe failed: ' + e.message;
            }
        },

        // ============================================================
        //                       Mosaic planner
        // ============================================================
        openMosaicPlanner() {
            if (!this.skyTarget) { this.toast('Select a target first', 'warn'); return; }
            this.mosaic.req.targetName = this.skyTarget.name || 'Target';
            this.mosaic.req.centreRaHours = this.skyTarget.ra ?? this.skyTarget.raHours ?? 0;
            this.mosaic.req.centreDecDeg  = this.skyTarget.dec ?? this.skyTarget.decDeg  ?? 0;
            // Auto-fill per-panel FOV from the active rig's FOV calculation (already on this.fov)
            this.mosaic.req.panelFovWidthDeg  = +(this.fov?.width  || 1).toFixed(3);
            this.mosaic.req.panelFovHeightDeg = +(this.fov?.height || 1).toFixed(3);
            this.mosaic.plan = null;
            this.mosaicOpen = true;
            this.updateMosaicPreview();
        },

        async updateMosaicPreview() {
            try {
                const plan = await this.apiPost('/api/mosaic/plan', this.mosaic.req);
                this.mosaic.plan = plan;
                // Draw an overlay on Aladin: clear previous, then add each panel as a rectangle
                this._mosaicDrawOverlay(plan);
            } catch (e) { /* keep last plan on transient errors */ }
        },

        _mosaicDrawOverlay(plan) {
            // Add a GeoJSON polyline-per-panel overlay to d3-celestial. Each
            // panel becomes one LineString in equatorial coords. The overlay
            // is keyed by .mosaic so subsequent re-draws replace it cleanly.
            if (!this._celestialReady || !plan?.panels) return;
            const halfW = plan.panelFovWidthDeg / 2;
            const halfH = plan.panelFovHeightDeg / 2;
            const features = plan.panels.map(p => {
                const raDeg = p.raHours * 15;
                const cosDec = Math.cos(p.decDeg * Math.PI / 180) || 1e-6;
                const ring = [
                    [raDeg - halfW/cosDec, p.decDeg - halfH],
                    [raDeg + halfW/cosDec, p.decDeg - halfH],
                    [raDeg + halfW/cosDec, p.decDeg + halfH],
                    [raDeg - halfW/cosDec, p.decDeg + halfH],
                    [raDeg - halfW/cosDec, p.decDeg - halfH]
                ];
                return { type: 'Feature', properties: { name: p.name },
                         geometry: { type: 'LineString', coordinates: ring } };
            });
            try {
                Celestial.add({
                    type: 'line',
                    redraw: () => {
                        Celestial.container.selectAll('.mosaic').remove();
                        Celestial.container.selectAll('.mosaic')
                            .data(features).enter().append('path')
                            .attr('class', 'mosaic')
                            .attr('d', f => Celestial.map({ type: 'FeatureCollection', features: [f] }))
                            .style('stroke', '#fbbf24').style('stroke-width', 1.5)
                            .style('fill', 'none');
                    }
                });
                Celestial.redraw();
            } catch (e) { console.warn('Mosaic overlay failed', e); }
        },

        mosaicTimeFormat(seconds) {
            const h = Math.floor(seconds / 3600);
            const m = Math.floor((seconds % 3600) / 60);
            return h > 0 ? `${h}h ${m}m` : `${m}m`;
        },

        async exportMosaicToSequencer(loadIntoEngine) {
            try {
                const r = await this.apiPost('/api/mosaic/to-sequence', {
                    mosaic: this.mosaic.req,
                    exposureSeconds: this.mosaic.req.exposureSeconds,
                    exposureCount: this.mosaic.req.exposureCount,
                    filterName: this.mosaic.filterName || null,
                    gain: null,
                    binning: 1,
                    loadIntoEngine: !!loadIntoEngine
                });
                this.toast(loadIntoEngine
                    ? `Loaded ${r.plan.panels.length}-panel mosaic into Advanced Sequencer`
                    : `Built ${r.plan.panels.length}-panel mosaic JSON (download below)`,
                    'ok');
                if (!loadIntoEngine) {
                    // Trigger a download for the JSON
                    const blob = new Blob([JSON.stringify(r.document, null, 2)], {type: 'application/json'});
                    const url = URL.createObjectURL(blob);
                    const a = document.createElement('a');
                    a.href = url; a.download = (r.document.name || 'mosaic') + '.json';
                    a.click();
                    URL.revokeObjectURL(url);
                } else {
                    // Hop into the Adv Sequencer tab so the user can see what landed
                    this.mosaicOpen = false;
                    this.tab = 'seqadv';
                    await this.loadAdvSeq();
                }
            } catch (e) { this.toast('Mosaic export failed: ' + e.message, 'error'); }
        }
    };
}

// Expose the Alpine root reference globally so HTML-rendered tree click handlers
// can call back into the component (innerHTML strings can't use Alpine directives).
document.addEventListener('alpine:initialized', () => {
    const root = document.querySelector('[x-data]');
    if (root) window.__alpineRoot = Alpine.$data(root);
});

class ApiError extends Error {
    constructor(status, body) {
        super(`HTTP ${status}: ${body.substring(0, 200)}`);
        this.status = status;
        this.body = body;
    }
}
