# FOCUS tab

Manual focuser control + Manual Assist loop (HFR trend + Bahtinov
mask) + automated V-curve focusing with live frame preview.

The tab has two subtabs:

- **Manual Assist** (default): a loop-based focus aid that captures
  a frame every N seconds, plots HFR vs time, and (optionally) runs
  Bahtinov mask analysis with an on-canvas overlay. Works **without
  an electronic focuser**, so it covers manual / Crayford rigs and
  doubles as a fine-tune aid for motorised rigs after a V-curve.
- **Auto V-curve**: the classic automated sweep. Requires an
  electronic focuser; disabled when none is connected.

The motor stepper (manual position control) lives **above** the
subtabs whenever an electronic focuser is connected, so you can drive
the focuser by hand at any time regardless of which subtab is active.

## Manual stepper

The big stepper at the top of the tab:

```
[<<]  [<]  [12345]  [>]  [>>]
```

- `<` / `>` move 1 step in / out
- `<<` / `>>` move `Step Size` steps in one go
- The middle number is the current focuser position; click to type a
  direct value

**Step Size** slider sets the `<<` / `>>` jump distance (1 to 500
steps, persisted per rig).

Info panel below shows:
- **Temperature**, focuser-reported temp (°C) when supported. Drives
  the LIVE tab's auto-refocus ΔT trigger.
- **Status: MOVING**, flashes while the focuser is in motion. Inputs
  disable to prevent stacked commands.

## Manual Assist subtab

The Manual Assist subtab runs a client-driven capture loop and
streams the result back as live focus telemetry. Designed for two
audiences:

1. **No electronic focuser**: you turn the knob by hand, the loop
   tells you whether HFR is dropping (focus improving) or rising
   (you overshot).
2. **Have an electronic focuser, want fine-tune**: after V-curve
   completes, switch to Manual Assist for a final visual
   confirmation or to track focus drift over a long session.

### Layout

```
┌─ Live preview canvas (last captured frame) ┐  ┌─ Controls ─┐
│ + Bahtinov overlay when enabled            │  │ Exp (s)    │
│                                            │  │ Gain       │
│                                            │  │ Interval s │
│                                            │  │ Min stars  │
│                                            │  │ ▶ Start    │
│                                            │  │ ↻ Snap     │
│                                            │  │ Reset      │
│                                            │  │ HFR / FWHM │
│                                            │  │ Stars      │
│                                            │  │ Laplacian  │
│                                            │  │ Best HFR   │
│                                            │  │ ☐ Bahtinov │
│                                            │  │ Offset px  │
└────────────────────────────────────────────┘  └────────────┘
┌─ HFR trend chart (last 60 samples, ~2 min @ 2s) ────────────┐
│   HFR vs time, with dashed best-HFR baseline                │
└──────────────────────────────────────────────────────────────┘
```

### Parameters

- **Exposure (s)**: same exposure used for every loop tick. 2-3s
  for a dim DSO frame, 0.5-1s for a bright Bahtinov star.
- **Gain**: camera gain, same as PREVIEW tab.
- **Interval (s)**: gap between captures, 1-5s. Default 2s gives
  enough time to turn the focuser knob and see the result.
- **Min stars**: HFR is ignored when fewer stars are detected,
  the bad sample doesn't poison the chart or `Best HFR`. Default
  3 for typical fields, bump to 10+ for Bahtinov-on-single-star.

### Buttons

- **▶ Start loop / ⏸ Stop**: starts the per-interval capture loop.
  Each capture goes through the standard image pipeline so it also
  feeds the activity bar + image stream.
- **↻ Snap once**: capture a single frame without starting the
  loop. Useful for a one-off sample.
- **Reset baseline**: zeros the sample buffer, the best-HFR
  marker, and the Bahtinov overlay. Use it after each manual
  adjustment so you see the trend relative to the new position
  instead of the old session.

### Live metrics

After each capture the sidebar updates:

