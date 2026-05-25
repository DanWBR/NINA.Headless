# AI processing (ONNX in the browser)

Polaris embeds the **GraXpert AI models** (Background Extraction,
Denoise, Deconvolution) and runs them in your browser via
[onnxruntime-web](https://onnxruntime.ai/docs/tutorials/web/). The
server hosts the model files; any client browser fetches them on
demand, caches them in IndexedDB, and runs the inference locally.

This decouples Polaris from the GraXpert CLI subprocess — works on
any device (laptop, Android, iOS 16.4+) without installing GraXpert
on the host, and keeps the server's CPU free for capture/guiding.

## Setup

1. **Get the models**. Polaris does not ship the `.onnx` files;
   they're CC BY-NC-SA 4.0 (non-commercial, attribution, share-alike).
   Clone the [GraXpert repo](https://github.com/Steffenhir/GraXpert)
   to any path — the models ship inside under `models/`. Or copy
   the five `model.onnx` files into a folder of your own with the
   GraXpert layout:

   ```
   {your-root}/
     bge-ai-models/1.0.1/model.onnx
     denoise-ai-models/2.0.0/model.onnx
     denoise-ai-models/3.0.2/model.onnx
     deconvolution-stars-ai-models/1.0.0/model.onnx
     deconvolution-object-ai-models/1.0.1/model.onnx
   ```

2. **Point Polaris at the models**, in priority order:

   **(a) Drop them into the bundled folder** (zero-config — recommended
   for Pi / mini-PC deploys): copy the layout above into
   `src/NINA.Polaris/wwwroot/graxpert/models/` (or the published
   `wwwroot/graxpert/models/`). `OnnxModelRegistry` walks this folder
   at startup automatically. No Settings entry required.

   **(b) Or point a profile setting at an absolute path** (handy when
   your models live on an external SSD): Settings → **AI inference
   (ONNX)** → *Models path* → paste the absolute path → tab out.
   If both (a) and (b) exist, **the profile path wins** — the
   bundled folder is a fallback.

   First-time scan computes SHA-256 hashes (~5 seconds total on SSD).

3. **(Optional) Pick the default denoise version**. v2 (284 MB) is
   the default; v3 (456 MB) is sharper but heavier — useful on
   desktops, tight on iOS. Override via
   `Onnx:DefaultDenoiseVersion` in Settings.

That's it. The browser is now ready to run AI.

## Running on iPhone / iPad — the FP16 workflow

The Denoise pipeline tiles the master through hundreds of
`session.run()` calls. iOS Safari's per-tab memory budget (~1 GB
on iPhone) is too tight for the **FP32** Denoise models in their
default form — the tab dies silently with no error dialog ("crash
to home screen"). Two extra steps work around this:

### Step 1 — let Polaris pick the right backend per-model

iOS Safari's WebGPU EP has aggressive memory accounting — every
tensor allocated on the GPU counts against the page's budget. BGE
works (one `session.run()`, tiny GPU churn) but the FP32 Denoise
models tile for hundreds of calls and accumulate enough GPU buffer
churn to crest iOS Safari's ~1 GB per-tab limit.

Polaris auto-decides per model based on its on-disk size:

| Model size | iOS backend | Why |
|---|---|---|
| ≤ 200 MB | **WebGPU** | Fits comfortably; ~10× faster than CPU |
| > 200 MB | **WASM** (CPU) | Avoids the OOM cascade |

The Denoise v2 FP16 model is ~142 MB → WebGPU on iPhone. FP32
v2 (284 MB) and v3 (456 MB) → WASM. The decision is logged on
each load — DevTools console shows either
`Using WebGPU adapter: { vendor: 'Apple', ... }` or
`iOS + large model — forcing WASM (XXX MB > 200 MB iOS WebGPU cap)`.

No user action required. The 200 MB cut-off lives in
`IOS_WEBGPU_MAX_MODEL_BYTES` in `onnx-pipelines.js` — adjust if a
future iOS / iPadOS bumps the WebGPU budget.

### Step 2 — generate FP16 variants of the models

The FP32 Denoise model is 284 MB (v2) or 456 MB (v3). Plus the ORT
runtime overhead (~150-200 MB), this crests the iPhone Safari kill
threshold even with the buffer-shrinking work Polaris already does
on the JS side. Run the conversion script once to halve the weights:

```bash
pip install onnx onnxruntime onnxconverter-common
python scripts/quantize_onnx_models.py --only denoise --fp16
```

This walks `wwwroot/graxpert/models/`, finds every FP32 `model.onnx`,
and writes a sibling version directory with `-fp16` suffix:

```
denoise-ai-models/
├── 2.0.0/model.onnx        (284 MB, FP32 — original, kept)
├── 2.0.0-fp16/model.onnx   (142 MB, half precision — new)
├── 3.0.2/model.onnx        (456 MB)
└── 3.0.2-fp16/model.onnx   (228 MB — new)
```

Then trigger a rescan: either restart Polaris, or
`POST /api/onnx/rescan`, or click *Re-detect* in the Settings AI
panel. The Denoise modal dropdown now lists the FP16 variants
alongside the originals.

**On iOS**, the modal opens with the lightest available variant
pre-selected automatically (FP16 over FP32 over INT8). On desktop
it still picks FP32 by default — FP32 is fastest on WebGPU.

### What about INT8?

The script also supports `--int8`, which quarters the models
(~71 MB v2). **But INT8 does NOT work in the browser** — the
bundled ONNX Runtime Web distribution (`ort.webgpu.min.js`) ships
only the basic op set in its WASM execution provider; the
`QLinear*` / `DynamicQuantizeLinear` ops needed for INT8 graphs
are missing. Loading an INT8 model throws "no backend found" and
the failed-create allocation churn OOM-kills the iOS tab on the
way out.

INT8 models are still generated (and listed in the dropdown) for
completeness — if you ever ship a custom ORT Web build with the
quantized op set, they'll work. With the stock bundle: **stick to
FP16 on iOS**, FP32 elsewhere.

### Memory math, demystified

For a 3000×2000 RGB master with Denoise v2 on iOS:

| Component | FP32 | FP16 |
|---|---|---|
| Pixels (Uint16 RGB, held throughout) | 36 MB | 36 MB |
| Combined output (Uint16 RGB) | 36 MB | 36 MB |
| Per-channel scratch + tile tensor | 14 MB | 14 MB |
| **ONNX model weights** | **284 MB** | **142 MB** |
| ONNX runtime overhead (WASM) | ~150 MB | ~150 MB |
| **Peak total** | **~520 MB** | **~378 MB** |

FP16 drops the peak by ~140 MB — comfortably under the iPhone
Safari ceiling. Converting the FITS to PNG or shrinking pixels
would only save 18 MB; the model dominates, which is why model
quantization is the real fix.

## Running

### From the FILES tab

1. Select one or more FITS files.
2. Click **🌅 BGE** / **🔇 Denoise** / **✨ Decon** in the toolbar.
3. The modal opens with **Run in browser** ticked (default when an
   ONNX model is available + Prefer-CLI is off).
4. Click **Start**.

First run per model downloads the bytes from the server (~200-500
MB depending on op) and caches them in IndexedDB. Inference time
on a laptop with WebGPU is ~30 seconds for BGE on a 24-megapixel
master, ~3-5 minutes for Decon. Each input becomes a sibling
`{stem}_bge.fits` / `{stem}_denoise.fits` / `{stem}_decon.fits`
next to the source.

### From the EDITOR

Open any FITS, scroll the right panel down to the new **AI (GraXpert)**
section. Three buttons — Background / Denoise / Deconvolve. Each:

- Processes the editor's current source FITS,
- Saves a sibling FITS next to the source,
- Auto-loads the sibling as the new editor source,
- Preserves your Light/Color/Effects slider state across the swap.

The original source FITS is never modified. Ctrl+Z after an AI
swap reverts your edits (file remains on disk).

## First-use licence

The very first time you trigger any AI op per browser, a modal
prompts you to acknowledge the **CC BY-NC-SA 4.0** licence of the
GraXpert AI weights. Non-commercial use, attribution, share-alike
on derivatives. Polaris itself is MPL 2.0 and does not redistribute
the model files. Click **I agree** to continue; consent is cached
in localStorage + the server profile so subsequent sessions and
other browsers on the same profile don't re-prompt.

## Browser requirements

| Backend | Browser | Notes |
|---|---|---|
| **WebGPU** | Chrome/Edge 113+, Safari 16.4+ (macOS/iOS), Firefox 121+ behind flag | Fast — uses the device GPU. Default when supported. |
| **WASM SIMD** | All modern browsers | CPU-only fallback. ~5-10× slower than WebGPU but works everywhere. |

The runtime picks WebGPU > WASM SIMD > scalar WASM automatically,
**except on iOS** where Polaris force-disables WebGPU (see the
FP16 workflow section above for why). Detect what you got via
DevTools console: `OnnxRegistry.__lastBackend`.

### Memory caveats

Each model loaded into the runtime takes ~250-500 MB of resident
memory (FP32). On iOS Safari the per-tab budget is ~1 GB on
iPhone, looser on iPad. Loading Denoise v3 (456 MB FP32) on
iPhone reliably OOM-kills the tab even with all of Polaris's
JS-side memory mitigations.

**On iOS, generate FP16 model variants** (see the FP16 workflow
section). FP16 halves the model size and the runtime memory cost
without measurably affecting output quality on astrophotography
frames.

The IndexedDB cache persists across sessions. Total disk cost in
the browser ~1.5 GB if you've used all five models (more if you
also have FP16 / INT8 siblings cached). Drop via **Clear cache**
in the Settings AI panel.

## Falling back to CLI

If your browser can't run the models for any reason (very old, no
WebGPU + no SIMD, OOM on a huge master) you can still use the
GraXpert CLI subprocess on the host:

