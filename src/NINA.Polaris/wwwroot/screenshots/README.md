# HELP tab screenshots

Drop PNGs here to fill in the tutorial slots inside the in-app HELP
tab. Files are served by `UseStaticFiles` at `Program.cs:310`, so any
PNG you save here is immediately reachable at
`/screenshots/<topic>/<NN>-<slug>.png` without rebuilding.

## Naming convention

```
wwwroot/screenshots/
├── capture/        <- "Capture to export" stepper
│   ├── 01-welcome.png
│   ├── 02-rigs.png
│   ├── 03-location.png
│   ├── 04-polar.png
│   ├── 05-focus.png
│   ├── 06-sky.png
│   ├── 07-slew-center.png
│   ├── 08-guide.png
│   ├── 09-sequence.png
│   ├── 10-live.png
│   ├── 11-studio.png
│   └── 12-editor.png
├── first-night/    <- "First night" stepper
│   ├── 01-browser-cert.png
│   ├── 02-password.png
│   ├── 03-location.png
│   ├── 04-wifi.png
│   └── 05-first-device.png
├── lrgb/           <- LRGB mono pipeline
├── planetary/      <- Planetary / lucky imaging
└── pcc/            <- Photometric color calibration
```

The path each step expects is printed inside the in-app placeholder
when the file is missing, so you can copy it straight from the
browser.

## How to capture

1. Sign in to Polaris.
2. Navigate to the tab the step describes (the Help card has a
   one-click "Open X tab" button).
3. Take a screenshot of the full panel area (not the whole browser,
   skip the URL bar). 16:9-ish aspect is ideal; the card scales the
   image to its width.
4. Save with the path printed on the placeholder card.
5. Hard-refresh the browser, the placeholder is replaced by your
   image.

PNG preferred (lossless, sharp UI). JPEG works too. Keep individual
files under ~500 KB so the HELP tab stays snappy on slow Pi WiFi.

## Tips

- Use a 1440×900 or 1920×1080 viewport for consistent aspect.
- Enable a dark-themed sky on screenshots that include the canvas
  (cleaner contrast in the dark UI).
- For modals + popovers: open them BEFORE the screenshot so the
  context is captured in one shot.
- Annotations (red circles, arrows) are fine but use a single
  obvious marker per shot.
