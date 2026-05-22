# Nikon DSLR / Mirrorless on Windows (Skeleton — Open Work)

> **Status:** the Nikon driver in this build is a skeleton. The
> Camera card recognises the driver, lists it in the dropdown as
> *(not installed)*, and shows this page as the install banner —
> but the actual capture path is a stub. Contributions welcome.

## What's already in place

- `src/NINA.Camera.NikonSdk/` project skeleton with the correct
  `<SupportedOSPlatform>windows</SupportedOSPlatform>` and
  cross-project references.
- `NikonSdkCamera : ICamera` with the right shape: ISO options
  (64..102400), `Capabilities.Dslr` (cooler/binning hidden in the
  UI), `Gain` aliased to ISO so the status broadcast renders the
  right field.
- `NikonSdkDiscovery.Enumerate()` and `NikonSdkRegistry.IsAvailable`
  stubs returning empty/false so the Equipment UI surfaces the
  install banner without crashing.
- `EquipmentManager.SelectCamera` and the camera-drivers endpoint
  already include the `nikon-sdk` entry; once the binding lands
  here, the driver becomes selectable end-to-end with no other
  wiring changes.

## What's needed to make it real

The Nikon software stack splits in two — pick the one matching
the bodies you want to support:

### Option A — MAID SDK (DSLR + older bodies)

The **Module-based API for Image Devices** is Nikon's classic
SDK. It uses `.md3` module files dynamically loaded from disk
(one per body class). Coverage: DSLR bodies through the D6 era,
plus Z7 / Z7 II on the newer modules.

**Recommended path:** start from
[MekNikon](https://github.com/meklarian/MekNikon)
(MIT-licensed C# wrapper, covers D500 + Z7/Z7 II already).
Either:
- vendor its native binding code into `src/NINA.Camera.NikonSdk/Native/`,
  or
- adopt MekNikon's NuGet (if published) and adapt
  `NikonSdkCamera` to drive its API.

Once that's wired up: flip `NikonSdkRegistry.IsAvailable` to
return true when the `.md3` modules are reachable, populate
`NikonSdkDiscovery.Enumerate()`, and the rest of Polaris picks
up the new driver automatically.

The MAID `.md3` modules are **not redistributable**. Same
arrangement as Canon EDSDK: users register on
[sdk.nikonimaging.com](https://sdk.nikonimaging.com/), accept
the EULA, download the SDK, and drop the modules into
`plugins/nikon-sdk/`.

### Option B — Nikon Imaging SDK (Z-series only)

Nikon's newer SDK targets the Z-mount mirrorless line (Z 5, Z 6,
Z 7, Z 8, Z 9 plus their II variants). API surface is C++ class-based,
which makes P/Invoke harder than MAID but the modern bodies are
better supported.

Same registration page at
[sdk.nikonimaging.com](https://sdk.nikonimaging.com/). Headers
+ samples + the matching SDK DLLs ship in the zip.

## Capture-path expectations

Match the Canon driver's shape so the rest of Polaris doesn't
need to change:

- Capture in RAW + JPEG mode on the camera.
- Pull both assets on each shutter trigger.
- Attach the NEF (or NEFs, plus the JPEG) bytes to the returned
  `IImageData` via `IHasRawFile.RawFileBytes` + `.nef` extension.
- Decode the JPEG to a Rec.601 luminance `ushort[]` for the live
  preview (SkiaSharp, same pattern as `CanonEdsdkCamera`).
- Map the requested exposure to the closest discrete Tv enum on
  the body, or fall back to Bulb (most Nikon bodies expose Bulb
  via the SDK as a special shutter-speed enum value).

## Once the binding works

In Polaris **Equipment** → Camera card, pick the **Nikon (MAID
SDK)** entry (or whichever you implemented) → **Detect** → pick
the body → **Connect**. Captures land in
`{rig}/lights/{target}/{filter}/{session}/IMG_*.nef` exactly
like Canon ones do as CR2.

## Tips for tethered Nikon sessions

- USB-PD power: the EH-7P + EP-5B trigger keeps Z bodies awake
  during long sessions. D-series bodies need the corresponding
  EH-5B + EP-5 dummy battery kit.
- Tethering apps that fight Polaris: close Nikon Camera Control
  Pro 2, Nikon NX Tether, and similar tools before connecting —
  same single-session-at-a-time constraint as Canon EDSDK.
- The MAID `.md3` modules are version-locked to specific
  bodies. New camera models need fresh `.md3` files from a
  Nikon SDK refresh.

## EULA reminder

Nikon's SDK DLLs and `.md3` modules are not redistributable.
The Polaris-side wrappers in this repo are MPL 2.0 and ship the
P/Invoke surface only — no Nikon code.
