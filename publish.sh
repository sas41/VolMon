#!/usr/bin/env bash
#
# VolMon cross-platform publish script
#
# Builds self-contained, single-file binaries for all five projects
# and places them under ./publish/<RID>/, along with the appropriate
# registration script for that platform.
#
# Output layout:
#   publish/
#     linux-x64/
#       VolMon.Daemon
#       VolMon.GUI
#       VolMon.Hardware
#       VolMon.HardwareGUI
#       Layouts/
#         VolMon_Layout_BeacnMix_*.json
#       volmon.png
#       register.sh
#     win-x64/
#       VolMon.Daemon.exe
#       VolMon.GUI.exe
#       VolMon.Hardware.exe
#       VolMon.HardwareGUI.exe
#       Layouts/
#         VolMon_Layout_BeacnMix_*.json
#       volmon.ico
#       register.ps1
#     osx-x64/
#       VolMon.Daemon
#       VolMon.GUI
#       VolMon.Hardware
#       VolMon.HardwareGUI
#       Layouts/
#         VolMon_Layout_BeacnMix_*.json
#       volmon.png
#       register.sh
#
# Usage:
#   ./publish.sh                # publish all platforms
#   ./publish.sh linux-x64      # publish only Linux
#   ./publish.sh win-x64        # publish only Windows
#   ./publish.sh osx-x64        # publish only macOS
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$SCRIPT_DIR"
PUBLISH_ROOT="$PROJECT_ROOT/publish"
REGISTER_DIR="$PROJECT_ROOT/install/register"
ASSETS_DIR="$PROJECT_ROOT/assets"
CONFIG="Release"

ALL_RIDS=(linux-x64 win-x64 osx-x64)
PROJECTS=(VolMon.Daemon VolMon.GUI VolMon.Hardware VolMon.HardwareGUI)

# ── Colors ────────────────────────────────────────────────────────────
red()   { printf '\033[0;31m%s\033[0m\n' "$*"; }
green() { printf '\033[0;32m%s\033[0m\n' "$*"; }
bold()  { printf '\033[1m%s\033[0m\n' "$*"; }

# ── Determine which RIDs to build ────────────────────────────────────
if [[ $# -gt 0 ]]; then
    RIDS=("$@")
    for rid in "${RIDS[@]}"; do
        valid=false
        for ok in "${ALL_RIDS[@]}"; do
            [[ "$rid" == "$ok" ]] && valid=true
        done
        if ! $valid; then
            red "Unknown RID: $rid"
            echo "Valid RIDs: ${ALL_RIDS[*]}"
            exit 1
        fi
    done
else
    RIDS=("${ALL_RIDS[@]}")
fi

# ── Checks ────────────────────────────────────────────────────────────
if ! command -v dotnet &>/dev/null; then
    red "Error: dotnet SDK not found. Install .NET 10 SDK first."
    red "  https://dotnet.microsoft.com/download"
    exit 1
fi

DOTNET_VERSION=$(dotnet --version 2>/dev/null || echo "0")
MAJOR="${DOTNET_VERSION%%.*}"
if [[ "$MAJOR" -lt 10 ]]; then
    red "Error: .NET 10 SDK required (found $DOTNET_VERSION)."
    exit 1
fi

# ── Publish ───────────────────────────────────────────────────────────
publish_project() {
    local rid="$1"
    local project="$2"
    local outdir="$PUBLISH_ROOT/$rid"

    echo "  $project ($rid)"
    dotnet publish "$PROJECT_ROOT/src/$project/$project.csproj" \
        --configuration "$CONFIG" \
        --runtime "$rid" \
        --self-contained \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:PublishTrimmed=false \
        --output "$outdir" \
        -v quiet
}

# Map RID to the register script that should be copied into its folder.
register_script_for_rid() {
    case "$1" in
        linux-x64) echo "$REGISTER_DIR/register-linux.sh"  ;;
        osx-x64)   echo "$REGISTER_DIR/register-macos.sh"  ;;
        win-x64)   echo "$REGISTER_DIR/register-windows.ps1" ;;
    esac
}

# Destination filename inside the publish folder.
register_dest_for_rid() {
    case "$1" in
        linux-x64) echo "register.sh"  ;;
        osx-x64)   echo "register.sh"  ;;
        win-x64)   echo "register.ps1" ;;
    esac
}

# Copy the platform-appropriate icon file(s) into the output folder.
copy_icons_for_rid() {
    local rid="$1"
    local outdir="$PUBLISH_ROOT/$rid"

    case "$rid" in
        linux-x64)
            cp "$ASSETS_DIR/VolMonLogo-256.png" "$outdir/volmon.png"
            ;;
        osx-x64)
            cp "$ASSETS_DIR/VolMonLogo-256.png" "$outdir/volmon.png"
            ;;
        win-x64)
            cp "$ASSETS_DIR/VolMonLogo.ico" "$outdir/volmon.ico"
            ;;
    esac
}

for rid in "${RIDS[@]}"; do
    bold "Publishing for $rid..."

    # Clean the output directory so stale artifacts don't linger
    rm -rf "${PUBLISH_ROOT:?}/$rid"
    mkdir -p "$PUBLISH_ROOT/$rid"

    for project in "${PROJECTS[@]}"; do
        publish_project "$rid" "$project"
    done

    # Copy the platform-appropriate register script
    src="$(register_script_for_rid "$rid")"
    dest="$PUBLISH_ROOT/$rid/$(register_dest_for_rid "$rid")"
    cp "$src" "$dest"
    chmod +x "$dest" 2>/dev/null || true

    # Copy icon assets
    copy_icons_for_rid "$rid"

    green "  Done: $PUBLISH_ROOT/$rid/"
done

echo ""
green "All builds complete."
echo ""
for rid in "${RIDS[@]}"; do
    echo "  $rid -> $PUBLISH_ROOT/$rid/"
done
echo ""
