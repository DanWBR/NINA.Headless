# N.I.N.A. Polaris

**Cross-platform headless astronomy controller for Raspberry Pi, ARM64 SBCs, and Windows mini PCs.**

> ⚠️ **N.I.N.A. Polaris is a community-driven fork of [N.I.N.A.](https://nighttime-imaging.eu/)** It is **not** affiliated with or supported by the official N.I.N.A. development team. Please **don't** ask them for support with this fork, open issues here instead.

N.I.N.A. Polaris is a lightweight, browser-controlled astrophotography system built on ASP.NET Core. It brings the power of [N.I.N.A.](https://nighttime-imaging.eu/) (Nighttime Imaging 'N' Astronomy) to single-board computers and small-form-factor PCs, with a responsive Web UI accessible from any device on the network.

The Raspberry Pi (or Windows mini PC) acts as a data broker, controlling hardware, saving FITS files, and streaming images, while your laptop, tablet, or phone handles all the heavy rendering in the browser.

```
Browser (laptop / tablet / phone)        Raspberry Pi / Mini PC
┌──────────────────────────────┐         ┌──────────────────────────┐
│  Web UI (Alpine.js)          │◄──HTTP─►│  ASP.NET Core + Kestrel  │
│  Live preview (Canvas/WebGL) │◄──WS───►│  INDI client (TCP 7624)  │
│  Image processing (client)   │         │  Plate solving (ASTAP)   │
│  Sky explorer                │         │  Sequence engine         │
└──────────────────────────────┘         │  Live stacking           │
                                         └──────────────────────────┘
```

## Contents

- [Features](#features)
  - [Equipment Control via INDI](#equipment-control-via-indi)
  - [DSLR / Mirrorless cameras](#dslr--mirrorless-cameras)
  - [Real-Time Image Streaming](#real-time-image-streaming)
  - [Live Stacking (EAA)](#live-stacking-eaa)
  - [Plate Solving & Centering](#plate-solving--centering)
  - [Guiding (PHD2), full management](#guiding-phd2--full-management)
  - [Manual Focus Assist (no motor required)](#manual-focus-assist-no-motor-required)
  - [Auto-Focus (V-Curve)](#auto-focus-v-curve)
  - [Meridian Flip Automation](#meridian-flip-automation)
  - [Advanced Sequencer (tree-based)](#advanced-sequencer-tree-based)
  - [Mosaic Planner](#mosaic-planner)
  - [Plugin System](#plugin-system)
  - [Dithering](#dithering)
  - [Sky Catalog & Sky Atlas](#sky-catalog--sky-atlas)
  - [Sky Map](#sky-map)
  - [Weather Forecast](#weather-forecast)
  - [Tonight's Best](#tonights-best)
  - [Studio (post-processing)](#studio-post-processing)
  - [External tools (Siril + GraXpert)](#external-tools-siril--graxpert)
  - [File explorer](#file-explorer)
  - [Sequence Engine + Image Persistence](#sequence-engine--image-persistence)
  - [Flat Wizard](#flat-wizard)
  - [Web UI](#web-ui)
  - [Equipment Rigs (multi-rig support)](#equipment-rigs-multi-rig-support)
  - [Telescope + Accessory Catalogue](#telescope--accessory-catalogue)
  - [Profile Management](#profile-management)
  - [Remote Access (Relay Server)](#remote-access-relay-server)
  - [Network Resilience](#network-resilience)
  - [Discovery & Cross-Platform Drivers](#discovery--cross-platform-drivers)
  - [Remote Terminal (SSH from the browser)](#remote-terminal-ssh-from-the-browser)
  - [Polar Alignment (TPPA)](#polar-alignment-tppa)
- [Architecture](#architecture)
  - [Technology Stack](#technology-stack)
- [Getting Started](#getting-started)
- [Deployment](#deployment)
- [API Reference](#api-reference)
- [Configuration](#configuration)
- [Performance Targets](#performance-targets)
- [Support the project](#support-the-project)
- [Contributing](#contributing)
- [License](#license)

> **Looking for the full tooling matrix?** See [REQUIREMENTS.md](REQUIREMENTS.md)
> for the complete required + optional dependency list per platform
> (Windows / Linux ARM-RPi / Linux x64), with firewall rules and hardware
> sizing guidance.

## Features

### Equipment Control via INDI

Full INDI protocol client with support for 400+ Linux drivers:

- **Camera**, Capture, exposure control, gain, binning, ROI, cooler temperature
- **Telescope/Mount**, Slew, GoTo, park/unpark, tracking (sidereal/lunar/solar), NSEW manual control
- **Focuser**, Absolute/relative move, step control, temperature readout
- **Filter Wheel**, Position selection by slot number or filter name
- **Guider**, Pulse guiding in 4 directions, guide camera exposure
- **Dome**, Azimuth slew, shutter open/close, park/unpark, slave mode
- **Rotator**, Angle positioning, reverse toggle
- **Weather**, Temperature, humidity, dew point, wind, pressure, cloud cover, SQM, rain, safety status
- **Flat Panel**, Light on/off, brightness control, dust cap open/close

### DSLR / Mirrorless cameras

Beyond the dedicated astronomy cameras INDI exposes, Polaris speaks
to consumer DSLR / mirrorless bodies through a shared `ICamera`
abstraction with driver-specific backends:

- **Linux**, use the existing INDI `indi_gphoto_ccd` driver (wraps
  libgphoto2, supports hundreds of Canon / Nikon / Sony / Fuji
  bodies). Zero extra Polaris code; pick `driver=indi` in the
  Equipment card and the gphoto-exposed camera shows up alongside
  the astro CCDs. Setup walkthrough in
  [`docs/dslr-linux.md`](docs/dslr-linux.md).
- **Canon (Windows)**, native Canon EDSDK integration with full
  capture path: RAW + JPEG dual delivery, ISO + shutter + bulb
  control, automatic SaveTo=Host. CR2 files land verbatim under
  `{rig}/lights/.../`; the embedded JPEG drives the live preview.
  Install instructions + EULA caveats in
  [`docs/dslr-windows-canon.md`](docs/dslr-windows-canon.md).
- **Nikon (Windows)**, skeleton driver wired into the Equipment UI
  and ICamera dispatch. Implementation path documented (vendor the
  MIT-licensed [MekNikon](https://github.com/meklarian/MekNikon)
  MAID bindings, or build against the Nikon Imaging SDK for Z
  series) in [`docs/dslr-windows-nikon.md`](docs/dslr-windows-nikon.md).
- **Sony (Windows + Linux)**, skeleton driver covering two
  complementary paths: the legacy Wi-Fi Camera Remote API v1.90
  (HTTP/JSON, cross-platform, easiest to implement, reference:
  [nantcom/SonyCameraSDK](https://github.com/nantcom/SonyCameraSDK))
  for older α / NEX bodies, and the modern USB Camera Remote SDK
  v2.x for α7 III onward. Full landscape in
  [`docs/dslr-windows-sony.md`](docs/dslr-windows-sony.md).

The UI auto-detects which vendor SDKs are reachable on the running
host, shows install banners (with direct doc links) for any that
aren't installed, surfaces an **ISO** dropdown instead of the Gain
field for DSLR-class cameras, and hides cooler / binning controls
that don't apply. Captured RAW files (CR2 / NEF / ARW) are saved
verbatim alongside the camera-native pipeline, the embedded JPEG
becomes the on-screen preview while the RAW waits for the Studio
panel (or PixInsight / Siril if you'd rather process there).

### Real-Time Image Streaming

Dual-mode WebSocket image streaming with automatic format negotiation:

- **JPEG mode** (default), Server-side auto-stretch and JPEG encoding, works on all browsers (~300KB per frame)
- **Raw mode**, LZ4-compressed 16-bit pixel data with client-side WebGL debayer and MTF stretch (~3-10MB per frame)
- Backpressure handling, slow clients skip frames instead of falling behind
- Dead client eviction after consecutive send failures
- REST endpoint for latest preview image (`/api/image/latest/preview`)

### Live Stacking (EAA)

Real-time stacking for electronically assisted astronomy:

- Star detection via flood-fill HFR algorithm
- Triangle-based star matching for alignment
- Affine transform registration (translation + rotation + scale)
- Running average accumulation buffer
- Start/stop/reset controls with frame counter
- Per-frame median HFR + star count piggy-backed on the alignment pass
  (no extra detection cost), surfaced in WebSocket status so the LIVE
  tab can show drift over time

**Auto re-focus + auto re-center triggers** (LSTR): two independent
trigger axes that fire automatically during long EAA / comet-hunting
sessions without leaving the LIVE tab.

Re-focus triggers (any combination, first to cross fires):
  - Every N integrated frames
  - Every N minutes since last refocus
  - Sensor temperature drift ≥ ±X°C
  - HFR degradation ≥ Y% above HFR right after last AF run

Re-center triggers (same OR-combine pattern):
  - Every N integrated frames
  - Every N minutes
  - Plate-solve drift ≥ X arcsec (per-frame solve, heavy on RPi 4,
    default off)

Reference RA/Dec for re-center comes from a one-shot plate solve on
the first integrated frame (true astrometric position, not the mount's
report). Trigger handlers run sequentially inside `AddFrameAsync`,
the upstream capture pipeline naturally pauses during AF / re-center
since whoever's pushing frames is awaiting that call. Reentry guard
prevents concurrent AF + re-center on overlapping triggers.

Per-rig persistence via `EquipmentProfile.LiveStackTriggers` so each
setup keeps its own thermal + drift policy. UI is a collapsible
`<details>` panel inside the LIVE tab below the stack controls;
▶ Now buttons bypass gates for manual fires.

### Plate Solving & Centering

Strategy-based plate-solving dispatcher with four interchangeable backends and
a primary + blind-fallback pipeline:

- **ASTAP**, fast offline solver, hint-driven, the default primary
- **PlateSolve3**, PlaneWave's CLI, excellent at long focal lengths and small FOVs (≤10 stars), requires hints
- **Astrometry.net online**, REST API at nova.astrometry.net, truly blind, slow but robust (API key required)
- **Astrometry.net local**, `solve-field` wrapper with ±20% pixel-scale window, blind-capable
- Configurable per-installation: `PlateSolve:PrimarySolver`, `PlateSolve:BlindSolver`, `PlateSolve:UseBlindFallback`
- **Slew & Center**, Automated loop: slew to target → capture → solve → compute error → sync → re-slew (converges in 2-3 iterations, ~30-60s total)
- Configurable tolerance (default: 30 arcsec), async job tracking with real-time status polling
- Result carries `SolverUsed` so the UI knows which backend produced it

### Guiding (PHD2), full management

PHD2 is a first-class managed device, not just a black box we send commands to.

**Connection + telemetry:**
- JSON-RPC 2.0 line-framed protocol with async event loop on TCP (default port 4400)
- Ring buffer of last 300 GuideSteps with running RMS RA / RMS Dec / total RMS + peak values
- Live RA/Dec error chart (Chart.js) with auto-scaling Y-axis
- Calibration data captured automatically after a successful CalibrationComplete event
- Alert + settle status surfaced in the UI as toasts and banners
- Shows which guide camera + mount PHD2 is actually using (via `get_current_equipment`)

**Management, control PHD2 itself from the Web UI:**
- **Profile switcher**, list every PHD2 profile, switch with one click (auto-disconnects equipment first as PHD2 requires)
- **Equipment connect/disconnect**, tell PHD2 to wire up its own gear
- **Exposure**, dropdown populated from `get_exposure_durations` (e.g. "1.0s" / "100ms")
- **Dec guide mode**, Auto / North / South / Off
- **Auto-detect install location**, walks the well-known PHD2 install paths per OS (Windows: Program Files / Program Files (x86) / `%LocalAppData%\Programs`; macOS: `/Applications/PHD2.app`; Linux: `/usr/bin`, `/usr/local/bin`, `/opt/phd2/bin`, `/snap/bin`, plus a `$PATH` walk). When not detected the Guider tab surfaces an inline "Download PHD2" banner with a direct link
- **Launch / Shutdown PHD2 process**, when an executable is detected (or `PHD2:ExecutablePath` is set), N.I.N.A. Polaris can spawn PHD2 on the same host (loopback only) and gracefully shut it down via the `shutdown` RPC (falls back to process kill only if we own the process)
- **Auto-start on boot**, a single checkbox in the Guider tab makes the headless app launch PHD2 and connect the JSON-RPC client ~2s after every startup. Persisted per profile; survives restarts. Backed by a hosted service that retries the connect 5× in case PHD2's event server is slow to come up
- Commands: start guiding / stop / loop / pause / resume / dither (with settle pixels + time + timeout) / auto-select star / clear calibration / clear history

**Deep integration (PH2X):**
- **Rig ↔ PHD2 profile sync (1:1)**, each Polaris rig maps to a PHD2 profile of the same name. Switching rigs automatically switches the PHD2 profile via RPC + applies the rig's algorithm preset + any per-rig algorithm overrides. When a profile is missing, the GUI surfaces a banner pointing to the embedded PHD2 GUI tab where the user can run the Wizard.
- **Smart Calibrate**, one button: Polaris reads pixel scale, computes a sane calibration step from `(distance_px × pixel_scale) / guide_rate`, optionally slews the main mount to the celestial equator, clears calibration, finds a star, triggers `guide(recalibrate=true)`, monitors the calibration to completion via the AppState event stream, validates orthogonality + non-zero rate, and surfaces results. State machine + progress streamed live via `/ws/status` → `guider.calibrateJob`.
- **Algorithm tuning presets**, `Default` / `Reactive` / `Smooth` curated bundles for Hysteresis (RA) + Resist-Switch (DEC) algorithms; applied via `set_algo_param` with silent skip for params the current algorithm doesn't expose. Advanced disclosure shows every live knob (`get_algo_param_names` + per-name `get_algo_param`); editing any knob flips the preset to `Custom` and persists the bag on the rig.
- **Embedded PHD2 GUI** (Linux only), the GUIDE tab has a tabstrip: **Control** (JSON-RPC UI) | **PHD2 GUI** (xpra HTML5 client embedded via reverse-proxy). Lets you run PHD2's native Profile Wizard, Brain dialog, Guiding Assistant, dark library, etc. remotely without VNC/SSH. See [docs/phd2-gui-embedding.md](docs/phd2-gui-embedding.md) for install instructions. On Windows/macOS the Control tab still works fully; the GUI tab shows a clear OS-not-supported banner.

### Manual Focus Assist (no motor required)

A client-driven capture loop that turns the FOCUS tab into a manual-focus
aid, designed for rigs without an electronic focuser **and** as a
fine-tune helper after V-curve completes on motorised rigs.

- **HFR trend loop**: captures a frame every N seconds, parses HFR / FWHM
  / star count / Laplacian variance from the standard stats pipeline,
  plots HFR vs time on a 60-sample scrolling chart. A dashed baseline at
  the lowest HFR seen since the last Reset shows immediately when you've
  overshot focus.
- **Bahtinov mask analysis**: optional checkbox runs a radial line
  integration on the brightest star, finds the 3 diffraction spikes,
  identifies the central spike, and reports the perpendicular offset of
  the central line from the V's intersection. Canvas overlay draws the
  star cross + 3 spike lines + intersection circle + offset label.
  Sidebar surfaces a colour-coded readout (green / amber / red) with a
  rotate-inward / rotate-outward cue.
- **Coexists with motorised focusers**: subtabs `Manual Assist` (default
  when no motor) and `Auto V-curve`, with the manual stepper always
  available above the subtabs.
- See [docs/user-guide/focus.md](docs/user-guide/focus.md#manual-assist-subtab) for the full workflow.

### Auto-Focus (V-Curve)

Automated focus point determination via symmetric sweep:

- Captures N exposures around the current focuser position, measures HFR per sample via flood-fill star detection (median HFR for robustness against outliers)
- Least-squares parabola fit through valid samples; moves to the vertex
- Configurable step size, point count, exposure, minimum stars, backlash compensation, post-focus confirmation frame
- Live V-curve chart (Chart.js scatter) with fitted parabola and best-position marker
- **Live frame preview**, every AF sweep exposure pipes through the same `/ws/image-stream` channel as LIVE, rendered into a dedicated canvas on the Focus tab with a HUD chip showing `pos {N} · HFR {x.xx} · ★ {stars}` per sample. Lets you watch the focuser converge in real time without switching tabs.
- Restores starting position automatically on cancel or failure

### Meridian Flip Automation

Hands-off pier-side change during a sequence:

- Static LST/GMST math validated against USNO J2000 reference
- Workflow: pause PHD2 → re-slew to target (mount firmware flips) → settle → plate-solve recenter via Slew & Center → optional auto-focus → resume PHD2 guiding
- Configurable minutes-after-meridian trigger threshold (default 5 min)
- Live "Meridian in 1h 23m" countdown in the Sequence tab
- Safe failure paths: errors and cancels always try to resume guiding

### Advanced Sequencer (tree-based)

A full conditional-execution engine alongside (not replacing) the legacy
Simple Sequencer. Toggle the default tab via **Settings → Sequencer →
"Use Advanced Sequencer by default"**, both stay available either way.

**Tree model:**
- **Containers** group children: `Sequential` (run in order), `Parallel`
  (run concurrently, fail-fast on any child), `DeepSkyObject` (slew &
  plate-solve-center on a target before running children),
  `Templated` (paste-in a saved reusable fragment)
- **Instructions** are atomic actions; 30+ shipping:
  - Mount: Slew, Center (plate-solve), Park / Unpark, SetTracking, SolveAndSync
  - Camera: TakeExposure (N frames + filter / gain / binning / image type),
    CoolCamera (setpoint + tolerance), WarmCamera (gradual ramp at °C/min)
  - Focuser: AutoFocus (V-curve), MoveFocuser, MoveToFilterOffset (looks up
    the active rig's per-filter offset table; safe no-op when the filter
    isn't configured)
  - Filter wheel: SwitchFilter (by name or position)
  - Guider: StartGuiding, StopGuiding, Dither, AutoSelectStar
  - Dome: Open / Close shutter, Park, SlewToAzimuth, SyncToScope (Alt/Az math)
  - Flat panel: Open/Close cover, SetBrightness, ToggleLight
  - Rotator: RotateToAngle
  - Flow control: WaitForTime, WaitUntilTime (UTC), WaitUntilAltitude,
    WaitForSunBelowHorizon (low-precision sun alt for twilight),
    WaitForMoon (Above / Below altitude)
  - External: RunExternalScript (stdout/stderr captured, exit-code aware),
    SendHttpRequest (webhooks for Discord / Slack / dashboards)
- **Conditions** are loop predicates (containers with `isLoop=true` keep
  iterating while every condition holds): Until Time / Altitude / N Exposures
  / Duration / Moon Sets / While Safe (cloud cover + wind from weather device)
- **Triggers** fire between every child step:
  - Auto-focus on Temperature Change / HFR Increase / Every N Minutes / Filter Change
    (HFR trigger reads the median HFR from a StarDetector run that
    `TakeExposureInstruction` performs after every successful frame and
    parks in `ctx.Scratch["Frame:LastHfr"]`)
  - Meridian Flip (delegates to the existing service)
  - Dither After N Exposures (skipped silently when PHD2 isn't guiding)
  - Center After Drift (periodic plate-solve check against pinned coords)
  - Safety (cloud cover / wind / mount disconnect → graceful abort with reason)

**Persistence + execution:**
- Polymorphic JSON format with `$type` discriminator and `Version` field;
  every entity carries a stable Id so the editor can reference nodes
  across edits
- Validate() bubbles up errors with breadcrumb paths
  (`[DSO 'M31'/Lights] Exposure must be positive`); the engine refuses
  Start on a failing tree
- File-based template store (`Sequencer:TemplateDir`,
  default `./sequencer-templates/`) for reusable fragments hydrated
  into `TemplatedContainer` at load time
- Background runner with cancellation that propagates to every child;
  containers honour an `AbortRequested` flag (set by the Safety trigger)
  between every step

**Tree editor UI:**
- New "Adv" tab opens a tri-pane layout: palette (categorised by device),
  tree (status colour-coded per node), properties (auto-generated form
  by field type)
- Sortable.js drag-handle on the type badge to reorder siblings
- Save / Download JSON / Upload JSON / Validate / Start / Stop in the toolbar
- Live status mirroring, during a run the tree colours update every 2s
  showing what's Running / Completed / Failed / Skipped, plus the
  Safety-trigger abort reason if one fires

### Mosaic Planner

Build a multi-panel mosaic centred on any selected sky target. The
**🧩 Plan mosaic** button in the Sky tab opens a modal where you set
cols × rows + overlap %, with the per-panel FOV auto-filled from the
active rig (sensor + focal length). A live yellow grid overlay appears
on the sky map showing where each panel will land, adjust until it
covers your target.

- cos(δ) correction on RA, so the grid sits true at any declination
- Serpentine column order on alternating rows minimises slew distance
- Time estimate (slew + plate-solve + N × exposure) from configurable
  per-knob defaults
- **Export to Advanced Sequencer** lowers the whole grid into a
  `SequentialContainer` with one `DeepSkyObjectContainer` per panel
  (each plate-solves + centers + takes N exposures). "Export & load now"
  jumps straight into the Adv tab with the tree loaded and ready to run.

### Plugin System

Drop a third-party `.dll` into `./plugins/` (or
`Plugins:Directory`) and on next startup the host loads it into its
own `AssemblyLoadContext`, scans for `INinaPolarisPlugin`
implementations, and registers their contributed sequencer entities
into the polymorphic JSON converter + palette. From the user's point
of view the plugin's entities appear in the Advanced Sequencer
palette under whatever category the plugin chose.

- Isolated load context per plugin, failures don't take the host down
- Plugin assemblies reference the host's existing types (`ISequenceEntity`,
  `SequenceContext`, `ILogger`) directly, no SDK package to publish
- Contract surface in `Services/Plugins/`:
  - `INinaPolarisPlugin`, `Name`/`Version`/`Description`/`Author` +
    `Register(IPluginRegistry)`
  - `IPluginRegistry`, `RegisterSequencerEntity<T>(category)`
- `GET /api/plugins` lists what loaded + the entity discriminators each
  plugin contributed
- Sample plugin in [`samples/sample-plugin/`](samples/sample-plugin/README.md)
  with build + drop-in instructions

### Dithering

Random pixel-offset between frames to defeat fixed-pattern noise:

- Calls PHD2 `dither` RPC after every N successfully captured frames
- Waits for SettleDone event before next exposure
- Configurable dither pixels, every-N-frames, RA-only toggle, settle parameters
- Silent skip with debug log when PHD2 isn't connected or guiding, sequence never aborts

### Sky Catalog & Sky Atlas

Embedded deep sky catalog with 200+ objects:

- All 110 Messier objects + popular Caldwell + notable NGC/IC targets
- Fuzzy search by designation, common name, or alias ("M31", "Andromeda", "NGC 224")
- **Filtered browser**, type / magnitude range / declination range, sorted brightest first
- **Altitude chart**, target altitude across tonight's window (sunset → sunrise) with civil / nautical / astronomical twilight transitions
- Object metadata: coordinates (J2000), magnitude, type, common names

### Sky Map

Embedded sandboxed sky viewer for visual target selection, powered by
[stellarium-web-engine](https://github.com/Stellarium/stellarium-web-engine)
running as a WebGL2 iframe sub-app under `/sky/`:

- Gaia stars to ~mag 16, DSO surveys with image overlays, IAU
  constellation art + names in multiple cultures, atmosphere + horizon,
  sun + moon + planets + bright asteroids, HiPS Milky Way tiles
- Fully offline when the skydata bundle is present (~300 MB, bundled
  with `dotnet publish` by default)
- Camera-FOV overlay calculated from sensor + focal length
  (cos(Dec)-corrected). Mount rectangle (blue, anchored on current
  scope pointing) + target rectangle (red dashed, anchored at viewport
  centre, ASIAIR-style drag-to-frame)
- Click-to-pick targets, "Center on mount" + "Center selected" buttons
- Stellarium Remote Control sync, pull the currently-selected object
  from Stellarium with one click

> WebGL2 required. The SKY tab gracefully degrades to a banner on
> hosts without WebGL2 (e.g. running the local browser on a Pi 2
> framebuffer), open Polaris from a modern desktop or tablet
> browser instead.

### Weather Forecast

Astronomy-specific cloud / seeing / transparency forecast for tonight and
the next two nights:

- **Source**, [7Timer ASTRO API](https://www.7timer.info/) (free, no API
  key, 3-day window in 3-hour slots).
- **Per-slot observation score (0-100)** combining cloud cover, seeing,
  transparency and humidity, zero on precipitation. Colour-coded chip per
  slot (green ≥ 70 / amber 40-69 / red &lt; 40).
- **Tonight's best windows** callout, top three continuous runs of
  high-score slots between sunset and sunrise, ranked by total duration ×
  average score.
- **Per-day moon phase + illumination** alongside sunrise / sunset /
  astronomical twilight times (computed locally via SunCalc).
- **Weather emoji** per slot (☀ / 🌤 / ☁ / 🌧 / 🌨 / 🌫) with a moon glyph
  during the night hours.
- Server-side cache (15 min TTL) so multiple browsers on the same LAN
  share one upstream fetch. Falls back to a clear "Forecast unavailable"
  message when offline.

### Tonight's Best

Ranked list of objects worth observing right now from the observer's
location:

- **Categories**: deep-sky catalog objects (peak altitude ≥ 30°), the
  Moon, planets (Mercury through Neptune, peak altitude ≥ 10°), and a
  curated set of bright periodic comets.
- **Score** combines magnitude and altitude, balanced across categories
  so the Moon and bright planets don't push everything else off the list.
- **Each card** shows a thumbnail (NASA Image Library with Wikipedia
  fallback, cached on disk), name and common name, RA / Dec, magnitude,
  angular size, current and peak altitude, a 12 h altitude chart, and a
  compass arrow on the current azimuth.
- **Fits FOV badge**, when a camera is connected, each candidate is
  measured against the active rig's field of view (focal length + sensor)
  and flagged ✓ Fits / ⊘ Larger, with a chip filter to show only what
  fits.
- **Go to**, when a mount is connected, one click jumps to the Sky tab,
  centres the map on the target and kicks off Slew &amp; Center (slew +
  plate-solve + re-centre).
- **Image prefetch** in Settings pulls thumbnails for the full catalog +
  Moon + planets + comets to disk so the panel stays usable at offline
  observing sites.

### Studio (post-processing)

Browse, calibrate, stack, debayer, clean and export the FITS files
captured during the session, all from the same browser UI.

Files are auto-organised under `{ImageOutputDir}/{rig}/...`:

```
{rig}/lights/{target}/{filter}/{session}/light_*.fits
{rig}/calibration/dark/{exp}s_g{gain}/dark_*.fits
{rig}/calibration/bias/g{gain}/bias_*.fits
{rig}/calibration/flat/{filter}_g{gain}/flat_*.fits
{rig}/calibration/darkflat/{exp}s_g{gain}/darkflat_*.fits
{rig}/calibration/masters/master_*.fits
{rig}/calibrated/{target}/{filter}/cal_*.fits
{rig}/integrated/{target}/{filter}/master_light_*.fits
{rig}/processed/{target}/*.{fits,tif,png,jpg}
```

Session date follows the astronomical noon-to-noon convention, a
capture at 02:30 local time still belongs to the previous evening.

**Frame browser**:
- SQLite-backed metadata index, header-only FITS scan keeps a
  2000-frame session re-walkable in under a second.
- Filter by type / filter / target / date range; thumbnail grid with
  auto-stretched 256 px JPEGs generated on demand and cached on disk.
- Multi-select with status-bar counts that drive the batch operations.

**Single-frame viewer**:
- Double-click any thumbnail to open the fullscreen viewer
  (OpenSeadragon).
- Black / midtone / white sliders with auto-stretch defaults (MTF) and
  live debounced preview.
- Star annotation overlay, log-scale histogram (Chart.js), full
  statistics table.
- Export to **16-bit linear TIFF** (preserves dynamic range for
  downstream PixInsight / Siril), 8-bit stretched PNG, or JPEG.

**Master calibration frames** (bias / dark / flat / dark-flat):
- Select ≥ 2 raw calibration frames → "Create master from selection" →
  choose integration method (Mean / Median / **Sigma-clipped mean**
  default, 3σ low and high, 2 iterations).
- Background job with progress bar; output carries `NSUBS`, `INTMETH`,
  `IMAGETYP=MASTER{TYPE}` headers.
- Cross-frame dimension validation guards against mixed inputs.

**Light-frame calibration**:
- Select N raw lights → "Calibrate lights" → backend applies
  `(light − dark) / normalised_flat` and writes the result with a
  `CALSTAT` header (B / D / F letters per SBIG convention) plus
  `MDARK` / `MFLAT` / `MBIAS` filenames for traceability.
- **Auto-match per light**, for each light, picks the master with the
  same gain and closest exposure (darks), same gain and filter (flats),
  or same gain (bias). Manual override per dropdown is available.
- Bias is only applied when no dark is provided (darks already contain
  the bias signal). Dark-flats are preferred over bias as the flat
  calibrator.
- Per-light failures don't abort the batch; the job reports OK / failed
  counts.

**Batch stack** (offline alignment + integration):
- "Integrate (stack)" detects stars in each frame, aligns by affine
  star-matching against the reference (the frame with the most stars),
  resamples to the reference's coordinate system and reduces per-pixel
  with the chosen method.
- Frames that fail to align are skipped and reported as *dropped*.
- Output carries `NCOMBINE`, `EXPTOTAL`, `INTMETH`, `REJECT`, `STACKREF`
  headers and `IMAGETYP=MASTERLIGHT`.

**Per-frame operations** (in the viewer):
- **🎨 Debayer**, bilinear demosaic of RGGB / GRBG / GBRG / BGGR; the
  output is a Rec.601 luminance plane with `DEBAYER` / `BAYERIN` headers.
- **◐ Remove gradient**, sample-grid (8 × 6 default) median with
  MAD-based stellar rejection, 2D polynomial (degree 2) least-squares
  fit, subtracted relative to the fitted minimum so global brightness
  survives. `BGSUB` / `BGSAMPX` / `BGSAMPY` / `BGDEG` headers.
- **⌇ Noise reduce**, separable Gaussian blur. `NRMETHOD` / `NRRADIUS`
  headers.
- **✦ Sharpen**, unsharp mask with optional threshold guard for the
  noise floor. `SHARPEN` / `SHARPAMT` / `SHARPRAD` / `SHARPTHR` headers.

Each operation writes a new FITS under `{rig}/processed/{target}/` and
auto-refreshes the library.

### External tools (Siril + GraXpert)

Polaris drives two external CLIs when they're installed on the
host machine: **Siril** for preprocessing + stacking, and
**GraXpert** for AI-based background extraction, deconvolution,
and denoising.

Detection happens automatically on startup, the Settings tab's
**External tools** section shows the detected version and binary
path (or "Not detected" with install hints).

**Siril** ([siril.org](https://siril.org)) becomes the preferred
stacking engine the moment it's detected. The STUDIO tab gains a
**⚡ Stack with Siril** button that runs your chosen `.ssf`
script against the selected frames. Polaris ships 9 curated
preprocessing scripts (OSC + Mono × the with/without dark/flat/DBF
matrix, plus OSC narrowband extraction), and also picks up your
personal scripts from the standard Siril scripts dir so anything
you wrote works the same way. See
[docs/siril-setup.md](docs/siril-setup.md).

**GraXpert** ([graxpert.com](https://www.graxpert.com)) offers
three operations:
- **🌅 BGE (background extraction)**, removes gradients.
- **✨ Deconvolution** (v3.0+), sharpens a stacked master.
- **🔇 Denoise** (v3.0+), AI noise reduction on the master.

You can run GraXpert in three ways:
1. **Manual batch**, multi-select frames in **FILES**, click the
   op button, tune the sliders, hit Start.
2. **Auto during capture**, tick "Auto-extract gradient with
   GraXpert (per frame)" in the **AUTORUN** End Events panel.
   Every saved light fires a fire-and-forget BGE in the
   background. Designed for heavy-light-pollution sites where
   each frame has its own gradient.
3. **Combined with Siril**, in the STUDIO Siril modal, tick
   "Inject GraXpert BGE per-frame before stacking" to chain the
   two: GraXpert cleans each light first, then Siril stacks the
   `_bge` outputs. Slower but produces a much cleaner master.

Decon + Denoise are manual-only on integrated masters, running
them per-frame degrades SNR. See
[docs/graxpert-setup.md](docs/graxpert-setup.md).

Outputs land in dedicated per-tool folders so you can tell what
came from where: `{rig}/siril/{target}/`, `{rig}/bge/{target}/`,
and `{rig}/decon|denoise/{target}/`.

When Siril / GraXpert isn't installed, the built-in C# pipeline
(MasterFrameService + CalibrationService + BatchStackingService)
remains available as a fallback so users without the externals
still get a working stacking workflow.

### GraXpert AI in the browser (WebGPU / ONNX Runtime Web)

GraXpert ships its background extraction, deconvolution, and
denoise models as ONNX files. Polaris hosts them and runs them
**directly in the client browser via WebGPU + ONNX Runtime Web**,
not on the Pi. That changes the perf math completely:

- **Server (Pi 4 / 5) load**: zero CPU during inference. Polaris
  just serves the `.onnx` files (cached locally per-client in
  IndexedDB) and the raw FITS pixels.
- **Where the GPU work happens**: your laptop, desktop, tablet,
  or phone that's connected to the Polaris UI. Modern integrated
  graphics (Intel Iris Xe, Apple M-series, AMD RDNA, NVIDIA) all
  expose WebGPU and crunch the GraXpert tile-based pipeline in
  a fraction of the time the Pi 5 CPU would take.
- **Fallback**: when WebGPU is unavailable (older browsers, no
  GPU surfaced), ONNX Runtime Web auto-falls back to **WASM SIMD
  + multi-threading**. Slower than WebGPU but still entirely
  client-side.

Typical end-to-end timing for a 6000×4000 RGB master, BGE +
Denoise + Decon-Stars pass:

| Client | Pipeline time |
|---|---|
| Apple M1 Pro (WebGPU) | 8-12 s |
| Intel Iris Xe / RTX 3060 (WebGPU) | 10-15 s |
| iPad Pro M2 (WebGPU) | 15-20 s |
| Older laptop, WASM SIMD fallback | 60-90 s |
| Pi 5 running GraXpert CLI on the host | 4-8 min |

Same FITS, same output quality (the math is identical, just on
different hardware). For users running Polaris on a Pi but
opening the UI from a decent laptop, in-browser AI is **30-50x
faster** than the CLI on the Pi itself.

In the **EDITOR** tab the AI section has BGE / Denoise / Decon
buttons that route to the in-browser pipeline by default. The
existing CLI path is still there (Settings toggle) for cases
where a strong server CPU + weak client GPU flip the math.

WebGPU on LAN requires HTTPS, which Polaris auto-configures via
a self-signed cert on port 5000. See
[docs/user-guide/onnx-inference.md](docs/user-guide/onnx-inference.md)
for the full breakdown + browser compatibility matrix, and
[docs/user-guide/https-setup.md](docs/user-guide/https-setup.md)
for the one-time per-device cert trust step.

### File explorer

The **Files** tab is a full server-side file explorer. Browse the
device that runs Polaris, including USB sticks and external SSDs,
without dropping into an SSH session.

- **Roots**, Windows drive letters (`C:`, `D:`, …) or Unix mount
  points (`/`, `/home`, `/mnt`, `/media`, `~`). Free-space and volume
  label shown per root.
- **Navigation**, clickable breadcrumbs, parent shortcut, "show
  hidden" toggle, persistent cwd across reloads.
- **Selection**, plain click selects one, ctrl/cmd-click toggles,
  shift-click selects a range. The header checkbox selects all.
- **Operations**, new folder, rename, cut, copy, paste, delete.
  Cut + paste across volumes falls back to copy-then-delete
  automatically.
- **Preview**, FITS / XISF render via the same auto-stretch as
  Studio (JPEG). PNG / JPG / GIF / BMP / WebP pass through. TIFF
  decoded via SkiaSharp. `.txt` / `.log` / `.json` / `.md` / `.xml` /
  `.csv` open in an inline text viewer (first ~32 KB).
- **Download**, single file is a direct browser download with the
  correct filename; multi-select streams a ZIP archive built on the
  fly, so dragging 50 × 60 MB FITS onto your laptop doesn't OOM the
  Raspberry Pi.
- **Studio root**, select a folder and click **⭐ Set as Studio
  root**. Studio rescans this tree on its next visit. The Settings
  tab no longer carries the directory input; it just shows the
  current value and links here.

**Safety**: every destructive operation prompts with `window.confirm`
and the server requires `confirmed=true` on the `/delete` endpoint,
nothing gets wiped by an accidental double-click. A path blocklist
refuses access to `C:\Windows`, `/proc`, `/sys`, `/dev`, `/etc/shadow`,
`/etc/ssh`, and per-segment matches for `.ssh`, `.aws`, `.gnupg`, and
`.config/gh` at any depth. Every file operation logs at `Information`
level with `EventId="FileOp"`.

> **The Polaris server has no authentication on the LAN.** The Files
> tab exposes the filesystem to anyone who can reach the server
> address. Polaris assumes a trusted local network, do **not**
> expose port 5000 directly to the internet. For remote access use
> the Relay (which has per-tenant tokens and quotas).

### Sequence Engine + Image Persistence

Target list execution with automated imaging:

- Multi-target sequences with per-target exposure, gain, binning, filter, and frame count
- Automatic slew-to-target before capture
- Pause/resume with SemaphoreSlim gate
- Real-time progress tracking via WebSocket (1Hz status broadcast)
- **Persisted output** in two formats, selectable per profile:
  - **FITS**, extended headers (camera / telescope / focuser / rotator / filter / weather / observer / target, 30+ keywords per the N.I.N.A. manual spec)
  - **XISF** (PixInsight native), UInt16 monochrome with optional LZ4 compression (~3-10× smaller than FITS); native `<Property>` elements alongside `<FITSKeyword>` mirrors so any downstream tool works
- Configurable filename pattern (`{target}_{filter}_{exposure}s_{date}_{seq}` etc.)
- Optional dithering between frames + automatic meridian flip
- Focal length / focal ratio come from the **active equipment rig** (see below), not a global setting

### Flat Wizard

Automated flat-field acquisition:

- Binary search on exposure time per filter until median ADU lands within tolerance of target (default 30000 ADU ± 5%)
- Captures N flat frames at the converged exposure, tagged `IMAGETYP=FLAT`
- Per-(filter, binning) trained exposures persisted to `trained-flats.json` for next session

### Web UI

Responsive, dark-themed interface inspired by ASIAIR:

- **Home**, Cold-start landing with a Horsehead/Flame nebula hero. 4 colour-coded status cards (Equipment / Guider / Sequence / Server) react in real time to the rest of the app, plus 6 quick-action tiles that jump straight into the relevant tab (Connect / Plan / Launch PHD2 / Build sequence / Auto-focus / Live view). Live UTC clock in the hero
- **Live View**, Real-time camera preview with WebGL2 GPU rendering (debayer + MTF stretch on GPU), star annotations overlay, crosshair + 3x3 grid, hover pixel readout (raw ADU or RGB), manual stretch sliders, image-history thumbnail strip, HFR + star-count history chart, detailed statistics panel + histogram, full-resolution zoom viewer (OpenSeadragon)
- **Preview**, Dedicated snap-shot tab (between Focus and Autorun) with exp/gain/bin/filter controls, single-shot + opt-in loop mode, "💾 Save to disk" toggle that routes captures to `{rig}/snaps/{filter}_{date}/` (separate from sequence lights so test snaps don't contaminate the science folder). Filter dropdown only appears when a filter wheel is connected; pre-capture filter swap happens automatically when chosen
- **RIGS** (was "Equipment"), Reorganised by **role**: connection strip pinned to the top (compact when INDI/Alpaca is connected, expanded with the connect form when not), then a responsive role-based grid, Main Telescope (OTA optics + curated **catalogue picker** that auto-fills aperture / focal length / f-ratio / required back-focus), Camera (with auto-detected sensor + temperature + cooler power chart + **driver dropdown** for INDI / Canon EDSDK / Nikon / Sony / Alpaca with install-banner links when a vendor SDK isn't reachable), Mount, Focuser, Filter Wheel, Guidescope (metadata), Guide Camera (read-only, PHD2 owns it). Optional accessories (Rotator, Flat Panel, Dome, Weather) collapsed in a `<details>` block below, auto-expanded when at least one is configured. Rig selector + "💾 Save selections" in the header. "Manage rigs…" modal handles rename / activate / duplicate / delete + per-rig filter offsets (the device pickers + optics live on the cards now, no longer duplicated in the modal)
- **Mount Control**, NSEW directional pad, tracking toggle, park/unpark, GoTo via Sky Explorer
- **Focus**, Manual stepper + full Auto-Focus V-curve panel (start/abort, live progress bar, fitted parabola chart, best-position marker) + **live frame preview canvas** that renders each AF sample exposure with a HUD chip showing current position / HFR / star count
- **Guider**, Two-tab layout: **Control** (existing JSON-RPC UI, connect, profile switcher, exposure, Dec-mode, equipment connect, guiding controls, live RA/Dec error chart, settle parameters, **Smart Calibrate** button with optional slew-to-equator, **algorithm preset pills** Default/Reactive/Smooth/Custom + Advanced disclosure for individual algorithm knobs, profile-sync indicator chip) and **PHD2 GUI** (Linux only, xpra HTML5 client iframe embedding PHD2's native window for Wizard / Brain / Guiding Assistant / dark library access, see [docs/phd2-gui-embedding.md](docs/phd2-gui-embedding.md)). Launch / Shutdown / Auto-start on boot persist as before
- **Sky Explorer**, stellarium-web-engine WebGL2 iframe (sandboxed sub-app at `/sky/`) with Gaia stars to ~mag 16, DSO surveys with image overlays, IAU constellation art, atmosphere/horizon, sun + moon + planets + bright asteroids, and HiPS Milky Way tiles. Fully offline when the bundled skydata is present. Drag-to-frame ASIAIR-style target rectangle + blue mount rectangle (auto cos(δ) correction). Object search, filtered catalog browser, "Tonight's altitude" chart with twilight bands, Stellarium sync, Slew & Center, "Plan mosaic" (panel grid pushed to the engine as yellow tile overlays), Add to Sequence. WebGL2 required.
- **Tonight**, Ranked list of best DSOs / Moon / planets / comets for the current observing window. Cards with NASA / Wikipedia thumbnails (offline-cached), live ephemeris, mini altitude chart, compass widget, FOV-fit badge, and a mount-gated "Go to" that triggers Slew & Center
- **Weather**, Astronomy-specific 3-day forecast from 7Timer with per-3 h-slot observation score (cloud + seeing + transparency + humidity), tonight's best windows callout, per-slot weather emoji (lunar glyph at night) and per-day sun/moon ephemeris from SunCalc
- **Sequence**, Target list editor with progress bars, collapsible Meridian Flip + Dithering panels, start/pause/resume/stop
- **Adv**, Advanced Sequencer tri-pane tree editor (palette / tree / properties) with drag-reorder, live status colouring during runs, load/save/import/export JSON, template management
- **Studio**, Post-processing tab: SQLite-indexed FITS browser, single-frame viewer with manual stretch + multi-format export, master integration, light calibration with auto-match, batch alignment + stack, debayer + background extraction + noise reduction + sharpening
- **Settings**, INDI connection, observatory location (with **address geocoder** + **"Use my location"** GPS button, accessible any time after first-run), image output (format: FITS or XISF), profile management. Sensor dimensions are auto-read from the connected camera; focal length lives per-rig (Equipment → Manage rigs)
- **First-run**, Location-setup modal with **address geocoder** (OpenStreetMap/Nominatim), browser geolocation, or manual lat/lon entry

**Activity Bar**, App-wide footer (36 px, glass treatment) showing live operation chips (sequence progress, AF run, meridian flip, slew, exposure, filter change, PHD2 calibrating/settling, live stack, Siril/GraXpert jobs) on the left + host CPU% + RAM (green < 60% / amber 60-85% / red > 85%) on the right. Always visible across every tab; collapses chip-row to nothing when idle.

**Night Mode**, Red-on-black theme that preserves dark adaptation (critical for field use).

**Mobile Responsive**, Full functionality on phones and tablets with bottom tab navigation.

### Equipment Rigs (multi-rig support)

One physical N.I.N.A. Polaris host frequently serves multiple physical setups,
"backyard SCT", "travel APO", "remote site mono camera + AO". Each user
profile carries a list of named **rigs**; switch in one click and every device
selector + per-rig default (cooler temperature, focuser step size, focal
length, PHD2 host/port, PHD2 algorithm preset) is reapplied automatically.

Per-rig stored data:
- Device names: Camera / Telescope / Focuser / FilterWheel / Rotator / FlatDevice / Dome / Weather (INDI names as returned by getProperties)
- Cooler target temperature, default gain / offset / binning
- Focuser step size + backlash
- **Main scope focal length + aperture** (drives FOV calculation + FITS FOCALLEN; auto-fillable from the catalogue picker)
- **Guide scope focal length + aperture** (record-keeping + PHD2 pixel-scale sanity check)
- **Telescope brand + model + accessory** (auto-resolved from `wwwroot/data/telescopes.json` + `optical-accessories.json` via the Main Telescope card's catalogue picker)
- PHD2 endpoint (host + port)
- **PHD2 deep-integration fields**: `PHD2ProfileId` cache after first name-match, `PHD2AlgoPreset` (Default / Reactive / Smooth / Custom), `PHD2CalibrationStepMsOverride`, `PHD2AutoSyncOnRigSwitch` (default true, triggers `PHD2ProfileSyncService` to swap PHD2 profile + apply preset on every rig activation), `PHD2CustomAlgoParams` (free-form `axis:name → value` bag)
- Per-filter focuser offsets (consumed by `MoveToFilterOffsetInstruction`)

CRUD via `/api/equipment/rigs/*`. UI: dropdown in the RIGS tab header
plus a slim **Manage rigs…** modal for rename / activate / duplicate / delete
+ filter offsets (the device pickers + optics live directly on the RIGS-tab
cards, no longer duplicated in the modal).

Existing profiles auto-migrate on first load, the pre-existing
`LastCamera` / `LastTelescope` / etc. fields become the rig named "Default".

### Telescope + Accessory Catalogue

Curated catalogue of popular astro OTAs + reducers / Barlows /
flatteners that drives auto-fill of the rig's optical fields.
Pick brand → model → optional accessory and Polaris computes the
aperture, native + effective focal length, f-ratio, and the
required camera-side back-focus.

- **~80 telescopes** spanning Askar (FRA + PHQ + APO lines),
  Astro-Physics, Celestron (C-series + EdgeHD + RASA), Explore
  Scientific, GSO RC + Newtonian, Meade LX200, Sharpstar EDPH,
  Skywatcher (Esprit + Evostar + Quattro + Skymax), SVBony (SV48P
  + SV503 + SV550 + SV535 + SV545 + SV555), Takahashi (FSQ + TOA
  + Epsilon), Tele Vue, Vixen, William Optics (RedCat + ZenithStar
  + GT + FLT).
- **~25 optical accessories**, Celestron 0.7× EdgeHD + f/6.3 SCT
  reducers, Starizona Hyperstar 8/11, Skywatcher 0.85× Esprit,
  Askar / Sharpstar / SVBony / Takahashi dedicated reducers, WO
  Flat 6A III flatteners, Tele Vue Powermate 2/2.5/4/5×, generic
  1.6/2/3× Barlows. `compatibleScopes` filters the dropdown to
  entries that fit the picked OTA; empty list means generic.
- **Effective focal length** = native × accessory factor (rounded).
  Back-focus reminder surfaces in amber, wrong backspacing is the
  most common cause of elongated stars in the corners of a flatener
  shot.
- The picker auto-fills, then writes the resolved values into the
  rig, the catalogue can change later without breaking saved rigs.
  Off-catalogue scopes still work via the manual focal length /
  aperture inputs.
- Catalogues live in `wwwroot/data/telescopes.json` +
  `wwwroot/data/optical-accessories.json`. PRs welcome to extend
  the lists.

### Profile Management

JSON-based settings persistence with multi-profile support:

- Observatory location
- INDI connection settings
- Plate solver configuration (primary, blind fallback, paths, API keys)
- Image output directory, naming pattern, format (FITS / XISF)
- List of equipment rigs (see above)
- Save, load, and rename profiles

### Remote Access (Relay Server)

For accessing a N.I.N.A. Polaris host on a remote LAN (observatory site, friend's
house) without inbound port-forwarding or dynamic DNS, this repo ships a
companion **NINA.Relay.Server** project that acts as a reverse tunnel.

```
 Browser  ──HTTPS──►  relay.example.com  ──reverse WebSocket──►  Raspberry Pi
                       (NINA.Relay.Server)                        (N.I.N.A. Polaris,
                                                                   no public IP)
```

- N.I.N.A. Polaris opens an **outbound** WebSocket to the relay (firewall-friendly)
- Multiplexed binary protocol: many concurrent HTTP requests on one socket
- Auto-reconnect on the client side with exponential backoff (2s → 60s)
- Subdomain routing (`alice.relay.example.com`) OR path-prefix (`/t/alice/...`)
- Multi-tenant: each headless host has its own bearer token
- **WebSocket-over-tunnel forwarding**, image stream + status broadcasts +
  any other browser-side WS endpoint now work end-to-end through the relay
  (browser ↔ relay ↔ tunnel client ↔ local Kestrel WS, bidirectional pump)
- **JSON tenant store** (`tenants.json`), hot-reloaded on file change; no
  server restart needed when adding or revoking tokens (legacy
  `appsettings.json` `Tenants:` section still works for trivial setups)
- **Per-tenant rate limiting**, token-bucket on both HTTP requests/sec and
  bytes/sec (request + response counted together), with configurable burst.
  Exceeding either bucket returns HTTP 429 with a `Retry-After` header
  naming which bucket tripped
- **Monthly byte quotas** + **expiring tokens**, per-tenant `monthlyBytes`
  cap (counter persists across restarts in `tenant-state.json`, auto-resets
  on the 1st UTC, HTTP 402 when exhausted) and `expiresAt` timestamp
  (auth refused after expiry, useful for trials)
- **Built-in TLS**, `Tls:Mode=letsencrypt` (LettuceEncrypt fetches +
  renews certs from Let's Encrypt automatically) or `Tls:Mode=pfx` (load a
  static `.pfx`). No reverse proxy required
- **Web admin UI** at `/admin/`, gated by `Admin:Password` (HTTP Basic);
  add/edit/delete tenants, view live tunnels + monthly usage bars,
  generate cryptographically-random tokens, reset usage counters,
  browse the audit log with per-tenant filter
- **Per-request audit log** (JSON-lines `audit.log`), timestamp, tenant,
  method, path, status, bytes in/out, duration, source IP, outcome
  reason. Auto-rotates at 50 MB. In-memory ring buffer surfaced via
  `/_admin/audit?tenant=&limit=` and the admin UI
- **mTLS for tunnel auth**, per-tenant `clientCertThumbprint` pins the
  X.509 cert the tunnel client must present. Bearer token alone is the
  default; mTLS is opt-in per tenant. N.I.N.A. Polaris client points at a
  `.pfx` via `Relay:ClientCertPath` (+ optional password)
- Admin endpoints: `/_health`, `/_tunnels` (with per-tunnel byte counters),
  `/_admin/tenants` (full CRUD), `/_admin/generate-token`,
  `/_admin/usage/{token}/reset`, `/_admin/audit`, `/_admin/reload-tenants`
- Two deployment models, self-host on a $5 VPS, or use a hosted instance
- See [`src/NINA.Relay.Server/README.md`](src/NINA.Relay.Server/README.md) for
  deployment instructions, Caddy reverse-proxy example, full `tenants.json`
  schema, and protocol details

### Network Resilience

Built for unreliable field WiFi:

- WebSocket auto-reconnect with exponential backoff + jitter
- Request deduplication for in-flight API calls
- Server reachability detection with automatic recovery
- Toast notifications for connection state changes
- 15-second request timeout with abort controller
- **Adaptive bandwidth**, server measures actual WebSocket send latency and auto-downgrades raw clients to JPEG when bandwidth degrades, upgrades back when it recovers

### Discovery & Cross-Platform Drivers

- **mDNS announcer**, host reachable at `polaris-app.local:5000` from any device on the LAN (no IP needed). Override the instance name via `Mdns:InstanceName` in `appsettings.json` if you need a different label (e.g. multiple Polaris instances on the same network).
- **Alpaca (ASCOM HTTP) support**, UDP discovery on port 32227 plus base Camera / Telescope wrappers, so you can drive Windows-only ASCOM drivers exposed over the network

### WiFi mode switch (Hotspot ↔ Station)

ASIAIR-style WiFi management built into the .deb. The Pi comes up
as a hotspot named `Polaris-Hotspot` (password `polaris1234`) on
first boot, so you can reach `https://polaris-pi.local:5000` from
a phone in the field without ever plugging in a monitor. From the
Settings → Network panel you switch the Pi onto your home WiFi at
home, with a 30 s try-and-revert safety net that pops the hotspot
back if the new credentials fail. Linux + NetworkManager only
(Pi OS Bookworm default). See
[docs/user-guide/network-mode.md](docs/user-guide/network-mode.md).

### Remote Terminal (SSH from the browser)

Embedded xterm.js + SSH.NET bridge under SETTINGS → **Remote terminal**.
Opens an interactive shell against any LAN host (or `localhost` for the
Polaris host itself), so you can restart `indiserver`, tail logs, or
`sudo systemctl status` something on a headless Pi without plugging in a
screen.

- Off by default, set `Terminal:Enabled = true` in `appsettings.json` to
  expose the `/ws/terminal` endpoint
- No auto-login, credentials are entered per session and never persisted
- 10-minute idle timeout closes abandoned sessions server-side
- Resizes with the panel; supports `vim`, `htop`, `tmux`, colours, scrollback

See [docs/user-guide/remote-terminal.md](docs/user-guide/remote-terminal.md).

### Polar Alignment (TPPA)

Three-point polar alignment built into the **POLAR** sidebar tab. Slews the
mount to three configurable RA positions, plate-solves at each, computes
the mount axis vs the true celestial pole, and reports azimuth + altitude
errors in arcminutes. Hemisphere-aware (works in both N + S latitudes).

- **Refine** mode loops capture + solve in real time so you watch the
  error vector shrink as you adjust the tripod knobs (SharpCap-style UX,
  red → amber → green overlay on the live frame)
- Per-rig parameters (slew step, exposure, settle, gain) saved in the
  active equipment profile
- Cancel mid-flight via the standard panic-stop control

## Architecture

```
nina-polaris/
├── src/
│   ├── NINA.Polaris/              ← ASP.NET Core app (Kestrel, Minimal API)
│   │   ├── Program.cs              ← Host builder, service registration
│   │   ├── Endpoints/              ← REST API (13 endpoint groups)
│   │   ├── WebSocket/              ← Image stream + status broadcast
│   │   ├── Services/               ← Business logic layer
│   │   └── wwwroot/                ← Web UI (HTML, JS, CSS)
│   │
│   ├── NINA.Core.Portable/         ← Shared enums, models, utilities (net10.0)
│   ├── NINA.Image.Portable/        ← Image processing, FITS I/O, statistics (net10.0)
│   └── NINA.INDI/                  ← INDI protocol client (net10.0)
│       ├── Protocol/               ← XML parser/writer
│       ├── Client/                 ← TCP client, blob receiver, connection manager
│       └── Devices/                ← 9 device type implementations
│
│   ├── NINA.Relay.Protocol/        ← Shared multiplexed frame format (net10.0)
│   └── NINA.Relay.Server/          ← Standalone reverse-tunnel relay (ASP.NET Core)
│
├── tests/
│   └── NINA.Polaris.Test/         ← 294 unit tests (NUnit)
│
├── deploy/                         ← Deployment scripts
│   ├── nina-polaris.service        ← systemd unit file
│   ├── install.sh                   ← Linux installer
│   ├── publish-linux-arm64.sh       ← RPi build script
│   ├── publish-win-x64.ps1         ← Windows build script
│   └── docker-build.sh             ← Multi-arch Docker buildx
│
├── Dockerfile                      ← Multi-stage, linux/amd64 + arm64
└── docker-compose.yml              ← NINA + optional indiserver sidecar
```

### Technology Stack

| Layer | Technology | Purpose |
|-------|-----------|---------|
| Web server | Kestrel (standalone) | Native .NET, no nginx/IIS needed |
| API | ASP.NET Core Minimal API | Low overhead, AOT-friendly |
| Real-time (images) | WebSocket (binary) | JPEG or LZ4-compressed raw frames, adaptive |
| Real-time (status) | WebSocket (JSON) | Equipment + sequence + guider + AF + meridian flip at 1Hz |
| Frontend framework | Alpine.js v3 | Reactive UI (~15KB, no build step) |
| UI typeface | Inter (SIL OFL 1.1, self-hosted) | Variable woff2 for every weight + italic, ~740 KB total. No external CDN call, the UI looks the same online and offline |
| Charts | Chart.js v4 | Guiding, focus, HFR, temperature, histogram, altitude |
| Sky map | stellarium-web-engine (AGPLv3, sandboxed in `/sky/` iframe) | WebGL2 sky viewer with Gaia stars, DSO surveys, constellation art, atmosphere, HiPS Milky Way tiles |
| Image viewer | OpenSeadragon | Full-resolution zoom/pan over last frame |
| Image rendering | WebGL2 shaders | GPU debayer + MTF stretch (CPU fallback) |
| Image encoding | SkiaSharp | Cross-platform JPEG / PNG encoding (incl. STUDIO previews + thumbnails) |
| FITS I/O | Custom FITSWriter | Extended headers per N.I.N.A. manual spec |
| XISF I/O | Custom XISFWriter | PixInsight native, LZ4-compressed, FITSKeyword mirrored |
| TIFF export | Custom TiffWriter | Baseline uncompressed 8-bit / 16-bit grayscale (SkiaSharp doesn't ship TIFF) |
| STUDIO frame index | Microsoft.Data.Sqlite | On-disk metadata cache so 2000-frame sessions list in &lt; 50 ms |
| Astronomy ephemeris | CosineKitty.AstronomyEngine | Planet positions for the Tonight's Best panel (MIT, ~150 KB, no native deps) |
| Sun / moon math | SunCalc (BSD-2, vendored) | Sunset / sunrise / twilight / moon phase for the Weather panel |
| Weather forecast | 7Timer ASTRO (HTTP, no key) | Cloud / seeing / transparency, 3-day window in 3 h slots |
| Compression | K4os.Compression.LZ4 | Fast image compression (~2GB/s) |
| Equipment drivers | INDI protocol (TCP/XML) + Alpaca (HTTP) | 400+ Linux drivers + ASCOM over network |
| Plate solving | ASTAP / PlateSolve3 / Astrometry.net (online + local) | Strategy dispatcher with primary + blind fallback |
| Guiding | PHD2 (TCP/JSON-RPC, port 4400), fully managed | Profile switch, equipment connect, process launch/shutdown |
| Remote access | NINA.Relay.Server reverse tunnel | Public access without inbound port-forwarding |
| Discovery | Makaretu.Dns.Multicast | mDNS announcer for `nina.local` |
| Geocoding | Nominatim (OpenStreetMap, proxied) | Address → coordinates for location setup |
| Stellarium sync | HTTP (Remote Control plugin, port 8090) | Pull selected object as target |
| Logging | Serilog | Structured logging to console + file |
| Target framework | .NET 10.0 | Latest LTS, cross-platform |

## Getting Started

### Prerequisites

Minimum to build + run from source:

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Git (with submodules: stellarium-web-engine is pulled at build time)
- On Linux for hardware control: `sudo apt install indi-full`
- Optional plate-solving: [ASTAP](https://www.hnsky.org/astap.htm) +
  H17/H18 database

For the complete tooling matrix, Windows + Linux ARM (Raspberry Pi) +
Linux x64, required vs optional per feature, firewall rules, hardware
sizing, see **[REQUIREMENTS.md](REQUIREMENTS.md)**.

### Build & Run (Development)

```bash
git clone https://github.com/DanWBR/nina-polaris.git
cd nina-polaris
dotnet build
dotnet run --project src/NINA.Polaris
```

Open `http://localhost:5000` in your browser.

### Run Tests

```bash
dotnet test
```

## Deployment

### Raspberry Pi 4 / 5 (one-line install)

The `.deb` package (built automatically by GitHub Actions on every
tag push) handles user creation, systemd unit, indi-web venv, apt
dependencies, and self-signed HTTPS cert generation. End-user install:

```bash
wget https://github.com/DanWBR/NINA.Polaris/releases/latest/download/polaris_arm64.deb
sudo apt install ./polaris_arm64.deb
# 30 seconds later: Polaris running at https://<hostname>.local:5000
```

The postinst prints the URL, sets up the service, and starts it.
Full breakdown in [packaging/README.md](packaging/README.md). Pi-
specific end-to-end recipe (hardware checklist, OS flashing, optional
SSD mount) in [docs/user-guide/raspberry-pi-setup.md](docs/user-guide/raspberry-pi-setup.md).

**Manage the service:**

```bash
sudo systemctl status polaris       # Check status
sudo journalctl -u polaris -f       # Follow logs
sudo systemctl restart polaris      # Restart
```

### Other Linux (portable tarball)

For non-Debian distros (Fedora, Arch, etc) or when you prefer no
systemd integration:

```bash
wget https://github.com/DanWBR/NINA.Polaris/releases/latest/download/polaris-linux-arm64.tar.gz
tar -xzf polaris-linux-arm64.tar.gz
cd polaris-linux-arm64
./NINA.Polaris   # foreground; wire your own service unit if needed
```

Replace `linux-arm64` with `linux-x64` for Intel/AMD 64-bit Linux.

### Windows Mini PC

Download the portable zip from
[GitHub Releases](https://github.com/DanWBR/NINA.Polaris/releases/latest):

```powershell
# x64 (most desktops/laptops):
Invoke-WebRequest -Uri "https://github.com/DanWBR/NINA.Polaris/releases/latest/download/polaris-win-x64.zip" -OutFile polaris.zip
Expand-Archive polaris.zip
cd polaris-win-x64
.\NINA.Polaris.exe

# ARM64 (Surface Pro X, some Copilot+ PCs):
Invoke-WebRequest -Uri "https://github.com/DanWBR/NINA.Polaris/releases/latest/download/polaris-win-arm64.zip" -OutFile polaris.zip
Expand-Archive polaris.zip
cd polaris-win-arm64
.\NINA.Polaris.exe
```

Open `https://localhost:5000` (accept the self-signed cert once).

For unattended Windows installs, wire your own service via `sc.exe`,
NSSM, or a scheduled task. Build-from-source path:

```powershell
.\deploy\publish-win-x64.ps1
```

### Docker

Multi-stage `Dockerfile` and `docker-compose.yml` are checked in. Builds for both `linux/amd64` and `linux/arm64`:

```bash
# Single host build (uses your platform)
docker compose up -d --build

# Multi-arch build + push to registry
REGISTRY=ghcr.io/yourname ./deploy/docker-build.sh latest
```

The default compose file runs in `network_mode: host` so mDNS and INDI LAN
reach work out of the box. Add `--profile indi` to also start an
indiserver sidecar with the standard simulators (good for testing with no
hardware).

Persistence:
- `nina-data` volume → profiles + trained-flat exposures
- `./images` bind-mount → captured FITS output

## API Reference

### Equipment

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/equipment/devices` | List all discovered INDI devices |
| POST | `/api/equipment/connect` | Connect to all selected devices |
| POST | `/api/equipment/disconnect` | Disconnect all devices |
| GET | `/api/equipment/status` | Aggregated status of every selected device (includes auto-derived sensor dimensions) |

### Equipment Rigs (multi-rig profiles)

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/equipment/rigs` | All rigs + active id |
| GET | `/api/equipment/rigs/active` | Active rig (full payload) |
| POST | `/api/equipment/rigs` | Create empty rig `{ name }` |
| POST | `/api/equipment/rigs/clone` | Duplicate the active rig `{ newName }` |
| PUT | `/api/equipment/rigs/{id}` | Update a rig (selections, defaults, focal lengths, PHD2 endpoint) |
| POST | `/api/equipment/rigs/{id}/activate` | Switch to this rig |
| DELETE | `/api/equipment/rigs/{id}` | Delete a rig (refuses to delete the last one) |

### Camera

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/camera/select/{name}` | Select camera by INDI device name |
| POST | `/api/camera/connect` | Connect selected camera |
| POST | `/api/camera/capture` | Capture an image `{ exposure, gain, binning, filter }` |
| POST | `/api/camera/abort` | Abort current exposure |
| POST | `/api/camera/cooler` | Set cooler `{ enabled, targetTemperature }` |
| GET | `/api/camera/status` | Camera status |

### Telescope

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/telescope/select/{name}` | Select mount |
| POST | `/api/telescope/slew` | Slew to coordinates `{ ra, dec }` |
| POST | `/api/telescope/move/{direction}` | Manual move (north/south/east/west/stop) |
| POST | `/api/telescope/park` | Park mount |
| POST | `/api/telescope/unpark` | Unpark mount |
| POST | `/api/telescope/tracking` | Toggle tracking `{ enabled }` |
| POST | `/api/telescope/abort` | Emergency stop |

### Focuser

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/focuser/select/{name}` | Select focuser |
| POST | `/api/focuser/move/relative` | Move relative `{ steps }` |
| POST | `/api/focuser/move/absolute` | Move to position `{ position }` |
| POST | `/api/focuser/abort` | Abort movement |

### Filter Wheel

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/filterwheel/status` | Current filter and position |
| POST | `/api/filterwheel/position/{slot}` | Move to slot number |
| POST | `/api/filterwheel/filter/{name}` | Move to filter by name |

### Imaging

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/image/latest/preview` | Latest image as JPEG |
| GET | `/api/image/latest/stats?withStars` | Image dimensions + mean/median/min/max/stddev/MAD (+ optional star detection HFR stats) |
| GET | `/api/image/latest/histogram?bins=256` | Pixel-value histogram |
| GET | `/api/image/latest/stars?maxStars&sigma` | Detected star list with (x, y, HFR, flux, peak) |
| GET | `/api/image/stream/clients` | Per-client WebSocket diagnostics (mode, latency, streaks) |
| POST | `/api/image/stream/adaptive` | Toggle adaptive bandwidth `{ enabled }` |

### Guider (PHD2)

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/guider/connect` | Connect to PHD2 `{ host, port }` |
| POST | `/api/guider/disconnect` | Disconnect |
| GET | `/api/guider/status` | App state, RMS, peak, settle, pixel scale, last alert |
| GET | `/api/guider/equipment` | Guide camera + mount + aux mount + AO names (`get_current_equipment`) |
| GET | `/api/guider/steps?limit=N` | Recent GuideStep history |
| POST | `/api/guider/guide` | Start guiding `{ settlePixels, settleTime, settleTimeout, recalibrate }` |
| POST | `/api/guider/dither` | Dither `{ pixels, raOnly, settle* }` |
| POST | `/api/guider/stop` / `/loop` / `/pause` / `/resume` | State changes |
| POST | `/api/guider/find-star` / `/clear-calibration` / `/clear-history` | Maintenance |
| GET | `/api/guider/profiles` | List PHD2 profiles + current one |
| POST | `/api/guider/profile/{id}` | Switch PHD2 profile (auto-disconnects equipment first) |
| GET | `/api/guider/equipment/connected` | Whether PHD2's own equipment is connected |
| POST | `/api/guider/equipment/{connect,disconnect}` | Toggle PHD2's own equipment |
| GET | `/api/guider/exposure` | Current exposure ms + list of available durations |
| POST | `/api/guider/exposure/set/{ms}` | Set guide exposure |
| GET | `/api/guider/dec-mode` | Current Dec guide mode |
| POST | `/api/guider/dec-mode/{Auto\|North\|South\|Off}` | Set Dec mode |
| GET | `/api/guider/process/status` | Is PHD2 running? did we launch it? path configured? |
| POST | `/api/guider/process/launch` | Spawn PHD2 (loopback only, polls port 4400 for up to 30s) |
| POST | `/api/guider/process/shutdown` | Graceful JSON-RPC shutdown, falls back to kill only if we own it |
| GET | `/api/guider/install-info` | Detected install (`installed`, `resolvedPath`, `downloadUrl`, `os`, `searchedPaths`), UI uses this to surface "Download PHD2" when missing |
| POST | `/api/guider/auto-start/{true\|false}` | Persist auto-start-on-boot preference in the user profile |
| POST | `/api/guider/profile/sync` | Sync a rig (default: active rig) to its matching PHD2 profile + apply preset. Body: `{ rigId? }` |
| GET | `/api/guider/profile/sync/status` | Last sync phase / error / profileMissing flag |
| POST | `/api/guider/calibrate/smart` | Start smart calibration job. Body: `SmartCalibrateOptions` (slewToEquator, exposureMsOverride, calibrationStepMsOverride, timeoutSeconds). Returns `{ jobId }` |
| GET | `/api/guider/calibrate/smart/{jobId}` | Poll calibration state (phase + stepMs + pixelScale + calibration + warnings) |
| POST | `/api/guider/calibrate/smart/{jobId}/abort` | Abort running calibration |
| GET | `/api/guider/algo-presets` | Curated algorithm presets (Default / Reactive / Smooth) with the (axis, name, value) triples each applies |
| POST | `/api/guider/algo-preset/{name}` | Apply preset live + persist on the active rig |
| GET | `/api/guider/algo-params` | Live values: per axis, every param `get_algo_param_names` reports |
| PUT | `/api/guider/algo-params` | Set a single live knob `{ axis, name, value }` + flip preset to "Custom" |
| GET | `/api/guider/gui-session/status` | xpra-hosted PHD2 GUI lifecycle (xpra installed? version? session running? bind port) |
| POST | `/api/guider/gui-session/{start,stop,restart}` | Manage the embedded PHD2 GUI session (Linux only; 501 elsewhere) |
| ALL | `/phd2-gui/{**}` | Reverse-proxy to xpra HTML5 client (HTTP + WebSocket). Same-origin so iframe sessionStorage works |

### Auto-Focus

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/autofocus/start` | Start V-curve `{ steps, stepSize, exposureSeconds, minStars, backlashSteps }` |
| POST | `/api/autofocus/abort` | Abort + restore start position |
| GET | `/api/autofocus/status` | Live progress + sampled points |
| GET | `/api/autofocus/result` | Most recent completed run + fitted parabola coefficients |

### Meridian Flip

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/meridianflip/settings` | Current configuration |
| PUT | `/api/meridianflip/settings` | Update settings |
| GET | `/api/meridianflip/status` | State + LST + hour angle + minutes-to-meridian |
| POST | `/api/meridianflip/trigger` | Manual flip `{ ra, dec }` |
| POST | `/api/meridianflip/abort` | Abort in-progress flip |

### Flat Wizard

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/flatwizard/start` | Start automated flat acquisition `{ filters, framesPerFilter, targetAdu, tolerance, minExposure, maxExposure, binning }` |
| POST | `/api/flatwizard/abort` | Abort |
| GET | `/api/flatwizard/status` | Live progress + per-filter results |
| GET | `/api/flatwizard/trained` | Persisted (filter+binning → exposure) dictionary |

### Alpaca (ASCOM HTTP)

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/alpaca/discover?timeoutMs=3000` | UDP-broadcast discovery on port 32227 + per-server `/management/v1/configureddevices` enrichment |
| GET | `/api/alpaca/devices?host=&port=` | Direct device list query (skip discovery) |
| GET | `/api/alpaca/camera/info?host=&port=&device=` | Camera probe (sensor, cooler, binning) |
| GET | `/api/alpaca/telescope/info?host=&port=&device=` | Telescope probe (pointing, tracking, pier side) |
| POST | `/api/alpaca/{camera,telescope}/connect?host=&port=&device=&connect=` | Connect / disconnect |

### Stellarium

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/stellarium/target?host=&port=` | Pull currently-selected object from Stellarium Remote Control plugin |
| GET | `/api/stellarium/view?host=&port=` | Current view direction (alt / az / fov) |

### Weather Forecast

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/weather/forecast?lat=&lon=` | 7Timer ASTRO 3-day forecast in 3 h slots with computed `observationScore` (0-100) per slot. Server-cached 15 min |

### Tonight's Best

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/sky/tonights-best?lat=&lon=&limit=` | Ranked list of DSOs / Moon / planets / comets observable during tonight's window |
| GET | `/api/sky/image?name=` | Resolve thumbnail URL for a celestial object (NASA Image Library → Wikipedia fallback, disk-cached 30 days) |
| POST | `/api/sky/image/prefetch` | Walk the full DSO catalog + Moon + planets + comets and pull all thumbnails to disk for offline use |

### STUDIO, Post-Processing

Frame browser, master integration, calibration, batch stacking, debayer,
background extraction, noise reduction, sharpening, and multi-format
export.

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/studio/rescan` | Walk `ImageOutputDir` recursively, header-only FITS scan, upsert SQLite index |
| GET | `/api/studio/rescan/status` | Rescan progress |
| GET | `/api/studio/frames?type=&filter=&target=&dateFrom=&dateTo=&limit=&offset=` | Paginated frame list |
| GET | `/api/studio/frames/{id}` | Full row + FITS keyword dump |
| GET | `/api/studio/frames/{id}/thumb` | Auto-stretched 256 px JPEG thumbnail (cached on disk) |
| GET | `/api/studio/stats` | Aggregate: total lights, total exposure (h), distinct targets / filters |
| GET | `/api/studio/frames/{id}/preview?black=&mid=&white=&max=&format=jpg\|png` | Stretched preview (debounced slider re-renders hit this) |
| GET | `/api/studio/frames/{id}/autostretch` | Auto-stretch black/mid/white triple to seed UI sliders |
| GET | `/api/studio/frames/{id}/stats?stars=` | Full ImageStatistics + StarDetector output + histogram |
| POST | `/api/studio/frames/{id}/export?format=tif\|png\|jpg&stretched=&black=&mid=&white=` | Export to `{rig}/processed/{target}/` |
| POST | `/api/studio/masters` | Start master-frame integration `{ frameIds, type: Bias\|Dark\|Flat\|DarkFlat, method: Mean\|Median\|SigmaClippedMean }` → `{ jobId }` |
| GET | `/api/studio/masters/{jobId}/status` | Master-integration progress |
| POST | `/api/studio/calibrate` | Calibrate lights `{ lightIds, masterDarkId?, masterFlatId?, masterBiasId? }` (null = auto-match per light) → `{ jobId }` |
| GET | `/api/studio/calibrate/{jobId}/status` | Calibration progress with succeeded / failed counts |
| POST | `/api/studio/integrate` | Batch stack `{ frameIds, method }` (align + integrate) → `{ jobId }` |
| GET | `/api/studio/integrate/{jobId}/status` | Stack progress with combined / dropped / total exposure |
| POST | `/api/studio/frames/{id}/debayer` | Bilinear demosaic → luminance FITS in `{rig}/processed/{target}/` |
| POST | `/api/studio/frames/{id}/bgextract?samplesX=&samplesY=&polyDegree=` | Subtract polynomial gradient |
| POST | `/api/studio/frames/{id}/nr?radius=` | Gaussian noise reduction |
| POST | `/api/studio/frames/{id}/sharpen?amount=&radius=&threshold=` | Unsharp mask sharpening |

### Live Stacking

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/livestack/start` | Start live stacking |
| POST | `/api/livestack/stop` | Stop live stacking |
| POST | `/api/livestack/reset` | Reset stack buffer |
| GET | `/api/livestack/status` | Stack frame count and state |

### Simple Sequence (flat list)

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/sequence` | Current sequence items and state |
| POST | `/api/sequence` | Load sequence `[{ name, exposure, gain, ... }]` |
| POST | `/api/sequence/start` | Start execution |
| POST | `/api/sequence/pause` | Pause execution |
| POST | `/api/sequence/resume` | Resume from pause |
| POST | `/api/sequence/stop` | Stop execution |
| GET | `/api/sequence/status` | Detailed progress |

### Advanced Sequencer (tree-based)

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/sequencer/document` | Current `SequenceDocument` + state + lastError + abortReason |
| POST | `/api/sequencer/document` | Load a `SequenceDocument` (JSON object) |
| GET | `/api/sequencer/document/json` | Raw JSON download for "save sequence to file" |
| POST | `/api/sequencer/document/json` | Accept raw JSON body, "load sequence from file" |
| POST | `/api/sequencer/start` | Validate + run the tree in the background |
| POST | `/api/sequencer/stop` | Cancel the run via the engine's CTS |
| POST | `/api/sequencer/validate` | Walk Validate() across the tree, return errors |
| GET | `/api/sequencer/types` | Palette listing, every known `(type, category, kind)` |
| GET | `/api/sequencer/templates` | List saved templates + their store dir |
| GET | `/api/sequencer/templates/{name}` | Load a named template |
| POST | `/api/sequencer/templates/{name}` | Save a `SequenceDocument` as a named template |
| DELETE | `/api/sequencer/templates/{name}` | Delete a template |

### Mosaic Planner

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/mosaic/plan` | Compute panels + time estimate from `MosaicRequest` (for the UI overlay preview) |
| POST | `/api/mosaic/to-sequence` | Build the plan + lower to a `SequenceDocument`; optionally load into the engine via `loadIntoEngine=true` |

### Plugins

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/plugins` | List loaded plugins with name / version / author / discriminators they contributed |

### Sky & Plate Solving

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/sky/catalog/search?query=M31` | Search embedded DSO catalog |
| GET | `/api/sky/catalog/{name}` | Get object by exact name |
| GET | `/api/sky/catalog/types` | Distinct object types (for filter dropdowns) |
| GET | `/api/sky/catalog/filter?query&type&minMag&maxMag&minDec&maxDec&limit` | Filtered catalog query |
| GET | `/api/sky/altitude?ra&dec&stepMinutes` | Target altitude track across tonight's window + twilight transitions |
| GET | `/api/sky/fov` | Current FOV based on optics config |
| GET | `/api/sky/solver/status` | Primary + blind solver availability and identity |
| GET | `/api/sky/solver/list` | All four plate-solver backends with id / name / available / blind flag |
| POST | `/api/sky/slew-and-center` | Start slew & center job `{ ra, dec, toleranceArcsec }` |
| GET | `/api/sky/slew-and-center/{id}/status` | Job progress |
| POST | `/api/sky/slew-and-center/{id}/cancel` | Cancel job |

### Sequence (Dither)

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/sequence/dither` | Current dither settings |
| PUT | `/api/sequence/dither` | Update dither settings `{ enabled, pixels, everyNFrames, raOnly, settle* }` |

### System

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/system/status` | System info (CPU, RAM, uptime) |
| GET | `/api/system/geocode?query=&limit=` | Address geocoding via Nominatim (rate-limited, User-Agent set) |
| GET | `/api/system/relay` | Relay tunnel status (`state`, `hostname`, `lastError`) |
| GET | `/api/system/profiles` | List profiles |
| GET | `/api/system/profile` | Active profile |
| PUT | `/api/system/profile` | Update settings |
| POST | `/api/system/profile/save-as` | Save profile as new name |
| POST | `/api/system/profile/load/{id}` | Load profile by ID |

### WebSocket Streams

| Endpoint | Type | Description |
|----------|------|-------------|
| `/ws/image-stream` | Binary | Live image frames (JPEG or raw+LZ4) |
| `/ws/status` | JSON | Equipment + sequence status at 1Hz |

**Image stream negotiation:** After connecting, send `{"mode":"jpeg"}` or `{"mode":"raw"}` to select format.

**Status message format:**

```json
{
  "type": "status",
  "equipment": {
    "indi": { "connected": true },
    "camera": { "name": "ZWO ASI2600MC", "temperature": -10.0 },
    "telescope": { "ra": 0.713, "dec": 41.27, "tracking": true, "slewing": false },
    "focuser": { "position": 12500, "temperature": 15.2 },
    "filterWheel": { "position": 3, "currentFilter": "Ha", "filters": ["L","R","G","B","Ha","OIII","SII"] }
  },
  "liveStack": { "isRunning": true, "frameCount": 42 },
  "sequence": { "state": "running", "currentItemIndex": 1, "totalFrames": 100, "totalFramesCompleted": 37 }
}
```

## Configuration

### appsettings.json

```json
{
  "Indi": {
    "Host": "localhost",
    "Port": 7624
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    }
  }
}
```

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `ASPNETCORE_URLS` | `http://0.0.0.0:5000` | Listen address and port |
| `DOTNET_gcServer` | `0` | Use Workstation GC (saves RAM on RPi) |
| `Indi__Host` | `localhost` | INDI server hostname |
| `Indi__Port` | `7624` | INDI server port |
| `PHD2__ExecutablePath` | (auto-detected) | Override the path to phd2.exe / phd2 binary. By default the app walks the standard install paths per OS, only set this for non-standard installs |
| `PHD2__Host` / `PHD2__Port` | `localhost` / `4400` | PHD2 event server endpoint |
| `PHD2__InstanceNumber` | `1` | PHD2 `-i N` instance number |
| `PHD2__AutoStart` | `false` | Fallback for `PHD2AutoStart` profile flag. UI checkbox in Guider tab is the normal way to set this |
| `Sequencer__TemplateDir` | `sequencer-templates` | Folder where Advanced Sequencer templates are stored (one JSON file per template) |
| `Plugins__Enabled` | `true` | Set to false to skip the plugin scan entirely |
| `Plugins__Directory` | `plugins` | Folder scanned at startup for plugin `.dll` files |
| `PlateSolve__PrimarySolver` | `astap` | One of `astap`, `platesolve3`, `astrometry-net-online`, `astrometry-net-local` |
| `PlateSolve__BlindSolver` | `astrometry-net-online` | Fallback when primary fails |
| `PlateSolve__UseBlindFallback` | `true` | Disable to lock to the primary only |
| `PlateSolve__AstapPath` | (auto) | ASTAP CLI path |
| `PlateSolve__PlateSolve3Path` | (none) | PlateSolve3.exe path |
| `PlateSolve__SolveFieldPath` | `/usr/bin/solve-field` | Local Astrometry.net binary |
| `PlateSolve__AstrometryApiKey` | (none) | nova.astrometry.net API key |
| `Mdns__Enabled` / `Mdns__InstanceName` | `true` / `nina-<hostname>` | mDNS announcer |
| `Relay__Enabled` | `false` | Enable reverse-tunnel client |
| `Relay__ServerUrl` | (none) | e.g. `wss://relay.example.com/_tunnel` |
| `Relay__Token` | (none) | Bearer token matching a tenant entry on the relay server |
| `Relay__ClientCertPath` | (none) | Path to a `.pfx` to present on tunnel TLS handshake (mTLS) |
| `Relay__ClientCertPassword` | (none) | Password for the `.pfx` (optional) |

Relay **server** side (different process, same `Relay__*` prefix in `appsettings.json`):

| Key | Default | Purpose |
|-----|---------|---------|
| `Relay__TenantsFile` | `tenants.json` | Path to the JSON tenant store; hot-reloaded on change. Falls back to the legacy `Tenants:` section if empty/missing |
| `Relay__UsageStateFile` | `tenant-state.json` | Persistent monthly-byte counter file |
| `Proxy__TimeoutSeconds` | `60` | Per-request timeout (long enough for plate-solving uploads) |
| `Proxy__HostnameSuffix` | (none) | e.g. `.relay.example.com` to enable subdomain routing |
| `Admin__Password` | (empty) | Password for `/_admin/*` and the `/admin/` Web UI. Empty = admin API disabled (returns 503) |
| `Audit__Enabled` | `true` | Set to false to disable the audit log |
| `Audit__Path` | `audit.log` | JSON-lines audit log path |
| `Audit__MaxFileBytes` | `52428800` | Rotate at this size (default 50 MB) |
| `Audit__RingBufferSize` | `5000` | In-memory ring for `/_admin/audit` |
| `Tls__Mode` | `off` | `off` / `pfx` / `letsencrypt` |
| `Tls__ClientCertificateMode` | `request` | `none` / `request` / `require`, Kestrel client-cert behaviour (mTLS) |
| `Tls__HttpsPort` | `443` | HTTPS bind port when TLS is enabled |
| `Tls__RedirectHttpToHttps` | `false` | 308-redirect plain HTTP to HTTPS |
| `Tls__PfxPath` / `Tls__PfxPassword` |, | Static cert when `Tls:Mode=pfx` |
| `Tls__LetsEncrypt__Domains` |, | string[] of domains for ACME issuance |
| `Tls__LetsEncrypt__EmailAddress` |, | Contact email for Let's Encrypt |
| `Tls__LetsEncrypt__UseStaging` | `false` | Use Let's Encrypt staging API while testing |

## Performance Targets

| Metric | Target | Notes |
|--------|--------|-------|
| Memory | < 500 MB | RPi 4 with 2GB RAM |
| Startup | < 5 seconds | RPi 4 |
| Image relay | ~3-10 MB/frame | LZ4 compressed, fits WiFi 5GHz |
| JPEG preview | ~200-400 KB | For mobile/weak clients |
| Frontend bundle | ~580 KB total | Alpine.js + libs, cacheable |
| WASM live-stack bundle | ~12 MB on disk, ~3 MB gzipped | One-time download per browser |
| Status broadcast | 1 Hz | Equipment + sequence state |

### Testing without hardware

Polaris ships with a one-click button to spawn a fake telescope +
camera + focuser + filter wheel. Open Settings → Equipment simulator
→ Launch. The simulated camera renders **real stars** from the GSC
catalog at whatever RA/Dec the simulated mount is pointing at,
plate solve, auto-focus, live stacking all work end-to-end against
it. Linux/macOS uses INDI simulators (`apt install indi-bin`);
Windows uses Alpaca Omni Simulator. See
[docs/user-guide/simulator-mode.md](docs/user-guide/simulator-mode.md).

### Client-side compute offload (CLST)

Live stacking can run **in your browser** via a WebAssembly module
that reuses the same `NINA.Image.Portable` algorithms the server
runs. On Pi 2 / Pi 3 hosts this is the only way to keep up, the
Pi just orchestrates equipment + relays raw frames, the browser
does StarDetector + alignment + accumulator. Auto-detected on WS
handshake; per-rig override in the LIVE tab toolbar. See
[docs/user-guide/client-side-compute.md](docs/user-guide/client-side-compute.md).

## Support the project

If N.I.N.A. Polaris saves you an evening of fiddling with rigs and you want to
chip in for hosting / a coffee / dark-sky travel:

[**❤️ Donate via Stripe**](https://buy.stripe.com/9B68wPeoLcMSgOz2iJbMQ02)

Donations are entirely optional, the project stays free and open-source
either way. Bug reports and PRs are just as welcome (see below).

## Contributing

Contributions are welcome! This project follows the same coding standards as the main [N.I.N.A. repository](https://github.com/isbeorn/nina).

### Project Structure for Contributors

- **Endpoints** are in `src/NINA.Polaris/Endpoints/`, each is an extension method on `WebApplication`
- **Services** are in `src/NINA.Polaris/Services/`, registered as singletons in `Program.cs`
- **INDI devices** follow a consistent pattern in `src/NINA.INDI/Devices/`
- **Frontend** is plain HTML/JS/CSS in `src/NINA.Polaris/wwwroot/`, no build step required
- **Tests** go in `tests/NINA.Polaris.Test/` using NUnit

## Data attribution

When the Photometric Color Calibration (PCC) workflow is enabled,
Polaris uses the AAVSO **APASS DR10** star catalog under a CC-BY
4.0 license. The catalog is downloaded by `scripts/download-apass.py`
to `wwwroot/catalogs/apass/apass.db` (gitignored). If you publish
images calibrated with PCC, please credit:

> Henden, A. A., Levine, S., Terrell, D., Welch, D. L., Munari, U.,
> & Kloppenborg, B. K. (2018). "The APASS Data Release 10." VizieR
> On-line Data Catalog: II/336. https://www.aavso.org/apass

## License

This project is licensed under the Mozilla Public License 2.0.
