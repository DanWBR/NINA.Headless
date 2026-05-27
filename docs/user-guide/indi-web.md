# INDI Drivers manager (embedded indi-web)

Polaris can embed the **indi-web** ([indiwebmanager](https://github.com/knro/indiwebmanager))
UI inside its own RIGS tab so you can start, stop, and configure
INDI drivers without ssh'ing into the host and editing
`indiserver` command lines by hand.

Unlike the embedded PHD2 GUI (which needs xpra to stream a desktop
window), `indi-web` is already a browser app — Polaris just
reverse-proxies it through `/indi-web/` and shows it in an iframe.
No extra display server, no extra streaming bandwidth.

## When you need this

Polaris's normal `IndiClient` connects to a running `indiserver` on
port 7624 and lists whatever drivers that server has already loaded.
It cannot, by itself, add or remove drivers — that requires either
restarting `indiserver` with new arguments or talking to its FIFO.
The embedded `indi-web` panel gives you:

- A checklist of every INDI driver installed on the host
- Profiles (groups of drivers you turn on together: "Mono SHO rig",
  "OSC travel rig", "Sim only")
- Start / stop / restart of `indiserver` from the browser
- Telescope simulator + utility drivers without command-line wizardry

Without it, every "I plugged in a new accessory and need its driver
loaded" trip means ssh + `pkill indiserver` + relaunching with the
new driver in the argv. With it, you tick a checkbox and click
Start.

## Requirements

| | |
|---|---|
| OS | Linux or macOS. Windows is unsupported — `indiserver` itself doesn't ship for Windows. |
| Python | 3.x with `pip` on PATH. |
| INDI core | Installed via `apt install indi-bin` (Debian/Ubuntu/Raspberry Pi OS) or your distro's equivalent — `indi-web` shells out to `indiserver`, it does not bundle it. |
| Port | 8624 (`indi-web`'s default). Bound to `127.0.0.1` only — Polaris proxies access through itself. |

## Install

One-line, on the Polaris host:

```bash
pip install indiwebmanager
```

You don't need to start `indi-web` yourself — Polaris's
`IndiWebManagerService` detects the binary, manages the process,
and surfaces it in the RIGS tab.

If `pip install` lands in a virtual environment, point Polaris at
the absolute path of the `indi-web` binary in your
`appsettings.json`:

```json
{
  "IndiWeb": {
    "ExecutablePath": "/home/danie/.venv/polaris/bin/indi-web",
    "AutoStart": true,
    "Port": 8624,
    "BindAddress": "127.0.0.1"
  }
}
```

`BindAddress` should stay on loopback unless you really know what
you're doing — `indi-web` has no auth, so binding it to `0.0.0.0`
re-exposes driver control to anyone on the LAN.

## Workflow

1. Open the **RIGS** tab in Polaris.
2. Scroll to the **INDI Drivers** section near the bottom of the
   page (below Accessories).
3. The status pill shows the current state:
   - **● Running** — green, indi-web is up and the iframe is loaded
   - **Stopped** — installed but not currently running, click ▶ Start
   - **Not installed** — pip install hint shown inline
   - **OS not supported** — Windows banner with the reason
4. Click **▶ Start**. The iframe mounts after the TCP probe
   confirms indi-web is listening (typically 1-3 seconds).
5. Use the embedded UI to: pick a Profile (or create one), tick
   the drivers you want, click "Server" → "Start". indi-web now
   owns the `indiserver` process; Polaris's normal INDI client
   tabs (Mount, Camera, etc.) see the loaded drivers in their
   device dropdowns.
6. When you're done, click **■ Stop** in the Polaris control row
   to shut indi-web down. Polaris will kill the child process
   tree cleanly.

## Coexistence with the built-in Simulator

Both `IndiWebManagerService` and the built-in
`SimulatorService` (Settings → Simulator) want to control
`indiserver`. They get along but you have to pick one owner per
session:

- **You're using indi-web for everything** (real hardware OR
  simulators) → leave SimulatorService disabled. Use indi-web's
  Profile dropdown to load the simulator drivers (`indi_simulator_ccd`,
  `indi_simulator_telescope`, etc.) when you want a dry-run.
- **You're using SimulatorService for dry-runs** → leave indi-web
  stopped. SimulatorService spawns its own indiserver via FIFO.

If both run at once they will race on the same FIFO and one of
them loses commands. A future Polaris version will route
SimulatorService's start/stop commands through indi-web's REST API
when indi-web is the active owner, but for now: pick one.

## Auto-start on boot

`appsettings.json` → `IndiWeb:AutoStart` = `true`. With auto-start
on, the service launches `indi-web` ~3 seconds after Polaris
itself comes up; the RIGS tab's INDI Drivers section shows the
running iframe immediately on first open.

Without auto-start (the default), `indi-web` only runs when you
click **▶ Start**.

## Troubleshooting

**Banner says "indi-web not detected" but I installed it.**
Polaris looks at the `PATH` of the user running Polaris. If you
installed with `pip install --user` and ran Polaris under a
different user (e.g. systemd as `polaris`), `~/.local/bin` may not
be in that user's PATH. Either:

- Install system-wide with `sudo pip install indiwebmanager`
- Or set `IndiWeb:ExecutablePath` to the absolute path in
  `appsettings.json`

**Iframe shows "Bad Gateway"** (502).
The reverse proxy got an error talking to `127.0.0.1:8624`. Check
the Polaris log for the inner error. Usually means indi-web died
between the status probe and the iframe fetch — click **⟳** in
the Polaris control row to re-probe.

**Iframe shows the indi-web UI but driver list is empty.**
That's an indi-web ↔ indiserver problem, not a Polaris problem.
Check that `indi-bin` is installed on the host (`apt list --installed | grep indi-bin`).
indi-web finds drivers by scanning XML files under
`/usr/share/indi/`; if that directory is empty there's nothing
to list.

**Polaris's INDI tabs don't see drivers I started via indi-web.**
Polaris's `IndiClient` connects via TCP to `indiserver` on the
configured host:port (Settings → INDI). Check that the host:port
match what indi-web's "Server" page reports.

## See also

- [Equipment setup](equipment.md) — pre-`indi-web` workflow,
  still relevant when you'd rather edit `indiserver` args manually
- [Simulator mode](simulator-mode.md) — built-in equipment
  simulator (see Coexistence section above for the rules)
- [PHD2 deep integration](phd2-gui-embedding.md) — for the more
  complex xpra-hosted PHD2 GUI; this is the lighter-weight cousin
