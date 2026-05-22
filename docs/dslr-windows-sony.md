# Sony α-series on Windows + Linux (Skeleton — Open Work)

> **Status:** the Sony driver in this build is a skeleton. The
> Camera card recognises the driver, lists it as *(not installed)*,
> and shows this page as the install banner — but the actual
> capture path is a stub. Contributions welcome.

## Why Sony is the most attractive vendor driver to finish

Unlike Canon EDSDK and the Nikon stack (Windows-only), Sony's
**Camera Remote SDK** (SCRSDK) v2.x ships native binaries for
both **Windows** and **Linux**, including ARM64. That makes it
the only vendor SDK we can ship to Raspberry Pi users running
Polaris headless — Canon and Nikon DSLR users on Linux need to
go through the INDI gphoto path instead.

## What's already in place

- `src/NINA.Camera.SonySdk/` project skeleton with the right
  cross-project references. **No `<SupportedOSPlatform>` attribute**
  (intentionally) so the assembly compiles on Linux too — the
  actual runtime SDK probe handles the per-host decision.
- `SonySdkCamera : ICamera` with the right shape: ISO options
  (50..102400), `Capabilities.Dslr`, `Gain` aliased to ISO so the
  status broadcast renders the right field.
- `SonySdkDiscovery.Enumerate()` and `SonySdkRegistry.IsAvailable`
  stubs returning empty/false so the Equipment UI surfaces the
  install banner without crashing.
- `EquipmentManager.SelectCamera` and the camera-drivers endpoint
  include the `sony-sdk` entry; once the binding lands here the
  driver becomes selectable end-to-end with no other wiring
  changes.

## What's needed to make it real

1. Register on
   <https://developer.sony.com/imaging-products/camera-remote-sdk/>
   (free, requires accepting the SDK licence).
2. Download SCRSDK v2.x for the platforms you care about
   (Windows x64, Linux x64, Linux ARM64 if you want Raspberry Pi
   support).
3. Implement the native bindings under
   `src/NINA.Camera.SonySdk/Native/`. The SDK provides a
   C-style API surface (`SCRSDK::Init`, `EnumCameraObjects`,
   `Connect`, `SetDeviceProperty`, `SendCommand(S1Shooting)`,
   etc.) that's easier to P/Invoke than the C++ class layouts in
   Canon EDSDK and Nikon Imaging SDK.
4. Wire up `SonySdkDiscovery.Enumerate()` against
   `EnumCameraObjects` so the UI's **Detect** button populates
   the camera dropdown.
5. Wire up `SonySdkCamera.ConnectAsync` /
   `CaptureAsync` against the SDK's connect + shutter +
   transfer flow.
6. Flip `SonySdkRegistry.IsAvailable` to return true on
   successful `SCRSDK::Init`.

## Supported bodies

SCRSDK v2.x covers (model availability depends on the SDK
version you download):

- α7 III / α7 IV
- α7R III / α7R IV / α7R V
- α7S III
- α9 II / α1
- α7C / α7C II / α7C R
- ZV-E1 / ZV-E10 II
- FX3 / FX30
- α6700

Older bodies (α7 II, α6500, etc.) are not supported by SCRSDK
and need to be driven through the Sony Imaging Edge Remote
Control app via a screen-scrape adapter — not in scope here.

## Capture-path expectations

Match the Canon driver's shape so the rest of Polaris doesn't
need to change:

- Capture in RAW + JPEG mode on the camera (Sony calls it RAW +
  JPEG, ARW + JPEG, or RAW + Small JPEG).
- Pull both assets on each shutter trigger via the SDK's content
  transfer callback.
- Attach the ARW bytes to the returned `IImageData` via
  `IHasRawFile.RawFileBytes` + `.arw` extension.
- Decode the JPEG to a Rec.601 luminance `ushort[]` for the live
  preview (SkiaSharp, same pattern as `CanonEdsdkCamera`).
- Map the requested exposure to the closest shutter-speed enum
  on the body, or fall back to Bulb (Sony exposes Bulb as a
  shutter-speed enum value).

## Once the binding works

In Polaris **Equipment** → Camera card, pick the **Sony (Camera
Remote SDK)** entry → **Detect** → pick the body → **Connect**.
Captures land in
`{rig}/lights/{target}/{filter}/{session}/IMG_*.arw` exactly
like Canon ones do as CR2.

## Tips for tethered Sony sessions

- USB-PD power: Sony α bodies accept USB Power Delivery — a
  PD-capable battery pack or wall adapter (≥ 9V output) powers
  the body during long sessions. The internal NP-FZ100 stays
  charged at the same time.
- "Connect" mode: set USB Connection on the camera body to **PC
  Remote**, not Mass Storage or MTP. Without this the SDK won't
  see the camera.
- Tethering apps that fight Polaris: close Sony Imaging Edge
  Desktop / Remote before connecting — same single-session-
  at-a-time constraint as Canon and Nikon.
- Body firmware: newer α bodies often need firmware updates to
  match newer SCRSDK versions. The SCRSDK release notes list the
  minimum firmware per body.

## EULA reminder

Sony Camera Remote SDK binaries are not redistributable. Same
arrangement as Canon and Nikon: users register, accept the EULA,
download the SDK, drop the libraries into `plugins/sony-sdk/`.
The Polaris-side wrappers in this repo are MPL 2.0 and ship the
P/Invoke surface only — no Sony code.
