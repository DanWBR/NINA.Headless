# Mono workflow (RGB / LRGB / narrowband)

The end-to-end flow for the **mono camera + filter wheel** branch of the
Polaris workflow. If you shoot OSC (one-shot color), the
[end-to-end workflow guide](end-to-end-workflow.md) covers your path
already. This page is the missing piece for mono shooters: how to take
the per-filter masters that come out of STUDIO and combine them into a
single colour image, all without leaving Polaris.

> **L is optional.** Plenty of mono workflows are pure RGB (no
> separate luminance), and narrowband-only shooters skip RGB
> entirely. Use the **RGB** tab when you only have R/G/B masters;
> the **LRGB** tab when you also captured a luminance master and
> want its SNR to drive the lightness channel; the **PixelMath**
> tab for narrowband palettes or anything custom.

## What you need before starting

- A mono camera + filter wheel.
- At least 2 per-filter masters already produced by
  **STUDIO -> Integrate**, one `master_*.fits` per filter, all
  landing under `{rig}/integrated/{target}/{filter}/`. RGB sets need
  3 masters; LRGB needs 4; PixelMath needs as many as your
  expression references (minimum 2). If you have not done the
  per-filter integration yet, follow the
  [end-to-end guide](end-to-end-workflow.md) up to step 4 first.
- The masters should be stretched to comparable brightness ranges.
  In practice that means: either run them all linear (raw stack
  output), or pre-stretch each one in STUDIO with the same
  parameters. Polaris trusts the planes are comparable; if one is
  wildly brighter than the others you get a brightness-shifted
  output.

A typical mono session can land any of these layouts on disk:

```
# Plain RGB (no luminance), common for fast or short sessions
{rig}/integrated/M81/
  R/master_R_30x180s.fits
  G/master_G_30x180s.fits
  B/master_B_30x180s.fits

# Classic LRGB (luminance master adds SNR)
{rig}/integrated/M81/
  L/master_L_30x180s.fits          (extra L pass for noise control)
  R/master_R_15x180s.fits
  G/master_G_15x180s.fits
  B/master_B_15x180s.fits

# Narrowband only (no RGB at all)
{rig}/integrated/NGC7000/
  Ha/master_Ha_20x300s.fits
  OIII/master_OIII_15x300s.fits
  SII/master_SII_10x300s.fits      (optional, for SHO palette)
```

## Step 1: Open the Combine dialog

In **STUDIO**, multi-select the per-filter masters you want to use
(Ctrl-click each one in the frame grid). The selection bar at the
bottom lights up. Click **Combine**.

The modal opens on the **RGB** tab. The selection bar shows the
frame count so you can verify you picked the right ones.

## Step 2: Pick a combine mode

Three tabs, three modes:

### RGB

The default and the one most mono shooters use. Three mono masters
(R/G/B), one RGB output. No luminance master required, this is the
right tab when you only captured RGB filters or when you don't want
to spend extra session time on a separate L pass.

Polaris auto-fills the R/G/B slots from each frame's `FILTER`
header. If your filter names don't match (e.g. you set
`FILTER=Red`), pick them manually from the dropdowns.

Output:
`{rig}/integrated/{target}/composed/rgb_{target}_{stamp}.fits`

### LRGB

For shooters who **also** captured a luminance master. Four masters
(R/G/B/L), one RGB output that takes its lightness from L. **L is
where the SNR lives** because it is captured through the broadest
filter, so this is what makes LRGB look better than plain RGB,
when you have it.

If you don't have an L master, use the **RGB** tab instead, that
gives you the same R/G/B compose without forcing an L slot.

Pick an algorithm:

- **Lab swap** (default, recommended). Converts the RGB stack to CIE
  Lab, replaces the lightness channel with the L master, converts
  back. Best chrominance preservation; matches PixInsight's
  LRGBCombination output.
- **Ratio** (classical, faster). Per-pixel multiplies R/G/B by
  `L / luminance(RGB)`. Slightly cheaper, can shift saturation if
  the ratio swings far from 1.

Output:
`{rig}/integrated/{target}/composed/lrgb_{target}_{stamp}.fits`

### PixelMath

For when you want to do something the RGB and LRGB modes can't:
narrowband palettes, synthetic luminance, channel arithmetic.

Each row in **Variable bindings** maps a selected frame to a name
you choose. The name defaults to the filter header (so Ha + OIII
masters auto-populate as `Ha` and `OIII`).

Each **Output expression** is a formula using those variable names.
For RGB output you write three (one per output channel R/G/B); tick
**Mono output** if you only need one expression and a single-channel
FITS.

