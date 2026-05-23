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
        // CLST-7: per-rig override for where the math runs.
        // "auto" = let the server pick based on WASM handshake (default).
        // "server" / "client" = force. Hydrated from active rig +
        // persisted via PUT /api/equipment/rigs/{id}.
        liveStackComputeMode: 'auto',

        // LSTR-5: live-stack auto-refocus + auto-recenter triggers.
        // Mirror of EquipmentProfile.LiveStackTriggers — hydrated from
        // /api/livestack/triggers/status on first load and on rig
        // switch, written back via debounced PUT on any field change.
        liveStackTriggers: {
            refocusEnabled: false,
            refocusEveryNFrames: 30,
            refocusEveryMinutes: 0,
            refocusTempDeltaC: 0,
            refocusHfrIncreasePercent: 0,
            refocusRequest: {
                steps: 9, stepSize: 50, exposureSeconds: 3,
                minStars: 5, backlashSteps: 0
            },
            recenterEnabled: false,
            recenterEveryNFrames: 50,
            recenterEveryMinutes: 0,
            recenterDriftArcsec: 0,
            recenterToleranceArcsec: 30
        },
        liveStackStatus: null,    // { isRunning, frameCount, ..., triggers: {...} }

        // Mount
        mount: {
            ra: null, dec: null, alt: null, az: null,
            tracking: false, slewing: false, parked: false,
            pierSide: 'unknown', connected: false
        },

        // Floating mount control inside the Sky tab. Position lives
        // in localStorage so the panel comes back where the user left
        // it. _drag is transient state held during a mouse/touch drag.
        mountPanel: { x: 24, y: 80, visible: true },
        _mountDrag: null,

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
            imageOutputDir: '',
            imageNamePattern: '',
            stellariumHost: 'localhost',
            stellariumPort: 8090,
            preferAdvancedSequencer: false,
            // External tools — see ExternalTools section in Settings.
            // Empty = auto-detect (BinaryLocator on the server picks
            // the right path for the host OS).
            sirilPath: '',
            sirilScriptsDir: '',
            graxpertPath: '',
            graxpertBgeSmoothing: 1.0,
            graxpertBgeCorrection: 'Subtraction',
            graxpertDeconStrength: 0.5,
            graxpertDeconPsfSize: 4.0,
            graxpertDenoiseStrength: 0.5,
            // Main Telescope OTA optics (mirrored from active rig).
            // Bound to the Main Telescope card on the RIGS tab; edits
            // persist via saveOpticsDebounced -> saveCurrentSelectionsToRig.
            aperture: 0,
            telescopeBrand: '',
            telescopeModel: '',
            accessoryType: '',
            accessoryModel: '',
            accessoryFactor: 1.0,
            requiredBackspacingMm: null,
            // Guidescope optics (mirrored from active rig).
            guiderFocalLengthMm: 200,
            guiderApertureMm: 50,
            guideTelescopeBrand: '',
            guideTelescopeModel: ''
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
        // Derived: the active rig's display name. The home panel uses
        // this in places like `x-show="activeRig"` + "Rig: {name}".
        // Defined as a getter so Alpine reacts when rigs or activeRigId
        // change. Falls back to '' so x-show treats it as falsy when
        // no rig is loaded yet.
        get activeRig() {
            const r = this.rigs && this.rigs.find(x => x.id === this.activeRigId);
            return r ? r.name : '';
        },

        // Telescope + optical-accessory catalogues (lazy-loaded from
        // wwwroot/data/ when the Manage Rigs modal opens). Drives
        // the picker dropdowns and the "Required backspacing"
        // readout. See loadOpticsCatalogue().
        opticsCatalogue: { telescopes: [], accessories: [], loaded: false },

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

        // Camera driver state (DSLR support). cameraDriver picks
        // which backend the next Select call uses ('indi' default,
        // 'canon-edsdk'/'nikon-sdk'/'sony-sdk' for vendor SDKs).
        // cameraDrivers is populated once from /api/camera/drivers;
        // cameraVendorDevices is the per-driver discovery payload
        // refreshed by the "Detect" button.
        cameraDriver: 'indi',
        cameraDrivers: [],
        cameraVendorDevices: [],
        cameraDiscovering: false,
        cameraIso: 800,

        // Mount driver state — same shape as camera. Today the
        // dropdown shows INDI + synscan-wifi (the rest of the
        // catalogue is "(not installed)"). mountDriver drives the
        // ?driver= query param on /api/telescope/select; the
        // equipMountChoice input doubles as "INDI device name" or
        // "host:port" depending on driver.
        mountDriver: 'indi',
        mountDrivers: [],

        // PREVIEW tab — snap test shots. Defaults match what a
        // typical "is this thing focused / framed?" check would use:
        // 2s exposure, modest gain, 1x1 binning. Save-to-disk is
        // off by default because the whole point of PREVIEW is
        // "look without committing".
        preview: {
            exposure: 2.0,
            gain: 100,
            binning: 1,
            filter: '',          // empty = keep current filter
            saveToDisk: false,
            targetName: 'snap',
            busy: false,
            looping: false,
            lastSnapAt: null,
            lastStats: null      // { mean, median, stdev, starCount, hfr, min, max }
        },

        // Server-side continuous video stream. Started by the Stream
        // button in PREVIEW; backend's CameraStreamService auto-picks
        // native CCD_VIDEO_STREAM vs server-loop based on camera
        // capabilities. Updated each second from /ws/status.
        cameraStream: {
            running: false,
            mode: 'idle',         // 'idle' | 'native' | 'loop'
            fps: 0,
            frames: 0,
            exposure: 0.1,
            gain: 0,
            supportsNative: false,
            lastError: null
        },

        // VIDEO tab state — planetary capture (SER) + lucky-imaging stack.
        // Driven by VideoRecordingService + PlanetaryStackerService on the
        // server; the WS status feed populates videoRecording / videoStack.
        videoTab: 'capture',       // 'capture' | 'process'
        video: {
            exposure: 0.05,
            gain: 200,
            binning: 1,
            targetName: 'planet',
            maxDurationSec: 60,
            wbR: 50,
            wbB: 50,
            // Process side
            processSerPath: '',
            serList: [],          // [{ path, label }]
            keepPercent: 50,
            outputName: 'stack'
        },
        // Per-camera capability flags loaded from /api/camera/status on
        // tab open. Drives WB-slider visibility + future
        // ROI / cooler / ISO conditional UI inside VIDEO Capture.
        cameraCaps: {
            cooler: false, binning: false, roi: false, iso: false,
            bulb: false, videoStream: false, whiteBalance: false
        },
        videoRecording: {
            recording: false, path: null, frames: 0, bytes: 0,
            durationSec: 0, droppedFrames: 0, lastError: null
        },
        videoStack: null,         // { id, phase, framesAnalyzed, ..., done }

        // Auto slew-preview state. SlewPreviewService streams its
        // decision flags here so the SKY tab inset can fade itself in
        // and out without per-tab polling.
        slewPreview: {
            enabled: true, active: false, slewing: false, captureIdle: true,
            lastCheckedAt: null, lastError: null
        },

        // Activity bar (bottom). Populated from the status WS message
        // each second. host comes from HostMetricsService; sirilActiveJobs
        // and graXpertActiveJobs are compact summaries of the respective
        // ActiveJobs surfaces. The chip row is computed locally via the
        // activityChips() helper so we don't duplicate state.
        host: {
            cpuPercent: null, memoryPercent: null,
            memoryUsedMB: 0, memoryTotalMB: 0,
            processCpuPercent: 0, processMemoryMB: 0
        },
        sirilActiveJobs: [],
        graXpertActiveJobs: [],

        // SIM-6: built-in equipment simulator. WS payload populates
        // `simulator` once a second; `simulatorSettings` mirrors the
        // persisted UserProfile fields and is loaded on first Settings
        // tab visit. Defaults match the server's sensible-defaults so
        // the UI renders coherently before the first GET completes.
        simulator: {
            kind: 'none', isSupported: false, installed: false,
            version: null, devicesAvailable: [],
            isRunning: false, runningDevices: [],
            launchedAt: null, lastError: null, downloadUrl: ''
        },
        simulatorSettings: {
            autoStart: false,
            devices: ['ccd', 'telescope', 'focus', 'wheel'],
            port: 7624
        },
        _simulatorSettingsLoaded: false,

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
        guideChartTickCount: 0, // visible heartbeat for the guide chart
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

        // PH2X tab + state. guideTab toggles between the existing
        // JSON-RPC control panel and the xpra-hosted GUI iframe.
        guideTab: 'control',
        phd2AlgoPresetNames: [],
        phd2ActivePreset: 'Default',
        phd2AlgoParams: null,            // { axes: { ra: {Hyst:0.1, ...}, dec: {...} } }
        smartCalibrate: { slewToEquator: false },
        phd2GuiSession: null,            // { supportedOs, xpraInstalled, running, port, ... }
        phd2GuiBusy: false,

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

        // Weather forecast (7Timer via /api/weather/forecast).
        // forecast is the raw DTO from the backend. weatherDays() /
        // weatherBestWindows() (declared below) derive view-model data
        // on the fly, using SunCalc for sun + moon ephemeris.
        weather: { forecast: null, loading: false, error: '', lastFetched: null },
        _weatherLastKey: '',

        // Studio (post-processing) — ST-1 frame browser + ST-2 viewer
        studio: {
            frames: [],
            stats: null,
            rescan: null,
            filter: { type: '', target: '', filter: '' },
            selectedIds: [],
            // Viewer modal state (ST-2). When `viewer.frame` is set the
            // modal is open. Stretch params drive /api/studio/frames/:id/preview;
            // the URL is bumped through `viewer.previewUrl` after each
            // debounced slider change.
            viewer: {
                frame: null,
                previewUrl: '',
                stretch: { black: null, mid: null, white: null },
                showStars: false,
                stats: null,
                loadingStats: false,
                exporting: false,
                lastExport: '',
                // ST-6: per-frame operations (debayer / bgextract)
                // share a single in-flight flag + result line.
                opRunning: false,
                lastOp: ''
            },
            // ST-3: master-frame creation dialog. type/method drive the
            // POST body; lastJob carries the rolling progress payload
            // returned by /api/studio/masters/{jobId}/status.
            master: {
                open: false,
                running: false,
                type: 'Dark',
                method: 'SigmaClippedMean',
                lastJob: null
            },
            // ST-4: light calibration dialog. master ids = null means
            // auto-match per light; setting an id pins that master for
            // the whole batch. masters.{darks,flats,biases} populated
            // on dialog open from the library.
            calibrate: {
                open: false,
                running: false,
                darkId: null,
                flatId: null,
                biasId: null,
                masters: { darks: [], flats: [], biases: [] },
                lastJob: null
            },
            // ST-5: batch stack (integrate). method drives the
            // per-pixel reducer (same enum the master-frame service
            // uses). lastJob carries job progress + final counts
            // (combined/dropped/total exposure).
            integrate: {
                open: false,
                running: false,
                method: 'SigmaClippedMean',
                lastJob: null
            }
        },
        _studioRescanPoll: null,
        _studioViewerDebounce: null,
        _studioHistogramChart: null,
        _studioMasterPoll: null,
        _studioCalibratePoll: null,
        _studioIntegratePoll: null,

        // Observatory location helpers (Settings → Observatory)
        obsAddressQuery: '',
        obsAddressLoading: false,
        obsAddressError: '',
        obsAddressResults: [],
        obsGpsLoading: false,

        // Tonight's Best — ranked list from /api/sky/tonights-best, plus
        // a per-name thumbnail cache filled on demand by _kickTonightThumbs.
        tonight: {
            items: [], envelope: null, loading: false, error: '',
            lastFetched: null, filter: 'all', fitsFovOnly: false,
            thumbs: {}      // { [name]: { url, title, credit, missing } }
        },
        _tonightLastKey: '',

        // --- FILES tab state ---
        // cwd lives in localStorage so a refresh keeps the user in the
        // same folder. selectedPaths is a flat array of full paths
        // (Alpine handles reactivity better with arrays than Sets).
        // clipboard is null when there's nothing pending; { mode, paths,
        // sourceDir } when the user has hit Cut or Copy.
        files: {
            cwd: '',
            entries: [],
            roots: [],
            showHidden: false,
            selectedPaths: [],
            clipboard: null,
            loading: false,
            error: '',
            preview: { open: false, path: '', name: '', kind: '', textContent: null }
        },
        _filesLastShiftIndex: -1,    // anchor for shift-click range selection

        // External tools (Siril + GraXpert). status is the snapshot
        // from /api/{tool}/status; scripts is the script catalogue.
        // jobs (later) will hold active Siril/GraXpert job summaries.
        siril: {
            status: null,        // { available, binaryPath, version, scriptsCount }
            scripts: [],         // [{ name, path, source }]
            jobs: [],            // active jobs polled from /api/siril/jobs
            modalOpen: false,
            modalScriptName: '',
            modalTargetName: '',
            modalLights: [],     // string[] of full paths
            modalDarks: [],
            modalFlats: [],
            modalBiases: [],
            modalInjectBge: false,   // pre-process each light with GraXpert BGE
            modalBgePhase: null,     // null | { jobId, total, done, failed }
            currentJobId: null,
            currentJob: null,
            _pollTimer: null
        },
        // GraXpert state mirrors Siril's. modalOp distinguishes which
        // of the three operations the modal is currently driving.
        graxpert: {
            status: null,        // { available, binaryPath, version, supportsDeconvolution, supportsDenoising }
            modalOpen: false,
            modalOp: 'background-extraction',
            modalPaths: [],
            modalSmoothing: 1.0,
            modalCorrection: 'Subtraction',
            modalSaveBackground: false,
            modalDeconStrength: 0.5,
            modalDeconPsfSize: 4.0,
            modalDenoiseStrength: 0.5,
            currentJobId: null,
            currentJob: null,
            _pollTimer: null
        },

        // d3-celestial Sky Viewer (offline, BSD-3-Clause).
        // Always renders the live sky from the observer's location at the
        // current UTC time, in horizontal projection — same convention as
        // ASIAIR. No mode toggle: we only support equatorial mounts, so an
        // alternate "equatorial chart" view would just duplicate this one
        // with a different rotation axis and worse UX (drag pivoting
        // around the celestial pole feels wildly off-axis).
        _celestialReady: false,
        _fovLayerId: null,
        _skyTicker: null,                // setInterval handle for datetime refresh
        skyClock: '',                    // displayed in the toolbar (HH:MM:SS UTC)
        locationLabel: '',               // "City, Country" if reverse-geocoded, else "5.18°S 37.36°W"
        _locationLastKey: '',            // memoise so we only reverse-geocode once per coord pair
        aladinFov: 90,                  // initial FOV in degrees (wide field)
        aladinShowFov: true,             // toggle camera-FOV overlay

        // OpenSeadragon image viewer
        imageViewerOpen: false,
        _osdViewer: null,
        // URL the viewer should load. Defaults to the live-camera
        // preview; FILES tab overrides it to point at any file. Reset
        // to default on close so the next open from any other tab
        // still gets the latest frame.
        imageViewerUrl: '/api/image/latest/preview',
        imageViewerTitle: 'Image Viewer — full resolution',
        // FITS header overlay panel inside the image viewer. Open
        // automatically when previewing a .fits/.fit/.fts file from
        // the FILES tab — toggleable by the user via the toolbar
        // button. Visibility is sticky in localStorage so power users
        // who always want headers visible don't have to keep clicking.
        fitsHeaders: {
            visible: localStorage.getItem('fitsHeadersVisible') !== '0',
            loading: false,
            data: null,         // { fileName, totalCards, groups: [{name, cards: [{keyword,value,comment}]}] }
            error: '',
            path: ''
        },

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

        // End-of-run actions (post-sequence housekeeping). Default = nothing.
        // Mirrors the SequenceEndActions DTO on the server.
        endActions: {
            parkMount: false,
            stopTracking: false,
            warmCamera: false,
            disconnectGuider: false,
            runOnStop: false,
            autoGraXpert: false
        },
        seqEndExpanded: false,
        _endActionsSaveTimer: null,

        // In-flight request tracking
        _pending: {},
        _previewFetching: false,
        _imageClientId: null,

        init() {
            this.updateClock();
            setInterval(() => this.updateClock(), 1000);
            this.updateFov();

            // CLST-2: listen for the WASM module's "ready" signal so
            // we can flip the offload-capable flag + log it. CLST-4
            // turns this into the actual frame-pipeline dispatch.
            this.wasmReady = false;
            this.wasmVersion = null;
            window.addEventListener('nina-wasm-ready', (e) => {
                this.wasmReady = true;
                this.wasmVersion = (e.detail && e.detail.version) || 'unknown';
                console.log('[Polaris] WASM live-stack ready, ' + this.wasmVersion);
            });

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
            this.loadEndActions();
            this.loadSirilStatus();
            this.loadGraxpertStatus();
            this.loadAtlasTypes();
            this.loadRigs();
            // Telescope/accessory catalog feeds the Main Telescope card
            // dropdowns on the RIGS tab and the Manage Rigs modal. Load
            // upfront so the dropdowns are populated on first open.
            this.loadOpticsCatalogue();
            // LSTR-5: live-stack triggers settings (per-rig). Loaded
            // upfront so the LIVE tab's <details> panel renders with
            // the persisted values on first open instead of showing
            // defaults that then flicker to the real values.
            this.loadLiveStackTriggers();
            this.loadCameraDrivers();
            this.loadMountDrivers();
            this.restoreMountPanel();
            window.addEventListener('resize', () => this._clampMountPanel());
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
                // CLST-4: if WASM is ready, ask for raw mode (uint16 +
                // LZ4) so the client-side stacker can do its work.
                // Otherwise stick with JPEG (universally supported,
                // server-side stacked preview already encoded).
                const mode = this.wasmReady ? 'raw' : 'jpeg';
                ws.send(JSON.stringify({ mode }));
                // Tell the server we can stack client-side. CLST-5 will
                // act on this (flip LiveStackingService → MetricsOnly).
                // Until CLST-5 ships the server logs + ignores it;
                // sending now is benign.
                ws.send(JSON.stringify({
                    type: 'client-capability',
                    wasm: !!this.wasmReady,
                    wasmVersion: this.wasmVersion || null
                }));
            };
            // Re-send capability + switch mode when WASM finishes
            // loading after the WS opens (race common on first page
            // load because WASM init is async).
            window.addEventListener('nina-wasm-ready', () => {
                if (ws.readyState === WebSocket.OPEN) {
                    ws.send(JSON.stringify({ mode: 'raw' }));
                    ws.send(JSON.stringify({
                        type: 'client-capability',
                        wasm: true,
                        wasmVersion: this.wasmVersion
                    }));
                }
            }, { once: true });

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
                // Mirror to PREVIEW canvas so switching tabs shows the
                // latest frame without a re-fetch.
                this._mirrorLiveToPreviewCanvas();
            };
            img.onerror = () => URL.revokeObjectURL(url);
            img.src = url;
        },

        // Copy whatever is currently on liveCanvas to every secondary
        // canvas that wants a copy of the latest frame (PREVIEW tab,
        // FOCUS tab auto-focus preview). Cheap — single drawImage per
        // destination. Called at the end of every successful render
        // path so all tabs always show the most recent frame.
        _mirrorLiveToPreviewCanvas() {
            const src = document.getElementById('liveCanvas');
            if (!src) return;
            if (src.width === 0 || src.height === 0) return;
            for (const id of ['previewCanvas', 'focusCanvas', 'videoCaptureCanvas', 'slewPreviewCanvas']) {
                const dst = document.getElementById(id);
                if (!dst) continue;
                // Size the destination canvas to fit its container
                // while preserving the source aspect ratio. When the
                // tab isn't visible yet (display:none collapses the
                // container), copy at native dimensions so the bitmap
                // is ready when the user switches tabs.
                const container = dst.parentElement;
                if (!container) continue;
                const containerW = container.clientWidth;
                const containerH = container.clientHeight;
                if (containerW <= 0 || containerH <= 0) {
                    dst.width = src.width;
                    dst.height = src.height;
                } else {
                    const scale = Math.min(containerW / src.width, containerH / src.height, 1);
                    dst.width = Math.round(src.width * scale);
                    dst.height = Math.round(src.height * scale);
                }
                const ctx = dst.getContext('2d');
                ctx.imageSmoothingEnabled = true;
                ctx.imageSmoothingQuality = 'high';
                ctx.drawImage(src, 0, 0, dst.width, dst.height);
            }
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
        // CLST-7: persist the live-stack compute mode override (auto /
        // server / client) into the active rig. The server's mode
        // evaluator re-runs on rig-switch and immediately on the next
        // WS handshake event, so the new setting takes effect within
        // ~1 second without an explicit refresh.
        async saveLiveStackComputeMode() {
            try {
                const rig = this.rigs.find(r => r.id === this.activeRigId);
                if (!rig) return;
                // Server's PUT merges only the fields present in the
                // body, so we can patch just this one without round-
                // tripping the whole rig object. (See EquipmentEndpoints
                // — the null-check on update.LiveStackComputeMode
                // keeps other fields untouched.)
                await this.apiPost('/api/equipment/rigs/' + encodeURIComponent(rig.id), null, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ liveStackComputeMode: this.liveStackComputeMode })
                });
                rig.liveStackComputeMode = this.liveStackComputeMode;
                this.toast(`Live-stack compute: ${this.liveStackComputeMode}`, 'ok');
            } catch (e) {
                this.toast('Save failed: ' + (e.message || e), 'error');
            }
        },

        // CLST-6: upload the WASM-accumulated stack to the server as a
        // FITS. Reads the latest cached raw frame for dimensions +
        // metadata; the actual pixels come from the WASM module's
        // GetStackedResult (NOT the cached frame — those might be a
        // single frame's worth, not the accumulator).
        async saveClientStack() {
            if (!this.wasmReady) {
                this.toast('WASM not ready yet — wait for the live-stack module to load.', 'warn');
                return;
            }
            const interop = globalThis.NINA?.Polaris?.Wasm?.Interop;
            if (!interop) { this.toast('WASM Interop missing.', 'error'); return; }

            const [w, h] = interop.GetDimensions();
            if (w === 0 || h === 0) {
                this.toast('No frames stacked yet.', 'warn');
                return;
            }
            const stackedInt32 = interop.GetStackedResult();
            if (!stackedInt32 || stackedInt32.length === 0) {
                this.toast('Stack is empty.', 'warn');
                return;
            }

            // Pack int[] → uint16 LE bytes for the POST body.
            const bytes = new Uint8Array(stackedInt32.length * 2);
            const dv = new DataView(bytes.buffer);
            for (let i = 0; i < stackedInt32.length; i++) {
                dv.setUint16(i * 2, stackedInt32[i] & 0xFFFF, /* littleEndian */ true);
            }

            const target = (this.seqStatus?.currentTarget
                            || this.skyTarget?.name
                            || 'live-stack').replace(/[^A-Za-z0-9_\-]/g, '_');
            const frameCount = this.liveStackFrames || 0;
            const bitDepth = this._lastRawFrame?.bitDepth || 16;
            const url = `/api/livestack/upload-result?width=${w}&height=${h}`
                      + `&bitDepth=${bitDepth}&target=${encodeURIComponent(target)}`
                      + `&frameCount=${frameCount}`;

            this.toast('Uploading stack...', 'info');
            try {
                const resp = await fetch(url, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/octet-stream' },
                    body: bytes
                });
                if (!resp.ok) {
                    const err = await resp.text();
                    throw new Error(`HTTP ${resp.status}: ${err}`);
                }
                const data = await resp.json();
                this.toast(`Stack saved: ${data.savedPath}`, 'success');
            } catch (e) {
                console.error('saveClientStack failed', e);
                this.toast('Save failed: ' + (e.message || e), 'error');
            }
        },

        // CLST-4: feed a raw uint16 frame to the WASM stacker and
        // return the running-mean accumulator for display. Side
        // effect: posts a 'client-stack-progress' message back to the
        // server so the LSTR trigger orchestrator gets HFR + star
        // count even though the server itself isn't accumulating
        // anymore (MetricsOnly mode).
        _stackViaWasm(pixels, width, height) {
            const interop = globalThis.NINA.Polaris.Wasm.Interop;
            // JSExport marshaller doesn't grok Uint16Array directly;
            // pass through Int32Array (free aliasing, no per-element
            // copy — JS just reinterprets the buffer view).
            const asInt32 = new Int32Array(pixels.length);
            for (let i = 0; i < pixels.length; i++) asInt32[i] = pixels[i];

            const metrics = interop.AddFrame(asInt32, width, height);
            const [frameCount, hfrX100, starCount, alignmentOk, _reserved] = metrics;

            // Send the metrics back to the server so the trigger
            // orchestrator (LiveStackTriggersService) sees the same
            // numbers it'd get from server-side StarDetector.
            if (this.imageWs && this.imageWs.readyState === WebSocket.OPEN) {
                this.imageWs.send(JSON.stringify({
                    type: 'client-stack-progress',
                    frameCount,
                    hfr: hfrX100 / 100,
                    starCount,
                    alignmentOk: !!alignmentOk
                }));
            }

            // Pull the accumulated stack out for display. The
            // GetStackedResult export returns int[] (JSExport doesn't
            // marshal ushort[]); widen back to Uint16Array via the
            // low 16 bits.
            const stackedInt32 = interop.GetStackedResult();
            const out = new Uint16Array(stackedInt32.length);
            for (let i = 0; i < stackedInt32.length; i++) out[i] = stackedInt32[i] & 0xFFFF;
            return out;
        },

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
            let pixels = new Uint16Array(decompressed.buffer);
            const maxVal = (1 << bitDepth) - 1;

            // CLST-4: when the server tells us it's in MetricsOnly
            // mode and our WASM module is loaded, route this frame
            // through the WASM stacker instead of treating it as the
            // displayable result. WASM accumulates → we display its
            // running mean. While the server stays in Full mode the
            // raw frames it relays are ALREADY the accumulated stack,
            // so feeding them to WASM again would compound — only run
            // the WASM path when the server is opted into metrics-only.
            const serverMode = this.liveStackStatus?.mode || 'full';
            if (this.wasmReady && serverMode === 'metricsonly'
                && globalThis.NINA?.Polaris?.Wasm?.Interop) {
                try {
                    pixels = this._stackViaWasm(pixels, width, height);
                } catch (e) {
                    console.warn('[Polaris] WASM stack failed, rendering raw frame as-is:', e);
                }
            }

            // Cache the (possibly WASM-accumulated) frame so manual-
            // stretch slider changes re-render against the latest
            // accumulator without waiting for the next capture.
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
                    // Refresh the home-dashboard location label whenever the
                    // profile loads / changes. Synchronous formatted-coords
                    // fallback first, then an async best-effort reverse-geocode
                    // upgrades it to "City, Country" if we can reach Nominatim.
                    this._refreshLocationLabel();
                    this.settings.sensorWidth = data.sensorWidthMm || 23.5;
                    this.settings.sensorHeight = data.sensorHeightMm || 15.7;
                    this.settings.focalLength = data.focalLengthMm || 478;
                    this.settings.imageFormat = data.imageFormat || 'fits';
                    this.settings.imageOutputDir = data.imageOutputDir || '';
                    this.settings.imageNamePattern = data.imageNamePattern || '';
                    this.settings.preferAdvancedSequencer = !!data.preferAdvancedSequencer;
                    this.settings.sirilPath = data.sirilPath || '';
                    this.settings.sirilScriptsDir = data.sirilScriptsDir || '';
                    this.settings.graxpertPath = data.graxpertPath || '';
                    this.settings.graxpertBgeSmoothing = data.graxpertBgeSmoothing ?? 1.0;
                    this.settings.graxpertBgeCorrection = data.graxpertBgeCorrection || 'Subtraction';
                    this.settings.graxpertDeconStrength = data.graxpertDeconStrength ?? 0.5;
                    this.settings.graxpertDeconPsfSize = data.graxpertDeconPsfSize ?? 4.0;
                    this.settings.graxpertDenoiseStrength = data.graxpertDenoiseStrength ?? 0.5;
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
                    // All-sky projection (Aitoff). d3-celestial flags
                    // stereographic / orthographic / azimuthal* as
                    // clip:true (hemisphere-only) — the horizon marker
                    // only draws correctly in non-clipped, all-sky
                    // projections. Aitoff is the standard astronomy
                    // all-sky projection (oval, equal-area-ish, both
                    // celestial hemispheres visible at once).
                    projection: 'aitoff',
                    // d3-celestial only ships an 'equatorial' transform
                    // (and a useless 'supergalactic'); there is NO
                    // 'horizontal' transform. Setting it silently fell
                    // back to equatorial. We instead express the zenith
                    // in equatorial coords [LST*15, lat] and put THAT at
                    // the projection centre — then d3.geo.zoom's drag
                    // naturally rotates around the zenith because it's
                    // the centred point.
                    transform: 'equatorial',
                    // follow: 'center' leaves the projection centred on
                    // whatever we pass via `center`. We pick the celestial
                    // pole matching the observer's hemisphere by default
                    // (NCP if lat ≥ 0, SCP if lat < 0); if a mount is
                    // connected, we sync to its RA/Dec instead — see
                    // _computeInitialCentre below.
                    follow: 'center',
                    center: initialCentre,
                    // Observer position — used to draw the horizon line
                    // and place sun/moon correctly above/below it.
                    geopos: [lat, lng],
                    location: true,
                    zoomlevel: null,
                    zoomextend: 10,
                    interactive: true,
                    // Let drag rotate the *orientation* (the third / roll
                    // component of projection.rotate) instead of locking
                    // it to 0. With orientationfixed:false, dragging the
                    // sky around the centred point (the zenith for us)
                    // pivots around that point — exactly the compass-
                    // rotation feel we want, instead of the polar-axis
                    // spin you get with orientationfixed:true.
                    orientationfixed: false,
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
                        // Show the equatorial grid (RA/Dec lines) on top of the
                        // horizontal projection — useful for an equatorial
                        // mount user to relate the live sky to RA/Dec coords.
                        equatorial: { show: true, stroke: '#aaffaa', width: 1, opacity: 0.35 },
                        ecliptic:   { show: true, stroke: '#ffcc66', width: 1, opacity: 0.4 },
                        horizon:    { show: true, stroke: '#cccccc', width: 5.0, opacity: 0.7 },
                        galactic:   { show: false }
                    },
                    // Horizon marker — shown when location is set and the map
                    // is an all-sky projection. Thick light-grey line with the
                    // below-horizon area filled solid black at 0.7 opacity.
                    horizon: { show: true, stroke: '#cccccc', width: 5.0, fill: '#000000', opacity: 0.7 },
                    // Daylight overlay disabled — d3-celestial's built-in
                    // daytime sky tint is a bright blue that washes out the
                    // stars + constellation lines. For an astrophotography
                    // planning view we always want a night-mode render even
                    // if the sun is up, so the user can find their target now.
                    daylight: { show: false }
                });

                // Push current UTC date so horizon, sun and moon positions
                // are accurate for the live view.
                try { Celestial.date(new Date()); } catch {}
                // Re-apply the centre after display() — belt-and-braces
                // against any internal reset during initial draw. Wrapped
                // in a microtask so it runs after Celestial finishes its
                // synchronous initialisation.
                Promise.resolve().then(() => {
                    try { Celestial.rotate({ center: initialCentre }); } catch {}
                });
                this._startSkyTicker(lat, lng);

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
                        // invert returns [RA°, Dec°] (projection is equatorial).
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
                console.log('d3-celestial ready (live horizontal projection)');
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

        // Returns the [RA_deg, Dec_deg, orientation_deg] tuple to use as
        // the projection centre on initial load:
        //   - mount connected → sync to its RA/Dec
        //   - otherwise → celestial pole matching the observer's
        //     hemisphere (NCP if lat ≥ 0, SCP if lat < 0), with RA set
        //     to the current Local Sidereal Time so the meridian of the
        //     observer's zenith becomes the projection's vertical
        //     centreline. That way the horizon line — perpendicular to
        //     the zenith direction — comes out horizontal on screen
        //     instead of tilted at whatever angle the sidereal time
        //     happens to be at when the page loaded.
        // Dec is clamped to ±89.5° because d3.geo's stereographic rotate
        // hits a singularity at exactly ±90° and silently no-ops.
        _computeInitialCentre(lat, lng) {
            const mountRa = this.mount?.ra;
            const mountDec = this.mount?.dec;
            const mountConnected = this.mount?.connected && mountRa != null && mountDec != null;

            if (mountConnected) {
                return [mountRa * 15, Math.max(-89.5, Math.min(89.5, mountDec)), 0];
            }
            const poleDec = lat >= 0 ? 89.5 : -89.5;
            const lstHours = this._localSiderealTime(new Date(), lng);
            const raDeg = (lstHours * 15) % 360;
            return [raDeg, poleDec, 0];
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

        // Inverse of _equatorialToHorizontal: convert observed [az, alt] back
        // to [raHours, decDeg] for the given observer/UTC. Used when the user
        // clicks somewhere on the horizontal-projection sky map and we need
        // to know which celestial coords they picked.
        _horizontalToEquatorial(azDeg, altDeg, latDeg, lngDeg, when) {
            const azRad  = azDeg  * Math.PI / 180;
            const altRad = altDeg * Math.PI / 180;
            const latRad = latDeg * Math.PI / 180;
            const sinDec = Math.sin(altRad) * Math.sin(latRad)
                         + Math.cos(altRad) * Math.cos(latRad) * Math.cos(azRad);
            const dec = Math.asin(Math.max(-1, Math.min(1, sinDec)));
            const cosDec = Math.cos(dec);
            if (Math.abs(cosDec) < 1e-9) {
                // At the celestial pole RA is undefined; return LST so the
                // pick is at least self-consistent (HA = 0).
                return [this._localSiderealTime(when, lngDeg), dec * 180 / Math.PI];
            }
            const sinHA = -Math.sin(azRad) * Math.cos(altRad) / cosDec;
            const cosHA = (Math.sin(altRad) - Math.sin(latRad) * sinDec)
                        / (Math.cos(latRad) * cosDec);
            const haRad = Math.atan2(sinHA, cosHA);
            const haHours = ((haRad * 180 / Math.PI) / 15 + 24) % 24;
            const lstHours = this._localSiderealTime(when, lngDeg);
            const raHours = ((lstHours - haHours) + 24) % 24;
            return [raHours, dec * 180 / Math.PI];
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
                    const decClamped = Math.max(-89.5, Math.min(89.5, mountDec));
                    try { Celestial.rotate({ center: [mountRa * 15, decClamped, 0] }); } catch {}
                }
                this._updateSkyClock();
            }, 30_000);
            this._updateSkyClock();
        },

        _stopSkyTicker() {
            if (this._skyTicker) { clearInterval(this._skyTicker); this._skyTicker = null; }
        },

        // Format lat/lng in N/S E/W form (more readable than negative numbers).
        // Examples: 5.18°S 37.36°W, 40.71°N 74.01°W.
        _formatCoords(lat, lng) {
            const ns = lat >= 0 ? 'N' : 'S';
            const ew = lng >= 0 ? 'E' : 'W';
            return `${Math.abs(lat).toFixed(2)}°${ns} ${Math.abs(lng).toFixed(2)}°${ew}`;
        },

        // Refresh the location label shown on the Home dashboard. Always sets
        // a synchronous formatted-coords fallback so something appears
        // immediately even if we're offline; kicks off an async Nominatim
        // reverse-geocode in the background to upgrade to "City, Country"
        // when we can reach the network. Memoised by lat/lng key so we don't
        // re-query for the same coords on every settings refresh.
        async _refreshLocationLabel() {
            const lat = this.settings.latitude;
            const lng = this.settings.longitude;
            if (lat == null || lng == null
                || (Math.abs(lat) < 0.01 && Math.abs(lng) < 0.01)) {
                this.locationLabel = '';
                this._locationLastKey = '';
                return;
            }
            // Synchronous fallback first.
            this.locationLabel = this._formatCoords(lat, lng);
            const key = `${lat.toFixed(3)},${lng.toFixed(3)}`;
            if (key === this._locationLastKey) return;
            this._locationLastKey = key;
            // Best-effort reverse geocode. Nominatim is free, no API key, but
            // requires a custom User-Agent (browser sets one automatically)
            // and rate-limits to 1 req/sec — fine for one-off lookups on
            // settings changes.
            try {
                const url = `https://nominatim.openstreetmap.org/reverse?format=json&zoom=10&lat=${lat}&lon=${lng}`;
                const r = await fetch(url, { headers: { 'Accept-Language': 'en' } });
                if (!r.ok) return;
                const j = await r.json();
                const a = j.address || {};
                const city = a.city || a.town || a.village || a.hamlet
                            || a.municipality || a.county || a.state_district || a.state;
                const country = a.country;
                if (city && country) {
                    this.locationLabel = `${city}, ${country}`;
                } else if (country) {
                    // Fall back to country + coords if no locality matched.
                    this.locationLabel = `${country} · ${this._formatCoords(lat, lng)}`;
                }
                // Otherwise leave the coords-only fallback in place.
            } catch (e) {
                // Offline or blocked — silent fallback to coords.
            }
        },

        // ─── Observatory location helpers (Settings → Observatory) ───────

        // Look up the user-typed address via the backend Nominatim proxy
        // and render the top results as clickable cards. Picking one
        // writes its lat/lng into settings, persists, and refreshes the
        // home-page location label.
        async lookupObservatoryAddress() {
            const q = (this.obsAddressQuery || '').trim();
            if (!q) return;
            this.obsAddressLoading = true;
            this.obsAddressError = '';
            this.obsAddressResults = [];
            try {
                const r = await this.apiGet(`/api/system/geocode?query=${encodeURIComponent(q)}&limit=5`);
                this.obsAddressResults = Array.isArray(r) ? r : [];
                if (!this.obsAddressResults.length) {
                    this.obsAddressError = 'No matches — try a more specific search (city, state, country).';
                }
            } catch (e) {
                this.obsAddressError = 'Address lookup failed: ' + (e.message || 'unknown error');
            } finally {
                this.obsAddressLoading = false;
            }
        },

        adoptObservatoryResult(r) {
            this.settings.latitude  = Number(r.latitude.toFixed(4));
            this.settings.longitude = Number(r.longitude.toFixed(4));
            this.obsAddressResults  = [];
            this.obsAddressQuery    = r.displayName;
            this.saveSettings();
            this._refreshLocationLabel();
        },

        // Use the browser's Geolocation API. Requires user permission
        // and a secure context (localhost is fine, plain-HTTP LAN
        // hosts are NOT — modern browsers gate this on https://).
        useBrowserLocation() {
            if (!('geolocation' in navigator)) {
                this.obsAddressError = 'Geolocation is not supported by this browser.';
                return;
            }
            this.obsGpsLoading = true;
            this.obsAddressError = '';
            navigator.geolocation.getCurrentPosition(
                (pos) => {
                    this.settings.latitude  = Number(pos.coords.latitude.toFixed(4));
                    this.settings.longitude = Number(pos.coords.longitude.toFixed(4));
                    if (pos.coords.altitude != null) {
                        this.settings.altitude = Math.round(pos.coords.altitude);
                    }
                    this.obsGpsLoading = false;
                    this.saveSettings();
                    this._refreshLocationLabel();
                },
                (err) => {
                    this.obsGpsLoading = false;
                    const map = {
                        1: 'Permission denied — allow location in the browser address bar.',
                        2: 'Position unavailable. GPS / Wi-Fi positioning may be off.',
                        3: 'Timed out waiting for a location fix.'
                    };
                    this.obsAddressError = map[err.code]
                        || ('Geolocation error: ' + err.message);
                },
                { enableHighAccuracy: true, timeout: 15000, maximumAge: 60000 }
            );
        },

        // ─── STUDIO (post-processing) — ST-1 frame browser ───────────────

        async loadStudio() {
            try {
                const params = new URLSearchParams();
                if (this.studio.filter.type)   params.set('type',   this.studio.filter.type);
                if (this.studio.filter.target) params.set('target', this.studio.filter.target);
                if (this.studio.filter.filter) params.set('filter', this.studio.filter.filter);
                params.set('limit', '200');
                const [frames, stats, rescan] = await Promise.all([
                    this.apiGet(`/api/studio/frames?${params.toString()}`),
                    this.apiGet(`/api/studio/stats`),
                    this.apiGet(`/api/studio/rescan/status`)
                ]);
                this.studio.frames = frames || [];
                this.studio.stats  = stats;
                this.studio.rescan = rescan;
                // Continue polling the rescan status while it's in progress
                // so the toolbar progress label updates without a manual refresh.
                if (rescan?.inProgress && !this._studioRescanPoll) {
                    this._studioRescanPoll = setInterval(async () => {
                        try {
                            const r = await this.apiGet('/api/studio/rescan/status');
                            this.studio.rescan = r;
                            if (!r?.inProgress) {
                                clearInterval(this._studioRescanPoll);
                                this._studioRescanPoll = null;
                                // Reload list with newly indexed frames.
                                this.loadStudio();
                            }
                        } catch { /* keep polling */ }
                    }, 2000);
                }
            } catch (e) {
                this.toast?.('Studio load failed: ' + e.message, 'error');
            }
        },

        async studioRescan() {
            try {
                await this.apiPost('/api/studio/rescan', {});
                this.toast?.('Rescan started…', 'info');
                this.loadStudio();
            } catch (e) {
                this.toast?.('Rescan failed: ' + e.message, 'error');
            }
        },

        studioToggleSelect(id) {
            const idx = this.studio.selectedIds.indexOf(id);
            if (idx >= 0) this.studio.selectedIds.splice(idx, 1);
            else this.studio.selectedIds.push(id);
        },

        // Return absolute file paths for the currently-selected frames.
        // Used by sirilOpenRunModal to prefill the lights list.
        studioSelectedLightPaths() {
            const set = new Set(this.studio.selectedIds);
            return this.studio.frames
                .filter(f => set.has(f.id))
                .map(f => f.path);
        },

        // ─── ST-2: Single-frame viewer ────────────────────────────────────

        // Open the viewer modal for a frame. Fetches the auto-stretch
        // defaults so the sliders start at sensible values, then triggers
        // the first preview render + stats load.
        async studioOpenViewer(frame) {
            this.studio.viewer.frame = frame;
            this.studio.viewer.stats = null;
            this.studio.viewer.lastExport = '';
            this.studio.viewer.stretch = { black: null, mid: null, white: null };
            this.studio.viewer.previewUrl = '';
            try {
                const a = await this.apiGet(`/api/studio/frames/${frame.id}/autostretch`);
                this.studio.viewer.stretch = {
                    black: +a.black.toFixed(4),
                    mid:   +a.mid.toFixed(4),
                    white: +a.white.toFixed(4)
                };
            } catch { /* server will compute defaults on first preview */ }
            this._studioRenderPreview();
            this._studioLoadStats(frame.id);
        },

        studioCloseViewer() {
            this.studio.viewer.frame = null;
            this.studio.viewer.previewUrl = '';
            this.studio.viewer.stats = null;
            if (this._studioHistogramChart) {
                this._studioHistogramChart.destroy();
                this._studioHistogramChart = null;
            }
        },

        // Triggered by slider input. Coalesces rapid drags into one
        // render request — 150 ms is short enough to feel live, long
        // enough to skip 80% of intermediate frames during a drag.
        studioStretchChanged() {
            clearTimeout(this._studioViewerDebounce);
            this._studioViewerDebounce = setTimeout(() => this._studioRenderPreview(), 150);
        },

        async studioAutoStretch() {
            const fr = this.studio.viewer.frame;
            if (!fr) return;
            try {
                const a = await this.apiGet(`/api/studio/frames/${fr.id}/autostretch`);
                this.studio.viewer.stretch = {
                    black: +a.black.toFixed(4),
                    mid:   +a.mid.toFixed(4),
                    white: +a.white.toFixed(4)
                };
                this._studioRenderPreview();
            } catch (e) {
                this.toast?.('Auto stretch failed: ' + e.message, 'error');
            }
        },

        _studioRenderPreview() {
            const fr = this.studio.viewer.frame;
            if (!fr) return;
            const s = this.studio.viewer.stretch;
            const qs = new URLSearchParams();
            if (s.black != null) qs.set('black', s.black);
            if (s.mid   != null) qs.set('mid',   s.mid);
            if (s.white != null) qs.set('white', s.white);
            // cache-bust so the <img> actually re-fetches after slider tweaks
            qs.set('_t', Date.now());
            this.studio.viewer.previewUrl = `/api/studio/frames/${fr.id}/preview?${qs.toString()}`;
        },

        async _studioLoadStats(frameId) {
            this.studio.viewer.loadingStats = true;
            try {
                const s = await this.apiGet(`/api/studio/frames/${frameId}/stats?stars=true`);
                this.studio.viewer.stats = s;
                this.$nextTick(() => this._studioRenderHistogram());
            } catch (e) {
                this.toast?.('Stats failed: ' + e.message, 'error');
            } finally {
                this.studio.viewer.loadingStats = false;
            }
        },

        _studioRenderHistogram() {
            const stats = this.studio.viewer.stats;
            if (!stats?.histogram) return;
            const canvas = document.getElementById('studio-histogram');
            if (!canvas || typeof Chart === 'undefined') return;
            if (this._studioHistogramChart) this._studioHistogramChart.destroy();
            this._studioHistogramChart = new Chart(canvas, {
                type: 'bar',
                data: {
                    labels: stats.histogram.map((_, i) => i),
                    datasets: [{
                        data: stats.histogram,
                        backgroundColor: '#9cb3ff',
                        borderWidth: 0,
                        barPercentage: 1.0,
                        categoryPercentage: 1.0
                    }]
                },
                options: {
                    animation: false,
                    responsive: true,
                    maintainAspectRatio: false,
                    plugins: { legend: { display: false }, tooltip: { enabled: false } },
                    scales: {
                        x: { display: false },
                        // Log scale flattens the huge background spike that
                        // would otherwise dwarf the highlight tail.
                        y: { type: 'logarithmic', display: false }
                    }
                }
            });
        },

        // ─── ST-3: Master frame creation ─────────────────────────────────

        // Open the create-master modal. Auto-detect type from the
        // majority IMAGETYP across the selection — usually the user
        // already filtered to one type, but the override is there in
        // case they didn't.
        studioOpenMasterDialog() {
            const selectedRows = this.studio.frames.filter(f => this.studio.selectedIds.includes(f.id));
            const tally = {};
            for (const f of selectedRows) {
                const t = (f.imageType || 'LIGHT').toUpperCase();
                tally[t] = (tally[t] || 0) + 1;
            }
            // Map FITS IMAGETYP to our enum names.
            const winner = Object.keys(tally).sort((a, b) => tally[b] - tally[a])[0] || 'DARK';
            this.studio.master.type = winner === 'BIAS'      ? 'Bias'
                                     : winner === 'FLAT'     ? 'Flat'
                                     : winner === 'DARKFLAT' ? 'DarkFlat'
                                     : 'Dark';   // default for anything else
            this.studio.master.method = 'SigmaClippedMean';
            this.studio.master.lastJob = null;
            this.studio.master.open = true;
        },

        studioCloseMasterDialog() {
            this.studio.master.open = false;
            if (this._studioMasterPoll) {
                clearInterval(this._studioMasterPoll);
                this._studioMasterPoll = null;
            }
        },

        async studioStartMaster() {
            if (this.studio.selectedIds.length < 2) return;
            this.studio.master.running = true;
            this.studio.master.lastJob = { stage: 'queued', done: 0, total: this.studio.selectedIds.length };
            try {
                const resp = await this.apiPost('/api/studio/masters', {
                    frameIds: this.studio.selectedIds,
                    type:     this.studio.master.type,
                    method:   this.studio.master.method
                });
                const r = await resp.json();
                // Poll the job until it finishes. Long jobs can run for
                // minutes on a 32MP × 30-frame stack; 1 Hz polling is
                // plenty responsive without hammering the server.
                this._studioMasterPoll = setInterval(async () => {
                    try {
                        const s = await this.apiGet(`/api/studio/masters/${r.jobId}/status`);
                        this.studio.master.lastJob = s;
                        if (!s.inProgress) {
                            clearInterval(this._studioMasterPoll);
                            this._studioMasterPoll = null;
                            this.studio.master.running = false;
                            if (s.stage === 'done') {
                                this.toast?.('Master frame written → ' + s.outputPath, 'ok');
                                // Refresh browser so the new master shows up.
                                this.loadStudio();
                            } else if (s.stage === 'error') {
                                this.toast?.('Master integration failed: ' + s.error, 'error');
                            }
                        }
                    } catch { /* swallow transient failure, keep polling */ }
                }, 1000);
            } catch (e) {
                this.studio.master.running = false;
                this.studio.master.lastJob = { stage: 'error', error: e.message, done: 0, total: 0 };
                this.toast?.('Master start failed: ' + e.message, 'error');
            }
        },

        // ─── ST-4: Light frame calibration ───────────────────────────────

        studioHasLightSelected() {
            return this.studio.frames.some(f =>
                this.studio.selectedIds.includes(f.id) &&
                (f.imageType || '').toUpperCase() === 'LIGHT');
        },

        studioLightCount() {
            return this.studio.frames.filter(f =>
                this.studio.selectedIds.includes(f.id) &&
                (f.imageType || '').toUpperCase() === 'LIGHT').length;
        },

        async studioOpenCalibrateDialog() {
            // Pull the available masters from the library so the
            // override dropdowns aren't empty. One full /frames call
            // is fine — the user typically has a handful of masters.
            this.studio.calibrate.darkId = null;
            this.studio.calibrate.flatId = null;
            this.studio.calibrate.biasId = null;
            this.studio.calibrate.lastJob = null;
            try {
                const all = await this.apiGet('/api/studio/frames?limit=500');
                const byType = t => all.filter(f =>
                    (f.imageType || '').toUpperCase() === 'MASTER' + t);
                this.studio.calibrate.masters = {
                    darks:  byType('DARK'),
                    flats:  byType('FLAT'),
                    biases: byType('BIAS')
                };
            } catch (e) {
                this.toast?.('Could not load master list: ' + e.message, 'error');
                this.studio.calibrate.masters = { darks: [], flats: [], biases: [] };
            }
            this.studio.calibrate.open = true;
        },

        studioCloseCalibrateDialog() {
            this.studio.calibrate.open = false;
            if (this._studioCalibratePoll) {
                clearInterval(this._studioCalibratePoll);
                this._studioCalibratePoll = null;
            }
        },

        async studioStartCalibrate() {
            // Filter selectedIds down to LIGHTs only — calibrating a
            // dark by accident produces noise frames in calibrated/.
            const lightIds = this.studio.frames
                .filter(f => this.studio.selectedIds.includes(f.id) &&
                             (f.imageType || '').toUpperCase() === 'LIGHT')
                .map(f => f.id);
            if (lightIds.length === 0) return;

            this.studio.calibrate.running = true;
            this.studio.calibrate.lastJob = {
                stage: 'queued', done: 0, total: lightIds.length,
                succeeded: 0, failed: 0
            };
            try {
                const resp = await this.apiPost('/api/studio/calibrate', {
                    lightIds:     lightIds,
                    masterDarkId: this.studio.calibrate.darkId,
                    masterFlatId: this.studio.calibrate.flatId,
                    masterBiasId: this.studio.calibrate.biasId
                });
                const r = await resp.json();
                this._studioCalibratePoll = setInterval(async () => {
                    try {
                        const s = await this.apiGet(`/api/studio/calibrate/${r.jobId}/status`);
                        this.studio.calibrate.lastJob = s;
                        if (!s.inProgress) {
                            clearInterval(this._studioCalibratePoll);
                            this._studioCalibratePoll = null;
                            this.studio.calibrate.running = false;
                            const ok = s.succeeded || 0;
                            const fail = s.failed || 0;
                            const msg = `Calibration done — ${ok} OK` + (fail > 0 ? `, ${fail} failed` : '');
                            this.toast?.(msg, fail > 0 ? 'warning' : 'ok');
                            this.loadStudio();
                        }
                    } catch { /* swallow transient failure */ }
                }, 1000);
            } catch (e) {
                this.studio.calibrate.running = false;
                this.studio.calibrate.lastJob = {
                    stage: 'error', error: e.message,
                    done: 0, total: 0, succeeded: 0, failed: 0
                };
                this.toast?.('Calibration start failed: ' + e.message, 'error');
            }
        },

        // ─── ST-5: Batch stack (integrate) ──────────────────────────────

        studioOpenIntegrateDialog() {
            this.studio.integrate.method = 'SigmaClippedMean';
            this.studio.integrate.lastJob = null;
            this.studio.integrate.open = true;
        },

        studioCloseIntegrateDialog() {
            this.studio.integrate.open = false;
            if (this._studioIntegratePoll) {
                clearInterval(this._studioIntegratePoll);
                this._studioIntegratePoll = null;
            }
        },

        async studioStartIntegrate() {
            if (this.studio.selectedIds.length < 2) return;
            this.studio.integrate.running = true;
            this.studio.integrate.lastJob = {
                stage: 'queued',
                done: 0,
                total: this.studio.selectedIds.length,
                combined: 0,
                dropped: 0,
                totalExposureSec: 0
            };
            try {
                const resp = await this.apiPost('/api/studio/integrate', {
                    frameIds: this.studio.selectedIds,
                    method:   this.studio.integrate.method
                });
                const r = await resp.json();
                // Long-running job: align + integrate of 20 × 20 MP can
                // run for several minutes on a RPi. 1 Hz poll is fine.
                this._studioIntegratePoll = setInterval(async () => {
                    try {
                        const s = await this.apiGet(`/api/studio/integrate/${r.jobId}/status`);
                        this.studio.integrate.lastJob = s;
                        if (!s.inProgress) {
                            clearInterval(this._studioIntegratePoll);
                            this._studioIntegratePoll = null;
                            this.studio.integrate.running = false;
                            if (s.stage === 'done') {
                                this.toast?.(
                                    `Stack done — ${s.combined} combined` +
                                    (s.dropped > 0 ? `, ${s.dropped} dropped` : '') +
                                    ` → ${s.outputPath}`,
                                    s.dropped > 0 ? 'warning' : 'ok'
                                );
                                this.loadStudio();
                            } else if (s.stage === 'error') {
                                this.toast?.('Integration failed: ' + s.error, 'error');
                            }
                        }
                    } catch { /* swallow transient failure */ }
                }, 1000);
            } catch (e) {
                this.studio.integrate.running = false;
                this.studio.integrate.lastJob = {
                    stage: 'error', error: e.message,
                    done: 0, total: 0, combined: 0, dropped: 0, totalExposureSec: 0
                };
                this.toast?.('Integration start failed: ' + e.message, 'error');
            }
        },

        // ─── ST-6: Per-frame operations (debayer / bgextract) ───────

        async studioDebayer() {
            const fr = this.studio.viewer.frame;
            if (!fr) return;
            this.studio.viewer.opRunning = true;
            this.studio.viewer.lastOp = '';
            try {
                const resp = await this.apiPost(`/api/studio/frames/${fr.id}/debayer`);
                const r = await resp.json();
                this.studio.viewer.lastOp = r.path;
                this.toast?.('Debayered → ' + r.path, 'ok');
                this.loadStudio();
            } catch (e) {
                this.toast?.('Debayer failed: ' + e.message, 'error');
            } finally {
                this.studio.viewer.opRunning = false;
            }
        },

        async studioRemoveGradient() {
            const fr = this.studio.viewer.frame;
            if (!fr) return;
            this.studio.viewer.opRunning = true;
            this.studio.viewer.lastOp = '';
            try {
                // Defaults match BackgroundExtractor.Options.Default;
                // expose them in the UI later if anyone wants finer
                // control without re-deploying.
                const resp = await this.apiPost(`/api/studio/frames/${fr.id}/bgextract`);
                const r = await resp.json();
                this.studio.viewer.lastOp = r.path;
                this.toast?.('Background removed → ' + r.path, 'ok');
                this.loadStudio();
            } catch (e) {
                this.toast?.('Background extraction failed: ' + e.message, 'error');
            } finally {
                this.studio.viewer.opRunning = false;
            }
        },

        async studioNoiseReduce() {
            const fr = this.studio.viewer.frame;
            if (!fr) return;
            this.studio.viewer.opRunning = true;
            this.studio.viewer.lastOp = '';
            try {
                // radius=2 default = subtle smoothing. Knobs deliberately
                // not exposed in v1; the user can re-run with explicit
                // ?radius= via curl if they want to tune.
                const resp = await this.apiPost(`/api/studio/frames/${fr.id}/nr`);
                const r = await resp.json();
                this.studio.viewer.lastOp = r.path;
                this.toast?.('Noise reduced → ' + r.path, 'ok');
                this.loadStudio();
            } catch (e) {
                this.toast?.('Noise reduction failed: ' + e.message, 'error');
            } finally {
                this.studio.viewer.opRunning = false;
            }
        },

        async studioSharpen() {
            const fr = this.studio.viewer.frame;
            if (!fr) return;
            this.studio.viewer.opRunning = true;
            this.studio.viewer.lastOp = '';
            try {
                // amount=1.0 default = moderate sharpen. Knobs deliberately
                // not exposed in v1; the user can re-run with explicit
                // ?amount=&radius=&threshold= via curl if they want to tune.
                const resp = await this.apiPost(`/api/studio/frames/${fr.id}/sharpen`);
                const r = await resp.json();
                this.studio.viewer.lastOp = r.path;
                this.toast?.('Sharpened → ' + r.path, 'ok');
                this.loadStudio();
            } catch (e) {
                this.toast?.('Sharpen failed: ' + e.message, 'error');
            } finally {
                this.studio.viewer.opRunning = false;
            }
        },

        async studioExport(format) {
            const fr = this.studio.viewer.frame;
            if (!fr) return;
            this.studio.viewer.exporting = true;
            this.studio.viewer.lastExport = '';
            try {
                const s = this.studio.viewer.stretch;
                const qs = new URLSearchParams({ format });
                if (s.black != null) qs.set('black', s.black);
                if (s.mid   != null) qs.set('mid',   s.mid);
                if (s.white != null) qs.set('white', s.white);
                // TIFF defaults to *linear* 16-bit so downstream PixInsight /
                // Siril can re-process without our stretch baked in. PNG/JPG
                // always get the stretched 8-bit view.
                if (format === 'tif') qs.set('stretched', 'false');
                const resp = await this.apiPost(`/api/studio/frames/${fr.id}/export?${qs.toString()}`, {});
                const r = await resp.json();
                this.studio.viewer.lastExport = r.path;
                this.toast?.(`Exported ${format.toUpperCase()} → ${r.path}`, 'ok');
            } catch (e) {
                this.toast?.('Export failed: ' + e.message, 'error');
            } finally {
                this.studio.viewer.exporting = false;
            }
        },

        // ─── Weather forecast (7Timer via /api/weather/forecast) ──────────

        async loadWeatherForecast(force = false) {
            const lat = this.settings.latitude;
            const lng = this.settings.longitude;
            if (lat == null || lng == null
                || (Math.abs(lat) < 0.01 && Math.abs(lng) < 0.01)) {
                this.weather.error = 'Set your observing location in Settings first.';
                this.weather.forecast = null;
                return;
            }
            const key = `${lat.toFixed(2)},${lng.toFixed(2)}`;
            // Skip refetch within the cache window unless the caller wants
            // to force (e.g. Refresh button). Backend has its own 15 min
            // cache so even a force-refresh storm is harmless.
            if (!force && key === this._weatherLastKey && this.weather.forecast?.available) return;
            this._weatherLastKey = key;
            this.weather.loading = true;
            this.weather.error = '';
            try {
                const r = await this.apiGet(`/api/weather/forecast?lat=${lat}&lon=${lng}`);
                this.weather.forecast = r;
                this.weather.lastFetched = new Date();
                if (!r?.available) {
                    this.weather.error = r?.error || 'Forecast unavailable';
                }
            } catch (e) {
                this.weather.error = 'Could not reach forecast service';
                this.weather.forecast = null;
            } finally {
                this.weather.loading = false;
            }
        },

        // 7Timer cloudcover: 1 (0–6%) → 9 (94–100%). Convert to mid-bucket %.
        _cloudPercent(bucket) {
            const map = { 1: 3, 2: 13, 3: 31, 4: 50, 5: 68, 6: 81, 7: 88, 8: 94, 9: 98 };
            return map[bucket] ?? 0;
        },

        _scoreClass(score) {
            if (score >= 70) return 'weather-slot--good';
            if (score >= 40) return 'weather-slot--meh';
            return 'weather-slot--bad';
        },

        // Pick a weather emoji that summarises a single slot. Precipitation
        // trumps everything; otherwise we map the cloudcover bucket to a
        // sun-and-clouds icon during the day, and to a moon variant at
        // night. Precipitation glyphs are intentionally not day/night-
        // sensitive — rain looks like rain regardless of the sun's position.
        _weatherIconFor(cloud, prec, isNight = false) {
            const p = (prec || 'none').toLowerCase();
            if (p === 'snow')                       return '🌨️';
            if (p === 'icep' || p === 'frzr')       return '🧊';
            if (p === 'rain' && cloud >= 8)         return '⛈️';
            if (p === 'rain')                       return '🌧️';
            if (isNight) {
                // Unicode doesn't ship "moon-behind-cloud" glyphs in a
                // reliable cross-platform set, so we collapse the partial
                // and mostly-cloudy night buckets into ☁️ — at night the
                // actually-useful signal for astrophotography is "is the
                // sky clear or not". Crescent moon stays for ≤ 50% cloud.
                if (cloud <= 2) return '🌙';
                if (cloud <= 4) return '🌙';
                return '☁️';
            }
            if (cloud <= 2)  return '☀️';   // 0–13% cloud
            if (cloud <= 4)  return '🌤️';   // 13–50%
            if (cloud <= 6)  return '⛅';    // 50–81%
            if (cloud <= 8)  return '🌥️';   // 81–94%
            return '☁️';                    // 94–100%
        },

        // Day-average summary icon: average cloud cover bucket across all
        // slots, and the worst precip type that occurred. Lets the user
        // glance at the per-day header and immediately know "tomorrow is
        // mostly cloudy" without parsing 8 numbers.
        _summariseDayWeather(slots) {
            if (!slots?.length) return { icon: '', avgCloud: 0 };
            const avgBucket = Math.round(
                slots.reduce((a, s) => a + s.raw.cloudCover, 0) / slots.length);
            // Worst precip wins. Order matters: snow/ice > rain > none.
            const precRank = { 'none': 0, '': 0, 'rain': 1, 'snow': 2, 'icep': 2, 'frzr': 2 };
            const worstPrec = slots
                .map(s => (s.raw.precType || 'none').toLowerCase())
                .reduce((a, b) => (precRank[b] ?? 0) > (precRank[a] ?? 0) ? b : a, 'none');
            return {
                icon: this._weatherIconFor(avgBucket, worstPrec),
                avgCloud: this._cloudPercent(avgBucket)
            };
        },

        _moonIconForPhase(phase) {
            // SunCalc returns moon phase in [0,1]: 0=new, 0.25=first qtr,
            // 0.5=full, 0.75=last qtr. Pick the closest emoji bucket.
            if (phase < 0.0625 || phase >= 0.9375) return '🌑';
            if (phase < 0.1875) return '🌒';
            if (phase < 0.3125) return '🌓';
            if (phase < 0.4375) return '🌔';
            if (phase < 0.5625) return '🌕';
            if (phase < 0.6875) return '🌖';
            if (phase < 0.8125) return '🌗';
            return '🌘';
        },

        _fmtLocalTime(d) {
            return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
        },

        // Group raw forecast slots into per-local-day buckets, attaching
        // sun + moon ephemeris (SunCalc) and pre-formatted display strings
        // so the template stays declarative.
        weatherDays() {
            const f = this.weather.forecast;
            if (!f?.available || !f.slots?.length) return [];
            const lat = this.settings.latitude || 0;
            const lng = this.settings.longitude || 0;
            const todayMs = new Date().setHours(0, 0, 0, 0);
            // Bucket by local date string.
            const buckets = new Map();
            for (const raw of f.slots) {
                const utc = new Date(raw.utcStart);
                const localKey = utc.toLocaleDateString();
                if (!buckets.has(localKey)) buckets.set(localKey, []);
                const slotDate = utc;
                // Day/night classification — use slot midpoint (slot start
                // + 1.5h) so a slot that straddles sunset isn't mis-tagged.
                // SunCalc gives us sunrise/sunset for the slot's local date.
                let isNight = false;
                if (typeof SunCalc !== 'undefined') {
                    const midUtc = new Date(utc.getTime() + 90 * 60 * 1000);
                    const localDay = new Date(midUtc); localDay.setHours(12, 0, 0, 0);
                    const dayTimes = SunCalc.getTimes(localDay, lat, lng);
                    if (dayTimes.sunrise && dayTimes.sunset) {
                        isNight = midUtc < dayTimes.sunrise || midUtc > dayTimes.sunset;
                    }
                }
                buckets.get(localKey).push({
                    raw,
                    utcMs:      utc.getTime(),
                    localTime:  this._fmtLocalTime(slotDate),
                    score:      raw.observationScore,
                    scoreClass: this._scoreClass(raw.observationScore),
                    cloudLabel: this._cloudPercent(raw.cloudCover) + '%',
                    icon:       this._weatherIconFor(raw.cloudCover, raw.precType, isNight),
                    isNight,
                    tooltip:    `${this._fmtLocalTime(slotDate)}  · score ${raw.observationScore}\n`
                              + `Cloud ${this._cloudPercent(raw.cloudCover)}%`
                              + `  · Seeing ${raw.seeing}/8  · Transp ${raw.transparency}/8\n`
                              + `${raw.temp2m.toFixed(1)}°C  · RH ${raw.rh2m}%`
                              + `  · Wind ${raw.windSpeed} (${raw.windDirection})`
                              + (raw.precType && raw.precType !== 'none' ? `\nPrecip: ${raw.precType}` : '')
                });
            }
            // Order by date key (chronological).
            const out = [];
            for (const [key, slots] of buckets) {
                const refDate = new Date(slots[0].utcMs);
                const dayStart = new Date(refDate); dayStart.setHours(0, 0, 0, 0);
                const dayOffset = (dayStart.getTime() - todayMs) / 86400000;
                let headerName;
                if (Math.abs(dayOffset) < 0.5) headerName = 'Tonight';
                else if (Math.abs(dayOffset - 1) < 0.5) headerName = 'Tomorrow';
                else headerName = refDate.toLocaleDateString(undefined, { weekday: 'long' });
                const headerDate = refDate.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });

                // Sun/moon ephemeris. SunCalc takes Date in local time but
                // computes everything in UTC under the hood — we feed it
                // the date at local noon to avoid edge cases at midnight.
                const noon = new Date(refDate); noon.setHours(12, 0, 0, 0);
                let sun = {}, moon = {}, moonIllum = {};
                if (typeof SunCalc !== 'undefined') {
                    sun       = SunCalc.getTimes(noon, lat, lng);
                    moon      = SunCalc.getMoonTimes(noon, lat, lng);
                    moonIllum = SunCalc.getMoonIllumination(noon);
                }
                const summary = this._summariseDayWeather(slots);
                out.push({
                    dateKey:           key,
                    headerName, headerDate,
                    slots,
                    weatherIcon:       summary.icon,
                    avgCloudLabel:     summary.avgCloud + '% avg cloud',
                    sunset:            sun.sunset,
                    sunrise:           sun.sunriseEnd || sun.sunrise,
                    sunsetLabel:       sun.sunset    ? this._fmtLocalTime(sun.sunset)    : '',
                    sunriseLabel:      sun.sunrise   ? this._fmtLocalTime(sun.sunrise)   : '',
                    duskAstro:         sun.nightEnd ? sun.night : null,
                    dawnAstro:         sun.nightEnd,
                    duskAstroLabel:    sun.night     ? this._fmtLocalTime(sun.night)     : '—',
                    dawnAstroLabel:    sun.nightEnd  ? this._fmtLocalTime(sun.nightEnd)  : '—',
                    moonIcon:          this._moonIconForPhase(moonIllum.phase ?? 0),
                    moonIllumination:  Math.round((moonIllum.fraction ?? 0) * 100),
                });
            }
            return out;
        },

        // Find continuous runs of slots (in chronological order) with
        // score ≥ 70 that fall between sunset and sunrise of tonight.
        // Returns top 3 by total duration × average score.
        weatherBestWindows() {
            const days = this.weatherDays();
            if (!days.length) return [];
            const lat = this.settings.latitude || 0;
            const lng = this.settings.longitude || 0;
            const slots = this.weather.forecast.slots
                .map(s => ({ ...s, utc: new Date(s.utcStart) }))
                .sort((a, b) => a.utc - b.utc);
            // "Tonight" = first sunset onward through next sunrise.
            const now = new Date();
            let sunsetT = null, sunriseT = null;
            if (typeof SunCalc !== 'undefined') {
                const today = SunCalc.getTimes(now, lat, lng);
                sunsetT  = today.sunset;
                const tomorrow = SunCalc.getTimes(new Date(now.getTime() + 86400000), lat, lng);
                sunriseT = tomorrow.sunrise;
                // Edge case: it's already past sunset.
                if (sunsetT < now) sunsetT = now;
            }
            const nightSlots = slots.filter(s => {
                if (!sunsetT || !sunriseT) return true;
                return s.utc >= sunsetT && s.utc <= sunriseT;
            });
            // Group consecutive good slots into runs.
            const runs = [];
            let run = null;
            for (const s of nightSlots) {
                if (s.observationScore >= 70) {
                    if (!run) run = { start: s.utc, end: s.utc, scores: [], slots: [] };
                    run.end = new Date(s.utc.getTime() + 3 * 3600 * 1000);
                    run.scores.push(s.observationScore);
                    run.slots.push(s);
                } else if (run) {
                    runs.push(run); run = null;
                }
            }
            if (run) runs.push(run);
            return runs.map(r => {
                const durMs = r.end - r.start;
                const hours = durMs / 3600000;
                const avg = Math.round(r.scores.reduce((a, b) => a + b, 0) / r.scores.length);
                const avgCloud = Math.round(
                    r.slots.reduce((a, s) => a + this._cloudPercent(s.cloudCover), 0) / r.slots.length);
                return {
                    startMs:        r.start.getTime(),
                    startLocal:     this._fmtLocalTime(r.start),
                    endLocal:       this._fmtLocalTime(r.end),
                    durationLabel:  hours >= 1 ? `${hours.toFixed(0)} h` : `${Math.round(hours * 60)} min`,
                    avgScore:       avg,
                    summary:        `${avgCloud}% cloud avg`,
                    _rank:          hours * avg
                };
            })
            .sort((a, b) => b._rank - a._rank)
            .slice(0, 3);
        },

        // ─── Tonight's Best (/api/sky/tonights-best) ─────────────────────

        async loadTonightsBest(force = false) {
            const lat = this.settings.latitude;
            const lng = this.settings.longitude;
            if (lat == null || lng == null
                || (Math.abs(lat) < 0.01 && Math.abs(lng) < 0.01)) {
                this.tonight.error = 'Set your observing location in Settings first.';
                this.tonight.items = [];
                return;
            }
            // Refresh once per UI mount; force=true bypasses (e.g. button).
            if (!force && this.tonight.items.length && this._tonightLastKey === lat + ',' + lng) return;
            this._tonightLastKey = lat + ',' + lng;
            this.tonight.loading = true;
            this.tonight.error = '';
            try {
                const r = await this.apiGet('/api/sky/tonights-best?limit=30');
                this.tonight.items = r.items || [];
                this.tonight.envelope = r;
                this.tonight.lastFetched = new Date();
                // Kick off thumbnail + per-card chart rendering after Alpine
                // commits the DOM with the new template instances.
                this.$nextTick(() => {
                    this.tonight.items.forEach(i => this._renderTonightChart(i));
                    this._kickTonightThumbs();
                });
            } catch (e) {
                this.tonight.error = 'Failed to load Tonight\'s Best: ' + (e.message || 'unknown error');
                this.tonight.items = [];
            } finally {
                this.tonight.loading = false;
            }
        },

        // --- FILES tab methods -----------------------------------------

        // First entry into the FILES tab: load the platform roots and
        // cd to either the persisted cwd, the current Studio root, or
        // the first root as a last resort.
        async filesInit() {
            try {
                this.files.roots = await this.apiGet('/api/files/roots');
            } catch (e) {
                this.files.roots = [];
                this.files.error = 'Failed to enumerate roots: ' + (e.message || '');
            }
            const remembered = localStorage.getItem('filesCwd');
            const target = remembered
                || this.settings.imageOutputDir
                || (this.files.roots[0]?.name ?? '');
            if (target) await this.filesCd(target);
        },

        // Navigate. Always clears selection because shift-click anchors
        // and per-row checkboxes assume the displayed list matches the
        // selection set.
        async filesCd(path) {
            if (!path) return;
            this.files.loading = true;
            this.files.error = '';
            try {
                const r = await this.apiGet('/api/files/list?path='
                    + encodeURIComponent(path)
                    + '&hidden=' + (this.files.showHidden ? 'true' : 'false'));
                this.files.cwd = r.path || path;
                this.files.entries = (r.entries || []).slice().sort(this._filesSortCmp);
                this.files.selectedPaths = [];
                this._filesLastShiftIndex = -1;
                localStorage.setItem('filesCwd', this.files.cwd);
            } catch (e) {
                this.files.entries = [];
                this.files.error = e.message || 'List failed';
                this.toast(this.files.error, 'error');
            } finally {
                this.files.loading = false;
            }
        },

        filesReload() { return this.filesCd(this.files.cwd); },

        // Folders first, then case-insensitive name order. Matches the
        // convention every desktop file manager uses.
        _filesSortCmp(a, b) {
            if (a.isDirectory !== b.isDirectory) return a.isDirectory ? -1 : 1;
            return a.name.localeCompare(b.name, undefined, { sensitivity: 'base' });
        },

        // Build the parent path for the ".." row + the up button.
        // Returns '' when we're at a root so the UI hides the shortcut.
        filesParentPath() {
            const cwd = this.files.cwd || '';
            if (!cwd) return '';
            // Strip trailing separator first, then drop the last segment.
            const trimmed = cwd.replace(/[\\/]+$/, '');
            const sep = trimmed.includes('\\') ? '\\' : '/';
            const idx = trimmed.lastIndexOf(sep);
            if (idx <= 0) {
                // Already at a top-level root (e.g. "C:\" or "/").
                return '';
            }
            const parent = trimmed.substring(0, idx);
            // On Windows "C:" needs the trailing backslash to be valid.
            if (/^[A-Za-z]:$/.test(parent)) return parent + '\\';
            return parent || sep;
        },

        // Breadcrumbs split + accumulated paths for nav.
        filesCrumbs() {
            const cwd = this.files.cwd || '';
            if (!cwd) return [];
            const sep = cwd.includes('\\') ? '\\' : '/';
            const parts = cwd.split(/[\\/]+/).filter((s, i) => s || i === 0);
            const out = [];
            let acc = '';
            for (let i = 0; i < parts.length; i++) {
                const p = parts[i];
                if (i === 0) {
                    // Anchor: drive letter on Windows ("C:") or "/" on Unix.
                    acc = p === '' ? sep : p + (sep === '\\' ? '\\' : '');
                    out.push({ label: p === '' ? '/' : p, path: acc });
                } else {
                    acc = acc.endsWith(sep) ? (acc + p) : (acc + sep + p);
                    out.push({ label: p, path: acc });
                }
            }
            return out;
        },

        // --- Selection -------------------------------------------------

        // Ctrl-click toggles, shift-click selects range from anchor,
        // plain click sets single-selection. Mirrors desktop conventions.
        filesToggleSelect(path, ev) {
            const idx = this.files.entries.findIndex(e => e.fullPath === path);
            if (idx < 0) return;

            const isMulti = ev && (ev.ctrlKey || ev.metaKey);
            const isRange = ev && ev.shiftKey && this._filesLastShiftIndex >= 0;

            if (isRange) {
                const lo = Math.min(idx, this._filesLastShiftIndex);
                const hi = Math.max(idx, this._filesLastShiftIndex);
                this.files.selectedPaths = this.files.entries
                    .slice(lo, hi + 1).map(e => e.fullPath);
                return;
            }

            if (isMulti) {
                const i = this.files.selectedPaths.indexOf(path);
                if (i >= 0) this.files.selectedPaths.splice(i, 1);
                else        this.files.selectedPaths.push(path);
            } else {
                // Plain click: if it's the only selection already, toggle off
                // (so the user can clear by clicking the same row twice).
                const already = this.files.selectedPaths.length === 1
                             && this.files.selectedPaths[0] === path;
                this.files.selectedPaths = already ? [] : [path];
            }
            this._filesLastShiftIndex = idx;
        },

        filesToggleAll(checked) {
            this.files.selectedPaths = checked
                ? this.files.entries.map(e => e.fullPath)
                : [];
        },

        filesSelectionSize() {
            const set = new Set(this.files.selectedPaths);
            return this.files.entries
                .filter(e => set.has(e.fullPath))
                .reduce((s, e) => s + (e.sizeBytes || 0), 0);
        },

        // Used by the Studio-root button: enabled only when exactly one
        // *folder* is selected.
        filesSelectedDir() {
            if (this.files.selectedPaths.length !== 1) return null;
            const entry = this.files.entries
                .find(e => e.fullPath === this.files.selectedPaths[0]);
            return (entry && entry.isDirectory) ? entry : null;
        },

        // --- Clipboard + mutations ------------------------------------

        filesCopy() {
            if (this.files.selectedPaths.length === 0) return;
            this.files.clipboard = {
                mode: 'copy',
                paths: this.files.selectedPaths.slice(),
                sourceDir: this.files.cwd
            };
            this.toast(`Copied ${this.files.clipboard.paths.length} item(s)`, 'info');
        },

        filesCut() {
            if (this.files.selectedPaths.length === 0) return;
            this.files.clipboard = {
                mode: 'cut',
                paths: this.files.selectedPaths.slice(),
                sourceDir: this.files.cwd
            };
            this.toast(`Cut ${this.files.clipboard.paths.length} item(s)`, 'info');
        },

        async filesPaste() {
            const cb = this.files.clipboard;
            if (!cb) return;
            const dstDir = this.files.cwd;
            // For each source path: join its leaf onto the dst dir.
            const sep = dstDir.includes('\\') ? '\\' : '/';
            let overwrite = false;
            let okCount = 0, failCount = 0;
            for (const src of cb.paths) {
                const leaf = src.split(/[\\/]+/).filter(Boolean).pop() || 'item';
                const dst = dstDir.endsWith(sep) ? (dstDir + leaf) : (dstDir + sep + leaf);
                try {
                    const url = '/api/files/' + (cb.mode === 'cut' ? 'move' : 'copy');
                    await this.apiPost(url,
                        { src, dst, overwrite },
                        { method: 'POST',
                          headers: { 'Content-Type': 'application/json' },
                          body: JSON.stringify({ src, dst, overwrite }) });
                    okCount++;
                } catch (e) {
                    // 409 means destination exists; ask once and retry the whole batch.
                    if ((e.message || '').includes('Destination exists') && !overwrite) {
                        if (window.confirm(
                                `${leaf} already exists in the destination. Overwrite this and any subsequent conflicts?`)) {
                            overwrite = true;
                            // Retry this one and keep going.
                            try {
                                const url = '/api/files/' + (cb.mode === 'cut' ? 'move' : 'copy');
                                await this.apiPost(url,
                                    { src, dst, overwrite: true },
                                    { method: 'POST',
                                      headers: { 'Content-Type': 'application/json' },
                                      body: JSON.stringify({ src, dst, overwrite: true }) });
                                okCount++;
                            } catch (e2) { failCount++; }
                        } else {
                            failCount++;
                        }
                    } else {
                        failCount++;
                    }
                }
            }
            this.toast(
                `Paste: ${okCount} ok, ${failCount} failed`,
                failCount > 0 ? 'warn' : 'ok');
            if (cb.mode === 'cut') this.files.clipboard = null;
            await this.filesReload();
        },

        async filesDelete() {
            if (this.files.selectedPaths.length === 0) return;
            const n = this.files.selectedPaths.length;
            if (!window.confirm(
                    `Delete ${n} item(s)? This is permanent — folders are removed recursively.`)) return;
            try {
                await this.apiPost('/api/files/delete', null, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ paths: this.files.selectedPaths, confirmed: true })
                });
                this.toast(`Deleted ${n} item(s)`, 'ok');
                await this.filesReload();
            } catch (e) {
                this.toast('Delete failed: ' + (e.message || ''), 'error');
            }
        },

        async filesMkdir() {
            const name = (window.prompt('New folder name:') || '').trim();
            if (!name) return;
            try {
                await this.apiPost('/api/files/mkdir', null, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ parent: this.files.cwd, name })
                });
                this.toast(`Created ${name}`, 'ok');
                await this.filesReload();
            } catch (e) {
                this.toast('mkdir failed: ' + (e.message || ''), 'error');
            }
        },

        async filesRename() {
            if (this.files.selectedPaths.length !== 1) return;
            const path = this.files.selectedPaths[0];
            const oldName = path.split(/[\\/]+/).filter(Boolean).pop() || '';
            const newName = (window.prompt('Rename to:', oldName) || '').trim();
            if (!newName || newName === oldName) return;
            try {
                await this.apiPost('/api/files/rename', null, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ path, newName })
                });
                this.toast(`Renamed to ${newName}`, 'ok');
                await this.filesReload();
            } catch (e) {
                this.toast('Rename failed: ' + (e.message || ''), 'error');
            }
        },

        // --- Upload + download ----------------------------------------

        // Upload via multipart POST per file. Server doesn't expose an
        // upload endpoint yet (planned in FB-7); show a friendly message
        // until then so the UI doesn't lie.
        async filesUpload(_fileList) {
            this.toast('Upload from client → server lands in a follow-up. ' +
                'For now copy files into the folder server-side.', 'info');
        },

        // Single file = browser-native download via direct GET so the
        // browser handles the Content-Disposition; multi = POST to the
        // ZIP endpoint, follow the streamed response.
        async filesDownload() {
            const sel = this.files.selectedPaths;
            if (sel.length === 0) return;
            if (sel.length === 1 && !this._filesIsSelectionDir(sel[0])) {
                this.filesDownloadOne(sel[0]);
                return;
            }
            await this._filesDownloadZip(sel);
        },

        _filesIsSelectionDir(path) {
            const e = this.files.entries.find(x => x.fullPath === path);
            return !!(e && e.isDirectory);
        },

        filesDownloadOne(path) {
            // window.location triggers the same dialog the user would
            // get from a direct link; honours Content-Disposition.
            window.location = '/api/files/download?path=' + encodeURIComponent(path);
        },

        async _filesDownloadZip(paths) {
            try {
                const fileName = (this.files.cwd.split(/[\\/]+/).filter(Boolean).pop()
                                  || 'polaris') + '-files.zip';
                const r = await fetch('/api/files/download-zip', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ paths, rootForNames: this.files.cwd, fileName })
                });
                if (!r.ok) throw new Error('HTTP ' + r.status);
                const blob = await r.blob();
                const url = URL.createObjectURL(blob);
                const a = document.createElement('a');
                a.href = url; a.download = fileName; document.body.appendChild(a);
                a.click();
                a.remove();
                URL.revokeObjectURL(url);
                this.toast(`Downloaded ${paths.length} item(s) as ${fileName}`, 'ok');
            } catch (e) {
                this.toast('ZIP download failed: ' + (e.message || ''), 'error');
            }
        },

        // --- Preview --------------------------------------------------

        async filesOpenPreview(entry) {
            if (!entry || entry.isDirectory) return;
            const ext = (entry.name.toLowerCase().split('.').pop() || '');
            const imgExts = ['fits','fit','fts','xisf','png','jpg','jpeg','gif','bmp','webp','tif','tiff'];
            const textExts = ['txt','log','md','json','xml','csv'];

            if (imgExts.includes(ext)) {
                // Reuse the same OpenSeadragon viewer STUDIO uses. Set
                // the URL to the FILES preview endpoint.
                const url = '/api/files/preview?path=' + encodeURIComponent(entry.fullPath)
                          + '&maxDim=2400&t=' + Date.now();
                this._openImageViewerWithUrl(url, entry.name);
                // Kick off the FITS header fetch in parallel with the
                // image load. The overlay panel renders as soon as the
                // JSON lands; visibility is a separate sticky toggle.
                this.loadFitsHeaders(entry.fullPath);
                return;
            }
            if (textExts.includes(ext)) {
                try {
                    const r = await fetch('/api/files/preview?path=' + encodeURIComponent(entry.fullPath));
                    const txt = r.ok ? await r.text() : `(preview failed: HTTP ${r.status})`;
                    this.files.preview = {
                        open: true, path: entry.fullPath, name: entry.name,
                        kind: 'text', textContent: txt
                    };
                } catch (e) {
                    this.toast('Preview failed: ' + (e.message || ''), 'error');
                }
                return;
            }
            // Unknown format: just offer download.
            if (window.confirm(`No preview available for ${entry.name}. Download instead?`)) {
                this.filesDownloadOne(entry.fullPath);
            }
        },

        filesClosePreview() {
            this.files.preview = { open: false, path: '', name: '', kind: '', textContent: null };
        },

        // Open the shared image-viewer modal with a custom URL + title.
        // Routes through openImageViewer() so the existing modal frame,
        // close handler, navigator config, and OSD destroy/leak guard
        // all apply — no parallel pipeline.
        _openImageViewerWithUrl(url, title) {
            this.imageViewerUrl = url;
            this.imageViewerTitle = title || 'Image Viewer';
            this.openImageViewer();
        },

        // --- Studio root setter ---------------------------------------

        async filesSetStudioRoot() {
            const dir = this.filesSelectedDir();
            if (!dir) {
                this.toast('Select a single folder first', 'warn');
                return;
            }
            if (!window.confirm(
                    `Use this as the Studio root?\n\n${dir.fullPath}\n\n` +
                    `Studio will rescan this tree on its next open.`)) return;
            try {
                const r = await this.apiPost('/api/files/studio-root', null, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ path: dir.fullPath })
                });
                this.settings.imageOutputDir = r.imageOutputDir || dir.fullPath;
                this.toast('Studio root set to ' + this.settings.imageOutputDir, 'ok');
            } catch (e) {
                this.toast('Could not set Studio root: ' + (e.message || ''), 'error');
            }
        },

        // --- Display helpers ------------------------------------------

        filesIcon(e) {
            if (e.isDirectory) return '📁';
            const ext = (e.name.toLowerCase().split('.').pop() || '');
            if (['fits','fit','fts','xisf'].includes(ext)) return '🔭';
            if (['png','jpg','jpeg','gif','bmp','webp','tif','tiff'].includes(ext)) return '🖼';
            if (['txt','log','md','json','xml','csv'].includes(ext)) return '📄';
            if (['zip','tar','gz','7z','rar'].includes(ext)) return '🗜';
            return '📦';
        },

        filesTypeLabel(e) {
            if (e.isDirectory) return 'Folder';
            const ext = (e.name.toLowerCase().split('.').pop() || '');
            return ext ? ext.toUpperCase() : 'File';
        },

        filesFormatBytes(n) {
            if (n == null || n < 0) return '—';
            if (n < 1024) return n + ' B';
            if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
            if (n < 1024 * 1024 * 1024) return (n / 1048576).toFixed(1) + ' MB';
            return (n / 1073741824).toFixed(2) + ' GB';
        },

        filesFormatDate(iso) {
            if (!iso) return '—';
            const d = new Date(iso);
            if (isNaN(d.getTime()) || d.getFullYear() < 1980) return '—';
            return d.getFullYear() + '-' +
                   String(d.getMonth() + 1).padStart(2, '0') + '-' +
                   String(d.getDate()).padStart(2, '0') + ' ' +
                   String(d.getHours()).padStart(2, '0') + ':' +
                   String(d.getMinutes()).padStart(2, '0');
        },

        // Subset honoured by the chip row at the top of the panel.
        tonightFiltered() {
            let xs = this.tonight.items;
            if (this.tonight.filter && this.tonight.filter !== 'all') {
                const cap = this.tonight.filter.charAt(0).toUpperCase() + this.tonight.filter.slice(1);
                xs = xs.filter(i => i.category.toLowerCase() === this.tonight.filter);
            }
            if (this.tonight.fitsFovOnly && this.tonightHasFovData()) {
                xs = xs.filter(i => i.fitsCameraFov === true);
            }
            return xs;
        },

        tonightHasFovData() {
            return this.tonight.items.some(i => i.fitsCameraFov !== null);
        },

        // Used in `:key` / `:id` bindings — has to be DOM-safe (no slashes,
        // colons, parens). Comet names like "22P/Kopff" would otherwise
        // produce invalid IDs.
        tonightSafeKey(item) {
            return (item.category + '_' + item.name).replace(/[^a-zA-Z0-9_]/g, '_');
        },

        azCardinal(az) {
            const dirs = ['N','NE','E','SE','S','SW','W','NW'];
            return dirs[Math.round((az % 360) / 45) % 8];
        },

        // Click the name → set as the current Sky target, jump to Sky tab,
        // recentre the map. Doesn't move the mount — that's the Go to btn.
        tonightPickTarget(item) {
            this.skyTarget = {
                name:    item.name,
                ra:      item.raHours,
                dec:     item.decDeg,
                type:    item.type,
                magnitude: item.magnitude != null ? item.magnitude : ''
            };
            this.tab = 'sky';
            this.$nextTick(() => {
                if (typeof this.skyGoToMount === 'function') this.skyGoToMount();
                if (typeof this.updateSkyCameraFov === 'function') this.updateSkyCameraFov();
            });
        },

        // Mount-connected click → slew + plate solve + centre, same workflow
        // the existing Sky tab Slew & Center button uses.
        async tonightGoTo(item) {
            this.tonightPickTarget(item);
            this.$nextTick(() => {
                if (typeof this.slewAndCenter === 'function') {
                    this.slewAndCenter();
                }
            });
        },

        // Helper: mark a card's thumb as failed-to-load. If we were
        // trying the local cached URL and a remote URL is also known,
        // swap to the remote URL once before giving up — this covers
        // the case where /api/sky/image/file/{slug} 404s due to a
        // stale in-memory cache on the backend but NASA / Wikipedia
        // still has the original. Reassigns the whole `thumbs` dict
        // so Alpine picks up the change (direct property writes on a
        // tracked object don't always trigger re-render in v3).
        tonightThumbFailed(item) {
            const current = this.tonight.thumbs[item.name];
            if (current?.url && current?.remoteUrl && current.url !== current.remoteUrl) {
                // First failure: fall back to the remote URL.
                this.tonight.thumbs = {
                    ...this.tonight.thumbs,
                    [item.name]: { ...current, url: current.remoteUrl }
                };
                return;
            }
            this.tonight.thumbs = {
                ...this.tonight.thumbs,
                [item.name]: { url: null, missing: true }
            };
        },

        // Lazy-load thumbnails one at a time so we don't fire 30 parallel
        // /api/sky/image requests on tab open. Sequential is plenty fast
        // for a list of this size and is much kinder to NASA / Wikipedia.
        //
        // For each candidate we try a few name variants in order of
        // search-friendliness — NASA Image Library is indexed by popular
        // names ("Carina Nebula"), much less by raw catalogue codes
        // ("NGC 3372"). Common name first, then catalogue name as the
        // fallback. Backend caches each lookup independently.
        //
        // Alpine 3 reactivity tracks property assignments on tracked
        // objects but doesn't reliably pick up additions of brand-new
        // keys to a plain {}. To force a render, we reassign the whole
        // `thumbs` object after each update so the template re-evaluates.
        async _kickTonightThumbs() {
            for (const item of this.tonight.items) {
                if (this.tonight.thumbs[item.name]?.url || this.tonight.thumbs[item.name]?.missing) continue;

                const tryNames = [];
                if (item.commonName) tryNames.push(item.commonName);
                // Backend routes catalogue codes (M / NGC / IC / Sh2 / Caldwell)
                // straight to Wikipedia where they hit a reliable per-object
                // article. The bare item.name covers that path.
                tryNames.push(item.name);

                let found = null;
                for (const q of tryNames) {
                    try {
                        const r = await this.apiGet(`/api/sky/image?name=${encodeURIComponent(q)}`);
                        if (r?.available) { found = r; break; }
                    } catch { /* keep trying remaining variants */ }
                }

                // Prefer the local cached-on-disk URL when the backend
                // downloaded the bytes (PrefetchAsync or a previous
                // lazy fetch). Falls back to the remote NASA/Wikipedia
                // URL if local isn't available, so even un-prefetched
                // sessions keep working with internet. We also remember
                // the remoteUrl separately so tonightThumbFailed() can
                // swap to it if the local URL 404s at the browser.
                this.tonight.thumbs = {
                    ...this.tonight.thumbs,
                    [item.name]: found ? {
                        url:       found.localUrl || found.thumbnailUrl,
                        remoteUrl: found.thumbnailUrl,
                        title:     found.title,
                        credit:    found.credit,
                        missing:   false
                    } : { url: null, missing: true }
                };
            }
        },

        // Trigger the prefetch endpoint that walks the whole catalogue +
        // planets + comets and downloads every thumbnail to the backend's
        // disk cache. After this completes, the Tonight tab works fully
        // offline. Surfaced from the Settings page as a one-click action.
        async prefetchCelestialImages() {
            if (this.tonight._prefetching) return;
            this.tonight._prefetching = true;
            try {
                this.toast?.('Downloading object thumbnails — may take a couple of minutes…', 'info');
                const r = await this.apiPost('/api/sky/image/prefetch', {});
                this.toast?.(
                    `Prefetch done · ${r.foundCount}/${r.attemptedCount} images, ` +
                    `${(r.downloadedBytes / 1024 / 1024).toFixed(1)} MB downloaded`,
                    'ok'
                );
                // Force a thumb re-fetch on next Tonight tab open so the
                // local URLs (where available) get picked up.
                this.tonight.thumbs = {};
            } catch (e) {
                this.toast?.('Prefetch failed: ' + (e.message || 'unknown error'), 'error');
            } finally {
                this.tonight._prefetching = false;
            }
        },

        // Mini per-card altitude chart (~12 h window). Can't use
        // _ensureChart() — it looks up canvases via $refs which only
        // works for static templates, not the dynamic x-for loop here.
        // Instead resolve the canvas by id and keep instances in a
        // separate dict so refresh can destroy them.
        async _renderTonightChart(item) {
            const safe = this.tonightSafeKey(item);
            const canvasId = 'tonightChart_' + safe;
            const canvas = document.getElementById(canvasId);
            if (!canvas) return;
            if (typeof Chart === 'undefined') return;
            // If a previous chart instance exists for this card (e.g. on
            // refresh), tear it down before creating a new one — leaving
            // it leaks GPU contexts.
            this._tonightCharts ??= {};
            if (this._tonightCharts[safe]) {
                try { this._tonightCharts[safe].destroy(); } catch {}
                delete this._tonightCharts[safe];
            }
            try {
                const data = await this.apiGet(
                    `/api/sky/altitude?ra=${item.raHours}&dec=${item.decDeg}&stepMinutes=30`);
                const t = this._chartTheme();
                const samples = data.samples || [];
                this._tonightCharts[safe] = new Chart(canvas, {
                    type: 'line',
                    data: {
                        labels: samples.map(s => new Date(s.utc).toLocaleTimeString([],
                            { hour: '2-digit', minute: '2-digit' })),
                        datasets: [
                            { label: 'Altitude',
                              data: samples.map(s => Math.max(0, s.altitudeDeg)),
                              borderColor: '#4fc3f7',
                              backgroundColor: 'rgba(79,195,247,0.12)',
                              tension: 0.25, pointRadius: 0, borderWidth: 1.5, fill: true }
                        ]
                    },
                    options: {
                        responsive: true, maintainAspectRatio: false, animation: false,
                        plugins: { legend: { display: false }, tooltip: { enabled: false } },
                        scales: {
                            x: { display: true,
                                 ticks: {
                                     color: t.tick, font: { size: 9 },
                                     maxRotation: 0, autoSkip: true, maxTicksLimit: 6
                                 },
                                 grid: { color: t.grid, drawTicks: false } },
                            y: { min: 0, max: 90,
                                 ticks: { display: false },
                                 grid: { color: t.grid } }
                        }
                    }
                });
            } catch { /* leave canvas blank */ }
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
            const decClamped = Math.max(-89.5, Math.min(89.5, dec));
            try { Celestial.rotate({ center: [ra * 15, decClamped, 0] }); } catch {}
        },

        updateSkyCameraFov() {
            if (!this._celestialReady) return;
            // Camera-FOV rectangle is drawn as a custom GeoJSON layer
            // (d3-celestial supports user-supplied overlays via add()).
            if (!this.aladinShowFov || !this.skyTarget) {
                if (this._fovLayerId) try { Celestial.remove(this._fovLayerId); } catch {}
                this._fovLayerId = null;
                Celestial.redraw();
                return;
            }
            const ra  = this.skyTarget.ra ?? this.skyTarget.raHours;
            const dec = this.skyTarget.dec ?? this.skyTarget.decDeg;
            if (ra == null || dec == null) return;

            // FOV rectangle in equatorial coords. The corners are offset
            // from the target by ±w in RA (corrected by cos(dec) for the
            // RA-axis squish near the poles) and ±h in Dec.
            const w = this.fov.width / 2;
            const h = this.fov.height / 2;
            const cosDec = Math.cos(dec * Math.PI / 180) || 1e-6;
            const raDeg = ra * 15;
            const ring = [
                [raDeg - w / cosDec, dec - h],
                [raDeg + w / cosDec, dec - h],
                [raDeg + w / cosDec, dec + h],
                [raDeg - w / cosDec, dec + h],
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
                    callback: () => Celestial.container.selectAll('.fov').remove(),
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
            // Reset to live-camera defaults so the next "View full image"
            // from any other tab doesn't accidentally re-open a file.
            this.imageViewerUrl = '/api/image/latest/preview';
            this.imageViewerTitle = 'Image Viewer — full resolution';
            // Drop the header cache so reopening a different FITS doesn't
            // briefly flash the previous file's headers.
            this.fitsHeaders.data = null;
            this.fitsHeaders.path = '';
            this.fitsHeaders.error = '';
        },

        reloadImageViewer() {
            if (this._osdViewer) {
                // Bust the cache + reuse whatever URL the viewer is
                // configured for. The live-camera path needs the
                // timestamp; a static file path is harmless.
                const sep = this.imageViewerUrl.includes('?') ? '&' : '?';
                this._osdViewer.open({
                    type: 'image',
                    url: this.imageViewerUrl + sep + 't=' + Date.now()
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
            const sep = this.imageViewerUrl.includes('?') ? '&' : '?';
            this._osdViewer = OpenSeadragon({
                id: 'osd-viewer',
                tileSources: {
                    type: 'image',
                    url: this.imageViewerUrl + sep + 't=' + Date.now()
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
            // Surface backend failures as toasts so the user isn't
            // staring at a black canvas wondering why nothing loaded.
            this._osdViewer.addHandler('open-failed', (ev) => {
                this.toast('Could not load image: ' + (ev?.message || 'unknown error'), 'error');
            });
        },

        // --- OpenSeadragon toolbar (custom buttons in modal header) ---

        // The modal header drives these instead of OSD's built-in
        // navigation control. The native control needs an `images/`
        // sprite folder we don't vendor; calling viewport methods
        // directly is the documented bypass.
        osdZoom(factor) {
            if (!this._osdViewer) return;
            try {
                const vp = this._osdViewer.viewport;
                vp.zoomBy(factor);
                vp.applyConstraints();
            } catch (e) { /* viewer disposed mid-click; ignore */ }
        },

        osdHome() {
            if (!this._osdViewer) return;
            try { this._osdViewer.viewport.goHome(); } catch (e) {}
        },

        osdToggleFullPage() {
            if (!this._osdViewer) return;
            try {
                const cur = this._osdViewer.isFullPage();
                this._osdViewer.setFullPage(!cur);
                // Hint the user how to come back. OSD's full-page mode
                // hides the modal header (where our exit button lives),
                // so without this they have no visual cue.
                if (!cur) this.toast('Full page — press Esc to exit', 'info');
            } catch (e) {}
        },

        // --- FITS header overlay panel --------------------------------

        // Fetch + cache header cards for a given path. Skipped silently
        // for non-FITS paths so the caller can just always invoke it.
        async loadFitsHeaders(path) {
            const ext = (path || '').toLowerCase().split('.').pop();
            if (!['fits','fit','fts'].includes(ext)) {
                this.fitsHeaders.data = null;
                this.fitsHeaders.path = '';
                return;
            }
            // Skip the fetch if we already have headers for this path
            // (the user toggled visibility off then on without leaving
            // the viewer).
            if (this.fitsHeaders.path === path && this.fitsHeaders.data) return;
            this.fitsHeaders.loading = true;
            this.fitsHeaders.error = '';
            this.fitsHeaders.path = path;
            try {
                const r = await this.apiGet('/api/files/fits-headers?path='
                    + encodeURIComponent(path));
                this.fitsHeaders.data = r;
            } catch (e) {
                this.fitsHeaders.data = null;
                this.fitsHeaders.error = e.message || 'Could not read headers';
            } finally {
                this.fitsHeaders.loading = false;
            }
        },

        toggleFitsHeaders() {
            this.fitsHeaders.visible = !this.fitsHeaders.visible;
            localStorage.setItem('fitsHeadersVisible',
                this.fitsHeaders.visible ? '1' : '0');
        },

        // True when the currently-open viewer should show the
        // (toggleable) FITS header panel. Used to gate the button +
        // panel + image padding in the template. Testing the title
        // (which is just the file name) instead of the URL avoids
        // the trap where the URL has the .fits extension but is
        // followed by &maxDim=... query params, so the previous
        // regex `\.(fits|fit|fts)(\?|$)` never matched and the
        // button never appeared.
        get fitsHeadersAvailable() {
            return /\.(fits|fit|fts)$/i.test(this.imageViewerTitle || '');
        },

        // Escape behaviour for the image-viewer modal: first press
        // exits OSD full-page mode (where the modal header is hidden,
        // so the user has no other escape hatch); second press closes
        // the modal entirely. Without this, full-page traps the user
        // because closeImageViewer() destroys the OSD instance while
        // the document still thinks it's full-page.
        onImageViewerEscape() {
            if (this._osdViewer) {
                try {
                    if (this._osdViewer.isFullPage()) {
                        this._osdViewer.setFullPage(false);
                        return;
                    }
                } catch (e) { /* fall through to close */ }
            }
            this.closeImageViewer();
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

        // Guider chart: RA (red) + Dec (blue) vs sample index.
        // The canvas lives inside x-show="guider.connected" which starts
        // as display:none, so Chart.js used to measure 0x0 at first
        // create and never re-fit. We defer instance creation until the
        // canvas actually has a non-zero size — then a single Chart.js
        // instance is reused and just gets its data swapped on each
        // ~1Hz WS tick.
        updateGuideChart() {
            const canvas = this.$refs.guideChart;
            if (!canvas || typeof Chart === 'undefined') return;

            // Wait until the canvas has pixels — its parent may still be
            // display:none on the first few WS ticks after page load.
            // We don't bail in subsequent ticks even if clientWidth dips
            // because that would freeze the chart on transient layouts.
            const ready = canvas.clientWidth >= 10 && canvas.clientHeight >= 10;
            if (!this._charts.guide && !ready) return;

            const t = this._chartTheme();
            const steps = (this.guider.recentSteps || []).filter(
                s => Number.isFinite(Number(s.ra)) && Number.isFinite(Number(s.dec))
            );
            const raVals  = steps.map(s => Number(s.ra));
            const decVals = steps.map(s => Number(s.dec));
            const labels  = steps.map((_, i) => i);

            let c = this._charts.guide;
            if (!c) {
                c = new Chart(canvas, {
                    type: 'line',
                    data: {
                        labels,
                        datasets: [
                            { label: 'RA', data: raVals, borderColor: '#e57373',
                              backgroundColor: 'transparent', tension: 0.2,
                              pointRadius: 0, borderWidth: 1.5 },
                            { label: 'Dec', data: decVals, borderColor: '#64b5f6',
                              backgroundColor: 'transparent', tension: 0.2,
                              pointRadius: 0, borderWidth: 1.5 }
                        ]
                    },
                    options: {
                        responsive: true, maintainAspectRatio: false,
                        animation: false,
                        plugins: { legend: { display: false } },
                        scales: {
                            x: { display: false, grid: { color: t.grid } },
                            // Symmetric y so positive + negative excursions
                            // are both visible — auto-fit would otherwise
                            // anchor at 0 when all samples sit on one side.
                            y: {
                                ticks: { color: t.tick, font: { size: 10 } },
                                grid:  { color: t.grid },
                                title: { display: true, text: 'arcsec',
                                         color: t.tick, font: { size: 10 } },
                                suggestedMin: -2, suggestedMax: 2
                            }
                        }
                    }
                });
                this._charts.guide = c;
                // Fall through into the update path so the freshly-
                // created chart paints its first frame instead of
                // staying at the initial empty data Chart.js cached
                // before the first draw cycle.
            } else {
                // Reuse: swap data in place. Chart.js v4 picks up the
                // new array identities on next update().
                c.data.labels = labels;
                c.data.datasets[0].data = raVals;
                c.data.datasets[1].data = decVals;
            }
            // Default update mode re-runs the layout pass and the
            // axis-scale calculation. 'none' (which we tried before)
            // skips animation but in some Chart.js v4 builds also
            // skips the data-rebind step, freezing the visible plot
            // even though c.data.datasets[0].data points at fresh
            // numbers. Default is safe + fast since we set
            // animation: false above.
            c.update();
            // Visible heartbeat — increments even if line shape barely
            // changed, so the user can verify the chart is alive.
            this.guideChartTickCount = (this.guideChartTickCount || 0) + 1;
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
            // CLST-7: per-rig compute target override. Defaults to
            // "auto" for old rigs without the field.
            this.liveStackComputeMode = rig.liveStackComputeMode || 'auto';
            this.equipCameraChoice = rig.camera || '';
            // Hydrate the camera-driver dropdown from the rig. Old
            // rigs without the field default to "indi" via the
            // backend's ?? coalesce; the UI mirrors that here.
            this.cameraDriver = rig.cameraDriver || 'indi';
            this.equipMountChoice = rig.telescope || '';
            // Same pattern for the mount driver. For direct WiFi drivers
            // (synscan-wifi, nexstar-wifi, lx200-tcp) the "device name"
            // is the host:port the user typed; for INDI it's the device
            // name advertised by the indiserver.
            this.mountDriver = rig.telescopeDriver || 'indi';
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
            // OTA optics — hydrate the Main Telescope card on the RIGS tab.
            // Empty/zero values are fine (the card just shows blanks).
            this.settings.aperture = rig.apertureMm || 0;
            this.settings.telescopeBrand = rig.telescopeBrand || '';
            this.settings.telescopeModel = rig.telescopeModel || '';
            this.settings.accessoryType = rig.accessoryType || '';
            this.settings.accessoryModel = rig.accessoryModel || '';
            this.settings.accessoryFactor = rig.accessoryFactor || 1.0;
            this.settings.requiredBackspacingMm = rig.requiredBackspacingMm ?? null;
            // Guidescope card
            this.settings.guiderFocalLengthMm = rig.guiderFocalLengthMm || 200;
            this.settings.guiderApertureMm   = rig.guiderApertureMm   || 50;
            this.settings.guideTelescopeBrand = rig.guideTelescopeBrand || '';
            this.settings.guideTelescopeModel = rig.guideTelescopeModel || '';
            if (rig.phd2Host) this.guiderHost = rig.phd2Host;
            if (rig.phd2Port) this.guiderPort = rig.phd2Port;
            // PH2X: surface the rig's PHD2 algo preset choice on the pill.
            this.phd2ActivePreset = rig.phd2AlgoPreset || 'Default';
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

        // ---- Telescope + accessory catalogues ----
        // Loaded once on first open of the Manage Rigs modal.
        // wwwroot/data/{telescopes,optical-accessories}.json are
        // static assets served by Kestrel's UseStaticFiles middleware.
        async loadOpticsCatalogue() {
            if (this.opticsCatalogue.loaded) return;
            try {
                const [scopesResp, accResp] = await Promise.all([
                    fetch('/data/telescopes.json'),
                    fetch('/data/optical-accessories.json')
                ]);
                const scopes = await scopesResp.json();
                const acc = await accResp.json();
                this.opticsCatalogue = {
                    telescopes:  scopes.telescopes  || [],
                    accessories: acc.accessories     || [],
                    loaded: true
                };
            } catch (e) {
                this.toast?.('Failed to load optics catalogue: ' + e.message, 'warn');
                this.opticsCatalogue = { telescopes: [], accessories: [], loaded: true };
            }
        },

        /// Distinct telescope brands in the catalogue, sorted.
        get opticsBrands() {
            const set = new Set(this.opticsCatalogue.telescopes.map(t => t.brand));
            return Array.from(set).sort();
        },

        /// Models for the brand currently picked on the given rig.
        opticsModelsForRig(rig) {
            return this.opticsCatalogue.telescopes
                .filter(t => t.brand === rig.telescopeBrand)
                .sort((a, b) => a.apertureMm - b.apertureMm);
        },

        /// Accessory entries compatible with the rig's picked OTA
        /// model, plus any generic / empty-compatibility entries
        /// that work on anything.
        opticsAccessoriesForRig(rig) {
            const model = rig.telescopeModel || '';
            return this.opticsCatalogue.accessories.filter(a => {
                const list = a.compatibleScopes || [];
                if (list.length === 0) return true;   // generic
                return list.some(s => model.includes(s));
            }).sort((a, b) => {
                if (a.type !== b.type) return a.type.localeCompare(b.type);
                return (a.brand + a.model).localeCompare(b.brand + b.model);
            });
        },

        /// Triggered when the user picks a telescope brand → reset
        /// the model and clear computed fields so the next dropdown
        /// hit re-populates them.
        onTelescopeBrandChange(rig) {
            rig.telescopeModel = '';
            this._applyOpticsToRig(rig);
            this.saveRig(rig);
        },

        /// Triggered when the user picks a telescope model. Auto-
        /// fills aperture + native focal length, recomputes the
        /// effective focal length using the current accessory, and
        /// saves.
        onTelescopeModelChange(rig) {
            this._applyOpticsToRig(rig);
            this.saveRig(rig);
        },

        /// Triggered when the user picks (or clears) an accessory.
        /// Recomputes the effective focal length + backspacing.
        onAccessoryChange(rig) {
            this._applyOpticsToRig(rig);
            this.saveRig(rig);
        },

        /// Compute the effective optics fields from the catalogue
        /// picks. Called by every onXChange above.
        _applyOpticsToRig(rig) {
            const scope = this.opticsCatalogue.telescopes
                .find(t => t.brand === rig.telescopeBrand
                        && t.model === rig.telescopeModel);
            if (scope) {
                rig.apertureMm = scope.apertureMm;
                // Base scope back-focus — overridden below if the
                // accessory publishes its own value (most do).
                rig.requiredBackspacingMm = scope.backspacingMm;
            } else {
                // Off-catalogue scope: leave aperture as the user
                // entered it manually; same for backspacing.
            }
            const accessory = this.opticsCatalogue.accessories
                .find(a => a.brand + ' ' + a.model === rig.accessoryModel)
                || this.opticsCatalogue.accessories
                    .find(a => a.model === rig.accessoryModel);
            if (accessory) {
                rig.accessoryType   = accessory.type;
                rig.accessoryFactor = accessory.factor;
                if (accessory.backspacingMm != null) {
                    rig.requiredBackspacingMm = accessory.backspacingMm;
                }
            } else {
                rig.accessoryType   = '';
                rig.accessoryFactor = 1.0;
            }
            // Effective focal length = native × accessory factor.
            // Only auto-recompute when a scope is picked from the
            // catalogue; off-catalogue rigs keep the user's manual
            // FocalLengthMm value untouched.
            if (scope) {
                rig.focalLengthMm = Math.round(
                    scope.focalLengthMm * (rig.accessoryFactor || 1.0));
            }
        },

        /// Native (no-accessory) focal length the picker is showing
        /// for this rig. Used in the UI to display "Native fl /
        /// Effective fl" side-by-side.
        opticsNativeFocalLength(rig) {
            const scope = this.opticsCatalogue.telescopes
                .find(t => t.brand === rig.telescopeBrand
                        && t.model === rig.telescopeModel);
            return scope ? scope.focalLengthMm : null;
        },

        /// f-ratio = focal length / aperture. UI helper for the
        /// readout below the picker.
        opticsFocalRatio(rig) {
            if (!rig.apertureMm || !rig.focalLengthMm) return null;
            return rig.focalLengthMm / rig.apertureMm;
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

        // Settings-mirror version of _applyOpticsToRig — same lookup,
        // writes to this.settings.* instead of a rig object. Used by
        // the catalog picker dropdowns in the Main Telescope card on
        // the RIGS tab (settings.* later flushes into the active rig
        // via saveCurrentSelectionsToRig). Off-catalogue picks (brand
        // cleared back to blank) leave the typed fields alone.
        _applyOpticsToSettings() {
            const s = this.settings;
            const scope = this.opticsCatalogue.telescopes
                .find(t => t.brand === s.telescopeBrand
                        && t.model === s.telescopeModel);
            if (scope) {
                s.aperture = scope.apertureMm;
                s.requiredBackspacingMm = scope.backspacingMm;
            }
            const accessory = this.opticsCatalogue.accessories
                .find(a => a.brand + ' ' + a.model === s.accessoryModel)
                || this.opticsCatalogue.accessories
                    .find(a => a.model === s.accessoryModel);
            if (accessory) {
                s.accessoryType   = accessory.type;
                s.accessoryFactor = accessory.factor;
                if (accessory.backspacingMm != null) {
                    s.requiredBackspacingMm = accessory.backspacingMm;
                }
            } else if (!s.accessoryModel) {
                s.accessoryType   = '';
                s.accessoryFactor = 1.0;
            }
            // Effective focal length = native × accessory factor.
            // Only recompute when picked from the catalog so manual
            // off-catalog values stay untouched.
            if (scope) {
                s.focalLength = Math.round(
                    scope.focalLengthMm * (s.accessoryFactor || 1.0));
                this.updateFov();
            }
        },

        // Catalog picker change handlers for the Main Telescope card.
        // Brand reset clears the model; model + accessory changes
        // re-derive aperture/focal/backspacing from the catalog.
        onTelescopeBrandPick() {
            this.settings.telescopeModel = '';
            this._applyOpticsToSettings();
            this.saveOpticsDebounced();
        },
        onTelescopeModelPick() {
            this._applyOpticsToSettings();
            this.saveOpticsDebounced();
        },
        onAccessoryPick() {
            this._applyOpticsToSettings();
            this.saveOpticsDebounced();
        },

        // Debounced save for inline OTA / Guidescope edits from the
        // RIGS-tab cards. Without the debounce, every keystroke
        // would PUT the whole rig — 600 ms is long enough that the
        // user finishes typing a number before we round-trip.
        saveOpticsDebounced() {
            if (this._opticsSaveTimer) clearTimeout(this._opticsSaveTimer);
            this._opticsSaveTimer = setTimeout(() => {
                this.saveCurrentSelectionsToRig();
            }, 600);
        },

        // How many of the four optional devices (Rotator, Flat Panel,
        // Dome, Weather) have a selection saved on the active rig.
        // Powers the accessories <details> summary count.
        accessoryCount() {
            let n = 0;
            if (this.equipRotatorChoice) n++;
            if (this.equipFlatChoice)    n++;
            if (this.equipDomeChoice)    n++;
            if (this.equipWeatherChoice) n++;
            return n;
        },

        // True if at least one accessory is configured — the
        // <details> auto-opens in this case so the user sees what
        // they previously set without having to click.
        anyAccessoryConfigured() {
            return this.accessoryCount() > 0;
        },

        async saveCurrentSelectionsToRig() {
            const rig = this.rigs.find(r => r.id === this.activeRigId);
            if (!rig) return;
            const updated = {
                ...rig,
                camera: this.equipCameraChoice || rig.camera,
                cameraDriver: this.cameraDriver || rig.cameraDriver || 'indi',
                telescope: this.equipMountChoice || rig.telescope,
                telescopeDriver: this.mountDriver || rig.telescopeDriver || 'indi',
                focuser: this.equipFocuserChoice || rig.focuser,
                filterWheel: this.equipFilterChoice || rig.filterWheel,
                rotator: this.equipRotatorChoice || rig.rotator,
                flatDevice: this.equipFlatChoice || rig.flatDevice,
                dome: this.equipDomeChoice || rig.dome,
                weather: this.equipWeatherChoice || rig.weather,
                coolerTargetTemperature: this.equipCoolerTarget,
                focuserStepSize: this.focusStep,
                focalLengthMm: this.settings.focalLength,
                // OTA optics (Main Telescope card on the RIGS tab)
                apertureMm: this.settings.aperture,
                telescopeBrand: this.settings.telescopeBrand,
                telescopeModel: this.settings.telescopeModel,
                accessoryType: this.settings.accessoryType,
                accessoryModel: this.settings.accessoryModel,
                accessoryFactor: this.settings.accessoryFactor,
                requiredBackspacingMm: this.settings.requiredBackspacingMm,
                // Guidescope card
                guiderFocalLengthMm: this.settings.guiderFocalLengthMm,
                guiderApertureMm:    this.settings.guiderApertureMm,
                guideTelescopeBrand: this.settings.guideTelescopeBrand,
                guideTelescopeModel: this.settings.guideTelescopeModel,
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

        // --- End-of-run actions ---

        async loadEndActions() {
            try {
                const data = await this.apiGet('/api/sequence/end-actions');
                if (data) {
                    this.endActions = {
                        parkMount: !!data.parkMount,
                        stopTracking: !!data.stopTracking,
                        warmCamera: !!data.warmCamera,
                        disconnectGuider: !!data.disconnectGuider,
                        runOnStop: !!data.runOnStop,
                        autoGraXpert: !!data.autoGraXpert
                    };
                }
            } catch (e) { /* not fatal — defaults stand */ }
        },

        saveEndActions() {
            if (this._endActionsSaveTimer) clearTimeout(this._endActionsSaveTimer);
            this._endActionsSaveTimer = setTimeout(async () => {
                try {
                    await this.apiPost('/api/sequence/end-actions', null, {
                        method: 'PUT',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify(this.endActions)
                    });
                } catch (e) {
                    this.toast('Failed to save end actions', 'error');
                }
            }, 400);
        },

        // Short summary shown in the panel header so the user knows what
        // will happen without expanding the body.
        endActionsSummary() {
            const ea = this.endActions || {};
            const parts = [];
            if (ea.parkMount) parts.push('park');
            else if (ea.stopTracking) parts.push('stop tracking');
            if (ea.warmCamera) parts.push('warm camera');
            if (ea.disconnectGuider) parts.push('stop PHD2');
            if (!parts.length) return '(none)';
            return parts.join(' · ') + (ea.runOnStop ? ' · also on stop' : '');
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
                        imageOutputDir: this.settings.imageOutputDir,
                        imageNamePattern: this.settings.imageNamePattern,
                        preferAdvancedSequencer: this.settings.preferAdvancedSequencer,
                        sirilPath: this.settings.sirilPath,
                        sirilScriptsDir: this.settings.sirilScriptsDir,
                        graxpertPath: this.settings.graxpertPath,
                        graxpertBgeSmoothing: this.settings.graxpertBgeSmoothing,
                        graxpertBgeCorrection: this.settings.graxpertBgeCorrection,
                        graxpertDeconStrength: this.settings.graxpertDeconStrength,
                        graxpertDeconPsfSize: this.settings.graxpertDeconPsfSize,
                        graxpertDenoiseStrength: this.settings.graxpertDenoiseStrength
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

        // --- PREVIEW tab (snap-and-look) ---

        // Take one test shot. Reuses the LIVE capture endpoint with the
        // new SaveToDisk + TargetName fields plumbed through. Result
        // image arrives via the WS image stream (same channel LIVE uses)
        // and gets mirrored onto the PREVIEW canvas by
        // _mirrorLiveToPreviewCanvas.
        async previewTakeSnap() {
            if (this.preview.busy) return;
            if (!this.selectedCamera) {
                this.toast('No camera connected', 'warn');
                return;
            }
            this.preview.busy = true;
            try {
                const r = await this.apiPost('/api/camera/capture', null, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        exposure: this.preview.exposure,
                        gain: this.preview.gain,
                        binning: parseInt(this.preview.binning) || 1,
                        filter: this.preview.filter || null,
                        saveToDisk: !!this.preview.saveToDisk,
                        targetName: this.preview.targetName || 'snap'
                    })
                });
                this.preview.lastStats = r?.stats || null;
                this.preview.lastSnapAt = Date.now();
                if (r?.saved) {
                    this.toast('Snap saved to {rig}/snaps/', 'ok', 2500);
                }
            } catch (e) {
                this.toast('Snap failed: ' + (e.message || ''), 'error');
                // Break the loop on error — don't hammer the camera
                // with a guaranteed-to-fail sequence of requests.
                this.preview.looping = false;
            } finally {
                this.preview.busy = false;
            }
            // Loop kicks the next snap only after the previous one
            // fully resolved. $nextTick yields so the UI updates
            // (stats refresh, button labels) before the next exposure.
            if (this.preview.looping) {
                this.$nextTick(() => this.previewTakeSnap());
            }
        },

        previewToggleLoop() {
            this.preview.looping = !this.preview.looping;
            if (this.preview.looping && !this.preview.busy) {
                this.previewTakeSnap();
            }
        },

        async previewAbort() {
            this.preview.looping = false;   // stop the chain first
            // If a server-side stream is running, take it down too so the
            // Abort button is a true "everything stop" panic switch.
            if (this.cameraStream.running) {
                try { await this.apiPost('/api/camera/stream/stop'); }
                catch (e) { /* server may have already stopped it */ }
            }
            try {
                await this.apiPost('/api/camera/abort');
                this.toast('Snap aborted', 'warn');
            } catch (e) {
                this.toast('Abort failed: ' + (e.message || ''), 'error');
            }
        },

        // Toggle the server-side continuous stream. Backend auto-picks
        // native (CCD_VIDEO_STREAM, ~10-30 fps) when the camera supports
        // it, else falls back to a tight capture loop on the server.
        // Frames pipe through the existing /ws/image-stream channel —
        // the LIVE / PREVIEW / Focus canvases all render them.
        async toggleCameraStream() {
            if (this.cameraStream.running) {
                try {
                    await this.apiPost('/api/camera/stream/stop');
                    this.toast('Stream stopped', 'info');
                } catch (e) { this.toast('Stop failed: ' + e.message, 'error'); }
                return;
            }
            try {
                const r = await this.apiPost('/api/camera/stream/start', {
                    exposure: this.preview.exposure,
                    gain: this.preview.gain,
                    binning: parseInt(this.preview.binning)
                });
                this.toast(`Stream started (${r.mode}${r.supportsNative ? ' / native CCD_VIDEO_STREAM' : ' / server loop'})`, 'ok');
            } catch (e) {
                this.toast('Stream failed: ' + (e.message || 'unknown'), 'error');
            }
        },

        // ----- VIDEO tab (planetary capture + lucky-imaging stack) -----

        // Capture side — wraps the existing /api/camera/stream endpoints
        // with the VIDEO tab's own exposure/gain/binning. Recording
        // subscribes to the stream on the server side (no client-side
        // frame routing).
        async videoToggleStream() {
            if (this.cameraStream.running) {
                try { await this.apiPost('/api/camera/stream/stop'); }
                catch (e) { this.toast('Stop failed: ' + e.message, 'error'); }
                return;
            }
            try {
                await this.apiPost('/api/camera/stream/start', {
                    exposure: this.video.exposure,
                    gain: this.video.gain,
                    binning: parseInt(this.video.binning)
                });
            } catch (e) {
                this.toast('Stream failed: ' + (e.message || 'unknown'), 'error');
            }
        },
        async videoToggleRecord() {
            if (this.videoRecording.recording) {
                try {
                    await this.apiPost('/api/video/record/stop');
                    this.toast('Recording stopped', 'info');
                } catch (e) { this.toast('Stop failed: ' + e.message, 'error'); }
                return;
            }
            try {
                const r = await this.apiPost('/api/video/record/start', {
                    targetName: this.video.targetName || 'planet',
                    maxDurationSeconds: this.video.maxDurationSec > 0 ? this.video.maxDurationSec : null
                });
                this.toast(`Recording → ${r.path}`, 'ok');
            } catch (e) { this.toast('Record failed: ' + (e.message || 'unknown'), 'error'); }
        },

        // Camera capability probe — populates cameraCaps so WB / ROI /
        // ISO controls show/hide per-camera. Called on VIDEO tab open
        // and after a camera swap. Tolerates 400 responses (no camera
        // selected yet) by leaving the cached flags as-is.
        async loadCameraCapabilities() {
            try {
                const r = await this.apiGet('/api/camera/status');
                if (r && r.capabilities) {
                    this.cameraCaps = Object.assign({}, this.cameraCaps, r.capabilities);
                }
                if (r && typeof r.whiteBalanceR === 'number') this.video.wbR = r.whiteBalanceR;
                if (r && typeof r.whiteBalanceB === 'number') this.video.wbB = r.whiteBalanceB;
            } catch (e) { /* no camera connected yet */ }
        },
        async videoSetWhiteBalance() {
            try {
                await this.apiPost('/api/camera/white-balance', {
                    red: this.video.wbR, blue: this.video.wbB
                });
            } catch (e) {
                this.toast('WB write failed: ' + (e.message || 'driver rejected'), 'warn');
            }
        },
        videoResetWhiteBalance() {
            this.video.wbR = 50;
            this.video.wbB = 50;
            this.videoSetWhiteBalance();
        },

        // Process side — enumerates SER files under {ImageOutputDir}/planetary
        // via the FileBrowserService API and offers them in the dropdown.
        async loadVideoSerList() {
            try {
                // Walk the planetary subtree. Files endpoint gives us
                // recursive directory listings.
                const root = (this.settings.imageOutputDir || '') + '/planetary';
                const list = await this.apiGet(`/api/files/list?path=${encodeURIComponent(root)}&recursive=true`);
                this.video.serList = (list.entries || [])
                    .filter(e => !e.isDir && e.name.toLowerCase().endsWith('.ser'))
                    .map(e => ({
                        path: e.path,
                        label: e.name + ' (' + ((e.sizeBytes / 1048576) | 0) + ' MB)'
                    }));
            } catch (e) {
                // FilesEndpoint may return 4xx if dir doesn't exist yet
                // (no recordings made). Leave list empty; no toast spam.
                this.video.serList = [];
            }
        },
        async videoStartStack() {
            if (!this.video.processSerPath) return;
            try {
                const r = await this.apiPost('/api/video/stack/start', {
                    serPath: this.video.processSerPath,
                    keepPercent: this.video.keepPercent,
                    outputName: this.video.outputName
                });
                this.toast(`Stack started (job ${r.jobId?.slice?.(0, 8) || ''}…)`, 'info');
            } catch (e) { this.toast('Stack failed: ' + e.message, 'error'); }
        },
        async videoAbortStack() {
            if (!this.videoStack?.id) return;
            try { await this.apiPost(`/api/video/stack/${this.videoStack.id}/abort`); }
            catch (e) { this.toast('Abort failed: ' + e.message, 'warn'); }
        },
        videoStackPercent() {
            const j = this.videoStack;
            if (!j) return 0;
            // Rough progress: weight phases evenly. Within Analyzing /
            // Aligning / Stacking, use the per-frame ratio.
            const phaseWeights = {
                Reading: 5, Analyzing: 30, Ranking: 5,
                Aligning: 20, Stacking: 35, Writing: 5
            };
            const order = ['Reading', 'Analyzing', 'Ranking', 'Aligning', 'Stacking', 'Writing'];
            let pct = 0;
            for (const p of order) {
                if (p === j.phase) {
                    let inner = 0;
                    if (p === 'Analyzing' && j.totalFrames) inner = j.framesAnalyzed / j.totalFrames;
                    else if (p === 'Aligning' && j.framesPicked) inner = j.framesAligned / j.framesPicked;
                    else if (p === 'Stacking' && j.framesPicked) inner = j.framesStacked / j.framesPicked;
                    pct += phaseWeights[p] * inner;
                    break;
                }
                pct += phaseWeights[p] || 0;
            }
            return Math.min(100, Math.max(0, pct));
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

        // --- LSTR-5: live-stack triggers ---

        async loadLiveStackTriggers() {
            try {
                const r = await this.apiGet('/api/livestack/triggers/status');
                if (r?.settings) this.liveStackTriggers = Object.assign({},
                    this.liveStackTriggers, r.settings);
            } catch (e) { /* first load before any save — ignore */ }
        },
        _liveStackTriggersSaveTimer: null,
        saveLiveStackTriggers() {
            if (this._liveStackTriggersSaveTimer) clearTimeout(this._liveStackTriggersSaveTimer);
            this._liveStackTriggersSaveTimer = setTimeout(async () => {
                try {
                    await this.apiPost('/api/livestack/triggers/settings', null, {
                        method: 'PUT',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify(this.liveStackTriggers)
                    });
                } catch (e) {
                    this.toast('Save triggers failed: ' + e.message, 'error');
                }
            }, 500);
        },
        async refocusNow() {
            try {
                await this.apiPost('/api/livestack/triggers/refocus-now');
                this.toast('Refocus fired', 'info');
            } catch (e) { this.toast('Refocus failed: ' + e.message, 'error'); }
        },
        async recenterNow() {
            try {
                await this.apiPost('/api/livestack/triggers/recenter-now');
                this.toast('Recenter fired', 'info');
            } catch (e) { this.toast('Recenter failed: ' + e.message, 'error'); }
        },
        // Format helpers used by the trigger status lines.
        formatRelativeTime(iso) {
            if (!iso) return '—';
            const t = new Date(iso).getTime();
            const dt = (Date.now() - t) / 1000;
            if (dt < 60) return Math.floor(dt) + 's ago';
            if (dt < 3600) return Math.floor(dt / 60) + 'm ago';
            return Math.floor(dt / 3600) + 'h ago';
        },
        formatRaDecShort(raHours, decDeg) {
            if (raHours == null || decDeg == null) return '—';
            const h = Math.floor(raHours);
            const m = Math.floor((raHours - h) * 60);
            const decSign = decDeg >= 0 ? '+' : '-';
            const dAbs = Math.abs(decDeg);
            const dd = Math.floor(dAbs);
            const dm = Math.floor((dAbs - dd) * 60);
            return `${h}h ${m}m, ${decSign}${dd}° ${dm}'`;
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

        // --- Floating mount panel: drag + persistence ---

        // Load saved position/visibility from localStorage. Called from
        // init() so the panel reappears in the same spot across reloads.
        restoreMountPanel() {
            try {
                const raw = localStorage.getItem('mountPanel');
                if (!raw) return;
                const saved = JSON.parse(raw);
                if (typeof saved.x === 'number') this.mountPanel.x = saved.x;
                if (typeof saved.y === 'number') this.mountPanel.y = saved.y;
                if (typeof saved.visible === 'boolean') this.mountPanel.visible = saved.visible;
                this._clampMountPanel();
            } catch { /* corrupt storage — ignore */ }
        },

        persistMountPanel() {
            try {
                localStorage.setItem('mountPanel', JSON.stringify({
                    x: this.mountPanel.x, y: this.mountPanel.y,
                    visible: this.mountPanel.visible
                }));
            } catch { /* storage full / disabled — non-fatal */ }
        },

        // Keep the panel header on-screen when the window resizes or
        // a saved position is now off the viewport (smaller display).
        _clampMountPanel() {
            const w = window.innerWidth, h = window.innerHeight;
            // Leave a 12px margin and at least 80px of the panel visible
            // so the user can always grab the header again.
            const minX = -180, maxX = w - 80;
            const minY = 0, maxY = h - 32;
            this.mountPanel.x = Math.max(minX, Math.min(maxX, this.mountPanel.x));
            this.mountPanel.y = Math.max(minY, Math.min(maxY, this.mountPanel.y));
        },

        mountPanelDragStart(ev) {
            // Don't start a drag from the close button — that has its
            // own click handler with .stop already.
            const isTouch = ev.type === 'touchstart';
            const point = isTouch ? ev.touches[0] : ev;
            this._mountDrag = {
                offsetX: point.clientX - this.mountPanel.x,
                offsetY: point.clientY - this.mountPanel.y,
                touch: isTouch
            };
            const move = (e) => this._mountPanelDragMove(e);
            const end = () => this._mountPanelDragEnd(move, end);
            if (isTouch) {
                window.addEventListener('touchmove', move, { passive: false });
                window.addEventListener('touchend', end);
                window.addEventListener('touchcancel', end);
            } else {
                window.addEventListener('mousemove', move);
                window.addEventListener('mouseup', end);
            }
            ev.preventDefault();
        },

        _mountPanelDragMove(ev) {
            if (!this._mountDrag) return;
            const point = this._mountDrag.touch ? ev.touches[0] : ev;
            this.mountPanel.x = point.clientX - this._mountDrag.offsetX;
            this.mountPanel.y = point.clientY - this._mountDrag.offsetY;
            this._clampMountPanel();
            if (ev.cancelable) ev.preventDefault();
        },

        _mountPanelDragEnd(moveHandler, endHandler) {
            this._mountDrag = null;
            window.removeEventListener('mousemove', moveHandler);
            window.removeEventListener('mouseup', endHandler);
            window.removeEventListener('touchmove', moveHandler);
            window.removeEventListener('touchend', endHandler);
            window.removeEventListener('touchcancel', endHandler);
            this.persistMountPanel();
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
                // Pass driver as query param so the backend dispatches
                // to the correct backend factory. Default 'indi' keeps
                // legacy behaviour unchanged.
                const qs = this.cameraDriver && this.cameraDriver !== 'indi'
                    ? `?driver=${encodeURIComponent(this.cameraDriver)}` : '';
                await this.apiPost(
                    `/api/camera/select/${encodeURIComponent(this.equipCameraChoice)}${qs}`);
                await this.apiPost('/api/camera/connect');
                this.selectedCamera = this.equipCameraChoice;
                this.toast('Camera connected: ' + this.equipCameraChoice, 'ok');
                this.pollCameraInfo();
            } catch (e) {
                this.toast('Camera connection failed: ' + e.message, 'error');
            }
        },

        // Load the list of camera-driver kinds offered by this host
        // and their availability flags. Cached for the session — call
        // again only after the user installs a vendor SDK.
        async loadCameraDrivers() {
            try {
                this.cameraDrivers = await this.apiGet('/api/camera/drivers');
            } catch (e) {
                // Treat any failure as "INDI only" so the dropdown
                // doesn't disappear entirely.
                this.cameraDrivers = [{
                    id: 'indi', name: 'INDI', available: true,
                    description: 'Standard astronomy cameras via INDI server.'
                }];
            }
        },

        // "Detect cameras" button handler. Calls the backend's
        // per-driver discovery endpoint and populates the camera
        // dropdown for vendor SDKs (INDI uses the existing devices
        // list from the connected indiserver).
        async detectVendorCameras() {
            this.cameraDiscovering = true;
            try {
                const list = await this.apiGet(
                    `/api/camera/discover?driver=${encodeURIComponent(this.cameraDriver)}`);
                this.cameraVendorDevices = list || [];
                if (this.cameraVendorDevices.length === 0) {
                    this.toast('No cameras detected for ' + this.cameraDriver, 'warn');
                }
            } catch (e) {
                this.toast('Discovery failed: ' + e.message, 'error');
                this.cameraVendorDevices = [];
            } finally {
                this.cameraDiscovering = false;
            }
        },

        // Compute the currently-selected driver descriptor for UI
        // gating. Used by the template to drive the install banner,
        // capability hides, etc.
        get cameraDriverInfo() {
            return this.cameraDrivers.find(d => d.id === this.cameraDriver) || null;
        },

        get isDslrCamera() {
            return ['canon-edsdk', 'nikon-sdk', 'sony-sdk'].includes(this.cameraDriver);
        },

        async setCameraIso(iso) {
            // The capture endpoint takes per-shot ISO via the request
            // body; this setter is for the manual control on the
            // Equipment tab. Not yet implemented on the backend as a
            // standalone POST — exposed here as a stub so the dropdown
            // is interactive even before that endpoint exists.
            this.cameraIso = +iso;
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
                // Pass driver as query param so the backend dispatches
                // to the right ITelescope factory. Default 'indi' keeps
                // the legacy path unchanged.
                const qs = this.mountDriver && this.mountDriver !== 'indi'
                    ? `?driver=${encodeURIComponent(this.mountDriver)}` : '';
                await this.apiPost(
                    `/api/telescope/select/${encodeURIComponent(this.equipMountChoice)}${qs}`);
                await this.apiPost('/api/telescope/connect');
                this.selectedTelescope = this.equipMountChoice;
                this.mount.connected = true;
                this.toast('Mount connected: ' + this.equipMountChoice, 'ok');
            } catch (e) {
                this.toast('Mount connection failed: ' + e.message, 'error');
            }
        },

        // Load the mount-driver catalogue once per session. Same
        // pattern as loadCameraDrivers — INDI is always available, the
        // WiFi/TCP drivers advertise their availability flag.
        async loadMountDrivers() {
            try {
                this.mountDrivers = await this.apiGet('/api/telescope/drivers');
            } catch (e) {
                this.mountDrivers = [{
                    id: 'indi', name: 'INDI', available: true,
                    description: 'Mounts via INDI server.'
                }];
            }
        },

        // Currently-selected mount-driver descriptor. Drives the
        // install banner + the INDI-dropdown-vs-host-input toggle.
        get mountDriverInfo() {
            return this.mountDrivers.find(d => d.id === this.mountDriver) || null;
        },

        // --- External tools: Siril ------------------------------------

        // Fetch detection status + script catalogue. Called on init
        // and after Settings changes (Re-detect button, path edits).
        async loadSirilStatus() {
            try {
                this.siril.status = await this.apiGet('/api/siril/status');
                if (this.siril.status.available) {
                    await this.sirilReloadScripts();
                }
            } catch (e) {
                // Server might not have SirilEndpoints yet (older build);
                // don't break the rest of the app.
                this.siril.status = { available: false };
            }
        },

        async sirilReloadScripts() {
            try {
                this.siril.scripts = await this.apiGet('/api/siril/scripts');
            } catch (e) {
                this.siril.scripts = [];
            }
        },

        // Re-probe the Siril binary on the server (cache invalidation).
        // Use after the user installs Siril or changes the path override.
        async sirilRedetect() {
            try {
                const r = await this.apiPost('/api/siril/redetect');
                this.siril.status = {
                    available: r.available,
                    binaryPath: r.binaryPath,
                    version: r.version,
                    scriptsCount: this.siril.scripts.length
                };
                if (r.available) await this.sirilReloadScripts();
                this.toast(r.available
                    ? 'Siril detected: v' + (r.version || '?')
                    : 'Siril not found — check the path override', r.available ? 'ok' : 'warn');
            } catch (e) {
                this.toast('Siril detection failed: ' + (e.message || ''), 'error');
            }
        },

        // --- Siril run modal (STUDIO "Stack with Siril" entry point) ---

        sirilOpenRunModal(prefilledLights) {
            if (!this.siril.status?.available) {
                this.toast('Siril is not installed on this host', 'warn');
                return;
            }
            if (this.siril.scripts.length === 0) {
                this.toast('No Siril scripts found', 'warn');
                return;
            }
            // Default to the first OSC preprocessing script if it
            // exists; otherwise just take whatever the catalogue
            // returns first (bundled comes before user, sorted).
            const defaultScript = this.siril.scripts.find(s =>
                /OSC_Preprocessing\.ssf/i.test(s.name)) || this.siril.scripts[0];
            this.siril.modalScriptName = defaultScript?.name || '';
            this.siril.modalTargetName = 'Untitled';
            this.siril.modalLights = (prefilledLights || []).slice();
            this.siril.modalDarks = [];
            this.siril.modalFlats = [];
            this.siril.modalBiases = [];
            // BGE inject is opt-in per run, not sticky — every modal
            // open starts unchecked so the user must consciously add
            // the ~10 s × N frames cost.
            this.siril.modalInjectBge = false;
            this.siril.modalBgePhase = null;
            this.siril.currentJobId = null;
            this.siril.currentJob = null;
            this.siril.modalOpen = true;
        },

        sirilCloseModal() {
            this.siril.modalOpen = false;
            if (this.siril._pollTimer) {
                clearInterval(this.siril._pollTimer);
                this.siril._pollTimer = null;
            }
        },

        async sirilStartRun() {
            if (!this.siril.modalScriptName) {
                this.toast('Pick a script first', 'warn');
                return;
            }
            if (this.siril.modalLights.length === 0) {
                this.toast('No light frames selected', 'warn');
                return;
            }

            // Phase 1 (optional): GraXpert BGE on each light, then
            // swap the lights list to the _bge siblings. We sit in
            // the modal showing BGE progress while this runs, then
            // transition seamlessly into the Siril phase.
            let lightsForSiril = this.siril.modalLights;
            if (this.siril.modalInjectBge && this.graxpert.status?.available) {
                this.toast('Running GraXpert BGE on ' + lightsForSiril.length
                           + ' lights first (this can take a while)…', 'info');
                try {
                    const bgeRun = await this.apiPost('/api/graxpert/run', null, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({
                            paths: lightsForSiril,
                            operation: 'background-extraction',
                            smoothing: this.settings.graxpertBgeSmoothing,
                            correction: this.settings.graxpertBgeCorrection
                        })
                    });
                    this.siril.modalBgePhase = { jobId: bgeRun.jobId, total: lightsForSiril.length,
                                                  done: 0, failed: 0 };
                    // Poll the BGE batch until it terminates.
                    const bgeResult = await this._sirilWaitForBgeBatch(bgeRun.jobId);
                    if (!bgeResult) return;   // user cancelled or error
                    // Resolve the new lights paths from the batch results
                    // (sibling _bge.fits next to each original).
                    lightsForSiril = bgeResult.results
                        .filter(r => !r.error && r.outputPath)
                        .map(r => r.outputPath);
                    if (lightsForSiril.length === 0) {
                        this.toast('GraXpert produced no usable outputs — aborting Siril phase', 'error');
                        this.siril.modalBgePhase = null;
                        return;
                    }
                    this.toast('BGE complete (' + lightsForSiril.length + ' frames clean) — starting Siril', 'ok');
                } catch (e) {
                    this.toast('GraXpert pre-pass failed: ' + (e.message || ''), 'error');
                    this.siril.modalBgePhase = null;
                    return;
                }
            }

            // Phase 2: Siril script on the (possibly BGE-cleaned) lights.
            try {
                const r = await this.apiPost('/api/siril/run', null, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        scriptName: this.siril.modalScriptName,
                        targetName: this.siril.modalTargetName,
                        lightPaths: lightsForSiril,
                        darkPaths: this.siril.modalDarks.length ? this.siril.modalDarks : null,
                        flatPaths: this.siril.modalFlats.length ? this.siril.modalFlats : null,
                        biasPaths: this.siril.modalBiases.length ? this.siril.modalBiases : null
                    })
                });
                this.siril.currentJobId = r.jobId;
                this.toast('Siril job started: ' + r.jobId, 'ok');
                this._sirilStartPolling();
            } catch (e) {
                this.toast('Siril start failed: ' + (e.message || ''), 'error');
            }
        },

        // Promise-style waiter for the BGE pre-pass: polls the
        // GraXpert batch endpoint until completedAt is set, returns
        // the final job snapshot (or null on failure / cancel).
        async _sirilWaitForBgeBatch(jobId) {
            return new Promise(resolve => {
                const tick = async () => {
                    try {
                        const j = await this.apiGet('/api/graxpert/jobs/' + encodeURIComponent(jobId));
                        if (j) {
                            this.siril.modalBgePhase = {
                                jobId, total: j.total, done: j.done, failed: j.failed
                            };
                            if (j.completedAt) { resolve(j); return; }
                        }
                    } catch { /* transient — keep polling */ }
                    setTimeout(tick, 1500);
                };
                tick();
            });
        },

        _sirilStartPolling() {
            if (this.siril._pollTimer) clearInterval(this.siril._pollTimer);
            this.siril._pollTimer = setInterval(async () => {
                if (!this.siril.currentJobId) return;
                try {
                    const job = await this.apiGet('/api/siril/jobs/'
                        + encodeURIComponent(this.siril.currentJobId));
                    this.siril.currentJob = job;
                    if (job && (job.stage === 'done' || job.stage === 'failed'
                                || job.stage === 'cancelled')) {
                        clearInterval(this.siril._pollTimer);
                        this.siril._pollTimer = null;
                        if (job.stage === 'done') {
                            this.toast('Siril finished: ' + (job.resultPath || ''), 'ok');
                        } else if (job.stage === 'failed') {
                            this.toast('Siril failed: ' + (job.lastError || ''), 'error');
                        }
                    }
                } catch (e) {
                    // Transient — keep polling.
                }
            }, 1500);
        },

        async sirilCancelCurrent() {
            if (!this.siril.currentJobId) return;
            try {
                await this.apiPost('/api/siril/jobs/'
                    + encodeURIComponent(this.siril.currentJobId) + '/cancel');
                this.toast('Cancellation requested', 'info');
            } catch (e) {
                this.toast('Cancel failed: ' + (e.message || ''), 'error');
            }
        },

        // --- External tools: GraXpert ----------------------------------

        async loadGraxpertStatus() {
            try {
                this.graxpert.status = await this.apiGet('/api/graxpert/status');
            } catch (e) {
                this.graxpert.status = { available: false };
            }
        },

        async graxpertRedetect() {
            try {
                const r = await this.apiPost('/api/graxpert/redetect');
                this.graxpert.status = r;
                this.toast(r.available
                    ? 'GraXpert detected: v' + (r.version || '?')
                    : 'GraXpert not found — check the path override', r.available ? 'ok' : 'warn');
            } catch (e) {
                this.toast('GraXpert detection failed: ' + (e.message || ''), 'error');
            }
        },

        // Open the GraXpert batch modal for the operation requested
        // (BGE / Decon / Denoise). Defaults pulled from the profile
        // so the modal already has sensible values per op.
        graxpertOpenModal(operation) {
            if (!this.graxpert.status?.available) {
                this.toast('GraXpert is not installed on this host', 'warn');
                return;
            }
            // Source paths come from FILES selection (when called from
            // the FILES toolbar). Studio + Autorun call this with
            // explicit prefilledPaths in F7.
            this.graxpert.modalPaths = (this.files?.selectedPaths || []).slice();
            this.graxpert.modalOp = operation;
            this.graxpert.modalSmoothing = this.settings.graxpertBgeSmoothing;
            this.graxpert.modalCorrection = this.settings.graxpertBgeCorrection;
            this.graxpert.modalSaveBackground = false;
            this.graxpert.modalDeconStrength = this.settings.graxpertDeconStrength;
            this.graxpert.modalDeconPsfSize = this.settings.graxpertDeconPsfSize;
            this.graxpert.modalDenoiseStrength = this.settings.graxpertDenoiseStrength;
            this.graxpert.currentJobId = null;
            this.graxpert.currentJob = null;
            this.graxpert.modalOpen = true;
        },

        graxpertCloseModal() {
            this.graxpert.modalOpen = false;
            if (this.graxpert._pollTimer) {
                clearInterval(this.graxpert._pollTimer);
                this.graxpert._pollTimer = null;
            }
        },

        // Heuristic: if the paths sit under a "lights" folder we
        // warn the user that decon/denoise on individual lights is
        // usually a mistake (they're best on integrated masters).
        graxpertPathsLookLikeLights() {
            return this.graxpert.modalPaths.some(p => /[\\/]lights[\\/]/i.test(p));
        },

        graxpertSuffix(op) {
            return op === 'background-extraction' ? '_bge'
                 : op === 'deconvolution'        ? '_decon'
                 : op === 'denoising'            ? '_denoise'
                 : '_gx';
        },

        graxpertJobPercent() {
            const j = this.graxpert.currentJob;
            if (!j || !j.total) return 0;
            return Math.min(100, Math.round(100 * (j.done + j.failed) / j.total));
        },

        async graxpertStartRun() {
            if (this.graxpert.modalPaths.length === 0) {
                this.toast('No files selected', 'warn');
                return;
            }
            try {
                const r = await this.apiPost('/api/graxpert/run', null, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        paths: this.graxpert.modalPaths,
                        operation: this.graxpert.modalOp,
                        // Backend reads the operation-specific fields
                        // it needs; passing them all is cheap + harmless.
                        smoothing: this.graxpert.modalSmoothing,
                        correction: this.graxpert.modalCorrection,
                        saveBackground: this.graxpert.modalSaveBackground,
                        deconStrength: this.graxpert.modalDeconStrength,
                        deconPsfSize: this.graxpert.modalDeconPsfSize,
                        denoiseStrength: this.graxpert.modalDenoiseStrength
                    })
                });
                this.graxpert.currentJobId = r.jobId;
                this.toast('GraXpert batch started: ' + r.jobId, 'ok');
                this._graxpertStartPolling();
            } catch (e) {
                this.toast('GraXpert start failed: ' + (e.message || ''), 'error');
            }
        },

        _graxpertStartPolling() {
            if (this.graxpert._pollTimer) clearInterval(this.graxpert._pollTimer);
            this.graxpert._pollTimer = setInterval(async () => {
                if (!this.graxpert.currentJobId) return;
                try {
                    const job = await this.apiGet('/api/graxpert/jobs/'
                        + encodeURIComponent(this.graxpert.currentJobId));
                    this.graxpert.currentJob = job;
                    if (job?.completedAt) {
                        clearInterval(this.graxpert._pollTimer);
                        this.graxpert._pollTimer = null;
                        const msg = `GraXpert done — ${job.done} ok, ${job.failed} failed`;
                        this.toast(msg, job.failed ? 'warn' : 'ok');
                        if (this.tab === 'files') {
                            // Refresh the FILES listing so new _bge/_decon/_denoise
                            // siblings show up immediately.
                            try { await this.filesReload(); } catch {}
                        }
                    }
                } catch (e) { /* transient — keep polling */ }
            }, 1500);
        },

        async graxpertCancelCurrent() {
            if (!this.graxpert.currentJobId) return;
            try {
                await this.apiPost('/api/graxpert/jobs/'
                    + encodeURIComponent(this.graxpert.currentJobId) + '/cancel');
                this.toast('Cancellation requested', 'info');
            } catch (e) {
                this.toast('Cancel failed: ' + (e.message || ''), 'error');
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
                this.fetchPhd2EquipmentConnected(),
                // PH2X: presets list + live params (UI binds to these);
                // load once on connect so the pill + Advanced surface aren't blank.
                this.loadPhd2AlgoPresets(),
                this.loadPhd2AlgoParams()
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

        // ----- PH2X-4: Smart Calibrate -----
        async phd2SmartCalibrate() {
            try {
                const r = await this.apiPost('/api/guider/calibrate/smart', {
                    slewToEquator: !!this.smartCalibrate.slewToEquator,
                    timeoutSeconds: 240
                });
                this.toast(`Smart calibrate kicked off (job ${r.jobId?.slice(0, 8)}…)`, 'info');
            } catch (e) { this.toast('Smart calibrate failed: ' + e.message, 'error'); }
        },

        // ----- PH2X-5: Algorithm presets + advanced knobs -----
        async loadPhd2AlgoPresets() {
            try {
                const r = await this.apiGet('/api/guider/algo-presets');
                this.phd2AlgoPresetNames = (r.names || []).concat(['Custom']);
            } catch (e) { /* PHD2 may not be reachable yet */ }
        },
        async phd2ApplyAlgoPreset(name) {
            try {
                await this.apiPost(`/api/guider/algo-preset/${encodeURIComponent(name)}`);
                this.phd2ActivePreset = name;
                this.toast(`Applied preset: ${name}`, 'ok');
                this.loadPhd2AlgoParams();  // refresh live values
            } catch (e) { this.toast('Apply preset failed: ' + e.message, 'error'); }
        },
        async loadPhd2AlgoParams() {
            try {
                const r = await this.apiGet('/api/guider/algo-params');
                this.phd2AlgoParams = r;
            } catch (e) { /* PHD2 disconnected */ }
        },
        async phd2SetAlgoParam(axis, name, value) {
            if (!isFinite(value)) return;
            try {
                await this.apiPost('/api/guider/algo-params', null, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ axis, name, value })
                });
                this.phd2ActivePreset = 'Custom';
                this.toast(`${axis}/${name} = ${value}`, 'ok');
            } catch (e) { this.toast('Set algo param failed: ' + e.message, 'error'); }
        },

        // ----- PH2X-6/8: xpra GUI session lifecycle -----
        async loadPhd2GuiStatus() {
            try {
                this.phd2GuiSession = await this.apiGet('/api/guider/gui-session/status');
            } catch (e) { this.phd2GuiSession = { supportedOs: false, lastError: e.message }; }
        },
        async phd2GuiStart() {
            this.phd2GuiBusy = true;
            try {
                const r = await this.apiPost('/api/guider/gui-session/start');
                if (r.running) {
                    this.toast('PHD2 GUI session started', 'ok');
                } else {
                    this.toast('Start failed: ' + (r.error || 'unknown'), 'error');
                }
            } catch (e) { this.toast('Start failed: ' + e.message, 'error'); }
            finally {
                this.phd2GuiBusy = false;
                await this.loadPhd2GuiStatus();
            }
        },
        async phd2GuiStop() {
            this.phd2GuiBusy = true;
            try {
                await this.apiPost('/api/guider/gui-session/stop');
                this.toast('PHD2 GUI session stopped', 'warn');
            } catch (e) { this.toast('Stop failed: ' + e.message, 'error'); }
            finally {
                this.phd2GuiBusy = false;
                await this.loadPhd2GuiStatus();
            }
        },
        async phd2GuiRestart() {
            this.phd2GuiBusy = true;
            try {
                const r = await this.apiPost('/api/guider/gui-session/restart');
                this.toast(r.running ? 'PHD2 GUI restarted' : ('Restart failed: ' + (r.error || '')),
                    r.running ? 'ok' : 'error');
            } catch (e) { this.toast('Restart failed: ' + e.message, 'error'); }
            finally {
                this.phd2GuiBusy = false;
                await this.loadPhd2GuiStatus();
            }
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
                ra: null, dec: null,
                imageType: 'LIGHT'
            });
        },

        // --- Autorun derived totals (shown in the estimate strip) ---

        sequenceTotalFrames() {
            return this.sequence.reduce((s, it) => s + (Number(it.count) || 0), 0);
        },

        sequenceTotalSeconds() {
            // Per-item: (exposure + ~3s overhead per frame) × count.
            // The 3s constant is the typical readout + write hit observed
            // in the field; close enough for a planning estimate.
            return this.sequence.reduce((s, it) => {
                const exp = Number(it.exposure) || 0;
                const n = Number(it.count) || 0;
                return s + (exp + 3) * n;
            }, 0);
        },

        sequenceEstimatedMB() {
            // Need camera dimensions to estimate file size. If unknown,
            // return 0 and the row hides the disk-hit line.
            const w = this.equipCameraInfo?.width || this.cameraWidthPx;
            const h = this.equipCameraInfo?.height || this.cameraHeightPx;
            if (!w || !h) return 0;
            // 16-bit mono FITS ≈ w*h*2 bytes per frame. Multiply by total frames.
            const bytesPerFrame = w * h * 2;
            return (bytesPerFrame * this.sequenceTotalFrames()) / 1048576;
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

        // --- Activity bar (bottom) helpers --------------------------

        // Derive the chip list from the cached app state. Only
        // transient operations show up — steady-state things like
        // "INDI connected" or "mount tracking" live in the header
        // and aren't duplicated here.
        activityChips() {
            const out = [];

            // Sequence (Autorun)
            if (this.seqState === 'running') {
                const s = this.seqStatus;
                const pct = s?.totalFrames
                    ? Math.round(100 * (s.totalFramesCompleted || 0) / s.totalFrames)
                    : 0;
                out.push({
                    id: 'seq', icon: '📑', kind: 'info',
                    label: `Autorun ${s?.totalFramesCompleted || 0}/${s?.totalFrames || 0}`,
                    progress: pct
                });
            }

            // Auto-focus
            if (this.autoFocus.state === 'running') {
                const i = this.autoFocus.currentSampleIndex ?? 0;
                const n = this.autoFocus.steps ?? 0;
                out.push({
                    id: 'af', icon: '🔄', kind: 'info',
                    label: `Auto-focus ${Math.max(0, i + 1)}/${n}`,
                    progress: n ? Math.round(100 * Math.max(0, i + 1) / n) : 0
                });
            }

            // Meridian flip — any non-idle stage
            if (this.mfState && this.mfState !== 'idle') {
                out.push({
                    id: 'mf', icon: '↔️', kind: 'warn',
                    label: 'Meridian flip: ' + this.mfState
                });
            }

            // Mount slewing
            if (this.mount?.slewing) {
                out.push({ id: 'slew', icon: '🔭', kind: 'info', label: 'Slewing' });
            }

            // Camera exposing / downloading. The state string comes
            // straight from INDI (or vendor-driver State enum); regex
            // covers the variants we've seen across backends.
            const camState = this.equipCameraInfo?.state;
            if (camState && /expos|download|reading/i.test(camState)) {
                out.push({
                    id: 'expose', icon: '📷', kind: 'info',
                    label: 'Camera: ' + String(camState).toLowerCase()
                });
            }

            // Focuser moving
            if (this.focusMoving) {
                out.push({ id: 'focuser', icon: '🎯', kind: 'info', label: 'Focuser moving' });
            }

            // Filter wheel
            if (this.filterWheel?.moving) {
                out.push({ id: 'fw', icon: '🎨', kind: 'info', label: 'Filter change' });
            }

            // PHD2 transient. Steady-state guiding is NOT shown —
            // that's the normal background hum during a sequence.
            // Only the eventful transitions matter for the chip row.
            if (this.guider.calibrating) {
                out.push({ id: 'phd2-cal', icon: '🌟', kind: 'warn', label: 'PHD2 calibrating' });
            } else if (this.guider.settling) {
                out.push({ id: 'phd2-set', icon: '🌟', kind: 'info', label: 'PHD2 settling' });
            }

            // Live stacking
            if (this.liveStackEnabled) {
                out.push({
                    id: 'ls',
                    // CLST-7: show where the stacking math is running.
                    // 🌐 = browser (WASM), 🖥 = server. The chip
                    // doubles as an at-a-glance debug aid when
                    // diagnosing "is my Pi pegged?".
                    icon: (this.liveStackStatus?.mode === 'metricsonly') ? '🌐' : '🖥',
                    kind: 'info',
                    label: `Live stack ${this.liveStackFrames || 0}f`
                });
            }

            // PREVIEW tab snap in flight. Single chip whether it's a
            // one-shot or a loop iteration; the "loop" indicator
            // belongs to the PREVIEW tab itself, not the global bar.
            if (this.preview && this.preview.busy) {
                out.push({
                    id: 'snap', icon: '📸', kind: 'info',
                    label: `Preview ${this.preview.exposure || 0}s`
                });
            }

            // Siril active jobs (one chip per job — usually 1)
            for (const j of (this.sirilActiveJobs || [])) {
                out.push({
                    id: 'siril-' + j.jobId, icon: '⚡', kind: 'info',
                    label: `Siril: ${(j.scriptName || '').replace(/\.ssf$/i, '')} ${j.stage || ''}`,
                    progress: j.percentDone || 0
                });
            }

            // GraXpert active jobs
            for (const j of (this.graXpertActiveJobs || [])) {
                const opIcon = j.operation === 'BackgroundExtraction' ? '🌅'
                             : j.operation === 'Deconvolution'        ? '✨'
                             : '🔇';
                const total = j.total || 0;
                const pct = total ? Math.round(100 * ((j.done || 0) + (j.failed || 0)) / total) : 0;
                out.push({
                    id: 'gx-' + j.jobId, icon: opIcon, kind: 'info',
                    label: `GraXpert ${j.done || 0}/${total}` + (j.failed ? ` (${j.failed} failed)` : ''),
                    progress: pct
                });
            }

            return out;
        },

        // Red > 85%, amber 60-85%, green < 60% — same threshold for
        // both CPU and RAM so the user reads "green/amber/red" the
        // same way across both stats.
        hostCpuClass() {
            const p = this.host.cpuPercent;
            if (p == null) return '';
            return p > 85 ? 'host-red' : p > 60 ? 'host-amber' : 'host-green';
        },
        hostMemClass() {
            const p = this.host.memoryPercent;
            if (p == null) return '';
            return p > 85 ? 'host-red' : p > 60 ? 'host-amber' : 'host-green';
        },
        // Picks an emoji icon matching the device kind classification
        // done server-side by HostInfo.ClassifyLinuxModel /
        // ClassifyWindowsModel. Generic fallback for anything we
        // don't have a specific glyph for.
        hostDeviceIcon() {
            const k = (this.host && this.host.device && this.host.device.kind) || '';
            switch (k) {
                case 'raspberry-pi': return '🍓';
                case 'jetson':       return '🧠';
                case 'rockpi':
                case 'odroid':
                case 'mini-pc':      return '🖥️';
                case 'vm':           return '🧪';
                case 'mac':          return '';
                case 'windows':      return '🪟';
                case 'linux':        return '🐧';
                default:             return '🖥️';
            }
        },
        hostDeviceLabel() {
            return (this.host && this.host.device && this.host.device.shortLabel) || '';
        },

        // --- SIM-6: built-in equipment simulator ---

        // Hydrate the persisted settings (autoStart, devices, port)
        // from the active UserProfile. Called on first visit to the
        // Settings tab + after Re-detect (in case the server's
        // default-resolution logic kicked in).
        async loadSimulatorSettings() {
            if (this._simulatorSettingsLoaded) return;
            try {
                const data = await this.apiGet('/api/simulator/settings');
                this.simulatorSettings = {
                    autoStart: !!data.autoStart,
                    devices: data.devices || ['ccd', 'telescope', 'focus', 'wheel'],
                    port: data.port || 7624
                };
                this._simulatorSettingsLoaded = true;
            } catch (e) { /* server may not be reachable yet */ }
        },

        async saveSimulatorSettings() {
            try {
                await this.apiPost('/api/simulator/settings', null, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(this.simulatorSettings)
                });
            } catch (e) {
                this.toast('Simulator settings save failed: ' + (e.message || e), 'error');
            }
        },

        async simulatorReDetect() {
            try {
                await this.apiPost('/api/simulator/detect');
                this.toast('Simulator re-detected', 'ok');
            } catch (e) {
                this.toast('Re-detect failed: ' + (e.message || e), 'error');
            }
        },

        async simulatorLaunch() {
            // Refresh persisted settings first so the launch matches
            // what the user sees on screen — defensive against the
            // checkbox @change debounce racing the button click.
            await this.saveSimulatorSettings();
            try {
                const resp = await this.apiPost('/api/simulator/launch', {
                    devices: this.simulatorSettings.devices,
                    port: this.simulatorSettings.port
                });
                this.toast('Simulator launched: ' + (resp.devices || []).join(', '), 'ok');
            } catch (e) {
                this.toast('Launch failed: ' + (e.message || e), 'error');
            }
        },

        async simulatorShutdown() {
            try {
                await this.apiPost('/api/simulator/shutdown');
                this.toast('Simulator stopped', 'ok');
            } catch (e) {
                this.toast('Shutdown failed: ' + (e.message || e), 'error');
            }
        },

        // SIM-8: device checkbox toggle handler. Two cases:
        //   - Server stopped: just persist the new device list (used
        //     by the next Launch).
        //   - Server running: send the add/remove command to the
        //     FIFO RIGHT NOW so the user gets immediate feedback
        //     (real-world usage: pick up a guide camera mid-session
        //     without tearing the whole rig down).
        async simulatorOnDeviceToggle(dev) {
            // x-model has already mutated simulatorSettings.devices
            // by the time @change fires — persist that.
            await this.saveSimulatorSettings();
            if (!this.simulator.isRunning) return;

            const enabled = (this.simulatorSettings.devices || []).includes(dev);
            const wasRunning = (this.simulator.runningDevices || []).includes(dev);
            try {
                if (enabled && !wasRunning) {
                    await this.apiPost(`/api/simulator/device/${encodeURIComponent(dev)}/start`);
                    this.toast(`Started ${dev}`, 'ok');
                } else if (!enabled && wasRunning) {
                    await this.apiPost(`/api/simulator/device/${encodeURIComponent(dev)}/stop`);
                    this.toast(`Stopped ${dev}`, 'ok');
                }
            } catch (e) {
                this.toast(`${dev} toggle failed: ${e.message || e}`, 'error');
                // Refresh settings from server to undo the optimistic
                // x-model change so the checkbox visually reflects
                // reality.
                this._simulatorSettingsLoaded = false;
                await this.loadSimulatorSettings();
            }
        },
        // Pre-formatted CPU brand + freq + cores from HostInfo.cs
        // (e.g. "Intel Core i7-12700K @ 3.60 GHz · 20 cores"). Null
        // on hosts where CPU detection failed.
        hostCpuLabel() {
            return (this.host && this.host.device && this.host.device.cpuLabel) || '';
        },
        hostDeviceTooltip() {
            const d = this.host && this.host.device;
            if (!d) return '';
            // model + OS + (arch + cores) + optional CPU brand line.
            // CPU is null on hosts where /proc/cpuinfo or WMI failed
            // — only render the line when we actually have it.
            let s = d.model + '\n' + d.os + '\n' + d.architecture + ' · ' + d.cores + ' cores';
            if (d.cpu) s += '\n' + d.cpu;
            return s;
        },
        formatHostRam(usedMB, totalMB) {
            if (!totalMB || totalMB <= 0) return '— / —';
            // Render in GB once we cross 1 GB total; below that
            // (containers / cgroup limited), stay in MB for accuracy.
            if (totalMB >= 1024) {
                return `${(usedMB / 1024).toFixed(1)} / ${(totalMB / 1024).toFixed(1)} GB`;
            }
            return `${usedMB} / ${totalMB} MB`;
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
                    // First time we learn PHD2 is connected (typically
                    // after a page refresh while PHD2 was already up):
                    // fetch the management state the user would normally
                    // get by clicking Connect — profile list, exposure,
                    // dec mode, equipment-connected flag, algo presets,
                    // algo params, guide-camera/mount snapshot. Without
                    // this the GUIDE-tab Control surface was blank
                    // (empty profile dropdown, no algo preset buttons,
                    // "Connect equipment" stuck even when guiding).
                    const wasDisconnected = !this.guider.connected;
                    if (wasDisconnected) this.fetchGuiderEquipment();
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
                        recentSteps: g.recentSteps || [],
                        // PH2X-9 sub-objects — UI binds chips + state to these.
                        profileSync: g.profileSync || null,
                        calibrateJob: g.calibrateJob || null,
                        guiSession: g.guiSession || null
                    };
                    // Auto-expand chart scale based on peak (with floor of 2")
                    const need = Math.max(this.guider.peakRA, this.guider.peakDec, 1.0) * 1.2;
                    if (need > this.guideChartScale) this.guideChartScale = Math.ceil(need);
                }
                // Even on disconnect, surface the sync/calibrate/gui-session
                // sub-objects so the chips + GUI tab still update.
                if (g.profileSync)  this.guider.profileSync = g.profileSync;
                if (g.calibrateJob) this.guider.calibrateJob = g.calibrateJob;
                if (g.guiSession)   this.phd2GuiSession = g.guiSession;
            }
            if (msg.liveStack) {
                this.liveStackEnabled = msg.liveStack.isRunning;
                this.liveStackFrames = msg.liveStack.frameCount;
                // Whole payload kept around so the triggers panel can
                // read .triggers + per-frame HFR / star count without
                // a second source of truth.
                this.liveStackStatus = msg.liveStack;
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

            // Activity-bar inputs. host, sirilJobs, graXpertJobs are
            // produced by HostMetricsService + the *.ActiveJobs surfaces
            // on the Siril and GraXpert services. The chip-row in the
            // activity bar derives its content purely from these (plus
            // the equipment + sequence state that handlers above
            // already populated).
            if (msg.host) this.host = msg.host;
            // SIM-6: simulator backend status (kind/installed/version/
            // running/runningDevices). The Settings panel binds to this.
            if (msg.simulator) this.simulator = msg.simulator;
            if (msg.cameraStream) {
                // Preserve last-known values so the button label stays
                // readable while the stream service initialises.
                this.cameraStream = Object.assign({}, this.cameraStream, msg.cameraStream);
            }
            if (msg.videoRecording) this.videoRecording = msg.videoRecording;
            if (msg.videoStack !== undefined) this.videoStack = msg.videoStack;  // null when idle
            if (msg.slewPreview) this.slewPreview = msg.slewPreview;
            if (msg.sirilJobs) this.sirilActiveJobs = msg.sirilJobs;
            if (msg.graXpertJobs) this.graXpertActiveJobs = msg.graXpertJobs;

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
