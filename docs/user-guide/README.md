# N.I.N.A. Polaris, User Guide

Welcome. N.I.N.A. Polaris is a browser-controlled astrophotography
controller running on a Raspberry Pi, mini-PC, or any small Linux/Windows
host on your network. You point any device (laptop, tablet, phone) at
the host and get the full equipment + capture + processing workflow
without a desktop.

This guide is split into **flows** (do this to accomplish X) and
**references** (this is what every control does).

## New to Polaris? Start here

1. **[Installation](installation.md)**, get the server running on your
   Pi / mini-PC / Windows box. ~10 minutes.
2. **[First-night setup](first-night.md)**, connect your gear, run
   your first sequence. ~30 minutes. Walks through INDI/Alpaca,
   creating a rig, hooking PHD2, slew-and-center, and triggering a
   ten-frame test sequence on Vega or similar bright reference target.
3. **[End-to-end workflow](end-to-end-workflow.md)**, the full path
   from "scope on the tripod" to "JPEG exported": setup, acquire,
   calibrate, integrate, GraXpert AI cleanup (BGE / Denoise / Decon),
   editor pass, export. Stitches every Polaris feature into one
   continuous night with a worked-example time budget.
4. **[Mono workflow (RGB / LRGB / narrowband)](lrgb-mono-workflow.md)**,
   the mono-camera branch of the end-to-end pipeline: combine
   per-filter masters into one RGB FITS via the STUDIO Combine tool.
   Covers plain RGB (no luminance needed), LRGB when you do have a
   luminance master, and PixelMath for narrowband palettes like HOO
   and SHO. Then continues through GraXpert and the editor exactly
   like the OSC path.

## By feature

Each tab in the sidebar has its own page. Read the ones you need:

- **[RIGS](rigs.md)**, multi-rig equipment management. INDI/Alpaca/vendor
  drivers, per-role cards (Main Telescope, Camera, Mount, Focuser, Filter
  Wheel, Guidescope, Guide Camera, Rotator, Flat Panel, Dome, Weather).
- **[GUIDE (PHD2)](guide-phd2.md)**, full PHD2 integration. Connection
  + management + smart calibration + algorithm presets + embedded xpra
  GUI (Linux only).
- **[FOCUS](focus.md)**, manual stepper + V-curve auto-focus with live
  preview canvas.
- **[PREVIEW](preview.md)**, snap test shots with optional save +
  continuous stream mode.
- **[AUTORUN](autorun.md)**, simple sequence editor with end-actions.
- **[ADV](adv-sequencer.md)**, advanced tree-based sequencer (NINA-style).
- **[LIVE](live-stacking.md)**, real-time EAA stacking with auto
  re-focus / re-center triggers. See also
  [client-side compute](client-side-compute.md) for the WASM offload
  that lets a slow server (Pi 2/3) do the math in your browser instead.
- **[VIDEO](video-planetary.md)**, planetary capture + lucky-imaging
  stack pipeline (SER format).
- **[SKY](sky-explorer.md)**, offline sky map, target search, mosaic
  planner, tonight's best altitudes.
- **[STUDIO](studio.md)**, post-processing: frame browser, master
  generation, calibration, batch stacking, debayer + BGE + NR + sharpen.
- **[EDITOR](editor.md)**, Lightroom-style non-destructive editor with
  sliders for Light / Color / Effects / Detail + JPEG/PNG/TIFF export
  with quality + resize. Sidecar JSON (`.edit.json`) preserves your
  edits next to the source file.
- **[AI inference (ONNX)](onnx-inference.md)**, GraXpert AI models
  (BGE / Denoise / Decon) running directly in the browser via
  onnxruntime-web. Server hosts the `.onnx` files; any device with
  WebGPU or WASM SIMD does the heavy lifting locally.
- **[FILES](files.md)**, server-side file explorer.
- **[HTTPS setup](https-setup.md)**, self-signed cert on port 5001,
  required for WebGPU + multi-thread WASM on LAN client devices
  (any device other than the host itself). One-time per-device cert
  trust click in the browser; after that, in-browser GraXpert AI
  pipelines use the client's GPU instead of falling back to slow
  CPU-only inference.
- **[Relay (remote access)](relay.md)**, TLS-tunneled access from
  outside your LAN.
- **[Remote terminal](remote-terminal.md)**, embedded SSH terminal
  (xterm.js + SSH.NET) in SETTINGS. Restart services on a headless Pi
  from the browser, without plugging in a screen.

## Reference

- **[Glossary](GLOSSARY.md)**, every astrophotography term used in this
  guide explained in two lines. Read in any order.
- **[Requirements](../../REQUIREMENTS.md)**, full Windows + Linux (RPi)
  tooling matrix, what's required vs optional per feature, firewall
  rules, hardware sizing.
- **[Troubleshooting](troubleshooting.md)**, common problems + fixes.
- **[FAQ](faq.md)**, questions that come up a lot.

## For developers

- **[Debug on Raspberry Pi from Visual Studio](rpi-debug-from-vs.md)**,
  SSH remote debug setup (full breakpoints + step-debug from VS on
  Windows over the network), plus simpler publish + run + hot-reload
  workflows.
- **[Equipment simulator mode](simulator-mode.md)**, built-in fake
  telescope + camera + focuser + filter wheel for testing the whole
  pipeline without real hardware. Renders real stars at the simulated
  mount position so plate solve, auto-focus, and live stacking all
  actually work.

## A note on screenshots

Screenshots in this guide use placeholder paths like
`![](screenshots/rigs-tab-overview.png)`. They land as broken images
in the rendered Markdown, that's expected. They'll be filled in over
time as we capture good ones from real sessions. If you want to help,
the placeholder filenames are descriptive enough that you can match
them to your own captures and open a PR.

## A note on audience

This guide is written for people who have shot astrophotography
before but are new to Polaris. Concepts like HFR, dither, plate solve,
bias / dark / flat get a quick line in context and a deeper entry in
the [glossary](GLOSSARY.md), newcomers should keep that tab open
while reading.

For complete beginners to astrophotography, supplement with the
[Photographing the Universe playlist](https://www.youtube.com/@AstroBackyard)
or any of the standard intro books. We focus on Polaris-specific
workflow here, not the underlying technique.
