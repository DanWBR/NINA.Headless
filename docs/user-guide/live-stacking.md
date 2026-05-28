# LIVE tab (live stacking + EAA)

Real-time integration of incoming frames into a single growing image.
For Electronically Assisted Astronomy (EAA), comet hunting, or just
watching your DSO target build up while you have a beer.

> **Where the math runs**: by default the server (Pi / mini-PC) does
> the stacking. On underpowered hosts (Pi 2/3) you can flip the
> Compute dropdown to client-side WASM offload so the browser owns
> the accumulator, see [client-side compute](client-side-compute.md).

## How it works

Polaris doesn't drive the capture from the LIVE tab, it **subscribes**
to frames arriving from anywhere (sequence engine, PREVIEW snap, camera
stream). When **Stack ON**, each incoming frame:

1. Has its stars detected
2. Is aligned (affine transform) against the reference (first frame's
   star pattern)
3. Accumulates into a running-mean buffer
4. Re-renders the stacked image to the canvas

Frame count + reference star count update each second.

## Controls (top row)

- **Capture / Loop / Stop**, same as PREVIEW; convenience to fire
  captures without leaving the tab
- **Stack ON / Stack**, toggle integration on/off
- **Reset** (visible when Stack ON), discards the running stack +
  reference. Next incoming frame becomes the new reference.
- **⛶ View**, OpenSeadragon viewer on the current stack

## Stats bar

- **Stars**, count in latest frame
- **HFR**, median HFR of latest frame (lower = sharper)
- **Mean / Median / StDev / Min / Max**, pixel stats of the stack
- **HFR + Star count history chart**, last N frames trend

## Auto re-focus / re-center

The big feature: long sessions degrade as focus drifts with temperature
and mount drift accumulates. Polaris fires AF + re-center automatically
on configurable triggers, without you intervening.

Click the **⚡ Auto re-focus / re-center** `<details>` panel to expand.

### Auto re-focus

Enable + pick any combination of triggers (first to cross fires):

- **Every N integrated frames**, pure frame counter
- **Every N minutes**, wall-clock elapsed
- **ΔT ≥ X°C**, sensor temperature drift since last AF
- **HFR ≥ Y% above last**, when current HFR degrades by Y% vs the
  HFR right after the last successful AF