Settings → AI inference (ONNX) → **Advanced** → tick *Prefer CLI
subprocess by default*. The FILES-tab modal now opens with
**Run in browser** unchecked; it's still per-run editable.

You can also run both side by side — install the CLI on the host
AND keep the ONNX path enabled. The toggle picks per invocation.

## How it works

```
┌─ Browser (any OS) ──────────────────────┐
│  onnx-pipelines.js                      │
│    OnnxRegistry.loadSession(family, v)  │
│      → IndexedDB lookup by SHA-256      │
│      → GET /api/onnx/model/...          │
│      → ort.InferenceSession.create()    │
│    BgePipeline.run(pixels, w, h, opts)  │
│      → MAD normalize → session.run →    │
│         denormalize → smooth → correct  │
└──────────────┬──────────────────────────┘
               │ HTTP (model bytes, pixels)
┌──────────────▼──────────────────────────┐
│  Polaris server                         │
│  OnnxModelRegistry                      │
│    1. profile's Onnx:ModelsPath if set  │
│    2. wwwroot/graxpert/models (bundled) │
│    3. nothing (UI shows configure tip)  │
│    walks recursively, infers            │
│    family/version from path layout      │
│    (also recognises -fp16 / -int8       │
│    quantization suffixes)               │
│    lazy SHA-256 hash cache              │
│  GET  /api/onnx/manifest                │
│  GET  /api/onnx/model/{family}/{version}│
│       ETag + Cache-Control: immutable   │
│  GET  /api/onnx/source-pixels?path=     │
│  POST /api/onnx/save (sibling FITS)     │
└─────────────────────────────────────────┘
```

