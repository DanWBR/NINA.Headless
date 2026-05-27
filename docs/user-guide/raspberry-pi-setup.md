# Raspberry Pi 4 / 5 setup guide

End-to-end recipe to take a brand new Raspberry Pi from blank SD card
to a Polaris server that auto-starts on boot, hosts INDI drivers, plate
solves, and is reachable from any laptop or phone on your LAN. Roughly
60 to 90 minutes the first time, mostly waiting on `apt`.

Single page on purpose. If you already have a working Pi and just need
to install Polaris, jump to [Install Polaris](#5-install-polaris).
Otherwise follow top to bottom.

## 0. Hardware checklist

| Item | Pi 4 | Pi 5 | Notes |
|---|---|---|---|
| RAM | 4 GB minimum, 8 GB recommended | 4 GB minimum, 8 GB recommended | Pi 4 with 2 GB technically boots but live stacking and editor will swap heavily. |
| Power supply | Official 5V/3A USB-C | Official 27W USB-C (5V/5A PD) | Underpowered PSU is the #1 source of "USB camera randomly disconnects". |
| Cooling | Active fan (Pi 4 case fan or Argon ONE) | Active fan mandatory (Pi 5 throttles aggressively without one) | Long sessions get to 70 C inside cases. |
| Storage | SanDisk Extreme Pro 64 GB+ or USB 3.0 SSD | Pi 5 NVMe HAT + NVMe SSD recommended | SD card is fine for the OS; put image output on a separate SSD via USB 3.0 or NVMe. |
| Network | Wired Ethernet preferred | Wired Ethernet preferred | WiFi works but live stack frames are big; wired keeps the browser responsive. |
| USB hub | Powered USB 3.0 hub if camera + mount + EFW + focuser all plug in directly | Same | Pi 5 has better per-port budget but a powered hub still helps with cooled CMOS cameras. |

You also need: a microSD card reader on your main computer, an HDMI
cable for first boot (or use headless setup below).

## 1. Flash Raspberry Pi OS

Use **Raspberry Pi OS Lite (64-bit) Bookworm**. The Desktop edition
also works but adds 2 GB of stuff you do not need on a headless astro
server. 64-bit (aarch64) is mandatory: .NET 10 runtime, vsdbg, and the
ONNX models do not ship 32-bit ARM builds.

1. Install [Raspberry Pi Imager](https://www.raspberrypi.com/software/)
   on your laptop.
2. Choose Device, then Pi 4 or Pi 5.
3. Choose OS, then "Raspberry Pi OS (other)", then
   "Raspberry Pi OS Lite (64-bit)".
4. Choose Storage, then your SD card.
5. Click the gear icon (Advanced options) BEFORE writing:
   - Set hostname: pick anything (this guide uses `polaris-pi` as an example, so the Pi becomes reachable at `polaris-pi.local` on the LAN).
   - Enable SSH, use password authentication (or paste a public key).
   - Set username: `polaris`, password: whatever you want.
   - Configure WiFi if you do not have Ethernet at the scope.
   - Set locale: your timezone, keyboard layout.
6. Write the card, eject, boot the Pi.

After boot it shows up on your network as `<hostname>.local` (mDNS) or by
IP from your router admin page. SSH in:

```bash
ssh polaris@<hostname>.local
```

## 2. First-boot system tasks

Done once, in order. Each step is small.

### 2.1. Update everything

```bash
sudo apt update && sudo apt full-upgrade -y
sudo reboot
```

After reboot, SSH back in.

### 2.2. Confirm 64-bit kernel

```bash
uname -m
```

Should print `aarch64`. If it prints `armv7l` you accidentally flashed
32-bit OS. Reflash with the Lite 64-bit image.

### 2.3. Set timezone and locale (if you skipped in Imager)

```bash
sudo raspi-config
```

`Localisation Options`, set timezone (e.g. America/Fortaleza), locale,
WLAN country. `Finish`.

### 2.4. Bump swap to 2 GB (helps editor, ONNX, batch stacking)

Recent Raspberry Pi OS Lite images no longer ship `dphys-swapfile`
pre-installed, so install it first:

```bash
sudo apt install -y dphys-swapfile
sudo dphys-swapfile swapoff
sudo sed -i 's/CONF_SWAPSIZE=.*/CONF_SWAPSIZE=2048/' /etc/dphys-swapfile
sudo dphys-swapfile setup
sudo dphys-swapfile swapon
```

Verify: `free -m` should show ~2000 MB Swap.

### 2.5. Pi 4 only: minimum GPU split, max CPU RAM

```bash
sudo sed -i 's/^gpu_mem=.*/gpu_mem=16/' /boot/firmware/config.txt 2>/dev/null \
  || echo 'gpu_mem=16' | sudo tee -a /boot/firmware/config.txt
```

Polaris runs headless; the GPU is unused. Defaults to 64 MB, reclaim
the 48 MB. Pi 5 manages GPU/CPU memory automatically; skip this step.

### 2.6. USB current cap (Pi 4 only, if powering camera off the Pi)

```bash
echo 'max_usb_current=1' | sudo tee -a /boot/firmware/config.txt
```

Pi 5 already runs at full USB current with the official 27W PSU. Skip.

### 2.7. Reboot to apply firmware changes

```bash
sudo reboot
```

## 3. Set up the capture root

The convention this guide uses:

| Path | Purpose |
|---|---|
| `/home/polaris/polaris/` | Polaris application binary + assets |
| `/home/polaris/files/` | Captures, masters, calibration frames, planetary SER, GraXpert output |

Create the captures dir:

```bash
mkdir -p ~/files
```

The systemd unit in section 7 seeds the env var
`POLARIS_IMAGE_OUTPUT_DIR=/home/polaris/files` so Polaris auto-uses
this path on first boot without you having to click through the UI.

### 3.1. (Optional, recommended) Mount a USB SSD over ~/files

SD cards die fast under sequence writes. If you have a USB 3.0 SSD or
NVMe drive, mount it at `~/files` so all captures land on the SSD
without changing any Polaris path.

```bash
lsblk
```

Find your drive (typically `sda` with partition `sda1`). Format if new
(this **WIPES** the drive):

```bash
sudo mkfs.ext4 /dev/sda1
```

Mount permanently over `~/files`:

```bash
echo "UUID=$(sudo blkid -s UUID -o value /dev/sda1) /home/polaris/files ext4 defaults,nofail 0 2" \
  | sudo tee -a /etc/fstab
sudo mount -a
sudo chown polaris:polaris /home/polaris/files
```

Now writes to `~/files/` land on the SSD; the SD card only handles the
OS and Polaris binary.

## 4. Install all dependencies

Apt covers most. Two callouts:

- **INDI, PHD2, xpra**: the apt versions on Bookworm lag upstream by
  6 to 18 months. If you want bleeding edge (new camera drivers,
  PHD2 fixes, xpra protocol updates), compile from source. See
  [section 4.2](#42-optional-compile-indi-phd2-xpra-from-source).
- **GraXpert**: no apt package. Download from
  [graxpert.com](https://www.graxpert.com/), extract into a folder
  (the convention used here is `~/graxpert`), point Polaris at it via
  Settings (UI does the wiring, no JSON editing).

### 4.1. Apt block (copy-paste, walk away ~10 min)

```bash
# .NET 10 ASP.NET Core runtime (installed under the polaris user, not
# system-wide; keeps apt away from /usr and makes upgrades a single
# re-run of the install script)
sudo apt install -y libicu-dev libssl-dev curl libfontconfig1
curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin \
  --channel 10.0 --runtime aspnetcore --install-dir $HOME/.dotnet

# Make `dotnet` available in interactive shells and to systemd later
cat >> ~/.bashrc <<'BASHRC'

# .NET 10 (user install)
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$DOTNET_ROOT:$PATH
BASHRC
source ~/.bashrc

# INDI core + all bundled drivers (apt version)
sudo apt install -y indi-bin indi-full

# Plate solver
sudo apt install -y astap

# pipenv for installing indi-web in a managed venv
sudo apt install -y pipenv

# PHD2 autoguider (apt version)
sudo apt install -y phd2

# Siril (post-processing, Polaris invokes its CLI)
sudo apt install -y siril

# xpra for the embedded PHD2 GUI (apt version)
sudo apt install -y xpra xserver-xorg-video-dummy

# Diagnostics helpers
sudo apt install -y lsof
```

Verify .NET landed:

```bash
dotnet --info
```

Should show `Microsoft.AspNetCore.App 10.x.x`. If not, the install
script silently fell back; re-run it.

### 4.2. (Optional) compile INDI, PHD2, xpra from source

Skip this section unless you have a specific reason. Apt versions work
for most setups. Reasons to compile:

- New camera was released after the apt INDI version froze
  (e.g. ZWO ASI2600MM Pro support).
- PHD2 has a fix you need that has not made it to Debian yet.
- xpra HTML5 client needs a newer protocol than apt ships for the
  embedded PHD2 GUI in modern browsers.

Build dependencies:

```bash
sudo apt install -y build-essential cmake git pkg-config \
  libnova-dev libcfitsio-dev libusb-1.0-0-dev zlib1g-dev libgsl-dev \
  libjpeg-dev libcurl4-gnutls-dev libtheora-dev libfftw3-dev \
  libftdi1-dev libgps-dev libraw-dev libdc1394-dev libgphoto2-dev \
  libboost-dev libboost-regex-dev libindi-dev libnova-dev libwxgtk3.2-dev
```

**INDI** (clone, build, install):

```bash
git clone --depth 1 https://github.com/indilib/indi.git ~/src/indi
mkdir -p ~/src/indi/build && cd ~/src/indi/build
cmake -DCMAKE_INSTALL_PREFIX=/usr/local -DCMAKE_BUILD_TYPE=Release ..
make -j$(nproc)
sudo make install
```

For 3rd-party drivers (ZWO, QHY, Pegasus, etc):

```bash
git clone --depth 1 https://github.com/indilib/indi-3rdparty.git ~/src/indi-3rdparty
# follow per-driver build instructions in the README
```

**PHD2**:

```bash
git clone --recursive --depth 1 https://github.com/OpenPHDGuiding/phd2.git ~/src/phd2
mkdir -p ~/src/phd2/build && cd ~/src/phd2/build
cmake -DCMAKE_INSTALL_PREFIX=/usr/local -DCMAKE_BUILD_TYPE=Release ..
make -j$(nproc)
sudo make install
```

**xpra**: source builds are involved (lots of optional codecs). The
project's [Pi setup notes](https://github.com/Xpra-org/xpra/wiki/Raspberry-Pi-Build)
are the canonical reference. Most users will be fine with the apt
version, the embedded PHD2 GUI works either way.

After source installs, `which indiserver`, `which phd2`, `which xpra`
should resolve to `/usr/local/bin/`. The apt versions in `/usr/bin/`
get shadowed by the PATH order; uninstall them if you want to be
certain (`sudo apt remove indi-bin phd2 xpra`).

### 4.3. ASTAP star database (one-time, 290 MB)

ASTAP alone cannot solve, it needs a star catalog. Install the H17
database (good for focal lengths from 50 mm to 2000 mm):

```bash
cd /tmp
wget https://sourceforge.net/projects/astap-program/files/star_databases/h17_star_database_mag17_colour.zip
sudo apt install -y unzip
sudo mkdir -p /opt/astap
sudo unzip h17_star_database_mag17_colour.zip -d /opt/astap/
```

ASTAP looks in `/opt/astap`, `~/.local/share/astap`, or alongside its
binary. The `/opt/astap` path is read-anyone, works for all users.

### 4.4. GraXpert (manual install)

GraXpert ships in two forms: a self-contained binary (Windows / macOS,
plus old Linux releases) and a Python package on PyPI (the only
reliable form on Pi today, since pre-built ARM binaries are not
always published). Polaris auto-detects both styles.

**Recommended on Pi: install via pip in a venv.**

```bash
sudo apt install -y python3-venv python3-pip
mkdir -p ~/GraXpert && cd ~/GraXpert
python3 -m venv graxpert
./graxpert/bin/pip install --upgrade pip
./graxpert/bin/pip install graxpert
```

That gives you `/home/polaris/GraXpert/graxpert/bin/python` with the
`graxpert` module installed. Polaris auto-detects this layout (no
Settings configuration needed) and invokes it as
`python -m graxpert.main ARGS`.

Verify:

```bash
~/GraXpert/graxpert/bin/python -m graxpert.main --version
```

If you have the standalone binary instead (downloaded from
[graxpert.com](https://www.graxpert.com/)), drop it at
`/home/polaris/graxpert/graxpert` (lowercase folder + binary) or
`/usr/local/bin/graxpert`. Polaris auto-detects both.

#### Installing AI models

GraXpert v3 no longer auto-downloads the AI models; you have to drop
them in place by hand. Each operation (BGE, deconvolution, denoise)
needs its model directory.

The location follows the XDG Base Directory spec via Python's
`appdirs.user_data_dir(appname="GraXpert")`, which on Linux resolves
to `~/.local/share/GraXpert/`. Final layout:

```
/home/polaris/.local/share/GraXpert/
├── ai-models/                  # decon + denoise models
│   ├── deconvolution-stars/{version}/model.onnx
│   ├── deconvolution-object/{version}/model.onnx
│   └── denoise/{version}/model.onnx
└── bge-ai-models/              # background extraction
    └── {version}/model.onnx
```

If you already have the models on your Windows PC (under
`%LOCALAPPDATA%\GraXpert\`, i.e. `C:\Users\YOU\AppData\Local\GraXpert\`),
copy them to the Pi over SSH:

```powershell
# From Windows PowerShell:
scp -r "$env:LOCALAPPDATA\GraXpert\ai-models" polaris@<hostname>.local:.local/share/GraXpert/
scp -r "$env:LOCALAPPDATA\GraXpert\bge-ai-models" polaris@<hostname>.local:.local/share/GraXpert/
```

Or, from WSL / Git Bash:

```bash
rsync -avh "/mnt/c/Users/YOU/AppData/Local/GraXpert/" polaris@<hostname>.local:.local/share/GraXpert/
```

Verify on the Pi:

```bash
ls ~/.local/share/GraXpert/
du -sh ~/.local/share/GraXpert/*
```

You should see both `ai-models/` and `bge-ai-models/` with a few
hundred MB each.

**systemd gotcha**: the unit in section 7 sets
`Environment=HOME=/home/polaris`. Without that, the `appdirs` lookup
inside GraXpert returns a path that does not exist and you get
"model not found" errors at runtime even when the files are in place.
Same root cause as the indi-web "stopped right after start" gotcha.

### 4.5. Verify external binaries

```bash
dotnet --info       # Microsoft.AspNetCore.App 10.x.x
indiserver --help   # should print usage
which astap         # /usr/bin/astap (or /usr/local/bin/astap if from source)
which siril         # /usr/bin/siril
# GraXpert: pick the line that matches your install style
~/GraXpert/graxpert/bin/python -m graxpert.main --version   # venv install
~/graxpert/graxpert --version                                # standalone binary
```

## 5. Install Polaris

Pick one of three paths. Pre-built release is fastest.

### Option A: pre-built release (recommended)

```bash
mkdir -p ~/polaris && cd ~/polaris
curl -L https://github.com/DanWBR/NINA.Polaris/releases/latest/download/polaris-linux-arm64.tar.gz \
  | tar xz
chmod +x NINA.Polaris
```

### Option B: Docker

```bash
sudo apt install -y docker.io
sudo usermod -aG docker polaris
# log out and back in for group to take effect
docker run -d --name polaris \
  --network host \
  --restart unless-stopped \
  -e POLARIS_IMAGE_OUTPUT_DIR=/data \
  -v ~/files:/data \
  -v ~/.config/polaris:/root/.config \
  ghcr.io/danwbr/nina-polaris:latest
```

`--network host` is critical: INDI BLOB streaming over loopback is much
faster than through Docker's bridge. Skip the systemd steps below if
you use Docker; the container handles restart on its own.

### Option C: build from source

For developers iterating on Polaris itself. See
[rpi-debug-from-vs.md](rpi-debug-from-vs.md) for the full developer
workflow including step-debugging from Visual Studio.

```bash
git clone https://github.com/DanWBR/NINA.Polaris.git
cd NINA.Polaris
dotnet publish src/NINA.Polaris/NINA.Polaris.csproj \
  -c Release -r linux-arm64 --self-contained -o ~/polaris
```

## 6. First-run smoke test (foreground)

Before wiring auto-start, confirm it actually runs:

```bash
cd ~/polaris
ASPNETCORE_URLS=http://0.0.0.0:5000 ./NINA.Polaris
```

You should see log lines like:

```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://0.0.0.0:5000
info: NINA.Polaris.Services.MdnsService[0]
      Advertising _nina._tcp on <hostname>.local
```

From your laptop, open `http://<hostname>.local:5000` (or
`http://<pi-ip>:5000`). The Polaris home page loads. Ctrl-C to stop.

If the page does not load: check firewall on the Pi (`sudo ufw status`,
default is none on Pi OS Lite) and your router's client isolation
setting (some mesh systems block device-to-device traffic on the guest
SSID).

## 7. Auto-start on boot (systemd)

Create a unit so Polaris comes back after every reboot, power loss, or
crash. Skip if you used Docker (option B).

```bash
sudo tee /etc/systemd/system/polaris.service > /dev/null <<'EOF'
[Unit]
Description=N.I.N.A. Polaris astrophotography server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=polaris
Group=polaris
WorkingDirectory=/home/polaris/polaris
ExecStart=/home/polaris/polaris/NINA.Polaris
Environment=ASPNETCORE_URLS=http://0.0.0.0:5000
Environment=HOME=/home/polaris
Environment=DOTNET_ROOT=/home/polaris/.dotnet
Environment=PATH=/home/polaris/.dotnet:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
Environment=POLARIS_IMAGE_OUTPUT_DIR=/home/polaris/files
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable polaris.service
sudo systemctl start polaris.service
```

Confirm:

```bash
sudo systemctl status polaris.service
# should show "active (running)"
journalctl -u polaris.service -f
# tail logs in real time, Ctrl-C to exit
```

Reboot the Pi: `sudo reboot`. After ~30 seconds `<hostname>.local:5000`
should be back without you touching anything.

## 8. INDI: pick how drivers are loaded

Two options. Pick one, do not run both.

### 8.1. Manual indiserver per session

Simplest. SSH in, start the drivers you need for tonight:

```bash
indiserver -v indi_simulator_ccd indi_simulator_telescope
# or for real gear:
indiserver -v indi_asi_ccd indi_eqmod_telescope indi_asi_focuser indi_asi_wheel
```

Leave that SSH session open during your imaging session.

### 8.2. indi-web (recommended): web UI to pick drivers, no SSH

Install indi-web in a pipenv-managed virtualenv (Raspberry Pi OS
Bookworm blocks system-wide `pip install` per PEP 668):

```bash
cd ~
mkdir -p indiweb && cd indiweb
pipenv --python=$(which python3)
pipenv install indiweb
pipenv --venv
```

`pipenv --venv` prints something like
`/home/polaris/.local/share/virtualenvs/indiweb-AbCd1234`. Note that
path; the `indi-web` binary is at `{that path}/bin/indi-web`.

Tell Polaris where it is. Edit `~/polaris/appsettings.json` (create if
missing):

```json
{
  "IndiWeb": {
    "ExecutablePath": "/home/polaris/.local/share/virtualenvs/indiweb-AbCd1234/bin/indi-web",
    "AutoStart": true,
    "Port": 8624,
    "BindAddress": "127.0.0.1"
  }
}
```

Restart Polaris: `sudo systemctl restart polaris.service`. Open the
RIGS tab, scroll to the bottom. The "INDI Drivers" section now embeds
the indi-web UI inside Polaris. Pick a Profile, tick the drivers, click
Server > Start. Done.

Full details and troubleshooting in [indi-web.md](indi-web.md). Common
pitfall: if status flips back to "Stopped" right after Start, check
that the systemd unit above includes `Environment=HOME=/home/polaris`,
because Bottle (the framework indi-web uses) reads config from `$HOME`
and exits if it is empty.

## 9. PHD2 autoguiding (optional)

Already installed if you ran the apt block in step 4. PHD2 has its own
JSON-RPC server on port 4400; Polaris talks to it via the GUIDE tab.

For the embedded PHD2 GUI inside the Polaris browser (uses xpra over
HTTP), see [phd2-gui-embedding.md](../phd2-gui-embedding.md). Optional;
PHD2 also runs perfectly on the Pi desktop or via SSH X11 forwarding.

## 10. HTTPS for WebGPU (optional, recommended for GraXpert AI)

The GraXpert ONNX models run in your browser via WebGPU. Browsers
require HTTPS for WebGPU on non-localhost origins. Polaris ships a
self-signed certificate generator:

```bash
# in your Polaris install
./NINA.Polaris --setup-https
```

This generates a cert and serves on port 5001 in addition to 5000.
Trust the cert on each client device once (see [https-setup.md](https-setup.md)).
After that GraXpert AI runs at 5 to 20x the speed of CPU-only WASM.

## 11. Verify your setup

End-to-end check from your laptop:

1. Browse to `http://<hostname>.local:5000`.
2. Home page loads with sidebar tabs (RIGS, GUIDE, FOCUS, PREVIEW,
   AUTORUN, etc).
3. RIGS tab: INDI Drivers section shows green "Running" pill (or
   "Stopped" with a Start button if you did not enable AutoStart).
4. Start the simulator profile (Telescope Simulator + CCD Simulator)
   in indi-web, click Server > Start.
5. Back in Polaris RIGS, pick "Telescope Simulator" in the Mount
   dropdown, "CCD Simulator" in the Camera dropdown. Click Connect on
   each card.
6. Click PREVIEW tab, set Exposure to 2 seconds, click Take Snap.
7. After ~3 seconds a frame appears with stars on it. The simulator
   renders real stars from the GSC catalog based on the simulated
   mount position.
8. Optionally: STUDIO tab, click any existing FITS frame, click
   "Plate solve". ASTAP solves in 1 to 5 seconds with the H17 database.

If all of that works, you have a fully functional Polaris install.
Move on to [First-night setup](first-night.md) for the walkthrough of
connecting real hardware.

## 12. Updating Polaris later

```bash
sudo systemctl stop polaris.service
cd ~/polaris
curl -L https://github.com/DanWBR/NINA.Polaris/releases/latest/download/polaris-linux-arm64.tar.gz \
  | tar xz --overwrite
sudo systemctl start polaris.service
```

`appsettings.json` and your image library are not touched by the tar
extraction (they live elsewhere). Profiles are stored in
`~/.config/polaris/`, also untouched.

## 13. Troubleshooting

**`<hostname>.local` does not resolve.** Some routers block mDNS across
VLANs or guest networks. Fall back to IP: find it with
`hostname -I` on the Pi.

**Polaris starts but UI is blank or hangs.** Check the journal:
`journalctl -u polaris.service -n 100`. Likely culprits: corrupt
`appsettings.json` (validate JSON syntax), missing
`libfontconfig1` (SkiaSharp needs it for image encoding), wrong
permissions on the image output folder.

**systemd says `dotnet: command not found` or `framework not found`.**
The .NET install lives under `/home/polaris/.dotnet/`, which is not on
the system PATH. The systemd unit in section 7 includes
`Environment=DOTNET_ROOT=...` and `Environment=PATH=...` to fix that.
If you wrote your own unit, copy those two lines in.

**INDI driver shows in indi-web but Polaris RIGS dropdown is empty.**
Polaris connects to `127.0.0.1:7624` by default. If you changed the
INDI port in Polaris settings or in indi-web, they have to match.

**Camera disconnects mid-exposure.** Almost always a power issue.
Check: official PSU? Powered USB hub for camera + dew heater + mount +
focuser? Pi 4 has 1.2 A total USB budget by default; a cooled CMOS
camera can pull 1.5 A alone.

**Pi gets hot and throttles (CPU drops to 600 MHz).** Active cooling
is mandatory on Pi 5 and strongly recommended on Pi 4 in a case.
`vcgencmd measure_temp` from SSH shows current; throttling kicks in
at 80 C, hard cap at 85 C.

**SD card corruption after a few weeks.** Move image output to a USB
SSD (step 3). Polaris does heavy sequential writes during sequences,
and consumer SD cards are not rated for that. The OS itself is light
on writes and is fine on SD.

## See also

- [Installation](installation.md), shorter cross-platform install
  reference covering Pi, x64 Linux, Windows
- [First-night setup](first-night.md), connect gear and take first frames
- [INDI Drivers manager](indi-web.md), full detail on the embedded
  indi-web feature
- [PHD2 deep integration](../phd2-gui-embedding.md), xpra-hosted PHD2
  GUI inside the Polaris browser
- [HTTPS setup](https-setup.md), self-signed cert workflow needed for
  WebGPU on LAN clients
- [Troubleshooting](troubleshooting.md), broader problem catalog
- [Debug from Visual Studio](rpi-debug-from-vs.md), developer workflow
  for editing Polaris source and remote-debugging on the Pi