- **HFR (px)**: median half-flux radius across detected stars.
  Lower = sharper. Colour-coded green/red based on the local
  trend (down vs up).
- **FWHM (px)**: derived as `HFR × 2.355` (gaussian approximation;
  per-star gaussian fit is out of scope here).
- **Stars**: how many stars were detected; sanity check that
  exposure / gain are right.
- **Laplacian**: variance of the Laplacian over the centre 256 px
  ROI. Secondary sharpness metric that works on starless fields
  (lunar surface, single bright star) where HFR is meaningless.
- **Best HFR**: lowest HFR seen since the last Reset. The chart
  draws a horizontal dashed line at this value so you see
  immediately when you've overshot focus and started getting
  worse.

### HFR trend chart

A 60-sample scrolling time series, X axis is seconds back from
now (right edge = newest sample). The dashed line is `Best HFR`.

Workflow:

1. Click ▶ Start loop with Exp=2s, Interval=2s.
2. Watch HFR plateau, that's the focus you start with.
3. Click Reset baseline so the chart starts fresh.
4. Turn the focuser knob a small amount in one direction.
5. After 2-3 ticks (~5s), see whether HFR went **down** (good,
   keep turning the same way) or **up** (wrong way, reverse).
6. Repeat until HFR plateaus at a new minimum lower than before.
7. Make smaller adjustments as you get close to the bottom of
   the V.

### Bahtinov mask analysis (optional)

A **Bahtinov mask** is a physical diffraction grating you place
over the front of the telescope. Pointing at a bright star, it
turns the star's airy disk into 3 spikes forming a V plus a
central crossbar. When focused, the central spike passes exactly
through the V's intersection. Defocused, the central spike sits
off the intersection by an amount proportional to focus error.

Polaris analyses Bahtinov masks automatically:

1. Install a Bahtinov mask on the front of your scope.
2. Slew to a bright star (magnitude < 3, e.g. Vega, Sirius,
   Polaris, Capella). The brighter the better, but not so bright
   that the centre saturates.
3. In Manual Assist, set Exposure to 0.5-2s and start the loop.
4. Tick **☐ Bahtinov mask analysis** in the sidebar.

After each capture, Polaris:

1. Finds the brightest star in the frame (or accepts a manual
   `starX` / `starY` if you POST one to `/api/focus/bahtinov`).
2. Crops a 200 px ROI around it.
3. Sweeps through every angle 0-180° in 0.5° steps, integrating
   intensity along each line through the ROI centre.
4. Picks the 3 strongest peaks (with a 30° minimum separation
   so it doesn't double-count a single fat spike).
5. Refines each line's perpendicular offset (ρ) within ±20 px.
6. Identifies the central spike (the one whose angle is closest
   to the bisector of the other two).
7. Computes the perpendicular distance from the central spike
   to the V's intersection: that's the focus error in pixels.

The overlay canvas draws:

- A cross marker at the picked star.
- The 3 spike lines extended across the canvas. The central
  spike is highlighted in the offset colour.
- A circle at the V's intersection (target for the central
  spike when in focus).
- An "Bahtinov offset: ±N.NN px" label in the top-left.

The sidebar shows the offset with a direction cue:

- **✓ In focus** when `|offset| ≤ 0.5 px`.
- **Near focus, fine-tune** when `|offset| ≤ 1.5 px`.
- **Rotate inward / outward** for larger offsets. The actual
  physical direction depends on your focuser orientation, learn
  it once for your rig (watch the sign change as you turn the
  knob, then map "inward" to whichever direction you turn).

Colour coding: green when in focus, amber when fine-tuning,
red when far off.

### Common Manual Assist pitfalls

**HFR doesn't move when I turn the knob**: the loop interval is
longer than you think. Default 2s plus exposure time means a
full reading takes 3-4s. Wait for at least 3 ticks before
deciding the adjustment did nothing.

**Bahtinov analysis says "could not detect 3 spikes"**: the
target star is too faint, too low in the sky, or the mask isn't
seated cleanly on the front of the tube. Try a brighter star or
re-check the mask alignment.

