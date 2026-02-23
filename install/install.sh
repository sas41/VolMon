#!/usr/bin/env bash
#
# VolMon installer for Linux
#
# Publishes self-contained binaries for the daemon, GUI, and CLI,
# installs them to ~/.local/bin, sets up the systemd user service,
# desktop entry, and icon.
#
# Usage:
#   ./install/install.sh            # install
#   ./install/install.sh --uninstall # remove everything
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

INSTALL_BIN="$HOME/.local/bin"
INSTALL_LIB="$HOME/.local/lib/volmon"
SYSTEMD_DIR="$HOME/.config/systemd/user"
DESKTOP_DIR="$HOME/.local/share/applications"
ICON_DIR="$HOME/.local/share/icons/hicolor"

RID="linux-x64"
CONFIG="Release"

# ── Colors ────────────────────────────────────────────────────────────
red()   { printf '\033[0;31m%s\033[0m\n' "$*"; }
green() { printf '\033[0;32m%s\033[0m\n' "$*"; }
bold()  { printf '\033[1m%s\033[0m\n' "$*"; }

# ── Uninstall ─────────────────────────────────────────────────────────
uninstall() {
    bold "Uninstalling VolMon..."

    # Stop and disable the service
    if systemctl --user is-active volmon &>/dev/null; then
        systemctl --user stop volmon
    fi
    if systemctl --user is-enabled volmon &>/dev/null; then
        systemctl --user disable volmon
    fi

    rm -f "$SYSTEMD_DIR/volmon.service"
    rm -f "$INSTALL_BIN/volmon-daemon"
    rm -f "$INSTALL_BIN/volmon-gui"
    rm -f "$INSTALL_BIN/volmon"
    rm -rf "$INSTALL_LIB"
    rm -f "$DESKTOP_DIR/volmon.desktop"
    rm -f "$ICON_DIR/256x256/apps/volmon.png"

    systemctl --user daemon-reload 2>/dev/null || true
    gtk-update-icon-cache -f -t "$ICON_DIR" 2>/dev/null || true

    green "VolMon uninstalled."
    exit 0
}

if [[ "${1:-}" == "--uninstall" ]]; then
    uninstall
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
bold "Publishing VolMon ($CONFIG, $RID)..."

dotnet restore "$PROJECT_ROOT/VolMon.slnx" --runtime "$RID" -v quiet

publish_project() {
    local project="$1"
    local name="$2"
    echo "  Publishing $name..."
    dotnet publish "$PROJECT_ROOT/src/$project/$project.csproj" \
        --configuration "$CONFIG" \
        --runtime "$RID" \
        --self-contained \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:PublishTrimmed=false \
        --output "$INSTALL_LIB/$name" \
        -v quiet
}

publish_project_slim() {
    local project="$1"
    local name="$2"
    echo "  Publishing $name..."
    dotnet publish "$PROJECT_ROOT/src/$project/$project.csproj" \
        --configuration "$CONFIG" \
        --runtime "$RID" \
        -p:PublishTrimmed=true \
        --output "$INSTALL_LIB/$name" \
        -v quiet
}

publish_project "VolMon.Daemon" "daemon"
publish_project "VolMon.GUI"    "gui"
publish_project "VolMon.CLI"    "cli"

# ── Install binaries ──────────────────────────────────────────────────
bold "Installing binaries to $INSTALL_BIN..."
mkdir -p "$INSTALL_BIN"

# Create symlinks to the single-file executables
ln -sf "$INSTALL_LIB/daemon/VolMon.Daemon" "$INSTALL_BIN/volmon-daemon"
ln -sf "$INSTALL_LIB/gui/VolMon.GUI"       "$INSTALL_BIN/volmon-gui"
ln -sf "$INSTALL_LIB/cli/VolMon.CLI"       "$INSTALL_BIN/volmon"

# ── systemd service ──────────────────────────────────────────────────
bold "Installing systemd user service..."
mkdir -p "$SYSTEMD_DIR"
cp "$SCRIPT_DIR/volmon.service" "$SYSTEMD_DIR/volmon.service"
systemctl --user daemon-reload

# ── Desktop entry ─────────────────────────────────────────────────────
bold "Installing desktop entry..."
mkdir -p "$DESKTOP_DIR"
cp "$SCRIPT_DIR/volmon.desktop" "$DESKTOP_DIR/volmon.desktop"

# ── Icon ──────────────────────────────────────────────────────────────
bold "Installing icon..."
mkdir -p "$ICON_DIR/256x256/apps"
cp "$PROJECT_ROOT/assets/VolMonLogo-256.png" "$ICON_DIR/256x256/apps/volmon.png"
gtk-update-icon-cache -f -t "$ICON_DIR" 2>/dev/null || true

# ── Enable service ────────────────────────────────────────────────────
bold "Enabling and starting the daemon..."
systemctl --user enable volmon
systemctl --user restart volmon

# ── Done ──────────────────────────────────────────────────────────────
echo ""
green "VolMon installed successfully!"
echo ""
echo "  Daemon:   systemctl --user status volmon"
echo "  GUI:      volmon-gui"
echo "  CLI:      volmon --help"
echo ""

# Check PATH
if [[ ":$PATH:" != *":$INSTALL_BIN:"* ]]; then
    echo "  Note: $INSTALL_BIN is not in your PATH."
    echo "  Add it with:  export PATH=\"\$HOME/.local/bin:\$PATH\""
    echo ""
fi
