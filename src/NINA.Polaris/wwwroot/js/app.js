// Chart instances live OUTSIDE the Alpine component so Alpine's reactive
// Proxy doesn't wrap them. Chart.js mutates its own internal state during
// every update() / configure() / layout pass, when those objects were
// proxied, each property read went through Alpine's get-trap, registered
// the running effect as a dependency, and re-ran the effect on the next
// internal mutation. Result: infinite recursion ("Maximum call stack size
// exceeded") plus half-applied layout state surfacing as
// "Cannot set properties of undefined (setting 'fullSize')".
// Keeping the registry at module scope guarantees Chart.js sees raw
// instances and stays out of Alpine's reactivity graph.
const _polarisCharts = {
    guide: null, af: null, hfr: null, temp: null, hist: null, alt: null
};

// Astrophoto exposure ladder (seconds). Roughly geometric, covers
// the typical span: planetary lucky-imaging frame times (~50µs upwards
// on modern CMOS), narrowband sub-exposures (300-600s), and the
// rare "leave it on for 1000s" case the user asked for as the upper
// bound. Used by the global #exposure-presets <datalist> so every
// numeric exposure input across the UI shows the same dropdown.
const EXPOSURE_PRESETS_ALL = [
    0.0001, 0.001, 0.01, 0.05, 0.1, 0.5,
    1, 2, 3, 4, 5, 10, 15, 20, 30, 45,
    60, 90, 120, 150, 180, 300, 600, 1000
];

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
        // SHUT-1: timestamp + snapshotted exposure for the live shutter
        // ring. capture() sets these at the start of each exposure so
        // liveShutterCtx().progress() can compute (Date.now() - started)
        // / exposureSec for the SVG dashoffset. nulled when idle so the
        // ring resets to empty between exposures.
        _captureStartedAt: null,
        _captureExposure: 0,
        // SHUT-1: shared shutter timer. setInterval(50ms) when ANY
        // capture is active; ticks shutterTick to force Alpine reactivity.
        shutterTick: 0,
        armingLoop: false,         // true while user holds the shutter
        _shutterRafTimer: null,
        _shutterPressTimer: null,
        _shutterLongPressed: false,
        _shutterArmTimer: null,    // animates the arming ring at 60ms cadence
        _shutterArmStartedAt: 0,
        stats: { starCount: '--', hfr: null, mean: null, snr: null },
        currentTime: '--:--:--',
        cameraTemp: null,
        sessionCaptures: 0,
        imageHistory: [],

        // Live stacking
        // Default true: stacking is continuous + automatic out of
        // the box. The pause toggle in the LIVE tab lets the user
        // flip to raw passthrough when they specifically don't
        // want stacking (rare). Hydrated from the WS status payload
        // on first connect so what the browser shows matches the
        // server's actual state.
        liveStackEnabled: true,
        liveStackFrames: 0,
        // SNR-7: session-level target SNR override + ETA debounce
        // timer (PUT is fired ~400 ms after the user stops typing).
        // null in the input = "use the active rig's TargetSnr".
        liveStack: { targetSnrInput: null, _saveTargetTimer: null },
        // Auto-pause cap, MINUTES (the UI is friendlier in minutes
        // even though the backend stores seconds). 0 = unlimited.
        // Hydrated from the active rig on _applyRigToChoices and
        // persisted via PUT /api/livestack/max-duration.
        liveStackMaxMinutes: 0,
        // CLST-7: per-rig override for where the math runs.
        // "auto" = let the server pick based on WASM handshake (default).
        // "server" / "client" = force. Hydrated from active rig +
        // persisted via PUT /api/equipment/rigs/{id}.
        liveStackComputeMode: 'auto',
        // Per-frame disk persistence toggle. Mirrors the LIVE tab
        // checkbox. PUT /api/livestack/save-frames updates both the
        // running service flag and the active rig's
        // LiveStackSaveFramesToDisk profile field, so the choice
        // survives a Polaris restart + rig switch. Default ON —
        // most users want both the integrated preview AND an
        // archive they can re-stack offline.
        liveStackSaveFrames: true,

        // LSTR-5: live-stack auto-refocus + auto-recenter triggers.
        // Mirror of EquipmentProfile.LiveStackTriggers, hydrated from
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
            pierSide: 'unknown', connected: false,
            // Capabilities flags from MountCapabilities.cs, populated
            // each WS tick. Drives per-button visibility (Park, Find
            // Home, pier side indicator) so AltAz / non-GEM mounts
            // don't show buttons that would 501 if clicked.
            capabilities: { park: true, trackingToggle: true, sync: true,
                            pierSide: true, manualJog: true, findHome: false,
                            setSiteLocation: false }
        },

        // Floating mount control inside the Sky tab. Position lives
        // in localStorage so the panel comes back where the user left
        // it. _drag is transient state held during a mouse/touch drag.
        mountPanel: { x: 24, y: 80, visible: true },
        // Live-camera preview floats next to the mount panel by
        // default. Shares the same draggable + persist pattern.
        // Visibility flag lives in slewPreviewVisible above so the
        // floating 📷 Camera pill keeps the same hook.
        cameraPanel: { x: 24, y: 360 },
        _mountDrag: null,

        // AUTH-3: shared-password gate for the local server.
        // Boot flow: init() calls _authBoot() which restores any
        // saved token, hits /api/auth/status, and either:
        //  - configured=false -> needSetup=true (wizard overlay)
        //  - enabled=false -> nothing, app loads as before
        //  - configured=true && !authenticated -> needLogin=true
        //  - authenticated=true -> token persists, rest of init runs
        // 401 from apiFetch flips needLogin=true and shows overlay.
        // rememberMe=true persists in localStorage (cross-session),
        // false sticks to sessionStorage (cleared on tab close).
        auth: {
            token: '',
            configured: false,
            enabled: true,
            authenticated: false,
            sessionTimeoutHours: 24,
            needLogin: false,
            needSetup: false,
            loginPassword: '',
            loginError: '',
            rememberMe: false,
            // Wizard fields
            setupPassword: '',
            setupConfirm: '',
            setupError: '',
            // Change-password modal fields (Settings)
            cpOpen: false,
            cpCurrent: '',
            cpNew: '',
            cpConfirm: '',
            cpError: '',
            cpBusy: false,
            // Disable toggle confirm modal (Settings)
            disableOpen: false,
            disablePassword: '',
            disableError: '',
            disableBusy: false,
            booting: true        // hides app shell until /status responds
        },

        // CLOCK-3: server / client wall-clock skew tracker. Driven
        // by the WS status payload's server.utcNow (refreshed every
        // 1s). When |skew| > 30s the activity bar shows a chip; the
        // Settings card surfaces a Sync button that POSTs the
        // client's UTC into /api/system/clock/sync.
        clockSync: {
            serverUtc: '',
            skewSeconds: 0,
            supported: false,
            busy: false,
            lastSyncAt: null,
            lastError: null
        },

        // HELP-1: in-app tutorial state. tutorial null = landing
        // (4-card picker); otherwise one of 'firstNight', 'capture',
        // 'workflowsPicker', 'lrgb', 'planetary', 'pcc', 'troubleshoot'.
        // step is a 0-based index into the chosen tutorial's array.
        // Persisted to localStorage so refreshing or jumping into
        // another tab and coming back lands you on the same step.
        help: {
            tutorial: null,
            step: 0,
            landingHintDismissed: false
        },

        // Focus
        focusPosition: 0,
        // SLOW step: small, single-arrow buttons (◀ / ▶). For ZWO EAF
        // these are the safe-no-crash steps -- empirically 1-5 steps
        // work on all firmware revisions. Default 5.
        focusStepSlow: 5,
        // FAST step: large, double-arrow buttons (◀◀ / ▶▶). User can
        // crank this up for quick coarse focus. Backend chunks any
        // move > 100 steps into safe sub-moves with settle waits, so
        // even a 1000-step click won't crash the driver -- just takes
        // proportionally longer wall-clock to complete.
        focusStepFast: 50,
        // Legacy alias kept for code that still reads focusStep
        // (computed getter would be cleaner but Alpine reactivity is
        // simpler with a plain mirrored field updated on input).
        focusStep: 50,
        focusTemp: null,
        focusMoving: false,
        focusConnected: false,
        // Driver-reported MaxPosition (capped travel of the focuser
        // gear). Used by sliders that want an absolute scale (VIDEO
        // sidebar) so the user can drag from min to max without
        // hand-typing a ceiling. Defaults to 0 = "unknown / show no
        // slider".
        focusMaxPosition: 0,
        // Pending value while the user drags the absolute-position
        // slider in VIDEO. Decoupled from focusPosition so the WS
        // status push (~1 Hz) does not snap the slider back under
        // your finger mid-drag. Committed on @change (mouseup /
        // touchend).
        focusSliderTarget: 0,
        focusSliderDirty: false,

        // Spec-compliance v2: capabilities hidrated from focuser.capabilities
        // sub-object in the WS payload. UI gates the Sync / Reverse /
        // Backlash sections on these so users with drivers that don't
        // expose the matching FOCUS_* properties don't see buttons
        // that would just 501. All default to false so the controls
        // stay hidden until the backend confirms support.
        focuserCaps: { sync: false, reverse: false, backlash: false, temperature: false },
        // Spec-compliance v2: collapsible settings card state. Each
        // sub-form holds the pending values the user is editing
        // before they hit Apply; not coupled to the live focuser
        // position so the WS tick doesn't trash them.
        focuserSettings: {
            // FOCUS_SYNC: position to write when the user clicks
            // Sync. Pre-filled with current Position when the card
            // opens so "Sync to current" is one click.
            syncPosition: 0,
            // FOCUS_REVERSE_MOTION: pending toggle state. Not
            // round-tripped from WS — the driver doesn't broadcast
            // current reverse state, so the toggle reflects what
            // the user has set in THIS session.
            reversed: false,
            // FOCUS_BACKLASH_*: pending enable + step count.
            backlashEnabled: false,
            backlashSteps: 0,
            // Show/hide the disclosure section. Default collapsed
            // because most users never touch these after setup.
            open: false,
            busy: false,
            lastError: null,
            lastMessage: null
        },

        // MFOC: FOCUS tab subtab. 'assist' = manual HFR loop +
        // Bahtinov; 'vcurve' = the existing motor-driven V-curve.
        // Defaults to whichever is more useful given the current
        // hardware (no motor → assist).
        focusTab: 'assist',

        // MFOC-1: rolling state for the Manual Assist loop.
        // Samples is a circular buffer of the last 60 captures; each
        // entry is { t, hfr, fwhm, starCount, laplacian }.
        // bestHfr tracks the lowest non-NaN HFR seen since the last
        // Reset baseline click (the chart marks it with a horizontal
        // line so the user knows when they overshot).
        manualFocus: {
            running: false,
            exposureSec: 2,
            gain: 100,
            intervalSec: 2,
            minStars: 3,
            samples: [],
            bestHfr: null,
            lastError: null,
            // MFOC-4: Bahtinov mask analysis. When showBahtinov is on
            // the loop POSTs /api/focus/bahtinov after each capture
            // and overlays the spike geometry on the manual-focus
            // canvas. lastFrameWidth/Height come back with the
            // /api/camera/capture stats so we can scale frame-pixel
            // coords (analyser output) to canvas-pixel coords (display
            // is a fitted scale).
            showBahtinov: false,
            bahtinovResult: null,
            bahtinovError: null,
            lastFrameWidth: 0,
            lastFrameHeight: 0,
            // SHUT-3: timestamp of the current loop cycle for the
            // shutter ring fill. Reset at the top of each
            // _manualFocusTick + each manualFocusSnap.
            _tickStartedAt: null
        },
        _manualFocusTimer: null,

        // Filter Wheel
        filterWheel: {
            connected: false,
            position: 0,
            currentFilter: '',
            filters: [],
            moving: false,
            // Spec-compliance v2: hidrated from filterWheel.capabilities
            // in the WS payload. UI hides the "Edit names" affordance
            // for wheels that don't advertise edit support (ASCOM/Alpaca,
            // by design — names are managed externally there).
            capabilities: { editNames: false }
        },
        selectedFilterWheel: null,
        // Spec-compliance v2: in-place edit state for the filter
        // name editor. Cloned from filterWheel.filters when the user
        // clicks Edit, written back via PUT /api/filterwheel/names
        // on Save. Decoupled from the live filterWheel.filters so a
        // mid-edit WS push doesn't trash what the user is typing.
        filterNamesEdit: {
            open: false,
            names: [],
            busy: false,
            lastError: null,
            lastMessage: null
        },

        // Sky
        skySearch: '',
        skyTarget: null,

        // User toggle: when ON, picking a target (map click, search
        // hit, Stellarium import) smoothly pans the sky engine to
        // the chosen object via _skyLookAt. When OFF, the same picks
        // still populate skyTarget + the skyInfo card, but the
        // engine view stays exactly where the user left it, useful
        // for browsing without losing the current framing.
        // Persisted in localStorage so it survives reloads. Default
        // ON to match the previous behaviour.
        skyAutoCenterOnSelect: true,

        // User toggle: stream the DSS Color HiPS from CDS Strasbourg
        // as a deep-sky background image. Default ON, the whole
        // point of having a real engine vs a vector renderer is
        // seeing the actual sky when you zoom into a target. Turn
        // off when offline (the engine logs HEALPix tile 404s
        // otherwise) or when the user prefers a cleaner vector view.
        skyDssVisible: true,

        // Remote terminal (xterm.js + /ws/terminal SSH bridge).
        // Credentials are never persisted, every Connect prompts
        // again. Terminal:Enabled=false on the server returns 403
        // and the section still renders the form but Connect toasts
        // the error and stops. _term* fields hold the running xterm
        // + WebSocket + addon so we can tear down cleanly.
        term: {
            host: 'localhost', port: 22, user: '', password: '',
            connected: false, connecting: false, lastError: ''
        },
        _termInstance: null, _termSocket: null, _termFitAddon: null,
        _termResizeObserver: null,

        // Global UI zoom (CSS body { zoom: X }). uiZoom is the
        // committed/applied value (what's currently on
        // body.style.zoom); uiZoomDraft is the slider position
        // while the user is dragging. They sync only when Apply
        // (or Reset) is clicked, without that two-step, the
        // page reflowed under the slider's own cursor on every
        // input tick and aiming a value became impossible.
        // First-paint default comes from localStorage if set,
        // otherwise from the viewport-based @media defaults (1.0
        // desktop, 0.85 ≤960px, 0.75 ≤640px) so phones still get
        // the smaller UI on first load.
        uiZoom: 1.0,
        uiZoomDraft: 1.0,

        // FONT-1: app-wide font picker. Values match the
        // [data-font="..."] selectors in app.css that override
        // --font-body / --font-mono. 'atkinson' is the default
        // (Braille Institute's Atkinson Hyperlegible — unique
        // letter shapes so B/8, l/1/I, 0/O never confuse, best
        // for the older / low-vision operators Polaris targets);
        // 'inter' is the previous default kept as a clean modern
        // alternative (vendored InterVariable in css/fonts/);
        // 'plex' is IBM Plex Sans for a corporate-tech vibe;
        // 'system' falls back to the OS UI font. The setter is
        // an attribute on <html> rather than a class, so the
        // initial load can apply via inline script before the
        // CSS even parses (avoids FOUT).
        uiFont: 'atkinson',

        // SWE-5: object-info card overlay on the sky map. Populated
        // when the bridge emits a map-click with a rich object payload
        // (the user clicked on a recognised star/DSO/planet rather
        // than empty sky). The thumbnail comes from the same /api/sky/
        // image endpoint Tonight's Best already consumes.
        skyInfo: {
            visible: false,
            title: '',
            subtitle: '',
            icon: '',
            imageUrl: '',
            magnitude: null,
            distanceKm: null,
            radiusKm: null,
            raDeg: null,
            decDeg: null,
            types: null,
            // Populated async from /api/sky/altitude. samples = altitude
            // track tonight (sunset → sunrise, 15 min step); transit /
            // setText = human-readable "time-to-meridian" + "time-to-
            // horizon" derived from the same track on the client.
            altitudeSamples: null,
            transitText: '',
            setText: '',
        },
        _skyInfoChart: null,
        skyResults: [],
        skyShowResults: false,
        slewCenterJobId: null,
        slewCenterStatus: null,
        _slewCenterTimer: null,
        fov: { width: 2.82, height: 1.88 },

        // Sequence
        sequence: [],
        seqState: 'idle',
        // FW-2: AUTORUN sub-tab toggle. 'sequence' = the existing
        // editor; 'flat' = Flat Wizard sub-tab. Defaults to sequence
        // so existing muscle memory + first-open lands on the
        // familiar pane. Switching to 'flat' triggers
        // flatWizardOpenTab() which hydrates form + trained cache.
        autorunTab: 'sequence',
        // Flat Wizard state. Form fields mirror EquipmentProfile.FlatWizard
        // (FW-1) and are hydrated by flatWizardOpenTab() from
        // activeRig.flatWizard. WS payload populates state/progress/
        // lastError each tick via handleStatusMessage absorption.
        flatWizard: {
            state: 'idle',         // 'idle' | 'running'
            progress: null,        // populated while running, see WS payload
            lastError: null,
            trained: {},           // { "L_bin1": 1.85, "R_bin1": 2.34, ... }
            // Form mirror of EquipmentProfile.FlatWizard, defaults
            // match FlatWizardSettings.cs so a brand-new browser
            // session renders sane values before the rig hydrates.
            targetAdu: 30000,
            tolerance: 0.05,
            framesPerFilter: 20,
            minExposureSec: 0.1,
            maxExposureSec: 30.0,
            binning: 1,
            maxSearchIterations: 10,
            panelBrightness: 0,
            selectedFilters: []
        },
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
            // DBGLOG-9: when ON, LogRotatorService flushes every log
            // entry to a JSONL file under {LocalAppData}/NINA.Polaris/logs/.
            // Default OFF; the in-memory ring buffer is enough for most
            // bug-hunting and saves SD-card write cycles on the Pi.
            logToDisk: false,
            // Boot-time auto-connect, INDI + Alpaca discovery +
            // active-rig device bind. Pushed by HardwareAutoConnectService.
            autoConnectOnStartup: false,
            // External tools, see ExternalTools section in Settings.
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
            // GX-1b: ONNX in-browser inference (GraXpert AI models)
            onnxModelsPath: '',
            onnxLicenseAcknowledged: false,
            onnxDefaultDenoiseVersion: '2.0.0',
            onnxPreferCli: false,
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

        // ZWO ASI gain presets (L/M/H). Mirrors ASIAIR's three-button
        // shortcut: L = lowest gain (planets/lunar, max dynamic range),
        // M = balanced general-purpose, H = HCG threshold for the
        // sensor (best SNR for narrowband / deep sky, lower DR).
        // Loaded once from /data/zwo-gain-presets.json on init.
        // null until the fetch resolves; presetsForActiveCamera() returns
        // null if the active camera isn't recognised as a ZWO model.
        _zwoPresets: null,

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

        // Mount driver state, same shape as camera. Today the
        // dropdown shows INDI + synscan-wifi (the rest of the
        // catalogue is "(not installed)"). mountDriver drives the
        // ?driver= query param on /api/telescope/select; the
        // equipMountChoice input doubles as "INDI device name" or
        // "host:port" depending on driver.
        mountDriver: 'indi',
        mountDrivers: [],
        // Per-driver telescope discovery, populated by Detect when
        // mountDriver === 'ascom-com'. Same shape as
        // cameraVendorDevices.
        mountVendorDevices: [],
        mountDiscovering: false,

        // Focuser + filter-wheel driver state. Same shape as camera /
        // mount, drives the ?driver= query string on /select. INDI
        // dropdown reads from the live indiserver device list; ASCOM
        // dropdown reads from the local Windows ASCOM registry via
        // /api/focuser/discover?driver=ascom-com. Hidden when only
        // INDI is available (e.g. Linux server).
        focuserDriver: 'indi',
        focuserDrivers: [],
        focuserVendorDevices: [],
        focuserDiscovering: false,
        filterWheelDriver: 'indi',
        filterWheelDrivers: [],
        filterWheelVendorDevices: [],
        filterWheelDiscovering: false,

        // ASCOM SetupDialog modal state. True while the driver's
        // setup form is open; disables the Setup button to prevent
        // a second concurrent dialog (drivers usually crash on that).
        ascomSetupBusy: false,

        // PREVIEW tab, snap test shots. Defaults match what a
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
            lastStats: null,     // { mean, median, stdev, starCount, hfr, min, max }
            // SHUT-2: snapshot of when the active snap started + its
            // exposure (seconds). Fed by previewTakeSnap() and read
            // by previewShutterProgress() for the ring fill.
            _snapStartedAt: null,
            _snapExposure: 0
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

        // VIDEO tab state, planetary capture (SER) + lucky-imaging stack.
        // Driven by VideoRecordingService + PlanetaryStackerService on the
        // server; the WS status feed populates videoRecording / videoStack.
        videoTab: 'capture',       // 'capture' | 'process'
        equipTab: 'equipment',     // 'equipment' | 'indi-web' (RIGS sub-tabstrip)
        video: {
            exposure: 0.05,
            gain: 200,
            binning: 1,
            targetName: 'planet',
            maxDurationSec: 60,
            wbR: 50,
            wbB: 50,
            // FOV / ROI state. roiSize = 0 means full sensor; non-zero
            // (square pills) keeps the square aspect for compatibility
            // with the prior shape. roiW/roiH/roiX/roiY mirror the
            // last-applied subframe (any aspect ratio). Persisted on
            // the active rig (LastVideoRoi*).
            roiSize: 0,
            roiW: 0, roiH: 0,
            roiX: 0, roiY: 0,
            roiAspect: 'square',     // 'square' | '4:3' | '16:9' (for active-pill check)
            roiHintDismissed: false,
            // Process side
            processSerPath: '',
            serList: [],          // [{ path, label }]
            keepPercent: 50,
            outputName: 'stack'
        },
        // Per-camera capability flags loaded from /api/camera/status on
        // tab open. Drives WB-slider visibility + ROI / cooler / ISO
        // conditional UI inside VIDEO Capture. maxX/maxY come from
        // the same status payload so the FOV pills can clamp options
        // larger than the actual sensor.
        cameraCaps: {
            cooler: false, binning: false, roi: false, iso: false,
            bulb: false, videoStream: false, whiteBalance: false,
            maxX: 0, maxY: 0
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
        // User toggle for whether the camera preview WINDOW is shown
        // when SlewPreviewService says a stream is live. Defaults to
        // true so the existing auto-show behaviour stays the same on
        // first run. The × button on the inset flips this to false;
        // the floating "📷 Camera" pill flips it back to true.
        // Persisted in localStorage so the choice survives reloads.
        slewPreviewVisible: true,

        // Custom confirm() modal -- replaces window.confirm() so OK/Cancel
        // labels are in English regardless of browser locale. Driven via
        // _confirmAsync() which returns a Promise that resolves true on
        // confirm, false on cancel/ESC/backdrop click. _resolver is the
        // pending Promise resolver (set by _confirmAsync, called by
        // _confirmAccept/_confirmCancel). Reused across the app -- any
        // call site that previously used window.confirm() can switch to
        // `if (await this._confirmAsync('Title?')) { ... }`.
        confirmModal: { open: false, title: '', message: '',
                        okLabel: 'OK', cancelLabel: 'Cancel', danger: false },
        _confirmResolver: null,

        // Activity bar (bottom). Populated from the status WS message
        // each second. host comes from HostMetricsService; sirilActiveJobs
        // and graXpertActiveJobs are compact summaries of the respective
        // ActiveJobs surfaces. The chip row is computed locally via the
        // activityChips() helper so we don't duplicate state.
        host: {
            cpuPercent: null, memoryPercent: null,
            memoryUsedMB: 0, memoryTotalMB: 0,
            processCpuPercent: 0, processMemoryMB: 0,
            // Pi under-voltage flags. Both default false → chips
            // hidden on non-Pi hosts where the probe returns nothing.
            // underVoltageNow drives a RED chip (active power problem
            // right now -- expect imminent USB crashes). occurred
            // drives a quieter AMBER chip ("rail sagged at some
            // point since boot -- consider a powered hub").
            underVoltageNow: false,
            underVoltageOccurred: false
        },
        sirilActiveJobs: [],
        graXpertActiveJobs: [],

        // NET-1: client-side network activity indicator. rxRate/txRate
        // are bytes/sec averaged over a sliding 3s window so brief
        // 0-byte gaps don't flicker the readout to zero. rxPulse/txPulse
        // are momentary booleans that drive a 120ms CSS keyframe each
        // time bytes arrive, gives a LED-style "data flowing"
        // confirmation that's easier to spot than the changing number.
        // rxTotal/txTotal cumulate session bytes for the hover tooltip.
        net: {
            rxRate: 0, txRate: 0,
            rxPulse: false, txPulse: false,
            rxTotal: 0, txTotal: 0,
        },

        // XFER-1: in-flight HTTP transfers. ASIAIR-style per-transfer
        // progress bar in the activity bar so the user sees real
        // upload / download progress instead of guessing from the
        // ambient rxRate / txRate. Wired by apiUpload (XHR-based, gives
        // upload.onprogress) and apiDownload (fetch + ReadableStream
        // reader, counts bytes as chunks arrive). Each transfer has:
        //   { id, label, direction: 'up'|'down', loaded, total, done }
        // total=0 means "size unknown" — the chip shows an indeterminate
        // bar. done=true triggers a short "✓ Done" hold before removal.
        transfers: [],
        _nextTransferId: 1,
        // DBGLOG-6: client-side log mirror of the server ring buffer.
        // entries: rolling window of LogEntry objects ingested from the
        //   /ws/status debugLog sub-object + locally-generated client
        //   entries (toasts, apiFetch metadata, window.onerror). Capped
        //   at 5000 so we mirror the server-side MaxKept.
        // cursor: server-assigned Id of the most-recent entry we've
        //   absorbed; the WS sub-object advances it monotonically.
        // unreadErrors / unreadWarnings: counters since the last time
        //   the user opened the log panel. Drives the badge colour +
        //   number in the header.
        // panelOpen: fullscreen overlay shown when the user clicks the
        //   header badge. Filters live in filterLevel / filterSource /
        //   searchText, all server-side via GET /api/logs query params.
        logs: {
            entries: [],
            cursor: 0,
            unreadErrors: 0,
            unreadWarnings: 0,
            panelOpen: false,
            filterLevel: 'info',
            filterSource: 'all',
            searchText: '',
            truncated: false,
            persistToDisk: false
        },
        // Client-side batch queue + debounce timer. POST /api/logs/client
        // is fired ~1s after the last entry queues OR when the queue
        // hits 50 entries, whichever first. Keeps a runaway error loop
        // (e.g. window.onerror in a render loop) from saturating both
        // the network and the server ring buffer.
        _logClientQueue: [],
        _logClientFlushTimer: null,
        _logHooksInstalled: false,
        // Internal: deltas accumulated since last meter tick. Tick
        // reads + zeroes them. Separate from the rolling window samples
        // so the meter can compute "bytes in last 3s" cheaply.
        _netDeltaRx: 0,
        _netDeltaTx: 0,
        // Rolling samples for 3s window. {tMs, dRx, dTx} pushed at each
        // tick; entries older than 3000ms dropped before computing the
        // displayed rate.
        _netSamples: [],
        // Tracks the last PerformanceResourceTiming index we counted so
        // each tick only sums new entries (no double-count).
        _netRtLastIdx: 0,

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

        // INDI-WEB-3: indi-web (indiwebmanager) iframe-based driver
        // management UI lives inside the RIGS panel. `indiWeb.status`
        // mirrors GET /api/indi/web/status (refreshed on RIGS open +
        // after each lifecycle button click). `autoStart` is a UI-
        // local mirror of the IndiWeb:AutoStart config flag persisted
        // via /api/system/settings (same plumbing as PHD2 auto-start).
        indiWeb: { status: null, autoStart: false, busy: false },
        // INDI Control Panel sub-tab in RIGS. The launch button posts
        // /api/indi/cp/launch which uses `xpra control :100 start-child
        // indi_control_panel` to spawn the binary inside the same xpra
        // session that already hosts the PHD2 GUI. The iframe points
        // at /phd2-gui/ since both windows live in that one display;
        // a cache-busted timestamp on the src forces the HTML5 client
        // to reconnect (otherwise it might still be focused on the
        // PHD2 window). `launched` gates the iframe so we don't load
        // /phd2-gui/ on first tab entry when nothing was launched yet.
        indiCp: { status: null, busy: false, launched: false,
                  iframeSrc: '/phd2-gui/' },

        // INDIPROP-2: native in-process property browser. Replaces the
        // xpra-hosted indi_control_panel Qt tool with a 100% browser-
        // side editor over the IndiClient.Devices snapshot exposed by
        // /api/indi/properties. State:
        //   devices         current snapshot from GET /api/indi/properties
        //   selectedDevice  optional filter, "" = show all
        //   search          free-text filter applied to name+label
        //   collapsedGroups dict of "device/group" → true when collapsed
        //   autoRefresh     when true, schedules a 2s tick (only while
        //                   the indi-cp sub-tab is visible)
        //   busy            in-flight GET; suppresses overlapping fetches
        //   lastError       last /set rejection (400/403/500)
        indiProps: {
            devices: [],
            selectedDevice: '',
            search: '',
            collapsedGroups: {},
            autoRefresh: true,
            busy: false,
            lastError: ''
        },
        _indiPropsTimer: null,

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

        // PH2X tab + state. guideTab toggles between the xpra-hosted GUI
        // iframe (default; setup work happens there) and the JSON-RPC
        // control panel (monitoring + automation).
        guideTab: 'gui',
        phd2AlgoPresetNames: [],
        phd2ActivePreset: 'Default',
        phd2AlgoParams: null,            // { axes: { ra: {Hyst:0.1, ...}, dec: {...} } }
        smartCalibrate: { slewToEquator: false },
        phd2GuiSession: null,            // { supportedOs, xpraInstalled, running, port, ... }
        phd2GuiBusy: false,
        // PH2VNC: Windows TightVNC + noVNC bridge state, sibling of
        // phd2GuiSession. Populated by /api/guider/vnc-session/status
        // and refreshed every 1 Hz via the WS payload's
        // guider.vncSession sub-block. null until the first status
        // load so the GUIDE tab's branching uses optional-chaining.
        phd2VncSession: null,            // { supportedOs, tightVncInstalled, serviceRunning, listening, ... }
        phd2VncBusy: false,

        // WIFI-4: NetworkManagerService snapshot, updated each 1Hz WS tick.
        // null until the first payload arrives so the template gates on
        // (network && network.supportedOs) instead of flashing the
        // "unsupported" banner on first paint.
        network: null,                   // { mode, ssid, ip, signal, supportedOs, ... }
        networkSwitching: false,
        networkStation: {
            open: false, ssid: '', hiddenSsid: '', password: '',
            scanResults: [], scanning: false, lastError: ''
        },
        networkHotspot: {
            open: false, ssid: 'Polaris-Hotspot', password: '', lastError: ''
        },

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
            servers: [],          // [{ host, port, serverName, manufacturer, serverVersion, devices: [...] }]
            connected: {},        // key: host:port:type:num → true (legacy manual-flow flag)
            totalDevices: 0,      // count after last Discover (across all servers)
            autoConnected: [],    // [{ host, port, deviceType, deviceNumber, deviceName, ok, error }]
            showDetails: false,   // expand the per-server device table for debugging
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
        atlasFilter: { type: '', catalog: '', constellation: '',
                       minMag: null, maxMag: null, minDec: null, maxDec: null },
        atlasResults: [],
        atlasTypes: [],
        // CAT-4: list of catalog sources available in the DSO DB
        // ('NGC','IC','M','C','Arp','Sh2','HCG','AGC'). Empty array
        // when the bundled DB is missing — the catalog dropdown
        // hides itself via x-show in that case.
        atlasCatalogs: [],
        altitudeData: null,

        // Weather forecast (7Timer via /api/weather/forecast).
        // forecast is the raw DTO from the backend. weatherDays() /
        // weatherBestWindows() (declared below) derive view-model data
        // on the fly, using SunCalc for sun + moon ephemeris.
        weather: { forecast: null, loading: false, error: '', lastFetched: null },
        _weatherLastKey: '',

        // Studio (post-processing), ST-1 frame browser + ST-2 viewer
        studio: {
            frames: [],
            stats: null,
            rescan: null,
            filter: { type: '', target: '', filter: '' },
            selectedIds: [],
            // Tree-view companion state: the single frame currently
            // displayed in the right-pane preview (separate from
            // selectedIds, which is the multi-select for batch
            // actions). previewUrl is bumped after auto-stretch
            // resolves so the <img> swaps in one go.
            activeFrame: null,
            activePreviewUrl: '',
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
            },
            // CC-6: channel combine modal. Three tabs (rgb / lrgb /
            // pixelmath) backed by their own mapping state so the user
            // can flip between tabs without losing selections. Common
            // controls (register, normalize) sit above the tabs.
            // Mappings hold frameId per variable name; UI pre-fills
            // them from the selected frames' filters on open.
            combine: {
                open: false,
                running: false,
                activeTab: 'rgb',
                register: true,
                normalize: true,
                lrgbAlgo: 'lab',         // 'lab' | 'ratio'
                monoOutput: false,
                rgb:  { mapping: { R: null, G: null, B: null } },
                lrgb: { mapping: { R: null, G: null, B: null, L: null } },
                pm: {
                    rows: [],            // [{ var: 'Ha', frameId: 42 }, ...]
                    expressions: ['R', 'G', 'B']
                },
                lastJob: null
            },
            // CCALB-1/2/3: Siril-style color calibration on a single
            // selected RGB master. Three tabs (BG / Manual / PCC)
            // mirror the combine modal pattern.
            colorCal: {
                open: false,
                running: false,
                activeTab: 'bg',         // 'bg' | 'manual' | 'pcc'
                frameId: null,
                sourceName: '',
                bgSample: 'auto',        // 'auto' | 'patch'
                bgPatch:    { x: 0, y: 0, w: 64, h: 64 },
                whitePatch: { x: 0, y: 0, w: 64, h: 64 },
                catalogStatus: null,     // populated by studioOpenColorCalDialog
                lastJob: null
            }
        },
        _studioRescanPoll: null,
        _studioViewerDebounce: null,
        _studioHistogramChart: null,
        _studioMasterPoll: null,
        _studioCalibratePoll: null,
        _studioIntegratePoll: null,
        _studioCombinePoll: null,
        _studioColorCalPoll: null,

        // Observatory location helpers (Settings → Observatory)
        obsAddressQuery: '',
        obsAddressLoading: false,
        obsAddressError: '',
        obsAddressResults: [],
        obsGpsLoading: false,

        // Tonight's Best, ranked list from /api/sky/tonights-best, plus
        // a per-name thumbnail cache filled on demand by _kickTonightThumbs.
        tonight: {
            items: [], envelope: null, loading: false, error: '',
            lastFetched: null, filter: 'all', fitsFovOnly: false,
            thumbs: {}      // { [name]: { url, title, credit, missing } }
        },

        // ── Editor (Lightroom-style) state. The .edits subtree mirrors
        // the EditParams record on the server; we always send it whole on
        // every preview call so the pipeline doesn't have to remember
        // anything between requests. previewUrl + originalUrl are Blob
        // URLs we revoke when superseded, important on a long session
        // (otherwise the browser leaks ~100 MB per few minutes of slider
        // drags). Debounce timer keeps preview requests at ~10/s max
        // regardless of how fast the slider fires "input" events.
        editorState: {
            // Modal visibility. The Lightroom-style editor lives as a
            // fullscreen overlay opened on demand (Edit toolbar button
            // in FILES, or filesOpenInEditor()). The Stack workspace
            // on the right side of the FILES tab stays fixed under it.
            modalOpen: false,
            session:  null,        // sessionId from /api/editor/load
            sourcePath: '',
            width:    0,
            height:   0,
            channels: 1,
            pathInput: '',         // text in the "open by path" field
            loading:  false,
            // Path of the file currently being fetched. Shown under
            // the loading spinner so the user has feedback while
            // /api/editor/load runs (FITS reads from a slow disk
            // can take 1-3 s for a multi-hundred-MB master).
            loadingPath: '',
            rendering: false,
            autoBusy: false,       // AUTOED-2: true while /auto is in flight
            error:    '',
            edits:    {},          // EditParams shape
            dirty:    false,       // edits changed since last sidecar save
            previewUrl: '',        // current edited preview blob URL
            originalUrl: '',       // unedited preview blob URL (for compare)
            showOriginal: false,
            // Histogram overlay toggle. The histogram lives as an
            // absolutely-positioned strip pinned to the bottom of
            // the preview area instead of stealing a permanent
            // 80 px stripe below it. Default ON so first-time
            // users still see the curve; click the toolbar button
            // to dismiss when working tight on vertical space.
            showHistogram: true,
            exportModal: false,
            exporting: false,
            // ED-6 compute target: 'server' is the always-available
            // fallback; 'wasm' renders client-side via the AOT bundle.
            // Persists via localStorage so the user's preference survives
            // a reload. Auto-falls-back to server on bundle load failure.
            computeMode: 'server',
            wasmLoaded:  false,    // true once the working buffer is
                                   // sitting in the WASM heap for the
                                   // current session

            // Zoom + pan state. CSS transform on .editor-preview-stage:
            // translate(panX, panY) scale(zoom). Reset = fit-to-window
            // (zoom 1, pan 0). Mouse wheel zooms toward the cursor;
            // click-drag pans when zoomed > 1.
            zoom: 1,
            panX: 0,
            panY: 0,
            panning: false,
            _panStartX: 0, _panStartY: 0,
            _panOriginX: 0, _panOriginY: 0,

            // Non-destructive undo/redo. Stack holds JSON-serialised
            // snapshots of `edits`; index points to the current entry.
            // Pushing while index < stack.length-1 truncates the
            // redo tail (matches Lightroom/PixInsight behaviour).
            history: { stack: [{}], index: 0 },

            export: {
                format: 'jpg',
                quality: 92,
                resizeMode: 'none',  // 'none' | 'long' | 'pct'
                resizeValue: 100
            }
        },
        _editorPreviewTimer: null,
        _editorPendingPreview: false,
        // Slider drag-aware throttling. The WASM EditorApplyEdit call
        // is synchronous on the main thread; on a 20MP master a single
        // render costs 100-300ms which makes a continuous slider drag
        // feel stuck. While the user is actively dragging we (a) drop
        // the preview maxDim to a smaller value (4x fewer pixels =
        // ~4x faster), (b) skip the histogram render entirely, (c)
        // yield to requestAnimationFrame before each WASM call so the
        // browser paints the slider position before the heavy work
        // starts, (d) bump the debounce slightly. On pointerup we
        // re-fire one full-quality render + histogram.
        _editorDragging: false,
        _editorDragSettleTimer: null,
        _editorHistTimer: null,
        editorLightSliders: [
            { key: 'exposure',   label: 'Exposure',   min: -5,   max: 5,   step: 0.05, dp: 2 },
            { key: 'contrast',   label: 'Contrast',   min: -1,   max: 1,   step: 0.01, dp: 2 },
            { key: 'highlights', label: 'Highlights', min: -1,   max: 1,   step: 0.01, dp: 2 },
            { key: 'shadows',    label: 'Shadows',    min: -1,   max: 1,   step: 0.01, dp: 2 },
            { key: 'whites',     label: 'Whites',     min: -1,   max: 1,   step: 0.01, dp: 2 },
            { key: 'blacks',     label: 'Blacks',     min: -1,   max: 1,   step: 0.01, dp: 2 },
        ],
        editorColorSliders: [
            { key: 'vibrance',   label: 'Vibrance',   min: -1,   max: 1,   step: 0.01, dp: 2 },
            { key: 'saturation', label: 'Saturation', min: -1,   max: 1,   step: 0.01, dp: 2 },
            { key: 'hue',        label: 'Hue',        min: -180, max: 180, step: 1,    dp: 0 },
        ],
        editorEffectsSliders: [
            { key: 'texture',         label: 'Texture',  min: -1,   max: 1,   step: 0.01, dp: 2 },
            { key: 'clarity',         label: 'Clarity',  min: -1,   max: 1,   step: 0.01, dp: 2 },
            { key: 'dehaze',          label: 'Dehaze',   min: -1,   max: 1,   step: 0.01, dp: 2 },
            { key: 'vignetteAmount',  label: 'Vignette', min: -1,   max: 1,   step: 0.01, dp: 2 },
        ],
        editorDetailSliders: [
            { key: 'sharpenAmount',   label: 'Sharpen',  min: 0,    max: 1,   step: 0.01, dp: 2 },
            { key: 'sharpenRadius',   label: 'Radius',   min: 0.5,  max: 5,   step: 0.1,  dp: 1, default: 1 },
            { key: 'noiseReduce',     label: 'Noise red.', min: 0,  max: 1,   step: 0.01, dp: 2 },
        ],
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
            // UNIF-4: opt-in FITS metadata columns in the list. Reads
            // initial value from localStorage so a power user who
            // always wants them on doesn't have to re-toggle each
            // session.
            showFitsMeta: (typeof localStorage !== 'undefined'
                && localStorage.getItem('polaris-files-fitsmeta') === '1'),
            selectedPaths: [],
            clipboard: null,
            loading: false,
            error: '',
            preview: { open: false, path: '', name: '', kind: '', textContent: null }
        },
        filesPersistMetaToggle() {
            try {
                if (typeof localStorage === 'undefined') return;
                localStorage.setItem('polaris-files-fitsmeta',
                    this.files.showFitsMeta ? '1' : '0');
            } catch (e) { /* private mode / quota */ }
        },
        _filesLastShiftIndex: -1,    // anchor for shift-click range selection

        // UNIF-1: FILES panel sub-tab. The merged tab contains two
        // workspaces that share the browser to the left:
        //   'stack' -- the old STUDIO workflow: classify selected
        //              files into lights/darks/flats/biases slots,
        //              then trigger Master/Calibrate/Integrate/...
        //   'edit'  -- the old EDITOR workflow: load a single FITS
        //              into OpenSeadragon + sliders.
        // Persisted to localStorage so the user returns to the
        // last-used workspace on reload.
        // Stack workspace is now always visible on the right side of
        // the FILES tab; the editor lives in a fullscreen overlay
        // modal opened on demand. filesSubTab stays around for
        // backward compatibility with any legacy bindings, but the
        // setter just redirects 'edit' to opening the modal.
        filesSubTab: 'stack',
        setFilesSubTab(name) {
            if (name === 'edit') {
                this.editorOpenModal();
            } else if (name === 'stack') {
                this.editorCloseModal();
            }
        },
        editorOpenModal() {
            this.editorState.modalOpen = true;
            // Lock background scroll so the underlying FILES tab
            // doesn't drift while the user is in the editor.
            try { document.body.style.overflow = 'hidden'; } catch (e) {}
        },
        editorCloseModal() {
            this.editorState.modalOpen = false;
            try { document.body.style.overflow = ''; } catch (e) {}
        },

        // UNIF-2: Stack workspace state. Four user-classified slots
        // (lights/darks/flats/biases) hold absolute paths added via
        // the "+ Adicionar selecao" button on each card. Three
        // optional master-frame slots let users plug an existing
        // master_dark/flat/bias into Calibrate without having to
        // recreate it. activeJob mirrors the polling pattern of the
        // old STUDIO jobs panel (job id + progress %).
        //
        // Persisted as a whole object in localStorage so a refresh
        // or server restart doesn't wipe the user's day of curating.
        // Migration safe: missing keys read as []; unexpected types
        // get coerced to [] on load.
        stack: (() => {
            const empty = {
                lights: [], darks: [], flats: [], biases: [],
                masterDarks: [], masterFlats: [], masterBiases: [],
                activeJob: null
            };
            try {
                if (typeof localStorage === 'undefined') return empty;
                const raw = localStorage.getItem('polaris-stack-slots');
                if (!raw) return empty;
                const parsed = JSON.parse(raw);
                for (const k of Object.keys(empty)) {
                    if (k === 'activeJob') continue;
                    if (!Array.isArray(parsed[k])) parsed[k] = [];
                }
                parsed.activeJob = null;   // job state is session-scoped
                return parsed;
            } catch (e) { return empty; }
        })(),

        // Slot metadata used by both the Add buttons and the slot
        // cards. Keeping the labels Portuguese-first because that's
        // where the rest of the UI lives ("Lights" stays English
        // since that's the astrophotography term users recognise).
        stackSlotDefs: [
            { key: 'lights', label: 'Lights', hint: 'Capture frames (target)' },
            { key: 'darks',  label: 'Darks',  hint: 'Thermal calibration (same exp + gain as lights)' },
            { key: 'flats',  label: 'Flats',  hint: 'Optical calibration (vignetting + dust)' },
            { key: 'biases', label: 'Biases', hint: 'Read-noise calibration (minimum exposure)' }
        ],

        _stackPersist() {
            try {
                if (typeof localStorage === 'undefined') return;
                const snap = {
                    lights: this.stack.lights,
                    darks: this.stack.darks,
                    flats: this.stack.flats,
                    biases: this.stack.biases,
                    masterDarks: this.stack.masterDarks,
                    masterFlats: this.stack.masterFlats,
                    masterBiases: this.stack.masterBiases
                };
                localStorage.setItem('polaris-stack-slots', JSON.stringify(snap));
            } catch (e) { /* quota / private mode */ }
        },

        // Only FITS / XISF-like files belong in stacking slots. Reject
        // dirs + non-image extensions silently when the user multi-
        // selects a mixed bag and clicks Add.
        _stackIsStackable(path) {
            if (!path) return false;
            const lower = path.toLowerCase();
            return lower.endsWith('.fits') || lower.endsWith('.fit')
                || lower.endsWith('.fts') || lower.endsWith('.xisf');
        },

        // Push current browser selection into a slot (with dedupe),
        // clear the browser selection so the user gets visual
        // feedback that the assignment happened, persist, toast a
        // summary. Rejects when no .fits/.fit/.xisf in the selection.
        stackAddToSlot(slotKey) {
            if (!Array.isArray(this.stack[slotKey])) return;
            const selected = (this.files.selectedPaths || [])
                .filter(p => this._stackIsStackable(p));
            if (selected.length === 0) {
                this.toast('Select FITS files in the browser before adding', 'warn');
                return;
            }
            const before = this.stack[slotKey].length;
            const merged = [...this.stack[slotKey]];
            const seen = new Set(merged);
            for (const p of selected) {
                if (!seen.has(p)) { merged.push(p); seen.add(p); }
            }
            const added = merged.length - before;
            const dupes = selected.length - added;
            this.stack[slotKey] = merged;
            this._stackPersist();
            this.files.selectedPaths = [];
            const slotDef = this.stackSlotDefs.find(s => s.key === slotKey);
            const label = slotDef ? slotDef.label : slotKey;
            const msg = `${added} adicionado(s) a ${label}`
                + (dupes > 0 ? ` (${dupes} ja estava(m) no slot)` : '');
            this.toast(msg, added > 0 ? 'ok' : 'info', 2400);
        },

        stackRemoveFromSlot(slotKey, path) {
            if (!Array.isArray(this.stack[slotKey])) return;
            this.stack[slotKey] = this.stack[slotKey].filter(p => p !== path);
            this._stackPersist();
        },

        stackClearSlot(slotKey) {
            if (!Array.isArray(this.stack[slotKey])) return;
            if (this.stack[slotKey].length === 0) return;
            if (!confirm(`Limpar slot ${slotKey} (${this.stack[slotKey].length} arquivos)?`)) return;
            this.stack[slotKey] = [];
            this._stackPersist();
        },

        // UNIF-3b: action handlers. Each posts paths from the relevant
        // slot to /api/studio/* and polls the job status until done.
        // Parameters are collected via prompt() to keep this commit
        // small -- proper modals come back in a follow-up. Tracks the
        // active job in stack.activeJob so the UI shows progress.

        async _stackPollJob(endpoint, jobId, label) {
            this.stack.activeJob = { label, jobId, stage: 'queued',
                                     done: 0, total: 0 };
            const start = Date.now();
            for (;;) {
                await new Promise(r => setTimeout(r, 800));
                let status;
                try {
                    status = await this.apiGet(endpoint + jobId
                        + (endpoint.endsWith('/integrate/') || endpoint.endsWith('/calibrate/')
                            || endpoint.endsWith('/masters/') ? '/status' : ''));
                } catch (e) {
                    this.stack.activeJob = null;
                    this.toast(label + ' status fetch failed: ' + e.message, 'error');
                    return null;
                }
                this.stack.activeJob = {
                    label, jobId,
                    stage: status.stage || 'running',
                    done: status.done ?? 0,
                    total: status.total ?? 0
                };
                if (status.stage === 'done' || status.stage === 'error'
                    || status.inProgress === false) {
                    this.stack.activeJob = null;
                    if (status.stage === 'error' || status.error) {
                        this.toast(label + ' failed: ' + (status.error || 'unknown'), 'error');
                        return null;
                    }
                    const secs = Math.round((Date.now() - start) / 1000);
                    this.toast(label + ' done in ' + secs + 's', 'ok');
                    return status;
                }
                if (Date.now() - start > 30 * 60 * 1000) {
                    this.stack.activeJob = null;
                    this.toast(label + ' timed out after 30 min', 'error');
                    return null;
                }
            }
        },

        async stackRunMaster(kind) {
            // kind ∈ 'dark' | 'flat' | 'bias'
            const slot = kind === 'dark' ? 'darks' : kind === 'flat' ? 'flats' : 'biases';
            const paths = this.stack[slot];
            if (paths.length < 2) {
                this.toast('Need at least 2 ' + slot + ' to make a master', 'warn');
                return;
            }
            const methodIn = prompt(
                'Integration method (Mean / Median / SigmaClippedMean):',
                'SigmaClippedMean');
            if (!methodIn) return;
            const type = kind.charAt(0).toUpperCase() + kind.slice(1);
            let resp;
            try {
                resp = await this.apiPost('/api/studio/masters', {
                    framePaths: paths, type, method: methodIn.trim()
                });
            } catch (e) {
                this.toast('Master ' + kind + ' submit failed: ' + e.message, 'error');
                return;
            }
            const body = await resp.json();
            if (!body.jobId) {
                this.toast('Master ' + kind + ' rejected: '
                    + (body.error || 'unknown'), 'error');
                return;
            }
            await this._stackPollJob('/api/studio/masters/', body.jobId,
                'Master ' + kind);
        },

        async stackRunCalibrate() {
            if (this.stack.lights.length === 0) {
                this.toast('Add lights first', 'warn');
                return;
            }
            // If the user populated master slots, use their pinned
            // pick; otherwise let the backend auto-match by gain +
            // exposure + filter from the FrameLibrary index.
            const dPath = this.stack.masterDarks[0] || null;
            const fPath = this.stack.masterFlats[0] || null;
            const bPath = this.stack.masterBiases[0] || null;
            const msg = 'Calibrate ' + this.stack.lights.length + ' lights?'
                + '\n\nMaster overrides:'
                + '\n  Dark:  ' + (dPath ? this.stackBasename(dPath) : '(auto-match)')
                + '\n  Flat:  ' + (fPath ? this.stackBasename(fPath) : '(auto-match)')
                + '\n  Bias:  ' + (bPath ? this.stackBasename(bPath) : '(auto-match)');
            if (!confirm(msg)) return;
            let resp;
            try {
                resp = await this.apiPost('/api/studio/calibrate', {
                    lightPaths: this.stack.lights,
                    masterDarkPath: dPath,
                    masterFlatPath: fPath,
                    masterBiasPath: bPath
                });
            } catch (e) {
                this.toast('Calibrate submit failed: ' + e.message, 'error');
                return;
            }
            const body = await resp.json();
            if (!body.jobId) {
                this.toast('Calibrate rejected: ' + (body.error || 'unknown'), 'error');
                return;
            }
            await this._stackPollJob('/api/studio/calibrate/', body.jobId,
                'Calibrate');
        },

        async stackRunIntegrate() {
            if (this.stack.lights.length < 2) {
                this.toast('Need at least 2 lights to integrate', 'warn');
                return;
            }
            const methodIn = prompt(
                'Integration method (Mean / Median / SigmaClippedMean):',
                'SigmaClippedMean');
            if (!methodIn) return;
            if (!confirm('Integrate ' + this.stack.lights.length
                + ' lights with method ' + methodIn.trim() + '?')) return;
            let resp;
            try {
                resp = await this.apiPost('/api/studio/integrate', {
                    framePaths: this.stack.lights,
                    method: methodIn.trim()
                });
            } catch (e) {
                this.toast('Integrate submit failed: ' + e.message, 'error');
                return;
            }
            const body = await resp.json();
            if (!body.jobId) {
                this.toast('Integrate rejected: ' + (body.error || 'unknown'), 'error');
                return;
            }
            await this._stackPollJob('/api/studio/integrate/', body.jobId,
                'Integrate');
        },

        // UNIF-3c: Channel Combine. Treats the lights slot as a
        // collection of per-filter mono masters. User declares the
        // variable name (R, G, B, L, Ha, OIII, SII, etc) for each
        // entry via prompt(); empty/cancel drops that entry.
        // Mode pick is RGB / LRGB / PixelMath; only RGB + LRGB are
        // wired here (PixelMath needs an expression editor).
        async stackRunCombine() {
            if (this.stack.lights.length < 2) {
                this.toast('Need at least 2 per-filter masters in Lights', 'warn');
                return;
            }
            const mode = (prompt(
                'Combine mode: rgb | lrgb', 'rgb') || '').trim().toLowerCase();
            if (mode !== 'rgb' && mode !== 'lrgb') {
                this.toast('Cancelled (mode must be rgb or lrgb)', 'info');
                return;
            }
            const need = mode === 'lrgb' ? 4 : 3;
            if (this.stack.lights.length < need) {
                this.toast(mode.toUpperCase() + ' needs at least ' + need
                    + ' inputs in Lights slot', 'warn');
                return;
            }
            const channelMap = [];
            for (const p of this.stack.lights) {
                const v = (prompt(
                    'Variable for ' + this.stackBasename(p)
                        + '\n(R, G, B' + (mode === 'lrgb' ? ', L' : '')
                        + ' -- empty to skip)',
                    '') || '').trim();
                if (!v) continue;
                channelMap.push({ variable: v, framePath: p });
            }
            const required = mode === 'lrgb' ? ['R', 'G', 'B', 'L'] : ['R', 'G', 'B'];
            for (const r of required) {
                if (!channelMap.some(c => c.variable.toUpperCase() === r)) {
                    this.toast('Missing variable "' + r + '" for ' + mode.toUpperCase(), 'warn');
                    return;
                }
            }
            let lrgbAlgo = null;
            if (mode === 'lrgb') {
                lrgbAlgo = (prompt('LRGB algorithm: lab | ratio', 'lab')
                    || 'lab').trim().toLowerCase();
                if (lrgbAlgo !== 'lab' && lrgbAlgo !== 'ratio') lrgbAlgo = 'lab';
            }
            let resp;
            try {
                resp = await this.apiPost('/api/studio/combine', {
                    mode: mode === 'rgb' ? 'rgb' : 'lrgb',
                    channelMap,
                    register: true,
                    normalize: true,
                    lrgbAlgo
                });
            } catch (e) {
                this.toast('Combine submit failed: ' + e.message, 'error');
                return;
            }
            const body = await resp.json();
            if (!body.jobId) {
                this.toast('Combine rejected: ' + (body.error || 'unknown'), 'error');
                return;
            }
            // /api/studio/combine status route uses /{jobId} (no /status).
            await this._stackPollJob('/api/studio/combine/', body.jobId,
                'Combine ' + mode.toUpperCase());
        },

        // UNIF-3c: Color Calibration -- only BG-auto in v1. Manual
        // and PCC need patch-picker + catalog-status UI which is
        // bigger than the prompt() approach supports cleanly.
        async stackRunColorCal() {
            if (this.stack.lights.length === 0) {
                this.toast('Add the master integrated FITS to Lights first', 'warn');
                return;
            }
            const path = this.stack.lights[0];
            if (!confirm('Background-neutralise (auto sample mode):\n\n'
                + this.stackBasename(path)
                + '\n\nOutput will be a sibling FITS with the _bgneu suffix.\n'
                + '(For Manual + PCC modes, use the old endpoint via curl '
                + 'until the richer modal lands.)')) return;
            let resp;
            try {
                resp = await this.apiPost('/api/studio/colorcal', {
                    framePath: path,
                    mode: 'bg',
                    bgSample: 'auto'
                });
            } catch (e) {
                this.toast('ColorCal submit failed: ' + e.message, 'error');
                return;
            }
            const body = await resp.json();
            if (!body.jobId) {
                this.toast('ColorCal rejected: ' + (body.error || 'unknown'), 'error');
                return;
            }
            await this._stackPollJob('/api/studio/colorcal/', body.jobId,
                'Color Cal (BG)');
        },

        stackClearAll() {
            const total = this.stackSlotDefs.reduce(
                (s, def) => s + this.stack[def.key].length, 0);
            if (total === 0) return;
            if (!confirm(`Limpar TODOS os slots (${total} arquivos)?`)) return;
            for (const def of this.stackSlotDefs) this.stack[def.key] = [];
            this.stack.masterDarks = [];
            this.stack.masterFlats = [];
            this.stack.masterBiases = [];
            this._stackPersist();
            this.toast('Slots limpos', 'ok');
        },

        // Pretty short name from an absolute path (just the filename).
        // Used by the slot cards to keep the list scannable.
        stackBasename(path) {
            if (!path) return '';
            const i = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'));
            return i >= 0 ? path.slice(i + 1) : path;
        },

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
            _pollTimer: null,
            // Live console feed from the Siril subprocess. consoleLines
            // is the accumulating tail (capped at 500 server-side, the
            // UI grows linearly until the next job starts and resets
            // it). _consoleSince is the cursor the next poll passes
            // back as ?sinceLine= so each round-trip only ships new
            // lines. consoleFollow toggles auto-scroll on append;
            // flipped to false automatically when the user scrolls up
            // (sirilConsoleOnScroll) so reading old output isn't
            // interrupted, flipped back to true when they scroll to
            // the bottom.
            consoleLines: [],
            _consoleSince: 0,
            consoleFollow: true,
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
            // GX-12h: parity with GraXpert UI, pick the dedicated
            // decon-stars or decon-objects ONNX model. Browser path
            // forwards as opts.target; CLI path tags the family in
            // the API request.
            modalDeconTarget: 'stars',
            modalDenoiseStrength: 0.5,
            // GX-12k: per-run denoise model version. Defaults to the
            // profile's onnxDefaultDenoiseVersion (settings) when the
            // modal opens, but the user can override per-run from the
            // dropdown. Two versions ship: 2.0.0 (lighter, ~284 MB,
            // safer on iOS) and 3.0.2 (more aggressive, ~456 MB,
            // ±1 clip, better quality on capable hardware).
            modalDenoiseVersion: '2.0.0',
            currentJobId: null,
            currentJob: null,
            _pollTimer: null,
            // GX-2: browser-mode (ONNX) run state. Default toggle ON
            // when an operation has its model available in the
            // manifest (onnxAvailableForOp). When the user clicks Start,
            // graxpertStartRun branches on this, true → browser
            // pipeline, false → existing CLI subprocess.
            modalRunInBrowser: true,
            browserActive: false,
            // Set by graxpertAbortRun() to signal the browser-mode
            // loop to break out at its next safe checkpoint. The
            // pipeline.run() call itself can't be hard-killed mid-tile
            // (WebGPU shaders run sync from JS's POV), but we can stop
            // launching new tiles + skip subsequent files in the batch.
            browserAbortRequested: false,
            browserDone: 0,
            browserTotal: 0,
            browserPhase: '',
            browserProgress: 0,
        },

        // CROP-2: drag-rectangle crop picker state. open=true while the
        // modal is showing. roi tracks the rectangle the user drew in
        // DISPLAY pixels (relative to the picker's bounding rect), with
        // start/end captured on pointer down / up. cropStartRun converts
        // DISPLAY → IMAGE coords using imgNaturalWidth / imgDisplayWidth
        // ratio before POST /api/crop/run so the server slice math
        // operates in real pixel space regardless of how the browser
        // scaled the preview.
        crop: {
            open: false,
            sourcePath: '',     // single file path being cropped
            outputName: '',     // basename for the toast on success
            previewUrl: '',     // /api/files/preview?path=... + auth token
            error: '',
            busy: false,
            // Drag rectangle in DISPLAY-pixel coordinates relative to
            // .crop-picker bounding rect. Both null when no selection.
            roi: { startX: null, startY: null, endX: null, endY: null },
            dragging: false,
            // Captured on <img> load — image's intrinsic dimensions
            // (the FITS source resolution) and how the browser laid
            // them out after max-height/max-width clamping. Used to
            // convert ROI back to image coords before POST.
            imgNaturalWidth: 0,
            imgNaturalHeight: 0,
            imgDisplayWidth: 0,
            imgDisplayHeight: 0,
        },

        // GX-5: editor "AI" section runtime state. Single in-flight
        // button across the section, pipelines are heavy + don't
        // compose with each other anyway. phase is a user-facing
        // status string for the section's progress line.
        editorAi: { busy: false, phase: '' },

        // GX-1b: in-browser ONNX inference state (GraXpert AI models).
        // manifest mirrors what GET /api/onnx/manifest returns; cacheSize
        // is in bytes (browser IndexedDB store sum). Pipelines (GX-2/3/4)
        // hang job state off this object as they land.
        onnx: {
            manifest: null,        // { modelsPath, models: [...] }
            scanning: false,
            clearingCache: false,
            cacheSize: 0,
            licenseAcknowledged: false,
            backends: null,        // ['webgpu', 'wasm'] after first probe
        },
        // GX-6: consent modal state. _onnxLicenseResolver is the Promise
        // resolver awaited by `_ensureOnnxLicenseAccepted`; the
        // I-agree / cancel buttons resolve true/false to it.
        onnxLicenseModalOpen: false,
        _onnxLicenseResolver: null,

        // GX-10: HTTPS endpoint info, populated from
        // /api/system/https-info on startup. null until first fetch
        // completes so x-show guards in the template hide the banner.
        // Shape: { httpsEnabled, httpsPort, fingerprint,
        //          hostnames: [...], exampleHttpsUrls: [...] }
        httpsInfo: null,

        // GX-12q: cert-install instructions modal. Open from the
        // Settings → HTTPS endpoints "Install instructions →" button.
        // `os` auto-picks based on the visiting device's UA so the
        // tab the user sees first is the right one for the device
        // they're holding; they can still flip to the others.
        certModal: {
            open: false,
            os: 'windows',   // 'windows' | 'macos' | 'ios' | 'android' | 'linux'
        },

        certModalOpen() {
            // UA sniff to default the active tab to the user's OS.
            // Cheap heuristic; user can flip tabs if we got it wrong
            // (e.g. cross-device install from a desktop browser).
            const ua = (navigator.userAgent || '').toLowerCase();
            const platform = (navigator.platform || '').toLowerCase();
            let os = 'windows';
            if (/iphone|ipad|ipod/.test(ua)) os = 'ios';
            else if (platform === 'macintel' && navigator.maxTouchPoints > 1) os = 'ios';
            else if (/android/.test(ua)) os = 'android';
            else if (/mac/.test(platform) || /macintosh/.test(ua)) os = 'macos';
            else if (/linux/.test(platform) || /linux/.test(ua)) os = 'linux';
            this.certModal.os = os;
            this.certModal.open = true;
        },

        // GX-11: before/after comparator state. Auto-opens at the end
        // of a GraXpert run (BGE / Denoise / Decon) with the source
        // FITS and the freshly-written sibling. Slider position is
        // 0..1 (0=full source, 1=full output, 0.5=split in the middle).
        // pairs is [{ src, out, label }, ...]; we render only pairs[0]
        // today, but the array shape leaves room for a future
        // "step through multiple files" arrow control.
        //
        // GX-12g: `mode` distinguishes the GraXpert-op auto-open
        // (`'gx'`, default, shows generic BEFORE / AFTER tags since
        // the user just performed a known transformation) from the
        // arbitrary FILES "↔ Compare" button (`'compare'`, shows
        // the actual filenames in the corner tags so the user can
        // tell two unrelated files apart).
        graxpertCompare: {
            open: false,
            pairs: [],
            index: 0,
            split: 0.5,
            dragging: false,
            mode: 'gx',
            // GX-12r: which GraXpert op produced these pairs, drives
            // the modal title ("GraXpert Denoise Comparison" vs
            // "GraXpert Decon Comparison" etc). Null when the user
            // opens the comparator via the FILES "Compare" button
            // (no op context, mode='compare' rules instead).
            op: null,
        },

        // d3-celestial Sky Viewer (offline, BSD-3-Clause).
        // Always renders the live sky from the observer's location at the
        // current UTC time, in horizontal projection, same convention as
        // ASIAIR. No mode toggle: we only support equatorial mounts, so an
        // alternate "equatorial chart" view would just duplicate this one
        // with a different rotation axis and worse UX (drag pivoting
        // around the celestial pole feels wildly off-axis).
        // SWE-6: _celestialReady, _fovLayerId, _skyTicker dropped along
        // with d3-celestial. The stellarium-web-engine bridge has its own
        // ready signal (_skyBridgeReady) and runs the sky clock internally.
        _fovLayerId: null,
        skyClock: '',                    // displayed in the toolbar (HH:MM:SS UTC)
        locationLabel: '',               // "City, Country" if reverse-geocoded, else "5.18°S 37.36°W"
        _locationLastKey: '',            // memoise so we only reverse-geocode once per coord pair
        aladinFov: 45,                  // initial FOV in degrees, 90 was
                                        // too wide (full-sky Aitoff looks
                                        // distorted + camera FOV rectangle
                                        // shrinks below 1 pixel)
        aladinShowFov: true,             // toggle camera-FOV overlay

        // OpenSeadragon image viewer
        imageViewerOpen: false,
        _osdViewer: null,
        // URL the viewer should load. Defaults to the live-camera
        // preview; FILES tab overrides it to point at any file. Reset
        // to default on close so the next open from any other tab
        // still gets the latest frame.
        imageViewerUrl: '/api/image/latest/preview',
        imageViewerTitle: 'Image Viewer, full resolution',
        // FITS header overlay panel inside the image viewer. Open
        // automatically when previewing a .fits/.fit/.fts file from
        // the FILES tab, toggleable by the user via the toolbar
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
        // LIVE tab image-history + HFR chart are now a semi-transparent
        // overlay over the stacked image. Default hidden so the focus
        // stays on the master being built. User toggles via the
        // history button in .preview-overlay-controls; preference
        // persisted in localStorage so it survives reloads.
        liveOverlayVisible: false,
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

        // Client-side white-balance (display tint applied in the WebGL
        // shader AFTER stretch). Shared across LIVE, PREVIEW, FOCUS,
        // VIDEO so changing it anywhere updates the preview everywhere.
        // Defaults 1.7 / 1.5 approximate daylight WB on raw OSC data;
        // wb.auto = true makes the next-frame handler recompute via
        // gray-world from the actual pixels. NOT the same as the
        // hardware-side video.wbR / video.wbB which write WB_R/WB_B
        // to the camera driver (different OSCs expose those very
        // differently — display tint always works).
        wb: { r: 1.7, b: 1.5, auto: false },
        _wbLastAutoAt: 0,
        showWbPanel: false,

        // Temperature history (sensor temp samples for chart)
        tempHistory: [],     // [{t: msEpoch, temp: °C, power: %}]
        _tempLastSample: 0,

        // Chart.js instances (created lazily when canvas is visible)
        // (Chart.js instances live at module scope in _polarisCharts,
        //  see comment at the top of this file for why they can't
        //  live on the reactive component.)

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
        // PA-4: TPPA polar alignment state. Mirrors the WS payload's
        // polarAlignment sub-object. completedOk + isActive are
        // derived from phase + presence of CurrentJob server-side.
        // Form fields (slewDeg/exposureSec/settleSec/gain) hydrate
        // from the active rig's PolarAlign* settings on load.
        polar: {
            phase: 'Idle',
            isActive: false,
            completedOk: false,
            points: [],
            azErrorArcsec: 0,
            altErrorArcsec: 0,
            totalErrorArcsec: 0,
            lastError: null,
            // Form-bound (per-rig). Initial values overridden by
            // _hydratePolarSettingsFromRig() after rigs load.
            slewDeg: 30,
            exposureSec: 3.0,
            settleSec: 2,
            gain: 100
        },
        // PA-6: "Best targets for TPPA now" chip list. Fetched on tab
        // enter + on demand via Refresh. items[] comes from
        // /api/polar/best-targets and is sorted server-side by score.
        polarTargets: { items: [], loading: false, lastFetchUtc: null },
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

        // PA-7: build-time auto-incrementing version surfaced by
        // /api/system/status. Fetched once on init + displayed by the
        // brand badge in the status bar.
        appVersion: '',

        init() {
            this.updateClock();
            setInterval(() => this.updateClock(), 1000);
            this.updateFov();
            // DBGLOG-6: window error + unhandledrejection capture.
            // Installed once per app boot. apiFetch + toast wrappers
            // are inlined into those methods themselves (cleaner than
            // monkey-patching at runtime + survives Alpine re-init).
            this._installLogHooks();

            // XFER: expose transfer helpers to plain scripts that can't
            // reach the Alpine component directly (e.g. onnx-pipelines.js
            // running outside Alpine scope). They register a transfer
            // chip + update its progress as their internal stream
            // reader pulls bytes, so the activity bar shows the same
            // download the AI-modal progress indicator does.
            window.__polarisRegisterTransfer = (opts) => this._transferStart(opts);
            window.__polarisTransferProgress = (id, loaded) => this._transferProgress(id, loaded);
            window.__polarisTransferEnd = (id, ok) => this._transferEnd(id, ok);

            // SKY engine refresh on tab-visibility return. Browsers
            // pause requestAnimationFrame while a tab is hidden, so
            // the stellarium-web-engine's internal observer.utc
            // freezes too. The periodic re-sync inside
            // _updateSkyClock only fires every 30 s; without this
            // listener someone coming back from a 5-minute background
            // sees the sky 5 minutes in the past until the next tick
            // happens to land on a % 30 == 0. Pushing observer + time
            // on visibilitychange (visible) closes that window so the
            // moon / planets snap to correct position immediately.
            if (typeof document !== 'undefined') {
                document.addEventListener('visibilitychange', () => {
                    if (document.visibilityState === 'visible'
                        && this._skyBridgeReady) {
                        this._skyPushObserverAndTime();
                    }
                });
            }

            // AUTH-3: gate the rest of init on auth. _authBoot is
            // async; it restores the saved token, queries /status,
            // and either sets needLogin/needSetup (deferring the
            // heavy init until the user authenticates) or proceeds
            // straight to _initAfterAuth. The clock + FOV math above
            // are pre-auth so the login overlay isn't a blank page.
            this._authBoot();
        },

        // AUTH-3: deferred init that runs once auth clears (either
        // because auth is disabled, or after the user logs in / sets
        // up). Pulls everything the original init used to call into a
        // single function so login can replay it once.
        _initAfterAuth() {
            if (this._authInited) return;
            this._authInited = true;
            this._initCore();
        },

        _initCore() {

            // PA-7: pull the running version once. Cheap, fire-and-forget.
            // 'cache: no-store' so a long-lived browser tab against an
            // updated server picks up the new version after a Polaris
            // restart without forcing the user to ctrl+F5.
            fetch('/api/system/status', { cache: 'no-store' })
                .then(r => r.ok ? r.json() : null)
                .then(s => { if (s && s.version) this.appVersion = s.version; })
                .catch(() => { /* badge stays as '…', non-fatal */ });

            // NET-1: kick the throughput meter immediately. WS opens
            // moments later, by the time the first frames flow the
            // tick loop is running and the rolling window absorbs them.
            this._netStartMeter();

            // Restore client-side WB settings (display tint applied
            // by the WebGL shader). Defaults preserved on missing /
            // malformed localStorage.
            this.loadWb();

            // ALPACA: fire-and-forget LAN discovery so a user who
            // already has ASCOM Remote Server / Alpaca Omni
            // Simulator running gets devices auto-connected without
            // needing to open RIGS → ASCOM/Alpaca → Discover. Silent
            // on empty results so the toast pump stays quiet for
            // users who don't have any Alpaca server.
            this.autoDiscoverAlpaca();

            // iOS / ASIAIR vertical drum pickers. Same auto-mount
            // pattern as the range-slider augmentation below: walk
            // the DOM for [data-wheel-picker], turn each one into
            // an interactive wheel, and re-run on every mutation
            // so Alpine-mounted modals / sidebar swaps get picked
            // up too.
            this._mountWheelPickers();
            const wheelObs = new MutationObserver(() => {
                if (this._wheelObsPending) return;
                this._wheelObsPending = true;
                queueMicrotask(() => {
                    this._wheelObsPending = false;
                    this._mountWheelPickers();
                });
            });
            wheelObs.observe(document.body, { childList: true, subtree: true });

            // Touch-friendly range slider augmentation: walks the DOM
            // looking for <input type="range"> and wraps each one in
            // a [-] slider [+] control row. Idempotent; safe to run
            // many times. A MutationObserver also re-runs the pass
            // when Alpine mounts modals / panels late, so editor
            // sliders, GraXpert dialogs, etc. all get the treatment.
            this._augmentRangeInputs();
            const rangeObs = new MutationObserver(() => {
                // Microtask debounce so Alpine's burst-rendering does
                // not trigger one pass per inserted node.
                if (this._rangeObsPending) return;
                this._rangeObsPending = true;
                queueMicrotask(() => {
                    this._rangeObsPending = false;
                    this._augmentRangeInputs();
                });
            });
            rangeObs.observe(document.body, { childList: true, subtree: true });

            // SWE-1: stand up the postMessage bridge to the Sky
            // sub-application iframe (/sky/index.html). The iframe
            // posts back { type: "ready" } once it's loaded; until
            // then any _skySendMessage call queues. The engine itself
            // ships in SWE-2, for now this just confirms the round-trip
            // is alive in DevTools.
            this._initSkyBridge();

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

            // Sky auto-center toggle is its own localStorage key so it
            // doesn't get bundled with server-side profile settings.
            const autoCenterSaved = localStorage.getItem('nina-sky-autocenter');
            if (autoCenterSaved !== null) {
                this.skyAutoCenterOnSelect = autoCenterSaved !== '0';
            }
            this.$watch('skyAutoCenterOnSelect', (v) => {
                localStorage.setItem('nina-sky-autocenter', v ? '1' : '0');
            });

            // Camera preview window show/hide preference.
            try {
                const v = localStorage.getItem('slewPreviewVisible');
                if (v !== null) this.slewPreviewVisible = v !== '0';
            } catch { /* ignore */ }

            // DSS background toggle, same pattern.
            const dssSaved = localStorage.getItem('nina-sky-dss');
            if (dssSaved !== null) {
                this.skyDssVisible = dssSaved !== '0';
            }
            this.$watch('skyDssVisible', (v) => {
                localStorage.setItem('nina-sky-dss', v ? '1' : '0');
            });

            // ZWO gain presets, static lookup table. Tiny file (~1 KB)
            // so fire-and-forget. If the fetch fails the L/M/H buttons
            // simply never appear (zwoPresetsForActiveCamera returns null).
            fetch('/data/zwo-gain-presets.json')
                .then(r => r.ok ? r.json() : null)
                .then(j => { if (j && j.presets) this._zwoPresets = j.presets; })
                .catch(() => { /* silently degrade */ });

            // UI zoom: load explicit user value if previously set,
            // else derive from current viewport so phones / tablets
            // start at the same scale as the CSS @media rules.
            const zoomSaved = localStorage.getItem('nina-ui-zoom');
            this.uiZoom = zoomSaved != null
                ? Math.max(0.5, Math.min(1.5, parseFloat(zoomSaved) || 1.0))
                : this._defaultUiZoom();
            this.uiZoomDraft = this.uiZoom;
            this.applyUiZoom();

            // FONT-1: restore font choice. Use the inline boot
            // attribute that index.html stamps before Alpine
            // initialises (avoids FOUT). Otherwise default to
            // 'atkinson' (best readability for the target operator).
            const fontSaved = localStorage.getItem('nina-ui-font');
            this.uiFont = fontSaved && ['inter','atkinson','plex','system'].includes(fontSaved)
                ? fontSaved
                : 'atkinson';
            this.applyUiFont();

            // Re-render the cached frame whenever the user switches
            // tabs. Fixes the classic "last snap painted on PREVIEW,
            // user switches to VIDEO, sees black canvas", the
            // previously-hidden videoCaptureCanvas was reported as
            // hidden(0x0) during the original fan-out, so it never
            // received the bitmap. $nextTick waits for x-show to
            // flip display:block + the layout to settle so the
            // target container actually has dimensions when we
            // re-fanout.
            this.$watch('tab', () => {
                this.$nextTick(() => {
                    if (this._lastRawFrame) {
                        this.applyManualStretch();
                    }
                });
            });
            // Same trick for sub-tabs that host their own preview
            // canvas (VIDEO has Capture/Process; Capture owns
            // videoCaptureCanvas). Without this, the bitmap follows
            // only outer tab switches.
            this.$watch('videoTab', () => {
                this.$nextTick(() => {
                    if (this._lastRawFrame) {
                        this.applyManualStretch();
                    }
                });
            });

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
            this.loadFocuserDrivers();
            this.loadFilterWheelDrivers();
            this.restoreMountPanel();
            this.restoreCameraPanel();
            window.addEventListener('resize', () => {
                this._clampMountPanel();
                this._clampCameraPanel();
            });
            // Universal ESC-key escape hatch for the floating Mount +
            // Camera panels, since the X buttons have a history of
            // getting stuck behind their headers' drag handlers in
            // specific tab / state combinations. Single Esc keypress
            // closes whichever is open.
            window.addEventListener('keydown', (ev) => this._floatPanelEscapeClose(ev));
            window.addEventListener('keydown', (ev) => this._confirmModalKeydown(ev));
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

            // NET-1: account for upload bytes (Performance API only
            // surfaces transferSize for the response). For JSON bodies
            // that's the stringified length; for FormData we sum file
            // sizes; for ArrayBuffer / TypedArray it's the byteLength.
            const body = options.body;
            if (body) {
                let txBytes = 0;
                if (typeof body === 'string') {
                    txBytes = body.length;
                } else if (body instanceof ArrayBuffer) {
                    txBytes = body.byteLength;
                } else if (body && body.byteLength != null) {
                    txBytes = body.byteLength;
                } else if (typeof FormData !== 'undefined' && body instanceof FormData) {
                    // FormData iterator gives [name, valueOrFile] pairs.
                    for (const [, v] of body) {
                        if (v && v.size != null) txBytes += v.size;
                        else if (typeof v === 'string') txBytes += v.length;
                    }
                }
                if (txBytes > 0) this._netTx(txBytes);
            }

            const timeout = options.timeout || 15000;
            const controller = new AbortController();
            const timer = setTimeout(() => controller.abort(), timeout);

            // AUTH-3: inject the bearer token on every request when we
            // have one. The server's AuthMiddleware accepts the header
            // OR the polaris_session cookie (auto-attached same-origin),
            // but we send the header anyway as the primary path so it
            // survives even when cookies are blocked (incognito, third-
            // party blockers, etc.). Existing Authorization headers in
            // opts.headers win, so callers can override per-request if
            // ever needed.
            const headers = options.headers ? { ...options.headers } : {};
            if (this.auth && this.auth.token &&
                    !headers.Authorization && !headers.authorization) {
                headers.Authorization = 'Bearer ' + this.auth.token;
            }

            // DBGLOG-6: measure round-trip for the log entry. performance.now()
            // gives sub-ms precision, which surfaces slow endpoints (>100ms)
            // visibly in the panel.
            const t0 = (typeof performance !== 'undefined' && performance.now) ? performance.now() : Date.now();

            const promise = fetch(url, {
                ...options,
                headers,
                signal: controller.signal
            }).then(resp => {
                clearTimeout(timer);
                delete this._pending[key];

                if (!this.serverReachable) {
                    this.serverReachable = true;
                    this.toast('Server reconnected', 'ok');
                }

                // DBGLOG-6: log the response. error level for 5xx, warn for
                // 4xx (including 401 — useful for diagnosing auth churn),
                // info otherwise. Skip /api/logs/client to break the feedback
                // loop (POSTing a log entry would log the POST, recursion).
                if (!url.startsWith('/api/logs')) {
                    const dur = ((typeof performance !== 'undefined' && performance.now) ? performance.now() : Date.now()) - t0;
                    const lvl = resp.status >= 500 ? 'error'
                              : resp.status >= 400 ? 'warn'
                              : 'info';
                    this._logFromClient(lvl, `${method} ${url} ${resp.status} ${dur.toFixed(0)}ms`, {
                        source: 'apiFetch', method, path: url, status: resp.status, durationMs: dur
                    });
                }

                if (resp.status === 401) {
                    // AUTH-3: token expired / invalidated by a remote
                    // password change / server restart. Trigger the
                    // login overlay so the user can re-authenticate
                    // without a full reload. The body MAY contain
                    // { authConfigured: false } which means the
                    // operator wiped the profile, show wizard instead.
                    return resp.text().then(body => {
                        let payload = null;
                        try { payload = JSON.parse(body); } catch {}
                        this._handle401(payload);
                        throw new ApiError(401, body);
                    });
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

                // DBGLOG-6: log the network failure (timeout, DNS, refused).
                // Skip /api/logs/client to prevent the same recursion loop
                // as the success branch.
                if (!url.startsWith('/api/logs')) {
                    const dur = ((typeof performance !== 'undefined' && performance.now) ? performance.now() : Date.now()) - t0;
                    const msg = err && err.message ? err.message : String(err);
                    this._logFromClient('error', `${method} ${url} failed: ${msg}`, {
                        source: 'apiFetch', method, path: url, durationMs: dur,
                        exceptionType: err && err.name, exceptionMsg: msg
                    });
                }

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

        // ─── AUTH helper for <img>, <iframe>, <a> URLs ─────────────────
        //
        // Browser <img src=...> and <iframe src=...> requests don't
        // carry the Authorization header the way fetch does, they fall
        // through to cookie auth instead. The polaris_session cookie
        // covers most cases, but it dies on browser close and isn't
        // sent over plain HTTP when set Secure. The AuthMiddleware also
        // accepts ?token=... as a query-string fallback (used by
        // /api/files/download already), so we just append the bearer
        // token here for any URL that needs to render through a tag
        // instead of fetch.
        //
        // Pass the URL through and either:
        //   /api/foo                  → /api/foo?token=xxx
        //   /api/foo?bar=baz          → /api/foo?bar=baz&token=xxx
        // Skipped when auth.token is empty (e.g. auth disabled OR not
        // yet logged in — the request will 401 either way, and the
        // ?token= would just be empty).
        authUrl(url) {
            if (!url || !this.auth?.token) return url;
            const sep = url.includes('?') ? '&' : '?';
            return url + sep + 'token=' + encodeURIComponent(this.auth.token);
        },

        // ─── XFER-1: per-transfer progress helpers ─────────────────────

        _transferStart({ label, direction, total }) {
            const id = this._nextTransferId++;
            this.transfers.push({
                id,
                label: label || (direction === 'up' ? 'Uploading' : 'Downloading'),
                direction,
                loaded: 0,
                total: total || 0,
                done: false,
                startedAt: Date.now()
            });
            return id;
        },
        _transferProgress(id, loaded) {
            const t = this.transfers.find(x => x.id === id);
            if (t) t.loaded = loaded;
        },
        _transferEnd(id, ok = true) {
            const t = this.transfers.find(x => x.id === id);
            if (!t) return;
            t.done = true;
            t.ok = ok;
            // Hold the completed chip for ~800ms so the user gets to
            // see the 100% / Done state, then drop it. Failed transfers
            // hang around a bit longer so the error stays readable.
            const holdMs = ok ? 800 : 3000;
            setTimeout(() => {
                const i = this.transfers.findIndex(x => x.id === id);
                if (i >= 0) this.transfers.splice(i, 1);
            }, holdMs);
        },

        // Format helper used by the activity-bar template.
        formatTransferLine(t) {
            const loaded = this.formatBytes(t.loaded);
            if (t.total > 0) {
                return `${loaded} / ${this.formatBytes(t.total)}`;
            }
            return loaded;
        },
        transferPercent(t) {
            if (!t.total || t.total <= 0) return null;
            return Math.max(0, Math.min(100, Math.round(100 * t.loaded / t.total)));
        },

        // XHR-based upload that exposes real per-chunk progress.
        // fetch() doesn't surface upload progress events (only download
        // via response.body reader), so for any POST/PUT that ships a
        // meaningful payload size we go through XHR. Drop-in replacement
        // for apiFetch: returns a Response so callers can .json() etc.
        //
        // opts: { method?, headers?, label?, signal? }
        apiUpload(url, body, opts = {}) {
            return new Promise((resolve, reject) => {
                const xhr = new XMLHttpRequest();
                const method = opts.method || 'POST';
                xhr.open(method, url);

                // AUTH: same bearer token apiFetch injects.
                if (this.auth?.token) {
                    xhr.setRequestHeader('Authorization', 'Bearer ' + this.auth.token);
                }
                // Caller-provided headers (Content-Type for JSON etc).
                // FormData: don't set Content-Type, the browser writes
                // the multipart boundary header for us.
                const headers = opts.headers || {};
                for (const [k, v] of Object.entries(headers)) {
                    if (!(body instanceof FormData) ||
                        k.toLowerCase() !== 'content-type') {
                        xhr.setRequestHeader(k, v);
                    }
                }

                // Estimate the upload payload size up front so the
                // progress chip can render a determinate bar.
                let total = 0;
                if (body instanceof FormData) {
                    for (const [, v] of body) {
                        if (v?.size != null) total += v.size;
                        else if (typeof v === 'string') total += v.length;
                    }
                } else if (body instanceof ArrayBuffer) {
                    total = body.byteLength;
                } else if (body?.byteLength != null) {
                    total = body.byteLength;
                } else if (typeof body === 'string') {
                    total = body.length;
                }

                const tid = this._transferStart({
                    label: opts.label, direction: 'up', total
                });

                let lastLoaded = 0;
                xhr.upload.onprogress = (e) => {
                    if (!e.lengthComputable && total <= 0) {
                        // Indeterminate — best we can do is advance the
                        // "loaded" counter so the chip shows activity.
                        this._transferProgress(tid, e.loaded);
                    } else {
                        this._transferProgress(tid, e.loaded);
                    }
                    const delta = e.loaded - lastLoaded;
                    if (delta > 0) this._netTx(delta);
                    lastLoaded = e.loaded;
                };

                xhr.onload = () => {
                    this._transferEnd(tid, xhr.status >= 200 && xhr.status < 400);
                    if (xhr.status === 401) {
                        let payload = null;
                        try { payload = JSON.parse(xhr.responseText); } catch {}
                        this._handle401(payload);
                        reject(new ApiError(401, xhr.responseText));
                        return;
                    }
                    if (xhr.status < 200 || xhr.status >= 300) {
                        reject(new ApiError(xhr.status, xhr.responseText));
                        return;
                    }
                    // Wrap as a Response so callers can .json()/.blob()/
                    // .text() like with apiFetch.
                    const blob = xhr.response instanceof Blob
                        ? xhr.response
                        : new Blob([xhr.responseText || '']);
                    const responseHeaders = new Headers();
                    const raw = xhr.getAllResponseHeaders().trim().split(/[\r\n]+/);
                    for (const line of raw) {
                        const idx = line.indexOf(':');
                        if (idx > 0) {
                            responseHeaders.set(
                                line.slice(0, idx).trim(),
                                line.slice(idx + 1).trim());
                        }
                    }
                    resolve(new Response(blob, {
                        status: xhr.status,
                        statusText: xhr.statusText,
                        headers: responseHeaders
                    }));
                };
                xhr.onerror = () => {
                    this._transferEnd(tid, false);
                    reject(new Error('Upload network error'));
                };
                xhr.onabort = () => {
                    this._transferEnd(tid, false);
                    reject(new Error('Upload aborted'));
                };
                if (opts.signal) {
                    opts.signal.addEventListener('abort', () => xhr.abort());
                }
                // Leave responseType at the default ('') so the onload
                // handler can read xhr.responseText on both success and
                // error paths. The previous responseType='blob' tripped
                // an InvalidStateError on every non-2xx response —
                // reject(new ApiError(xhr.status, xhr.responseText))
                // throws because responseText is forbidden in blob
                // mode, masking the real server error. The success
                // path wraps xhr.responseText in a fresh Blob anyway,
                // so dropping the blob responseType costs nothing.
                xhr.send(body);
            });
        },

        // fetch + ReadableStream reader so we can show real download
        // progress without losing the response object. The body is
        // buffered as it streams so callers can still call .arrayBuffer()
        // / .json() / .blob() on the returned Response. apiFetch handles
        // auth + 401 + abort + deduplication; we just wrap the body read.
        async apiDownload(url, opts = {}) {
            const resp = await this.apiFetch(url, opts);
            if (!resp || !resp.ok) return resp;

            const totalHeader = resp.headers.get('Content-Length');
            const total = totalHeader ? parseInt(totalHeader, 10) : 0;
            const tid = this._transferStart({
                label: opts.label, direction: 'down', total
            });

            // Older browsers / opaque responses skip the stream and just
            // fall back to .blob(). Still gives the user a one-shot
            // completion mark even without mid-transfer progress.
            if (!resp.body || !resp.body.getReader) {
                try {
                    const blob = await resp.blob();
                    this._transferProgress(tid, blob.size);
                    this._transferEnd(tid, true);
                    return new Response(blob, {
                        status: resp.status,
                        statusText: resp.statusText,
                        headers: resp.headers
                    });
                } catch (e) {
                    this._transferEnd(tid, false);
                    throw e;
                }
            }

            try {
                const reader = resp.body.getReader();
                const chunks = [];
                let loaded = 0;
                while (true) {
                    const { done, value } = await reader.read();
                    if (done) break;
                    chunks.push(value);
                    loaded += value.byteLength;
                    this._transferProgress(tid, loaded);
                }
                this._transferEnd(tid, true);
                const blob = new Blob(chunks);
                return new Response(blob, {
                    status: resp.status,
                    statusText: resp.statusText,
                    headers: resp.headers
                });
            } catch (e) {
                this._transferEnd(tid, false);
                throw e;
            }
        },

        // ---- AUTH-3: client-side auth boot + login + wizard ----------

        // Restore saved token (sessionStorage by default, localStorage
        // when the user ticked "remember me"), then ask the server
        // what state we're in. Branches into wizard / login / app.
        // Called once at init() from outside the gated rest-of-init.
        async _authBoot() {
            try {
                const saved = sessionStorage.getItem('polaris_token')
                    || localStorage.getItem('polaris_token');
                if (saved) {
                    this.auth.token = saved;
                    this.auth.rememberMe = !!localStorage.getItem('polaris_token');
                }
                // Use bare fetch (not apiFetch) so a 401 here doesn't
                // recurse through _handle401 before we've decided what
                // to do with it.
                const headers = this.auth.token
                    ? { Authorization: 'Bearer ' + this.auth.token } : {};
                const r = await fetch('/api/auth/status',
                    { cache: 'no-store', headers });
                const s = await r.json();
                this.auth.configured = !!s.configured;
                this.auth.enabled = !!s.enabled;
                this.auth.authenticated = !!s.authenticated;
                this.auth.sessionTimeoutHours = s.sessionTimeoutHours || 24;
                if (!this.auth.enabled) {
                    // Opt-out toggle ON: skip the gate entirely.
                    this.auth.booting = false;
                    this._initAfterAuth();
                    return;
                }
                if (!this.auth.configured) {
                    this.auth.needSetup = true;
                    this.auth.booting = false;
                    return;
                }
                if (!this.auth.authenticated) {
                    // Saved token was invalid or expired. Drop it.
                    this.auth.token = '';
                    sessionStorage.removeItem('polaris_token');
                    localStorage.removeItem('polaris_token');
                    this.auth.needLogin = true;
                    this.auth.booting = false;
                    return;
                }
                this.auth.booting = false;
                this._initAfterAuth();
            } catch (e) {
                // Backend unreachable: surface login screen with a
                // generic error so the user knows the server is down
                // rather than seeing a blank page.
                this.auth.loginError = 'Cannot reach Polaris server';
                this.auth.needLogin = true;
                this.auth.booting = false;
            }
        },

        async authLogin() {
            this.auth.loginError = '';
            try {
                const r = await fetch('/api/auth/login', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ password: this.auth.loginPassword })
                });
                if (r.status === 401) {
                    this.auth.loginError = 'Invalid password';
                    return;
                }
                if (!r.ok) {
                    this.auth.loginError = 'Login failed (HTTP ' + r.status + ')';
                    return;
                }
                const j = await r.json();
                this._authStoreToken(j.token);
                this.auth.loginPassword = '';
                this.auth.needLogin = false;
                this.auth.authenticated = true;
                this._initAfterAuth();
            } catch (e) {
                this.auth.loginError = 'Network error';
            }
        },

        async authSetup() {
            this.auth.setupError = '';
            if (!this.auth.setupPassword || this.auth.setupPassword.length < 8) {
                this.auth.setupError = 'Password must be at least 8 characters';
                return;
            }
            if (this.auth.setupPassword !== this.auth.setupConfirm) {
                this.auth.setupError = 'Passwords do not match';
                return;
            }
            try {
                const r = await fetch('/api/auth/setup', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ password: this.auth.setupPassword })
                });
                if (!r.ok) {
                    const t = await r.text();
                    this.auth.setupError = 'Setup failed: ' + t;
                    return;
                }
                const j = await r.json();
                this._authStoreToken(j.token);
                this.auth.setupPassword = '';
                this.auth.setupConfirm = '';
                this.auth.needSetup = false;
                this.auth.configured = true;
                this.auth.authenticated = true;
                this._initAfterAuth();
            } catch (e) {
                this.auth.setupError = 'Network error';
            }
        },

        async authLogout() {
            try {
                await this.apiPost('/api/auth/logout');
            } catch {}
            this._authClearToken();
            this.auth.needLogin = true;
            // Stop active WS connections so they don't reconnect with
            // a stale token. The login flow re-runs _initAfterAuth
            // which will re-open them.
            try { this._statusWs && this._statusWs.close(); } catch {}
            try { this._imageWs && this._imageWs.close(); } catch {}
        },

        // Settings -> Change password
        async authChangePassword() {
            this.auth.cpError = '';
            if (!this.auth.cpNew || this.auth.cpNew.length < 8) {
                this.auth.cpError = 'New password must be at least 8 characters';
                return;
            }
            if (this.auth.cpNew !== this.auth.cpConfirm) {
                this.auth.cpError = 'New passwords do not match';
                return;
            }
            this.auth.cpBusy = true;
            try {
                const r = await this.apiPost('/api/auth/change-password', {
                    current: this.auth.cpCurrent,
                    new: this.auth.cpNew
                });
                if (!r.ok) {
                    const t = await r.text();
                    this.auth.cpError = 'Failed: ' + t;
                    return;
                }
                this.auth.cpOpen = false;
                this.auth.cpCurrent = '';
                this.auth.cpNew = '';
                this.auth.cpConfirm = '';
                this.toast('Password changed', 'ok');
            } catch (e) {
                if (e instanceof ApiError && e.status === 401) {
                    this.auth.cpError = 'Current password incorrect';
                } else {
                    this.auth.cpError = 'Network error';
                }
            } finally {
                this.auth.cpBusy = false;
            }
        },

        // Settings -> Disable auth (opt-out)
        async authDisable() {
            this.auth.disableError = '';
            this.auth.disableBusy = true;
            try {
                const r = await this.apiPost('/api/auth/disable',
                    { password: this.auth.disablePassword });
                if (!r.ok) {
                    const t = await r.text();
                    this.auth.disableError = 'Failed: ' + t;
                    return;
                }
                this.auth.enabled = false;
                this.auth.disableOpen = false;
                this.auth.disablePassword = '';
                this.toast('Authentication disabled', 'warn');
            } catch (e) {
                this.auth.disableError = e instanceof ApiError
                        && e.status === 401
                    ? 'Invalid password'
                    : 'Network error';
            } finally {
                this.auth.disableBusy = false;
            }
        },

        async authEnable() {
            // Re-enable doesn't need a confirm modal: prompt() inline
            // so the user types the password once, we POST, done.
            const pwd = prompt('Re-enable authentication, enter the password:');
            if (!pwd) return;
            try {
                const r = await this.apiPost('/api/auth/enable',
                    { password: pwd });
                if (!r.ok) {
                    this.toast('Failed to enable auth', 'error');
                    return;
                }
                this.auth.enabled = true;
                this.toast('Authentication enabled', 'ok');
            } catch (e) {
                this.toast('Network error', 'error');
            }
        },

        _authStoreToken(token) {
            this.auth.token = token;
            if (this.auth.rememberMe) {
                localStorage.setItem('polaris_token', token);
                sessionStorage.removeItem('polaris_token');
            } else {
                sessionStorage.setItem('polaris_token', token);
                localStorage.removeItem('polaris_token');
            }
        },

        _authClearToken() {
            this.auth.token = '';
            this.auth.authenticated = false;
            sessionStorage.removeItem('polaris_token');
            localStorage.removeItem('polaris_token');
        },

        // Called by apiFetch when a request returns 401. Drops the
        // token + shows the right overlay so the user can recover
        // without a full reload.
        _handle401(payload) {
            this._authClearToken();
            if (payload && payload.authConfigured === false) {
                this.auth.needSetup = true;
                this.auth.needLogin = false;
            } else {
                this.auth.needLogin = true;
                this.auth.needSetup = false;
            }
        },

        // ---- HELP-1: tutorial stepper + landing helpers ----------------

        // Restore last position on tab entry. Skips the restore when
        // the user is already mid-tutorial (e.g. clicked Help from
        // the sidebar while inside the stepper, that's a no-op).
        helpOnTabEnter() {
            if (this.help.tutorial) return;
            try {
                const raw = localStorage.getItem('polaris-help-pos');
                if (!raw) return;
                const pos = JSON.parse(raw);
                if (pos && pos.tutorial && this._helpTutorials()[pos.tutorial]) {
                    this.help.tutorial = pos.tutorial;
                    this.help.step = Math.min(
                        Math.max(0, pos.step | 0),
                        this._helpTutorials()[pos.tutorial].length - 1);
                }
            } catch { /* no-op */ }
        },

        helpStart(tutorialKey) {
            this.help.tutorial = tutorialKey;
            this.help.step = 0;
            this._helpPersist();
        },

        helpExit() {
            this.help.tutorial = null;
            this.help.step = 0;
            try { localStorage.removeItem('polaris-help-pos'); } catch {}
        },

        helpNext() {
            const max = this._helpSteps().length - 1;
            if (this.help.step < max) {
                this.help.step++;
                this._helpPersist();
            }
        },

        helpPrev() {
            if (this.help.step > 0) {
                this.help.step--;
                this._helpPersist();
            }
        },

        helpJumpTo(idx) {
            const steps = this._helpSteps();
            this.help.step = Math.min(Math.max(0, idx | 0), steps.length - 1);
            this._helpPersist();
        },

        // Switches the app to the named tab but PRESERVES the help
        // position so coming back via the sidebar lands the user
        // exactly where they left off.
        helpOpenTab(tabId) {
            this._helpPersist();
            this.tab = tabId;
            // Mirror the side effects the sidebar buttons normally
            // run on entering a tab. Without these the Sky map
            // doesn't initialise its WebGL viewer and the Studio
            // browser shows stale paths until the user manually
            // navigates.
            try {
                if (tabId === 'sky') {
                    this.$nextTick(() => this.initSkyViewer && this.initSkyViewer());
                } else if (tabId === 'files') {
                    this.filesInit && this.filesInit();
                } else if (tabId === 'tonight') {
                    this.loadTonightsBest && this.loadTonightsBest();
                } else if (tabId === 'weather') {
                    this.loadWeatherForecast && this.loadWeatherForecast();
                }
            } catch (e) { /* best effort -- tab still switches */ }
        },

        _helpPersist() {
            if (!this.help.tutorial) return;
            try {
                localStorage.setItem('polaris-help-pos',
                    JSON.stringify({
                        tutorial: this.help.tutorial,
                        step: this.help.step
                    }));
            } catch {}
        },

        // Convenience wrappers for the template.
        _helpSteps() {
            const t = this._helpTutorials()[this.help.tutorial];
            return Array.isArray(t) ? t : [];
        },

        helpCurrentStep() {
            const steps = this._helpSteps();
            if (!steps.length) return { title: '', body: [] };
            return steps[Math.min(this.help.step, steps.length - 1)];
        },

        _helpTutorialMeta(key) {
            return ({
                firstNight:   { title: 'First night' },
                capture:      { title: 'Capture to export' },
                lrgb:         { title: 'LRGB / mono pipeline' },
                planetary:    { title: 'Planetary / lucky imaging' },
                pcc:          { title: 'Photometric color calibration' },
                troubleshoot: { title: 'Troubleshooting & FAQ' }
            })[key] || { title: '' };
        },

        // ---- HELP catalogue --------------------------------------------
        // One method returning the full set keeps everything in one
        // place; the steppers and the landing card counts both pull
        // from here. Each tutorial is an array of step objects with:
        //   title       string, required
        //   body        string[] of paragraphs, required
        //   screenshot  string, optional; resolves to /screenshots/<path>
        //   tab         string, optional; tab id to jump to
        //   tabLabel    string, optional; display label for the button
        //   docLink     string, optional; filename under docs/user-guide/
        //   tip         string, optional; renders a 💡 callout
        //   warn        string, optional; renders a ⚠ callout
        // HELP-2..5 fill these arrays with real content.
        _helpTutorials() {
            return {

                // HELP-3: First-night checklist (~5 steps). Targeted
                // at someone who just installed Polaris and hasn't
                // connected anything yet. Walks them from "URL works
                // in the browser" through "first device responds".
                firstNight: [
                    {
                        title: 'Open Polaris in your browser',
                        screenshot: 'first-night/01-browser-cert.png',
                        docLink: 'installation.md',
                        body: [
                            'Open https://polaris-pi.local:5000 from any device on the same WiFi. On the Pi the hostname is whatever you set; on a fresh .deb install it defaults to polaris-pi.',
                            'Your browser will warn about a self-signed certificate. That is expected, Polaris generates one on first boot so HTTPS works on the LAN without a CA. Click Advanced / Proceed; the browser remembers the exception per device.',
                            'If polaris-pi.local does not resolve, fall back to the IP printed in the install summary or hostname -I on the Pi.'
                        ],
                        tip: 'HTTPS on port 5000 is required for in-browser GraXpert (WebGPU). HTTP on port 5080 is loopback-only for SSH tunnels.'
                    },
                    {
                        title: 'Set a password',
                        screenshot: 'first-night/02-password.png',
                        docLink: 'authentication.md',
                        body: [
                            'The first visit shows a full-screen wizard asking you to set a password. There is no default and no skip, this protects the rig from anyone else on the same WiFi.',
                            'Pick something at least 8 characters. You will use this on every other device that opens Polaris (laptop, phone, second tablet). The "Remember on this device" checkbox at the login screen later persists across browser restarts.'
                        ],
                        warn: 'Forgot it later? SSH to the Pi and clear AuthPasswordHash + AuthPasswordSalt in ~/.config/NINA.Polaris/profiles/active.json, restart the service, set a new one in the wizard.'
                    },
                    {
                        title: 'Set the observatory location',
                        tab: 'settings',
                        tabLabel: 'SETTINGS',
                        screenshot: 'first-night/03-location.png',
                        docLink: 'first-night.md',
                        body: [
                            'Settings → Location. Type your latitude / longitude / altitude or click the "Use my location" button (browser geolocation; needs HTTPS, which Polaris already serves).',
                            'This drives every astronomy calculation in the app: altitude charts, twilight times on the Sky map, sun/moon positions on the Tonight tab, and the polar alignment math. Bad coords = wrong sky.'
                        ]
                    },
                    {
                        title: 'WiFi: Hotspot or join your home network',
                        tab: 'settings',
                        tabLabel: 'SETTINGS',
                        screenshot: 'first-night/04-wifi.png',
                        docLink: 'network-mode.md',
                        body: [
                            'On a fresh .deb install the Pi comes up as a hotspot named "Polaris-Hotspot" (password "polaris1234") so you can reach it without plugging in a screen. The hotspot is great in the field but useless at home, your phone disconnects from the internet whenever you join it.',
                            'Settings → Network has a "Switch to Station" button: pick your home SSID, type the password, click Switch. The Pi joins your home WiFi, mDNS keeps the polaris-pi.local hostname pointing at the new IP. If something goes wrong (wrong password, dead AP), it auto-reverts to hotspot after 30s.'
                        ],
                        tip: 'Linux only (NetworkManager). On Windows mini-PCs the button is hidden, manage WiFi through Windows itself.'
                    },
                    {
                        title: 'Connect your first device',
                        tab: 'equip',
                        tabLabel: 'RIGS',
                        screenshot: 'first-night/05-first-device.png',
                        docLink: 'rigs.md',
                        body: [
                            'RIGS tab is where every camera / mount / focuser / filter wheel lives. Pick a driver (INDI is the default on Linux + cross-platform, Alpaca / native vendor drivers also work), connect, and Polaris remembers it as part of the active rig profile.',
                            'For Linux + INDI: open the "INDI Drivers" sub-tab, enable indi-web, pick your hardware from the Web Manager, then come back to "Connect" sub-tab and hit Connect All.',
                            'When the badges turn green you are ready to start capturing. The full Capture-to-export tutorial picks up from here.'
                        ],
                        tip: 'No hardware on hand? Enable Simulator on Settings → Equipment simulator and you get a fake CCD + Telescope + Focuser + FilterWheel to drive the whole pipeline end-to-end.'
                    }
                ],

                // HELP-2: Capture-to-export end-to-end (~12 steps).
                // The main tutorial. Each step is short on purpose,
                // 2-4 sentence summary + link to the deep doc.
                capture: [
                    {
                        title: 'Welcome to Polaris',
                        screenshot: 'capture/01-welcome.png',
                        body: [
                            'This tutorial walks you from cold equipment all the way to a finished image you can post or print. About 12 steps, mostly waiting on the night sky.',
                            'You will spend most of the time inside the AUTORUN tab once the sequence is running; the steps before it are setup, the steps after it are post-processing. Each step has a "Read more" link into the deeper docs for when you want detail.'
                        ],
                        tip: 'Skim every step first, then come back and execute. The order matters: focus before sequence, sequence before stack.'
                    },
                    {
                        title: 'Connect equipment in RIGS',
                        tab: 'equip',
                        tabLabel: 'RIGS',
                        screenshot: 'capture/02-rigs.png',
                        docLink: 'rigs.md',
                        body: [
                            'Open the RIGS tab. Each device (Main Telescope, Main Camera, Mount, Focuser, Filter Wheel, Guidescope, Guide Camera) is a card. Pick the driver, pick the specific device the driver reports, hit Connect.',
                            'Save the result as a named rig profile ("OnStep + ASI2600MC + EAF"). Polaris remembers it and lets you switch rigs without re-typing focal lengths, pixel sizes, etc.',
                            'Device-level tweaks (slew rate, tracking mode, EAF backlash, EFW filter names, etc) persist automatically via INDI CONFIG_PROCESS: 3 seconds after any change in the INDI properties panel, Polaris saves to ~/.indi/{driver}_config.xml; on the next connect the values reload automatically. Set things once, they survive reboots and reconnects.'
                        ]
                    },
                    {
                        title: 'Polar alignment',
                        tab: 'polar',
                        tabLabel: 'POLAR',
                        screenshot: 'capture/03-polar.png',
                        docLink: 'polar-alignment.md',
                        body: [
                            'POLAR runs Three-Point Polar Alignment (TPPA): it slews the mount to three points, plate-solves each, and computes how far off your physical mount axis is from the true pole.',
                            'Adjust the alt/az bolts on the wedge while watching the error vector overlay shrink. Goal: error under 1 arcmin for unguided + nominal exposures, under 10 arcsec for guided + long subs.'
                        ],
                        warn: 'Needs a connected camera + mount + working plate solver (ASTAP). Solver path is configured in Settings if auto-detect misses it.'
                    },
                    {
                        title: 'Focus',
                        tab: 'focus',
                        tabLabel: 'FOCUS',
                        screenshot: 'capture/04-focus.png',
                        docLink: 'focus.md',
                        body: [
                            'Two flavors: Manual Assist (HFR trend loop + optional Bahtinov overlay) for setups without an electronic focuser, and Auto V-curve for motorised focusers.',
                            'Manual: turn the knob, watch HFR drop, stop when it bottoms out. Auto: pick a bright star, hit Start AF, the focuser sweeps and lands on the minimum of the parabola fit. Either way: HFR (half-flux radius) is the metric, lower = sharper.'
                        ]
                    },
                    {
                        title: 'Pick a target on SKY',
                        tab: 'sky',
                        tabLabel: 'SKY',
                        screenshot: 'capture/05-sky.png',
                        docLink: 'sky-explorer.md',
                        body: [
                            'SKY embeds the Stellarium Web engine: pan around, type a name in the search bar (M31, NGC 7000, etc), click the result. The FOV overlay shows what your camera will actually see overlaid on the sky.',
                            'Need help deciding what to shoot? The SKY tab has three sub-tabs at the top: Sky map, Tonight\'s Best (ranked list with altitude / twilight / moon distance / FOV fit), and Weather (3-day cloud / seeing / transparency forecast). All three live under the single SKY entry in the sidebar.'
                        ],
                        tip: 'Drag the FOV rectangle to compose framing before slewing; Polaris remembers the rotation and re-uses it for plate solve.'
                    },
                    {
                        title: 'Slew & Center on the target',
                        tab: 'sky',
                        tabLabel: 'SKY',
                        screenshot: 'capture/06-slew-center.png',
                        docLink: 'sky-explorer.md',
                        body: [
                            'With the target picked, hit "Slew & Center". The mount slews, Polaris takes a plate-solve frame, computes the offset, nudges the mount, and re-checks. The loop converges until the target is inside the tolerance you configured (default 30 arcsec).',
                            'No more "I hope the alignment was good" trial-and-error. The center is exact.',
                            'During the slew a live camera preview pops up over the SKY map so you can confirm framing. A Mount control popup (jog buttons, Park, tracking) is also available. Both have pills at the bottom-right of SKY that toggle their visibility. Tap anywhere on the camera popup to dismiss it; the Mount popup has an X. ESC closes both. Right-click (or shift+click) on either pill to reset the popup back to its default position if you accidentally drag it off-screen.'
                        ]
                    },
                    {
                        title: 'Start guiding with PHD2',
                        tab: 'guide',
                        tabLabel: 'GUIDE',
                        screenshot: 'capture/07-guide.png',
                        docLink: 'guide-phd2.md',
                        body: [
                            'GUIDE tab embeds the PHD2 protocol client + (on Linux) the actual PHD2 GUI via xpra. Pick a profile (or create one in the wizard), connect equipment, hit Calibrate then Guide.',
                            'Polaris also offers Smart Calibrate: one click computes step size from pixel scale + guide rate, slews to the celestial equator for the cleanest calibration, runs it, validates the result. Saves 5 minutes of manual setup per session.'
                        ],
                        tip: 'No PHD2? It is optional. Short subs (<1 min) work unguided on a well-polar-aligned mount. Long DSO subs need guiding.'
                    },
                    {
                        title: 'Build the sequence in AUTORUN',
                        tab: 'sequence',
                        tabLabel: 'AUTORUN',
                        screenshot: 'capture/08-sequence.png',
                        docLink: 'sequence.md',
                        body: [
                            'AUTORUN runs the simple sequencer: N frames per filter at a given exposure + gain, with optional triggers (auto-refocus on temperature change, dither every K frames, meridian flip, etc).',
                            'For a typical 3-hour OSC session: target name, 60-120 lights of 60-120s each, dither every 3, refocus on +/-3 degC delta, meridian flip enabled. Hit Start.',
                            'The big round shutter button is the universal capture control (same component appears in PREVIEW, FOCUS, VIDEO, and LIVE tabs). Tap = single snap, long-press (~600ms) = loop, tap while a capture is running = abort. The progress ring around it fills as the exposure advances so you always know how far in you are.'
                        ],
                        warn: 'Advanced (ADV) sequencer is the tree-based version with conditional containers and parallel branches. Pick that for multi-target nights with rotator + filter wheel choreography.'
                    },
                    {
                        title: 'Watch live stacking',
                        tab: 'live',
                        tabLabel: 'LIVE',
                        screenshot: 'capture/09-live.png',
                        docLink: 'live-stacking.md',
                        body: [
                            'LIVE accumulates every frame the sequence captures into a running mean stack, aligned by star matching. SNR climbs in real time, you watch the nebula emerge over the first 20 frames.',
                            'Optional triggers: auto-refocus when HFR degrades by 30% or temperature drifts 2 degC, recenter when plate-solve drift crosses 30 arcsec. Set them once and let the night run unattended.'
                        ]
                    },
                    {
                        title: 'Calibrate + integrate in STUDIO',
                        tab: 'files',
                        tabLabel: 'STUDIO',
                        screenshot: 'capture/10-studio.png',
                        docLink: 'files.md',
                        body: [
                            'STUDIO is the unified post-capture workspace: file browser on the left, Stack workspace pinned to the right. Browse to your captures, multi-select the lights, click "+ Add selection" on the Lights slot card; repeat for darks, flats, biases by browsing to those folders.',
                            'Then click the action buttons under the slots in order: Master Dark / Flat / Bias to build masters from raw cal frames, Calibrate Lights to apply them, Integrate to align + stack with sigma-clipping. Multi-night targets: just add both sessions\' lights into the same slot.'
                        ],
                        tip: 'Turn on the "Show FITS metadata" toggle in the browser to see IMAGETYP / FILTER / TARGET / EXPOSURE columns -- makes it trivial to spot the right frames in a folder full of mixed captures.'
                    },
                    {
                        title: 'AI cleanup (optional)',
                        tab: 'files',
                        tabLabel: 'STUDIO',
                        screenshot: 'capture/11-ai.png',
                        docLink: 'onnx-inference.md',
                        body: [
                            'Pick the integrated master in the browser and click the GraXpert icons in the toolbar (BGE for gradient removal, Decon for sharpening, Denoise for noise reduction). All three run in the browser via WebAssembly + WebGPU when supported; falls back to CLI subprocess on the server otherwise.',
                            'Typical order: BGE first to flatten the background, then optionally Denoise and Decon. Each operation writes a sibling FITS (suffixed _bge / _decon / _denoise) so you keep the original.'
                        ]
                    },
                    {
                        title: 'Edit + export',
                        tab: 'files',
                        tabLabel: 'STUDIO',
                        screenshot: 'capture/12-editor-export.png',
                        docLink: 'editor.md',
                        body: [
                            'Single-select the master and click the Edit icon in the browser toolbar. The Lightroom-style editor opens as a fullscreen modal over the Studio tab: tone curves, stretch (autostretch or manual), saturation, sharpening, vignette. Every adjustment is non-destructive; the sidecar stores the recipe, the master is never overwritten.',
                            'When happy, hit Export inside the modal: JPEG / PNG / 16-bit TIFF, optional downscale, optional EXIF stamp. The result lands under {rig}/processed/edited/ and appears in the browser on the left next to everything else.'
                        ],
                        tip: 'ESC closes the editor. Reopen the same master later and the sliders restore exactly where you left them.'
                    }
                ],

                // HELP-4: LRGB / mono pipeline (~5 steps).
                lrgb: [
                    {
                        title: 'Why LRGB instead of OSC',
                        screenshot: 'lrgb/01-overview.png',
                        docLink: 'lrgb-mono-workflow.md',
                        body: [
                            'Mono sensors capture more light per pixel because they skip the Bayer mosaic. You shoot through a filter wheel: Luminance for detail, Red / Green / Blue for color, optionally Hydrogen-Alpha / OIII / SII for narrowband emission nebulae.',
                            'Trade-off: each filter is a separate target session. Same target = 4x the time vs OSC. Reward: cleaner data, sharper detail, way better narrowband.'
                        ]
                    },
                    {
                        title: 'Capture per filter',
                        tab: 'sequence',
                        tabLabel: 'AUTORUN',
                        screenshot: 'lrgb/02-per-filter.png',
                        docLink: 'sequence.md',
                        body: [
                            'AUTORUN with the Advanced sequencer (ADV tab) handles multi-filter cleanly: a loop container with a "switch filter" step + "take exposure" step + nested dither + meridian flip triggers.',
                            'Typical OSC-equivalent OSC: 60 L + 30 R + 30 G + 30 B at 120s. Narrowband (Ha/OIII/SII) at 300-600s; fewer subs but longer each.'
                        ],
                        tip: 'Refocus per filter swap; the rig profile remembers per-filter focuser offsets so the swap + refocus is one button.'
                    },
                    {
                        title: 'Integrate per filter',
                        tab: 'files',
                        tabLabel: 'STUDIO',
                        screenshot: 'lrgb/03-integrate.png',
                        docLink: 'files.md',
                        body: [
                            'STUDIO: build one master per filter. For each filter, browse to that filter\'s folder, add the lights to the Lights slot, hit Calibrate Lights and then Integrate. Repeat for L, R, G, B (and Ha/OIII/SII if you have them).',
                            'Output: 4-7 mono FITS masters, one per filter, ready for channel combine.'
                        ]
                    },
                    {
                        title: 'Channel combine into RGB',
                        tab: 'files',
                        tabLabel: 'STUDIO',
                        screenshot: 'lrgb/04-combine.png',
                        docLink: 'lrgb-mono-workflow.md',
                        body: [
                            'Add the per-filter integrated masters into the Lights slot (R, G, B, optionally L). Click the Combine button: a prompt asks which file maps to which variable, and whether you want RGB or LRGB output.',
                            'For narrowband palettes (HOO, SHO, HaRGB) use pixel-math expressions in the same modal. Output: a single integrated RGB master, ready for the editor.'
                        ]
                    },
                    {
                        title: 'Edit + export the color master',
                        tab: 'files',
                        tabLabel: 'STUDIO',
                        screenshot: 'lrgb/05-edit.png',
                        docLink: 'editor.md',
                        body: [
                            'Single-select the combined RGB master in the browser, click the Edit icon. Same flow as the OSC editor modal: AI cleanup if you want it, stretch, color balance, saturation, sharpening, export. The RGB master behaves exactly like an OSC master from this point on.'
                        ]
                    }
                ],

                // HELP-4: Planetary / lucky imaging (~4 steps).
                planetary: [
                    {
                        title: 'Why lucky imaging is different',
                        screenshot: 'planetary/01-overview.png',
                        docLink: 'video-planetary.md',
                        body: [
                            'Planets are bright and small, the limit is atmospheric seeing, not photons. You record THOUSANDS of short frames (5-50ms each), then keep only the few percent where seeing happened to be still, and stack those.',
                            'Polaris does this via the VIDEO tab + SER recording + per-frame quality analysis. Very different from DSO capture.'
                        ]
                    },
                    {
                        title: 'Record an SER stream',
                        tab: 'video',
                        tabLabel: 'VIDEO',
                        screenshot: 'planetary/02-record.png',
                        docLink: 'video-planetary.md',
                        body: [
                            'VIDEO → Capture sub-tab. Set exposure (5-20ms for Jupiter, 30-80ms for Saturn at f/20+), gain high enough to fill the histogram to ~60%, click Record. Polaris streams native CCD_VIDEO_STREAM (INDI) or falls back to a tight capture loop.',
                            'Aim for 5-20 thousand frames. SER files end up in {rig}/planetary/{target}/, openable in AutoStakkert / RegiStax later if you want to compare.'
                        ],
                        tip: 'Crop to a tight ROI around the planet, smaller frames = higher framerate = more "lucky" moments captured.'
                    },
                    {
                        title: 'Analyze + rank frames',
                        tab: 'video',
                        tabLabel: 'VIDEO',
                        screenshot: 'planetary/03-analyze.png',
                        docLink: 'video-planetary.md',
                        body: [
                            'VIDEO → Process sub-tab. Open the SER, the Laplacian variance metric scores every frame for sharpness, sorts them best-first, shows you a quality histogram.',
                            'Pick a "keep" percentage (typically 10-25% of the total). Polaris stacks those into a single image, aligned by brightest-pixel centroid.'
                        ]
                    },
                    {
                        title: 'Export the planet image',
                        tab: 'files',
                        tabLabel: 'STUDIO',
                        screenshot: 'planetary/04-export.png',
                        docLink: 'editor.md',
                        body: [
                            'Open STUDIO, browse to the stacked planet master, single-select it, click the Edit icon. The editor modal opens with the master loaded. Apply Decon for the wavelet-like sharpening planets crave, optional color saturation boost, export to PNG / TIFF.',
                            'Final tweaks (false-color, derotation, animations) are usually done in WinJUPOS / RegiStax post-Polaris.'
                        ]
                    }
                ],

                // HELP-4: Photometric color calibration (~3 steps).
                pcc: [
                    {
                        title: 'What PCC does',
                        screenshot: 'pcc/01-overview.png',
                        docLink: 'color-calibration.md',
                        body: [
                            'Photometric color calibration removes the color bias of your sensor + filters + atmosphere by comparing the brightness of stars in your image against a catalog of stars with known colors (APASS).',
                            'Result: the white point is mathematically calibrated, not eyeballed. Every nebula renders in its "true" color. The alternative (manual color balance sliders) is fast but subjective.'
                        ]
                    },
                    {
                        title: 'Plate-solve the master',
                        tab: 'files',
                        tabLabel: 'STUDIO',
                        screenshot: 'pcc/02-solve.png',
                        docLink: 'color-calibration.md',
                        body: [
                            'PCC needs to know where each star in your image is on the sky. Open STUDIO, browse to the integrated master, add it to the Lights slot in the Stack workspace, then run the integration pipeline (or use an already-integrated master). The Integrate step bakes WCS coordinates into the output FITS header via ASTAP.',
                            'Without WCS the catalog match cannot run. Polaris will refuse to start PCC and tell you why.'
                        ]
                    },
                    {
                        title: 'Run PCC, apply the gains',
                        tab: 'files',
                        tabLabel: 'STUDIO',
                        screenshot: 'pcc/03-run.png',
                        docLink: 'color-calibration.md',
                        body: [
                            'Add the plate-solved master to the Lights slot, hit the Color Cal action button. Polaris queries the APASS catalog for stars in your field, matches them with the stars it detects, fits per-channel gains that minimize the color error.',
                            'Output: a new color-calibrated master appears in the browser. Single-select it and click the Edit icon to continue in the editor modal.'
                        ],
                        warn: 'APASS bundled dataset (~80 MB) needs to be downloaded once. Run scripts/download-apass.py on the server. Polaris prints a clear error pointing at this if the DB is missing.'
                    }
                ],

                // HELP-5: Troubleshoot accordion. NOT a stepper; the
                // template renders this array as <details> entries.
                // Each item has title + body (string[]) + optional
                // docLink. No screenshot, no Open-tab button.
                troubleshoot: [
                    {
                        title: "I can't reach https://polaris-pi.local:5000",
                        docLink: 'troubleshooting.md',
                        body: [
                            "mDNS may not resolve on every device (especially Android). Find the Pi's IP with hostname -I on the Pi or your router's DHCP table, then open https://192.168.x.y:5000 instead.",
                            "If you're trying to reach the hotspot (SSID Polaris-Hotspot, password polaris1234), make sure your phone actually joined that network, then open https://10.42.0.1:5000.",
                            "Check the systemd service status on the Pi: sudo systemctl status polaris.service should show 'active (running)'. Logs: journalctl -u polaris.service -f."
                        ]
                    },
                    {
                        title: "Plate solve always fails",
                        docLink: 'plate-solving.md',
                        body: [
                            "Three things to check: (1) ASTAP is installed and Polaris found the binary (Settings → External tools shows the path), (2) you have a star catalog installed (V50 covers most setups, ~1.5 GB), (3) the field actually has stars (open a preview snap and confirm).",
                            "Give the solver hints: RA/Dec from the mount (Polaris does this automatically when the mount is connected), search radius 5-10 degrees, expected pixel scale from your rig profile. Without hints the solver may take minutes; with hints, seconds.",
                            "Star catalogs go in the same dir as the ASTAP binary. On Linux: /opt/astap/. Download V50 deb from the ASTAP project on SourceForge."
                        ]
                    },
                    {
                        title: "Sequence won't start, says 'no camera connected'",
                        docLink: 'rigs.md',
                        body: [
                            "Go to RIGS, check the camera card. Is it green? If not, click Connect. If the driver dropdown is empty, the discovery missed your hardware, re-check on the INDI Web sub-tab.",
                            "Some drivers (especially DSLR) need the device to be powered on + USB-connected BEFORE you click Connect. Plug it in first, then drive picker, then Connect.",
                            "If the camera is green but capture immediately fails, look at the activity bar at the bottom for the actual error chip. Common: cooler can't reach setpoint, exposure timeout, full disk."
                        ]
                    },
                    {
                        title: "PHD2 won't connect / no guide camera",
                        docLink: 'guide-phd2.md',
                        body: [
                            "PHD2 must be running BEFORE you click Connect in GUIDE. Polaris doesn't auto-launch on Windows; on Linux the Phd2GuiSessionService can launch xpra + PHD2 for you (toggle in Settings).",
                            "The guide camera lives inside PHD2's profile, not in Polaris's RIGS tab. Pick the right PHD2 profile (the dropdown in GUIDE → Control) and Polaris will sync.",
                            "Smart Calibrate fails with 'no star found': lower the SigmaThreshold in PHD2 Brain → Star detection, or hand-pick a star in the PHD2 GUI tab."
                        ]
                    },
                    {
                        title: "GraXpert says 'model not found'",
                        docLink: 'onnx-inference.md',
                        body: [
                            "Browser mode (default): the ONNX models live under wwwroot/graxpert/models/ on the server OR /home/polaris/models on Linux. Polaris auto-discovers either layout, no Settings config needed if the files are in one of those paths.",
                            "CLI mode (advanced toggle in Settings): GraXpert v3 expects models under ~/.local/share/GraXpert/{ai-models, bge-ai-models}/. The Pi setup doc has rsync one-liners to copy them from a Windows machine."
                        ]
                    },
                    {
                        title: "Live stack drifts, target slides out of frame",
                        docLink: 'live-stacking.md',
                        body: [
                            "Two root causes: bad polar alignment (mount tracks a small circle around the wrong pole) or no guiding (no closed-loop drift correction). Fix the upstream cause; live stacking can't paper over either.",
                            "Quick check: open POLAR and re-run TPPA. If the residual is over 1 arcmin, that's your drift source.",
                            "Workaround for short sessions: enable 'auto recenter' in LIVE → Triggers with a 30 arcsec threshold. Polaris will plate-solve every N frames and nudge the mount back on target. Costs CPU but covers light drift."
                        ]
                    }
                ]
            };
        },

        // ---- CLOCK-3: wall-clock sync helpers --------------------------

        // Posts the client's current UTC to the server. Server applies
        // it via timedatectl (Linux + polkit) and returns the post-
        // sync residual skew. Disables the button while in flight to
        // avoid double-fires.
        async clockSyncFromClient() {
            if (this.clockSync.busy) return;
            this.clockSync.busy = true;
            this.clockSync.lastError = null;
            try {
                // Bumped to 20s explicitly: backend timedatectl can take
                // 5s + 5s in the worst case (set-ntp + set-time both
                // hitting their max). Default apiFetch timeout of 15s
                // races with that and surfaces as 'Request timed out'
                // -> 'Network error' in the catch even when the
                // backend would have responded with the real reason.
                const r = await this.apiPost('/api/system/clock/sync', {
                    clientUtc: new Date().toISOString()
                }, { timeout: 20000 });
                const j = await r.json();
                if (j.ok) {
                    this.clockSync.lastSyncAt = new Date();
                    this.toast('Pi clock synced from this device', 'ok');
                    // Force next WS payload to refresh skew quickly.
                    this.clockSync.skewSeconds = 0;
                } else {
                    this.clockSync.lastError = j.error || 'Sync failed';
                    this.toast(this.clockSync.lastError, 'error');
                }
            } catch (e) {
                // Three distinct failure modes to surface clearly:
                //   1) ApiError(status, body) — server responded with a
                //      4xx/5xx and a JSON body containing { error: "..." }
                //   2) "Request timed out" — apiFetch aborted (backend
                //      hung longer than 20s, likely polkit waiting on an
                //      auth agent we can't provide)
                //   3) Other (offline / TLS / CORS) -> fallback message
                let msg;
                if (e && e.body) {
                    try { msg = JSON.parse(e.body).error; } catch {}
                }
                if (!msg && e && e.message && e.message.includes('timed out')) {
                    msg = 'Server did not respond in 20s. Most likely the '
                        + "polkit rule isn't installed -- check the Pi log "
                        + 'for "Not authorized" from timedatectl, or '
                        + 'reinstall the .deb package.';
                }
                if (!msg) {
                    msg = (e && e.message) || 'Network error';
                }
                this.clockSync.lastError = msg;
                this.toast('Clock sync failed: ' + msg, 'error');
            } finally {
                this.clockSync.busy = false;
            }
        },

        // Human-readable skew label. Positive = server is AHEAD,
        // negative = server is BEHIND. Pluralizes + scales seconds /
        // minutes / hours, returns null when |skew| <= 30s so the
        // chip stays hidden in the normal case.
        clockSkewLabel() {
            const s = this.clockSync.skewSeconds | 0;
            const abs = Math.abs(s);
            if (abs <= 30) return null;
            const direction = s > 0 ? 'ahead' : 'behind';
            if (abs < 90) return abs + 's ' + direction;
            if (abs < 60 * 60) return Math.round(abs / 60) + 'm ' + direction;
            if (abs < 24 * 60 * 60) return Math.round(abs / 3600) + 'h ' + direction;
            return Math.round(abs / 86400) + 'd ' + direction;
        },

        // Bigger threshold for the loud "drift is serious" styling
        // (vs the soft warning at 30s). 5 minutes is the line where
        // sequence timing + dither + plate-solve hint expirations
        // start breaking, so the chip turns from amber to red there.
        clockSkewClass() {
            const abs = Math.abs(this.clockSync.skewSeconds | 0);
            if (abs > 300) return 'host-red';
            if (abs > 30) return 'host-amber';
            return 'host-green';
        },

        // ---- Exposure preset dropdown source --------------------------
        // Returns the global ladder filtered to >= camera's minimum
        // supported exposure when the connected camera reports one
        // (equipCameraInfo.minExposure, plumbed through the camera
        // DTO when the backend grows that field), otherwise 0.0001s
        // as a safe default that doesn't clip modern CMOS lower bounds.
        // Consumed by the single <datalist id="exposure-presets">; all
        // exposure inputs across the UI carry list="exposure-presets"
        // so any future tweak (per-camera min, log/linear hybrid, etc.)
        // touches one place.
        exposurePresets() {
            const min = (this.equipCameraInfo && this.equipCameraInfo.minExposure)
                || 0.0001;
            return EXPOSURE_PRESETS_ALL.filter(v => v >= min);
        },

        // ZWO L/M/H gain presets, mirrors ASIAIR's three-button shortcut.
        // Returns { L, M, H, hcg } for the active camera if its INDI/Alpaca
        // device name matches a known ZWO model key (substring match,
        // case-insensitive), null otherwise. The UI conditionally renders
        // the L/M/H button strip on this, non-ZWO cameras get nothing.
        zwoPresetsForActiveCamera() {
            if (!this._zwoPresets) return null;
            const name = (this.selectedCamera
                || this.equipCameraChoice
                || '').toUpperCase();
            if (!name) return null;
            for (const key in this._zwoPresets) {
                if (name.includes(key)) return this._zwoPresets[key];
            }
            return null;
        },

        // Apply a preset to a x-model binding. Object-path setter so a
        // single helper can write to `preview.gain`, `video.gain`,
        // `polar.gain`, `gain` (root level), or any sequence row item's
        // gain field. `path` is dot-separated; `value` is the numeric
        // preset (0, 100, 250, etc.). Triggers @change handlers via
        // Alpine reactivity.
        applyGainPreset(path, value) {
            const parts = path.split('.');
            let obj = this;
            for (let i = 0; i < parts.length - 1; i++) {
                obj = obj[parts[i]];
                if (obj == null) return;
            }
            obj[parts[parts.length - 1]] = value;
        },

        // ----- Remote terminal (xterm.js + SSH via /ws/terminal) -----

        async termConnect() {
            if (this.term.connected || this.term.connecting) return;
            if (!this.term.host || !this.term.user) {
                this.term.lastError = 'Host and user are required.';
                return;
            }
            if (typeof Terminal === 'undefined') {
                this.term.lastError = 'xterm.js failed to load. Check the network tab.';
                return;
            }
            this.term.connecting = true;
            this.term.lastError = '';

            // Build xterm instance fresh on every Connect, recycling
            // across sessions leaks DOM state from the previous host.
            this._termInstance = new Terminal({
                cursorBlink: true,
                fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
                fontSize: 13,
                theme: { background: '#0b0f18', foreground: '#e6ecf5' },
                scrollback: 5000,
            });
            this._termFitAddon = new FitAddon.FitAddon();
            this._termInstance.loadAddon(this._termFitAddon);

            const mount = this.$refs.termMount;
            mount.innerHTML = '';
            this._termInstance.open(mount);
            // First fit AFTER open() so the size matches the actual
            // mounted DOM box (xterm needs the parent's metrics).
            this.$nextTick(() => { try { this._termFitAddon.fit(); } catch { } });

            // Resize observer keeps the terminal grid in sync with
            // the panel width on window resize / sidebar toggle.
            this._termResizeObserver = new ResizeObserver(() => {
                try {
                    this._termFitAddon.fit();
                    if (this._termSocket?.readyState === WebSocket.OPEN) {
                        this._wsSendTracked(this._termSocket, JSON.stringify({
                            type: 'resize',
                            cols: this._termInstance.cols,
                            rows: this._termInstance.rows
                        }));
                    }
                } catch { /* observer fires after teardown sometimes */ }
            });
            this._termResizeObserver.observe(mount);

            // Open the WebSocket bridge.
            const wsProto = location.protocol === 'https:' ? 'wss:' : 'ws:';
            // Same authUrl rationale as the other WS endpoints, the
            // WebSocket upgrade can't carry an Authorization header.
            const url = this.authUrl(wsProto + '//' + location.host + '/ws/terminal');
            const ws = new WebSocket(url);
            ws.binaryType = 'arraybuffer';
            this._termSocket = ws;

            ws.onopen = () => {
                // Auth handshake: single JSON frame with creds + PTY size.
                // Server keeps them only in memory for this socket's life.
                this._wsSendTracked(ws, JSON.stringify({
                    type: 'auth',
                    host: this.term.host,
                    port: this.term.port,
                    user: this.term.user,
                    password: this.term.password,
                    cols: this._termInstance.cols,
                    rows: this._termInstance.rows
                }));
                // Wipe the password field as soon as the byte is on the
                // wire so it doesn't sit visible in the form.
                this.term.password = '';
                this.term.connecting = false;
                this.term.connected = true;
            };
            ws.onmessage = (ev) => {
                // Server frames are UTF-8 strings (SSH stdout/stderr).
                const data = typeof ev.data === 'string'
                    ? ev.data
                    : new TextDecoder().decode(new Uint8Array(ev.data));
                // NET-1: account for both text + binary terminal frames.
                this._netRx(typeof ev.data === 'string'
                    ? ev.data.length
                    : (ev.data.byteLength || 0));
                this._termInstance.write(data);
            };
            ws.onerror = () => {
                this.term.lastError = 'WebSocket error. Is Terminal:Enabled=true on the server?';
            };
            ws.onclose = (ev) => {
                this.term.connected = false;
                this.term.connecting = false;
                if (ev.code !== 1000 && !this.term.lastError) {
                    this.term.lastError = 'Disconnected (' + (ev.reason || 'code ' + ev.code) + ')';
                }
                this._termCleanup();
            };

            // Pipe local keystrokes → server.
            this._termInstance.onData((data) => {
                if (ws.readyState === WebSocket.OPEN) ws.send(data);
            });
        },

        termDisconnect() {
            try { this._termSocket?.close(1000, 'user disconnect'); }
            catch { /* may already be closed */ }
            this._termCleanup();
            this.term.connected = false;
            this.term.connecting = false;
        },

        _termCleanup() {
            try { this._termResizeObserver?.disconnect(); } catch { }
            this._termResizeObserver = null;
            try { this._termInstance?.dispose(); } catch { }
            this._termInstance = null;
            this._termFitAddon = null;
            this._termSocket = null;
        },

        // Mirror the CSS @media rules in the head section of app.css:
        // ≤640 → 0.75, ≤960 → 0.85, otherwise 1.0. Used both as the
        // first-paint default before any persisted preference is
        // loaded AND as the value the Reset button restores to.
        _defaultUiZoom() {
            try {
                if (window.matchMedia('(max-width: 640px)').matches) return 0.75;
                if (window.matchMedia('(max-width: 960px)').matches) return 0.85;
            } catch (_) { /* matchMedia missing in very old browsers */ }
            return 1.0;
        },

        // Commit uiZoomDraft (slider position) → uiZoom (live page
        // zoom). The inline body style outranks the @media-keyed
        // `body { zoom: 0.85 }` rules so the slider always wins,
        // including at the same breakpoint. Persist alongside.
        applyUiZoom() {
            const z = Math.max(0.5, Math.min(1.5, +this.uiZoomDraft || 1.0));
            this.uiZoom = z;
            this.uiZoomDraft = z;
            document.body.style.zoom = String(z);
            try { localStorage.setItem('nina-ui-zoom', String(z)); }
            catch (_) { /* private mode etc. */ }
        },

        // FONT-1: write [data-font] on <html> + persist. The CSS
        // selectors in app.css pick up the attribute and swap
        // --font-body / --font-mono; the change is instant — no
        // reflow more expensive than a font swap, and no need to
        // re-render any canvas since canvases don't use document
        // CSS. 'atkinson' clears the attribute (default state) so
        // the bare :root vars apply.
        applyUiFont() {
            const allowed = ['inter', 'atkinson', 'plex', 'system'];
            const v = allowed.includes(this.uiFont) ? this.uiFont : 'atkinson';
            this.uiFont = v;
            try {
                if (v === 'atkinson') document.documentElement.removeAttribute('data-font');
                else document.documentElement.setAttribute('data-font', v);
                localStorage.setItem('nina-ui-font', v);
            } catch (_) { /* private mode etc. */ }
        },

        // Drop the user's explicit pick and fall back to whatever the
        // viewport-based default is right now. Useful when the user
        // moved between window sizes (e.g. docking a tablet) and
        // wants the auto default again. Syncs both committed value
        // and the slider so the next Apply isn't comparing stale
        // numbers.
        resetUiZoom() {
            try { localStorage.removeItem('nina-ui-zoom'); } catch (_) { }
            const z = this._defaultUiZoom();
            this.uiZoom = z;
            this.uiZoomDraft = z;
            document.body.style.zoom = String(z);
        },

        // Toast notification (auto-dismiss)
        // DBGLOG-6: install one-time global error capture. window.onerror
        // covers thrown exceptions; unhandledrejection catches awaited
        // promises that rejected without a .catch. Both are silently
        // dropped by browsers when no handler is set, which is exactly
        // what we want to fix: those silent drops are the worst bugs to
        // diagnose remotely. We do NOT preventDefault so the default
        // browser console output still happens for the dev sitting in
        // front of devtools.
        _installLogHooks() {
            if (this._logHooksInstalled) return;
            this._logHooksInstalled = true;
            const self = this;
            window.addEventListener('error', (ev) => {
                try {
                    const e = ev && ev.error;
                    const msg = ev && ev.message ? ev.message : String(ev);
                    self._logFromClient('error', 'window.onerror: ' + msg, {
                        source: 'exception',
                        exceptionType: e && e.name,
                        exceptionMsg: e && e.message,
                        stackTrace: e && e.stack ? String(e.stack).split('\n').slice(0, 20).join('\n') : null,
                        path: ev && ev.filename
                    });
                } catch (_) {}
            });
            window.addEventListener('unhandledrejection', (ev) => {
                try {
                    const r = ev && ev.reason;
                    const msg = r && r.message ? r.message : String(r);
                    self._logFromClient('error', 'unhandledrejection: ' + msg, {
                        source: 'exception',
                        exceptionType: r && r.name,
                        exceptionMsg: msg,
                        stackTrace: r && r.stack ? String(r.stack).split('\n').slice(0, 20).join('\n') : null
                    });
                } catch (_) {}
            });
        },

        // DBGLOG-6: queue a single client-side entry. Debounced flush
        // bundles up to 50 entries per POST to /api/logs/client. The
        // server enforces a 100-entry hard cap and rate-limits per IP
        // so even a runaway error loop won't saturate the buffer.
        _logFromClient(level, message, extra) {
            try {
                const entry = {
                    at: new Date().toISOString(),
                    level: level || 'info',
                    source: (extra && extra.source) || 'client',
                    message: String(message || ''),
                    category: extra && extra.category,
                    method: extra && extra.method,
                    path: extra && extra.path,
                    status: extra && extra.status,
                    durationMs: extra && extra.durationMs,
                    exceptionType: extra && extra.exceptionType,
                    exceptionMsg: extra && extra.exceptionMsg,
                    stackTrace: extra && extra.stackTrace
                };
                this._logClientQueue.push(entry);
                if (this._logClientQueue.length >= 50) {
                    this._flushClientLogs();
                } else if (!this._logClientFlushTimer) {
                    this._logClientFlushTimer = setTimeout(
                        () => this._flushClientLogs(), 1000);
                }
            } catch (_) {
                // logging must never throw into the calling code path
            }
        },

        async _flushClientLogs() {
            if (this._logClientFlushTimer) {
                clearTimeout(this._logClientFlushTimer);
                this._logClientFlushTimer = null;
            }
            if (this._logClientQueue.length === 0) return;
            const batch = this._logClientQueue.splice(0, 100);
            try {
                await this.apiFetch('/api/logs/client', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ entries: batch })
                });
            } catch (_) {
                // drop on failure — re-queueing risks an infinite loop
                // when the server is down. Local entries already render
                // in the panel via _ingestLocalEntry below.
            }
        },

        // DBGLOG-7: log panel open/close + filter helpers. Backfill on
        // open via GET /api/logs?since=cursor so a long-gap reconnection
        // picks up entries that fell out of our local 5000-cap window.
        async logOpen() {
            this.logs.panelOpen = true;
            this.logs.unreadErrors = 0;
            this.logs.unreadWarnings = 0;
            try {
                const r = await this.apiFetch(
                    `/api/logs/?since=${this.logs.cursor}&max=1000`);
                const data = await r.json();
                if (data && Array.isArray(data.entries)) {
                    for (const e of data.entries) this._absorbLogEntry(e);
                }
            } catch (_) {}
        },
        logClose() { this.logs.panelOpen = false; },

        // DBGLOG-7: client-side filter pass. Server pre-filters via the
        // initial backfill query, but the WS stream pushes everything;
        // re-filtering here keeps the panel responsive when the user
        // tweaks level/source/search without an extra round-trip.
        logFiltered() {
            const lvlRank = { debug: 0, info: 1, warn: 2, error: 3, critical: 4 };
            const minRank = lvlRank[this.logs.filterLevel] ?? 1;
            const src = this.logs.filterSource;
            const needle = (this.logs.searchText || '').trim().toLowerCase();
            // .slice() before .reverse() because reverse() mutates in place
            // and Alpine treats the array reference as stable -- mutating
            // it would scramble the on-screen render. Newest first means
            // the operator sees the latest event without scrolling.
            return this.logs.entries.filter(e => {
                if ((lvlRank[e.level] ?? 1) < minRank) return false;
                if (src && src !== 'all' && e.source !== src) return false;
                if (needle) {
                    const hay = (e.message + ' ' + (e.category || '') + ' ' + (e.path || '')).toLowerCase();
                    if (!hay.includes(needle)) return false;
                }
                return true;
            }).slice().reverse();
        },

        // DBGLOG-7: insert a server-supplied LogEntry into our local
        // window, advance cursor, tick unread counters when the panel
        // is closed. Truncates at 5000 to mirror the server cap.
        _absorbLogEntry(entry) {
            if (!entry || typeof entry.id !== 'number') return;
            if (entry.id <= this.logs.cursor) return;   // already have it
            this.logs.entries.push(entry);
            if (this.logs.entries.length > 5000) this.logs.entries.shift();
            if (entry.id > this.logs.cursor) this.logs.cursor = entry.id;
            if (!this.logs.panelOpen) {
                if (entry.level === 'error' || entry.level === 'critical') {
                    this.logs.unreadErrors++;
                } else if (entry.level === 'warn') {
                    this.logs.unreadWarnings++;
                }
            }
        },

        // DBGLOG-8: download the full ring buffer as JSONL via the
        // browser's download dialog. Uses the auth-attached cookie
        // (same origin) so the GET works without us baking the token
        // into the URL (avoids logging the token in the access log).
        logExport(format) {
            const fmt = format === 'txt' ? 'txt' : 'jsonl';
            const url = '/api/logs/export?format=' + fmt;
            const a = document.createElement('a');
            a.href = url;
            a.download = '';   // browser uses Content-Disposition filename
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
        },

        // DBGLOG-8: confirm + wipe the server ring buffer. Local cursor
        // intentionally NOT reset — subsequent WS ticks will see a
        // truncated:true flag and the panel UI can show "buffer cleared".
        async logClear() {
            if (!await this._confirmAsync(
                    'Clear all server log entries? This cannot be undone.',
                    { title: 'Clear debug log', okLabel: 'Clear', danger: true })) return;
            try {
                await this.apiFetch('/api/logs/', { method: 'DELETE' });
                this.logs.entries = [];
                this.toast('Log do servidor limpo', 'ok');
            } catch (e) {
                this.toast('Falha ao limpar log: ' + (e.message || ''), 'error');
            }
        },

        toast(message, type = 'info', duration = 4000) {
            const id = ++this._toastId;
            this.toasts.push({ id, message, type });
            if (this.toasts.length > 5) this.toasts.shift();
            setTimeout(() => {
                this.toasts = this.toasts.filter(t => t.id !== id);
            }, duration);
            // DBGLOG-6: mirror every toast into the debug log. Map our
            // 4-tier ui type ('info'|'ok'|'warn'|'error') to the canonical
            // 5-level scheme; treat 'ok' as info because it's positive
            // feedback rather than a problem worth flagging.
            const level = type === 'error' ? 'error'
                        : type === 'warn'  ? 'warn'
                        : 'info';
            this._logFromClient(level, message, { source: 'toast' });
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
            // WebSocket upgrade can't carry the Authorization header,
            // browsers only attach cookies on same-origin upgrades.
            // The polaris_session cookie covers most cases, but it
            // dies on browser close + isn't sent under some hostname /
            // scheme combos (mDNS hostname switch after a WiFi mode
            // flip, etc). authUrl appends ?token= as the query fallback
            // the AuthMiddleware also accepts.
            const ws = new WebSocket(
                this.authUrl(`${protocol}//${location.host}/ws/status`));

            ws.onopen = () => {
                this._statusWsAttempt = 0;
                this.serverReachable = true;
            };

            ws.onmessage = (evt) => {
                // NET-1: status frames are JSON text, length is a
                // reasonable byte-count approximation for ASCII payload.
                if (typeof evt.data === 'string') this._netRx(evt.data.length);
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
            // Same authUrl rationale as /ws/status: WebSocket upgrades
            // can't carry the Authorization header; ?token= falls
            // through to AuthMiddleware's query-fallback path.
            const ws = new WebSocket(
                this.authUrl(`${protocol}//${location.host}/ws/image-stream`));

            ws.binaryType = 'arraybuffer';

            ws.onopen = () => {
                this._imageWsAttempt = 0;
                // CLST-4: if WASM is ready, ask for raw mode (uint16 +
                // LZ4) so the client-side stacker can do its work.
                // Otherwise stick with JPEG (universally supported,
                // server-side stacked preview already encoded).
                const mode = this.wasmReady ? 'raw' : 'jpeg';
                this._wsSendTracked(ws, JSON.stringify({ mode }));
                // Tell the server we can stack client-side. CLST-5 will
                // act on this (flip LiveStackingService → MetricsOnly).
                // Until CLST-5 ships the server logs + ignores it;
                // sending now is benign.
                this._wsSendTracked(ws, JSON.stringify({
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
                    this._wsSendTracked(ws, JSON.stringify({ mode: 'raw' }));
                    this._wsSendTracked(ws, JSON.stringify({
                        type: 'client-capability',
                        wasm: true,
                        wasmVersion: this.wasmVersion
                    }));
                }
            }, { once: true });

            ws.onmessage = (evt) => {
                // NET-1: account for both control text + binary frame.
                if (typeof evt.data === 'string') {
                    this._netRx(evt.data.length);
                    // Welcome or control message
                    try {
                        const msg = JSON.parse(evt.data);
                        if (msg.type === 'connected') {
                            this._imageClientId = msg.clientId;
                        }
                    } catch (e) { }
                    return;
                }
                this._netRx(evt.data.byteLength || 0);
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
        // In JPEG mode, the frame is a raw JPEG blob, draw via Image element.
        // In raw mode, the frame is: [4B headerLen][header][LZ4 compressed uint16 pixels].
        handleImageFrame(arrayBuffer) {
            this.liveActive = true;

            // Three on-wire formats now possible:
            //  (a) Bare JPEG -- starts with FF D8 at offset 0. Legacy
            //      shape from older servers that didn't envelope JPEG.
            //      No FrameKind available -- defaults to Live (0).
            //  (b) Headered JPEG -- 4-byte int32 headerLen + 24-byte
            //      stream header (carrying FrameKind at offset 20 of
            //      the header itself) + raw JPEG bytes starting with
            //      FF D8 at offset 4+headerLen. Current server emits
            //      this; carries the per-panel routing tag so PREVIEW
            //      snaps no longer leak onto the LIVE canvas.
            //  (c) Raw LZ4 -- 4-byte headerLen + 24-byte header +
            //      LZ4-compressed pixel payload. Always carried
            //      FrameKind correctly via _renderRawFrame.
            const view = new Uint8Array(arrayBuffer);
            const isBareJpeg = view.length >= 2
                && view[0] === 0xFF && view[1] === 0xD8;
            let headeredJpegKind = null;
            let headeredJpegBytes = null;
            if (!isBareJpeg && view.length >= 30) {
                const dv0 = new DataView(arrayBuffer);
                const headerLen0 = dv0.getInt32(0, true);
                // Sanity-bound the headerLen so an LZ4 payload starting
                // with an arbitrary 4-byte sequence doesn't get
                // mis-parsed as a giant header.
                if (headerLen0 >= 20 && headerLen0 <= 64) {
                    const payloadOffset = 4 + headerLen0;
                    if (payloadOffset + 2 <= view.length
                        && view[payloadOffset] === 0xFF
                        && view[payloadOffset + 1] === 0xD8) {
                        // Headered JPEG match. FrameKind lives at
                        // header offset 20 = arrayBuffer offset 24
                        // (4B length prefix shifts every header field
                        // by +4). See the long comment in
                        // _renderRawFrame for the offset-24 vs -20
                        // confusion that hid this bug for a release.
                        headeredJpegKind = headerLen0 >= 24
                            ? dv0.getInt32(24, true) : 0;
                        headeredJpegBytes = arrayBuffer.slice(payloadOffset);
                    }
                }
            }
            const isJpeg = isBareJpeg || headeredJpegBytes !== null;

            // One-shot diagnostic, leaves a single line per session so
            // we can confirm in DevTools whether snap captures actually
            // deliver a binary frame over /ws/image-stream. Quiet for
            // subsequent frames so the video feed doesn't spam.
            if (!this._loggedFirstFrame) {
                this._loggedFirstFrame = true;
                console.log('[Polaris] first image frame: ' + (isJpeg ? 'JPEG' : 'RAW')
                    + ' · ' + arrayBuffer.byteLength + ' bytes'
                    + ' · wasmReady=' + !!this.wasmReady
                    + ' · serverMode=' + (this.liveStackStatus?.mode || 'full'));
            }

            // XFER-4: ephemeral chip for each image-stream frame that
            // arrived. WebSocket API doesn't expose mid-frame progress
            // (browsers buffer until the full message is delivered),
            // so we can't show "downloading 40%" — but we CAN flash a
            // chip showing the frame size + format the moment it lands.
            // Skip tiny frames (< 64 KB) which are usually thumbnails
            // or stats-only payloads and would just spam the chip row.
            //
            // The chip starts already at 100% (loaded=total=byteLength)
            // and _transferEnd's natural ~800ms hold gives the user
            // time to read it before it fades.
            if (arrayBuffer.byteLength >= 64 * 1024) {
                const tid = this._transferStart({
                    label: (isJpeg ? 'JPEG' : 'RAW') + ' frame',
                    direction: 'down',
                    total: arrayBuffer.byteLength
                });
                this._transferProgress(tid, arrayBuffer.byteLength);
                this._transferEnd(tid, true);
            }

            if (isJpeg) {
                // Pass the kind through when we got the headered shape
                // (case b). Bare JPEG falls back to default 0 / Live.
                if (headeredJpegBytes !== null) {
                    this._renderJpegFrame(headeredJpegBytes, headeredJpegKind);
                } else {
                    this._renderJpegFrame(arrayBuffer);
                }
            } else {
                this._renderRawFrame(arrayBuffer);
            }
        },

        // JPEG mode: create blob URL, draw to every receiving canvas.
        // Used to draw only to #liveCanvas + then mirror, but when the
        // user is on a tab where LIVE is display:none, liveCanvas's
        // parent has 0 width and the canvas ended up sized 0x0, the
        // mirror bailed because src.width === 0 and the visible
        // previewCanvas / videoCaptureCanvas got nothing. Render
        // straight into each known canvas instead, sizing from its
        // OWN visible parent.
        _renderJpegFrame(arrayBuffer, frameKind = 0) {
            const blob = new Blob([arrayBuffer], { type: 'image/jpeg' });
            const url = URL.createObjectURL(blob);

            const img = new Image();
            img.onload = () => {
                // JPEG mode has no header to carry the FrameKind, so
                // the caller passes it through from the WS dispatch
                // when known. Default to Live for legacy callers.
                const targets = this._canvasIdsForFrameKind(frameKind);
                let drewAny = false;
                for (const id of targets) {
                    const canvas = document.getElementById(id);
                    if (!canvas) continue;
                    const container = canvas.parentElement;
                    if (!container) continue;
                    const cw = container.clientWidth;
                    const ch = container.clientHeight;
                    // Skip canvases whose container is collapsed (parent
                    // tab not visible). They'll get a fresh paint when
                    // the user switches to that tab via the existing
                    // mirror call.
                    if (cw <= 0 || ch <= 0) continue;
                    const scale = Math.min(cw / img.width, ch / img.height, 1);
                    canvas.width  = Math.round(img.width  * scale);
                    canvas.height = Math.round(img.height * scale);
                    const ctx = canvas.getContext('2d');
                    ctx.imageSmoothingEnabled = true;
                    ctx.imageSmoothingQuality = 'high';
                    ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
                    drewAny = true;
                }
                URL.revokeObjectURL(url);

                if (drewAny) {
                    this.redrawOverlay();
                    // Also fan out to any still-hidden canvases via the
                    // mirror path so the latest frame is ready when the
                    // user switches tabs.
                    this._mirrorLiveToPreviewCanvas();
                }
            };
            img.onerror = () => URL.revokeObjectURL(url);
            img.src = url;
        },

        // LEGACY: used to copy liveCanvas to every other panel's
        // canvas at the end of each render so all tabs showed the
        // same image. With the FrameKind-aware fanout that defeats
        // the per-panel isolation (a PREVIEW snap would land on
        // previewCanvas → this mirror would immediately overwrite it
        // back to whatever liveCanvas was showing). Now a no-op kept
        // only because a handful of call sites still invoke it. Safe
        // to delete the call sites in a future cleanup.
        _mirrorLiveToPreviewCanvas() { return; },
        _mirrorLiveToPreviewCanvas_legacy() {
            const src = document.getElementById('liveCanvas');
            if (!src) return;
            if (src.width === 0 || src.height === 0) return;
            for (const id of ['previewCanvas', 'focusCanvas', 'videoCaptureCanvas', 'slewPreviewCanvas', 'manualFocusCanvas']) {
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

        // PixInsight MTF (used by both _computeStretchParams and
        // the WebGL shader). f(x;m) = (m-1)x / ((2m-1)x - m).
        // Properties: f(0;m)=0, f(1;m)=1, f(m;m)=0.5.
        _mtf(x, m) {
            if (x <= 0) return 0;
            if (x >= 1) return 1;
            if (m <= 0) return 1;
            if (m >= 1) return 0;
            return ((m - 1) * x) / ((2 * m - 1) * x - m);
        },

        // Compute shadow / scale / midtone for the WebGL stretch.
        //
        // Returns three values consumed by the fragment shader:
        //   shadow      — black point, in raw ADU. Pixels below
        //                 this get clipped to 0.
        //   scaleFactor — 1 / (white - shadow). Maps the [shadow,
        //                 white] window onto [0, 1] for the MTF.
        //   midtone     — MTF parameter, 0..1. m=0.5 is identity;
        //                 smaller stretches shadows (typical DSO).
        //
        // The previous version computed only shadow + scaleFactor
        // and let the shader run its own MTF against a hardcoded
        // 0.25 default. Combined with a broken MTF formula in the
        // shader, the visible result was a near-linear stretch:
        // the median sat at ~30% gray and faint structure stayed
        // washed out. Now we port the same GraXpert "15% Bg, 3σ"
        // preset the server-side AutoStretch.cs uses, so live-stack
        // previews look like the FILES / STUDIO thumbnails.
        _computeStretchParams(pixels, maxVal) {
            if (!this.stretchAuto) {
                const shadow = this.stretchBlack * maxVal;
                const white = Math.max(shadow + 1, this.stretchWhite * maxVal);
                const midtone = Math.min(0.999, Math.max(0.001,
                    this.stretchMid || 0.25));
                return { shadow, scaleFactor: 1.0 / (white - shadow), midtone };
            }
            // Find observedMax. Saturation threshold is normally
            // maxVal, but drivers that pack N-bit sensor data into
            // a 16-bit buffer often cap below the theoretical max
            // (ZWO 10-bit shifted into the high 6 bits saturates
            // at 65472, not 65535). When the observed peak looks
            // like a saturation wall (>= 99% of maxVal) we use it
            // as the threshold. ANY scene where observedMax is
            // below 99% means the brightest pixel is legitimate
            // signal that hasn't hit full-well, and excluding it
            // would narrow the sample, raise shadow, and crush
            // mid-tones to black — the user perceived this as
            // "the image got smaller" after the 0.9 version of
            // this heuristic mis-fired on a bright daytime preview.
            const step = Math.max(1, Math.floor(pixels.length / 200000));
            let observedMax = 0;
            for (let i = 0; i < pixels.length; i += step) {
                const v = pixels[i];
                if (v > observedMax) observedMax = v;
            }
            const wallThreshold = Math.floor(maxVal * 0.99);
            const satThreshold = (observedMax >= wallThreshold && observedMax < maxVal)
                ? observedMax : maxVal;
            // Second pass: collect non-saturated samples.
            const sampleArr = [];
            for (let i = 0; i < pixels.length; i += step) {
                const v = pixels[i];
                if (v === 0 || v >= satThreshold) continue;
                sampleArr.push(v);
            }
            if (sampleArr.length === 0) {
                // Uniformly saturated / overexposed frame. Set the
                // scale factor so the observed max maps to 1.0,
                // midtone identity (0.5) — result is a near-white
                // canvas, intuitive for "blown out exposure" instead
                // of the old "everything's black" behaviour.
                const whiteVal = observedMax > 0 ? observedMax : maxVal;
                return {
                    shadow: 0,
                    scaleFactor: 1.0 / whiteVal,
                    midtone: 0.5
                };
            }
            const sorted = Float32Array.from(sampleArr).sort();
            const median = sorted[Math.floor(sorted.length * 0.5)];
            const devs = Float32Array.from(sorted, v => Math.abs(v - median)).sort();
            const mad = devs[Math.floor(devs.length * 0.5)];

            const shadow = Math.max(0, median - 3.0 * mad);
            // GraXpert: midtone = MTF(x_med, target_bg) where
            // x_med = (median - shadow) / (maxVal - shadow).
            // Setting target_bg = 0.15 lands the median at 15%
            // gray in the output — matches the AutoStretch.cs +
            // GraXpert default that the editor / thumbnails use.
            const targetBg = 0.15;
            const denom = Math.max(1, maxVal - shadow);
            const xMed = Math.max(0, (median - shadow) / denom);
            let midtone = this._mtf(xMed, targetBg);
            // Clamp tightly so the shader can't divide-by-zero
            // when the midtone falls exactly on the (m-1)x - m
            // singularity at x = m / (2m-1).
            midtone = Math.min(0.999, Math.max(0.001, midtone));

            // Throttled diagnostic — last-resort debugging when the
            // canvas comes out black or the displayed image looks off.
            // Logs once every ~2s so live-stacking doesn't spam.
            const nowMs = performance.now();
            if (!this._stretchLogAt || nowMs - this._stretchLogAt > 2000) {
                this._stretchLogAt = nowMs;
                const minV = sorted[0];
                const maxV = sorted[sorted.length - 1];
                console.log('[Polaris stretch]',
                    `samples=${sorted.length}`,
                    `min=${minV} max=${maxV}`,
                    `median=${median.toFixed(0)} MAD=${mad.toFixed(0)}`,
                    `shadow=${shadow.toFixed(0)}`,
                    `xMed=${xMed.toFixed(4)}`,
                    `midtone=${midtone.toFixed(4)}`);
            }

            return {
                shadow,
                scaleFactor: maxVal > shadow ? 1.0 / (maxVal - shadow) : 1.0,
                midtone
            };
        },

        // Re-render the cached last frame with current stretch settings.
        // Called when sliders move + on tab/videoTab change via $watch.
        applyManualStretch() {
            const f = this._lastRawFrame;
            if (!f) return;
            const { shadow, scaleFactor, midtone } =
                this._computeStretchParams(f.pixels, f.maxVal);
            // Pass the original frameKind so the re-render lands on
            // the SAME canvas the frame first painted into. Default
            // kind=0 here would silently shove PREVIEW snaps onto
            // liveCanvas on every $watch('tab') tick, which was the
            // 'snap appears in LIVE too' regression after the kind
            // routing fix.
            this._tryRenderWebGL(f.pixels, f.width, f.height, f.bitDepth,
                f.bayerPattern, shadow, scaleFactor, midtone, f.frameKind || 0);
        },

        // Re-render the cached frame with whatever wb / stretch state
        // is current. Used by the WB sliders + auto-WB so a control
        // change updates the canvas immediately without waiting for
        // the next capture. Same plumbing as applyManualStretch.
        applyWb() { this.applyManualStretch(); },

        // Gray-world auto white-balance from the current raw frame.
        // Walks the Bayer mosaic, sums R / G / B contributions, sets
        // wb.r = mean(G) / mean(R) and wb.b = mean(G) / mean(B) so
        // the channel means equalise. Sub-sampled (every 16 px) so
        // it runs in ~5ms even on a 24 MP master. Returns true on
        // success, false when the algorithm couldn't compute (mono
        // sensor, all-zero frame, etc — leaves wb untouched).
        _computeAutoWB(pixels, width, height, bayerPattern) {
            // Mono sensors (BayerPattern = None / 0) don't have R/G/B
            // separation to balance. Same for malformed inputs.
            if (!bayerPattern || bayerPattern < 1 || bayerPattern > 4) return false;
            if (!pixels || width < 4 || height < 4) return false;

            let sumR = 0, sumG = 0, sumB = 0;
            let nR = 0, nG = 0, nB = 0;
            // Walk 2x2 super-pixels with stride. stride=8 gives one
            // sample per 16×16 pixel patch — enough to converge on
            // gray-world without iterating every pixel.
            const stride = 8;
            for (let y = 0; y + 1 < height; y += 2 * stride) {
                for (let x = 0; x + 1 < width; x += 2 * stride) {
                    const p00 = pixels[y * width + x];
                    const p10 = pixels[y * width + x + 1];
                    const p01 = pixels[(y + 1) * width + x];
                    const p11 = pixels[(y + 1) * width + x + 1];
                    // Skip pure-black super-pixels (subframe edges,
                    // unfilled live-stack accumulator cells). Without
                    // this a frame with a big dark border would drag
                    // every channel mean toward zero and explode the
                    // gain ratios.
                    if (p00 === 0 && p10 === 0 && p01 === 0 && p11 === 0) continue;
                    switch (bayerPattern) {
                        case 1: // RGGB
                            sumR += p00; nR++;
                            sumG += p10 + p01; nG += 2;
                            sumB += p11; nB++;
                            break;
                        case 2: // BGGR
                            sumB += p00; nB++;
                            sumG += p10 + p01; nG += 2;
                            sumR += p11; nR++;
                            break;
                        case 3: // GBRG
                            sumG += p00 + p11; nG += 2;
                            sumB += p10; nB++;
                            sumR += p01; nR++;
                            break;
                        case 4: // GRBG
                            sumG += p00 + p11; nG += 2;
                            sumR += p10; nR++;
                            sumB += p01; nB++;
                            break;
                    }
                }
            }
            if (nR === 0 || nG === 0 || nB === 0) return false;
            const meanR = sumR / nR;
            const meanG = sumG / nG;
            const meanB = sumB / nB;
            // Guard against division explosion on (nearly) black
            // channels. Below ~10 ADU is noise floor; computing a
            // gain from that produces unhelpful 200× numbers.
            if (meanR < 10 || meanB < 10 || meanG < 10) return false;
            // Clamp the gain to a sane operating window. 0.3..3.0
            // covers every realistic OSC daylight + nightscape;
            // anything outside is probably bogus.
            const newR = Math.min(3.0, Math.max(0.3, meanG / meanR));
            const newB = Math.min(3.0, Math.max(0.3, meanG / meanB));
            this.wb.r = newR;
            this.wb.b = newB;
            return true;
        },

        // Manual "Auto WB now" button handler. Recomputes against the
        // last raw frame + immediately re-renders so the user sees
        // the result without waiting for the next capture.
        autoWbNow() {
            const f = this._lastRawFrame;
            if (!f) {
                this.toast('No frame yet — take a snap first.', 'warn');
                return;
            }
            const ok = this._computeAutoWB(f.pixels, f.width, f.height, f.bayerPattern);
            if (ok) {
                this.applyWb();
                this.persistWb();
                this.toast('White balance: R=' + this.wb.r.toFixed(2)
                    + ', B=' + this.wb.b.toFixed(2), 'ok');
            } else {
                this.toast('Could not compute auto WB (mono sensor or '
                    + 'empty frame).', 'warn');
            }
        },

        // Reset display WB to the neutral defaults that work for most
        // daylight OSC captures (1.7 / 1.5 — calibrated against
        // ZWO / QHY OSC sensors with no IR cut on a typical scope).
        resetWb() {
            this.wb.r = 1.7;
            this.wb.b = 1.5;
            this.wb.auto = false;
            this.applyWb();
            this.persistWb();
        },

        // Persist wb to localStorage so the setting survives reloads.
        // Storage key matches the other UI settings.
        persistWb() {
            try {
                localStorage.setItem('polaris-wb', JSON.stringify({
                    r: this.wb.r, b: this.wb.b, auto: !!this.wb.auto
                }));
            } catch { /* private mode / quota */ }
        },

        // Load saved WB from localStorage. Called from _initCore on
        // boot so user choices stick. Defaults preserved on missing /
        // malformed storage.
        loadWb() {
            try {
                const raw = localStorage.getItem('polaris-wb');
                if (!raw) return;
                const j = JSON.parse(raw);
                if (typeof j.r === 'number' && j.r > 0) this.wb.r = j.r;
                if (typeof j.b === 'number' && j.b > 0) this.wb.b = j.b;
                if (typeof j.auto === 'boolean') this.wb.auto = j.auto;
            } catch { /* malformed; leave defaults */ }
        },

        // ----- WebGL renderer (debayer + MTF stretch on GPU) -----
        // State held on the Alpine instance so it survives across frames.
        // _gl, _glProgram, _glLocs, _glTexture, _glCanvas

        _initWebGL() {
            if (this._gl) return true;
            // Offscreen canvas so the WebGL output isn't tied to one
            // visible panel. liveCanvas, previewCanvas, videoCanvas
            // etc. are pure 2D display targets; the GPU writes here
            // once and the fan-out helper drawImage-blits to whichever
            // canvas the current FrameKind owns. Detached from DOM so
            // it has a stable backing store regardless of which tab
            // is mounted/hidden.
            const canvas = document.createElement('canvas');
            canvas.width = 1;
            canvas.height = 1;
            this._glCanvas = canvas;
            // preserveDrawingBuffer:true is required so we can drawImage(this offscreen, ...)
            // onto secondary canvases AFTER WebGL rendered. Without it the
            // browser is allowed to clear the buffer between the gl.drawArrays
            // call and the fan-out drawImage, leaving the colour-debayered
            // bitmap as fully transparent on the display targets.
            const gl = canvas.getContext('webgl2', {
                antialias: false,
                premultipliedAlpha: false,
                preserveDrawingBuffer: true
            });
            if (!gl) {
                console.info('WebGL2 not available, falling back to CPU stretch');
                return false;
            }

            // Vertex shader: clip-space quad.
            //
            // Important: the y-flip is done on gl_Position (negate
            // a_pos.y), NOT on v_uv. The fragment shader's Bayer
            // debayer snaps each output pixel to a 2x2 source cell
            // via floor(v_uv * texSize/2) * 2; if we flipped v_uv
            // instead, an even-height texture (e.g. 2192 rows) would
            // shift every 2x2 cell by one source row, turning an
            // RGGB sensor into a GBRG read at the shader level.
            // That gave a heavy green/cyan colour cast on OSC
            // cameras (e.g. ZWO ASI715MC). Flipping the position
            // instead leaves texel (0,0) of the upload mapping to
            // screen-top-left, so the Bayer pattern enum the server
            // sent us applies directly.
            const vs = `#version 300 es
                in vec2 a_pos;
                out vec2 v_uv;
                void main() {
                    v_uv = (a_pos.xy + vec2(1.0)) * 0.5;
                    gl_Position = vec4(a_pos.x, -a_pos.y, 0.0, 1.0);
                }`;

            // Fragment shader: sample uint16 R channel + optional 2x2 debayer + MTF stretch.
            // Bayer pattern encoding MUST match NINA.Core.Enum.BayerPatternEnum:
            //   0 = None (mono), 1 = RGGB, 2 = BGGR, 3 = GBRG, 4 = GRBG
            // (3 and 4 were previously swapped in this shader relative
            // to the C# enum, the GBRG/GRBG case fell through to the
            // wrong colour assignment.)
            const fs = `#version 300 es
                precision highp float;
                precision highp usampler2D;
                uniform usampler2D u_tex;
                uniform vec2 u_texSize;
                uniform float u_shadow;
                uniform float u_scale;
                uniform float u_mtf;   // typically 0.25
                uniform int u_bayer;   // 0=mono 1=RGGB 2=BGGR 3=GBRG 4=GRBG
                uniform float u_wbR;   // red channel gain  (default 1.7 for daylight OSC)
                uniform float u_wbB;   // blue channel gain (default 1.5 for daylight OSC)
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
                    // PixInsight MTF: y = (m-1)*x / ((2m-1)*x - m)
                    // Properties: f(0;m)=0, f(1;m)=1, f(m;m)=0.5.
                    // m=0.5 is identity; m<0.5 stretches shadows
                    // (typical DSO preset is m≈0.15..0.30).
                    //
                    // Previous shader used (m*x) / ((m-1)x - m + 1)
                    // which is a different (broken) parameterisation,
                    // f(0.5; 0.5) came out as 1.0 instead of 0.5, so
                    // the midtone was effectively ignored and the
                    // image looked dim + pale.
                    float denom = (2.0 * u_mtf - 1.0) * n - u_mtf;
                    return ((u_mtf - 1.0) * n) / (denom - 1e-12);
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
                    else if (u_bayer == 3) { g = 0.5 * (p00 + p11); b = p10; r = p01; }   // GBRG
                    else /* 4 GRBG */      { g = 0.5 * (p00 + p11); r = p10; b = p01; }
                    // Stretch FIRST, then apply per-channel white
                    // balance. Earlier this multiplied R and B by 1.7
                    // / 1.5 before stretch, which on a near-uniform
                    // bias / dark frame (all pixels at the noise floor
                    // ~ADU 1024) pushed R and B above the auto-stretch
                    // shadow point while G stayed at it -- result was
                    // pure magenta noise instead of grey noise. By
                    // stretching first we let the auto-stretch see the
                    // raw signal and only colour-balance the normalised
                    // output, which clamps cleanly to [0, 1]. Defaults
                    // 1.7 / 1.5 still approximate daylight WB for OSC
                    // sensors (ZWO ASI224/462/715, QHY5III678C, etc.)
                    // when there IS real signal above the noise floor.
                    float sr = stretch(r);
                    float sg = stretch(g);
                    float sb = stretch(b);
                    fragColor = vec4(clamp(sr * u_wbR, 0.0, 1.0),
                                     sg,
                                     clamp(sb * u_wbB, 0.0, 1.0), 1.0);
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
                bayer: gl.getUniformLocation(prog, 'u_bayer'),
                wbR: gl.getUniformLocation(prog, 'u_wbR'),
                wbB: gl.getUniformLocation(prog, 'u_wbB')
            };
            this._glTexture = gl.createTexture();
            console.info('WebGL2 renderer initialised');
            return true;
        },

        _tryRenderWebGL(pixels, width, height, bitDepth, bayerPattern, shadow, scaleFactor, midtone, frameKind = 0) {
            if (!this._initWebGL()) return false;
            const gl = this._gl;
            // GPU surface is an OFFSCREEN canvas, not the LIVE display.
            // _initWebGL() creates it once and attaches the WebGL2
            // context to it. Rendering used to target liveCanvas
            // directly, which meant a PREVIEW snap (kind=1) painted
            // over the live-stack accumulator's display canvas — the
            // user would see preview content on the LIVE tab after
            // a single tap on PREVIEW. With the offscreen separation
            // we render once, then drawImage out to whichever canvas
            // the FrameKind targets, leaving liveCanvas untouched
            // unless the frame is actually a Live one.
            const canvas = this._glCanvas;
            if (!canvas) return false;

            // Always render at SOURCE resolution into liveCanvas regardless
            // of whether the LIVE tab is currently visible. liveCanvas is
            // our "GPU output", we then drawImage() it onto whichever
            // visible canvas the user is looking at (PREVIEW / VIDEO /
            // FOCUS) via the fan-out helper. Scaling for display happens
            // there. Previous version bailed when LIVE was hidden, which
            // forced everything onto the 2D fallback path, and the 2D
            // fallback has no debayer, so OSC colour cameras rendered as
            // grayscale (or a Bayer dot pattern) and the video tab stayed
            // black entirely whenever the WASM stacker rejected frames.
            //
            // Cap the GPU render size to avoid uploading absurd buffers
            // when a 6000×4000 sensor lands, fan-out scaling handles
            // visual fidelity beyond ~2048 anyway.
            const MAX_GPU_DIM = 2048;
            const renderScale = Math.min(MAX_GPU_DIM / width, MAX_GPU_DIM / height, 1);
            canvas.width = Math.max(1, Math.round(width * renderScale));
            canvas.height = Math.max(1, Math.round(height * renderScale));

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
            // Midtone parameter for the PixInsight MTF in the
            // fragment shader. Caller supplies it (auto-stretch
            // path computes from median+MAD using GraXpert's
            // "15% Bg" preset; manual path uses the user slider).
            // Fall back to 0.25 if the caller didn't pass one,
            // keeps any legacy call sites safe.
            const mtfParam = (typeof midtone === 'number' && isFinite(midtone))
                ? Math.min(0.999, Math.max(0.001, midtone))
                : Math.min(0.999, Math.max(0.001, this.stretchMid || 0.25));
            gl.uniform1f(this._glLocs.mtf, mtfParam);
            gl.uniform1i(this._glLocs.bayer, bayerPattern | 0);
            // Per-channel WB gain. Defaults give a roughly neutral
            // daylight look on raw OSC data; users can tune via the
            // existing WB Red / WB Blue sliders in VIDEO (and soon
            // in PREVIEW). Server-side WB writes via /api/camera/
            // white-balance still happen too, these multipliers
            // stack on top for client-side preview correction.
            // Shared WB state (this.wb.{r,b}). previewWbR / previewWbB
            // were the legacy fallback names — kept here as a final
            // safety net so any legacy debug poke from the console
            // still works, but the canonical source is wb.{r,b}.
            gl.uniform1f(this._glLocs.wbR,
                this.wb?.r ?? this.previewWbR ?? 1.7);
            gl.uniform1f(this._glLocs.wbB,
                this.wb?.b ?? this.previewWbB ?? 1.5);

            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);

            // One-shot diagnostic, captures the GL canvas dims +
            // any pending error after the first real frame so we
            // can confirm in DevTools that the GPU side actually
            // ran (vs the bitmap being lost between drawArrays and
            // the fan-out drawImage).
            if (!this._loggedWebGLRender) {
                this._loggedWebGLRender = true;
                const err = gl.getError();
                console.log('[Polaris] WebGL render OK · liveCanvas=' + canvas.width + 'x' + canvas.height
                    + ' · srcTex=' + width + 'x' + height + ' · glError=0x' + err.toString(16));
            }

            // GPU bitmap is ready in the offscreen canvas. Fan it out
            // ONLY to canvases that belong to this frame's panel
            // (kind=0 Live → liveCanvas, kind=1 Preview → previewCanvas,
            // etc.). Without this kind gate every WebGL frame would
            // bleed into every other panel.
            this._fanOutFrameToCanvases(canvas, canvas.width, canvas.height, frameKind);
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
                //, the null-check on update.LiveStackComputeMode
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

        // LIVE tab "Save each frame" toggle. Hits the dedicated
        // endpoint which writes both the runtime flag (LiveStackingService.
        // SaveFramesToDisk, takes effect on the next AddFrameAsync call)
        // and the active rig's persisted LiveStackSaveFramesToDisk
        // field so the choice survives a restart. We also mirror it
        // onto the local rig object so an immediate rig switch +
        // switch-back reflects correctly without a profile re-fetch.
        async saveLiveStackSaveFrames() {
            try {
                await this.apiPost('/api/livestack/save-frames', null, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ enabled: this.liveStackSaveFrames })
                });
                const rig = this.rigs.find(r => r.id === this.activeRigId);
                if (rig) rig.liveStackSaveFramesToDisk = this.liveStackSaveFrames;
                this.toast(this.liveStackSaveFrames
                    ? 'Saving each live-stack frame to disk'
                    : 'Live-stack frames no longer saved', 'ok');
            } catch (e) {
                // Revert the checkbox if the server rejected it so the
                // UI doesn't lie about the actual state.
                this.liveStackSaveFrames = !this.liveStackSaveFrames;
                this.toast('Save failed: ' + (e.message || e), 'error');
            }
        },

        // PUT the live-stack auto-pause cap. UI value is in minutes
        // (friendlier), backend stores seconds. 0 minutes = unlimited.
        // Server returns the clamped value so we can echo it back if
        // someone sneaks a negative into the input.
        async saveLiveStackMaxDuration() {
            const mins = Math.max(0, this.liveStackMaxMinutes || 0);
            const secs = Math.round(mins * 60);
            try {
                await this.apiPost('/api/livestack/max-duration', null, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ seconds: secs })
                });
                const rig = this.rigs.find(r => r.id === this.activeRigId);
                if (rig) rig.liveStackMaxDurationSeconds = secs;
                this.toast(secs === 0
                    ? 'Live stacking will run until you reset it'
                    : `Live stack will auto-pause after ${mins} min`, 'ok');
            } catch (e) {
                this.toast('Save failed: ' + (e.message || e), 'error');
            }
        },

        // Render the current stack's elapsed time as "MM:SS" (when
        // under an hour) or "HH:MM" (when longer). Reads off
        // liveStackStatus.elapsedSeconds which the server updates
        // on every status broadcast (~1 Hz).
        formatLiveStackElapsed() {
            const s = Math.max(0, Math.floor(this.liveStackStatus?.elapsedSeconds || 0));
            if (s < 3600) {
                const m = Math.floor(s / 60);
                const r = s % 60;
                return `${m}:${r.toString().padStart(2, '0')}`;
            }
            const h = Math.floor(s / 3600);
            const m = Math.floor((s % 3600) / 60);
            return `${h}h${m.toString().padStart(2, '0')}`;
        },

        // CLST-6: upload the WASM-accumulated stack to the server as a
        // FITS. Reads the latest cached raw frame for dimensions +
        // metadata; the actual pixels come from the WASM module's
        // GetStackedResult (NOT the cached frame, those might be a
        // single frame's worth, not the accumulator).
        async saveClientStack() {
            if (!this.wasmReady) {
                this.toast('WASM not ready yet, wait for the live-stack module to load.', 'warn');
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
                // XFER: apiUpload via XHR so the activity bar shows
                // upload progress for the multi-MB stacked buffer
                // (24Mpix mono 16-bit ~46 MB; RGB triples it).
                const resp = await this.apiUpload(url, bytes, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/octet-stream' },
                    label: 'Save stack (' + target + ')'
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
            // copy, JS just reinterprets the buffer view).
            const asInt32 = new Int32Array(pixels.length);
            for (let i = 0; i < pixels.length; i++) asInt32[i] = pixels[i];

            // (Re-)initialise the accumulator whenever the incoming
            // frame's dimensions don't match what's already cached.
            // The WASM Interop has no separate Initialize() — AddFrame
            // itself auto-bootstraps the buffers on the first frame
            // (frameCount==0), so all we need to do here is Reset()
            // to drop the previous-resolution buffers + reference
            // stars. Without this, a snap captured at a different
            // resolution than the previous video stream would land in
            // an AddFrame that silently rejects (frame-size mismatch
            // branch) and the renderer would loop on a stale buffer.
            const expectedLen = width * height;
            if (this._wasmInitDims?.w !== width
                || this._wasmInitDims?.h !== height) {
                try {
                    interop.Reset();
                    this._wasmInitDims = { w: width, h: height };
                } catch (e) {
                    console.warn('[Polaris] WASM Reset failed:', e);
                    return pixels;   // bail to raw frame
                }
            }

            const metrics = interop.AddFrame(asInt32, width, height);
            // SNR-5: packed return is now int[7]; slots 5 + 6 carry
            // per-frame SNR and cumulative SNR × 100 so the server's
            // LiveStackingService can populate LastFrameSnr /
            // CumulativeSnr / ETA in MetricsOnly (WASM) mode too.
            const [frameCount, hfrX100, starCount, alignmentOk, _reserved,
                   frameSnrX100, cumSnrX100] = metrics;

            // Send the metrics back to the server so the trigger
            // orchestrator (LiveStackTriggersService) sees the same
            // numbers it'd get from server-side StarDetector, and the
            // SNR fields feed the LIVE overlay + ETA widget without
            // the server having to re-process the accumulator itself.
            if (this.imageWs && this.imageWs.readyState === WebSocket.OPEN) {
                this._wsSendTracked(this.imageWs, JSON.stringify({
                    type: 'client-stack-progress',
                    frameCount,
                    hfr: hfrX100 / 100,
                    starCount,
                    alignmentOk: !!alignmentOk,
                    frameSnr: frameSnrX100 / 100,
                    cumulativeSnr: cumSnrX100 / 100
                }));
            }

            // If the stacker didn't actually integrate this frame
            // (frameCount didn't tick, alignment failed, no stars
            // detected, frame rejected), the accumulator is still
            // empty / unchanged. Returning GetStackedResult here
            // would hand back a zero-filled buffer the right size,
            // and the outer renderer would happily paint a black
            // canvas over the user's actual scene. Falling back to
            // the raw incoming pixels keeps short-exposure planetary
            // video, snap previews of dim fields, and any other
            // "stacker can't use this frame" case visible. Live
            // stacking still works because frameCount > 0 once the
            // first usable frame lands.
            if (!frameCount || frameCount <= 0) {
                return pixels;
            }

            // Pull the accumulated stack out for display. The
            // GetStackedResult export returns int[] (JSExport doesn't
            // marshal ushort[]); widen back to Uint16Array via the
            // low 16 bits. If the returned buffer doesn't match the
            // expected pixel count (init race, marshalling glitch,
            // single-frame snap that the stacker hasn't seen), fall
            // back to the raw incoming pixels so the canvas isn't
            // left empty.
            const stackedInt32 = interop.GetStackedResult();
            if (!stackedInt32 || stackedInt32.length !== expectedLen) {
                return pixels;
            }
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
            // FrameKind. The HEADER (the 24-byte block emitted by
            // ImageBuffer.GetStreamHeader) lays its 6 int32 fields
            // at relative offsets 0/4/8/12/16/20. BUT the WIRE
            // payload received here is [4B headerLen][24B header]
            // [LZ4 payload], so every header field lives at
            // arrayBuffer offset (4 + header_offset). FrameKind's
            // header offset is 20 -> arrayBuffer offset is 24.
            //
            // Two earlier attempts got this wrong:
            //  - Original code read at arrayBuffer offset 24 with
            //    the comment 'header offset 24', which IS correct
            //    by accident (the arrayBuffer offset, not the
            //    header offset).
            //  - A subsequent 'fix' moved the read to arrayBuffer
            //    offset 20, claiming the header layout puts kind
            //    there. Header offset 20 is right -- but arrayBuffer
            //    offset 20 is the UncompressedSize field! Result:
            //    frameKind always equalled uncompressedSize (a huge
            //    number that switch-case fell through to default),
            //    so every PREVIEW snap landed on liveCanvas.
            //    Symptom: 'o frame do preview esta aparecendo no
            //    live stack e nao no preview'.
            //
            // Correct read: arrayBuffer offset 24, gated on the
            // full 24-byte header being present (headerLen >= 24).
            // Need byteLength >= 28 to safely read those 4 bytes.
            // 0 = Live, 1 = Preview, 2 = Focus, 3 = Video, 4 = SlewPreview.
            const frameKind = (headerLen >= 24 && arrayBuffer.byteLength >= 28)
                ? dv.getInt32(24, true) : 0;

            // Bail on placeholder / heartbeat frames before they spam
            // the WebGL pipeline. We were seeing periodic 0x0 frames
            // arrive over /ws/image-stream, likely a service-side
            // empty broadcast (slew-preview kicking off, live-stack
            // accumulator reset, etc.). Renderer would faithfully
            // upload an empty texture, fan out a 0-sized bitmap,
            // and end up clearing every visible canvas. Skip silently.
            if (width <= 0 || height <= 0 || uncompressedSize <= 0) {
                return;
            }

            // LZ4 decompression requires lz4.min.js, fallback to REST JPEG if unavailable
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

            // Some native video streams (ZWO ASI under indi_asi_ccd at
            // 8-bit FITS, for example) advertise BITPIX=16 in the
            // stream header but only ever fill the low byte, every
            // pixel reads <= 255 against a maxVal of 65535, the MTF
            // stretch collapses everything to ~0, and the canvas is
            // black. Probe a stride sample on the first few frames
            // after a mismatched-bitdepth handoff and clamp maxVal
            // down to the actual dynamic range when it's wildly off.
            // Costs ~5µs per frame.
            let maxVal = (1 << bitDepth) - 1;
            const probeStride = Math.max(1, (pixels.length / 4096) | 0);
            let observedMax = 0;
            for (let i = 0; i < pixels.length; i += probeStride) {
                if (pixels[i] > observedMax) observedMax = pixels[i];
            }
            if (observedMax > 0 && observedMax < (maxVal >>> 1)) {
                // Round up to the nearest power-of-two boundary so the
                // stretch math has a sensible normaliser. Stops at 16
                // because anything dimmer than that is probably a true
                // black frame, not a bit-depth mismatch.
                let fitted = 255;
                while (fitted < observedMax * 2 && fitted < 65535) fitted = (fitted << 1) | 1;
                maxVal = fitted;
            }

            // Periodic diagnostic, one line per ~30 frames so we can
            // confirm in DevTools what the WS pipeline is actually
            // delivering when something looks off. Cheap.
            this._rawFrameCounter = (this._rawFrameCounter || 0) + 1;
            if ((this._rawFrameCounter % 30) === 1) {
                console.log('[Polaris] raw frame #' + this._rawFrameCounter
                    + ' · ' + width + 'x' + height + ' · bitDepth=' + bitDepth
                    + ' · observedMax=' + observedMax + ' · effectiveMaxVal=' + maxVal
                    + ' · bayer=' + bayerPattern);
            }

            // CLST-4: when the server tells us it's in MetricsOnly
            // mode and our WASM module is loaded, route this frame
            // through the WASM stacker instead of treating it as the
            // displayable result. WASM accumulates → we display its
            // running mean. While the server stays in Full mode the
            // raw frames it relays are ALREADY the accumulated stack,
            // so feeding them to WASM again would compound, only run
            // the WASM path when the server is opted into metrics-only.
            //
            // Additionally gate on liveStackRunning, without it, a
            // WASM-capable client would route EVERY frame through the
            // accumulator (snap previews, video stream frames, focus
            // captures) even when the user isn't live-stacking. That
            // turned the VIDEO tab into a black canvas because the
            // star matcher rejects every short-exposure planetary
            // frame, leaving the accumulator empty.
            const serverMode = this.liveStackStatus?.mode || 'full';
            // frameKind=1 (PREVIEW snap, FOCUS Manual, etc.) means the
            // server already decided this frame is a one-off test shot
            // and must NOT feed the stacker. Without this gate, a tap
            // on PREVIEW would bump the always-on stack counter and
            // poison the running mean with a frame the user only
            // wanted to glance at.
            if (this.wasmReady && serverMode === 'metricsonly'
                && this.liveStackEnabled
                && frameKind === 0
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
            //
            // Keep frameKind in the cache. Without it, applyManualStretch
            // re-renders the cached frame with kind=0 (default arg) ->
            // the bitmap blits onto liveCanvas. Symptom: tap a PREVIEW
            // snap, switch tabs (triggers $watch('tab') -> applyManualStretch),
            // and the preview frame leaks onto the LIVE canvas because
            // the re-render forgot which panel originally owned it.
            this._lastRawFrame = { pixels, width, height, bitDepth, bayerPattern, maxVal, frameKind };

            // Auto WB: when enabled, recompute gain ratios from the
            // current frame at most every ~2s so we don't burn CPU
            // re-balancing every video frame. The shader picks up the
            // updated wb.r / wb.b the next time uniforms are set,
            // which happens immediately below in _tryRenderWebGL.
            if (this.wb?.auto && bayerPattern) {
                const tNow = performance.now();
                if (!this._wbLastAutoAt || tNow - this._wbLastAutoAt > 2000) {
                    this._wbLastAutoAt = tNow;
                    if (this._computeAutoWB(pixels, width, height, bayerPattern)) {
                        this.persistWb();
                    }
                }
            }

            const { shadow, scaleFactor, midtone } =
                this._computeStretchParams(pixels, maxVal);

            // Try WebGL2 path first (GPU does debayer + stretch in microseconds)
            if (this._tryRenderWebGL(pixels, width, height, bitDepth,
                    bayerPattern, shadow, scaleFactor, midtone, frameKind)) {
                return;
            }

            // Build a native-resolution offscreen bitmap once, then fan
            // out to every visible canvas. Previously this drew only
            // into liveCanvas, which is display:none whenever the user
            // is on PREVIEW / FOCUS / VIDEO, its container had 0
            // width, canvas got sized 0×0, image disappeared. By
            // rendering to an offscreen first and then drawImage()ing
            // into each target, whichever tab is open shows the frame.
            const offscreen = document.createElement('canvas');
            offscreen.width = width;
            offscreen.height = height;
            const oCtx = offscreen.getContext('2d');
            const imgData = oCtx.createImageData(width, height);
            const data = imgData.data;
            for (let i = 0; i < pixels.length; i++) {
                const normalized = Math.max(0, (pixels[i] - shadow) * scaleFactor);
                const mtf = normalized > 0 ? (normalized * 0.25) / ((0.25 - 1) * normalized + 1) : 0;
                const val = Math.min(255, Math.round(mtf * 255));
                const j = i * 4;
                data[j] = val;
                data[j + 1] = val;
                data[j + 2] = val;
                data[j + 3] = 255;
            }
            oCtx.putImageData(imgData, 0, 0);

            this._fanOutFrameToCanvases(offscreen, width, height, frameKind);
            this.redrawOverlay();
        },

        // Shared helper used by the raw + JPEG render paths. Draws the
        // source bitmap (an HTMLCanvasElement or HTMLImageElement) into
        // every known display canvas, sizing each from its OWN visible
        // parent. Skips canvases whose parent is collapsed (display:
        // none on a hidden tab), the next tab switch will pick up the
        // bitmap via the existing mirror call.
        // Map a server-tagged FrameKind to the canvas IDs that panel
        // owns. Keeps streams isolated — a PREVIEW snap no longer
        // overwrites the LIVE canvas, an autofocus exposure doesn't
        // bleed into VIDEO, etc. Live (default / unknown) keeps the
        // legacy single liveCanvas target; everything else is panel-
        // scoped.
        _canvasIdsForFrameKind(kind) {
            switch (kind | 0) {
                case 1:  return ['previewCanvas'];                          // Preview
                case 2:  return ['focusCanvas', 'manualFocusCanvas'];       // Focus
                case 3:  return ['videoCaptureCanvas'];                     // Video
                case 4:  return ['slewPreviewCanvas'];                      // SlewPreview
                case 0:
                default: return ['liveCanvas'];                             // Live
            }
        },

        _fanOutFrameToCanvases(src, srcW, srcH, frameKind = 0) {
            const targets = this._canvasIdsForFrameKind(frameKind);
            const skipLive = (src && src.id === 'liveCanvas');   // src IS liveCanvas → don't blit-to-self
            // Diagnostic accumulator. ALWAYS log for non-Live kinds
            // (PREVIEW / FOCUS / VIDEO / SlewPreview) so the user can
            // confirm in DevTools that the routing is happening, then
            // throttle to once-per-60 for Live (which is the steady
            // sequence stream that would otherwise spam the console).
            const isNonLive = (frameKind | 0) !== 0;
            const debugThisCall = isNonLive
                || !this._loggedFanout
                || (++this._fanoutCounter % 60 === 0);
            const report = debugThisCall ? [] : null;
            this._loggedFanout = true;
            this._fanoutCounter = this._fanoutCounter || 0;

            for (const id of targets) {
                if (skipLive && id === 'liveCanvas') {
                    if (report) report.push(id + '=src');
                    continue;
                }
                const canvas = document.getElementById(id);
                if (!canvas) { if (report) report.push(id + '=missing'); continue; }
                const container = canvas.parentElement;
                if (!container) { if (report) report.push(id + '=noparent'); continue; }
                const cw = container.clientWidth;
                const ch = container.clientHeight;
                if (cw <= 0 || ch <= 0) {
                    if (report) report.push(id + '=hidden(' + cw + 'x' + ch + ')');
                    continue;
                }
                const scale = Math.min(cw / srcW, ch / srcH, 1);
                canvas.width  = Math.round(srcW * scale);
                canvas.height = Math.round(srcH * scale);
                try {
                    const ctx = canvas.getContext('2d');
                    ctx.imageSmoothingEnabled = true;
                    ctx.imageSmoothingQuality = 'high';
                    ctx.drawImage(src, 0, 0, canvas.width, canvas.height);
                    if (report) report.push(id + '=drew(' + canvas.width + 'x' + canvas.height + ')');
                } catch (e) {
                    if (report) report.push(id + '=err(' + e.message + ')');
                }
            }
            if (report) {
                console.log('[Polaris] fanout #' + this._fanoutCounter + ' src='
                    + srcW + 'x' + srcH + ' → ' + report.join(' '));
            }
        },

        // Fallback: fetch JPEG preview via REST endpoint. Used when
        // raw mode is in effect but LZ4 isn't loaded or decompression
        // failed. Renders into every visible canvas via the same
        // fan-out helper the binary paths use so PREVIEW / FOCUS /
        // VIDEO all see the frame, not just LIVE.
        //
        // Throttled with a 5s back-off after any 404 so a missing
        // preview endpoint can't generate a 5fps console-spam loop
        // when the video stream is running. (Symptom we hit: 100+
        // failed GETs per second on a fresh page load before LZ4
        // was vendored.)
        _fetchPreviewFallback() {
            if (this._previewFetching) return;
            const now = Date.now();
            if (this._previewBackoffUntil && now < this._previewBackoffUntil) return;
            this._previewFetching = true;

            fetch('/api/image/latest/preview')
                .then(resp => {
                    if (!resp.ok) {
                        // Back off for 5s on any non-OK (typically 404
                        // "No image available" while the server hasn't
                        // produced a frame yet, or before live-stack /
                        // capture has started). Prevents the per-frame
                        // 404 flood we saw in production.
                        this._previewBackoffUntil = Date.now() + 5000;
                        throw new Error('No preview');
                    }
                    return resp.blob();
                })
                .then(blob => {
                    const url = URL.createObjectURL(blob);
                    const img = new Image();
                    img.onload = () => {
                        this._fanOutFrameToCanvases(img, img.width, img.height);
                        this.redrawOverlay();
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
            // Always keep the Home tab's UTC clock alive too, the Sky-tab
            // ticker only fires when that tab is open, but the Home hero
            // wants the time even on first paint.
            this._updateSkyClock();
        },

        updateFov() {
            const { sensorWidth: sw, sensorHeight: sh, focalLength: fl } = this.settings;
            if (fl > 0 && sw > 0 && sh > 0) {
                // Standard small-angle FOV from focal length + sensor:
                //   FOV = 2 * atan(sensor / (2 * focal length))
                // Result in radians, convert to degrees.
                this.fov.width  = 2 * Math.atan(sw / (2 * fl)) * (180 / Math.PI);
                this.fov.height = 2 * Math.atan(sh / (2 * fl)) * (180 / Math.PI);
            }
            // Push the new dimensions into the sky overlay so the
            // rectangle resizes the moment the user edits focal length
            // in the Main Telescope card or a new camera connects.
            // SWE-6: the bridge is queue-safe before its 'ready' message
            // lands, so no readiness gate needed here.
            if (this.tab === 'sky' && typeof this.updateSkyCameraFov === 'function') {
                this.updateSkyCameraFov();
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
                    this.settings.autoConnectOnStartup = !!data.autoConnectOnStartup;
                    // DBGLOG-9: hydrate the persist-to-disk toggle.
                    this.settings.logToDisk = !!data.logToDisk;
                    // Mirror to the logs sub-object so the panel can
                    // surface the current state (informational only).
                    if (this.logs) this.logs.persistToDisk = !!data.logToDisk;
                    this.settings.sirilPath = data.sirilPath || '';
                    this.settings.sirilScriptsDir = data.sirilScriptsDir || '';
                    this.settings.graxpertPath = data.graxpertPath || '';
                    this.settings.graxpertBgeSmoothing = data.graxpertBgeSmoothing ?? 1.0;
                    this.settings.graxpertBgeCorrection = data.graxpertBgeCorrection || 'Subtraction';
                    this.settings.graxpertDeconStrength = data.graxpertDeconStrength ?? 0.5;
                    this.settings.graxpertDeconPsfSize = data.graxpertDeconPsfSize ?? 4.0;
                    this.settings.graxpertDenoiseStrength = data.graxpertDenoiseStrength ?? 0.5;
                    this.settings.onnxModelsPath = data.onnxModelsPath || '';
                    this.settings.onnxLicenseAcknowledged = !!data.onnxLicenseAcknowledged;
                    this.settings.onnxDefaultDenoiseVersion = data.onnxDefaultDenoiseVersion || '2.0.0';
                    this.settings.onnxPreferCli = !!data.onnxPreferCli;
                    // Manifest is small + cheap; fetch on every settings
                    // load so the AI panel reflects the current state.
                    this.loadOnnxManifest();
                    // GX-10: HTTPS info also lives on a low-traffic
                    // endpoint and only changes at server restart, so
                    // piggyback on the settings load instead of
                    // polling. Surfaces URLs + cert fingerprint to
                    // the banner in the AI inference section.
                    this.loadHttpsInfo();
                    // First time the app boots, honour the user's preferred sequencer flavour.
                    if (!this._sequencerTabBootHandled) {
                        this._sequencerTabBootHandled = true;
                        if (this.settings.preferAdvancedSequencer && this.tab === 'home') {
                            // Don't ambush the user, only switch from the initial 'live' tab
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
                this.locSetup.error = 'Browser geolocation API not available.';
                return;
            }
            // iOS Safari + most modern browsers refuse Geolocation on
            // non-HTTPS pages outside of localhost, Polaris over the
            // LAN at http://polaris-app.local hits exactly that wall.
            // Detect proactively so the user sees an actionable error
            // instead of the silent permission-denied callback that
            // iOS produces a few seconds later.
            const isSecure = window.isSecureContext
                || location.protocol === 'https:'
                || ['localhost', '127.0.0.1', '[::1]'].includes(location.hostname);
            if (!isSecure) {
                this.locSetup.error =
                    'Browser location needs HTTPS (or localhost). Type the coordinates ' +
                    'manually below, or search by address. Tip: opening Polaris on ' +
                    'iOS/Android from a phone usually means HTTPS isn\'t set up, ' +
                    'one-tap address search above does the same job.';
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
                    // PERMISSION_DENIED (1), POSITION_UNAVAILABLE (2),
                    // TIMEOUT (3). The default err.message on iOS is
                    // unhelpful; surface the code too.
                    const codes = { 1: 'permission denied', 2: 'position unavailable', 3: 'timeout' };
                    const label = codes[err.code] || 'failed';
                    this.locSetup.error =
                        `Geolocation ${label}. Use the address search above, or type the ` +
                        `coordinates manually below.`;
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

        // ---- Sky Viewer ----
        // SWE-6: d3-celestial dropped. The SKY tab is now a
        // stellarium-web-engine iframe (see wwwroot/sky/). The legacy
        // /js/lib/celestial bundle has been deleted; everything below
        // talks to the engine via postMessage through the bridge in
        // wwwroot/sky/js/sky-bridge.js.

        initSkyViewer() {
            // d3-celestial is gone (SWE-6); the SKY tab now hosts the
            // stellarium-web-engine iframe (#skyFrame) which boots
            // itself from /sky/index.html. What we DO need here is to
            // push observer + time on tab activation: requestAnimationFrame
            // is paused while the browser tab is hidden OR while the
            // user is on another Polaris tab if the browser throttles
            // background iframes (most do). When the user returns the
            // engine's observer.utc is whatever stale value it had
            // before the pause; the next periodic re-sync inside
            // _updateSkyClock only fires every 30 s. Pushing the fresh
            // wall clock right now makes the moon / sun / planets
            // jump to the correct position immediately instead of
            // looking N seconds (or N minutes) behind.
            if (this._skyBridgeReady) {
                this._skyPushObserverAndTime();
            }
            // If the bridge isn't ready yet, the ready handler at
            // line ~4003 already calls _skyPushObserverAndTime on
            // first ready, so we don't need to queue anything here.
        },

        // ---------------------------------------------------------------
        // SWE-1: stellarium-web-engine bridge (postMessage RPC to the
        // /sky/ sub-application iframe).
        //
        // The engine itself lands in SWE-2; this commit only wires the
        // round-trip, message listener that absorbs the bridge's
        // "ready" + a helper to push commands the other way. d3-celestial
        // continues to do the actual rendering until SWE-4 swaps
        // visibility and SWE-6 deletes it.
        //
        // Why a helper instead of inline postMessage calls everywhere:
        // - Centralised guard against the iframe not having loaded yet
        //   (early calls get queued via _skyPending).
        // - Single place to wrap targetOrigin / debug logging if we
        //   tighten security later.
        // ---------------------------------------------------------------

        _skySendMessage(msg) {
            if (!msg || !msg.type) return;
            const frame = document.getElementById('skyFrame');
            if (!frame || !frame.contentWindow) {
                // Iframe not in DOM yet (page still loading). Queue and
                // flush when "ready" arrives.
                this._skyPending = this._skyPending || [];
                this._skyPending.push(msg);
                return;
            }
            if (!this._skyBridgeReady) {
                this._skyPending = this._skyPending || [];
                this._skyPending.push(msg);
                return;
            }
            try {
                frame.contentWindow.postMessage(msg, '*');
            } catch (e) {
                console.warn('[Polaris→Sky] postMessage failed', e);
            }
        },

        // SWE-4: convenience wrappers around _skySendMessage. Each one
        // is one of the documented bridge message types from
        // sky-bridge.js's skyHandleMessage switch.
        _skyPushObserverAndTime() {
            const lat = this.settings.latitude;
            const lng = this.settings.longitude;
            if (typeof lat === 'number' && typeof lng === 'number') {
                this._skySendMessage({ type: 'set-observer', lat, lng });
            }
            this._skySendMessage({ type: 'set-time', utc: Date.now() });
        },

        // Forced re-sync helper. Re-pushes observer + UTC and, if the
        // mount is connected, re-issues the initial look-at. Used by
        // the bridge 'ready' handler at +800ms and +2200ms because the
        // first push can land before the engine's HiPS / skydata
        // pipeline is ready to honour it — symptom is "sky doesn't
        // show the right place on first refresh". Idempotent: if
        // everything already converged, all three calls are no-ops
        // from the engine's perspective.
        _skyForcedResync(delayMs) {
            setTimeout(() => {
                if (!this._skyBridgeReady) return;
                this._skyPushObserverAndTime();
                if (this.mount?.connected
                    && Number.isFinite(this.mount.ra)
                    && Number.isFinite(this.mount.dec)) {
                    this._skyLookAt(this.mount.ra, this.mount.dec, 15);
                } else if (!this._skyDefaultLookAtDone) {
                    // No mount connected — aim at the celestial pole of
                    // the observer's hemisphere. Engine's default points
                    // at RA=0/Dec=0 which on a southern site puts the
                    // camera looking north at high altitude, with the
                    // wrong polar axis visible. User in the SH expects
                    // the south pole (Octans) on first open so they can
                    // start planning without panning manually.
                    //
                    // Dec ±89 (not ±90) avoids the gimbal singularity at
                    // the exact pole; RA=0 is arbitrary at that
                    // declination. Default FOV stays unset so the
                    // engine keeps whatever the user's last zoom level
                    // was, or its built-in default on first boot.
                    //
                    // Guard `_skyDefaultLookAtDone` so the second and
                    // third resync ticks (800ms + 2200ms) don't yank
                    // the camera back if the user has already panned
                    // somewhere else in the meantime.
                    const lat = this.settings?.latitude;
                    if (typeof lat === 'number' && isFinite(lat)) {
                        const poleDec = lat >= 0 ? 89 : -89;
                        this._skyLookAt(0, poleDec);
                        this._skyDefaultLookAtDone = true;
                    }
                }
                this._pushSkyFovOverlays();
            }, delayMs);
        },

        // SWE: push the DSS background visibility to the bridge. Called
        // on the SKY toolbar checkbox change AND right after 'ready'
        // (so the persisted localStorage choice is honoured on reload
        //, the bridge defaults to ON inside its own data-source
        // registration, but if the user had it off we need to push
        // that across).
        _skyToggleDss() {
            this._skySendMessage({ type: 'set-dss-visible', visible: !!this.skyDssVisible });
        },

        // Async search via the engine. Returns Promise<result|null>.
        // result = { name, raDeg, decDeg, magnitude }. Keyed by query
        // so concurrent searches resolve independently.
        _skySearch(query) {
            return new Promise(resolve => {
                this._skySearchPending = this._skySearchPending || {};
                this._skySearchPending[query] = resolve;
                this._skySendMessage({ type: 'search', query });
                // 5s timeout, engine returns sync once ready, but
                // belt-and-suspenders if a queued search gets stuck.
                setTimeout(() => {
                    if (this._skySearchPending && query in this._skySearchPending) {
                        delete this._skySearchPending[query];
                        resolve(null);
                    }
                }, 5000);
            });
        },

        // Aim the engine camera at RA (hours) / Dec (degrees). fovDeg
        // optional. Use this in place of the old Celestial.rotate.
        //
        // Pushes observer + UTC FIRST so the bridge's RA/Dec → Alt/Az
        // conversion uses fresh inputs. Without this, look-ats issued
        // long after page-load (e.g. clicking "Center" on a Tonight
        // card after the user has been parked on another tab for an
        // hour) compute against a stale LST and pan to the wrong patch
        // of sky — sometimes below the horizon, which looks like
        // "Center didn't do anything". Cheap: three postMessage calls,
        // delivered in order to the iframe.
        _skyLookAt(raHours, decDeg, fovDeg, objectName) {
            this._skyPushObserverAndTime();
            // Mark a "programmatic pan in progress" window so the
            // bridge's centre echoes during the smooth-pan animation
            // don't clobber skyTarget.name. The engine's pan typically
            // completes in ~500–1500 ms; 3 s gives the trailing centre
            // events room to land. The 'center' handler reads this
            // timestamp and treats any echo before it as a pan-echo
            // (skyTarget kept verbatim) instead of a user drag.
            this._skyProgrammaticPanUntil = Date.now() + 3000;
            this._skySendMessage({
                type: 'look-at',
                raDeg: (raHours || 0) * 15,
                decDeg: decDeg || 0,
                fovDeg: fovDeg || undefined,
                objectName: objectName || undefined
            });
        },

        // Read back the current map centre. Async, engine replies via
        // 'center' message. Returns Promise<{raDeg,decDeg,fovDeg}|null>.
        _skyGetCenter() {
            return new Promise(resolve => {
                this._skyCenterPending = resolve;
                this._skySendMessage({ type: 'get-center' });
                setTimeout(() => {
                    if (this._skyCenterPending === resolve) {
                        this._skyCenterPending = null;
                        resolve(null);
                    }
                }, 2000);
            });
        },

        _initSkyBridge() {
            if (this._skyBridgeInstalled) return;
            this._skyBridgeInstalled = true;
            this._skyBridgeReady = false;
            this._skyPending = [];

            window.addEventListener('message', (ev) => {
                const msg = ev.data;
                if (!msg || typeof msg !== 'object' || !msg.type) return;
                // Only accept messages that came from our own bridge, by
                // convention every bridge message carries __from === 'sky-bridge'.
                if (msg.__from !== 'sky-bridge') return;

                switch (msg.type) {
                    case 'ready':
                        this._skyBridgeReady = true;
                        this._skyBridgeVersion = msg.version || 'unknown';
                        this._skyEngineLoaded = !!msg.engineLoaded;
                        this._skyEngineMissing = !!msg.engineMissing;
                        console.log('[Polaris] Sky bridge ready v' + this._skyBridgeVersion
                            + ' webgl2=' + msg.webgl2 + ' engineLoaded=' + msg.engineLoaded
                            + (msg.engineMissing ? ' (engine WASM not built, run scripts/build-stellarium-web.sh)' : ''));
                        // Surface a one-time, non-blocking dev toast
                        // when the WASM build hasn't been committed
                        // yet. Production users won't see this, by
                        // SWE-3 the engine is bundled with publish.
                        if (msg.engineMissing && !this._skyEngineMissingToasted) {
                            this._skyEngineMissingToasted = true;
                            this.toast('Sky engine not built yet, run scripts/build-stellarium-web.sh', 'warn', 6000);
                        }
                        // SWE-4: push observer + time BEFORE draining
                        // queued messages. If a queued message is a
                        // look-at, its RA/Dec → Alt/Az conversion
                        // depends on observer.latitude / utc /
                        // longitude being set — otherwise the bridge
                        // silently returns false and the pan never
                        // happens (this was the "Center from Tonight
                        // doesn't centre" bug). Pushing first means
                        // the queued look-at lands on a configured
                        // engine. Replacing the engine's default of
                        // Geneva, 2009 also keeps the sky reflecting
                        // the active site + current UTC immediately.
                        this._skyPushObserverAndTime();
                        // Flush anything queued before the bridge was up.
                        const queued = this._skyPending || [];
                        this._skyPending = [];
                        for (const q of queued) this._skySendMessage(q);
                        // SWE: honour persisted DSS toggle. The bridge
                        // defaults to ON during data-source registration,
                        // so we only need to push a message if the user
                        // turned it OFF previously, but pushing both
                        // ways is harmless and keeps the bridge/UI in
                        // sync deterministically.
                        this._skyToggleDss();
                        // SWE-5: ASIAIR-style initial framing. If the
                        // mount is connected at ready time, centre the
                        // view on mount.ra/dec at FOV=15°. Then seed
                        // skyTarget from the engine's actual current
                        // centre via _skyGetCenter(), this is robust
                        // to (a) mount.connected being false at ready
                        // because the first WS status push hasn't
                        // landed yet, (b) the change-hook not firing
                        // before user interaction, (c) stale skyTarget
                        // values persisted from a previous session.
                        if (this.mount?.connected
                            && Number.isFinite(this.mount.ra)
                            && Number.isFinite(this.mount.dec)) {
                            this._skyLookAt(this.mount.ra, this.mount.dec, 15);
                        }
                        this._skyGetCenter().then(c => {
                            if (c && Number.isFinite(c.raDeg) && Number.isFinite(c.decDeg)) {
                                this.skyTarget = {
                                    name: 'Centre ' + c.raDeg.toFixed(2) + '°,' + c.decDeg.toFixed(2) + '°',
                                    ra: c.raDeg / 15,
                                    dec: c.decDeg
                                };
                                this._pushSkyFovOverlays();
                            }
                        });
                        this._pushSkyFovOverlays();
                        // Forced re-sync after the engine has had time to
                        // settle its data-source pipeline. The first
                        // pushObserverAndTime + lookAt races against
                        // HiPS tile + skydata loading, so on first
                        // refresh the engine sometimes renders with a
                        // stale observer (Geneva 2009) or skips the
                        // initial pan entirely — the symptom the user
                        // sees is "sky doesn't load the right place on
                        // first refresh". Two extra pushes at +800ms
                        // and +2200ms cover both fast and slow loads
                        // (Pi 5 ~600ms ready; Pi 2/3 + cold cache up
                        // to ~2s). Cheap: each is 3-4 postMessage
                        // calls, and the engine ignores no-op observer
                        // updates.
                        this._skyForcedResync(800);
                        this._skyForcedResync(2200);
                        break;
                    case 'webgl-unavailable':
                        this._skyWebGLAvailable = false;
                        console.warn('[Polaris] Sky engine: WebGL2 unavailable, fallback in place');
                        break;
                    case 'search-result':
                        // SWE-4: resolve the pending search promise so
                        // the caller (search box) can update results
                        // without polling. Keyed by query string in
                        // case multiple searches are inflight.
                        if (this._skySearchPending && msg.query in this._skySearchPending) {
                            const cb = this._skySearchPending[msg.query];
                            delete this._skySearchPending[msg.query];
                            try { cb(msg.result); } catch (e) { console.warn(e); }
                        }
                        break;
                    case 'center':
                        if (this._skyCenterPending) {
                            // Reply to an explicit get-center request.
                            const cb = this._skyCenterPending;
                            this._skyCenterPending = null;
                            try { cb(msg.center); } catch (e) { console.warn(e); }
                        }
                        // SWE-5: ASIAIR-style "target rectangle always
                        // at map centre". The bridge fires {fromDrag:true}
                        // on every observer.yaw/pitch change, that
                        // covers user drag AND programmatic look-at
                        // echoes, both of which should update the
                        // planning target to whatever's now centred.
                        // Throttled 10 Hz at the bridge.
                        if (msg.fromDrag && msg.center
                            && Number.isFinite(msg.center.raDeg)
                            && Number.isFinite(msg.center.decDeg)) {
                            const c = msg.center;
                            // _skyLookAt() set a 3 s "programmatic pan
                            // in progress" window. While that window is
                            // open, the centre events arriving are pan
                            // echoes from the engine animating toward
                            // the user-picked target — NOT a genuine
                            // drag. Treat them as such: keep skyTarget
                            // verbatim (especially .name, which was set
                            // to the object name by _populateSkyInfo
                            // milliseconds ago), only refresh the FOV
                            // overlay so the red rectangle tracks the
                            // moving centre. Without this gate, the
                            // intermediate centre frames overwrite
                            // skyTarget.name with "Centre ra,dec" and
                            // Add-to-sequence picks up the coord string
                            // instead of "Moon" / "M31" / etc.
                            const inPan = this._skyProgrammaticPanUntil
                                && Date.now() < this._skyProgrammaticPanUntil;
                            if (!inPan) {
                                this.skyTarget = {
                                    name: 'Centre ' + c.raDeg.toFixed(2) + '°,' + c.decDeg.toFixed(2) + '°',
                                    ra: c.raDeg / 15,
                                    dec: c.decDeg
                                };
                            }
                            // Re-push so the red target rectangle
                            // re-anchors at the new centre. Mount
                            // rectangle stays at mount.ra/dec.
                            this._pushSkyFovOverlays();
                        }
                        break;
                    case 'map-click':
                        if (!Number.isFinite(msg.raDeg) || !Number.isFinite(msg.decDeg)) break;
                        if (msg.object && msg.object.name) {
                            this._populateSkyInfo(msg.object);
                        } else if (msg.objectName) {
                            this._populateSkyInfo({
                                name: msg.objectName,
                                raDeg: msg.raDeg, decDeg: msg.decDeg
                            });
                        } else {
                            // Empty-sky click, close any open card and
                            // stash coords as skyTarget so a follow-up
                            // Slew & Center has somewhere to go.
                            this.skyInfo.visible = false;
                            this.skyTarget = {
                                name: 'Click ' + msg.raDeg.toFixed(2) + ',' + msg.decDeg.toFixed(2),
                                ra: msg.raDeg / 15, dec: msg.decDeg
                            };
                        }
                        break;
                    default:
                        console.log('[Polaris] Sky → unknown:', msg.type, msg);
                }
            });
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

        // Meeus low-precision LST (good to a few seconds, fine for orientation).
        _localSiderealTime(utc, longitudeDeg) {
            const jd = utc.getTime() / 86400000 + 2440587.5;
            const t = (jd - 2451545.0) / 36525;
            let gmst = 280.46061837 + 360.98564736629 * (jd - 2451545.0)
                     + 0.000387933 * t * t - (t * t * t) / 38710000;
            gmst = ((gmst % 360) + 360) % 360;
            const lst = (gmst + longitudeDeg + 360) % 360;
            return lst / 15;
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
            // and rate-limits to 1 req/sec, fine for one-off lookups on
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
                // Offline or blocked, silent fallback to coords.
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
                // The geocode endpoint returns { query, count, results }, NOT a
                // raw array, the older Array.isArray(r) check silently
                // dropped every match. Search "New York" → "No matches"
                // even though the server returned 5 hits.
                this.obsAddressResults = Array.isArray(r?.results) ? r.results : [];
                if (!this.obsAddressResults.length) {
                    this.obsAddressError = 'No matches, try a more specific search (city, state, country).';
                }
            } catch (e) {
                this.obsAddressError = 'Address lookup failed: ' + (e.message || 'unknown error');
            } finally {
                this.obsAddressLoading = false;
            }
        },

        adoptObservatoryResult(r) {
            // Coerce both fields through Number() before toFixed,
            // System.Text.Json sometimes serialises doubles as numbers
            // but custom services have shipped them as strings in the
            // past, and string.toFixed throws.
            const lat = Number(r.latitude) || 0;
            const lon = Number(r.longitude) || 0;
            this.settings.latitude  = Number(lat.toFixed(4));
            this.settings.longitude = Number(lon.toFixed(4));
            this.obsAddressResults  = [];
            this.obsAddressQuery    = r.displayName;
            this.saveSettings();
            this._refreshLocationLabel();
        },

        // Use the browser's Geolocation API. Requires user permission
        // and a secure context (localhost is fine, plain-HTTP LAN
        // hosts are NOT, modern browsers gate this on https://).
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
                        1: 'Permission denied, allow location in the browser address bar.',
                        2: 'Position unavailable. GPS / Wi-Fi positioning may be off.',
                        3: 'Timed out waiting for a location fix.'
                    };
                    this.obsAddressError = map[err.code]
                        || ('Geolocation error: ' + err.message);
                },
                { enableHighAccuracy: true, timeout: 15000, maximumAge: 60000 }
            );
        },

        // ─── STUDIO (post-processing), ST-1 frame browser ───────────────

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

        // ─── Studio tree-view picker ──────────────────────────────────
        // The STUDIO panel is laid out as a 2-column workspace: a
        // collapsible tree on the left grouping frames by Target →
        // Type → Filter, and a single-frame preview pane on the right.
        // These helpers wire that interaction.

        // Build the tree from the flat frame list. Returns:
        //   [{ name, count, types: [{ name, count, filters: [
        //       { name, count, frames: [...] }
        //   ] }] }]
        // Frames with no target land under "Untargeted"; with no
        // filter, under "(no filter)". Sorted alphabetically at every
        // level so the layout is stable across re-renders.
        studioTree() {
            const byTarget = new Map();
            for (const f of (this.studio.frames || [])) {
                const tName = f.target || 'Untargeted';
                const yName = f.imageType || 'OTHER';
                const fName = f.filter || '(no filter)';
                if (!byTarget.has(tName)) byTarget.set(tName, new Map());
                const byType = byTarget.get(tName);
                if (!byType.has(yName)) byType.set(yName, new Map());
                const byFilter = byType.get(yName);
                if (!byFilter.has(fName)) byFilter.set(fName, []);
                byFilter.get(fName).push(f);
            }
            const sortNames = (a, b) => a.name.localeCompare(b.name, undefined, { numeric: true });
            const tree = [];
            for (const [tName, byType] of byTarget) {
                const typesArr = [];
                let tCount = 0;
                for (const [yName, byFilter] of byType) {
                    const filtersArr = [];
                    let yCount = 0;
                    for (const [fName, frames] of byFilter) {
                        // Newest first inside a leaf so the latest
                        // exposure of a run sits at the top.
                        frames.sort((a, b) =>
                            new Date(b.dateObs || 0) - new Date(a.dateObs || 0));
                        filtersArr.push({ name: fName, count: frames.length, frames });
                        yCount += frames.length;
                    }
                    filtersArr.sort(sortNames);
                    typesArr.push({ name: yName, count: yCount, filters: filtersArr });
                    tCount += yCount;
                }
                typesArr.sort(sortNames);
                tree.push({ name: tName, count: tCount, types: typesArr });
            }
            tree.sort(sortNames);
            return tree;
        },

        // Single-click on a tree leaf. Default = make this the
        // active frame in the right preview pane AND replace the
        // multi-selection with just this id. Ctrl/Cmd-click toggles
        // the id in/out of the multi-selection without touching
        // active preview (so batch picks keep building the list).
        // Shift-click is currently treated like Ctrl-click (range
        // select is a follow-up if anyone misses it).
        studioPickFrame(frame, ev) {
            const multi = ev && (ev.ctrlKey || ev.metaKey || ev.shiftKey);
            if (multi) {
                this.studioToggleSelect(frame.id);
                return;
            }
            this.studio.selectedIds = [frame.id];
            this.studio.activeFrame = frame;
            this._studioLoadActivePreview();
        },

        // Resolve the active frame's auto-stretch defaults so the
        // right-pane thumbnail uses the same values the viewer modal
        // would. authUrl appends ?token= for the <img> tag.
        async _studioLoadActivePreview() {
            const f = this.studio.activeFrame;
            if (!f) { this.studio.activePreviewUrl = ''; return; }
            try {
                const a = await this.apiGet(`/api/studio/frames/${f.id}/autostretch`);
                const qs = `?black=${a.black}&mid=${a.mid}&white=${a.white}&maxDim=1024`;
                this.studio.activePreviewUrl = this.authUrl(
                    `/api/studio/frames/${f.id}/preview${qs}`);
            } catch {
                // Fall back to the 256 px thumbnail if /autostretch fails
                // (e.g. corrupt FITS); user can still see SOMETHING.
                this.studio.activePreviewUrl = this.authUrl(
                    `/api/studio/frames/${f.id}/thumb`);
            }
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
        // render request, 150 ms is short enough to feel live, long
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
        // majority IMAGETYP across the selection, usually the user
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
            // is fine, the user typically has a handful of masters.
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
            // Filter selectedIds down to LIGHTs only, calibrating a
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
                            const msg = `Calibration done, ${ok} OK` + (fail > 0 ? `, ${fail} failed` : '');
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

        // ─── ED-7: hand a Library frame to the Editor ──────────────────
        // Looks up the single-selected frame's filesystem path, switches
        // to the EDITOR tab, then calls editorLoad which kicks off the
        // session lifecycle (load → render initial preview + cached
        // original → hydrate sidecar edits if present).
        async studioOpenInEditor() {
            if (this.studio.selectedIds.length !== 1) return;
            const id = this.studio.selectedIds[0];
            const frame = this.studio.frames.find(f => f.id === id);
            if (!frame || !frame.path) {
                this.toast('Frame has no path on disk', 'warn');
                return;
            }
            // UNIF-6: STUDIO tab is gone; this handler may still
            // be called from leftover JS paths (master-completion
            // hooks, etc). Route the editor open through the new
            // FILES > Edit sub-tab instead of the deleted top-level
            // editor tab.
            this.tab = 'files';
            this.setFilesSubTab('edit');
            await this.$nextTick();
            await this.editorLoad(frame.path);
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
                                    `Stack done, ${s.combined} combined` +
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

        // ─── CC-6: channel combine modal (RGB / LRGB / PixelMath) ──
        // Selection bar surface for the mono LRGB workflow's last
        // step before the editor. Modal has three tabs that share
        // the register/normalize toggles + the progress block.

        studioOpenCombineDialog() {
            const frames = this._studioSelectedFrames();
            // Auto-fill the per-tab mappings from the selected frames'
            // FILTER headers. Users who run mono LRGB sequences end up
            // with FITS metadata that already says "R"/"G"/"B"/"L" or
            // "Ha"/"OIII"/"SII", we map by filter == variable name
            // (case-insensitive). Anything that doesn't match stays
            // empty so the user picks it manually from the dropdown.
            const byFilter = {};
            for (const f of frames) {
                const k = (f.filter || '').toUpperCase();
                if (k && !byFilter[k]) byFilter[k] = f.id;
            }
            const pick = (name) => byFilter[name.toUpperCase()] ?? null;
            this.studio.combine.rgb.mapping  = { R: pick('R'), G: pick('G'), B: pick('B') };
            this.studio.combine.lrgb.mapping = {
                R: pick('R'), G: pick('G'), B: pick('B'), L: pick('L'),
            };
            // PixelMath: one row per selected frame, variable name
            // defaults to the filter (or "v1", "v2" ... if unset so
            // the expression at least has SOMETHING to reference).
            this.studio.combine.pm.rows = frames.map((f, i) => ({
                var: (f.filter || `v${i + 1}`),
                frameId: f.id,
            }));
            this.studio.combine.pm.expressions = ['R', 'G', 'B'];
            this.studio.combine.lastJob = null;
            this.studio.combine.activeTab = 'rgb';
            this.studio.combine.open = true;
        },

        studioCloseCombineDialog() {
            this.studio.combine.open = false;
            if (this._studioCombinePoll) {
                clearInterval(this._studioCombinePoll);
                this._studioCombinePoll = null;
            }
        },

        // Helpers: hand the modal the list of currently-selected
        // frames + their thin metadata (id, name, filter) so the
        // mapping <select>s can render without re-querying the
        // backend. Pulled from this.studio.frames which is the live
        // list shown in the studio grid.
        _studioSelectedFrames() {
            const ids = new Set(this.studio.selectedIds || []);
            return (this.studio.frames || []).filter(f => ids.has(f.id));
        },

        studioCombineCanRun() {
            if (this.studio.combine.running) return false;
            switch (this.studio.combine.activeTab) {
                case 'rgb':
                    return this._allFilled(this.studio.combine.rgb.mapping, ['R', 'G', 'B']);
                case 'lrgb':
                    return this._allFilled(this.studio.combine.lrgb.mapping, ['R', 'G', 'B', 'L']);
                case 'pm':
                    if (this.studio.combine.pm.rows.length < 2) return false;
                    const need = this.studio.combine.monoOutput ? 1 : 3;
                    return this.studio.combine.pm.expressions
                        .slice(0, need)
                        .every(e => (e || '').trim().length > 0);
                default: return false;
            }
        },

        _allFilled(mapping, keys) {
            return keys.every(k => mapping[k] != null);
        },

        _studioPmExpressionsCount() {
            // Resize the expressions array to match RGB (3) or mono (1).
            // Pads with empty strings on growth, trims on shrink.
            const need = this.studio.combine.monoOutput ? 1 : 3;
            const cur = this.studio.combine.pm.expressions;
            if (cur.length === need) return cur;
            const defaults = ['R', 'G', 'B'];
            const next = [];
            for (let i = 0; i < need; i++) {
                next.push(cur[i] != null ? cur[i] : defaults[i] || '');
            }
            this.studio.combine.pm.expressions = next;
            return next;
        },

        async studioStartCombine() {
            if (!this.studioCombineCanRun()) return;
            const tab = this.studio.combine.activeTab;
            let body = {
                mode: tab === 'pm' ? 'pixelmath' : tab,
                channelMap: [],
                register: this.studio.combine.register,
                normalize: this.studio.combine.normalize,
            };
            if (tab === 'rgb') {
                for (const v of ['R', 'G', 'B']) {
                    body.channelMap.push({
                        variable: v, frameId: this.studio.combine.rgb.mapping[v],
                    });
                }
            } else if (tab === 'lrgb') {
                for (const v of ['R', 'G', 'B', 'L']) {
                    body.channelMap.push({
                        variable: v, frameId: this.studio.combine.lrgb.mapping[v],
                    });
                }
                body.lrgbAlgo = this.studio.combine.lrgbAlgo;
            } else {
                // PixelMath: emit each row + the expressions list.
                for (const row of this.studio.combine.pm.rows) {
                    if (!row.var || !row.frameId) continue;
                    body.channelMap.push({
                        variable: row.var.trim(), frameId: row.frameId,
                    });
                }
                const need = this.studio.combine.monoOutput ? 1 : 3;
                body.expressions = this.studio.combine.pm.expressions.slice(0, need);
                body.monoOutput = this.studio.combine.monoOutput;
            }

            this.studio.combine.running = true;
            this.studio.combine.lastJob = {
                stage: 'queued', mode: body.mode, done: 0,
                total: body.channelMap.length,
            };
            try {
                const resp = await this.apiPost('/api/studio/combine', body);
                const r = await resp.json();
                this._studioCombinePoll = setInterval(async () => {
                    try {
                        const s = await this.apiGet(`/api/studio/combine/${r.jobId}`);
                        this.studio.combine.lastJob = s;
                        if (!s.inProgress) {
                            clearInterval(this._studioCombinePoll);
                            this._studioCombinePoll = null;
                            this.studio.combine.running = false;
                            if (s.stage === 'done') {
                                this.toast?.(
                                    `Combine done: ${s.outputPath}`, 'ok');
                                this.loadStudio();
                            } else if (s.stage === 'error') {
                                this.toast?.(
                                    'Combine failed: ' + s.error, 'error');
                            }
                        }
                    } catch { /* swallow transient failure */ }
                }, 800);
            } catch (e) {
                this.studio.combine.running = false;
                this.studio.combine.lastJob = {
                    stage: 'error', error: e.message,
                    done: 0, total: body.channelMap.length,
                };
                this.toast?.('Combine start failed: ' + e.message, 'error');
            }
        },

        // ─── CCALB-1/2/3: Color calibration modal ───────────────────
        // Siril-style colour calibration on a single selected RGB
        // master. Three tabs (BG / Manual / PCC) share the modal
        // shell + the progress block, mirroring the Combine modal.

        studioOpenColorCalDialog() {
            const frame = this._studioSelectedFrames()[0];
            if (!frame) return;
            this.studio.colorCal.frameId = frame.id;
            this.studio.colorCal.sourceName = frame.fileName || '';
            // Seed patch defaults to the centre of the frame so the
            // user only has to nudge dimensions, not coordinates,
            // when they enter Manual mode.
            const w = Math.max(64, (frame.width || 1000) >> 4);
            const h = Math.max(64, (frame.height || 1000) >> 4);
            const cx = Math.max(0, ((frame.width || 1000) >> 1) - (w >> 1));
            const cy = Math.max(0, ((frame.height || 1000) >> 1) - (h >> 1));
            this.studio.colorCal.bgPatch    = { x: cx, y: cy, w, h };
            this.studio.colorCal.whitePatch = { x: cx, y: cy, w, h };
            this.studio.colorCal.bgSample = 'auto';
            this.studio.colorCal.activeTab = 'bg';
            this.studio.colorCal.lastJob = null;
            this.studio.colorCal.catalogStatus = null;
            this.studio.colorCal.open = true;
            // CCALB-3c: kick off the PCC pre-flight fetch in the
            // background. Cheap (single GET) + lets the PCC tab
            // render its catalog badge without waiting for the user
            // to click the tab first.
            this.apiGet('/api/studio/colorcal/catalog-status')
                .then(s => { this.studio.colorCal.catalogStatus = s; })
                .catch(() => { /* badge stays in 'unknown' state, fine */ });
        },

        studioCloseColorCalDialog() {
            this.studio.colorCal.open = false;
            if (this._studioColorCalPoll) {
                clearInterval(this._studioColorCalPoll);
                this._studioColorCalPoll = null;
            }
        },

        studioColorCalCanRun() {
            if (this.studio.colorCal.running) return false;
            if (!this.studio.colorCal.frameId) return false;
            const tab = this.studio.colorCal.activeTab;
            if (tab === 'bg') {
                if (this.studio.colorCal.bgSample === 'patch') {
                    const p = this.studio.colorCal.bgPatch;
                    return p && p.w > 0 && p.h > 0;
                }
                return true;
            }
            if (tab === 'manual') {
                const w = this.studio.colorCal.whitePatch;
                if (!w || w.w <= 0 || w.h <= 0) return false;
                if (this.studio.colorCal.bgSample === 'patch') {
                    const p = this.studio.colorCal.bgPatch;
                    if (!p || p.w <= 0 || p.h <= 0) return false;
                }
                return true;
            }
            // PCC: backend gates with an error toast on Run; UI
            // doesn't have enough info to pre-flight (catalog +
            // WCS status come from the server). Let the user click
            // Run and see the error if anything is missing.
            return true;
        },

        async studioStartColorCal() {
            if (!this.studioColorCalCanRun()) return;
            const tab = this.studio.colorCal.activeTab;
            const mode = tab === 'bg' ? 'bg'
                       : tab === 'manual' ? 'manual'
                       : 'pcc';
            const body = {
                frameId: this.studio.colorCal.frameId,
                mode,
                bgSample: this.studio.colorCal.bgSample,
                bgPatch: this.studio.colorCal.bgSample === 'patch'
                    ? this.studio.colorCal.bgPatch
                    : null,
                whitePatch: tab === 'manual'
                    ? this.studio.colorCal.whitePatch
                    : null,
            };
            this.studio.colorCal.running = true;
            this.studio.colorCal.lastJob = { stage: 'queued', mode };
            try {
                const resp = await this.apiPost('/api/studio/colorcal', body);
                const r = await resp.json();
                // ~5-30s for BG/Manual depending on input size.
                this._studioColorCalPoll = setInterval(async () => {
                    try {
                        const s = await this.apiGet(`/api/studio/colorcal/${r.jobId}`);
                        this.studio.colorCal.lastJob = s;
                        if (!s.inProgress) {
                            clearInterval(this._studioColorCalPoll);
                            this._studioColorCalPoll = null;
                            this.studio.colorCal.running = false;
                            if (s.stage === 'done') {
                                let msg = 'Color calibration done';
                                if (s.matchedStars > 0) {
                                    msg += `: ${s.matchedStars} stars matched`;
                                }
                                if (s.gainR != null) {
                                    msg += `, gains R=${s.gainR.toFixed(2)} ` +
                                           `G=${s.gainG.toFixed(2)} B=${s.gainB.toFixed(2)}`;
                                }
                                this.toast?.(msg, 'ok');
                                this.loadStudio();
                            } else if (s.stage === 'error') {
                                this.toast?.(
                                    'Color calibration failed: ' + s.error, 'error');
                            }
                        }
                    } catch { /* swallow transient failure */ }
                }, 800);
            } catch (e) {
                this.studio.colorCal.running = false;
                this.studio.colorCal.lastJob = {
                    stage: 'error', mode, error: e.message,
                };
                this.toast?.('Color calibration start failed: ' + e.message, 'error');
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
        // sensitive, rain looks like rain regardless of the sun's position.
        _weatherIconFor(cloud, prec, isNight = false) {
            const p = (prec || 'none').toLowerCase();
            if (p === 'snow')                       return '🌨️';
            if (p === 'icep' || p === 'frzr')       return '🧊';
            if (p === 'rain' && cloud >= 8)         return '⛈️';
            if (p === 'rain')                       return '🌧️';
            if (isNight) {
                // Unicode doesn't ship "moon-behind-cloud" glyphs in a
                // reliable cross-platform set, so we collapse the partial
                // and mostly-cloudy night buckets into ☁️, at night the
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
                // Day/night classification, use slot midpoint (slot start
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
                // computes everything in UTC under the hood, we feed it
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
                    duskAstroLabel:    sun.night     ? this._fmtLocalTime(sun.night)     : ', ',
                    dawnAstroLabel:    sun.nightEnd  ? this._fmtLocalTime(sun.nightEnd)  : ', ',
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

        // ─── Editor (Lightroom-style; ED-4 server-mode, ED-6 adds WASM) ───
        // The state lives in `editorState`; helpers below own the
        // load/preview/export/sidecar lifecycle. previewUrl is a Blob URL
        // we rotate on every render so the <img> swaps to a fresh src
        // (and we revoke the previous URL to avoid the browser leaking
        // ~1MB per slider tick of dragging).

        editorTabOpened() {
            // Restore prior compute-mode preference (default server).
            // We only honour 'wasm' here if the bundle's actually ready,
            // saved-pref-says-wasm but bundle-not-loaded yet → stay on
            // server until the user explicitly flips it.
            try {
                const saved = localStorage.getItem('nina-editor-compute');
                if (saved === 'wasm' && this.wasmReady) {
                    this.editorState.computeMode = 'wasm';
                } else {
                    this.editorState.computeMode = 'server';
                }
            } catch { /* private mode */ }
            this._editorBindKeyHandlers();
            this._editorBindDragHandlers();
        },

        // Flip between server-mode and WASM-mode. When entering WASM
        // for the first time on a session, lazy-fetch the raw working
        // buffer + hand it to Interop.EditorLoad. Falls back silently
        // to server if anything fails.
        async editorToggleComputeMode() {
            const next = this.editorState.computeMode === 'wasm' ? 'server' : 'wasm';
            this.editorState.computeMode = next;
            try { localStorage.setItem('nina-editor-compute', next); } catch { }

            if (next === 'wasm' && this.editorState.session && !this.editorState.wasmLoaded) {
                await this._editorLoadWasmBuffer();
            }
            // Re-render with the new mode.
            this._editorSchedulePreview();
        },

        async _editorLoadWasmBuffer() {
            if (!this.editorState.session) return;
            if (!globalThis.NINA?.Polaris?.Wasm?.Interop) {
                console.warn('[Editor] WASM bundle not ready, falling back to server');
                this.editorState.computeMode = 'server';
                return;
            }
            try {
                // XFER: apiDownload so the user sees a real progress
                // bar — the raw working buffer is typically 50-200 MB
                // (full-res 8-bit per channel) and used to feel like
                // a freeze on first open.
                const r = await this.apiDownload(
                    '/api/editor/raw/' + this.editorState.session,
                    { label: 'Load editor session' });
                if (!r.ok) throw new Error('HTTP ' + r.status);
                const w  = parseInt(r.headers.get('X-Width')  || '0', 10);
                const h  = parseInt(r.headers.get('X-Height') || '0', 10);
                const ch = parseInt(r.headers.get('X-Channels') || '1', 10);
                const bytes = new Uint8Array(await r.arrayBuffer());
                globalThis.NINA.Polaris.Wasm.Interop.EditorLoad(bytes, w, h, ch);
                this.editorState.wasmLoaded = true;
                this.toast('WASM editor ready (' + Math.round(bytes.length / 1024 / 1024) + ' MB loaded)', 'ok');
            } catch (e) {
                console.warn('[Editor] WASM buffer fetch failed', e);
                this.editorState.computeMode = 'server';
                this.toast('WASM load failed, using server-mode', 'warn');
            }
        },

        async editorLoad(path) {
            if (!path) return;
            this._editorTeardownBlobs();
            this.editorState.loading = true;
            this.editorState.loadingPath = path;
            this.editorState.error = '';
            try {
                const r = await this.apiFetch('/api/editor/load', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ path })
                });
                if (!r.ok) {
                    const e = await r.json().catch(() => null);
                    throw new Error(e?.error || `HTTP ${r.status}`);
                }
                const info = await r.json();
                this.editorState.session = info.sessionId;
                this.editorState.sourcePath = info.sourcePath;
                this.editorState.width = info.width;
                this.editorState.height = info.height;
                this.editorState.channels = info.channels;
                // Hydrate sidecar edits if present, else defaults.
                this.editorState.edits = info.edits || this._editorDefaultEdits();
                this.editorState.dirty = false;
                // Reset zoom/pan + history so each new source starts
                // fresh, undo doesn't reach into a prior session.
                this.editorZoomReset();
                this._editorResetHistory(this.editorState.edits);
                // Mark WASM buffer stale, new source needs a fresh
                // EditorLoad before the next ApplyEdit.
                this.editorState.wasmLoaded = false;
                if (this.editorState.computeMode === 'wasm') {
                    await this._editorLoadWasmBuffer();
                }
                // Render initial preview + an unedited reference for the
                // "Hold to compare" button (always server-mode, gives a
                // pristine reference even when WASM is active).
                await this._editorRenderOriginal();
                this._editorSchedulePreview();
            } catch (e) {
                this.editorState.error = 'Open failed: ' + (e.message || 'unknown');
                this.editorState.session = null;
            } finally {
                this.editorState.loading = false;
                this.editorState.loadingPath = '';
            }
        },

        async editorHandleUpload(file) {
            if (!file) return;
            this.editorState.loading = true;
            this.editorState.error = '';
            try {
                const fd = new FormData();
                fd.append('file', file);
                // XFER: apiUpload via XHR so the activity-bar transfer
                // chip can show real bytes-uploaded progress (fetch()
                // doesn't expose request body progress). _netTx is
                // wired inside apiUpload's upload.onprogress.
                const r = await this.apiUpload('/api/editor/upload', fd, {
                    label: 'Upload ' + (file.name || 'file')
                });
                if (!r.ok) throw new Error(`HTTP ${r.status}`);
                const j = await r.json();
                await this.editorLoad(j.path);
            } catch (e) {
                this.editorState.error = 'Upload failed: ' + (e.message || 'unknown');
            } finally {
                this.editorState.loading = false;
            }
        },

        editorHandleDrop(ev) {
            const f = ev.dataTransfer?.files?.[0];
            if (f) this.editorHandleUpload(f);
        },

        editorReset() {
            if (!this.editorState.session) return;
            this.editorState.edits = this._editorDefaultEdits();
            this.editorState.dirty = true;
            this._editorSchedulePreview();
            // Reset is itself an undoable step, push immediately
            // instead of waiting for the slider-idle timer.
            this._editorPushHistory();
        },

        // Middle-truncated path for the editor toolbar so deep
        // upload paths (C:\...\7bffd2631c47495098227ef33a96778d\
        // result_4200s_graxpert_bge.fits) don't push the right
        // controls column narrower. Shows the filename + parent
        // dir whenever we can; collapses the middle to "…" when
        // the full string would overflow ~60 chars. Hover (title
        // attribute on the span) still surfaces the full path.
        editorShortPath(p) {
            if (!p) return '';
            if (p.length <= 60) return p;
            // Split on either separator so Windows + POSIX both work.
            const parts = p.split(/[\\/]/);
            if (parts.length <= 3) return p;       // already short
            const base = parts[parts.length - 1];
            const parent = parts[parts.length - 2];
            const head = parts[0];                  // C: / "" (POSIX root)
            // Reassemble with a consistent separator — pick the
            // one the input used so the displayed path still
            // looks native to the user's OS.
            const sep = p.includes('\\') ? '\\' : '/';
            return `${head}${sep}…${sep}${parent}${sep}${base}`;
        },

        // AUTOED-2: Compute reasonable Light + Color slider values from
        // the session's working buffer histogram and apply them through
        // the same setters the manual sliders use. The setters handle
        // dirty marking + history snapshots + preview re-render, so Auto
        // behaves exactly like a fast manual fiddle session: one undoable
        // step (the debounced history push collapses the burst), Save
        // still persists to sidecar, individual sliders can be refined.
        async editorAuto() {
            if (!this.editorState.session) return;
            if (this.editorState.autoBusy) return;
            this.editorState.autoBusy = true;
            try {
                const resp = await this.apiPost('/api/editor/auto',
                    { sessionId: this.editorState.session });
                if (!resp) return;
                const r = await resp.json();
                if (r.light) {
                    for (const [k, v] of Object.entries(r.light)) {
                        if (typeof v === 'number') this.editorSetLight(k, v);
                    }
                }
                if (r.color) {
                    for (const [k, v] of Object.entries(r.color)) {
                        if (typeof v === 'number') this.editorSetColor(k, v);
                    }
                }
                this.toast('Auto adjustments applied', 'success');
            } catch (e) {
                this.toast('Auto failed: ' + (e?.message || e), 'error');
            } finally {
                this.editorState.autoBusy = false;
            }
        },

        // ─── GX-5: AI section runner ────────────────────────────────────
        // Process the editor's current source file via the matching ONNX
        // pipeline (browser-local inference), write a sibling FITS, then
        // load that new file into the same editor session. Treats the
        // AI result as the new "source" the user keeps editing on.
        //
        // Non-destructive in two senses: (a) the original source FITS
        // stays on disk untouched (sibling is a new file), (b) the
        // editor's slider state is preserved across the reload (we
        // re-apply the current `edits` after the new session is open).
        async editorRunAi(op) {
            if (this.editorAi.busy) return;
            if (!this.editorState.session) return;
            const src = this.editorState.sourcePath;
            if (!src) {
                this.toast('Editor has no source path', 'warn');
                return;
            }
            // License consent, same path the FILES tab uses.
            const ok = await this._ensureOnnxLicenseAccepted();
            if (!ok) return;

            this.editorAi.busy = true;
            this.editorAi.phase = 'preparing';
            try {
                let pipeline;
                let runOpts = {};
                switch (op) {
                    case 'background-extraction':
                        pipeline = new OnnxRegistry.BgePipeline();
                        runOpts = { correction: this.settings.graxpertBgeCorrection };
                        break;
                    case 'denoising':
                        pipeline = new OnnxRegistry.DenoisePipeline();
                        runOpts = {
                            strength: this.settings.graxpertDenoiseStrength,
                            // GX-12k: per-run override via modal dropdown
                            // (when set), falls back to profile default.
                            version: this.graxpert?.modalDenoiseVersion
                                  || this.settings.onnxDefaultDenoiseVersion
                                  || '2.0.0',
                        };
                        break;
                    case 'deconvolution':
                        pipeline = new OnnxRegistry.DeconPipeline();
                        runOpts = {
                            strength: this.settings.graxpertDeconStrength,
                            psfPixels: this.settings.graxpertDeconPsfSize,
                            // GX-12h: parity with GraXpert UI, let the
                            // user pick Stars-only vs Object-only here too.
                            target: this.graxpert?.modalDeconTarget || 'stars',
                        };
                        break;
                    default:
                        throw new Error('Unknown AI op: ' + op);
                }
                // GX-12i: suffix includes the decon variant so output
                // filenames don't collide between stars/objects runs.
                const suffix = this.graxpertSuffix(op, runOpts);

                this.editorAi.phase = 'fetching pixels';
                const raw = await this._onnxFetchSourcePixels(src);
                if (!raw) throw new Error('Could not decode source');

                this.editorAi.phase = 'running ' + op;
                const result = await pipeline.run(
                    raw.pixels, raw.width, raw.height,
                    Object.assign({}, runOpts, {
                        // GX-9: forward channel count so RGB FITS
                        // process per-channel.
                        channels: raw.channels,
                        onProgress: (phase, frac) => {
                            this.editorAi.phase = op + ', ' + phase
                              + (frac != null ? ' ' + Math.round(frac * 100) + '%' : '');
                        }
                    }));

                this.editorAi.phase = 'saving sibling FITS';
                const outPath = await this._onnxSaveResult(
                    src, suffix, result.pixels, result.width,
                    result.height, result.channels);
                if (!outPath) throw new Error('Save failed');

                // Preserve the user's current edits across the source
                // swap, re-apply them on the new session.
                const savedEdits = JSON.parse(JSON.stringify(
                    this.editorState.edits || {}));

                this.editorAi.phase = 'reloading editor with ' + outPath.split(/[\\/]+/).pop();
                this.toast('AI ' + op + ' → ' + outPath.split(/[\\/]+/).pop(), 'ok');
                await this.editorLoad(outPath);
                this.editorState.edits = savedEdits;
                this.editorState.dirty = true;
                this._editorSchedulePreview();
                this._editorPushHistory();
            } catch (e) {
                console.error('[Editor AI] ' + op, e);
                this.toast('AI ' + op + ' failed: ' + (e.message || ''), 'error');
            } finally {
                this.editorAi.busy = false;
                this.editorAi.phase = '';
            }
        },

        editorClose() {
            if (this.editorState.session) {
                // Fire-and-forget, server reaps on idle anyway, but
                // freeing now is cheaper.
                fetch('/api/editor/release', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ sessionId: this.editorState.session })
                }).catch(() => { /* ignore */ });
            }
            this._editorTeardownBlobs();
            // Drop the WASM working buffer too, saves 50-200MB heap.
            if (this.editorState.wasmLoaded && globalThis.NINA?.Polaris?.Wasm?.Interop) {
                try { globalThis.NINA.Polaris.Wasm.Interop.EditorRelease(); }
                catch { /* ignore */ }
            }
            this.editorState.wasmLoaded = false;
            this.editorState.session = null;
            this.editorState.sourcePath = '';
            this.editorState.edits = {};
            this.editorState.dirty = false;
            this.editorState.error = '';
        },

        async editorSaveSidecar() {
            if (!this.editorState.session) return;
            try {
                const r = await this.apiFetch('/api/editor/sidecar', {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        path: this.editorState.sourcePath,
                        edits: this.editorState.edits
                    })
                });
                if (!r.ok) throw new Error(`HTTP ${r.status}`);
                const j = await r.json();
                this.editorState.dirty = false;
                this.toast('Edits saved → ' + j.sidecarPath, 'ok');
            } catch (e) {
                this.toast('Save failed: ' + (e.message || ''), 'error');
            }
        },

        editorOpenExportModal() {
            this.editorState.exportModal = true;
        },

        editorSyncResizeFromMode() {
            const m = this.editorState.export.resizeMode;
            if (m === 'long') {
                this.editorState.export.resizeValue =
                    Math.max(this.editorState.width, this.editorState.height);
            } else if (m === 'pct') {
                this.editorState.export.resizeValue = 100;
            }
        },

        editorExportOutputPreview() {
            const fmt = this.editorState.export.format;
            const ext = fmt === 'png' ? '.png' : fmt === 'tif' ? '.tif' : '.jpg';
            const stem = (this.editorState.sourcePath || '').split(/[\\/]/).pop()
                ?.replace(/\.[^.]+$/, '') || 'image';
            return `…/edited/${stem}__edited_*${ext}`;
        },

        async editorDoExport() {
            if (!this.editorState.session) return;
            const ex = this.editorState.export;
            let tw = null, th = null;
            if (ex.resizeMode === 'long') {
                const long = Math.max(this.editorState.width, this.editorState.height);
                const scale = ex.resizeValue / long;
                tw = Math.round(this.editorState.width  * scale);
                th = Math.round(this.editorState.height * scale);
            } else if (ex.resizeMode === 'pct') {
                tw = Math.round(this.editorState.width  * ex.resizeValue / 100);
                th = Math.round(this.editorState.height * ex.resizeValue / 100);
            }
            this.editorState.exporting = true;
            try {
                const r = await this.apiFetch('/api/editor/export', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        sessionId: this.editorState.session,
                        edits: this.editorState.edits,
                        format: ex.format,
                        quality: ex.quality,
                        targetWidth: tw,
                        targetHeight: th
                    })
                });
                if (!r.ok) {
                    const e = await r.json().catch(() => null);
                    throw new Error(e?.error || `HTTP ${r.status}`);
                }
                const j = await r.json();
                this.editorState.exportModal = false;
                this.toast('Exported → ' + j.path, 'ok');
            } catch (e) {
                this.toast('Export failed: ' + (e.message || ''), 'error');
            } finally {
                this.editorState.exporting = false;
            }
        },

        editorSetLight(key, val) { this._editorPatch('light', key, val); },
        editorSetColor(key, val) { this._editorPatch('color', key, val); },
        editorSetEffects(key, val) { this._editorPatch('effects', key, val); },
        editorSetDetail(key, val) { this._editorPatch('detail', key, val); },
        editorSetWB(key, val)    { this._editorPatch('whiteBalance', key, val); },

        _editorPatch(section, key, val) {
            if (!this.editorState.session) return;
            const e = this.editorState.edits;
            if (!e[section]) e[section] = {};
            e[section][key] = val;
            this.editorState.dirty = true;
            this._editorSchedulePreview();
            this._editorScheduleHistoryPush();
        },

        // ─── Undo / redo (history of edits snapshots) ───────────────────
        // Snapshot semantics: every slider movement schedules a push 500ms
        // after the last input. A continuous drag is therefore one
        // undoable step instead of dozens. Snapshots are JSON-serialised
        // (cheap; the edits tree is tiny) so undo restores via parse.

        _editorScheduleHistoryPush() {
            clearTimeout(this._editorHistoryTimer);
            this._editorHistoryTimer = setTimeout(
                () => this._editorPushHistory(), 500);
        },

        _editorPushHistory() {
            const h = this.editorState.history;
            const snap = JSON.stringify(this.editorState.edits || {});
            // Skip if identical to current (e.g. slider released without
            // moving the value).
            if (h.stack[h.index] === snap) return;
            // Truncate the redo tail if we branched off mid-history.
            if (h.index < h.stack.length - 1) {
                h.stack = h.stack.slice(0, h.index + 1);
            }
            h.stack.push(snap);
            // Cap stack length to keep memory bounded on long sessions.
            const MAX = 200;
            if (h.stack.length > MAX) {
                h.stack.splice(0, h.stack.length - MAX);
            }
            h.index = h.stack.length - 1;
        },

        editorCanUndo() {
            return this.editorState.session != null
                   && this.editorState.history.index > 0;
        },
        editorCanRedo() {
            const h = this.editorState.history;
            return this.editorState.session != null
                   && h.index < h.stack.length - 1;
        },

        editorUndo() {
            if (!this.editorCanUndo()) return;
            // Flush any pending push so the current edits become an
            // undo step before we move backwards.
            if (this._editorHistoryTimer) {
                clearTimeout(this._editorHistoryTimer);
                this._editorHistoryTimer = null;
                this._editorPushHistory();
            }
            const h = this.editorState.history;
            h.index--;
            this.editorState.edits = JSON.parse(h.stack[h.index]);
            this.editorState.dirty = true;
            this._editorSchedulePreview();
        },

        editorRedo() {
            if (!this.editorCanRedo()) return;
            const h = this.editorState.history;
            h.index++;
            this.editorState.edits = JSON.parse(h.stack[h.index]);
            this.editorState.dirty = true;
            this._editorSchedulePreview();
        },

        _editorResetHistory(initialEdits) {
            const snap = JSON.stringify(initialEdits || {});
            this.editorState.history = { stack: [snap], index: 0 };
            if (this._editorHistoryTimer) {
                clearTimeout(this._editorHistoryTimer);
                this._editorHistoryTimer = null;
            }
        },

        // ─── Zoom + pan ───────────────────────────────────────────────
        // CSS transform on .editor-preview-stage. Mouse wheel zooms
        // around the cursor; click-drag pans when zoomed > 1. Double-
        // click = reset to fit-to-window.

        editorZoomIn()  { this._editorZoomBy(1.25); },
        editorZoomOut() { this._editorZoomBy(1 / 1.25); },
        editorZoomReset() {
            this.editorState.zoom = 1;
            this.editorState.panX = 0;
            this.editorState.panY = 0;
        },

        _editorZoomBy(factor, anchorX, anchorY) {
            const s = this.editorState;
            const next = Math.max(0.1, Math.min(16, s.zoom * factor));
            if (Math.abs(next - s.zoom) < 1e-4) return;
            // Anchor-aware zoom, keep the point under the cursor fixed
            // in screen space. If no anchor given (button click), zoom
            // around the centre (anchor offsets default to 0,0).
            if (anchorX != null && anchorY != null) {
                const k = next / s.zoom - 1;
                s.panX -= (anchorX - s.panX) * k / (next / s.zoom);
                s.panY -= (anchorY - s.panY) * k / (next / s.zoom);
            }
            s.zoom = next;
        },

        editorOnWheel(ev) {
            // Wheel zooms with the cursor as anchor. We use deltaY sign
            // (not magnitude, track-pads vary wildly) for predictable
            // 1.1× steps.
            const wrap = ev.currentTarget;
            const rect = wrap.getBoundingClientRect();
            const cx = ev.clientX - rect.left - rect.width / 2;
            const cy = ev.clientY - rect.top - rect.height / 2;
            const factor = ev.deltaY < 0 ? 1.1 : 1 / 1.1;
            this._editorZoomBy(factor, cx, cy);
        },

        editorOnPanStart(ev) {
            if (this.editorState.zoom <= 1) return;
            this.editorState.panning = true;
            this.editorState._panStartX = ev.clientX;
            this.editorState._panStartY = ev.clientY;
            this.editorState._panOriginX = this.editorState.panX;
            this.editorState._panOriginY = this.editorState.panY;
        },
        editorOnPanMove(ev) {
            if (!this.editorState.panning) return;
            this.editorState.panX = this.editorState._panOriginX
                                    + (ev.clientX - this.editorState._panStartX);
            this.editorState.panY = this.editorState._panOriginY
                                    + (ev.clientY - this.editorState._panStartY);
        },
        editorOnPanEnd() {
            this.editorState.panning = false;
        },

        // Slider drag detection. While a range input has the
        // pointer captured (pointerdown..pointerup), flag the editor
        // as "dragging" so _editorRunPreview can downscale the
        // preview + skip the histogram render. On pointerup we wait
        // a short settle window then re-fire one full-quality render
        // + histogram. Document-level capture so we still see the
        // pointerup if the user releases outside the slider track.
        _editorBindDragHandlers() {
            if (this._editorDragHandlerBound) return;
            this._editorDragHandlerBound = true;
            const onDown = (e) => {
                if (this.tab !== 'editor') return;
                const el = e.target;
                if (!(el instanceof HTMLInputElement) || el.type !== 'range') return;
                // Only treat the slider as "dragging" while inside
                // the editor panel; the simulator + other tabs have
                // their own ranges and we don't want to throttle
                // their renders.
                if (!el.closest('.editor-panel')) return;
                this._editorDragging = true;
                clearTimeout(this._editorDragSettleTimer);
            };
            const onUp = () => {
                if (!this._editorDragging) return;
                // Short settle window — covers the case where the
                // user releases briefly then re-grabs the same
                // slider (mouse wheel, keyboard arrows on a focused
                // range emit input events between pointerup +
                // pointerdown).
                clearTimeout(this._editorDragSettleTimer);
                this._editorDragSettleTimer = setTimeout(() => {
                    this._editorDragging = false;
                    // Re-fire one full-quality render so the user
                    // ends up looking at the high-res preview after
                    // settle, plus the histogram that was skipped.
                    this._editorSchedulePreview();
                }, 120);
            };
            document.addEventListener('pointerdown', onDown, true);
            document.addEventListener('pointerup', onUp, true);
            document.addEventListener('pointercancel', onUp, true);
        },

        // Wire Ctrl+Z / Ctrl+Y keyboard shortcuts when the editor tab
        // is the active surface. Hooked in editorTabOpened.
        _editorBindKeyHandlers() {
            if (this._editorKeyHandlerBound) return;
            this._editorKeyHandlerBound = true;
            window.addEventListener('keydown', (e) => {
                if (this.tab !== 'editor' || !this.editorState.session) return;
                // Skip when user is typing in a real input/textarea.
                const tag = (e.target?.tagName || '').toLowerCase();
                if (tag === 'input' || tag === 'textarea' || e.target?.isContentEditable) {
                    // Allow Ctrl+Z on sliders, they don't have a useful
                    // native undo anyway, and the user expects undo to
                    // walk the edit history regardless of focus.
                    if (tag === 'input' && e.target?.type !== 'range') return;
                }
                if ((e.ctrlKey || e.metaKey) && !e.altKey) {
                    const k = e.key.toLowerCase();
                    if (k === 'z' && !e.shiftKey) { e.preventDefault(); this.editorUndo(); }
                    else if (k === 'z' && e.shiftKey) { e.preventDefault(); this.editorRedo(); }
                    else if (k === 'y') { e.preventDefault(); this.editorRedo(); }
                    else if (k === '0') { e.preventDefault(); this.editorZoomReset(); }
                    else if (k === '=' || k === '+') { e.preventDefault(); this.editorZoomIn(); }
                    else if (k === '-') { e.preventDefault(); this.editorZoomOut(); }
                }
            });
        },

        // Debounced render, every input pings, but we coalesce to one
        // request in flight + one queued. Prevents the server from
        // queueing 100 stale requests while the user is mid-drag.
        //
        // Two debounce profiles:
        //   - dragging (pointer held down on a slider): 120 ms so
        //     a fast back-and-forth doesn't spam the WASM heap
        //   - idle / single click: 60 ms so a one-shot edit feels
        //     near-instant
        _editorSchedulePreview() {
            if (this._editorPreviewTimer) clearTimeout(this._editorPreviewTimer);
            const delay = this._editorDragging ? 120 : 60;
            this._editorPreviewTimer = setTimeout(
                () => this._editorRunPreview(), delay);
        },

        async _editorRunPreview() {
            if (!this.editorState.session) return;
            if (this.editorState.rendering) {
                // Already a request in flight, flag pending and bail.
                this._editorPendingPreview = true;
                return;
            }
            this.editorState.rendering = true;
            try {
                if (this.editorState.computeMode === 'wasm' && this.editorState.wasmLoaded) {
                    // Drag-aware downscale: while the user is
                    // sweeping a slider we render a smaller preview
                    // so the per-frame cost stays under one rAF
                    // tick. The settle handler re-fires at full
                    // 1600 px once the pointer goes up.
                    const dragging = this._editorDragging;
                    const maxDim = dragging ? 700 : 1600;
                    // Yield to the next animation frame BEFORE the
                    // synchronous WASM call so the browser gets to
                    // paint the slider thumb at its new position
                    // before the main thread sticks for a few
                    // hundred ms.
                    await new Promise(r => requestAnimationFrame(() => r()));
                    this._editorRunPreviewWasm(maxDim);
                } else {
                    // Server path's fetch is already async; the only
                    // main-thread cost is the JPEG decode at blob
                    // unwrap time. Still respect the drag-aware
                    // downscale to keep the network roundtrip + decode
                    // snappy during a drag.
                    const dragging = this._editorDragging;
                    const maxDim = dragging ? 900 : 1600;
                    await this._editorRunPreviewServer(maxDim);
                }
                // Histogram is purely informational; skip it during
                // active drag (re-fired by the drag-settle handler)
                // and defer to requestIdleCallback so it never
                // competes with the preview render for main-thread
                // time.
                if (!this._editorDragging) {
                    clearTimeout(this._editorHistTimer);
                    this._editorHistTimer = setTimeout(() => {
                        this._idleRun(() => this._editorRenderHistogram());
                    }, 200);
                }
            } catch (e) {
                console.warn('[Editor] preview failed', e);
            } finally {
                this.editorState.rendering = false;
                if (this._editorPendingPreview) {
                    this._editorPendingPreview = false;
                    this._editorSchedulePreview();
                }
            }
        },

        // Tiny requestIdleCallback shim — Safari doesn't ship rIC
        // even today, so fall back to a short setTimeout. The
        // histogram update is non-critical so a 100 ms delay is
        // fine when rIC isn't available.
        _idleRun(fn) {
            if (typeof requestIdleCallback === 'function') {
                requestIdleCallback(fn, { timeout: 500 });
            } else {
                setTimeout(fn, 100);
            }
        },

        async _editorRunPreviewServer(maxDim = 1600) {
            const r = await this.apiFetch('/api/editor/preview', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    sessionId: this.editorState.session,
                    edits: this.editorState.edits,
                    maxDim: maxDim,
                    quality: 85
                })
            });
            if (!r.ok) throw new Error(`HTTP ${r.status}`);
            const blob = await r.blob();
            const url = URL.createObjectURL(blob);
            if (this.editorState.previewUrl) URL.revokeObjectURL(this.editorState.previewUrl);
            this.editorState.previewUrl = url;
        },

        _editorRunPreviewWasm(maxDim = 1600) {
            // Synchronous JSExport call, pixels come back as a Uint8Array
            // we render to the editor canvas via ImageData. No JPEG encode,
            // no network roundtrip; latency is just the pipeline + canvas
            // blit.
            const interop = globalThis.NINA?.Polaris?.Wasm?.Interop;
            if (!interop) {
                // Lost the bundle somehow, graceful fallback to server.
                this.editorState.computeMode = 'server';
                return this._editorRunPreviewServer();
            }
            const editsJson = JSON.stringify(this.editorState.edits || {});
            const pixels = interop.EditorApplyEdit(editsJson, maxDim);
            const dims = interop.EditorGetOutputDims();
            const w = dims[0], h = dims[1], ch = dims[2];
            if (!w || !h) return;

            const canvas = document.getElementById('editorWasmCanvas');
            if (!canvas) return;
            if (canvas.width !== w || canvas.height !== h) {
                canvas.width = w;
                canvas.height = h;
            }
            const ctx = canvas.getContext('2d');
            const img = ctx.createImageData(w, h);
            // Expand mono → RGBA or RGB → RGBA. The Canvas only takes
            // RGBA8888, so we always write 4 bytes/pixel here regardless
            // of input channel count.
            const dst = img.data;
            if (ch === 1) {
                for (let i = 0, j = 0; i < pixels.length; i++, j += 4) {
                    const v = pixels[i];
                    dst[j] = v; dst[j + 1] = v; dst[j + 2] = v; dst[j + 3] = 255;
                }
            } else {
                for (let i = 0, j = 0; i < pixels.length; i += 3, j += 4) {
                    dst[j]     = pixels[i];
                    dst[j + 1] = pixels[i + 1];
                    dst[j + 2] = pixels[i + 2];
                    dst[j + 3] = 255;
                }
            }
            ctx.putImageData(img, 0, 0);
        },

        async _editorRenderOriginal() {
            // Fetches a one-shot unedited preview for the "Hold to compare"
            // button. Cached for the life of the session.
            if (!this.editorState.session) return;
            try {
                const r = await this.apiFetch('/api/editor/preview', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        sessionId: this.editorState.session,
                        edits: this._editorDefaultEdits(),
                        maxDim: 1600,
                        quality: 85
                    })
                });
                if (!r.ok) return;
                const blob = await r.blob();
                if (this.editorState.originalUrl) URL.revokeObjectURL(this.editorState.originalUrl);
                this.editorState.originalUrl = URL.createObjectURL(blob);
            } catch { /* non-fatal */ }
        },

        async _editorRenderHistogram() {
            if (!this.editorState.session) return;
            try {
                let hist;
                if (this.editorState.computeMode === 'wasm' && this.editorState.wasmLoaded
                    && globalThis.NINA?.Polaris?.Wasm?.Interop) {
                    // Synchronous; same math as server, no network hop.
                    hist = globalThis.NINA.Polaris.Wasm.Interop.EditorComputeHistogram(
                        JSON.stringify(this.editorState.edits || {}));
                } else {
                    const r = await this.apiFetch('/api/editor/histogram', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({
                            sessionId: this.editorState.session,
                            edits: this.editorState.edits
                        })
                    });
                    if (!r.ok) return;
                    hist = await r.json();
                }
                this._editorDrawHistogram(hist);
            } catch { /* non-fatal */ }
        },

        _editorDrawHistogram(hist) {
            const canvas = document.getElementById('editorHistogram');
            if (!canvas || !hist) return;
            const ctx = canvas.getContext('2d');
            ctx.clearRect(0, 0, canvas.width, canvas.height);
            const isRgb = hist.length === 768;
            const channels = isRgb
                ? [
                    { off: 0,   color: 'rgba(248, 113, 113, 0.7)' },  // R
                    { off: 256, color: 'rgba(74,  222, 128, 0.7)' },  // G
                    { off: 512, color: 'rgba(96,  165, 250, 0.7)' }   // B
                  ]
                : [{ off: 0, color: 'rgba(229, 231, 235, 0.85)' }];
            // Normalise each channel's max so colours don't drown each other.
            for (const ch of channels) {
                let max = 0;
                for (let i = 0; i < 256; i++) if (hist[ch.off + i] > max) max = hist[ch.off + i];
                if (max <= 0) continue;
                ctx.fillStyle = ch.color;
                ctx.beginPath();
                ctx.moveTo(0, canvas.height);
                for (let i = 0; i < 256; i++) {
                    const x = i / 255 * canvas.width;
                    const y = canvas.height - (hist[ch.off + i] / max) * (canvas.height - 4);
                    ctx.lineTo(x, y);
                }
                ctx.lineTo(canvas.width, canvas.height);
                ctx.closePath();
                ctx.fill();
            }
        },

        _editorDefaultEdits() {
            // Empty record-of-records, all sections null/missing means
            // "defaults" on the server (EditParams.IsDefault per section).
            return {};
        },

        _editorTeardownBlobs() {
            if (this.editorState.previewUrl) {
                URL.revokeObjectURL(this.editorState.previewUrl);
                this.editorState.previewUrl = '';
            }
            if (this.editorState.originalUrl) {
                URL.revokeObjectURL(this.editorState.originalUrl);
                this.editorState.originalUrl = '';
            }
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
                    + '&hidden=' + (this.files.showHidden ? 'true' : 'false')
                    + (this.files.showFitsMeta ? '&withMeta=true' : ''));
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

        // GX-12d: Compare button predicates. The before/after comparator
        // can render any two files the preview endpoint understands
        // (FITS / PNG / JPG / TIFF), so the only client-side gate is
        // "exactly two files, no directories". File-type validity is
        // checked by /api/files/preview itself, bad type just renders
        // a 415 inside the comparator instead of crashing.
        filesSelectionHasDir() {
            const sel = this.files.selectedPaths;
            if (sel.length === 0) return false;
            return sel.some(p => {
                const e = this.files.entries.find(x => x.fullPath === p);
                return e && e.isDirectory;
            });
        },

        filesCompareSelected() {
            const sel = this.files.selectedPaths;
            if (sel.length !== 2) return;
            // Sort alphabetically so common pairings (master + its
            // _bge/_denoise/_decon sibling) land BEFORE = master,
            // AFTER = sibling, '.' (0x2e) sorts before '_' (0x5f),
            // and "_denoise" sorts after the bare stem.
            const ordered = [...sel].sort((a, b) =>
                a.localeCompare(b, undefined, { numeric: true, sensitivity: 'base' }));
            const labelFor = p => p.split(/[\\/]+/).pop();
            const pair = {
                src: ordered[0],
                out: ordered[1],
                label: labelFor(ordered[0]) + '  ↔  ' + labelFor(ordered[1]),
            };
            // mode='compare' switches the corner tags from
            // BEFORE/AFTER (the GraXpert-op semantic) to the actual
            // filenames, the user is comparing two arbitrary files,
            // not a known before/after pair.
            this.graxpertOpenCompare([pair], 0, 'compare');
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
                    `Delete ${n} item(s)? This is permanent, folders are removed recursively.`)) return;
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
                // XFER: apiDownload streams the ZIP body through the
                // ReadableStream reader so the activity-bar transfer
                // chip can show progress. Zips of 50+ FITS easily run
                // into GB; without a bar the browser just looks frozen.
                const r = await this.apiDownload('/api/files/download-zip', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ paths, rootForNames: this.files.cwd, fileName }),
                    label: 'Download ' + fileName
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
                //
                // DO NOT pre-apply authUrl here, _initOsdViewer +
                // reloadImageViewer wrap the URL with authUrl right
                // before they hand it to OpenSeadragon. Double-applying
                // appends ?token= twice and Query["token"] then resolves
                // to "abc,abc" (comma-joined StringValues), which fails
                // validation and 401s. Single auth point keeps the
                // round-trip clean.
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
                    const r = await this.apiFetch('/api/files/preview?path=' + encodeURIComponent(entry.fullPath));
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
        // all apply, no parallel pipeline.
        _openImageViewerWithUrl(url, title) {
            this.imageViewerUrl = url;
            this.imageViewerTitle = title || 'Image Viewer';
            this.openImageViewer();
        },

        // --- Open in editor (ED-7) ------------------------------------
        // Hand the currently-selected file off to the EDITOR tab. We
        // grab the path from selectedPaths (already filtered to exactly
        // one entry by the button's :disabled), switch tabs, and call
        // editorLoad which handles the rest of the session lifecycle.
        async filesOpenInEditor() {
            if (this.files.selectedPaths.length !== 1) return;
            const path = this.files.selectedPaths[0];
            // UNIF-5: editor markup now lives inside the FILES tab as
            // a sub-tab (filesSubTab='edit'), not its own top-level
            // tab. We're already on FILES (the button only renders
            // there) so just flip the sub-tab and let the editor
            // panel render in the right pane.
            this.setFilesSubTab('edit');
            await this.$nextTick();
            await this.editorLoad(path);
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
            if (n == null || n < 0) return ', ';
            if (n < 1024) return n + ' B';
            if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
            if (n < 1024 * 1024 * 1024) return (n / 1048576).toFixed(1) + ' MB';
            return (n / 1073741824).toFixed(2) + ' GB';
        },

        filesFormatDate(iso) {
            if (!iso) return ', ';
            const d = new Date(iso);
            if (isNaN(d.getTime()) || d.getFullYear() < 1980) return ', ';
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

        // Used in `:key` / `:id` bindings, has to be DOM-safe (no slashes,
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
        // recentre the map ON THE PICKED OBJECT. Doesn't move the mount,
        // that's the Go to btn.
        //
        // Bug fix: previously this called skyGoToMount() which prefers
        // mount.ra/dec over skyTarget, so the map snapped to the mount
        // position instead of the picked card. Now we drive the engine
        // straight via _skyLookAt with the card's coordinates.
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
                // Centre on the picked target directly. Pick a sensible FOV
                // so the card's object actually fills a reasonable fraction
                // of the viewport without losing context (same heuristic
                // skyGoToMount uses: ~4× the camera width, ≥ 1°).
                let fovDeg;
                if (this.fov && this.fov.width > 0) {
                    fovDeg = Math.max(this.fov.width * 4, 1);
                }
                this._skyLookAt(item.raHours, item.decDeg, fovDeg, item.name);
                this._pushSkyFovOverlays();
                if (typeof this.updateSkyCameraFov === 'function') this.updateSkyCameraFov();

                // Open the SKY info card so the user sees the same context
                // they had on the Tonight card (name, mag, altitude chart,
                // transit/set times). _populateSkyInfo takes an obj shaped
                // like a stellarium-web search result, so synthesise one
                // from the tonight item, types[] becomes [item.type] when
                // present, subtitle gets the common name if any.
                this._populateSkyInfo({
                    name: item.name,
                    types: item.type ? [item.type] : null,
                    subtitle: item.commonName || '',
                    magnitude: typeof item.magnitude === 'number' ? item.magnitude : null,
                    raDeg: item.raHours * 15,
                    decDeg: item.decDeg
                });
            });
        },

        // "Center" action, purely a map operation. Picks the card as
        // the SKY target (opens the info card, centres the engine on
        // the coords, refreshes FOV overlays). Does NOT move the mount.
        // The button used to slew; the user explicitly asked that the
        // mount stay put, slewing now lives on the SKY tab's
        // Slew / Slew & Center overlays after the map is positioned.
        async tonightGoTo(item) {
            this.tonightPickTarget(item);
        },

        // Helper: mark a card's thumb as failed-to-load. If we were
        // trying the local cached URL and a remote URL is also known,
        // swap to the remote URL once before giving up, this covers
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
        // search-friendliness, NASA Image Library is indexed by popular
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
                this.toast?.('Downloading object thumbnails, may take a couple of minutes…', 'info');
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
        // _ensureChart(), it looks up canvases via $refs which only
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
            // refresh), tear it down before creating a new one, leaving
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
            // SWE-4: keep the engine's observer.utc in sync so the
            // moon, sun, planets render at the right phase/altitude.
            // Throttle to once every 30 ticks (~30s), the engine
            // interpolates per-frame internally, so we don't need to
            // push the clock every second.
            this._skyTimePushTick = (this._skyTimePushTick || 0) + 1;
            if (this._skyTimePushTick % 30 === 0 && this._skyBridgeReady) {
                this._skySendMessage({ type: 'set-time', utc: Date.now() });
            }
        },

        skyGoToMount() {
            // SWE-4: removed Celestial.rotate dependency. Now drives
            // the stellarium-web-engine iframe via _skyLookAt. The
            // _celestialReady gate has been retired with d3, instead
            // we trust _skySendMessage to queue the message if the
            // bridge hasn't announced ready yet.
            const ra  = this.mount?.ra  ?? (this.skyTarget?.ra)  ?? 0;
            const dec = this.mount?.dec ?? (this.skyTarget?.dec) ?? 0;
            const decClamped = Math.max(-89.5, Math.min(89.5, dec));
            // Tighter FOV so the camera's mount rectangle is actually
            // visible, default 45° wide-field view dwarfs typical
            // 1-3° camera FOVs. Zoom to ~4× the camera width so the
            // blue rectangle takes a comfortable fraction of the
            // viewport without losing context.
            let fovDeg = undefined;
            if (this.fov && this.fov.width > 0) {
                fovDeg = Math.max(this.fov.width * 4, 1);
            }
            this._skyLookAt(ra, decClamped, fovDeg, null);
            // Re-push the FOV overlays so the blue mount rectangle and
            // red target rectangle are anchored on the new centre.
            this._pushSkyFovOverlays();
        },

        // ASIAIR-style two-FOV overlay on the sky map:
        //   • BLUE rectangle, where the mount is currently pointing
        //     (always shown when a mount is connected). Lets the user
        //     see what's actually in frame right now without picking
        //     anything.
        //   • RED rectangle, where the user is planning to go
        //     (anchored on the picked sky target if any). Acts as the
        //     "preview my next slew" indicator.
        // Both share the same FOV dimensions (sensor + focal length
        // from the active rig). Drawn as custom d3-celestial layers
        //, we register the layers ONCE and then mutate the cached
        // GeoJSON on subsequent calls, then nudge Celestial.redraw().
        // (The old code re-registered the layer every call, leaking
        // a new SVG layer per WS tick and never cleaning the old.)
        _buildFovRing(raHours, decDeg) {
            const w = this.fov.width / 2;
            const h = this.fov.height / 2;
            const cosDec = Math.cos(decDeg * Math.PI / 180) || 1e-6;
            const raDeg = raHours * 15;
            const ring = [
                [raDeg - w / cosDec, decDeg - h],
                [raDeg + w / cosDec, decDeg - h],
                [raDeg + w / cosDec, decDeg + h],
                [raDeg - w / cosDec, decDeg + h],
                [raDeg - w / cosDec, decDeg - h]
            ];
            return {
                type: 'FeatureCollection',
                features: [{ type: 'Feature', properties: { name: 'FOV' },
                             geometry: { type: 'LineString', coordinates: ring } }]
            };
        },

        // SWE-6: _skyMapCenter() removed, d3-celestial's projection
        // is gone. Sync reads of the live map centre are not possible
        // through the bridge; callers use _skyGetCenter() async or
        // fall back to skyTarget.

        updateSkyCameraFov() {
            // SWE-5: push the mount+target FOV rectangles to the
            // stellarium-web-engine iframe via postMessage instead of
            // mutating the old d3-celestial layers. The bridge owns
            // a single 'polaris-fov' layer with up to 3 geojson
            // objects (mount blue, target red dashed, mosaic yellow);
            // we re-send the full overlay state on every call.
            this._pushSkyFovOverlays();
        },

        // SWE-5: build the {widthDeg, heightDeg, rotationDeg} pair from
        // the active rig + connected camera and post a set-fov-overlays
        // message. Mount FOV anchors on the live mount RA/Dec, target
        // FOV anchors on skyTarget. Either side can be null to clear it.
        _pushSkyFovOverlays() {
            if (!this.aladinShowFov || !(this.fov?.width > 0)) {
                this._skySendMessage({ type: 'set-fov-overlays',
                    mount: null, target: null });
                return;
            }
            const w = this.fov.width, h = this.fov.height;
            const rot = (this.fov.rotationDeg || this.solveRotationDeg || 0);

            let mount = null;
            if (this.mount?.connected
                && Number.isFinite(this.mount.ra)
                && Number.isFinite(this.mount.dec)) {
                mount = {
                    raDeg: this.mount.ra * 15,
                    decDeg: this.mount.dec,
                    widthDeg: w, heightDeg: h, rotationDeg: rot
                };
            }

            // SWE-5: target rectangle is SCREEN-anchored (always at
            // viewport centre). Bridge renders it as a CSS overlay
            // sized from widthDeg/heightDeg + engine fov. No RA/Dec
            // needed; the rectangle's "celestial position" IS the
            // current map centre, which the user is dragging around.
            const target = {
                widthDeg: w, heightDeg: h, rotationDeg: rot
            };

            // Skip when nothing actually changed since last push.
            // The mount status WS push fires several times per second
            // when slewing, and re-creating the engine geojson objects
            // on every tick is wasteful (each call removes + adds
            // layer geometry) and floods the console. Only re-send
            // when at least one of mount.{ra,dec} or target.{ra,dec}
            // moved more than 0.001° (~3 arcsec, well below the
            // pointing precision the rectangles convey).
            const key = JSON.stringify({
                m: mount && { r: mount.raDeg.toFixed(3), d: mount.decDeg.toFixed(3),
                              w: mount.widthDeg.toFixed(3), rot: (mount.rotationDeg||0).toFixed(2) },
                t: target && { w: target.widthDeg.toFixed(3),
                              h: target.heightDeg.toFixed(3),
                              rot: (target.rotationDeg||0).toFixed(2) }
            });
            if (key === this._lastFovOverlayKey) return;
            this._lastFovOverlayKey = key;

            // Helpful diagnostic when "where's my FOV rectangle?" comes up:
            // logs whether the parent decided to send a mount/target side
            // and why (e.g. mount: false because mount.connected is false).
            console.log('[Polaris] _pushSkyFovOverlays mount=',
                mount ? `${mount.raDeg.toFixed(2)}°/${mount.decDeg.toFixed(2)}°` : 'null',
                'target=screen-centred',
                'fov=', w.toFixed(2) + '°×' + h.toFixed(2) + '°');

            this._skySendMessage({ type: 'set-fov-overlays', mount, target,
                mosaic: this.mosaicTiles && this.mosaicTiles.length
                    ? { tiles: this.mosaicTiles } : null });
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
            // PA-5: polar-alignment error vector. Visible whenever we
            // have a computed error vector (post-TPPA), so the arrow
            // stays on-screen during Refine and the user can watch it
            // shrink while adjusting knobs.
            if (this.polar && this.polar.totalErrorArcsec > 0) {
                this._drawPolarErrorVector(ctx, ovr.width, ovr.height);
            }
        },

        // PA-5: polar error vector overlay.
        //
        // Draws an arrow from the canvas centre pointing in the
        // direction the user needs to nudge the tripod knobs to
        // reduce the error to zero. Arrow length is logarithmic in
        // total arcmin (30' fills, 1' is small but visible) so it
        // shrinks smoothly during a Refine run. Colour: red > 5',
        // amber 1-5', green < 1'.
        //
        // Direction math: (azErr, altErr) are in arcsec in topocentric
        // alt/az. The CAMERA frame is rotated by the last solve's
        // rotationDeg relative to north-up. Rotating the error vector
        // by -rotationDeg orients the arrow with the camera's view,
        // up-on-screen corresponds to "up in altitude" only after this
        // de-rotation.
        _drawPolarErrorVector(ctx, w, h) {
            const azErr = this.polar.azErrorArcsec || 0;
            const altErr = this.polar.altErrorArcsec || 0;
            const totalArcmin = (this.polar.totalErrorArcsec || 0) / 60.0;
            if (totalArcmin <= 0) return;

            // Last solve's rotation (camera Y-axis vs sky north).
            const pts = this.polar.points || [];
            const rotDeg = pts.length > 0 ? (pts[pts.length - 1].rotationDeg || 0) : 0;
            const rotRad = -rotDeg * Math.PI / 180.0;

            // Map (azErr, altErr) → screen vector. +alt = up-on-camera
            // (after de-rotation), +az = east-of-camera. Use Y inverted
            // because canvas Y grows downward.
            const cos = Math.cos(rotRad), sin = Math.sin(rotRad);
            const ex = azErr, ey = altErr;
            const xUnit =  ex * cos - ey * sin;
            const yUnit = -(ex * sin + ey * cos);  // invert for screen Y

            // Length scaled logarithmically: 30' → fills, 1' → ~25%
            const maxLen = Math.min(w, h) * 0.42;
            const scale = Math.log(1 + totalArcmin) / Math.log(1 + 30);
            const magn = Math.sqrt(xUnit * xUnit + yUnit * yUnit);
            const len = maxLen * Math.min(1, scale);
            const dx = (xUnit / Math.max(1e-9, magn)) * len;
            const dy = (yUnit / Math.max(1e-9, magn)) * len;

            const cx = w / 2, cy = h / 2;
            const tipX = cx + dx, tipY = cy + dy;

            // Colour by magnitude.
            let color;
            if (totalArcmin > 5) color = 'rgba(239, 68, 68, 0.95)';
            else if (totalArcmin > 1) color = 'rgba(245, 158, 11, 0.95)';
            else color = 'rgba(74, 222, 128, 0.95)';

            // Shaft.
            ctx.save();
            ctx.strokeStyle = color;
            ctx.fillStyle = color;
            ctx.lineWidth = 3;
            ctx.beginPath();
            ctx.moveTo(cx, cy);
            ctx.lineTo(tipX, tipY);
            ctx.stroke();

            // Arrowhead, small triangle at the tip.
            const ang = Math.atan2(dy, dx);
            const headLen = 14;
            const headHalf = 7;
            ctx.beginPath();
            ctx.moveTo(tipX, tipY);
            ctx.lineTo(tipX - headLen * Math.cos(ang) + headHalf * Math.sin(ang),
                       tipY - headLen * Math.sin(ang) - headHalf * Math.cos(ang));
            ctx.lineTo(tipX - headLen * Math.cos(ang) - headHalf * Math.sin(ang),
                       tipY - headLen * Math.sin(ang) + headHalf * Math.cos(ang));
            ctx.closePath();
            ctx.fill();

            // Centre marker.
            ctx.beginPath();
            ctx.arc(cx, cy, 4, 0, 2 * Math.PI);
            ctx.fill();

            // Label box, top-left.
            const az = (azErr / 60).toFixed(2);
            const alt = (altErr / 60).toFixed(2);
            const tot = totalArcmin.toFixed(2);
            const label = `Az ${az}'  Alt ${alt}'  Total ${tot}'`;
            ctx.font = '12px sans-serif';
            const textW = ctx.measureText(label).width;
            ctx.fillStyle = 'rgba(0, 0, 0, 0.65)';
            ctx.fillRect(8, 8, textW + 12, 22);
            ctx.fillStyle = color;
            ctx.fillText(label, 14, 24);
            ctx.restore();
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
            // No full image archive (yet), best we can do is open OSD with the
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
            this.imageViewerTitle = 'Image Viewer, full resolution';
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
                // authUrl: <img> can't carry the Authorization header,
                // so append the bearer token as a query-string fallback
                // for OSD's internal image fetch.
                const sep = this.imageViewerUrl.includes('?') ? '&' : '?';
                this._osdViewer.open({
                    type: 'image',
                    url: this.authUrl(this.imageViewerUrl + sep + 't=' + Date.now())
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
                    // authUrl: see reloadImageViewer comment above.
                    url: this.authUrl(this.imageViewerUrl + sep + 't=' + Date.now())
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
                if (!cur) this.toast('Full page, press Esc to exit', 'info');
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
            if (!this.skyTarget) return;
            const ra = this.skyTarget.ra ?? this.skyTarget.raHours;
            const dec = this.skyTarget.dec ?? this.skyTarget.decDeg;
            if (!Number.isFinite(ra) || !Number.isFinite(dec)) return;
            // SWE-6: aim the engine via the postMessage bridge
            // instead of the old Celestial.rotate. FOV defaults to
            // whatever the engine already has (no zoom change).
            this._skyLookAt(ra, Math.max(-89.5, Math.min(89.5, dec)),
                undefined, this.skyTarget.name || null);
            this._pushSkyFovOverlays();
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
            if (_polarisCharts[key]) return _polarisCharts[key];
            if (typeof Chart === 'undefined') return null;
            const canvas = this.$refs[refName];
            if (!canvas) return null;
            _polarisCharts[key] = new Chart(canvas, makeConfig());
            return _polarisCharts[key];
        },

        // Guider chart: RA (red) + Dec (blue) vs sample index.
        // The canvas lives inside x-show="guider.connected" which starts
        // as display:none, so Chart.js used to measure 0x0 at first
        // create and never re-fit. We defer instance creation until the
        // canvas actually has a non-zero size, then a single Chart.js
        // instance is reused and just gets its data swapped on each
        // ~1Hz WS tick.
        updateGuideChart() {
            const canvas = this.$refs.guideChart;
            if (!canvas || typeof Chart === 'undefined') return;

            // Wait until the canvas has pixels, its parent may still be
            // display:none on the first few WS ticks after page load.
            // We don't bail in subsequent ticks even if clientWidth dips
            // because that would freeze the chart on transient layouts.
            const ready = canvas.clientWidth >= 10 && canvas.clientHeight >= 10;
            if (!_polarisCharts.guide && !ready) return;

            const t = this._chartTheme();
            // Build plain numeric arrays without going through .map()
            // on Alpine's reactive proxy. .map() preserves the proxy
            // chain on the new array's elements, and when Chart.js
            // later iterates dataset.data each [i] read triggers
            // Alpine's reactive get-trap. The trap then records a
            // dependency on the currently-running effect (us), which
            // re-runs us, which assigns again, ad infinitum →
            // RangeError: Maximum call stack size exceeded +
            // "Cannot set properties of undefined (setting 'fullSize')"
            // from Chart.js's layout pass tripping over the unfinished
            // assignment. Manual for-loop with primitives stays
            // outside Alpine's reactivity graph.
            const stepsRef = this.guider.recentSteps || [];
            const raVals = [];
            const decVals = [];
            const labels = [];
            for (let i = 0; i < stepsRef.length; i++) {
                const s = stepsRef[i];
                if (!s) continue;
                const ra = Number(s.ra);
                const dec = Number(s.dec);
                if (Number.isFinite(ra) && Number.isFinite(dec)) {
                    raVals.push(ra);
                    decVals.push(dec);
                    labels.push(i);
                }
            }

            let c = _polarisCharts.guide;
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
                            // are both visible, auto-fit would otherwise
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
                _polarisCharts.guide = c;
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
            // Visible heartbeat, increments even if line shape barely
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

        // Live-stack overlay chart: SNR cumulative (primary) +
        // per-frame HFR (secondary), indexed by image #. SNR is the
        // headline number — "is my stack getting better?" — HFR
        // stays as the secondary line for focus drift diagnostics.
        // Star count moved into the chart tooltip to keep the
        // overlay visually quiet (3-line charts get noisy fast).
        updateHfrChart() {
            const t = this._chartTheme();
            const c = this._ensureChart('hfrChart', 'hfr', 'line', () => ({
                type: 'line',
                data: {
                    labels: [],
                    datasets: [
                        { label: 'SNR', data: [], borderColor: '#4fc3f7', backgroundColor: 'transparent',
                          yAxisID: 'y', tension: 0.2, pointRadius: 2, borderWidth: 1.8 },
                        { label: 'HFR', data: [], borderColor: '#ffb74d', backgroundColor: 'transparent',
                          yAxisID: 'y1', tension: 0.2, pointRadius: 2, borderWidth: 1.2 }
                    ]
                },
                options: {
                    responsive: true, maintainAspectRatio: false, animation: false,
                    plugins: {
                        legend: { labels: { color: t.color, font: { size: 10 } } },
                        tooltip: {
                            callbacks: {
                                // Star count surfaced in the tooltip
                                // (not its own line) so the chart
                                // doesn't get a third Y axis.
                                afterBody: (items) => {
                                    if (!items?.length) return '';
                                    const i = items[0].dataIndex;
                                    const hist = (this.imageHistory || []).slice().reverse();
                                    const stars = parseInt(hist[i]?.stars) || 0;
                                    return stars > 0 ? `Stars: ${stars}` : '';
                                }
                            }
                        }
                    },
                    scales: {
                        x: { ticks: { color: t.tick, font: { size: 10 } }, grid: { color: t.grid } },
                        y: { position: 'left', beginAtZero: true,
                             ticks: { color: '#4fc3f7', font: { size: 10 } }, grid: { color: t.grid },
                             title: { display: true, text: 'SNR', color: '#4fc3f7', font: { size: 10 } } },
                        y1: { position: 'right', beginAtZero: true,
                              ticks: { color: '#ffb74d', font: { size: 10 } }, grid: { display: false },
                              title: { display: true, text: 'HFR', color: '#ffb74d', font: { size: 10 } } }
                    }
                }
            }));
            if (!c) return;
            // imageHistory is newest-first → reverse for chronological order
            const hist = (this.imageHistory || []).slice().reverse();
            c.data.labels = hist.map((_, i) => i + 1);
            c.data.datasets[0].data = hist.map(h => parseFloat(h.snr) || 0);
            c.data.datasets[1].data = hist.map(h => parseFloat(h.hfr) || 0);
            c.update('none');
        },

        // SNR-7: format ETA seconds → human-friendly "~12 min" /
        // "~45 s" / "—". Pass the liveStackStatus payload so we can
        // decorate "✓ done" when the target's already reached.
        formatSnrEta(ls) {
            if (!ls) return '—';
            if (ls.cumulativeSnr > 0 && ls.targetSnr > 0
                && ls.cumulativeSnr >= ls.targetSnr) {
                return '✓ done';
            }
            const sec = ls.etaSeconds;
            if (sec == null || !isFinite(sec) || sec <= 0) return '—';
            if (sec < 60) return `~${Math.round(sec)} s`;
            if (sec < 3600) return `~${Math.round(sec / 60)} min`;
            const h = Math.floor(sec / 3600);
            const m = Math.round((sec % 3600) / 60);
            return `~${h} h ${m} m`;
        },

        // SNR-7: debounced PUT for the LIVE tab's target-SNR input.
        // Null / 0 in the input means "drop the override, use the
        // active rig's TargetSnr". Same debounce pattern as the
        // dither / max-duration inputs.
        saveTargetSnr() {
            if (this.liveStack._saveTargetTimer) {
                clearTimeout(this.liveStack._saveTargetTimer);
            }
            this.liveStack._saveTargetTimer = setTimeout(async () => {
                try {
                    const v = this.liveStack.targetSnrInput;
                    await this.apiPost('/api/livestack/target-snr', {
                        targetSnr: (v == null || v === '' || v <= 0) ? null : v
                    }, { method: 'PUT' });
                } catch (e) {
                    this.toast('Could not save target SNR: ' + e.message, 'error');
                }
            }, 400);
        },

        // Temperature chart: sensor temp + cooler power vs time
        updateTempChart() {
            // Guard against Chart.js's "Cannot set properties of undefined
            // (setting 'fullSize')", fires when the canvas is in the DOM
            // but its parent has zero measured size (initial paint pass,
            // x-show transition). Wait until the canvas has real pixels.
            const canvas = this.$refs.tempChart;
            if (!canvas || typeof Chart === 'undefined') return;
            if (!_polarisCharts.temp
                && (canvas.clientWidth < 10 || canvas.clientHeight < 10)) return;

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
            // Per-rig save-each-frame toggle. Defaults ON when the
            // field is missing (matches the new server-side default
            // post-redesign) so legacy rigs adopt the same friendly
            // behaviour without a manual flip.
            this.liveStackSaveFrames = rig.liveStackSaveFramesToDisk !== false;
            // Auto-pause cap in MINUTES (UI unit). Backend stores
            // seconds. 0 = unlimited (default).
            this.liveStackMaxMinutes = Math.round(
                (rig.liveStackMaxDurationSeconds || 0) / 60);
            // VIDEO tab FOV / ROI hydration. Restore the last-picked
            // crop so opening VIDEO after a reload lands on the same
            // box around the planet, instead of forcing the user to
            // re-pick a 640 ROI every session. Old rigs (no fields)
            // boot at full sensor.
            this.video.roiW = rig.lastVideoRoiW || 0;
            this.video.roiH = rig.lastVideoRoiH || 0;
            this.video.roiX = rig.lastVideoRoiX || 0;
            this.video.roiY = rig.lastVideoRoiY || 0;
            this.video.roiSize = rig.lastVideoRoiSize || 0;
            this.video.roiAspect = rig.lastVideoRoiAspect || 'square';
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
            this.focuserDriver = rig.focuserDriver || 'indi';
            this.equipFilterChoice = rig.filterWheel || '';
            this.filterWheelDriver = rig.filterWheelDriver || 'indi';
            this.equipRotatorChoice = rig.rotator || '';
            this.equipFlatChoice = rig.flatDevice || '';
            this.equipDomeChoice = rig.dome || '';
            this.equipWeatherChoice = rig.weather || '';
            if (rig.coolerTargetTemperature != null) this.equipCoolerTarget = rig.coolerTargetTemperature;
            // Hydrate FAST step from the rig profile (legacy field
            // name kept for backwards compat). SLOW step lives in
            // localStorage only -- per-rig persistence would need a
            // ProfileService field which we'll add in a follow-up if
            // operators want it per-rig.
            if (rig.focuserStepSize) {
                this.focusStepFast = rig.focuserStepSize;
                this.focusStep = rig.focuserStepSize;
            }
            this._loadFocusStepsFromLocalStorage();
            // PA-4: hydrate polar alignment TPPA tunables from the rig.
            this._hydratePolarSettingsFromRig(rig);
            if (rig.focalLengthMm) {
                this.settings.focalLength = rig.focalLengthMm;
                this.updateFov();
            }
            // OTA optics, hydrate the Main Telescope card on the RIGS tab.
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

        // Debounced PUT, covers focal length / cooler target / etc. inline edits
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
            // Re-sync the Main Telescope / Guidescope dropdowns to the
            // active rig's saved brand + model. Without this step, the
            // common load order (loadRigs resolves first, then this
            // catalogue) leaves the <select>s stuck on "Manual entry"
            // and "Pick a model": when _applyRigToChoices set
            // settings.telescopeBrand the brand <option> template was
            // still empty, so the native select silently dropped the
            // value, and Alpine's x-model never re-evaluates on
            // template population alone. Toggling the value through
            // null + back via $nextTick forces the select to pick up
            // the now-present option.
            const active = this.rigs?.find(r => r.id === this.activeRigId);
            if (active) {
                const bt = active.telescopeBrand || '';
                const mt = active.telescopeModel || '';
                const bg = active.guideTelescopeBrand || '';
                const mg = active.guideTelescopeModel || '';
                this.settings.telescopeBrand = '';
                this.settings.telescopeModel = '';
                this.settings.guideTelescopeBrand = '';
                this.settings.guideTelescopeModel = '';
                await this.$nextTick();
                this.settings.telescopeBrand = bt;
                this.settings.guideTelescopeBrand = bg;
                await this.$nextTick();
                this.settings.telescopeModel = mt;
                this.settings.guideTelescopeModel = mg;
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
                // Base scope back-focus, overridden below if the
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

        // Settings-mirror version of _applyOpticsToRig, same lookup,
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
        // would PUT the whole rig, 600 ms is long enough that the
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

        // True if at least one accessory is configured, the
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
                focuserDriver: this.focuserDriver || rig.focuserDriver || 'indi',
                filterWheel: this.equipFilterChoice || rig.filterWheel,
                filterWheelDriver: this.filterWheelDriver || rig.filterWheelDriver || 'indi',
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
            } catch (e) { /* not fatal, defaults stand */ }
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
            if (!parts.length) return '';
            return parts.join(' · ') + (ea.runOnStop ? ' · also on stop' : '');
        },

        // Whether ANY end-action toggle is on — used by the compact
        // autorun-options-bar chip to paint the green status dot
        // without re-running endActionsSummary's string formatter.
        endActionsHasAny() {
            const ea = this.endActions || {};
            return !!(ea.parkMount || ea.stopTracking || ea.warmCamera
                   || ea.disconnectGuider || ea.autoGraXpert);
        },

        // Debounced PUT, fires 400ms after the last edit
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
                        autoConnectOnStartup: this.settings.autoConnectOnStartup,
                        // DBGLOG-9: opt-in disk persistence.
                        logToDisk: this.settings.logToDisk,
                        sirilPath: this.settings.sirilPath,
                        sirilScriptsDir: this.settings.sirilScriptsDir,
                        graxpertPath: this.settings.graxpertPath,
                        graxpertBgeSmoothing: this.settings.graxpertBgeSmoothing,
                        graxpertBgeCorrection: this.settings.graxpertBgeCorrection,
                        graxpertDeconStrength: this.settings.graxpertDeconStrength,
                        graxpertDeconPsfSize: this.settings.graxpertDeconPsfSize,
                        graxpertDenoiseStrength: this.settings.graxpertDenoiseStrength,
                        onnxModelsPath: this.settings.onnxModelsPath,
                        onnxLicenseAcknowledged: this.settings.onnxLicenseAcknowledged,
                        onnxDefaultDenoiseVersion: this.settings.onnxDefaultDenoiseVersion,
                        onnxPreferCli: this.settings.onnxPreferCli
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
                // After the device list is in place, re-apply the
                // connected-device names to the per-card dropdowns.
                // We have to do this AFTER `devices` populates because
                // a <select> can't pick a value whose matching <option>
                // doesn't exist yet, the browser silently resets it
                // to the first option ("Select device"). The WS handler
                // does an early write to equip*Choice, but that lands
                // before this fetch returns. Re-apply here so the
                // dropdowns end up correct regardless of timing.
                this._syncEquipChoicesFromConnected();
            } catch (e) {
                this.toast('Failed to refresh devices', 'error');
            }
        },

        // Mirror connected device names into the RIGS-tab dropdowns.
        // Two-step per device:
        //   1. If the choice points at a name not present in the current
        //      devices list, clear it. This catches the very common case
        //      where the saved rig profile carries a stale device name
        //      ("ZWO ASI120MM" but tonight's indiserver only exposes
        //      "CCD Simulator"), without clearing, the truthy stale
        //      value silently blocks step 2 from running.
        //   2. If the choice is now empty AND the live equipment payload
        //      reports a connected device whose name DOES match a current
        //      dropdown option, set the choice to that name. The
        //      dropdown then shows the connected device pre-selected.
        // Called from refreshDevices() (after the list arrives) and from
        // the WS handler each tick (cheap; only mutates when needed).
        _syncEquipChoicesFromConnected() {
            if (!Array.isArray(this.devices) || this.devices.length === 0) return;
            const names = new Set(this.devices.filter(d => d && d.name).map(d => d.name));
            // Camera
            if (this.equipCameraChoice && !names.has(this.equipCameraChoice)) {
                this.equipCameraChoice = '';
            }
            if (!this.equipCameraChoice && this.selectedCamera
                && names.has(this.selectedCamera)) {
                this.equipCameraChoice = this.selectedCamera;
            }
            // Mount
            if (this.equipMountChoice && !names.has(this.equipMountChoice)) {
                this.equipMountChoice = '';
            }
            if (!this.equipMountChoice && this.selectedTelescope
                && names.has(this.selectedTelescope)) {
                this.equipMountChoice = this.selectedTelescope;
            }
            // Focuser
            if (this.equipFocuserChoice && !names.has(this.equipFocuserChoice)) {
                this.equipFocuserChoice = '';
            }
            if (!this.equipFocuserChoice && this.selectedFocuser
                && names.has(this.selectedFocuser)) {
                this.equipFocuserChoice = this.selectedFocuser;
            }
            // Filter wheel
            if (this.equipFilterChoice && !names.has(this.equipFilterChoice)) {
                this.equipFilterChoice = '';
            }
            if (!this.equipFilterChoice && this.selectedFilterWheel
                && names.has(this.selectedFilterWheel)) {
                this.equipFilterChoice = this.selectedFilterWheel;
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
            // SHUT-1: snapshot exposure + start time for the shutter's
            // progress ring. Snapshot so changing the exposure input
            // mid-capture doesn't rescale the ring.
            this._captureStartedAt = Date.now();
            this._captureExposure = Number(this.exposure) || 0;
            this._startShutterTick();
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
                    this.stats.snr = data.stats.snr?.toFixed(1);
                }
                this.imageHistory.unshift({
                    id: 'h-' + Date.now() + '-' + Math.random().toString(36).slice(2, 7),
                    time: new Date().toLocaleTimeString('en-GB'),
                    exposure: this.exposure,
                    gain: this.gain,
                    filter: this.filterWheel.connected ? this.filterWheel.currentFilter : null,
                    stars: data.stats?.starCount || '--',
                    hfr: data.stats?.hfr?.toFixed(2) || '--',
                    snr: data.stats?.snr?.toFixed(1) || '--',
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
                if (!this.looping) {
                    this.capturing = false;
                    this._captureStartedAt = null;
                } else {
                    // Reset start-time for the next loop iteration so
                    // the ring restarts from 0 instead of carrying over
                    // residual elapsed from the previous frame.
                    this._captureStartedAt = Date.now();
                    this._captureExposure = Number(this.exposure) || 0;
                }
            }
        },

        async loopCapture() {
            this.looping = true;
            await this.capture();
        },

        async stopCapture() {
            this.looping = false;
            this.capturing = false;
            this._captureStartedAt = null;
            try { await this.apiPost('/api/camera/abort'); } catch (e) { }
        },

        // ----- SHUT-1: shared shutter component glue -----

        /// Returns true while ANY tab's capture is in flight. Used by
        /// the periodic tick to know when to keep ticking vs stop.
        _anyShutterActive() {
            return this.capturing
                || this.looping
                || (this.preview && (this.preview.busy || this.preview.looping))
                || (this.manualFocus && this.manualFocus.running)
                || (this.videoRecording && this.videoRecording.recording)
                || this.seqState === 'running'
                || (this.flatWizard && this.flatWizard.state === 'running');
        },

        /// Start the 50ms tick that drives the shutter ring's
        /// smooth countdown. Idempotent — if already running, no-op.
        /// Auto-stops itself when _anyShutterActive() drops to false.
        _startShutterTick() {
            if (this._shutterRafTimer) return;
            this._shutterRafTimer = setInterval(() => {
                if (this._anyShutterActive() || this.armingLoop) {
                    // Cheap mutation that invalidates any computed
                    // reading shutterTick. Wraparound at 1M avoids
                    // any potential int-precision drift over very
                    // long sessions (1M ticks @ 50ms = ~14h).
                    this.shutterTick = (this.shutterTick + 1) % 1000000;
                } else {
                    clearInterval(this._shutterRafTimer);
                    this._shutterRafTimer = null;
                }
            }, 50);
        },

        /// Compute progress (0..1) given a startedAt timestamp + total
        /// duration in seconds. Returns 0 when startedAt is falsy.
        /// Reads shutterTick so Alpine knows to recompute on each tick.
        _shutterProgressFor(startedAt, durationSec) {
            // eslint-disable-next-line no-unused-expressions
            this.shutterTick;   // dependency for reactivity
            if (!startedAt || !durationSec || durationSec <= 0) return 0;
            const elapsed = (Date.now() - startedAt) / 1000;
            return Math.max(0, Math.min(1, elapsed / durationSec));
        },

        /// Compute the dashoffset for a SHUT progress = 1 - progress
        /// multiplied by the SVG circumference (289 for r=46).
        _shutterDashoffsetFor(progress) {
            return 289 * (1 - Math.max(0, Math.min(1, progress)));
        },

        /// Human-readable countdown label. Shows remaining seconds
        /// when an exposure is active and durationSec is known.
        /// Returns the empty string when nothing's running so the
        /// label slot collapses.
        _shutterCountdownFor(startedAt, durationSec) {
            // eslint-disable-next-line no-unused-expressions
            this.shutterTick;
            if (!startedAt) return '';
            if (!durationSec || durationSec <= 0) return '...';
            const elapsed = (Date.now() - startedAt) / 1000;
            const remaining = Math.max(0, durationSec - elapsed);
            if (remaining < 10) return remaining.toFixed(1) + 's';
            return Math.ceil(remaining) + 's';
        },

        // ----- Gesture state machine -----

        shutterPointerDown(ev, ctx) {
            if (!ctx) return;
            // Mouse: only respond to primary button. Touch + pen
            // have no button concept; pass through.
            if (ev.pointerType === 'mouse' && ev.button !== 0) return;
            ev.preventDefault();
            // Disabled shutter: no-op (matches the visual cue from
            // aria-disabled="true").
            if (ctx.disabled && ctx.disabled()) return;
            // Active state ignores long-press entirely — tap = abort
            // is the only gesture here. The release handler decides.
            if (ctx.isActive && ctx.isActive()) return;
            // Long-press arming starts. We use a separate animator at
            // 60ms cadence so the ring smoothly fills during the
            // 600ms hold and gives the user visual feedback that
            // they're about to commit to a loop instead of a snap.
            this._shutterLongPressed = false;
            this.armingLoop = true;
            this._shutterArmStartedAt = Date.now();
            this._startShutterTick();   // also drives the arming animation
            this._shutterPressTimer = setTimeout(() => {
                this._shutterLongPressed = true;
                this.armingLoop = false;
                this._shutterArmStartedAt = 0;
                if (ctx.onLongPress) {
                    try { ctx.onLongPress(); }
                    catch (e) { console.warn('shutter onLongPress threw', e); }
                }
            }, 600);
        },

        shutterPointerUp(ev, ctx) {
            if (!ctx) return;
            if (this._shutterPressTimer) {
                clearTimeout(this._shutterPressTimer);
                this._shutterPressTimer = null;
            }
            this.armingLoop = false;
            this._shutterArmStartedAt = 0;
            if (this._shutterLongPressed) {
                // Long-press already fired onLongPress in the timeout.
                // The release is a no-op; we just reset the flag.
                this._shutterLongPressed = false;
                return;
            }
            if (ctx.disabled && ctx.disabled()) return;
            if (ctx.isActive && ctx.isActive()) {
                if (ctx.onAbort) {
                    try { ctx.onAbort(); }
                    catch (e) { console.warn('shutter onAbort threw', e); }
                }
            } else {
                if (ctx.onTap) {
                    try { ctx.onTap(); }
                    catch (e) { console.warn('shutter onTap threw', e); }
                }
            }
        },

        shutterPointerCancel() {
            // Pointer left the button or got cancelled by the
            // browser (scroll, alert dialog, etc.). Clean up the
            // arming state so we don't fire a stale long-press.
            if (this._shutterPressTimer) {
                clearTimeout(this._shutterPressTimer);
                this._shutterPressTimer = null;
            }
            this.armingLoop = false;
            this._shutterLongPressed = false;
            this._shutterArmStartedAt = 0;
        },

        /// Progress used by the ring while the user is holding to arm
        /// a loop. Returns 0..1 across the 600ms hold window.
        _shutterArmProgress() {
            // eslint-disable-next-line no-unused-expressions
            this.shutterTick;
            if (!this.armingLoop || !this._shutterArmStartedAt) return 0;
            const elapsed = Date.now() - this._shutterArmStartedAt;
            return Math.max(0, Math.min(1, elapsed / 600));
        },

        // ----- Per-tab context objects -----

        /// LIVE tab shutter context.
        /// Tap on LIVE means "start continuous capture + stacking".
        /// Old behaviour was tap=single, long-press=loop, but with the
        /// always-on stacker the natural intent of a LIVE tap is
        /// "begin a session" not "give me exactly one frame". Long-
        /// press still kicks the loop explicitly for the user who
        /// builds muscle memory across tabs. Tap-while-active aborts.
        liveShutterCtx() {
            return {
                isActive: () => this.capturing || this.looping,
                disabled: () => !this.selectedCamera,
                onTap: () => this.loopCapture(),
                onLongPress: () => this.loopCapture(),
                onAbort: () => this.stopCapture()
            };
        },

        /// Progress for the LIVE shutter. Returns 0..1, prefers the
        /// real capture progress; falls back to the arming-loop fill
        /// while the user is holding.
        liveShutterProgress() {
            if (this.armingLoop) return this._shutterArmProgress();
            return this._shutterProgressFor(this._captureStartedAt, this._captureExposure);
        },
        liveShutterDashoffset() {
            return this._shutterDashoffsetFor(this.liveShutterProgress());
        },
        liveShutterCountdown() {
            if (this.armingLoop) return 'hold for loop...';
            if (!this.capturing && !this.looping) return '';
            return this._shutterCountdownFor(this._captureStartedAt, this._captureExposure);
        },

        /// PREVIEW tab shutter context.
        previewShutterCtx() {
            return {
                isActive: () => !!(this.preview.busy || this.preview.looping),
                disabled: () => !this.selectedCamera || this.cameraStream.running,
                onTap: () => this.previewTakeSnap(),
                onLongPress: () => this.previewToggleLoop(),
                onAbort: () => this.previewAbort()
            };
        },
        previewShutterProgress() {
            if (this.armingLoop) return this._shutterArmProgress();
            return this._shutterProgressFor(
                this.preview._snapStartedAt, this.preview._snapExposure);
        },
        previewShutterDashoffset() {
            return this._shutterDashoffsetFor(this.previewShutterProgress());
        },
        previewShutterCountdown() {
            if (this.armingLoop) return 'hold for loop...';
            if (!this.preview.busy && !this.preview.looping) return '';
            return this._shutterCountdownFor(
                this.preview._snapStartedAt, this.preview._snapExposure);
        },

        /// FOCUS Manual Assist shutter context. Tap = single snap,
        /// long-press = start loop, tap-while-active = stop loop.
        focusShutterCtx() {
            return {
                isActive: () => !!this.manualFocus.running,
                disabled: () => !this.selectedCamera,
                onTap: () => this.manualFocusSnap(),
                onLongPress: () => this.manualFocusStart(),
                onAbort: () => this.manualFocusStop()
            };
        },
        /// Progress for FOCUS shutter. While looping, fills 0 to 1
        /// across each intervalSec cycle so the user can see the next
        /// snap coming. Idle single-snap: leaves the ring empty (the
        /// snap is a one-shot, no meaningful "progress").
        focusShutterProgress() {
            if (this.armingLoop) return this._shutterArmProgress();
            if (!this.manualFocus.running) return 0;
            return this._shutterProgressFor(
                this.manualFocus._tickStartedAt,
                this.manualFocus.intervalSec || 1);
        },
        focusShutterDashoffset() {
            return this._shutterDashoffsetFor(this.focusShutterProgress());
        },
        focusShutterCountdown() {
            if (this.armingLoop) return 'hold for loop...';
            if (!this.manualFocus.running) return '';
            return this._shutterCountdownFor(
                this.manualFocus._tickStartedAt,
                this.manualFocus.intervalSec || 1);
        },

        /// VIDEO Capture shutter context. Record is a single toggle
        /// (no separate snap vs loop), so long-press maps to the same
        /// action as tap, and tap-while-active is the stop.
        videoShutterCtx() {
            return {
                isActive: () => !!this.videoRecording.recording,
                disabled: () => !this.selectedCamera,
                onTap: () => this.videoToggleRecord(),
                onLongPress: () => this.videoToggleRecord(),
                onAbort: () => this.videoToggleRecord()
            };
        },
        /// Progress for VIDEO shutter. With a max duration set we
        /// show recorded elapsed / max. Without one, we hand back
        /// 0 and the template flips the shutter into the
        /// .shutter-indeterminate spinner via shutter-indeterminate
        /// class.
        videoShutterIndeterminate() {
            return !!this.videoRecording.recording
                && !(this.video.maxDurationSec > 0);
        },
        videoShutterProgress() {
            if (this.armingLoop) return this._shutterArmProgress();
            if (!this.videoRecording.recording) return 0;
            if (!(this.video.maxDurationSec > 0)) return 0;
            return Math.max(0, Math.min(1,
                (this.videoRecording.durationSec || 0)
                / this.video.maxDurationSec));
        },
        videoShutterDashoffset() {
            return this._shutterDashoffsetFor(this.videoShutterProgress());
        },
        videoShutterCountdown() {
            if (this.armingLoop) return 'hold...';
            if (!this.videoRecording.recording) return '';
            const f = this.videoRecording.frames || 0;
            if (this.video.maxDurationSec > 0) {
                const remaining = Math.max(0,
                    this.video.maxDurationSec
                    - (this.videoRecording.durationSec || 0));
                return f + ' · ' + remaining.toFixed(0) + 's left';
            }
            return f + ' frames';
        },

        /// AUTORUN shutter context. Tap = startSequence(),
        /// long-press = same (no distinct loop), tap-while-active =
        /// stopSequence(). Pause/Resume stays as a separate button
        /// in the sidebar since it's a third state that doesn't fit
        /// the tap/long-press/abort grammar.
        autorunShutterCtx() {
            return {
                isActive: () => this.seqState === 'running'
                    || this.seqState === 'paused',
                disabled: () => this.sequence.length === 0,
                onTap: () => {
                    if (this.seqState === 'idle') this.startSequence();
                },
                onLongPress: () => {
                    if (this.seqState === 'idle') this.startSequence();
                },
                onAbort: () => this.stopSequence()
            };
        },
        /// Progress for AUTORUN. Reads the existing seqProgress()
        /// helper (frames completed / total) so the ring shows the
        /// progress of the whole sequence, not the current sub-
        /// exposure. Returns 0..1.
        autorunShutterProgress() {
            if (this.armingLoop) return this._shutterArmProgress();
            if (this.seqState === 'idle') return 0;
            // seqProgress() returns 0-100 (integer percent).
            return Math.max(0, Math.min(1, (this.seqProgress() || 0) / 100));
        },
        autorunShutterDashoffset() {
            return this._shutterDashoffsetFor(this.autorunShutterProgress());
        },
        autorunShutterCountdown() {
            if (this.armingLoop) return 'hold...';
            if (this.seqState === 'idle') return '';
            const done = this.seqStatus?.totalFramesCompleted || 0;
            const total = this.seqStatus?.totalFrames || 0;
            if (this.seqState === 'paused') return done + '/' + total + ' · paused';
            return done + '/' + total;
        },

        // ----- FW-2: Flat Wizard tab glue -----

        /// Hydrate form + trained cache when the user clicks the
        /// Flat Wizard sub-tab. Reads from active rig (already
        /// loaded into this.rigs) and fetches /api/flatwizard/trained.
        async flatWizardOpenTab() {
            const rig = this.rigs?.find(r => r.id === this.activeRigId);
            const s = rig?.flatWizard;
            if (s) {
                this.flatWizard.targetAdu = s.targetAdu ?? 30000;
                this.flatWizard.tolerance = s.tolerance ?? 0.05;
                this.flatWizard.framesPerFilter = s.framesPerFilter ?? 20;
                this.flatWizard.minExposureSec = s.minExposureSec ?? 0.1;
                this.flatWizard.maxExposureSec = s.maxExposureSec ?? 30.0;
                this.flatWizard.binning = s.binning ?? 1;
                this.flatWizard.maxSearchIterations = s.maxSearchIterations ?? 10;
                this.flatWizard.panelBrightness = s.panelBrightness ?? 0;
            }
            try {
                const trained = await this.apiGet('/api/flatwizard/trained');
                this.flatWizard.trained = trained || {};
            } catch (e) {
                /* first run before any save, ignore */
            }
        },

        flatWizardToggleFilter(f) {
            const idx = this.flatWizard.selectedFilters.indexOf(f);
            if (idx >= 0) this.flatWizard.selectedFilters.splice(idx, 1);
            else this.flatWizard.selectedFilters.push(f);
        },
        flatWizardSelectAll() {
            this.flatWizard.selectedFilters = [...(this.filterWheel.filters || [])];
        },
        flatWizardClearFilters() {
            this.flatWizard.selectedFilters = [];
        },

        /// Debounced PUT into the active rig. Settings live on
        /// EquipmentProfile.FlatWizard (FW-1). Reuses saveRig() so
        /// it gets the same 400ms debounce + error-toast surface that
        /// every other rig-side input uses.
        flatWizardSave() {
            const rig = this.rigs?.find(r => r.id === this.activeRigId);
            if (!rig) return;
            rig.flatWizard = {
                targetAdu: this.flatWizard.targetAdu,
                tolerance: this.flatWizard.tolerance,
                framesPerFilter: this.flatWizard.framesPerFilter,
                minExposureSec: this.flatWizard.minExposureSec,
                maxExposureSec: this.flatWizard.maxExposureSec,
                binning: this.flatWizard.binning,
                maxSearchIterations: this.flatWizard.maxSearchIterations,
                panelBrightness: this.flatWizard.panelBrightness
            };
            this.saveRig(rig);
        },

        async flatWizardStart() {
            if (this.flatWizard.selectedFilters.length === 0) {
                this.toast('Pick at least 1 filter', 'warn');
                return;
            }
            if (!this.selectedCamera) {
                this.toast('Connect a camera first', 'warn');
                return;
            }
            // If a flat panel is connected and the user picked a non-zero
            // brightness, set it before kicking the wizard. 0 means
            // "don't touch the panel" — sky / T-shirt flats.
            if (this.flatDevice?.connected && this.flatWizard.panelBrightness > 0) {
                try {
                    await this.apiPost('/api/flatdevice/brightness', null, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ brightness: this.flatWizard.panelBrightness })
                    });
                } catch (e) {
                    this.toast('Set panel brightness failed: ' + e.message, 'warn');
                    // keep going — user may want to proceed without panel
                }
            }
            try {
                await this.apiPost('/api/flatwizard/start', null, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        filters: this.flatWizard.selectedFilters,
                        framesPerFilter: this.flatWizard.framesPerFilter,
                        targetAdu: this.flatWizard.targetAdu,
                        tolerance: this.flatWizard.tolerance,
                        minExposure: this.flatWizard.minExposureSec,
                        maxExposure: this.flatWizard.maxExposureSec,
                        binning: this.flatWizard.binning,
                        maxSearchIterations: this.flatWizard.maxSearchIterations
                    })
                });
                this._startShutterTick();
            } catch (e) {
                this.toast('Start flat wizard failed: ' + e.message, 'error');
            }
        },
        async flatWizardAbort() {
            try {
                await this.apiPost('/api/flatwizard/abort');
            } catch (e) {
                this.toast('Abort failed: ' + e.message, 'warn');
            }
        },

        /// Per-tab shutter context. Tap and long-press both fire start
        /// (no separate loop concept — the wizard runs to completion
        /// once kicked). Tap-during-active aborts.
        flatWizardShutterCtx() {
            return {
                isActive: () => this.flatWizard.state === 'running',
                disabled: () => !this.selectedCamera
                    || !this.filterWheel?.connected
                    || this.flatWizard.selectedFilters.length === 0,
                onTap: () => this.flatWizardStart(),
                onLongPress: () => this.flatWizardStart(),
                onAbort: () => this.flatWizardAbort()
            };
        },
        /// Progress for the AUTORUN > Flat Wizard shutter ring.
        /// Composite progress: (filtersDone + frames-in-current-filter)
        /// / totalFilters. Returns 0..1.
        flatWizardShutterProgress() {
            if (this.armingLoop) return this._shutterArmProgress();
            const p = this.flatWizard.progress;
            if (this.flatWizard.state !== 'running' || !p || !p.totalFilters) return 0;
            const frameFrac = (p.framesCaptured || 0)
                / Math.max(1, p.totalFramesPerFilter || 1);
            const overall = ((p.currentFilterIndex || 0) + frameFrac)
                / p.totalFilters;
            return Math.max(0, Math.min(1, overall));
        },
        flatWizardShutterDashoffset() {
            return this._shutterDashoffsetFor(this.flatWizardShutterProgress());
        },
        flatWizardShutterCountdown() {
            if (this.armingLoop) return 'hold...';
            if (this.flatWizard.state !== 'running') return '';
            const p = this.flatWizard.progress;
            if (!p) return 'starting...';
            const f = p.currentFilter || '?';
            const phase = p.phase || '';
            return f + ' · ' + phase;
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
            // SHUT-2: snapshot for the PREVIEW shutter ring.
            this.preview._snapStartedAt = Date.now();
            this.preview._snapExposure = Number(this.preview.exposure) || 0;
            this._startShutterTick();
            try {
                // apiPost returns a Response object, we need .json()
                // to get the actual { stats, saved, ... } payload.
                // Use a per-request timeout proportional to exposure
                // (default apiFetch timeout is 15s, which is too short
                // for a 30s sub).
                const previewBody = {
                    exposure: this.preview.exposure,
                    gain: this.preview.gain,
                    binning: parseInt(this.preview.binning) || 1,
                    filter: this.preview.filter || null,
                    saveToDisk: !!this.preview.saveToDisk,
                    targetName: this.preview.targetName || 'snap',
                    // PREVIEW is "test shot to check framing/focus" —
                    // never feed the live stack, otherwise the
                    // always-on stacker counts these frames + fires
                    // the LSTR auto-recenter plate solve on the
                    // first preview snap of every session.
                    feedLiveStack: false,
                    // kind=preview routes the broadcast frame to
                    // previewCanvas only, leaving the LIVE canvas
                    // untouched. Without this every preview tap
                    // overwrites the live-stack accumulator's display.
                    kind: 'preview'
                };
                // Diag log -- if the user reports 'preview frame ends
                // up on the LIVE canvas / live stack', open DevTools
                // and confirm this log shows feedLiveStack:false +
                // kind:'preview'. If it doesn't, the browser is
                // serving stale JS -- hard-refresh (Ctrl+Shift+R).
                console.log('[Polaris] previewTakeSnap body:', previewBody);
                const resp = await this.apiPost('/api/camera/capture', previewBody, {
                    timeout: Math.max(15000, (this.preview.exposure + 30) * 1000)
                });
                const r = await resp.json();
                this.preview.lastStats = r?.stats || null;
                this.preview.lastSnapAt = Date.now();
                // Snap fired successfully, surface a quick confirmation
                // (the actual image lands on previewCanvas via the WS
                // image-stream broadcast that the backend kicked off).
                if (r?.saved) {
                    this.toast('Snap saved · HFR ' + (r.stats?.hfr?.toFixed?.(2) || '--')
                        + ' · ' + (r.stats?.starCount ?? '--') + ' stars', 'ok', 2500);
                } else {
                    this.toast('Snap done · HFR ' + (r.stats?.hfr?.toFixed?.(2) || '--')
                        + ' · ' + (r.stats?.starCount ?? '--') + ' stars', 'ok', 2000);
                }
            } catch (e) {
                this.toast('Snap failed: ' + (e.message || ''), 'error');
                // Break the loop on error, don't hammer the camera
                // with a guaranteed-to-fail sequence of requests.
                this.preview.looping = false;
            } finally {
                this.preview.busy = false;
                if (!this.preview.looping) {
                    this.preview._snapStartedAt = null;
                }
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
        // Frames pipe through the existing /ws/image-stream channel,
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

        // Capture side, wraps the existing /api/camera/stream endpoints
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

        // Camera capability probe, populates cameraCaps so WB / ROI /
        // ISO controls show/hide per-camera. Called on VIDEO tab open
        // and after a camera swap. Tolerates 400 responses (no camera
        // selected yet) by leaving the cached flags as-is.
        async loadCameraCapabilities() {
            try {
                const r = await this.apiGet('/api/camera/status');
                if (r && r.capabilities) {
                    this.cameraCaps = Object.assign({}, this.cameraCaps, r.capabilities);
                }
                // Sensor dimensions feed the FOV pills (so a 1920 pill
                // greys out on a 1280-wide sensor) + the "centered ·
                // 640 × 640 on a 4144 × 2822 sensor" hint line.
                if (r && typeof r.maxX === 'number') this.cameraCaps.maxX = r.maxX;
                if (r && typeof r.maxY === 'number') this.cameraCaps.maxY = r.maxY;
                if (r && typeof r.whiteBalanceR === 'number') this.video.wbR = r.whiteBalanceR;
                if (r && typeof r.whiteBalanceB === 'number') this.video.wbB = r.whiteBalanceB;
                // If the rig had a saved ROI, push it to the camera
                // now (subframe sticks across server restarts but not
                // across camera reconnects, which is the more common
                // case). Skip when a stream is already running — the
                // driver would reject the change mid-exposure.
                if (this.video.roiW > 0 && this.video.roiH > 0
                    && !this.cameraStream.running
                    && this.cameraCaps.roi !== false) {
                    try {
                        const resp = await this.apiPost('/api/camera/subframe', {
                            x: this.video.roiX, y: this.video.roiY,
                            width: this.video.roiW, height: this.video.roiH
                        });
                        await resp.json();
                    } catch (e) { /* non-fatal; user can re-pick */ }
                }
            } catch (e) { /* no camera connected yet */ }
        },

        // POST /api/camera/subframe with width×height centered on the
        // sensor. width=height=0 clears the ROI. Square pills pass
        // (w==h, aspect='square', squareSize=size) so the square-row
        // active-pill check still works; rectangular pills pass
        // (different w,h, aspect='4:3'|'16:9', squareSize=0). Driver
        // applies the new geometry on the next capture; the UI disables
        // pills while a stream / record is in flight so we don't have
        // to restart the stream here.
        async videoSetRoiRect(width, height, aspect, squareSize) {
            let x = 0, y = 0, w = 0, h = 0;
            if (width > 0 && height > 0) {
                const mx = this.cameraCaps.maxX | 0;
                const my = this.cameraCaps.maxY | 0;
                w = mx > 0 ? Math.min(width, mx) : width;
                h = my > 0 ? Math.min(height, my) : height;
                x = mx > w ? Math.floor((mx - w) / 2) : 0;
                y = my > h ? Math.floor((my - h) / 2) : 0;
            }
            try {
                const resp = await this.apiPost('/api/camera/subframe',
                    { x, y, width: w, height: h });
                await resp.json();
                this.video.roiSize = squareSize | 0;
                this.video.roiW = w; this.video.roiH = h;
                this.video.roiX = x; this.video.roiY = y;
                this.video.roiAspect = aspect || 'square';
                await this._videoPersistRoi();
                this.toast((w === 0 || h === 0)
                    ? 'FOV: full sensor'
                    : `FOV: ${w} × ${h} centered`, 'ok');
            } catch (e) {
                this.toast('ROI set failed: ' + (e.message || 'driver rejected'), 'warn');
            }
        },

        // Click on the preview canvas while a ROI is active → re-center
        // the ROI on that point. Canvas always shows the current ROI's
        // pixels (the camera only captures what's inside the box), so a
        // click at (fx, fy) of the canvas in 0..1 maps to sensor coords
        // via (current ROI top-left) + fx * (current ROI width). New
        // ROI is the same width × height, just shifted so its centre
        // sits on the click.
        async videoCenterRoiAt(ev) {
            if (this.video.roiW <= 0 || this.video.roiH <= 0) return; // FULL = nothing to recenter
            if (!this.cameraStream.running) return;       // canvas idle, click would not match a frame
            const canvas = ev.currentTarget;
            const rect = canvas.getBoundingClientRect();
            if (rect.width <= 0 || rect.height <= 0) return;
            const fx = (ev.clientX - rect.left) / rect.width;
            const fy = (ev.clientY - rect.top) / rect.height;
            if (fx < 0 || fx > 1 || fy < 0 || fy > 1) return;
            // Sensor coordinates of the click (currently roi'd view)
            const sensorClickX = this.video.roiX + fx * this.video.roiW;
            const sensorClickY = this.video.roiY + fy * this.video.roiH;
            // New top-left so the click lands at the centre of the new ROI
            let nx = Math.floor(sensorClickX - this.video.roiW / 2);
            let ny = Math.floor(sensorClickY - this.video.roiH / 2);
            // Clamp to sensor bounds
            const mx = this.cameraCaps.maxX | 0;
            const my = this.cameraCaps.maxY | 0;
            if (mx > 0) nx = Math.max(0, Math.min(nx, mx - this.video.roiW));
            if (my > 0) ny = Math.max(0, Math.min(ny, my - this.video.roiH));
            try {
                const resp = await this.apiPost('/api/camera/subframe',
                    { x: nx, y: ny, width: this.video.roiW, height: this.video.roiH });
                await resp.json();
                this.video.roiX = nx;
                this.video.roiY = ny;
                this.video.roiHintDismissed = true;
                await this._videoPersistRoi();
            } catch (e) {
                this.toast('Re-center failed: ' + (e.message || 'driver rejected'), 'warn');
            }
        },

        // Persist the current ROI on the active rig so the next session
        // (or a tab reload) restores it without the user re-picking. The
        // backend stores LastVideoRoi{W,H,X,Y,Size,Aspect} on the rig;
        // null roiW means "always boot at FULL" so reloads on a known-
        // good setup do not surprise the user with a stale ROI.
        async _videoPersistRoi() {
            if (!this.equipmentProfile?.id) return;
            try {
                await this.apiPost(
                    '/api/equipment/rigs/' + encodeURIComponent(this.equipmentProfile.id),
                    null, {
                        method: 'PUT',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({
                            lastVideoRoiW: this.video.roiW,
                            lastVideoRoiH: this.video.roiH,
                            lastVideoRoiX: this.video.roiX,
                            lastVideoRoiY: this.video.roiY,
                            lastVideoRoiSize: this.video.roiSize,
                            lastVideoRoiAspect: this.video.roiAspect
                        })
                    });
            } catch (e) { /* non-fatal; ROI still applied on the device */ }
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

        // Process side, enumerates SER files under {ImageOutputDir}/planetary
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
                // Explicit null check: `temp || null` collapses 0 to
                // null because 0 is falsy, which silently swallowed
                // "Set target to 0 C" — the server got no target and
                // skipped the call. Numeric coerce + check covers
                // both string and number input (the number input
                // field still binds via x-model.number).
                const n = Number(temp);
                const target = Number.isFinite(n) ? n : null;
                await this.apiPost('/api/camera/cooler', {
                    enabled, targetTemperature: target
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
            } catch (e) { /* first load before any save, ignore */ }
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

        // REFSUG-2: dismiss the refocus-suggestion chip / callout.
        // resolved=true is the "I refocused" path, replaces the
        // baseline with the recent rolling HFR so the next eval uses
        // the post-refocus state as the new good. dismissOnly=true
        // just clears the chip without changing baseline (rare,
        // user wants to ack but trust the old reference).
        async refocusSuggestionResolved() {
            try {
                await this.apiPost('/api/livestack/refocus-suggestion/dismiss',
                    null, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ resetBaseline: true })
                    });
                this.toast('Baseline reset', 'ok');
            } catch (e) {
                this.toast('Dismiss failed: ' + e.message, 'error');
            }
        },
        async refocusSuggestionDismiss() {
            try {
                await this.apiPost('/api/livestack/refocus-suggestion/dismiss',
                    null, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ resetBaseline: false })
                    });
            } catch (e) {
                this.toast('Dismiss failed: ' + e.message, 'error');
            }
        },
        // Format helpers used by the trigger status lines.
        formatRelativeTime(iso) {
            if (!iso) return ', ';
            const t = new Date(iso).getTime();
            const dt = (Date.now() - t) / 1000;
            if (dt < 60) return Math.floor(dt) + 's ago';
            if (dt < 3600) return Math.floor(dt / 60) + 'm ago';
            return Math.floor(dt / 3600) + 'h ago';
        },
        formatRaDecShort(raHours, decDeg) {
            if (raHours == null || decDeg == null) return ', ';
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

        // Helper: pull the {error} field out of an ApiError body so
        // the toast shows the actual driver-side reason ("Device not
        // connected", "Property TELESCOPE_MOTION_NS not found", etc.)
        // instead of a generic "failed". apiFetch throws ApiError with
        // the response body in .message; that's typically JSON.
        _mountErrorText(e) {
            const msg = e?.message || String(e);
            try {
                const j = JSON.parse(msg);
                if (j?.error) return j.error;
            } catch { /* not JSON, fall through */ }
            return msg;
        },

        async mountMove(direction) {
            const dirMap = { n: 'north', s: 'south', e: 'east', w: 'west', stop: 'stop' };
            const dir = dirMap[direction] || direction;
            try {
                await this.apiPost(`/api/telescope/move/${dir}`);
                // Quiet success — the mount panel's status row updates
                // via the next WS tick (Track / RA / Dec / Slewing) and
                // the user sees the motion immediately.
            } catch (e) {
                this.toast(`Move ${dir} failed: ${this._mountErrorText(e)}`, 'error');
            }
        },

        // Press-and-hold variant for the d-pad. mountMoveStart fires
        // on pointerdown (mouse or touch) and is paired with
        // mountMoveStop on pointerup/leave/cancel. Holds a token so a
        // stray Stop call without a matching Start (browser fires
        // pointerleave before pointerdown sometimes during fast taps)
        // is a no-op instead of an extra abort. Captures the pointer
        // so that dragging the cursor off the button while held still
        // counts as "still pressing" -- only an explicit release stops
        // the motion. Matches the standard d-pad UX from ASIAIR /
        // SharpCap / NINA Desktop.
        _mountJogActive: null,
        async mountMoveStart(direction, evt) {
            if (!this.mount.connected) return;
            // Capture the pointer so subsequent pointerup fires on
            // the SAME button even if the user's finger / cursor
            // drifts outside its bounds while held.
            try {
                if (evt?.currentTarget?.setPointerCapture && evt.pointerId != null) {
                    evt.currentTarget.setPointerCapture(evt.pointerId);
                }
            } catch { /* setPointerCapture occasionally throws on rapid taps */ }
            this._mountJogActive = direction;
            await this.mountMove(direction);
        },
        async mountMoveStop() {
            // Only stop if we actually started a jog. Without this
            // guard a pointerleave event that fires before any
            // matching pointerdown (e.g. browser hovering a different
            // button during fast keyboard nav) would issue a spurious
            // abort that could kill an in-progress GoTo slew.
            if (this._mountJogActive == null) return;
            this._mountJogActive = null;
            // Use abort, which clears any active MOTION_* switch.
            // We considered sending explicit MOTION_NORTH=false etc
            // per spec but it requires per-direction stop endpoints;
            // abort works on every mount driver we've tested and
            // also recovers cleanly if the user mashed two buttons
            // simultaneously (both motions get cancelled at once).
            try { await this.apiPost('/api/telescope/abort'); }
            catch (e) { /* abort failures aren't surfaced -- the next
                           WS tick shows whether the mount stopped */ }
        },

        async mountStop() {
            try {
                await this.apiPost('/api/telescope/abort');
                this.toast('Mount stopped', 'info');
            } catch (e) {
                this.toast('Stop failed: ' + this._mountErrorText(e), 'error');
            }
        },

        async parkMount() {
            try {
                await this.apiPost('/api/telescope/park');
                this.toast('Parking mount...', 'info');
            } catch (e) {
                this.toast('Park failed: ' + this._mountErrorText(e), 'error');
            }
        },

        async unparkMount() {
            try {
                await this.apiPost('/api/telescope/unpark');
                this.toast('Unparking mount...', 'info');
            } catch (e) {
                this.toast('Unpark failed: ' + this._mountErrorText(e), 'error');
            }
        },

        async toggleTracking() {
            const newState = !this.mount.tracking;
            try {
                await this.apiPost('/api/telescope/tracking', { enabled: newState });
                this.toast('Tracking ' + (newState ? 'ON' : 'OFF'), 'info');
            } catch (e) {
                this.toast('Tracking toggle failed: ' + this._mountErrorText(e), 'error');
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
            } catch { /* corrupt storage, ignore */ }
        },

        persistMountPanel() {
            try {
                localStorage.setItem('mountPanel', JSON.stringify({
                    x: this.mountPanel.x, y: this.mountPanel.y,
                    visible: this.mountPanel.visible
                }));
            } catch { /* storage full / disabled, non-fatal */ }
        },

        // Persist the user's show/hide preference for the camera
        // preview window. Called from the inset's × button and from
        // the floating 📷 Camera pill so the choice survives reloads
        //, the auto-driven `slewPreview.active` keeps its own state.
        persistSlewPreviewToggle() {
            try {
                localStorage.setItem('slewPreviewVisible',
                    this.slewPreviewVisible ? '1' : '0');
            } catch { /* non-fatal */ }
        },

        // ESC-key safety net: closes any open floating panel
        // (Mount + Camera) regardless of what's happening with the
        // X buttons. Wired in init() via a global keydown listener.
        // Operator confirmed both X buttons can get stuck in some
        // states (likely a stale event handler from a hot-swap of
        // the markup); ESC is the universal escape hatch that
        // always works.
        _floatPanelEscapeClose(ev) {
            if (ev.key !== 'Escape' && ev.key !== 'Esc') return;
            // Confirm modal takes priority -- ESC cancels it, not float
            // panels behind it. Enter accepts. Both stop here so the
            // float panel handler doesn't run too.
            if (this.confirmModal.open) {
                this._confirmCancel();
                ev.preventDefault();
                return;
            }
            let closed = false;
            if (this.mountPanel?.visible) {
                this.mountPanel.visible = false;
                try { this.persistMountPanel(); } catch {}
                closed = true;
            }
            if (this.slewPreviewVisible) {
                this.slewPreviewVisible = false;
                try { this.persistSlewPreviewToggle(); } catch {}
                closed = true;
            }
            // Diagnostic so the operator can see in DevTools that the
            // ESC handler fired even if the X buttons don't react.
            if (closed) console.log('[Polaris] ESC closed floating panels');
        },

        // Custom replacement for window.confirm(). Returns a Promise
        // that resolves true on OK, false on Cancel/ESC/backdrop.
        // Usage: `if (await this._confirmAsync('Wipe it?', { danger: true })) {...}`
        _confirmAsync(message, opts = {}) {
            // If a previous prompt is still open, cancel it before opening
            // a new one -- avoids stuck Promises with no resolver.
            if (this.confirmModal.open && this._confirmResolver) {
                this._confirmResolver(false);
                this._confirmResolver = null;
            }
            this.confirmModal = {
                open: true,
                title: opts.title || 'Confirm',
                message: message || '',
                okLabel: opts.okLabel || 'OK',
                cancelLabel: opts.cancelLabel || 'Cancel',
                danger: !!opts.danger
            };
            return new Promise(resolve => {
                this._confirmResolver = resolve;
            });
        },
        _confirmAccept() {
            const r = this._confirmResolver;
            this._confirmResolver = null;
            this.confirmModal.open = false;
            if (r) r(true);
        },
        _confirmCancel() {
            const r = this._confirmResolver;
            this._confirmResolver = null;
            this.confirmModal.open = false;
            if (r) r(false);
        },
        // Enter-key shortcut so the operator can hit return to accept.
        _confirmModalKeydown(ev) {
            if (!this.confirmModal.open) return;
            if (ev.key === 'Enter') {
                this._confirmAccept();
                ev.preventDefault();
            }
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

        // Snap the mount popup back to its default top-left corner
        // and persist. Used by the toggle pill's right-click /
        // long-press rescue path when the operator drags the popup
        // off-screen and can't grab the header anymore.
        resetMountPanelPosition() {
            this.mountPanel.x = 24;
            this.mountPanel.y = 80;
            this._clampMountPanel();
            try { this.persistMountPanel(); } catch {}
        },

        // Same idea for the Camera float. Default coords mirror the
        // initial state in `cameraPanel: { x: 24, y: 360 }`.
        resetCameraPanelPosition() {
            this.cameraPanel.x = 24;
            this.cameraPanel.y = 360;
            this._clampCameraPanel();
            try { this.persistCameraPanel(); } catch {}
        },

        mountPanelDragStart(ev) {
            // Don't start a drag from the close button, that has its
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

        // --- Camera preview floating panel (mirror of mountPanel) ---

        restoreCameraPanel() {
            try {
                const raw = localStorage.getItem('cameraPanel');
                if (!raw) return;
                const saved = JSON.parse(raw);
                if (typeof saved.x === 'number') this.cameraPanel.x = saved.x;
                if (typeof saved.y === 'number') this.cameraPanel.y = saved.y;
                this._clampCameraPanel();
            } catch { /* corrupt, ignore */ }
        },

        persistCameraPanel() {
            try {
                localStorage.setItem('cameraPanel', JSON.stringify({
                    x: this.cameraPanel.x, y: this.cameraPanel.y
                }));
            } catch { /* non-fatal */ }
        },

        _clampCameraPanel() {
            const w = window.innerWidth, h = window.innerHeight;
            const minX = -240, maxX = w - 80;
            const minY = 0, maxY = h - 32;
            this.cameraPanel.x = Math.max(minX, Math.min(maxX, this.cameraPanel.x));
            this.cameraPanel.y = Math.max(minY, Math.min(maxY, this.cameraPanel.y));
        },

        cameraPanelDragStart(ev) {
            const isTouch = ev.type === 'touchstart';
            const point = isTouch ? ev.touches[0] : ev;
            this._cameraDrag = {
                offsetX: point.clientX - this.cameraPanel.x,
                offsetY: point.clientY - this.cameraPanel.y,
                touch: isTouch
            };
            const move = (e) => this._cameraPanelDragMove(e);
            const end = () => this._cameraPanelDragEnd(move, end);
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

        _cameraPanelDragMove(ev) {
            if (!this._cameraDrag) return;
            const point = this._cameraDrag.touch ? ev.touches[0] : ev;
            this.cameraPanel.x = point.clientX - this._cameraDrag.offsetX;
            this.cameraPanel.y = point.clientY - this._cameraDrag.offsetY;
            this._clampCameraPanel();
            if (ev.cancelable) ev.preventDefault();
        },

        _cameraPanelDragEnd(moveHandler, endHandler) {
            this._cameraDrag = null;
            window.removeEventListener('mousemove', moveHandler);
            window.removeEventListener('mouseup', endHandler);
            window.removeEventListener('touchmove', moveHandler);
            window.removeEventListener('touchend', endHandler);
            window.removeEventListener('touchcancel', endHandler);
            this.persistCameraPanel();
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

        // Software-only recovery from a wedged EAF driver. Cycles
        // the device's INDI CONNECTION switch (disconnect -> 2s wait
        // -> reconnect). When even small moves crash with "DISCONNECTED
        // within 500ms", the driver's USB session is gunked up after
        // prior crashes and a CONNECT cycle frequently clears it
        // without needing to physically unplug the cable. If the
        // reconnect itself fails, the toast tells the user to power
        // -cycle physically (USB unplug + replug).
        async focuserReconnect() {
            this.toast('Reconnecting focuser driver (~3s)...', 'info');
            try {
                // 15s timeout is plenty -- backend does 2s + 1s of
                // waits, the rest is INDI handshake.
                await this.apiPost('/api/focuser/reconnect', null, { timeout: 15000 });
                this.toast('Focuser reconnected', 'ok');
            } catch (e) {
                const body = e?.message || '';
                let msg = body;
                try { const j = JSON.parse(body); if (j?.error) msg = j.error; } catch {}
                this.toast('Focuser reconnect failed: ' + msg, 'error');
            }
        },

        // ─── SLOW / FAST step persistence ────────────────────────
        // localStorage stores both step values keyed by selected
        // focuser device name when available (so different scopes
        // remember different sweet spots), falling back to a global
        // key for ad-hoc connections. Triggered on @change of the
        // number inputs so the user's tweak survives reloads.
        saveFocusSteps() {
            try {
                const key = 'polaris.focusSteps:' + (this.selectedFocuser || '_default');
                localStorage.setItem(key, JSON.stringify({
                    slow: this.focusStepSlow,
                    fast: this.focusStepFast
                }));
                // Mirror FAST into legacy focusStep field for any
                // downstream code still reading it.
                this.focusStep = this.focusStepFast;
            } catch { /* private mode / quota exceeded -> silent */ }
        },

        _loadFocusStepsFromLocalStorage() {
            try {
                const key = 'polaris.focusSteps:' + (this.selectedFocuser || '_default');
                const raw = localStorage.getItem(key);
                if (!raw) return;
                const v = JSON.parse(raw);
                if (Number.isFinite(v?.slow) && v.slow >= 1) this.focusStepSlow = v.slow;
                if (Number.isFinite(v?.fast) && v.fast >= 1) {
                    this.focusStepFast = v.fast;
                    this.focusStep = v.fast;
                }
            } catch { /* ignore */ }
        },

        // ─── FOCUSER-SPEC v2 ─────────────────────────────────────
        // Toggle the "Driver settings" disclosure card. When opening,
        // pre-fill the Sync input with current position so "Sync to
        // current" is one click instead of two (much more common than
        // syncing to an arbitrary value the user remembers).
        focuserSettingsToggle() {
            this.focuserSettings.open = !this.focuserSettings.open;
            if (this.focuserSettings.open) {
                this.focuserSettings.syncPosition = this.focusPosition || 0;
                this.focuserSettings.lastError = null;
                this.focuserSettings.lastMessage = null;
            }
        },

        // INDI FOCUS_SYNC: redefine current physical position as the
        // entered step value WITHOUT moving the motor. Used to recover
        // from counter loss or after manually reseating the drawtube.
        async focuserSync() {
            const pos = parseInt(this.focuserSettings.syncPosition, 10);
            if (!Number.isFinite(pos) || pos < 0) {
                this.toast('Sync position must be a non-negative integer', 'warn');
                return;
            }
            this.focuserSettings.busy = true;
            this.focuserSettings.lastError = null;
            this.focuserSettings.lastMessage = null;
            try {
                await this.apiPost('/api/focuser/sync', { position: pos });
                this.focuserSettings.lastMessage = `Focuser position synced to ${pos}`;
                this.toast(this.focuserSettings.lastMessage, 'ok');
            } catch (e) {
                const msg = e?.message || 'Sync failed';
                this.focuserSettings.lastError = msg;
                this.toast('Focuser sync failed: ' + msg, 'error');
            } finally {
                this.focuserSettings.busy = false;
            }
        },

        // INDI FOCUS_REVERSE_MOTION: flip in/out direction. One-time
        // setup for focusers mounted backwards relative to the
        // optical train. Driver remembers it across reconnects.
        async focuserSetReverse(reversed) {
            this.focuserSettings.busy = true;
            this.focuserSettings.lastError = null;
            this.focuserSettings.lastMessage = null;
            try {
                await this.apiPost('/api/focuser/reverse', { reversed });
                this.focuserSettings.reversed = reversed;
                this.focuserSettings.lastMessage =
                    'Focuser direction ' + (reversed ? 'reversed' : 'restored to default');
                this.toast(this.focuserSettings.lastMessage, 'ok');
            } catch (e) {
                const msg = e?.message || 'Reverse failed';
                this.focuserSettings.lastError = msg;
                this.toast('Focuser reverse failed: ' + msg, 'error');
                // Roll back the local toggle so it doesn't visually
                // claim success when the backend rejected the call.
                // Alpine has already moved the model by the time we
                // hit catch, so re-set explicitly.
                this.focuserSettings.reversed = !reversed;
            } finally {
                this.focuserSettings.busy = false;
            }
        },

        // INDI FOCUS_BACKLASH_TOGGLE + FOCUS_BACKLASH_STEPS combined.
        // Sends both values atomically (per backend contract: steps
        // first when enabling, then toggle). Validates the step
        // count against a sensible upper bound -- 500 steps is more
        // than any consumer focuser actually needs, anything beyond
        // that is likely a typo.
        async focuserApplyBacklash() {
            const enabled = !!this.focuserSettings.backlashEnabled;
            const steps = parseInt(this.focuserSettings.backlashSteps, 10) || 0;
            if (enabled && (steps < 0 || steps > 500)) {
                this.toast('Backlash steps must be between 0 and 500', 'warn');
                return;
            }
            this.focuserSettings.busy = true;
            this.focuserSettings.lastError = null;
            this.focuserSettings.lastMessage = null;
            try {
                await this.apiPost('/api/focuser/backlash', { enabled, steps });
                this.focuserSettings.lastMessage = enabled
                    ? `Backlash compensation enabled (${steps} steps)`
                    : 'Backlash compensation disabled';
                this.toast(this.focuserSettings.lastMessage, 'ok');
            } catch (e) {
                const msg = e?.message || 'Backlash apply failed';
                this.focuserSettings.lastError = msg;
                this.toast('Backlash apply failed: ' + msg, 'error');
            } finally {
                this.focuserSettings.busy = false;
            }
        },

        // ─── FILTERWHEEL-SPEC v2 ─────────────────────────────────
        // Open the inline filter-name editor, snapshotting the
        // current names so the user edits a local copy and we don't
        // race with the 1Hz WS push. Disabled when wheel reports
        // no edit support.
        filterNamesEditOpen() {
            if (!this.filterWheel.capabilities?.editNames) {
                this.toast('This filter wheel driver does not support renaming slots', 'warn');
                return;
            }
            this.filterNamesEdit = {
                open: true,
                names: [...(this.filterWheel.filters || [])],
                busy: false,
                lastError: null,
                lastMessage: null
            };
        },

        filterNamesEditCancel() {
            this.filterNamesEdit.open = false;
            this.filterNamesEdit.names = [];
            this.filterNamesEdit.lastError = null;
            this.filterNamesEdit.lastMessage = null;
        },

        // PUT /api/filterwheel/names — pushes the edited names back
        // into the driver via INDI FILTER_NAME. Validates count
        // client-side first (server validates too, but this gives
        // friendlier feedback than 400 Bad Request).
        async filterNamesEditSave() {
            const slots = (this.filterWheel.filters || []).length;
            if (this.filterNamesEdit.names.length !== slots) {
                this.toast(`Filter name count mismatch (got ${this.filterNamesEdit.names.length}, expected ${slots})`,
                    'warn');
                return;
            }
            // Trim names to keep accidental whitespace out of the
            // driver config. Empty strings are tolerated by INDI
            // (slot stays unnamed) but we coerce to "Slot N" so the
            // dropdown stays readable.
            const cleaned = this.filterNamesEdit.names.map(
                (n, i) => (n || '').trim() || `Slot ${i + 1}`);
            this.filterNamesEdit.busy = true;
            this.filterNamesEdit.lastError = null;
            this.filterNamesEdit.lastMessage = null;
            try {
                // No dedicated apiPut helper; PUT via apiFetch with
                // method override + JSON body. Same auth + transfer
                // chip plumbing as apiPost.
                const resp = await this.apiFetch('/api/filterwheel/names', {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ names: cleaned })
                });
                const body = await resp.json().catch(() => ({}));
                this.filterNamesEdit.lastMessage = `Saved ${cleaned.length} filter names`;
                this.toast(this.filterNamesEdit.lastMessage, 'ok');
                // Mirror the saved names locally so the buttons
                // update immediately rather than waiting for the
                // next WS tick.
                this.filterWheel.filters = body?.names ? [...body.names] : cleaned;
                this.filterNamesEdit.open = false;
            } catch (e) {
                const msg = e?.message || 'Save failed';
                this.filterNamesEdit.lastError = msg;
                this.toast('Filter names save failed: ' + msg, 'error');
            } finally {
                this.filterNamesEdit.busy = false;
            }
        },

        // ─── MFOC-1: Manual Focus Assist ─────────────────────────
        // Default tab selection when the FOCUS tab opens. If a motor
        // is connected the V-curve auto-focus is the primary workflow,
        // so land there; otherwise the manual assist is the only
        // useful thing. The user can flip freely either way.
        manualFocusOnTabEnter() {
            if (this.focusConnected && this.focusTab === 'assist'
                && this.manualFocus.samples.length === 0) {
                this.focusTab = 'vcurve';
            } else if (!this.focusConnected) {
                this.focusTab = 'assist';
            }
        },
        // Re-render the trend chart whenever the assist subtab regains
        // focus (otherwise Chart.js draws into an offscreen canvas at
        // mount time and the bars look blank on first activation).
        manualFocusOnTabSwitch() {
            if (this.focusTab === 'assist') {
                this.$nextTick(() => this._renderManualFocusChart());
            }
        },

        // Loop start / stop. The loop is purely client-driven: every
        // intervalSec we POST /api/camera/capture, the existing
        // endpoint already returns HFR + starCount + laplacianVar
        // inline (no server-side focus-loop state to manage). Each
        // result is appended to a rolling 60-sample buffer that
        // drives the live metrics block + the trend chart.
        manualFocusToggle() {
            if (this.manualFocus.running) this.manualFocusStop();
            else this.manualFocusStart();
        },
        manualFocusStart() {
            if (!this.selectedCamera) {
                this.toast('Connect a camera in RIGS first', 'warn');
                return;
            }
            this.manualFocus.running = true;
            this.manualFocus.lastError = null;
            // SHUT-3: shutter ring needs a start time to animate.
            this.manualFocus._tickStartedAt = Date.now();
            this._startShutterTick();
            // Fire the first capture immediately so the user does
            // not stare at an empty canvas for `intervalSec` after
            // clicking Start.
            this._manualFocusTick();
        },
        // MFOC-4: invoked when the user toggles the Bahtinov checkbox.
        // Off → clear the cached result + wipe overlay. On → trigger
        // one immediate analysis if we have a cached frame, so the
        // overlay appears without waiting for the next loop tick.
        manualFocusBahtinovToggle() {
            if (!this.manualFocus.showBahtinov) {
                this.manualFocus.bahtinovResult = null;
                this.manualFocus.bahtinovError = null;
                this._renderBahtinovOverlay();
            } else {
                this._manualFocusBahtinovOnce();
            }
        },
        manualFocusStop() {
            this.manualFocus.running = false;
            this.manualFocus._tickStartedAt = null;
            if (this._manualFocusTimer) {
                clearTimeout(this._manualFocusTimer);
                this._manualFocusTimer = null;
            }
        },
        async manualFocusSnap() {
            // Out-of-loop single capture. Same code path as the
            // looping tick but doesn't reschedule.
            const prevRunning = this.manualFocus.running;
            this.manualFocus.running = false;
            try {
                await this._manualFocusCaptureOnce();
            } finally {
                this.manualFocus.running = prevRunning;
            }
        },
        manualFocusResetBaseline() {
            this.manualFocus.samples = [];
            this.manualFocus.bestHfr = null;
            this.manualFocus.lastError = null;
            // MFOC-4: clear the Bahtinov overlay too so the canvas
            // doesn't keep stale spike lines after a Reset.
            this.manualFocus.bahtinovResult = null;
            this.manualFocus.bahtinovError = null;
            this._renderManualFocusChart();
            this._renderBahtinovOverlay();
        },
        // Captures one exposure, parses the stats, appends a sample,
        // updates bestHfr, re-renders the chart. Used by both the
        // loop and the Snap-once button.
        async _manualFocusCaptureOnce() {
            try {
                const resp = await this.apiPost('/api/camera/capture', {
                    exposure: this.manualFocus.exposureSec,
                    gain: this.manualFocus.gain,
                    binning: 1,
                    saveToDisk: false,
                    // Manual focus assist is test shots, not science.
                    feedLiveStack: false,
                    // kind=focus routes the frame to the FOCUS tab
                    // canvases only (focusCanvas + manualFocusCanvas).
                    kind: 'focus'
                });
                const r = await resp.json();
                if (r.status !== 'complete') {
                    this.manualFocus.lastError = 'Capture cancelled';
                    return;
                }
                const stats = r.stats || {};
                const hfr = Number.isFinite(stats.hfr) && stats.hfr > 0
                    ? stats.hfr : NaN;
                const goodStarCount = (stats.starCount | 0)
                    >= (this.manualFocus.minStars | 0);
                const usableHfr = goodStarCount ? hfr : NaN;
                this.manualFocus.samples.push({
                    t: Date.now(),
                    hfr: usableHfr,
                    fwhm: Number.isFinite(usableHfr) ? usableHfr * 2.355 : NaN,
                    starCount: stats.starCount | 0,
                    laplacian: Number.isFinite(stats.laplacianVar)
                        ? stats.laplacianVar : 0
                });
                if (this.manualFocus.samples.length > 60) {
                    this.manualFocus.samples.shift();
                }
                if (Number.isFinite(usableHfr) &&
                    (this.manualFocus.bestHfr == null
                     || usableHfr < this.manualFocus.bestHfr)) {
                    this.manualFocus.bestHfr = usableHfr;
                }
                this.manualFocus.lastError = goodStarCount
                    ? null
                    : `Only ${stats.starCount} stars detected (need ${this.manualFocus.minStars}+). HFR ignored.`;
                this.manualFocus.lastFrameWidth = r.width | 0;
                this.manualFocus.lastFrameHeight = r.height | 0;
                this._renderManualFocusChart();
                // MFOC-4: Bahtinov analysis runs piggyback on the same
                // cached frame the capture call just produced. Cheap
                // enough (~50-200 ms) to do after every tick while the
                // checkbox is on. Failures stay non-fatal: the manual
                // focus loop keeps running, just without the overlay.
                if (this.manualFocus.showBahtinov) {
                    await this._manualFocusBahtinovOnce();
                }
            } catch (e) {
                this.manualFocus.lastError = 'Capture failed: '
                    + (e?.message || String(e));
            }
        },

        // MFOC-4: hit POST /api/focus/bahtinov and stash the result.
        // The endpoint pulls the last frame from ImageRelayService so
        // we don't pay for a second exposure here. Result lives on
        // manualFocus.bahtinovResult and feeds the sidebar readout +
        // the overlay drawn over manualFocusOverlayCanvas.
        async _manualFocusBahtinovOnce() {
            try {
                const resp = await this.apiPost('/api/focus/bahtinov', {});
                const r = await resp.json();
                if (r && r.ok) {
                    this.manualFocus.bahtinovResult = r;
                    this.manualFocus.bahtinovError = null;
                } else {
                    this.manualFocus.bahtinovResult = null;
                    this.manualFocus.bahtinovError = (r && r.error)
                        ? r.error : 'analysis failed';
                }
            } catch (e) {
                this.manualFocus.bahtinovResult = null;
                this.manualFocus.bahtinovError = 'request failed: '
                    + (e?.message || String(e));
            }
            // Draw on next animation frame so the canvas has settled
            // any pending resize from the JPEG mirror.
            this.$nextTick(() => this._renderBahtinovOverlay());
        },
        async _manualFocusTick() {
            if (!this.manualFocus.running) return;
            // SHUT-3: reset the shutter ring start time at the top of
            // each cycle so the ring restarts from empty + fills across
            // the (capture + intervalSec) cycle.
            this.manualFocus._tickStartedAt = Date.now();
            await this._manualFocusCaptureOnce();
            if (!this.manualFocus.running) return;
            // Schedule next tick after intervalSec. setTimeout chain
            // (not setInterval) so a slow capture doesn't stack
            // backlogged ticks.
            this._manualFocusTimer = setTimeout(
                () => this._manualFocusTick(),
                Math.max(500, (this.manualFocus.intervalSec | 0) * 1000));
        },
        // Helpers for the live metrics block in the sidebar.
        manualFocusLastSample() {
            const s = this.manualFocus.samples;
            return s.length ? s[s.length - 1] : null;
        },
        manualFocusFormat(field) {
            const s = this.manualFocusLastSample();
            if (!s) return '—';
            const v = s[field];
            if (!Number.isFinite(v)) return '—';
            if (field === 'laplacian') {
                return v >= 1000 ? v.toExponential(1) : v.toFixed(1);
            }
            return v.toFixed(2);
        },
        manualFocusHfrClass() {
            // Colour the HFR metric box by trend over the last 3
            // samples: green = decreasing (focus improving),
            // amber = flat, red = increasing (lost focus).
            const t = this.manualFocusTrendNumeric();
            if (t == null) return '';
            if (t < -0.05) return 'manual-focus-metric--good';
            if (t > 0.05)  return 'manual-focus-metric--bad';
            return 'manual-focus-metric--flat';
        },
        manualFocusTrend() {
            const t = this.manualFocusTrendNumeric();
            if (t == null) return '';
            if (t < -0.05) return '▼';
            if (t > 0.05)  return '▲';
            return '◆';
        },
        manualFocusTrendNumeric() {
            const s = this.manualFocus.samples
                .filter(x => Number.isFinite(x.hfr));
            if (s.length < 3) return null;
            const a = s[s.length - 1].hfr;
            const b = s[s.length - 3].hfr;
            return a - b;
        },
        // MFOC-2: HFR trend chart. Time-series scatter+line plot.
        // X = seconds elapsed from now (negative going back, so the
        // most recent sample sits at x=0 on the right edge), Y =
        // HFR (px). Best-HFR baseline drawn as a flat dashed line
        // across the whole X range so the user sees when they've
        // overshot. Reuses _ensureChart so the same Chart.js
        // instance is rebound across re-renders.
        _renderManualFocusChart() {
            const t = this._chartTheme();
            const c = this._ensureChart('manualFocusChart', 'manualFocus', 'line', () => ({
                type: 'line',
                data: {
                    datasets: [
                        { label: 'HFR', data: [], borderColor: '#64b5f6',
                          backgroundColor: '#64b5f6', pointRadius: 3,
                          showLine: true, borderWidth: 2, tension: 0.25,
                          spanGaps: true },
                        { label: 'Best',
                          data: [], borderColor: '#4caf50',
                          borderDash: [4, 3], backgroundColor: 'transparent',
                          pointRadius: 0, borderWidth: 1.5, showLine: true,
                          tension: 0 }
                    ]
                },
                options: {
                    responsive: true, maintainAspectRatio: false, animation: false,
                    plugins: { legend: { display: false } },
                    scales: {
                        x: {
                            type: 'linear',
                            ticks: { color: t.tick, font: { size: 10 },
                                     callback: v => v + 's' },
                            grid: { color: t.grid },
                            title: { display: true, text: 'seconds ago',
                                     color: t.tick, font: { size: 10 } }
                        },
                        y: {
                            beginAtZero: true,
                            ticks: { color: t.tick, font: { size: 10 } },
                            grid: { color: t.grid },
                            title: { display: true, text: 'HFR (px)',
                                     color: t.tick, font: { size: 10 } }
                        }
                    }
                }
            }));
            if (!c) return;
            const now = Date.now();
            const samples = this.manualFocus.samples
                .filter(s => Number.isFinite(s.hfr));
            const points = samples.map(s => ({
                x: (s.t - now) / 1000,    // negative seconds back from now
                y: s.hfr
            }));
            c.data.datasets[0].data = points;
            if (points.length > 0 && this.manualFocus.bestHfr != null) {
                // Flat dashed line at y=bestHfr across the visible X range
                const xMin = points[0].x;
                const xMax = points[points.length - 1].x;
                c.data.datasets[1].data = [
                    { x: xMin, y: this.manualFocus.bestHfr },
                    { x: xMax, y: this.manualFocus.bestHfr }
                ];
            } else {
                c.data.datasets[1].data = [];
            }
            c.update('none');
        },

        // MFOC-4: draw the Bahtinov result on the manual-focus overlay
        // canvas. Always clears first (so toggling the checkbox off
        // wipes stale art). Draws:
        //   - cross marker at the picked star (StarX, StarY in frame
        //     coords, scaled to canvas dimensions);
        //   - the 3 spike lines clipped to the canvas;
        //   - the central spike highlighted in the offset colour
        //     (green/amber/red on a 0.5 / 1.5 px threshold);
        //   - a small circle at the V-bisector intersection point so
        //     the user sees exactly where the central spike should
        //     pass through when in focus.
        _renderBahtinovOverlay() {
            const live = document.getElementById('manualFocusCanvas');
            const ovr = document.getElementById('manualFocusOverlayCanvas');
            if (!live || !ovr) return;
            if (ovr.width !== live.width || ovr.height !== live.height) {
                ovr.width = live.width || 1;
                ovr.height = live.height || 1;
            }
            const ctx = ovr.getContext('2d');
            ctx.clearRect(0, 0, ovr.width, ovr.height);
            const r = this.manualFocus.bahtinovResult;
            if (!this.manualFocus.showBahtinov || !r || !r.ok) return;

            const fw = this.manualFocus.lastFrameWidth || live.width;
            const fh = this.manualFocus.lastFrameHeight || live.height;
            if (!fw || !fh) return;
            const sx = ovr.width / fw;
            const sy = ovr.height / fh;
            const cx = r.starX * sx;
            const cy = r.starY * sy;

            const offsetPx = Math.abs(r.offsetPx || 0);
            const thr = r.inFocusThresholdPx || 0.5;
            let color;
            if (offsetPx <= thr) color = 'rgba(74, 222, 128, 0.95)';
            else if (offsetPx <= thr * 3) color = 'rgba(245, 158, 11, 0.95)';
            else color = 'rgba(239, 68, 68, 0.95)';

            // Star cross.
            ctx.save();
            ctx.strokeStyle = color;
            ctx.lineWidth = 1.5;
            const ch = 10;
            ctx.beginPath();
            ctx.moveTo(cx - ch, cy); ctx.lineTo(cx + ch, cy);
            ctx.moveTo(cx, cy - ch); ctx.lineTo(cx, cy + ch);
            ctx.stroke();

            // Spike lines. Each spike is a line through (cx + rho*nx,
            // cy + rho*ny) at angleDeg, extended across the canvas.
            const spikes = [
                { ang: r.spike1Angle, rho: r.spike1Rho, central: r.centreSpikeIndex === 0 },
                { ang: r.spike2Angle, rho: r.spike2Rho, central: r.centreSpikeIndex === 1 },
                { ang: r.spike3Angle, rho: r.spike3Rho, central: r.centreSpikeIndex === 2 }
            ];
            const maxLen = Math.hypot(ovr.width, ovr.height);
            for (const s of spikes) {
                if (!Number.isFinite(s.ang)) continue;
                const theta = s.ang * Math.PI / 180.0;
                const dx = Math.cos(theta);
                const dy = Math.sin(theta);
                // Perpendicular offset by rho (in frame px, scaled).
                const rhoCanvasX = -dy * (s.rho || 0) * sx;
                const rhoCanvasY = dx * (s.rho || 0) * sy;
                const x0 = cx + rhoCanvasX - dx * maxLen;
                const y0 = cy + rhoCanvasY - dy * maxLen;
                const x1 = cx + rhoCanvasX + dx * maxLen;
                const y1 = cy + rhoCanvasY + dy * maxLen;
                ctx.beginPath();
                if (s.central) {
                    ctx.strokeStyle = color;
                    ctx.lineWidth = 2.5;
                } else {
                    ctx.strokeStyle = 'rgba(180, 200, 255, 0.7)';
                    ctx.lineWidth = 1.5;
                }
                ctx.moveTo(x0, y0);
                ctx.lineTo(x1, y1);
                ctx.stroke();
            }

            // Intersection marker (where the V's two outer spikes
            // cross). When focused, the central spike passes here.
            if (Number.isFinite(r.intersectionX) && Number.isFinite(r.intersectionY)) {
                const ix = r.intersectionX * sx;
                const iy = r.intersectionY * sy;
                ctx.strokeStyle = 'rgba(180, 200, 255, 0.9)';
                ctx.lineWidth = 1.5;
                ctx.beginPath();
                ctx.arc(ix, iy, 5, 0, 2 * Math.PI);
                ctx.stroke();
            }

            // Offset label, top-left of the overlay.
            ctx.fillStyle = color;
            ctx.font = '12px system-ui, sans-serif';
            const sign = (r.offsetPx || 0) >= 0 ? '+' : '';
            const label = `Bahtinov offset: ${sign}${(r.offsetPx || 0).toFixed(2)} px`;
            ctx.fillText(label, 8, 16);
            ctx.restore();
        },

        // MFOC-4: directional helper for the sidebar readout. We don't
        // know which physical rotation direction the user's focuser
        // maps to (depends on tube + filter + scope orientation), so
        // we just describe the geometry: central spike is offset by
        // +N or -N px from the V's intersection. User watches the
        // sign change as they adjust the knob and learns which way
        // their rig wants.
        manualFocusBahtinovDirection() {
            const r = this.manualFocus.bahtinovResult;
            if (!r || !r.ok) return '';
            const off = r.offsetPx || 0;
            const abs = Math.abs(off);
            const thr = r.inFocusThresholdPx || 0.5;
            if (abs <= thr) return '✓ In focus';
            if (abs <= thr * 3) return 'Near focus, fine-tune';
            return off > 0 ? 'Rotate inward' : 'Rotate outward';
        },
        manualFocusBahtinovClass() {
            const r = this.manualFocus.bahtinovResult;
            if (!r || !r.ok) return '';
            const abs = Math.abs(r.offsetPx || 0);
            const thr = r.inFocusThresholdPx || 0.5;
            if (abs <= thr) return 'manual-focus-metric--good';
            if (abs <= thr * 3) return 'manual-focus-metric--flat';
            return 'manual-focus-metric--bad';
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

        // ---- PA-4: Polar alignment (TPPA) actions ----

        // PA-6: pull the "best targets for TPPA now" list from the
        // server. Cheap (~5ms server-side) so we just refetch each
        // time, no client-side caching beyond the items[] buffer.
        async loadPolarTargets() {
            this.polarTargets.loading = true;
            try {
                const r = await this.apiGet('/api/polar/best-targets?limit=6');
                this.polarTargets.items = Array.isArray(r) ? r : [];
                this.polarTargets.lastFetchUtc = new Date().toISOString();
            } catch (e) {
                this.polarTargets.items = [];
                this.toast('Could not load TPPA targets: ' + (e.message || ''), 'warn');
            } finally {
                this.polarTargets.loading = false;
            }
        },

        // PA-6: slew + plate-solve + sync on the chosen target. Reuses
        // skyTarget + slewAndCenter, same flow Tonight's Best / Sky
        // tab use. We do NOT auto-start TPPA after slew: the user
        // confirms the field is good and clicks Start. Two reasons:
        //   1. Plate-solve might fail (cloud, exposure off), better
        //      to fix that first than have TPPA also fail mid-flight.
        //   2. The Start button is right there; one click is fine.
        async polarGoToTarget(t) {
            if (!this.mount?.connected) {
                this.toast('Mount not connected', 'warn');
                return;
            }
            this.skyTarget = {
                name: t.name,
                ra: t.raHours,
                dec: t.decDeg,
                type: t.type || 'TPPA target',
                magnitude: t.magnitude != null ? t.magnitude.toFixed(2) : '',
                raFormatted: this.formatRA(t.raHours),
                decFormatted: this.formatDec(t.decDeg),
            };
            try {
                await this.slewAndCenter();
                this.toast('Slewing to ' + t.name + ', click Start TPPA when ready',
                    'info');
            } catch (e) {
                this.toast('Slew failed: ' + (e.message || ''), 'error');
            }
        },

        /// Start a TPPA job using the form values (which mirror the
        /// active rig's saved PolarAlign* settings).
        async polarStart() {
            try {
                await this.apiPost('/api/polar/start', {
                    slewStepDegrees: this.polar.slewDeg,
                    exposureSeconds: this.polar.exposureSec,
                    settleSeconds: this.polar.settleSec,
                    gain: this.polar.gain
                });
                this.toast('Polar alignment started', 'info');
            } catch (e) {
                this.toast('Polar start failed: ' + (e.message || ''), 'error');
            }
        },

        async polarAbort() {
            try {
                await this.apiPost('/api/polar/abort');
                this.toast('Polar alignment abort requested', 'warn');
            } catch (e) { this.toast('Polar abort failed', 'error'); }
        },

        async polarRefineStart() {
            try {
                await this.apiPost('/api/polar/refine/start');
                this.toast('Polar refine loop started', 'info');
            } catch (e) {
                this.toast('Refine start failed: ' + (e.message || ''), 'error');
            }
        },

        async polarRefineStop() {
            try {
                await this.apiPost('/api/polar/refine/stop');
                this.toast('Polar refine stopped', 'warn');
            } catch (e) { this.toast('Refine stop failed', 'error'); }
        },

        /// Persist the 4 form fields back to the active rig profile
        /// so the next session uses the same TPPA settings.
        async savePolarRigSettings() {
            const rig = this.rigs?.find(r => r.id === this.activeRigId);
            if (!rig) return;
            try {
                await this.apiPost('/api/equipment/rigs/' + encodeURIComponent(rig.id), null, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        ...rig,
                        polarAlignSlewDegrees: parseInt(this.polar.slewDeg) || 30,
                        polarAlignExposureSec: parseFloat(this.polar.exposureSec) || 3.0,
                        polarAlignSettleSeconds: parseInt(this.polar.settleSec) || 2,
                        polarAlignGain: parseInt(this.polar.gain) || 100
                    })
                });
            } catch (e) {
                this.toast('Polar settings save failed: ' + (e.message || ''), 'error');
            }
        },

        /// Pull the per-rig polar settings into the form on rig load /
        /// rig switch. Called from _applyRigToChoices.
        _hydratePolarSettingsFromRig(rig) {
            if (!rig) return;
            if (rig.polarAlignSlewDegrees > 0) this.polar.slewDeg = rig.polarAlignSlewDegrees;
            if (rig.polarAlignExposureSec > 0) this.polar.exposureSec = rig.polarAlignExposureSec;
            if (rig.polarAlignSettleSeconds >= 0) this.polar.settleSec = rig.polarAlignSettleSeconds;
            if (rig.polarAlignGain > 0) this.polar.gain = rig.polarAlignGain;
        },

        // ---- PA-4: Polar UI helpers (status pills, progress, formatting) ----

        polarStatusLabel() {
            const p = this.polar.phase || 'Idle';
            if (p === 'Idle') return 'IDLE';
            if (p === 'Ok') return 'ALIGNED';
            if (p === 'Failed') return 'FAILED';
            if (p === 'Cancelled') return 'CANCELLED';
            if (p === 'Refining') return 'REFINING';
            return p.toUpperCase();
        },

        polarStatusClass() {
            const p = this.polar.phase;
            if (p === 'Ok') return 'ok';
            if (p === 'Failed') return 'err';
            if (p === 'Cancelled') return 'warn';
            if (this.polar.isActive) return 'warn';
            return '';
        },

        polarPhaseLabel() {
            // Insert spaces for readability: MovingToPoint1 → Moving to point 1
            const p = this.polar.phase || '';
            return p.replace(/([A-Z])/g, ' $1').replace(/(\d)/g, ' $1').trim();
        },

        polarProgressPercent() {
            // 11 distinct working phases between Preflight and Ok.
            // Rough linear mapping is good enough for a UX bar.
            const order = ['Preflight',
                'MovingToPoint1', 'SolvingPoint1',
                'MovingToPoint2', 'SolvingPoint2',
                'MovingToPoint3', 'SolvingPoint3',
                'Computing', 'SlewingHome', 'Ok'];
            const idx = order.indexOf(this.polar.phase);
            if (idx < 0) return 0;
            return Math.round(100 * (idx + 1) / order.length);
        },

        // Colour classes for the result block, by error magnitude in arcmin.
        polarAzClass() { return this._polarColorBy(this.polar.azErrorArcsec); },
        polarAltClass() { return this._polarColorBy(this.polar.altErrorArcsec); },
        polarTotalClass() { return this._polarColorBy(this.polar.totalErrorArcsec); },
        _polarColorBy(arcsec) {
            const abs = Math.abs(arcsec || 0) / 60.0; // arcmin
            if (abs < 1.0) return 'text-ok';
            if (abs < 5.0) return 'text-warn';
            return 'text-err';
        },

        /// Format arcsec error with sign + arcmin units (NINA convention).
        formatArcmin(arcsec) {
            if (arcsec == null || isNaN(arcsec)) return ', ';
            const arcmin = arcsec / 60.0;
            const sign = arcmin >= 0 ? '+' : '';
            return sign + arcmin.toFixed(2) + "'";
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
            // We don't get a/b/c on the status stream, derive from points if absent.
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
            // Immediate ack so the user gets feedback that the click
            // registered; the server-side wait can take a few seconds
            // and a silent UI would feel broken.
            this.toast('Moving to filter: ' + filterName, 'info');
            try {
                // The endpoint blocks (up to 30 s) until INDI reports
                // the wheel settled — that's where we get the real
                // "done" signal to surface to the user.
                const r = await this.apiPost(
                    `/api/filterwheel/filter/${encodeURIComponent(filterName)}`);
                if (r?.settled) {
                    this.toast('Filter: ' + (r.filter || filterName), 'ok');
                } else {
                    this.toast(
                        'Filter wheel still moving — ' + (r?.message || 'no settle ack'),
                        'warn');
                }
            } catch (e) {
                this.toast('Filter change failed: ' + (e?.message || e), 'error');
            }
        },

        async setFilterPosition(slot) {
            this.toast('Moving to slot: ' + slot, 'info');
            try {
                const r = await this.apiPost(`/api/filterwheel/position/${slot}`);
                if (r?.settled) {
                    this.toast(
                        'Filter wheel at slot ' + (r.position || slot)
                            + (r.currentFilter ? ' (' + r.currentFilter + ')' : ''),
                        'ok');
                } else {
                    this.toast(
                        'Filter wheel still moving — ' + (r?.message || 'no settle ack'),
                        'warn');
                }
            } catch (e) {
                this.toast('Filter change failed: ' + (e?.message || e), 'error');
            }
        },

        // MOUNT: send the telescope to its mechanical home position.
        // Capability-gated (button only renders when the active mount
        // reports SupportsFindHome). Confirms before firing because
        // home moves are typically large + irreversible until the
        // user takes another action.
        async telescopeFindHome() {
            if (!confirm('Send the mount to its home position?\n\n' +
                         'The mount will slew at full speed to its mechanical ' +
                         'home (CW down, RA/Dec hard stops). Use this only when ' +
                         'you have clearance for the move.')) return;
            try {
                await this.apiPost('/api/telescope/find-home');
                this.toast('Mount moving to home', 'info');
            } catch (e) {
                this.toast('Find Home failed: ' + (e?.message || e), 'error');
            }
        },

        // Strain-wave-mount workaround. Drivers for the ZWO AM3 and
        // similar harmonic mounts lose their internal position
        // reference on every power cycle: a plain Find Home does
        // nothing because the driver has no idea where "home" is
        // relative to current encoder counts. A Park (forces the
        // driver to adopt current position as a known state) +
        // Unpark (releases motion) + Home (now reachable) sequence
        // re-establishes the reference and the mount slews home as
        // expected. Backend handler waits for each transition to
        // settle before chaining the next step, so this call can
        // take up to 60s end-to-end.
        async telescopeFindHomeReset() {
            if (!confirm('Run Park -> Unpark -> Home?\n\n' +
                         'This is the workaround for strain-wave mounts (ZWO AM3 ' +
                         'and similar) where plain Home does nothing after the ' +
                         'mount has been power-cycled.\n\n' +
                         'The mount will park first (slewing to its park position) ' +
                         'then unpark and slew to home. Make sure the full slew ' +
                         'path is clear of obstructions.')) return;
            this.toast('Reset & Home: park -> unpark -> home (up to 90s)...', 'info');
            try {
                // 90s client-side timeout: backend allows 30s for park
                // to settle + 30s for unpark + actual home slew time.
                // Default apiFetch timeout (15s) was firing AbortError
                // mid-sequence while the mount was still parking, which
                // left the backend chugging away successfully while the
                // user saw a misleading "Request timed out" toast.
                const resp = await this.apiPost('/api/telescope/find-home-reset',
                    null, { timeout: 90000 });
                const body = await resp.json().catch(() => ({}));
                if (body?.status === 'homing') {
                    this.toast('Mount homing after reset sequence', 'ok');
                } else if (body?.error) {
                    this.toast('Reset & Home: ' + body.error, 'warn');
                }
            } catch (e) {
                this.toast('Reset & Home failed: ' + (e?.message || e), 'error');
            }
        },

        // MOUNT-LOC: push the observer coords from the active profile
        // into the mount via INDI GEOGRAPHIC_COORD / Alpaca site-*
        // properties. Sends nothing in the body so the server falls
        // through to profile.Latitude / Longitude / Altitude -- the
        // values the user already set up in SETTINGS. Without doing
        // this once after the mount connects, every GoTo will land
        // off-target because the mount's internal alt/az math uses
        // its own site config, not Polaris's.
        // Push the observatory coordinates AND the wall-clock UTC
        // into the mount in one action. The mount uses (lat, lon, utc)
        // together to compute Local Sidereal Time -- pushing one
        // without the other is half a sync and produces silently
        // wrong GoTos.
        //
        // Field-observed failure that motivated this combined action:
        // operator clicked "Sync Location" (which only pushed lat/lon)
        // but the INDI driver's TIME_UTC was still stuck at its boot
        // default of "2000-01-01T00:00:00" -- so the mount computed
        // LST for the year 2000 and every GoTo landed ~25 years of
        // sidereal drift off target. Bundling both writes makes that
        // failure mode impossible.
        //
        // Both writes are awaited independently so a driver that
        // accepts location but rejects TIME_UTC (or vice versa)
        // surfaces the partial-success state to the toast instead
        // of hiding it.
        async telescopeSyncSite() {
            let locOk = false, timeOk = false, locMsg = '', timeMsg = '', errMsg = '';
            // Location push first because every mount driver exposes
            // GEOGRAPHIC_COORD; the time vector is more brittle and
            // we want at least the location to land if time fails.
            try {
                const resp = await this.apiPost('/api/telescope/sync-location');
                const body = await resp.json().catch(() => ({}));
                if (body?.status === 'synced') {
                    locOk = true;
                    locMsg = `${body.latitude?.toFixed(4) ?? '?'}°, ${body.longitude?.toFixed(4) ?? '?'}°, ${body.elevation ?? 0} m`;
                }
            } catch (e) {
                errMsg = 'Location: ' + (e?.message || e);
            }
            // Time push. Body is omitted so the backend pulls
            // DateTime.UtcNow + the host's local-time offset. The
            // user can refine the host's clock via the existing
            // CLOCK-1 sync-with-PC card before clicking this.
            try {
                const resp = await this.apiPost('/api/telescope/sync-time');
                const body = await resp.json().catch(() => ({}));
                if (body?.status === 'synced') {
                    timeOk = true;
                    timeMsg = `UTC ${(body.utc || '').replace('T', ' ').replace(/\..*$/, '')}, offset ${body.offsetHoursFromUtc ?? 0}h`;
                }
            } catch (e) {
                errMsg = (errMsg ? errMsg + ' / ' : '') + 'Time: ' + (e?.message || e);
            }
            // Report. All-success → single ok toast. Partial →
            // warning toast naming what worked and what didn't so
            // the user can decide whether to retry. All-fail →
            // error toast.
            if (locOk && timeOk) {
                this.toast(`Mount site synced (${locMsg} · ${timeMsg})`, 'ok');
            } else if (locOk || timeOk) {
                this.toast(
                    'Partial sync: ' +
                    (locOk ? `location OK (${locMsg})` : 'location FAILED') + ', ' +
                    (timeOk ? `time OK (${timeMsg})` : 'time FAILED') +
                    (errMsg ? ` -- ${errMsg}` : ''),
                    'warn');
            } else {
                this.toast('Site sync failed' + (errMsg ? `: ${errMsg}` : ''), 'error');
            }
        },

        // Back-compat alias: HTML button still calls telescopeSyncLocation,
        // and existing operator muscle memory + any tutorial doc references
        // it by that name. Keep the old name pointing at the new combined
        // behaviour so the upgrade is invisible.
        async telescopeSyncLocation() { return this.telescopeSyncSite(); },

        // Push just the wall-clock UTC + host's local-time offset
        // into the mount via TIME_UTC. Useful when the operator
        // already synced the host clock (chrony / manual /
        // sync-with-PC) and wants to forward only the time without
        // re-sending the location.
        async telescopeSyncTime() {
            try {
                const resp = await this.apiPost('/api/telescope/sync-time');
                const body = await resp.json().catch(() => ({}));
                if (body?.status === 'synced') {
                    const utc = (body.utc || '').replace('T', ' ').replace(/\..*$/, '');
                    this.toast(
                        `Mount UTC set to ${utc} (offset ${body.offsetHoursFromUtc ?? 0}h)`,
                        'ok');
                } else if (body?.error) {
                    this.toast('Time sync rejected: ' + body.error, 'warn');
                } else {
                    this.toast('Time sync responded with no confirmation', 'warn');
                }
            } catch (e) {
                this.toast('Time sync failed: ' + (e?.message || e), 'error');
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
                // Persist the (device, driver) pair into the active rig
                // so the next browser reload restores the same driver
                // selection. Without this the dropdown reverts to INDI
                // on every page load even though the device was last
                // connected via ASCOM.
                this._persistRigSelection({
                    camera: this.equipCameraChoice,
                    cameraDriver: this.cameraDriver || 'indi'
                });
                this.toast('Camera connected: ' + this.equipCameraChoice, 'ok');
                this.pollCameraInfo();
            } catch (e) {
                this.toast('Camera connection failed: ' + e.message, 'error');
            }
        },

        // Patch the active rig with whatever fields are passed and
        // schedule a debounced PUT via saveRig. Used by the per-device
        // connect handlers so the (deviceName, driver) pair sticks
        // across browser reloads without forcing the user to remember
        // hitting "💾 Save selections".
        _persistRigSelection(patch) {
            const rig = this.activeRig;
            if (!rig) return;
            Object.assign(rig, patch);
            this.saveRig(rig);
        },

        // Load the list of camera-driver kinds offered by this host
        // and their availability flags. Cached for the session, call
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

        // "Detect" button handler for ASCOM-COM telescope drivers.
        // INDI mounts are picked from the live indiserver device list;
        // direct WiFi drivers (synscan-wifi) take a host:port and
        // need no discovery. Only ASCOM enumerates via the registry.
        async detectVendorMounts() {
            this.mountDiscovering = true;
            try {
                const list = await this.apiGet(
                    `/api/telescope/discover?driver=${encodeURIComponent(this.mountDriver)}`);
                this.mountVendorDevices = list || [];
                if (this.mountVendorDevices.length === 0) {
                    const hint = this.mountDriver === 'alpaca'
                        ? 'No Alpaca telescopes discovered. Run Discover in the ASCOM/Alpaca tab first.'
                        : 'No ASCOM telescope drivers registered on this host';
                    this.toast(hint, 'warn');
                }
            } catch (e) {
                this.toast('Mount detect failed: ' + (e.message || ''), 'error');
            } finally {
                this.mountDiscovering = false;
            }
        },

        // ASCOM SetupDialog. Spawns the driver's modal setup form on
        // a dedicated STA thread server-side. The request blocks
        // until the user dismisses the dialog, so we set
        // ascomSetupBusy=true to disable the button and avoid a
        // concurrent open (most drivers crash on that). Toast shows
        // success / hint on failure (typical failure is "no
        // interactive desktop" when Polaris runs as a service).
        async openAscomSetup(progId) {
            if (!progId) return;
            this.ascomSetupBusy = true;
            try {
                await this.apiPost(`/api/ascom/setup/${encodeURIComponent(progId)}`);
                this.toast('Setup dialog closed for ' + progId, 'ok');
            } catch (e) {
                // ApiError.message is "HTTP 400: {full json envelope}"
                // — useless in a toast. The server-side handler stuffs
                // the human-readable text into the .error field of the
                // JSON body, so parse the body out and surface that
                // instead. Hint (if present) follows as a second line.
                let msg = '';
                if (e && e.body) {
                    try {
                        const j = JSON.parse(e.body);
                        msg = j.error || '';
                        if (j.hint) msg += msg ? ('\n' + j.hint) : j.hint;
                    } catch { /* not JSON, fall through */ }
                }
                if (!msg) msg = (e && e.message) || 'Unknown error';
                this.toast('ASCOM setup failed: ' + msg, 'error');
            } finally {
                this.ascomSetupBusy = false;
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
            // standalone POST, exposed here as a stub so the dropdown
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
                this._persistRigSelection({
                    telescope: this.equipMountChoice,
                    telescopeDriver: this.mountDriver || 'indi'
                });
                this.toast('Mount connected: ' + this.equipMountChoice, 'ok');
            } catch (e) {
                this.toast('Mount connection failed: ' + e.message, 'error');
            }
        },

        // Load the mount-driver catalogue once per session. Same
        // pattern as loadCameraDrivers, INDI is always available, the
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
                    : 'Siril not found, check the path override', r.available ? 'ok' : 'warn');
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
            // BGE inject is opt-in per run, not sticky, every modal
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
                        this.toast('GraXpert produced no usable outputs, aborting Siril phase', 'error');
                        this.siril.modalBgePhase = null;
                        return;
                    }
                    this.toast('BGE complete (' + lightsForSiril.length + ' frames clean), starting Siril', 'ok');
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
                // Wipe any leftover console from the previous run so
                // the new job's first poll fills a clean buffer.
                this.siril.consoleLines = [];
                this.siril._consoleSince = 0;
                this.siril.consoleFollow = true;
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
                    } catch { /* transient, keep polling */ }
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
                    // Pass the line cursor so the server only ships the
                    // new tail since the last poll. First call (since=0)
                    // returns everything buffered so far; subsequent
                    // calls trim to the delta.
                    const since = this.siril._consoleSince || 0;
                    const job = await this.apiGet('/api/siril/jobs/'
                        + encodeURIComponent(this.siril.currentJobId)
                        + '?sinceLine=' + since);
                    this.siril.currentJob = job;
                    if (job && Array.isArray(job.logLines) && job.logLines.length) {
                        this.siril.consoleLines.push(...job.logLines);
                        // Server side caps the buffer at 500 lines so
                        // totalLines is exact; track it as the next
                        // cursor. Falls back to local length when the
                        // server omits totalLines for some reason.
                        this.siril._consoleSince = typeof job.totalLines === 'number'
                            ? job.totalLines
                            : (this.siril._consoleSince + job.logLines.length);
                        // Auto-scroll to the bottom unless the user
                        // scrolled up (consoleFollow flipped off by
                        // sirilConsoleOnScroll).
                        if (this.siril.consoleFollow) this._sirilScrollConsoleToBottom();
                    }
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
                    // Transient, keep polling.
                }
            }, 1500);
        },

        _sirilScrollConsoleToBottom() {
            // Wait for Alpine to flush the new lines into the DOM
            // before scrolling, otherwise the scrollHeight would be
            // measured before the append landed.
            this.$nextTick(() => {
                const el = document.getElementById('sirilConsole');
                if (el) el.scrollTop = el.scrollHeight;
            });
        },

        // Track manual scroll so auto-follow turns off when the user
        // scrolls up to read older lines, and back on when they
        // scroll all the way down. Threshold of 8 px so subpixel
        // rounding from the browser doesn't flip the flag spuriously.
        sirilConsoleOnScroll(ev) {
            const el = ev.target;
            const atBottom = (el.scrollHeight - el.scrollTop - el.clientHeight) < 8;
            this.siril.consoleFollow = atBottom;
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
                    : 'GraXpert not found, check the path override', r.available ? 'ok' : 'warn');
            } catch (e) {
                this.toast('GraXpert detection failed: ' + (e.message || ''), 'error');
            }
        },

        // ─── GX-10: HTTPS info loader ───────────────────────────────
        // Reads the server's HTTPS configuration (port, cert
        // fingerprint, SAN-list hostnames + ready-to-click URLs).
        // Fetched on startup and after a settings save, surface
        // lives in the AI inference banner so the user sees the
        // upgrade path when WebGPU isn't available.

        async loadHttpsInfo() {
            try {
                this.httpsInfo = await this.apiGet('/api/system/https-info');
            } catch (e) {
                console.warn('[Polaris] HTTPS info fetch failed:', e);
                this.httpsInfo = null;
            }
        },

        // ─── GX-1b: ONNX manifest + cache control ───────────────────────
        // Pulls the server's view of which models exist + lazily probes
        // the IndexedDB cache size. Surfaced in the Settings AI section.

        async loadOnnxManifest() {
            if (typeof OnnxRegistry === 'undefined') return;
            try {
                this.onnx.manifest = await OnnxRegistry.fetchManifest(true);
            } catch (e) {
                console.warn('[Onnx] manifest fetch failed', e);
                this.onnx.manifest = { models: [], error: String(e) };
            }
            // Cache size probe, non-fatal if IDB unavailable.
            try { this.onnx.cacheSize = await OnnxRegistry.idbTotalSize(); }
            catch { this.onnx.cacheSize = 0; }
        },

        async onnxRescan() {
            if (this.onnx.scanning) return;
            this.onnx.scanning = true;
            try {
                await this.apiPost('/api/onnx/rescan');
                await this.loadOnnxManifest();
            } catch (e) {
                this.toast('ONNX rescan failed: ' + (e.message || ''), 'error');
            } finally {
                this.onnx.scanning = false;
            }
        },

        async onnxClearCache() {
            if (this.onnx.clearingCache) return;
            if (!window.confirm('Drop all cached ONNX model bytes? Next use will re-download.'))
                return;
            this.onnx.clearingCache = true;
            try {
                if (typeof OnnxRegistry !== 'undefined') await OnnxRegistry.idbClear();
                this.onnx.cacheSize = 0;
                this.toast('ONNX cache cleared', 'ok');
            } finally {
                this.onnx.clearingCache = false;
            }
        },

        // ─── GX-6: license consent ──────────────────────────────────────
        // Awaited promise that resolves true when the user has
        // acknowledged CC BY-NC-SA 4.0, false when they cancel. Cached
        // in localStorage (early-out without bothering the user) and
        // persisted to the server profile via saveSettingsToServer so
        // the consent survives across browsers / clean installs of the
        // page bundle.
        _ensureOnnxLicenseAccepted() {
            try {
                if (localStorage.getItem('nina-onnx-license-accepted') === '1') return true;
            } catch { /* private mode */ }
            if (this.settings.onnxLicenseAcknowledged) return true;
            return new Promise(resolve => {
                this._onnxLicenseResolver = resolve;
                this.onnxLicenseModalOpen = true;
            });
        },

        async onnxLicenseAccept() {
            this.onnxLicenseModalOpen = false;
            this.settings.onnxLicenseAcknowledged = true;
            try { localStorage.setItem('nina-onnx-license-accepted', '1'); }
            catch { /* private mode */ }
            // Best-effort flush to the server so other browsers /
            // devices inherit the consent without re-prompting.
            try { await this.saveSettingsToServer(); } catch { }
            if (this._onnxLicenseResolver) {
                this._onnxLicenseResolver(true);
                this._onnxLicenseResolver = null;
            }
        },

        onnxLicenseDecline() {
            this.onnxLicenseModalOpen = false;
            if (this._onnxLicenseResolver) {
                this._onnxLicenseResolver(false);
                this._onnxLicenseResolver = null;
            }
        },

        // Reused helper for displaying bytes in the AI panel + per-model
        // size column. Mirrors the auto-scale rules formatBytesPerSec
        // uses for the network indicator but for static quantities (no
        // per-second suffix).
        formatBytes(bytes) {
            if (!Number.isFinite(bytes) || bytes <= 0) return '0 B';
            if (bytes < 1024) return Math.round(bytes) + ' B';
            if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
            if (bytes < 1024 * 1024 * 1024) return (bytes / 1024 / 1024).toFixed(1) + ' MB';
            return (bytes / 1024 / 1024 / 1024).toFixed(2) + ' GB';
        },

        // Open the GraXpert batch modal for the operation requested
        // (BGE / Decon / Denoise). Defaults pulled from the profile
        // so the modal already has sensible values per op.
        //
        // pathOverride: optional string|string[]. When the editor's
        // controls-header buttons call this, they pass
        // editorState.sourcePath so the modal works on the file
        // currently being edited instead of FILES tab's selection.
        // When omitted (FILES toolbar path), the existing
        // files.selectedPaths flow runs.
        graxpertOpenModal(operation, pathOverride) {
            // GX-7: open the modal if either path is viable, CLI
            // installed OR the matching ONNX model is in the registry.
            // Block only when both are unavailable.
            const cliOk     = !!this.graxpert.status?.available;
            const browserOk = this.onnxAvailableForOp(operation);
            if (!cliOk && !browserOk) {
                this.toast('GraXpert unavailable: install the CLI or '
                         + 'configure Onnx:ModelsPath in Settings', 'warn');
                return;
            }
            // Source paths come from the override when present
            // (editor / STUDIO / autorun), otherwise fall back to
            // the FILES tab selection. Normalise to an array so the
            // batch loop downstream doesn't care which caller it was.
            let paths;
            if (pathOverride) {
                paths = Array.isArray(pathOverride) ? pathOverride : [pathOverride];
            } else {
                paths = (this.files?.selectedPaths || []).slice();
            }
            this.graxpert.modalPaths = paths;
            this.graxpert.modalOp = operation;
            this.graxpert.modalSmoothing = this.settings.graxpertBgeSmoothing;
            this.graxpert.modalCorrection = this.settings.graxpertBgeCorrection;
            this.graxpert.modalSaveBackground = false;
            this.graxpert.modalDeconStrength = this.settings.graxpertDeconStrength;
            this.graxpert.modalDeconPsfSize = this.settings.graxpertDeconPsfSize;
            this.graxpert.modalDenoiseStrength = this.settings.graxpertDenoiseStrength;
            // GX-12k/n: hydrate the per-run denoise model version.
            //   Desktop → profile default (typically 2.0.0 or 3.0.2 FP32).
            //   iOS    → smallest available variant. If a quantized
            //            sibling (2.0.0-fp16 / 2.0.0-int8) exists in
            //            the manifest, prefer it, that's the only
            //            way to fit under Safari's per-tab budget.
            //            Falls back to v2.0.0 FP32 when no quantized
            //            variant is registered yet.
            const profileDefault = this.settings.onnxDefaultDenoiseVersion || '2.0.0';
            if (this._isIOS()) {
                // denoiseModelChoices() sorts smallest-first on iOS,
                // so [0] is the lightest option available.
                const choices = this.denoiseModelChoices();
                this.graxpert.modalDenoiseVersion =
                    (choices[0] && choices[0].version) || '2.0.0';
            } else {
                this.graxpert.modalDenoiseVersion = profileDefault;
            }
            // GX-7: default depends on the user's preference + per-op
            // availability. Force browser-off when there's no model
            // even if the user prefers browser (CLI is the only path).
            this.graxpert.modalRunInBrowser = browserOk && !this.settings.onnxPreferCli;
            this.graxpert.currentJobId = null;
            this.graxpert.currentJob = null;
            this.graxpert.browserActive = false;
            this.graxpert.browserAbortRequested = false;
            this.graxpert.modalOpen = true;
        },

        graxpertCloseModal() {
            this.graxpert.modalOpen = false;
            if (this.graxpert._pollTimer) {
                clearInterval(this.graxpert._pollTimer);
                this.graxpert._pollTimer = null;
            }
        },

        // ── CROP picker ───────────────────────────────────────────────
        // Pre-BGE/Decon/Denoise step the user normally does in GraXpert:
        // pick a rectangle on the master, slice it out as a new FITS
        // sibling _crop.fits, and continue processing on the smaller
        // frame. Same JPEG preview pipe FILES uses for thumbnails;
        // ROI is drawn in DISPLAY pixels, converted to IMAGE pixels
        // at submit-time using the natural-vs-display width ratio
        // captured when the <img> loaded.

        cropOpenForFile(path) {
            if (!path) return;
            this.crop.sourcePath = path;
            // Derive a friendly output name for the toast/log so the
            // user can locate the result without copying the full
            // server path.
            const base = path.split(/[\\/]/).pop() || path;
            const stem = base.replace(/\.[^.]+$/, '');
            this.crop.outputName = stem + '_crop.fits';
            // Reuse the same FILES preview endpoint that ships the
            // auto-stretched JPEG of the master — keeps the preview
            // visually consistent with what the user sees in FILES.
            this.crop.previewUrl = this.authUrl(
                '/api/files/preview?path=' + encodeURIComponent(path)
                + '&maxDim=2400');
            this.crop.error = '';
            this.crop.busy = false;
            this.cropResetRoi();
            this.crop.imgNaturalWidth = 0;
            this.crop.imgNaturalHeight = 0;
            this.crop.imgDisplayWidth = 0;
            this.crop.imgDisplayHeight = 0;
            this.crop.open = true;
        },

        cropClose() {
            this.crop.open = false;
            this.crop.previewUrl = '';
            this.cropResetRoi();
        },

        cropResetRoi() {
            this.crop.roi = { startX: null, startY: null, endX: null, endY: null };
            this.crop.dragging = false;
        },

        // Capture natural-vs-display dimensions once the <img> finishes
        // loading. These ratios are what cropStartRun uses to map the
        // ROI back to image pixel coordinates the server slicer expects.
        cropOnImageLoaded(ev) {
            const img = ev.target;
            if (!img) return;
            this.crop.imgNaturalWidth = img.naturalWidth || 0;
            this.crop.imgNaturalHeight = img.naturalHeight || 0;
            this.crop.imgDisplayWidth = img.clientWidth || img.width || 0;
            this.crop.imgDisplayHeight = img.clientHeight || img.height || 0;
        },

        _cropPointerXY(ev, pickerEl) {
            const rect = pickerEl.getBoundingClientRect();
            // Touch events nest the coords under changedTouches[0];
            // fall back to clientX/Y for plain pointer/mouse.
            const cx = ev.clientX != null ? ev.clientX :
                (ev.changedTouches && ev.changedTouches[0] ? ev.changedTouches[0].clientX : 0);
            const cy = ev.clientY != null ? ev.clientY :
                (ev.changedTouches && ev.changedTouches[0] ? ev.changedTouches[0].clientY : 0);
            // Clamp to picker bounds so a drag that overshoots the
            // image edge still produces a valid ROI snapped to the
            // displayed image area.
            const x = Math.max(0, Math.min(rect.width, cx - rect.left));
            const y = Math.max(0, Math.min(rect.height, cy - rect.top));
            return { x, y };
        },

        cropOnPointerDown(ev) {
            if (this.crop.busy) return;
            const picker = ev.currentTarget;
            const { x, y } = this._cropPointerXY(ev, picker);
            this.crop.dragging = true;
            this.crop.roi = { startX: x, startY: y, endX: x, endY: y };
        },

        cropOnPointerMove(ev) {
            if (!this.crop.dragging) return;
            const picker = ev.currentTarget;
            const { x, y } = this._cropPointerXY(ev, picker);
            this.crop.roi.endX = x;
            this.crop.roi.endY = y;
        },

        cropOnPointerUp(ev) {
            if (!this.crop.dragging) return;
            this.crop.dragging = false;
            // Treat a click-without-drag (< 4 px in either axis) as a
            // clear instead of an accidental zero-size rectangle.
            const r = this.crop.roi;
            if (r.startX == null || Math.abs((r.endX || 0) - (r.startX || 0)) < 4
                                  || Math.abs((r.endY || 0) - (r.startY || 0)) < 4) {
                this.cropResetRoi();
            }
        },

        // Returns inline-style props for the .crop-picker-overlay <div>.
        // Always normalises (startX/Y → top-left) so a drag in any
        // direction produces a sane rectangle.
        cropRoiStyle() {
            const r = this.crop.roi;
            if (r.startX == null || r.endX == null) return 'display:none';
            const x = Math.min(r.startX, r.endX);
            const y = Math.min(r.startY, r.endY);
            const w = Math.abs(r.endX - r.startX);
            const h = Math.abs(r.endY - r.startY);
            return `left:${x}px; top:${y}px; width:${w}px; height:${h}px;`;
        },

        // Human-readable summary shown beneath the picker. Reports
        // dimensions in IMAGE pixels (what the server will actually
        // crop), not display pixels — that's the number the user
        // cares about because it determines final master resolution.
        cropRoiSummary() {
            const img = this._cropRoiInImagePixels();
            if (!img) return '';
            return `${img.width} × ${img.height} px`;
        },

        // Returns ROI in IMAGE pixel coordinates (server-space) or null
        // if the user hasn't drawn yet or the <img> hasn't measured.
        _cropRoiInImagePixels() {
            const r = this.crop.roi;
            if (r.startX == null || r.endX == null) return null;
            const dispW = this.crop.imgDisplayWidth;
            const dispH = this.crop.imgDisplayHeight;
            const natW = this.crop.imgNaturalWidth;
            const natH = this.crop.imgNaturalHeight;
            if (!dispW || !dispH || !natW || !natH) return null;
            const sx = natW / dispW;
            const sy = natH / dispH;
            const x = Math.round(Math.min(r.startX, r.endX) * sx);
            const y = Math.round(Math.min(r.startY, r.endY) * sy);
            const w = Math.round(Math.abs(r.endX - r.startX) * sx);
            const h = Math.round(Math.abs(r.endY - r.startY) * sy);
            // Clamp inside image bounds — float math + the picker's
            // bounding-rect clamp can leave a 1-pixel overshoot near
            // the right/bottom edges that the server would reject.
            const cw = Math.min(w, natW - x);
            const ch = Math.min(h, natH - y);
            if (cw < 1 || ch < 1) return null;
            return { x, y, width: cw, height: ch };
        },

        async cropStartRun() {
            if (this.crop.busy) return;
            const roi = this._cropRoiInImagePixels();
            if (!roi) {
                this.crop.error = 'Draw a rectangle on the image first.';
                return;
            }
            this.crop.busy = true;
            this.crop.error = '';
            try {
                const body = {
                    paths: [this.crop.sourcePath],
                    x: roi.x, y: roi.y,
                    width: roi.width, height: roi.height
                };
                const r = await this.apiFetch('/api/crop/run', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(body)
                });
                const data = await r.json();
                const fail = (data.failures && data.failures[0]);
                if (fail) {
                    this.crop.error = fail.error || 'Crop failed';
                    return;
                }
                const ok = (data.results && data.results[0]);
                const out = ok ? (ok.outputPath.split(/[\\/]/).pop() || ok.outputPath)
                               : this.crop.outputName;
                this.toast(`Cropped — saved as ${out}`, 'success');
                this.cropClose();
                // Refresh the FILES library so the new sibling shows
                // up without a manual rescan. RescanAsync on the
                // server already runs but the client cache needs a
                // re-fetch.
                if (typeof this.studioRescan === 'function') {
                    try { this.studioRescan(); } catch {}
                }
            } catch (e) {
                this.crop.error = (e && e.message) ? e.message : String(e);
            } finally {
                this.crop.busy = false;
            }
        },

        // Heuristic: warn when the selection appears to be individual
        // light frames rather than a stacked master. Decon/Denoise on
        // un-stacked lights is usually a mistake, the model bakes
        // in noise that integration would have averaged out, AND
        // strength normalization is computed per-frame so per-tile
        // stretching looks inconsistent across the batch.
        //
        // Positive signals (path is probably a MASTER → no warning):
        //   • basename starts with: result_, integration_, integrated_,
        //     stack_, stacked_, master_, autosave (Siril autosave.fit),
        //     livestack_, or contains _drizzle_ / _stack_ / _integrated_
        //   • basename has a {totalseconds}s suffix (Siril stacker
        //     names its outputs e.g. result_3960s.fit)
        //   • already a processed sibling: ends with _bge / _denoise /
        //     _decon (you'd be chaining ops on a master)
        //   • path is under /integrated/ /processed/ /masters/ /stacks/
        //     /results/ /siril/ /bge/ /denoise/ /decon/
        //
        // Only when NONE of those positives match AND the path is
        // under /lights/ do we fire the warning. Lights-folder alone
        // used to be enough but that false-positives every time a
        // user keeps stacks under e.g. /Astro/M81/lights_processed/.
        graxpertPathsLookLikeLights() {
            // GX-12i: _decon now ships with a variant suffix
            // (_decon_stars, _decon_objects). Match either the old
            // bare _decon, the new _decon_<variant>, or with a numeric
            // collision-avoidance suffix tacked on by the saver.
            const masterRx = /(^|[\\/])(?:result|integration|integrated|stack|stacked|master|autosave|livestack)[_-]|_drizzle_|_stack_|_integrated_|_\d+s\.(?:fits?|xisf|fts)$|_bge(?:_\d+)?\.|_denoise(?:_\d+)?\.|_decon(?:_(?:stars|objects))?(?:_\d+)?\./i;
            const masterFolderRx = /[\\/](?:integrated|processed|masters?|stacks?|results?|siril|bge|denoise|decon)[\\/]/i;
            return this.graxpert.modalPaths.some(p => {
                if (masterFolderRx.test(p)) return false;
                if (masterRx.test(p.split(/[\\/]/).pop() || '')) return false;
                return /[\\/]lights?[\\/]/i.test(p);
            });
        },

        // GX-12m: iOS detection. UA includes "iPhone"/"iPad"/"iPod" on
        // classic iOS; iPadOS 13+ reports "MacIntel" + maxTouchPoints>1
        // (Apple's "desktop class" Safari mode). Both surfaces have the
        // same per-tab OOM kill behaviour that we want to warn about.
        _isIOS() {
            const ua = navigator.userAgent || '';
            if (/iPhone|iPad|iPod/i.test(ua)) return true;
            if (navigator.platform === 'MacIntel'
                && navigator.maxTouchPoints > 1) return true;
            return false;
        },

        // GX-12n: dynamic Denoise model picker, driven by the ONNX
        // manifest so quantized siblings (e.g. 2.0.0-fp16, 2.0.0-int8
        // produced by scripts/quantize_onnx_models.py) appear in the
        // dropdown without any code change. iOS rises the quantized
        // variants because they're the only ones with a real shot of
        // not OOM-killing the tab.
        denoiseModelChoices() {
            const models = (this.onnx?.manifest?.models || [])
                .filter(m => m.family === 'denoise');
            // Build label: "<version>, <sizeMB> MB"
            const choices = models.map(m => {
                const mb = m.sizeBytes
                    ? (m.sizeBytes / (1024 * 1024)).toFixed(0)
                    : '?';
                let tag = '';
                if (m.version.endsWith('-fp16')) tag = ' (FP16)';
                else if (m.version.endsWith('-int8')) tag = ' (INT8)';
                return {
                    version: m.version,
                    label: `v${m.version}, ${mb} MB${tag}`,
                    sizeBytes: m.sizeBytes || 0,
                    isQuantized: tag !== '',
                };
            });
            // GX-12n2: Sort priority on iOS is NOT just "smallest first".
            // Order matters because ORT Web's WASM execution provider
            // (the only backend we use on iOS, WebGPU is force-disabled
            // there) does NOT include the INT8 quantized operators
            // bundled in the default ort.webgpu.min.js distribution.
            // Loading an -int8 model on iOS produces "no backend found"
            // + an OOM cascade while the runtime allocates buffers
            // before realising the ops are missing.
            //
            // So on iOS the ordering is: FP16 first, then FP32, then
            // INT8 last (still listed so the user can try it if they
            // ship a custom ORT Web build with int8 ops, but not the
            // default pick). Desktop keeps the "non-quantized first"
            // ordering since FP32 is fastest there with WebGPU.
            const iOS = this._isIOS();
            const tagPriority = (v) => {
                if (v.endsWith('-fp16')) return 0;
                if (v.endsWith('-int8')) return 2;
                return 1;   // unsuffixed FP32
            };
            choices.sort((a, b) => {
                if (iOS) {
                    const pa = tagPriority(a.version);
                    const pb = tagPriority(b.version);
                    if (pa !== pb) return pa - pb;
                    return a.sizeBytes - b.sizeBytes;
                }
                // Desktop: prefer non-quantized first, then by version.
                if (a.isQuantized !== b.isQuantized)
                    return a.isQuantized ? 1 : -1;
                return a.version.localeCompare(b.version);
            });
            // Hard fallback when the manifest is empty (server hasn't
            // rescanned yet), keep the original built-in choices so
            // the UI doesn't go blank.
            if (choices.length === 0) {
                return [
                    { version: '2.0.0', label: 'v2.0.0, ~284 MB',  sizeBytes: 284e6, isQuantized: false },
                    { version: '3.0.2', label: 'v3.0.2, ~456 MB',  sizeBytes: 456e6, isQuantized: false },
                ];
            }
            return choices;
        },

        graxpertSuffix(op, runOpts) {
            // GX-12i: decon now has two flavours (stars-only vs object-
            // only) picked via the modal Method dropdown. Surface the
            // variant in the filename so a directory with both runs on
            // the same source doesn't collide and the user can see at a
            // glance which model produced each output.
            if (op === 'deconvolution') {
                const t = (runOpts && runOpts.target) || 'stars';
                return t === 'objects' ? '_decon_objects' : '_decon_stars';
            }
            return op === 'background-extraction' ? '_bge'
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
            // GX-2: branch on the in-browser toggle. When the operation
            // has an ONNX model available + the user hasn't opted out,
            // run the pipeline locally. Otherwise fall through to the
            // existing CLI subprocess. Toggle defaults to the browser
            // path so the user gets the in-browser benefits without
            // having to think about it.
            if (this.graxpert.modalRunInBrowser
                && this.onnxAvailableForOp(this.graxpert.modalOp)) {
                return this._graxpertRunInBrowser();
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
                        // GX-12i: stars vs objects picks the GraXpert
                        // CLI subcommand (deconv-stellar / deconv-obj)
                        // and the output suffix (_decon_stars / _decon_objects).
                        deconTarget: this.graxpert.modalDeconTarget || 'stars',
                        denoiseStrength: this.graxpert.modalDenoiseStrength,
                        // GX-12k: forward the picked denoise model
                        // version so CLI runs the same AI variant the
                        // browser would. GraXpert's `-ai_version` is
                        // op-agnostic, so only send it for denoise to
                        // avoid accidentally pinning BGE/decon to a
                        // value that doesn't exist for those families.
                        aiVersion: this.graxpert.modalOp === 'denoising'
                            ? (this.graxpert.modalDenoiseVersion
                                || this.settings.onnxDefaultDenoiseVersion)
                            : null
                    })
                });
                this.graxpert.currentJobId = r.jobId;
                this.toast('GraXpert batch started: ' + r.jobId, 'ok');
                this._graxpertStartPolling();
            } catch (e) {
                this.toast('GraXpert start failed: ' + (e.message || ''), 'error');
            }
        },

        // GX-2: returns true when the operation can run via ORT Web,
        // depends on the bundle being loadable + the matching model
        // being present in the manifest.
        onnxAvailableForOp(op) {
            const models = this.onnx?.manifest?.models || [];
            if (!models.length) return false;
            switch (op) {
                case 'background-extraction':
                    return models.some(m => m.family === 'bge');
                case 'denoising':
                    return models.some(m => m.family === 'denoise');
                case 'deconvolution':
                    return models.some(m => m.family === 'decon-stars'
                                          || m.family === 'decon-objects');
                default:
                    return false;
            }
        },

        // ─── GX-2: in-browser GraXpert runner ──────────────────────────
        // For each selected file: GET raw uint16 pixels from the server,
        // hand them to the matching pipeline, POST the result back as a
        // sibling FITS. Pipelines (BGE/Denoise/Decon) only differ in the
        // class instantiated + the params dict; the orchestration here is
        // shared.

        async _graxpertRunInBrowser() {
            // GX-6: gate on CC BY-NC-SA 4.0 consent. Resolves
            // immediately when the flag is already set (localStorage
            // OR server-side onnxLicenseAcknowledged); otherwise the
            // promise pauses here until the user clicks I-agree /
            // cancel in the modal.
            const ok = await this._ensureOnnxLicenseAccepted();
            if (!ok) {
                this.toast('AI inference cancelled (licence not accepted)', 'warn');
                return;
            }
            const paths = [...this.graxpert.modalPaths];
            this.graxpert.browserActive = true;
            this.graxpert.browserAbortRequested = false;
            this.graxpert.browserDone = 0;
            this.graxpert.browserTotal = paths.length;
            this.graxpert.browserPhase = 'preparing';
            this.graxpert.browserProgress = 0;
            try {
                const op = this.graxpert.modalOp;
                let pipeline;
                let runOpts = {};
                switch (op) {
                    case 'background-extraction':
                        pipeline = new OnnxRegistry.BgePipeline();
                        runOpts = {
                            correction: this.graxpert.modalCorrection,
                            // GX-9: forward the "save background" toggle so
                            // BgePipeline returns the modelled background
                            // plane; we save it as a second sibling FITS
                            // ({stem}_bge_bg.fits) below. Matches the CLI
                            // path's -bg behaviour.
                            saveBackground: !!this.graxpert.modalSaveBackground,
                        };
                        break;
                    case 'denoising':
                        pipeline = new OnnxRegistry.DenoisePipeline();
                        runOpts = {
                            strength: this.graxpert.modalDenoiseStrength,
                            // GX-12k: per-run model version from the
                            // modal dropdown (hydrated from settings,
                            // user can override).
                            version: this.graxpert.modalDenoiseVersion
                                  || this.settings.onnxDefaultDenoiseVersion
                                  || '2.0.0',
                        };
                        break;
                    case 'deconvolution':
                        pipeline = new OnnxRegistry.DeconPipeline();
                        // GX-12h: target now comes from the modal
                        // dropdown, Stars-only picks decon-stars,
                        // Object-only picks decon-objects ONNX models.
                        runOpts = {
                            strength: this.graxpert.modalDeconStrength,
                            psfPixels: this.graxpert.modalDeconPsfSize,
                            target: this.graxpert.modalDeconTarget || 'stars',
                        };
                        break;
                    default:
                        throw new Error('Unknown operation: ' + op);
                }
                const suffix = this.graxpertSuffix(op, runOpts);
                const written = [];
                for (let idx = 0; idx < paths.length; idx++) {
                    // Abort checkpoint between files. The user clicked
                    // Abort on the modal, so stop launching new work
                    // -- the file already in flight finishes (no way
                    // to hard-kill a WebGPU shader from JS), but we
                    // skip everything remaining in the batch.
                    if (this.graxpert.browserAbortRequested) {
                        this.graxpert.browserPhase = 'aborted';
                        this.toast('GraXpert aborted by user', 'warn');
                        break;
                    }
                    const path = paths[idx];
                    const stem = path.split(/[\\/]+/).pop();
                    this.graxpert.browserPhase = stem + ', fetching pixels';
                    this.graxpert.browserProgress = idx / paths.length;

                    const src = await this._onnxFetchSourcePixels(path);
                    if (!src) {
                        this.toast('Skipped ' + stem + ', could not decode', 'warn');
                        continue;
                    }

                    this.graxpert.browserPhase = stem + ', running ' + op;
                    const result = await pipeline.run(
                        src.pixels, src.width, src.height,
                        Object.assign({}, runOpts, {
                            // GX-9: forward the channel count so RGB
                            // FITS process per-channel instead of
                            // collapsing to the first plane.
                            channels: src.channels,
                            onProgress: (phase, frac) => {
                                this.graxpert.browserPhase = stem + ', ' + phase
                                  + (frac != null ? ' ' + Math.round(frac * 100) + '%' : '');
                                // GX-9 (UX): also drive the overall
                                // progress bar from within-pipeline
                                // ticks instead of only jumping per
                                // completed file. For multi-file
                                // batches the bar advances through
                                // (idx + within) / total.
                                if (frac != null) {
                                    this.graxpert.browserProgress =
                                        (idx + frac) / paths.length;
                                }
                            }
                        }));

                    this.graxpert.browserPhase = stem + ', saving sibling FITS';
                    const outPath = await this._onnxSaveResult(
                        path, suffix, result.pixels, result.width,
                        result.height, result.channels);
                    if (outPath) written.push(outPath);

                    // GX-9: if BGE returned the modelled background plane
                    // (saveBackground: true), save it as a second sibling
                    // {stem}_bge_bg.fits next to the corrected output.
                    // CLI's -bg flag does the same.
                    if (result.background) {
                        this.graxpert.browserPhase = stem + ', saving background model';
                        const bgPath = await this._onnxSaveResult(
                            path, suffix + '_bg',
                            result.background, result.width,
                            result.height, result.channels);
                        if (bgPath) written.push(bgPath);
                    }

                    this.graxpert.browserDone++;
                    this.graxpert.browserProgress = (idx + 1) / paths.length;
                }
                this.graxpert.browserPhase = 'done';
                this.toast('Browser GraXpert done, ' + written.length
                          + ' / ' + paths.length + ' written', 'ok');
                // GX-11: build src→out pairs for the comparator. We
                // need to re-walk because the inner loop pushed only
                // outPaths to `written` (saveBackground pushes two
                // entries per source, main result + bg model, so
                // a positional zip would mis-align). Match by stem.
                const pairs = [];
                for (const inPath of paths) {
                    const stem = inPath.split(/[\\/]+/).pop()
                        .replace(/\.(fits|fit|fts|xisf)$/i, '');
                    const match = written.find(o => {
                        const name = o.split(/[\\/]+/).pop();
                        return name.startsWith(stem + suffix);
                    });
                    if (match) pairs.push({
                        src: inPath, out: match,
                        label: inPath.split(/[\\/]+/).pop(),
                    });
                }
                await this._graxpertHandleCompletion(
                    written, paths.length - written.length, pairs);
            } catch (e) {
                console.error('[GraXpert browser] failed', e);
                this.toast('Browser run failed: ' + (e.message || ''), 'error');
            } finally {
                this.graxpert.browserActive = false;
            }
        },

        async _onnxFetchSourcePixels(path) {
            // XFER: source-pixels can be tens of MB for a stretched
            // full-res ushort buffer, show the transfer chip so the
            // user sees the wait isn't a freeze.
            const r = await this.apiDownload(
                '/api/onnx/source-pixels?path=' + encodeURIComponent(path),
                { label: 'Read ' + (path.split(/[\\/]/).pop() || 'pixels') });
            if (!r.ok) return null;
            const w  = parseInt(r.headers.get('X-Width'),    10);
            const h  = parseInt(r.headers.get('X-Height'),   10);
            const ch = parseInt(r.headers.get('X-Channels'), 10) || 1;
            const buf = await r.arrayBuffer();
            // uint16 LE - DataView would copy; Uint16Array on a buffer
            // of even-byte length is a zero-copy view if the platform
            // is LE (which all browsers are).
            const pixels = new Uint16Array(buf);
            return { pixels, width: w, height: h, channels: ch };
        },

        async _onnxSaveResult(source, suffix, pixels, width, height, channels) {
            const fd = new FormData();
            fd.append('source',   source);
            fd.append('suffix',   suffix);
            fd.append('width',    String(width));
            fd.append('height',   String(height));
            fd.append('channels', String(channels));
            // Wrap the Uint16Array's underlying ArrayBuffer directly,
            // Blob constructor accepts BufferSource without copying.
            const blob = new Blob([pixels.buffer]);
            fd.append('pixels', blob, 'pixels.bin');
            // XFER: apiUpload so the activity bar shows real upload
            // progress (these FITS blobs can be 50+ MB after GraXpert
            // expands the working buffer).
            const r = await this.apiUpload('/api/onnx/save', fd, {
                label: 'Save ' + (source.split(/[\\/]/).pop() || 'ONNX result')
            });
            if (!r.ok) {
                const e = await r.json().catch(() => null);
                throw new Error(e?.error || ('HTTP ' + r.status));
            }
            const j = await r.json();
            return j.path;
        },

        // UX: close the GraXpert modal after a successful run + (when on
        // FILES) refresh the directory and pre-select the new sibling(s)
        // so the user lands on them instead of staring at a static
        // "done" modal they have to dismiss manually. Both browser-mode
        // (_graxpertRunInBrowser) and CLI-mode (_graxpertStartPolling)
        // call this on completion. failedCount > 0 keeps the modal
        // open so the user can read the error context, auto-closing on
        // partial failure would hide the diagnostics.
        //
        // GX-11: pairs is an optional [{ src, out, label }, ...] used
        // to auto-open the before/after comparator. Browser-mode
        // builds it inline; CLI-mode pairs the modalPaths input list
        // against the job's results array by index.
        async _graxpertHandleCompletion(writtenPaths, failedCount, pairs) {
            if (failedCount > 0) return;
            // GX-12r: snapshot the op BEFORE closing the modal,
            // graxpertCloseModal resets modalOp, and we want the
            // comparator title to know which op produced these pairs.
            const opThatRan = this.graxpert.modalOp;
            // Close + null-out the modal flag. graxpertCloseModal also
            // tears down any CLI poll timer so we don't keep hitting
            // the server after the modal goes away.
            this.graxpertCloseModal();
            if (this.tab !== 'files') return;
            try { await this.filesReload(); }
            catch { /* non-fatal, selection step below is best-effort */ }
            if (!writtenPaths || writtenPaths.length === 0) return;
            // Pre-select the new siblings so they're visually
            // distinguished from the source. Filter against the actual
            // entries the listing just returned so a path that doesn't
            // belong to the current cwd (output went to a different
            // folder) doesn't silently leave a dangling selection.
            // Match server-emitted paths against entry.fullPath. Both
            // sides come from the same backend so the separator
            // (Win: '\\', Linux: '/') is consistent, no normalize
            // needed. Case-insensitive on Windows is also fine since
            // Windows fs is case-insensitive and a mismatch would be
            // a server bug, not a UX one.
            const isWin = navigator.platform.startsWith('Win');
            const eq = isWin
                ? (a, b) => a.toLowerCase() === b.toLowerCase()
                : (a, b) => a === b;
            const inCwd = writtenPaths.filter(p =>
                this.files.entries.some(e => eq(e.fullPath, p)));
            if (inCwd.length === 0) return;
            this.files.selectedPaths = inCwd.slice();
            // Scroll the first match into view on the next render tick
            // so Alpine has actually painted the highlighted row.
            this.$nextTick(() => {
                const first = inCwd[0];
                const row = document.querySelector(
                    '[data-files-row-path="' + CSS.escape(first) + '"]');
                if (row && typeof row.scrollIntoView === 'function') {
                    row.scrollIntoView({ behavior: 'smooth', block: 'center' });
                }
            });
            // GX-11: auto-open the before/after comparator on the
            // first src→out pair. Filter to pairs that actually have
            // both ends populated, saveBackground secondary writes
            // share the same source FITS, so duplicates are dropped.
            if (pairs && pairs.length) {
                const seen = new Set();
                const valid = pairs.filter(p => {
                    if (!p || !p.src || !p.out) return false;
                    const k = p.src + '|' + p.out;
                    if (seen.has(k)) return false;
                    seen.add(k); return true;
                });
                if (valid.length) this.graxpertOpenCompare(valid, 0, 'gx', opThatRan);
            }
        },

        // ─── GX-11: Before/After comparator ──────────────────────────
        // Modal overlay with two stretched JPEGs of the same dimensions
        // stacked on top of each other; the top image (output) is
        // clipped via clip-path to the right of the drag handle so
        // moving the handle left reveals the source underneath.
        // pairs: [{ src, out, label }]; index picks which pair to show.

        graxpertOpenCompare(pairs, index, mode, op) {
            this.graxpertCompare.pairs = pairs;
            this.graxpertCompare.index = index || 0;
            this.graxpertCompare.split = 0.5;
            this.graxpertCompare.dragging = false;
            // GX-12g: 'gx' = GraXpert auto-open (BEFORE/AFTER tags);
            // 'compare' = arbitrary two-file pick from FILES (show
            // the actual filenames on each side).
            this.graxpertCompare.mode = mode || 'gx';
            // GX-12r: op identifies the GraXpert pipeline that
            // produced these pairs so the header can read e.g.
            // "GraXpert Denoise Comparison". null in compare-mode
            // (no op context, see graxpertCompareTitle).
            this.graxpertCompare.op = op || null;
            this.graxpertCompare.open = true;
        },

        // GX-12r: header title resolver for the comparator. Maps the
        // op tag to a human-readable label; falls back to the
        // generic Before/After or Compare wording when no op is set
        // (FILES "Compare" entry point, older snapshots without
        // op context).
        graxpertCompareTitle() {
            if (this.graxpertCompare.mode === 'compare') return 'Compare';
            switch (this.graxpertCompare.op) {
                case 'denoising':              return 'GraXpert Denoise Comparison';
                case 'deconvolution':          return 'GraXpert Decon Comparison';
                case 'background-extraction':  return 'GraXpert BGE Comparison';
                default:                       return 'Before vs After';
            }
        },

        // GX-12r2: file-pair label shown next to the title in the
        // header. Shows BOTH basenames separated by an arrow so the
        // user sees what came in and what went out (e.g.
        // "M81_master.fits → M81_master_denoise.fits"). Falls back
        // gracefully when src/out are missing or identical.
        graxpertComparePairLabel() {
            const pair = this.graxpertCompare.pairs[this.graxpertCompare.index];
            if (!pair) return '';
            const base = (p) => (p || '').split(/[\\/]+/).pop();
            const srcName = base(pair.src);
            const outName = base(pair.out);
            if (srcName && outName && srcName !== outName) {
                return srcName + ' → ' + outName;
            }
            // Fallback to whichever side has a name (or the pair's
            // own .label that callers may have set explicitly).
            return outName || srcName || pair.label || '';
        },

        // GX-12g: label resolver for the corner tags. Returns the
        // generic BEFORE/AFTER words for a GraXpert op (the user
        // just produced the AFTER file, they don't need its name
        // surfaced redundantly) and the actual filename for an
        // arbitrary Compare invocation.
        graxpertCompareTag(side /* 'src' | 'out' */) {
            const pair = this.graxpertCompare.pairs[this.graxpertCompare.index];
            if (this.graxpertCompare.mode === 'compare' && pair) {
                const p = pair[side];
                if (p) return p.split(/[\\/]+/).pop();
            }
            return side === 'src' ? 'BEFORE' : 'AFTER';
        },

        graxpertCloseCompare() {
            this.graxpertCompare.open = false;
            this.graxpertCompare.dragging = false;
        },

        graxpertCompareNext() {
            const n = this.graxpertCompare.pairs.length;
            if (n <= 1) return;
            this.graxpertCompare.index =
                (this.graxpertCompare.index + 1) % n;
            this.graxpertCompare.split = 0.5;
        },

        graxpertComparePrev() {
            const n = this.graxpertCompare.pairs.length;
            if (n <= 1) return;
            this.graxpertCompare.index =
                (this.graxpertCompare.index - 1 + n) % n;
            this.graxpertCompare.split = 0.5;
        },

        // URL helper that points the FILES preview endpoint at the
        // FITS file at full-ish resolution (maxDim=2400 matches what
        // filesOpenPreview uses, gives us a sharp render without
        // dragging the full 24 MP through the wire). Cache-busted by
        // pair index so swapping between pairs reuses cached bytes
        // for the same image but re-fetches when the path changes.
        graxpertCompareSrc(side /* 'src' | 'out' */) {
            const pair = this.graxpertCompare.pairs[this.graxpertCompare.index];
            if (!pair) return '';
            const p = pair[side];
            if (!p) return '';
            // GX-12c: AFTER renders with the BEFORE file's histogram
            // params pinned via stretchFrom, otherwise each side
            // auto-stretches independently and a slight noise-floor
            // shift in the denoised output produces wildly different
            // colour mapping (looks like a colour-balance change
            // instead of a noise reduction).
            let url = '/api/files/preview?path=' + encodeURIComponent(p)
                    + '&maxDim=2400';
            if (side === 'out' && pair.src) {
                url += '&stretchFrom=' + encodeURIComponent(pair.src);
            }
            // <img> can't carry the Authorization header; append the
            // bearer token as a query-string fallback so the preview
            // request authenticates even when the session cookie is
            // missing / expired / not sent.
            return this.authUrl(url);
        },

        // Mouse / touch handlers for the drag handle. Position is
        // computed against the comparator wrapper's bounding rect so
        // it works regardless of viewport size or aspect-letterbox.
        graxpertCompareMouseDown(ev) {
            this.graxpertCompare.dragging = true;
            this.graxpertCompareMove(ev);
        },
        graxpertCompareMouseUp() {
            this.graxpertCompare.dragging = false;
        },
        graxpertCompareMove(ev) {
            if (!this.graxpertCompare.dragging) return;
            const wrap = document.querySelector('.graxpert-compare-wrap');
            if (!wrap) return;
            const rect = wrap.getBoundingClientRect();
            const clientX = ev.touches && ev.touches[0]
                ? ev.touches[0].clientX : ev.clientX;
            const x = Math.max(0, Math.min(rect.width, clientX - rect.left));
            this.graxpertCompare.split = x / rect.width;
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
                        const msg = `GraXpert done, ${job.done} ok, ${job.failed} failed`;
                        this.toast(msg, job.failed ? 'warn' : 'ok');
                        // UX: auto-close + select the new siblings.
                        // GraXpertBatchJob.Results carries the full
                        // output path for each finished file; map to
                        // a flat string[] for _graxpertHandleCompletion.
                        const written = (job.results || [])
                            .map(r => r.outputPath)
                            .filter(p => !!p);
                        // GX-11: pair against the modalPaths input
                        // list. CLI processes inputs in order so a
                        // positional zip is safe (no per-input split
                        // like saveBackground in the browser path).
                        const inputs = this.graxpert.modalPaths || [];
                        const pairs = [];
                        for (let i = 0; i < written.length && i < inputs.length; i++) {
                            pairs.push({
                                src: inputs[i], out: written[i],
                                label: inputs[i].split(/[\\/]+/).pop(),
                            });
                        }
                        await this._graxpertHandleCompletion(
                            written, job.failed || 0, pairs);
                    }
                } catch (e) { /* transient, keep polling */ }
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

        // Unified abort for the GraXpert modal. Routes to the CLI
        // cancel endpoint when a server-side job is in flight, or
        // flips the browser-loop abort flag when the in-browser
        // pipeline is running. The modal's Abort button is wired to
        // this so the user doesn't have to know which mode is
        // active.
        async graxpertAbortRun() {
            if (this.graxpert.currentJobId) {
                await this.graxpertCancelCurrent();
            }
            if (this.graxpert.browserActive) {
                this.graxpert.browserAbortRequested = true;
                this.graxpert.browserPhase = 'aborting...';
                this.toast('GraXpert abort requested -- waiting for current file to finish', 'info');
            }
        },

        // True while the GraXpert modal is busy in either backend.
        // Drives the modal button visibility (Start + Close hidden,
        // Abort shown) so the user can't kick off a second run or
        // dismiss the dialog mid-execution.
        graxpertIsRunning() {
            return !!(this.graxpert.browserActive
                || (this.graxpert.currentJob && !this.graxpert.currentJob.completedAt));
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
                // Same ?driver= dispatch the camera / mount selects use.
                // INDI is the default — older clients that never picked a
                // driver still work, and the ASCOM picker on Windows is
                // honoured.
                const qs = this.focuserDriver && this.focuserDriver !== 'indi'
                    ? `?driver=${encodeURIComponent(this.focuserDriver)}` : '';
                await this.apiPost(
                    `/api/focuser/select/${encodeURIComponent(this.equipFocuserChoice)}${qs}`);
                await this.apiPost('/api/focuser/connect');
                this.selectedFocuser = this.equipFocuserChoice;
                this.focusConnected = true;
                this._persistRigSelection({
                    focuser: this.equipFocuserChoice,
                    focuserDriver: this.focuserDriver || 'indi'
                });
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
                const qs = this.filterWheelDriver && this.filterWheelDriver !== 'indi'
                    ? `?driver=${encodeURIComponent(this.filterWheelDriver)}` : '';
                await this.apiPost(
                    `/api/filterwheel/select/${encodeURIComponent(this.equipFilterChoice)}${qs}`);
                await this.apiPost('/api/filterwheel/connect');
                this.selectedFilterWheel = this.equipFilterChoice;
                this.filterWheel.connected = true;
                this._persistRigSelection({
                    filterWheel: this.equipFilterChoice,
                    filterWheelDriver: this.filterWheelDriver || 'indi'
                });
                this.toast('Filter wheel connected: ' + this.equipFilterChoice, 'ok');
            } catch (e) {
                this.toast('Filter wheel connection failed: ' + e.message, 'error');
            }
        },

        // ASCOM-4 follow-up: parallel of loadCameraDrivers /
        // loadMountDrivers for focuser + filter wheel. Same payload
        // shape (CameraDriverInfo on the wire), same fallback list
        // when the endpoint isn't reachable yet (offline page reload).
        async loadFocuserDrivers() {
            try {
                this.focuserDrivers = await this.apiGet('/api/focuser/drivers');
            } catch (e) {
                this.focuserDrivers = [{
                    id: 'indi', name: 'INDI', available: true,
                    description: 'Any focuser the running INDI server exposes.'
                }];
            }
        },
        async loadFilterWheelDrivers() {
            try {
                this.filterWheelDrivers = await this.apiGet('/api/filterwheel/drivers');
            } catch (e) {
                this.filterWheelDrivers = [{
                    id: 'indi', name: 'INDI', available: true,
                    description: 'Any filter wheel the running INDI server exposes.'
                }];
            }
        },
        // Vendor-side discovery (ASCOM registry). Same call shape as
        // detectVendorCameras. Skip the IndiClient-driven dropdown.
        async detectVendorFocusers() {
            this.focuserDiscovering = true;
            try {
                const list = await this.apiGet(
                    `/api/focuser/discover?driver=${encodeURIComponent(this.focuserDriver)}`);
                this.focuserVendorDevices = list || [];
                if (this.focuserVendorDevices.length === 0) {
                    this.toast('No focusers detected for ' + this.focuserDriver, 'warn');
                }
            } catch (e) {
                this.toast('Detect failed: ' + (e.message || ''), 'error');
                this.focuserVendorDevices = [];
            } finally {
                this.focuserDiscovering = false;
            }
        },
        async detectVendorFilterWheels() {
            this.filterWheelDiscovering = true;
            try {
                const list = await this.apiGet(
                    `/api/filterwheel/discover?driver=${encodeURIComponent(this.filterWheelDriver)}`);
                this.filterWheelVendorDevices = list || [];
                if (this.filterWheelVendorDevices.length === 0) {
                    this.toast('No filter wheels detected for ' + this.filterWheelDriver, 'warn');
                }
            } catch (e) {
                this.toast('Detect failed: ' + (e.message || ''), 'error');
                this.filterWheelVendorDevices = [];
            } finally {
                this.filterWheelDiscovering = false;
            }
        },
        get focuserDriverInfo() {
            return this.focuserDrivers.find(d => d.id === this.focuserDriver) || null;
        },
        get filterWheelDriverInfo() {
            return this.filterWheelDrivers.find(d => d.id === this.filterWheelDriver) || null;
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
                    this.toast('PHD2 is up, connecting…', 'ok');
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
                // Equipment is disconnected by SetProfileAsync, refresh
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
                // Force the iframe to reload so the user sees the
                // refreshed PHD2 window without having to hard-refresh.
                this._reloadPhd2GuiIframe();
            }
        },
        async phd2GuiRelaunchPhd2() {
            this.phd2GuiBusy = true;
            try {
                const r = await this.apiPost('/api/guider/gui-session/relaunch-phd2');
                if (r.phd2Running) {
                    this.toast('PHD2 relaunched inside session', 'ok');
                    // Iframe currently shows a bare xpra desktop; reload
                    // to pick up the freshly-spawned PHD2 window.
                    this._reloadPhd2GuiIframe();
                } else {
                    this.toast('Relaunch failed: ' + (r.error || 'unknown'), 'error');
                }
            } catch (e) {
                this.toast('Relaunch failed: ' + e.message, 'error');
            } finally {
                this.phd2GuiBusy = false;
                await this.loadPhd2GuiStatus();
            }
        },
        _reloadPhd2GuiIframe() {
            // Defer + reassign src so xpra HTML5 client reconnects
            // after a session restart / phd2 relaunch.
            this.$nextTick(() => {
                const iframe = document.querySelector('.phd2-gui-iframe');
                if (iframe) {
                    // Cache-buster ensures the iframe actually re-fetches
                    // even if it would otherwise reuse the existing connection.
                    iframe.src = '/phd2-gui/?_=' + Date.now();
                }
            });
        },

        // ----- PH2VNC: Windows TightVNC + noVNC bridge lifecycle -----
        // Sibling of phd2GuiStart/Stop/Restart above. The status
        // payload arrives on every WS tick via guider.vncSession,
        // so the explicit GET is only needed on first paint (when
        // the tab loads before the first WS frame) and as a manual
        // re-detect after the user installs/uninstalls TightVNC.
        async loadPhd2VncStatus() {
            try {
                this.phd2VncSession = await this.apiGet('/api/guider/vnc-session/status');
            } catch (e) {
                this.phd2VncSession = { supportedOs: false, lastError: e.message };
            }
        },
        async phd2VncRedetect() {
            this.phd2VncBusy = true;
            try {
                await this.apiPost('/api/guider/vnc-session/redetect');
                await this.loadPhd2VncStatus();
                if (this.phd2VncSession?.tightVncInstalled) {
                    this.toast('TightVNC v' + this.phd2VncSession.tightVncVersion + ' detected', 'ok');
                } else {
                    this.toast('TightVNC still not detected', 'warn');
                }
            } catch (e) {
                this.toast('Re-detect failed: ' + e.message, 'error');
            } finally {
                this.phd2VncBusy = false;
            }
        },
        async phd2VncStartService() {
            this.phd2VncBusy = true;
            try {
                const r = await this.apiPost('/api/guider/vnc-session/start-service');
                if (r.serviceRunning) {
                    this.toast('TightVNC service started', 'ok');
                } else {
                    // Most common failure: not running elevated. Backend
                    // returns the actionable message in r.error.
                    this.toast(r.error || 'Failed to start TightVNC service', 'error');
                }
            } catch (e) {
                this.toast('Start failed: ' + e.message, 'error');
            } finally {
                this.phd2VncBusy = false;
                await this.loadPhd2VncStatus();
            }
        },
        async phd2VncStopService() {
            this.phd2VncBusy = true;
            try {
                const r = await this.apiPost('/api/guider/vnc-session/stop-service');
                if (!r.serviceRunning) {
                    this.toast('TightVNC service stopped', 'warn');
                } else {
                    this.toast(r.error || 'Failed to stop TightVNC service', 'error');
                }
            } catch (e) {
                this.toast('Stop failed: ' + e.message, 'error');
            } finally {
                this.phd2VncBusy = false;
                await this.loadPhd2VncStatus();
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
                    // Reconcile driver + device-selection state with what
                    // the backend actually has connected. Without this the
                    // RIGS Camera card reads cameraDriver from the saved
                    // rig profile (often 'indi') even when the backend
                    // is already running an Alpaca camera that auto-
                    // connected after Discover -- the dropdown shows the
                    // wrong driver + an empty device select while the
                    // camera-info readout below it is alive and well.
                    if (data.driver && data.driver !== this.cameraDriver) {
                        this.cameraDriver = data.driver;
                    }
                    if (data.deviceName) {
                        this.selectedCamera = data.deviceName;
                        if (!this.equipCameraChoice) this.equipCameraChoice = data.deviceName;
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
            // CAT-4: pull the catalog source list at the same time so
            // the Atlas filter's catalog dropdown is ready when the
            // user expands the filters panel. Empty array (or 503)
            // when the expanded DSO DB is missing.
            try {
                const cats = await this.apiGet('/api/sky/catalog/catalogs');
                this.atlasCatalogs = Array.isArray(cats) ? cats : [];
            } catch (e) {
                this.atlasCatalogs = [];
            }
        },

        async atlasSearch() {
            const params = new URLSearchParams();
            if (this.atlasFilter.type) params.set('type', this.atlasFilter.type);
            // CAT-4: backend param is `catalogId=` (the C# parameter
            // name); the dropdown stores it as filter.catalog locally
            // to match the data shape.
            if (this.atlasFilter.catalog) params.set('catalogId', this.atlasFilter.catalog);
            if (this.atlasFilter.constellation)
                params.set('constellation', this.atlasFilter.constellation);
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
            this.atlasFilter = { type: '', catalog: '', constellation: '',
                                 minMag: null, maxMag: null, minDec: null, maxDec: null };
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
                // Open the translucent info card on the left so the
                // imported Stellarium target has the same visual
                // treatment as a click or local-catalog search.
                this._populateSkyInfo({
                    name: t.name,
                    subtitle: t.type || 'Stellarium',
                    raDeg: t.raHours * 15,
                    decDeg: t.decDeg,
                    magnitude: typeof t.magnitude === 'number' ? t.magnitude : null,
                    types: t.type ? [t.type] : null
                });
                this.toast('Loaded from Stellarium: ' + t.name, 'ok');
            } catch (e) {
                this.toast('Stellarium fetch failed: ' + e.message, 'error');
            }
        },

        async searchSky() {
            const q = this.skySearch.trim();
            if (!q) return;
            try {
                // Always query BOTH sources in parallel and merge. The
                // DSO catalog only knows deep-sky objects (Messier /
                // NGC / IC / Arp / Sh2 / HCG / Abell). The engine
                // knows everything else the user is likely to type —
                // Sun, Moon, planets, satellites, bright stars (Vega,
                // Sirius), bundled comets. Querying both matters when
                // a query hits BOTH (e.g. "Sun" matches Sunflower
                // Galaxy NGC 5055 / M63 in the catalog AND the actual
                // Sun in the engine — user wants the engine hit
                // surfaced, not just the galaxies).
                const [catalogData, engineHit] = await Promise.all([
                    this.apiGet(
                        `/api/sky/catalog/search?query=${encodeURIComponent(q)}`)
                        .catch(() => ({ results: [] })),
                    this._skySearch(q).catch(() => null)
                ]);

                const catalogResults = catalogData.results || [];

                // Project engine hit (if any) onto the same shape as
                // catalog rows so the result list renders uniformly.
                let engineRow = null;
                if (engineHit
                    && Number.isFinite(engineHit.raDeg)
                    && Number.isFinite(engineHit.decDeg)) {
                    engineRow = {
                        name: engineHit.name || q,
                        ra: engineHit.raDeg / 15,
                        dec: engineHit.decDeg,
                        magnitude: typeof engineHit.magnitude === 'number'
                            ? engineHit.magnitude : null,
                        type: 'Solar system / Star',
                        commonName: null,
                        aliases: []
                    };
                }

                // Merge: engine first (it's typically what the user
                // typed verbatim — "Sun" / "Moon" / "Jupiter"), then
                // catalog. Dedupe by name (engine "Sun" vs catalog
                // entry literally named "Sun" — unlikely but cheap to
                // guard).
                const merged = [];
                if (engineRow) merged.push(engineRow);
                for (const r of catalogResults) {
                    if (!engineRow || (r.name || '').toLowerCase()
                            !== (engineRow.name || '').toLowerCase()) {
                        merged.push(r);
                    }
                }
                this.skyResults = merged;

                if (merged.length === 0) {
                    this.skyTarget = null;
                    this.skyShowResults = false;
                    this.toast('No objects found for "' + q + '"', 'warn');
                    return;
                }
                if (merged.length === 1) {
                    this.selectSkyTarget(merged[0]);
                    this.skyShowResults = false;
                    return;
                }
                this.skyShowResults = true;
            } catch (e) {
                this.toast('Sky search failed', 'error');
            }
        },

        // SWE-5: take a bridge rich-object payload and project it onto
        // the skyInfo card state. Also seeds skyTarget so Slew &
        // Center actions outside the card still go somewhere sensible.
        async _populateSkyInfo(obj) {
            if (!obj) return;
            // Icon character based on the first type. Stellarium type
            // codes ("Pla", "Gal", "Neb", "Sao", "OpC", "GlC", ...).
            const firstType = (obj.types && obj.types[0]) || '';
            const isPlanet = /^(Pla|Moo|Sun|Com|MPl)/i.test(firstType);
            const isStar = /^(Sao|Pul|PNe|\\*)/i.test(firstType) || (!firstType && obj.magnitude != null && obj.magnitude < 7);
            const icon = isPlanet ? '🌑' : isStar ? '★' : '☆';

            // Subtitle: prefer explicit one from bridge, else the type label.
            const subtitle = obj.subtitle
                || (obj.types ? obj.types.join(' · ') : '');

            this.skyInfo = {
                visible: true,
                title: obj.name || 'Unknown',
                subtitle: subtitle,
                icon: icon,
                imageUrl: '',         // filled async below
                magnitude: typeof obj.magnitude === 'number' ? obj.magnitude : null,
                distanceKm: typeof obj.distanceMeters === 'number'
                    ? Math.round(obj.distanceMeters / 1000) : null,
                radiusKm: typeof obj.radiusMeters === 'number'
                    ? Math.round(obj.radiusMeters / 1000) : null,
                raDeg: typeof obj.raDeg === 'number' ? obj.raDeg : null,
                decDeg: typeof obj.decDeg === 'number' ? obj.decDeg : null,
                types: obj.types || null,
                altitudeSamples: null,
                transitText: '',
                setText: '',
            };

            // Tear down any previous chart instance, Chart.js leaks
            // the canvas otherwise and the next render throws "Canvas
            // is already in use".
            if (this._skyInfoChart) {
                try { this._skyInfoChart.destroy(); } catch (_) { }
                this._skyInfoChart = null;
            }

            // Also stash as skyTarget so the existing Slew & Center
            // button below the map picks it up.
            if (Number.isFinite(obj.raDeg) && Number.isFinite(obj.decDeg)) {
                this.skyTarget = {
                    name: obj.name,
                    ra: obj.raDeg / 15,
                    dec: obj.decDeg
                };
                // Smooth-pan the engine view to the picked object,
                // gated by the user's "Auto-center on select" toggle.
                // When off, the card still opens and skyTarget is
                // still set (so Slew & Center / Slew Only buttons
                // work normally), but the engine view stays put.
                if (this.skyAutoCenterOnSelect) {
                    this._skyLookAt(obj.raDeg / 15, obj.decDeg, undefined, obj.name);
                }
            }

            // Async fetch the thumbnail from the Tonight's Best image
            // endpoint. Card opens immediately with the icon fallback;
            // the photo slides in if our catalog knows the name.
            try {
                const r = await this.apiGet(`/api/sky/image?name=${encodeURIComponent(obj.name)}`);
                if (r && r.available && r.thumbnailUrl
                    && this.skyInfo.title === obj.name) {
                    this.skyInfo.imageUrl = r.thumbnailUrl;
                }
            } catch (e) { /* no photo, keep icon */ }

            // Altitude chart + meridian/horizon times. Same endpoint
            // Tonight's Best uses (/api/sky/altitude → samples over the
            // observer's twilight window, 15 min step). We compute the
            // human-readable time-to-meridian-transit and
            // time-to-horizon-set client-side from the samples so the
            // numbers stay consistent with what's plotted.
            if (Number.isFinite(obj.raDeg) && Number.isFinite(obj.decDeg)) {
                try {
                    const raHours = obj.raDeg / 15;
                    const data = await this.apiGet(
                        `/api/sky/altitude?ra=${raHours}&dec=${obj.decDeg}&stepMinutes=15`);
                    if (data && Array.isArray(data.samples) && data.samples.length > 0
                        && this.skyInfo.title === obj.name) {
                        this.skyInfo.altitudeSamples = data.samples;
                        const events = this._skyInfoComputeEvents(data.samples);
                        this.skyInfo.transitText = events.transitText;
                        this.skyInfo.setText = events.setText;
                        // Render after Alpine commits the DOM, the
                        // canvas only exists once skyInfo.altitudeSamples
                        // flips x-show on.
                        this.$nextTick(() => this._renderSkyInfoChart());
                    }
                } catch (e) { /* leave chart hidden if altitude lookup fails */ }
            }
        },

        // From an altitude track [{utc, altitudeDeg}], find the time of
        // the peak (≈ meridian transit) and the first time after now
        // when the object drops below the horizon. Returns short labels
        // ready to drop into the card.
        _skyInfoComputeEvents(samples) {
            const now = Date.now();
            let peakIdx = 0;
            for (let i = 1; i < samples.length; i++) {
                if (samples[i].altitudeDeg > samples[peakIdx].altitudeDeg) peakIdx = i;
            }
            const peakUtc = new Date(samples[peakIdx].utc).getTime();
            // Horizon set: first sample after now where altitude goes
            // ≤ 0. If the object never sets within tonight's window,
            // walk back to the last positive-altitude sample and label
            // it as "stays up".
            let setUtc = null;
            for (let i = 0; i < samples.length; i++) {
                const t = new Date(samples[i].utc).getTime();
                if (t < now) continue;
                if (samples[i].altitudeDeg <= 0) { setUtc = t; break; }
            }
            return {
                transitText: this._skyInfoFormatDelta(peakUtc, now,
                    peakUtc < now ? 'past' : 'until'),
                setText: setUtc != null
                    ? this._skyInfoFormatDelta(setUtc, now, 'until')
                    : (samples[samples.length - 1].altitudeDeg > 0
                        ? 'stays up tonight' : 'below horizon now'),
            };
        },

        _skyInfoFormatDelta(when, now, dir) {
            const deltaMin = Math.round((when - now) / 60000);
            const abs = Math.abs(deltaMin);
            const h = Math.floor(abs / 60), m = abs % 60;
            const hm = h > 0 ? `${h}h ${m}m` : `${m}m`;
            return dir === 'past' ? `${hm} ago` : `in ${hm}`;
        },

        _renderSkyInfoChart() {
            const cv = this.$refs.skyInfoChart;
            if (!cv || !this.skyInfo.altitudeSamples) return;
            if (this._skyInfoChart) {
                try { this._skyInfoChart.destroy(); } catch (_) { }
                this._skyInfoChart = null;
            }
            const t = (typeof getNightTheme === 'function')
                ? getNightTheme() : { tick: '#9ca3af', grid: 'rgba(255,255,255,0.08)' };
            const samples = this.skyInfo.altitudeSamples;
            const labels = samples.map(s =>
                new Date(s.utc).toLocaleTimeString('en-GB',
                    { hour: '2-digit', minute: '2-digit' }));
            const data = samples.map(s => s.altitudeDeg);
            this._skyInfoChart = new Chart(cv.getContext('2d'), {
                type: 'line',
                data: {
                    labels,
                    datasets: [{
                        data, borderColor: '#64b5f6',
                        backgroundColor: 'rgba(100,181,246,0.18)',
                        fill: true, tension: 0.3, pointRadius: 0,
                        borderWidth: 1.5,
                    }]
                },
                options: {
                    responsive: true, maintainAspectRatio: false, animation: false,
                    plugins: { legend: { display: false } },
                    scales: {
                        x: { ticks: { color: t.tick, font: { size: 9 },
                                      maxTicksLimit: 6 },
                             grid: { color: t.grid } },
                        y: { min: -10, max: 90,
                             ticks: { color: t.tick, font: { size: 9 } },
                             grid: { color: t.grid } }
                    }
                }
            });
        },

        // Card action: smoothly pan the SKY map view to the object
        // without touching the mount. Mirrors the auto-center-on-
        // select pan, but explicit so the user can still trigger it
        // when Auto-center on select is off. The mount-side Slew
        // buttons continue to target whatever's framed in the red
        // target FOV, i.e. the live map centre, which is now this
        // object after Center is clicked.
        async skyInfoCenterMap() {
            if (!Number.isFinite(this.skyInfo.raDeg)
                || !Number.isFinite(this.skyInfo.decDeg)) return;
            this._skyLookAt(this.skyInfo.raDeg / 15, this.skyInfo.decDeg,
                undefined, this.skyInfo.title);
        },

        // Card action: route Slew & Center via the existing path.
        async skyInfoSlewCenter() {
            // _currentSlewTarget already falls back to skyTarget which
            // _populateSkyInfo populated. Trigger the full workflow.
            return this.slewAndCenter();
        },

        // Card action: Slew Only, same target resolution as
        // skyInfoSlewCenter but skips the plate-solve centering loop.
        // Mirrors the standalone "Slew Only" button below the map.
        async skyInfoSlewOnly() {
            return this.slewToCurrent();
        },

        skyInfoAddToSequence() {
            if (this.skyTarget) this.addToSequence();
        },

        selectSkyTarget(obj) {
            this.skyTarget = obj;
            this.skyShowResults = false;
            // Atlas-filter result list shares the same dismissal
            // pattern as the free-text search dropdown: once the
            // user picks one, drop the list and collapse the filter
            // panel, otherwise it sits open over the map forever
            // with no obvious close affordance.
            this.atlasResults = [];
            this.showAtlasFilters = false;
            this._goToSelectedTarget();
            // SWE-4: also tell the stellarium-web-engine iframe to
            // re-aim its camera at the picked target so the visible
            // map matches the planning UI selection. Uses pointAndLock
            // when an object name is recognised by the engine, falling
            // back to direct yaw/pitch from coords otherwise.
            if (obj && (obj.ra != null) && (obj.dec != null)) {
                // Project the catalog search hit onto the same
                // rich-object shape the bridge's map-click handler
                // emits, then route through _populateSkyInfo so the
                // translucent left-side card opens with the object's
                // name, magnitude, type, RA/Dec, and (async) the
                // Tonight's Best thumbnail when our offline catalog
                // knows it. _populateSkyInfo also does the smooth
                // _skyLookAt(... pointAndLock) animation, so we don't
                // need a separate look-at call here.
                const types = obj.type ? [obj.type] : null;
                const subtitle = obj.commonName && obj.commonName !== obj.name
                    ? obj.commonName : null;
                this._populateSkyInfo({
                    name: obj.name,
                    subtitle: subtitle,
                    raDeg: obj.ra * 15,
                    decDeg: obj.dec,
                    magnitude: typeof obj.magnitude === 'number' ? obj.magnitude : null,
                    types: types
                });
            }
        },

        // ASIAIR-style "Slew & Center to whatever's framed in the
        // red target FOV right now". Reads the map centre via the
        // d3-celestial projection rotation, the same source the
        // on-map target rectangle uses, so what the user sees framed
        // is what the mount tries to put under the camera.
        // Falls back to a picked skyTarget if the map centre can't
        // be read for any reason.
        async slewAndCenter() {
            // SWE-5: prefer the live engine centre (= where the red
            // target rectangle is right now). The bridge's change-hook
            // updates skyTarget on every observer.yaw/pitch mutation,
            // but on the very first call after page load it may not
            // have fired yet, querying the engine directly closes
            // that gap and gives an exact "go to what's centred"
            // semantics regardless of skyTarget freshness.
            let target = null;
            try {
                const c = await this._skyGetCenter();
                if (c && Number.isFinite(c.raDeg) && Number.isFinite(c.decDeg)) {
                    target = { ra: c.raDeg / 15, dec: c.decDeg };
                }
            } catch { /* fall through to skyTarget */ }
            if (!target) target = this._currentSlewTarget();
            if (!target) {
                this.toast('Sky map not ready', 'error');
                return;
            }
            try {
                const resp = await this.apiPost('/api/sky/slew-and-center', {
                    ra: target.ra,
                    dec: target.dec,
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

        // Slew Only handler, same target source as slewAndCenter
        // (live engine centre = where the red rectangle is) but
        // skips the plate-solve loop.
        async slewToCurrent() {
            let target = null;
            try {
                const c = await this._skyGetCenter();
                if (c && Number.isFinite(c.raDeg) && Number.isFinite(c.decDeg)) {
                    target = { ra: c.raDeg / 15, dec: c.decDeg };
                }
            } catch { /* fall through to skyTarget */ }
            if (!target) target = this._currentSlewTarget();
            if (!target) { this.toast('Sky map not ready', 'error'); return; }
            return this.slewTo(target.ra, target.dec);
        },

        _currentSlewTarget() {
            // SWE-6: d3-celestial removed; the live map centre is
            // now reachable only async via _skyGetCenter(). Sync
            // callers fall back to the last picked skyTarget.
            const t = this.skyTarget;
            if (t && Number.isFinite(t.ra) && Number.isFinite(t.dec)) {
                return { ra: t.ra, dec: t.dec };
            }
            return null;
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

        // Unified panic-stop: kills whatever mount motion is in
        // flight. Cancels an active Slew & Center job (which itself
        // now also aborts the mount), AND posts /telescope/abort
        // unconditionally so a raw Slew Only / Go to that doesn't
        // have a slewCenterJobId also halts immediately.
        async stopAnySlew() {
            if (this.slewCenterJobId) {
                try { await this.cancelSlewCenter(); }
                catch (e) { /* fall through to telescope abort below */ }
            }
            try { await this.apiPost('/api/telescope/abort'); }
            catch (e) { this.toast('Mount abort failed: ' + (e.message || ''), 'error'); }
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
            if (!this.skyTarget) {
                this.toast('Pick a target on the map first', 'warn');
                return;
            }
            const item = {
                name: this.skyTarget.name,
                exposure: this.exposure,
                gain: this.gain,
                binning: parseInt(this.binning),
                count: 10,
                filter: null,
                ra: parseFloat(this.skyTarget.ra) || null,
                dec: parseFloat(this.skyTarget.dec) || null,
                // Mark items added from Sky Explorer so the card UI
                // knows it has a catalog name worth resolving against
                // the celestial-image service (NASA / Wikipedia
                // thumbnail). Manual "+ Add" items default to false
                // and only attempt the lookup if the user later types
                // a name that looks catalogish.
                fromSky: true,
                thumbUrl: null
            };
            this.sequence.push(item);
            this._loadCelestialThumb(item);
            this.syncSequenceToServer();
            // Toast so the user gets confirmation without switching
            // tabs to verify the add landed. Includes the new
            // sequence total so they know how many items are queued.
            const n = this.sequence.length;
            this.toast(
                `Added "${item.name}" to sequence (${n} item${n === 1 ? '' : 's'})`,
                'ok');
        },

        // Resolve a sequence item's name to a Wikipedia / NASA
        // thumbnail via the existing CelestialImageService that
        // already powers the Tonight tab. Result cached on the item
        // itself (item.thumbUrl) so Alpine re-renders pick it up
        // and we never re-hit the API for the same item. Silent
        // no-op when the catalog has no match — the card just
        // renders without a thumb.
        //
        // URL priority:
        //   1) localUrl  — server already downloaded the JPEG and
        //                  proxies it from /api/sky/image/file/{slug}.
        //                  Same-origin, no CORS / mixed-content
        //                  pitfalls; needs ?token= via authUrl()
        //                  because the path is under /api/* (gated).
        //   2) thumbnailUrl — direct CDN URL from Wikipedia / NASA.
        //                     Fallback when the disk cache miss left
        //                     localFileExt empty (offline server,
        //                     etc). Cross-origin but img tags don't
        //                     need auth headers for upstream loads.
        //   3) fullUrl    — full-size variant; last resort, can be
        //                   several MB so we try the thumbs first.
        async _loadCelestialThumb(item) {
            if (!item || !item.name) return;
            try {
                const r = await this.apiGet(
                    '/api/sky/image?name=' + encodeURIComponent(item.name));
                if (!r || !r.available) return;
                if (r.localUrl) {
                    item.thumbUrl = this.authUrl(r.localUrl);
                } else if (r.thumbnailUrl) {
                    item.thumbUrl = r.thumbnailUrl;
                } else if (r.fullUrl) {
                    item.thumbUrl = r.fullUrl;
                }
            } catch (e) {
                // No match / offline — render without a thumb. Logged
                // at debug level so devtools can show why a name the
                // user expected didn't get a picture.
                console.debug('[Polaris] celestial thumb lookup failed for',
                    item.name, e);
            }
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
                imageType: 'LIGHT',
                // FLAT auto-exposure toggle (engine reads it when
                // imageType === 'FLAT'). Defaults off so LIGHT/DARK/
                // BIAS items don't accidentally pick up the flag.
                autoExposure: false
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
                if (data.items) {
                    this.sequence = data.items;
                    // Rehydrate celestial thumbnails for catalog targets
                    // — the server doesn't persist thumbUrl (it's a
                    // pure UI cache), so on reload we re-resolve any
                    // item that was originally added from Sky. Server
                    // caches the API response on disk for 30 days so
                    // this is a cheap re-fetch.
                    for (const item of this.sequence) {
                        if (item && item.fromSky && item.name && !item.thumbUrl) {
                            this._loadCelestialThumb(item);
                        }
                    }
                }
                this.seqState = data.state || 'idle';
            } catch (e) { }
        },

        async startSequence() {
            try {
                await this.apiPost('/api/sequence', this.sequence);
                await this.apiPost('/api/sequence/start');
                this.seqState = 'running';
                // SHUT-4: kick the shutter tick so the autorun ring
                // animates smoothly as frames complete.
                this._startShutterTick();
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
        // transient operations show up, steady-state things like
        // "INDI connected" or "mount tracking" live in the header
        // and aren't duplicated here.
        activityChips() {
            const out = [];

            // Pi under-voltage. Highest priority -- this is the
            // smoking gun for "my USB devices randomly disconnect"
            // and the user really wants to see it surfaced. Active
            // under-voltage = RED (USB crashes imminent). Past-only
            // under-voltage (cleared but flagged since boot) =
            // AMBER advice ("consider a powered hub"). The chip
            // links to the troubleshooting doc anchor on click.
            if (this.host.underVoltageNow) {
                out.push({
                    id: 'uv-now', icon: '⚡', kind: 'error',
                    label: 'Under-voltage NOW — expect USB crashes',
                    href: 'docs/user-guide/troubleshooting.md#usb-device-crashes-mid-operation-under-voltage'
                });
            } else if (this.host.underVoltageOccurred) {
                out.push({
                    id: 'uv-past', icon: '⚡', kind: 'warn',
                    label: 'Pi was under-voltage since boot — use a powered USB hub',
                    href: 'docs/user-guide/troubleshooting.md#usb-device-crashes-mid-operation-under-voltage'
                });
            }

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

            // Meridian flip, any non-idle stage
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

            // PHD2 transient. Steady-state guiding is NOT shown,
            // that's the normal background hum during a sequence.
            // Only the eventful transitions matter for the chip row.
            if (this.guider.calibrating) {
                out.push({ id: 'phd2-cal', icon: '🌟', kind: 'warn', label: 'PHD2 calibrating' });
            } else if (this.guider.settling) {
                out.push({ id: 'phd2-set', icon: '🌟', kind: 'info', label: 'PHD2 settling' });
            }

            // Live stacking — only show the activity chip when frames
            // have actually been accumulated. liveStackEnabled defaults
            // true at boot (matches the always-on stacker behaviour),
            // which would otherwise render a "Live stack 0f" chip
            // immediately on page load even though the user hasn't
            // taken any frames yet. Tying visibility to frame count
            // means the chip only surfaces when there's real activity
            // to report.
            if (this.liveStackEnabled && (this.liveStackFrames || 0) > 0) {
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

            // REFSUG-2: trend-based refocus suggestion. Server fires
            // this when LSTR-3 auto-fire is OFF and HFR is trending
            // bad. Clicking the chip jumps to FOCUS Manual Assist
            // where the user can refocus by hand, then the LIVE-tab
            // callout (REFSUG-3) lets them mark it resolved.
            const rs = this.liveStackStatus?.refocusSuggestion;
            if (rs && rs.suggesting) {
                out.push({
                    id: 'refocus-suggest',
                    icon: '🎯',
                    kind: 'warn',
                    label: 'Refocus: ' + (rs.reason || 'HFR drifting'),
                    tooltip: (rs.reason || 'HFR drifting')
                        + '\nClick to open FOCUS → Manual Assist.',
                    onClick: () => {
                        this.tab = 'focus';
                        this.focusTab = 'assist';
                    }
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

            // Siril active jobs (one chip per job, usually 1)
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

        // Red > 85%, amber 60-85%, green < 60%, same threshold for
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
        // Disk free / total. Colour by % USED of the capture volume so
        // the gauge flips amber / red before a sequence runs into an
        // ENOSPC. host.diskFreeGB / diskTotalGB come from the backend
        // HostMetricsService (DriveInfo on the active rig's
        // ImageOutputDir, walked to the longest matching mount).
        hostDiskClass() {
            const total = this.host.diskTotalGB || 0;
            const free = this.host.diskFreeGB || 0;
            if (total <= 0) return '';
            const usedPct = 100 * (1 - free / total);
            return usedPct > 90 ? 'host-red'
                 : usedPct > 75 ? 'host-amber'
                 : 'host-green';
        },
        hostDiskTooltip() {
            const total = this.host.diskTotalGB || 0;
            const free = this.host.diskFreeGB || 0;
            const mount = this.host.diskMountName || '';
            const root = this.settings?.imageOutputDir || '(not set)';
            if (total <= 0) return 'Disk usage probe failed (no rig / unmounted path).';
            const usedPct = (100 * (1 - free / total)).toFixed(1);
            return 'Capture root: ' + root
                + '\nMount: ' + (mount || '(unknown)')
                + '\nFree: ' + free.toFixed(1) + ' GB of ' + total.toFixed(1) + ' GB'
                + '\nUsed: ' + usedPct + '%';
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
            // what the user sees on screen, defensive against the
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

        // ── WIFI-4: NetworkManagerService client ────────────────────
        // Settings → Network panel + Switch / Edit modals dispatch
        // to /api/network/*. The 'Switch to station' path is tricky
        // because the HTTP socket itself rides on the WiFi link that
        // is about to drop, so we have to tolerate the response never
        // arriving (browser sees ECONNRESET, then the home network
        // route kicks in once the user reconnects their laptop).

        networkModeLabel() {
            if (!this.network) return 'Unknown';
            switch (this.network.mode) {
                case 'hotspot':       return '🟢 Hotspot';
                case 'station':       return '🟢 Station';
                case 'disconnected':  return '⚠ Disconnected';
                case 'unsupported':   return '— Unsupported';
                default:              return '— Unknown';
            }
        },

        networkExpectedReachUrl() {
            // Best-effort hint for the user when they need to reconnect
            // to their home WiFi. The hostname stays the same (mDNS), but
            // the IP changes. Fall back to the current hostname if we
            // cannot guess anything better.
            const host = (window.location && window.location.hostname) || 'polaris-pi.local';
            return `https://${host}:${window.location.port || 5000}/`;
        },

        networkStationCanSubmit() {
            if (this.networkSwitching) return false;
            const ssid = this.networkStation.ssid === '__hidden__'
                ? (this.networkStation.hiddenSsid || '').trim()
                : (this.networkStation.ssid || '').trim();
            const pwd = this.networkStation.password || '';
            return ssid.length > 0 && ssid.length <= 32 && pwd.length >= 8 && pwd.length <= 63;
        },

        async networkOpenSwitchDialog() {
            this.networkStation.open = true;
            this.networkStation.ssid = '';
            this.networkStation.hiddenSsid = '';
            this.networkStation.password = '';
            this.networkStation.lastError = '';
            await this.networkRescan();
        },

        networkOpenHotspotDialog() {
            this.networkHotspot.open = true;
            this.networkHotspot.ssid = (this.network && this.network.hotspotSsid) || 'Polaris-Hotspot';
            this.networkHotspot.password = '';
            this.networkHotspot.lastError = '';
        },

        async networkRescan() {
            this.networkStation.scanning = true;
            this.networkStation.lastError = '';
            try {
                const results = await this.apiGet('/api/network/scan');
                this.networkStation.scanResults = Array.isArray(results) ? results : [];
            } catch (e) {
                this.networkStation.lastError = 'Scan failed: ' + (e.message || e);
                this.networkStation.scanResults = [];
            } finally {
                this.networkStation.scanning = false;
            }
        },

        async networkSwitchToStation() {
            if (!this.networkStationCanSubmit()) return;
            const ssid = this.networkStation.ssid === '__hidden__'
                ? this.networkStation.hiddenSsid.trim()
                : this.networkStation.ssid.trim();
            const password = this.networkStation.password;
            this.networkSwitching = true;
            this.networkStation.lastError = '';
            try {
                const resp = await this.apiPost('/api/network/station', { ssid, password });
                const r = await resp.json();
                if (r && r.ok) {
                    this.toast(
                        `Connected to ${ssid}. The Pi is now at https://polaris-pi.local${window.location.port ? ':' + window.location.port : ''}/ on your home network. Reconnect this device to ${ssid}.`,
                        'ok');
                    this.networkStation.open = false;
                } else {
                    this.networkStation.lastError = (r && r.error) || 'Switch failed';
                    this.toast(this.networkStation.lastError, 'warn');
                }
            } catch (e) {
                // The most common failure mode here is the socket itself
                // disappearing because the WiFi link dropped between
                // "POST sent" and "response read". We surface a helpful
                // recovery message but do NOT auto-assume success, the
                // try-and-revert path on the server might already have
                // reverted to the hotspot.
                this.networkStation.lastError =
                    'Lost contact with the Pi. If your laptop / phone is connected ' +
                    'via the hotspot, reconnect it to ' + ssid + ' and reopen ' +
                    this.networkExpectedReachUrl() + '. If the new network failed, ' +
                    'reconnect to Polaris-Hotspot (auto-reverts after 30 s).';
            } finally {
                this.networkSwitching = false;
            }
        },

        async networkSwitchToHotspot() {
            this.networkSwitching = true;
            try {
                const resp = await this.apiPost('/api/network/hotspot');
                const r = await resp.json();
                if (r && r.ok) {
                    this.toast('Hotspot restored. Reconnect to ' +
                        ((this.network && this.network.hotspotSsid) || 'Polaris-Hotspot') +
                        ' to keep using Polaris.', 'ok');
                } else {
                    this.toast((r && r.error) || 'Hotspot switch failed', 'warn');
                }
            } catch (e) {
                // Same reasoning as networkSwitchToStation, we may lose
                // the link mid-call when the station drops.
                this.toast('Lost contact with the Pi during hotspot switch. ' +
                    'Reconnect to the hotspot WiFi to continue.', 'warn');
            } finally {
                this.networkSwitching = false;
            }
        },

        async networkSaveHotspotCredentials() {
            const ssid = (this.networkHotspot.ssid || '').trim();
            const password = this.networkHotspot.password || '';
            if (!ssid || password.length < 8 || password.length > 63) return;
            this.networkSwitching = true;
            this.networkHotspot.lastError = '';
            try {
                // apiPost with method override (PUT). The repo's HTTP
                // helper layer is POST/GET only by convention so PUTs
                // come through apiPost's opts overload.
                const resp = await this.apiPost('/api/network/hotspot/credentials',
                    null, {
                        method: 'PUT',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ ssid, password })
                    });
                const r = await resp.json();
                if (r && r.ok) {
                    this.toast(
                        `Hotspot now: ${ssid}. Reconnect with the new credentials.`,
                        'ok');
                    this.networkHotspot.open = false;
                } else {
                    this.networkHotspot.lastError = (r && r.error) || 'Save failed';
                }
            } catch (e) {
                // Same socket-drop tolerance as the station switch.
                this.networkHotspot.lastError =
                    'Lost contact with the Pi while restarting the hotspot. ' +
                    'Reconnect to ' + ssid + ' with the new password.';
            } finally {
                this.networkSwitching = false;
            }
        },

        // ── INDI-WEB-3: indi-web (indiwebmanager) lifecycle ─────
        // Fetches /api/indi/web/status, fires lifecycle commands,
        // surfaces install hint + iframe URL. Called by the RIGS
        // panel's <details> @toggle and the inline buttons.

        async indiWebStatusRefresh() {
            try {
                this.indiWeb.status = await this.apiGet('/api/indi/web/status');
            } catch (e) {
                this.indiWeb.status = {
                    supportedOs: false, installed: false, running: false,
                    unsupportedReason: 'Status endpoint unreachable',
                };
            }
        },

        async indiWebStart() {
            if (this.indiWeb.busy) return;
            this.indiWeb.busy = true;
            try {
                const r = await this.apiPost('/api/indi/web/start');
                if (r?.running) {
                    this.toast('indi-web started', 'ok');
                } else {
                    this.toast('Start failed: ' + (r?.error || 'unknown'), 'error');
                }
            } catch (e) {
                this.toast('Start failed: ' + (e.message || e), 'error');
            } finally {
                this.indiWeb.busy = false;
                await this.indiWebStatusRefresh();
            }
        },

        async indiWebStop() {
            if (this.indiWeb.busy) return;
            this.indiWeb.busy = true;
            try {
                await this.apiPost('/api/indi/web/stop');
                this.toast('indi-web stopped', 'ok');
            } catch (e) {
                this.toast('Stop failed: ' + (e.message || e), 'error');
            } finally {
                this.indiWeb.busy = false;
                await this.indiWebStatusRefresh();
            }
        },

        // Status pill: maps the status snapshot to a green / amber /
        // red label so the <details> summary surfaces the state
        // without having to open it.
        indiWebStatusClass() {
            const s = this.indiWeb.status;
            if (!s) return 'status-muted';
            if (!s.supportedOs) return 'status-muted';
            if (!s.installed) return 'status-warn';
            if (s.running) return 'status-ok';
            return 'status-muted';
        },
        indiWebStatusLabel() {
            const s = this.indiWeb.status;
            if (!s) return 'Status: …';
            if (!s.supportedOs) return 'OS not supported';
            if (!s.installed) return 'Not installed';
            if (s.running) return '● Running';
            return 'Stopped';
        },
        // INDI Control Panel sub-tab handlers. Mirror the indi-web
        // shape (status refresh + lifecycle action + label helpers)
        // so the two sub-tabs feel consistent to the user.
        async indiCpStatusRefresh() {
            try {
                this.indiCp.status = await this.apiGet('/api/indi/cp/status');
            } catch (e) {
                this.indiCp.status = {
                    supportedOs: false, xpraInstalled: false,
                    indiControlPanelInstalled: false, sessionRunning: false,
                    unsupportedReason: 'Status endpoint unreachable',
                };
            }
        },

        async indiCpLaunch() {
            if (this.indiCp.busy) return;
            this.indiCp.busy = true;
            try {
                const r = await this.apiPost('/api/indi/cp/launch');
                if (r?.launched) {
                    this.toast('INDI Control Panel launched', 'ok');
                    // Cache-bust the iframe src so the HTML5 xpra client
                    // re-attaches and picks up the newly-created window.
                    // Without this the iframe (already loaded with the
                    // PHD2 window in focus) might not surface the new
                    // window without a manual refresh.
                    this.indiCp.iframeSrc = '/phd2-gui/?t=' + Date.now();
                    this.indiCp.launched = true;
                } else {
                    this.toast('Launch failed: ' + (r?.error || 'unknown'), 'error');
                }
            } catch (e) {
                this.toast('Launch failed: ' + (e.message || e), 'error');
            } finally {
                this.indiCp.busy = false;
                await this.indiCpStatusRefresh();
            }
        },

        indiCpStatusClass() {
            const s = this.indiCp.status;
            if (!s) return 'status-muted';
            if (!s.supportedOs) return 'status-muted';
            if (!s.xpraInstalled) return 'status-warn';
            if (!s.indiControlPanelInstalled) return 'status-warn';
            if (s.sessionRunning) return 'status-ok';
            return 'status-muted';
        },
        indiCpStatusLabel() {
            const s = this.indiCp.status;
            if (!s) return 'Status: ...';
            if (!s.supportedOs) return 'OS not supported';
            if (!s.xpraInstalled) return 'xpra missing';
            if (!s.indiControlPanelInstalled) return 'indi-bin missing';
            if (s.sessionRunning) return '● xpra session up';
            return 'Session not started';
        },

        indiWebSummaryLabel() {
            const s = this.indiWeb.status;
            if (!s) return '(click to load)';
            if (!s.supportedOs) return '(unavailable on this OS)';
            if (!s.installed) return '(install with pip)';
            if (s.running) return '✓ running · ' + (s.version || 'v?');
            return 'stopped';
        },

        // INDIPROP-2: native INDI property browser.
        // ---------------------------------------------------
        // Loads the full device → group → property snapshot from
        // /api/indi/properties, replacing existing devices in-place
        // so per-element _draft inputs survive the re-render (when
        // the user is mid-edit on element X we don't want to overwrite
        // their unsaved value with the freshly-fetched one).
        async indiPropsLoad() {
            if (this.indiProps.busy) return;
            this.indiProps.busy = true;
            try {
                const r = await this.apiGet('/api/indi/properties/');
                this.indiProps.devices = this._indiPropsMerge(
                    this.indiProps.devices, r?.devices || []);
                this.indiProps.lastError = '';
            } catch (e) {
                this.indiProps.lastError = 'load failed: ' + (e.message || e);
            } finally {
                this.indiProps.busy = false;
                this.indiPropsScheduleRefresh();
            }
        },

        // Force a full server-side device-cache wipe + re-issue
        // INDI getProperties. Use after loading/unloading drivers in
        // indi-web -- the TCP connection survives the indi-web
        // reload, so the standard Refresh just re-reads our stale
        // cache. Resync rebuilds the cache from scratch.
        async indiPropsResync() {
            if (this.indiProps.busy) return;
            this.indiProps.busy = true;
            try {
                // Drop the local snapshot immediately so the UI shows
                // the re-discovery in progress instead of the old set.
                this.indiProps.devices = [];
                await this.apiPost('/api/indi/properties/refresh');
                this.toast('INDI device list resynced', 'ok');
                // Brief wait for the new defXxxVector flood to settle
                // before we GET the rebuilt snapshot.
                setTimeout(() => this.indiPropsLoad(), 200);
            } catch (e) {
                this.indiProps.lastError = 'resync failed: ' + (e.message || e);
                this.toast('Resync failed: ' + (e?.message || e), 'error');
            } finally {
                this.indiProps.busy = false;
            }
        },

        // Schedule the next auto-refresh tick, but only when:
        //   - autoRefresh is on, AND
        //   - the RIGS tab is open AND the indi-cp sub-tab is the
        //     one currently selected (no point polling while the
        //     panel isn't visible — saves the Pi a tiny bit of CPU
        //     + saves the user from running into the WS payload
        //     races caused by overlapping fetches).
        // 2 s cadence matches the typical INDI device update rate
        // without flooding mobile devices.
        indiPropsScheduleRefresh() {
            if (this._indiPropsTimer) {
                clearTimeout(this._indiPropsTimer);
                this._indiPropsTimer = null;
            }
            if (!this.indiProps.autoRefresh) return;
            if (this.tab !== 'equip' || this.equipTab !== 'indi-cp') return;
            this._indiPropsTimer = setTimeout(() => this.indiPropsLoad(), 2000);
        },

        // In-place merge so element-level _draft fields (the unsaved
        // value the user has typed into a number/text input) survive
        // a refresh. Only the *display* value comes from the server
        // -- the _draft is initialised once via x-init and then owned
        // by the input until the user clicks Set.
        _indiPropsMerge(prev, next) {
            const prevByDevice = {};
            for (const d of (prev || [])) prevByDevice[d.name] = d;
            for (const d of next) {
                const prevDev = prevByDevice[d.name];
                if (!prevDev) continue;
                const prevPropByName = {};
                for (const p of (prevDev.properties || [])) prevPropByName[p.name] = p;
                for (const p of (d.properties || [])) {
                    const prevProp = prevPropByName[p.name];
                    if (!prevProp || !prevProp.elements) continue;
                    const prevElByName = {};
                    for (const el of prevProp.elements) prevElByName[el.name] = el;
                    for (const el of (p.elements || [])) {
                        const prevEl = prevElByName[el.name];
                        if (prevEl && prevEl._draft !== undefined) {
                            el._draft = prevEl._draft;
                        }
                    }
                }
            }
            return next;
        },

        // Optional filters applied client-side. Always cheap because
        // the snapshot is small (< 1 MB even for a fully-loaded rig).
        indiPropsFilteredDevices() {
            const sel = this.indiProps.selectedDevice;
            const needle = (this.indiProps.search || '').trim().toLowerCase();
            let out = this.indiProps.devices;
            if (sel) out = out.filter(d => d.name === sel);
            if (needle) {
                out = out.map(d => ({
                    ...d,
                    properties: (d.properties || []).filter(p =>
                        (p.name || '').toLowerCase().includes(needle) ||
                        (p.label || '').toLowerCase().includes(needle) ||
                        (p.group || '').toLowerCase().includes(needle) ||
                        (p.elements || []).some(el =>
                            (el.name || '').toLowerCase().includes(needle) ||
                            (el.label || '').toLowerCase().includes(needle))
                    )
                })).filter(d => d.properties.length > 0);
            }
            return out;
        },

        indiPropsGroupKeys(dev) {
            // Stable, alphabetical group order so users can build muscle
            // memory ("Main is always first, then Site, then Options...").
            const groups = new Set();
            for (const p of (dev.properties || [])) groups.add(p.group || 'Main');
            return Array.from(groups).sort();
        },

        indiPropsByGroup(dev, group) {
            return (dev.properties || []).filter(p =>
                (p.group || 'Main') === group);
        },

        indiPropsToggleGroup(key) {
            // Mutate the object so Alpine's reactivity picks it up
            // (assigning to a key on an existing object via direct
            // property write requires a re-create to trigger).
            this.indiProps.collapsedGroups = {
                ...this.indiProps.collapsedGroups,
                [key]: !this.indiProps.collapsedGroups[key]
            };
        },

        // --- Setters per type. All three POST to the same endpoint
        //     with a type-discriminated body so the server can
        //     dispatch to IndiClient.SetNumberAsync / SetSwitchAsync
        //     / SetTextAsync without us shipping the INDI XML protocol
        //     to the browser.

        async indiPropsSetNumber(device, prop, el) {
            try {
                await this.apiFetch('/api/indi/properties/set', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        device, property: prop.name, type: 'number',
                        numbers: { [el.name]: Number(el._draft) }
                    })
                });
                this.indiProps.lastError = '';
                this.toast(`${prop.label || prop.name}.${el.name} = ${el._draft}`, 'ok');
                this.indiPropsLoad();
            } catch (e) {
                this.indiProps.lastError = 'set failed: ' + (e.message || e);
            }
        },

        // Switch: rule decides whether we mutate one element (OneOfMany /
        // AtMostOne, send only the toggled one ON) or N elements
        // (AnyOfMany: send just the toggled element's new state, INDI
        // will leave the rest alone). For OneOfMany we explicitly clear
        // sibling switches so the indiserver state matches the radio.
        async indiPropsSetSwitch(device, prop, el, ev) {
            const checked = ev?.target?.checked;
            const sw = {};
            if (prop.rule === 'oneOfMany' || prop.rule === 'atMostOne') {
                for (const sib of prop.elements) sw[sib.name] = (sib.name === el.name);
            } else {
                sw[el.name] = !!checked;
            }
            try {
                await this.apiFetch('/api/indi/properties/set', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        device, property: prop.name, type: 'switch', switches: sw
                    })
                });
                this.indiProps.lastError = '';
                this.indiPropsLoad();
            } catch (e) {
                this.indiProps.lastError = 'set failed: ' + (e.message || e);
            }
        },

        async indiPropsSetText(device, prop, el) {
            try {
                await this.apiFetch('/api/indi/properties/set', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        device, property: prop.name, type: 'text',
                        texts: { [el.name]: String(el._draft ?? '') }
                    })
                });
                this.indiProps.lastError = '';
                this.toast(`${prop.label || prop.name}.${el.name} updated`, 'ok');
                this.indiPropsLoad();
            } catch (e) {
                this.indiProps.lastError = 'set failed: ' + (e.message || e);
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
            // by the time @change fires, persist that.
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

        // PHD2 header badge: text + class + tooltip computed from the
        // live guider state (same source the GUIDE-tab Control panel
        // reads). Done as plain methods because the previous inline
        // x-show cascade rendered empty in some Alpine eval orders.
        phd2BadgeText() {
            const g = this.guider || {};
            if (!g.connected) return 'PHD2 OFF';
            if (g.guiding) {
                const rms = (g.rmsTotal != null) ? g.rmsTotal.toFixed(2) : '--';
                return `PHD2 GUIDING (${rms}")`;
            }
            const st = g.appState || '';
            if (!st || st === 'Stopped') return 'PHD2 ON';
            return 'PHD2 ' + st.toUpperCase();
        },
        phd2BadgeClass() {
            const g = this.guider || {};
            if (!g.connected) return 'off';
            if (g.guiding) return 'ok';
            if (g.appState === 'LostLock') return 'error';
            return 'warn';
        },
        phd2BadgeTitle() {
            const g = this.guider || {};
            if (!g.connected) return 'PHD2 not connected, click for Guider';
            if (g.guiding) {
                const rms = (g.rmsTotal != null) ? g.rmsTotal.toFixed(2) : '--';
                return `Guiding, RMS ${rms}", click for Guider`;
            }
            return `PHD2 ${g.appState || 'connected'}, click for Guider`;
        },
        hostDeviceTooltip() {
            const d = this.host && this.host.device;
            if (!d) return '';
            // model + OS + (arch + cores) + optional CPU brand line.
            // CPU is null on hosts where /proc/cpuinfo or WMI failed
            //, only render the line when we actually have it.
            let s = d.model + '\n' + d.os + '\n' + d.architecture + ' · ' + d.cores + ' cores';
            if (d.cpu) s += '\n' + d.cpu;
            return s;
        },
        formatHostRam(usedMB, totalMB) {
            if (!totalMB || totalMB <= 0) return ', /,';
            // Render in GB once we cross 1 GB total; below that
            // (containers / cgroup limited), stay in MB for accuracy.
            if (totalMB >= 1024) {
                return `${(usedMB / 1024).toFixed(1)} / ${(totalMB / 1024).toFixed(1)} GB`;
            }
            return `${usedMB} / ${totalMB} MB`;
        },

        // ─── NET-1: network throughput meter ────────────────────────────
        // Hooks that the WS handlers / wrapped ws.send / apiFetch / raw
        // editor fetches call to accumulate bytes. A 250ms timer
        // (_netStartMeter) drains the deltas into a rolling 3s sample
        // buffer and writes the displayed rxRate / txRate.

        _netRx(bytes) {
            if (!bytes || bytes <= 0) return;
            this._netDeltaRx += bytes;
            this.net.rxTotal += bytes;
            // Pulse, drop after 120ms so a steady stream looks like a
            // gentle flicker (each chunk re-arms it) and a one-off
            // request looks like a single blink.
            if (!this.net.rxPulse) {
                this.net.rxPulse = true;
                clearTimeout(this._netRxPulseT);
                this._netRxPulseT = setTimeout(() => { this.net.rxPulse = false; }, 120);
            }
        },
        _netTx(bytes) {
            if (!bytes || bytes <= 0) return;
            this._netDeltaTx += bytes;
            this.net.txTotal += bytes;
            if (!this.net.txPulse) {
                this.net.txPulse = true;
                clearTimeout(this._netTxPulseT);
                this._netTxPulseT = setTimeout(() => { this.net.txPulse = false; }, 120);
            }
        },

        // Drain Performance Resource Timing for any HTTP responses
        // since the last call. transferSize includes the wire bytes
        // (compression + headers) so it's a tighter proxy for "what
        // the link saw" than the body length. Clears the buffer to
        // keep it bounded (browser default cap ~150 entries).
        _netDrainResourceTimings() {
            if (typeof performance === 'undefined' || !performance.getEntriesByType) return;
            const entries = performance.getEntriesByType('resource');
            if (!entries.length) return;
            let sum = 0;
            for (const e of entries) {
                // transferSize === 0 means cached or CORS-opaque
                // (we're same-origin so the latter doesn't apply).
                // encodedBodySize as fallback when transferSize is
                // missing (older Firefox).
                const n = e.transferSize || e.encodedBodySize || 0;
                sum += n;
            }
            if (sum > 0) this._netRx(sum);
            // Drop the buffer so the next call only sees fresh entries.
            try { performance.clearResourceTimings(); } catch { /* unsupported */ }
        },

        _netMeterTick() {
            // Pick up any HTTP traffic the wrappers can't see directly
            // (image-tag / link-tag / iframe sub-resources, blob URL
            // fetches the browser issued internally, etc.).
            this._netDrainResourceTimings();

            const now = Date.now();
            this._netSamples.push({
                t: now,
                rx: this._netDeltaRx,
                tx: this._netDeltaTx
            });
            this._netDeltaRx = 0;
            this._netDeltaTx = 0;

            // Drop samples older than 3s.
            const cutoff = now - 3000;
            while (this._netSamples.length && this._netSamples[0].t < cutoff) {
                this._netSamples.shift();
            }

            // Sum window + compute rate. Window length is whatever's
            // covered by current samples (max 3s), gives meaningful
            // numbers on first ticks before the buffer fills.
            let rx = 0, tx = 0;
            for (const s of this._netSamples) { rx += s.rx; tx += s.tx; }
            const spanMs = this._netSamples.length
                ? Math.max(250, now - this._netSamples[0].t)
                : 1000;
            this.net.rxRate = rx * 1000 / spanMs;
            this.net.txRate = tx * 1000 / spanMs;
        },

        _netStartMeter() {
            if (this._netMeterTimer) return;
            this._netMeterTimer = setInterval(() => this._netMeterTick(), 250);
        },

        /**
         * Wrap every native <input type="range"> in a touch-friendly
         * [-] slider [+] control row. Buttons step by the slider's
         * own `step` attribute (default 1) and dispatch synthetic
         * `input` + `change` events so Alpine x-model + plain
         * @input handlers see the new value the same way they do
         * when the user drags.
         *
         * Idempotent: a sentinel data-flag on the input skips already
         * wrapped sliders. Inline `style="flex:1"` (used by sliders
         * inside flex labels for the Appearance card etc.) is moved
         * onto the wrapper instead so the row, not just the track,
         * stretches.
         */
        /**
         * Mount iOS / ASIAIR-style vertical drum pickers. Auto-scans
         * the DOM for `[data-wheel-picker]` divs and attaches a
         * WheelPicker behind each one. Two-way binding to an Alpine
         * state field is opt-in via `data-bind="path.to.field"` (the
         * picker reads the field via dot-walk, writes back on
         * change). Optional `data-scale="1000"` multiplies the
         * stored value before display (e.g. seconds → ms).
         *
         * Markup:
         *   <div data-wheel-picker
         *        data-label="Exposure (ms)"
         *        data-min="1" data-max="5000" data-step="1"
         *        data-bind="video.exposure" data-scale="1000"></div>
         *
         * Idempotent via the data-wheel-mounted sentinel.
         */
        _mountWheelPickers() {
            const els = document.querySelectorAll(
                '[data-wheel-picker]:not([data-wheel-mounted])');
            for (const el of els) {
                el.dataset.wheelMounted = '1';
                const opts = {
                    min: parseFloat(el.dataset.min || '0'),
                    max: parseFloat(el.dataset.max || '100'),
                    step: parseFloat(el.dataset.step || '1'),
                    label: el.dataset.label || '',
                    scale: parseFloat(el.dataset.scale || '1'),
                    bind: el.dataset.bind || '',
                };
                const picker = new WheelPicker(el, opts);
                if (opts.bind) {
                    // Initial value from the bound Alpine field.
                    const initial = this._wheelGetBound(opts.bind);
                    if (Number.isFinite(initial)) {
                        picker.setValue(initial * opts.scale, /*silent*/ true);
                    }
                    // Two-way: when the user changes the wheel, write
                    // back to Alpine. When Alpine updates the field
                    // elsewhere (e.g. preset button), sync the wheel.
                    picker.onChange = (v) => {
                        this._wheelSetBound(opts.bind, v / opts.scale);
                    };
                    this.$watch(opts.bind, (v) => {
                        if (Number.isFinite(v) && v * opts.scale !== picker.value) {
                            picker.setValue(v * opts.scale, /*silent*/ true);
                        }
                    });
                }
            }
        },
        _wheelGetBound(path) {
            const parts = path.split('.');
            let o = this;
            for (const p of parts) {
                if (o == null) return null;
                o = o[p];
            }
            return o;
        },
        _wheelSetBound(path, value) {
            const parts = path.split('.');
            const last = parts.pop();
            let o = this;
            for (const p of parts) {
                if (o[p] == null) return;
                o = o[p];
            }
            o[last] = value;
        },

        _augmentRangeInputs() {
            const sliders = document.querySelectorAll(
                'input[type="range"]:not([data-range-augmented])');
            for (const input of sliders) {
                // Mark first so a re-entrant MutationObserver firing
                // from the DOM moves below does not loop.
                input.dataset.rangeAugmented = '1';

                const wrap = document.createElement('span');
                wrap.className = 'range-with-controls';
                // Hand any inline flex sizing the slider had down to
                // the wrapper. Otherwise the slider's flex:1 stops
                // applying once the wrapper sits between it and the
                // surrounding flex container.
                if (input.style.flex) {
                    wrap.style.flex = input.style.flex;
                    input.style.flex = '';
                }
                if (input.style.width) {
                    wrap.style.width = input.style.width;
                    input.style.width = '';
                }

                const mkBtn = (label, dir) => {
                    const b = document.createElement('button');
                    b.type = 'button';
                    b.className = 'range-step-btn';
                    b.textContent = label;
                    b.setAttribute('aria-label',
                        dir < 0 ? 'Decrease' : 'Increase');
                    b.tabIndex = -1;
                    // pointerdown so it works for both mouse + touch
                    // without firing twice on touch devices that also
                    // emit synthetic click events.
                    b.addEventListener('pointerdown', (ev) => {
                        ev.preventDefault();
                        if (input.disabled) return;
                        const step = parseFloat(input.step) || 1;
                        const min = input.min !== '' ? parseFloat(input.min) : -Infinity;
                        const max = input.max !== '' ? parseFloat(input.max) : Infinity;
                        let cur = parseFloat(input.value);
                        if (!Number.isFinite(cur)) cur = 0;
                        let next = cur + dir * step;
                        // Round to step grid to avoid 0.30000000000004
                        // float drift when stepping fractional values.
                        const decimals = (input.step.split('.')[1] || '').length;
                        if (decimals > 0) {
                            next = parseFloat(next.toFixed(decimals));
                        }
                        next = Math.min(max, Math.max(min, next));
                        input.value = String(next);
                        input.dispatchEvent(new Event('input', { bubbles: true }));
                        input.dispatchEvent(new Event('change', { bubbles: true }));
                    });
                    return b;
                };

                const minus = mkBtn('−', -1);
                const plus = mkBtn('+', +1);

                const parent = input.parentNode;
                parent.insertBefore(wrap, input);
                wrap.appendChild(minus);
                wrap.appendChild(input);
                wrap.appendChild(plus);
            }
        },

        // Auto-scale to B/s · KB/s · MB/s with one decimal. Caller
        // gets a fixed-width-ish string suitable for a tabular-num
        // span without jumping width too much across the breakpoints.
        formatBytesPerSec(bps) {
            if (!Number.isFinite(bps) || bps <= 0) return '0 B/s';
            if (bps < 1024) return Math.round(bps) + ' B/s';
            if (bps < 1024 * 1024) return (bps / 1024).toFixed(1) + ' KB/s';
            return (bps / (1024 * 1024)).toFixed(1) + ' MB/s';
        },

        // Tooltip, cumulative session totals + the current window.
        netTooltip() {
            const fmtTotal = (b) => {
                if (b < 1024) return b + ' B';
                if (b < 1024 * 1024) return (b / 1024).toFixed(1) + ' KB';
                if (b < 1024 * 1024 * 1024) return (b / 1024 / 1024).toFixed(1) + ' MB';
                return (b / 1024 / 1024 / 1024).toFixed(2) + ' GB';
            };
            return 'Session totals\n'
                 + '  ↓ ' + fmtTotal(this.net.rxTotal) + ' received\n'
                 + '  ↑ ' + fmtTotal(this.net.txTotal) + ' sent';
        },

        // Wrap ws.send so it goes through _netTx. Returns the bytes
        // sent so callers that want the count get it back; otherwise
        // they can ignore.
        _wsSendTracked(ws, payload) {
            if (!ws || ws.readyState !== WebSocket.OPEN) return 0;
            let bytes = 0;
            if (typeof payload === 'string') {
                // UTF-8 length approximation. Most of our control
                // frames are pure ASCII JSON so this is exact; even
                // with multi-byte chars the rough bound is fine for a
                // throughput gauge.
                bytes = payload.length;
            } else if (payload instanceof ArrayBuffer) {
                bytes = payload.byteLength;
            } else if (payload && payload.byteLength != null) {
                // TypedArray / Blob-ish
                bytes = payload.byteLength;
            } else if (payload && payload.size != null) {
                // Blob
                bytes = payload.size;
            }
            ws.send(payload);
            this._netTx(bytes);
            return bytes;
        },

        // --- Status WebSocket handler ---

        handleStatusMessage(msg) {
            if (msg.type !== 'status') return;

            // DBGLOG-6: absorb the debug log sub-object before anything
            // else. Cheap, side-effect-only, and the badge needs to
            // refresh before the rest of the UI sees the tick.
            if (msg.debugLog && Array.isArray(msg.debugLog.entries)) {
                for (const e of msg.debugLog.entries) this._absorbLogEntry(e);
                if (msg.debugLog.truncated) this.logs.truncated = true;
            }

            const eq = msg.equipment || {};

            if (eq.indi) {
                // Detect the false→true transition (typical on page
                // refresh while INDI was already connected server-side)
                // and auto-hydrate the device list. Without this the
                // RIGS tab showed "INDI (0 devices)" + every device
                // dropdown was empty until the user manually clicked
                // Refresh, even though Camera/Mount/etc were already
                // connected and reporting data via the WS.
                const wasIndiDisconnected = !this.indiConnected;
                this.indiConnected = eq.indi.connected;
                if (wasIndiDisconnected && this.indiConnected) {
                    this.refreshDevices();
                }
            }
            if (eq.camera) {
                this.cameraTemp = eq.camera.temperature;
                // ONLY mirror the camera name as "selectedCamera" when
                // the backend reports it as actually CONNECTED. The
                // EquipmentManager keeps Camera!=null after the user
                // disconnects (the device stays selected; only the
                // physical link is closed), without this guard, every
                // status tick after a disconnect re-asserts
                // selectedCamera = name and the UI toggle flips back
                // ON within ~1 s, making the camera look like it
                // refuses to disconnect.
                this.selectedCamera = eq.camera.connected ? eq.camera.name : null;
                // Mirror the connected device into the RIGS-tab dropdown
                // so it shows the actual selection instead of "Select
                // device" on page-refresh-while-connected. Only set
                // when empty so we don't clobber a user mid-pick.
                if (!this.equipCameraChoice && eq.camera.name) {
                    this.equipCameraChoice = eq.camera.name;
                }
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
                        // Refresh the sky overlay so the rectangle resizes
                        // immediately when a camera connects with a sensor
                        // size different from the profile fallback.
                        if (this.tab === 'sky'
                            && typeof this.updateSkyCameraFov === 'function') {
                            this.updateSkyCameraFov();
                        }
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
                const prevRa = this.mount.ra, prevDec = this.mount.dec;
                Object.assign(this.mount, {
                    ra: eq.telescope.ra, dec: eq.telescope.dec,
                    alt: eq.telescope.alt, az: eq.telescope.az,
                    tracking: eq.telescope.tracking, slewing: eq.telescope.slewing,
                    parked: eq.telescope.parked, pierSide: eq.telescope.pierSide,
                    connected: eq.telescope.connected,
                    // MOUNT: capabilities sub-object gates per-button UI
                    // (Park, Find Home, pier side). Fall back to current
                    // value when the payload doesn't carry it (e.g.
                    // alternative backend that hasn't been updated).
                    capabilities: eq.telescope.capabilities || this.mount.capabilities
                });
                // Same "disconnect-doesn't-stick" guard as the camera
                // above, the EquipmentManager keeps Telescope!=null
                // after Disconnect, so the WS keeps echoing the name.
                // Only mirror it to selectedTelescope when the
                // backend says it's actually connected.
                this.selectedTelescope = eq.telescope.connected
                    ? eq.telescope.name : null;
                if (!this.equipMountChoice && eq.telescope.name) {
                    this.equipMountChoice = eq.telescope.name;
                }
                // Blue mount-anchored FOV overlay needs to track the
                // new RA/Dec on every status push so the user sees the
                // scope walk across the sky during a slew. The
                // !skyTarget gate from the d3-celestial era is gone,
                // with the new screen-anchored red target, skyTarget
                // is always set (it tracks the engine centre), so that
                // gate would prevent ALL updates. The bridge has its
                // own per-push dedup (3-decimal RA/Dec hash) that
                // skips pointless redraws when the mount is parked
                // and ra/dec actually didn't change, so we don't need
                // to debounce here.
                if (this.tab === 'sky'
                    && this.aladinShowFov
                    && (prevRa !== this.mount.ra || prevDec !== this.mount.dec)
                    && typeof this.updateSkyCameraFov === 'function') {
                    this.updateSkyCameraFov();
                }
            }
            if (eq.focuser) {
                this.focusPosition = eq.focuser.position;
                this.focusTemp = eq.focuser.temperature;
                this.focusMoving = eq.focuser.moving;
                if (typeof eq.focuser.maxPosition === 'number'
                    && eq.focuser.maxPosition > 0) {
                    this.focusMaxPosition = eq.focuser.maxPosition;
                }
                // Sync the slider's pending value to the current
                // position unless the user is actively dragging
                // (focusSliderDirty). Without this guard the 1 Hz
                // WS push snaps the slider back to the live position
                // mid-drag and you can't actually move it.
                if (!this.focusSliderDirty) {
                    this.focusSliderTarget = eq.focuser.position;
                }
                // Honour the backend's connected flag instead of
                // assuming "the focuser is in the payload, so it's
                // connected", same disconnect-doesn't-stick bug the
                // camera/telescope had. Treat missing 'connected'
                // (older server build) as true for backward compat.
                const focuserOnline = eq.focuser.connected !== false;
                this.focusConnected = focuserOnline;
                this.selectedFocuser = focuserOnline ? eq.focuser.name : null;
                if (!this.equipFocuserChoice && eq.focuser.name) {
                    this.equipFocuserChoice = eq.focuser.name;
                }
                // Capabilities (FOCUSER-SPEC v2). Older server builds
                // without the sub-object → leave defaults (all false),
                // so users on a mismatched build don't get controls
                // that would 501.
                if (eq.focuser.capabilities) {
                    this.focuserCaps = {
                        sync:        !!eq.focuser.capabilities.sync,
                        reverse:     !!eq.focuser.capabilities.reverse,
                        backlash:    !!eq.focuser.capabilities.backlash,
                        temperature: !!eq.focuser.capabilities.temperature
                    };
                }
            }
            if (eq.filterWheel) {
                const fwOnline = eq.filterWheel.connected !== false;
                this.filterWheel = {
                    connected: fwOnline,
                    position: eq.filterWheel.position,
                    currentFilter: eq.filterWheel.currentFilter,
                    filters: eq.filterWheel.filters || [],
                    moving: eq.filterWheel.moving,
                    // FILTERWHEEL-SPEC v2: editNames flag drives
                    // whether the "Edit names" affordance renders.
                    // Backward compat: missing capabilities object on
                    // older server builds → keep editor hidden.
                    capabilities: {
                        editNames: !!eq.filterWheel.capabilities?.editNames
                    }
                };
                this.selectedFilterWheel = fwOnline ? eq.filterWheel.name : null;
                if (!this.equipFilterChoice && eq.filterWheel.name) {
                    this.equipFilterChoice = eq.filterWheel.name;
                }
            }
            // Belt-and-suspenders: also sync on every WS tick so if a
            // race between this handler and refreshDevices() left a
            // dropdown empty (selected* set but devices list still
            // pending), the next tick after refreshDevices resolves
            // hydrates it. _syncEquipChoicesFromConnected is a no-op
            // when either devices is empty or the choice is already
            // set, so calling it 1Hz is cheap + idempotent.
            if (this.indiConnected) this._syncEquipChoicesFromConnected();
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
                    // get by clicking Connect, profile list, exposure,
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
                        // PH2X-9 sub-objects, UI binds chips + state to these.
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
                // PH2VNC: same shape as guiSession; UI branches by OS
                // on these two snapshots inside the GUIDE tab.
                if (g.vncSession)   this.phd2VncSession = g.vncSession;
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
            // CLOCK-3: server pushes utcNow on every tick. Compare
            // to Date.now() to compute wall-clock skew the user can
            // act on. Skew is positive when the server is AHEAD of
            // the client, negative when BEHIND.
            if (msg.server && msg.server.utcNow) {
                this.clockSync.serverUtc = msg.server.utcNow;
                this.clockSync.supported = !!msg.server.clockSyncSupported;
                const serverMs = Date.parse(msg.server.utcNow);
                if (Number.isFinite(serverMs)) {
                    this.clockSync.skewSeconds = Math.round(
                        (serverMs - Date.now()) / 1000);
                }
            }
            // SIM-6: simulator backend status (kind/installed/version/
            // running/runningDevices). The Settings panel binds to this.
            if (msg.simulator) this.simulator = msg.simulator;
            // WIFI-4: host WiFi snapshot. Skip overwriting while a
            // switch is in flight, the 5s server refresh can briefly
            // race the mid-switch transition and flicker the UI back
            // to the pre-switch state, the actual SwitchToStation
            // response is the source of truth in that window.
            if (msg.network && !this.networkSwitching) this.network = msg.network;
            if (msg.cameraStream) {
                // Preserve last-known values so the button label stays
                // readable while the stream service initialises.
                this.cameraStream = Object.assign({}, this.cameraStream, msg.cameraStream);
            }
            if (msg.videoRecording) this.videoRecording = msg.videoRecording;
            if (msg.videoStack !== undefined) this.videoStack = msg.videoStack;  // null when idle
            if (msg.slewPreview) this.slewPreview = msg.slewPreview;
            // FW-1: Flat Wizard tick. state + lastError always present;
            // progress is null when the wizard never ran (preserved as
            // null so the UI hides the progress block). When a run
            // completes, progress.filterResults stays populated so the
            // user can read the final per-filter outcome.
            if (msg.flatWizard) {
                this.flatWizard.state = msg.flatWizard.state || 'idle';
                this.flatWizard.lastError = msg.flatWizard.lastError || null;
                if (msg.flatWizard.progress !== undefined) {
                    this.flatWizard.progress = msg.flatWizard.progress;
                }
                // When a run just transitioned idle → running, kick the
                // shutter tick so the ring renders smoothly.
                if (this.flatWizard.state === 'running') this._startShutterTick();
                // When a run finishes, refresh trained exposures so the
                // table picks up the new converged values without a
                // second tab-open.
                if (this.flatWizard.state === 'idle'
                    && msg.flatWizard.progress?.filterResults?.length > 0
                    && this.autorunTab === 'flat') {
                    this.apiGet('/api/flatwizard/trained').then(t => {
                        if (t) this.flatWizard.trained = t;
                    }).catch(() => {});
                }
            }
            if (msg.sirilJobs) this.sirilActiveJobs = msg.sirilJobs;
            if (msg.graXpertJobs) this.graXpertActiveJobs = msg.graXpertJobs;
            // Server-pushed toasts. Server keeps a ring buffer; we
            // monotonically track the highest id we've already shown
            // so reconnects + status ticks don't re-fire stale toasts.
            // PA-4: polar alignment job snapshot. Server sends null
            // when no job has run yet, so the form fields keep their
            // hydrated values. completedOk + isActive derived locally
            // from phase to keep template bindings simple.
            if (msg.polarAlignment !== undefined) {
                const pa = msg.polarAlignment;
                if (pa) {
                    const prevTotal = this.polar.totalErrorArcsec;
                    this.polar.phase = pa.phase || 'Idle';
                    this.polar.isActive = !!pa.isActive;
                    this.polar.completedOk = pa.phase === 'Ok';
                    this.polar.points = pa.points || [];
                    this.polar.azErrorArcsec = pa.azErrorArcsec || 0;
                    this.polar.altErrorArcsec = pa.altErrorArcsec || 0;
                    this.polar.totalErrorArcsec = pa.totalErrorArcsec || 0;
                    this.polar.lastError = pa.lastError || null;
                    // PA-5: any time the error vector moves, repaint
                    // the overlay so the arrow tracks fresh values
                    // during Refine. Cheap (single clear + redraw
                    // already done at 1Hz for star annotations).
                    if (this.polar.totalErrorArcsec !== prevTotal) {
                        this.$nextTick(() => this.redrawOverlay());
                    }
                }
            }
            // Skip the very first payload after a WS connect, those
            // are notifications that happened BEFORE the user opened
            // the browser, and replaying 20 stale toasts on page load
            // is more annoying than useful. The id is still recorded
            // so subsequent fresh notifications fire normally.
            if (Array.isArray(msg.notifications) && msg.notifications.length) {
                if (this._notificationsLastId === undefined) {
                    this._notificationsLastId = msg.notifications[msg.notifications.length - 1].id;
                } else {
                    for (const n of msg.notifications) {
                        if (n.id > this._notificationsLastId) {
                            this.toast(n.text, n.kind || 'info', n.ttlMs || 4000);
                            this._notificationsLastId = n.id;
                        }
                    }
                }
            }

            // Refresh charts once per status frame (1Hz), only if their canvas
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
                const r = await this.apiFetch('/api/sequencer/document/json', {
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
        async discoverAlpaca(opts = {}) {
            // `silent: true` suppresses the no-results warn + the
            // per-device error toasts. Used by the boot-time auto-
            // discovery (_initCore → autoDiscoverAlpaca) where an
            // empty result is the normal case on machines without
            // an ASCOM Remote Server running — toasting a warning
            // there would be noise. Manual button click stays loud
            // so the user gets feedback for what they explicitly
            // asked for.
            const silent = opts.silent === true;
            this.alpaca.discovering = true;
            try {
                // Backend handles discovery + auto-connect of every device
                // in one round-trip. autoConnect=true is the default, set
                // false only if a future "browse without pairing" mode is
                // needed. Response carries autoConnected[] so we can
                // toast per-device success/failure if anything went sideways.
                const r = await this.apiGet('/api/alpaca/discover?timeoutMs=3000&autoConnect=true');
                this.alpaca.servers = (r.servers || []).map(s => ({
                    host: s.host, port: s.port,
                    serverName: s.serverName, manufacturer: s.manufacturer,
                    manufacturerVersion: s.manufacturerVersion,
                    devices: s.devices || null, _probe: null
                }));
                this.alpaca.totalDevices = r.totalDevices || 0;
                this.alpaca.autoConnected = r.autoConnected || [];

                if (this.alpaca.servers.length === 0) {
                    if (!silent) {
                        this.toast('No Alpaca servers found. Try manual entry or check that ASCOM Remote Server is running.', 'warn');
                    }
                } else {
                    const ok = this.alpaca.autoConnected.filter(d => d.ok).length;
                    const fail = this.alpaca.autoConnected.length - ok;
                    if (fail === 0) {
                        // Always toast on success — even auto-discovery,
                        // since "your equipment just came online" is
                        // info worth surfacing.
                        this.toast(`${ok} Alpaca device(s) connected. Pick them in the cards under "Alpaca" driver.`, 'ok');
                    } else {
                        this.toast(`${ok} connected, ${fail} failed. See RIGS tab for details.`, 'warn');
                        if (!silent) {
                            for (const d of this.alpaca.autoConnected.filter(x => !x.ok)) {
                                this.toast(`${d.deviceType}: ${d.error || 'connect failed'}`, 'error');
                            }
                        }
                    }
                }

                // Refresh every per-card driver dropdown so the new "Alpaca"
                // entries appear without the user having to reload the tab.
                try {
                    this.cameraDrivers = await this.apiGet('/api/camera/drivers');
                    this.mountDrivers = await this.apiGet('/api/telescope/drivers');
                    this.focuserDrivers = await this.apiGet('/api/focuser/drivers');
                    this.filterWheelDrivers = await this.apiGet('/api/filterwheel/drivers');
                } catch (_) { /* best-effort; the next tab open will repopulate */ }
            } catch (e) {
                if (!silent) {
                    this.toast('Alpaca discovery failed: ' + e.message, 'error');
                } else {
                    console.warn('[Polaris] auto Alpaca discovery failed:', e.message);
                }
            } finally {
                this.alpaca.discovering = false;
            }
        },

        // Boot-time auto-discovery. Fires from _initCore so the user
        // doesn't need to open the RIGS tab + click Discover to bring
        // an already-running Alpaca server online. Silent mode: no
        // warn toast when there's nothing on the LAN (that's the
        // normal case for users on pure-INDI setups), but a success
        // toast still surfaces when devices DO connect so they know
        // their rig is online. Deferred ~1.5 s so the WS handshake
        // + auth complete first.
        autoDiscoverAlpaca() {
            if (this._alpacaAutoTried) return;
            this._alpacaAutoTried = true;
            setTimeout(() => {
                this.discoverAlpaca({ silent: true })
                    .catch(e => console.warn('[Polaris] auto Alpaca discovery rejected:', e));
            }, 1500);
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
            // Validate host before firing the request. Catches the
            // classic typo "1270.0.0.1" (extra zero, octet > 255)
            // that DNS rejects + the server can only surface as a
            // 500 with a SocketException message. Better to fail
            // here with an obvious toast that points at the field.
            const host = (this.alpaca.manualHost || '').trim();
            if (!this._isValidHostOrIp(host)) {
                this.toast('Invalid host: "' + host + '" is not a valid IP address or hostname.', 'error');
                return;
            }
            const port = parseInt(this.alpaca.manualPort, 10);
            if (!Number.isInteger(port) || port < 1 || port > 65535) {
                this.toast('Invalid port: must be 1–65535.', 'error');
                return;
            }
            // Add or update the manual server in the list, then enumerate
            let srv = this.alpaca.servers.find(s => s.host === host && s.port === port);
            if (!srv) {
                srv = { host, port, serverName: 'Manual entry', devices: null };
                this.alpaca.servers.push(srv);
            }
            await this.alpacaQueryServer(srv);
        },

        // Accepts dotted-quad IPv4 (each octet 0-255), bracketed or
        // bare IPv6, or RFC-1123 hostname (letters / digits / hyphens
        // / dots, no leading or trailing hyphen per label). Conservative
        // on purpose: the goal is to block "1270.0.0.1" / "127.0.0.0.1"
        // / "host name with spaces" before the request even leaves
        // the browser, not to reproduce the full RFC.
        _isValidHostOrIp(s) {
            if (!s || s.length > 253) return false;
            // IPv4
            const v4 = s.match(/^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/);
            if (v4) {
                for (let i = 1; i <= 4; i++) {
                    const n = parseInt(v4[i], 10);
                    if (n < 0 || n > 255) return false;
                }
                return true;
            }
            // IPv6 (very loose — just hex groups + colons + optional ::)
            if (/^\[?[0-9a-fA-F:]+\]?$/.test(s) && s.includes(':')) return true;
            // Hostname (RFC-1123ish)
            return /^([a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)*[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$/.test(s);
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
            // SWE-6: push mosaic tiles to the stellarium-web bridge
            // (yellow polygons) as part of the FOV overlay payload.
            // _pushSkyFovOverlays reads this.mosaicTiles and forwards.
            if (!plan?.panels) { this.mosaicTiles = null;
                this._pushSkyFovOverlays(); return; }
            this.mosaicTiles = plan.panels.map(p => ({
                raDeg: p.raHours * 15, decDeg: p.decDeg,
                widthDeg: plan.panelFovWidthDeg,
                heightDeg: plan.panelFovHeightDeg,
                rotationDeg: 0
            }));
            this._pushSkyFovOverlays();
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

/**
 * Vertical drum / wheel picker. Renders an iOS-date-picker-style
 * scrolling column inside the host element. Pointer drag (touch
 * + mouse), mouse wheel, and click-on-adjacent-item all change
 * the value. Snaps to step on release. Virtualised: only
 * renders a small window of items around the current value, so
 * a 5000-step range stays cheap.
 *
 * Constructor opts:
 *   min, max, step:  numeric range + grid (defaults 0/100/1)
 *   label:           caption painted at the top
 *
 * Public API:
 *   setValue(v, silent)  - move the wheel to `v` (clamped + snapped).
 *                          When silent=true, does not fire onChange.
 *   onChange             - callback (v) → void, fired on user-driven
 *                          value changes. Reassignable.
 */
class WheelPicker {
    static ITEM_HEIGHT = 28;
    static VISIBLE_COUNT = 5;          // 2 above + center + 2 below

    constructor(el, opts) {
        this.el = el;
        this.min = opts.min;
        this.max = opts.max;
        this.step = opts.step || 1;
        this.label = opts.label || '';
        this.value = this._clamp(this.min);
        this.onChange = null;

        this.el.classList.add('wheel-picker');

        // Label
        if (this.label) {
            const lab = document.createElement('div');
            lab.className = 'wheel-picker-label';
            lab.textContent = this.label;
            this.el.appendChild(lab);
        }

        // Fades
        const ftop = document.createElement('div');
        ftop.className = 'wheel-picker-fade wheel-picker-fade-top';
        const fbot = document.createElement('div');
        fbot.className = 'wheel-picker-fade wheel-picker-fade-bottom';
        this.el.appendChild(ftop);
        this.el.appendChild(fbot);

        // Item list container
        this.list = document.createElement('div');
        this.list.className = 'wheel-picker-list';
        this.el.appendChild(this.list);

        this._dragging = false;
        this._dragStartY = 0;
        this._dragStartValue = 0;
        this._pointerId = null;
        // Cached center-of-container Y so we can render items at
        // the correct vertical offset for the current value.
        this._centerY = this.el.clientHeight / 2;

        this.el.addEventListener('pointerdown', this._onDown.bind(this));
        this.el.addEventListener('pointermove', this._onMove.bind(this));
        this.el.addEventListener('pointerup',   this._onUp.bind(this));
        this.el.addEventListener('pointercancel', this._onUp.bind(this));
        this.el.addEventListener('wheel', this._onWheel.bind(this), { passive: false });

        this._render();
    }

    _clamp(v) {
        if (v < this.min) return this.min;
        if (v > this.max) return this.max;
        return v;
    }
    _snap(v) {
        const k = Math.round((v - this.min) / this.step);
        return this._clamp(this.min + k * this.step);
    }
    _decimals() {
        const s = String(this.step);
        const i = s.indexOf('.');
        return i < 0 ? 0 : (s.length - i - 1);
    }
    _format(v) {
        return v.toFixed(this._decimals());
    }

    setValue(v, silent) {
        const snapped = this._snap(v);
        if (snapped === this.value) return;
        this.value = snapped;
        this._render();
        if (!silent && this.onChange) this.onChange(this.value);
    }

    _render() {
        // Render a virtualised window of (VISIBLE_COUNT + 4) items
        // centered on this.value. The list itself is translated so
        // the centre item sits at the centre band.
        const dec = this._decimals();
        const stepsAroundCenter = Math.floor(WheelPicker.VISIBLE_COUNT / 2) + 2;
        const items = [];
        for (let i = -stepsAroundCenter; i <= stepsAroundCenter; i++) {
            const v = this.value + i * this.step;
            if (v < this.min - this.step || v > this.max + this.step) {
                items.push({ value: null, blank: true });
            } else if (v < this.min || v > this.max) {
                items.push({ value: null, blank: true });
            } else {
                items.push({ value: v, blank: false });
            }
        }
        // Build / reuse children
        while (this.list.children.length < items.length) {
            const d = document.createElement('div');
            d.className = 'wheel-picker-item';
            this.list.appendChild(d);
        }
        while (this.list.children.length > items.length) {
            this.list.removeChild(this.list.lastChild);
        }
        for (let i = 0; i < items.length; i++) {
            const child = this.list.children[i];
            const offset = i - stepsAroundCenter;          // -N..+N
            const absOffset = Math.abs(offset);
            child.textContent = items[i].blank
                ? ''
                : items[i].value.toFixed(dec);
            child.classList.toggle('wheel-picker-item--center', offset === 0);
            child.classList.toggle('wheel-picker-item--adjacent', absOffset === 1);
        }
        // Position the list so the center item sits at the center band
        const containerH = this.el.clientHeight || 168;
        const topOfFirst = (containerH / 2) - WheelPicker.ITEM_HEIGHT / 2
                           - stepsAroundCenter * WheelPicker.ITEM_HEIGHT;
        this.list.style.transform = `translateY(${topOfFirst}px)`;
    }

    _onDown(ev) {
        if (this.el.dataset.disabled === 'true') return;
        ev.preventDefault();
        this._dragging = true;
        this._dragStartY = ev.clientY;
        this._dragStartValue = this.value;
        this._pointerId = ev.pointerId;
        this.list.classList.remove('snapping');
        try { this.el.setPointerCapture(ev.pointerId); } catch {}
    }
    _onMove(ev) {
        if (!this._dragging || ev.pointerId !== this._pointerId) return;
        const deltaY = ev.clientY - this._dragStartY;
        // Dragging DOWN should move to LOWER values (numbers scroll
        // up on the wheel as your finger pulls down). One ITEM_HEIGHT
        // of finger movement = one step.
        const stepsDelta = -deltaY / WheelPicker.ITEM_HEIGHT;
        const target = this._dragStartValue + stepsDelta * this.step;
        const snapped = this._snap(target);
        if (snapped !== this.value) {
            this.value = snapped;
            this._render();
            if (this.onChange) this.onChange(this.value);
        }
    }
    _onUp(ev) {
        if (!this._dragging) return;
        this._dragging = false;
        this.list.classList.add('snapping');
        try { this.el.releasePointerCapture(this._pointerId); } catch {}
        this._pointerId = null;
        // Final render to re-anchor (in case the drag moved the list
        // off-grid pixels). The CSS .snapping transition smooths it.
        this._render();
        // Fire a DOM event so consumers that want a single "commit"
        // hook on drag-end (e.g. fire-and-forget focuser move) can
        // attach @wheel-pick-end without having to debounce the
        // per-step onChange firehose.
        this.el.dispatchEvent(new CustomEvent('wheel-pick-end', {
            bubbles: true, detail: { value: this.value }
        }));
    }
    _onWheel(ev) {
        if (this.el.dataset.disabled === 'true') return;
        ev.preventDefault();
        const direction = ev.deltaY > 0 ? 1 : -1;
        this.setValue(this.value + direction * this.step);
    }
}
window.WheelPicker = WheelPicker;