Plus AF sweep config (steps, step size, exposure) reused from the
[FOCUS tab](focus.md#parameters).

When the trigger fires:

1. Captures pause naturally (the frame handler awaits the AF run, and
   whoever's pushing frames is awaiting that handler)
2. AF sweeps + finds best position
3. New HFR + temperature are baselined
4. Captures resume on their own

**▶ Now** button bypasses gates for manual fires.

### Auto re-center

Same OR-combine pattern. Three trigger types:

- **Every N frames**
- **Every N minutes**
- **Drift ≥ X arcsec**, runs a plate-solve **per frame** to compute
  drift from the reference; **expensive on RPi 4** (1-3s per solve).
  Default 0 = disabled.

When the trigger fires, Polaris re-slews + plate-solves until the
mount is centered to within **Tolerance arcsec** (default 30").

**Reference RA/Dec** is established by a one-shot plate solve on the
**first integrated frame** of the session, true astrometric position,
not the mount's potentially-biased report. Status line shows the
reference once it's solved; banner appears if the first-frame solve
failed (recenter disabled until next stack reset).

**▶ Now** button only enables once reference is solved.

### Mutex

Trigger handlers run sequentially inside the frame integration. AF +
re-center can't run concurrently (reentry guard skips trigger eval
for frames arriving during an executing trigger).

### Per-rig persistence

The full policy lives on `EquipmentProfile.LiveStackTriggers`. Switch
rigs + the new rig's policy auto-loads. Different setups (cold APO vs
SCT with active dew heater) have different thermal characteristics,
so per-rig makes sense.

## Refocus suggestion (trend-based, manual focuser friendly)

The auto re-focus path above only fires when you have a motorized
focuser AND have turned on **RefocusEnabled** in the rig. Plenty of
setups don't qualify, manual Crayford focusers, the cheap rack &
pinion on a kid's scope, or motorized rigs where you prefer to
refocus by hand. Without either condition Polaris would otherwise
be silent about degrading focus, you'd only notice after several
blurry frames piled up in the stack.

Polaris fills that gap with a **trend-based suggestion**. It watches
the same per-frame HFR + star count stream the auto-fire path uses
and raises an advisory chip + LIVE-tab callout when the numbers are
trending the wrong way. Nothing moves the focuser, you refocus
manually, then click "I refocused" to acknowledge.

### How the detection works

The detection is automatic, no thresholds to tune.

1. **Warm-up**: the service buffers the first 15 valid samples
   (HFR > 0, star count >= 5) without raising anything.
2. **Baseline**: once warmed up, the 5th-percentile HFR over the
   last 20 samples becomes the "best stable HFR" reference, plus
   the median star count.
3. **Trend test** on every subsequent frame:
   - Linear regression of the last 10 HFR samples gives a slope.
   - The rolling mean of the last 5 HFR samples is compared to
     baseline.
   - Fires when **slope > 0** AND **mean > baseline x 1.15** AND
     **5-frame extrapolated change > 30% of baseline**.
4. **Star-count secondary**: a 30% drop in average star count vs
   the baseline median fires on its own. Covers very-out-of-focus
   where HFR looks deceptively stable because dim stars dropped out
   entirely and HFR is computed on a shrinking set of bright cores.
5. **Auto-dismiss** once the rolling means recover to within 5% of
   baseline for 3 consecutive frames.

These constants live in `Services/RefocusSuggestionService.cs` as
`private const` so a future tuning pass touches one file.

### When you'll see it

- A toast appears the first time the detector fires.
- A `Refocus: HFR rising 18% over 10 frames` chip joins the activity
  bar at the bottom of every tab. Click the chip to jump straight to
  FOCUS, Manual Assist.
- A yellow callout appears in the LIVE tab above the auto-refocus
  panel with three buttons:
  - **I refocused** dismisses and replaces the baseline with the
    post-refocus HFR (the new "good").
  - **Open FOCUS** jumps to FOCUS, Manual Assist without dismissing.
  - **Dismiss** clears the chip without resetting the baseline
    (useful if you trust the original baseline and just want to
    acknowledge).

### When it stays silent

- **Auto-refocus is enabled in the rig**: the suggestion does
  nothing, LSTR-3 already covers you. Toggle one or the other in
  the LIVE tab's "Auto re-focus / re-center" panel.
- **First 15 frames of any session**, warm-up gate.
- **Frames with HFR == 0 or star count < 5**, dropped as
  unreliable. Bad seeing / clouds won't poison the detector.
- **No live stacking running**, the detector subscribes to
  FrameIntegrated events.

### API

- `POST /api/livestack/refocus-suggestion/dismiss` with body
  `{ "resetBaseline": true }` (default) replaces the baseline with
  the rolling mean. `false` clears the chip without changing the
  baseline.
- `GET /api/livestack/refocus-suggestion/status` for direct polling.
  The same payload also rides inside `liveStack.refocusSuggestion`
  on the WS tick.

## WebSocket payload

For automation / external dashboards, the live stack state is in the
1Hz `/ws/status`:

```jsonc
{
  "liveStack": {
    "isRunning": true,
    "frameCount": 42,
    "width": 4144, "height": 2822,
    "referenceStarCount": 87,
    "lastFrameHfr": 1.94,
    "lastFrameStarCount": 92,
    "triggers": {
      "isExecuting": false, "executingKind": null,
      "lastRefocusAt": "2026-05-22T22:48:13Z", "lastRefocusFrame": 30,
      "lastRefocusHfr": 1.87, "lastRefocusTempC": -10.2,
      "lastRecenterAt": null, "lastRecenterFrame": 0,
      "lastRecenterDriftArcsec": 0,
      "referenceRaHours": 23.234, "referenceDecDeg": 12.583,
      "referenceSolved": true,
      "lastError": null
    },
    "refocusSuggestion": {
      "suggesting": true,
      "reason": "HFR rising 18% over 10 frames",
      "baselineHfr": 1.92, "currentHfr": 2.27,
      "slopePerFrame": 0.04,
      "framesSinceBaseline": 28, "sampleCount": 28,
      "baselineStarCount": 87,
      "suggestedAt": "2026-05-22T23:05:11Z"
    }
  }
}
```

## Common pitfalls

**Frames stop integrating mid-session**, alignment failed for a long
stretch (clouds, dew on the corrector). Polaris keeps trying each
frame; clear the dew + integration resumes automatically.

**Auto-recenter never triggers**, reference solve failed on the
first frame (no stars, blurry, plate-solver path wrong). Reset the
stack so the next first frame solves cleanly. Verify ASTAP is
installed + reachable.

**HFR trigger fires spuriously after passing meridian**, pier-side
flip changes the frame orientation; star detection sees "new" stars
and the median HFR jumps temporarily. Bump the HFR threshold or
combine it with "every N frames" so the spurious fire is bounded.

**Stack quality looks worse than individual frames**, alignment
keeps failing silently, only the first frame is actually in the
stack. Check the WebSocket payload: `frameCount` should match how many
frames you've captured. Persistent reference-star-count of 0 means
the first frame had no detectable stars, Reset + try again.

## See also

- [FOCUS tab](focus.md), the AF engine these triggers invoke
- [SKY tab → Slew & Center](sky-explorer.md), the recenter engine
- [Glossary → EAA / HFR / Dither / Plate solve](GLOSSARY.md#e)