**Offset value oscillates wildly between ticks**: seeing is too
poor for sub-pixel Bahtinov. The mask still works as a coarse
indicator, just trust the visual overlay (does the central
spike sit on the intersection?) over the numeric value.

**No camera capture, "Start loop disabled"**: connect a camera
in the RIGS tab first.

## Auto-Focus (V-curve)

Click **▶ Start AF** to run a symmetric sweep around the current
position. Polaris:

1. Builds a list of N positions: `current ± (Steps/2) × StepSize`
2. (Optional) Applies backlash compensation by overshooting in one
   direction
3. At each position:
   - Moves the focuser + waits for settle
   - Captures an exposure (`Exposure (s)`)
   - Detects stars + computes median HFR (`Min Stars` floor, frames
     with fewer stars are dropped)
4. Fits a parabola through valid (position, HFR) samples
5. Moves to the parabola's vertex (best focus)
6. Reverts to start position on cancel / failure

### Parameters

- **Steps** (3-25, odd), how many sample positions in the sweep.
  9 is a good default; 5 if you're already near focus + want speed;
  15 if you're far off.
- **Step Size**, same units as the manual stepper. Should be small
  enough that the V-curve has clear shape (not all samples at the
  bottom, not all at the top). Typical: 50-200 steps for SCT, 20-80
  for refractor.
- **Exposure (s)**, long enough to register stars in the field. 3s
  is fine for most DSO setups; planetary uses 50-500ms.
- **Min Stars**, minimum stars required for a valid HFR sample. 5
  is sane; bump to 20 for crowded fields where HFR is noisy.
- **Backlash**, overshoot in steps when reversing direction. 0 for
  belt-driven focusers; use your focuser's published backlash for
  geared ones.

### Live progress

While AF is running:

- **Progress bar** + sample counter `8 / 9`
- **Last HFR + star count** update each sample
- **V-curve chart** appears bottom of panel, plotting (position, HFR)
  with the fitted parabola overlaid + best-position marker
- **Live frame preview canvas** shows the actual frame Polaris just
  captured at each sample, with a HUD chip displaying
  `pos {N} · HFR {x.xx} · ★ {stars}`

The preview canvas pipes through the same `/ws/image-stream` channel
LIVE + PREVIEW use, so you see the focus visually converging as the
focuser steps.

### Abort

- **Abort AF**, cancels the sweep; restores the starting focuser
  position
- **Stop Focuser**, only enabled while the focuser is moving (not
  during AF); emergency stop for a runaway manual command

## Auto-focus triggers (advanced)

AF doesn't just run on demand, it can be **automatically triggered**
in two places:

1. **Sequence engine** (AUTORUN tab): trigger AF at every N frames /
   on temperature change / on HFR degradation / on filter change.
   Configure under the sequence's Triggers panel.
2. **Live stacking** (LIVE tab): same four trigger types, evaluated
   per integrated frame. Captures pause naturally while AF runs (see
   [LIVE auto re-focus](live-stacking.md)).

## Common pitfalls

**HFR comes back as 0**, no stars detected. Increase exposure, check
that you're actually pointed at the sky + not a flat-grey panel.

**V-curve has no clear minimum**, Step Size too small (all samples
clustered at bottom) or too large (all in noise). Try doubling /
halving Step Size and retry.

**AF moves to a wildly wrong position**, parabola fit was poisoned by
outliers. Polaris validates "best position within ±2 × StepSize × N/2
of start"; outside that it warns. Use Backlash > 0 if your focuser has
hysteresis; bump Min Stars to drop noisy samples.

**Focuser stops moving mid-sweep**, driver lost connection. Check
INDI logs; reconnect the focuser; restart AF.

## See also

- [LIVE auto re-focus triggers](live-stacking.md#auto-re-focus--re-center)
- [AUTORUN sequence triggers](autorun.md)
- [Glossary → HFR / V-curve](GLOSSARY.md#h)
