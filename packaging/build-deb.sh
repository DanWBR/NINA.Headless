#!/bin/bash
# Builds polaris_VERSION_ARCH.deb from a fresh dotnet publish.
#
# Usage:
#   ./packaging/build-deb.sh [VERSION] [ARCH]
#
# Defaults:
#   VERSION: 0.0.0-dev
#   ARCH:    arm64    (Pi 4 / 5)
#
# Examples:
#   ./packaging/build-deb.sh 0.1.0 arm64
#   ./packaging/build-deb.sh 0.1.0 amd64
#
# Output: polaris_${VERSION}_${ARCH}.deb in the current working dir.
#
# Requirements:
#   - dotnet SDK 10.x with linux-arm64 and linux-x64 runtime targets
#   - dpkg-deb (any Debian / Ubuntu host or WSL)
#   - gzip (for changelog)

set -euo pipefail

VERSION="${1:-0.0.0-dev}"
ARCH="${2:-arm64}"

# Resolve dotnet. Many users install the .NET SDK via the install
# script (~/.dotnet) or the Microsoft apt feed (/usr/share/dotnet)
# without dropping a symlink on PATH for non-login shells. Look in
# the usual spots before bailing so the script just works without
# the operator hand-editing PATH.
if command -v dotnet >/dev/null 2>&1; then
    DOTNET="$(command -v dotnet)"
else
    for candidate in \
        "$HOME/.dotnet/dotnet" \
        "/usr/share/dotnet/dotnet" \
        "/usr/local/share/dotnet/dotnet" \
        "/opt/dotnet/dotnet" \
        "/snap/dotnet-sdk/current/dotnet"; do
        if [ -x "$candidate" ]; then
            DOTNET="$candidate"
            break
        fi
    done
fi
if [ -z "${DOTNET:-}" ]; then
    cat >&2 <<EOF
ERROR: dotnet SDK not found.

Looked in PATH and the standard install locations:
  ~/.dotnet/dotnet
  /usr/share/dotnet/dotnet
  /usr/local/share/dotnet/dotnet
  /opt/dotnet/dotnet
  /snap/dotnet-sdk/current/dotnet

Install the .NET 10 SDK first:
  curl -L https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
  export PATH="\$HOME/.dotnet:\$PATH"
EOF
    exit 1
fi
echo "    dotnet:     $DOTNET"

case "$ARCH" in
    arm64)
        RID=linux-arm64
        ;;
    amd64)
        RID=linux-x64
        ;;
    *)
        echo "Unsupported arch: $ARCH (use arm64 or amd64)" >&2
        exit 1
        ;;
esac

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUILD_DIR="$REPO_ROOT/build/deb-${ARCH}"
SRC_DEB="$REPO_ROOT/packaging/deb"
OUTPUT="$REPO_ROOT/polaris_${VERSION}_${ARCH}.deb"

echo "==> Building polaris_${VERSION}_${ARCH}.deb"
echo "    RID:        $RID"
echo "    Source:     $SRC_DEB"
echo "    Staging:    $BUILD_DIR"
echo "    Output:     $OUTPUT"
echo ""

# 1. Fresh staging area
rm -rf "$BUILD_DIR"
mkdir -p "$BUILD_DIR"
cp -r "$SRC_DEB/." "$BUILD_DIR/"

# 2. Publish self-contained Polaris into /opt/polaris
#    -p:Version forwards the VERSION arg to MSBuild so the
#    assembly + the UI version banner show the same number that the
#    .deb is named with, instead of the auto-generated date-based
#    stamp the csproj falls back to for local dev builds.
echo "==> dotnet publish (this takes a few minutes)"
"$DOTNET" publish "$REPO_ROOT/src/NINA.Polaris/NINA.Polaris.csproj" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -p:Version="$VERSION" \
    -o "$BUILD_DIR/opt/polaris" \
    --nologo

# 3. Restore the conffile that publish may have overwritten
cp "$SRC_DEB/opt/polaris/appsettings.json" "$BUILD_DIR/opt/polaris/appsettings.json"

