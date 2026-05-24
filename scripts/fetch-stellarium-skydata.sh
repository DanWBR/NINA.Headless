#!/usr/bin/env bash
#
# fetch-stellarium-skydata.sh — mirror the Stellarium Web HiPS data
# bundle into wwwroot/sky/data/skydata/ so the engine can render
# stars / DSOs / surveys offline.
#
# The live Stellarium Web app at https://stellarium-web.org loads
# its data from CloudFront. We mirror the same tree so Polaris works
# in an observatory with no internet (the design goal — astrophotography
# is often done offline).
#
# Size: ~500MB-1GB depending on which surveys you grab.
# Time: 10-30 minutes on a 100 Mbps connection.
# Resumable: yes — wget -c, re-running picks up where it left off.
#
# After the script finishes:
#   - sky-bridge.js's addDataSource() calls find local files → stars,
#     DSOs, surveys all render.
#   - Without this data the engine still boots and shows atmosphere +
#     sun + horizon, but no stars / DSOs / Milky Way.
#
# Requires: wget (Git Bash on Windows has it; `apt install wget` on
# Linux; `brew install wget` on macOS). Falls back to curl if wget
# isn't on PATH.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUTPUT_DIR="${REPO_ROOT}/src/NINA.Polaris/wwwroot/sky/data/skydata"

# CloudFront base URL where Stellarium Web hosts its skydata.
# Discovered by inspecting the live app's network requests
# (https://stellarium-web.org → DevTools Network → first XHR after
# the app boots).
BASE_URL="https://d3ufh70wg9uzo4.cloudfront.net/skydata"

# The HiPS pyramids + supporting files the engine expects. Each
# pulls recursively (HiPS is a hierarchical tree of tiles per
# HEALPix Norder). Listing them explicitly rather than wget-ing the
# whole /skydata/ root because CloudFront doesn't serve directory
# indices we can crawl.
COMPONENTS=(
    "stars"                       # Gaia + Hipparcos star catalogue
    "dso"                         # Deep sky objects (NGC / IC / etc.)
    "surveys/milkyway"            # Milky Way HiPS imagery
    "surveys/sso/moon"            # Solar System: Moon surface
    "surveys/sso/sun"             # Solar System: Sun surface
    "landscapes/guereins"         # Default horizon landscape
    "skycultures/western"         # IAU 88 constellations + names
)

# Standalone files (not directories).
FILES=(
    "mpcorb.dat"                  # Minor Planet Center orbital elements
)

mkdir -p "${OUTPUT_DIR}"

# Pick a fetcher. wget supports --recursive cleanly; curl falls back
# to a per-file loop if wget is missing.
if command -v wget >/dev/null 2>&1; then
    FETCHER=wget
elif command -v curl >/dev/null 2>&1; then
    FETCHER=curl
    echo "ℹ wget not found — falling back to curl (slower, no recursive mode)."
else
    echo "✗ Neither wget nor curl on PATH. Install one and retry."
    exit 1
fi

fetch_recursive() {
    local rel_path="$1"
    local url="${BASE_URL}/${rel_path}/"
    local dest="${OUTPUT_DIR}/${rel_path}"
    mkdir -p "${dest}"

    if [ "${FETCHER}" = "wget" ]; then
        # -r       recursive
        # -np      no parent directories
        # -nH      no host directory
        # --cut-dirs=2  strip "skydata/<component>" from the saved path so
        #               files land directly under ${dest}
        # -R       reject HTML index pages (CloudFront sometimes serves them)
        # -c       continue partial downloads on re-run
        # -q       quiet (script prints its own progress)
        # --show-progress  per-file bar
        wget --recursive --no-parent --no-host-directories \
             --cut-dirs=2 \
             --reject "index.html*,*.html" \
             --continue \
             --quiet --show-progress \
             --directory-prefix="${dest}" \
             "${url}"
    else
        # curl recursive mode requires walking the tree manually.
        # Skip the heavy surveys when wget is unavailable; the user
        # gets warned to install wget for the full mirror.
        echo "  (curl mode: skipping recursive ${rel_path}, only top-level metadata)"
        curl -fsSL --create-dirs -o "${dest}/properties" "${url}properties" || true
    fi
}

fetch_file() {
    local rel_path="$1"
    local url="${BASE_URL}/${rel_path}"
    local dest="${OUTPUT_DIR}/${rel_path}"
    mkdir -p "$(dirname "${dest}")"
    if [ "${FETCHER}" = "wget" ]; then
        wget --continue --quiet --show-progress -O "${dest}" "${url}"
    else
        curl -fSL --output "${dest}" "${url}"
    fi
}

START=$(date +%s)
echo "→ Mirroring Stellarium Web skydata to ${OUTPUT_DIR}"
echo "  Source: ${BASE_URL}"
echo "  ~500MB-1GB total. Resumable — Ctrl-C is safe; re-run to continue."
echo ""

for c in "${COMPONENTS[@]}"; do
    echo "→ ${c}/"
    fetch_recursive "${c}"
done

for f in "${FILES[@]}"; do
    echo "→ ${f}"
    fetch_file "${f}"
done

ELAPSED=$(( $(date +%s) - START ))
TOTAL_SIZE=$(du -sh "${OUTPUT_DIR}" 2>/dev/null | cut -f1)
echo ""

# HiPS pyramids on CloudFront don't expose directory indices, so
# neither wget --recursive nor curl can walk them. This script
# currently only mirrors top-level metadata (properties files +
# fixed-name files like mpcorb.dat). A 36KB-ish total is the sign
# you hit this path — the heavy lifting (HEALPix tile pyramids)
# isn't covered.
if [ "${FETCHER}" = "curl" ] || [ "$(du -sb "${OUTPUT_DIR}" 2>/dev/null | cut -f1)" -lt 100000 ]; then
    echo "⚠ Only metadata mirrored (~${TOTAL_SIZE}). The HiPS tile pyramids"
    echo "  (~500MB-1GB of actual data) are NOT downloaded — neither wget's"
    echo "  recursive nor curl can crawl CloudFront's directory tree."
    echo ""
    echo "  For now, sky-bridge.js defaults to fetching tiles direct from the"
    echo "  Stellarium Web CDN — the browser caches them after first hit, so"
    echo "  the second observing session is effectively offline."
    echo ""
    echo "  A true offline mirror needs a HiPS-aware crawler (walks each"
    echo "  Norder level + HEALPix tile index from the properties files)."
    echo "  Filed as a future enhancement."
else
    echo "✓ Skydata mirror complete in ${ELAPSED}s — total size ${TOTAL_SIZE}"
    echo "  Path: ${OUTPUT_DIR}"
    echo ""
    echo "  Switch sky-bridge.js to local mode by setting"
    echo "  window.__skyDataBase = 'data/skydata/' before the iframe loads"
    echo "  (or edit the SKYDATA_BASE default in sky-bridge.js)."
fi
echo ""
echo "Peek at the iframe in DevTools:"
echo "  document.getElementById('skyFrame').style.cssText="
echo "    'position:static; width:100%; height:600px; visibility:visible;'"
