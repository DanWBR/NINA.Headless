<!--
  PLAN.md, translated history of every plan that shaped N.I.N.A. Polaris.

  This is the chronological design record: each major feature was first
  scoped here before any code landed. The most recent plan (CLST,
  client-side live stacking via WASM) is at the top; older plans follow
  in reverse-chronological order all the way back to the original
  gap-analysis that bootstrapped the project.

  Read top-to-bottom for "what we shipped most recently and why";
  jump by H1 anchor to revisit a specific feature's design rationale.

  Original written in Portuguese during planning sessions; translated to
  English for handoff. Code blocks, file paths, and identifiers preserved
  verbatim, only the prose around them was translated.
-->

# Current plan: Expanded sky-view catalogs (CAT-1..5)

> The full chain of plans between Pi 5 packaging and CAT is
> preserved below, translated from the Portuguese working notes.
> Reading order top-to-bottom (newest first): CAT → FW → SHUT →
> REFSUG → HELP → AUTH → MFOC → WIFI → CCALB → CC → GX → NET →
> ED → SWE → PA → SIM → Pi 5 packaging → CLST → LSTR → VIDEO →
> PHD2 deep → RIGS → PREVIEW → Activity bar → Siril+GraXpert →
> FILES → DSLR → Gap analysis → STUDIO → Weather → Tonight.
>
> Two shipped features don't have dedicated plan sections (they
> were small enough to ship without a formal plan file):
>
> - **CLOCK-1..3** (ClockSyncService + polkit + activity-bar
>   chip + Settings card). Detects > 30 s skew between
>   `Date.now()` on the client and `DateTime.UtcNow` on the
>   server (broadcast on the WS payload as
>   `server.utcNow`), pops a "Clock N off" chip the user can
>   click to push `chronyd` / `timedatectl set-time` via the
>   polkit-gated `/api/system/clock/sync` endpoint. Polkit rule
>   in `packaging/deb/etc/polkit-1/rules.d/55-polaris-time.rules`,
>   wired by postinst.
> - **INDI-WEB-1..4** (IndiWebManager service + reverse proxy
>   `/indi-web/*` + RIGS-tab iframe + SimulatorService
>   coexistence). Wraps the pip-installed `indi-web` daemon
>   (lifecycle + health probe like `Phd2GuiSessionService`),
>   reverse-proxies its UI under `/indi-web/`, embeds it in the
>   RIGS tab via iframe so the user can start/stop arbitrary
>   INDI drivers without dropping to a terminal.

## Context

The `SkyCatalogService` historically hardcoded ~150 objects in C#
(`AddMessier()` + `AddCaldwell()`: 110 Messier + ~30 Caldwell +
~20 popular NGC). Limitations:

- **Search failed for anything outside that top-150**, typing
  "NGC 7331", "Arp 273", or "Sh2-279" returned empty. Users had to
  open Stellarium externally, copy coords, and paste them into the
  Sky tab.
- **Filter panel** only listed types that appeared in those 150
  entries (Globular Cluster, Open Cluster, Emission Nebula etc),
  missing things like HII Region (Sh2), Peculiar Galaxy (Arp),
  Galaxy Group (HCG), Galaxy Cluster (Abell).
- **Tonight's Best** iterated only those 150, missing bright
  NGC/IC targets outside the Messier catalog.
- **Slew & Center via "Go to" cards on Tonight + RA/Dec search in
  the Sky tab** depended on the same source, so NGC targets fell
  outside its reach.

The stellarium-web-engine already labels NGC/IC/Messier on the
**map itself** (via HiPS tiles bundled under
`wwwroot/sky/data/skydata/dso/`), so when the user zooms in they
see the labels. But search / filter / Tonight runs against the
C# `SkyCatalogService` — that's the bottleneck this plan addresses.

Decisions (locked in via AskUserQuestion):

- **Storage**: SQLite + R*tree (the same pattern as
  `Services/Sky/ApassCatalog.cs`). Unlocks spatial queries
  (objects inside a FOV, nearest neighbour to a given RA/Dec)
  that pave the way for a future Mosaic-planner auto-suggest and
  plate-solve overlays.
- **v1 scope**: Caldwell + complete Messier + complete NGC/IC +
  ARP + Sharpless 2 + Abell PN + Hickson Compact Groups + Abell
  galaxy clusters. ~17k objects targeted, ~14.5k actually shipped
  (V/84 PN catalog deferred — Vizier only exposes B1950
  sexagesimal columns for it, would require a separate parse path).
- **Distribution**: bundled in the repo (under
  `wwwroot/catalogs/dso/`). The catalog is small enough (~2.6 MB)
  not to need a script-download step like APASS. Works offline at
  first boot, zero setup.
- **No map overlay**: the stellarium engine already covers the
  popular catalogs via HiPS. The extra layer powers search /
  filter / Tonight / slew only. Visible markers stay as a
  follow-up (CAT-6) if there's demand.

## Architecture

### Data source (build-time)

A `scripts/build-dso-catalog.py` orchestrator builds the SQLite DB
once on the release machine (not at runtime, the `.db` file is
committed). It downloads from:

1. **OpenNGC** master CSV at
   `https://github.com/mattiaverga/OpenNGC/raw/master/database_files/NGC.csv`
   (CC-BY-SA 4.0). Covers 13,226 NGC/IC objects with name, RA, Dec,
   type, V-mag, size (major/minor axis), constellation, and
   built-in Messier cross-references.
2. **Sharpless 2** via Vizier `VII/20/catalog` (313 HII regions,
   public domain via CDS).
3. **ARP peculiar galaxies** via Vizier `VII/192A/arplist` (~338
   Arp numbers, ~592 individual component rows).
4. **Hickson Compact Groups** via Vizier `VII/213/groups` (100
   groups).
5. **Abell-Corwin-Olowin galaxy clusters** via Vizier
   `VII/110A/table3`, magnitude-trimmed to m10 < 17 to keep the
   ~767 brightest of 2,712.

All Vizier downloads go through the `asu-tsv` REST endpoint with
`-out=_RAJ2000` + `-out=_DEJ2000` so we always get decimal-degree
coordinates regardless of the catalog's native epoch (B1900 /
B1950 / etc) — no precession code on our side.

The script also synthesizes Caldwell rows: an embedded mapping
table (109 entries) joins each Caldwell number to its underlying
NGC/IC entry, emitting a separate "C"-cataloged row pointing at
the same coordinates so a search for `C14` matches directly
without needing OpenNGC to carry the cross-ref column.

### SQLite schema

```sql
CREATE TABLE objects (
    id           INTEGER PRIMARY KEY,
    catalog      TEXT NOT NULL,    -- 'NGC' | 'IC' | 'M' | 'C' | 'Arp' | 'Sh2' | 'HCG' | 'AGC'
    catalog_id   TEXT NOT NULL,
    name         TEXT NOT NULL,
    common_name  TEXT,
    type         TEXT NOT NULL,
    ra_hours     REAL NOT NULL,
    dec_deg      REAL NOT NULL,
    magnitude    REAL,
    size_arcmin  REAL,
    constellation TEXT,
    aliases      TEXT
);
CREATE VIRTUAL TABLE objects_idx USING rtree(
    id, min_ra, max_ra, min_dec, max_dec
);
CREATE INDEX idx_objects_name      ON objects(name COLLATE NOCASE);
CREATE INDEX idx_objects_catalog   ON objects(catalog, catalog_id);
CREATE INDEX idx_objects_type      ON objects(type);
CREATE INDEX idx_objects_magnitude ON objects(magnitude);
```

### New service: `Services/Sky/DsoCatalog.cs`

Singleton. Mirrors `Services/Sky/ApassCatalog.cs` exactly: same
read-only connection per query, lazy `ObjectCount` cache, R*tree
bounding-box pre-filter + great-circle distance check for cone
searches.

Public API:

- `IsAvailable` / `ObjectCount` / `DbPath` — status + sanity check.
- `GetByNameAsync(name)` — exact-name (case-insensitive) or alias
  substring lookup.
- `SearchAsync(query, limit)` — prefix-match on name, LIKE on
  `common_name` + `aliases`, ranked by exact-match-first then
  magnitude-ascending.
- `FilterAsync(DsoFilter)` — combines type / catalog / constellation
  / magnitude range / dec range / limit, magnitude-ascending ranked.
- `QueryRegionAsync(raHours, decDeg, radiusDeg, magLimit, limit)`
  — cone search with RA-wrap handling (splits at 0h/24h).
- `GetCatalogsAsync()` / `GetTypesAsync()` — distinct-list helpers
  for the Atlas filter dropdowns.
- `LoadAllAsync(magCap = 12.0)` — streams every row brighter than
  the cap for `SkyCatalogService`'s lazy `AllObjects` cache.

### `SkyCatalogService` becomes a facade

Same public shape (`Search`, `Filter`, `GetByName`,
`GetObjectTypes`, `AllObjects`) so every existing caller
(`TonightsBestService`, `SkyEndpoints`, frontend search /
atlas wiring) keeps working. New ctor injects the `DsoCatalog`
singleton; parameterless ctor is preserved for tests / legacy
callers (DsoCatalog stays null in that case, every query falls
through to the existing hardcoded path unchanged).

When `_dso?.IsAvailable == true`:

- `Search / Filter / GetByName / GetObjectTypes` delegate to
  `DsoCatalog` and convert `DsoObject` → `CatalogObject` via
  `FromDso()`. `DsoObject.Magnitude` is nullable; the converter
  maps null → 99.0 sentinel so the existing `if (mag > 10) skip`
  gates in `TonightsBestService` and the Atlas filter hide them
  naturally.
- `AllObjects` lazy-loads at first access via
  `DsoCatalog.LoadAllAsync(magCap: 12.0)`, so the Pi 2/3 in-memory
  footprint stays ~1 MB (~5k rows) even though the DB holds 14.5k
  rows. Subsequent accesses hit the cached list. Thread-safe via
  a `_allLock` lock.
- A new `GetCatalogs()` helper feeds the Atlas filter's catalog
  dropdown.

`CatalogObject` gains 4 optional fields: `Catalog`, `CatalogId`,
`Constellation`, `SizeArcmin`. Defaults are safe (null), so JSON
round-trips for old clients don't break.

`CatalogFilter` gains `Catalog` + `Constellation` params. Both
no-op in the legacy fallback path.

### New endpoints (`Endpoints/SkyEndpoints.cs`)

- `GET /api/sky/catalog/catalogs` — list of distinct catalog
  sources (`['NGC','IC','M','C','Arp','Sh2','HCG','AGC']`). Feeds
  the Atlas filter's catalog dropdown.
- `GET /api/sky/catalog/filter` extended with `?catalogId=` +
  `?constellation=` (back-compat: old clients omit them).
- `GET /api/sky/catalog/near?ra=&dec=&radius=&maxMag=&limit=` —
  cone search. Returns 503 with a clear "run
  build-dso-catalog.py" message when the expanded DB is missing.

### Frontend (minimal)

The Atlas filter panel gains two new chips, visible only when
`atlasCatalogs.length > 0` (i.e. the expanded DB is loaded):

1. **Catalog** dropdown — "Any" + every source in the DB.
2. **Constellation** 3-letter IAU free-text input ("Cyg", "Ori",
   ...). Free text because the constellation set is fixed + tiny
   — quicker to type than scroll.

Both stay hidden on old installs so the UI doesn't show
broken-looking empty dropdowns. `atlasSearch()` forwards the new
params; `loadAtlasTypes()` also pulls the catalogs list at boot;
`resetAtlasFilters()` clears the new fields.

Search box behaviour unchanged — already pulls from
`SkyCatalogService.Search`, so the new ~14.5k entries are reachable
without any frontend changes.

## Phases (5 commits, all shipped)

### CAT-1 (`f49f059`): build script + bundled `dso.db` + LICENSE

- `scripts/build-dso-catalog.py` (~430 lines) — orchestrates
  downloads + ETL + writes `wwwroot/catalogs/dso/dso.db`.
  Dependencies: Python 3.8+ stdlib only (`urllib` + `sqlite3` +
  `csv`). Idempotent, caches downloads under `scripts/.dso-cache/`
  for fast re-runs.
- `wwwroot/catalogs/dso/LICENSE.txt` — attribution for OpenNGC
  (CC BY-SA 4.0) + per-catalog CDS Vizier license notes +
  Vizier acknowledgment boilerplate.
- The Web SDK's default `wwwroot/**` Content include picks the new
  tree up on `dotnet publish` automatically — no csproj change
  needed (same as `apass/*` + `sky/data/` already do).
- Committed `dso.db` (~2.6 MB, 14,555 rows). Per-catalog counts:
  NGC 7572, IC 5000, M 107, C 104, Arp 592, Sh2 313, HCG 100,
  AGC 767.

### CAT-2 (`7bf5b48`): `DsoCatalog` service + tests

- `src/NINA.Polaris/Services/Sky/DsoCatalog.cs` (~330 lines).
- Registered as singleton in `Program.cs`.
- `tests/NINA.Polaris.Test/DsoCatalogTests.cs` (11 cases):
  IsAvailable + count > 10k sanity; `GetByName` known objects
  (NGC 7331 / IC 5146 / M31 / C14 / Arp 273 / Sh2 279 / HCG 92)
  all resolve with valid RA/Dec/type; miss returns null;
  `SearchAsync` prefix match + brightest-first rank;
  `FilterAsync` by catalog and by type+mag; `GetCatalogs` returns
  all 8 sources; `GetTypes` includes Galaxy + Nebula;
  `QueryRegion` near NGC 7331 finds HCG 92 (~30 arcmin away);
  `LoadAll` mag cap filters dim rows. When the DB is missing,
  `SetUp` ignores all assertions with a clear pointer to
  `python scripts/build-dso-catalog.py`.

### CAT-3 (`7c0c54b`): `SkyCatalogService` as facade

- Refactored to inject `DsoCatalog?` and delegate when available.
- `CatalogObject` gains `Catalog` / `CatalogId` /
  `Constellation` / `SizeArcmin` optional fields.
- `CatalogFilter` gains `Catalog` / `Constellation`.

### CAT-4 (`43e9efa`): endpoints + Atlas UI chips

- `SkyEndpoints.cs` gains `/catalog/catalogs` + `/catalog/near`;
  `/catalog/filter` extended with `catalogId=` + `constellation=`.
- `index.html` Atlas filter panel gains catalog dropdown +
  constellation text input.
- `app.js`: state `atlasCatalogs`, methods `loadAtlasTypes`
  (extended to also pull catalogs), `atlasSearch` (forwards new
  params), `resetAtlasFilters` (clears new fields).

### CAT-5 (`005da5c`): docs + verify + README

- `docs/user-guide/sky-explorer.md` gains "Catalogs bundled"
  section with full per-source table + license + rebuild
  pointer + fallback note.
- README "Sky Catalog & Sky Atlas" bullet expands from "200+
  objects" to "~14,500" with the per-source breakdown.

## Files created

- `scripts/build-dso-catalog.py`
- `src/NINA.Polaris/Services/Sky/DsoCatalog.cs`
- `src/NINA.Polaris/wwwroot/catalogs/dso/dso.db` (~2.6 MB,
  committed)
- `src/NINA.Polaris/wwwroot/catalogs/dso/LICENSE.txt`
- `tests/NINA.Polaris.Test/DsoCatalogTests.cs`

## Files modified

- `src/NINA.Polaris/Services/SkyCatalogService.cs` — facade
  delegating to DsoCatalog, public shape preserved.
- `src/NINA.Polaris/Endpoints/SkyEndpoints.cs` —
  `/catalogs`, `/near`, `/filter` extended.
- `src/NINA.Polaris/Program.cs` — register `DsoCatalog`
  singleton.
- `src/NINA.Polaris/wwwroot/index.html` — Atlas filter panel
  (catalog dropdown + constellation input).
- `src/NINA.Polaris/wwwroot/js/app.js` — `atlasCatalog` /
  `atlasConstellation` state + extended methods.
- `docs/user-guide/sky-explorer.md` — Catalogs bundled section,
  Filters section updated.
- `README.md` — Sky Catalog & Sky Atlas bullet expanded.

## Reused code

- **`Services/Sky/ApassCatalog.cs`** — template literal for the
  SQLite + R*tree pattern, lazy-connection-per-query, Haversine
  cone search. Copied shape into DsoCatalog.
- **`scripts/download-apass.py`** — template for the orchestrator
  shell (stdlib-only Python, cache directory pattern,
  argparse + Path).
- **`Services/SkyCatalogService.CatalogObject` record** — only
  gained 4 optional fields, existing callers don't break because
  the new properties have safe defaults.
- **`Endpoints/SkyEndpoints.cs:/api/sky/catalog/filter`** —
  endpoint kept, extended with 2 new optional query params.
- **stellarium-web-engine HiPS DSO tiles** — not touched. Still
  labels NGC/IC/M on the map. The DsoCatalog layer powers
  search / filter / Tonight only.
- **`TonightsBestService`** — iterates `catalog.AllObjects`
  unchanged. Picks up the new ~5000 mag≤12 objects automatically.
- **Frontend `atlasSearch()`** — only added the 2 new params to
  the querystring when set. Result rendering unchanged (already
  shows name / type / magnitude / commonName).

## End-to-end verification

### Build + tests

- `python scripts/build-dso-catalog.py` on the release machine
  produces `dso.db` ~2.6 MB with 14,555 entries
  (`SELECT COUNT(*) FROM objects`).
- `dotnet build src/NINA.Polaris/NINA.Polaris.csproj` — clean.
- `dotnet test tests/NINA.Polaris.Test --filter "FullyQualifiedName~DsoCatalog"`
  — 11 new cases pass.

### Smoke API

- `curl https://localhost:5000/api/sky/catalog/search?query=NGC+7331`
  returns RA/Dec/mag/type/aliases for NGC 7331 (mag 9.4).
- Same for `Arp+273`, `Sh2-279`, `HCG+92`, `IC+5146`, `M31`, `C14`.
- `curl https://localhost:5000/api/sky/catalog/catalogs` returns
  `['AGC','Arp','C','HCG','IC','M','NGC','Sh2']`.
- `curl 'https://localhost:5000/api/sky/catalog/filter?catalogId=Arp&limit=1000'`
  returns 592 entries.
- `curl 'https://localhost:5000/api/sky/catalog/near?ra=22.617&dec=34.4&radius=2&maxMag=14'`
  returns both NGC 7331 and HCG 92 (Stephan's Quintet ~30' away).

### Smoke UI

- SKY tab → Atlas dropdown gains Catalog entry
  (NGC/IC/M/C/Arp/Sh2/HCG/AGC).
- Filter by `catalog=Sh2` → lists Sharpless 2 nebulae.
- Search "NGC 7000" → result, click → Slew & Center works.
- Tonight tab gains many more cards (not Messier-only).
- With `dso.db` deleted manually → service falls back to hardcoded
  150-object list, app still usable.

## License + size notes

- **OpenNGC**: CC BY-SA 4.0, share-alike. Polaris is MPL 2.0
  (code); data in a separate license is fine to bundle as long
  as (1) attribution preserved (`LICENSE.txt` adjacent), (2) any
  modifications to the original data remain CC BY-SA. Our script
  only normalises the schema, doesn't alter values → still
  CC BY-SA original.
- **Vizier sources**: CDS Strasbourg redistributes public-domain
  catalogs (Sharpless, ARP, Hickson, Abell ACO). Attribution
  recommended ("This research has made use of the VizieR catalogue
  access tool, CDS, Strasbourg, France"), shipped in
  `LICENSE.txt`.
- **Bundle size**: 2.6 MB. `.csproj` already includes
  `wwwroot/**` in publish output, so `.deb` + Docker image grow
  by ~3 MB. Negligible vs APASS (80 MB optional) or skydata
  HiPS (~300 MB).
- **Performance**: SQLite + R*tree responds to queries in &lt; 10 ms
  even on Pi 2/3 (proven with APASS at 5M rows). Boot adds ~50 ms
  to open + sanity-check.

## Out of scope (deferred)

- **Map markers / overlay** for niche catalogs (ARP / Sh2 / HCG)
  on the stellarium engine. The engine already has NGC/IC/M
  natively via HiPS; ARP + Sh2 markers would need to push geojson
  + a toggle UI. Follow-up CAT-6.
- **PN-G / Strasbourg-ESO PN catalog (V/84)** — attempted but
  Vizier only exposes B1950 sexagesimal columns for it; would
  need a sexagesimal-string parse path in
  `ingest_vizier_tsv`. OpenNGC already covers the PNe with
  NGC/IC designations.
- **Automatic refresh** script that updates the `.db` by
  re-downloading OpenNGC / Vizier. For now manual re-run at
  release.
- **Multi-language** common names (some catalogs have German /
  Latin names). v1 English only.
- **User-added objects** (comet positions, ad-hoc targets).
  Separate `comets.json` already covers comets; ad-hoc stays a
  distinct feature.

---

# Previous plan: Flat Wizard UI (FW-1..3)

> Previous plan (SHUT-1..5, Polaris Shutter) preserved below.

## Context

Flat capture is essential to a normal workflow: divides the lights
to normalise vignetting and dust motes. Polaris already had a
`FlatWizardService` doing a binary search on exposure until the
target median ADU (default 30000) was hit, then captured N frames
per filter and persisted the trained time in `trained-flats.json`
for the next session to reuse.

Until FW-1..3, that service was reachable only via curl to
`/api/flatwizard/start` — users operating Polaris from the browser
had no way to run flats without dropping to a terminal. This plan
adds the missing UI panel.

**Decisions** (AskUserQuestion):
- **Location**: sub-tab inside AUTORUN. AUTORUN already got a
  2-column sidebar in SHUT-4; wrap the existing pane in a
  "Sequence" | "Flat Wizard" tabstrip (same pattern as FOCUS /
  GUIDE / VIDEO). Thematically right: flats are part of a capture
  session, and trained exposures feed sequence items.
- **Per-rig persistence**: new fields on
  `EquipmentProfile.FlatWizard` (same pattern as
  `LiveStackTriggers`). Each rig (70 mm refractor vs 8" SCT) has
  its own illumination profile; per-rig TargetADU / tolerance /
  framesPerFilter / min-max exposure / binning / brightness
  defaults.

## Architecture

### Backend persistence

**`Services/FlatWizardSettings.cs`** (new):

```csharp
public class FlatWizardSettings {
    public int TargetAdu { get; set; } = 30000;
    public double Tolerance { get; set; } = 0.05;
    public int FramesPerFilter { get; set; } = 20;
    public double MinExposureSec { get; set; } = 0.1;
    public double MaxExposureSec { get; set; } = 30.0;
    public int Binning { get; set; } = 1;
    public int MaxSearchIterations { get; set; } = 10;
    public int PanelBrightness { get; set; } = 0;
}
```

`ProfileService.cs`: adds `FlatWizardSettings FlatWizard` to
`EquipmentProfile` with clone-path coverage (all fields copied
explicitly, same pattern used for `LiveStackTriggers` in LSTR-2).
Migration safe: a profile without the block loads defaults.

`EquipmentEndpoints.cs`: the PUT `/rigs/{id}` accepts `FlatWizard`
in the body and copies it defensively (null = leave the existing
field untouched).

`StatusStreamHandler.cs`: injects `FlatWizardService` and emits a
`flatWizard` sub-object on the 1 Hz payload (state + progress +
lastError). Progress is null when never-ran and idle.

### Frontend: AUTORUN tabstrip + Flat Wizard panel

`index.html` AUTORUN tab wraps the existing pane in a tabstrip
("Sequence" | "Flat Wizard") and adds a sibling `.flatwiz-pane`
with the same 2-column layout (`.live-pane,.preview-pane,
.autorun-pane,.flatwiz-pane` CSS comma-list pattern).

Left column (config):
- **Pre-flight**: ✓/⚠/✗ rows reading `selectedCamera`,
  `filterWheel.connected`, `flatDevice.connected`.
- **Filter pick**: multi-select chips fed from
  `filterWheel.filters` with select-all/clear helpers.
- **Settings form**: TargetADU, tolerance (%), frames per filter,
  min/max exposure, binning, max iterations. Each input
  debounce-saves (~400 ms) into the active rig via the existing
  `saveRig()` PUT.
- **Trained exposures table**: lazy-fetched from
  `GET /api/flatwizard/trained`, grouped by filter+binning.

Right column (sidebar, 320 px):
- **Panel brightness slider** (0..100), visible only when a flat
  device is connected. 0 = "don't touch the panel" (sky/T-shirt
  flats). When > 0, `POST /api/flatdevice/brightness` runs before
  the wizard kicks.
- **Polaris Shutter** (SHUT-1 component) with the new
  `flatWizardShutterCtx`. Tap = start, long-press = start (no
  loop concept), tap during active = abort. Ring shows composite
  progress `(filtersDone + frames-in-current-filter) /
  totalFilters`.
- **Live readout**: filter index/name, search attempt + median
  ADU, capture frame counter, per-filter results.

### Alpine wiring

- New state: `autorunTab` (`'sequence'` | `'flat'`),
  `flatWizard.{state, progress, lastError, trained, form fields,
  selectedFilters}`.
- Methods: `flatWizardOpenTab` (hydrates form + fetches trained),
  `SelectAll` / `ClearFilters` / `ToggleFilter`, `Save` (debounced
  rig PUT), `Start` (sets panel brightness first if connected,
  then POST `/start`), `Abort`, `ShutterCtx` / `ShutterProgress`
  / `ShutterDashoffset` / `ShutterCountdown`.
- WS handler absorbs `msg.flatWizard` each tick; auto-fires the
  shutter tick on idle→running; auto-refreshes trained cache on
  run completion.
- `_anyShutterActive()` includes flat-wizard running so the tick
  loop doesn't stop mid-run.

## Phases (3 commits)

### FW-1: backend persistence + WS payload
- `FlatWizardSettings.cs` + `EquipmentProfile.FlatWizard` +
  clone path + EquipmentEndpoints PUT + StatusStreamHandler
  flatWizard sub-object. Tests pin defaults + JSON round-trip +
  per-rig instance isolation.

### FW-2: AUTORUN tabstrip + Flat Wizard panel + Alpine wiring
- Tabstrip + `.flatwiz-pane` 2-col layout + pre-flight, filter
  pick, settings form, brightness slider, shutter, progress,
  trained table.
- CSS extends `.live-pane,.preview-pane,.autorun-pane` with
  `.flatwiz-pane`.

### FW-3: docs + README
- `docs/user-guide/flat-wizard.md` walkthrough (indoor with
  panel, outdoor sky, troubleshooting non-convergence).
- README "Flat Wizard" bullet expanded.

### FW polish (subsequent commit)
- Dark + tap-friendly form inputs (`min-height: 38px`,
  `padding: 9px 10px`, `font-size: 14px`) scoped to `.flatwiz-pane`
  to override `.settings-section`'s 400-px fixed height and white
  default form chrome. Later promoted to `.input-sm`/`.input-md`/
  `.search-input`/`.location-search-row input`/`.prop-row input`
  app-wide for consistent touch targets.
- An "Auto" pill on FLAT sequence items (separate small follow-up
  commit). When ON, the SequenceEngine asks
  `FlatWizardService.AutoFindExposureAsync` to resolve the
  exposure at runtime via the trained cache (fast path) or a
  binary search (cold path). Lets users without a filter wheel
  still benefit from auto-exposure flats.

## Reused code

- `FlatWizardService` (D6) — backend complete, zero change.
- 4 existing routes consumed verbatim.
- `LiveStackTriggers` pattern (LSTR-2) — mirror for the new
  `EquipmentProfile.FlatWizard` field (clone path,
  EquipmentEndpoints PUT, hydrate on tab-enter, debounced save).
- `polaris-shutter` component (SHUT-1) — reused with new ctx
  object. Zero new CSS, zero new gesture code.
- `.live-pane / .preview-pane / .autorun-pane` comma-list —
  `.flatwiz-pane` slots in and inherits 2-col layout + mobile
  fallback.

## Verification

- `dotnet build` clean; `dotnet test --filter ~FlatWizard` — new
  round-trip cases pass.
- Manual with INDI simulator: pre-flight ✓ all three rows; pick
  L+R+G+B; settings; tap shutter; ring fills as binary search
  converges then frame counter ticks; trained table refreshes on
  completion. Switch rigs and back; form retains its rig-scoped
  values.

## Out of scope (deferred)

- Activity bar chip for active Flat Wizard.
- Apply trained exposures automatically to the next sequence.
- Dawn/dusk sky-flat scheduling tied to solar altitude.
- Multi-binning sweep in one wizard run.
- Post-capture flat statistics (histogram per frame).

---

# Previous plan: Polaris Shutter (SHUT-1..5) — ASIAIR-style circular capture button

> Previous plan (REFSUG, refocus suggestion) preserved below.

## Context

Each tab (LIVE, PREVIEW, FOCUS Manual, VIDEO Capture, AUTORUN)
had its own horizontal action-button row controlling capture.
Visually inconsistent and the gestures (Capture vs Loop vs Stream
vs Stop vs Abort) varied per tab. ASIAIR / StellaVita inspiration:
**one large circular shutter** that unifies every capture surface
with a single gesture vocabulary:

- **Tap** when idle → snap (single capture)
- **Long-press** (≥600 ms) when idle → enter loop mode
- **Tap** when capturing → cancel / abort
- **Ring around the button** shows live progress
  `(now - startedAt) / exposureSec`
- **Inner icon** swaps: solid circle when idle, square (STOP)
  when capturing

Centered vertically + horizontally inside the existing
`.quick-controls` right sidebar (320 px). AUTORUN didn't have
that sidebar before — gets one as part of SHUT-4.

**Decisions** (AskUserQuestion):
- **Tap + long-press, not multiple buttons.** One shutter, gesture
  differentiates snap vs loop. Stream (PREVIEW) and Pause (AUTORUN)
  stay as small toggles below the shutter.
- **AUTORUN gets a right sidebar.** Sequence list on the left
  (flex:1), shutter + Pause/Resume + Add/Edit on the right (320 px).
- **Scope: LIVE + PREVIEW + FOCUS Manual + VIDEO Capture + AUTORUN.**
  FLAT WIZARD has backend but no browser UI yet — out of scope here
  (the UI panel comes next as FW-1..3).

## Architecture

### Reusable shutter component

Inline Alpine template (no Web Component, no build step). Each
tab passes a 4-property context:

```html
<div class="polaris-shutter"
     :class="{ 'shutter-active': isActive, 'shutter-arming': armingLoop }"
     @pointerdown="shutterPointerDown($event, ctx)"
     @pointerup="shutterPointerUp($event, ctx)"
     @pointercancel="shutterPointerCancel()"
     @pointerleave="shutterPointerCancel()">
    <svg class="shutter-svg" viewBox="0 0 100 100">
        <circle class="shutter-track" cx="50" cy="50" r="46"/>
        <circle class="shutter-progress" cx="50" cy="50" r="46"
                :style="`stroke-dashoffset: ${289 * (1 - progress)}`"/>
    </svg>
    <span class="shutter-icon">
        <svg viewBox="0 0 24 24" x-show="!isActive"><circle cx="12" cy="12" r="6"/></svg>
        <svg viewBox="0 0 24 24" x-show="isActive"><rect x="7" y="7" width="10" height="10" rx="1"/></svg>
    </span>
    <span class="shutter-countdown" x-text="countdownLabel"></span>
</div>
```

`ctx` object per tab:
```js
{
    isActive: () => /* bool */,
    progress: () => /* 0..1 */,
    countdownLabel: () => /* "3.2s" or "12/30" */,
    onTap: () => /* snap */,
    onLongPress: () => /* loop */,
    onAbort: () => /* stop */
}
```

### Ring via SVG `stroke-dasharray`

Perimeter of `r=46` circle ≈ 289. Set `stroke-dasharray="289 289"`,
`stroke-dashoffset="289"` when progress=0 (empty), `"0"` when
progress=1 (full). CSS `transition: stroke-dashoffset 0.1s linear`
for smooth motion. Rotate the `<svg>` `-90deg` so the ring starts
at 12 o'clock instead of 3 o'clock.

### Client-side countdown loop

Single `setInterval` at 50 ms when ANY shutter is active.
`_anyShutterActive()` aggregates `capturing | looping |
preview.busy | preview.looping | manualFocus.running |
videoRecording.recording | seqState==='running'` (+ later
`flatWizard.state==='running'`). Each capture method records
`*StartedAt = Date.now()` + `*Exposure = exposureSec`; the
computed progress reads `(Date.now() - startedAt) / exposureSec`
clamped to `[0,1]`.

### Long-press detection

Pointer-event pattern (touch + mouse):

```js
shutterPointerDown(ev, ctx) {
    if (ctx.isActive()) return;
    this._shutterLongPressed = false;
    this.armingLoop = true;          // ring fills during the hold
    this._shutterPressTimer = setTimeout(() => {
        this._shutterLongPressed = true;
        this.armingLoop = false;
        if (ctx.onLongPress) ctx.onLongPress();
    }, 600);
}
shutterPointerUp(ev, ctx) {
    clearTimeout(this._shutterPressTimer);
    this.armingLoop = false;
    if (this._shutterLongPressed) { this._shutterLongPressed = false; return; }
    if (ctx.isActive()) { ctx.onAbort(); } else { ctx.onTap(); }
}
shutterPointerCancel() {
    clearTimeout(this._shutterPressTimer);
    this.armingLoop = false;
    this._shutterLongPressed = false;
}
```

Visual feedback during `armingLoop`: ring fills proportionally to
the 600 ms hold (`stroke-dashoffset = 289 * (1 - holdElapsed/600)`)
confirming the user is arming a loop. Release before 600 ms = snap.

### Per-tab mapping

| Tab | isActive | progress | onTap | onLongPress | onAbort |
|---|---|---|---|---|---|
| LIVE | `capturing\|\|looping` | client timer | `capture()` | `loopCapture()` | `stopCapture()` |
| PREVIEW | `preview.busy\|\|preview.looping` | client timer | `previewTakeSnap()` | `previewToggleLoop()` | `previewAbort()` |
| FOCUS Manual | `manualFocus.running` | per `intervalSec` | `manualFocusSnap()` | `manualFocusToggle()` | `manualFocusToggle()` |
| VIDEO Capture | `videoRecording.recording` | `frames*exp/maxDur` or indeterminate spinner | `videoToggleRecord()` | same | same |
| AUTORUN | `seqState==='running'` | `seqProgress()` (whole sequence) | `startSequence()` | same | `stopSequence()` |

### AUTORUN sidebar refactor

Before SHUT-4: AUTORUN was full-width sequence list + footer-style
buttons. After: `.autorun-pane` flex-row; sequence list left, new
`.autorun-sidebar` (320 px) on the right with centered shutter +
Pause/Resume toggle + progress numbers (frames, ETA, elapsed) that
used to live in the horizontal `seq-progress-section` (removed —
the ring now visually represents progress).

CSS reuses `.live-pane,.preview-pane,.autorun-pane` comma-list
selectors. Mobile (≤900 px) collapses to flex-column.

## Phases (5 commits)

### SHUT-1: shutter component + LIVE wiring
- `.polaris-shutter*` CSS (~200 lines), Alpine state + handlers,
  LIVE replaces 3-button action row with centered shutter. View
  / Save / Compute toggles move to a `.shutter-secondary` block.

### SHUT-2: PREVIEW
- Same template, replace Take snap / Loop / Abort. Stream toggle
  + View stay below as separate buttons.

### SHUT-3: FOCUS Manual + VIDEO Capture
- FOCUS: tap = single snap, long-press = loop, tap-during-loop =
  stop. Ring progress is per-cycle (`intervalSec`).
- VIDEO: tap = start record, tap = stop. Ring = duration progress
  if maxDurationSec set; indeterminate spinner otherwise.

### SHUT-4: AUTORUN sidebar + shutter
- Wrap body in `.autorun-pane` (flex-row).
- `.quick-controls.autorun-sidebar` on the right with centered
  shutter + Pause/Resume below + progress text.
- Remove `.seq-progress-section` horizontal bar.

### SHUT-5: docs + README
- README "Capture button" section + per-feature docs (live-stacking,
  preview, sequence, etc.) updated with gesture model.

## Reused code

- `.live-pane / .preview-pane` CSS pattern — `.autorun-pane`
  inherits via comma-selector.
- Existing state (`capturing`, `looping`, `preview.busy`,
  `preview.looping`, `manualFocus.running`,
  `videoRecording.recording`, `seqState`).
- Existing methods (`capture()`, `loopCapture()`, `stopCapture()`,
  `previewTakeSnap()`, etc.) — called verbatim from shutter ctx.
- `seqProgress()` for the AUTORUN ring.
- `setInterval` pattern (used by `updateClock` 1 Hz and
  `_skyTicker`).

## Verification

- LIVE: tap = snap, ring fills 0..1 during exposure, long-press =
  loop, tap-during-loop = abort.
- PREVIEW: same, plus Stream toggle works independently.
- FOCUS Manual: tap = snap, long-press starts loop, tap-during-loop
  stops.
- VIDEO: tap = start record, ring fills if maxDuration set, spinner
  otherwise. Tap = stop.
- AUTORUN: 2-col layout. Tap shutter = startSequence. Ring shows
  whole-sequence progress. Pause/Resume toggle independent. Mobile
  ≤900 px collapses to single column.
- Cross-tab: switching during active capture shows the correct
  state on the new tab's shutter.

## Out of scope (deferred)

- Inner ring (sub-exposure progress overlaid on whole-sequence
  ring) — polish, SHUT-6 follow-up.
- Haptic feedback on mobile when long-press arms loop (vibrate
  API).
- FLAT WIZARD shutter — needs the panel UI first (covered in FW-1..3).
- WS payload exposure-remaining field — server-pushed timer.
  Client-side timer suffices for v1.
- Configurable long-press duration. Hardcoded 600 ms in v1.

---

# Previous plan: Refocus suggestion in live stacking (REFSUG-1..3)

> Previous plan (HELP, in-app stepper tutorials) preserved below.

## Context

Live stacking already has two paths to handle focus drift:

1. **LSTR-3 `LiveStackTriggersService`** — auto-refocus when
   enabled, fires `AutoFocusService` automatically on HFR /
   temperature / frame-count / minutes thresholds the user
   configures per-rig. Requires a motorised focuser and
   `RefocusEnabled=true`.
2. **Nothing** when the user has a manual focuser (Crayford on a
   budget refractor, kids' scope rack & pinion) OR a motor but
   prefers to refocus by hand.

Case 2 is exactly who needs a heads-up the most: turning the knob
manually with no feedback that focus is degrading. The FOCUS tab
Manual Assist exists (MFOC-1..5) but requires the user to look at
it actively. Live stacking never said "hey, focus is getting
worse".

**User proposal**: trend-based suggestion. Instead of fixed
"HFR > baseline × 1.20" thresholds, watch the **slope** of the
last N HFR samples + compare against the recent best. Detects
systematic degradation without user-tuned thresholds.

**Decisions**:
- **When**: only when `RefocusEnabled=false` on the active rig
  (auto handles the rest). Don't duplicate info when AF will fire
  anyway.
- **Detection**: automatic trend-based, no profile fields the user
  has to tune. Polaris figures it out.
- **Surfaces**: toast on first detection + persistent chip in the
  activity bar.
- **Cleanup**: auto-detect via HFR recovering + "I refocused"
  button for instant dismiss + baseline reset.

## Architecture

All backend, new singleton `RefocusSuggestionService` that
subscribes to the same `LiveStackingService.FrameIntegrated` event
LSTR-3 already consumes. Zero changes to LSTR-3, zero new profile
fields.

### `Services/RefocusSuggestionService.cs` (new)

Singleton + `IDisposable`. Subscribes to
`LiveStackingService.SubscribeFrameIntegrated()` in the ctor
(same pattern as `LiveStackTriggersService.cs`).

```csharp
public class RefocusSuggestionService : IDisposable {
    public RefocusSuggestionStatus CurrentStatus { get; }
    public event Action<RefocusSuggestionStatus>? StatusChanged;
    public void Dismiss(bool resetBaseline);   // "I refocused" button
}

public record RefocusSuggestionStatus(
    bool Suggesting, string? Reason,
    double BaselineHfr, double CurrentHfr,
    double SlopePerFrame, int FramesSinceBaseline,
    DateTime? SuggestedAt);
```

Detection algorithm, evaluated each `FrameIntegrated`:

1. **Skip** when `LiveStackTriggers.RefocusEnabled == true` on the
   active rig OR the active rig has no focuser (no point
   suggesting when there's nothing to fix).
2. **Skip** when frame has `MedianHfr <= 0` or `StarCount < 5`
   (drop unreliable samples).
3. **Maintain** a `Queue<HfrSample>` of the last 30 valid samples.
4. **Warm-up**: ≥15 samples before evaluating.
5. **Baseline** = 5th-percentile HFR over the last 20 samples
   ("best stable HFR" reference) + median star count. Reset on
   `LiveStackingService.Reset()`.
6. **Trend test** every frame after warm-up:
   - `slope > 0` (HFR rising) AND
   - `mean(last 5 HFR) > baseline * 1.15` AND
   - `slope * 5 > 0.3 * baseline` (extrapolated 5-frame change
     > 30% of baseline)
   → fire suggestion.
7. **Secondary signal**: a 30% drop in average star count vs
   baseline median fires on its own. Covers very-out-of-focus
   where HFR looks deceptively stable because dim stars dropped
   out entirely.
8. **Auto-dismiss** when **both** HFR rolling mean recovers to
   within 5% of baseline AND star count recovers, for 3
   consecutive frames. The "both" matters: a star-count-only
   trigger with stable HFR would otherwise auto-clear immediately.
9. **Manual dismiss** via `Dismiss(resetBaseline)`. `true`
   replaces baseline with current rolling mean (user refocused;
   this is the new "good"). `false` clears the chip without
   resetting (acknowledge but trust the existing baseline).
10. **Debounce**: don't fire the toast more than once every 60 s
    for the same suggestion cycle. The chip stays until cleared.

All numerical constants live as `private const` so a tuning pass
touches one file.

### Notifications + WS payload

- On first fire: `NotificationService.Push("warn", "Refocus
  suggested: {reason}. Open FOCUS → Manual Assist.", ttlMs:
  8000)`.
- On dismiss / auto-clear: no notification (don't spam).
- `StatusStreamHandler` adds `refocusSuggestion` sub-object
  mirroring `RefocusSuggestionStatus`. Cadence is existing 1 Hz.

### Dismiss endpoint

`POST /api/livestack/refocus-suggestion/dismiss` body
`{resetBaseline?: bool}` → 204 No Content.

### Frontend: WS state + activity-bar chip

- State: `refocusSuggestion: { suggesting, reason, baselineHfr,
  currentHfr, slopePerFrame, suggestedAt }`.
- `activityChips()` gains an entry when `refocusSuggestion.suggesting`:
  ```js
  out.push({
      id: 'refocus-suggest', icon: '🎯', kind: 'warn',
      label: 'Refocus needed: ' + refocusSuggestion.reason,
      onClick: () => { this.tab = 'focus'; this.focusTab = 'assist'; }
  });
  ```
- Methods `refocusSuggestionDismiss()` (POST without reset) and
  `refocusSuggestionResolved()` (POST with resetBaseline=true).

### LIVE tab callout

Small yellow box above the live-stack canvas with three buttons:
- **I refocused** — dismisses + replaces baseline with rolling mean.
- **Open FOCUS** — jumps to FOCUS Manual Assist without dismiss.
- **Dismiss** — clears chip without resetting baseline.

## Phases (3 commits)

### REFSUG-1: service + WS payload + Dismiss endpoint
- `Services/RefocusSuggestionService.cs` (~250 lines).
- Hook `LiveStackingService.SubscribeFrameIntegrated`.
- Reset on `LiveStackingService.Reset()` + on
  `EquipmentProfileActivated` (rig switch).
- `Program.cs` singleton + eager-resolve.
- `StatusStreamHandler` adds `refocusSuggestion` to the 1 Hz
  payload.
- `LiveStackEndpoints` gets `POST /refocus-suggestion/dismiss`.
- Unit tests: warmup gate, skip when RefocusEnabled, rising-HFR
  fires, stable HFR doesn't, star-count crash fires, auto-clear
  when both recover, manual dismiss with/without baseline reset.

### REFSUG-2: frontend chip + toast
- `app.js` state + `handleStatusMessage` hook.
- `activityChips()` entry + chip onClick.
- `refocusSuggestionDismiss()` + `refocusSuggestionResolved()`.
- Toast through existing `NotificationService` pump.

### REFSUG-3: LIVE callout + docs
- Yellow box above live-stack canvas with 3 buttons.
- `docs/user-guide/live-stacking.md` gains "Refocus suggestion"
  section explaining the trend-based detection + how it
  complements (doesn't replace) LSTR-3 auto-refocus.
- README bullet under live-stacking.

## Reused code

- `LiveStackingService.SubscribeFrameIntegrated` (LSTR-1) — same
  hook LSTR-3 uses; second subscriber is fine.
- `LiveStackingService.Reset()` event (LSTR-3 already listens).
- `NotificationService.Push(kind, text, ttlMs)` — direct call;
  client-side pump already wired.
- `ProfileService.EquipmentProfileActivated` event — hook for rig
  switch reset.
- `app.js` toast pump — handles the warn toast automatically.

## Verification

- Pin baseline HFR (~1.9) over 20 frames; turn focuser knob out
  manually; after ~3-5 degrading frames, toast fires "Refocus
  suggested: HFR rising 18% over 12 frames".
- Chip appears in activity bar.
- Click chip → app switches to FOCUS → Manual Assist subtab.
- User refocuses by hand, clicks "I refocused" → chip clears,
  baseline resets to current.
- `RefocusEnabled = true` → never fires.
- Heavy clouds (StarCount < 5 every frame) → never fires.
- `LiveStackingService.Reset()` → clears baseline.
- Rig switch mid-stack → resets via `EquipmentProfileActivated`.

## Notes

- Why 15-sample warmup: ≥10 needed for stable regression, +5
  buffer for 1-2 bad samples.
- Why 1.15× baseline + slope check: HFR fluctuates a few % from
  seeing. 15% over best + clear positive slope filters
  seeing-driven jitter (oscillates around mean) from systematic
  drift (trends one direction).
- Star-count secondary signal covers edge case where HFR appears
  stable but the brightest stars dimmed out (focus way off,
  detector lost them).
- Temperature trend NOT used as primary signal v1: passive
  predictor, not direct measurement. Documented as future v2.

## Out of scope (deferred)

- Suggestion history graph.
- Configurable threshold multipliers via Settings.
- Temperature as third signal.
- Star FWHM trend (alternative to HFR).
- Auto-fire LSTR-3 from suggestion confidence.

---

# Previous plan: HELP tab — in-app stepper tutorials (HELP-1..5)

> Previous plan (AUTH, basic auth) preserved below.

## Context

Polaris has 33 Markdown docs under `docs/user-guide/` covering
basically everything — but they live outside the app. Someone who
just installs the `.deb` and opens `https://polaris-pi.local:5000`
for the first time encounters 17 sidebar tabs (Home, Rigs, Sky,
Guide, Polar, Focus, Preview, Video, Autorun, Live, Adv, Studio,
Editor, Tonight, Weather, Files, Settings) with no in-app guidance
on what to do first. Doc links exist in context-specific spots
(cert install in Settings, mounts.md in RIGS, dslr-*.md via FILES)
but don't answer the basic "how do I take my first photo?".

A new HELP tab. Pattern is familiar (sidebar button + tab-panel,
just like every other tab); `UseStaticFiles()` already serves any
PNG dropped under `wwwroot/screenshots/`, and tutorial steps map
~1:1 to `docs/user-guide/end-to-end-workflow.md` (English, like
the rest of the UI). HELP is a UX layer that consumes the docs
content and presents it step-by-step.

**Decisions** (AskUserQuestion):
- **Format**: stepper (one step per card, Prev/Next, progress
  indicator). Full-width card with hero screenshot + short body
  + tip + "read more in docs" link + optional "open this tab now"
  button.
- **Screenshots**: placeholders for now, real screenshots later.
  Each slot becomes a `.help-screenshot-placeholder` showing the
  exact path the operator drops the PNG at
  (`wwwroot/screenshots/{tutorial}/{NN}-{slug}.png`); when the
  file exists, the placeholder hides + the image appears. No
  blocking: structure ships ready, screenshots fill in
  organically.
- **Scope**: 4 tutorials.
  1. **Capture-to-export end-to-end** (~12 steps, main flow)
  2. **First-night checklist** (~5 steps, first boot)
  3. **Specific workflows** (LRGB mono ~5 + planetary ~4 + PCC ~3)
  4. **Troubleshooting / FAQ** (list of common problems)
- **Language**: English, aligned with the docs + rest of UI.

## Architecture

Almost everything is frontend; zero backend (docs already in repo,
static serving already wired). Pattern cloned from the TONIGHT
tab: button + tab-panel + Alpine state + methods.

### Sidebar button

End of nav stack (after Settings, position #18). Circular "?"
SVG icon. `title="Help"`.

### State + tutorial data

`wwwroot/js/app.js` gets:

```js
help: {
    // null = landing (pick a tutorial); else: 'capture'
    //   'first-night' | 'lrgb' | 'planetary' | 'pcc' | 'troubleshoot'
    tutorial: null,
    step: 0,
    landingHintDismissed: false
}
```

Tutorial content lives in a `_helpTutorials()` method (returns
the catalog) so the data sits next to the code rendering it — no
separate JSON file, no fetch. Each step:

```js
{
    title: "Slew & Center on the target",
    tab: "sky",                // optional: "Open this tab" button
    docLink: "sky-explorer.md",
    screenshot: "sky/03-slew-center.png",
    body: ["From the SKY tab, ...", "Polaris pre-fills RA/Dec ..."],
    tip: "Pixel scale comes from the connected camera...",
    warn: null
}
```

Catalog size: ~35 step objects = ~300 lines of JS data inline.
Manageable; not big enough to warrant a JSON split.

### Tab markup (~120 lines)

`<div x-show="tab === 'help'" class="tab-panel help-panel">` with
two states:

**Landing** (`help.tutorial === null`): 4 tutorial cards in a
grid (capture/first-night/workflows/troubleshoot) with brief
descriptions + step counts.

**Stepper** (`help.tutorial !== null`): hero screenshot full-width,
title, step number indicator (dots), body paragraphs, optional
yellow callout, "📖 Read more" + "→ Open <tab>" buttons, then
"← Previous" / "Next →" navigation. Progress saved in
localStorage per tutorial so re-entry resumes mid-stream.

### Methods

- `helpOnTabEnter()` — restore tutorial + step from localStorage.
- `helpStart(key)` / `helpExit()` / `helpNext()` / `helpPrev()`
  / `helpJumpTo(idx)`.
- `helpOpenTab(tabId)` — switches tabs + remembers HELP position
  so coming back lands on the same step.
- `helpCurrentStep()` — derived from `tutorial + step`.

### Screenshot placeholder pattern

Each step's `screenshot` is a relative path under
`wwwroot/screenshots/`. Template:

```html
<div class="help-screenshot">
    <template x-if="step.screenshot">
        <img :src="'/screenshots/' + step.screenshot"
             :alt="step.title"
             @error="$el.style.display = 'none';
                     $el.nextElementSibling.style.display = 'flex'">
    </template>
    <div class="help-screenshot-placeholder">
        Screenshot pending:
        <code>wwwroot/screenshots/<span x-text="step.screenshot"></span></code>
    </div>
</div>
```

`@error` hides the `<img>` + shows the placeholder when the file
isn't there yet. Drop a PNG, hard-refresh, done.

### CSS

New `.help-*` block (~150 lines): landing 3-col grid card layout,
stepper header with progress dots, step card max-width 920 px
centered, screenshot block with placeholder variant, step body
typography, tip + warn yellow/blue accent boxes, Prev/Next button
row.

## Phases (5 commits)

### HELP-1: sidebar + tab panel shell + state plumbing
Skeleton: button #18, `help` state + methods, localStorage
persistence (`polaris-help-pos`), landing markup with 4 cards
(placeholder counts), stepper shell rendering one empty step +
Prev/Next + progress dots (no content yet).

### HELP-2: Capture-to-export tutorial (12 steps)
1. Welcome / what you'll need (no tab, no shot)
2. Connect equipment in RIGS → `equip` → `rigs.md`
3. Set observatory location → `settings` → `first-night.md`
4. Polar alignment → `polar` → `polar-alignment.md`
5. Focus → `focus` → `focus.md`
6. Pick a target (SKY) → `sky` → `sky-explorer.md`
7. Slew & Center → `sky` → `sky-explorer.md`
8. Start guiding (PHD2) → `guide` → `guide-phd2.md`
9. Build a sequence (AUTORUN) → `sequence` → `sequence.md`
10. Watch frames stack (LIVE) → `live` → `live-stacking.md`
11. Calibrate + integrate (STUDIO) → `studio` → `studio.md`
12. Edit + export (EDITOR) → `editor` → `editor.md`

### HELP-3: First-night checklist (5 steps)
1. Open Polaris (browser + cert) → `installation.md`
2. Set password (wizard) → `authentication.md`
3. Set observatory location → `settings` → `first-night.md`
4. Set WiFi (Hotspot ↔ Station) → `settings` → `network-mode.md`
5. Connect first device → `equip` → `rigs.md`

### HELP-4: Specific workflows (LRGB mono + planetary + PCC)
Landing card opens a 3-card sub-grid before launching the chosen
stepper. Tutorials: LRGB mono pipeline (~5 steps), planetary /
lucky imaging (~4 steps), photometric color calibration (~3
steps). ~12 step objects total under keys `lrgb` / `planetary` /
`pcc`.

### HELP-5: Troubleshooting / FAQ + docs README link
Different shape: single screen with `<details>` accordion of ~6
common problems (mDNS unreachable / plate-solve fails / sequence
won't start / PHD2 won't connect / GraXpert "model not found" /
live stack drift), each with diagnosis + fix + "see also" doc
link. Final card: "Didn't find what you need? Full docs index"
→ external link to `docs/user-guide/README.md` on raw GitHub.

## Files created

- `wwwroot/screenshots/.gitkeep` (empty marker)
- `wwwroot/screenshots/README.md` (naming convention)

## Files modified

- `wwwroot/index.html` — sidebar button #18 + tab panel + landing
  grid + stepper shell + workflows sub-picker + troubleshoot
  accordion.
- `wwwroot/js/app.js` — `help` state, 7 methods,
  `_helpTutorials()` catalog (~300 lines of data).
- `wwwroot/css/app.css` — `.help-*` block (~150 lines).
- `README.md` — bullet under Features pointing at the HELP tab.

## Reused code

- All `docs/user-guide/*.md` pages — body content + "Read more"
  link targets.
- TONIGHT tab pattern (button + tab-panel) — template for HELP.
- `<details>` collapsible pattern (600+ uses in index.html) —
  direct lift for troubleshoot section.
- `app.js` localStorage helpers — pattern for persisting
  `polaris-help-pos`.
- `.btn` / `.btn-primary` / `.text-muted` / `.text-warn` /
  `.tonight-card` / `.settings-section` existing CSS.

## Verification

1. Sign in → sidebar shows "?" icon at the bottom labeled "Help".
2. Click → landing with 4 cards.
3. Click "Capture to export" → stepper at step 1 of 12.
4. Hit Next → step 2 (RIGS) with placeholder "Screenshot pending:
   `wwwroot/screenshots/capture/02-rigs.png`", body, "📖 Read more"
   → `rigs.md`, "→ Open RIGS tab".
5. Click "Open RIGS tab" → app switches to RIGS, help position
   persisted.
6. Click Help → returns to step 2 of capture-to-export.
7. Drop a real PNG at the placeholder path, hard-refresh →
   placeholder gone, image rendered.
8. localStorage `polaris-help-pos` carries
   `{ tutorial: "capture", step: 4 }` — refresh browser, lands on
   same step.

## Notes

- **No new tests**: HELP is content + UI; AUTH-2 middleware
  already serves `/screenshots/*` as static assets.
- **Localization deferred**: English-only v1. A future i18n pass
  would lift step bodies into a `help-en.js` / `help-pt.js`
  switch.
- **Print / PDF export deferred**.
- **Embedded videos deferred** — text + screenshot is enough for
  v1.
- **Auth scope**: HELP tab gated by AUTH-2 like everything else.

---

# Previous plan: Basic auth for the Polaris server (AUTH-1..5)

> Previous plan (MFOC, manual focus assist) preserved below.

## Context

The Polaris server listens on `0.0.0.0:5000` (HTTPS) with no auth.
Anyone on the same WiFi (neighbor, guest, hospital, star party)
can open `https://polaris-pi.local:5000` and have full control:
stop the sequence, slew the mount, turn off the cooler, delete
files via the FILES tab, capture frames to the operator's disk,
etc. HTTPS protects the wire from sniffing but **does not
authenticate clients** — it's just encryption.

`Services/SelfSignedCertService` documents this explicitly
("Polaris assumes trusted LAN"), but "trusted LAN" doesn't hold
in several real scenarios for the target user: Pi hotspot
(everyone who knows the `polaris1234` password), hotel/hospital
WiFi, star parties with dozens of astrophotographers on the same
SSID.

Phase 1 + 2 research confirmed:
- **Zero auth in the backend**: no `[Authorize]`, no middleware,
  no token check. 33 `MapGroup("/api/...")` + 3 WebSockets
  (`/ws/status`, `/ws/image-stream`, `/ws/terminal`) + 2
  reverse-proxies (`/phd2-gui/*`, `/indi-web/*`) all open.
- **Frontend has a clear chokepoint**: `apiFetch` in
  `app.js:1425-1493` is where 95% of calls go. 29 sites use raw
  `fetch()` and need individual fixes.
- **WebSockets already do a first-message handshake**: they send
  `{ type: 'client-capability', wasm, ... }` on open. Can append
  `{ type: 'auth', token }` to the same initial message without
  touching the URL.
- **Relay server (`src/NINA.Relay.Server/`) has a reusable
  shape**: `TenantRegistry.TryAuthenticate(token, ...)` + bearer
  token + optional mTLS thumbprint. Steal the hashing/check
  pattern (not the multi-tenant infrastructure).

**Decisions** (AskUserQuestion):
- **User model**: single shared password. No admin/viewer
  concept; typical operator is one person. Multi-user is a
  follow-up if demanded.
- **First-run**: wizard forces password creation on first access.
  No default printed in postinst — avoids leaving someone stuck
  with a hardcoded "polaris1234".
- **Opt-out**: toggle in Settings, default ON. Cover for the
  field-isolated remote observatory case. Requires current
  password to turn off.
- **Loopback bypass**: `127.0.0.1` / `::1` skip auth. Jupyter /
  Grafana / RStudio pattern — whoever's on the Pi is trusted.
  Simplifies SSH tunnels + local scripts + dev.

## Architecture

Four backend pieces + two frontend.

### `Services/Auth/AuthService.cs` (new)

Singleton. Owns password hash + active session store.

```csharp
public class AuthService {
    public bool IsConfigured { get; }
    public bool IsEnabled { get; }
    public int SessionTimeoutSeconds { get; }

    Task<string?> SetInitialPasswordAsync(string password);
    Task<bool> ChangePasswordAsync(string current, string newPwd);
    Task<bool> SetEnabledAsync(string currentPassword, bool enabled);

    Task<string?> LoginAsync(string password);
    void Logout(string token);
    bool ValidateToken(string token);
}
```

- **Password hashing**: PBKDF2-SHA256 (100k iterations) via
  `Microsoft.AspNetCore.Cryptography.KeyDerivation`. 16-byte
  random salt per hash. Persists hash + salt + algorithm name
  in the profile.
- **Session store**: `ConcurrentDictionary<string, SessionInfo>`
  in memory. `SessionInfo { Token, CreatedAt, LastActivityAt }`.
  Sliding 24 h default. Sweeper background task removes expired
  sessions every 10 min. Not persisted on disk — restart
  invalidates all sessions (intentional: simple + reacts to
  "forgot password → reset via SSH"). Tokens are 32 bytes random
  base64-url (~43 chars).
- **Constant-time string compare** for password check
  (`CryptographicOperations.FixedTimeEquals`).
- **Rate limit**: max 5 attempts/minute per IP on `LoginAsync`.
  Exponential backoff up to 1 h on repeated failures. In-memory
  dict IP → (failures, lockedUntil).

### `Middleware/AuthMiddleware.cs` (new)

ASP.NET middleware registered BEFORE endpoint mapping. Per
request:

```csharp
if (!auth.IsEnabled) { await next(); return; }
if (IsLoopback(ctx.Connection.RemoteIpAddress)) { await next(); return; }
if (IsAuthExemptPath(ctx.Request.Path)) { await next(); return; }
var token = ExtractToken(ctx.Request);
if (token == null || !auth.ValidateToken(token)) {
    ctx.Response.StatusCode = 401;
    await ctx.Response.WriteAsJsonAsync(new {
        error = "auth required",
        authConfigured = auth.IsConfigured
    });
    return;
}
await next();
```

Exempt paths: `/api/auth/*`, `/api/system/version`, static assets,
`/data/*.json`, `/`.

Token extraction: `Authorization: Bearer <token>` (HTTP) > `?token=...`
query string (img/blob srcs) > `polaris_session` cookie (iframes
inside the app).

For WebSockets: middleware lets the upgrade through, validation
happens in the handler when the first message
`{type:'auth',token}` arrives. Bad token → close 4401.

### Cookie HttpOnly for iframes

`/phd2-gui/*` and `/indi-web/*` (iframes that load HTML + make
relative fetches) don't preserve the `Authorization` header on
their inner requests. Login also sets cookie
`polaris_session=<token>; HttpOnly; Secure; SameSite=Strict;
Path=/`. Cookie dies when the browser closes (no `Max-Age`),
mirrors the sessionStorage timing on the frontend.

### `Endpoints/AuthEndpoints.cs` (new)

| Method | Route | Body | Behaviour |
|---|---|---|---|
| GET | `/api/auth/status` | — | `{configured, enabled, authenticated}` |
| POST | `/api/auth/setup` | `{password}` | Only when `!IsConfigured`; creates hash + returns token + sets cookie |
| POST | `/api/auth/login` | `{password}` | `{token}` + cookie. Rate-limited. |
| POST | `/api/auth/logout` | — | Invalidate token + clear cookie |
| POST | `/api/auth/change-password` | `{current, new}` | Requires auth |
| POST | `/api/auth/disable` | `{password}` | Toggle off (needs current pwd) |
| POST | `/api/auth/enable` | `{password}` | Toggle on |

### Profile fields

`UserProfile` gains:

```csharp
public bool AuthEnabled { get; set; } = true;
public string AuthPasswordHash { get; set; } = "";
public string AuthPasswordSalt { get; set; } = "";
public string AuthHashAlgo { get; set; } = "pbkdf2";
public int AuthSessionTimeoutHours { get; set; } = 24;
```

Migration safe: missing fields → not configured → wizard fires
on first access.

### Frontend: login overlay + first-run wizard + `apiFetch` injection

**Alpine state**:
```js
auth: {
    token: '', configured: false, enabled: true,
    needLogin: false, needSetup: false,
    loginPassword: '', loginError: '',
    rememberMe: false   // true → localStorage; false → sessionStorage
}
```

**Boot order** (`init()`):
1. Try restoring token from `sessionStorage` then `localStorage`.
2. `GET /api/auth/status`.
3. Branch: not-enabled → app loads normally; not-configured →
   wizard overlay; configured + not-authenticated → login overlay;
   authenticated → app loads.

**Overlay** — new `<div id="auth-overlay">` at the top of `<body>`,
`position: fixed; z-index: 99999; backdrop-filter: blur(8px);`.
Shows either login form (1 password input + remember-me + Sign
In) or first-run wizard (2 password inputs + confirm + min-8-chars
+ at-least-1-number hint).

**`apiFetch` injection** (`app.js:1459`):
```js
if (this.auth.token) {
    opts.headers = { ...opts.headers, 'Authorization': 'Bearer ' + this.auth.token };
}
```

**401 handler**: when `apiFetch` receives 401, sets
`auth.needLogin = true`, clears token, shows overlay.

**Bare `fetch()` sites** (29 of them): create
`this.authFetch(url, opts)` helper that does the same injection,
sweep all 29 with conservative search-and-replace. File-download
sites (editor export, FITS preview `src=`) use `?token=` query
instead of header.

**WebSocket handshake** (`app.js:~1788`): right after `onopen`,
send `{ type: 'auth', token }` before
`{ type: 'client-capability', ... }`. Invalid token → server
closes 4401 → client sets `auth.needLogin = true` + reconnects
after login.

**Iframes** (`/phd2-gui/`, `/indi-web/`): cookie
`polaris_session` is sent automatically by the browser
(same-origin + Path=/). Zero change needed here.

### Settings panel "Authentication"

New section showing status (🟢 Enabled / session XXX...), Change
password button, Sign out, "Require password to access Polaris
from the LAN" toggle (disable confirm asks for current password),
session timeout input.

## Phases (5 commits)

### AUTH-1: AuthService + endpoints + tests
- `Services/Auth/AuthService.cs` (~250 lines).
- `Endpoints/AuthEndpoints.cs` (7 routes).
- `ProfileService.cs` gets 5 new fields + migration.
- `Program.cs` registers singleton + maps endpoints.
- Tests (~12 cases): hash roundtrip, fixed-time compare, salt
  randomness, SetInitialPassword runs only once, ChangePassword
  requires current pwd, LoginAsync rate-limit + lockout, session
  sweep, constant-time check defeats timing attack (smoke).

### AUTH-2: AuthMiddleware + WS handshake validation
- `Middleware/AuthMiddleware.cs` (~120 lines), loopback +
  exempt-paths + token extraction (header > query > cookie).
- Registered in `Program.cs` BEFORE `UseRouting`.
- `WebSocket/ImageStreamHandler.cs` + `StatusStreamHandler.cs`:
  first message must be `{type:'auth',token}` when auth enabled.
  Invalid → close 4401.

### AUTH-3: Frontend login + first-run wizard + apiFetch injection
- Auth state + boot order.
- Overlay HTML + CSS (`.auth-overlay`, `.auth-modal`).
- `apiFetch` header injection.
- 401 handler triggers login.
- WS handshake.
- Settings "Authentication" section.

### AUTH-4: Fix 29 bare fetch sites + cookie for iframes
- `authFetch` helper.
- Sweep `app.js` + `onnx-pipelines.js` replacing `fetch(` with
  `this.authFetch(` or injecting `?token=` for img/blob srcs.
- Backend login sets `polaris_session` cookie.
- Verify PHD2-GUI / INDI-Web / Stellarium iframes load after
  login without extra prompt.

### AUTH-5: Tests + docs + verify
- Integration tests via WebApplicationFactory: 401 without token,
  200 with token, loopback bypass, first-run flow, WS handshake
  reject.
- `docs/user-guide/authentication.md` — first-run wizard, change
  password, reset forgotten password (SSH + clear ProfileService
  fields), disable auth for trusted LAN + warning.
- README "Authentication" section.

## Verification

- Fresh install → wizard "Set a password to protect your Polaris
  server" full-screen, no other tabs accessible.
- Type password + confirm → POST `/api/auth/setup` → token →
  cookie set → app appears.
- Refresh → still authenticated.
- Hard refresh + clear storage → login screen.
- Second device on the LAN → login screen.
- Wrong password 6× → 6th response says "locked, try again in N
  minutes".
- Settings → Authentication → Sign out → back to login.
- Settings → Change password → confirm → all other sessions
  invalidated → next request 401 → re-login.
- SSH on the Pi: `curl http://localhost:5080/api/system/status`
  no token → 200 (loopback bypass). Same `curl` to the LAN IP →
  401.
- DevTools: `/ws/image-stream` open sends `{type:'auth',token}`
  before `{type:'client-capability'}`. `wscat` without handshake
  → close 4401.
- PHD2-GUI iframe loads (cookie propagated). INDI-Web idem.
  Stellarium sub-app loads.
- Settings → uncheck "Require password" + confirm with current
  pwd → toggle off persists. Logout, close browser, reopen → app
  loads without login.
- Re-enable via Settings → toggle ON requires current password.
- Forgot password: SSH `nano ~/.config/NINA.Polaris/profile.json`,
  clear `AuthPasswordHash` + `Salt`, restart service → wizard
  appears on next access.

## Notes

- **Hash strength**: PBKDF2-SHA256 100k iters OK for offline
  attack + fast enough for Pi 2 (<1 s login). Argon2id preferable
  but needs `Konscious.Security.Cryptography`; deferred.
- **Token entropy**: 32 bytes random base64-url = 256 bits.
- **CSRF**: cookie is SameSite=Strict so cross-origin requests
  don't carry it. Bearer header always intentional. Endpoints
  accept only JSON body (no form-urlencoded) → reduced surface.
- **iOS Safari sessionStorage**: some devices clear sessionStorage
  in background tabs. localStorage with "remember me" toggle
  handles this.
- **Logout doesn't stop sequences**: only invalidates the token.
  Sequence/guiding/etc keep running. User can log back in + Stop
  explicitly. Involuntary abort on logout would be worse than
  losing the login session.

## Out of scope (deferred)

- Multi-user / RBAC (admin vs viewer).
- 2FA / TOTP.
- mTLS client cert auth.
- OAuth / SSO / LDAP.
- Auth audit log.
- Password recovery via email.
- Token TTL configurable via env var (hardcoded 24 h v1).

---

# Previous plan: Manual focus assist in the FOCUS tab (MFOC-1..5)

> Previous plan (WIFI, Hotspot ↔ Station management) preserved below.

## Context

The FOCUS tab originally only had **V-curve auto-focus**
(`AutoFocusService`), which requires a motorised focuser. Users
with manual focusers (Crayford / rack&pinion without motors,
budget refractors, kid scopes) arrived at FOCUS and found a grey
"Connect a focuser to use" — end of story.

Standard solutions in astrophotography:
1. **Live HFR feedback**: user turns the knob, watches HFR update
   every few seconds, stops when the number stops dropping.
2. **Bahtinov mask**: diffraction mask creating 3 spikes on a
   bright star. When focused, the central spike crosses exactly
   through the intersection of the other two. Software measures
   the central-spike offset and says "still 3.5 px off, turn in".
3. **HFR trend over time**: chart showing "you were at 2.8 px
   12 s ago, now jumped to 3.4 — you overshot".

Research (Explore phase):
- `AutoFocusService.MeasureHFR(image, minStars)` reusable: returns
  `(medianHfr, starCount)` via `StarDetector.Detect()`.
- `/api/camera/capture` already returns inline stats:
  `{ status, width, height, stats: { hfr, starCount, ... } }`.
  Client-side loop just calls it repeatedly.
- `FrameQualityAnalyzer.LaplacianVariance` (used in VIDEO /
  planetary): secondary sharpness metric useful when few stars
  (Bahtinov uses 1 bright star).
- Chart.js wrapper from V-curve already mountable: clone for
  time-series.
- `focusConnected` flag in JS state — gate to show/hide the
  motorised Auto-focus panel.
- **Bahtinov**: zero code in repo (`bahtinov|diffraction` no
  match). Algorithm from scratch.
- PREVIEW loop exists (`preview.looping` + `previewTakeSnap`) but
  coupled to PREVIEW. Cleaner to have its own loop for Manual
  Focus so it doesn't compete with/duplicate state.

**Decisions** (AskUserQuestion):
- **Bahtinov mask analysis in scope now** — algorithm from
  scratch, detects 3 spikes + central offset to say "clockwise /
  stop", visual canvas overlay.
- **Always available** even with motor connected — user with
  auto-focus can use Manual for fine-tuning after a V-curve, or
  just check HFR between exposures. Tabstrip in FOCUS:
  "Manual" + "Auto V-curve" coexist.

## Architecture

```
┌─ FOCUS tab ──────────────────────────────────────────────────┐
│ Tabstrip: [ Manual (default no-motor) ] [ Auto V-curve ]     │
│                                                               │
│ ┌─ Manual subtab ──────────────────────────────────────────┐ │
│ │ ┌─ Live preview canvas + Bahtinov overlay ┐  ┌─ Sidebar ┐│ │
│ │ │ (frame with star annotations +          │  │ Exp Gain ││ │
│ │ │  Bahtinov overlay when on)              │  │ ▶ Start  ││ │
│ │ │                                          │  │ Interval ││ │
│ │ │                                          │  │ Min stars││ │
│ │ │                                          │  │          ││ │
│ │ │                                          │  │ Metrics  ││ │
│ │ │                                          │  │ HFR 3.42 ││ │
│ │ │                                          │  │ FWHM 8.05││ │
│ │ │                                          │  │ Stars 47 ││ │
│ │ │                                          │  │ Laplace  ││ │
│ │ │                                          │  │          ││ │
│ │ │                                          │  │ ☐ Bahtinov│ │
│ │ │                                          │  │  Offset  ││ │
│ │ │                                          │  │  -3.4 ▶▶▶││ │
│ │ │                                          │  │          ││ │
│ │ │                                          │  │ [Reset]  ││ │
│ │ └──────────────────────────────────────────┘  └──────────┘│ │
│ │ ┌─ HFR trend chart (last 60 samples) ─────────────────────┐│ │
│ │ │ HFR (px)                                                ││ │
│ │ │    ╱╲                                                   ││ │
│ │ │   ╱  ╲___                                               ││ │
│ │ │  ─────────── time →                                     ││ │
│ │ └─────────────────────────────────────────────────────────┘│ │
│ └──────────────────────────────────────────────────────────┘ │
│                                                               │
│ ┌─ Auto V-curve subtab (existing) ──────────────────────────┐│
│ │  Steps + Step Size + Exposure + Min Stars + Backlash      ││
│ │  Start sweep  +  V-curve chart  +  Best position fit       ││
│ └────────────────────────────────────────────────────────────┘│
└───────────────────────────────────────────────────────────────┘
```

### Backend: extend `/api/camera/capture` stats

Add `laplacianVar` to the `stats` block. Cheap computation
(one 3×3 convolution over center 256×256 ROI), already
implemented in `FrameQualityAnalyzer.LaplacianVariance`. Doesn't
break old clients.

### `Services/Focus/BahtinovAnalyzer.cs` (new)

Classic radial-line-integration algorithm:

1. **Locate bright star**: `StarDetector.Detect` on the full
   image, pick brightest by `Peak` (or user picks via canvas
   click). Crop 200×200 ROI centred.
2. **Background subtract**: global median subtracted from ROI.
3. **Angular sampling**: for each θ ∈ [0°, 180°) at 0.5° steps,
   integrate intensity along a line through the ROI centre at
   angle θ. Result: 360-value array.
4. **Peak-find**: 3 local maxima separated by at least 30°.
   Those are the 3 spikes.
5. **Geometry**: from the 3 detected angles, identify the central
   one (closest to the mean of the other two). Measure
   perpendicular distance from central spike to midpoint between
   the other two.
6. **Output**: `BahtinovResult { OffsetPixels, OffsetSign,
   InFocusThreshold, StarX, StarY, Spike1, Spike2, Spike3 }`.

Endpoint `POST /api/focus/bahtinov` reuses
`ImageRelayService`'s last cached frame OR captures fresh;
returns `BahtinovResult` JSON.

### Frontend: tabstrip + Manual subtab

- Tabstrip with `focusTab = 'manual' | 'auto'`. Default:
  `focusConnected ? 'auto' : 'manual'`.
- **Manual subtab**: 2-col layout (canvas left, controls right,
  chart full-width below) — same pattern as VIDEO Capture.
- **Auto subtab**: existing V-curve markup unchanged.

### State + capture loop

```js
manualFocus: {
    running: false,
    intervalSec: 2,
    minStars: 3,
    showBahtinov: false,
    bahtinovResult: null,
    samples: [],          // {t, hfr, fwhm, starCount, laplacian}
    bestHfr: null,
    baselineHfr: null
}

async manualFocusToggle() { /* start/stop loop */ }
async _manualFocusTick() {
    const r = await this.apiPost('/api/camera/capture', {
        exposure: this.preview.exposure || 2.0,
        gain: this.preview.gain || 100,
        binning: this.preview.binning || 1,
        saveToDisk: false
    });
    const json = await r.json();
    this.manualFocus.samples.push({
        t: Date.now(),
        hfr: json.stats.hfr,
        fwhm: json.stats.hfr * 2.355,
        starCount: json.stats.starCount,
        laplacian: json.stats.laplacianVar
    });
    if (this.manualFocus.samples.length > 60)
        this.manualFocus.samples.shift();
    if (json.stats.hfr > 0 &&
        (this.manualFocus.bestHfr == null
         || json.stats.hfr < this.manualFocus.bestHfr)) {
        this.manualFocus.bestHfr = json.stats.hfr;
    }
    if (this.manualFocus.showBahtinov) {
        const b = await this.apiPost('/api/focus/bahtinov', {});
        this.manualFocus.bahtinovResult = await b.json();
    }
    this._renderManualFocusChart();
}
```

**Chart**: clone of V-curve wrapper. Time on X, HFR on Y. Marker
horizontal line for `bestHfr`. Optional FWHM + Laplacian second
series.

**Bahtinov overlay**: when `showBahtinov=true` + result populated,
draws over canvas:
- Cross at `(StarX, StarY)`
- 3 lines for the spikes
- Arrow indicating central-spike offset (green if <threshold,
  amber medium, red if >2× threshold)
- Label `"Offset: -3.4 px ▶ rotate inward"` (sign → direction)

### Tests + docs

- `BahtinovAnalyzerTests.cs`: 6 synthetic cases (exact in-focus,
  positive offset, negative offset, faint star error, 2-spike
  error, 4+ spikes pick the 3 strongest).
- `docs/user-guide/focus.md` — update with Manual Focus +
  Bahtinov workflow.
- README — short bullet.

## Phases (5 commits)

### MFOC-1: backend stats + tabstrip + client-side manual loop
- `CameraEndpoints.cs`: add `laplacianVar` to response stats.
- `index.html` FOCUS tab: tabstrip Manual / Auto, Manual subtab
  with canvas + sidebar (no chart yet).
- `app.js`: `manualFocus` state, `manualFocusToggle`,
  `_manualFocusTick`, sample rolling window. Live HFR / FWHM /
  Stars / Laplacian rendered in sidebar. No chart yet, no
  Bahtinov.

### MFOC-2: HFR trend chart
- Clone Chart.js wrapper from V-curve.
- HFR vs time line, horizontal marker for `bestHfr`.
- Options to toggle FWHM + Laplacian as additional series (Y2).
- "Reset baseline" button zeroes samples + bestHfr.

### MFOC-3: BahtinovAnalyzer service + endpoint
- `Services/Focus/BahtinovAnalyzer.cs` (~200-300 lines).
- `Endpoints/FocusEndpoints.cs` with
  `POST /api/focus/bahtinov`.
- Reuses `ImageRelayService.LastFrame` if available; otherwise
  400 ("no recent frame; click Capture first").
- Unit tests with synthetic frames.

### MFOC-4: Bahtinov UI
- Checkbox "Bahtinov mask" in Manual subtab sidebar.
- When on, each tick also calls `/api/focus/bahtinov` and draws
  the overlay on canvas.
- Numeric readout + directional text ("rotate inward").
- Tooltip explaining how to install the mask physically.

### MFOC-5: docs + verify
- `docs/user-guide/focus.md` "Manual focus + Bahtinov" section.
- README bullet.
- End-to-end verify in manual / motor / bahtinov scenarios with
  the simulator.

## Reused code

- `AutoFocusService.MeasureHFR` (or `StarDetector.Detect` directly).
- `StarDetector.cs` for star detection in Bahtinov.
- `FrameQualityAnalyzer.LaplacianVariance` for the stats response.
- `CameraEndpoints.cs:/capture` already returns inline HFR + star
  count.
- `ImageRelayService.LastFrame` for Bahtinov reuse.
- Chart.js V-curve wrapper for the HFR trend chart.
- Tabstrip pattern from GUIDE / VIDEO / RIGS (`.video-tabstrip`).
- 2-col VIDEO Capture sidebar layout (`.video-pane >
  .preview-area + .quick-controls`).

## Verification

- Simulator OR real camera + no-motor scope.
- FOCUS tab → tabstrip shows Manual selected (no motor).
- Start loop Exp=2 s, Gain=100, interval=2 s.
- Every 2 s canvas refreshes + sidebar shows HFR / FWHM / Stars /
  Laplacian.
- "Reset baseline" zeroes samples + bestHfr.
- Turn focuser manually; HFR drops (focus going in), chart shows
  descending trend, bestHfr updates.
- Keep turning past best: HFR rises; user sees the overshoot on
  chart.

### Bahtinov workflow
- Install Bahtinov mask on scope.
- Point at bright star (Vega, Sirius).
- FOCUS → Manual → Start loop → check "Bahtinov mask".
- Canvas shows overlay: cross at star, 3 spike lines, arrow with
  offset.
- Turn focuser: offset drops to ~0 near focus, arrow colour
  changes red → amber → green.
- "In focus!" text when offset < 0.5 px.

### Coexistence with auto-focus motor
- Connect motorised focuser (simulator).
- FOCUS → tabstrip default Auto, Manual still accessible.
- Run V-curve, complete.
- Switch to Manual → user fine-tunes last-minute (turn knob ±1-2
  steps) confirming via HFR / Bahtinov.

### Graceful failure
- No camera: ▶ Start disabled, banner "Connect a camera in RIGS
  first".
- Frame with no detectable stars: HFR=NaN, banner "No stars
  detected; increase exposure".
- Bahtinov without physical mask (3 spikes not detected): endpoint
  returns `{error: "could not detect 3 diffraction spikes"}`,
  actionable toast.

## Notes

- **Bahtinov requires a bright star**. If brightest detected has
  `Peak < 20000` (16-bit), returns "star too faint, point at a
  magnitude < 3 star like Vega/Sirius".
- **2 s default loop**: reasonable cadence. User turns knob,
  waits 2 s, sees result, turns again. Configurable 1-5 s in UI.
- **HFR vs FWHM**: HFR is native metric (StarDetector). FWHM
  shown as `HFR * 2.355` (Gaussian approximation). Real per-star
  gaussian fit deferred.
- **Laplacian variance** useful when few/no stars (Bahtinov ROI,
  lunar surface, pure nebula). Second Y axis on chart.
- **Performance**: 2 s capture loop doesn't stress Pi 4/5.
  Bahtinov call adds ~50-200 ms.
- **Persistence**: nothing saved in rig profile — sample buffer
  in-memory, clears on tab switch / reload (intentional: ad-hoc
  focus assist).

## Out of scope (deferred)

- Audio beep on HFR improvement (ASIAIR-like feedback).
- Real per-star gaussian FWHM fit.
- Donut method (useful for refractors without Bahtinov).
- Save focus curves historically per rig.
- Bahtinov for mosaic / multi-star (single-star v1).

---

# Previous plan: WiFi management (Hotspot ↔ Station) on Polaris (WIFI-1..6)

> Previous plan (CCALB, Color Calibration Siril-style) preserved below.

## Context

User wants to distribute Polaris on a Pi 4/5 pre-configured as a
**WiFi hotspot** (fixed SSID `Polaris-Hotspot`, password
`polaris1234`). Field workflow:

1. Power the Pi for the first time (no ethernet cable, no monitor).
2. Connect phone/laptop to the WiFi `Polaris-Hotspot`.
3. Open `https://polaris-pi.local:5000` (HTTPS is already default).
4. Accept the self-signed cert once.
5. **NEW**: go to a "Network" panel and optionally switch to
   **Station mode**, picking a local WiFi network + typing the
   password. Polaris applies the switch + the Pi connects to the
   home network.
6. User reconnects phone/laptop to the home network +
   re-accesses `polaris-pi.local:5000` (mDNS keeps resolving).

Today Polaris has **nothing** for network management. `MdnsService`
only announces, doesn't configure. The `.deb` creates the
`polaris` user but doesn't touch NetworkManager or hostapd. Whoever
wants a hotspot has to configure it manually via `nmcli` or
`raspi-config`.

**Decisions** (AskUserQuestion):
- **Switch safety**: **try-and-revert 30 s**. When user switches
  to Station, Polaris applies + waits for DHCP + gateway ping
  for 30 s. If neither comes, auto-reverts to hotspot. Prevents
  bricking access when password is wrong / network gone.
- **Hotspot defaults**: **fixed** — SSID `Polaris-Hotspot`,
  password `polaris1234`. Astrophotography community memorises
  once. Paranoid user can change via UI.
- **Distribution**: **`.deb` + bootstrap script only**. SD card
  image via pi-gen is a follow-up. The script
  `/opt/polaris/bin/polaris-wifi-bootstrap.sh` runs once via
  systemd on first boot post-install and creates the
  `polaris-hotspot` connection in NetworkManager. Pre-baked image
  becomes a separate issue.

**Target platform**: **Linux + NetworkManager only**. Pi OS
Bookworm (Pi 4/5) uses NM by default since 2023. Pi OS Bullseye
(pre-2023) uses `dhcpcd` + `wpa_supplicant` — we document as
"v1 not supported, please upgrade to Bookworm". Windows/macOS:
panel shows "Network management requires Linux + NetworkManager.
On this OS, manage WiFi via the OS settings." Same pattern as
`Phd2GuiSessionService`.

## Architecture

Five pieces, all mirroring the pattern established by
`Phd2GuiSessionService` + `IndiWebManagerService`.

### `Services/Network/NetworkManagerService.cs` (new, BackgroundService)

Singleton + hosted service. Wraps `nmcli`. Identical pattern to
`Phd2GuiSessionService.cs` (constructor → detection → loop with
health probe → start/stop → status snapshot).

```csharp
public class NetworkManagerService : BackgroundService {
    public bool IsSupportedOs { get; }
    public bool NmcliInstalled { get; private set; }
    public string? NmcliVersion { get; private set; }
    public bool HasWifiInterface { get; private set; }
    public string? WifiInterface { get; private set; }   // wlan0, wlx*, etc

    public WifiMode CurrentMode { get; private set; }    // Hotspot|Station|Disconnected
    public string? CurrentSsid { get; private set; }
    public string? CurrentIp { get; private set; }
    public int SignalStrength { get; private set; }
    public string? HotspotSsid { get; private set; }
    public string? LastError { get; private set; }

    Task<List<WifiNetwork>> ScanAsync(CancellationToken ct);
    Task<bool> SwitchToStationAsync(string ssid, string password, CancellationToken ct);
    Task<bool> SwitchToHotspotAsync(CancellationToken ct);
    Task<bool> SetHotspotCredentialsAsync(string ssid, string password, CancellationToken ct);
    Task<NetworkSnapshot> GetSnapshotAsync();
}

public enum WifiMode { Hotspot, Station, Disconnected, Unsupported }
```

**Detection** (startup `ExecuteAsync`):
- `which nmcli` → `NmcliInstalled`.
- `nmcli --version` → `NmcliVersion`.
- `nmcli -t -f DEVICE,TYPE device status | grep wifi` → first
  `wifi` iface → `WifiInterface`.
- If any of these missing, marks `IsSupportedOs = false` or
  `HasWifiInterface = false` with explanatory `LastError`.
- 5 s loop: re-parse `nmcli -t -f DEVICE,STATE,CONNECTION
  device` to refresh `CurrentMode/Ssid/Ip/Signal`.

**Try-and-revert** (`SwitchToStationAsync`):

```csharp
public async Task<bool> SwitchToStationAsync(string ssid, string password, ct) {
    var hotspotWasUp = (CurrentMode == WifiMode.Hotspot);

    // 1. Create or update the station connection
    await RunNmcliAsync($"connection delete polaris-station", ct, ignoreExit: true);
    var add = await RunNmcliAsync(
        $"connection add type wifi ifname {WifiInterface} con-name polaris-station " +
        $"ssid \"{Escape(ssid)}\" wifi-sec.key-mgmt wpa-psk wifi-sec.psk \"{Escape(password)}\"",
        ct);
    if (add.ExitCode != 0) { LastError = "nmcli add failed: " + add.Stderr; return false; }

    // 2. Bring station up (auto-downs the hotspot)
    var up = await RunNmcliAsync($"connection up polaris-station", ct, timeoutMs: 35000);
    if (up.ExitCode != 0) { await RevertToHotspotAsync(hotspotWasUp, ct); return false; }

    // 3. Wait up to 30 s for IPv4 lease + gateway reachability
    var ok = await WaitForLeaseAsync(WifiInterface!, TimeSpan.FromSeconds(30), ct);
    if (!ok) {
        LastError = "No DHCP lease within 30s, reverting to hotspot";
        await RevertToHotspotAsync(hotspotWasUp, ct);
        return false;
    }
    return true;
}
```

### Endpoints + WS payload

5 new routes under `/api/network`: `GET /status`, `GET /scan`,
`POST /station`, `POST /hotspot`, `PUT /hotspot/credentials`.

`StatusStreamHandler` gains a `network` sub-object (mode, ssid,
ip, signal, hotspotSsid, lastError, etc) on the 1 Hz payload.

### Polkit rule (privilege)

The Polaris daemon runs as `polaris:polaris` (systemd
`User=polaris`). `nmcli connection up/down` needs NetworkManager
auth, which NM evaluates via polkit. Without a rule, every nmcli
fails with `Not authorized`.

New file: `packaging/deb/etc/polkit-1/rules.d/50-polaris-nm.rules`:

```javascript
polkit.addRule(function(action, subject) {
    if (action.id.indexOf("org.freedesktop.NetworkManager.") === 0
        && subject.user == "polaris") {
        return polkit.Result.YES;
    }
});
```

`postinst` already creates the `polaris` user, so the rule applies
directly. Polkit reload via `systemctl restart polkit` in postinst
(idempotent).

### Bootstrap script + first-boot service

`/opt/polaris/bin/polaris-wifi-bootstrap.sh`:

```bash
#!/bin/bash
set -euo pipefail
CONN=polaris-hotspot
SSID="Polaris-Hotspot"
PSK="polaris1234"
IFACE=$(nmcli -t -f DEVICE,TYPE device status | awk -F: '$2=="wifi"{print $1; exit}')
if [ -z "$IFACE" ]; then exit 0; fi
if nmcli -t connection show | grep -q "^${CONN}:"; then exit 0; fi
nmcli connection add type wifi ifname "$IFACE" con-name "$CONN" \
    autoconnect yes ssid "$SSID" \
    802-11-wireless.mode ap 802-11-wireless.band bg \
    ipv4.method shared ipv6.method ignore \
    wifi-sec.key-mgmt wpa-psk wifi-sec.psk "$PSK"
nmcli connection up "$CONN" || true
```

Wired via
`/lib/systemd/system/polaris-wifi-bootstrap.service`:

```ini
[Unit]
After=NetworkManager.service
ConditionPathExists=!/var/lib/polaris/wifi-bootstrap.done
[Service]
Type=oneshot
ExecStart=/opt/polaris/bin/polaris-wifi-bootstrap.sh
ExecStartPost=/bin/sh -c 'mkdir -p /var/lib/polaris && touch /var/lib/polaris/wifi-bootstrap.done'
[Install]
WantedBy=multi-user.target
```

Enabled via postinst. Sentinel file
`/var/lib/polaris/wifi-bootstrap.done` prevents re-execution on
upgrades — but deleting it re-runs the script (idempotent via the
`grep -q` check).

### UI (Settings → Network panel)

New section in Settings, before "Equipment simulator". Shows
current mode + SSID + IP, "Switch to Station Mode" + "Edit
hotspot SSID/pwd" buttons.

**Switch to Station modal**: scan results with signal bars, password
input, warning explaining the Pi will disconnect from its hotspot
(user needs to reconnect to the chosen network), 30 s auto-revert
disclaimer.

Click "Connect & switch" → POST `/api/network/station` → spinner
"Switching..." → 30 s later `{ok: true, ip: "..."}` or
`{ok: false, error: "..."}`. Toast with next steps.

**Edge case**: the POST itself arrives via WiFi from the hotspot,
and the response would go back the same way. When `nmcli connection
up polaris-station` takes the hotspot down, the TCP socket for the
POST dies before the response arrives. Mitigation:
1. Frontend marks `pendingSwitch = true` before the POST.
2. If the POST times out / connection-resets, frontend assumes
   "maybe it worked", waits 35 s, tries to reconnect via WS to
   the new IP (mDNS).
3. Reconnected WS shows the new `network.mode` in the payload.

If revert fires, the hotspot returns before the timeout → POST
returns `{ok: false}` normally.

Home tab also gets a small chip ("📡 Hotspot mode ·
Polaris-Hotspot" or "📡 Station mode · HomeNet5G · IP
192.168.1.42").

## Phases (6 commits)

### WIFI-1: NetworkManagerService core + detection
- Service skeleton, `IsSupportedOs` + nmcli/iface detection.
- `GetSnapshotAsync` parses `nmcli -t -f DEVICE,TYPE,STATE,CONNECTION
  device`.
- Tests: parse of nmcli output samples from Pi + Ubuntu desktop.

### WIFI-2: Switch logic + try-and-revert
- `SwitchToStationAsync` + `RevertToHotspotAsync` +
  `WaitForLeaseAsync`.
- `SwitchToHotspotAsync` + `SetHotspotCredentialsAsync`.
- Input validation (SSID len, PSK len, shell-escape).
- Tests with mock `RunNmcliAsync` simulating DHCP success,
  timeout, add-failure; assert revert paths.

### WIFI-3: Endpoints + WS payload
- `NetworkEndpoints.cs` with 5 routes + platform guards.
- `StatusStreamHandler` emits `network` block.
- Smoke via curl.

### WIFI-4: UI panel + scan modal
- Settings → Network section (idle state, status display).
- "Switch to Station Mode" modal with scan + password input +
  warning.
- "Edit hotspot SSID/pwd" small modal.
- Home tab chip.
- Frontend pending-switch + WS reconnect logic for socket-loss
  case.

### WIFI-5: .deb integration
- `polaris-wifi-bootstrap.sh` + service.
- Polkit rule.
- postinst install + systemctl enable.
- `control`: depends novos (`network-manager`, `policykit-1`).
- `postrm`: cleanup of connections on purge.
- Build + `sudo apt install ./polaris_arm64.deb` on a Pi sandbox
  → hotspot up automatically, polaris user can `nmcli connection
  up` without sudo.

### WIFI-6: Docs + verify
- `docs/user-guide/network-mode.md`.
- Update `raspberry-pi-setup.md`.
- README bullet.
- Manual verify on real Pi.

## Verification

### Smoke install
1. `sudo apt install ./polaris_arm64.deb` — postinst clean.
2. Reboot.
3. After ~30 s, `Polaris-Hotspot` visible on phone.
4. Connect with `polaris1234` → IP `10.42.0.1` (NM default).
5. Open `https://polaris-pi.local:5000`, accept cert.
6. Settings → Network shows: Hotspot · Polaris-Hotspot · 10.42.0.1.

### Switch to Station (happy path)
7. Click "Switch to Station Mode" → modal with scan.
8. Pick HomeNet5G, type correct password, click Connect & switch.
9. UI spinner 10-25 s → toast success with new IP.
10. Phone loses hotspot WiFi (Pi took it down).
11. Reconnect phone to HomeNet5G.
12. Open `https://polaris-pi.local:5000` → home appears.
13. Settings → Network shows: Station · HomeNet5G · IP · signal
    78%.

### Switch to Station (revert path)
14. Click "Switch to Station Mode", pick HomeNet5G, **wrong**
    password.
15. UI spinner 30 s → toast "WiFi credentials rejected, reverted
    to hotspot".
16. Polaris GUI still accessible on phone (never left the
    hotspot).

### Back to Hotspot
17. (In Station mode) Click "Switch to Hotspot Mode".
18. Pi brings hotspot up, phone loses home WiFi.
19. Reconnect to Polaris-Hotspot, GUI functional.

### Edit hotspot credentials
20. Click "Edit hotspot SSID/pwd" → modal.
21. Change to `My-Polaris` / `house-password` → Save.
22. Polaris recreates hotspot with new SSID + PSK.
23. Phone loses WiFi, reconnect to `My-Polaris` with new pwd.
24. GUI functional.

### Build + tests
- `dotnet build` clean.
- `dotnet test` — 700-ish atual + ~10 novos = ~710.

### Windows / macOS regression
- Windows mini-PC: Settings → Network shows banner "Network
  management requires Linux + NetworkManager." Endpoints return
  501. Other tabs work.

## Notes (security, performance, edge cases)

- **Password in transit**: POST `/api/network/station` carries
  password in plaintext. HTTPS is default on Polaris (self-signed
  cert on port 5000), so OK on LAN/hotspot. Doc note: "don't
  expose the panel to the internet without Relay tokens" (same
  posture as the rest).
- **Password at rest**: NetworkManager stores in
  `/etc/NetworkManager/system-connections/*.nmconnection` (mode
  600, root-owned). Polaris doesn't persist anything in
  `profile.json` — avoids drift + leak.
- **Hotspot default credentials**: documented clearly as public
  (`polaris1234`). User who cares changes on first access.
- **Polkit reload**: `systemctl restart polkit` in postinst is
  idempotent. If it fails (polkit not running), silent warning.
- **Pi without onboard WiFi**: `HasWifiInterface = false`, panel
  shows "No WiFi interface detected. Ethernet connections are
  managed externally."
- **Multi-WiFi**: Pi 5 has 1 onboard; if user plugs USB adapter,
  first `wifi` reported by `nmcli device status` wins. v1 doesn't
  support selecting between multiple WiFi NICs.
- **Concurrent users**: two clients on the hotspot open Network
  panel simultaneously, one switches — WS payload updates for the
  other. No race in commands (nmcli serialises via D-Bus).
- **Hidden SSID**: modal has "Other (hidden SSID)" opening
  manual SSID + password input.
- **5 GHz vs 2.4 GHz**: hotspot in 2.4 GHz (`band bg`) for max
  compatibility with old phones + Pi 4. Pi 5 supports 5 GHz but
  regulatory-domain confusion isn't worth the risk in v1.
- **Ethernet connected simultaneously**: NM handles automatically.
  Show "Ethernet: connected · 192.168.1.50" as read-only info in
  the panel when detected, no management.

## Out of scope (deferred)

- Pi OS Bullseye/Buster (wpa_supplicant).
- Bluetooth / Bluetooth tethering.
- SD card image pre-baked via pi-gen.
- Captive portal redirect.
- WPA3 (NM supports it but AP-mode WPA3 breaks too many old
  clients; stay on WPA2 v1).
- mTLS / token auth on Polaris itself (separate, on Relay
  roadmap).
- Status history (signal vs time chart).

---

# Previous plan: Color Calibration Siril-style (CCALB-0..4)

> Previous plan (CC, Mono LRGB workflow) preserved below.

## Context

After `ChannelCombineService` (CC-1..3) generates an RGB / LRGB
master, the user jumps straight to GraXpert AI cleanup, then to
the editor. Missing the step Siril does at this point: **color
calibration**. Without it, the RGB comes out with cast (usually
green-yellow from sensor bias + sky glow), and the user has to
fix manually with Temp/Tint sliders in the editor — eyeball work
and guesswork.

Siril solves with 3 tools:

1. **Background Neutralisation** (BG neut), picks an empty-sky
   patch (or auto-detects), equalises background across the 3
   channels.
2. **Color Calibration Manual**: BG neut + a known-white region
   (G2V star, galaxy core). Computes per-channel gains that
   neutralise the white.
3. **Photometric Color Calibration (PCC)**: the science feature.
   Plate-solves the frame, queries a star catalog (Gaia DR3 /
   APASS / Tycho-2) for B-V / Bp-Rp of each detected star,
   adjusts per-channel gains so stellar colors match the catalog.
   Physics-based calibration, not heuristic.

**Research** (Explore Phase 1):
- **No persisted WCS in FITS**: `AstapSolver` computes
  CRVAL/CDELT/CROTA but doesn't write back into the file header.
  Without WCS in the FITS, PCC can't run without re-solving.
- **No star catalog**: `SkyCatalogService` only has DSOs (M31,
  NGC, ...), nothing for individual stars with B-V / Bp-Rp.
- **`StarDetector` is mono-only**, returns total `Flux`. PCC
  needs `FluxR` / `FluxG` / `FluxB` per star.
- **`EditPipeline.cs` has `ColorSpace.TempTintToGain`** (Kelvin
  → RGB gains) + per-pixel multiply already. All the apply-gain
  math exists; only the gain *computation* via scientific
  calibration is missing.

**Decisions** (AskUserQuestion Phase 3):
- **Scope**: all 3 tools (BG + Manual + PCC).
- **UI**: STUDIO (not Editor). Color cal operates on linear/raw
  buffers, not 8-bit display-stretched. Output is a sibling FITS
  re-indexed by `FrameLibraryService`, same pattern as Combine /
  Calibrate / Integrate.
- **PCC catalog**: APASS bundled offline (~80 MB). Remote
  observatory without internet works; first run of a script
  populates the bundle, then it's pure local.

## Architecture

```
Server:
  CCALB-0: Foundation (shared across the 3 tools)
    StarPhotometer.cs (new in Image.Portable)
      MeasureRgb(planeSequential, w, h, detectedStars)
        → List<StarPhotometry> { X, Y, FluxR, FluxG, FluxB }
      Aperture photometry, sum per-channel in circle r=2*HFR.
      DetectedStar keeps total Flux; new record avoids polluting.

    WcsHeaders.cs (new in Image.Portable)
      Add(plate, customKeywords): inject CRVAL1/2, CRPIX1/2,
        CD1_1..CD2_2, CTYPE1=RA---TAN, CTYPE2=DEC--TAN.
      Read(headers): extract → WcsInfo { Ra0, Dec0, ScaleX,
        ScaleY, Rotation, PxRefX, PxRefY }.
      PixelToRaDec(x, y) + RaDecToPixel(ra, dec) helpers.

    FITSWriter.cs (modify): customKeywords accepts WCS block;
    FITSReader.cs (modify): ImageProperties.Wcs?
    AstapSolver.cs (modify): after solve, re-stamp source FITS
      with the WCS headers (write new .fits temp + atomic rename
      to preserve history).

  Services/Studio/ColorCalibrationService.cs (new, ~350 lines)
    Job pattern mirroring ChannelCombineService:
      StartJob(req) → jobId; GetStatus(jobId)
    Three modes: BgNeutral, Manual, Photometric
    Common: Load FITS, Validate Channels==3, compute adjustment,
      apply per-pixel out[c,i] = (in[c,i] - offset[c]) * gain[c],
      clamp [0, 65535], write sibling FITS with custom keywords
      recording the recipe (CCAL_MODE, CCAL_GAINR/G/B,
      CCAL_OFFR/G/B, CCAL_SRC = matched-star count for PCC),
      FrameLibraryService.RescanAsync().

  Services/Sky/ApassCatalog.cs (new, ~200 lines)
    Loads SQLite (Microsoft.Data.Sqlite already in project).
    Schema: stars(ra, dec, mag_v, mag_b, b_v, source) + R*tree
      idx_radec(min_ra, max_ra, min_dec, max_dec).
    QueryRegionAsync(ra, dec, radiusDeg, magLimit) → List<Star>.
    IsAvailable check + clear "run scripts/download-apass.py"
      error if SQLite absent.

  scripts/download-apass.py (new)
    Downloads APASS DR10 from AAVSO mirror (subset mag ≤ 13,
    ~80 MB), builds SQLite with R*tree at
    src/NINA.Polaris/wwwroot/catalogs/apass/apass.db
    (gitignored, csproj Content Include for publish output).

  Endpoints/StudioEndpoints.cs (extend):
    POST /api/studio/colorcal
      body: { frameId, mode, bgPatch?, whitePatch?,
              referenceStarType? (G2V default) }
      → { jobId }
    GET  /api/studio/colorcal/{jobId}
    GET  /api/studio/colorcal/catalog-status
      → { available, source, starCount, version }

Browser:
  STUDIO viewer modal (new "Color calibration" item in the
  viewer's single-frame ops menu):
    Modal with 3 tabs (mirrors CC-6 Combine modal):
      • BG: radio "Auto (lowest 5%)" | "Pick patch"
      • Manual: "Pick BG patch" + "Pick white patch"
      • PCC: catalog status badge + "Run" (auto when WCS in FITS,
        else "Plate solve first" button)
    Patch picker: overlay on viewer canvas, click+drag rect ROI,
      shows median per-channel in real-time.
    Run → POST + poll progress + toast with output path.
```

### Math per mode

**BG neutralisation**:
```
Sample (auto = lowest 5% sorted by luminance; patch = user ROI):
  For c in [R, G, B]:
    median[c] = histogram median on channel c
    offset[c] = median[c] - min(medians)
Apply: out[c, i] = (in[c, i] - offset[c])
Clamp [0, 65535]
```

**Manual color calibration**:
```
Step 1: BG neutralisation (same math), offsets applied
Step 2: White-reference scale:
  For c in [R, G, B]:
    mean[c] = average of white-patch pixels on channel c
    gain[c] = max(means) / mean[c]      (anchor brightest to 1.0)
Apply: out[c, i] = (in[c, i] - offset[c]) * gain[c]
```

**Photometric (PCC)**:
```
1. WCS check: read CRVAL/CDELT/CD or fail "plate-solve first"
2. ApassCatalog.QueryRegion(ra0, dec0, radius = half-diagonal,
                            magLimit = 12)
   → List<{ ra, dec, mag_v, b_v }>
3. StarDetector + StarPhotometer.MeasureRgb on the FITS pixels
   → List<{ x, y, fluxR, fluxG, fluxB }>
4. Match: catalog (ra,dec) → image (x,y) via WCS inverse.
   Pair with detected star within 3 px. Drop matches with
   saturated pixels (any channel >= 60000), zero flux, or
   pixelCount < 9.
5. For each matched pair:
     expected_R, expected_G, expected_B from G2V-relative B-V
       (Pickles 1998 atlas sampled at common filter responses)
     observed_R = fluxR, etc.
     ratio_c = expected_c / observed_c per star
6. Per-channel gain = median(ratio_c) across matched stars
   (robust to outliers like saturated cores, hot pixels)
7. Apply gains per pixel; offset = BG neutralisation (run first)
8. Record matched-star count in CCAL_NSTAR header
```

### Failure modes for PCC

- WCS absent in FITS: actionable error "Plate solve the source
  first (STUDIO → Solve, or run integration with platesolve
  enabled)".
- APASS DB absent: "Run `scripts/download-apass.py` once on the
  server to populate the catalog (~80 MB download)".
- Fewer than 5 matched stars: "Too few catalog matches (need ≥5,
  got N). Increase plate-solve accuracy, or fall back to Manual
  color calibration".
- All stars saturated: "All matched stars are saturated; re-run
  on a sub-frame or use shorter total integration".

## Phases (10 commits)

### CCALB-0a: WCS read/write helpers
- `WcsHeaders.cs` (new) — Add/Read/PixelToRaDec/RaDecToPixel.
- `FITSWriter.cs` modified — accepts `WcsInfo?` and emits the
  WCS keywords.
- `FITSReader.cs` modified — populates `ImageProperties.Wcs`.
- `AstapSolver.cs` modified — after solve, re-stamps source FITS
  (write-temp + atomic rename).
- `BatchStackingService.cs` modified — if input frames carry
  WCS, persist the reference's WCS on the output master.
- Tests: roundtrip; pixel↔RA/Dec invertible within 0.1 px;
  AstapSolver stamps the source FITS.

### CCALB-0b: per-channel star photometry
- `StarPhotometer.cs` (new) — `MeasureRgb(planeSequentialUshort,
  w, h, channels, stars)` returns
  `List<StarPhotometry>` with X/Y + FluxR/G/B. Aperture circle
  r = 2 * HFR. Background sub via local annulus mean. Per-pixel
  skip if saturated.
- Tests: synthetic 3-channel frame, known R=10000 G=20000 B=15000
  on stars; recover within 2%.

### CCALB-1: BackgroundNeutralization
- `ColorCalibrationService.cs` skeleton + BgNeutral mode (auto +
  patch).
- `POST /api/studio/colorcal` + GET status.
- UI: Modal with BG tab. Auto = direct Run. Patch mode = canvas
  overlay click+drag picker.
- Output sibling: `_bgneu.fits`. FITS headers `CCAL_MODE='BG'`,
  `CCAL_OFFR/G/B`.
- Tests: synthetic RGB with forced cast, BG neut recovers neutral
  background.

### CCALB-2: Manual color calibration
- Add Manual mode to service (BG offset + white-ref gain).
- Modal gets Manual tab: two pickers (BG + white).
- Output sibling: `_ccal.fits`. Headers `CCAL_MODE='MANUAL'`,
  `CCAL_OFFR/G/B`, `CCAL_GNR/G/B`.
- Tests: synthetic with cast + known white reference, recover
  neutral color.

### CCALB-3a: APASS catalog service + download script
- `ApassCatalog.cs` (new in `Services/Sky/`) — SQLite loader,
  R*tree cone search. Graceful fail when DB missing.
- `scripts/download-apass.py` (new) — wget APASS DR10 subset +
  build SQLite with R*tree.
- `.gitignore`: ignore `src/NINA.Polaris/wwwroot/catalogs/apass/`.
- `NINA.Polaris.csproj`: `<Content Include="wwwroot\catalogs\**\*">`
  for Docker / publish output.
- Endpoint `GET /api/studio/colorcal/catalog-status`.
- Tests: synthetic SQLite with 10 stars, region query works.

### CCALB-3b: PCC math + Photometric mode
- `ColorCalibrationMath.cs` (new): G2V reference B-V→RGB lookup,
  per-star ratio computation, median-gain fit.
- `ColorCalibrationService.Photometric` mode: orchestrates
  WcsHeaders.Read → ApassCatalog.QueryRegion → StarDetector →
  StarPhotometer.MeasureRgb → match → fit → apply.
- Output sibling: `_pcc.fits`. Headers `CCAL_MODE='PCC'`,
  `CCAL_NSTAR`, `CCAL_GNR/G/B`.
- Tests: synthetic frame with stars at known catalog positions
  (mock APASS), PCC recovers unity gains; with forced cast,
  recovers the inverted cast.

### CCALB-3c: PCC UI tab + catalog status badge
- Modal Color calibration gets PCC tab.
- Pre-flight check: WCS present? Catalog available? Show status
  + catalog star count in FOV. Run button only enables when both
  OK.
- Toast on done: "N stars matched, gains R=x.xx G=x.xx B=x.xx".

### CCALB-4a: Tests + lrgb-mono-workflow.md update
- Update `docs/user-guide/lrgb-mono-workflow.md` with new
  "Color calibration" section between Combine and GraXpert.
- Update `docs/user-guide/end-to-end-workflow.md` with step
  inserted between Integrate and AI cleanup.
- Update `docs/user-guide/studio.md` with the Color calibration
  tool.

### CCALB-4b: README + APASS attribution
- Update `README.md` with PCC note + APASS attribution
  (DR10 license: CC-BY 4.0, requires citation).
- `docs/user-guide/photometric-calibration.md` new: full PCC
  walkthrough, troubleshooting, alternative via Vizier online
  (deferred, document as future).

## Files created

- `src/NINA.Image.Portable/ImageAnalysis/StarPhotometer.cs`
- `src/NINA.Image.Portable/FileFormat/FITS/WcsHeaders.cs`
- `src/NINA.Polaris/Services/Studio/ColorCalibrationService.cs`
- `src/NINA.Polaris/Services/Studio/ColorCalibrationMath.cs`
- `src/NINA.Polaris/Services/Sky/ApassCatalog.cs`
- `scripts/download-apass.py`
- `src/NINA.Polaris/wwwroot/catalogs/apass/.gitignore`
- `tests/NINA.Polaris.Test/Studio/ColorCalibrationServiceTests.cs`
- `tests/NINA.Polaris.Test/Studio/StarPhotometerTests.cs`
- `tests/NINA.Polaris.Test/Studio/WcsHeadersTests.cs`
- `tests/NINA.Polaris.Test/Studio/ApassCatalogTests.cs`
- `docs/user-guide/photometric-calibration.md`

## Files modified

- `src/NINA.Image.Portable/FileFormat/FITS/FITSWriter.cs` — WCS opcional
- `src/NINA.Image.Portable/FileFormat/FITS/FITSReader.cs` — WCS extract
- `src/NINA.Image.Portable/ImageData/ImageProperties.cs` — `WcsInfo?`
- `src/NINA.Polaris/Services/PlateSolving/AstapSolver.cs` — re-stamp
- `src/NINA.Polaris/Services/Studio/BatchStackingService.cs` —
  propagate WCS
- `src/NINA.Polaris/Endpoints/StudioEndpoints.cs` — 3 new endpoints
- `src/NINA.Polaris/Program.cs` — register `ColorCalibrationService`
  + `ApassCatalog` singletons
- `src/NINA.Polaris/wwwroot/index.html` — new 3-tab modal + STUDIO
  toolbar item
- `src/NINA.Polaris/wwwroot/js/app.js` — state + methods +
  canvas patch picker
- `src/NINA.Polaris/wwwroot/css/app.css` — `.colorcal-*` +
  `.patch-picker` overlay
- `src/NINA.Polaris/NINA.Polaris.csproj` — `<Content Include>` for
  the APASS bundle
- `Dockerfile` — COPY of the bundle
- Docs + README + LICENSE entries

## Verification

### Build + tests
- `dotnet build` clean.
- `dotnet test` 670 atuais + ~30 novos = ~700.

### BG neutralisation
1. STUDIO viewer opens an RGB master with forced green cast.
2. Click Color calibration → BG tab → Auto → Run.
3. ~5 s: output `_bgneu.fits` appears, opens in viewer showing
   neutral grey background.

### Manual calibration
4. Same master → Color cal → Manual tab.
5. Click "Pick BG patch" → drag rect in dark region.
6. Click "Pick white patch" → click on known G2V star.
7. Run → ~10 s: output `_ccal.fits` with neutral color.

### PCC (offline APASS bundled)
8. Pre-flight: `python scripts/download-apass.py` once on server
   (generates apass.db ~80 MB).
9. Frame master already plate-solved; if not, STUDIO → Solve
   first.
10. Color cal → PCC tab.
11. Status badge shows "✓ APASS DR10 (~5M stars), 247 in FOV,
    WCS OK".
12. Run → ~30 s: output `_pcc.fits`. Toast shows "N=18 stars
    matched, gains R=1.12 G=0.97 B=1.05".
13. Compare side-by-side: original vs PCC output, stars have
    natural colors (K stars reddish, A stars bluish, etc.).

### Workflow end-to-end (M81 LRGB scenario)
14. AUTORUN: capture mono LRGB.
15. STUDIO: per-filter integrate (BatchStackingService).
16. STUDIO: Combine (CC-1, LRGB tab) → `lrgb_M81_*.fits`.
17. **STUDIO: Color calibration → PCC → `lrgb_M81_*_pcc.fits`**.
18. FILES: GraXpert BGE → Denoise → Decon (on `_pcc` master).
19. Editor: fine adjustment.
20. Export: final JPEG.

### Failure modes
- PCC without WCS in FITS: error toast "Plate-solve the source
  first" with clickable link opening STUDIO → Solve.
- PCC without catalog: error toast "Run
  scripts/download-apass.py" with path shown.
- PCC with <5 matched stars: error "Too few stars (N=3), need
  ≥5".

## Notes

- **Bundle size**: APASS DR10 subset mag ≤ 13 + R*tree index =
  ~80 MB. Bundling in Docker image OK; Pi 2/3 with 16 GB card is
  tight but viable. Documented.
- **APASS license**: AAVSO APASS DR10 is CC-BY 4.0. Requires
  attribution in README + footer of the PCC modal. Polaris
  MPL 2.0 stays untouched.
- **Memory**: cone search for 1°² returns ~200 stars; payload
  fits trivially in RAM even on Pi 2.
- **Performance**: PCC end-to-end ~30 s on Pi 5 (plate solve
  ~5 s, catalog query ~1 s, photometry of ~200 stars ~10 s,
  match + fit ~5 s, write FITS ~5 s).

## Out of scope (deferred)

- **PCC online via Vizier** (Gaia DR3, no bundle): second backend
  if demand exists; bundled APASS covers the common case.
- **Custom reference star types** (B0V, M5V beyond G2V): G2V
  (Sun) is industry standard; others go to v2.
- **Saturation-aware photometry with elliptical apertures**: our
  aperture is fixed circular; eccentric stars (mount drift, coma)
  may have sub-optimal photometry. PixInsight has this; Polaris
  v1 doesn't.
- **PCC integration during sequence capture** (auto after each
  master): capture is mono per-filter, PCC only makes sense on
  the post-combine RGB. Stays out of the hot path.

---

# Previous plan: Mono LRGB workflow (CC-1..7), channel combine, LRGB and PixelMath

> Previous plan (GX, GraXpert ONNX in-browser) preserved below.

## Context

STUDIO already produces **one master per filter**
(`integrated/{target}/L/master_L_*.fits`, same for R/G/B/Ha) via
`BatchStackingService`. Those masters sit in the STUDIO library
waiting for the user to "do something" with them. The only option
today is exporting mono TIFFs per filter + finishing outside
Polaris.

Missing the **channel combine** step: take separate mono masters
and assemble a single RGB or LRGB. PixInsight does this with
`ChannelCombination`, `LRGBCombination`, and `PixelMath`. Without
it, Polaris doesn't close the mono workflow end-to-end — which
is exactly what `end-to-end-workflow.md` just promised to close.

**Research** (Explore agents Phase 1):
- **Backend ready**: `FITSReader` + `FITSWriter` already support
  3-channel plane-sequential (NAXIS=3, NAXIS3=3) via
  `ImageProperties.Channels`.
- **`BatchStackingService`** is the canonical long-running job
  template (jobId + ConcurrentDictionary<progress> + RunAsync).
- **`BgePipeline`** in `onnx-pipelines.js` already runs RGB
  natively (path `opts.channels === 3` does
  resize/replicate per channel).
- **Frontend ready**: STUDIO multi-select exists
  (`studio.selectedIds`), selection-bar has 4 buttons + room for
  a 5th "🎨 Combine".

**Confirmed gaps**:
1. No `ChannelCombineService` in the codebase (search for
   "ChannelCombine|LrgbCombine|RgbCompose|PixelMath" empty).
2. `ImageEditService.cs:129` hardcodes `channels = 1` when opening
   FITS, silently flattening RGB combine results to mono in the
   EDITOR.
3. `DenoisePipeline` + `DeconPipeline` in `onnx-pipelines.js`
   (`runPerChannel` line 904) force mono input — same problem on
   AI cleanup post-combine.

**Decisions** (AskUserQuestion in the Plan phase):
- **v1 scope**: RGB + LRGB + PixelMath, simple expressions
  without statements, named variables (R, G, B, L, Ha, OIII, SII,
  free).
- **Downstream**: fix editor + ONNX Denoise/Decon in the same
  plan, channel combine is useless if the rest of the pipeline
  silently flattens to mono.

## Architecture

### Algorithms (LRGB)

**Lab Swap** (default, PixInsight-style):

1. RGB → linear sRGB (gamma 2.2 inverse): `linear = pow(srgb/65535, 2.2)`
2. linear sRGB → CIE XYZ via D65 matrix.
3. XYZ → Lab via `f(t) = t^(1/3)` piecewise.
4. Replace `L_lab` with the L master histogram-matched to
   `luminance(rgb)`.
5. Lab → XYZ → linear sRGB → RGB.

**Ratio** (classical):

```
Lum_rgb = 0.2126*R + 0.7152*G + 0.0722*B    (Rec.709)
ratio = L / max(Lum_rgb, 1)
R' = clamp(R * ratio)
G' = clamp(G * ratio)
B' = clamp(B * ratio)
```

Histogram-match L's `median` and MAD to `luminance(RGB)` before
the swap, otherwise the L overpowers the RGB.

### Cross-channel registration (default ON)

Per-filter masters come out of `BatchStackingService` aligned to
the best-HFR-frame **of that job**, i.e. master_R aligned to the
best R, master_G aligned to the best G, etc. Even in the same
session without re-framing, this almost never produces sub-pixel
agreement between master_L and master_R (different reference
frames, different plate-solve, different momentary seeing).
Result: RGB stack without registration shows colour fringes on
stars and ghost doubles on nebulae.

Mandatory v1 (toggle "Register channels" default ON; off-switch
exists for rare cases like permanent observatory with rotator
zeroed):

```csharp
// inside ChannelCombineService, after Load + Validate
var detector = new StarDetector {
    SigmaThreshold = 7.0,    // masters have high SNR, raise to avoid halos
    MaxStarSize = 80          // lower MaxStarSize, master stars are sharper
};
var refIdx = mode == LrgbCompose ? indexOfL : 0;
var refStars = detector.Detect(planes[refIdx], w, h);
for (int i = 0; i < planes.Length; i++) {
    if (i == refIdx) continue;
    var stars = detector.Detect(planes[i], w, h);
    var t = StarMatcher.Match(refStars, stars,
        maxSearchRadius: 100);
    if (t == null) {
        throw new InvalidOperationException(
            $"Could not register channel {channelNames[i]} to reference " +
            $"{channelNames[refIdx]}: too few matched stars. " +
            $"Detected {stars.Count} stars in this channel, " +
            $"{refStars.Count} in reference.");
    }
    planes[i] = ImageResampler.ApplyTransform(planes[i], w, h, t);
    transforms[i] = t;
}
```

FITS headers recorded per combine output:
- `REGISTER` = `T` or `F`
- `REGREF` = reference channel name (e.g. `L`)
- `REG_{ch}` = `M00,M01,M10,M11,Tx,Ty` of the applied transform.

### Optional normalisation

After registration, before compose: scale per-channel to a common
median (reference = max of all medians) via linear factor. Useful
when R/G/B were captured in different sessions with different
backgrounds. Toggle ON by default for RGB/LRGB, OFF for PixelMath
(user can pre-scale in the expression).

### PixelMath UX

Modal shows:

```
Frame variable mapping:
  master_L_M31_120x180s.fits  →  [L]
  master_R_M31_60x180s.fits   →  [R]
  master_G_M31_60x180s.fits   →  [G]
  master_B_M31_60x180s.fits   →  [B]
  master_Ha_M31_30x300s.fits  →  [Ha]

Output channels:
  R  =  [ 0.7*R + 0.3*Ha ]
  G  =  [ G               ]
  B  =  [ B               ]
  (or single field if "Mono output" toggled)
```

Client-side validation: parse each expression before submit;
inline error ("variable 'X' undefined" / "unbalanced parenthesis").

## Phases (7 commits)

### CC-1: ChannelCombineService skeleton + RgbCompose + registration
- New `ChannelCombineService.cs` job-pattern mirroring
  `BatchStackingService`.
- Mode `RgbCompose`: loads 3 frames via
  `FrameLibraryService`, validates W×H identical.
- **Cross-channel star register** (default ON):
  `StarDetector` (`SigmaThreshold=7.0`, `MaxStarSize=80`) on each
  plane, first selected = reference, `StarMatcher.Match` +
  `ImageResampler.ApplyTransform` per non-ref channel. Fail loud
  on match failure.
- Normalise per-channel optional post-registration.
- Final RGB plane-sequential pack + `FITSWriter` with
  `Channels=3`.
- Output `integrated/{target}/composed/rgb_{target}_{stamp}.fits`
  via extension of the `ImageWriterService.BuildSubDir` switch
  with case `"COMPOSED"`.
- Endpoint `POST /api/studio/combine` (mode=rgb) +
  `GET /api/studio/combine/{jobId}`.
- Reindex via `FrameLibraryService.RescanAsync()` at job end.
- Tests: synthetic 3 mono perfectly aligned → byte-correct RGB
  per plane; synthetic R shifted +5px,+3px → after registration,
  pixel-perfect aligned (RMS centroid < 0.5 px); registration OFF
  skips the phase; mismatch dimensions → actionable error.

### CC-2: LrgbCombiner (Lab + Ratio) + LrgbCompose mode
- New `LrgbCombiner.cs` with `LabSwap` and `Ratio`.
- Roundtrip tests RGB→Lab→RGB tolerance ±2 ADU in 16-bit.
- Histogram-match helper (median + MAD scale).
- `ChannelCombineService` gets `LrgbCompose` mode (4 inputs).
- Output `lrgb_{target}_{stamp}.fits`.
- Tests: synthetic L bright + RGB dim → output preserves colour
  + tracks luminance.

### CC-3: PixelMathEvaluator + PixelMath mode
- New `PixelMathEvaluator.cs` with recursive-descent parser
  AOT-friendly.
- Compiles to `Func<Dictionary<string,float>, float>` single-pass.
- Built-in functions: min, max, abs, pow, sqrt, exp, log, clamp.
- Validation: undefined identifiers → exception with position.
- `ChannelCombineService` gets `PixelMath` mode: N named inputs,
  1 expression (mono out) or 3 (RGB out).
- Tests: precedence (2+3*4=14), parens, function call, undefined
  variable, identity expression produces output==input.

### CC-4: Fix ImageEditService + EditPipeline RGB path
- `ImageEditService.cs:129` reads `img.Properties.Channels`
  instead of hardcoded 1.
- Audit `EditPipeline.Apply` (already exists) confirming it
  respects `Channels=3` per step (Color does HSL on RGB; Light
  does per-pixel tonal; Detail USM per-channel; etc). Add RGB
  tests where missing.
- E2E smoke: editor opens RGB FITS, Color sliders functional,
  save sidecar, reopen, preserves.

### CC-5: ONNX Denoise + Decon RGB path
- Mirror `BgePipeline.runRgb` in `onnx-pipelines.js` for
  `DenoisePipeline` and `DeconPipeline`:
  - If `opts.channels === 3` → split plane-sequential into 3
    mono buffers → 3× `runPerChannel` → recombine
    plane-sequential.
  - Memory budget OK on iOS (FP16 path already picks backend by
    model size).
- JS smoke tests: mock session returning input → assert
  recombine produces same buffer.

### CC-6: UI Combine modal + selection-bar wiring
- "🎨 Combine" button in studio selection-bar, enabled when
  `selectedIds.length ≥ 2` and all are masters
  (`type === 'MASTER'` via IMAGETYP).
- Modal `studio-combine-modal` with 3 tabs (Alpine).
- RGB tab: 3-row table (R/G/B) with `<select>` populated by
  selectedIds, auto-pick by `frame.filter` on open.
- LRGB tab: same + L row + radio `algorithm: lab | ratio`.
- PixelMath tab: variable-name input per frame + N text fields
  for expressions + "Mono output" checkbox.
- **Register channels (recommended)** checkbox, default ON (all
  modes). Tooltip explains when to disable.
- Normalise checkbox (default ON for rgb/lrgb, OFF for pm).
- Live output-path preview computed.
- Run button → POST + poll progress + toast on done + auto-open
  output in editor.

### CC-7: Tests + docs + verify
- `tests/NINA.Polaris.Test/ChannelCombineServiceTests.cs`
  covering the 3 modes + edge cases (mismatch, normalise, jobId
  polling).
- `tests/NINA.Polaris.Test/PixelMathEvaluatorTests.cs` (~15
  cases).
- `tests/NINA.Polaris.Test/LrgbCombinerTests.cs` (Lab roundtrip,
  Ratio math).
- JS smoke test for DenoisePipeline RGB path.
- New `docs/user-guide/lrgb-mono-workflow.md`, mono branch of
  the end-to-end workflow: L + R + G + B + Ha + OIII capture →
  per-filter masters → combine → AI cleanup → editor → export.
- Update `docs/user-guide/end-to-end-workflow.md` with "Mono
  LRGB variation" pointing at `lrgb-mono-workflow.md`.
- Update `docs/user-guide/studio.md` with new Combine tool.

## Files created

- `src/NINA.Polaris/Services/Studio/ChannelCombineService.cs`
- `src/NINA.Polaris/Services/Studio/LrgbCombiner.cs`
- `src/NINA.Polaris/Services/Studio/PixelMathEvaluator.cs`
- `tests/NINA.Polaris.Test/ChannelCombineServiceTests.cs`
- `tests/NINA.Polaris.Test/PixelMathEvaluatorTests.cs`
- `tests/NINA.Polaris.Test/LrgbCombinerTests.cs`
- `docs/user-guide/lrgb-mono-workflow.md`

## Files modified

- `Endpoints/StudioEndpoints.cs` — 2 new endpoints
- `Program.cs` — register `ChannelCombineService` singleton
- `Services/ImageWriterService.cs` — case `"COMPOSED"` in
  `BuildSubDir`
- `Services/Editor/ImageEditService.cs` — fix line 129
  (`channels = img.Properties.Channels`)
- `Services/Editor/EditPipeline.cs` — audit RGB path (if any gap)
- `wwwroot/js/onnx-pipelines.js` — RGB path on Denoise + Decon
- `wwwroot/index.html` — Combine button + modal
- `wwwroot/js/app.js` — state + `studioCombine*` methods
- `wwwroot/css/app.css` — `.studio-combine-modal` (small)
- `docs/user-guide/end-to-end-workflow.md` — "Mono LRGB variation"
  section
- `docs/user-guide/studio.md` — Combine tool doc
- `docs/user-guide/README.md` — new link
- `README.md` — short mention

## Reused code

- `Services/Studio/BatchStackingService.cs:37-276` — literal
  template for the job pattern AND the registration pipeline
  (lines 72-148 do exactly Star detect → Match → Resample we
  want to replicate cross-channel).
- `Services/Studio/MasterFrameService.cs` — pattern of combining
  N frames per-pixel (sigma-clipped); reuse the per-pixel loop
  structure.
- `Services/Studio/FrameLibraryService.GetById` — load inputs.
- `Image.Portable/ImageAnalysis/StarDetector.cs` —
  `Detect(ushort[], w, h)` stateless, returns
  `List<DetectedStar>` (X, Y, HFR, Peak, Flux).
- `Image.Portable/ImageAnalysis/StarMatcher.cs` —
  `Match(refStars, currentStars, maxSearchRadius, maxStarsToUse,
  ransacIterations)` static, returns `AffineTransform?`.
- `Image.Portable/ImageAnalysis/AffineTransform.cs` +
  `ImageResampler.cs` — `ApplyTransform(ushort[] source, w, h,
  T)` returns aligned grid.
- `Image.Portable/FileFormat/FITS/FITSReader.cs:17-46` — already
  reads NAXIS=3 → `Channels=3`.
- `Image.Portable/FileFormat/FITS/FITSWriter.cs:57-80` — already
  writes 3-channel plane-sequential.
- `Image.Portable/ImageData/ImageProperties.cs:21` — `Channels`
  property.
- `Image.Portable/ImageAnalysis/ColorSpace.cs` — RGB↔HSL
  helpers (add Lab in the same file if absent).
- `Services/ImageWriterService.cs:250-275 BuildSubDir` — switch
  pattern to extend with `"COMPOSED"`.
- `Services/Editor/EditPipeline.cs` — verify RGB-aware.
- `wwwroot/js/onnx-pipelines.js BgePipeline.runRgb` (line ~675)
  — exact pattern to mirror in Denoise + Decon.
- `wwwroot/js/app.js studioStartIntegrate()` — model for
  `studioRunCombine()`.
- HTML `studio-integrate-modal` (~line 4469) — model for
  `studio-combine-modal`.
- Selection-bar (~line 4258), 4 existing buttons, add 5th in the
  same row.

## Verification

### Build + tests
- `dotnet build` clean.
- `dotnet test` 620 atuais + ~30 novos = ~650 verdes.

### Mono workflow complete (Bortle 4 backyard, mono ZWO 2600MM)
1. AUTORUN: 30× L 180 s + 15× R 180 s + 15× G 180 s + 15× B 180 s
   + 20× Ha 300 s.
2. STUDIO rescan, calibrate per filter, integrate per filter:
   - `integrated/M31/L/master_L_30x180s.fits`
   - `integrated/M31/R/master_R_15x180s.fits`
   - `integrated/M31/G/master_G_15x180s.fits`
   - `integrated/M31/B/master_B_15x180s.fits`
   - `integrated/M31/Ha/master_Ha_20x300s.fits`
3. STUDIO select 4 masters R/G/B/L (Ctrl-click), click
   **🎨 Combine**.
4. Modal opens on LRGB tab, auto-detect mapping by filter.
5. Algorithm: Lab Swap (default), Register channels ON,
   Normalise ON.
6. Click Run, progress shows "Detecting stars in 4 channels..."
   → "Registering R/G/B to L..." → "Combining..." → done.
   ~25 s on Pi 5 (registration adds ~15 s vs combine alone).
7. Output `integrated/M31/composed/lrgb_M31_*.fits` appears in
   library, "RGB" badge visible. FITS headers `REGISTER=T`,
   `REGREF=L`, `REG_R=...` etc.
8. Crop 200% in PixInsight to check stars: no colour fringes
   (registration worked). Compare with registration OFF — fringes
   clearly visible.
9. Click "Open in editor" from the toast, editor loads RGB FITS
   (Color sliders active with Temp/Tint/Vibrance/Saturation/Hue
   working), confirms CC-4 fix.
10. EDITOR → AI section → Denoise → output FITS RGB (3-channel),
    reopens confirming CC-5 RGB path.
11. EDITOR → Light + Color + Effects adjust → Save edits → Export
    JPEG quality 92.

### HOO synth (DSLR Bortle 8 with only Ha + OIII)
12. Select master_Ha + master_OIII, click Combine, PixelMath tab.
13. Variable mapping: Ha → `Ha`, OIII → `OIII`.
14. Output channels: R = `Ha`, G = `0.5*Ha + 0.5*OIII`,
    B = `OIII`.
15. Mono output OFF, Normalise OFF.
16. Run → output RGB visible in the classic HOO narrowband colour.

### Failure modes
- Mismatch W×H between inputs → actionable error "Frame X is
  3000×2000, expected 4000×3000 (matching first frame)", job
  marked `failed`.
- Registration fails (`StarMatcher.Match` returns null for some
  channel): descriptive error with detected star counts +
  suggestion "increase exposure for filter X, or disable Register
  channels if you trust your pre-existing alignment". Job ends
  `failed`.
- PixelMath invalid expression → 400 response with line+column.
- Editor opens RGB FITS but with channel mismatch (PixInsight
  multi-image XISF packed) → fallback to mono with warning log
  (existing behaviour preserved).

## Notes

- **LRGB workflow is the mono core**; this plan closes the
  "promise" made in end-to-end-workflow.md about Polaris covering
  the complete pipeline.
- **Cross-channel star alignment is MANDATORY by default**: each
  per-filter master comes out of `BatchStackingService` aligned
  to its own job's reference frame, master_R is not pixel-perfect
  with master_L. Without registration the RGB output has colour
  fringes on stars. Polaris detects stars on each plane
  (`SigmaThreshold=7.0`, higher than single-sub because of higher
  SNR), runs `StarMatcher.Match` against the reference channel,
  resamples. Toggle "Register channels" default ON, off-switch
  exists for permanent observatories with rotator zeroed.
- **Bit depth**: input mono is uint16, output RGB also uint16
  plane-sequential. No visible intermediate float precision to
  the user; internal calcs in float (Lab transform requires
  float).
- **PixelMath is minimal**: no statements, no temp variables, no
  pixel-coord access (x/y). Covers 80% of cases (narrowband
  boost, synth L, channel ratio); advanced features go to v2.
- **Memory**: 4 masters 24Mpx mono = ~192 MB weight, RGB output
  ~144 MB. Pi 4 with 4 GB handles it, Pi 2 with 1 GB tight.
  Documented.

## Out of scope (deferred)

- Pre-baked SHO Hubble palette wizard (user can code via
  PixelMath).
- Star removal (StarNet++ wrap) for LRGB with starless + star
  RGB layers.
- Multi-frame XISF (PixInsight project) input.

---

# Previous plan: GraXpert ONNX in-browser (GX-1..8)

> Previous plan (NET, client-side network activity indicator) preserved below.

## Context

GraXpert integration (FILES tab + AutoGraXpert in SequenceEngine)
calls the `graxpert` binary as a subprocess. Three issues:

1. **Host coupling**: an iPhone client sees the buttons but
   needs the Polaris server host to have the CLI installed +
   models downloaded on disk. Mobile-only setups (Polaris on a
   remote PC) depend on host config.
2. **Server CPU**: subprocess runs on the server, so on Pi 4
   each BGE/decon/denoise op takes minutes.
3. **Maintenance**: each GraXpert release demands updating the
   installed CLI; Polaris knows nothing about the model versions
   the binary uses internally.

User has the GraXpert repo cloned at
`C:\Users\danie\source\repos\DanWBR\GraXpert\` with **5 ready
ONNX models** (~1.5 GB total):

| Model | Path | Size | Input shape | Notes |
|---|---|---|---|---|
| BGE | `models/bge-ai-models/1.0.1/model.onnx` | 208 MB | [B,256,256,3] | Single forward pass, no tiling |
| Denoise v2 | `models/denoise-ai-models/2.0.0/model.onnx` | 284 MB | [B,256,256,3] | Tiling 256/stride 128 |
| Denoise v3 | `models/denoise-ai-models/3.0.2/model.onnx` | 456 MB | [B,256,256,3] | Tiling 256/stride 128, ±1 clip |
| Decon Stars | `models/deconvolution-stars-ai-models/1.0.0/model.onnx` | 267 MB | [B,1,512,512] | Tiling 512/stride 448, aux params |
| Decon Objects | `models/deconvolution-object-ai-models/1.0.1/model.onnx` | 267 MB | [B,1,512,512] | Same |

Models are **float32 NHWC** (BGE/Denoise) or **NCHW** (Decon,
single channel). License: **CC BY-NC-SA 4.0** (different from
the GPL-3 code — non-commercial use).

**Decisions**:
- **OS-independent implementation**: server is just a CDN for
  models (HTTP route serves the bytes), inference runs 100% in
  the client browser via `onnxruntime-web`. Works on laptop,
  Android, iOS 16.4+, any device with WebGPU OR WASM SIMD.
- **Mode**: **auto-detect** on the WS handshake. Client reports
  capability (`wasmReady`, version), server decides. Manual
  override in Settings (force server / force client).
- **Multi-client**: each client stacks independently. No
  master/slave. Reconnect mid-session → resumes from server's
  current frame.

## Architecture

```
┌── Server (any host: Pi, mini-PC, Windows) ──────────────────────┐
│  OnnxModelRegistry (in-memory dict)                             │
│   ├─ Reads from Onnx:ModelsPath setting (absolute path)         │
│   ├─ Recursive walk for model.onnx + parse path                 │
│   │   to extract family + version                               │
│   └─ SHA-256 hash of file (lazy, on-demand)                     │
│                                                                  │
│  OnnxModelEndpoints                                              │
│   ├─ GET /api/onnx/manifest                                      │
│   │    → { models: [{family, version, sizeBytes, hash}]}         │
│   ├─ GET /api/onnx/model/{family}/{version}                      │
│   │    → application/octet-stream + ETag (= hash)                │
│   │    + Cache-Control: immutable, max-age=31536000              │
│   │    Browser caches in IndexedDB indefinitely; ETag survives   │
│   │    reloads + new sessions                                    │
│   └─ POST /api/onnx/save (multipart: source=, suffix=, bytes=)   │
│        → writes result as sibling FITS, returns path             │
└──────────────────────────────────────────────────────────────────┘

┌── Browser (any client OS) ──────────────────────────────────────┐
│  wwwroot/js/lib/onnxruntime-web/  (vendored ~5 MB JS + WASM)    │
│    ort.min.js + ort-wasm-simd-threaded.wasm + ort-webgpu.js     │
│    Backends: webgpu (preferred) → wasm-simd → wasm (fallback)   │
│                                                                  │
│  onnx-pipelines.js  (new)                                        │
│   ├─ OnnxRegistry.fetchManifest()                                │
│   ├─ OnnxRegistry.load(family, version)                          │
│   │   ↳ check IndexedDB by hash; else GET + cache + return      │
│   │     bytes; pass to ort.InferenceSession.create()            │
│   ├─ BgePipeline.run(pixels, w, h, channels, opts)              │
│   ├─ DenoisePipeline.run(pixels, w, h, channels, opts)          │
│   └─ DeconPipeline.run(pixels, w, h, channels, opts)            │
│                                                                  │
│  FILES tab modal + Editor "AI" section                           │
│   ├─ Detects ORT Web ready + manifest model availability         │
│   ├─ Dispatches via OnnxPipelines (browser inference)            │
│   └─ Falls back to CLI subprocess if toggle/incapable            │
└──────────────────────────────────────────────────────────────────┘
```

### Pipelines (math mirrored from GraXpert Python)

**BGE** (`graxpert/background_extraction.py:27-116`):
1. Shrink to 256×240, pad to 256×256 with edge mode.
2. Per-channel MAD normalise: `(v − median) / MAD × 0.04`, clip ±1.
3. Mono → expand to 3 channels.
4. `session.run({"gen_input_image": [1,256,256,3]})`.
5. Denormalise: × MAD/0.04 + median.
6. Gaussian blur σ=3.0, resize back to w×h.
7. Apply sample points + RBF/spline (interpolation pure JS).
8. Subtract OR divide from original (user mode).

**Denoise** (`graxpert/denoising.py:14-160`):
1. Tile size 256, stride 128, overlap 50%.
2. Padding: `(window_size − stride) / 2 = 64` on every border.
3. Per-tile MAD normalise (global subsampled median+MAD), clip ±10
   (v2) or ±1 (v3).
4. Batch tiles (8 per call — power-of-2, configurable).
5. De-tile: extract `[64:64+128, 64:64+128, :]` from each tile,
   paste on stride-aligned grid.
6. Blend strength: `out = denoised × s + original × (1−s)`.

**Decon** (`graxpert/deconvolution.py:12-177`):
1. Tile 512, stride 448, overlap 64.
2. Per-tile log-normalise:
   `(log(v − min + 1e-5) − mean) / (std × 0.1)`.
3. 2 inputs: `gen_input_image [B,1,512,512]` +
   `params [B,2]` = `[sigma_normalised, strength × 0.95]`.
4. Output is the residual; subtract from input.
5. De-tile: extract `[64:64+448, ...]`, stitch.
6. Inverse log-normalise: `exp(v × std × 0.1 + mean) + min`.

### Model serving + IndexedDB cache

Server:
- Single setting `Onnx:ModelsPath` (string, default null) pointing
  at a directory containing the models. Polaris does recursive
  walk looking for `model.onnx` and infers `family/version` from
  the path (compatible with GraXpert layout:
  `{family}-ai-models/{version}/model.onnx`).
- If setting empty OR folder absent: manifest returns empty list,
  UI grey with banner asking to configure.
- SHA-256 hash computed lazily + cached in memory.
- Serves bytes with `ETag: "{hash}"` + `Cache-Control: immutable,
  max-age=31536000` — first GET downloads, subsequent 304.

Client:
- ORT Web bootstrap lazy: only loads when user invokes a GraXpert
  op for the first time in the session.
- Model bytes → IndexedDB store `polaris-onnx-models`, key =
  `{family}/{version}/{hash}`. Next sessions reuse without
  hitting the server (even offline afterwards).
- Hash mismatch (server updated model) → drop entry + re-download.

### iOS / Android constraints

- **iOS Safari ≥16.4**: WebGPU available, WASM SIMD OK. Heap
  limit ~2 GB per origin; Denoise v3 (456 MB) loadable but
  tight. Default v2 (284 MB) on mobile detected by user-agent;
  user can switch to v3 in Settings.
- **Android Chrome ≥113**: WebGPU + WASM SIMD native, heap less
  restrictive than iOS.
- **Bandwidth**: 1.5 GB of models on first use per client. On
  LAN WiFi 5: ~3 minutes. On LTE: skip. Hint UI "First-time
  download: 208 MB" on the BGE / etc button.

### Editor integration (ED-* already shipped)

ONNX ops need **scene-referred** (linear pre-stretch) full-bit-
depth. Editor today works in 8-bit display-stretched. Simple v1
solution:
- "AI" section in the editor with 3 buttons (BGE, Denoise, Decon).
- Each one: takes the current session via
  `/api/editor/raw/{sessionId}` (already exists — returns the
  8-bit working buffer), processes, OR alternatively fetches the
  source FITS via new `/api/editor/source-raw/{sessionId}`
  returning the ushort[] pre-stretch.
- For v1 restricted to 8-bit (degrades a bit vs CLI but works).
  Full-bit-depth follow-up.

### CC BY-NC-SA 4.0 license

- First time user clicks a GraXpert op: consent modal ("Models by
  GraXpert dev team, CC BY-NC-SA 4.0, non-commercial use — read
  more / I agree"). Flag saved in localStorage + on server
  profile.
- Settings panel shows link to licenses.
- Polaris does NOT redistribute the models in the installer —
  discovered only via GraXpert install OR upstream download under
  consent.

## Phases (8 commits, plus 4 follow-ups GX-9..12)

### GX-1: Model serving + ORT Web vendored
- Vendor `onnxruntime-web@1.x` in `wwwroot/js/lib/onnxruntime/`
  (ort.min.js + ort-wasm-simd.wasm + ort-webgpu binding).
- New `Services/Onnx/OnnxModelRegistry.cs`: recursive walk of
  `Onnx:ModelsPath`, extracts `family/version` from path, lazy
  SHA-256.
- New `Endpoints/OnnxEndpoints.cs`: manifest + model bytes
  (ETag-based conditional GET) + POST /save.
- Settings: new `Onnx:ModelsPath` field (string?, default null) +
  Settings UI panel shows detected models or banner to configure.
- Smoke: set path to `C:\…\GraXpert\models` → manifest returns 5
  models; GET `/api/onnx/model/bge/1.0.1` downloads 208 MB with
  ETag; second GET unchanged → 304.

### GX-2: BGE pipeline + FILES tab toggle
- `wwwroot/js/onnx-pipelines.js` with `OnnxRegistry` + class
  `BgePipeline`.
- BGE: implement shrink + MAD normalise + inference + denormalise
  + Gaussian smooth + sample-point RBF + subtract/divide.
- FILES tab GraXpert modal: new toggle "Run in browser" (default
  ON when ORT Web ready + model available).
- Toggle ON → BgePipeline.run, result → POST `/api/onnx/save`.
- Toggle OFF → falls through to existing GraXpertService CLI.
- Parity test: golden FITS → CLI output vs browser output, ≤1%
  pixel diff.

### GX-3: Denoise pipeline (v2 + v3 selection)
- `DenoisePipeline` with tiling 256/stride 128, blend
  stride-based.
- Manifest exposes v2 + v3, UI radio (default v2 on mobile, v3
  on desktop detected by screen width + UA).
- Reuses OnnxRegistry + save endpoint.

### GX-4: Deconvolution (Stars + Objects)
- `DeconPipeline` with tiling 512/stride 448, log-normalise, aux
  params, subtract residual.
- UI radio Stars / Objects in modal.
- Reuses plumbing.

### GX-5: Editor "AI" section
- New collapsible "AI" on the editor's right panel (ED-4 markup).
- 3 buttons: "Background extract" / "Denoise" / "Deconvolution".
- Each one: opens small param dialog, calls pipeline on the
  current session's working buffer, posts result via
  `/api/editor/replace-working/{sessionId}` (new endpoint:
  swaps the session's cached buffer with the new; next preview
  reflects it).
- Non-destructive: reopening the source removes the effect
  (not persisted in sidecar — operation is in-session).

### GX-6: License consent + first-time UX
- Consent modal CC BY-NC-SA 4.0 on first invocation.
- Flag persisted in localStorage + UserProfile
  (`Onnx:LicenseAcknowledged = true`).
- Settings panel gets "AI inference (ONNX)" section showing
  detected models + sizes, IndexedDB cache size, "Clear cache"
  button, license links.

### GX-7: CLI hidden behind advanced toggle
- Settings adds toggle "Use GraXpert CLI subprocess instead of
  in-browser inference" — default OFF.
- When OFF: GraXpert UI (FILES tab buttons + editor AI section)
  hides CLI controls and shows the browser-pipeline.
- When ON: existing behavior (subprocess via GraXpertService).
- Tooltip "Advanced: useful when the browser doesn't have enough
  GPU/RAM for the models".

### GX-8: Tests + docs + parity verification
- `OnnxModelRegistryTests.cs` — auto-discovery, manifest shape,
  hash stability.
- `OnnxEndpointsTests.cs` — model serve with ETag, conditional
  GET, save endpoint.
- Manual parity script: 3 synthetic + real frames, run CLI +
  browser pipeline, measure pixel diff. Accept ≤1% RMSE.
- `docs/user-guide/onnx-inference.md` — workflow + supported
  browsers + license + mobile constraints.
- README section "AI processing (ONNX)" + attribution.

### Follow-ups GX-9..12 (subsequent commits)
- GX-9: RGB FITS support in ONNX pipelines (3-channel paths).
- GX-10: HTTPS self-signed for WebGPU on LAN (WebGPU requires
  secure context).
- GX-11: Before/after slider after GraXpert run.
- GX-12: Port GraXpert autostretch (15% Bg, 3 sigma) as default.

## Files created

- `src/NINA.Polaris/Services/Onnx/OnnxModelRegistry.cs`
- `src/NINA.Polaris/Endpoints/OnnxEndpoints.cs`
- `src/NINA.Polaris/wwwroot/js/lib/onnxruntime/` (vendored bundle)
- `src/NINA.Polaris/wwwroot/js/onnx-pipelines.js`
- `tests/NINA.Polaris.Test/OnnxModelRegistryTests.cs`
- `tests/NINA.Polaris.Test/OnnxEndpointsTests.cs`
- `docs/user-guide/onnx-inference.md`

## Files modified

- `Services/ProfileService.cs` — `Onnx:ModelsPath` (string?),
  `Onnx:LicenseAcknowledged` (bool),
  `Onnx:DefaultDenoiseVersion` (string, default "2.0.0").
- `Program.cs` — register `OnnxModelRegistry` +
  `MapOnnxEndpoints()`.
- `wwwroot/index.html` — script tag ORT Web (lazy import); new
  "AI" section in editor; license consent modal; FILES tab modal
  toggle "Run in browser"; Settings section "AI inference".
- `wwwroot/js/app.js` — pipeline wiring, IndexedDB cache,
  dispatch toggle, license modal.
- `wwwroot/css/app.css` — small additions for license modal + AI
  section.
- `Endpoints/EditorEndpoints.cs` — new
  `POST /api/editor/replace-working/{sessionId}` for GX-5 to
  inject AI op result back into the editor session.
- `Services/Editor/ImageEditService.cs` — method
  `ReplaceWorkingBuffer(sessionId, bytes, w, h, channels)` called
  by the new endpoint.
- `README.md` — "AI processing (ONNX)" section.

## Reused code

- `Services/External/GraXpertService.cs` — KEEP as CLI fallback;
  just hide by default.
- `Services/External/BinaryLocator.cs` — pattern of cross-OS
  auto-discovery (replicate for `OnnxModelRegistry`).
- `Services/Editor/ImageEditService.cs` — session pattern; new
  `ReplaceWorkingBuffer` reuses `_sessions` dict + reuses the
  stretching done at Load.
- `Endpoints/EditorEndpoints.cs::raw` — pattern of binary stream
  with `X-Width/X-Height/X-Channels` headers — replicate for
  ONNX models.
- `wwwroot/js/app.js _editorLoadWasmBuffer` — pattern of binary
  buffer fetch + IndexedDB cache (already implemented for ED-6
  WASM).
- `wwwroot/js/lib/lz4.js` — not used here, but the vendored
  library pattern serves as reference for ORT Web vendoring.

## Verification

### Preconditions
- Modern desktop browser (Chrome/Edge ≥113, Safari ≥16.4,
  Firefox ≥118) with WebGPU OR WASM SIMD.
- Polaris server with setting `Onnx:ModelsPath` pointing at a
  directory containing the GraXpert models.

### Smoke
1. `dotnet build` clean + `dotnet test` green (~620 tests).
2. `GET /api/onnx/manifest` returns 5 models with correct sizes.
3. `GET /api/onnx/model/bge/1.0.1` downloads 208 MB once; second
   GET returns 304 Not Modified.
4. Open EDITOR → AI section → click "Background extract":
   - Console: "Loading model bge/1.0.1 (208 MB)…" + spinner.
   - After load, "Running BGE on master (3000×2000 RGB)…".
   - ~10-30 s later (laptop with GPU) preview updates with
     background removed.
5. Refresh browser → AI section → BGE again:
   - Console: "Model bge/1.0.1 hit IndexedDB cache" → load
     < 1 s.
6. FILES tab: select a FITS → "BGE" button → toggle "Run in
   browser" → click Run → ~30 s later `…_bge.fits` appears in
   the folder + visible in FrameLibrary.

### Parity vs CLI
7. Take a test FITS; run via CLI (`graxpert -cmd
   background-extraction …`) → ref output.
8. Same FITS via browser pipeline → diff RMSE vs ref.
9. Accept ≤1% RMSE; document the expected difference
   (tile-boundary blending can diverge slightly).

### Mobile
10. iPhone Safari (iOS 16.4+) → same flow as step 4:
    - Default Denoise is v2 (auto-selected by mobile detection).
    - BGE runs in ~60-90 s (CPU-bound on A-series), produces
      correct output.
11. Android Chrome → same, with WebGPU using GPU (faster).

### Failure scenarios
- **WebGPU + WASM SIMD both unavailable** (very old browser) →
  AI section shows "Browser too old; enable CLI in Settings"
  banner.
- **Model not found** (empty registry) → manifest returns
  `{models: []}`; UI hides buttons + shows banner "Configure
  Onnx:ModelsPath in Settings or wait for download".
- **iOS heap OOM on Denoise v3** → catch + toast "Model too
  large for this device; switching to Denoise v2".
- **License consent not accepted** → modal blocks the run +
  forces decision.
- **No internet AND model not in local cache** → manifest shows
  "available: false" + UI grey with tooltip.

## License, performance, security notes

- **CC BY-NC-SA 4.0 of models**: Polaris doesn't redistribute in
  the installer. User MUST either point at a local GraXpert
  install OR consent to upstream download (server fetch from
  official GraXpert release URLs). Polaris stays pure MPL 2.0.
- **GPL-3 of GraXpert code**: no GraXpert code comes into
  Polaris; only the `.onnx` model files (assets), which are under
  the separate CC BY-NC-SA 4.0 license.
- **ORT Web bundle**: ~5 MB (1 MB JS + 4 MB WASM backends). MIT
  license. No native deps on the server (the C# server does NOT
  need Microsoft.ML.OnnxRuntime).
- **IndexedDB cache size**: client may have ~1.5 GB in permanent
  cache. Browser eventually expires under disk pressure; Polaris
  re-downloads on demand.
- **Privacy**: zero image data leaves the client browser (all
  inference local); models come from the Polaris server OR
  upstream GraXpert release.
- **Expected performance** (BGE 256×256 single pass):
  - Laptop NVIDIA GPU + WebGPU: ~50 ms.
  - Laptop integrated GPU + WebGPU: ~300 ms.
  - Pi 4 browser + WASM SIMD: ~3-5 s.
  - iPhone 14 + WebGPU: ~200 ms.
  - Android Pixel 6 + WebGPU: ~300 ms.
  - Denoise full master 3000×2000 (60 tiles): ~5-15 s on GPU,
    ~30-90 s on WASM SIMD CPU.
- **Heap**: Denoise v3 loads ~500 MB of weights; iOS tight near
  this. Default v2 on mobile gives margin.

## Out of scope (deferred)

- **Server-side ONNX runtime (C# Microsoft.ML.OnnxRuntime)**:
  interesting feature (server does inference for weak clients)
  but duplicates code + adds native deps. Browser-only is the
  path. If demand appears, GX-9.
- **AutoGraXpert in browser** (auto-BGE during capture): today
  fire-and-forget via CLI in `SequenceEngine`. For browser would
  need hand-off via WS to a capable client + ack. Keep CLI for
  this specific case for now.
- **Full-bit-depth in the editor**: GX-5 v1 works in 8-bit
  display-stretched. ushort[] pre-stretch version is follow-up.
- **Advanced Decon parameter tuning** (PSF estimate from stars,
  etc): Polaris exports simple sliders; advanced tuning stays in
  GraXpert standalone.
- **Custom AI models** (user-trained): out of scope. v1 supports
  only the 5 official GraXpert models.

---

# Previous plan: Activity bar — client↔server network indicator (NET)

> Previous plan (ED, Editor Lightroom-style) preserved below.

## Context

The activity bar at the footer (lines 5251-5304 in `index.html`)
today shows CPU%, RAM%, host icon at right; activity chips on
the left. Missing: data **traffic** indication — when the
image-stream WS is gushing LZ4 frames at 5-20 MB/s, or when
`/api/editor/raw` is downloading 50-200 MB of the master, the
user has no visual feedback that the link is busy. When the
preview is slow to appear, they can't tell if it's the server,
the WiFi, or a frozen app.

**Objective**: small fixed footer strip showing at a glance:
**↓ rxKB/s** and **↑ txKB/s**, each with LED-style pulse on
activity, beside the CPU/RAM block.

**Decisions** (already implied, small feature):
- **Metrics**: client-side. Wrap `apiFetch` + `.onmessage` of
  the 3 WebSockets + 6 raw editor fetches to accumulate bytes.
- **Visibility**: always visible. CPU/RAM always present; chip
  row appears when there's an active op, empty when idle.
- **Update cadence**: 4 Hz (250 ms) — responsive without
  flooding the RAF queue.
- **Units**: auto-scale (B/s / KB/s / MB/s), 1 decimal.
- **Zero backend**: no new endpoint, no extra payload.

## Architecture

### Counter + rolling window (pure JS, no libs)

Single state object `net: { rxBuf, txBuf, rxRate, txRate,
rxPulse, txPulse, rxTotal, txTotal }`.

`rxBuf` / `txBuf` are circular arrays of tuples
`{ tMs, bytes }` covering the last ~3 s. Every 250 ms (timer),
purge entries older than 3 s, sum remaining `bytes`, divide by
window → current rate. Write to `rxRate` / `txRate`. Pulse: set
`rxPulse=true`, `setTimeout(120 ms)` to clear.

Functions:
- `_netRx(bytes)` — called by each arrival handler.
- `_netTx(bytes)` — called by each send wrapper.
- `_netStartMeter()` — 250 ms timer.
- `formatBytesPerSec(bps)` — autoscale B→KB→MB with 1 decimal.

### Instrumentation points

**RX (server → client)**:
1. `/ws/status` `.onmessage` (`app.js:1392`) — small JSON but
   continuous (1 Hz × ~2 KB). `this._netRx(event.data.length)`.
2. `/ws/image-stream` `.onmessage` (`app.js:1453`) — binary
   `arraybuffer`. `this._netRx(event.data.byteLength)`. Heaviest
   path: live stack in raw mode reaches 5-20 MB/s.
3. `/ws/terminal` `.onmessage` (`app.js:1278`) — SSH text.
   Negligible but instrumented for consistency.
4. **`apiFetch` wrapper** (`app.js:1088-1152`) — single point for
   all `apiPost`/`apiGet`. Use **Performance Resource Timing**:
   `performance.getEntriesByType('resource')` returns
   `PerformanceResourceTiming` with `transferSize`. Drain via
   `performance.clearResourceTimings()` after each tick.
   Captures HTTP RX + thumbnails + sky tiles automatically.
5. **6 raw editor fetches** — covered automatically via the
   Performance API.

**TX (client → server)**:
1. **WS sends** — wrap each `ws.send(...)`:
   - `/ws/status`: subscribe message on open.
   - `/ws/image-stream`: client-stack-progress, capability.
   - `/ws/terminal`: keystrokes.
   Helper `_wsSendTracked(ws, payload)` does
   `ws.send(payload)` + `this._netTx(byteLength(payload))`.
   Refactor ~10 call sites.
2. **`apiFetch` requests** — body length. For
   `JSON.stringify(body)` count string length pre-send. For
   `FormData` (multipart), sum `file.size`. Performance API does
   NOT give upload size, so TX needs manual instrumentation.
3. **Editor raw fetches** — count body explicitly at call sites
   since they bypass `apiFetch`.

### Performance Resource Timing for RX (design choice)

Instead of cloning every response (cost of duplicating big
buffers), use
`performance.getEntriesByType('resource')` which returns
`PerformanceResourceTiming` with `transferSize` (bytes-on-the-
wire, including headers; better proxy than body size).
Limitations:
- `transferSize` is 0 for CORS opaque without
  `Timing-Allow-Origin`. Polaris is same-origin → OK.
- Browser internal buffer limited (default ~150 entries). Drain
  with `performance.clearResourceTimings()` after each tick.
- Doesn't cover WebSocket data (WS frames aren't
  PerformanceResource) → WS counted manually via `.onmessage`.

### UI (markup)

Add before the CPU/RAM block in `.activity-bar-host`:

```html
<div class="activity-net" :title="netTooltip()">
    <span class="activity-net-row" :class="{ 'net-pulse': net.rxPulse }">
        <span class="activity-net-arrow">↓</span>
        <span x-text="formatBytesPerSec(net.rxRate)"></span>
    </span>
    <span class="activity-net-row" :class="{ 'net-pulse': net.txPulse }">
        <span class="activity-net-arrow">↑</span>
        <span x-text="formatBytesPerSec(net.txRate)"></span>
    </span>
</div>
```

Tooltip shows session cumulative totals ("12.4 MB ↓ · 230 KB ↑").

### CSS

`.activity-net` — narrow column (~70-90 px), font 11 px, spacing
similar to `.activity-host-stat`. `.activity-net-arrow` coloured
green (rx) / blue (tx). `@keyframes net-pulse` 120 ms (brightness
+ small scale).

## Phases

Single commit — small feature.

### NET-1: Counter + meter loop + instrumentation
- `net` state in `app.js`.
- Helpers `_netRx`, `_netTx`, `_netMeterTick`,
  `formatBytesPerSec`, `netTooltip`.
- 250 ms timer started in `init()` after WS connect.
- 3 `.onmessage` handlers instrumented.
- `_wsSendTracked` replacing `ws.send` on the 3 sockets.
- `apiFetch` (line 1088) instrumented for TX (body length) and
  invokes `_netMeterTick` at the end.
- 6 editor raw fetch sites instrumented for TX.
- `.activity-net` markup in footer.
- CSS `.activity-net*` + pulse keyframe.
- Build smoke + manual verify (DevTools network: live stack on
  → rx rises to 5-20 MB/s; open editor → spike of 50-200 MB on
  /raw).

## Files modified

- `wwwroot/js/app.js`
- `wwwroot/index.html`
- `wwwroot/css/app.css`

No files to create. No backend changes.

## Reused code

- 250 ms timer pattern like `_skyTicker` in app.js.
- `host` block in `.activity-bar-host` — clone layout.
- `formatRam` in app.js — reference for `formatBytesPerSec`
  (same aesthetic: number + compact unit).
- `apiFetch` (app.js:1088-1152) — single TX chokepoint, already
  has timeout/error handling, just adds 2 lines.
- 3 WebSocket sites + 6 editor fetch sites already mapped.

## Verification

1. **Build**: `dotnet build` — no-op (no C# changes).
2. **Smoke idle**: open browser, DevTools Network, leave still.
   Bar should show `↓ 0.0 B/s · ↑ 0.0 B/s` (or low KB/s from the
   1 Hz `/ws/status`).
3. **Live stack ON**: start sequence or camera stream in raw
   mode. Network panel confirms LZ4 frames arriving; bar shows
   `↓ 5-20 MB/s` with continuous ↓ pulse.
4. **Editor open**: open master FITS in editor → spike
   `↓ 50-200 MB` during /raw download → drops to near 0 after
   load. Brief ↓ pulse.
5. **Editor slider drag (server mode)**: each preview render
   generates fetch JSON (request) + JPEG (response). Small ↑
   spike + larger ↓ spike.
6. **Editor slider drag (WASM mode)**: zero traffic (all
   computation local). Rate stays at 0 → confirms indicator
   distinguishes server-mode (network) from WASM-mode (zero
   network).
7. **Tooltip**: hover over the net block shows cumulative
   totals.
8. **Session reset**: closing/reopening the browser zeroes
   totals. (Doesn't persist — by design.)

## Notes

- Performance Resource Timing buffer can fill if there's lots
  of short fetches (thumbnails).
  `performance.clearResourceTimings()` per tick keeps it at 0.
- Small WebSocket sends (subscribe, keystrokes) may round to
  0.0 B/s even when active. Acceptable — goal is to visualise
  significant traffic.
- Pulse animation is purely visual; perf impact negligible even
  on Pi 2.
- If we ever want **host-wide** throughput (total link of the
  Polaris machine, not just this session), extend
  HostMetricsService with /proc/net/dev (Linux) / Windows perf
  counters. Out of scope.

---

# Previous plan: Editor Lightroom-style at the end of the STUDIO workflow

> Previous plan (SWE, Stellarium-web-engine swap) preserved below.

## Context

Today STUDIO ends when the user gets an integrated master at
`{rig}/integrated/{target}/`. After that they leave Polaris and
open Lightroom (or similar) on a PNG/TIFF exported from
Siril/PixInsight to do the creative post-processing: final
stretching, curves, vibrance, saturation, sharpening, clarity,
dehaze, vignette, noise reduction, crop/resize, final
JPG/PNG export. This step blocks the workflow and keeps the user
tied to another app.

**Objective**: a Lightroom-mobile-style editor as a sub-tab
inside STUDIO (and an "Open in editor" button on the FILES tab).
Edits are **non-destructive** (Lightroom-style sidecar JSON
`.edit.json` beside the source file). Live preview via **hybrid
WASM + server fallback** mirroring the CLST live-stack pattern
(capability detection on handshake, auto-switch, UI override).
Accepts: masters from the library (FITS/XISF), arbitrary
PNG/JPG/TIFF via the FILES tab, and direct drag-and-drop /
upload through the tab itself.

**Decisions** (with the user):
- **Compute**: hybrid WASM (when available) + server (Skia + libs)
  as fallback. CLST pattern.
- **Sources**: 3 paths — Library masters, FILES tab "Open in
  editor", direct upload / drag-and-drop.
- **Edit model**: non-destructive. Sidecar JSON beside the file
  preserves adjustments. Final export applies + writes to
  `processed/{target}/edited/`.

## Architecture

### Layers

```
┌─ Polaris main app ───────────────────────────────────────────────┐
│  STUDIO panel — new "Editor" sub-tab                             │
│                                                                   │
│  ┌──── Editor view ───────────────────────────────────────────┐  │
│  │  • Source picker: Library card / FILES file / Upload       │  │
│  │  • OpenSeadragon preview (vendored in B3) with             │  │
│  │    before/after toggle by keypress or click                │  │
│  │  • RGB histogram (Chart.js)                                │  │
│  │  • Collapsible panels (Lightroom mobile replica):          │  │
│  │     - Light: Exposure/Contrast/Highlights/Shadows/         │  │
│  │       Whites/Blacks                                        │  │
│  │     - Colour: WB (temp/tint), Vibrance/Saturation/Hue      │  │
│  │     - Curve: spline RGB + per-channel R/G/B                │  │
│  │     - Detail: Sharpening (amount/radius/threshold),        │  │
│  │       Noise reduction (luminance)                          │  │
│  │     - Effects: Texture/Clarity/Dehaze/Vignette             │  │
│  │     - Geometry: Crop, Rotate, Resize                       │  │
│  │  • Buttons: Reset, Apply preset, Save edits (sidecar),     │  │
│  │    Export…                                                 │  │
│  │  • Export modal: format JPG/PNG/TIFF · quality 0-100 ·     │  │
│  │    resize (px or %) · output path                          │  │
│  └────────────────────────────────────────────────────────────┘  │
│                                                                   │
│  Dispatch (mirrors CLST):                                         │
│   slider input → debounce 100 ms →                                │
│     if wasmReady && editorWasmActive:                             │
│        Interop.ApplyEdit(json) → render to canvas                 │
│     else:                                                         │
│        POST /api/studio/editor/preview → <img src>                │
└───────────────────────────────────────────────────────────────────┘

┌─ NINA.Polaris.Wasm (extended — Interop.cs) ──────────────────────┐
│  LoadEditorFrame(pixels[], w, h, bitDepth, bayer) → sessionId     │
│  ApplyEdit(sessionId, paramsJson) → previewBytes (8-bit JPEG)     │
│  ComputeHistogram(sessionId) → int[256*3]                         │
│  ReleaseEditorFrame(sessionId)                                    │
│  All use EditOperations.cs (same DLL as server, AOT-trimmed)      │
└───────────────────────────────────────────────────────────────────┘

┌─ Server ─────────────────────────────────────────────────────────┐
│  Services/Editor/                                                 │
│    ImageEditService.cs       — LRU cache + apply edits + preview  │
│    EditSidecarStore.cs       — read/write {source}.edit.json      │
│    EditOperations.cs         — canonical-order pipeline (shared   │
│                                via NINA.Image.Portable so WASM    │
│                                and server use same math)          │
│  Endpoints/EditorEndpoints.cs                                     │
│    POST /api/editor/load       {source}        → {sessionId,…}    │
│    POST /api/editor/preview    {sessionId, edits} → JPEG bytes    │
│    POST /api/editor/histogram  {sessionId, edits} → int[]         │
│    POST /api/editor/export     {sessionId, edits, fmt, quality,   │
│                                  resize, outputPath} → {path}     │
│    GET  /api/editor/sidecar?path=…             → JSON             │
│    PUT  /api/editor/sidecar?path=…             → ok               │
│    POST /api/editor/upload     multipart       → temp path        │
└───────────────────────────────────────────────────────────────────┘
```

### Canonical pipeline (order matters — server and WASM apply identical)

```
1. Decode → float[] working buffer (mono or RGB linear-light)
2. White balance (temp/tint → RGB gain multipliers)
3. Exposure (× 2^stops)
4. Contrast (S-curve around 0.5)
5. Highlights / Shadows (anchored tone curve)
6. Whites / Blacks (point movement at extremes)
7. Tone curve (spline LUT 256 entries — RGB master + per-channel)
8. Vibrance / Saturation (HSL multiply; vibrance protects already-saturated pixels)
9. Hue shift (HSL rotation)
10. Clarity (large-radius USM + low amount on luminance)
11. Dehaze (global contrast boost + luminance-attenuated saturation)
12. Texture (small-radius USM + medium amount)
13. Sharpen (small-radius USM + high amount)
14. Noise reduction (3×3 median or bilateral on luminance)
15. Vignette (radial multiply)
16. Crop + Resize (bilinear or Lanczos when downsampling)
17. Encode (Skia: JPEG quality 0-100, PNG 8-bit or 16-bit, TIFF 16-bit)
```

Skip any step where the param is at default — passes the buffer
forward without copy.

### Sidecar JSON

`{source}.edit.json` beside the source. Versioned schema:

```jsonc
{
  "version": 1,
  "source": "M31_master.fits",
  "savedAt": "2026-05-24T22:30:00Z",
  "edits": {
    "whiteBalance": { "temp": 5500, "tint": 0 },
    "light": { "exposure": 0.2, "contrast": 0.1,
               "highlights": -0.3, "shadows": 0.4,
               "whites": 0.1, "blacks": -0.05 },
    "color":  { "vibrance": 0.2, "saturation": 0.0, "hue": 0 },
    "detail": { "sharpenAmount": 0.5, "sharpenRadius": 1.0,
                "sharpenThreshold": 0, "noiseReduce": 0.2 },
    "effects":{ "texture": 0.32, "clarity": 0.09,
                "dehaze": 0.21, "vignetteAmount": 0,
                "vignetteFeather": 50 },
    "toneCurve": {
      "rgb": [[0,0],[64,60],[128,140],[255,255]],
      "r": null, "g": null, "b": null
    },
    "crop": null,
    "rotate": 0
  }
}
```

Values 0/null = default (skip). Reopening rehydrates the sliders.

### Reuse (explicit, do NOT reinvent)

Everything below already exists — confirmed via exploration:

- **`AutoStretch.cs`** — `ApplyManual(buf, black, mid, white)` is
  the base for stretch / black / white points.
- **`UnsharpMask.cs`** — sharpening, texture, clarity.
- **`GaussianBlur.cs`** — kernel for USM.
- **`ImageResampler.cs`** — bilinear resize.
- **`JpegEncoder.cs`** — encode with quality.
- **`ImageStatistics.cs`** — per-channel histogram.
- **`BayerDebayer.cs`** — debayer for raw color sources.
- **`FitsThumbnailer.cs`** — Skia Gray8/RGB encode pipeline.
- **`FrameProcessingService.cs`** — LRU cache of 4 decoded frames
  + `RenderJpegAsync` / `RenderPngAsync` / `ExportAsync`.
- **`FrameOperationsService.cs`** — RemoveGradient polynomial,
  NoiseReduction, Sharpen async wrappers.
- **`NINA.Polaris.Wasm/Interop.cs`** — `[JSExport]` pattern +
  primitive types passed at the boundary.
- **`NINA.Polaris.Wasm.csproj`** — net10.0 / browser-wasm / AOT
  / SIMD already configured.
- **`wwwroot/js/wasm/main.js`** + `nina-wasm-ready` event —
  handshake already in production.
- **OpenSeadragon** (vendored in `wwwroot/js/lib/openseadragon/`).
- **Chart.js** for the histogram.
- **STUDIO viewer markup** (lines 4111-4260) to clone the
  scaffold.

### To build from scratch

In `NINA.Image.Portable/ImageAnalysis/`:
- **`ColorSpace.cs`** — RGB↔HSL, RGB↔Lab (CIE D65),
  temperature/tint → RGB gains.
- **`ToneCurve.cs`** — natural cubic spline interpolating control
  points, generates LUT 256.
- **`BilateralFilter.cs`** — optional (expensive, ~2-5× slower
  than gaussian); used by Clarity/Dehaze.
- **`Vignette.cs`** — radial falloff with feather.

In `NINA.Polaris.Portable/Editor/` (new namespace shared between
WASM and server):
- **`EditParams.cs`** — record with sub-records (WhiteBalance,
  Light, Color, Detail, Effects, ToneCurve, Crop). Immutable;
  rehydratable via System.Text.Json.
- **`EditPipeline.cs`** — `Apply(EditParams, float[] working) →
  float[]`. Applies the 16 steps in order, skipping defaults.

## Phases (7 commits)

### ED-1: ColorSpace + ToneCurve + EditParams + EditPipeline
- `NINA.Image.Portable/ImageAnalysis/ColorSpace.cs`:
  - `RgbToHsl(r,g,b)` / `HslToRgb(h,s,l)` (Rec.601).
  - `TempTintToGain(tempK, tint) → (rGain, gGain, bGain)`
    (Bradford).
- `NINA.Image.Portable/ImageAnalysis/ToneCurve.cs`: natural cubic
  spline, input `Point[]` (0-255 domain), produces `byte[256]`
  LUT.
- `NINA.Polaris.Portable/Editor/EditParams.cs` (records).
- `NINA.Polaris.Portable/Editor/EditPipeline.cs` —
  `Apply(buf, w, h, channels, EditParams)`.
- Tests: ColorSpace roundtrip RGB↔HSL preserves ±1 in 8-bit;
  identity ToneCurve produces linear LUT; default EditParams =
  no-op; saturation 0 = grayscale; exposure +1 = ×2 brightness.

### ED-2: ImageEditService server-side
- LRU cache of 4 decoded frames (same pattern as
  `FrameProcessingService.cs`).
- `LoadAsync(string path)` — opens FITS/XISF/PNG/JPG/TIFF,
  returns session `{sessionId, w, h, channels, bitDepth}`.
- `RenderPreviewAsync(sessionId, EditParams, maxDim, quality)` —
  applies pipeline, downsamples to maxDim (default 1600 px for
  snappy preview), encodes JPEG.
- `ComputeHistogramAsync(sessionId, EditParams)` — post-pipeline,
  256×3 RGB or 256×1 mono.
- `ExportAsync(sessionId, EditParams, format, quality, resize,
  outputPath)` — full-res pipeline + encode to disk; reuses
  `ImageWriterService.BuildSubDir` for
  `processed/{target}/edited/`.
- Registered as singleton in `Program.cs`.

### ED-3: EditSidecarStore + EditorEndpoints
- `EditSidecarStore.cs`:
  - `LoadAsync(sourcePath)` reads `{sourcePath}.edit.json`.
  - `SaveAsync(sourcePath, EditParams)` atomic write (temp +
    rename).
  - Versioning — if `version` mismatch, migrate or ignore.
- `EditorEndpoints.cs` with the 6 routes.
- `POST /api/editor/upload` — multipart, saves at
  `{AppData}/Polaris/uploads/{guid}/{filename}`, returns path;
  housekeeping cron cleans uploads > 24 h later.

### ED-4: UI scaffold (server-mode only)
- `wwwroot/index.html`: new "Editor" sub-tab inside STUDIO
  (alongside Library + Viewer).
- Lightroom mobile visual replica: collapsible `<details>`
  panels with sliders.
- Chart.js histogram at the top.
- OpenSeadragon mounted in the centre, source =
  `/api/editor/preview?sessionId=…&edits=…` (cache-buster by
  timestamp).
- Each slider input → debounce 100 ms → POST preview → swap
  `<img>` or OpenSeadragon source.
- "Reset" button zeroes all sliders.
- "Save edits" → PUT sidecar.
- Before/after: hold space or click the "Original" button to
  show source without edits.
- `app.js`: state `editor: {sessionId, edits: {...}, source,
  dirty, …}` + methods.

### ED-5: Export dialog + writes
- Modal export: format dropdown (JPEG/PNG 8-bit/PNG 16-bit/
  TIFF 16-bit), quality slider (visible only for JPEG), resize
  inputs (px or %), output filename preview.
- Default output:
  `{rig}/processed/{target}/edited/{stem}__edited_{timestamp}.{ext}`.
- Export button → POST `/api/editor/export` → toast with path +
  "Open in FILES" link.
- Indexes the generated file via
  `FrameLibraryService.RescanAsync()` (existing pattern used by
  GraXpert).

### ED-6: WASM extension + client dispatch
- Extend `src/NINA.Polaris.Wasm/Interop.cs`:
  - `LoadEditorFrame(int[] pixels, int w, int h, int bitDepth,
    int channels) → string sessionId`.
  - `ApplyEdit(string sessionId, string editsJson) → byte[]
    jpegBytes`.
  - `ComputeHistogram(string sessionId, string editsJson) →
    int[]`.
  - `ReleaseEditorFrame(string sessionId)`.
- `EditPipeline.cs` (ED-1) is already AOT-compiled via project
  reference — only needs to be **reached** by the new JSExports
  so the trimmer keeps it.
- Rebuild WASM bundle: `dotnet publish src/NINA.Polaris.Wasm
  -p:PublishAot=true -r browser-wasm -c Release -o
  src/NINA.Polaris/wwwroot/js/wasm`.
- In `app.js`: new `_editorWasmActive` flag (default true if
  `wasmReady`); UI toggle to force server.
- Dispatch on slider handler:
  `if (this.wasmReady && this._editorWasmActive) { call WASM,
  get bytes, blob URL → <img> } else { POST preview }`.
- Frame load: server decodes FITS (complicated), sends uint16
  pixels via WS to the client, client calls `LoadEditorFrame`
  once. Reuses existing raw LZ4 transport (`handleImageFrame`
  raw path).

### ED-7: Upload + FILES tab entrypoint + tests + docs
- Drag-and-drop zone in editor (HTML5 DnD API).
- "Upload from disk" button → `<input type=file>` (PNG/JPG/TIFF/
  FITS/XISF).
- POST multipart → `/api/editor/upload` → session.
- In FILES tab: new "Open in editor" button for selected file
  (creates session pointing directly at it, no copy).
- Tests:
  - `EditPipelineTests` end-to-end with synthetic frame (known
    gradient), verifies each step changes output as expected.
  - `EditSidecarStoreTests` — roundtrip + version migration.
  - `ImageEditServiceTests` — LRU eviction + cancel handling.
  - `EditorEndpointsTests` (integration via TestServer) —
    load → preview → histogram → export → sidecar PUT/GET.
- Docs: `docs/user-guide/editor.md` (workflow + shortcuts +
  differences vs Lightroom).
- README "Editor" section.
- Per-project `ARCHITECTURE.md` + Mermaid update.

### Follow-up: ED async slider freezes (separate commit)
- Sliders were synchronous causing freeze during drag on big
  masters. Refactor to async + drag-aware (skip preview while
  mid-drag, fire on release).

## Files created

- `src/NINA.Image.Portable/ImageAnalysis/ColorSpace.cs`
- `src/NINA.Image.Portable/ImageAnalysis/ToneCurve.cs`
- `src/NINA.Image.Portable/ImageAnalysis/BilateralFilter.cs` (if
  used for clarity)
- `src/NINA.Image.Portable/ImageAnalysis/Vignette.cs`
- `src/NINA.Polaris.Portable/Editor/EditParams.cs`
- `src/NINA.Polaris.Portable/Editor/EditPipeline.cs`
- `src/NINA.Polaris/Services/Editor/ImageEditService.cs`
- `src/NINA.Polaris/Services/Editor/EditSidecarStore.cs`
- `src/NINA.Polaris/Endpoints/EditorEndpoints.cs`
- `tests/NINA.Polaris.Test/Editor/` — 4 test files
- `docs/user-guide/editor.md`

## Files modified

- `src/NINA.Polaris.Wasm/Interop.cs` — 4 new `[JSExport]` editor
  methods.
- `src/NINA.Polaris/Program.cs` — register singletons + map
  endpoints.
- `src/NINA.Polaris/Services/ImageWriterService.cs` — extend
  `BuildSubDir` with case `"EDITED"` →
  `processed/{target}/edited/`.
- `src/NINA.Polaris/Services/Studio/FrameLibraryService.cs` —
  rescan after export (already a pattern from GraXpert).
- `src/NINA.Polaris/wwwroot/index.html` — Editor sub-tab in
  STUDIO + Editor modal/panel + Export modal.
- `src/NINA.Polaris/wwwroot/js/app.js` — state `editor`,
  methods.
- `src/NINA.Polaris/wwwroot/css/app.css` — `.editor-*` styles.
- `README.md` — Editor section.
- ARCHITECTURE.md updates.

## Verification

### Build + tests
- `dotnet build` clean.
- `dotnet publish src/NINA.Polaris.Wasm -p:PublishAot=true
  -r browser-wasm -c Release` rebuild artifacts in
  `wwwroot/js/wasm/`.
- `dotnet test` — ~510 atuais + ~25 novos = ~535 verdes.

### Server-mode (no WASM)
1. STUDIO → Library → select M31 master → "Open in editor".
2. Editor opens, OpenSeadragon shows the auto-stretched master.
3. Move Exposure slider +0.5 → preview updates in <500 ms
   (server LRU cache hit after first decode).
4. Apply S-curve → highlights/shadows respond.
5. Vibrance +30 → colors livelier, doesn't over-saturate
   already-saturated pixels.
6. Sharpening amount 0.5 / radius 1.0 → stars sharper.
7. Click "Save edits" → `M31_master.fits.edit.json` appears in
   the directory.
8. Refresh browser → reopens editor with the same master →
   sliders restored to saved values.
9. Click "Export" → modal → JPEG quality 90, resize 50% → file
   appears at `M31/edited/M31_master__edited_*.jpg`.
10. File shows in FILES tab (automatic rescan).

### WASM mode
11. Same flow; DevTools console shows
    `[Polaris] Editor using WASM (v0.3)`. Slider responds in
    <50 ms (no network roundtrip).
12. Toggle "Force server" in Settings → next slider input goes
    back to server.
13. Block `_framework/dotnet.native.wasm` in DevTools network →
    client falls back to server-mode silently.

### Upload + FILES
14. Drag-and-drop a PNG from Lightroom into the editor → upload
    → session created → preview renders.
15. FILES tab → right-click a TIFF → "Open in editor" → editor
    opens with that file.

### Sidecar lifecycle
16. Edit master → Save → close → reopen → sliders restored.
17. Edit + Export → generated file at
    `processed/{target}/edited/` carries its own sidecar
    (snapshot of the edits that generated it).
18. Editor on a FITS without write permission in the directory →
    fallback writes sidecar to
    `{AppData}/Polaris/sidecars/{md5-of-path}.edit.json`.

### Failure scenarios
- **WASM doesn't load** → silent server-mode.
- **Server without Skia** (build without deps) → 500 from preview
  endpoint, UI shows "Editor unavailable" banner.
- **Corrupted sidecar JSON** → ignore + log warning, open with
  defaults.
- **Huge master (8 GB)** → WASM load fails (heap limit),
  automatically falls back to server-mode (which can do tile-by-
  tile pipeline if needed — TODO in ED-6 v2).

## Out of scope (deferred)

- **Layers** (Lightroom doesn't have, but Photoshop does) —
  huge scope.
- **Local adjustments** (brush, gradient, radial — Lightroom has
  but requires complex mask UI). v2 separate if demand.
- **Shared presets / preset catalog** — user can still save
  sidecars manually and copy between projects.
- **Advanced crop with aspect ratio lock and freehand rotation**
  — v1 only rectangular axis-aligned crop + 90° rotation.
- **AI denoise (DxO PureRAW / Topaz style)** — out of scope; user
  can use GraXpert v3 denoise (already wired) before opening the
  editor.
- **RAW/CR2/NEF decode** — scope restricted to FITS/XISF/PNG/JPG/
  TIFF v1; CR2/NEF deferred (would need LibRaw bindings).

## License + compatibility notes

- **Skia/SkiaSharp**: Apache 2.0; already in project via
  FitsThumbnailer.
- **System.Text.Json**: BCL.
- **WASM AOT**: already in production via CLST.
- **Sidecar pattern**: Lightroom (.xmp) precedent — our format,
  no patent.
- **Performance**:
  - Server preview on 6000×4000 RGB master: ~400 ms on Pi 4
    (Skia + optimised USM).
  - WASM same master: ~80-150 ms on modern laptop, 300-500 ms
    on mobile.
  - FITS decode once per session (~1-2 s for 12000×8000 master),
    cached in RAM.
- **Memory**: each WASM session ~200 MB for a medium master.
  Limit of 2 simultaneous sessions on the client (UI guards
  against opening a 3rd without closing).
- **Sidecar disk footprint**: ~2 KB per file. Negligible.

---

# Previous plan: Swap d3-celestial → stellarium-web-engine (iframe sub-app)

> Previous plan (PA, Polar Alignment panel) preserved below.

## Context

The SKY tab originally used **d3-celestial** (~3.9 MB of
vendored assets: `celestial.min.js`, d3 v3, Hipparcos mag 6
catalog, DSOs, Milky Way contours, etc.) to render the sky map
with Aitoff projection + custom overlays (blue mount FOV
rectangle, red target rectangle, drag-to-frame, click-to-pick
coords, integration with Slew & Center). It worked but the user
wanted to swap for **stellarium-web-engine** to get:

- Real WebGL rendering (not SVG/d3), sky projected in every
  direction, atmosphere, more sophisticated visual ecliptic,
  Milky Way via HiPS tiles (not simple vector contours),
  Gaia stars to mag 16 (vs Hipparcos mag 6), DSOs from SDSS/DSS
  surveys with real images (not just names).
- Constellation art (stick figures + names in multiple cultures).
- Behaviour identical to Stellarium desktop — user already knows.

**Decisions** (confirmed):

1. **License**: stellarium-web-engine is **AGPLv3 + mandatory
   CLA**. Polaris is MPL 2.0. To keep the boundary clear without
   relicensing Polaris, isolate stellarium-web in a **separate
   sub-application served in an iframe** (`/sky/index.html`).
   UI crosses data via `postMessage`. Stellarium-web stays AGPL
   inside the iframe, Polaris stays MPL.
2. **HiPS data**: bundle locally — package the tile pyramids
   (stars / DSOs / surveys / landscapes) inside the Polaris
   publish to work offline (remote observatory). Cost ~500 MB-1 GB
   added to installer / Docker image, accepted.
3. **WebGL**: SKY tab becomes **desktop-browser-only**. RPi 2/3
   still serves the files but warns "WebGL not available" if
   anyone opens the local browser on the Pi. Realistic — no one
   opens the Sky map in a Pi browser.

## Architecture

### Layers

```
┌─ Polaris main app (MPL 2.0) ─────────────────────────────┐
│  index.html + js/app.js                                  │
│  SKY tab: <iframe src="/sky/" id="skyFrame">             │
│           postMessage RPC ↔                              │
└────────────────────────┬──────────────────────────────────┘
                         │ postMessage
┌────────────────────────▼─────────────────────────────────┐
│  Stellarium sub-app (AGPL boundary, served at /sky/)     │
│  wwwroot/sky/index.html                                  │
│    <script src="js/stellarium-web-engine.js">            │
│    <script src="js/sky-bridge.js">                       │
│  wwwroot/sky/js/wasm/stellarium-web-engine.{js,wasm}     │
│  wwwroot/sky/data/skydata/                               │
│    stars/  dso/  surveys/  landscapes/  skycultures/     │
│    mpcorb.dat  CometEls.txt  tle_satellite.jsonl.gz      │
└──────────────────────────────────────────────────────────┘
```

### postMessage RPC contract

Messages parent → iframe:
- `{type:'set-observer', lat, lng}` — observer location (deg).
- `{type:'set-time', utc}` — epoch ms.
- `{type:'look-at', raDeg, decDeg, fovDeg}` — point camera.
- `{type:'search', query}` — search object; reply via
  `search-result`.
- `{type:'set-fov-overlays', mount, target}` — geojson FOV
  rectangles (computed parent-side).
- `{type:'set-drag-mode', mode}` — `'free'` | `'fixed-target'`.

Messages iframe → parent:
- `{type:'ready', version, webgl}` — bridge initialised.
- `{type:'search-result', query, result}`.
- `{type:'center', raDeg, decDeg, fovDeg}` — current map centre.
- `{type:'map-click', raDeg, decDeg, objectName}`.
- `{type:'webgl-unavailable'}` — UI should show fallback.

`sky-bridge.js` in the sub-app:
1. Initialises `StelWebEngine({wasmFile, canvas, onReady})`.
2. Configures data sources via `core.stars.addDataSource(...)`
   pointing at `./data/skydata/...` (paths relative to iframe).
3. Listens to `window.addEventListener('message', ...)` and
   dispatches to `stel.*` methods (observer, getObj, createObj,
   etc.).
4. Implements "look-at-RA/Dec" converting RA/Dec → local alt/az
   via `stel.convertFrame` (engine has this) and setting
   `observer.yaw + observer.pitch`.
5. Renders FOV overlays via
   `stel.createObj('geojson', {...})` with pre-computed
   rectangles (parent already knows focal length + sensor +
   rotation).

### WASM build pipeline

stellarium-web-engine needs Emscripten to compile. We don't
require Emscripten installed by everyone building Polaris day-
to-day — solution:

1. **Git submodule** `external/stellarium-web-engine/` pointing
   at a dedicated fork (commit pinned for reproducibility).
2. **Script `scripts/build-stellarium-web.sh`** that runs
   Emscripten via Docker (`emscripten/emsdk:3.x.x`), calls
   `make js`, copies `build/stellarium-web-engine.{js,wasm}` to
   `src/NINA.Polaris/wwwroot/sky/js/wasm/`. Output committed in
   the repo — whoever builds Polaris on Windows/Linux doesn't
   need Emscripten to run `dotnet build`.
3. Manual refresh when upstream stellarium-web-engine updates
   (rare — engine is stable); script-driven, not automatic.

### HiPS tile bundle

stellarium-web's `apps/test-skydata/` on GitHub only has
skeleton (empty properties files). The real dataset lives at
`https://data.stellarium.org/`. Plan:

1. **Script `scripts/fetch-stellarium-skydata.sh`** downloads
   the full test-skydata (~500 MB-1 GB) via wget/curl recursive
   pointing at the official mirror. Runs once on the release
   machine.
2. `src/NINA.Polaris/wwwroot/sky/data/skydata/` directory stays
   gitignored (so the git repo doesn't explode), but the CI/CD
   script adds it to publish output.
3. **Stripped-down config**: use `max_vmag = 12` (not 16) by
   default to cut unnecessary tiles. Configurable via
   `properties` files. Reduces bundle to ~300 MB tolerable.
4. Polaris Docker image (`scripts/build-docker.sh`) includes
   skydata via COPY in Dockerfile.

## Phases (6 commits)

### SWE-1: iframe scaffold + WebGL detection + fallback
- Creates `wwwroot/sky/index.html` minimal: `<canvas>` + script
  loader + WebGL detection. No stellarium-web yet — just the
  empty iframe showing "Loading sky engine…" or "WebGL not
  available".
- `wwwroot/sky/js/sky-bridge.js` skeleton: listens to `message`,
  replies `{type:'ready', webgl:true|false}`.
- Replaces `<div id="celestial-map">` in SKY tab of
  `index.html` with `<iframe id="skyFrame" src="/sky/"
  sandbox="allow-scripts allow-same-origin">`.
- `app.js`: new helper `_skySendMessage(msg)` + listener for
  iframe messages. d3-celestial STILL loaded this commit (in
  parallel) so nothing breaks.
- Build clean, iframe loads placeholder + logs "[Sky] bridge
  ready webgl=true".

### SWE-2: Submodule + build script + WASM commit
- Git submodule `external/stellarium-web-engine/` pinned to a
  known commit.
- `scripts/build-stellarium-web.sh`: Docker-based Emscripten
  build, output at
  `src/NINA.Polaris/wwwroot/sky/js/wasm/{stellarium-web-engine.js,
  stellarium-web-engine.wasm}`.
- Run script manually once, commit the 2 outputs (gitignored
  sources, committed builds — pattern for expensive generated
  binaries).
- `wwwroot/sky/index.html` now loads
  `stellarium-web-engine.js` + calls `StelWebEngine({...})`.
  No data yet — engine initialises showing empty sky.
- README + `docs/architecture-sky.md`: explain how to re-build
  the WASM, link to AGPL license at `wwwroot/sky/LICENSE-AGPL`.

### SWE-3: HiPS skydata bundle + offline-first config
- `scripts/fetch-stellarium-skydata.sh`: downloads test-skydata
  via wget to `src/NINA.Polaris/wwwroot/sky/data/skydata/`.
- `.gitignore`: ignores `sky/data/skydata/` (doesn't go to git,
  but goes to publish output via csproj `<Content Include>`).
- `sky-bridge.js`: calls
  `core.stars.addDataSource({url:"./data/skydata/stars"})` + DSOs
  + surveys + landscapes + skycultures + planets surveys
  (sso/moon, sso/sun).
- Sky tab renders complete sky in the iframe. d3-celestial still
  running in parallel in the same tab (kept during migration).
- `csproj` `<Content Include="wwwroot\sky\data\skydata\**\*">`
  for recursive publish.

### SWE-4: postMessage RPC — observer + look-at + search
- `sky-bridge.js` implements handlers for `set-observer`,
  `set-time`, `look-at`, `search`, `get-center`.
- `look-at` converts RA/Dec → alt/az via
  `stel.convertFrame('CIRS', 'OBSERVED', [ra, dec, 0])`,
  sets `observer.yaw + observer.pitch`.
- `search` calls `stel.getObj(query)` + returns extracted
  RA/Dec.
- Canvas click → emits `map-click` with coords (uses
  `stel.c2s(screenCoords)` or equivalent).
- `app.js`: replaces calls to `Celestial.rotate(...)`,
  `Celestial.date(...)`, `Celestial.mapProjection(...)` with
  postMessage versions. Search box / click-to-pick / Slew &
  Center / 30 s ticker start talking to the iframe.
- d3-celestial still in the DOM but hidden via CSS for A/B
  during review.

### SWE-5: FOV overlays + drag-to-frame
- `sky-bridge.js` implements `set-fov-overlays`:
  - Mount FOV (blue): creates geojson polygon rectangle in
    equatorial coords via
    `stel.createObj('geojson', {data: rect})`.
  - Target FOV (red dashed): creates geojson polygon centred on
    current map centre.
  - Recreates on each update (engine without in-place mutation;
    remove old + create new, or use `setData`).
- **Drag-to-frame ASIAIR-style**: capture iframe drag, translate
  into yaw/pitch delta, update via
  `set-drag-mode: fixed-target` (iframe knows it's in fixed-
  target mode → after each drag emits `center` with new RA/Dec
  of map centre). Parent receives + posts `set-fov-overlays`
  with new target.
- Click without drag: emits `map-click` with RA/Dec → parent
  sets `skyTarget` + calls `slewAndCenter()`.
- Mosaic grid overlay (from mosaic planner): same geojson
  pattern.

### SWE-6: Decommission d3-celestial + docs + verify
- Remove `<script src="...celestial...">` +
  `<link ...celestial.css>` from `index.html`.
- Delete `wwwroot/js/lib/celestial/` entirely (~3.9 MB of saved
  bandwidth for clients).
- Clean up dead d3-celestial code in `app.js` (`_buildCelestial`,
  `_initSky`'s old config block, etc.). Keep only the bridge
  methods (`_skySendMessage`, listener, helpers).
- Update `README.md` + `docs/user-guide/sky-explorer.md`
  mentioning the stellarium-web engine + requirements (WebGL,
  ~300 MB+ dataset).
- Update per-project ARCHITECTURE.md.
- End-to-end verification (next section).

## Files created

- `external/stellarium-web-engine/` (git submodule, pinned)
- `scripts/build-stellarium-web.sh` (Docker-based Emscripten
  compile)
- `scripts/fetch-stellarium-skydata.sh` (HiPS tiles wget)
- `src/NINA.Polaris/wwwroot/sky/index.html` (sub-app shell)
- `src/NINA.Polaris/wwwroot/sky/js/sky-bridge.js` (postMessage
  RPC)
- `src/NINA.Polaris/wwwroot/sky/js/wasm/stellarium-web-engine.{js,wasm}`
  (committed build outputs)
- `src/NINA.Polaris/wwwroot/sky/data/skydata/...` (gitignored,
  in publish output via csproj Content Include)
- `src/NINA.Polaris/wwwroot/sky/LICENSE-AGPL.txt` (copy of
  AGPLv3 stellarium-web-engine license — required by AGPL)
- `docs/architecture-sky.md` (iframe + AGPL boundary +
  postMessage contract + how to rebuild WASM)

## Files modified

- `src/NINA.Polaris/NINA.Polaris.csproj` — `<Content Include>`
  for `wwwroot/sky/**/*` (recursive, including gitignored
  skydata).
- `src/NINA.Polaris/wwwroot/index.html` — replace
  `<div id="celestial-map">` with `<iframe id="skyFrame"
  src="/sky/">`, remove `<script>` + `<link>` of celestial in
  SWE-6.
- `src/NINA.Polaris/wwwroot/js/app.js` — replace ~20+
  Celestial.* calls with postMessage pattern; add
  `_skySendMessage`, message event listener, FOV geojson
  computation helpers. In SWE-6 delete dead d3-celestial code
  (~200 lines).
- `.gitignore` — adds
  `src/NINA.Polaris/wwwroot/sky/data/skydata/`.
- `Dockerfile` — adds
  `COPY src/NINA.Polaris/wwwroot/sky/data/`.
- `README.md` — "Sky tab requires WebGL + ~300 MB skydata
  bundle" section.
- `docs/user-guide/sky-explorer.md` — overhaul describing the
  new features (stick figures, surveys, atmosphere).
- per-project ARCHITECTURE.md.

## Files deleted (SWE-6)

- `src/NINA.Polaris/wwwroot/js/lib/celestial/` (entire folder,
  ~3.9 MB).
- References to celestial.css / celestial.min.js in index.html.

## Reused code

- **Sub-app pattern** — did similar with xpra/PHD2 GUI (PH2X-6:
  `Phd2GuiSessionService` + `<iframe>` in the GUIDE panel).
  Same strategy: optional backend service + iframe in the UI
  with same-origin so postMessage works without CORS.
- **`Services/PHD2ProcessManager.cs`** — Linux/Docker subprocess
  pattern for inspiration on `build-stellarium-web.sh`.
- **`csproj` Content Include patterns** — already have for
  `wwwroot/js/lib/celestial/data/*.json` (provisional). Replicate
  with recursive for the new skydata.
- **WebGL detection** — already done in `app.js _initWebGL()`
  (lines 1143-...). Reuse the pattern (canvas.getContext('webgl2')
  → fallback message).
- **`_skyMapCenter()`** (`app.js` ~4194) — existing logic of
  RA/Dec extraction from map centre: now becomes "parent asks
  via postMessage `get-center` + receives `center` response
  async". Same semantics, different transport.
- **FOV ring computation** (`_buildFovRing` in `app.js` ~4030) —
  math of equatorial rectangle from sensor + focal + rotation
  remains **identical** on parent. Only the destination changes:
  instead of feeding a d3-celestial layer, post as GeoJSON to
  iframe.
- **Slew & Center workflow** (`SlewCenterService.cs`) —
  completely agnostic to renderer; only consumes final RA/Dec.
  Zero change.
- **stellarium-web `apps/simple-html/stellarium-web-engine.html`
  + `tests.js`** — canonical reference for the bridge JS shape.

## Verification

### Preconditions
- Modern desktop browser (Chrome/Firefox/Safari current) with
  WebGL2.
- ~300 MB free in client cache (skydata comes from first
  session).
- Polaris server linux-x64/win-x64 with publish output including
  `wwwroot/sky/data/`.

### Smoke (SWE-1..3)
1. `dotnet build src/NINA.Polaris/NINA.Polaris.csproj` — clean.
2. SKY tab in the browser → iframe loads → DevTools shows
   `[Sky] bridge ready webgl=true version=x.y.z`. Sky still
   without data in SWE-2; SWE-3 already shows stars to mag 12 +
   complete DSOs (M31, Orion Nebula, Pleiades) + Milky Way
   HiPS tiles.

### postMessage RPC (SWE-4)
3. Search box "M31" → iframe replies `search-result` → parent
   centres map + shows FOV overlay. Works via postMessage, not
   Celestial.rotate.
4. Click directly on the map on a known star → iframe emits
   `map-click` with coords + name → parent sets skyTarget +
   slewAndCenter starts.

### FOV overlays + drag (SWE-5)
5. Mount connects + tracking on + slew to Polaris → blue
   rectangle appears centred on RA mount/Dec mount, rotated by
   the angle of the most recent plate-solve.
6. Drag the map (fixed-target ASIAIR mode) → red target
   rectangle stays fixed on screen centre, background slides →
   parent receives new coords + updates skyTarget.
7. Mosaic planner opens the panel → yellow grid appears over
   sky, click a cell → highlight, click Confirm → sequence
   created with those RA/Dec.

### Decommission (SWE-6)
8. After deleting d3-celestial: client bundle drops ~3.9 MB.
   SKY tab still functional. README updated.

### Failure scenarios
- **WebGL absent** (DevTools toggle "disable WebGL") → iframe
  posts `webgl-unavailable` → parent UI shows "Sky tab requires
  WebGL — open Polaris from a desktop browser." Other tabs
  work.
- **Skydata folder absent** (Polaris running without the dataset
  bundled — CI build case without running fetch script) → tile
  404s in console, sky renders empty but engine doesn't crash.
  Log message "Sky data missing — run
  scripts/fetch-stellarium-skydata.sh".
- **Iframe doesn't load** (CSP, sandbox) → parent timeout 5 s →
  fallback banner "Sky engine failed to load."

## License + security notes

- **AGPLv3 boundary**: files under `wwwroot/sky/` (including the
  WASM + bridge JS when it calls engine APIs) effectively fall
  under AGPL by the "derivative work" principle. Handling:
  - `wwwroot/sky/LICENSE-AGPL.txt` exact copy of upstream.
  - Manual header on each file in `wwwroot/sky/` references AGPL.
  - `wwwroot/sky/README.md` explains the license boundary.
  - Root `README.md` of Polaris adds an "Embedded AGPL
    component: Sky map sub-application" section linking to
    source.
  - **Serving the source**: AGPL requires remote users to be
    able to obtain the source. Polaris is already open-source on
    GitHub, so linking the repo tag (including the pinned
    submodule commit) satisfies. Add explicit link in
    `/sky/index.html` footer: "Source code:
    https://github.com/DanWBR/NINA.Polaris/tree/...".
- **Upstream CLA**: not going to contribute patches upstream in
  this plan. If we want to eventually, sign the CLA then; until
  then keep the fork in the submodule without touching upstream.
- **Iframe sandbox**: `sandbox="allow-scripts
  allow-same-origin"`. Same-origin needed for postMessage to
  return local fetch results (sky data) without CORS. No
  `allow-popups` / `allow-forms`.
- **HiPS dataset origin**: stellarium-web's test-skydata is
  AGPL in the context of the app; bundled tiles are public
  domain / CC0 from upstream sources (Hipparcos, Gaia, etc.).
  List attributions at `wwwroot/sky/data/skydata/ATTRIBUTION.md`.
- **WebGL and mobile browsers**: test on iOS Safari + Chrome
  Android. Expected to work (both support WebGL2). Performance
  variable on low-end devices.

## Known risks / out of scope

- **Build pipeline**: requires Docker to run
  `build-stellarium-web.sh`. Covers Linux + macOS. Windows
  requires Docker Desktop. Documented.
- **Submodule update friction**: each upstream
  stellarium-web bump requires re-running build + committing new
  `.js/.wasm`. Accepted — upstream stable, low frequency.
- **API divergence**: canonical reference is
  `apps/simple-html/tests.js` upstream. Some APIs (look-at-by-
  RADec, hit-testing) aren't formally documented; each phase
  may discover gotchas + require patches to `sky-bridge.js`.
  Mitigation: start with SWE-1..3 conservative, evolve the RPC
  contract as we implement.
- **Slew preview inset** (lower-right canvas of the Sky tab from
  VIDPL-10) keeps using the existing local WebGL canvas — not
  involved in this plan. iframe occupies only the main map
  area.
- **Tonight tab + Sky catalog filters** (D10) keep using the
  `SkyCatalogService` backend + separate UI; only the visual
  rendering on the SKY tab is swapped.
- **Stellarium remote control sync** (D11) still holds — sync
  pushes RA/Dec to Stellarium desktop (other process), no link
  to the embedded engine.

---

# Previous plan: Polar Alignment panel (TPPA + Refine)

> Previous plan (SIM, built-in equipment simulator) preserved below.

## Context

Polaris today solves most pre-imaging (PHD2 calibration, auto-
focus, slew & center, meridian flip, sequence, live stacking)
but still has no polar alignment — the first thing the user does
when setting up at night. Without it, they have to open SharpCap
on Windows or run TPPA from NINA desktop before coming to
Polaris. ASIAIR, KStars/Ekos, and NINA desktop all have this
panel; missing on Polaris.

**Objective**: POLAR panel on the sidebar doing TPPA (Three-
Point Polar Alignment) end-to-end in the browser — captures 3
frames at RA positions ~30° apart, plate-solves each, computes
the error vector (azimuth + altitude in arcsec/arcmin), and
draws an arrow over the live frame indicating the direction to
nudge the tripod screws. After TPPA, a "Refine" button enters a
capture/solve loop every ~3 s so the user can watch the error
shrink in real time while adjusting (SharpCap-style UX).

**Decisions** (with the user):
- **TPPA + Refine in the same feature** (don't split). Refine is
  where the UX shines — without it the user would have to run
  full TPPA every screw turn.
- **Both hemispheres in v1.** South serves the user (Brazil)
  directly. Cost: ~5 lines + 1 test (polar-axis sign flips with
  negative latitude).
- **Auto-slew only.** Every modern mount has GoTo. Manual-slew
  (user spins RA by hand) is a follow-up if demand appears —
  would add a big branch to the state machine.

## Architecture

Pattern identical to `PHD2CalibrationOrchestrator` +
`AutoFocusService`: a singleton with
`StartJob/Abort/GetJob/StartRefinement/StopRefinement`, state
machine per phase, current job broadcast via WS at 1 Hz.

### TPPA algorithm (math)

Each plate-solve returns the true RA/Dec of the optical axis at
instant `t`. If the mount was perfectly aligned, rotating in
hour angle (HA) would sweep the optical axis along a circle of
constant Dec. A misaligned mount → the sweep is still a small
circle on the celestial sphere, but the pole of that circle is
the **mount's rotation axis**, not the celestial pole. The
difference between the two poles is the polar alignment error.

Pseudo-code:

```
for each point i ∈ {0, 1, 2}:
    v_i = AltAzUnitVector(ra_i, dec_i, lst_i, siteLat)

# Normal to the plane of the 3 vectors = mount axis
n = normalize((v1 - v0) × (v2 - v0))

# Convert to Alt/Az
(altAxis, azAxis) = VectorToAltAz(n)

# North: ideal pole at (Alt=lat, Az=0°)
# South: ideal pole at (Alt=-lat, Az=180°)
if siteLat >= 0:
    altErr = altAxis - siteLat
    azErr  = azAxis - 0
else:
    altErr = altAxis - (-siteLat)
    azErr  = azAxis - 180

return (azErr * 3600, altErr * 3600)  # arcsec
```

Reference: Challis (1879) "Lectures on Practical Astronomy" +
modern formulation in Hook & Wallace (SOFA) for spherical-
coordinate axis adjustment. Same math NINA desktop, KStars and
SharpCap use internally.

### Explicit reuse

Everything below already mapped via exploration — no
reinvention:

- `Services/PHD2CalibrationOrchestrator.cs` — copy/paste the
  shape (Job + CTS + ConcurrentDictionary + StartJob/Abort/
  GetJob + RunAsync phase-loop + Fail helper).
- `Services/PlateSolveService.cs` →
  `SolveAsync(string fitsPath, PlateSolveOptions, ct)` returns
  `{Success, RaHours, DecDeg, ScaleArcsecPerPixel, RotationDeg,
  Error}`. **Takes a file path**, not IImageData — write each
  capture to `Path.GetTempPath()` before the solve, try-finally
  `File.Delete`.
- `Services/ImageWriterService.cs` →
  `SaveImage(IImageData, ...)` to write the temp FITS.
- `Services/EquipmentManager.cs` →
  `Telescope.SlewAsync(ra, dec, ct)`,
  `Telescope.RightAscension / Declination / Altitude / Azimuth /
  SideOfPier`, `Camera.CaptureAsync(seconds, opts, ct)`.
- `Services/ProfileService.cs` → `ActiveEquipmentProfile` getter
  + setter pattern for the PolarAlign* fields.
- `Services/NotificationService.cs:Push(kind, text)` — toasts on
  each phase transition (reused from auto-connect).
- `WebSocket/StatusStreamHandler.cs` — pattern of adding a
  sub-object to the status payload.
- `wwwroot/js/app.js redrawOverlay() + _drawCrosshairOnOverlay`
  pattern — clone to `_drawPolarErrorVector`.
- `wwwroot/index.html` AutoFocus tab (682-770) — layout template
  for the new POLAR tab.

## Phases (5 commits)

### PA-1: Profile fields + skeleton
- `EquipmentProfile` gains 4 fields:
  `PolarAlignSlewDegrees=30`,
  `PolarAlignExposureSec=3.0`,
  `PolarAlignSettleSeconds=2`,
  `PolarAlignGain=100`.
- `EquipmentEndpoints` PUT/POST handlers map the 4 new fields.
- `Services/PolarAlignmentService.cs` created: enum
  `PolarAlignmentPhase { Idle, Preflight, MovingToPoint1,
  SolvingPoint1, ..., Computing, Ok, Failed, Cancelled,
  Refining }`, record `PolarPoint(int Index, double Ra,
  double Dec, double RotationDeg, DateTime At)`, class
  `PolarAlignmentJob` with `Id, Phase, Points, AzErrorArcsec,
  AltErrorArcsec, TotalErrorArcsec, LastError, Mode, Cts, Task`.
  Constructor injects `EquipmentManager, PlateSolveService,
  ImageWriterService, ProfileService, ILogger`. Stubs
  `StartJob/Abort/GetJob/StartRefinement/StopRefinement` throw
  `NotImplemented`. Registered as singleton in `Program.cs`.
- `Endpoints/PolarAlignmentEndpoints.cs`: 5 routes
  (`POST /api/polar/start`, `/abort`, `/refine/start`,
  `/refine/stop`, `GET /api/polar/status`). Wired in
  `Program.cs`.
- **Smoke**: `GET /api/polar/status` returns
  `{phase:'idle'}`. Build clean.

### PA-2: TPPA capture/slew loop (no math yet)
Implements `RunAsync` end-to-end except the error calculation.

1. **Preflight**: validates `Telescope?.IsConnected`,
   `Tracking=true`, `Camera?.IsConnected`. Saves the original
   `(ra0, dec0)`.
2. **Loop i ∈ {0, 1, 2}**:
   - `SetPhase(MovingToPointN)`.
   - `targetRa = ra0 + i * slewStepDegrees / 15.0` (hours).
   - `await Telescope.SlewAsync(targetRa, dec0, ct)`.
   - `await Task.Delay(settleSeconds * 1000, ct)`.
   - `SetPhase(SolvingPointN)`.
   - `var image = await Camera.CaptureAsync(exposureSec,
     new CaptureOptions { Gain = profile.PolarAlignGain }, ct)`.
   - `string tempPath = ImageWriterService.SaveImage(image, ...)`
     in temp dir.
   - `var result = await PlateSolveService.SolveAsync(tempPath,
     new PlateSolveOptions { RaHoursHint = currentRa,
     DecDegHint = currentDec, ScaleHintArcsecPerPixel =
     profile.PixelScale }, ct)`.
   - Append `PolarPoint(i, result.RaHours, result.DecDeg,
     result.RotationDeg, DateTime.UtcNow)` to `job.Points`.
   - `try { File.Delete(tempPath); } catch { }` (housekeeping).
3. `SetPhase(Computing)` → (PA-3 fills here) → `SetPhase(Ok)`.
4. **Slew back** to `(ra0, dec0)` (courtesy).
5. **Cancel handling**: `OperationCanceledException` →
   `SetPhase(Cancelled)`. Other exceptions → `Fail(job,
   ex.Message)` mirroring PHD2.

Adds `event Action<PolarAlignmentJob>? JobUpdated;` fired on
each `SetPhase` and new point. `StatusStreamHandler` hooks the
event for event-driven push (not just 1 Hz polling).

Build clean. Manual test: `POST /api/polar/start` with sim, see
phase spin via `GET /api/polar/status`. Result block still shows
0/0/0 arcsec.

### PA-3: Math + tests
`Services/PolarAlignmentMath.cs` new:

```csharp
public static (double azErrSec, double altErrSec) ComputeError(
    PolarPoint p1, PolarPoint p2, PolarPoint p3,
    double siteLatDeg, double siteLongDeg);
```

Hemisphere-aware (positive lat = north, negative = south). Uses
LST of each point (computes local sidereal time from
DateTime UTC + longitude).

`tests/NINA.Polaris.Test/PolarAlignmentServiceTests.cs`:
- **Test 1 — perfect mount north**: synthesises 3 points of
  perfect sweep at lat=+45°, expects residual error < 10".
- **Test 2 — known error north**: simulates mount with
  Az=+120", Alt=-300", recovers within ±5".
- **Test 3 — perfect mount south**: lat=-23.5° (Brazil),
  expects <10".
- **Test 4 — known error south**: mount with known error in
  southern hemisphere, recovers within ±5".
- **Test 5 — cancel**: stub `EquipmentManager` with blocking
  `SlewAsync`, `Abort()` → phase = `Cancelled` in <500 ms.
- **Test 6 — plate-solve fail**: mock `PlateSolveService`
  returns `Success=false`, phase goes to `Failed` with
  descriptive error.

Plumb `PolarAlignmentMath.ComputeError` into
`RunAsync.Computing`. Build clean + 6 tests pass.

### PA-4: WebSocket payload + frontend (static UI, no overlay)
- `StatusStreamHandler.cs`: new `polarAlignmentPayload` block
  mirroring `autoFocusPayload`, included in the merged status
  object. `JobUpdated` hook forces immediate push.
- `wwwroot/index.html`:
  - **Sidebar**: new POLAR button between GUIDE and FOCUS. SVG
    icon: compass/target (concentric circles + crosshair).
  - **New tab panel** `<div x-show="tab === 'polar'">` cloned
    from AutoFocus: header with phase pill + spinner, parameter
    form bound to rig fields (slew deg / exposure / settle /
    gain, POST on blur to `/api/equipment/rigs/{id}`), `Start`
    button → `polarStart()`, progress bar "Point N/3 — phase",
    result block `Az: {{azArcmin}}' Alt: {{altArcmin}}' Total:
    {{totalArcmin}}'`, `Refine` button, `Stop`/`Abort` buttons.
  - **Manage Rigs modal**: new "Polar alignment" collapsible
    section (next to "Filter offsets") with the 4 fields.
- `wwwroot/js/app.js`:
  - Alpine data: `polarAlignment: {state:'idle', phase:'idle',
    points:[], azErrorArcsec:0, altErrorArcsec:0,
    totalErrorArcsec:0}`.
  - WS handler: `if (msg.polarAlignment) this.polarAlignment =
    msg.polarAlignment;`.
  - 4 methods `polarStart/polarAbort/polarRefineStart/
    polarRefineStop` POST-ing to the endpoints with form values.

Build + manual: POLAR tab shows phases changing + final result
(numeric). Still no arrow on canvas.

### PA-5: Live error vector overlay + refinement loop
- `Services/PolarAlignmentService.cs`: implements
  `StartRefinement()` — separate Task + CTS, loop
  `while (!ct.IsCancellationRequested)` capturing+solving at
  the current position (no slew), recomputes error using the
  3 initial points + current sample (sliding window — replaces
  the oldest point), updates `Job.AzErrorArcsec/AltErrorArcsec`
  each iteration, sleeps `settleSeconds` between. `StopRefinement`
  cancels. `finally` guarantees `Phase = Idle`.
- `wwwroot/js/app.js`: new branch
  `_drawPolarErrorVector(ctx, w, h)` in `redrawOverlay()`:
  - Reads
    `this.polarAlignment.{azErrorArcsec, altErrorArcsec}` +
    last solve's `rotationDeg`.
  - Converts `(azErr, altErr)` → screen vector: rotates by
    `-rotationDeg` so the arrow points in the visual direction
    of the frame.
  - Draws arrow from canvas centre, length
    `min(w,h)/2 * log(1 + totalArcmin)/log(1+30)`
    (logarithmic: 30' fills the canvas, 1' is small).
  - **Colour by magnitude**: red >5', amber 1-5', green <1'.
  - Label "Az: X' / Alt: Y' / Total: Z'" at canvas top.
- **TODO** comment for meridian-aware point picker (deferred —
  see edge cases).

Build + verify end-to-end.

## Files created

- `src/NINA.Polaris/Services/PolarAlignmentService.cs`
- `src/NINA.Polaris/Services/PolarAlignmentMath.cs`
- `src/NINA.Polaris/Endpoints/PolarAlignmentEndpoints.cs`
- `tests/NINA.Polaris.Test/PolarAlignmentServiceTests.cs`

## Files modified

- `src/NINA.Polaris/Services/ProfileService.cs` — 4 fields on
  `EquipmentProfile`.
- `src/NINA.Polaris/Endpoints/EquipmentEndpoints.cs` — maps the
  4 new fields on PUT/POST.
- `src/NINA.Polaris/Program.cs` — DI singleton +
  `MapPolarAlignmentEndpoints()`.
- `src/NINA.Polaris/WebSocket/StatusStreamHandler.cs` —
  `polarAlignmentPayload` block + `JobUpdated` push hook.
- `src/NINA.Polaris/wwwroot/index.html` — sidebar button, POLAR
  tab panel, rig modal section.
- `src/NINA.Polaris/wwwroot/js/app.js` — Alpine state, WS
  handler, 4 methods, `_drawPolarErrorVector` branch in
  `redrawOverlay`.
- `src/NINA.Polaris/wwwroot/css/app.css` — small additions for
  `.polar-result`, `.polar-error-pill`, etc.
- `README.md` — "Polar Alignment" section.

## Verification end-to-end ("Tonight's first alignment")

**Sim setup (no real hardware)**:

1. Enable INDI simulator stack via SETTINGS → Simulator tab.
2. Connect Telescope Simulator + CCD Simulator via RIGS tab.
3. Turn tracking on. Slew to a point near the pole (Dec ~70°+
   north, or ~-70° south).
4. **POLAR tab appears** between GUIDE and FOCUS. Click.
5. Configure parameters (slew=30°, expose=3 s, settle=2 s,
   gain=100).
6. Click **Start**.
7. Watch phase pill cycling Preflight → MovingToPoint1 →
   SolvingPoint1 → MovingToPoint2 → ... → Computing → Ok.
   Toast on each transition (reuses `NotificationService`).
8. Result block shows non-zero Az/Alt arcmin (sim has ~1° offset
   on purpose).
9. Mount slews back to original position automatically.
10. Click **Refine**.
11. Captures/solves every ~3 s, **arrow appears on canvas**
    pointing direction of error, label "Az: X' / Alt: Y' /
    Total: Z'".
12. Call `/api/telescope/sync` with slightly shifted RA/Dec
    (mimics screw adjustment) → arrow shrinks + colour changes
    red → amber → green in real-time.
13. Click **Stop** → phase returns to Idle, refinement task
    ends. Click **Abort** mid-slew in a fresh run → phase
    `Cancelled` in <1 s, mount stops slewing.

**Real-rig verification**:
- Total error after a Refine session ≤ 1' on CGEM-class mount.
- Eye-test against PHD2 drift assistant (RMS guiding pre/post-
  align).

**Southern hemisphere**:
- Repeat steps 1-13 with lat=-23.5° (user's Brazil config).
- Math should be equivalent — pole sign inverts but error
  magnitude stays consistent.

## Edge cases (declared, deferred)

- **Meridian crossing during 30° slew**: picker should choose
  3 points all east OR all west of meridian. If `ra0 +
  slewStep` would cross → slew the opposite direction
  (-slewStep). Stub `// TODO: meridian-aware point picker` in
  `RunAsync` at PA-2, fix in a separate PA-6.
- **Low star count (solve fail)**: retry once with
  `exposureSec * 2`, then fail with actionable message "Plate
  solve failed at point N — increase exposure or gain".
  Already in PA-3.
- **Camera rotation**: arrow uses `rotationDeg` from the most
  recent solve; if the camera is remounted mid-session, next
  solve corrects automatically.
- **Refine Stop mid-solve**: outer `ct` cancels in-flight
  `SolveAsync` and `CaptureAsync` (both accept ct). `finally`
  guarantees `Phase = Idle`. Already in PA-5.
- **SideOfPier flip during job**: deferred — TODO comment only.
  Realistic? 30° slew rarely crosses, but high latitude near
  the pole can.

## Out of scope (deferred)

- DARV / drift alignment (no plate solver).
- Manual-slew TPPA (user spins RA by hand).
- Polar scope clocking-angle assistance (shows "rotate polar
  scope reticle to HH:MM position").
- Permanent error log/history (e.g. records every alignment in
  the STUDIO frame library with metadata).

---

# Previous plan: Built-in equipment simulator (SIM-1..8)

> Previous plan (CLST, client-side live stacking via WASM)
> preserved below.

## Context

To test Polaris end-to-end today, the user needs **real hardware**
(Pi + camera + mount + cables) OR the wisdom to run INDI
simulators manually. The simulators exist (`indi_simulator_ccd`,
`_telescope`, `_focus`, `_wheel`, `_guide`) and the best of them,
`indi_simulator_ccd`, **renders real stars** from the GSC catalog
based on the simulated mount's current RA/Dec — slew to M31, see
M31; defocus, HFR rises; dither, position changes. Perfect suite
for testing plate-solve + alignment + live stacking + autofocus
**without hardware**.

The problem: today the user needs to open a terminal and run
`indiserver -v indi_simulator_ccd indi_simulator_telescope ...`
by hand. For newbies it's a barrier; for devs iterating on
Polaris it's overhead every session.

This plan adds an **integrated simulator mode**: the UI has
"Launch simulator stack" + "Stop" buttons, optional auto-start
on boot, install detection + download link when absent. Visual
parity with the existing `PHD2ProcessManager`.

**Decisions** (with the user):
- **Platforms**: Linux/macOS first-class (INDI simulators via
  `indi-bin` apt/brew) + **Windows too** (detects ASCOM CamSim/
  TelescopeSim/FocuserSim/FilterWheelSim installs from ASCOM
  Platform). No degradation by OS.
- **Configurable stack**: checkbox per device type (CCD,
  Telescope, Focuser, FilterWheel, Guider, Dome, Weather) — user
  picks which to spawn. Sensible default: CCD + Telescope +
  Focuser + FilterWheel.
- **Lifecycle**: both — "Auto-start on boot" toggle + manual
  Launch/Stop buttons. Mirrors PHD2AutoStartService +
  PHD2ProcessManager pattern.

## Architecture

Three identical-in-shape layers to the PHD2 stack (referenced by
the corresponding filename in `src/NINA.Polaris/Services/`):

### 1. Per-platform process manager

**`Services/Simulator/ISimulatorBackend.cs`** — thin interface:

```csharp
public interface ISimulatorBackend {
    string Kind { get; }                                // "indi" | "ascom"
    bool IsSupported { get; }                           // platform check
    Task<SimulatorInstall> DetectInstallAsync(CancellationToken ct);
    Task<bool> LaunchAsync(SimulatorLaunchRequest req, CancellationToken ct);
    Task ShutdownAsync(CancellationToken ct);
    Task<bool> IsRunningAsync(CancellationToken ct);
    string DownloadInstructionsUrl { get; }
}

public record SimulatorInstall(
    bool Installed,
    string? Version,
    string? Path,
    IReadOnlyList<string> AvailableDevices,    // ["ccd","telescope","focus","wheel",...]
    string? Error);

public record SimulatorLaunchRequest(
    IReadOnlyList<string> Devices,
    int Port = 7624);
```

**`Services/Simulator/IndiSimulatorBackend.cs`** (Linux/macOS):
- `DetectInstallAsync`: tries `which indiserver` + `which
  indi_simulator_ccd`. Parses `indiserver --version` for a
  version string. Lists which `indi_simulator_*` binaries exist
  (varies per install).
- `LaunchAsync`: `indiserver -v -p {port} indi_simulator_ccd
  indi_simulator_telescope ...` as subprocess via PHD2-style
  `Process.Start` (redirect stdout/stderr to Polaris log). Holds
  PID in `_proc`.
- `ShutdownAsync`: graceful via `SIGTERM`
  (Process.CloseMainWindow on Linux ≡ SIGTERM), 3 s timeout, then
  Kill.
- `IsRunningAsync`: TCP probe on `127.0.0.1:{port}` with 500 ms
  timeout (same pattern as PHD2ProcessManager).
- `DownloadInstructionsUrl`: link to
  `https://www.indilib.org/get-indi/download.html`.

**`Services/Simulator/AscomSimulatorBackend.cs`** (Windows):
ASCOM simulators are **GUI apps** (.exe opening a window), not
daemons. Polaris can't silently spawn them the way it does
indiserver. Different strategy:
- `DetectInstallAsync`: reads ASCOM Platform's COM registry
  checking for `ASCOM.Simulator.Camera`,
  `ASCOM.Simulator.Telescope`, etc. present
  (`HKLM\SOFTWARE\Classes\ASCOM.Simulator.Camera`).
- `LaunchAsync`: does **local Alpaca discovery** (we already
  have `Services/Alpaca/AlpacaDiscovery.cs`) — user needs to run
  the **Alpaca Omni Simulator** (single .exe exposing all
  ASCOM sims via Alpaca HTTP). If omni sim not running, we
  `Process.Start("AlpacaOmniSimulator.exe")` if detected on PATH
  or Program Files; otherwise show banner "Install + start
  Alpaca Omni Simulator from
  https://github.com/ASCOMInitiative/ASCOMSimulators".
- `ShutdownAsync`: kill `AlpacaOmniSimulator.exe` process if we
  launched it. If user opened it manually, just log "simulator
  still running, close it manually" — we don't own the PID.
- `IsRunningAsync`: TCP probe on `127.0.0.1:32323` (default
  port of Alpaca Omni Sim) + quick check of
  `/management/v1/configureddevices`.

Why this Windows-specific detail matters: real cross-OS parity
matters for CI/dev test on any OS, and Alpaca Omni Simulator is
the only viable "single binary, daemon-mode" option on Windows.

### 2. Orchestrator service

**`Services/Simulator/SimulatorService.cs`** — singleton
coordinating backends + exposing surface for endpoint/UI:

```csharp
public class SimulatorService {
    public ISimulatorBackend ActiveBackend { get; }
    public SimulatorInstall? LastDetect { get; }
    public bool IsRunning { get; }
    public IReadOnlyList<string> RunningDevices { get; }
    public DateTime? LaunchedAt { get; }
    public string? LastError { get; }

    Task<SimulatorInstall> RefreshDetectionAsync();
    Task<bool> LaunchAsync(IReadOnlyList<string> devices, int port);
    Task ShutdownAsync();
    Task<bool> ProbeRunningAsync();
}
```

Resolves which backend to use in the constructor via
`OperatingSystem.IsWindows()` etc. Extra backends (e.g. future
NINA Desktop simulator headless via remote API) plug in here
without changing the above interfaces.

### 3. Auto-start service

**`Services/Simulator/SimulatorAutoStartService.cs`** —
`BackgroundService`, copies the exact pattern of
`PHD2AutoStartService`:
- `ExecuteAsync`: waits 3 s for app to come up, reads
  `UserProfile.SimulatorAutoStart` + `UserProfile.SimulatorDevices`,
  calls `SimulatorService.LaunchAsync(...)` if toggle ON and
  detection OK.
- Non-blocking: fire-and-forget via `Task.Run` so startup isn't
  delayed.
- Reacts to toggle changes? NO — only runs on boot. Changes
  require a Polaris restart OR manual Launch button click.

### 4. Persistence

**`Services/ProfileService.cs`** — adds to `UserProfile`:
```csharp
public bool SimulatorAutoStart { get; set; } = false;
public List<string> SimulatorDevices { get; set; }
    = new() { "ccd", "telescope", "focus", "wheel" };
public int SimulatorPort { get; set; } = 7624;     // INDI default
```

Settings-level (not rig), because "fake hardware" isn't
equipment-specific — it's a dev-environment toggle.

### 5. Endpoints

**`Endpoints/SimulatorEndpoints.cs`** new:
- `GET /api/simulator/status` →
  `{kind, installed, version, devicesAvailable, isRunning,
  runningDevices, launchedAt, lastError}`.
- `POST /api/simulator/launch` body `{devices: string[], port:
  int}`.
- `POST /api/simulator/shutdown`.
- `POST /api/simulator/detect` (force refresh detection).
- `PUT /api/simulator/settings` body `{autoStart, devices, port}`
  → persists to UserProfile.
- `GET /api/simulator/settings` → returns current config.

### 6. WebSocket status

`WebSocket/StatusStreamHandler.cs` gets `simulator` block on the
existing 1 Hz payload:
```json
"simulator": {
  "kind": "indi", "installed": true, "version": "2.1.4",
  "isRunning": true,
  "runningDevices": ["ccd","telescope","focus","wheel"],
  "launchedAt": "2026-05-23T10:30:00Z",
  "autoStart": false
}
```

### 7. UI

**New "Simulator" panel in the SETTINGS tab** (not in RIGS —
this is dev-mode, not equipment config). Layout:

```
┌─ Equipment simulator ──────────────────────────────────────┐
│ Backend: INDI simulators (Linux)        [⟳ Re-detect]      │
│ Status:  ✓ Installed v2.1.4  ·  Currently: 🟢 Running      │
│                                                              │
│ Devices to start:                                           │
│   ☑ Camera (CCD)       ☑ Telescope                          │
│   ☑ Focuser            ☑ Filter Wheel                       │
│   ☐ Guider             ☐ Dome           ☐ Weather           │
│                                                              │
│ INDI port: [7624]   ☐ Auto-start when Polaris boots         │
│                                                              │
│ [▶ Launch simulators]    [⏹ Stop]                           │
│                                                              │
│ ↓ Tip: After launch, go to RIGS tab and pick "Simulator"    │
│   in each device dropdown.                                  │
└──────────────────────────────────────────────────────────────┘
```

When `installed: false`, the central content swaps for a banner:
```
⚠ INDI simulator drivers not installed.
   Run: sudo apt install indi-bin
   Or: brew install indi-bin (macOS)
   [Open INDI docs →]
```

(Windows equivalent: banner points at the Alpaca Omni Simulator
release page on GitHub.)

**Launch** button fires POST `/api/simulator/launch`, shows
spinner until `isRunning` becomes true via WS (~1-2 s).

## Phases

### SIM-1: ISimulatorBackend + IndiSimulatorBackend (Linux/macOS)
- Interface, record types, IndiSimulatorBackend implementation.
- Process spawn + graceful shutdown + TCP probe.
- Detection via `which` + version parse.
- Tests: parsing version output, mock subprocess for shutdown
  logic.

### SIM-2: SimulatorService orchestrator + ProfileService fields
- Singleton, dependency-injected, picks backend by OS.
- New `UserProfile` fields + migration safe (defaults).
- Tests: backend selection by OS, settings round-trip.

### SIM-3: SimulatorAutoStartService
- BackgroundService, copies PHD2AutoStartService pattern.
- Reads profile on startup, calls LaunchAsync if autoStart=true
  + detection OK.
- Tests: triggers launch with correct devices; skips when
  disabled.

### SIM-4: Endpoints + WS payload
- 5 new endpoints in SimulatorEndpoints.cs.
- StatusStreamHandler extension with `simulator` block.
- Smoke tests via integration (TestServer).

### SIM-5: AscomSimulatorBackend (Windows)
- Registry COM detection for ASCOM.Simulator.* keys.
- Detect + launch Alpaca Omni Simulator binary.
- Health probe via Alpaca management API.
- Tests: registry parse mock, port probe.

### SIM-6: UI panel in Settings tab
- New panel following the visual pattern of other panels (PHD2).
- Alpine state: `simulator: {kind, installed, version, isRunning,
  devices, ...}`.
- Methods `simulatorLaunch / simulatorShutdown / simulatorReDetect
  / saveSimulatorSettings`.
- CSS: reuse `.equip-card-*` classes.

### SIM-7: Docs + verify
- `docs/user-guide/simulator-mode.md` walkthrough.
- README section "Testing without hardware".
- Verify end-to-end (next phase because it touches real runtime).

### SIM-8: Dynamic device add/remove via INDI FIFO (follow-up)
After SIM-1..7 shipped, a follow-up added the ability to add/
remove devices without restarting indiserver, using its FIFO
control file. Lets the UI checkboxes act live.

## Files created

- `src/NINA.Polaris/Services/Simulator/ISimulatorBackend.cs`
- `src/NINA.Polaris/Services/Simulator/IndiSimulatorBackend.cs`
- `src/NINA.Polaris/Services/Simulator/AscomSimulatorBackend.cs`
- `src/NINA.Polaris/Services/Simulator/SimulatorService.cs`
- `src/NINA.Polaris/Services/Simulator/SimulatorAutoStartService.cs`
- `src/NINA.Polaris/Endpoints/SimulatorEndpoints.cs`
- `tests/NINA.Polaris.Test/IndiSimulatorBackendTests.cs`
- `tests/NINA.Polaris.Test/AscomSimulatorBackendTests.cs`
- `tests/NINA.Polaris.Test/SimulatorServiceTests.cs`
- `docs/user-guide/simulator-mode.md`

## Files modified

- `src/NINA.Polaris/Services/ProfileService.cs` — 3 fields on
  `UserProfile`.
- `src/NINA.Polaris/Program.cs` — register `SimulatorService` +
  backend + `SimulatorAutoStartService` (hosted), map
  `SimulatorEndpoints`.
- `src/NINA.Polaris/WebSocket/StatusStreamHandler.cs` —
  `simulator` block on payload.
- `src/NINA.Polaris/wwwroot/index.html` — new "Equipment
  simulator" panel on Settings tab.
- `src/NINA.Polaris/wwwroot/js/app.js` — state + methods.
- `src/NINA.Polaris/wwwroot/css/app.css` — small tweaks if
  necessary.
- `docs/user-guide/README.md` — link to simulator-mode.md under
  "For developers" section.
- `README.md` — "Testing without hardware" section.

## Reused code

- **`Services/PHD2ProcessManager.cs`** — direct template for the
  process-manager pattern: `which`-style detection, candidate
  paths cross-platform, TCP liveness probe (500 ms timeout),
  graceful shutdown + force-kill timeout. Copy the structure,
  adapt the paths.
- **`Services/PHD2AutoStartService.cs`** — direct template for
  BackgroundService auto-start: 3 s stagger, reads profile
  toggle, fire-and-forget launch via Task.Run.
- **`Services/Phd2GuiSessionService.cs`** — Linux subprocess
  launch pattern (xpra). Same spawn + log-redirect shape.
- **`Services/Alpaca/AlpacaDiscovery.cs`** + `AlpacaClient.cs`
  — Alpaca Omni Simulator is an Alpaca server; we already have
  the client. `AscomSimulatorBackend.IsRunningAsync` reuses it
  for health probe.
- **PHD2 UI pattern in index.html** (Settings/Guide tab, status
  badge + Launch/Stop buttons + "not detected" banner) —
  exact copy/paste.
- **WS status broadcast pattern** — StatusStreamHandler already
  has a pattern for all blocks; just add another sub-object.
- **`UserProfile.PHD2AutoStart`** — exact same toggle shape for
  SimulatorAutoStart.

## Verification end-to-end

### Linux (Pi 2 or desktop)
1. **Without `indi-bin` installed**: Settings → Simulator panel
   shows "Not installed" banner + `sudo apt install indi-bin`
   command.
2. **`sudo apt install indi-bin`** → click Re-detect → status
   becomes "✓ Installed v2.x.x", list of available devices
   appears.
3. **Click Launch** → spinner ~1-2 s → status becomes
   "🟢 Running" + list of running devices.
4. **RIGS tab** → each dropdown now shows "CCD Simulator",
   "Telescope Simulator", etc. (because indiserver is running
   with them).
5. **Connect simulated camera + mount** → capture a frame →
   see real stars from the GSC catalog (not synthetic noise).
6. **Slew to M31** via "Go to" → next capture shows M31
   (simulator cortex renders the pointed region).
7. **Plate solve** the frame → ASTAP resolves correctly (real
   sky, just rendered).
8. **Live stack ON** → 5 frames → CLST pipeline (WASM client-
   side if browser supports) accumulates correctly; HFR stable
   because simulator focus is perfect.
9. **Click Stop** → process terminates, RIGS dropdowns show
   empty list on next refresh.

### Windows (mini PC)
1. **Without Alpaca Omni Sim**: banner points at release page.
2. **After install + auto-detect of the .exe on PATH**: click
   Launch → subprocess starts, status "🟢 Running".
3. **RIGS tab** → "Alpaca" driver dropdown lists simulated
   devices (discovered via local Alpaca discovery on port
   32323).
4. Rest of flow identical to Linux.

### Auto-start
- Check "Auto-start when Polaris boots" → saves.
- Restart Polaris → simulator stack comes up ~3 s after app
  startup (visible in log: "Simulator auto-started:
  indi_simulator_ccd ...").

### Cross-client / multi-tab
- Open 2 browsers on the same Polaris → both see the same
  simulator state (via WS payload). Click Launch in one →
  the other sees status change within ~1 s.

## License + compatibility notes

- **INDI**: GPLv2; we run as a separate subprocess, no code
  mixing. Polaris stays MPL 2.0.
- **ASCOM Platform / Alpaca Omni Simulator**: MIT-equivalent
  ASCOM license; same situation (subprocess, no linkage).
- **Privacy**: nothing leaves the host — simulator is 100% local.
- **Performance**: `indiserver` + 4 sim drivers ≈ 30-80 MB RAM
  total on Pi 2. Acceptable; Polaris on Pi 2 is already tight
  (CLST solves the image-math side).
- **Failure mode**: if `indiserver` dies mid-session (crash),
  the TCP probe sees and marks `isRunning: false` on the next
  tick. User clicks Launch again. No automatic restart — keep
  it simple; PHD2 doesn't either.
- **Port conflict**: if the user already has `indiserver`
  running manually on port 7624, our Launch fails (port in
  use). Clear error in banner: "Port 7624 already in use. Stop
  the other indiserver or change Polaris's port in Settings".

---

# Previous plan: Pi 5 first-class deployment + .deb packaging + tag-driven release pipeline (DEB-* / PI5-* / PHD2GUI-*)

> Previous plan (CLST, client-side live stacking via WASM) preserved
> below starting at `# Previous plan: Client-side live stacking via WASM (CLST)`.
> The entire CLST-1..8 stack is in production; this plan builds the
> distribution and operability layer on top so a regular
> astrophotographer can install Polaris on a Pi 5 with `apt install`
> and never need a shell.

## Context

After CLST + the SIM/PA/SWE/ED/GX/CC/CCALB/INDI-WEB feature plans
landed, Polaris on a Pi 4/5 was technically functional but operating
it required:
- A ~500-line setup doc (`raspberry-pi-setup.md`)
- Manual apt installs, .NET runtime install, indi-web pipenv setup
- SSH access to debug any service that misbehaved
- Manual `xpra start` + `pkill` + `pip install` for recovery

To get to "regular user, no shell, no debugging" four things had
to happen:

1. **One-line install** via a Debian package wrapping all setup
2. **Release pipeline** that publishes the package automatically on tag
3. **Single source of truth doc** for Pi 5 install (consolidated guide)
4. **Recovery flows that work from the browser** (no SSH for common breakage)

Plus a fifth requirement that surfaced during real-Pi testing: the
PHD2 GUI iframe (PH2X-7) had been written but never end-to-end
validated against a real PHD2 install, so several bugs hid there
(proxy prefix strip missing, RAM widget reporting 100%, postinst URL
saying http://, Python 3.13 cgi removal breaking indi-web, etc).

## Decisions confirmed with the user

- **.deb is the recommended install path on Pi**; manual setup doc
  stays as fallback for users who want to understand the pieces.
- **Self-contained .NET 10 runtime** bundled in the .deb (~150 MB
  package, but eliminates the fragile "install dotnet runtime
  separately" step on Bookworm where it's not in apt).
- **Six release targets** in one workflow: deb arm64/amd64, portable
  tar.gz arm64/x64, portable zip win-x64/win-arm64.
- **Tag-driven version** flows through to the assembly so the UI
  banner shows the same number as the .deb filename and the GitHub
  Release.
- **PHD2 GUI subtab is the default** in the GUIDE panel (setup happens
  there; Control is for monitoring).
- **No SSH required for recovery**: Relaunch PHD2 button + iframe
  reload + Restart all work from the browser.
- **WebGPU in-browser AI prominent in docs**: the user explicitly
  flagged that "GraXpert running on client GPU directly in the
  browser" was buried and deserved top-billing in the README.

## What shipped (chronological commit order)

### Discovery + small fixes (before .deb landed)

- `665526c` GraXpert: add `~/graxpert/{graxpert,GraXpert}` to Linux
  auto-detect candidates. User extracted GraXpert tarball under
  lowercase path; the existing candidate list only had capital-G.
- `f293edc` GraXpert: detect Python venv installs and invoke via
  `python -m graxpert.main`. ARM users typically have GraXpert as a
  PyPI package in a venv (no prebuilt ARM binary), so the
  `IsPythonInvocation` check + `ArgsPrefix` synthesize the
  `-m graxpert.main` prefix transparently. `BuildArgs` contract
  unchanged so existing tests keep pinning.
- `c7c849c` docs(rpi-setup): GraXpert via pip venv + manual AI
  model transfer to `~/.local/share/GraXpert/{ai-models,bge-ai-models}/`.

### Single consolidated Pi setup doc

- New `docs/user-guide/raspberry-pi-setup.md`: hardware checklist
  (Pi 4 vs Pi 5 PSU/cooling/NVMe), OS flashing, first-boot tasks
  (apt upgrade, aarch64 verify, 2 GB swap, Pi 4 GPU split), optional
  SSD mount over `~/files/`, single apt block, three install paths
  (release tarball / Docker / build from source), systemd unit with
  `HOME` + `DOTNET_ROOT` + `PATH` env baked in, INDI driver loading
  (manual `indiserver` vs indi-web), PHD2 + HTTPS optional sections,
  end-to-end verification using the simulator stack, update +
  troubleshooting.

### `.deb` packaging (DEB-1..6)

- New `packaging/` tree:
  - `deb/DEBIAN/control`: metadata, depends (libfontconfig1, libssl3,
    libicu72|76, indi-bin, python3-venv, adduser, systemd),
    recommends (indi-full, astap, phd2, siril, xpra,
    xserver-xorg-video-dummy, dphys-swapfile)
  - `deb/DEBIAN/conffiles`: marks `/opt/polaris/appsettings.json`
  - `deb/DEBIAN/postinst`: creates `polaris` system user, mkdir
    `~/files`, creates `/opt/polaris-indiweb-venv/` and pip-installs
    `indiweb` + `legacy-cgi`, enables + starts systemd unit, prints
    URL summary with HTTPS
  - `deb/DEBIAN/prerm`: stops service cleanly before file removal
  - `deb/DEBIAN/postrm`: on purge wipes venv + disables service,
    preserves `/home/polaris/files` and profile data
  - `deb/lib/systemd/system/polaris.service`: hard-coded
    `User=polaris`, `HOME=/home/polaris`, `POLARIS_IMAGE_OUTPUT_DIR=/home/polaris/files`
  - `deb/opt/polaris/appsettings.json`: default IndiWeb path
    (`/opt/polaris-indiweb-venv/bin/indi-web`)
- `packaging/.gitattributes`: forces LF on all packaging files
  (dpkg rejects CRLF on control + shell scripts dies with
  `bad interpreter`)
- `packaging/build-deb.sh`: local build script (dotnet publish
  self-contained + dpkg-deb), accepts VERSION arg, forwards to
  `-p:Version` so the assembly matches the .deb filename
- `packaging/README.md`: build/install/upgrade/purge docs

### Real-Pi shakedown fixes (caught during first .deb install)

- `7990736` indi-web: install `legacy-cgi` alongside `indiweb` in
  postinst. Python 3.13 (Bookworm late-2025 images) removed the
  `cgi` stdlib module that indi-web's vendored bottle.py imports
  unconditionally; without legacy-cgi the iframe shows
  "Start failed: unknown" because indi-web crashes at startup.
- `e778e5d` host-metrics: read /proc/meminfo on Linux instead of
  `IResourceMonitor.MemoryUsedPercentage` (which counts buff/cache
  as used and makes a healthy Pi with file cache show 4.0/4.0 GB
  red). Now matches what humans see in `free -h` available column.
- `0bf34d6` proxy fix: strip `/phd2-gui` prefix in the reverse proxy
  before forwarding to xpra. Without this xpra returned 404 for
  `/phd2-gui/js/Client.js` etc and the iframe showed bare HTML
  shell with no JS, hence the long-running "iframe black" bug. Same
  pattern as the `/indi-web/*` proxy a few lines below had since
  the start, just got missed in PH2X-7.
- `d3ccf2f` postinst + rpi doc: `https://` everywhere on port 5000
  (Polaris auto-generates self-signed cert via GX-10; `http://` on
  5000 fails with "Empty reply from server" because port speaks
  TLS only). Explainer about the self-signed cert warning + WebGPU
  rationale baked into the summary.

### CI release pipeline (DEPLOY-1..3)

- `1a4c979` `.github/workflows/release.yml`: consolidated workflow
  triggered on `v*` tag push or workflow_dispatch. Six matrix jobs
  (deb-arm64/amd64, linux-arm64/x64 tarball, win-x64/arm64 zip),
  each self-contained .NET publish. Lintian style-check job on the
  .debs. Release job downloads all artifacts, attaches to GitHub
  Release with auto-generated notes + per-platform install
  commands in the body.
- `bcb7ff6` tag-driven version. NINA.Polaris.csproj honors
  `-p:Version=X.Y.Z` when passed (release workflow + build-deb.sh
  both forward it), so the UI banner matches the tag. Falls back
  to the auto-stamp `0.1.{days}.{seconds}` for local dev builds
  without an explicit version.
- Release body now uses `/releases/latest/download/polaris_arm64.deb`
  (unversioned URLs that resolve to the newest release). Workflow
  copies versioned files to unversioned names so both exist on
  every release.

### PHD2 GUI iframe UX (PHD2GUI-1..3)

- `0604c4e` PHD2 GUI subtab is now default in the GUIDE tab (was
  Control). Matches the actual workflow: setup (Connect Equipment,
  Loop, select star) happens in the GUI; Control is for monitoring
  + automation after PHD2 is configured. Sidebar GUIDE button also
  calls `loadPhd2GuiStatus()` to refresh state when guideTab is gui.
- `688d885` PHD2 GUI iframe grows to fill remaining viewport height
  via `flex: 1 + min-height: 0` chain through `.phd2-gui-tab` and
  `.phd2-gui-frame-wrap` (previously clamped to 600 px, wasting
  30-40% of a 1080 viewport).
- `c5e80e2` PHD2-inside-xpra detection + UI "Relaunch PHD2" button.
  New `Phd2Running` property polled every 15 s via pgrep, exposed
  on the gui-session status JSON. UI toolbar gains a green / amber
  PHD2 chip distinguishing "xpra up + phd2 alive" from "xpra up
  but phd2 missing", plus an amber `Relaunch PHD2` button that
  calls `xpra control :100 start-child phd2` after pkill'ing any
  orphan phd2 (new `RelaunchPhd2Async` + `/api/guider/gui-session/relaunch-phd2`
  endpoint). User feedback was explicit: "a regular user is not
  going to SSH into the Pi to recover". All recovery now from the
  browser.

### Documentation alignment (DOC-PI5-*)

- `ae1571d` docs aligned: README Deployment section rewritten to
  lead with the .deb one-liner; tarball + zip for non-Debian
  platforms; old `publish-linux-arm64.sh` / `install.sh` recipe
  removed. user-guide README pointer updated. `guide-phd2.md`
  documents the new toolbar chips + Relaunch button + tab order.
  `packaging/README.md` gains a release-process section.
- `fa490d1` README + rpi-setup get a dedicated section on the
  in-browser GraXpert AI / WebGPU architecture. User flagged this
  was buried. Now top-billing with concrete timing table (Pi CLI
  4-8 min vs M1 WebGPU 8-12 s vs WASM-fallback 60-90 s) and a
  clear explanation that the Pi never runs the AI: serves .onnx
  files + raw FITS pixels, client GPU does the work.

## Verification

End-user happy path on fresh Raspberry Pi OS Lite (64-bit) Bookworm:

```bash
# After flashing OS and SSH'ing in (one time):
sudo apt update && sudo apt full-upgrade -y
sudo reboot
# SSH back in
wget https://github.com/DanWBR/NINA.Polaris/releases/latest/download/polaris_arm64.deb
sudo apt install ./polaris_arm64.deb
# postinst prints: "Polaris running at https://polaris-pi.local:5000"
```

From a laptop / tablet / phone on the LAN:
1. Open `https://polaris-pi.local:5000`, accept self-signed cert once
2. Aba GUIDE → PHD2 GUI subtab → ▶ Start session → iframe shows PHD2 native window
3. Aba RIGS → INDI Drivers section → pick simulator profile in indi-web → Start
4. RIGS cards autopopulate with simulators → Connect All
5. PREVIEW tab → Take Snap → frame appears with real GSC stars
6. EDITOR tab → AI section → Background Extraction → runs on the
   laptop GPU (WebGPU), Pi sits at ~5% CPU

Zero shell commands after the initial install. Zero failed
recovery paths that need shell intervention.

## Out of scope (deferred)

- **apt repo hosting** (`deb https://polaris.repo/...`): would let
  users `apt update && apt install polaris` for upgrades. Requires
  GPG-signed repo on GitHub Pages or similar. v1 ships .deb via
  GitHub Releases only; upgrade is `wget` + `apt install ./newer.deb`.
- **SD card image** (`image-build/README.md` plan): would bake the
  .deb into a Pi OS image so users skip even the OS install step.
  Plan exists but no scripts yet.
- **Windows MSI installer**: portable zip works for now; MSI/MSIX
  is polish for later.
- **Snap / Flatpak**: same idea, different ecosystems, lower priority.

---

# Previous plan: Client-side live stacking via WASM (CLST)

> Previous plan (LSTR, auto re-focus / re-center triggers) preserved
> below starting at `# Previous plan: Live stacking triggers`. The entire
> LSTR-1..6 stack is already in production; the current plan builds on
> top of it.

## Context

Polaris today does **all** image processing on the server, Pi 2/3/4
or mini-PC. The user's Pi 2 (`polaris`, ARMv7, 1GB RAM, 900 MHz)
saturates all 4 cores running `LiveStackingService` (StarDetector +
StarMatcher + AffineTransform + ImageResampler + accumulator) and still
needs to handle capture, INDI, file I/O. The client (laptop / desktop)
sits at 95% CPU idle just rendering the canvas.

**Architectural insight**: the server is irreplaceable **only** for
device management (INDI/PHD2/cameras), persistence (FITS/XISF on
SSD), and orchestration (sequence engine). Anything that's image
math can run wherever there's spare CPU, and that's usually the
client, not the Pi.

**MVP**: migrate live stacking to the client via WASM. If it works
well, it opens the door to moving BG extraction, frame quality
(Laplacian), star annotations, and (eventually) Studio batch jobs
too.

**Decisions confirmed with the user**:
- **Tech stack**: C# AOT-compiled to WASM via .NET 10. Reuses
  `NINA.Image.Portable` literally (StarDetector, StarMatcher,
  AffineTransform, ImageResampler) without rewriting in another
  language. Trade-off: ~5-8MB gzipped bundle, 2-3x slower than
  Rust+SIMD, but zero porting + zero algorithm divergence
  between client and server.
- **Mode**: **auto-detect** in the WS handshake. The client reports
  its capability (`wasmReady`, version), the server decides.
  Manual override available in Settings (force server / force client).
- **Multi-client**: each client stacks independently. No
  master/slave. If a client reconnects mid-session, it picks up from
  the server's current frame.

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│  Server (Raspberry Pi 2)                                     │
│                                                              │
│  [Camera] → CaptureAsync → ImageWriter (FITS to disk)        │
│                  ↓                                           │
│                  ImageRelayService                           │
│                  ↓ raw uint16 + LZ4 + 20-byte header         │
│                  /ws/image-stream                            │
│                                                              │
│  LiveStackingService, METRICS-ONLY mode                     │
│    runs only StarDetector for HFR/star count                 │
│    skips StarMatcher/AffineTransform/Resampler/accumulator   │
│    fires FrameIntegrated event (for triggers)                │
│                                                              │
│  LiveStackTriggersService, unchanged, consumes              │
│    server-side OR client-reported metrics                    │
└──────────────────────────────────────────────────────────────┘
                          ↓ ↑
                       WebSocket
                          ↓ ↑
┌──────────────────────────────────────────────────────────────┐
│  Client (browser)                                            │
│                                                              │
│  [WASM module: NINA.Polaris.Wasm]                            │
│    references NINA.Image.Portable                            │
│    JS-exports: Initialize/AddFrame/GetResult/Reset           │
│    uses SIMD for inner loops                                 │
│                                                              │
│  app.js handleImageFrame:                                    │
│    if computeMode === 'client' && wasmReady:                 │
│      → WASM.AddFrame(pixels) → metrics + stacked buffer      │
│      → WebGL render the stacked buffer                       │
│      → WS send 'client-stack-progress' {frameCount, hfr, …}  │
│    else: existing server-side path (unchanged)               │
└──────────────────────────────────────────────────────────────┘
```

**Bridge messages** (new):
- Client → server (on WS connect): `{type:'client-capability',
  wasm:true, wasmVersion:'1.0', simd:true}`
- Client → server (per frame): `{type:'client-stack-progress',
  frameCount, hfr, starCount, alignmentOk}`
- Server → client (in the existing status payload): `liveStack.
  computeMode: 'server' | 'client'` so the UI chip can show who's
  doing the work

## Phases

### CLST-1: Server passthrough mode
- `LiveStackingService` gains `enum StackMode { Full, MetricsOnly }`
- `MetricsOnly` mode runs only StarDetector + fires FrameIntegrated;
  skips the accumulator / resampler / relay-of-stacked-result
- Default stays `Full` (zero behavioral change until CLST-5)
- Tests pinning both modes
- WS payload adds `liveStack.computeMode`
- **No client-side change in this phase**, infra only

### CLST-2: WASM project scaffolding
- New `src/NINA.Polaris.Wasm/` net10.0 with `<WasmEnableSIMD>true</WasmEnableSIMD>`
- Placeholder: one `[JSExport]` method that doubles a number
- `dotnet publish -p:PublishAOT=true -r browser-wasm` → output in
  `src/NINA.Polaris/wwwroot/js/wasm/`
- `index.html` + `app.js` load it, smoke-test at boot, log
  "WASM ready vX.Y" in the console
- Pin the build pipeline before touching real code

### CLST-3: Port the math
- WASM project references `NINA.Image.Portable`
- API exposed via `[JSExport]`:
  ```csharp
  public static void Initialize(int w, int h, int bayerPattern);
  public static StackMetrics AddFrame(ushort[] pixels);
  public static ushort[] GetStackedResult();
  public static void Reset();
  ```
- Internally uses `StarDetector + StarMatcher + AffineTransform +
  ImageResampler` (same classes as the server)
- Console harness in `tests/NINA.Polaris.Wasm.Smoke/` validates with
  synthetic frames (doesn't run in the browser; validates the C# code
  works before AOT)

### CLST-4: Client-side wiring
- `app.js` boot: detect WASM + SIMD support, load the module, send
  the capability message on WS connect
- `handleImageFrame` raw mode gains a branch: `if (computeMode ===
  'client')` → feed bytes into WASM, read stacked result, render
  through the existing WebGL pipeline (no change to it)
- Per frame: sends `client-stack-progress` to the server

### CLST-5: Server-side handshake + auto-mode
- `ImageStreamHandler` consumes `client-capability` + `client-stack-
  progress` messages
- `LiveStackingService` switches to `MetricsOnly` when ≥1 client
  with `wasm: true` is connected
- Switches back to `Full` when the last WASM client disconnects
  (guarantees fallback if a client closes mid-session)
- `LiveStackTriggersService` consumes HFR/star count from the
  **server-side detector** (which still runs in MetricsOnly), no
  dependency on the client

### CLST-6: Save current stack from client
- New `POST /api/livestack/upload-result` accepts multipart
  `(width, height, bitDepth, pixels[])`
- Server writes FITS via `FITSWriter`, indexes via
  `FrameLibraryService.RescanAsync()`
- UI: new "Save current stack" button on the LIVE tab when
  computeMode === 'client'

### CLST-7: UI status + override
- Activity bar or LIVE tab: chip showing "🖥 Server" or
  "🌐 Client (N fps)", where stacking is happening + perf
- Settings: per-rig dropdown "Live stack compute: Auto (default) /
  Force server / Force client"
- Override persists in `EquipmentProfile.LiveStackComputeMode`

### CLST-8: Tests + docs + benchmark
- WASM smoke tests in `tests/NINA.Polaris.Wasm.Smoke/`
- Server-side tests for mode switching in `LiveStackingService`
- Benchmark harness `bench/livestack-bench.html` loads a known FITS
  + reports ms/frame in both modes for comparison
- `docs/user-guide/client-side-compute.md` new page
- README "Performance offloading" section

## Files to create

- `src/NINA.Polaris.Wasm/NINA.Polaris.Wasm.csproj` (`net10.0`,
  `<RuntimeIdentifier>browser-wasm</RuntimeIdentifier>`,
  `<PublishAot>true</PublishAot>`, `<WasmEnableSIMD>true</WasmEnableSIMD>`)
- `src/NINA.Polaris.Wasm/Program.cs` (empty entry point)
- `src/NINA.Polaris.Wasm/LiveStackInterop.cs` (`[JSExport]` surface)
- `src/NINA.Polaris/wwwroot/js/wasm/` (build output destination,
  `.gitignore`d except for a README pointing to the build script)
- `src/NINA.Polaris/wwwroot/js/livestack-client.js` (WASM glue +
  message dispatch)
- `tests/NINA.Polaris.Wasm.Smoke/` (NUnit harness, AOT not required
  for the smoke tests, they run on the desktop runtime)
- `bench/livestack-bench.html` (offline browser harness)
- `docs/user-guide/client-side-compute.md`

## Files to modify

- `src/NINA.Polaris/Services/LiveStackingService.cs`, `StackMode`
  enum + branch in `AddFrameAsync`
- `src/NINA.Polaris/Services/LiveStackTriggersService.cs`,
  trigger eval keeps working with metrics-only mode (it's already
  data-driven)
- `src/NINA.Polaris/Services/ProfileService.cs`, `LiveStackComputeMode`
  field on `EquipmentProfile` (Auto | Server | Client)
- `src/NINA.Polaris/WebSocket/StatusStreamHandler.cs`, broadcast
  `liveStack.computeMode` + count of wasm-capable clients
- `src/NINA.Polaris/WebSocket/ImageStreamHandler.cs`, receive
  `client-capability` + `client-stack-progress` text messages
- `src/NINA.Polaris/Endpoints/LiveStackEndpoints.cs`,
  `POST /upload-result`
- `src/NINA.Polaris/NINA.Polaris.csproj`, build target depends on
  `NINA.Polaris.Wasm`'s wasm output being present
- `src/NINA.Polaris/wwwroot/index.html`, `<script>` tag for the WASM
  loader + UI chip + Settings dropdown
- `src/NINA.Polaris/wwwroot/js/app.js`, capability probe, mode
  dispatch in `handleImageFrame`
- `src/NINA.Polaris/wwwroot/css/app.css`, mode chip
- `docs/user-guide/live-stacking.md`, "Server vs client compute" section
- `README.md`, note about offloading

## Reuse of existing code

- **`NINA.Image.Portable.ImageAnalysis.StarDetector`**, called
  literally by the WASM project (assembly reference). Same algorithm
  on client and server.
- **`NINA.Image.Portable.ImageAnalysis.StarMatcher` /
  `AffineTransform` / `ImageResampler`**, same
- **`NINA.Image.Portable.ImageData.BaseImageData` / `ImageBuffer`**,
  wraps the pixels client-side the same way the server does
- **`/ws/image-stream` raw mode transport**, ALREADY exists with the
  20-byte header + LZ4. Zero protocol change in the server→client
  direction.
- **WebGL2 stretch + debayer (app.js lines 1042-1100)**, feeds off
  the stacked result regardless of who did the stacking
- **`LiveStackTriggersService`**, trigger evaluation is already
  data-driven via the `FrameIntegrated` event; it just needs the
  event to fire
- **`FrameLibraryService.RescanAsync`**, called after upload to
  auto-index the saved FITS

## Verification

1. **Build**: `dotnet build NINA.Polaris.slnx` + `dotnet publish
   src/NINA.Polaris.Wasm -p:PublishAot=true -r browser-wasm` →
   bundle in `wwwroot/js/wasm/`, no errors
2. **Tests**: full suite green (currently 510/515; expect +20-30
   new ones for WASM + mode switching)
3. **Browser boot**: dev tools console "WASM live-stack ready, vX"
   in < 3s on a laptop, < 8s on a mid-range mobile
4. **Pi 2 load**: measure server CPU **before** (server mode,
   stacking active) vs **after** (client mode). Expected: drop from
   ~60-80% to <20%. Server resident memory drops ~50-200MB
   (no accumulator buffer).
5. **Client load**: a modern laptop with WASM active should use
   ~5-15% of 1 core. Pixel 6 mobile ~30-50%. iPhone 13 ~15-25%.
6. **Output parity**: the same sequence of N frames stacked
   server-side vs client-side should produce a byte-identical
   result (same algorithms, same order). Confirm via diff of the
   exported FITS.
7. **Trigger integration**: HFR-driven auto-refocus still fires
   correctly when stacking is client-side (the server-side detector
   still runs in MetricsOnly mode)
8. **Save current stack**: click the button → FITS appears in `{rig}/
   integrated/{target}/`, opens in PixInsight, dimensions + bit depth
   correct
9. **Multi-client**: open 2 browsers → each stacks independently,
   triggers still fire based on server metrics (no double execution)
10. **Force server mode**: override in Settings → behavior
    identical to today's (regression baseline)
11. **WASM offline**: block the .wasm load (DevTools network
    throttle) → the app automatically falls back to server-side,
    no visual error

## Compatibility and licensing notes

- **.NET 10 AOT for browser-wasm**: officially supported, MIT.
  Typical AOT trimmed bundle: 5-8MB gzipped for our use.
- **SIMD**: WASM SIMD (phase 4 proposal) supported in Chromium 91+,
  Firefox 90+, Safari 16.4+. Without SIMD the code still runs,
  ~2-3x slower.
- **Cross-Origin Isolation**: we don't need `SharedArrayBuffer`
  for v1 (1 worker is enough). No special CORS headers.
- **Memory**: the WASM bundle allocates ~30MB heap (frame buffer +
  accum + detector workspaces). Acceptable on all target clients.
- **First-frame latency**: WASM init ~2-3s on a laptop. Strategy:
  during WASM boot, rendering stays in server-side mode; when WASM
  is ready, transparent switch.
- **Fallback chain**: server-side mode is always the last working
  resort. If WASM fails to load / crashes / the client closes, the
  server resumes stacking with no frame loss (passthrough mode kept
  the metrics).

---

# Previous plan: Live stacking triggers

## Context

`LiveStackingService` today is passive: it receives frames via
`AddFrameAsync` from whoever is capturing (sequence, manual snap,
preview loop), aligns via `StarMatcher`, integrates into a running mean,
and relays the result. It has no notion of time, of focus degradation,
or of astrometric drift. Long live-stacking sessions (an hour+) suffer:

- Focus drifts as the temperature falls, HFR rises, frames get worse
- Mount drift accumulates (periodic error, atmospheric refraction,
  imperfect model), the field "walks" and the star matcher starts to
  fail / the target of interest leaves the center

People doing long-stack astrophotography (EAA, comet hunting, planetary
imaging with long sessions) need **two automatic schedulers** during
the stack: periodic refocus and periodic recenter, without manual
intervention. This plan covers both with 4 + 3 trigger types that the
user can combine.

**Decisions confirmed with the user:**

- **Refocus triggers** (all enabled in parallel):
  - Every N integrated frames
  - Every N minutes elapsed
  - Temperature delta ≥ ±X°C since last focus
  - HFR degradation ≥ Y% above HFR at last focus
- **Recenter triggers** (all enabled in parallel):
  - Every N integrated frames
  - Every N minutes elapsed
  - Plate-solve drift ≥ X arcsec (per-frame light solve)
- **Reference RA/Dec source**: plate solve of the first integrated
  frame. Establishes the true astrometric position (more accurate than
  reading from the mount, which can have model error).
- **UI**: expandable `<details>` panel inside the LIVE tab, below the
  Stack ON / Reset button.

## Architecture

Three pieces:

### 1. `LiveStackingService`, minimal extensions

File: `src/NINA.Headless/Services/LiveStackingService.cs`

Today the service is closed, with no callbacks out. Add:

```csharp
public event Action<LiveStackFrameInfo>? FrameIntegrated;

public record LiveStackFrameInfo(
    int FrameCount,        // após esta integração
    IImageData Frame,      // frame original (não o stack acumulado)
    double MedianHfr,      // HFR mediano calculado nesta integração
    int StarCount,
    DateTime At);
```

Compute `MedianHfr` by reusing the `StarDetector` that's already
instantiated inside `AddFrameAsync` for alignment, just extend the
output. Cost: negligible (detection already runs).

Fire the event at the end of `AddFrameAsync` after the accumulator
update, with `Task.Run` to decouple (the subscriber can take a long
time, a 60s AF, and must not block whoever calls `AddFrameAsync`).

**But wait**: the "trigger pauses captures" rule depends EXACTLY on
blocking. Decision: fire **synchronously** inside `AddFrameAsync`
(await handlers in sequence). Whoever calls `AddFrameAsync`
(SequenceEngine, PREVIEW loop, CameraInstructions) already awaits,
so if the trigger handler takes 60s running AF, the next capture
waits. That's exactly the desired behavior (don't capture while the
focuser is moving).

Implementation: a C# `event` isn't async-await friendly. Switch to a
list of `Func<LiveStackFrameInfo, Task>` delegates:

```csharp
public delegate Task LiveStackFrameHandler(LiveStackFrameInfo info);
private readonly List<LiveStackFrameHandler> _frameHandlers = new();

public IDisposable SubscribeFrameIntegrated(LiveStackFrameHandler handler);
// AddFrameAsync end: await sequencial dos handlers
```

### 2. `LiveStackTriggersService`, orchestrator (new)

File: `src/NINA.Headless/Services/LiveStackTriggersService.cs`

Singleton. Subscribes `LiveStackingService.SubscribeFrameIntegrated`
in the constructor. Per frame, evaluates triggers + executes actions.

```csharp
public class LiveStackTriggersService : IDisposable {
    public LiveStackTriggers Settings { get; set; } = new();
    public LiveStackTriggersStatus CurrentStatus { get; }
    public event Action<LiveStackTriggersStatus>? StatusChanged;

    Task FireRefocusNowAsync(CancellationToken ct);
    Task FireRecenterNowAsync(CancellationToken ct);
}

public class LiveStackTriggers {
    // Refocus block
    public bool RefocusEnabled { get; set; }
    public int RefocusEveryNFrames { get; set; }        // 0 = disabled
    public int RefocusEveryMinutes { get; set; }
    public double RefocusTempDeltaC { get; set; }
    public double RefocusHfrIncreasePercent { get; set; }
    public AutoFocusRequest RefocusRequest { get; set; } = new() {
        Steps = 9, StepSize = 50, ExposureSeconds = 3, MinStars = 5
    };

    // Recenter block
    public bool RecenterEnabled { get; set; }
    public int RecenterEveryNFrames { get; set; }
    public int RecenterEveryMinutes { get; set; }
    public double RecenterDriftArcsec { get; set; }
    public double RecenterToleranceArcsec { get; set; } = 30;
}

public class LiveStackTriggersStatus {
    public bool IsExecuting { get; init; }
    public string? ExecutingKind { get; init; }   // "refocus" | "recenter" | null
    public DateTime? LastRefocusAt { get; init; }
    public int LastRefocusFrame { get; init; }
    public double LastRefocusHfr { get; init; }
    public double LastRefocusTempC { get; init; }
    public DateTime? LastRecenterAt { get; init; }
    public int LastRecenterFrame { get; init; }
    public double LastRecenterDriftArcsec { get; init; }
    public double? ReferenceRaHours { get; init; }
    public double? ReferenceDecDeg { get; init; }
    public bool ReferenceSolved { get; init; }   // false until first solve succeeds
    public string? LastError { get; init; }
}
```

**Per-frame trigger evaluation**:

```csharp
private async Task OnFrameIntegratedAsync(LiveStackFrameInfo info) {
    if (_isExecuting) return;       // skip if AF/recenter in flight
    if (info.FrameCount == 1) {
        _ = Task.Run(() => SolveReferenceAsync(info.Frame));
        return;                     // first frame doesn't trigger anything
    }
    // Per-frame drift solve for recenter (if drift trigger enabled)
    double? currentDrift = null;
    if (Settings.RecenterEnabled && Settings.RecenterDriftArcsec > 0
        && _referenceSolved) {
        currentDrift = await ComputeDriftAsync(info.Frame);
    }

    if (ShouldRefocus(info)) {
        await ExecuteRefocusAsync(info);
        return;                     // refocus first; recenter on next frame
    }
    if (ShouldRecenter(info, currentDrift)) {
        await ExecuteRecenterAsync(info, currentDrift);
    }
}

private bool ShouldRefocus(LiveStackFrameInfo info) {
    if (!Settings.RefocusEnabled) return false;
    if (Settings.RefocusEveryNFrames > 0
        && info.FrameCount - _lastRefocusFrame >= Settings.RefocusEveryNFrames)
        return true;
    if (Settings.RefocusEveryMinutes > 0
        && (info.At - _lastRefocusAt) >= TimeSpan.FromMinutes(Settings.RefocusEveryMinutes))
        return true;
    if (Settings.RefocusTempDeltaC > 0 && _equip.Camera != null
        && Math.Abs(_equip.Camera.Temperature - _lastRefocusTempC) >= Settings.RefocusTempDeltaC)
        return true;
    if (Settings.RefocusHfrIncreasePercent > 0 && _lastRefocusHfr > 0
        && info.MedianHfr >= _lastRefocusHfr * (1 + Settings.RefocusHfrIncreasePercent / 100.0))
        return true;
    return false;
}
```

`ExecuteRefocusAsync` → `_autoFocus.Start(Settings.RefocusRequest)` +
poll `_autoFocus.State == Idle`. Updates `_lastRefocusFrame/At/Temp/Hfr`.

`ExecuteRecenterAsync` → `_slewCenter.StartJob(_referenceRa, _referenceDec,
Settings.RecenterToleranceArcsec)` + poll. Updates
`_lastRecenterFrame/At/Drift`.

**Reference solve** (once on the first frame):

```csharp
private async Task SolveReferenceAsync(IImageData firstFrame) {
    try {
        var result = await _plateSolve.SolveAsync(firstFrame);
        if (result.Success) {
            _referenceRa = result.RightAscensionHours;
            _referenceDec = result.DeclinationDegrees;
            _referenceSolved = true;
        }
    } catch (Exception ex) { LastError = ex.Message; }
}
```

If it fails, `_referenceSolved = false` and the UI shows a warning
"Recenter disabled, first-frame plate solve failed".

**Drift compute** (each frame, if drift trigger is enabled):

```csharp
private async Task<double?> ComputeDriftAsync(IImageData frame) {
    try {
        var sol = await _plateSolve.SolveAsync(frame);
        if (!sol.Success) return null;
        // arcsec between (sol.RA, sol.Dec) and (_referenceRa, _referenceDec)
        return AngularDistance.ArcsecBetween(
            sol.RightAscensionHours, sol.DeclinationDegrees,
            _referenceRa, _referenceDec);
    } catch { return null; }
}
```

Cost: per-frame plate solve is expensive. Document for the user that
turning the drift trigger on consumes a lot of CPU; the default
fallback is frame count + minutes only.

### 3. Settings persistence

File: `src/NINA.Headless/Services/ProfileService.cs`

Add to `EquipmentProfile`:

```csharp
public LiveStackTriggers LiveStackTriggers { get; set; } = new();
```

Each rig has its own rules (thermodynamics varies by setup).
`EquipmentEndpoints.cs` PUT is already defensive, just add a
conditional set for `LiveStackTriggers ?? r.LiveStackTriggers`.

`LiveStackTriggersService` reads from `_profiles.ActiveEquipmentProfile
.LiveStackTriggers` in the constructor + subscribes to the
`EquipmentProfileActivated` event (exists, PH2X-2) to reload when
the user switches rigs.

### 4. Endpoints

`Endpoints/LiveStackEndpoints.cs`:

- `GET /api/livestack/triggers/status`, current state + last actions
- `PUT /api/livestack/triggers/settings`, body = `LiveStackTriggers`
- `POST /api/livestack/triggers/refocus-now`, manual fire (ignores gates, validates only preconditions)
- `POST /api/livestack/triggers/recenter-now`, manual fire

### 5. WebSocket payload

`WebSocket/StatusStreamHandler.cs`, extend the `liveStack` block:

```jsonc
{
  "liveStack": {
    "...campos existentes...": "...",
    "triggers": {
      "isExecuting": false, "executingKind": null,
      "lastRefocusAt": "...", "lastRefocusFrame": 42, "lastRefocusHfr": 1.9,
      "lastRecenterAt": "...", "lastRecenterFrame": 80, "lastRecenterDriftArcsec": 38,
      "referenceSolved": true,
      "referenceRaHours": 23.234, "referenceDecDeg": 12.583,
      "lastError": null
    }
  }
}
```

### 6. UI, expandable panel in the LIVE tab

`wwwroot/index.html` inside the `tab === 'live'` panel, below the Stack button:

```html
<details class="livestack-triggers" :open="liveStackTriggers.refocusEnabled || liveStackTriggers.recenterEnabled">
  <summary>⚡ Auto re-focus / re-center</summary>

  <fieldset>
    <legend>Auto re-focus</legend>
    <label><input type="checkbox" x-model="liveStackTriggers.refocusEnabled" @change="saveLiveStackTriggers()"> Enabled</label>
    <label>Every <input type="number" x-model.number="liveStackTriggers.refocusEveryNFrames" min="0"> frames</label>
    <label>Every <input type="number" x-model.number="liveStackTriggers.refocusEveryMinutes" min="0"> minutes</label>
    <label>When ΔT ≥ <input type="number" x-model.number="liveStackTriggers.refocusTempDeltaC" min="0" step="0.1"> °C</label>
    <label>When HFR ≥ <input type="number" x-model.number="liveStackTriggers.refocusHfrIncreasePercent" min="0"> % above last</label>
    <!-- AF request knobs -->
    <label>Steps <input type="number" x-model.number="liveStackTriggers.refocusRequest.steps"></label>
    <label>Step size <input type="number" x-model.number="liveStackTriggers.refocusRequest.stepSize"></label>
    <label>Exposure (s) <input type="number" x-model.number="liveStackTriggers.refocusRequest.exposureSeconds"></label>
    <button class="btn btn-sm" @click="refocusNow()">▶ Now</button>
    <p x-show="liveStackStatus.triggers?.lastRefocusAt">
      Last: <span x-text="formatRelative(liveStackStatus.triggers.lastRefocusAt)"></span>
      (HFR <span x-text="liveStackStatus.triggers.lastRefocusHfr?.toFixed(2)"></span>)
    </p>
  </fieldset>

  <fieldset>
    <legend>Auto re-center</legend>
    <label><input type="checkbox" x-model="liveStackTriggers.recenterEnabled" @change="saveLiveStackTriggers()"> Enabled</label>
    <label>Every <input type="number" x-model.number="liveStackTriggers.recenterEveryNFrames" min="0"> frames</label>
    <label>Every <input type="number" x-model.number="liveStackTriggers.recenterEveryMinutes" min="0"> minutes</label>
    <label>Drift ≥ <input type="number" x-model.number="liveStackTriggers.recenterDriftArcsec" min="0"> arcsec
      <span class="hint">⚠ Costs a plate-solve per frame</span>
    </label>
    <label>Tolerance <input type="number" x-model.number="liveStackTriggers.recenterToleranceArcsec"> arcsec</label>
    <button class="btn btn-sm" @click="recenterNow()" :disabled="!liveStackStatus.triggers?.referenceSolved">▶ Now</button>
    <p x-show="!liveStackStatus.triggers?.referenceSolved" class="text-warn">
      Reference not established (waiting for first-frame solve).
    </p>
    <p x-show="liveStackStatus.triggers?.referenceSolved">
      Reference: <span x-text="formatRaDec(liveStackStatus.triggers.referenceRaHours, liveStackStatus.triggers.referenceDecDeg)"></span>
    </p>
  </fieldset>

  <p x-show="liveStackStatus.triggers?.isExecuting" class="text-warn">
    ⚙ Executing <span x-text="liveStackStatus.triggers.executingKind"></span>… captures paused.
  </p>
</details>
```

JS:
- `liveStackTriggers` state (mirror of the persisted settings)
- WS absorbs the `liveStackStatus.triggers` snapshot
- `saveLiveStackTriggers()`, debounced PUT
- `refocusNow()` / `recenterNow()`, POST to the endpoints

## Phases (separate commits)

1. **LSTR-1**: `LiveStackingService.FrameIntegrated` event + per-frame
   HFR computation. Tests: HFR median calc, multi-subscriber fan-out.
2. **LSTR-2**: `LiveStackTriggers` DTO + persist on `EquipmentProfile`.
   EquipmentEndpoints PUT accepts the field.
3. **LSTR-3**: `LiveStackTriggersService`, subscriber + trigger gates +
   reference solve + execute pipelines. Tests: gate logic (frame/time/
   temp/HFR thresholds), reentry guard, status snapshot.
4. **LSTR-4**: `/api/livestack/triggers/*` endpoints + WS payload
   extension.
5. **LSTR-5**: UI `<details>` panel + JS state/methods.
6. **LSTR-6**: README section + end-to-end verification.

## Files to create

- `src/NINA.Headless/Services/LiveStackTriggersService.cs`
- `tests/NINA.Headless.Test/LiveStackTriggersServiceTests.cs`

## Files to modify

- `src/NINA.Headless/Services/LiveStackingService.cs`, event + HFR
  + per-frame HFR via the already-instantiated `StarDetector`
- `src/NINA.Headless/Services/ProfileService.cs`, `LiveStackTriggers`
  field on `EquipmentProfile` + safe migration (default = new())
- `src/NINA.Headless/Endpoints/EquipmentEndpoints.cs`, PUT accepts the field
- `src/NINA.Headless/Endpoints/LiveStackEndpoints.cs`, 4 new endpoints
- `src/NINA.Headless/Program.cs`, register singleton + eager-resolve
- `src/NINA.Headless/WebSocket/StatusStreamHandler.cs`, extend the
  liveStack block with a `triggers` sub-object
- `src/NINA.Headless/wwwroot/index.html`, `<details>` panel in the LIVE tab
- `src/NINA.Headless/wwwroot/js/app.js`, state + methods
- `src/NINA.Headless/wwwroot/css/app.css`, `.livestack-triggers` styling
- `README.md`, "Live stacking triggers" section

## Reuse of existing code

- `Services/LiveStackingService.cs`, frame entry point + StarDetector
  already instantiated (HFR comes for free)
- `Services/AutoFocusService.cs`, `Start(AutoFocusRequest)` + `State`
  polling (PH2X-4 pattern)
- `Services/SlewCenterService.cs`, `StartJob(ra, dec, tolerance)` +
  job polling
- `Services/PlateSolveService.cs`, `SolveAsync(IImageData)` for
  reference + drift
- `Services/EquipmentManager.cs`, `.Camera.Temperature` for the
  thermal trigger
- `Services/ProfileService.cs`, `EquipmentProfileActivated` event
  (PH2X-2) to reload triggers on rig switch
- `WebSocket/StatusStreamHandler.cs`, guider/cameraStream sub-objects
  pattern; same 1Hz cadence
- Frontend `<details>` + chained POST pattern: existing dither /
  meridian flip / smart-calibrate UIs

## End-to-end verification

1. **Build + tests**: current 445 + ~12 new = ~457 green
2. **Refocus by frame count**:
   - Live stack ON + sequence running + AF target set
   - Triggers: refocus every 5 frames
   - After frame 5 → AF fires (visible on /ws/status), sequence pauses,
     AF completes, sequence resumes from frame 6
   - `lastRefocusFrame=5` in the UI
3. **Refocus by HFR**:
   - Set refocus HFR ≥ 30% above last focus
   - Manually defocus (move focuser ±200 steps)
   - HFR rises on the next frame → AF fires
4. **Refocus by temperature**:
   - Set ΔT ≥ 1.0°C
   - Turn cooler on after 5 frames; when temp drops 1°C → AF fires
5. **Recenter by frame count + reference**:
   - Triggers: recenter every 20 frames, reference solved automatically
   - UI shows "Reference: 23h 14m, +12° 34'" after the first solve
   - At frame 20 → slewcenter fires, plate solve shows drift, slew
     corrects, frame 21 aligns again
6. **Recenter by drift**:
   - Triggers: drift ≥ 60 arcsec
   - Manually shift the mount 90" → next frame solve detects → recenter
7. **Manual fire**:
   - "▶ Now" buttons fire ignoring gates (refocus only needs the
     focuser connected; recenter needs the reference solved)
8. **Mutex**:
   - While AF is running, a second frame arrives → trigger eval is
     skipped (`_isExecuting=true`)
9. **Cross-rig**:
   - Switch to a rig with different triggers → reloads settings via
     the event hook

## Compatibility notes

- **Performance**: drift trigger does a plate solve per frame, ~3-10s
  on ASTAP on an RPi 4. Document; default OFF.
- **Concurrency**: the frame handler is sync-await; it blocks the
  capture chain while a trigger executes. That's the desired behavior
  (don't capture while the focuser is moving or the mount is slewing).
- **Reentry**: the `_isExecuting` guard prevents two AFs or AF+recenter
  simultaneously. Triggers accumulated during execution are dropped
  (not queued, running two AFs back-to-back doesn't make sense).
- **Reset semantics**: `LiveStackingService.Reset()` should also reset
  the trigger state (last* timestamps, reference), add
  `_triggers.Reset()` to Reset.
- **Without live stack running**: triggers are dormant (no frame
  event to fire). Manual fire still works.

---

# Previous plan: VIDEO tab, planetary capture + processing (lucky imaging) + slew preview

> Previous plan (PHD2 integration with xpra) preserved below starting at `# Previous plan: Deep integration with PHD2`.

## Context

The video stream layer we just implemented (`CameraStreamService`, native `CCD_VIDEO_STREAM` mode or fallback loop) delivers continuous frames in the browser. What's missing is the classic use case most planet shooters want: **record the stream to a file + process via lucky imaging** (RegiStax / AutoStakkert! pipeline). All of this only makes sense if the user can also see the sky in real time during slews, to confirm framing, that's the third ask: **camera preview on the SKY tab while the mount is slewing**, automatically, but only when no image capture is in progress (the camera can't be in two places at once).

Decisions confirmed with the user:
- **Format**: SER only (planetary astrophotography standard, compatible with AutoStakkert/RegiStax/PIPP, 178-byte header + lossless raw frames)
- **Quality metric**: Laplacian variance (classic sharpness, robust, fast, works on any planet)
- **Slew preview**: default ON (auto-on when mount.slewing && no active capture; toggle to disable)
- **Stack pipeline**: basic for this first iteration, rank by quality, top-X%, alignment via brightest-pixel centroid, mean stack. Sigma rejection + median + wavelets are follow-up work.

## Architecture

Three blocks:

### 1. Capture, record stream to SER

`Services/Planetary/SerFileWriter.cs`, implements the [SER spec v3](http://www.grischa-hahn.homepage.t-online.de/astro/ser/) (fixed 178-byte header + raw frames + optional timestamp trailer).

```csharp
public class SerFileWriter : IDisposable {
    public SerFileWriter(string path, int width, int height, SerColorMode colorMode, int bitDepth, string observer = "Polaris");
    public void WriteFrame(byte[] rawBytes, DateTime utc);  // uint8 ou uint16 raw
    public int FrameCount { get; }
    public void Dispose();   // grava trailer de timestamps + fecha
}

public enum SerColorMode { Mono = 0, BayerRGGB = 8, BayerGRBG = 9, BayerGBRG = 10, BayerBGGR = 11, Rgb = 100, Bgr = 101 }
```

`Services/Planetary/VideoRecordingService.cs`, singleton, subscribes to `CameraStreamService` frames while recording is active:

```csharp
public class VideoRecordingService {
    bool IsRecording { get; }
    string? OutputPath { get; }
    int FrameCount { get; }
    long BytesWritten { get; }
    TimeSpan Duration { get; }
    int DroppedFrames { get; }

    void Start(RecordingConfig cfg);
    Task StopAsync();
}

public record RecordingConfig(
    string TargetName,            // pasta: {ImageOutputDir}/planetary/{TargetName}/{timestamp}.ser
    int? MaxFrames = null,        // null = recorde até Stop
    TimeSpan? MaxDuration = null,
    SerColorMode? ColorMode = null);  // null = auto-detectar via camera Bayer pattern
```

Subscribe path: calls `CameraStreamService.SubscribeFrames(handler)` (need to add that API to CameraStreamService, today it's only an internal fan-out). Each frame: serialize uint16 array → `SerFileWriter.WriteFrame`.

### 2. Processing, lucky imaging pipeline

`Services/Planetary/SerFileReader.cs`, opens SER, exposes `int FrameCount`, `(byte[] data, DateTime t) ReadFrame(int index)`. Memory-mapped for performance on large files (1000+ frames at 1.5 MB each = ~1.5 GB typical).

`Services/Planetary/FrameQualityAnalyzer.cs`, Laplacian metric:

```csharp
public static class FrameQualityAnalyzer {
    /// <summary>Variância do Laplaciano (3x3 kernel) sobre a região central
    /// do frame. Métrica clássica de sharpness, alto = nítido, baixo = borrado.</summary>
    public static double LaplacianVariance(ushort[] pixels, int width, int height, int? roiSize = null);
}
```

Kernel:
```
 0 -1  0
-1  4 -1
 0 -1  0
```

`Services/Planetary/PlanetaryStackerService.cs`, job-based orchestrator (same pattern as PHD2CalibrationOrchestrator):

```csharp
public class PlanetaryStackerService {
    StackJob StartJob(StackConfig cfg);
    StackJob? GetJob(string id);
    void Abort(string id);
}

public record StackConfig(
    string SerPath,
    double KeepPercent = 50,      // top 50% por qualidade
    string OutputDir,             // {ImageOutputDir}/planetary/{target}/stacked/
    string OutputName = "stack",
    string OutputFormat = "fits"  // fits | png | tif
);
```

State machine (phases observable via `/ws/status` → `videoStack`):
1. **Reading**, open SER + list frames
2. **Analyzing**, Laplacian variance per frame (parallelized, `Parallel.ForEach`)
3. **Ranking**, sort desc, take top KeepPercent%
4. **Aligning**, for each selected frame: find centroid (brightest 5x5 region, with sub-pixel refinement via parabolic fit), compute shift relative to the first frame
5. **Stacking**, mean per-pixel: `result[xy] = sum(frame[xy + shift]) / N`. Output bit depth 16-bit (summing 100 frames of 8-bit doesn't overflow 16-bit).
6. **Writing**, FITS via the existing `FITSWriter`, PNG/TIF via SkiaSharp (already in the project)
7. **Ok** / **Fail**

### 3. Slew preview on the SKY tab

`Services/SlewPreviewService.cs`, `BackgroundService` that monitors state every 1s:

```csharp
public class SlewPreviewService : BackgroundService {
    bool Enabled { get; set; } = true;        // Toggle persisted via settings
    bool IsPreviewActive { get; }              // current state
    bool LastDecision_Slewing { get; }         // diagnostic
    bool LastDecision_CaptureIdle { get; }     // diagnostic
}
```

Loop:
1. If `!Enabled` → make sure preview is stopped, sleep
2. Evaluate `MountIsSlewing` (via `equip.Telescope.IsSlewing` or `equip.Telescope.State`)
3. Evaluate `CaptureIdle = !sequence.IsRunning && !preview.busy && !autofocus.running && !flatwizard.running && !cameraStream.IsRunning && !videoRecording.IsRecording && !meridianFlip.IsFlipping`
4. If `MountIsSlewing && CaptureIdle && !IsPreviewActive` → `CameraStreamService.Start({ exposure: 0.1, gain: rig.DefaultGain })`. Mark `IsPreviewActive = true`.
5. If `IsPreviewActive && (!MountIsSlewing || !CaptureIdle)` → `CameraStreamService.StopAsync()`. Mark false.

`CameraStreamService` needs to gain a `StartedBySlewPreview` flag in its state to distinguish "user clicked Stream" vs "Polaris auto-started via slew". When the auto-stop fires, it doesn't interfere if the user started it.

UI on the SKY tab: **inset card** in the bottom-right of the map, showing a small version of the `liveCanvas` (same bitmap since `_mirrorLiveToPreviewCanvas` already fans out to any canvas that exists; just add `slewPreviewCanvas` to the list). Appears with a fade-in when preview activates, fade-out when it stops. Small label "📷 Slewing, live view".

## Phases (separate commits)

1. **VIDPL-1**: `SerFileWriter` + `SerFileReader` + tests (header parsing, frame round-trip)
2. **VIDPL-2**: `CameraStreamService.SubscribeFrames(handler)` public (today it's private/internal)
3. **VIDPL-3**: `VideoRecordingService` (subscribes to the stream + writes SER) + endpoints `/api/video/record/start|stop|status`
4. **VIDPL-4**: ROI/subframe extensions on `ICamera` + `IndiCamera.SetSubframeAsync(x, y, w, h)` (CCD_FRAME)
5. **VIDPL-5**: **VIDEO sidebar tab** + Capture sub-tab UI (video preview canvas + gain/exposure/binning/ROI/format/record/status controls)
6. **VIDPL-6**: `FrameQualityAnalyzer.LaplacianVariance` + tests (golden frames with known sharpness)
7. **VIDPL-7**: `PlanetaryStackerService` (rank → align → stack) + endpoints `/api/video/stack/start|get|abort`
8. **VIDPL-8**: Process sub-tab UI (file picker via FileBrowserService, quality histogram chart, top-X% slider, stack button + progress, embedded result preview)
9. **VIDPL-9**: `SlewPreviewService` (BackgroundService + capture-idle aggregator) + settings toggle
10. **VIDPL-10**: SKY tab inset video frame + auto-detect mode badge
11. **VIDPL-11**: WS payload extensions + Tests + Docs

## Files to create

- `src/NINA.Headless/Services/Planetary/SerFileWriter.cs`
- `src/NINA.Headless/Services/Planetary/SerFileReader.cs`
- `src/NINA.Headless/Services/Planetary/VideoRecordingService.cs`
- `src/NINA.Headless/Services/Planetary/FrameQualityAnalyzer.cs`
- `src/NINA.Headless/Services/Planetary/PlanetaryStackerService.cs`
- `src/NINA.Headless/Services/Planetary/CentroidAligner.cs` (helper: brightest-pixel centroid + parabolic sub-pixel refinement)
- `src/NINA.Headless/Services/SlewPreviewService.cs`
- `src/NINA.Headless/Endpoints/VideoEndpoints.cs`
- `tests/NINA.Headless.Test/Planetary/SerFileWriterReaderTests.cs`
- `tests/NINA.Headless.Test/Planetary/FrameQualityAnalyzerTests.cs`
- `tests/NINA.Headless.Test/Planetary/CentroidAlignerTests.cs`
- `tests/NINA.Headless.Test/Planetary/PlanetaryStackerServiceTests.cs`
- `tests/NINA.Headless.Test/SlewPreviewServiceTests.cs`

## Files to modify

- `src/NINA.Image.Portable/Interfaces/ICamera.cs`, add `SetSubframeAsync(int x, int y, int w, int h)` default no-op + `Capabilities.SupportsRoi` (already exists but not exposed via a setter method)
- `src/NINA.INDI/Devices/IndiCamera.cs`, `SetSubframeAsync` implements via `CCD_FRAME` (X/Y/WIDTH/HEIGHT)
- `src/NINA.Headless/Services/CameraStreamService.cs`, expose `SubscribeFrames(handler)` publicly; add a `StartedBySlewPreview` flag to state to distinguish auto vs manual
- `src/NINA.Headless/Program.cs`, register `VideoRecordingService`, `PlanetaryStackerService`, `SlewPreviewService` (singleton + hosted service)
- `src/NINA.Headless/WebSocket/StatusStreamHandler.cs`, extend payload with `videoRecording`, `videoStack`, `slewPreview` sub-objects
- `src/NINA.Headless/wwwroot/index.html`, new VIDEO sidebar button + tab panel with Capture/Process tabview + slew preview inset on SKY tab
- `src/NINA.Headless/wwwroot/js/app.js`, state `video`, `videoStack`, `slewPreview`, methods `videoStartRecord/stopRecord`, `videoLoadFile`, `videoStartStack`, etc.; absorption of the new sub-objects in the WS handler; multi-canvas mirror includes `slewPreviewCanvas`
- `src/NINA.Headless/wwwroot/css/app.css`, `.video-tab`, `.video-stats`, `.video-quality-chart`, `.slew-preview-inset`
- `README.md`, VIDEO section + planetary + slew preview

## Reuse of existing code

- `src/NINA.Headless/Services/CameraStreamService.cs` (commit 334b26c), base for recording, frames come from here
- `src/NINA.Image.Portable/FileFormat/FITS/FITSWriter.cs`, output of the final stack
- `src/NINA.Headless/Services/Studio/FitsThumbnailer.cs` (if it exists) or SkiaSharp directly, PNG/TIF output
- `src/NINA.Headless/Services/FileBrowserService.cs`, Process tab uses it to pick an existing SER
- `src/NINA.Headless/Services/ImageWriterService.cs` `BuildSubDir`, extend with a `PLANETARY` case → `{rig}/planetary/{target}/`
- `src/NINA.Headless/Services/EquipmentManager.cs` `.Telescope.IsSlewing` (need to expose if not already), SlewPreviewService monitors
- `src/NINA.Headless/Services/SequenceEngine.cs` `.GetStatus().State`, capture-idle aggregator checks
- `src/NINA.Headless/Services/AutoFocusService.cs` `.State`, same
- Long-running job + WS broadcast pattern: `PHD2CalibrationOrchestrator` + `AutoFocusService`, copy to `PlanetaryStackerService`
- BackgroundService loop + settings toggle pattern: `PHD2AutoStartService`, `MdnsService`, `Phd2GuiSessionService`, copy to `SlewPreviewService`
- Frontend multi-canvas mirror in `app.js _mirrorLiveToPreviewCanvas`, add `slewPreviewCanvas` to the list

## End-to-end verification

1. **Build + tests**: `dotnet build` + `dotnet test`, current 425 + ~25 new = ~450 green
2. **Capture happy path**:
   - Planetary camera (ZWO ASI224) connected via INDI, supports CCD_VIDEO_STREAM
   - Open VIDEO tab → Capture sub-tab
   - Set exposure 5ms, gain 350, ROI 800×600
   - Click Record → SER file appears at `{ImageOutputDir}/planetary/jupiter/2026-05-22T22-30-15.ser`
   - Counter: frames, bytes, duration increase in real time via WS
   - Click Stop after 60s → SER file closed with the timestamp trailer
   - Verify: file opens in AutoStakkert! / PIPP / SER Player
3. **Process happy path**:
   - VIDEO tab → Process sub-tab
   - File picker (via FileBrowserService) → pick the captured SER
   - Click Analyze → quality histogram appears (~2-5s for 1000 frames on an RPi 4)
   - Top-X% slider → preview shows how many frames remain
   - Click Stack → progress bar (Reading → Analyzing → Ranking → Aligning → Stacking → Writing)
   - Output FITS appears at `planetary/jupiter/stacked/stack_2026-05-22T22-35-00.fits`
   - Stacked image visibly sharper than individual frames
4. **Slew preview happy path**:
   - Settings toggle "Slew preview" ON (default)
   - Mount connected, idle
   - SKY tab open
   - Click "Go to" on some DSO → mount starts slew
   - Inset card appears in the bottom-right showing the live stream from the camera
   - Slew ends → inset fades out, stream stops automatically
5. **Slew preview mutex**:
   - Sequence running → slew during meridian flip → inset does NOT appear (capture-idle = false)
   - Manually click Stream on the PREVIEW tab → SKY inset disappears if already active (auto-yields to the user)
6. **Toggle OFF**:
   - Settings turns "Slew preview" off → all SlewPreviewService activity stops
7. **Cross-platform**:
   - Linux RPi: everything works
   - Windows: everything works EXCEPT native CCD_VIDEO_STREAM (falls back to loop mode automatically, lower fps but still recording SER)

## Compatibility notes

- **SER format**: public domain, spec available. No licenses.
- **Laplacian filter**: trivial math, no deps.
- **Stack output FITS**: existing pipeline.
- **Slew preview bandwidth**: 2-8 Mbps when active, 0 when idle. User on mobile can toggle off.
- **RPi 4 stacking 1000 frames of 800×600**: ~30s typical (single-threaded), ~10s with Parallel.ForEach. Acceptable.
- **Storage**: 60s of video at 30 fps with 800×600×uint16 = 1.7 GB. README should warn.

---
# Previous plan: Deep PHD2 integration, xpra embed + RPC orchestration

> History of earlier plans (including the first version of this plan with native storage-write) preserved below starting at `# Previous plan: RIGS tab`.

## Context

The user runs Polaris on a mini-PC or Raspberry Pi in an observatory / remote setup **with no screen access** to the PHD2 computer. Today, Polaris already manages ~80% of PHD2 via JSON-RPC (25 wrapped methods, process launch/shutdown, install detection, 26 REST endpoints, rich UI on the GUIDE tab). The gap: PHD2's JSON-RPC **does not expose** profile creation, equipment picker, Brain dialog, Guiding Assistant, dark library, and several advanced setup screens.

**Change of direction (vs. the first version of this plan):** the first version proposed reverse-engineering PHD2's native storage (registry on Windows, INI on Linux, plist on macOS) with backup/rollback. Additional research + a direct recommendation from [PHD2 issue #683](https://github.com/OpenPHDGuiding/phd2/issues/683#issuecomment-3707310067) led to a more robust approach: **host an xpra session running PHD2 with a detachable Xorg-dummy** and **embed the xpra HTML5 client (port 10000) in a tab inside the GUIDE panel**. PHD2's native GUI shows up inside the Polaris UI, including the profile Wizard, Brain, manual calibration, GA, dark library, everything. Zero code to "create a profile": it uses PHD2's own Wizard.

**Why xpra > storage-write:**
- Zero schema reverse-engineering (resilient to PHD2 updates)
- Zero risk of corrupting config (PHD2 manages its own state)
- Workflows are battle-tested (Wizard, validation, GA all native)
- ~80% less code to maintain
- New PHD2 features become available automatically

**Accepted trade-offs:**
- Linux-only on the server side (xpra on Windows is fragile; see fallback below)
- ~150MB RAM with the xpra session running + 2-8 Mbps bandwidth when the user opens the panel
- Sluggish on RPi 4 during heavy use (but it's a setup tool, not for live guiding)

**Decisions confirmed with the user:**
- **Fully replace** the storage-write approach with xpra-embed.
- **Windows**: document the limitation. xpra on Windows is fragile; users on a Windows mini-PC usually have screen access. Polaris on Windows offers only the JSON-RPC controls + smart calibrate + presets, no "PHD2 GUI" tab.
- **xpra lifecycle**: toggle in Settings ("Pre-start PHD2 GUI session"). Default OFF (lazy, start on demand). Power users turn it on for instant opening.
- **Embedding**: **tabstrip on the existing GUIDE panel**, Tab 1 "Control" (current JSON-RPC UI + smart-calibrate + presets) | Tab 2 "PHD2 GUI" (iframe via same-origin reverse-proxy to avoid sessionStorage issues).
- **rig↔profile sync**: 1:1 by name. Polaris only calls `set_profile` via RPC (does not create); if no profile with the rig's name exists, a banner points to Tab 2 with "Create profile 'X' in the PHD2 wizard".
- **Calibration**: orchestrate a smart workflow (compute step from pixel scale, optionally slew to the celestial equator, trigger via RPC, monitor, validate orthogonality).
- **Algorithm tuning**: simple presets (Default / Reactive / Smooth) + Advanced disclosure with individual knobs. Power users can also jump to PHD2's Brain on Tab 2.

## Architecture

Five layers, executable in independent phases:

### 1. New PHD2Client wrappers (`Services/PHD2Client.cs`)

Add 4 methods on the existing client, same pattern as the 25 wrappers already present:

```csharp
public Task SetAlgoParamAsync(string axis, string name, double value, CancellationToken ct = default)
    => CallAsync("set_algo_param", new object[] { axis, name, value }, ct: ct);
public Task<double?> GetAlgoParamAsync(string axis, string name, CancellationToken ct = default);
public Task<List<string>> GetAlgoParamNamesAsync(string axis, CancellationToken ct = default);
public Task FlipCalibrationAsync(CancellationToken ct = default) => CallAsync("flip_calibration", ct: ct);
```

Axis accepts `"ra"`, `"dec"`, `"Mount"` (scope depending on the method, PHD2 RPC docs). Handling: if PHD2 v2.6 doesn't have the requested param, catch the RPC error + return null/empty (don't throw).

### 2. ProfileService, new fields + event (`Services/ProfileService.cs`)

Add to `EquipmentProfile`:
- `int? PHD2ProfileId`, cache of the PHD2 id after first name match
- `string PHD2AlgoPreset`, default `"Default"`; values `"Default" | "Reactive" | "Smooth" | "Custom"`
- `int? PHD2CalibrationStepMsOverride`, null = auto-compute
- `bool PHD2AutoSyncOnRigSwitch`, default `true`; controls whether rig-switch triggers a PHD2 profile switch
- `Dictionary<string,double> PHD2CustomAlgoParams`, individual overrides (knobs edited via Advanced disclosure)

Migration safe: defaults null/empty/false.

Add an event on `ProfileService`:
```csharp
public event Action<EquipmentProfile>? EquipmentProfileActivated;
```
Fired inside `ActivateEquipmentProfile` after a successful save.

### 3. PHD2ProfileSyncService (`Services/PHD2ProfileSyncService.cs`, NEW)

Singleton. Responds to the ProfileService event + exposes explicit APIs for manual sync.

```csharp
public class PHD2ProfileSyncService {
    Task<SyncResult> SyncRigToProfileAsync(EquipmentProfile rig, CancellationToken ct);
    SyncStatus CurrentStatus { get; }
    event Action<SyncStatus>? StatusChanged;
}
public record SyncResult(bool Ok, string? Error, int? ProfileId, bool ProfileMissing, List<string> Warnings);
public record SyncStatus(string RigId, string Phase, string? Error, DateTime At);
```

Algorithm:
1. If `!phd2.IsConnected` → return warning, no-op
2. `GetProfilesAsync` via RPC; match by name (case-insensitive) against `rig.Name`
3. If it doesn't exist → `SyncResult { Ok: false, ProfileMissing: true, Error: "Create profile in PHD2 GUI tab" }` + notify UI
4. If it exists and id ≠ current → `SetProfileAsync(id)` (already exists, performs an internal equipment disconnect)
5. Apply the algo preset via `set_algo_param` (silent skip if the param doesn't exist in the current version)
6. Apply per-rig overrides (`PHD2CustomAlgoParams`, `PHD2CalibrationStepMsOverride` if set)
7. Return Ok

Constructor wire: `_profiles.EquipmentProfileActivated += rig => { if (rig.PHD2AutoSyncOnRigSwitch) _ = SyncRigToProfileAsync(rig, default); };`

### 4. PHD2CalibrationOrchestrator (`Services/PHD2CalibrationOrchestrator.cs`, NEW)

Singleton. 9-phase state machine, progress broadcast via `/ws/status` in the `guider.calibrateJob` sub-object.

```csharp
public record SmartCalibrateOptions(
    bool SlewToEquator = false,
    double? TargetRaHours = null,        // null → current LST (meridian)
    double TargetDecDeg = 0.0,           // celestial equator
    int? ExposureMsOverride = null,
    int CalibrationStepMsOverride = 0,   // 0 → auto-compute
    int TimeoutSeconds = 240);

public record SmartCalibrateResult(
    bool Ok, string Phase, string? Error,
    int CalibrationStepMs, double PixelScale, CalibrationData? Calibration);
```

Phases:
1. **Preflight**: PHD2 connected? Camera/Mount connected in PHD2 (`GetCurrentEquipmentAsync`)? Sequence not running? Otherwise fail-fast with an actionable message.
2. **PixelScale**: read `phd2.PixelScale`; if 0 reissue `get_pixel_scale`; fallback compute from `pixelSize_um * 206.265 / guiderFL_mm` (using `rig.GuiderFocalLengthMm`).
3. **Compute step**: `step_ms = round(25 * pxScale / guideRate * 1000)`. `guideRate` from `_equip.Telescope?.GuideRateRightAscension` if non-zero, otherwise `7.5"/s` (0.5x sidereal). Cap `[250, 3000]` ms. Honors `CalibrationStepMsOverride`.
4. **Slew (optional)**: if `SlewToEquator`, compute RA = current LST and Dec = 0, call `_slewCenter.StartJob`, await terminal state. Skip with a warning if dome is connected and not slaved.
5. **Apply step**: `set_algo_param("Mount", "calibration_step", step_ms)`.
6. **Clear + find + guide**: `ClearCalibrationAsync` → `SetExposureMsAsync(2000ms)` → `LoopAsync` → settle 3s → `AutoSelectStarAsync` → `StartGuidingAsync(recalibrate: true)`.
7. **Monitor**: subscribe to `phd2.AppStateChanged` + one-shot TCS; resolves on `Guiding` (OK) / `Stopped` after `CalibrationFailed` event (FAIL) / timeout. Capture via `phd2.Alert`.
8. **Validate**: read `phd2.Calibration` (already populated by the `CalibrationComplete` handler in `PHD2Client.cs:236-246`); reject if `|XAngle − YAngle|` deviates from 90° by >20° (orthogonality fail) or `XRate < 1e-5` (no movement).
9. Return result.

### 5. PHD2AlgoPresets (`Services/PHD2AlgoPresets.cs`, NEW)

Static table of concrete presets:

| Preset | RA (Hysteresis algo) | DEC (Resist Switch algo) | When to use |
|---|---|---|---|
| **Default** | hysteresis=0.10, aggressiveness=0.70, minMove=0.15 | aggressiveness=0.65, minMove=0.15, fastSwitch=true | PHD2 stock; balanced |
| **Reactive** | hysteresis=0.05, aggressiveness=0.90, minMove=0.10 | aggressiveness=0.80, minMove=0.10, fastSwitch=true | Short FL, good seeing, fast mount. May overshoot |
| **Smooth** | hysteresis=0.25, aggressiveness=0.50, minMove=0.20 | aggressiveness=0.45, minMove=0.20, fastSwitch=false | Long FL or wind/poor seeing |

Apply via `SetAlgoParamAsync("ra", "Hysteresis", 0.05)` etc. Silent skip (warning log) if `GetAlgoParamNamesAsync` doesn't list the param, the user picked a different algorithm in PHD2's Brain; respect it.

Custom preset = persist a Dictionary in `rig.PHD2CustomAlgoParams`. Advanced UI reads `GetAlgoParamNamesAsync` + per-name `GetAlgoParamAsync` to show every available knob with editable inputs.

### 6. Phd2GuiSessionService (`Services/Phd2GuiSessionService.cs`, NEW), Linux only

Singleton + hosted service. Manages the xpra session lifecycle:

```csharp
public class Phd2GuiSessionService : BackgroundService {
    public bool XpraInstalled { get; private set; }
    public string? XpraVersion { get; private set; }
    public bool SessionRunning { get; private set; }
    public int DisplayNumber { get; }       // default :100
    public int BindPort { get; }            // default 14600 (localhost only)

    Task<bool> StartSessionAsync(CancellationToken ct);
    Task<bool> StopSessionAsync(CancellationToken ct);
    Task<bool> RestartSessionAsync(CancellationToken ct);
}
```

Detection via `which xpra` + parse of `xpra --version`. On non-Linux, marks `XpraInstalled = false` and disables related endpoints (they return 501 Not Implemented).

Start command:
```bash
xpra start :100 \
    --start=phd2 \
    --html=on \
    --bind-tcp=127.0.0.1:14600 \
    --daemon=yes \
    --systemd-run=no \
    --no-pulseaudio
```

Stop command: `xpra stop :100`.

Health: poll TCP `127.0.0.1:14600` with a 500ms timeout. In `ExecuteAsync` (BackgroundService), if config `Phd2Gui:AutoStart=true` AND XpraInstalled AND PHD2 installed, fire `StartSessionAsync` at startup (after the 3s stagger so PHD2AutoStartService runs first). Otherwise: idle, awaiting an explicit call via endpoint.

### 7. Reverse proxy `/phd2-gui/*` → `ws://127.0.0.1:14600/*`

ASP.NET Core minimal proxy via [`Microsoft.AspNetCore.Http.HttpForwarder`](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/http-requests#yarp-direct-forwarding) (from the `Yarp.ReverseProxy` package or via raw `IHttpClientFactory` + WebSocket upgrade middleware).

Why an internal reverse-proxy instead of an HTML5 client connecting directly to port 14600:
- **Same-origin**: avoids the `sessionStorage` issues in iframes that the research identified
- **Auth piggyback**: port 14600 is bound to `127.0.0.1` only; all interaction goes through Polaris's listener which already has the Relay auth system
- **Path-based namespacing**: `/phd2-gui/` is obvious in the logs

Implementation: ~60 lines in `WebSocket/Phd2GuiProxyMiddleware.cs` (handle WebSocket upgrade + HTTP forward; the `Yarp.ReverseProxy` lib does this out-of-the-box in ~10 lines of config).

### 8. New endpoints (`Endpoints/GuiderEndpoints.cs`)

- `POST /api/guider/profile/sync`, body `{rigId?}`; forces sync (uses active rig if omitted)
- `POST /api/guider/calibrate/smart`, body = `SmartCalibrateOptions`; returns `{jobId}`
- `GET /api/guider/calibrate/smart/{jobId}`, poll status (also via /ws/status)
- `POST /api/guider/calibrate/smart/{jobId}/abort`, abort
- `GET /api/guider/algo-presets`, returns the preset table (name + description + values)
- `POST /api/guider/algo-preset/{name}`, applies the preset live + persists in `rig.PHD2AlgoPreset`
- `GET /api/guider/algo-params`, reads live: for each axis (`ra`, `dec`), `get_algo_param_names` + per-name `get_algo_param` value
- `PUT /api/guider/algo-params`, body `{axis, name, value}`; applies via `set_algo_param` + persists in `rig.PHD2CustomAlgoParams`
- `GET /api/guider/gui-session/status`, `{xpraInstalled, xpraVersion, sessionRunning, displayNumber, bindPort, autoStart, os, downloadHint}`
- `POST /api/guider/gui-session/start`, invokes `StartSessionAsync`
- `POST /api/guider/gui-session/stop`, invokes `StopSessionAsync`
- `POST /api/guider/gui-session/restart`, sequential stop + start
- `PUT /api/guider/gui-session/auto-start`, body `{enabled}` → persists in settings

### 9. WebSocket status, extend the `guider` block

```jsonc
{
  "guider": {
    "...existing fields...": "...",
    "profileSync": { "phase": "idle|switching|applying-preset|ok|missing-profile|error",
                     "rigId": "...", "error": null, "at": "iso8601" },
    "calibrateJob": { "phase": "preflight|pixel-scale|computing|slewing|applying|calibrating|validating|ok|fail",
                      "jobId": "...", "stepMs": 0, "pixelScale": 0, "ok": null, "error": null },
    "guiSession": { "available": true, "running": true, "xpraVersion": "6.0", "port": 14600 }
  }
}
```

### 10. UI, tabstrip on the GUIDE panel + Settings toggle

**GUIDE panel** (`index.html` `x-show="tab === 'guide'"`): wrap the existing content in a tabstrip with 2 tabs:

- **Tab "Control"** (default), ALL the current content: connection panel, profile dropdown, exposure slider, dec mode, equipment connect, guiding controls, status chart, process launch/shutdown, install warning. Adds:
  - **Smart Calibrate button** with options modal (slew checkbox, step override input)
  - **Algo preset pill** showing the active preset (clickable → dropdown with Default/Reactive/Smooth/Custom)
  - **Advanced `<details>`** collapsed by default, opens a section with each knob from `get_algo_param_names` + a numeric input per knob, "Save as Custom" button
  - **Profile sync indicator** (green ✓ / yellow spinner / red ⚠ with error tooltip)

- **Tab "PHD2 GUI"** (new), conditional behavior:
  - If OS ≠ Linux: banner "Embedded PHD2 GUI requires Linux + xpra. On this OS, use PHD2's native window."
  - If Linux + xpra not detected: banner "Install xpra to enable: `sudo apt install xpra`" + doc link
  - If xpra detected + session not running: card "Start PHD2 GUI session" (big button, info "takes ~5-10s") + checkbox "Pre-start on Polaris boot"
  - If session running: iframe `src="/phd2-gui/"` + thin toolbar at the top (Stop session, Restart, fullscreen toggle, health indicator)

**Settings tab**: new section "PHD2 Embedded GUI":
- Checkbox "Pre-start PHD2 GUI session on Polaris boot" (default off), written to `Phd2Gui:AutoStart`
- Read-only display: detected xpra version, display number, port, last health check
- "Manual restart session" button

### 11. Sequence (separate commits)

1. **PH2X-1**: PHD2Client wrappers (`SetAlgoParamAsync` + 3 others) + `PHD2AlgoPresets` static table. Tests green.
2. **PH2X-2**: `EquipmentProfile` gets 5 new fields + migration + `EquipmentProfileActivated` event. EquipmentEndpoints PUT accepts the new fields.
3. **PH2X-3**: `PHD2ProfileSyncService` + 1 endpoint + UI sync indicator. Hook into ProfileService event.
4. **PH2X-4**: `PHD2CalibrationOrchestrator` + 3 endpoints + Smart Calibrate button on the Control tab.
5. **PH2X-5**: Algo preset UI (pill + Advanced disclosure) + 4 algo-params endpoints.
6. **PH2X-6**: `Phd2GuiSessionService` (BackgroundService + lifecycle) + 5 session-control endpoints. Linux detection + non-Linux fallback (501 + clear message).
7. **PH2X-7**: Reverse proxy `/phd2-gui/*` via Yarp.ReverseProxy or custom middleware + registration in Program.cs.
8. **PH2X-8**: UI tabstrip on the GUIDE panel, wrap existing content in the "Control" tab, add "PHD2 GUI" tab with iframe + conditional states (OS, install, session). Settings toggle.
9. **PH2X-9**: WebSocket payload extensions (profileSync + calibrateJob + guiSession sub-blocks).
10. **PH2X-10**: Tests, `PHD2ClientTests` (4 new wrappers), `PHD2ProfileSyncServiceTests`, `PHD2CalibrationOrchestratorTests`, `CalibrationStepCalculatorTests` (pure-function), `Phd2GuiSessionServiceTests` (mock process spawn).
11. **PH2X-11**: README section + `docs/phd2-gui-embedding.md` (step-by-step install procedure from the PHD2 maintainer's comment + Xorg-dummy troubleshooting + where to tweak `55_server_x11.conf`).

## Files to create

- `src/NINA.Headless/Services/PHD2AlgoPresets.cs`
- `src/NINA.Headless/Services/PHD2ProfileSyncService.cs`
- `src/NINA.Headless/Services/PHD2CalibrationOrchestrator.cs`
- `src/NINA.Headless/Services/Phd2GuiSessionService.cs`
- `src/NINA.Headless/WebSocket/Phd2GuiProxyMiddleware.cs` (or `Yarp.ReverseProxy` config + ~10 lines)
- `docs/phd2-gui-embedding.md` (xpra install procedure + Xorg-dummy config + troubleshooting)
- `tests/NINA.Headless.Test/PHD2AlgoPresetsTests.cs`
- `tests/NINA.Headless.Test/PHD2ProfileSyncServiceTests.cs`
- `tests/NINA.Headless.Test/PHD2CalibrationOrchestratorTests.cs`
- `tests/NINA.Headless.Test/CalibrationStepCalculatorTests.cs` (pure-function)
- `tests/NINA.Headless.Test/Phd2GuiSessionServiceTests.cs`

## Files to modify

- `src/NINA.Headless/Services/PHD2Client.cs`, 4 new wrappers (`SetAlgoParamAsync`, `GetAlgoParamAsync`, `GetAlgoParamNamesAsync`, `FlipCalibrationAsync`) ~30 lines
- `src/NINA.Headless/Services/ProfileService.cs`, 5 new fields on `EquipmentProfile` + `event Action<EquipmentProfile>? EquipmentProfileActivated` + raise in `ActivateEquipmentProfile`
- `src/NINA.Headless/Endpoints/EquipmentEndpoints.cs`, PUT /rigs/{id} accepts the new fields
- `src/NINA.Headless/Endpoints/GuiderEndpoints.cs`, 12 new endpoints
- `src/NINA.Headless/WebSocket/StatusStreamHandler.cs`, extend the `guider` block with 3 sub-objects (`profileSync`, `calibrateJob`, `guiSession`)
- `src/NINA.Headless/Program.cs`, register `PHD2ProfileSyncService`, `PHD2CalibrationOrchestrator`, `Phd2GuiSessionService` (singletons + hosted service for the last one); map reverse proxy `/phd2-gui/*`
- `src/NINA.Headless/wwwroot/index.html`, tabstrip wrapper on the GUIDE panel; new "PHD2 GUI" tab; new Smart Calibrate button + modal; algo preset pill + Advanced disclosure; profile sync indicator; Settings section "PHD2 Embedded GUI"
- `src/NINA.Headless/wwwroot/js/app.js`, state `phd2Sync`, `phd2Calibrate`, `phd2AlgoPresets`, `phd2AlgoParams`, `phd2GuiSession`; corresponding methods; tabstrip state `guideTab` ('control'|'gui')
- `src/NINA.Headless/wwwroot/css/app.css`, `.guide-tabstrip`, `.guide-tab`, `.phd2-gui-iframe`, `.phd2-gui-banner`, `.algo-preset-pill`, `.smart-calibrate-modal`, `.profile-sync-indicator`
- `src/NINA.Headless/NINA.Headless.csproj`, add `<PackageReference Include="Yarp.ReverseProxy" Version="2.*" />` (if going the YARP route)
- `README.md`, "PHD2 deep integration" section linking `docs/phd2-gui-embedding.md`
- `tests/NINA.Headless.Test/PHD2ClientTests.cs`, tests for the 4 new wrappers

## Reuse of existing code

- `src/NINA.Headless/Services/PHD2ProcessManager.cs:41,114,57` (`IsRunningAsync`, `ShutdownAsync`, `LaunchAsync`), coexists with `Phd2GuiSessionService`: ProcessManager remains the source of truth for the PHD2 process itself; GuiSessionService only manages the **xpra container** that hosts PHD2. In xpra mode, PHD2 is spawned as a child of xpra (`--start=phd2`), so `Phd2GuiSessionService` needs to signal ProcessManager "xpra mode active, I own the PHD2 PID".
- `src/NINA.Headless/Services/PHD2Client.cs`, 25 methods already wrapped; full reuse; only adds 4
- `src/NINA.Headless/Services/PHD2AutoStartService.cs`, coexists unchanged; auto-start is independent of the xpra session
- `src/NINA.Headless/Services/ProfileService.cs:184,311-314` (PHD2Host/Port/AutoStart fields), basis for the 5 new fields
- `src/NINA.Headless/Services/SlewCenterService.cs`, `StartJob` used by the calibration orchestrator when `SlewToEquator=true`. Extract an `ISlewCenterService` interface for mockability in tests.
- `src/NINA.Headless/Services/EquipmentManager.cs`, `.Telescope.GuideRateRightAscension` (if exposed) for the calibration step compute
- `src/NINA.Headless/Endpoints/GuiderEndpoints.cs`, 26 existing routes kept; 12 new ones added
- `src/NINA.Headless/WebSocket/StatusStreamHandler.cs:54-85`, existing guider block; just extended with 3 sub-objects
- `BackgroundService` pattern: copy from `PHD2AutoStartService.cs`, `MdnsService.cs` for `Phd2GuiSessionService`
- Long-running job + WS broadcast pattern: `AutoFocusService` + `MeridianFlipService`, copy for `PHD2CalibrationOrchestrator`
- Frontend tabstrip pattern: already exists in other smaller panels (e.g., Equipment source tabs INDI/Alpaca), reuse HTML/CSS

## End-to-end verification

1. **Build + tests**:
   - `dotnet build src/NINA.Headless` clean
   - `dotnet test tests/NINA.Headless.Test`, 387 current + ~20 new = ~407 green
2. **Smart calibration (any OS)**:
   - Mount connected + tracking + any target + guide camera connected in PHD2
   - Click Smart Calibrate on the Control tab → options modal → slew=on, step override=0
   - Polaris slews to the meridian at dec=0 (visible on the Sky tab + progress)
   - Computes step (e.g., pxScale 2.1"/px, guideRate 7.5"/s → 700ms)
   - Applies step via `set_algo_param`, clear calibration, find_star, guide
   - WS status shows phase by phase: "preflight" → "slewing" → "applying" → "calibrating" → "validating" → "ok"
   - Result: `XAngle/YAngle/XRate/YRate` displayed with orthogonality check
3. **Algo preset switch (any OS)**:
   - Click pill "Reactive" → `set_algo_param("ra", "Hysteresis", 0.05)` + 3 others → next GuideStep uses the new values (visible in the ring buffer)
   - Click "Custom" → edit individually in Advanced → "Save" persists in `rig.PHD2CustomAlgoParams`; swap rig + come back, custom values are restored
4. **Profile sync (any OS, PHD2 already has profiles created)**:
   - Activate rig A (with PHD2ProfileId already cached) → PHD2 status: AppState "Stopped" momentarily → reconnect → algo preset applied → WS payload shows `guider.profileSync.phase: "ok"`
   - Activate rig B whose name **does not match** any PHD2 profile → WS payload shows `guider.profileSync.phase: "missing-profile"`; UI displays banner "Profile 'B' not found in PHD2. Open the PHD2 GUI tab and create it via Wizard." with an "Open PHD2 GUI" button
5. **PHD2 GUI session lifecycle (Linux + xpra installed)**:
   - Settings toggle "Pre-start" OFF → first access to the "PHD2 GUI" tab shows "Start PHD2 GUI session" + estimate "~5-10s"
   - Click start → progress bar → iframe loads (10s) → native PHD2 GUI appears inside the tab
   - User can run Profile Wizard, create profile "Polaris-EQ6", close Wizard
   - Back to the Control tab → click profile dropdown → "Polaris-EQ6" shows up in the list → sync works
   - Click stop session → iframe turns back into "Start"
   - Toggle "Pre-start" ON → restart Polaris → opening the "PHD2 GUI" tab shows the iframe ready (no delay)
6. **PHD2 GUI session on unsupported OS**:
   - Windows mini-PC: "PHD2 GUI" tab shows banner "Embedded PHD2 GUI requires Linux + xpra. Use PHD2 native window on this OS."
   - Linux without xpra: banner "Install xpra: `sudo apt install xpra xserver-xorg-video-dummy`" + doc link
   - Endpoints `/api/guider/gui-session/*` return 501 with an explanatory message
7. **Reverse proxy**:
   - DevTools Network tab: request to `/phd2-gui/` returns 200 with xpra's HTML
   - WebSocket upgrade for `/phd2-gui/` proxies correctly to `ws://127.0.0.1:14600/`
   - Iframe loads without CORS warnings, sessionStorage works (same-origin)
8. **Cross-rig sanity**:
   - Set up 2 rigs in Polaris (A and B), 2 corresponding profiles in PHD2
   - Switch rig A→B in Manage Rigs → PHD2 switches profile, applies rig B's preset, reconnects equipment
   - Switch back B→A → same in reverse
   - Each rig keeps its own `PHD2CalibrationStepMsOverride` + `PHD2CustomAlgoParams`

## Compatibility, license, and security notes

- **PHD2 license**: GPLv3. Polaris does not embed PHD2 code, it speaks JSON-RPC and hosts it in an xpra session. No code mixing.
- **xpra license**: GPLv2. Polaris invokes via subprocess; the user installs separately (not bundled). Documented.
- **Cross-platform**:
  - **Linux**: Preferred path. Detailed install procedure in `docs/phd2-gui-embedding.md` (based on the PHD2 maintainer's comment): apt install + edit `/etc/xpra/conf.d/55_server_x11.conf` to use Xorg-dummy instead of Xvfb. RPi 4/5 tested.
  - **Windows**: "PHD2 GUI" tab disabled with an explanatory banner. JSON-RPC controls + smart calibrate + presets keep working. The user opens native PHD2 on Windows itself when they need the Wizard.
  - **macOS**: same as Windows, disable the PHD2 GUI tab; RPC controls work.
- **Bandwidth**: xpra uses H.264/VP8 for large updates + JPEG for small ones. For a single PHD2 window (UI + camera view), 2-8 Mbps when the user has the tab open, 0 when closed. Configurable via `--refresh-rate=10` (10 Hz, default) or lower to save.
- **RPi 4 resource cost**: PHD2 + Xorg-dummy + xpra ≈ 150-200 MB resident. CPU ~15-25% idle, 40-60% during camera updates. Document: "Setup tool, not recommended to leave open during live guiding."
- **Security**:
  - Port 14600 bound to `127.0.0.1` only (never exposed directly to the network)
  - Access via `/phd2-gui/*` goes through the Polaris listener which has the Relay auth
  - In the README, same "Polaris assumes a trusted LAN, use Relay with tokens for internet access" warning
- **Failure modes**:
  - xpra crashes → `Phd2GuiSessionService` detects via health check (TCP probe fails), marks `SessionRunning=false`, UI shows "Session crashed, click Restart"
  - PHD2 crashes inside xpra → xpra keeps the session alive (empty); use `xpra exec :100 phd2` to restart without losing the session
  - Iframe fails to connect → DevTools console shows the error; toolbar has a "Restart session" button for recovery

---


# Previous plan: RIGS tab, connection-on-top + role-based cards

> The history (PREVIEW tab, Activity bar, Siril+GraXpert, FILES tab, DSLR/Mirrorless, original gap analysis, STUDIO, Weather, Tonight) is preserved below starting at `# Previous plan: PREVIEW tab`. This block at the top covers only the RIGS tab reorganization.

## Context

Today the RIGS tab shows **only** the INDI/Alpaca connection panel when disconnected, and **only** the 8+ equipment cards when connected (`x-show="!indiConnected"` vs `x-show="indiConnected"`). Whoever lands on the tab without having connected sees only two buttons; whoever is connected loses the "what am I connected to" feedback. Worse: the tab doesn't give a clear mental picture of the **rig**, a mix of Camera card, Mount card, Focuser card, FilterWheel card, Rotator + Flat + Dome + Weather, all visually equivalent despite being at very different levels of importance for the capture workflow.

The inspiration is ASIAIR: setup organized by **roles** (Main Telescope, Main Camera, Guide Scope, Guide Camera, AF Motor, Filter Wheel) with the connection always visible at the top.

**Goal**: redesign the RIGS tab with:
1. **Thin connection strip at the top** always visible, compact when connected, expanded (with INDI/Alpaca tabs) when disconnected
2. **Main grid of role cards**: Main Telescope (OTA optics), Main Camera, Guidescope (OTA optics), Guide Camera (PHD2 read-only), AutoFocus Motor, Filter Wheel, Mount (Go-To controller)
3. **Collapsible "Accessories" section at the bottom** for optionals: Rotator, Flat Panel, Dome, Weather

**Decisions confirmed with the user**:
- **Main Telescope = OTA metadata** (focal length, aperture, brand, model, accessory). No hardware connection. **Mount** stays as a separate card for the Go-To controller.
- **Guidescope** = metadata-only card (focal length). No connection.
- **Guide Camera** = read-only display of what PHD2 reports as the active camera. Polaris doesn't manage the guide cam directly.
- **Accessories** (Rotator, Flat, Dome, Weather) live in a separate collapsible section at the bottom.
- **Connection strip** always visible: when connected shows "✓ INDI · localhost:7624 · 12 devices" + Disconnect button; when disconnected shows INDI/Alpaca tabs + current form.

## Architecture

### Layout

```
┌─ Equipment ──────────────────────────────[ INDI Off ]─┐
│ RIG: [Default ▾]  [Manage rigs…]  [💾 Save selections] │
├────────────────────────────────────────────────────────┤
│  [✓ INDI · localhost:7624 · 12 devices]  [Disconnect]  │ ← when connected
│      OR                                                │
│  [INDI] [ASCOM/Alpaca]                                 │ ← when disconnected
│  Host [localhost] Port [7624] [Connect INDI]           │
├────────────────────────────────────────────────────────┤
│ ┌───────────────┐ ┌───────────────┐ ┌───────────────┐  │
│ │ 🔭 Main       │ │ 📷 Main       │ │ 🛰 Mount      │  │
│ │   Telescope   │ │   Camera      │ │  (Go-To)      │  │
│ │ [optics]      │ │ [INDI/Vendor] │ │ [INDI/WiFi]   │  │
│ └───────────────┘ └───────────────┘ └───────────────┘  │
│ ┌───────────────┐ ┌───────────────┐ ┌───────────────┐  │
│ │ 🎯 AutoFocus  │ │ 🎨 Filter     │ │ 🔭 Guidescope │  │
│ │   Motor       │ │   Wheel       │ │ [optics]      │  │
│ │ [INDI device] │ │ [INDI device] │ │ FL: 200mm     │  │
│ └───────────────┘ └───────────────┘ └───────────────┘  │
│ ┌───────────────┐                                      │
│ │ 🌟 Guide Cam  │                                      │
│ │ (PHD2 owned)  │                                      │
│ │ ZWO ASI120MM  │                                      │
│ └───────────────┘                                      │
├────────────────────────────────────────────────────────┤
│ ▸ Accessories (4)                                      │ ← collapsed default
│   Rotator · Flat Panel · Dome · Weather                │
└────────────────────────────────────────────────────────┘
```

Auto-fit grid (CSS Grid `repeat(auto-fit, minmax(280px, 1fr))`), reflowing to a single column on viewports < 768 px.

### Card mapping

| Role card | Data source | Type |
|---|---|---|
| 🔭 **Main Telescope** | `EquipmentProfile.{FocalLengthMm, ApertureMm, TelescopeBrand, TelescopeModel, AccessoryType, AccessoryModel, AccessoryFactor, RequiredBackspacingMm}` | Metadata (already exists) |
| 📷 **Main Camera** | Current Camera card (driver picker + device dropdown + cooler + ISO + temp chart) | Hardware (already exists) |
| 🛰 **Mount** (Go-To) | Current Mount card (INDI/WiFi driver picker + device dropdown + tracking/park controls + RA/Dec readout) | Hardware (already exists) |
| 🎯 **AutoFocus Motor** | Current Focuser card (device dropdown + position/temp readout + step controls) | Hardware (already exists, just visually renamed to "AutoFocus Motor") |
| 🎨 **Filter Wheel** | Current FilterWheel card | Hardware (already exists) |
| 🔭 **Guidescope** | `EquipmentProfile.GuiderFocalLengthMm` + new `GuiderApertureMm` (default 50 mm) | Metadata (partial exists, only aperture missing) |
| 🌟 **Guide Camera** | Read-only via PHD2: `guider.pixelScale` + active camera name (already comes from PHD2 status) | PHD2-owned, no local action |
| 🔄 Rotator / 💡 Flat / 🏠 Dome / 🌦 Weather | Current cards | Hardware (already exist, move to the Accessories section) |

### Connection strip

When `indiConnected === true`:
```html
<div class="equip-connection-strip connected">
    <span class="equip-conn-status ok">✓ INDI connected</span>
    <span class="equip-conn-detail">localhost:7624 · <strong x-text="devices.length"></strong> devices</span>
    <button class="btn btn-sm" @click="refreshDevices()">⟳ Refresh</button>
    <button class="btn btn-sm btn-danger" @click="disconnectIndi()">Disconnect</button>
</div>
```

When `indiConnected === false`: the current connection panel (INDI/Alpaca tabs + forms) stays the same, just looks like an "expanded strip" inside the same wrapper.

### Cards: unified visual contract

Every card follows the same skeleton to provide uniformity:

```
┌─ <icon> <Role label> ───────────── ●─┐  ← header (status dot: connected/disconnected/n-a)
│  <Type subtitle, e.g. "INDI device">│
│                                      │
│  [device dropdown or metadata form]  │  ← body
│                                      │
│  [Connect/Disconnect] [Detail btn]   │  ← actions
└──────────────────────────────────────┘
```

**Dot state**:
- Green "connected", device is active (camera connected, mount connected, etc.)
- Yellow "selected", choice was saved on the rig but isn't connected right now
- Gray "n/a", no selection (empty)

### Guide Camera card (new, read-only)

The only fully new card. Shows:
- Camera name reported by PHD2 (`guider.cameraName`, needs to be added to the PHD2 status payload? `guider.connected`, `guider.appState` already exist, but maybe not the device name. Investigate and expose if missing.)
- PHD2 pixel scale (`guider.pixelScale`)
- Clear message "Managed by PHD2, change via PHD2 Equipment Connect"
- "Open PHD2 settings" button that opens the GUIDE tab

### Guidescope card

Simple form:
- Focal length (mm), `GuiderFocalLengthMm`
- Aperture (mm), `GuiderApertureMm` (new profile field, default 50)
- Brand + Model, optional (`GuideTelescopeBrand`, `GuideTelescopeModel`, new fields)

Guiding resolution (arcsec/px) = `206.265 * pixel_size_um / GuiderFocalLengthMm` shown as derived info when PHD2 reports pixel size.

### Collapsible Accessories

```html
<details class="equip-accessories" :open="anyAccessoryConfigured()">
    <summary>
        <span>▸</span>
        <strong>Accessories</strong>
        <span class="text-muted" x-text="accessoryCount() + ' configured'"></span>
    </summary>
    <div class="equip-grid">
        <!-- Rotator, Flat, Dome, Weather cards reused -->
    </div>
</details>
```

Auto-expands when at least one accessory already has a saved selection on the rig (so the user doesn't have to click to see what they've already configured).

## Files to modify

### Profile / backend
- `src/NINA.Headless/Services/ProfileService.cs`, add `GuiderApertureMm` (double, default 50), `GuideTelescopeBrand` (string?), `GuideTelescopeModel` (string?). Trivial migration, null/zero defaults.
- `src/NINA.Headless/Endpoints/SystemEndpoints.cs`, PUT /profile accepts the 3 new fields
- `src/NINA.Headless/Services/PHD2Client.cs`, check whether the `guider` payload already carries `cameraName`; if not, add via `get_camera_frame_size` or `get_app_state`

### Frontend
- `src/NINA.Headless/wwwroot/index.html`, rewrite the `tab === 'equip'` panel (lines 801–1454):
  - Same header (Rig dropdown + Manage rigs + Save selections)
  - New connection strip always visible (compact / expanded)
  - New role card grid (reusing the content of the 8 current cards, just repositioned)
  - New "Main Telescope" card (OTA optics, already had the fields, now becomes a card)
  - New "Guidescope" card (optics)
  - New "Guide Camera" card (PHD2 read-only)
  - Visually rename the Focuser card to "AutoFocus Motor"
  - `<details class="equip-accessories">` wrapping Rotator/Flat/Dome/Weather
- `src/NINA.Headless/wwwroot/js/app.js`, state `settings.guiderApertureMm`, `settings.guideTelescopeBrand`, `settings.guideTelescopeModel`; load/save integrated with the profile; new helpers `accessoryCount()`, `anyAccessoryConfigured()`. Likely: extend `_applyRigToChoices` to hydrate the 3 new fields.
- `src/NINA.Headless/wwwroot/css/app.css`, `.equip-connection-strip` (compact + expanded variants), `.equip-grid` (CSS Grid auto-fit responsive), `.equip-accessories` (collapsible details styling), small tweaks in `.equip-card` for more consistent height between hardware cards and metadata-only cards.

### Tests
- `tests/NINA.Headless.Test/ProfileServiceTests.cs` (create if it doesn't exist), round-trip the 3 new fields via JSON.

## Reuse of existing code

- **8 current hardware cards** (Camera, Mount, Focuser, FilterWheel, Rotator, Flat, Dome, Weather), move them into the new grid / accessories section, **without** changing their internal content. Just wrapper + order.
- **`.equip-card`** CSS class, base visual kept; only add an `.equip-card-metadata` variant for OTA / Guidescope (no Connect button, just fields)
- **Existing Profile fields**, FocalLengthMm, ApertureMm, TelescopeBrand, TelescopeModel, AccessoryType, AccessoryModel, AccessoryFactor, RequiredBackspacingMm, GuiderFocalLengthMm, all already exist
- **Telescope catalog picker** (already exists, populated these fields when the user picked an OTA from the list), reuse inside the Main Telescope card
- **`devices` array from INDI**, already exists, feeds all dropdowns
- **PHD2 guider payload** (`guider.pixelScale`, `guider.appState`), already comes via WS, just read it

## End-to-end verification

1. **Build + tests**: `dotnet build` clean; 385 current + ~2 new = ~387 green
2. **Layout, no INDI**:
   - Open the RIGS tab, expanded connection strip at the top with INDI/Alpaca tabs
   - Main Telescope + Guidescope cards render (they're metadata-only, don't depend on INDI)
   - Main Camera, Mount, Focuser, Filter Wheel cards render **but with gray status dot** ("not connected"), selection fields empty
   - Accessories collapsed by default
3. **Layout, after connecting INDI**:
   - Connection strip becomes compact: "✓ INDI · localhost:7624 · 12 devices [Refresh] [Disconnect]"
   - Dropdowns on hardware cards populate with discovered devices
   - Pick a device on each → status dot turns yellow ("selected, not connected")
   - Click Connect on the card → status dot turns green ("connected")
4. **Profile fields**:
   - Change Main Telescope focal length 478 → 600 + Save selections → rig persists
   - Reload the page → fields come back loaded from the active rig
   - Change Guidescope aperture 50 → 70 → Save → persists
5. **Guide Camera card**:
   - Without PHD2: card shows "PHD2 not connected"
   - With PHD2 connected + camera selected on it: card shows "ZWO ASI120MM · 3.75 µm · scale 4.85"/px" (read-only)
   - "Open PHD2 settings" button navigates to the GUIDE tab
6. **Accessories**:
   - With no accessory selected: section collapsed with "(0 configured)"
   - Select Rotator + Save → reload → section auto-expanded with "(1 configured)"
7. **Responsive**:
   - 1200 px viewport: 3-column grid
   - 800 px viewport: 2-column grid
   - 500 px viewport: 1 column, full-width cards
8. **No regression**:
   - All existing hardware card controls keep working (cooler, tracking, park, filter swap, focuser move, etc.)
   - Manage rigs modal opens and works normally
   - Save selections persists all device names correctly

## Notes

- Reorganization is pure HTML/CSS/JS, the only C# change is adding 3 optional fields to the profile (no migration because defaults are null/zero)
- Mobile-first responsive grid via CSS auto-fit
- No changes to the INDI client, discovery, or connect/disconnect endpoints
- The Guide Camera card is the first "external-managed" card (PHD2 owns); sets a pattern in case we want to do the same for "Mount via NINA Desktop" in the future

---

# Previous plan: PREVIEW tab, quick snap with optional save

## Context

The LIVE tab currently mixes two purposes: (a) rendering frames coming in real time during a running Autorun, and (b) taking ad-hoc snaps via the Capture button. It works, but the UX is confusing, the user sees "Live" and thinks it's only for following the sequence; doesn't realize they can take test shots there. Worse: when they actually want a snap (test visual focus, framing, polar align, see whether the camera is framed before starting a sequence), they have to share mental space with the "live" content from the Autorun.

**Goal**: dedicated **PREVIEW** tab (between FOCUS and AUTORUN) with **snap-and-look** focused UX: exposure, bin, gain, filter (when filter wheel connected), "Take snap" button, opt-in "Loop" button, "Save to disk" toggle, canvas that renders the result + image stats (HFR, star count, mean). LIVE remains as "follow what's coming in from the Autorun".

**Decisions confirmed with the user**:
- **Save folder**: snaps saved go to `{rig}/snaps/{filter}_{date}/` separate from lights/calibrated, so as not to contaminate the sequence's science.
- **Loop**: opt-in via a second button. Default is single-shot.
- **Filter selector**: appears in the form when the filter wheel is connected.

## Architecture

### Principle: reuse the entire existing capture pipeline

The `POST /api/camera/capture` (in `CameraEndpoints.cs:9-45`) already does 95% of what PREVIEW needs: accepts `{ exposure, gain, binning, filter }`, calls `Camera.CaptureAsync`, routes to `ImageRelayService` which sends the JPEG via the WS `/ws/image-stream`. The only missing thing is the "save to disk" flag. And `_renderJpegFrame` / `_renderRawFrame` on the frontend already render to the LIVE canvas, just need them to also render to the PREVIEW canvas when both exist in the DOM.

### Backend, minimal extension of `/api/camera/capture`

Add two optional fields to the `CaptureRequest` body:

```csharp
public record CaptureRequest(
    double Exposure,
    int? Gain,
    int? Binning,
    string? Filter,
    bool? SaveToDisk,     // NEW, default false (preview-only)
    string? TargetName);  // NEW, used only when SaveToDisk=true (folder name hint)
```

In the handler:
1. Capture as today (already exists)
2. If `SaveToDisk == true && imageData != null`:
   - `_imageWriter.SaveImage(imageData, targetName: req.TargetName ?? "snap", imageType: "SNAP", gain: req.Gain ?? 0)`
3. Continue with `RelayImageAsync` as today (snap renders on the canvas independent of saving)

Switch the filter **before** capturing when `req.Filter` came populated and the filter wheel is connected, similar to what `SequenceEngine` does.

**Extension in `ImageWriterService.BuildSubDir`**: add a `"SNAP"` case that routes to `{rig}/snaps/{filter}_{date}/`:

```csharp
"SNAP" => Path.Combine("snaps",
            SanitizeFolder(string.IsNullOrEmpty(m.Exposure.Filter) ? "L" : m.Exposure.Filter)
                + "_" + sessionDate.ToString("yyyy-MM-dd"))
```

This keeps the noon-to-noon session-date convention the rest of the code already uses.

### Frontend, new PREVIEW tab

**Sidebar**: new button between FOCUS and AUTORUN.

**Tab panel** with vertical layout:
- **Controls toolbar** at the top: Exp (s), Gain, Bin (1/2/4) fields, Filter (only visible if `filterWheel.connected && filterWheel.filters.length > 0`), "💾 Save to disk" toggle, "📸 Take snap" + "↻ Loop"/"⏹ Stop" + "⏹ Abort" buttons (last one only visible during exposure)
- **Canvas + overlay** in the center (same structure as LIVE, `previewCanvas` + `previewOverlayCanvas`)
- **Stats strip** at the bottom: Exp/Gain/Bin used, Star count, HFR, Mean ADU, timestamp

**Renderer reuse**: the current `handleImageFrame` handler in `app.js` calls `_renderJpegFrame(blob)` or `_renderRawFrame(buf)`. These two methods today do `getElementById('liveCanvas')`. Generalize to iterate over a list `['liveCanvas', 'previewCanvas']`:

```js
_canvasIdsForRender() {
    const ids = [];
    if (document.getElementById('liveCanvas'))    ids.push('liveCanvas');
    if (document.getElementById('previewCanvas')) ids.push('previewCanvas');
    return ids;
}
```

Each call renders to every canvas found, negligible cost (same `drawImage` source bitmap, just blit). The canvas hidden by `x-show` still exists in the DOM and receives the buffer, so switching tabs shows the last image instantly.

**New state in app.js**:
```js
preview: {
    exposure: 2.0,        // seconds
    gain: 100,
    binning: 1,
    filter: '',           // '' = current filter, ignored
    saveToDisk: false,
    targetName: 'snap',   // folder under {rig}/snaps/ via imageType=SNAP routing
    looping: false,
    busy: false,          // true while a snap is in flight
    lastSnapAt: null,
    lastStats: null       // { starCount, hfr, mean } from /capture response
}
```

**New methods**:
```js
async previewTakeSnap() {
    if (this.preview.busy) return;
    this.preview.busy = true;
    try {
        const r = await this.apiPost('/api/camera/capture', null, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                exposure: this.preview.exposure,
                gain: this.preview.gain,
                binning: parseInt(this.preview.binning),
                filter: this.preview.filter || null,
                saveToDisk: this.preview.saveToDisk,
                targetName: this.preview.targetName
            })
        });
        this.preview.lastStats = r.stats || null;
        this.preview.lastSnapAt = new Date();
    } catch (e) {
        this.toast('Snap failed: ' + (e.message || ''), 'error');
        this.preview.looping = false;   // break loop on error
    } finally {
        this.preview.busy = false;
    }
    // Loop kicks the next snap when previous finished
    if (this.preview.looping) {
        this.$nextTick(() => this.previewTakeSnap());
    }
},

async previewToggleLoop() {
    this.preview.looping = !this.preview.looping;
    if (this.preview.looping && !this.preview.busy) {
        this.previewTakeSnap();
    }
},

async previewAbort() {
    this.preview.looping = false;   // stop loop first
    try { await this.apiPost('/api/camera/abort'); }
    catch (e) { this.toast('Abort failed', 'warn'); }
}
```

**Activity bar chip**: when `preview.busy === true`, the existing `activityChips()` helper on the activity-bar can gain an entry `{ id: 'snap', icon: '📸', label: 'Preview snap ' + Xs }`. Plus a 1-liner.

## Files to modify

- `src/NINA.Headless/Endpoints/CameraEndpoints.cs`, extend `CaptureRequest` with `SaveToDisk` + `TargetName`; conditionally call `_imageWriter.SaveImage(...)` after capture; inject `ImageWriterService` into the handler.
- `src/NINA.Headless/Services/ImageWriterService.cs`, add a `"SNAP"` case in `BuildSubDir` routing to `snaps/{filter}_{date}/`.
- `src/NINA.Headless/wwwroot/index.html`, new "Preview" button in the sidebar between Focus and Autorun; new `<div x-show="tab === 'preview'" class="tab-panel preview-panel">` with toolbar + canvas + stats.
- `src/NINA.Headless/wwwroot/js/app.js`, state `preview: {...}`; methods `previewTakeSnap / previewToggleLoop / previewAbort`; generalize `_canvasIdsForRender()` + update `_renderJpegFrame` / `_renderRawFrame` to iterate the list (no change if the PREVIEW canvas doesn't exist, preserves current LIVE behavior); 1 new entry in `activityChips()` when `preview.busy`.
- `src/NINA.Headless/wwwroot/css/app.css`, `.preview-panel` flex column, `.preview-toolbar` controls row, `.preview-canvas-wrap`, `.preview-stats` reusing the look-and-feel of `.preview-canvas` / `.preview-stats` already on LIVE.
- `tests/NINA.Headless.Test/ImageWriterServiceTests.cs` (if it exists, otherwise create), pin that `BuildSubDir("SNAP", ...)` produces `{rig}/snaps/{filter}_{date}`.
- `README.md`, small "Preview tab" section in features.

## Files to create
- (none, the feature is a pure extension of existing surfaces)

## Reuse of existing code

- `Endpoints/CameraEndpoints.cs:9-45`, `/api/camera/capture` handler, already does capture + relay; just adds 1 if/save
- `Services/ImageWriterService.cs:250-275` `BuildSubDir`, switch that already routes by imageType; new "SNAP" case follows the exact pattern of the DARK/FLAT/BIAS cases
- `Services/ImageRelayService.cs:44-143` `RelayImageAsync`, broadcast to WS, no change
- `WebSocket/ImageStreamHandler.cs`, no change, same byte channel
- `app.js _renderJpegFrame` / `_renderRawFrame`, just generalize to render to N canvases instead of 1
- `app.js capture()`, don't touch; PREVIEW has a separate method (`previewTakeSnap`) so LIVE and PREVIEW state don't couple
- `activityChips()` from the activity bar, 1 new entry, same structure as the other chips
- `app.js apiPost` + `toast`, utility handlers as always
- existing `IndiCamera.AbortExposureAsync` + `/api/camera/abort` endpoint, Abort button reuses

## End-to-end verification

1. **Build + tests**: `dotnet build src/NINA.Headless` clean; `dotnet test` keeps the 387 current + new snap-routing tests (~3 new) = ~390 green
2. **No camera connected**: PREVIEW opens, form renders, Take snap button shows toast "No camera connected" from the existing handler
3. **With camera connected (mock INDI)**:
   - Take snap with saveToDisk=false → frame renders on the PREVIEW + LIVE canvas; stats appear (HFR, star count); nothing on disk
   - Take snap with saveToDisk=true → frame renders + file in `{ImageOutputDir}/{rig}/snaps/L_2026-05-22/snap_001.fits`
   - Loop on → snap_001.fits, snap_002.fits, snap_003.fits... button turns into "Stop"; clicking Stop interrupts after the current snap finishes
   - Abort during a long exposure (60s) → exposure interrupted; loop stops; canvas shows the last good image
4. **Filter**: when the filter wheel is connected with R/G/B/L, dropdown appears; switch to "G" + Take snap → INDI switches the filter before the exposure (already the behavior of `SequenceEngine`)
5. **Activity bar**: during a long exposure, "📸 Preview snap" chip appears with dynamic label
6. **Cross-tab**: take a snap in PREVIEW → switch to LIVE → last image appears on the LIVE canvas (thanks to the multi-canvas render)
7. **Mobile**: responsive layout, toolbar wraps to two lines on viewports < 700px, canvas keeps aspect ratio

## Compatibility and security notes

- No new NuGet dependencies or frontend libraries
- Capture endpoint still without auth on the LAN (same model as the rest)
- Loop mode has a `busy` flag guard, no infinite queue of snaps fired before the previous one finishes; chaining via `$nextTick` after `finally`
- Save goes to a safe-by-design path (under the configured `ImageOutputDir`, sanitized by `SanitizeFolder`)
- If the user switches tabs during an exposure with loop active, the loop keeps running in the background (the snap also reaches LIVE), intentional behavior; to stop they need to come back to PREVIEW + click Stop, or Abort
- `targetName: 'snap'` default can be edited by the user in an optional field in the form (future), to organize snaps by intent ("focus test", "framing M81", etc.), scope deferred

---
# Previous plan: Activity bar (bottom) with operation chips + host metrics

## Context

Polaris today scatters status indicators across every tab: the auto-focus progress bar lives in the Focus tab, sequence progress in the Autorun tab, PHD2 status in the header (but only "connected/offline"), Siril jobs only in the STUDIO modal, GraXpert the same. A user running an N-hour sequence has to **navigate** to each tab to find out "what's running right now?". There's also no health signal at all for the mini-PC / Raspberry Pi that hosts the server, if a combo of live-stacking + per-frame GraXpert + STUDIO stacking starts blowing up RAM, the user only finds out when they see the crash.

**Goal**: a fixed bar at the bottom of the page (app-wide, not per-tab) that shows at a glance:
1. **Chips for transient active operations** (slew, expose, plate-solve, auto-focus, meridian flip, sequence running, Siril/GraXpert jobs, Studio jobs)
2. **Host metrics** (total machine CPU% + RAM used/total) refreshed every 1-2 seconds

**Decisions confirmed with the user**:
- **Metrics**: whole host (not just the Polaris process). Adds `Microsoft.Extensions.Diagnostics.ResourceMonitoring` (official MS NuGet, cross-platform, ~100 KB).
- **Visibility**: always visible as a fixed ~36 px strip at the bottom. CPU/RAM always present; the chip row appears when there's an active operation, stays empty when idle.
- **Chip scope**: only transient operations (slew, expose, focus run, flip, sequence run, Siril/GraXpert jobs, Studio batches). Connected state "INDI/Alpaca/PHD2" is already in the header, no duplication.

## Architecture

### Principle: no new broadcast for data that already exists

`StatusStreamHandler` already sends almost everything the bar needs in a single message per second (confirmed during exploration: equipment, autoFocus, meridianFlip, guider, sequence, liveStack). The **chip-by-chip derivation is client-side**, the JS looks at the latest payload and builds chips locally. That avoids duplicating schema on the server.

What needs to be **added** to the WS payload:
- `host` block: `{ cpuPercent, memoryPercent, memoryUsedMB, memoryTotalMB, processCpuPercent, processMemoryMB }`
- `sirilJobs`: a slim list of active jobs `[{ jobId, scriptName, targetName, stage, percentDone }]`
- `graXpertJobs`: slim list `[{ jobId, operation, done, total, failed }]`
- (optional v2) `studioJobs`: active jobs from `BatchStackingService` / `MasterFrameService` / `CalibrationService`

### Backend: `Services/HostMetricsService.cs`

New singleton + hosted service. Same pattern as `MdnsService` / `PHD2AutoStartService` (background loop registered via `AddHostedService`).

```csharp
public class HostMetricsService : BackgroundService {
    private readonly IResourceMonitor _monitor;
    private readonly ILogger<HostMetricsService> _logger;
    public HostMetricsSnapshot Latest { get; private set; } = new();

    protected override async Task ExecuteAsync(CancellationToken ct) {
        var process = Process.GetCurrentProcess();
        var lastCpuTime = process.TotalProcessorTime;
        var lastSampleTime = DateTime.UtcNow;
        while (!ct.IsCancellationRequested) {
            try {
                var util = _monitor.GetUtilization(TimeSpan.FromSeconds(2));
                // System-wide (from ResourceMonitoring): CpuUsedPercentage,
                //                                       MemoryUsedPercentage
                // System-wide totals via GC.GetGCMemoryInfo().TotalAvailableMemoryBytes
                var gcInfo = GC.GetGCMemoryInfo();

                // Process-only CPU% via TotalProcessorTime delta
                var now = DateTime.UtcNow;
                var elapsed = (now - lastSampleTime).TotalMilliseconds * Environment.ProcessorCount;
                var cpuDelta = (process.TotalProcessorTime - lastCpuTime).TotalMilliseconds;
                var processCpuPercent = elapsed > 0 ? Math.Min(100, 100 * cpuDelta / elapsed) : 0;
                lastCpuTime = process.TotalProcessorTime;
                lastSampleTime = now;

                Latest = new HostMetricsSnapshot {
                    CpuPercent = util.CpuUsedPercentage,
                    MemoryPercent = util.MemoryUsedPercentage,
                    MemoryUsedMB = (long)(gcInfo.TotalAvailableMemoryBytes
                                          * util.MemoryUsedPercentage / 100 / 1024 / 1024),
                    MemoryTotalMB = gcInfo.TotalAvailableMemoryBytes / 1024 / 1024,
                    ProcessCpuPercent = processCpuPercent,
                    ProcessMemoryMB = process.WorkingSet64 / 1024 / 1024,
                    SampledAt = now
                };
            } catch (Exception ex) {
                _logger.LogDebug(ex, "HostMetrics sample failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
}

public record HostMetricsSnapshot {
    public double CpuPercent { get; init; }
    public double MemoryPercent { get; init; }
    public long MemoryUsedMB { get; init; }
    public long MemoryTotalMB { get; init; }
    public double ProcessCpuPercent { get; init; }
    public long ProcessMemoryMB { get; init; }
    public DateTime SampledAt { get; init; }
}
```

**Cadence**: 2s (not 1s), `IResourceMonitor.GetUtilization(TimeSpan)` needs a minimum window to compute CPU%; 2s is the sweet spot for cost/precision. `StatusStreamHandler` pulls `Latest` on every broadcast (1s), it might show the same number twice, no problem.

**Defensive**: `IResourceMonitor` throws in some edge cases (container with no cgroup mount, first call within < 5s). The `try/catch` keeps `Latest` at the last valid value rather than zeroing it out.

### Wiring in `StatusStreamHandler.cs`

Extend the payload to include the new blocks. The current pattern already takes others via DI, just add 3 more parameters:

```csharp
public static async Task Handle(HttpContext ctx, ...,
    HostMetricsService hostMetrics,
    SirilService siril,
    GraXpertService graxpert) {

    // ... existing payload assembly ...

    var payload = new {
        // ... existing fields ...
        host = hostMetrics.Latest,
        sirilJobs = siril.ActiveJobs.Select(j => new {
            j.JobId, j.ScriptName, j.TargetName, j.Stage, j.PercentDone
        }).ToList(),
        graXpertJobs = graxpert.ActiveJobs.Select(j => new {
            j.JobId, operation = j.Operation.ToString(),
            j.Done, j.Total, j.Failed
        }).ToList()
    };
}
```

No cadence change (1Hz as before). Payload grows ~200 bytes when there are active jobs, ~80 bytes when idle.

### Registration in `Program.cs`

```csharp
builder.Services.AddResourceMonitoring();   // from the package
builder.Services.AddSingleton<HostMetricsService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HostMetricsService>());
```

### NuGet package

`Microsoft.Extensions.Diagnostics.ResourceMonitoring` 10.x, added to `NINA.Headless.csproj`. ~100 KB, 1 transitive dependency (`Microsoft.Extensions.Logging.Abstractions` is already in the project).

## Frontend

### Layout: fixed app-wide bar at the bottom

The bar is a single `<footer>` pinned below the content, outside any `tab-panel`. So it doesn't get hidden by scrolling panels, every tab-panel gets `padding-bottom: 36px` (the bar's height). The sticky `.home-footer` (already on the home page) rises by the same 36 px thanks to the padding, it lands above the activity bar.

Markup (insert before `</body>` or at the end of `<main>`):

```html
<!-- App-wide activity bar. Always visible. Reads from the WS
     status payload + new host block. -->
<footer class="activity-bar"
        :class="{ 'has-activities': activityChips().length > 0 }">
    <!-- Operations row (left), only renders when something's running -->
    <div class="activity-bar-ops">
        <template x-for="chip in activityChips()" :key="chip.id">
            <span class="activity-chip"
                  :class="'activity-chip-' + (chip.kind || 'info')">
                <span class="activity-chip-icon" x-text="chip.icon"></span>
                <span class="activity-chip-label" x-text="chip.label"></span>
                <span class="activity-chip-progress" x-show="chip.progress != null">
                    <span class="activity-chip-progress-bar">
                        <span :style="'width: ' + chip.progress + '%'"></span>
                    </span>
                </span>
            </span>
        </template>
    </div>

    <!-- Spacer pushes host metrics to the right -->
    <span style="flex:1"></span>

    <!-- Host CPU + RAM (always visible) -->
    <div class="activity-bar-host" x-show="host.cpuPercent != null">
        <span class="activity-host-stat" title="System CPU usage">
            <span class="activity-host-label">CPU</span>
            <span class="activity-host-value"
                  :class="hostCpuClass()"
                  x-text="(host.cpuPercent || 0).toFixed(0) + '%'"></span>
        </span>
        <span class="activity-host-stat" title="System memory">
            <span class="activity-host-label">RAM</span>
            <span class="activity-host-value"
                  :class="hostMemClass()"
                  x-text="formatRam(host.memoryUsedMB, host.memoryTotalMB)"></span>
        </span>
    </div>
</footer>
```

### State + derivation in `app.js`

```js
// State (added at top-level component scope)
host: { cpuPercent: null, memoryPercent: null,
        memoryUsedMB: 0, memoryTotalMB: 0,
        processCpuPercent: 0, processMemoryMB: 0 },
sirilActiveJobs: [],
graXpertActiveJobs: [],

// Wire-up: existing handleStatusMessage(msg) gains:
//   if (msg.host) this.host = msg.host;
//   if (msg.sirilJobs) this.sirilActiveJobs = msg.sirilJobs;
//   if (msg.graXpertJobs) this.graXpertActiveJobs = msg.graXpertJobs;

// Chip derivation, purely client-side from cached state.
// Each chip is { id, icon, label, kind, progress? }.
activityChips() {
    const out = [];

    // Sequence
    if (this.seqState === 'running') {
        const s = this.seqStatus;
        const pct = s?.totalFrames ? Math.round(100 * s.totalFramesCompleted / s.totalFrames) : 0;
        out.push({ id: 'seq', icon: '📑', kind: 'info',
                   label: `Autorun ${s?.totalFramesCompleted || 0}/${s?.totalFrames || 0}`,
                   progress: pct });
    }

    // Auto-focus
    if (this.autoFocus.state === 'running') {
        const i = this.autoFocus.currentSampleIndex ?? 0;
        const n = this.autoFocus.steps ?? 0;
        out.push({ id: 'af', icon: '🔄', kind: 'info',
                   label: `Auto-focus ${i + 1}/${n}`,
                   progress: n ? Math.round(100 * (i + 1) / n) : 0 });
    }

    // Meridian flip
    if (this.mfState && this.mfState !== 'idle') {
        out.push({ id: 'mf', icon: '↔️', kind: 'warn',
                   label: 'Meridian flip: ' + this.mfState });
    }

    // Slew (mount.slewing comes from telescope state)
    if (this.mount?.slewing) {
        out.push({ id: 'slew', icon: '🔭', kind: 'info', label: 'Slewing' });
    }

    // Camera exposing (state string from INDI)
    if (this.equipCameraInfo?.state &&
        /expos|download/i.test(this.equipCameraInfo.state)) {
        out.push({ id: 'expose', icon: '📷', kind: 'info',
                   label: 'Camera: ' + this.equipCameraInfo.state.toLowerCase() });
    }

    // Focuser moving
    if (this.focusMoving) {
        out.push({ id: 'focuser', icon: '🎯', kind: 'info', label: 'Focuser moving' });
    }

    // Filter wheel
    if (this.filterWheel?.moving) {
        out.push({ id: 'fw', icon: '🎨', kind: 'info', label: 'Filter change' });
    }

    // PHD2 transient (calibrating / settling / dithering, NOT steady guiding)
    if (this.guider.calibrating) {
        out.push({ id: 'phd2-cal', icon: '🌟', kind: 'warn', label: 'PHD2 calibrating' });
    } else if (this.guider.settling) {
        out.push({ id: 'phd2-set', icon: '🌟', kind: 'info', label: 'PHD2 settling' });
    }

    // Live stacking
    if (this.liveStackRunning) {
        out.push({ id: 'ls', icon: '💎', kind: 'info',
                   label: `Live stack ${this.liveStackInfo?.frameCount || 0}f` });
    }

    // Siril active jobs (one chip per job, usually only 1)
    for (const j of (this.sirilActiveJobs || [])) {
        out.push({ id: 'siril-' + j.jobId, icon: '⚡', kind: 'info',
                   label: `Siril: ${j.scriptName.replace(/\.ssf$/, '')} ${j.stage}`,
                   progress: j.percentDone });
    }

    // GraXpert active jobs
    for (const j of (this.graXpertActiveJobs || [])) {
        const opIcon = j.operation === 'BackgroundExtraction' ? '🌅'
                     : j.operation === 'Deconvolution' ? '✨'
                     : '🔇';
        const pct = j.total ? Math.round(100 * (j.done + j.failed) / j.total) : 0;
        out.push({ id: 'gx-' + j.jobId, icon: opIcon, kind: 'info',
                   label: `GraXpert ${j.done}/${j.total}`, progress: pct });
    }

    return out;
},

// CPU/RAM colour classes, green < 60%, amber 60-85%, red > 85%.
// Lets a glance at the bar surface trouble without reading the number.
hostCpuClass() {
    const p = this.host.cpuPercent || 0;
    return p > 85 ? 'host-red' : p > 60 ? 'host-amber' : 'host-green';
},
hostMemClass() {
    const p = this.host.memoryPercent || 0;
    return p > 85 ? 'host-red' : p > 60 ? 'host-amber' : 'host-green';
},
formatRam(usedMB, totalMB) {
    if (!totalMB) return ', /,';
    const usedGB = (usedMB / 1024).toFixed(1);
    const totalGB = (totalMB / 1024).toFixed(1);
    return `${usedGB} / ${totalGB} GB`;
}
```

### CSS, translucent glass matching the home cards

```css
.activity-bar {
    position: fixed;
    left: 0; right: 0; bottom: 0;
    height: 36px;
    z-index: 50;   /* above content, below toasts/modals */
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 0 14px;
    background: rgba(5, 8, 16, 0.78);
    backdrop-filter: blur(8px);
    -webkit-backdrop-filter: blur(8px);
    border-top: 1px solid var(--border);
    font-size: 11px;
    color: var(--text-secondary);
}
.activity-bar-ops {
    display: flex;
    gap: 6px;
    flex-wrap: nowrap;
    overflow-x: auto;   /* horizontal scroll on overflow */
    overflow-y: hidden;
    flex: 0 1 auto;
    min-width: 0;
}
.activity-chip {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    padding: 3px 9px;
    border-radius: 999px;
    background: rgba(255, 255, 255, 0.06);
    border: 1px solid var(--border);
    color: var(--text-primary);
    white-space: nowrap;
    font-size: 11px;
    line-height: 1.2;
}
.activity-chip-warn { border-color: rgba(245, 158, 11, 0.6); }
.activity-chip-error { border-color: rgba(239, 68, 68, 0.6); }
.activity-chip-progress-bar {
    width: 48px; height: 4px;
    background: rgba(0, 0, 0, 0.4);
    border-radius: 2px;
    overflow: hidden;
}
.activity-chip-progress-bar > span {
    display: block; height: 100%;
    background: var(--accent);
    transition: width 0.3s ease;
}
.activity-bar-host {
    display: flex;
    gap: 14px;
    align-items: center;
    flex-shrink: 0;
}
.activity-host-stat {
    display: inline-flex;
    align-items: center;
    gap: 4px;
}
.activity-host-label {
    color: var(--text-muted);
    text-transform: uppercase;
    font-size: 10px;
    letter-spacing: 0.5px;
}
.activity-host-value { font-variant-numeric: tabular-nums; }
.host-green { color: #86efac; }
.host-amber { color: #fcd34d; }
.host-red   { color: #fca5a5; font-weight: 600; }

/* Push everything else up so it doesn't get hidden behind the bar */
.tab-panel { padding-bottom: 36px !important; }
.home-panel { padding-bottom: 36px !important; }
```

## Files to create

- `src/NINA.Headless/Services/HostMetricsService.cs` (~120 lines)
- `tests/NINA.Headless.Test/HostMetricsServiceTests.cs` (snapshot construction, defensive try-catch boundary, color thresholds via static helper)

## Files to modify

- `src/NINA.Headless/NINA.Headless.csproj`, add `<PackageReference Include="Microsoft.Extensions.Diagnostics.ResourceMonitoring" Version="10.*" />`
- `src/NINA.Headless/Program.cs`, `builder.Services.AddResourceMonitoring()` + register `HostMetricsService` as singleton + hosted service
- `src/NINA.Headless/WebSocket/StatusStreamHandler.cs`, inject `HostMetricsService`, `SirilService`, `GraXpertService`; extend payload with `host`, `sirilJobs`, `graXpertJobs`
- `src/NINA.Headless/wwwroot/index.html`, add `<footer class="activity-bar">` at the end of `<main>` (or at the end of `<body>`)
- `src/NINA.Headless/wwwroot/js/app.js`, state `host`, `sirilActiveJobs`, `graXpertActiveJobs`; methods `activityChips()`, `hostCpuClass()`, `hostMemClass()`, `formatRam()`; hook in `handleStatusMessage` to absorb the new fields
- `src/NINA.Headless/wwwroot/css/app.css`, `.activity-bar*` styles + global `padding-bottom: 36px` on panels
- `README.md`, small "Activity bar" section below the existing features

## Reuse of existing code

- `Services/StatusStreamHandler.cs`, 1Hz WS broadcast pattern; just extend the payload
- `Services/External/SirilService.cs:181` (`ActiveJobs` property), already exists, just plumb it
- `Services/External/GraXpertService.cs:175` (`ActiveJobs` property), already exists, just plumb it
- `Services/MdnsService.cs` / `Services/PHD2AutoStartService.cs`, `BackgroundService` pattern with a loop + `Task.Delay`, copy into `HostMetricsService`
- `Endpoints/SystemEndpoints.cs:34-45`, already reports `WorkingSet64` + uptime; leave it as is, the new service becomes the authoritative source for the UI
- CSS `.home-footer` glass recipe (rgba 0.78 + backdrop-filter blur 8px), copy into `.activity-bar`
- CSS `.status-indicator` pill, base for `.activity-chip` (same ~20px height, same radius)
- CSS `.af-progress-bar` / `.af-progress-fill`, pattern for the mini-progress inside the chip
- `app.js handleStatusMessage` (already dispatches into `mount`, `sequence`, etc.), just add 3 ifs

## End-to-end verification

1. **Build + tests**:
   - `dotnet build` in `src/NINA.Headless`, no errors
   - `dotnet test` in `tests/NINA.Headless.Test`, 383 current + ~5 new = ~388 green
2. **Backend smoke**:
   - `curl http://localhost:5000/api/system/status`, unchanged (existing payload preserved)
   - Connect to the WS `/ws/status` and see the `host` block + `sirilJobs[]` + `graXpertJobs[]` added to the payload, with `host.cpuPercent` updating between snapshots
3. **UI idle state**:
   - Open the browser, go to any tab, fixed bar visible at the bottom
   - Empty chip row (nothing running), CPU/RAM updating every ~2s
   - Correct colors: green < 60%, amber 60-85%, red > 85%
4. **UI during a sequence**:
   - Start an Autorun of 30 frames, chip "📑 Autorun 0/30" with a filling progress bar
   - When an exposure runs, add chip "📷 Camera: exposing"
   - Auto-focus fired by a trigger, chip "🔄 Auto-focus 3/9" with progress
   - Meridian flip, chip "↔️ Meridian flip: slewing" → "centering" → "resuming" → disappears
5. **UI during GraXpert batch**:
   - FILES → Run GraXpert → 10 frames, chip "🌅 GraXpert 3/10" updates in real-time
   - Job completes → chip disappears
6. **Stress**:
   - Run sequence + auto-focus + live-stack + GraXpert batch simultaneously, chips sit side by side, horizontal-scroll overflow kicks in
   - CPU% climbs to 70%+ → label turns amber; > 85% → red + bold
7. **Cross-platform**:
   - Linux ARM64 (Pi 4): confirm `IResourceMonitor` returns valid values. If cgroups aren't mounted (rare), `Latest` keeps its old values without crashing.
   - Windows mini-PC: same

## Compatibility, license, and security notes

- **Microsoft.Extensions.Diagnostics.ResourceMonitoring**: MIT, official Microsoft, supports Windows + Linux (cgroups v1/v2). macOS not supported, Polaris has no macOS build target so OK.
- **Performance**: 2s sampling in `HostMetricsService` + 1s broadcast in `StatusStreamHandler`, negligible overhead even on a Pi 4 (<0.1% extra CPU measured by the research agent).
- **Privacy**: metrics are local; they only leave over the WS to the user's browser (no external telemetry).
- **Z-index**: the bar uses `z-index: 50`. The toast container (future), modals (z-index: 1000) and dropdown menus stay above. The header status bar (z-index: 100) also stays above, no conflict.
- **`.home-footer`** stays sticky inside `.home-panel`; the global `padding-bottom: 36px` pushes the footer 36px above the bottom edge, so it lands on top of the activity bar (not underneath it).

---

# Previous plan: Siril + GraXpert integration

## Context

Polaris has its own post-processing pipeline in C# (`MasterFrameService`, `CalibrationService`, `BatchStackingService`) but it competes with Siril, which is what the astrophotographer already uses, knows, and has custom scripts for. Worse: the user lives in a **heavily light-polluted** location and needs to remove gradients **per frame before stacking**, an operation the current C# pipeline doesn't do (only post-stack BGE via `FrameOperationsService.RemoveGradient`).

**Goal**: integrate Siril and GraXpert as external processing engines, exposed through the UI, with Siril becoming the preferred path for preprocessing/stacking and GraXpert removing per-frame gradients. Both run on the same machine that hosts Polaris (the user will install them there).

**Decisions confirmed with the user**:
- **Siril**: becomes the preferred path by default. The current C# pipeline (`CalibrationService`, `BatchStackingService`) stays as automatic fallback when Siril isn't installed.
- **GraXpert**: two modes. (a) **Auto during capture**, toggleable via Autorun End Events; each light that lands on disk goes to GraXpert in the background. (b) **Manual batch**, "Run GraXpert" button in STUDIO + FILES to select specific frames.
- **Output layout**: separate folders per tool, `{rig}/siril/{target}/` and `{rig}/bge/{target}/`. Doesn't mix with `calibrated/` or `integrated/`.
- **Siril scripts**: curated bundle of the 4-5 most common ones + also reads the user's scripts (`%APPDATA%/siril/scripts` on Windows, `~/.siril/scripts` on Linux). Unified dropdown in the UI.

## Architecture

### Core decision: everything external via Process.Start

Siril and GraXpert are native binaries. Polaris invokes them via `Process.Start` (established pattern, copy from `Services/PlateSolving/AstapSolver.cs` lines 23-157 for the short invocation + from `Services/PHD2ProcessManager.cs` lines 155-192 for cross-platform binary detection).

**Common strategy** for both services:
1. Locate the binary (explicit config > default OS paths > PATH fallback)
2. Set up a temp working dir at `{ImageOutputDir}/{rig}/.polaris-tmp/{jobId}/`
3. Copy/link inputs into the work dir (Siril) or point args directly at them (GraXpert)
4. Run via `Process.Start` with a configurable timeout + `CancellationToken`
5. Parse stdout/stderr + exit code
6. Move output to the final destination + clean up the work dir
7. Report progress via a mutable record state, following the `BatchStackingService` pattern (jobId-keyed `IntegrationProgress`)

### Service 1: `Services/External/BinaryLocator.cs` (shared helper)

Without it, Siril and GraXpert would each reinvent the "find where the exe is" wheel. No generic helper exists today, I'll create one:

```csharp
public static class BinaryLocator {
    public sealed record Candidate(string Description, string Path, bool Exists);

    /// <summary>
    /// Resolve a binary path. Priority: explicit config > OS-specific
    /// candidate paths > %PATH%. Returns null if nothing exists.
    /// </summary>
    public static string? Find(string? configuredPath,
                               string[] windowsCandidates,
                               string[] linuxCandidates,
                               string[] macCandidates,
                               string pathName);

    /// <summary>Diagnostic, list every place we looked + which exist.</summary>
    public static IReadOnlyList<Candidate> Enumerate(...);
}
```

Used by `SirilService` and `GraXpertService` (and any future external integration).

### Service 2: `Services/External/SirilService.cs`

Singleton. API:

```csharp
public class SirilService {
    bool IsAvailable { get; }           // detects at startup + on-demand
    string? BinaryPath { get; }         // null = not installed
    string SirilVersion { get; }        // parsed from "siril-cli --version"

    Task<SirilJob> RunScriptAsync(SirilJobRequest req, CancellationToken ct);
    SirilJob? GetJob(string jobId);
    IReadOnlyList<SirilScriptInfo> EnumerateScripts();
}

public record SirilJobRequest(
    string ScriptName,          // "OSC_Preprocessing.ssf" or full path
    string TargetName,          // for output naming
    List<string> LightPaths,    // absolute paths to light frames
    List<string>? DarkPaths,
    List<string>? FlatPaths,
    List<string>? BiasPaths,
    string? WorkDirOverride);   // optional, defaults to {rig}/.polaris-tmp/{jobId}

public record SirilJob(string JobId, string Stage, int PercentDone,
                       string? ResultPath, string? LastError);

public record SirilScriptInfo(string Name, string Path, string Source);
                                                   // Source = "bundled" | "user"
```

**`RunScriptAsync` flow**:

1. Creates work dir `{ImageOutputDir}/{rig}/.polaris-tmp/siril-{jobId}/`
2. Sub-folders per Siril convention: `lights/`, `darks/`, `flats/`, `biases/`
3. **Symlinks** (Linux) or **hardlinks** (Windows NTFS via `File.CreateSymbolicLink` + `CreateHardLink`) to the user's frames, avoids copying GB of data. Fallback to copy if the filesystem doesn't support it.
4. Resolves `ScriptName`: if it's a plain name, looks in bundled first, then user scripts. If it's an absolute path, uses it directly.
5. `Process.Start("siril-cli", $"-s {scriptPath} -d {workDir}")` with `RedirectStandardOutput=true`, `RedirectStandardError=true`, `CreateNoWindow=true`.
6. Async stdout reader parses known Siril patterns (`"status: register"`, `"progress: 45%"`) → updates `SirilJob.Stage` + `PercentDone`. The regex pattern in `BatchStackingService` covers the general case.
7. Awaits exit. Exit 0 = look for `result*.fit` in the work dir, move it to `{rig}/siril/{target}/result_{timestamp}.fit`. Exit != 0 = record `LastError` (last 500 chars of stderr).
8. Clean up the work dir after success (or keep it on failure, for debug).

**Bundled scripts**: shipped in `src/NINA.Headless/Resources/SirilScripts/`. Full coverage of the preprocessing matrix + the 2 most common extraction ones:

**OSC (one-shot color / DSLR)**:
- `OSC_Preprocessing.ssf` (full pipeline: bias + dark + flat)
- `OSC_Preprocessing_WithoutDark.ssf` (no darks)
- `OSC_Preprocessing_WithoutFlat.ssf` (no flats, useful when you got back from a trip without flats)
- `OSC_Preprocessing_WithoutDBF.ssf` (no dark + bias + flat, raw stacking only)

**Mono (LRGB / narrowband filter-wheel)**:
- `Mono_Preprocessing.ssf`
- `Mono_Preprocessing_WithoutDark.ssf`
- `Mono_Preprocessing_WithoutFlat.ssf`
- `Mono_Preprocessing_WithoutDBF.ssf`

**Extraction**:
- `OSC_Extract_HaOIII.ssf` (dual-narrowband split, useful for DSLR with L-eXtreme/L-Ultimate filter)

9 scripts total, copied from the official Siril repository with attribution. The "Without*" matrix covers the "forgot the flats" / "haven't shot good darks yet" / "I just want to stack what I have" scenarios without the user having to edit `.ssf` by hand.

Additional scripts are up to the user (read from `~/.siril/scripts` or `%APPDATA%/siril/scripts`, where they probably already have DSA-HubbleMatic, RGB_Composition, BayerDrizzle, etc.).

**`EnumerateScripts()`**: combines bundled + user-scripts dir (detected per OS). Each `SirilScriptInfo` carries the `Source` flag so the UI knows whether it's bundled or user.

### Service 3: `Services/External/GraXpertService.cs`

Singleton. GraXpert **v3.0+** exposes three operations via CLI, `background-extraction`, `deconvolution`, and `denoising`. The API handles all three under the same service with an operation discriminator, instead of bloating the codebase with three parallel services that share 90% of the code.

API:

```csharp
public class GraXpertService {
    bool IsAvailable { get; }
    string? BinaryPath { get; }
    string GraXpertVersion { get; }
    bool SupportsDeconvolution { get; }    // v3.0+
    bool SupportsDenoising { get; }        // v3.0+

    Task<GraXpertResult> ProcessFrameAsync(string inputPath,
                                            GraXpertOptions opts, CancellationToken ct);
    GraXpertBatchJob StartBatchAsync(GraXpertBatchRequest req, CancellationToken ct);
    GraXpertBatchJob? GetJob(string jobId);
}

public enum GraXpertOperation {
    BackgroundExtraction,   // -cmd background-extraction (all versions)
    Deconvolution,          // -cmd deconvolution         (v3.0+)
    Denoising               // -cmd denoising             (v3.0+)
}

public record GraXpertOptions(
    GraXpertOperation Operation = GraXpertOperation.BackgroundExtraction,
    // BGE-specific
    string Correction = "Subtraction",     // "Subtraction" | "Division"
    double Smoothing = 1.0,                // 0..1
    bool SaveBackground = false,
    // Deconvolution-specific
    double DeconStrength = 0.5,            // 0..1 sharpening intensity
    double DeconPsfSize = 4.0,             // pixels, PSF FWHM estimate
    // Denoising-specific
    double DenoiseStrength = 0.5,          // 0..1 noise reduction
    // Universal
    string? AiVersion = null);             // pin model version; null = latest

public record GraXpertResult(string OutputPath, string? BackgroundPath,
                              GraXpertOperation Operation,
                              double ElapsedSeconds, string? Error);

public record GraXpertBatchRequest(List<string> InputPaths,
                                    string OutputDir,
                                    GraXpertOptions Options,
                                    int Concurrency = 1);

public record GraXpertBatchJob(string JobId, GraXpertOperation Operation,
                                int Total, int Done, int Failed,
                                List<string> CurrentlyProcessing,
                                List<GraXpertResult> Results);
```

**Version detection**: parse `graxpert --version`. If major >= 3, expose `SupportsDeconvolution = true` + `SupportsDenoising = true`. The UI gates the corresponding buttons on these flags, under GraXpert 2.x only BGE shows up.

**Output naming convention**, suffix changes per operation so they don't clobber each other:
- BGE: `{stem}_bge.fits` → folder `{rig}/bge/{target}/`
- Decon: `{stem}_decon.fits` → folder `{rig}/decon/{target}/`
- Denoise: `{stem}_denoise.fits` → folder `{rig}/denoise/{target}/`

Allows chaining (decon after denoise becomes `{stem}_denoise_decon.fits`) without ambiguity.

**`ProcessFrameAsync` flow** (per operation):

| Op | CLI args |
|---|---|
| `background-extraction` | `"{input}" -cli -cmd background-extraction -output "{output}" -correction {Correction} -smoothing {Smoothing}` + optional `-bg "{bgPath}"` |
| `deconvolution` | `"{input}" -cli -cmd deconvolution -output "{output}" -strength {DeconStrength} -psfsize {DeconPsfSize}` |
| `denoising` | `"{input}" -cli -cmd denoising -output "{output}" -strength {DenoiseStrength}` |

Universal args (every op accepts): `-ai_version {AiVersion}` if set.

Rest of the flow identical to the original BGE, no real-time progress, decides success by exit code + `File.Exists(output) && length > 0`.

**`StartBatchAsync`**: runs `ProcessFrameAsync` sequentially (or with `Concurrency > 1` if the machine can handle it, decon/denoise AI models eat even more RAM than BGE, ~6-8 GB). Default `Concurrency=1`. Reports `Done/Total` plus `Operation` in the payload so the UI knows which job is which.

**Important decision: when to use each operation**:
- **BGE**: per-frame (during capture) makes sense, each frame's gradient differs
- **Decon + Denoise**: typically **post-stack**, applying them to every light degrades SNR (denoise too early) or amplifies noise (decon too early). The UI signals this: decon/denoise are available in STUDIO on the integrated master, NOT in Autorun End Events.
- The auto-during-capture (`SequenceEndActions.AutoGraXpert`) only fires BGE. Decon/denoise never run automatically, always manual in STUDIO.

### Integration with SequenceEngine (auto-GraXpert during capture)

`Services/SequenceEngine.cs` lines 246-247 already has the splice point: right after `_imageWriter.SaveImage(...)`. Add:

```csharp
// Post-capture hook, fire async, don't block next frame
if (_endActions?.AutoGraXpert == true && _graXpert.IsAvailable
    && imageType.Equals("LIGHT", StringComparison.OrdinalIgnoreCase)) {
    _ = Task.Run(() => _graXpert.ProcessFrameAsync(savedPath, null, ct));
}
```

**Where the toggle lives**: `SequenceEndActions` (record in `SequenceEngine.cs`) gains a new `bool AutoGraXpert` field. Checkbox in the End Events panel of the Autorun tab (the infra for that panel already exists).

**"Fire and forget" mode**: the next exposure doesn't wait for GraXpert to finish (which takes ~10s per frame). If the stack is kicked off before BGE finishes, it picks up the original frame. Explicit trade-off, performance > purity. If you want blocking mode, add a separate checkbox.

### Preferred pipeline: Siril replaces Studio C# by default

`Services/Studio/CalibrationService.cs` and `BatchStackingService.cs` remain as **fallback**. `StudioEndpoints` gains dispatch logic:

```csharp
g.MapPost("/integrate", async (BatchStackingService batch, SirilService siril,
                                IntegrateRequest req) => {
    if (siril.IsAvailable && req.Engine != "studio") {
        return await siril.RunScriptAsync(BuildSirilRequest(req));
    }
    return batch.StartAsync(req);   // existing path, unchanged
});
```

`req.Engine` is optional: `"siril"` forces Siril (error if absent), `"studio"` forces the C# pipeline, absent = preferred (Siril if available). Frontend shows which engine will run and allows override.

### New endpoints

| Method | Route | Behavior |
|---|---|---|
| GET | `/api/siril/status` | `{ available, binaryPath, version, scriptsCount }` |
| GET | `/api/siril/scripts` | `[{ name, path, source }]` |
| POST | `/api/siril/run` | Body: `SirilJobRequest` → 202 + `{ jobId }` |
| GET | `/api/siril/jobs/{jobId}` | Status + progress |
| POST | `/api/siril/jobs/{jobId}/cancel` | Cancels a running job |
| GET | `/api/graxpert/status` | `{ available, binaryPath, version, supportsDeconvolution, supportsDenoising }` |
| POST | `/api/graxpert/run` | Body: `{ paths: string[], options: { operation, ... } }` → 202 + `{ jobId }`. `operation` ∈ `"background-extraction" \| "deconvolution" \| "denoising"`. |
| GET | `/api/graxpert/jobs/{jobId}` | Status + per-frame progress + which operation |
| POST | `/api/graxpert/jobs/{jobId}/cancel` | Cancels the batch |

**Settings endpoint** extends `/api/system/settings` with:
- `sirilPath` (string, optional, override the auto-detect)
- `graxpertPath` (string, optional)
- `sirilScriptsDir` (string, optional, adds an extra folder to enumeration)
- `graxpertBgeSmoothing` (double, default 1.0)
- `graxpertBgeCorrection` (string, default "Subtraction")
- `graxpertDeconStrength` (double, default 0.5)
- `graxpertDeconPsfSize` (double, default 4.0)
- `graxpertDenoiseStrength` (double, default 0.5)

### UI

**New panel in SETTINGS tab**: "External tools"
- Siril row: status (✓ Detected v1.4.2 at `/usr/bin/siril-cli` | ✗ Not found), optional path input, "Re-detect" button, counter "5 bundled + 3 user scripts"
- GraXpert row: status (✓ Detected v3.0.2, BGE + Decon + Denoise | ✗ Not found). Collapsible sub-section with default sliders per operation (BGE smoothing, Decon strength + PSF size, Denoise strength)
- "Download instructions" link for both

**STUDIO tab**: three new toolbar buttons when GraXpert is available, grouped under a "Process with GraXpert" dropdown menu to avoid clutter:
- 🌅 **Remove gradient (BGE)**, always available
- ✨ **Deconvolution**, disabled if `graxpert.supportsDeconvolution === false` (version < 3.0)
- 🔇 **Denoise**, disabled if `graxpert.supportsDenoising === false`

Each item opens the same generic GraXpert processing modal (different fields depending on the selected op). Decon and Denoise come with an explicit "Best applied to integrated masters, not individual lights" warning if the user tries to run them on lights.

Separate "Stack with Siril" button on the toolbar (visible if `siril.available`). Click opens a modal:
- Script dropdown (bundled + user, grouped)
- Selected frames (already comes from the current STUDIO selection)
- Toggle "Inject GraXpert BGE between calibration and stack" (if GraXpert is also available)
- Start button → real-time progress

**FILES tab**: "Run GraXpert" button on the toolbar when ≥1 FITS is selected opens the generic modal with an operation selector (BGE / Decon / Denoise, disabled per support). Output goes to `{file_dir}/{op}/` (sibling) or `{rig}/{bge|decon|denoise}/` if inside ImageOutputDir.

**AUTORUN tab** (existing End Events panel): new checkbox "Auto-extract gradient with GraXpert", only enabled if GraXpert is detected. **BGE only**, decon/denoise never run automatically during capture (they'd degrade per-frame SNR; always post-stack).

### Status WebSocket

`StatusStreamHandler` already publishes active jobs from `BatchStackingService`. Extend to include:
- `sirilJobs`: list of active Siril jobs (jobId, stage, percent, target)
- `graxpertJobs`: list of active batches (jobId, done/total)

UI renders an icon in the header status bar (next to INDI/PHD2/ALPACA) when something is running.

## Files to create

- `src/NINA.Headless/Services/External/BinaryLocator.cs` (~80 lines)
- `src/NINA.Headless/Services/External/SirilService.cs` (~350 lines)
- `src/NINA.Headless/Services/External/GraXpertService.cs` (~250 lines)
- `src/NINA.Headless/Endpoints/SirilEndpoints.cs` (~120 lines)
- `src/NINA.Headless/Endpoints/GraXpertEndpoints.cs` (~100 lines)
- `src/NINA.Headless/Resources/SirilScripts/` (5 .ssf files + LICENSE)
- `tests/NINA.Headless.Test/BinaryLocatorTests.cs` (path resolution priority, OS-specific)
- `tests/NINA.Headless.Test/SirilServiceTests.cs` (script enumeration, work-dir layout, arg-builder, stdout parser, output collection, mock binary)
- `tests/NINA.Headless.Test/GraXpertServiceTests.cs` (arg-builder, batch concurrency, error propagation)
- `docs/siril-setup.md` (install + script location per OS)
- `docs/graxpert-setup.md` (install + first-run model download)

## Files to modify

- `src/NINA.Headless/Program.cs`, register `SirilService` + `GraXpertService` as singletons; map the new endpoints
- `src/NINA.Headless/Services/ProfileService.cs`, `UserProfile`: `SirilPath`, `GraXpertPath`, `SirilScriptsDir`, `GraXpertSmoothing`, `GraXpertCorrection`
- `src/NINA.Headless/Services/SequenceEngine.cs`, `SequenceEndActions.AutoGraXpert`; post-save hook that fires `_graXpert.ProcessFrameAsync` in fire-and-forget
- `src/NINA.Headless/Endpoints/StudioEndpoints.cs`, `/integrate` gains Siril/C# dispatch; new `/integrate-with-siril` endpoint
- `src/NINA.Headless/Endpoints/SequenceEndpoints.cs`, `/end-actions` PUT accepts `autoGraXpert`
- `src/NINA.Headless/WebSocket/StatusStreamHandler.cs`, include `sirilJobs` + `graxpertJobs` in the payload
- `src/NINA.Headless/wwwroot/index.html`, Settings panel "External tools" section; STUDIO "Stack with Siril" button + modal; FILES "Run GraXpert" button; Autorun End Events checkbox
- `src/NINA.Headless/wwwroot/js/app.js`, state `siril: { available, scripts, jobs }`, `graxpert: { available, jobs }`; methods `loadSirilStatus`, `runSirilScript`, `runGraXpertBatch`, etc.
- `src/NINA.Headless/wwwroot/css/app.css`, `.external-tool-row`, `.siril-modal`, `.graxpert-progress` styles
- `README.md`, "External tools (Siril + GraXpert)" section linking to the 2 new docs

## Reuse of existing code

- `src/NINA.Headless/Services/PlateSolving/AstapSolver.cs:23-157`, Process.Start with timeout pattern, arg building, output parsing. Copy.
- `src/NINA.Headless/Services/PHD2ProcessManager.cs:155-192`, `EnumerateCandidatePaths()` pattern for cross-platform detection. Generalize into `BinaryLocator`.
- `src/NINA.Headless/Services/Studio/BatchStackingService.cs:37-67`, `IntegrationProgress` keyed by jobId, mutable record updated in background. Apply the same pattern to Siril/GraXpert jobs.
- `src/NINA.Headless/Services/AutoFocusService.cs:56-60,102`, progress reporting via mutable state accessed by the polling endpoint. Same pattern.
- `src/NINA.Headless/Services/SequenceEngine.cs:246-247`, splice point for the auto-GraXpert hook
- `src/NINA.Headless/Services/Studio/FrameLibraryService.cs:96`, after GraXpert/Siril produces output, call `RescanAsync()` to index into SQLite (automatic Studio refresh)
- `src/NINA.Headless/Services/ImageWriterService.cs:250`, `BuildSubDir` pattern; extend to new types "SIRIL" and "BGE" mapping to the new folders
- `src/NINA.Headless/Endpoints/StudioEndpoints.cs:12-17`, pattern for endpoint that returns 202 + jobId for long-running tasks
- `src/NINA.Headless/WebSocket/StatusStreamHandler.cs`, broadcast pattern (already delivers other job updates)

## Implementation phases

1. **P1: BinaryLocator + SirilService skeleton**, detection, script enumeration, basic execution with 1 bundled script (OSC_Preprocessing). No GraXpert yet. Tests cover detection and script lookup.
2. **P2: Siril UI integration**, Settings panel external tools row + STUDIO "Stack with Siril" button + modal + endpoint dispatch. Real-time progress over WS.
3. **P3: Bundled scripts**, add the remaining 4 .ssf to `Resources/SirilScripts/`. Dropdown updates automatically.
4. **P4: GraXpertService (BGE) + manual batch**, base service with BGE only, version detection, endpoint, "Run GraXpert" button on the FILES tab, selection + estimate modal, output in `{rig}/bge/`.
5. **P5: GraXpert Deconvolution + Denoise (v3.0+)**, extend `GraXpertService` with the two new operations, sliders in Settings, three items in the STUDIO dropdown menu, output in `{rig}/decon/` + `{rig}/denoise/`. "Best for masters" warning when the user runs on lights.
6. **P6: Auto-GraXpert BGE in Autorun**, `SequenceEndActions.AutoGraXpert` + checkbox + hook in SequenceEngine. Fire-and-forget during capture. BGE only.
7. **P7: Studio Siril+GraXpert combo**, "Inject GraXpert BGE between calibration and stack" toggle in the Siril modal; chained pipeline.
8. **P8: Docs + README**, `docs/siril-setup.md`, `docs/graxpert-setup.md` (including a section on the 3 operations + when to use each), README section.

## End-to-end verification

1. **Build + tests**: `dotnet build` + `dotnet test`, all existing tests (346) stay green + ~25 new = ~371 total
2. **Without Siril/GraXpert installed**:
   - Settings panel shows "Not detected" for both
   - STUDIO "Stack with Siril" button hidden
   - Autorun checkbox "Auto-extract gradient" disabled with tooltip
   - `BatchStackingService` (C# fallback) keeps working normally
3. **With Siril installed** (Windows: `winget install Siril.Siril`; Linux: `sudo apt install siril`):
   - Re-detect Settings → ✓ + version
   - Scripts list shows the 5 bundled
   - Point `sirilScriptsDir` at `%APPDATA%/siril/scripts` → user scripts appear
   - STUDIO "Stack with Siril" → pick OSC_Preprocessing → select 20 M81/M82 lights + darks/flats → run → real-time progress → result in `{rig}/siril/M81/result_*.fit`
   - Output opens in the FILES viewer (already exists, FITS RGB renderer covers it)
4. **With GraXpert v2.x installed**:
   - Re-detect Settings → ✓ + version + "BGE only" (decon/denoise badges grayed)
   - FILES → select 1 FITS → "Run GraXpert" modal → only available op is BGE → 30s later the sibling `_bge.fits` appears
   - STUDIO menu shows only "🌅 Remove gradient", other items disabled with tooltip "Requires GraXpert v3.0+"
5. **With GraXpert v3.0+ installed**:
   - Re-detect Settings → ✓ + version + "BGE + Decon + Denoise" badges
   - STUDIO selects M81 integrated master → menu → "✨ Deconvolution" → modal with strength + PSF size sliders → start → `master_decon.fits` in `{rig}/decon/M81/`
   - STUDIO same master → "🔇 Denoise" → strength slider → start → `master_denoise.fits` in `{rig}/denoise/M81/`
   - STUDIO selects 5 lights → menu → "🔇 Denoise" → modal shows warning "Best applied to integrated masters" → user confirms anyway → runs
   - FILES → select 10 FITS → op modal → confirm → progress 10/10 + indicator of which operation
6. **Auto-GraXpert (BGE) during sequence**:
   - Autorun End Events panel → enable "Auto-extract gradient"
   - Start a 10-light sequence
   - Each light lands in `{rig}/lights/...` + fires GraXpert BGE in background → `{rig}/bge/{target}/light_001_bge.fits` shows up ~10s later without blocking the next exposure
   - Server log shows `FileOp GraXpert BGE input.fits → bge/light_001_bge.fits` per frame
   - Confirm that decon/denoise are **not** offered in Autorun (BGE only)
7. **Full chained pipeline**:
   - STUDIO → select 20 lights → "Stack with Siril" → toggle "Inject GraXpert BGE" ON → Polaris first runs GraXpert BGE on each one (10 × 30s = 5 min), then runs the Siril script on the BGE result → final master in `{rig}/siril/M81/`
   - STUDIO on the generated master → "✨ Deconvolution" → result in `{rig}/decon/M81/master_decon.fits`
   - STUDIO on the decon → "🔇 Denoise" → result in `{rig}/denoise/M81/master_decon_denoise.fits` (chained suffixes)
8. **Graceful cancellation**:
   - Start a long Siril job → POST `/api/siril/jobs/{id}/cancel` → process receives SIGTERM (Linux) or taskkill /T (Windows) → work dir preserved for debug

## Compatibility, license, and security notes

- **Siril**: GPLv3. Polaris only invokes via CLI, no code linking. Bundled scripts are GPLv3 too (from the official repo); include LICENSE in `Resources/SirilScripts/`.
- **GraXpert**: GPLv3. Same pattern (CLI-only). ML models embedded in the GraXpert distribution itself, Polaris doesn't touch them.
- **Cross-platform**: both have Linux + Windows + macOS builds. Detection covers all three (Windows: Program Files + winget paths; Linux: /usr/bin, /opt, snap, flatpak; macOS: /Applications).
- **Concurrency limits**: GraXpert AI uses a lot of RAM (~3-6GB per instance). Default `Concurrency=1`; user can bump it up on a beefier machine.
- **Work dir cleanup**: successful jobs clean their work dir. Failed jobs preserve it for debug (with path logged). Optional housekeeping cron to clean work dirs > 7 days old.
- **Atomicity**: GraXpert and Siril write the full output before moving to the final destination (write-temp + atomic rename). Prevents the Studio rescan from picking up a half-written file.
- **Auto-detection blast radius**: at startup, `SirilService` + `GraXpertService` run `siril-cli --version` / `graxpert --version` in the background (doesn't block startup). If one of them hangs on execution, 5s timeout + warning log.

---

# Previous plan: FILES tab (file explorer + Studio root picker + downloads)

## Context

Polaris today has no way for the user to navigate the filesystem of the device running the server. That blocks three common workflows:

1. **Picking the STUDIO root folder**, the only way today is to hand-type the path into a text field in Settings, with no idea whether the folder exists or what the surrounding structure looks like. A typical astrophotographer keeps output on an external SSD (`/mnt/astro` or `D:\Astrofotos`) and wants to point STUDIO there visually.
2. **Downloading images from the server to the client**, after a session, the user wants to pull FITS/XISF/the final master to a laptop/phone for processing offline. Today they have to `scp` or mount SMB.
3. **Doing housekeeping**, deleting old darks, moving masters to archive, checking how much space is left on the SSD. All done today via SSH/RDP.

**Goal**: a **FILES** tab with a full file explorer (inline preview, cut/copy/paste, multi-select, download), which also serves as the single place to point the STUDIO root folder.

**Decisions confirmed with the user**:
- **FS scope**: the whole disk (full filesystem). Main motivation: the user stores images on flash drives and external SSDs that wouldn't fit in a fixed sandbox. Mitigation: destructive actions (delete, paste-overwrite, cross-volume move) require **double confirmation**, and every destructive operation lands in the server log.
- **Inline preview**: FITS/XISF (via `FrameLibraryService.GetThumbnailAsync` reused) + PNG/JPG/TIFF (SkiaSharp) + text preview for `.txt`/`.log`/`.json`/`.md` (first ~32KB).
- **Multi-download**: a single ZIP streamed via `System.IO.Compression.ZipArchive`, written straight to the response stream, doesn't accumulate in memory, handles hundreds of FITS.
- **Settings**: the "Image output directory" field leaves the SETTINGS panel and becomes accessible only via the FILES tab (right-click a folder → "Set as Studio root", or a "Set as Studio root" button on the toolbar when a folder is selected).

## Architecture

### New backend: `Services/FileBrowserService.cs`

Singleton with no persisted mutable state. All disk I/O goes through here (the endpoint can't call `File.*` directly, I want a single point for logging + double-confirmation + sanitization).

```csharp
public class FileBrowserService {
    public sealed record DirEntry(
        string Name, string FullPath, bool IsDirectory,
        long SizeBytes, DateTime ModifiedUtc, string? Mime,
        bool IsHidden, bool IsReadOnly
    );
    public sealed record DriveInfoDto(
        string Name, string DisplayName, string? VolumeLabel,
        long? TotalBytes, long? FreeBytes, string DriveFormat
    );

    IReadOnlyList<DriveInfoDto> ListRoots();
    IReadOnlyList<DirEntry> List(string path, bool showHidden);
    DirEntry? Stat(string path);                              // single file metadata
    Stream OpenRead(string path);                             // for download/preview
    Task CopyAsync(string src, string dst, bool overwrite, CancellationToken ct);
    Task MoveAsync(string src, string dst, bool overwrite, CancellationToken ct);
    Task DeleteAsync(string path, bool recursive, CancellationToken ct);
    Task CreateFolderAsync(string parentPath, string name);
    Task RenameAsync(string path, string newName);
    Task<long> WriteZipAsync(IEnumerable<string> sources, Stream destination,
        string? commonRootForRelativeNames, CancellationToken ct);  // streaming ZIP
}
```

**Mime detection**: a small `ExtensionToMime` table inside the service, `.fits/.fit/.fts` → `image/fits`, `.xisf` → `image/x-xisf`, `.png/.jpg/.tiff/.txt/.log/.json/.md` → standard. For anything else it returns `application/octet-stream`.

**Path safety**: even with the whole FS unlocked, it sanitizes:
1. `Path.GetFullPath(userInput)`, normalize, expand, resolve `..`
2. Block any path that `StartsWith` a blocklist prefix (`C:\Windows\System32`, `/proc`, `/sys`, `/dev`, `/etc/shadow`, `~/.ssh` by filename, etc.). Configurable in code, hardcoded for now.
3. Destructive operations on a path that doesn't satisfy `RequireConfirm(path)` return 409, the UI has to resend with `?confirmed=true`.

**Cross-volume cut/paste**: `MoveAsync` attempts `File.Move`/`Directory.Move`; on cross-volume `IOException` it automatically falls back to `Copy` + `Delete` (preserves ctime when possible).

**Logging**: every `DeleteAsync` / `MoveAsync` / `WriteZipAsync` calls `_logger.LogInformation` with `EventId = "FileOp"` + path before/after + size. Goes to the same server log file.

### New endpoints: `Endpoints/FilesEndpoints.cs`

Same pattern as `StudioEndpoints.cs` (`MapGroup("/api/files")`).

| Method | Route | Behavior |
|---|---|---|
| GET | `/roots` | List of drives (Windows) or `/`, `/mnt`, `/media`, `$HOME` (Linux) |
| GET | `/list?path=...&hidden=false` | Lists directory entries |
| GET | `/stat?path=...` | Metadata for a single item |
| GET | `/download?path=...` | Streams a file, `Content-Disposition: attachment; filename=...` |
| GET | `/preview?path=...&maxDim=1600` | Renders JPEG/PNG/TXT preview. FITS/XISF goes through `FrameLibraryService` (factored out, see below). PNG/JPG served directly. TIFF decoded via SkiaSharp. Text returns `text/plain` truncated. |
| GET | `/thumb?path=...` | 256px thumbnail, same logic as preview with cache at `{AppData}/files/thumbs/{md5(path)}.jpg` |
| POST | `/download-zip` | Body: `{ paths: string[], rootForNames?: string }`. Response: streaming ZIP. |
| POST | `/copy` | Body: `{ src, dst, overwrite }`. 409 if destination exists and `overwrite=false`. |
| POST | `/move` | Same shape. |
| POST | `/delete` | Body: `{ paths: string[], confirmed: bool }`. 409 if `!confirmed`. |
| POST | `/mkdir` | Body: `{ parent, name }` |
| POST | `/rename` | Body: `{ path, newName }` |
| POST | `/studio-root` | Body: `{ path }`, shortcut that writes `profile.ImageOutputDir = path` and triggers a `FrameLibraryService` rescan. |

### Required refactor in STUDIO

`FrameLibraryService.GetThumbnailAsync(int frameId)` today takes an **id** (DB lookup). For the FILES tab I need the same logic taking an **arbitrary path** (the image might not even be indexed yet, it's a master that came out of PixInsight in some random directory).

Extract a static method in `Services/Studio/FitsThumbnailer.cs`:
```csharp
public static class FitsThumbnailer {
    public static byte[] RenderJpegFromPath(string fitsOrXisfPath, int maxDim);
    public static byte[] RenderJpegFromBuffer(ushort[] px, int w, int h, int maxDim);
}
```

`FrameLibraryService.GetThumbnailAsync` delegates to it. Zero behavior change, just extraction for reuse by the new `FilesEndpoints`.

### Remove from Settings panel

`src/NINA.Headless/wwwroot/index.html` ~line 2740: "Image Output Directory" field → delete the whole `<section>` (not just the input, the entire "Image Output" section leaves Settings). The help text stays: replace with a short banner "Set the root folder from the file explorer (FILES tab)" + a clickable link that switches `tab='files'`.

`app.js` line 1088: still **reads** `imageOutputDir` from the server (to show the current folder in other places), but the **save** goes only through POST `/api/files/studio-root`.

## Frontend

### Sidebar nav button

In `index.html` line ~129 (between STUDIO and Settings):

```html
<button class="nav-btn" :class="{ active: tab === 'files' }"
        @click="tab = 'files'; filesInit()" title="Files">
    <svg viewBox="0 0 24 24" width="22" height="22">
        <path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7z"
              fill="none" stroke="currentColor" stroke-width="2" stroke-linejoin="round"/>
    </svg>
    <span class="nav-label">Files</span>
</button>
```

### Tab layout

```
┌─────────────────────────────────────────────────────────────────┐
│ [drive ▼]  /home/dan/astro/M31/L                          ⟳    │  ← toolbar
│ Path crumbs: home › dan › astro › M31 › L                       │
├─────────────────────────────────────────────────────────────────┤
│ [+ New folder] [↑ Upload] [⬇ Download] [✂ Cut] [📋 Copy]        │  ← actions
│ [📥 Paste] [✏ Rename] [🗑 Delete] [⭐ Set as Studio root]       │
├─────────────────────────────────────────────────────────────────┤
│ ☐ Name                Size      Modified              Type      │
│ ☐ 📁 ..                                                          │
│ ☐ 📁 darks           ,         2026-05-15 23:00     Folder    │
│ ☑ 🖼 M31_001.fits     62.4 MB   2026-05-21 22:14     FITS      │
│ ☑ 🖼 M31_002.fits     62.4 MB   2026-05-21 22:18     FITS      │
│ ☐ 📄 session.log      4.2 KB    2026-05-21 23:55     Log       │
└─────────────────────────────────────────────────────────────────┘
[Selection: 2 files · 124.8 MB]   [Studio root: /home/dan/astro] ⭐
```

- **Crumbs**: each level clickable → changes path.
- **List**: table with a per-row checkbox + shift-click for range selection. Double-click on a folder = enter. Double-click on a file = preview (or download, if no preview is available). Right-click = context menu with the same actions as the toolbar.
- **Clipboard chip** above the toolbar when there's a pending cut/copy: `📋 3 items copied from /home/dan/astro/darks · [Clear]`. Paste shows valid targets in the current directory.
- **Bottom status bar**: count + total size + "Studio root: ⭐" indicator.

### State in `app.js`

```js
files: {
    cwd: '',                         // current working dir (server-side path)
    entries: [],                     // DirEntry[]
    roots: [],                       // drives/mount points
    showHidden: false,
    selectedPaths: [],               // array of full paths (multi-select)
    clipboard: null,                 // { mode: 'cut'|'copy', paths: [], sourceDir }
    sortBy: 'name',                  // name|size|modified|type
    sortDir: 'asc',
    loading: false,
    error: '',
    studioRoot: '',                  // mirrors profile.imageOutputDir
    preview: { open: false, path: '', kind: '', textContent: null }
},

async filesInit() { /* load roots + cd to studioRoot || home */ },
async filesCd(path) { /* GET /api/files/list, update cwd + entries */ },
filesToggleSelect(path, event) { /* shift-click range, ctrl-click toggle */ },
async filesCopy() { /* clipboard = { mode:'copy', paths:selected, sourceDir:cwd } */ },
async filesCut() { /* clipboard = { mode:'cut', ... } */ },
async filesPaste() { /* POST /copy or /move per clipboard.mode */ },
async filesDelete() { /* confirm() then POST /delete?confirmed=true */ },
async filesDownload() { /* single = window.location = /download?path=...
                          multi = POST /download-zip, follow returned URL */ },
async filesUpload(fileList) { /* multipart POST per file to /api/files/upload */ },
async filesMkdir() { /* prompt + POST /mkdir */ },
async filesRename(entry) { /* inline edit or prompt + POST /rename */ },
async filesSetStudioRoot() { /* confirm → POST /studio-root → toast + update studioRoot */ },
filesOpenPreview(entry) { /* fetch /preview, show in modal */ },
filesClosePreview() { /* mode === 'fits' uses existing OpenSeadragon */ },
```

### Preview modal (reuses OpenSeadragon)

- FITS/XISF/TIFF/PNG/JPG → opens the existing `image-viewer-modal` (`app.js:openImageViewer()`) with `tileSources.url = /api/files/preview?path=...`. Zero new modal.
- TXT/LOG/JSON/MD → a simple new modal `files-text-preview-modal` (style of `location-setup-modal`): `<pre>` with the first 32KB, Download button below.

### Destructive confirmations

Simple pattern reusing `window.confirm()` (already the app's default):
- Delete: `confirm("Delete ${N} item(s)? This action is irreversible.")`
- Paste overwrite: `confirm("${N} file(s) already exist at the destination. Overwrite?")`
- Cross-volume move: silent notice only in the post-op toast ("moved across volumes, copied then deleted source").

## Files to create / modify

### Create
- `src/NINA.Headless/Services/FileBrowserService.cs` (~350 lines)
- `src/NINA.Headless/Services/Studio/FitsThumbnailer.cs` (~80 lines, extracted)
- `src/NINA.Headless/Endpoints/FilesEndpoints.cs` (~250 lines)
- `tests/NINA.Headless.Test/FileBrowserServiceTests.cs` (path safety, ZIP streaming, cross-volume move fallback, mime detection)
- `tests/NINA.Headless.Test/FitsThumbnailerTests.cs` (small golden cases, makes sure the extraction didn't regress)

### Modify
- `src/NINA.Headless/Services/Studio/FrameLibraryService.cs`, `GetThumbnailAsync` delegates to the new `FitsThumbnailer`
- `src/NINA.Headless/Program.cs` (~line 92), register `FileBrowserService` singleton + `app.MapFilesEndpoints()`
- `src/NINA.Headless/wwwroot/index.html`:
  - Sidebar: new FILES button between STUDIO and Settings
  - Settings panel (~line 2740): remove "Image Output" section, replace with a short banner
  - New full `<div x-show="tab === 'files'">` panel
  - New `files-text-preview-modal` modal
- `src/NINA.Headless/wwwroot/js/app.js`:
  - State `files: {...}`
  - Methods `filesInit/filesCd/filesToggleSelect/filesCopy/filesCut/filesPaste/filesDelete/filesDownload/filesUpload/filesMkdir/filesRename/filesSetStudioRoot/filesOpenPreview`
  - Remove the handler that saved `imageOutputDir` directly to `/api/system/settings`
- `src/NINA.Headless/wwwroot/css/app.css`:
  - `.files-*` classes (toolbar, crumbs, table, selection-bar, clipboard-chip, text-preview modal)
- `README.md`, new "File explorer" section + security note ("server listens on the LAN without auth, don't expose it to the internet without using the Relay with tokens")

### Reuse (already exist, should be referenced)
- `Services/Studio/FrameLibraryService.cs:307` `GetThumbnailAsync`, FITS → JPEG decoder pattern (we'll extract, not copy)
- `Endpoints/ImageEndpoints.cs:30`, `Results.File(stream, mime, fileDownloadName)` pattern
- `Endpoints/StudioEndpoints.cs:43-46`, endpoint that serves thumb with cache pattern
- `Services/ImageWriterService.cs:246,287` `SanitizeFileName` / `SanitizeFolder`, to validate names in `mkdir`/`rename` (doesn't cover path traversal, but helps with invalid chars)
- `Services/ProfileService.cs:295` `ImageOutputDir`, single source of truth for the Studio root
- `wwwroot/js/lib/openseadragon/openseadragon.min.js` + `app.js:openImageViewer()` (~line 3025), viewer reused for image preview
- `index.html` patterns: `location-setup-modal` (~line 2871) for the text-preview modal structure; sidebar buttons (lines 84-132) for the new button style
- `app.js toast(msg, type)`, operation feedback
- SkiaSharp (already in the project via STUDIO), TIFF decoding
- `System.IO.Compression.ZipArchive` (BCL, no new dependency), streaming ZIP

## Implementation order (separate commits)

1. **FB-1: Backend foundation**, `FileBrowserService` + extracted `FitsThumbnailer` + tests. Build green, 314 → ~330 tests.
2. **FB-2: Endpoints**, `FilesEndpoints` registered + smoke test via `curl` (list, stat, download). No UI yet.
3. **FB-3: Basic UI**, sidebar button + tab panel with list/crumbs/cd + status bar. Read-only.
4. **FB-4: Mutations**, cut/copy/paste/delete/mkdir/rename + confirmations. Toast feedback.
5. **FB-5: Preview + download**, preview modal (OpenSeadragon reuse + text modal) + single download + multi ZIP.
6. **FB-6: Studio root integration**, "Set as Studio root" action + remove field from Settings + banner with link.
7. **FB-7: Polish + docs**, README + sort/filter/hidden toggle + drag-drop upload.

## End-to-end verification

1. **Build + tests**: `dotnet build` + `dotnet test`, 314 current + ~16 new = ~330 green
2. **Path safety**:
   - `curl "http://localhost:5000/api/files/list?path=/etc/shadow"` → 403 (blocklist)
   - `curl "http://localhost:5000/api/files/list?path=../../etc"` (with arbitrary client cwd) → resolved via `Path.GetFullPath`; still passes through the blocklist
3. **Cross-volume cut**: file from `/home/dan/test.txt` → `/mnt/usb/test.txt` should move without error, log shows "moved across volumes via copy+delete"
4. **ZIP streaming**: select 50 FITS at 60MB each, download ZIP → client receives ~3GB without the server blowing up memory (RPi 4 has 2GB typical)
5. **Happy-path UI**:
   - FILES tab loads → shows `/home` or the last cwd
   - Click a folder → enter
   - Clickable crumbs work
   - Multi-select with shift-click marks a range
   - Cut + Paste into another dir → files disappear from source, appear at destination
   - Delete with 3 selected → confirm → gone
   - Click a FITS → OpenSeadragon modal opens with stretched preview
   - Click a `.log` → text modal shows the first 32KB
   - "Set as Studio root" on a folder → toast "Studio root set to ...", STUDIO tab re-indexes
6. **Settings cleanup**:
   - Settings tab no longer shows the "Image Output Directory" field
   - Shows banner "Set it from the file explorer" with clickable link → switches to FILES tab
7. **Destructive confirmation**:
   - Try to delete `/etc/passwd` (if you manage to bypass the blocklist) → confirm + log warning
   - Paste over existing file → "Overwrite?" modal
8. **Mobile**: responsive layout, table becomes a vertical list under 768px, toolbar wraps

## Compatibility, license, and security notes

- **System.IO.Compression**: BCL, no new dependency
- **SkiaSharp**: already in the project, covers TIFF
- **Cross-platform**:
  - Windows: roots = `DriveInfo.GetDrives()` filtered by `IsReady`
  - Linux: roots = `/`, `/home`, `/mnt`, `/media`, `~`. Detected via existence-check at startup.
- **Privacy**: no data leaves the server except via requests the client makes explicitly
- **Security**:
  - Add a prominent note to the README: "FILES tab exposes the server filesystem without authentication. Polaris assumes a trusted LAN. Don't expose the server directly to the internet, use the Relay (which has tokens)."
  - Initial hardcoded blocklist: `C:\Windows`, `C:\Program Files`, `/proc`, `/sys`, `/dev`, `/boot`, `/etc/shadow`, `/etc/sudoers`, `~/.ssh`, `~/.aws`, `~/.config/gh`. More can be added in follow-up.
  - Double confirmation in the UI + `?confirmed=true` flag on the endpoint for destructive operations
  - Logging for every destructive op: path before/after, size, timestamp, source IP of the request
- **Performance on the RPi**:
  - ZIP streaming doesn't accumulate in memory (test with 50×60MB FITS = ~3GB)
  - Directory listing with 10k+ files paginated (default 500/page, query param `?offset=&limit=`)
  - Disk-based thumbnail cache with implicit TTL (regenerate if path mtime is newer than cache mtime)

---
# Previous plan: DSLR / Mirrorless support

## Context

Polaris today only talks to dedicated astrophotography cameras via INDI (`IndiCamera`), the `EquipmentManager.Camera` property is concretely typed as `IndiCamera?`. DSLRs and mirrorless cameras (Canon, Nikon, Sony) are a big slice of new users, especially those who haven't yet moved to dedicated cooled cameras.

**Goal**: add support for DSLR / Mirrorless cameras covering the three most common brands, keeping Polaris cross-platform.

**Decisions confirmed with the user**:
- **Linux**: use the existing `indi_gphoto_ccd` driver (libgphoto2 wrapper → INDI). Zero new code on the host, the current INDI client consumes the BLOB.
- **Windows**: native per-vendor drivers, **Canon EDSDK**, **Nikon SDK**, **Sony Camera Remote SDK**. No libgphoto2 P/Invoke on Windows (libusb / Zadig vs WPD conflict, no maintained upstream Windows binary, all the .NET wrappers are abandoned).
- **RAW**: save the CR2/NEF/ARW file as-is in `{rig}/lights/{target}/{filter}/{session}/`; use the embedded JPEG for the live preview / sequence stream. Real demosaicing is left for the Studio tab (future extension).

## Architecture

### Fundamental change: introduce `ICamera`

`EquipmentManager.Camera` being typed as `IndiCamera?` (line 10 of `src/NINA.Headless/Services/EquipmentManager.cs`) is what blocks any other backend. Without an interface, every new driver becomes an `if (...) cast` in the `EquipmentManager`, in the endpoints, and in the `StatusStreamHandler`.

Solution: extract `ICamera` into `src/NINA.Image.Portable/Interfaces/ICamera.cs` (same place as `IImageData`):

```csharp
public interface ICamera {
    string DeviceName { get; }
    bool IsConnected { get; }
    CameraStates State { get; }
    double Temperature { get; }
    bool CoolerOn { get; }
    double CoolerPower { get; }
    int BinX { get; }
    int BinY { get; }
    int BitDepth { get; }
    int MaxX { get; }
    int MaxY { get; }
    double PixelSizeX { get; }
    double PixelSizeY { get; }
    int Gain { get; }
    IReadOnlyList<int> IsoOptions => Array.Empty<int>();
    int SelectedIso { get; }
    CameraCapabilities Capabilities { get; }  // supportsCooler/binning/iso/roi flags

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    Task<IImageData> CaptureAsync(double exposureSeconds, CaptureOptions? opts = null, CancellationToken ct = default);
    Task SetBinningAsync(int x, int y);
    Task SetTemperatureAsync(double celsius);
    Task SetCoolerAsync(bool on);
    Task SetIsoAsync(int iso);
    Task AbortExposureAsync();
}

public record CaptureOptions(int? Gain, int? Iso, int? BinX, int? BinY,
                              string? ImageType, string? Filter, string? TargetName);
public record CameraCapabilities(bool SupportsCooler, bool SupportsBinning,
                                  bool SupportsRoi, bool SupportsIso, bool SupportsBulb);
```

Current implementations (`IndiCamera`, `AlpacaCamera`) now implement this interface. Methods unsupported by the backend (e.g. cooler on a DSLR) become a no-op + debug log; the flags in `Capabilities` dictate what the UI shows.

### Discriminator in EquipmentProfile

`EquipmentProfile.Camera` today is just a `string?` (INDI name). Add:

```csharp
public string? CameraDriver { get; set; } = "indi";
// values: "indi" | "alpaca" | "canon-edsdk" | "nikon-sdk" | "sony-sdk"
public string? CameraDeviceId { get; set; }  // semantics depend on driver
```

Migration: null `CameraDriver` → assume `"indi"` + use legacy `Camera` field as `CameraDeviceId`. Handled in `ProfileService.cs:165+` (clone path) + on-load.

### Capture path: raw + JPEG via `IImageData`

DSLRs deliver two assets per capture: the RAW file (CR2/NEF/ARW) and an embedded JPEG. To fit into the current pipeline (`IImageData` → `ImageRelayService` → `ImageWriterService` → live stream):

1. **Windows driver** captures → SDK writes the RAW to a buffer + extracts the embedded JPEG (all 3 SDKs expose this).
2. **Driver builds `IImageData`** by decoding the JPEG via `SkiaSharp` (already in the project, used by `JpegHelper`) into a grayscale `ushort[]` (Rec.601 luminance via `BayerDebayer.ToLuminance`, or direct Gray8 → ushort `<< 8` conversion). Resolution = embedded JPEG resolution (~1620×1080 or ~1920×1280), not the sensor's full resolution.
3. **`MetaData.Camera.SensorWidthMm/HeightMm`** filled in with the real sensor size (lookup table per model via SDK).
4. **RAW file** attached via a new optional `IImageData.RawFileBytes` + `RawFileExtension` (or a separate interface `IHasRawFile` if we want to keep `IImageData` pure). `ImageWriterService` reads these fields and, when present, writes the `.cr2`/`.nef`/`.arw` instead of the `.fits`, same path structure as `BuildSubDir`.
5. **`IImageData`** flows through the normal pipeline → `ImageRelayService.RelayImageAsync` for the live preview, no FITS generated.

### Windows driver, vendor SDKs

For each SDK the strategy is the same:

1. **Thin P/Invoke wrapper** in a new `src/NINA.Camera.{Vendor}/` project. Net10.0, `<SupportedOSPlatform>windows</SupportedOSPlatform>`.
2. **Camera driver class** (`CanonEdsdkCamera : ICamera`) invoked by `EquipmentManager.SelectCamera(driver, deviceId)`.
3. **Native DLLs don't go in the repo**, user downloads them from the manufacturer's site and drops them in `plugins/{vendor}/`. Each EULA requires this; our P/Invoke wrapper is MPL 2.0. Setup UI shows download link + instructions.

**Canon EDSDK**, version 13.x or 14.x. Main APIs: `EdsInitializeSDK` / `EdsGetCameraList` / `EdsOpenSession` / `EdsSetPropertyData(kEdsPropID_ISOSpeed)` / `EdsSendCommand(kEdsCameraCommand_TakePicture)` / `kEdsObjectEvent_DirItemRequestTransfer` callback for download. Marshalling pattern: copy the style of existing MIT open-source wrappers, not the lib itself.

**Nikon SDK**, Nikon MAID SDK (classic DSLR) or Nikon Imaging SDK (Z series mirrorless). Cover Z first (cleaner API); MAID if there's demand. Requires registration at developer.nikonimaging.com.

**Sony Camera Remote SDK**, version 2.x (Windows + native Linux). developer.sony.com/imaging-products. `Init` / `EnumCameraObjects` / `ConnectAsync` / `SetDeviceProperty(ISO)` / `SendCommand(S1Shooting)`. Recent, decent docs.

### Live view / focus assist (future scope)

All 3 SDKs expose live view (~30 fps JPEG preview). Not in v1: V-curve focus needs real exposures, and EDSDK preview frames are saturated for focus measurement. Separate issue when demand shows up.

## Phases

### F1, Foundation: `ICamera` + driver discriminator

Without this nothing connects. Doesn't touch any real driver, just refactors.

- Create `src/NINA.Image.Portable/Interfaces/ICamera.cs` with the interface above.
- Refactor `src/NINA.INDI/Devices/IndiCamera.cs` to `IndiCamera : ICamera`.
- Refactor `src/NINA.Headless/Services/Alpaca/AlpacaCamera.cs` to `AlpacaCamera : ICamera`.
- Change `EquipmentManager.Camera` from `IndiCamera?` to `ICamera?` (line 10).
- `EquipmentProfile` (`ProfileService.cs:313+`) gains `CameraDriver` + `CameraDeviceId`. Migration when loading old profile.
- `EquipmentManager.SelectCamera(driver, deviceId)` does a switch by driver (only `"indi"` and `"alpaca"` for now).
- `CameraEndpoints` (`POST /api/camera/select/{deviceName}`) accepts optional `?driver=` query param, default `indi`.
- New `GET /api/camera/drivers` returns the list of drivers available for the current OS (`indi`, `alpaca` always; vendor drivers only on `OperatingSystem.IsWindows()` + DLL present).
- `StatusStreamHandler` stays unchanged (reads from `EquipmentManager.Camera` which is now `ICamera`).
- UI: driver dropdown above the camera select in the Equipment tab.
- Tests: the current 273 keep passing; new `ICameraContractTests` validates that `IndiCamera` and `AlpacaCamera` honor the contract (mock backends).

### F2, Linux DSLR doc-only path

No C# code. Add to a doc at `docs/dslr-linux.md`:

1. `sudo apt install indi-gphoto` (or `indi-3rdparty` from the PPA).
2. `indiserver indi_gphoto_ccd` (or auto via `indi-web`).
3. Connect the USB camera. INDI sees it as `GPhoto CCD` or by model.
4. In Polaris: driver=indi, pick the camera in the dropdown.

Verify that the existing INDI client delivers the JPEG/RAW BLOB correctly, `indi_gphoto_ccd` by default sends the RAW as a BLOB. Possible tweak in `IndiCamera.OnBlobReceived` to distinguish FITS vs RAW (don't decode RAW as FITS, pass it raw through the write path via `RawFileBytes`).

### F3, Canon EDSDK driver (Windows-only)

Most common on the market, biggest payoff, implement first.

New structure:

```
src/NINA.Camera.CanonEdsdk/
├── NINA.Camera.CanonEdsdk.csproj   # net10.0, Windows-only
├── Native/
│   ├── EdsdkNative.cs               # P/Invoke struct + entry points
│   └── EdsdkConstants.cs            # property/command/event IDs
├── CanonEdsdkCamera.cs              # ICamera implementation
├── CanonEdsdkDiscovery.cs           # static EnumerateCameras()
└── README.md                        # EULA + download steps
```

- **Discovery**: `CanonEdsdkDiscovery.EnumerateCameras()` returns `[{ id, model, serialNumber }]`. New endpoint `GET /api/equipment/cameras/canon-edsdk` delegates here.
- **Capture**: `CaptureAsync(seconds, opts)`:
  1. `EdsSetPropertyData(kEdsPropID_Tv, encode(seconds))` for shutter speed (DSLRs don't have arbitrary exposure, map to the nearest enum or use bulb if > 30s via `kEdsCameraCommand_BulbStart`/`BulbEnd`).
  2. `EdsSetPropertyData(kEdsPropID_ISOSpeed, mapIsoToEnum(opts.Iso))`.
  3. `EdsSetPropertyData(kEdsPropID_SaveTo, kEdsSaveTo_Host)` + `EdsSetCapacity` (SDK trick to force direct download without a card).
  4. `EdsSendCommand(kEdsCameraCommand_TakePicture)`.
  5. Wait for `kEdsObjectEvent_DirItemRequestTransfer` (callback registered at connection).
  6. `EdsCreateMemoryStream` + `EdsDownload` + `EdsDownloadComplete` → buffer with CR2 + embedded JPEG.
- **Raw bytes** attached to the returned `IImageData`.
- **JPEG → ushort[]**: SkiaSharp decode → Gray8 byte[] → expand to `ushort` (shift << 8, or Rec.601 conversion if RGB).
- **Live preview**: the resulting `IImageData` follows the normal path.
- **Cooler/binning are no-op + debug log**, `Capabilities.SupportsCooler = false` etc. `Temperature` returns `double.NaN`.

### F4, Nikon SDK driver (Windows-only)

Same template as F3, project `src/NINA.Camera.NikonSdk/`. Nikon Imaging SDK (Z series mirrorless) is cleaner than MAID, cover Z first, MAID later if there's demand.

### F5, Sony SDK driver (Windows + Linux)

Sony has Linux binaries! Project `src/NINA.Camera.SonySdk/`, cross-RID. Same structure.

### F6, UI: per-vendor connect flow

- Driver dropdown in the Equipment Camera card: INDI / Alpaca / Canon / Nikon / Sony (vendor items only where the SDK is detected).
- When user picks a vendor driver: the camera dropdown becomes a "Detect cameras" button that calls `/api/equipment/cameras/{driver}` and populates with `{model} (#{serial})`.
- If the SDK isn't installed, banner with download link + instructions on where to put the DLL.
- Connect button calls `POST /api/camera/select/{deviceId}?driver=canon-edsdk` + `POST /api/camera/connect`.
- Cooler / binning fields data-driven by `ICamera.Capabilities`, hidden when the driver is DSLR.
- ISO dropdown shows instead of Gain when `IsoOptions.Count > 0`.

### F7, Capture pipeline: raw on disk + live JPEG

- Extend `IImageData` (or separate interface `IHasRawFile`) with `byte[]? RawFileBytes` + `string? RawFileExtension`.
- `ImageWriterService.SaveImage` when it sees `RawFileBytes != null`: writes the raw to the right folder **instead of** calling FITSWriter. Keeps the canonical path (`{rig}/lights/{target}/{filter}/{session}/IMG_{seq:0000}.cr2`).
- Live preview path unchanged, `ImageRelayService.RelayImageAsync(imageData)` uses the decoded JPEG.
- Sequence engine + Live stack keep working (they operate on `IImageData.Data` which is the demosaiced JPEG ushort[]).

### F8 (deferred), Studio: RAW debayer + integration

Extend `BayerDebayer` to accept CR2/NEF/ARW via LibRaw. Out of scope now, becomes a separate issue. For now the RAW sits on disk waiting for the user to process it in PixInsight/Siril.

## Critical files

**Create**:
- `src/NINA.Image.Portable/Interfaces/ICamera.cs`
- `src/NINA.Camera.CanonEdsdk/` (new project, F3)
- `src/NINA.Camera.NikonSdk/` (new project, F4)
- `src/NINA.Camera.SonySdk/` (new project, F5)
- `src/NINA.Headless/Endpoints/CameraDriversEndpoints.cs` (driver listing + per-driver discovery)
- `docs/dslr-linux.md` (F2)
- `docs/dslr-windows-canon.md` (Canon EDSDK install + EULA)
- `docs/dslr-windows-nikon.md`
- `docs/dslr-windows-sony.md`
- `tests/NINA.Headless.Test/ICameraContractTests.cs`

**Modify**:
- `src/NINA.INDI/Devices/IndiCamera.cs`, implement `ICamera`, distinguish FITS BLOB vs RAW
- `src/NINA.Headless/Services/Alpaca/AlpacaCamera.cs`, implement `ICamera`
- `src/NINA.Headless/Services/EquipmentManager.cs`, `Camera` changes to `ICamera?`, `SelectCamera(driver, deviceId)`
- `src/NINA.Headless/Services/ProfileService.cs`, `EquipmentProfile.CameraDriver` + `CameraDeviceId` + migration
- `src/NINA.Headless/Endpoints/CameraEndpoints.cs`, driver param on `select`
- `src/NINA.Headless/Endpoints/EquipmentEndpoints.cs`, rig PUT accepts driver fields
- `src/NINA.Headless/Services/ImageWriterService.cs`, RAW vs FITS branch via `RawFileBytes`
- `src/NINA.Headless/wwwroot/index.html`, driver dropdown + dynamic field visibility
- `src/NINA.Headless/wwwroot/js/app.js`, driver state + discovery flow
- `src/NINA.Headless/Program.cs`, register vendor camera services (singletons per available driver)
- `README.md`, "DSLR / Mirrorless support" section linking the 4 docs

**Reuse** (already exist):
- `NINA.Image.Portable.ImageAnalysis.JpegHelper`, to encode JPEG → bytes for the stream
- `NINA.Image.Portable.ImageAnalysis.BayerDebayer.ToLuminance`, to collapse RGB into a luminance ushort[] if we decide to demosaic
- `IImageData` / `BaseImageData` / `ImageProperties` / `ImageMetaData`, minimal extension for RAW bytes
- `ImageRelayService`, no changes
- `EquipmentEndpoints` per-rig CRUD, just adds two fields to the PUT body
- SkiaSharp (already vendored via `NINA.Image.Portable`), JPEG decode in the driver path

## Recommended order

1. **F1**, without this nothing else works (~1 day)
2. **F2**, doc only (~1h)
3. **F7**, raw+jpeg pipeline needs to be ready before any Windows driver lands (~half day)
4. **F3** Canon EDSDK, biggest payoff (~3-5 days)
5. **F6**, UI becomes usable as soon as F3 has a concrete driver
6. **F4** Nikon, in parallel with F5 if possible (~3 days)
7. **F5** Sony, Sony SDK has native Linux, scope similar to F4 (~3 days)
8. **F8**, only when someone asks (LibRaw integration)

## End-to-end verification

**F1 (refactor)**:
- `dotnet test`, all 273 existing tests keep passing
- `IndiCamera` still shows up in the Camera tab and captures normally
- `AlpacaCamera` appears as an alternative driver in the dropdown

**F2 (Linux INDI gphoto)**:
- On a Linux host with `indi-gphoto` installed: run `indi_gphoto_ccd`, connect a USB DSLR, see the camera in the driver=indi dropdown, capture, confirm the BLOB arrives as raw + is saved to `{rig}/lights/.../*.cr2`

**F3 (Canon EDSDK)**:
- Windows mini PC + Canon EOS R/RP/6D + USB cable
- Download EDSDK from Canon's site, drop DLLs in `plugins/canon-edsdk/`
- UI: driver dropdown → Canon → "Detect" → see "EOS RP (#01234)" in the select
- Connect → capture 30s → CR2 appears in `{rig}/lights/{target}/{filter}/{session}/`, JPEG preview appears in Live View
- ISO dropdown works (switch to ISO 1600 → next capture uses it)
- Sequence of N exposures works via Advanced Sequencer (`TakeExposure` instruction)
- Bulb (> 30s) uses `BulbStart`/`BulbEnd` correctly

**F4-F5 (Nikon, Sony)**: analogous to F3 with the corresponding vendor hardware.

**F6 (UI)**:
- On Linux: vendor dropdowns hidden (only INDI + Alpaca)
- On Windows with Canon EDSDK installed: Canon shows up, Nikon/Sony grayed out with a download link
- Per-driver discovery doesn't call anything beyond the chosen vendor's SDK

**F7 (raw persistence)**:
- Capture with the Canon driver → `IMG_0001.cr2` on disk, content opens in `dcraw` / Photoshop
- Studio rescan: the `.cr2` shows up in the frame library (type "RAW", not FITS), optional, can be ignored by the browser in v1 if it can't parse it

## License / compatibility notes

- **Canon EDSDK**: free after registration + EULA, **not redistributable** without authorization. Strategy: user downloads from Canon's site, drops the DLL in `plugins/canon-edsdk/`. Polaris detects presence + loads via runtime path. Repo only ships the P/Invoke wrapper (MPL 2.0).
- **Nikon Imaging SDK** + **Nikon MAID**: both free after registration, same strategy.
- **Sony Camera Remote SDK**: free after signup, redistribution gated by the EULA. Same strategy.
- Since the user provides the DLLs, this stays out of the Polaris build artifact (linux-arm64/linux-x64/win-x64 publish scripts don't copy vendor SDKs).
- Backend remains MPL 2.0; the user accepts the vendor's EULA when downloading.
- Linux: `indi-gphoto` is GPLv2 but runs in a separate process (indiserver), no code mixing with Polaris.

---

# Plan: NINA Headless, Complete and Prioritized Gap Analysis

## Context

NINA Headless (`C:\Users\danie\source\repos\DanWBR\nina-headless\`) already has the ASP.NET Core + INDI + Alpine.js Web UI foundation working. A comparative analysis was done between desktop NINA (via the 252-page manual PDF) and the current headless implementation to identify missing features.

**Current state** (already implemented): REST API + WebSocket, full INDI client (9 device types), JPEG/raw LZ4 streaming, Live Stacking, Plate Solving (ASTAP), Slew & Center, Sky Catalog (200+ DSOs), basic Sequence Engine, Profile Management, responsive Web UI with night mode, Equipment Management panel for Camera/Mount/Focuser/FilterWheel, 64 unit tests.

**Goal of this plan**: List ALL identified gaps, organized in 4 prioritized phases. User can pick which phases to execute and in which order.

---

## PHASE A, Essentials for a Real Astrophoto Session

Without these features, a complete acquisition session doesn't work end-to-end. Highest priority.

### A1. PHD2 Guider Integration (manual pp. 155-168)
- **Status**: Only placeholder UI today
- **Files to create**: `src/NINA.Headless/Services/PHD2Client.cs`, `src/NINA.Headless/Endpoints/GuiderEndpoints.cs`
- **Scope**:
  - TCP client for PHD2 on port 4400 (JSON-RPC protocol)
  - Commands: connect/disconnect, get_app_state, get_pixel_scale, set_exposure, start_capture, guide, dither, stop_capture, get_calibration_data
  - Event stream: AppState, Calibrating, CalibrationComplete, StarSelected, StartGuiding, Paused, Settling, SettleDone, GuideStep (RA/Dec error in pixels and arcsec), LockPositionLost, Alert
  - Settle parameters: pixels, time, timeout (from the manual: 1.5/10/40 defaults)
  - REST: GET `/api/guider/status`, POST `/api/guider/connect`, `/api/guider/guide`, `/api/guider/dither`, `/api/guider/stop`
- **UI**: Replace the placeholder in the Guider tab with a real-time chart (Chart.js) showing RA/Dec error vs time, RMS values, Settle status, calibration data display

### A2. Auto-Focus V-Curve (manual pp. 126-135)
- **Status**: Not implemented
- **Files to create**: `src/NINA.Headless/Services/AutoFocusService.cs`, `src/NINA.Headless/Endpoints/AutoFocusEndpoints.cs`
- **Scope**:
  - V-curve algorithm: capture exposures at N focal positions around the current point, measure HFR of each, fit a parabola or V, find the minimum
  - Configuration: step size, number of points, exposure time, binning, ROI/crop ratio, backlash compensation, autofocus filter
  - Filter offsets support (focal offset per filter relative to the reference filter)
  - Temperature compensation (linear coefficient steps/°C, automatic repositioning on temp change)
  - Triggers for auto-focus in a sequence: temperature > threshold, HFR > threshold, elapsed time, filter change
- **UI**: Auto-Focus tab/panel with HFR vs Position chart (Chart.js), Start AF button, status bar, settings panel

### A3. Meridian Flip Automation (manual pp. 136-148)
- **Status**: Backend has `SideOfPier` on IndiTelescope but no workflow
- **Files to create**: `src/NINA.Headless/Services/MeridianFlipService.cs`
- **Scope**:
  - Meridian detector: compute time to meridian using RA + LST
  - Configurable trigger: minutes after meridian (default 10), pause before meridian (default 0)
  - Full workflow:
    1. Pause guiding (via PHD2)
    2. Stop tracking (optional)
    3. Command the flip (slew back to target does this on most mounts)
    4. Wait for scope settle time
    5. Plate solve to detect new position
    6. Re-center via Slew & Center (already implemented)
    7. Optional auto-focus after flip
    8. Resume guiding (recalibration if needed)
    9. Resume sequence
  - Dome synchronization during flip (if dome connected)
- **UI**: Meridian Flip settings panel (Sequence or Settings tab) with toggles and parameters; real-time status during flip via SSE/WebSocket

### A4. Dithering (manual pp. 149-155)
- **Status**: Not implemented
- **Scope**:
  - Configuration: dither pixels (random offset), dither every N frames, settle pixels (tolerance), settle time min/timeout
  - PHD2 integration: `dither` JSON-RPC command that moves the guide star by X random pixels
  - Wait for SettleDone event before starting the next exposure
  - "Dither RA only" toggle for mounts without good Dec correction
- **Integration**: Add logic to `SequenceEngine` to call dither after every N frames
- **UI**: Settings in the sequence panel (dither toggle, pixels, every N, settle params)

### A5. Missing Equipment Panels

INDI backends already exist (`IndiRotator`, `IndiFlatDevice`, `IndiDome`, `IndiWeather`), only the UI is missing.

- **Rotator**:
  - Card in the Equipment tab: select dropdown, connect/disconnect, current angle display, target angle input, sync, reverse toggle
  - Endpoint: `IndiRotator.cs` already exists, `RotatorEndpoints.cs` is missing
- **Flat Panel**:
  - Card: select/connect/disconnect, light on/off toggle, brightness slider (0-100% or device-specific range), cover open/close if supported
  - Endpoint: create `FlatDeviceEndpoints.cs`
- **Dome**:
  - Card: select/connect/disconnect, azimuth display + target input, slew to azimuth, shutter open/close, park, sync to scope (slave mode toggle)
  - Endpoint: create `DomeEndpoints.cs`
- **Weather**:
  - Card: select/connect/disconnect, real-time readings of: cloud cover %, dew point, humidity, pressure, sky brightness, sky quality (MPSAS), sky temp, ambient temp, wind speed/direction/gust, rain
  - Safety status indicator (green/yellow/red based on configurable thresholds)
  - Endpoint: create `WeatherEndpoints.cs`

### A6. Camera Cooler Power & Warm-up
- **Status**: Temperature setpoint works, polish missing
- **Scope**:
  - Cooler power % display (INDI property `CCD_COOLER_POWER` already exists)
  - Gradual warming function: temperature ramp-up (protect the sensor)
  - Temperature chart over time (Chart.js), useful for diagnostics
- **UI**: Add to the Camera card in the Equipment tab

---

## PHASE B, Rich UI/UX

Visual features that make the experience complete. Don't block acquisition but greatly improve usage.

### B1. Chart.js Integration
- **Lib**: `wwwroot/js/lib/chart.min.js` (~65KB)
- **Charts**:
  - Guiding: RA/Dec error vs time with RMS (in the Guider tab)
  - Focus: HFR vs Position curve (in the Auto-Focus tab)
  - Temperature: Sensor temp + Cooler power vs time (in the Camera tab)
  - HFR History: HFR + Star count vs frame (in the Imaging tab)
- **Wiring**: WebSocket status stream already delivers data at 1Hz, just plot

### B2. Aladin Lite Sky Map (already in the original plan)
- **Lib**: `wwwroot/js/lib/aladin-lite.min.js` (~400KB) + WebAssembly
- **Component**: New "Sky Explorer" tab (or rename the current one)
- **Features**:
  - HiPS tile rendering (DSS, SDSS, 2MASS, Pan-STARRS)
  - Search bar with SIMBAD lookup + local catalog
  - FOV overlay computed from the profile's sensor/focal length
  - Drag and rotate overlay to pick framing
  - Target info panel: J2000 coords, magnitude, current altitude/azimuth, transit time, moon distance, "object fills X% of sensor"
  - Integrated "Slew & Center" button (workflow already exists on the backend)
  - Offline mode: local tile cache + Hipparcos JSON fallback

### B3. OpenSeadragon Full-Resolution Viewer
- **Lib**: `wwwroot/js/lib/openseadragon.min.js` (~60KB)
- **Component**: "Image Viewer" modal/panel for detailed analysis of the latest image
- **Features**:
  - Tile pyramid for interactive zoom on large FITS
  - Smooth pan, pixel-perfect 100% zoom
  - Optional overlay of detected stars

### B4. WebGL Shader Pipeline (Client-Side Image Processing)
- **Files**: `wwwroot/js/shaders/debayer-rggb.glsl`, `stretch-mtf.glsl`, `passthrough.vert`
- **Pipeline**:
  - Receive raw uint16 via WebSocket (raw LZ4 mode already exists)
  - Upload to WebGL texture
  - Shader 1: Debayer (RGGB/GRBG/BGGR/GBRG)
  - Shader 2: White balance
  - Shader 3: Auto-stretch MTF (midtone transfer function)
  - Render to `<canvas>`
- **Benefit**: Takes image processing off the RPi, frees CPU; enables real-time interactivity (instant stretch slider)
- **Fallback**: If the browser doesn't have WebGL2, use JPEG mode (server processes), already implemented

### B5. Image Statistics Panel
- **Location**: Imaging tab, side panel next to the image
- **Metrics to show**:
  - Mean, Median, Min, Max, StdDev, MAD (Median Absolute Deviation)
  - Per R/G/B channel if OSC
  - Star count, HFR average
  - Bit depth
  - Optimal Exposure Calculator (suggestion based on background ADU)
- **Implementation**: Can be client-side (Web Worker) using the pixel data already received via WebSocket, OR server-side if in JPEG mode (NINA.Image.Portable already has `ImageStatistics.cs`)

### B6. Manual Stretch Controls
- **Component**: Sliders in the Imaging tab
- **Controls**:
  - Black point (shadows clip)
  - White point (highlights clip)
  - Midtone (gamma/MTF)
  - "Auto Stretch" preset button
  - Visual histogram (overlay on the image or separate panel)
- **Integration**: With the WebGL pipeline (B4) the shader receives these params as uniforms; with JPEG, the server applies and re-sends

### B7. Image History Gallery
- **Status**: Today there's only a preview of the latest image
- **Scope**:
  - Thumbnail strip of the last N images of the session
  - Click to open in the viewer (B3)
  - Per image: timestamp, exposure, gain, filter, HFR, star count
  - HFR + Star count vs frame chart (Chart.js)

### B8. Star Annotations Overlay
- **Component**: Canvas overlay over the image
- **Features**:
  - Circles over detected stars (from existing star detection)
  - Optional label with HFR/coordinates
  - Toggle on/off
- **Bonus**: Plate solve object annotations (DSO names over nebulae, etc.)

### B9. Crosshair / Grid / Pixel Readout
- **Crosshair**: Crosshair line at the image center (focus aid)
- **Grid**: 3x3 or 5x5 grid overlay
- **Pixel readout**: Hover/click shows the ADU value of the pixel under the cursor
- **Everything togglable** via small buttons in the viewer corner

---

## PHASE C, Advanced Sequencer (Biggest Single Gap)

NINA's Advanced Sequencer is probably the feature that most differentiates the product. It's a conceptual rewrite of our simple `SequenceEngine`.

### C1. Data Model (Tree-based)
- **Files to create**: `src/NINA.Headless/Services/Sequencer/`
  - `ISequenceEntity.cs` (abstract base)
  - `SequenceContainer.cs` (can hold other entities)
  - `SequenceInstruction.cs` (atomic action)
  - `SequenceCondition.cs` (loop control)
  - `SequenceTrigger.cs` (event-based)
- **Container types**:
  - `SequentialContainer` (runs children in order)
  - `ParallelContainer` (runs children in parallel)
  - `DeepSkyObjectContainer` (specific target with coords, rotation, filter plan)
  - `TemplatedContainer` (loads from a saved template)

### C2. Instructions Library
List of essential instructions (from manual pp. 109-125):
- **Mount**: Slew to Target, Slew to Coordinates, Center on Target, Park, Unpark, Set Tracking, Solve and Sync
- **Camera**: Take Exposure, Take Many Exposures, Cool Camera, Warm Camera, Save Image
- **Focuser**: Auto Focus, Move Focuser, Move to Filter Offset
- **Filter Wheel**: Switch Filter
- **Guider**: Start Guiding, Stop Guiding, Dither, Auto-Select Star
- **Dome**: Open Shutter, Close Shutter, Park Dome, Slew to Azimuth, Sync to Scope
- **Flat Panel**: Cover Open/Close, Set Brightness, Toggle Light
- **Rotator**: Rotate to Angle, Sync
- **Flow Control**: Wait For Time, Wait Until Above Horizon, Wait For Altitude, Wait For Moon, Wait For Sun Below Horizon
- **External**: Run External Script, Send HTTP Request, Send Email/Notification

### C3. Conditions (Loop Until)
- Loop Until Time (specific datetime)
- Loop Until Altitude (target below X°)
- Loop For N Exposures (counter)
- Loop For Duration (timer)
- Loop Until Moon Sets
- Loop While Safe (weather safety check)

### C4. Triggers (Event-Based)
- Auto Focus on Temperature Change (delta °C)
- Auto Focus on HFR Increase (% threshold)
- Auto Focus on Time Elapsed
- Auto Focus on Filter Change
- Meridian Flip Trigger
- Dither After N Exposures
- Center After Drift (plate solve check)
- Safety Trigger (weather/equipment failure → abort to safe state)

### C5. Serialization
- **Format**: JSON (compatibility with existing NINA would be nice-to-have but big scope)
- **Operations**: Save/Load sequence, Save/Load template (reusable fragment)
- **Versioning**: include a version field for future migration

### C6. UI (Tree Editor)
- **Tech**: Alpine.js + drag-drop library (Sortable.js ~30KB)
- **Features**:
  - Interactive tree view
  - Drag-drop to reorder
  - Add/remove items via context menu or toolbar
  - Properties panel on item select
  - Validation indicators (item without a connected mount shows red, etc.)
  - Progress bars per container/item
  - Start/Pause/Stop/Skip-current controls

### C7. Migration Path
- Keep the old `SequenceEngine` (Simple Sequencer) as an alternative mode
- Settings toggle: "Use Advanced Sequencer" / "Use Simple Sequencer"
- Document differences

---

## PHASE D, Nice-to-Have / Polish

Features that add value but aren't essential.

### D1. Alpaca HTTP Device Support
- Alternative to INDI for cross-platform drivers
- HTTP client for the Alpaca REST API (port 11111)
- UDP broadcast discovery
- Implement for the same 9 device types
- **Benefit**: Windows drivers (ASCOM) become reachable via an Alpaca server, RPi user can control a Windows-side mount over the network

### D2. mDNS/Avahi Discovery
- **Lib**: `Makaretu.Dns.Multicast` or similar
- **Announce**: `_nina._tcp.local` on port 5000
- **Access**: `http://nina.local:5000` from any device on the network without needing to know the IP

### D3. Adaptive Bandwidth (Raw vs JPEG Auto-Switch)
- Server monitors WebSocket send latency
- If latency > threshold (sign of weak WiFi), automatically falls back to JPEG
- Returns to raw when latency normalizes
- Settings: configurable thresholds

### D4. Docker Image
- `Dockerfile` for `linux-arm64` and `linux-amd64`
- Multi-stage build (SDK → runtime)
- Volume mounts for `/config` (profiles) and `/images` (output)
- docker-compose with INDI server in the same stack
- Publish on Docker Hub

### D5. Mosaic Planner
- **Location**: Sky Explorer tab
- **Features**:
  - NxM grid of panels on the Aladin map
  - Configuration: target object, FOV per panel, overlap %, total grid size
  - Acquisition order: serpentine (optimizes slew time)
  - Calculator: estimated total time (panels × exposure × frames)
  - Export as Advanced Sequence (DSO containers for each panel)

### D6. Flat Wizard
- Automated flat acquisition per filter
- Settings: target ADU, tolerance %, exposure min/max, binning, filter list
- Algorithm: binary search of exposure time to hit target ADU
- Trained exposure times: save time per filter/bin for future sessions
- UI: step-by-step wizard in the Sequence tab or a new tab

### D7. Extended FITS Headers (manual pp. 169-173)
- Today only writes basics. Add:
  - Camera: GAIN, OFFSET, EGAIN, XPIXSZ, YPIXSZ, BAYERPAT, READOUTM
  - Telescope: TELESCOP, FOCALLEN, FOCRATIO, PIERSIDE
  - Filter: FWHEEL, FILTER
  - Focuser: FOCNAME, FOCPOS, FOCTEMP
  - Rotator: ROTNAME, ROTATOR, ROTATANG
  - Weather: CLOUDCVR, DEWPOINT, HUMIDITY, PRESSURE, SKYBRGHT, MPSAS, AMBTEMP, WINDSPD/DIR/GUST
  - Object: OBJCTRA, OBJCTDEC, OBJCTROT
  - Observer: SITELAT, SITELONG, SITEELEV, OBSERVER, OBSERVAT, SITENAME
- Custom user-configurable keywords

### D8. XISF Format Support
- PixInsight's format
- `LZ4` and `Zstd` compression libs already exist
- Headers as embedded XML
- Useful for PixInsight users

### D9. Plate Solver Choices (manual pp. 180-183)
- Today only ASTAP. Add:
  - PlateSolve3 (fast at long focal lengths)
  - Astrometry.net online (fallback)
  - All Sky Plate Solver (local astrometry.net wrapper)
- Settings: primary solver + blind solver fallback

### D10. Sky Atlas Filters (manual ch. Sky Atlas)
- Search filters:
  - Constellation (dropdown)
  - Object type (Galaxy, Nebula, Cluster, etc.)
  - Magnitude range (slider)
  - Size range (slider)
  - Altitude (visible tonight, currently above X°)
- Altitude chart: graph of object altitude vs time of night, with twilight bands overlaid
- Moon distance + illumination warning

### D11. Stellarium / Planetarium Sync (manual pp. 196-198)
- Receive coordinates from Stellarium via the Remote Control plugin (port 8090)
- Simple HTTP integration: GET coords from Stellarium → inject as target

### D12. Plugin System
- API/SDK for third parties to add:
  - New device types
  - New sequence instructions/conditions/triggers
  - Custom UI panels
- MEF (Microsoft Extensibility Framework) or simple assembly loading
- Plugins folder: `plugins/` in the working dir
- **Heads up**: Big scope, low priority

---

## Critical Files to Modify/Create

### Backend (C#)
- `src/NINA.Headless/Services/PHD2Client.cs` (A1)
- `src/NINA.Headless/Services/AutoFocusService.cs` (A2)
- `src/NINA.Headless/Services/MeridianFlipService.cs` (A3)
- `src/NINA.Headless/Services/Sequencer/` (C, multiple files)
- `src/NINA.Headless/Endpoints/GuiderEndpoints.cs` (A1)
- `src/NINA.Headless/Endpoints/AutoFocusEndpoints.cs` (A2)
- `src/NINA.Headless/Endpoints/RotatorEndpoints.cs` (A5)
- `src/NINA.Headless/Endpoints/FlatDeviceEndpoints.cs` (A5)
- `src/NINA.Headless/Endpoints/DomeEndpoints.cs` (A5)
- `src/NINA.Headless/Endpoints/WeatherEndpoints.cs` (A5)
- `src/NINA.Headless/Services/SequenceEngine.cs` (A4 dithering integration)

### Frontend
- `src/NINA.Headless/wwwroot/js/lib/chart.min.js` (B1)
- `src/NINA.Headless/wwwroot/js/lib/aladin-lite.min.js` (B2)
- `src/NINA.Headless/wwwroot/js/lib/openseadragon.min.js` (B3)
- `src/NINA.Headless/wwwroot/js/lib/sortable.min.js` (C6)
- `src/NINA.Headless/wwwroot/js/shaders/*.glsl` (B4)
- `src/NINA.Headless/wwwroot/js/app.js` (every phase A/B/C requires expansion)
- `src/NINA.Headless/wwwroot/index.html` (new tabs, panels, controls)
- `src/NINA.Headless/wwwroot/css/app.css` (styles for new components)

### Reuse (already exist, should be referenced)
- `src/NINA.INDI/Devices/IndiRotator.cs`, `IndiFlatDevice.cs`, `IndiDome.cs`, `IndiWeather.cs` (A5)
- `src/NINA.INDI/Devices/IndiTelescope.cs` (SideOfPier property already exists, use in A3)
- `src/NINA.Image.Portable/Analysis/ImageStatistics.cs` (B5)
- `src/NINA.Image.Portable/Analysis/StarDetection.cs` (B8)
- `src/NINA.Headless/Services/SlewCenterService.cs` (reuse in A3 meridian flip workflow)

---

## Recommended Execution Order

1. **A5** (missing equipment panels), quick wins, backends already exist, ~1 day
2. **A1** (PHD2), unblocks A4 and A3
3. **A4** (Dithering), depends on A1
4. **A2** (Auto-Focus), independent, can parallelize
5. **A3** (Meridian Flip), depends on A1 + A2 + existing Slew & Center
6. **B1** (Chart.js), enables A1/A2 charts
7. **B6+B5** (Stretch + Statistics), improves acquisition UX
8. **B2** (Aladin Lite), visually impactful
9. **B4** (WebGL pipeline), client performance
10. **B3, B7, B8, B9** (viewer + history + annotations + overlays)
11. **C1-C7** (Advanced Sequencer), large project, split into sub-phases
12. **Phase D**, on demand

---

## Verification per Phase

### Phase A
- [ ] PHD2 connects via UI, see guiding RMS chart, command dither
- [ ] Auto-focus completes a V-curve with HFR vs position chart, adjustment improves HFR
- [ ] Meridian flip runs without intervention: pause guiding → flip → solve → re-center → resume
- [ ] Dithering occurs every N frames during a sequence, settles correctly
- [ ] Rotator/FlatPanel/Dome/Weather UIs connect and show real-time data

### Phase B
- [ ] Chart.js charts render in real time
- [ ] Aladin Lite loads DSS tiles, FOV overlay correct, Slew & Center works
- [ ] WebGL pipeline debayer + stretch works in modern browsers, JPEG fallback on old browsers
- [ ] Image statistics update after each exposure
- [ ] Manual stretch sliders change the display in real-time

### Phase C
- [ ] Load sequence JSON, execute the full tree
- [ ] Triggers fire correctly (e.g. auto-focus on temp change)
- [ ] Drag-drop reorders items in the UI
- [ ] Pause/resume preserves state

### Phase D
- [ ] `nina.local:5000` resolves via mDNS
- [ ] Docker container runs on RPi
- [ ] Mosaic generates a valid sequence
- [ ] Flat Wizard hits target ADU within tolerance

---

## Compatibility notes

- Keep compatibility with Windows mini PC (project memory note)
- Live Stacking already implemented (memory note)
- Build targets: `linux-arm64` (RPi 4/5), `linux-x64` (Intel SBC), `win-x64` (Windows mini PC)
- Backend must remain headless-functional (no display), all new features must be 100% controllable via the Web UI

---

## Execution Status

### Completed sessions
- Phase A (A1-A5): **5/5 ✅** (commits 77d8fa4..1e56fff)
- Phase B (B1-B9): **9/9 ✅** (commits c53d353..bd2df6b)
- Phase D partial:
  - D2 mDNS ✅ (234ce04)
  - D3 Adaptive bandwidth ✅ (06099d5)
  - D7 Extended FITS headers ✅ (4904695)
  - D6 Flat Wizard ✅ (669b8a9)
  - D1 Alpaca HTTP ✅ (1bfedb4)
  - D11 Stellarium sync ✅ (50c07d1)
  - D4 Docker ✅ (30550e9)
  - D10 Sky Atlas filters + altitude ✅ (1e58b25)
  - **D9 Multiple plate solvers ✅** (3244129), refactored `PlateSolveService` into
    a dispatcher with primary+blind fallback, created `IPlateSolver` interface,
    extracted `AstapSolver`, added `PlateSolve3Solver`,
    `AstrometryNetOnlineSolver`, `AstrometryNetLocalSolver`. 126 tests.

### Wrapping up: D8, XISF format support

**Implementation complete** but commit/push pending (session entered plan mode):

Files created/modified:
- `src/NINA.Image.Portable/FileFormat/XISF/XISFWriter.cs` (new, ~280 lines):
  spec-compliant XISF 1.0 format. Signature `XISF0100` + uint32 LE header
  length + 4 reserved bytes + XML header (padded 4KB block) + binary
  attachment. UInt16 LE pixel data, optional LZ4 compression (`compression="lz4:N"`).
  Metadata via `<FITSKeyword>` + `<Property>` (Observation:Object:Name,
  Instrument:Camera:*, Instrument:Telescope:*, etc.). Header block count
  auto-grows when metadata exceeds 4096B.
- `src/NINA.Headless/Services/ImageWriterService.cs` (modified): honors
  `profile.ImageFormat == "xisf"` to route to XISFWriter or FITSWriter.
  Automatic `.xisf` or `.fits` extension.
- `tests/NINA.Headless.Test/XISFWriterTests.cs` (new, 9 tests): signature,
  valid XML header, uncompressed data block, LZ4 compressed, offset
  alignment, FITSKeywords present, BayerPat, imageType normalisation, header
  auto-expand. **135 tests total passing**.
- `src/NINA.Headless/wwwroot/index.html`: new "Image Output" section in
  Settings with `<select>` FITS/XISF.
- `src/NINA.Headless/wwwroot/js/app.js`: settings.imageFormat default + load
  from server.

Remaining steps:
1. Final `dotnet build` (already passed on the last iteration)
2. Final `dotnet test` (already 135 passing)
3. `git add` new/modified files:
   - `src/NINA.Image.Portable/FileFormat/XISF/XISFWriter.cs`
   - `src/NINA.Headless/Services/ImageWriterService.cs`
   - `src/NINA.Headless/wwwroot/index.html`
   - `src/NINA.Headless/wwwroot/js/app.js`
   - `tests/NINA.Headless.Test/XISFWriterTests.cs`
4. `git commit` with a descriptive message for phase D8
5. `git push origin master` (user already authorized direct push)

### D8 end-to-end verification
- Set `profile.ImageFormat = "xisf"` via Settings UI or PUT /api/system/profile
- Capture an image via a sequence → `.xisf` file shows up in `ImageOutputDir`
- Open in PixInsight: should open as UInt16 monochrome with all
  FITSKeywords available in PixInsight's File → FITS Header...
- LZ4 compression should reduce size ~3-10x vs FITS (same image)

### Pending after D8

**Phase C, Advanced Sequencer** (huge, not started)
- C1-C7: tree-based container/instruction/condition/trigger model

**Remaining Phase D**
- D5 Mosaic planner
- D12 Plugin system

---

# Plan: WEATHER tab, Astronomical forecast for planning

## Context

The current UI shows *current* readings from a connected INDI weather station (in the Equipment card), but the user has no visibility into **future forecast** to plan when to observe. Today the astrophotographer has to open another browser tab to check ClearOutside / 7Timer / Windy.

**Goal**: new **WEATHER** tab that shows astronomically relevant forecast (clouds, seeing, transparency) for today + the next 2 days, with a composite 0-100 score per 3h slot, automatic highlighting of the best observation windows at night, and overlay of sunrise/sunset, astronomical twilight, and moon phase.

**Decisions confirmed with the user**:
- **Provider**: 7Timer (only), free, no API key, astronomy-specific, 3 days in 3h slots.
- **Where to fetch**: server-side with 15min TTL cache, multiple clients share, browser without direct internet still works via LAN.

## Backend

### New `src/NINA.Headless/Services/WeatherForecastService.cs`

Singleton. Pattern identical to `Services/StellariumClient.cs` and `Services/GeocodingService.cs`:

- `static HttpClient` with 8s timeout, User-Agent set at startup
- In-memory cache `ConcurrentDictionary<string, (DateTime fetchedAt, JsonDocument data)>` keyed by `"{lat:F2},{lon:F2}"` with TTL 15 min
- Method `Task<WeatherForecastDto> GetForecastAsync(double lat, double lon)`:
  1. Look up cache; if hit + age < TTL, return
  2. Otherwise call `https://www.7timer.info/bin/astro.php?lon={lon}&lat={lat}&ac=0&unit=metric&output=json&tzshift=0`
  3. Parse 7Timer JSON → internal DTO
  4. Compute `observationScore` (0-100) per slot via private method `ScoreSlot(slot)`
  5. Store in cache, return
- On HTTP error / timeout / invalid JSON: log + return `WeatherForecastDto { Available = false, Error = "..." }` (don't throw)

DTO:
```csharp
public record WeatherForecastDto(bool Available, string Error,
    DateTime InitUtc, IReadOnlyList<WeatherSlot> Slots);

public record WeatherSlot(DateTime UtcStart, int CloudCover,
    int Seeing, int Transparency, int LiftedIndex,
    double Temp2m, int Rh2m, double WindSpeed, string WindDirection,
    string PrecType, int ObservationScore);
```

**`ScoreSlot` formula**:
- Base = `((10 - cloudcover) * 10) * 0.5 + ((9 - seeing) * 12.5) * 0.25 + ((9 - transparency) * 12.5) * 0.25`
- Hard zero if `precType != "none"`
- Multiply × 0.3 if `rh2m > 95`
- Clamp [0, 100]

### New endpoint in `src/NINA.Headless/Endpoints/WeatherEndpoints.cs`

Add to the existing class:
- `GET /api/weather/forecast?lat={lat}&lon={lon}` → `WeatherForecastDto` JSON
- Validation: lat ∈ [-90, 90], lon ∈ [-180, 180]; if invalid → 400

### Registration in `src/NINA.Headless/Program.cs`

`builder.Services.AddSingleton<WeatherForecastService>();` (after `StellariumClient`)

## Frontend

### Vendoring SunCalc (MIT)

- Download `suncalc.min.js` (~10KB, MIT license, compatible with MPL 2.0)
- Place in `src/NINA.Headless/wwwroot/js/lib/suncalc/`
- Create adjacent `LICENSE` with MIT text + attribution
- Include via `<script>` in `index.html`
- Used for: sunrise/sunset, astronomical twilight (sun -18°), moon phase, illumination %, moonrise/moonset

### Sidebar and tab panel in `src/NINA.Headless/wwwroot/index.html`

**Sidebar**, add between "Sky" and "Seq" (planning workflow):
```html
<button class="nav-btn" :class="{ active: tab === 'weather' }"
        @click="tab = 'weather'; loadWeatherForecast()" title="Weather">
    <span class="nav-icon">☁</span><span class="nav-label">WEATHER</span>
</button>
```

**Tab panel**, new `<div x-show="tab === 'weather'" class="tab-panel">` with:

1. **Top strip**, "Refresh" button + last-updated timestamp + loading indicator
2. **"Tonight's best windows"** callout, top 3 continuous windows (adjacent slots with score ≥ 70) between sunset and sunrise; each window shows duration + average score + condition summary
3. **3 day cards** (Today / Tomorrow / Day after) side-by-side on desktop, stacked on mobile:
   - Header: date + day of the week + sunset/sunrise + astronomical twilight start/end + moon phase icon + illumination %
   - Grid of 8 slots (3h each) with:
     - Background color based on score (red < 40, amber 40-70, green > 70)
     - Local time
     - Score number
     - Tooltip with breakdown (cloud %, seeing, transparency, temp, wind, RH)
4. **Legend** explaining the scoring system

### State and methods in `src/NINA.Headless/wwwroot/js/app.js`

Pattern identical to `_refreshLocationLabel()` (`app.js` lines 1420-1457):

```js
// State
weather: { forecast: null, loading: false, error: '', lastFetched: null },
_weatherLastKey: '',

// Methods
async loadWeatherForecast(force=false) {
    const lat = this.settings.latitude;
    const lng = this.settings.longitude;
    if (!lat || !lng) { this.weather.error = 'Set location in Settings first'; return; }
    const key = `${lat.toFixed(2)},${lng.toFixed(2)}`;
    if (!force && key === this._weatherLastKey && this.weather.forecast) return;
    this._weatherLastKey = key;
    this.weather.loading = true;
    try {
        const r = await this.apiGet(`/api/weather/forecast?lat=${lat}&lon=${lng}`);
        this.weather.forecast = r;
        this.weather.lastFetched = new Date();
        this.weather.error = r.available ? '' : (r.error || 'Forecast unavailable');
    } catch (e) {
        this.weather.error = 'Could not reach forecast service';
    } finally {
        this.weather.loading = false;
    }
},

// Computed helpers (used in template):
weatherDays() {
    // Group forecast.slots by local date → array of { date, slots[], sun, moon }
    // Uses SunCalc for sunrise/sunset/twilight + moon phase per day
},
weatherBestWindows() {
    // Find continuous runs of slots[].score >= 70 between sunset and sunrise
    // Return top 3 by total duration × avg score
}
```

### CSS in `src/NINA.Headless/wwwroot/css/app.css`

New blocks in the style of existing cards (`home-status-card`, `home-quick-card`):
- `.weather-day-card`, per-day card, padding 16px, gap 12px between header and grid
- `.weather-slot-grid`, 8-column CSS grid
- `.weather-slot`, cell with dynamic background via inline style, padding 8px, font-size 11px, border-radius 4px
- `.weather-slot--good` (green), `.weather-slot--meh` (amber), `.weather-slot--bad` (red)
- `.weather-window-callout`, visual highlight for best windows (green + subtle glow)
- `.weather-moon-icon`, inline SVG with clipping to show phase

## Tests

New `tests/NINA.Headless.Test/WeatherForecastServiceTests.cs`:

- `ScoreSlot_ClearSky_ReturnsHigh`, cloudcover=1, seeing=2, transparency=2, no precip → score > 85
- `ScoreSlot_OvercastWithRain_ReturnsZero`, cloudcover=9, prec_type="rain" → score == 0
- `ScoreSlot_HighHumidity_PenalisedHarshly`, clear but rh2m=98 → score × 0.3
- `ScoreSlot_BoundaryClamp`, extremes don't blow past [0, 100]
- `GetForecastAsync_CacheHit_SkipsHttp`, 2nd call within TTL doesn't fire a fetch (use HttpMessageHandler mock)
- `GetForecastAsync_OnHttpError_ReturnsAvailableFalse`, handler that throws → DTO with `Available = false`, no bubbling exception

Currently: 135 tests. After Weather: 141.

## Files to create / modify

### Create
- `src/NINA.Headless/Services/WeatherForecastService.cs`
- `src/NINA.Headless/wwwroot/js/lib/suncalc/suncalc.min.js` + `LICENSE`
- `tests/NINA.Headless.Test/WeatherForecastServiceTests.cs`

### Modify
- `src/NINA.Headless/Endpoints/WeatherEndpoints.cs`, add `/forecast` route
- `src/NINA.Headless/Program.cs`, register `WeatherForecastService`
- `src/NINA.Headless/wwwroot/index.html`, sidebar button + suncalc script + tab panel
- `src/NINA.Headless/wwwroot/js/app.js`, `weather` state, methods `loadWeatherForecast`, `weatherDays`, `weatherBestWindows`
- `src/NINA.Headless/wwwroot/css/app.css`, `.weather-*` styles
- `README.md`, document the Weather tab + 7Timer attribution

## Reuse of existing code

- `Services/GeocodingService.cs`, copy pattern static HttpClient + timeout + exception handling
- `Services/StellariumClient.cs`, copy pattern of simple HTTP client for a public external API
- `app.js _refreshLocationLabel()` (lines 1420-1457), copy pattern fetch + memoise by key + silent fallback
- `Endpoints/WeatherEndpoints.cs`, add a new route to the existing MapGroup (don't create a new file)
- `js/lib/celestial/data/*.json`, don't use, data source is runtime
- `this.settings.latitude` / `longitude` state, source of coords (already comes from profile loader)
- Existing tab pattern: clone the markup of a simple tab like "Settings" for the base panel structure

## End-to-end verification

1. **Build**: `dotnet build` in `src/NINA.Headless`, no errors
2. **Tests**: `dotnet test` in `tests/NINA.Headless.Test`, 141 tests pass (135 existing + 6 new)
3. **Backend endpoint manual**:
   - `curl "http://localhost:5000/api/weather/forecast?lat=-5.18&lon=-37.36"` → JSON with `available: true`, slot list, computed scores
   - Call 2x within < 15 min → second response comes from cache (check log)
   - Call with `?lat=999` → 400 Bad Request
4. **Happy-path UI flow**:
   - Open browser, go to Settings, confirm lat/lng set
   - Click the WEATHER tab → loading spinner → forecast renders
   - Verify 3 day cards, 8 slots each, colors varying by score
   - Verify "Tonight's best windows" callout shows 1-3 windows
   - Hover a slot → tooltip with full breakdown
   - Sunset/sunrise/twilight/moon phase correct for current coords/date
5. **Graceful error**:
   - Disconnect server's internet → forecast returns `available: false`
   - UI shows "Forecast unavailable" + Retry button
   - No crash, other tabs keep working
6. **Cache invalidation**:
   - Change lat/lng in Settings → next Weather tab load fires a new fetch (cache key changed)

## Implementation order

1. `WeatherForecastService.cs` + DTO + scoring logic
2. Endpoint `/api/weather/forecast` + registration in Program.cs
3. Unit tests (`ScoreSlot_*` + `GetForecastAsync_*`)
4. `dotnet build && dotnet test`, backend green
5. Vendor SunCalc + LICENSE
6. Sidebar button + tab panel skeleton in index.html
7. State + `loadWeatherForecast()` in app.js
8. `weatherDays()` + `weatherBestWindows()` helpers in app.js
9. Render the 3 day cards + slot grid in index.html
10. CSS `.weather-*` in app.css
11. Per-slot tooltip, best windows callout, refresh button
12. README, "Weather forecast" section with 7Timer + SunCalc attribution
13. Commit + push

## Compatibility and license notes

- **7Timer**: free, no restrictive terms of use, attribution recommended in the UI ("Forecast data: 7Timer.info")
- **SunCalc**: MIT license, compatible with MPL 2.0
- **Privacy**: lat/lng sent to 7Timer on the fetch, behavior identical to the existing Nominatim reverse-geocode, so doesn't change the privacy posture
- **Offline**: Raspberry Pi setup without internet → empty cache + Available=false → UI shows a friendly message, doesn't break
- **Mobile**: responsive layout (3 cards side-by-side at ≥1024px, stacked at < 768px)

---

# Plan: TONIGHT'S BEST tab, Ranked list of the best to observe tonight

## Context

An astrophotographer opening the UI at night wants to know *what's worth shooting right now*. Today they have to open Sky Atlas, remember which DSOs are visible at their latitude/season, check altitude one by one. ASIAIR solves this with a "Tonight's Best" panel, a ranked list with thumbnail, RA/Dec/mag/size, altitude chart, and current-direction compass.

**Goal**: new **TONIGHT** tab that shows ~20 celestial objects best positioned for observation tonight, grouped by type (DSOs, Moon, Planets, Comets), each with a visual card including a NASA/Wikipedia image, current ephemeris, mini altitude-vs-night chart, and compass.

**Decisions confirmed with the user**:
- **Where**: new tab in the sidebar (between Sky and Weather)
- **v1 content**: DSOs (existing catalog) + Moon + Planets + Comets

## Phases (3 separate commits to review incrementally)

### Phase TB-1: CelestialImageService (NASA + Wikipedia thumbnails)

Foundational, used by every card.

**New** `src/NINA.Headless/Services/CelestialImageService.cs`:
- Pattern identical to `GeocodingService.cs`: static HttpClient, timeout, User-Agent.
- **On-disk** cache (not just memory) for persistence across restarts: `images/cache/{slug}.json` storing `{ url, thumbnailUrl, credit, source, fetchedAt }`. Slug = normalized name (lowercase, alphanum-only, e.g. "m31", "ngc7000", "moon", "22pkopff").
- TTL: 30 days (images don't change).
- `Task<CelestialImage> GetImageAsync(string name, CancellationToken ct)`:
  1. Check on-disk cache
  2. Try **NASA Image Library**: `https://images-api.nasa.gov/search?q={name}&media_type=image`, pull `collection.items[0].links[0].href` (thumb) and `data[0].title/description`. Filter results by relevance (item whose `keywords` contains the name).
  3. **Wikipedia REST** fallback: `https://en.wikipedia.org/api/rest_v1/page/summary/{name}` (and variants with underscore, prefix "NGC_"), pull `thumbnail.source`.
  4. If nothing found → return `{ available: false }`. Cache it anyway with short TTL (1 day) so we don't hammer APIs on every refresh.

**New endpoint** in `SkyEndpoints.cs`:
- `GET /api/sky/image?name={name}` → `{ url, thumbnailUrl, credit, source, available }`

**Tests** `CelestialImageServiceTests.cs`:
- Slug normalisation (`Slugify_M31_GivesM31`, `Slugify_Sh2_279_StripsSpecial`)
- Cache hit short-circuit
- Graceful fallback when both providers 404
- HttpRequestException → returns `{ available: false }`

### Phase TB-2: TonightsBestService (backend ranking)

**New NuGet dependency** `CosineKitty.AstronomyEngine` (MIT, ~150KB) in `src/NINA.Headless/NINA.Headless.csproj`:
```xml
<PackageReference Include="CosineKitty.AstronomyEngine" Version="2.1.19" />
```

**New** `src/NINA.Headless/Services/TonightsBestService.cs`:

- `IReadOnlyList<TonightCandidate> ComputeAsync(double lat, double lng, DateTime nowUtc, int limit = 20)`:

1. **Night window**: uses `AltitudeService.ComputeNightWindow(lat, lng, nowUtc)` (already exists) to get astronomical twilight start/end. If nightWindow is null (never gets dark, e.g. polar summer) → window = `[now, now+12h]`.

2. **DSOs**: enumerates `SkyCatalogService.AllObjects()`. For each, computes peak altitude during the night window via `AltitudeService.ComputeTrack(ra, dec, dusk, dawn, stepMinutes=30)`. Filters: peak ≥ 30°, magnitude ≤ 10. Score = `(60 - magnitude) + (peakAlt / 90) * 20` (brighter and higher = better).

3. **Moon**: uses `Astronomy.Equator(Body.Moon, ...)` + `Astronomy.Horizon(...)` for altitude track. Always included if peak alt > 0 (any positive altitude).

4. **Planets**: iterates `[Mercury, Venus, Mars, Jupiter, Saturn, Uranus, Neptune]`. For each uses `Astronomy.Equator(body, time, observer, true, true)` for RA/Dec, then `AltitudeService.ComputeTrack` for altitude. Included if peak alt > 10°. Magnitude comes from `Astronomy.Illumination(body, time)`.

5. **Comets**: curated list of bright comets (kept in `wwwroot/data/comets.json`) with JPL Keplerian elements. Backend reads the file at startup, propagates position via `Astronomy.HelioState` + transform to equatorial. v1 starts with 5-10 hardcoded comets; can later grow to auto-fetch from MPC (`https://www.minorplanetcenter.net/iau/Ephemerides/Bright/2018/Soft00Bright.txt`, separate follow-up).

6. Sort everything by score, group by category (`Dso`, `Moon`, `Planet`, `Comet`), return top `limit`.

**DTO**:
```csharp
public record TonightCandidate(
    string Category,            // "Dso" | "Moon" | "Planet" | "Comet"
    string Name,
    string? CommonName,
    string? Type,
    double RaHours,
    double DecDeg,
    double? Magnitude,
    string? Size,               // "20.0' x 15.0'" or null
    double? SizeMajorArcmin,    // parsed from Size, for FOV comparison; null if unknown
    double? SizeMinorArcmin,
    double CurrentAltDeg,
    double CurrentAzDeg,
    double PeakAltDeg,
    DateTime PeakUtc,
    int Score,
    // Camera-FOV fit check. null when no camera connected, no active rig,
    // or the object has no known angular size (e.g. comets, point-source
    // planets). True = both axes fit inside the current camera FOV.
    bool? FitsCameraFov,
    double? CameraFovWidthArcmin,
    double? CameraFovHeightArcmin
);
```

The service computes camera FOV from the active rig profile (main focal length) + connected camera sensor dimensions (already exposed via `EquipmentManager.Camera.SensorWidthMm/SensorHeightMm`). Same formula already used by the FOV overlay in the Sky tab, extract into a small `FovCalculator` helper if not already present so both call sites share it. If any input missing → `FitsCameraFov = null`.

**New endpoint** in `SkyEndpoints.cs`:
- `GET /api/sky/tonights-best?lat={lat}&lon={lon}&limit={n}` → `IReadOnlyList<TonightCandidate>`

**Tests** `TonightsBestServiceTests.cs`:
- DSO filtering: objects below horizon all night excluded
- Planet inclusion when above horizon
- Moon always present
- Sort order: higher score first
- Empty result graceful (no exception when no objects above horizon)

### Phase TB-3: UI new TONIGHT tab

**Sidebar** in `index.html`, button between Sky and Weather:
```html
<button class="nav-btn" :class="{ active: tab === 'tonight' }"
        @click="tab = 'tonight'; loadTonightsBest()" title="Tonight's Best">
    <svg viewBox="0 0 24 24" width="22" height="22"><path d="M12 2 L13.5 10.5 L22 12 L13.5 13.5 L12 22 L10.5 13.5 L2 12 L10.5 10.5 Z" fill="currentColor"/></svg>
    <span class="nav-label">Tonight</span>
</button>
```

**Tab panel**, list of cards. Each card a horizontal-row layout ASIAIR-style:

```
┌─────────┬──────────────────────┬─────────────────────────┬──────────┐
│         │ M43                  │   altitude chart 12h    │  N       │
│ [thumb] │ RA 05h 36m 36s       │   18---22---02---06     │   ↗      │
│  64x64  │ DEC -05° 15' 16"     │                         │ 196° S   │
│         │ Mag 6.8 · Size 20'×15'│                        │          │
└─────────┴──────────────────────┴─────────────────────────┴──────────┘
```

**State** in `app.js`:
```js
tonight: { items: [], loading: false, error: '', lastFetched: null, filter: 'all' /* all|dso|planet|moon|comet */ },
_tonightLastKey: '',
_tonightCharts: {},  // per-card Chart.js instance keyed by candidate.name

async loadTonightsBest(force = false) { /* same memo+fetch pattern as loadWeatherForecast */ },
tonightFiltered() { /* returns items filtered by this.tonight.filter */ },
async loadTonightThumb(item) { /* fetches /api/sky/image, sets item.thumbUrl */ },
_renderTonightChart(canvas, item) { /* small altitude chart via Chart.js, reuse pattern from existing altitude chart */ },
```

**Each card**:
- Thumbnail (img tag, lazy load via IntersectionObserver, don't bombard NASA with 20 fetches on load)
- Info column: name (clickable link → sets it as skyTarget and opens the Sky tab centered), RA/Dec, magnitude/size/type
- **"Fits FOV" badge** when `item.fitsCameraFov === true` (green, e.g. `✓ Fits 1.2°×0.8° FOV`). When `false`, shows an amber badge (e.g. `⊘ Larger than 1.2°×0.8° FOV`). When `null` (no camera/data), no badge shown.
- Mini altitude chart (Chart.js, 12h window, no labels except X axis, red marker at current time)
- SVG compass widget: circle + arrow pointing to `currentAzDeg`, label `{az}° {NSEW}`
- **"Go to" button** (bottom-right corner), **only rendered when `mount.connected === true`**. Click:
  1. `this.skyTarget = { ra, dec, name: item.name }`
  2. `this.tab = 'sky'`
  3. `$nextTick(() => this.slewAndCenter())`, invokes the existing slew + plate solve + re-center workflow
  4. Sky tab shows progress via `slewCenterStatus` (already implemented, no change)

**Filters** (chips at the top of the tab):
- Category: All / DSOs / Planets / Moon / Comets
- Extra toggle **"Fits my FOV"** (chip): when active, filters `items.filter(i => i.fitsCameraFov === true)`. Disabled if no item has `fitsCameraFov !== null` (no camera connected).

**Click on the card name (not on the button)** → default behavior: `skyTarget = ...; tab = 'sky'; skyGoToMount()` (only centers the map, doesn't slew).

### Phase TB-4 (optional, follow-up): Auto-refresh of comets

Daily polling of the MPC bright-comets list, updates `wwwroot/data/comets.json` on disk. Hosted service in `Program.cs`. Can stay as a separate issue, v1 ships 5-10 hardcoded comets (Halley, Encke, Borrelly, NEOWISE when active, etc.).

## Files to create / modify

### Create
- `src/NINA.Headless/Services/CelestialImageService.cs`
- `src/NINA.Headless/Services/TonightsBestService.cs`
- `src/NINA.Headless/wwwroot/data/comets.json` (5-10 known comets with JPL elements)
- `tests/NINA.Headless.Test/CelestialImageServiceTests.cs`
- `tests/NINA.Headless.Test/TonightsBestServiceTests.cs`

### Modify
- `src/NINA.Headless/NINA.Headless.csproj`, add `CosineKitty.AstronomyEngine` PackageReference
- `src/NINA.Headless/Endpoints/SkyEndpoints.cs`, add `/image` and `/tonights-best` routes
- `src/NINA.Headless/Program.cs`, register `CelestialImageService` and `TonightsBestService` (singletons)
- `src/NINA.Headless/wwwroot/index.html`, sidebar button + tab panel
- `src/NINA.Headless/wwwroot/js/app.js`, `tonight` state, methods `loadTonightsBest` / `_renderTonightChart` / `loadTonightThumb`
- `src/NINA.Headless/wwwroot/css/app.css`, `.tonight-*` styles
- `README.md`, document the Tonight tab + attributions (NASA Image Library, Wikipedia, AstronomyEngine)

## Reuse of existing code

- `Services/AltitudeService.cs` → `ComputeTrack(ra, dec, from, to, step)` and `ComputeNightWindow(lat, lng)`, used by TonightsBestService for every candidate
- `Services/SkyCatalogService.cs` → `AllObjects()` enumerates the 200 catalog DSOs
- `Services/SlewCenterService.cs`, slew + plate solve + center workflow reused by the card's "Go to" button
- `Services/EquipmentManager.cs` → `Camera.SensorWidthMm/SensorHeightMm` + `ActiveRig.MainFocalLengthMm`, sources for the FOV calc (extract into a `FovCalculator` helper if not already shared with the Sky tab overlay)
- `Services/GeocodingService.cs`, pattern static HttpClient + timeout + cache identical to Nominatim
- `Services/WeatherForecastService.cs`, recent in-memory cache pattern with TTL, replicate for CelestialImageService (but with additional disk-backed cache)
- `wwwroot/js/lib/suncalc/suncalc.js`, `getMoonPosition(date, lat, lng)` for the Moon (no need for AstronomyEngine for it)
- `wwwroot/js/lib/chart.umd.min.js`, Chart.js already vendored, instantiated per card
- HTML pattern `sky-result-item` (index.html lines 1268-1287), visual base for the Tonight card
- Frontend `slewAndCenter()` / `slewCenterStatus`, wiring for the "Go to" button and progress indicator in the Sky tab (already exist, no change)
- Endpoint `/api/sky/altitude`, not called per card (TonightsBestService already computes peak; but if the UI wants drilldown it can reuse)

## End-to-end verification

1. **Build + tests**:
   - `dotnet build src/NINA.Headless/NINA.Headless.csproj` → no errors
   - `dotnet test tests/NINA.Headless.Test` → 187 current + ~10 new = 197

2. **Manual endpoint**:
   - `curl "http://localhost:5000/api/sky/tonights-best?lat=-5.18&lon=-37.36&limit=20"` → JSON sorted by score, containing mixed categories
   - `curl "http://localhost:5000/api/sky/image?name=M31"` → NASA thumb URL
   - `curl "http://localhost:5000/api/sky/image?name=ZZZ_nonexistent"` → `{ available: false }` without 500
   - Second identical request within < 30 days → cache file in `images/cache/` exists, no network call

3. **Happy-path UI flow**:
   - Settings with lat/lng set
   - Click sidebar "Tonight" → loading → list renders
   - Each card shows thumbnail (lazy-loaded on scroll), name/coords, altitude chart, compass
   - Click a card → goes to Sky tab with that object centered
   - "Planets" filter chip → only planets visible
   - No internet → cards show up without thumbnail (gray placeholder), but ephemeris data works normally

4. **Edge cases**:
   - Lat = 0 (equator): trivial visibility scenarios, should work
   - Lat = ±70 (high latitude in summer): twilight never gets dark, falls back to 12h window
   - Empty list (extreme latitude, everything below the horizon): UI shows "No objects visible tonight" without crashing

5. **Go to + Fits FOV**:
   - No mount connected → "Go to" button hidden from cards; "Fits my FOV" chip disabled
   - Mount present but no camera → "Go to" appears, "Fits my FOV" chip disabled (all `fitsCameraFov: null`)
   - With mount + camera + rig with focal → each card evaluates FOV; ✓/⊘ badge appears; "Fits my FOV" chip filters correctly
   - Click "Go to" → tab switches to Sky, map shows centered target, slew + plate solve progress bar appears, mount moves via the existing `SlewCenterService`

## Implementation order

1. **Phase TB-1**: CelestialImageService + endpoint + tests (isolated commit)
2. **Phase TB-2**: TonightsBestService + AstronomyEngine NuGet + endpoint + tests (isolated commit)
3. **Phase TB-3**: UI tab + CSS + Sky tab integration (isolated commit)
4. **Phase TB-4** (split, follow-up): comets auto-refresh

## Compatibility and license notes

- **NASA Image Library**: public domain, no heavy rate limit, no API key
- **Wikipedia REST**: CC BY-SA, no API key, attribution via card link
- **CosineKitty.AstronomyEngine**: MIT, ~150KB, no native deps, works on linux-arm64, linux-x64, win-x64
- **Comets**: orbital elements from JPL Small-Body Database are public domain
- **Privacy**: lat/lng sent to the backend (which may forward to NASA/Wikipedia in URL queries), behavior identical to the existing fetches
- **Image cache**: stored in `{AppDataDir}/images/cache/`, user can clear manually; size monitoring optional in a follow-up

---

# Plan: STUDIO panel, Complete post-processing workflow

## Context

Today Polaris captures .fits/.xisf frames and saves to disk via `ImageWriterService`, but has nothing to work with them afterwards. The astrophotographer has to export to PixInsight / Siril / Photoshop to do calibration, stacking, stretch and final export. The **STUDIO** panel brings the post-processing workflow into Polaris, frame browser, master frame creation, calibration, batch integration, debayer, background extraction, stretch and export.

**Decisions confirmed with the user**:
- **Scope**: complete workflow (all 7 planned sub-phases)
- **Format**: FITS only for now (XISF reader is a follow-up)
- **Strategy**: plan everything, execute incrementally

## Current state (what we already have, from the audit)

- **`NINA.Image.Portable/FileFormat/FITS/FITSReader.cs`**, decodes .fits (BITPIX 8/16/32/-32, BZERO/BSCALE, BAYERPAT) into an `ImageBuffer`
- **`NINA.Image.Portable/ImageData/ImageBuffer.cs`**, `ushort[] pixels`, Width, Height, BitDepth, BayerPattern
- **`NINA.Image.Portable/ImageData/BaseImageData.cs`**, rich wrapper with `ImageMetaData` (camera, telescope, observer, target, exposure, filter)
- **`NINA.Image.Portable/ImageAnalysis/StarDetector.cs`**, `Detect(ushort[], w, h) → List<DetectedStar>` with X/Y/HFR/Peak/Flux
- **`NINA.Image.Portable/ImageData/ImageStatistics.cs`**, Mean/Median/StDev/MAD/Min/Max + histogram
- **`NINA.Image.Portable/ImageAnalysis/AutoStretch.cs`**, MTF stretch → byte[] (8-bit preview)
- **`NINA.Headless/Services/LiveStackingService.cs`**, star-match + affine resample + running mean. Doesn't persist master to disk.
- **`NINA.Headless/Services/ImageWriterService.cs`**, writes .fits/.xisf using `profile.ImageOutputDir` + `ImageNamePattern` with tokens `{target}/{filter}/{exposure}/{date}/{seq}`
- **`NINA.Headless/Services/ImageRelayService.cs`**, in-memory buffer of the latest frame
- **`NINA.Headless/Services/AutoFocusService.cs`** + **`WebSocket/StatusStreamHandler.cs`**, long-running job pattern with progress via WebSocket

## Gaps to fill

- **XISF reader**: doesn't exist (writer only). Future phase.
- **RGGB/GRBG debayering**: ImageBuffer keeps the enum but never demosaics.
- **Saved-frame discovery**: no endpoint lists the .fits on disk.
- **Master frame creation**: no median / sigma-clipped mean routine over N frames.
- **Calibration pipeline**: subtract dark / divide flat doesn't exist.
- **Batch alignment+integration**: LiveStacking does it online; missing an offline version with more options (sigma-clip rejection, normalization by statistics).
- **Background extraction**: subtraction of a linear gradient or polynomial model.
- **Multi-format export**: 16-bit TIFF, PNG, JPEG of the final processed image.

## General architecture

Suggested directories under `ImageOutputDir`:
```
{ImageOutputDir}/
  lights/{target}/{filter}/light_*.fits          ← already lives here
  calibration/
    dark/dark_{exposure}_{gain}_*.fits           ← classified by header IMAGETYP=DARK
    bias/bias_*.fits
    flat/{filter}/flat_*.fits
    masters/                                      ← generated by STUDIO
      master_bias_{gain}.fits
      master_dark_{exposure}_{gain}.fits
      master_flat_{filter}_{gain}.fits
  calibrated/{target}/{filter}/calibrated_*.fits  ← generated by calibration
  integrated/{target}/{filter}/master_light_*.fits ← generated by stacking
  processed/{target}/finalimage.{tif,png,jpg}     ← final export
```

Metadata cache in SQLite or JSON at `{AppData}/NINA.Headless/studio/frames.db` to avoid re-parsing FITS headers on every list (3000 frames = ~5 MB cache).

---

## Sub-phases

### Phase ST-1: Frame Browser (foundational)

**Backend**, `src/NINA.Headless/Services/FrameLibraryService.cs`:
- `RescanAsync()`, recursively walks `ImageOutputDir`, opens each .fits header-only (without decoding pixels), extracts `IMAGETYP`, `FILTER`, `EXPOSURE`, `GAIN`, `OBJECT`, `DATE-OBS`, `XPIXSZ`/`YPIXSZ`, `NAXIS1/2`. Persists into `frames.db` (SQLite via `Microsoft.Data.Sqlite`).
- `Frame` record: `{ Id, Path, Type, Filter, ExposureSec, Gain, Target, DateObs, Width, Height, FileSizeBytes, IndexedAt }`.
- `Query(QueryParams)`, filters: type, filter, target, dateRange, gain. Returns paginated.
- `GetThumbnailAsync(int frameId, int maxDim=256)`, decodes FITS, applies `AutoStretch`, encodes JPEG with `System.Drawing` or `SixLabors.ImageSharp` (already has similar deps). Caches in `{AppData}/studio/thumbs/{frameId}.jpg`.

**Endpoints**, `src/NINA.Headless/Endpoints/StudioEndpoints.cs`:
- `POST /api/studio/rescan`, forces reindex (async, with progress via WS)
- `GET /api/studio/frames?type=&filter=&target=&dateFrom=&dateTo=&limit=&offset=`, paginated list
- `GET /api/studio/frames/{id}`, details (all FITS keywords)
- `GET /api/studio/frames/{id}/thumb`, 256px JPEG thumbnail
- `GET /api/studio/stats`, aggregates (total frames, total exposure, by target, by filter)

**Frontend**, new **STUDIO** tab in the sidebar (between ADV and SETTINGS, or after TONIGHT):
- Toolbar: rescan button, filters (type/filter/target/date), search box, view-mode toggle (grid/list)
- Grid of 256px thumbnails with overlay: filename, exposure, filter, HFR (if available)
- Click → selects (multi-select for following phases), double-click → viewer (ST-2)
- Status bar: count of selected frames + total exposure

**Tests**: rescan parsing mock FITS; query filter+pagination; thumbnail cache hit/miss.

---

### Phase ST-2: Single-Frame Viewer + Stretch + Export

**Backend**, `src/NINA.Headless/Services/FrameProcessingService.cs`:
- `StretchedJpegAsync(frameId, blackPoint, midPoint, whitePoint)`, decodes → applies manual stretch (extend `AutoStretch` to accept explicit params) → JPEG.
- `StretchedPngAsync(...)`, same, 16-bit-aware PNG via `ImageSharp`.
- `StatsAsync(frameId)`, full `ImageStatistics` + StarDetector results.
- `ExportTiffAsync(frameId, stretchedOr Linear)`, 16-bit TIFF via ImageSharp.

**Endpoints** (in StudioEndpoints):
- `GET /api/studio/frames/{id}/preview?black=&mid=&white=&format=jpeg`, live preview with adjustable stretch
- `GET /api/studio/frames/{id}/stats`, full stats + stars
- `POST /api/studio/frames/{id}/export?format=tif|png|jpg&...`, generates a file in `processed/`

**Frontend**, modal/inline viewer in the STUDIO tab:
- **OpenSeadragon** (already vendored in B3) with the rendered JPEG (re-fetch when sliders change, debounced 200ms)
- Sliders: Black point, Mid-tone, White point + "Auto stretch" button (computes default MTF)
- Histogram below the image (Chart.js bar, 256 bins)
- Side panel: numerical stats (mean, median, MAD, star count, avg HFR)
- Toggle: "Show star annotations" (canvas overlay with circles)
- Buttons: Export PNG / Export JPG / Export TIFF (16-bit)

**Tests**: stretch param boundaries; export formats.

---

### Phase ST-3: Master Calibration Frames

**Backend**, `src/NINA.Headless/Services/MasterFrameService.cs`:
- `CreateMasterAsync(IEnumerable<int> frameIds, MasterType type, IntegrationMethod method, string outputPath, CancellationToken ct)`
- `MasterType`: Bias, Dark, Flat
- `IntegrationMethod`: Median, Mean, SigmaClippedMean(sigmaLow=3, sigmaHigh=3, iterations=2)
- For each pixel: gathers the N values from the frames → applies the method → writes into the master
- Accumulates progress (0..100) emitted via WebSocket on the same `studio.master` channel
- Saves master to `calibration/masters/master_{type}_{key}.fits` with FITS headers IMAGETYP=MASTER{TYPE}, NSUBS=N, INTMETH=...
- Indexes the generated master in `FrameLibraryService` (special category)

**Endpoints**:
- `POST /api/studio/masters`, body: `{ frameIds, type, method }`. Returns `jobId`.
- `GET /api/studio/masters/{jobId}/status`, pollable status (also via WS)
- `GET /api/studio/masters`, list of existing masters (filters: type, gain, exposure, filter)
- `DELETE /api/studio/masters/{id}`

**Frontend**, "Create master" button in the STUDIO toolbar (enables when ≥3 frames selected):
- Modal: detects type automatically (header's IMAGETYP), allows override
- Integration method selector with explanatory tooltips
- Progress bar (each processed frame tick visible)
- Toast on completion with a link to the generated master
- Internal "Library" tab shows available masters with type/key/N/created date

**Tests**: median/mean/sigma-clip on synthetic pixels; outlier rejection.

---

### Phase ST-4: Light Frame Calibration

**Backend**, `src/NINA.Headless/Services/CalibrationService.cs`:
- `CalibrateAsync(lightFrameIds, masterDarkId?, masterFlatId?, masterBiasId?, ct)`
- Per frame: pixel = (light - dark - bias) / (flat - flatDark) * mean(flat - flatDark)
- Automatic match: for each light, picks the master dark closest in (exposure, gain), user can override
- Saves calibrated to `calibrated/{target}/{filter}/cal_{originalName}.fits` with header CALSTAT=BDF
- Progress via WS

**Endpoints**:
- `POST /api/studio/calibrate`, body: `{ lightIds, autoMatch:bool, masterDarkId?, masterFlatId? }`. JobId.
- `GET /api/studio/calibrate/{jobId}/status`

**Frontend**, "Calibrate" button in the STUDIO toolbar (enables with lights selected):
- Modal: shows which master dark/flat was auto-matched for each group (exposure/gain/filter), allows override per dropdown
- Progress + log of each frame processed
- Calibrated frames appear in the library with a "calibrated" badge

**Tests**: calibration pixel math; auto-match scoring.

---

### Phase ST-5: Batch Stack (offline integration)

**Backend**, `src/NINA.Headless/Services/BatchStackingService.cs`:
- Extends the logic of `LiveStackingService` to run in batch mode without dependency on the live stream
- `StackAsync(calibratedFrameIds, options, ct)`:
  - StarDetector on each frame → ref frame = best HFR
  - Affine align via star matching against the ref (logic already in LiveStacking)
  - Integration method: average / median / sigma-clipped average / Winsorized
  - Optional normalization: scale to mean / multiplicative
  - Per-pixel outlier rejection: cosmetic correction (hot/cold pixels), sigma rejection
- Saves master_light to `integrated/{target}/{filter}/master_{target}_{filter}_{N}x{exp}s.fits`
- FITS header: NCOMBINE, EXPTOTAL, INTMETH, REJECT

**Endpoints**:
- `POST /api/studio/integrate`, JobId
- `GET /api/studio/integrate/{jobId}/status` (WS too)

**Frontend**, "Integrate" button (enables with calibrated lights selected):
- Options modal (collapsible advanced section): method, sigma low/high, normalization, weights by HFR
- Progress: % done, frames aligned/rejected, current operation
- Result: link to the master_light + thumbnail

**Tests**: alignment math (transform application); sigma rejection edge cases.

---

### Phase ST-6: Debayer + Background Extraction

**Backend**, `src/NINA.Headless/Services/Demosaic/`:
- `Bilinear.cs`, RGGB/GRBG/BGGR/GBRG → R,G,B planes (linear interpolation)
- `Vng.cs` (optional, higher quality), Variable Number of Gradients
- API: `ushort[] gray + BayerPattern → (ushort[] r, ushort[] g, ushort[] b)`
- White balance: scale R and B to match the green channel mean (Gray World) or by the user

**Background extraction**, `src/NINA.Headless/Services/BackgroundExtraction.cs`:
- Sample grid: e.g. 8×6 boxes, in each picks median value (skips areas with stars via StarDetector mask)
- Fits polynomial (default degree 2): gradient surface
- Subtracts from every pixel
- Saves as a new frame with header BGREMOVE=POLY2

**Endpoints**:
- `POST /api/studio/debayer`, input frameId + bayer pattern (auto) → 3 new frames (R, G, B) OR 1 RGB
- `POST /api/studio/bgextract`, frameId + grid + degree → new frame

**Frontend**, additional actions in the viewer (ST-2):
- "Debayer" button (visible if BayerPattern detected)
- "Remove gradient" button with side-by-side preview

**Tests**: bilinear correctness on synthetic patterns; poly fit precision.

---

### Phase ST-7: Post-Processing Toolbox + Final Export

**Backend**, `src/NINA.Headless/Services/PostProcessing/`:
- `ChannelCombine.cs`, LRGB: L * (R/(R+G+B)/3, G/...,B/...) with L weight; or simple RGB stack
- `NoiseReduction.cs`, light gaussian blur (we already have `FastGaussianBlur` in the original upstream library but need to port, ~80 lines) + optional median filter
- `Sharpening.cs`, unsharp mask: img + factor × (img − blurred)
- `Saturation.cs`, RGB → HSV → multiply S → back

**Endpoints**:
- `POST /api/studio/postprocess/channel-combine`, body: `{ luminanceId, redId, greenId, blueId }`
- `POST /api/studio/postprocess/nr`, `{ frameId, radius }`
- `POST /api/studio/postprocess/sharpen`, `{ frameId, amount, radius }`
- `POST /api/studio/postprocess/saturation`, `{ frameId, factor }`

**Frontend**, "Pipeline" mode in the viewer:
- List of applied steps (drag-drop to reorder via the already-vendored Sortable.js)
- Each step: stretch / debayer / bg extract / NR / sharpen / saturation / export
- Live preview after each step
- "Save processed" → exports 16-bit TIFF, PNG or JPEG to `processed/{target}/`
- "Save pipeline" → JSON in the frame's metadata for reproducibility

**Tests**: each operation on a synthetic image.

---

## Files to create / modify

### Create
- `src/NINA.Headless/Services/FrameLibraryService.cs` (ST-1)
- `src/NINA.Headless/Services/FrameProcessingService.cs` (ST-2)
- `src/NINA.Headless/Services/MasterFrameService.cs` (ST-3)
- `src/NINA.Headless/Services/CalibrationService.cs` (ST-4)
- `src/NINA.Headless/Services/BatchStackingService.cs` (ST-5)
- `src/NINA.Headless/Services/Demosaic/Bilinear.cs` (ST-6)
- `src/NINA.Headless/Services/BackgroundExtraction.cs` (ST-6)
- `src/NINA.Headless/Services/PostProcessing/{ChannelCombine,NoiseReduction,Sharpening,Saturation}.cs` (ST-7)
- `src/NINA.Headless/Endpoints/StudioEndpoints.cs`
- Corresponding tests in `tests/NINA.Headless.Test/`

### Modify
- `src/NINA.Headless/Program.cs`, register 5+ new services + endpoints
- `src/NINA.Headless/NINA.Headless.csproj`, add `Microsoft.Data.Sqlite` + `SixLabors.ImageSharp` (multi-format export)
- `src/NINA.Image.Portable/ImageAnalysis/AutoStretch.cs`, accept explicit params (manual stretch)
- `src/NINA.Headless/wwwroot/index.html`, STUDIO sidebar button + tab panel
- `src/NINA.Headless/wwwroot/js/app.js`, `studio` state, methods for each phase
- `src/NINA.Headless/wwwroot/css/app.css`, `.studio-*` styles
- `README.md`, document the STUDIO pipeline

### Reuse (already exist)
- `NINA.Image.Portable/FileFormat/FITS/FITSReader.cs`, decodes frames
- `NINA.Image.Portable/ImageData/ImageBuffer.cs`, in-memory representation
- `NINA.Image.Portable/ImageAnalysis/StarDetector.cs`, for alignment + statistics
- `NINA.Image.Portable/ImageAnalysis/AutoStretch.cs`, for thumbnails + default preview
- `NINA.Image.Portable/ImageData/ImageStatistics.cs`, for per-frame quality
- `NINA.Image.Portable/FileFormat/FITS/FITSWriter.cs`, to save masters and calibrated
- `NINA.Headless/Services/LiveStackingService.cs`, alignment logic will be shared
- `NINA.Headless/Services/ImageWriterService.cs`, naming pattern reused
- `NINA.Headless/Services/AutoFocusService.cs`, long-running job + WS progress pattern
- `wwwroot/js/lib/openseadragon/openseadragon.min.js` (B3), interactive viewer
- `wwwroot/js/lib/chart.umd.min.js`, histogram
- `wwwroot/js/lib/sortable.min.js` (C6), pipeline reorder

## Verification per phase

### ST-1
- [ ] Rescan a directory with 100+ frames, metadata extracted correctly
- [ ] Filter by type/filter/target works
- [ ] Thumbnails generated and cached
- [ ] Responsive grid UI, double-click opens viewer

### ST-2
- [ ] Stretch sliders adjust preview in real-time (debounced)
- [ ] Star annotations on/off works
- [ ] Export JPG/PNG/TIFF generates valid files

### ST-3
- [ ] Master dark/flat/bias generated from 10+ frames
- [ ] Sigma-clip removes outliers
- [ ] Master indexed in the library with the correct type

### ST-4
- [ ] Light frames calibrated: dark subtracted + flat divided
- [ ] Auto-match of master correct by exposure/gain/filter
- [ ] Pixel values make sense (sharper stars, reduced flat gradient)

### ST-5
- [ ] 10+ lights aligned and integrated
- [ ] Master light saved with correct header
- [ ] Sigma-clip rejection visible in logs

### ST-6
- [ ] Debayer RGGB produces plausible RGB (gray world)
- [ ] BG extraction reduces gradient without affecting the nebula

### ST-7
- [ ] Pipeline with 5 steps runs end-to-end
- [ ] 16-bit TIFF export opens in PixInsight/Photoshop without loss
- [ ] Pipeline serialized as JSON, replay produces same result

## Execution order

1. **ST-1** (foundational browser), without this nothing else works
2. **ST-2** (viewer), first user-visible impact
3. **ST-3** (masters), kicks off the calibration pipeline
4. **ST-4** (calibration), depends on ST-3
5. **ST-5** (integration), depends on ST-4
6. **ST-6** (debayer + bg extract), can run in parallel with ST-5
7. **ST-7** (post-processing), complete feature

Each phase = 1 commit (or 2-3 sub-commits) with green tests before moving on.

## Compatibility and license notes

- **Microsoft.Data.Sqlite**: MIT, ~1 MB, native linux-arm64 support
- **SixLabors.ImageSharp**: Apache 2.0 (commercial requires paid license for prod use, but free for OSS like Polaris which is MPL 2.0). MIT alternative: `Magick.NET` or use `System.Drawing.Common` (linux requires libgdiplus, ARM64 issues).
  - **Decision**: use ImageSharp for TIFF/PNG/JPEG. MPL+Apache license is OSS-compatible.
- **Frame count scaling**: SQLite cache handles ≈10⁶ entries no problem. For typical sessions (hundreds to thousands of frames), zero issue.
- **Memory ceiling**: batch integration of 100×64MB frames = 6.4 GB, doesn't fit in memory. Stream in chunks: read 100 frames in 32×32 or 64×64 pixel windows, process tile-by-tile. I/O trade-off.
- **Cooperative cancellation**: every service accepts CancellationToken. UI has a "Cancel" button in every progress modal.
- **CPU intensive**: stacking/calibration maxes CPU. Consider `Parallel.For` with `MaxDegreeOfParallelism = Environment.ProcessorCount - 1` to keep the UI responsive on the RPi.
- **XISF reader**: left out of this plan. Add in a future sub-phase (~200 lines) if demand shows up. For now STUDIO ignores .xisf on rescan.