Supported syntax:
- Numbers: `0.5`, `1.2e3`
- Operators: `+`, `-`, `*`, `/`, `**` (power, right-associative)
- Parens: `(R + G) * 0.5`
- Functions: `min`, `max`, `abs`, `pow`, `sqrt`, `exp`, `log`,
  `clamp(value, min, max)`

Common recipes:

| What you want | Variable bindings | Expressions |
|---|---|---|
| HOO bicolor (Ha + OIII) | `Ha`, `OIII` | R = `Ha`, G = `0.5*Ha + 0.5*OIII`, B = `OIII` |
| SHO Hubble palette | `Ha`, `OIII`, `SII` | R = `SII`, G = `Ha`, B = `OIII` |
| Synthetic luminance from RGB | `R`, `G`, `B` | (Mono out) L = `(R + G + B) / 3` |
| Ha-boosted R for LRGB | `R`, `Ha` | R = `0.7*R + 0.3*Ha` (then re-combine as L+R'+G+B) |

Typos in variable names surface immediately in the modal as a job
error before any pixel I/O. Divide-by-zero falls back to 0 (no NaN
propagation through the integration).

Output:
`{rig}/integrated/{target}/composed/pm_{target}_{stamp}.fits`

## Step 3: Cross-channel registration (default on, leave it on)

The **Register channels** checkbox at the bottom of the modal is
**on by default**. This is what saves you from coloured fringes on
every star.

The reason: each per-filter master comes out of `BatchStackingService`
aligned to its own reference frame (the best-HFR frame within that
filter's stack). So master_R is aligned to its own best R sub, and
master_G to its own best G sub. They are not aligned to each other,
even when shot in the same session, because the references are
different frames captured under slightly different seeing.

Without registration, you get:
- Coloured halos around bright stars (R offset by 1-3 px from G/B)
- Ghosting of fine nebula structure
- General "soft" appearance

Polaris detects stars on each input (SigmaThreshold=7 because masters
have higher SNR than single subs, so the default 5 sigma would
flood-detect halos), matches against the reference channel (L if
LRGB, first input otherwise), computes an affine transform per
channel, and resamples. The transforms are recorded in the output
FITS headers (REGREF, REG_R, REG_G, REG_B) for reproducibility.

You can turn registration **off** if you know the masters are already
aligned (permanent pier observatory with no re-framing between
filters, same reference frame across stacks). For backyard or
mobile rigs, leave it on.

## Step 4: Normalize per-channel (also on by default)

Different filters captured in different sessions can have very
different sky backgrounds. The **Normalize per-channel** checkbox
scales each input so all their medians line up before the combine.
This keeps a bright-background R from making the output look
red-cast just because R was shot under a brighter sky.

Turn this **off** if you're doing PixelMath and want the raw
channel values to feed straight into your expression (e.g. you
computed your own pre-scale factors).

## Step 5: Run

Click **Combine**. Progress steps through `loading -> registering ->
normalizing -> composing -> writing -> done`. Time depends on input
size and how many channels need registration:

- 2 channels, register off: ~5s on a Pi 5
- 4 channels (LRGB), register on: ~25s on a Pi 5 (registration
  adds ~15s for star detect + match across all non-reference
  channels)
- 6 channels (LRGB+narrowband), register on: ~40s

A toast confirms the output path on completion. The new file shows
up in the STUDIO frame browser after the auto-rescan with a 3-channel
badge (or 1-channel for PixelMath mono output).

## Step 6: Continue with AI cleanup and editor

The composed RGB/LRGB FITS is just a frame; everything downstream
treats it like any other master:

1. **GraXpert AI cleanup** (BGE -> Denoise -> Decon) in the FILES tab.
   The pipelines auto-iterate per RGB plane, so the AI works on the
   composed file the same way it works on OSC RGB.
2. **Editor** (Lightroom-style tone work). The editor respects FITS
   plane count, so RGB composed files open as 3-channel sessions
   with the Color sliders (Temp, Tint, Vibrance, Saturation, Hue)
   active.
3. **Export** as JPG / PNG / TIFF 16-bit.

The rest of the workflow is identical to the OSC path in the
[end-to-end workflow guide](end-to-end-workflow.md) from step 6
onward.

## Worked example: M81 pure RGB

The simpler path, what most backyard mono shooters do. Pi 5 host,
ZWO ASI2600MM Pro, EQ6-R Pro, ASKAR FRA600, RGB filter set, no
luminance pass.

| Step | Time | Output |
|---|---|---|
| Capture: 20R + 20G + 20B at 180s each (3 hours unattended) | 3 h | 60 raw lights |
| STUDIO: calibrate + integrate per filter | 8 min | 3 per-filter masters |
| Combine: RGB tab, Register on, Normalize on | 15 s | `rgb_M81_*.fits` |
| FILES -> GraXpert BGE | 30 s | `_bge.fits` |
| FILES -> GraXpert Denoise (v2 FP32) | 90 s | `_denoise.fits` |
| Editor tone work | 8 min | sidecar saved |
| Export JPG quality 92 | 3 s | final image |

Active human work: ~20 minutes. The rest is camera + Polaris time.

## Worked example: M81 LRGB + Ha boost

For shooters who add a separate luminance pass + narrowband boost.
Same hardware as above, plus an L filter and Ha narrowband.

| Step | Time | Output |
|---|---|---|
| Capture: 30L + 15R + 15G + 15B + 20Ha (8 hours unattended) | 8 h | 95 raw lights |
| STUDIO: calibrate + integrate per filter | 12 min | 5 per-filter masters |
| Combine: LRGB tab, Lab swap, register on | 25 s | `lrgb_M81_*.fits` |
| FILES -> GraXpert BGE | 30 s | `_bge.fits` |
| FILES -> GraXpert Denoise (v2 FP32) | 90 s | `_denoise.fits` |
| FILES -> GraXpert Decon Objects | 2 min | `_decon_objects.fits` |
| Editor tone work | 10 min | sidecar saved |
| (Optional) Re-open the L+R+G+B masters and rerun the combine | | |
| with PixelMath: R = `0.7*R + 0.3*Ha`, G = `G`, B = `B` | 25 s | `pm_M81_*.fits` |
| Then re-combine pm output as RGB + L master, LRGB tab | 25 s | Ha-boosted LRGB |
| Export JPG quality 92, 50% resize | 3 s | final image |

The Ha boost step is optional; many users skip it and run straight
LRGB. But the PixelMath round-trip is the kind of thing that used
to mean exporting to PixInsight, doing the math there, then loading
back into Polaris for the editor. Now it stays in one app.

## Common pitfalls

- **Output has coloured star halos**: registration was off. Re-run
  with Register channels checked.
- **Output is much brighter or dimmer than expected**: L and RGB
  brightness ranges drift apart. Re-stretch each master in STUDIO
  with the same parameters before combining, or use the Ratio
  algorithm which is less sensitive to absolute brightness.
- **"Could not register channel X" error**: that filter's master has
  too few detectable stars (very short total exposure, very narrow
  filter, bad framing). Either increase that channel's total
  exposure, OR turn Register off if you trust pre-existing
  alignment.
- **PixelMath says "unknown variable Ha2"**: typo in the expression.
  The variable names come from the **Variable bindings** rows at the
  top of the PixelMath tab; the expression must use those exact
  names (case-sensitive).
- **Combine button stays disabled**: not enough channels assigned.
  RGB needs all 3 slots filled, LRGB needs all 4, PixelMath needs
  at least 2 binding rows AND each output expression non-empty.

## What's under the hood

The combine button POSTs to `/api/studio/combine` with a JSON
description of the request. The server runs
`ChannelCombineService`, which dispatches to either:

- `RgbCompose` mode (3 mono FITS -> packed RGB),
- `LrgbCombiner.Combine(...)` with `LabSwap` or `Ratio` (CC-2),
- `PixelMathEvaluator.Compile(...)` per output expression (CC-3),
  evaluated per pixel.

Output FITS headers record the recipe: `CHCOMBINE` (mode),
`REGISTER` (T/F), `REGREF` (reference channel for registration),
`REG_R`/`REG_G`/`REG_B`/`REG_L` (affine transforms applied per
channel as `M00,M01,M10,M11,Tx,Ty`), `NORMLIZE` (T/F), `NCHANNEL`
(input count). PixInsight's FITS Header view shows all of these so
you can audit exactly what happened to produce any composed file.

## Out of scope (today)

- **Star removal layers** (run combine on starless + star RGB layers,
  PixInsight-style). Use GraXpert decon-stars instead.
- **SHO Hubble palette wizard**. PixelMath gives you the recipe; a
  one-click preset is a follow-up.
- **Drizzle integration before combine** (super-resolution from
  many short subs). Out of scope; you can do this in
  Siril/PixInsight then bring the master back into Polaris.

## See also

- [End-to-end workflow](end-to-end-workflow.md), full pipeline for
  OSC + the entry point that links to this page from step 4.
- [STUDIO guide](studio.md), the full post-processing reference.
- [AI inference (ONNX)](onnx-inference.md), GraXpert AI cleanup on
  the composed output.
- [Editor guide](editor.md), Lightroom-style finishing on the
  composed image.
