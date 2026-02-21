#!/usr/bin/env bash
#
# Generates PNG and ICO icon files from VolMonLogo.svg.
#
# Output:
#   VolMonLogo.png      512x512 PNG
#   VolMonLogo-256.png  256x256 PNG
#   VolMonLogo.ico      Multi-size ICO (16, 32, 48, 64, 128, 256)
#
# Requires: ImageMagick (magick or convert)
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SVG="$SCRIPT_DIR/VolMonLogo.svg"

if [[ ! -f "$SVG" ]]; then
    echo "Error: $SVG not found" >&2
    exit 1
fi

if command -v magick &>/dev/null; then
    IM="magick"
elif command -v convert &>/dev/null; then
    IM="convert"
else
    echo "Error: ImageMagick not found. Install it first." >&2
    exit 1
fi

echo "Generating icons from $SVG..."

# 512x512 PNG
$IM -background none -density 384 "$SVG" -resize 512x512 -define png:color-type=6 \
    "$SCRIPT_DIR/VolMonLogo.png"
echo "  VolMonLogo.png      (512x512)"

# 256x256 PNG
$IM -background none -density 384 "$SVG" -resize 256x256 -define png:color-type=6 \
    "$SCRIPT_DIR/VolMonLogo-256.png"
echo "  VolMonLogo-256.png  (256x256)"

# Multi-size ICO
$IM -background none -density 384 "$SVG" \
    \( -clone 0 -resize 16x16 \) \
    \( -clone 0 -resize 32x32 \) \
    \( -clone 0 -resize 48x48 \) \
    \( -clone 0 -resize 64x64 \) \
    \( -clone 0 -resize 128x128 \) \
    \( -clone 0 -resize 256x256 \) \
    -delete 0 \
    "$SCRIPT_DIR/VolMonLogo.ico"
echo "  VolMonLogo.ico      (16, 32, 48, 64, 128, 256)"

echo "Done."