The server is a thin CDN + a file decoder/encoder. All AI math lives
client-side. Bandwidth: ~1.5 GB once per browser session (cached
afterwards) + ~50-200 MB per file processed (raw uint16 round-trip).

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| **No models in Settings** | Path empty or wrong. Confirm with `dir <path>\bge-ai-models\1.0.1\model.onnx` (Windows) or `ls <path>/bge-ai-models/1.0.1/model.onnx` (Unix). |
| **Hash column stuck on "(pending)"** | Manifest never fully fetched — visit Settings → AI inference once with the tab open and the rescan triggers it. |
| **`OnnxRegistry is not defined` in console** | `/js/onnx-pipelines.js` failed to load. Check DevTools Network for 404 / MIME issues. |
| **WebGPU not available** | Browser too old or feature flag off. Inference falls back to WASM SIMD; everything still works, just slower. |
| **Run hangs at "downloading 0%"** | Server CORS / Relay caching the model with the wrong content-type. Try Clear cache + reload. |
| **First save fails with 500** | Source path not writable by the Polaris service account. Check `journalctl -u nina-polaris` (Linux) or the EventLog (Windows). |
| **iPhone Safari tab dies silently during Denoise ("crash to home screen")** | OOM kill. Generate FP16 model variants — see the *Running on iPhone / iPad* section. `python scripts/quantize_onnx_models.py --only denoise --fp16` then rescan. The modal will auto-select the FP16 variant on iOS. |
| **"No backend found" / "Failed to load `<model>`" toast** | Most often: you picked the `-int8` model variant. The bundled ORT Web WASM EP doesn't include INT8 ops. Switch the AI model dropdown to `-fp16` or the unsuffixed FP32 variant. |
| **Inference suspiciously slow on iPhone** | If you're on a FP32 model (>200 MB), Polaris forced WASM CPU to avoid OOM — that's ~5-10× slower than WebGPU. Switch to the FP16 variant in the AI model dropdown (or generate one via `quantize_onnx_models.py --fp16` if it doesn't exist) — FP16 fits the WebGPU threshold and gets the GPU speed-up. Don't switch tabs during a long run; iOS suspends JS in background tabs. |

## Out of scope today

- **Sample-point background extraction** (manual BGE point picking +
  RBF interpolation). v1 runs auto-BGE only; the user-driven UX
  variant ships in a follow-up phase.
- **Mosaic-aware tiling**. Decon currently tiles per file; a
  super-tile path that uses overlap blending across a mosaic
  pass is interesting but deferred.
- **Full-bit-depth editor integration**. The editor's working
  buffer is 8-bit display-stretched; the AI section operates on
  the raw 16-bit source by reloading the editor with the AI
  output. A v2 will tap the raw plane in-session.
- **Custom user-trained models**. Only the five GraXpert official
  models are recognised by the family/version layout parser.
