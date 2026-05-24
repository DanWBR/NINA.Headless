#!/usr/bin/env bash
#
# build-stellarium-web.sh — compile stellarium-web-engine to JS+WASM
# using Emscripten inside a Docker container, then copy the outputs
# into wwwroot/sky/js/wasm/ so they ship with Polaris.
#
# Why Docker: the upstream build needs a specific Emscripten SDK +
# SCons. Pinning everything to a container removes host-toolchain
# drift and keeps the build reproducible across Windows / macOS /
# Linux developers.
#
# Run this whenever:
#   - The submodule pin advances (`git submodule update --remote
#     external/stellarium-web-engine`).
#   - You change any source under external/stellarium-web-engine.
#
# After the script finishes, commit:
#   git add src/NINA.Polaris/wwwroot/sky/js/wasm/
#   git commit -m "Sky: bump stellarium-web-engine build"
#
# Requirements: Docker Desktop running (Windows / macOS) or
# Docker Engine (Linux). No host-side toolchain needed.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SUBMODULE_DIR="${REPO_ROOT}/external/stellarium-web-engine"
OUTPUT_DIR="${REPO_ROOT}/src/NINA.Polaris/wwwroot/sky/js/wasm"
# Pinned Emscripten version. Stay on the cached 3.1.45 image —
# the build incompatibility isn't the emsdk version, it's
# upstream's SConstruct passing linker-only flags (MODULARIZE,
# ALLOW_MEMORY_GROWTH, EXPORT_NAME, ...) on per-file compile
# invocations. Modern emcc rejects this under -Werror. We disable
# -Werror via scons's `werror=0` flag below, so any emsdk works.
EMSDK_IMAGE="emscripten/emsdk:3.1.45"

# ---------- preflight ----------

if [ ! -d "${SUBMODULE_DIR}/.git" ] && [ ! -f "${SUBMODULE_DIR}/.git" ]; then
    echo "✗ Submodule not initialised. Run:"
    echo "    git submodule update --init external/stellarium-web-engine"
    exit 1
fi

if ! command -v docker >/dev/null 2>&1; then
    echo "✗ Docker is not installed or not on PATH."
    echo "  Install Docker Desktop (Win/Mac) or Docker Engine (Linux)."
    exit 1
fi

if ! docker info >/dev/null 2>&1; then
    echo "✗ Docker daemon is not running. Start Docker Desktop and retry."
    exit 1
fi

mkdir -p "${OUTPUT_DIR}"

# ---------- build inside container ----------

echo "→ Pulling ${EMSDK_IMAGE} (first run only, ~500MB) ..."
docker pull "${EMSDK_IMAGE}"

echo "→ Building stellarium-web-engine (this takes 3-10 minutes)..."
# Mount the submodule read-write so SCons can write its build artefacts.
# UID/GID match keeps the resulting files owned by the host user instead
# of root (Linux gotcha; harmless on Windows/macOS Docker Desktop).
HOST_UID="$(id -u 2>/dev/null || echo 1000)"
HOST_GID="$(id -g 2>/dev/null || echo 1000)"

# Disable MSYS POSIX-path translation when running under Git Bash on
# Windows. Otherwise the `-w /src` arg gets rewritten to
# `C:/Program Files/Git/src` and docker bombs with "working directory
# is invalid, it needs to be an absolute path". `MSYS_NO_PATHCONV=1`
# is the documented escape hatch; harmless on Linux/macOS (the var
# isn't read by any other shell).
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL='*'

