# End-to-end workflow

The complete path from "scope is on the tripod" to "final image
exported as JPEG". This guide stitches every Polaris feature into one
continuous night, with pointers to deeper per-feature pages when you
need them.

If you just want the speed-run, follow the numbered steps. If you
want to understand each tool, the per-section links go to the
reference pages.

## Audience and assumptions

This walkthrough assumes:

- Polaris is [installed](installation.md) on a Pi / mini-PC / Windows
  host and reachable in the browser at `https://polaris.local:5001`
  (or whatever your host is).
- You have done at least one [first-night setup](first-night.md), so
  you understand rigs, INDI/Alpaca, and PHD2 connection. We will not
  repeat that here.
- You are shooting deep-sky (DSO) with a cooled camera or DSLR. The
  planetary lucky-imaging path is covered in
  [VIDEO](video-planetary.md), not here.

Expected duration end to end: roughly **3 hours of capture** +
**30 minutes of processing**. Most of that is the camera doing its
job while you do something else.

## The map

```
1. Setup        RIGS → PHD2 → SKY (location, target)
   └─ ~15 min, once per night

2. Acquire      FOCUS → AUTORUN (or ADV) + LIVE
   └─ ~3 hours, mostly unattended

3. Calibrate    STUDIO masters + light calibration
   └─ ~10 min, automation

4. Integrate    STUDIO batch stack
   └─ ~10 min, automation

5. AI cleanup   GraXpert BGE → Denoise → Decon (browser ONNX)
   └─ ~5 min, browser inference

6. Edit         EDITOR sliders + sidecar
   └─ ~10 min, hands on

7. Export       JPEG/PNG/TIFF with resize and quality
   └─ ~1 min
```

The same artefact flows down the chain: lights and calibration
frames are captured to disk, STUDIO produces a master, GraXpert
cleans it in your browser, the editor adds the creative pass, and
the export drops the deliverable in `processed/{target}/edited/`.

---

## 1. Setup

### 1a. Pick (or load) a rig

Open **RIGS** in the sidebar.

If you already saved a rig on a previous night, pick it from the
dropdown at the top and you are done with this step: all device
selections, focal length, gain defaults and PHD2 profile binding
come back.

If this is a new equipment combination, see
[RIGS, multi-rig setup](rigs.md) for the full card-by-card walk.
Minimum to capture tonight:

- Main Telescope card: brand, model, accessory (reducer/flattener).
  Focal length and aperture auto-fill from the catalog.
- Main Camera card: pick the device under the connection strip
  (INDI on Linux, ASCOM/Alpaca on Windows), set cooler target, hit
  Connect.
- Mount card: pick device, Connect, confirm tracking is enabled.
- Focuser card: pick device, Connect.
- Filter Wheel card (if you have one): pick device, Connect, verify
  the filter list matches your wheel.

Click **Save selections** at the top so this rig captures the
choices for next time.

### 1b. Hook up PHD2

Switch to **GUIDE** in the sidebar. On the Control tab:

1. Click **Launch PHD2** (auto-detected install) or **Connect** if
   PHD2 is already running.
2. In PHD2 itself (or the **PHD2 GUI** tab on Linux): activate the
   profile that matches your guide camera and mount. Polaris will
   sync to it automatically when the rig and PHD2 profile names
   match.
3. Click **Connect equipment** in Polaris.
4. Click **Loop**, then **Find Star**, then **Smart Calibrate** if
   calibration is stale or missing. Polaris computes the calibration
   step from pixel scale and guide rate for you.

Detail: [GUIDE, full PHD2 integration](guide-phd2.md).

### 1c. Confirm location

