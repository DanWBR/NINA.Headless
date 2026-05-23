# Equipment simulator mode

Polaris ships with a one-click button to spawn a **fake telescope +
camera + focuser + filter wheel** so you can drive the whole pipeline
— sequencer, plate solve, live stacking, auto-focus — without
plugging a single cable. Great for:

- Smoke-testing a fresh install on a new Pi / mini-PC
- Demoing the UI to someone before sunset
- Iterating on Polaris itself during the day
- Practicing the workflow (Slew & Center, dither, meridian flip) when
  cloudy or before your first real session

The simulated camera **renders real stars** from the GSC catalog
based on the simulated mount's RA/Dec — slew to M31, see M31; bump
the focuser, HFR climbs; trigger a dither, the field shifts. It's
not a flat synthetic pattern, it's a believable mini-sky.

## Picking a backend

Polaris picks one automatically based on the host OS:

| Host OS | Backend | Source |
|---|---|---|
| Raspberry Pi OS, Ubuntu, Debian | INDI simulators (`indi_simulator_ccd`, ...) | `apt install indi-bin` |
| macOS | INDI simulators | `brew install indi-bin` |
| Windows | Alpaca Omni Simulator | [ASCOMInitiative/ASCOMSimulators](https://github.com/ASCOMInitiative/ASCOMSimulators/releases) |

Open Polaris → **Settings tab → Equipment simulator** section. The
panel shows which backend it picked + whether the binaries are
installed. If they're not, you'll see a banner with the exact
install command.

## Step-by-step (Linux / macOS)

1. **Install the binaries** (one-time):
   ```bash
   sudo apt install indi-bin       # Debian / Ubuntu / Raspberry Pi OS
   brew install indi-bin           # macOS
   ```

2. **Open Polaris → Settings → Equipment simulator**. Click
   **⟳ Re-detect**. Status flips to "✓ Installed v2.x.x" and the
   list of available devices populates.

3. **Pick devices** (default is Camera + Telescope + Focuser + Filter
   Wheel — sensible for most testing). Guide / Dome / Weather are
   optional checkboxes — toggle them on if you want to test PHD2
   dithering, dome slaving, weather-safety triggers.

4. **Set the INDI port** (default 7624 — leave alone unless something
   else on the host already uses it).

5. **(Optional)** Tick **Auto-start when Polaris boots** if you want
   the simulator stack to come up automatically with the app.

6. Click **▶ Launch simulators**. Within ~2 seconds the chip flips
   to "🟢 Running" and the picked drivers appear in the bottom hint:
   "Running: ccd, telescope, focus, wheel".

7. Go to the **RIGS tab**. Each device dropdown now lists
   "Simulator ..." options (because indiserver is up with those
   drivers). Pick "Simulator CCD" as Camera, "Simulator Telescope"
   as Mount, etc., then click **Connect** on each card.

8. **PREVIEW tab → Take snap** → you see a real star field rendered
   at whatever RA/Dec the simulated mount thinks it's pointing at
   (default: a chunk of Vela / Vega depending on the version).

9. **SKY tab → search M31 → Go to** → the simulated mount slews; the
   next capture shows M31's stars. Plate solve actually works
   against this — it's a real catalog projection, not noise.

10. **Live stack → Stack ON** → frames accumulate normally, HFR is
    consistent because the simulated focuser is perfect by default.
    Tweak the focuser position via FOCUS tab to make HFR climb;
    auto-focus picks up the V-curve correctly.

## Step-by-step (Windows)

1. **Download the Alpaca Omni Simulator** from the
   [ASCOMSimulators releases page](https://github.com/ASCOMInitiative/ASCOMSimulators/releases).
   It's a single .exe that exposes camera, telescope, focuser, etc.
   over a local Alpaca HTTP server (no ASCOM Platform install
   required for the Alpaca side). Install to the default location
   (`C:\Program Files\ASCOM\OmniSimulators\`) so Polaris can find it.

2. **Open Polaris → Settings → Equipment simulator** → click
   ⟳ Re-detect → status shows "✓ Installed v1.x.x".

3. Click **▶ Launch simulators**. Polaris spawns `AlpacaOmniSimulator.exe`
   minimized to the system tray. Within ~5 seconds the chip flips
   to 🟢 Running. The management endpoint at
   `http://localhost:32323/management/v1/configureddevices` returns
   the device list.

4. **RIGS tab** → in the Camera card, pick the **Alpaca** driver,
   then click **Detect** → "Alpaca Camera Sim" appears, Connect.
   Repeat for Mount / Focuser / Filter Wheel.

5. The rest of the workflow is identical to Linux.

**Caveat — no live add/remove on Windows.** The Omni Simulator
serves every device type simultaneously; there's no equivalent to
the INDI FIFO that lets us toggle drivers on a running server. The
device-list checkboxes in the Settings panel hide their "live toggle"
behaviour on Windows and show a banner saying so. To actually change
which devices are available, you Stop the simulator and restart it.
In practice this is rarely a problem — Omni Sim runs all device
types out of the box, and you pick which to *connect* in RIGS.

**Adopting an already-running Omni Sim** — if you started the
Omni Simulator from the start menu before opening Polaris,
Re-detect picks it up and Launch is a no-op success. Shutdown
will close it cleanly.

## What's running, where

- `indiserver` is a **child process** of Polaris. When you click
  Stop (or shut Polaris down without auto-restart), the simulator
  drivers go with it.
- The simulator's stdout/stderr lands in the Polaris log at
  `Debug` level — tail your `journalctl -u nina-polaris` (systemd)
  or the terminal where you launched Polaris to see driver output
  in real time.
- If `indiserver` crashes mid-session, the chip flips to "Stopped"
  within ~30 seconds (background health probe). Click **Launch**
  again to bring it back.

## Limits / what won't work

- **Plate solving** works because the simulated CCD renders real
  stars. ASTAP / Astrometry.net solve normally.
- **Auto-focus** works because the simulated focuser has a built-in
  parabolic HFR curve.
- **Dithering via PHD2** works if you also tick the **Guide**
  checkbox (spawns `indi_simulator_guide` for the guide camera).
- **DSLR-specific paths** (Canon EDSDK, Nikon SDK, Sony SDK) don't
  appear — the simulator is INDI / Alpaca only. To test those code
  paths you need the real vendor SDK.
- **Hardware-specific quirks** (USB disconnects, cooler runaway,
  USB-power brownouts) aren't reproducible — the simulator is too
  clean. Real-hardware testing is still required for the failure
  modes Polaris tries to recover from.

## Troubleshooting

**"Port 7624 already in use"**
You have another `indiserver` running outside Polaris. Either stop
it (`pkill indiserver`) or pick a different port in the Settings
panel.

**Devices show up in RIGS but Connect fails**
The simulator driver started but the device-specific connect
sequence failed. Tail the Polaris log — `indi_simulator_*` prints
a clear error per device. Common cause: simulating filter wheel
with zero filter slots; just stop + relaunch.

**Auto-start doesn't fire**
The toggle lives in `UserProfile.SimulatorAutoStart` (persisted to
`profile.json`). If you changed it via the API, restart Polaris —
the toggle is only read once at boot. UI-toggle changes ARE picked
up immediately for the next manual launch, but the auto-start
service only checks once per app start.

**Windows: Omni Simulator detected but Connect from Polaris fails**
Confirm the Omni Sim is actually serving — open
`http://localhost:32323/management/v1/configureddevices` in a browser,
you should see a JSON list. If it 404s, the Omni Sim isn't running
or is on a different port.

## See also

- [`docs/user-guide/rpi-debug-from-vs.md`](rpi-debug-from-vs.md) —
  remote debug Polaris on the Pi from Visual Studio (pairs nicely
  with simulator mode for "dev iteration without leaving the desk")
- [`docs/user-guide/installation.md`](installation.md) — full Polaris
  install
- [INDI simulator documentation](https://www.indilib.org/devices/auxiliary/ccd-simulator.html)
  — what knobs you can tweak in the simulated CCD (FWHM, jitter,
  noise) via the INDI control panel
