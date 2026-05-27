# Polaris SD card image build

Goal: ship a single `.img.xz` file that the user flashes to a microSD
card with Raspberry Pi Imager and boots into a fully working Polaris
server. Target audience: astrophotographers who do not want to type
apt commands.

This directory holds the build pipeline. End-user flash instructions
are in [docs/user-guide/sd-image.md](../docs/user-guide/sd-image.md).

## Status

**Planning phase.** This README documents the design decisions and the
build approach. Scripts and pi-gen stage are not yet implemented; see
[Open questions](#open-questions) before scaffolding.

## Approach: pi-gen with a custom stage

[pi-gen](https://github.com/RPi-Distro/pi-gen) is the official tool
the Raspberry Pi Foundation uses to build Raspberry Pi OS itself.
It produces deterministic `.img` files, runs in Docker, supports stage
composition. Adding Polaris is a matter of dropping a `stage-polaris/`
directory into a pi-gen checkout and including it in the `STAGE_LIST`.

Alternatives considered:

- **Yocto / Buildroot**: full distro builders. Massive overkill, weeks
  to set up, breaks `apt` for the end user.
- **Snapshot a running Pi with `dd`**: fast to iterate but not
  reproducible, captures UUIDs and host keys that need to be sanitised,
  every rebuild differs.
- **Cloud-init on stock Raspberry Pi OS**: defers all installs to first
  boot, requires the user to be on the internet at the scope, first
  boot takes 30+ minutes. Bad UX.

pi-gen wins on reproducibility, build speed, and matches how the
Raspberry Pi community already distributes images (Octopi, Ubuntu
Server for Pi, DietPi all use similar tooling).

## Build pipeline

```
pi-gen/
├── stage0/        # base bootstrap (Debian Bookworm)
├── stage1/        # systemd, networking
├── stage2/        # raspi-config, firmware
├── stage-polaris/ # our stage, adds Polaris + INDI + ASTAP + etc.
│   ├── prerun.sh
│   ├── 00-system-deps/
│   ├── 01-dotnet/
│   ├── 02-indi/
│   ├── 03-astap/
│   ├── 04-phd2/
│   ├── 05-siril/
│   ├── 06-xpra/
│   ├── 07-graxpert/
│   ├── 08-polaris/
│   ├── 09-indi-web/
│   ├── 10-systemd/
│   └── 11-firstboot/
└── export-image/  # produces polaris-rpi-VERSION.img.xz
```

Each numbered subdir is a pi-gen "sub-stage" that runs as a shell
script inside the build chroot. Order matters; later stages depend on
earlier ones (e.g. polaris needs dotnet which needs system-deps).

## Stage details

### 00-system-deps

Apt packages that everything else needs:

```
libicu-dev libssl-dev libfontconfig1 curl unzip lsof
build-essential cmake git pkg-config         # if compiling drivers later
```

### 01-dotnet

Install .NET 10 ASP.NET Core runtime to `/opt/dotnet`. System-wide
(not user-scoped) because this is a distributed image. Symlink to
`/usr/local/bin/dotnet` so systemd finds it without env tweaks.

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | \
  bash /dev/stdin --channel 10.0 --runtime aspnetcore \
                   --install-dir /opt/dotnet
ln -sf /opt/dotnet/dotnet /usr/local/bin/dotnet
```

### 02-indi

Two options to decide between:

- **apt indi-bin + indi-full**: fast, well tested, but lags upstream by
  6 to 18 months. Misses newest camera drivers.
- **Source build from indilib/indi + indi-3rdparty**: latest drivers,
  works with cameras released this year, but adds ~30 minutes to the
  build and ~500 MB to the image.

Recommend source build for the distributed image (users are likely to
have recent gear). Pin to a known-good commit; rev periodically.

### 03-astap

Download ASTAP .deb from the official site, install via dpkg. Then
download H17 star database (290 MB) to `/opt/astap/`. This is the
single biggest chunk of image size; cannot be avoided if we want
plate-solving to work offline.

### 04-phd2

Apt install. Same lag tradeoff as INDI but PHD2 evolves slowly and the
apt version is fine.

### 05-siril

Apt install.

### 06-xpra

Apt install. Needed for the embedded PHD2 GUI feature on the Pi.

### 07-graxpert

Download latest stable Linux release from GitHub releases into
`/opt/graxpert/`. Symlink versioned binary to `/opt/graxpert/graxpert`
so the path is stable across image versions. AI models are NOT
bundled (GraXpert downloads them on first use); document this in the
first-boot welcome so users on internetless rigs know to run a
processing op once at home.

License note: GraXpert is GPLv3. Bundling the binary in our image is
allowed; we redistribute via the same license terms. Document in
LICENSES.md in the image.

### 08-polaris

Copy the latest pre-built `polaris-linux-arm64.tar.gz` from the
GitHub releases into `/opt/polaris/`. Chown to the polaris user
(created in 10-systemd). Build pipeline parameter: `POLARIS_VERSION=latest`
or pin to a specific tag.

### 09-indi-web

Install via pip into a system venv (the image is single-tenant; no
need for pipenv ceremony). `/opt/indi-web-venv/bin/indi-web`.

### 10-systemd

Create the `polaris` user, drop in the systemd unit file (`polaris.service`),
enable it. Same unit as in the manual Pi setup guide but pointing at
`/opt/polaris/NINA.Polaris` and `/opt/dotnet/` paths.

Polaris's `appsettings.json` is seeded with:

```json
{
  "IndiWeb": {
    "ExecutablePath": "/opt/indi-web-venv/bin/indi-web",
    "AutoStart": true
  },
  "Profile": {
    "ImageOutputDir": "/home/polaris/Pictures/Polaris"
  }
}
```

### 11-firstboot

Cosmetic + setup wizard:

- Set hostname to `polaris` (overridable via Raspberry Pi Imager
  advanced options when the user flashes)
- Pre-create `/home/polaris/Pictures/Polaris/` for image library
- Drop a welcome MOTD with "Open http://polaris.local:5000 in your
  browser"
- Create `/boot/firstrun.sh` hook for any one-time config (resize
  filesystem to fill SD card, etc., though Pi OS handles resize
  automatically)

## Image size budget

Rough estimate:

| Layer | MB |
|---|---|
| Raspberry Pi OS Lite base | 700 |
| .NET 10 ASP.NET Core runtime | 80 |
| INDI + drivers | 400 |
| ASTAP + H17 catalog | 320 |
| PHD2 | 30 |
| Siril | 90 |
| xpra | 80 |
| GraXpert (no models) | 250 |
| Polaris binary | 100 |
| indi-web | 5 |
| Total uncompressed | ~2.1 GB |
| Compressed (.img.xz) | ~700 MB to 900 MB |

Fits comfortably on a 4 GB SD card with room for OS expansion.

## First-boot user experience

What the user sees the first time they boot the flashed card:

1. Pi boots, Raspberry Pi OS expands the filesystem to fill the card
   (automatic, ~30 seconds).
2. `polaris.service` starts. systemd logs to journal.
3. Polaris listens on port 5000 within ~15 seconds of boot.
4. User opens browser on their laptop to `http://polaris.local:5000`
   (or by IP from router).
5. Polaris home page loads. First visit triggers the location-setup
   modal (lat/lng for altitude charts and weather forecast).
6. User goes to RIGS tab, opens INDI Drivers section, picks a Profile
   in indi-web (which is already running and embedded), ticks the
   drivers, clicks Server > Start.
7. Connects camera/mount/focuser in the Polaris RIGS dropdowns.
8. Done. First frames in <5 minutes from flashing.

Things the user does NOT need to do:

- Type any commands
- SSH in
- Run apt
- Configure systemd
- Edit appsettings.json
- Install indi-web
- Download ASTAP catalogs

Things the user MAY still want to do:

- Set WiFi credentials at flash time (Raspberry Pi Imager advanced
  options) so the Pi joins their network
- Set hostname / username / password at flash time (same dialog)
- Mount a USB SSD at /mnt/astro and re-point Polaris's image library
  via the FILES tab

## Distribution

- **GitHub Releases**: upload `polaris-rpi-VERSION.img.xz` as a release
  asset. Image is large (~800 MB) but well within GitHub's 2 GB
  per-file limit.
- **Raspberry Pi Imager custom list**: host a `os_list.json` file
  somewhere stable (GitHub Pages on the polaris repo works) so Imager
  shows "N.I.N.A. Polaris" under "Other specific-purpose OS". This is
  the gold-standard UX; the user picks it from a list inside Imager
  without manually downloading.
- **Polaris home page banner**: link to "Download SD card image" right
  on https://nina-polaris.example/

Distributed image carries:

- All Polaris source code is MPL 2.0, redistributable.
- INDI is LGPL/GPL, redistributable, include LICENSE.
- PHD2 is GPLv3, redistributable, include LICENSE.
- Siril is GPLv3, redistributable, include LICENSE.
- GraXpert is GPLv3, redistributable, include LICENSE.
- xpra is GPLv2, redistributable, include LICENSE.
- .NET runtime is MIT.
- Raspberry Pi OS base is various open-source licenses.

Bundle all LICENSE files into `/opt/polaris/LICENSES/` in the image
and link from the Polaris UI footer.

## Update story

A user with the image installed needs a way to update Polaris without
re-flashing:

- **In-app update button**: Polaris adds a Settings > System > Update
  Polaris button that downloads the latest `polaris-linux-arm64.tar.gz`,
  extracts over `/opt/polaris/`, restarts the service. Already partially
  implemented as the manual `curl | tar xz` flow in the setup guide;
  needs UI wiring.
- **System updates (apt)**: `sudo apt update && sudo apt upgrade` runs
  as normal; user does this via SSH when desired.
- **Full image rebuild**: ship a new SD image for major changes
  (.NET runtime bump, INDI major version, etc.). User re-flashes
  every 6 to 12 months.

## Build script invocation

```bash
# Once: clone pi-gen
git clone https://github.com/RPi-Distro/pi-gen.git
cd pi-gen

# Copy our stage in (this dir, plus skip files for stages we don't need)
cp -r /path/to/nina-polaris/image-build/stage-polaris ./stage-polaris
touch ./stage3/SKIP ./stage4/SKIP ./stage5/SKIP
echo 'IMG_NAME="polaris"' > config
echo 'STAGE_LIST="stage0 stage1 stage2 stage-polaris export-image"' >> config
echo 'POLARIS_VERSION=v0.42.0' >> config

# Build (takes 30 to 60 minutes the first time)
sudo ./build.sh
# or via Docker (recommended on non-Debian hosts):
sudo ./build-docker.sh

# Output: ./deploy/polaris-YYYY-MM-DD-armhf-lite.img.xz
```

CI: a GitHub Actions workflow on the polaris repo can run pi-gen in
Docker, push the resulting `.img.xz` to a release on tag pushes.
Build artifact is ~800 MB so build time and storage are nontrivial;
maybe only run on `v*` tags, not every PR.

## Open questions

Decisions to make before scaffolding the stage scripts:

1. **INDI: apt or source?** Apt is faster to build, source has more
   drivers. Recommend source.
2. **APASS catalog (PCC)**: bundle the 80 MB SQLite file or download
   on first use? Bundle. Adds 80 MB but PCC works offline.
3. **GraXpert AI models**: bundle (~1.5 GB) or download on first run?
   Download. Image size doubles otherwise; users with internet at home
   can do the one-time download. Document it.
4. **WiFi pre-config**: rely on Raspberry Pi Imager's advanced options
   (gold standard, requires user to click the gear in Imager) or bake a
   captive portal that the Pi advertises as an AP on first boot? Stick
   with Imager; captive portal adds 200+ lines of NetworkManager
   config and a hostapd setup.
5. **Default user/password**: Pi OS no longer ships with default
   `pi:raspberry` credentials. User MUST create a user in Imager.
   Our image inherits that behavior, which is correct security-wise.
6. **HTTPS by default**: should the image auto-run `--setup-https` so
   port 5001 with self-signed cert is up out of the box? Recommend yes;
   makes WebGPU work without extra steps. User has to trust the cert
   on each client device once.
7. **Tested Pi models**: Pi 4 (4/8 GB) and Pi 5 are first-class. Pi 3
   technically works but ONNX models will be painful (no WebGPU on
   client matters less, but server-side stretching is slow). Document
   "Pi 4 8 GB or Pi 5 recommended" in the image's MOTD.
8. **Multi-arch?** Pi 4 and Pi 5 both run aarch64 64-bit images, so
   one image covers both. Pi Zero 2 W is also aarch64 but RAM-limited
   to 512 MB; document as unsupported.
9. **Plugins**: Polaris has a plugin system. Ship empty or pre-load
   common plugins? Ship empty; users add via Settings.
10. **Telemetry**: any phone-home? **None**, by design. The image is
    fully airgap-capable except for first-time GraXpert model download.

## Next steps

Once the open questions are answered:

1. Create `image-build/stage-polaris/` skeleton with the 11 sub-stages
   stubbed out.
2. Implement each sub-stage's `00-run.sh` script.
3. Test build locally on a Linux host (or Docker on Windows/macOS via
   pi-gen's `build-docker.sh`).
4. Flash the output `.img.xz` to a Pi 4 or 5, boot, verify the
   end-to-end first-boot experience matches the doc.
5. Write `docs/user-guide/sd-image.md` for end-user flash instructions.
6. Wire up GitHub Actions to publish on tag pushes.
7. Host `os_list.json` for Raspberry Pi Imager integration.
8. Announce.

## See also

- [Raspberry Pi 4 / 5 manual setup](../docs/user-guide/raspberry-pi-setup.md),
  the recipe this image automates
- [pi-gen documentation](https://github.com/RPi-Distro/pi-gen)
- [Raspberry Pi Imager custom OS list spec](https://www.raspberrypi.com/documentation/computers/raspberry-pi.html#raspberry-pi-imager-os-list-json)
