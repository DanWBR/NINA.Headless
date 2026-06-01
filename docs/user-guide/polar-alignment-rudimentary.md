# Rudimentary polar alignment (single-target iterative)

Polaris ships two polar-alignment workflows. **TPPA** (Three-Point
Polar Alignment) is the default and what most setups should use — it
sweeps the mount through 30° of RA and fits the mount's polar axis
from three plate-solved points.

**Rudimentary alignment** is the second option, designed for setups
where TPPA doesn't work:

- Balconies / urban observatories where the polar region is blocked
- Mounts that can't tracking-slew freely in RA without obstruction
- Setups where you've already aligned roughly with a compass + tilt
  app on your phone and just want a one-target sanity check
- Manual mounts (no GoTo) — you point at the target by hand, Polaris
  just captures and solves

## How it works

The workflow mirrors what experienced ASIAIR operators already do by
hand:

1. **Coarse-align physically.** Use a magnetic-declination calculator
   (e.g. NOAA's online tool) plus a tilt app on your smartphone. Align
   the mount as close to true polar as you can eyeball it. This part
   is outside Polaris — five minutes with the phone gets you within
   a degree or two.

2. **Pick a known, visible target.** Anything bright (Sirius, Vega,
   M42, etc) above the horizon. Visible to the naked eye helps — the
   plate solver still works fine on faint targets, but you want to
   know roughly where the camera is pointing.

3. **Polaris slews and solves.** Click **Start** with "Slew to target"
   selected. Polaris commands the mount to GoTo the chosen coords,
   waits to settle, captures one frame, and runs plate-solve.

4. **Read the error.** Polaris reports:
   - The pointing the mount actually achieved (RA, Dec)
   - Azimuth error and altitude error in arcsec / arcmin
   - Total error magnitude, colour-coded (green < 1', amber 1–5',
     red > 5')
   - A green dot (where the target should be) and a red dot (where
     the mount ended up) on the embedded sky map

5. **Adjust the mount.** Walk over and nudge the azimuth or altitude
   knob a little, in the direction the error indicates. Sign convention:
   - **Positive azError** → mount is pointing east of where it should
     be → turn azimuth knob **westward**.
   - **Positive altError** → mount is pointing above the target →
     turn altitude knob **down**.

6. **Click "Re-capture + solve".** Polaris captures another frame at
   the same mount position (no slew this time) and re-computes the
   error. The convergence sparkline below the result block shows the
   trend across iterations — bars get shorter and greener as you
   converge.

7. **Repeat until satisfied.** Unlike TPPA, there's no auto-stop —
   you decide when "good enough" is good enough. Most operators settle
   at 30–60" total error for visual / wide-field; serious deep-sky
   imaging targets < 30".

## Why the math works (despite being an approximation)

A single plate-solved frame can't tell you what's polar misalignment
vs what's mount pointing-model error (cone, non-orthogonality, etc).
Rudimentary attributes the entire pointing offset to polar misalignment,
which is mathematically wrong — but works iteratively:

- After 1–2 manual knob nudges, the polar component dominates the
  **change** between iterations
- The mount-model component is constant and vanishes from the visible
  arrow once you're a couple of iterations in

This is the same approximation SharpCap's "Plate-Solve Polar
Alignment" and KStars' single-target mode use, and the algorithm
ASIAIR / N.I.N.A. operators already run by hand.

## Pre-flight requirements

Before starting, the pre-flight strip at the top of the sub-pane
shows ✓ / ✗ for:

- **Camera connected** — required (the workflow captures a frame)
- **Mount connected** — required for "Slew to target" mode, optional
  for "Use current position" mode (manual mounts)
- **Site location** — required (the math needs lat/lon to convert
  RA/Dec to local alt/az). Set in **Settings → Site location**.

A plate solver also needs to be configured. ASTAP works without any
extra setup; PlateSolve3 and online astrometry.net need credentials
or paths. See `docs/user-guide/rigs.md` for solver setup.

## When to use Rudimentary vs TPPA

| Situation | Use |
|---|---|
| Full view of polar region, can slew 30° in RA freely | **TPPA** (more rigorous, fewer iterations) |
| Balcony / blocked polar view / limited slew range | **Rudimentary** |
| Manual mount (no GoTo) | **Rudimentary** with "Use current position" |
| First-night setup after assembly | **Rudimentary** (faster initial check, follow up with TPPA if you have time) |
| Permanent observatory after periodic check | **TPPA** (cleaner result) |

## Known limitations

- **Plate solve must succeed.** If the frame has too few stars
  (overcast, fogged optics, very short exposure) the solve fails
  and Polaris asks you to bump exposure / gain.
- **Single-target math drifts near the zenith.** Azimuth gets
  amplified by cos(altitude); Polaris already compensates for this
  in the reported number, but the underlying physics still mean
  alignment near the zenith is less informative than alignment
  closer to the celestial equator.
- **No auto-convergence threshold.** Some users want "stop when
  total < 30 arcsec." Today this is left manual — the sparkline
  + colour coding is enough signal for most operators. If demand
  exists, a configurable threshold per rig is a future addition.

## See also

- `docs/user-guide/end-to-end-workflow.md` — full session walkthrough
- `docs/user-guide/rigs.md` — plate-solver setup