Open **SETTINGS** in the sidebar and scroll to **Observatory
location**. If lat/lon are blank or wrong (you travelled), fix them
now: every later step (altitude scoring, meridian flip math,
tonight's-best filtering, twilight badges) reads from this single
source.

### 1d. Polar align (only if needed)

Open the **POLAR** tab. If you just set up the mount and have not
polar aligned yet, run TPPA from here:

1. The chip row at the top of POLAR suggests good TPPA targets for
   your hemisphere and time. Click **Go to** next to one of them, the
   mount slews and Polaris uses it as the reference for the three
   plate-solve points.
2. Hit **Start TPPA**. Polaris slews three points along an RA arc,
   solves each, computes the polar axis offset, and shows a vector
   overlay.
3. Optionally enable **Refinement** to keep re-solving while you
   adjust the alt/az knobs in real time.

If your mount is already polar aligned from a previous session
(permanent pier, observatory) skip this entirely.

### 1e. Pick a target

Open **SKY** in the sidebar. Search by name in the top bar (M31,
NGC 7000, Caldwell 14) or pick from the **TONIGHT** tab if you want
suggestions ranked by altitude and visibility right now.

Click the target card, then **Slew & Center**. Polaris commands the
mount, captures a plate-solve frame, computes the offset, re-slews
until the target is centred to within 30 arcsec (typically 30s to
2 min). When the Sky tab shows the FOV overlay sitting on the
target, you are framed.

---

## 2. Acquire

### 2a. Focus

Open **FOCUS**. Two options:

- **Manual**: stepper buttons, watch HFR in the stats strip drop as
  you approach focus.
- **V-curve auto-focus** (recommended): set step size and sample
  count for your focuser, hit **Start AF**. Polaris samples N
  positions, fits a parabola to the HFR curve, and moves the focuser
  to the vertex. Live preview canvas shows each sample as it lands.

Detail: [FOCUS](focus.md).

### 2b. Snap a test frame

Open **PREVIEW**. Take a single shot at the exposure you plan to
use for the sequence:

- Confirm the target is centred (it should be, after Slew & Center).
- Confirm stars are tight (HFR within the focuser tolerance you
  expect for tonight's seeing).
- Confirm the histogram is healthy: background just clear of the
  left edge, no clipping on the right.

If anything is off, fix it before committing to a 3 hour run.
Detail: [PREVIEW](preview.md).

### 2c. Build the sequence

For one target with one filter set, **AUTORUN** is the right tool:

1. **+ Add target**, the row pre-fills with your current SKY target.
2. Set: Filter, Exposure (s), Count, optionally Gain / Offset / Bin.
3. Repeat **+ Add target** rows if you want LRGB or multiple
   targets in the same night.
4. Optional collapsible panels:
   - **Dithering**: usually 5 px every frame, RA-only on weak Dec
     correction.
   - **Meridian flip**: turn on if your target will cross. Polaris
     pauses guiding, slews back to target, re-centres, optionally
     re-focuses, resumes guiding.
   - **End-actions**: park mount, warm camera, stop PHD2,
     **Auto-GraXpert BGE on each light** (fire-and-forget), shutdown
     server (Linux).
5. Hit **Start sequence**.

For multi-target unattended nights with conditional logic (loop
until altitude, abort on weather), use **ADV** instead. See
[ADV sequencer](adv-sequencer.md).

### 2d. Watch it run (or do not)

Open **LIVE**. Each frame lands here as it is captured:

- The frame mirrors the file landing in
  `{ImageOutputDir}/{Rig}/lights/{Target}/{Filter}/{Date}/`.
- Toggle **Stack ON** for ASIAIR-style live integration. You get a
  growing combined image you can show your kids while the sequence
  runs.
- Open the **Triggers** panel to enable auto re-focus on HFR
  degradation or temperature change, and auto re-center on plate-
  solve drift. See [LIVE](live-stacking.md).

You can close the browser tab and walk away. The server keeps
running. Come back in the morning.

---

## 3. Calibrate

You need calibration frames (bias, darks, flats) shot for this
camera. If you do not yet have them, run separate AUTORUN passes
with the corresponding image type set (Settings on the rig sets
the default folder layout). See
[FLAT wizard](studio.md#flat-wizard-via-end-action) for assisted
flat capture.

Once you have lights on disk plus a library of bias/dark/flat
frames, switch to **STUDIO**.

### 3a. Build masters

In the frame browser, filter by **Type = Bias** (or Dark, or Flat).
Select all the frames in a matching set (Shift-click range,
Ctrl-click toggle) and click **Create master**.

- Polaris reads `IMAGETYP` from the FITS header and pre-selects
  the master type.
- Pick integration method: **Sigma-clipped mean** (3σ, 2 iterations)
  is the default and the right choice for most cases. Use Median
  for very small stacks.
- Output lands in
  `{ImageOutputDir}/{Rig}/calibration/masters/master_{type}_{key}.fits`
  with `NSUBS` and `INTMETH` headers.

Repeat for each calibration type and key (per filter for flats, per
(exposure, gain) for darks).

### 3b. Calibrate the lights

Back in the frame browser, filter by **Type = Light** for tonight's
target. Select all of them and click **Calibrate**.

- The auto-match picker shows which master dark / flat / bias it
  paired with each light group, you can override per group from the
  dropdowns.
- Pixel math: `(light - dark - bias) / (flat - flatDark) ×
  mean(flat - flatDark)`.
- Output: `calibrated/{Target}/{Filter}/cal_{originalName}.fits`.
  Calibrated frames get a badge in the browser.

Detail: [STUDIO, light calibration](studio.md#light-calibration).

---

## 4. Integrate

In STUDIO, filter by the calibrated frames you just produced. Select
them all and click **Integrate**.

Pick:

- **Integration method**: Average for max SNR, Sigma-clipped average
  for outlier rejection (recommended for stacks ≥ 10), Median for
  noisy data.
- **Normalization**: Scale to mean is a safe default; turns off
  per-frame exposure variability.
- **Weighting**: per-frame HFR weight when seeing varied across the
  night.

Output: `integrated/{Target}/{Filter}/master_{Target}_{Filter}_{N}x{Exp}s.fits`
with `NCOMBINE`, `EXPTOTAL`, `INTMETH`, `REJECT` headers. The
result is your **master light**, the single frame you will polish
in the next two steps.

Detail: [STUDIO, batch integration](studio.md#batch-alignment-integration).

---

## 5. AI cleanup (GraXpert in the browser)

Open the master in **FILES** (or right-click in STUDIO and pick
**Open in FILES**). Polaris runs the GraXpert AI models locally in
your browser via ONNX, so this step costs zero server CPU and works
on a laptop, tablet or iPhone.

The recommended order is **BGE → Denoise → Decon**. Each step
writes a sibling FITS:

```
master_M31_L_120x180s.fits             ← integration output
master_M31_L_120x180s_bge.fits         ← after BGE
master_M31_L_120x180s_bge_denoise.fits ← after Denoise
master_M31_L_120x180s_bge_denoise_decon_objects.fits
```

### 5a. Background extraction (BGE)

Select the master, click **🌅 BGE** in the toolbar.

- Modal opens with **Run in browser** ticked.
- Smoothing default is fine for most light pollution; bump it up if
  your gradient is very smooth, down if you have local hotspots.
- Click **Start**. First run downloads ~200 MB of model weights to
  IndexedDB (one-time per browser). Inference takes 10 to 40
  seconds on a desktop GPU, longer on iPhone.

Output: `{stem}_bge.fits` next to the source.

### 5b. Denoise

Select the `_bge.fits` output, click **🔇 Denoise**.

- **AI model** dropdown lets you pick v2 (default, 284 MB) or v3
  (456 MB, sharper). On iPhone the FP16 variants auto-select to fit
  Safari's memory budget; see
  [ONNX inference, iPhone workflow](onnx-inference.md#running-on-iphone-ipad-the-fp16-workflow).
- **Strength** slider, 0 to 1. Start at 0.5 and adjust by comparing
  with the **Before / After** toggle after the run.
- Click **Start**. Tiles take a couple of minutes depending on
  master size and backend.

Output: `{stem}_bge_denoise.fits`.

### 5c. Deconvolution

Two choices, mutually exclusive (run one or the other, not both):

- **Decon Objects**, sharpens broad nebula structure
- **Decon Stars**, sharpens stellar PSFs without affecting nebulae

Select the `_bge_denoise.fits` output, click **✨ Decon**.

- **Method** dropdown: Objects or Stars.
- **Strength** slider, 0 to 1. 0.5 is a safe start.
- **FWHM**: read it off the source frame stats if you have them,
  otherwise leave the default.
- Click **Start**.

Output: `{stem}_bge_denoise_decon_{method}.fits`. The suffix
includes the method so running both produces distinct files.

Detail: [AI inference (ONNX)](onnx-inference.md).

> **Performance shortcut**: if you turned on **Auto-GraXpert BGE**
> in the AUTORUN end-actions, the `_bge.fits` siblings for every
> light frame are already on disk. You can run STUDIO's integrate
> on those directly and skip step 5a here.

---

## 6. Edit

The final creative pass happens in **EDITOR**.

### 6a. Open the AI-cleaned master

Two routes:

- **From STUDIO**: select the latest `_decon_*.fits` and click
  **🎨 Open in editor** in the selection bar.
- **From FILES**: select the file, click **🎨 Edit** in the
  toolbar.

The editor session opens with the source decoded, auto-stretched,
and your sliders at defaults. If you already saved a sidecar from a
previous editor session on this file, every slider hydrates to that
saved state, fully non-destructive.

### 6b. Light pass

The **Light** section is the first thing to touch. Recommended
order:

1. **Exposure** to set the overall brightness.
2. **Contrast** to add or remove punch.
3. **Highlights** (often negative) to recover star cores.
4. **Shadows** (often positive) to lift nebulae out of the
   background.
5. **Whites** and **Blacks** for the endpoints; whites just under
   clipping, blacks just clear of black.

Hold **👁 Compare** to flip back to the unedited reference, release
to come back.

### 6c. Color pass

Open **Color**:

1. **Temp** and **Tint** if the white balance is off (blue cast on
   broadband, magenta on narrowband mixes).
2. **Vibrance** to boost saturation in dim regions without
   blowing out star cores. This is usually preferable to
   **Saturation**, which scales everything.
3. **Hue** if you want a specific palette shift (Hubble-style on
   narrowband, for example).

### 6d. Effects pass

Open **Effects**:

- **Clarity** for broad-structure punch (mid radius, luminance
  only).
- **Texture** for fine-detail bite (small radius).
- **Dehaze** sparingly, this is an approximation, not a research
  algorithm.
- **Vignette** negative if your flats left a corner brightness
  gradient.

### 6e. Detail pass

Open **Detail**:

- **Sharpen** as a final polish, not a fix for soft focus.
- **Noise reduce** at low strength; you already denoised in GraXpert,
  this is just smoothing residual grain.

### 6f. Save the sidecar

Click **💾 Save edits**. Polaris writes
`{sourcePath}.edit.json` next to the source. Next time you open
this file in the editor, every slider comes back.

Detail: [EDITOR](editor.md), including the full pipeline order and
slider math.

---

## 7. Export

Click **↓ Export**. The export modal has:

- **Format**: JPEG (recommended for sharing), PNG (lossless), TIFF
  16-bit (for further editing in Photoshop/Affinity).
- **Quality**: JPEG quality 0 to 100. 90 is a sweet spot for web,
  100 for archive.
- **Resize**: pixel width / height, or percentage of source. Leave
  empty for full resolution.
- **Filename**: defaults to
  `{stem}__edited_{timestamp}.{ext}`, override if you want.

Click **Export**. The file lands in
`{sourceDir}/edited/{stem}__edited_{timestamp}.{ext}` and the FILES
index rescans automatically.

You can now download it through the FILES tab (right-click,
Download) or pull it off the host via your favourite mechanism
(SMB, scp, sync folder).

---

## Worked example: M31, one night

Times are wall-clock with a Raspberry Pi 5 host, ZWO ASI2600MC Pro
camera, EQ6-R Pro mount, ASKAR FRA600 scope, PHD2 on a 50 mm guide
scope.

| Step | Time | Output |
|---|---|---|
| 1. RIGS load, PHD2 connect, calibrate | 8 min | Guiding RMS 0.6" |
| 2a. Auto-focus | 3 min | HFR 1.8 px |
| 2b. PREVIEW snap, framing tweak | 2 min |, |
| 2c. AUTORUN start (120 × 180 s) | 6 hours | 120 lights on disk |
| 3a. Build masters (already on disk) | 1 min | bias, dark, flat masters |
| 3b. Calibrate 120 lights | 3 min | 120 cal_*.fits |
| 4. Integrate | 4 min | `master_M31_L_120x180s.fits` |
| 5a. GraXpert BGE in browser | 30 s | `_bge.fits` |
| 5b. GraXpert Denoise (FP32, desktop) | 90 s | `_bge_denoise.fits` |
| 5c. GraXpert Decon Objects | 2 min | `_bge_denoise_decon_objects.fits` |
| 6. EDITOR tone work | 10 min | `.edit.json` sidecar |
| 7. Export JPEG quality 92, 50% resize | 3 s | `…__edited_2026-05-26.jpg` |

The night itself is ~8 hours of clock time. Active human work is
under 30 minutes, the camera and Polaris handle the other 7.5.

---

## Troubleshooting and pitfalls

- **Integration produced a noisy master**: probably stacked
  uncalibrated lights. Re-check that STUDIO is filtering to the
  `cal_*.fits` outputs, not the raw `light_*.fits`.
- **BGE made it worse**: smoothing was too low, the AI fit local
  brightness as gradient. Bump smoothing toward 1.0 and re-run.
- **Decon Stars ringed the stars**: FWHM estimate is too small.
  Run STUDIO's star detector on the source to get a real FWHM
  number, then re-run Decon with that value.
- **Editor sliders feel laggy on Pi 2**: every slider tick is a
  server-side render. Drag in 200 ms increments instead of
  continuous scrubs. A future build will move the pipeline to
  WASM in-browser for instant feedback.
- **Export filename collision**: the timestamp suffix should
  prevent this; if you see it, you exported twice in the same
  second. Re-export.

For everything else, [troubleshooting](troubleshooting.md) and
[FAQ](faq.md).

---

## Next steps

You have shot, calibrated, stacked, AI-cleaned, edited, and
exported one target end to end. From here:

- For multi-target unattended nights, learn the
  [Advanced sequencer](adv-sequencer.md).
- For mosaics, see the [Sky explorer mosaic planner](sky-explorer.md).
- For planetary lucky imaging (Moon, Jupiter, Saturn), the
  [VIDEO tab](video-planetary.md) is a parallel workflow with its
  own capture, ranking and stack steps.
- For remote access from outside the LAN, set up the
  [Relay](relay.md).