docker run --rm \
    --user "${HOST_UID}:${HOST_GID}" \
    -v "${SUBMODULE_DIR}:/src" \
    -w /src \
    "${EMSDK_IMAGE}" \
    bash -c "
        set -e
        # The emscripten/emsdk image ships Python + Emscripten but
        # NOT scons. The upstream Makefile invokes 'emscons scons'
        # which is just a wrapper that prepends the emsdk env vars
        # to a system 'scons' binary — install it via pip in the
        # container's own scope (we're running as the host user so
        # use --user to avoid needing root).
        echo '→ Installing scons in container'
        pip install --quiet --user scons
        # pip --user installs into ~/.local/bin which isn't on
        # PATH by default for the non-root user inside the image.
        export PATH=\"\$HOME/.local/bin:\$PATH\"
        # Belt-and-suspenders for Windows checkouts: strip CRLF
        # line endings from every text source file in the submodule
        # tree. Git on Windows checks the submodule out with
        # autocrlf=true unless the parent .gitattributes pins it,
        # and CRLF breaks several things:
        #   - Python shebangs ('#!/usr/bin/python3\r' → ENOENT)
        #   - shader sources (auto-embedded into .inl as C string
        #     literals — the trailing \r becomes a stray byte
        #     inside the literal and clang reports "missing
        #     terminating '\"' character" for thousands of lines)
        #   - SCons / Make text parsing
        #
        # The durable fix is the '-text' rule for
        # external/stellarium-web-engine/** in the repo root
        # .gitattributes. This in-build strip handles already-
        # checked-out trees so the user doesn't have to nuke +
        # re-init the submodule.
        #
        # Whitelist by extension so we don't accidentally rewrite
        # genuine binaries (images, .ttf, etc.) that happen to have
        # 0x0D 0x0A pairs in their byte stream.
        echo '→ Stripping CRLF from source files'
        find . -type f \
            \( -name '*.c'  -o -name '*.h'  -o -name '*.cpp' -o -name '*.hpp' \
            -o -name '*.cc' -o -name '*.hh' -o -name '*.inl' -o -name '*.py' \
            -o -name '*.js' -o -name '*.json' -o -name '*.glsl' -o -name '*.frag' \
            -o -name '*.vert' -o -name '*.shader' -o -name '*.txt' -o -name '*.md' \
            -o -name '*.css' -o -name '*.html' -o -name '*.htm' \
            -o -name 'SConstruct' -o -name 'SConscript' -o -name 'Makefile' \
            -o -name '*.mk' -o -name '*.sh' \) \
            -exec sed -i 's/\r\$//' {} +
        # Re-mark the python scripts executable (sed -i doesn't
        # touch the mode bit but a safety chmod doesn't hurt).
        chmod +x tools/*.py 2>/dev/null || true
        # Same problem can hit upstream tools/*.py shebangs even after
        # CRLF strip — they hard-code '#!/usr/bin/python3' which only
        # resolves on Debian-style images. emscripten/emsdk has
        # python3 elsewhere on PATH (typically /emsdk/python/...).
        # Rewrite to env-based lookup.
        echo '→ Patching shebangs in tools/*.py'
        for f in tools/*.py; do
            if [ -f \"\$f\" ] && head -1 \"\$f\" | grep -q '^#!/usr/bin/python3\$'; then
                sed -i '1s|^#!/usr/bin/python3\$|#!/usr/bin/env python3|' \"\$f\"
            fi
        done
        echo '→ Configuring Emscripten environment'
        source /emsdk/emsdk_env.sh
        echo '→ Cleaning previous build artefacts'
        rm -rf build/
        # Bypass 'make js' (which hard-codes 'scons mode=release')
        # and call scons directly with werror=0 so the per-file
        # 'linker setting ignored during compilation' warnings stay
        # warnings instead of being promoted to errors. The Makefile's
        # only purpose is wrapping 'emscons scons mode=release'; we
        # add the one flag it lacks.
        echo '→ Running emscons scons mode=release werror=0 (-j8)'
        emscons scons -j8 mode=release werror=0
    "

# ---------- collect outputs ----------

BUILT_JS="${SUBMODULE_DIR}/build/stellarium-web-engine.js"
BUILT_WASM="${SUBMODULE_DIR}/build/stellarium-web-engine.wasm"

if [ ! -f "${BUILT_JS}" ] || [ ! -f "${BUILT_WASM}" ]; then
    echo "✗ Build finished but expected outputs are missing:"
    echo "    ${BUILT_JS}"
    echo "    ${BUILT_WASM}"
    exit 1
fi

cp "${BUILT_JS}" "${OUTPUT_DIR}/stellarium-web-engine.js"
cp "${BUILT_WASM}" "${OUTPUT_DIR}/stellarium-web-engine.wasm"

JS_SIZE=$(du -h "${OUTPUT_DIR}/stellarium-web-engine.js" | cut -f1)
WASM_SIZE=$(du -h "${OUTPUT_DIR}/stellarium-web-engine.wasm" | cut -f1)
PINNED_SHA=$(git -C "${SUBMODULE_DIR}" rev-parse --short HEAD)

echo ""
echo "✓ Build complete (pinned at ${PINNED_SHA})"
echo "    ${OUTPUT_DIR}/stellarium-web-engine.js   (${JS_SIZE})"
echo "    ${OUTPUT_DIR}/stellarium-web-engine.wasm (${WASM_SIZE})"
echo ""
echo "Next:"
echo "    git add src/NINA.Polaris/wwwroot/sky/js/wasm/"
echo "    git commit -m \"Sky: bump stellarium-web-engine build (${PINNED_SHA})\""