# 4. Variable substitution in control + changelog
sed -i "s/__VERSION__/${VERSION}/g; s/__ARCH__/${ARCH}/g" \
    "$BUILD_DIR/DEBIAN/control"

# 5. Generate a minimal changelog (Debian wants compressed)
mkdir -p "$BUILD_DIR/usr/share/doc/polaris"
TODAY=$(date -R)
cat > "$BUILD_DIR/usr/share/doc/polaris/changelog.Debian" <<EOF
polaris (${VERSION}) unstable; urgency=medium

  * Automated build from upstream commit $(cd "$REPO_ROOT" && git rev-parse --short HEAD 2>/dev/null || echo unknown).

 -- Daniel Boas <daniel@danielboas.com>  ${TODAY}
EOF
gzip -9 -n -f "$BUILD_DIR/usr/share/doc/polaris/changelog.Debian"

# 6. Permissions (dpkg-deb is picky about these)
chmod 0755 "$BUILD_DIR/DEBIAN/postinst" \
           "$BUILD_DIR/DEBIAN/prerm" \
           "$BUILD_DIR/DEBIAN/postrm"
chmod 0644 "$BUILD_DIR/DEBIAN/control" \
           "$BUILD_DIR/DEBIAN/conffiles"

# Binary needs to be executable; other Polaris payload is readable
chmod 0755 "$BUILD_DIR/opt/polaris/NINA.Polaris" 2>/dev/null || true
find "$BUILD_DIR/opt/polaris" -type d -exec chmod 0755 {} \;
find "$BUILD_DIR/opt/polaris" -type f -exec chmod 0644 {} \;
chmod 0755 "$BUILD_DIR/opt/polaris/NINA.Polaris"
# .so files need exec
find "$BUILD_DIR/opt/polaris" -name "*.so" -exec chmod 0755 {} \;
find "$BUILD_DIR/opt/polaris" -name "*.so.*" -exec chmod 0755 {} \;

# Systemd units + config
chmod 0644 "$BUILD_DIR/lib/systemd/system/polaris.service"
chmod 0644 "$BUILD_DIR/lib/systemd/system/polaris-wifi-bootstrap.service" 2>/dev/null || true
chmod 0644 "$BUILD_DIR/opt/polaris/appsettings.json"
chmod 0644 "$BUILD_DIR/usr/share/doc/polaris/README" \
           "$BUILD_DIR/usr/share/doc/polaris/copyright" \
           "$BUILD_DIR/usr/share/doc/polaris/changelog.Debian.gz"
find "$BUILD_DIR/usr" -type d -exec chmod 0755 {} \;
find "$BUILD_DIR/lib" -type d -exec chmod 0755 {} \;
# WIFI-5: bootstrap script + polkit rule
if [ -f "$BUILD_DIR/opt/polaris/bin/polaris-wifi-bootstrap.sh" ]; then
    chmod 0755 "$BUILD_DIR/opt/polaris/bin/polaris-wifi-bootstrap.sh"
fi
find "$BUILD_DIR/opt/polaris/bin" -type d -exec chmod 0755 {} \; 2>/dev/null || true
if [ -f "$BUILD_DIR/etc/polkit-1/rules.d/50-polaris-nm.rules" ]; then
    chmod 0644 "$BUILD_DIR/etc/polkit-1/rules.d/50-polaris-nm.rules"
fi
find "$BUILD_DIR/etc" -type d -exec chmod 0755 {} \; 2>/dev/null || true

# 7. Build the .deb
echo "==> dpkg-deb --build"
rm -f "$OUTPUT"
dpkg-deb --root-owner-group --build "$BUILD_DIR" "$OUTPUT"

# 8. Sanity check
echo ""
echo "==> Done."
ls -lh "$OUTPUT"
echo ""
echo "Metadata:"
dpkg-deb -I "$OUTPUT" | head -30
echo ""
echo "Install with:"
echo "  sudo apt install ./$(basename "$OUTPUT")"
