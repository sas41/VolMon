#!/usr/bin/env bash
#
# VolMon installer for Linux
#
# Publishes self-contained binaries for the daemon, GUI, CLI,
# hardware daemon, and hardware GUI, installs them to ~/.local/bin,
# sets up systemd user services, desktop entries, and icons.
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

    # Stop and disable services
    for svc in volmon volmon-hardware; do
        if systemctl --user is-active "$svc" &>/dev/null; then
            systemctl --user stop "$svc"
        fi
        if systemctl --user is-enabled "$svc" &>/dev/null; then
            systemctl --user disable "$svc"
        fi
        rm -f "$SYSTEMD_DIR/$svc.service"
    done

    rm -f "$INSTALL_BIN/volmon-daemon"
    rm -f "$INSTALL_BIN/volmon-gui"
    rm -f "$INSTALL_BIN/volmon-hardware"
    rm -f "$INSTALL_BIN/volmon-hardware-gui"
    rm -f "$INSTALL_BIN/volmon"
    rm -rf "$INSTALL_LIB"
    rm -f "$DESKTOP_DIR/volmon.desktop"
    rm -f "$DESKTOP_DIR/volmon-hardware-gui.desktop"
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

publish_project "VolMon.Daemon"      "daemon"
publish_project "VolMon.GUI"         "gui"
publish_project "VolMon.CLI"         "cli"
publish_project "VolMon.Hardware"    "hardware"
publish_project "VolMon.HardwareGUI" "hardware-gui"

# ── Install binaries ──────────────────────────────────────────────────
bold "Installing binaries to $INSTALL_BIN..."
mkdir -p "$INSTALL_BIN"

# Create symlinks to the single-file executables
ln -sf "$INSTALL_LIB/daemon/VolMon.Daemon"           "$INSTALL_BIN/volmon-daemon"
ln -sf "$INSTALL_LIB/gui/VolMon.GUI"                 "$INSTALL_BIN/volmon-gui"
ln -sf "$INSTALL_LIB/cli/VolMon.CLI"                 "$INSTALL_BIN/volmon"
ln -sf "$INSTALL_LIB/hardware/VolMon.Hardware"        "$INSTALL_BIN/volmon-hardware"
ln -sf "$INSTALL_LIB/hardware-gui/VolMon.HardwareGUI" "$INSTALL_BIN/volmon-hardware-gui"

# ── systemd services ─────────────────────────────────────────────────
bold "Installing systemd user services..."
mkdir -p "$SYSTEMD_DIR"
cp "$SCRIPT_DIR/volmon.service"          "$SYSTEMD_DIR/volmon.service"
cp "$SCRIPT_DIR/volmon-hardware.service" "$SYSTEMD_DIR/volmon-hardware.service"
systemctl --user daemon-reload

# ── Desktop entries ───────────────────────────────────────────────────
bold "Installing desktop entries..."
mkdir -p "$DESKTOP_DIR"
cp "$SCRIPT_DIR/volmon.desktop"              "$DESKTOP_DIR/volmon.desktop"
cp "$SCRIPT_DIR/volmon-hardware-gui.desktop" "$DESKTOP_DIR/volmon-hardware-gui.desktop"

# ── Icon ──────────────────────────────────────────────────────────────
bold "Installing icon..."
mkdir -p "$ICON_DIR/256x256/apps"
cp "$PROJECT_ROOT/assets/VolMonLogo-256.png" "$ICON_DIR/256x256/apps/volmon.png"
gtk-update-icon-cache -f -t "$ICON_DIR" 2>/dev/null || true

# ── Enable services ──────────────────────────────────────────────────
bold "Enabling and starting services..."
systemctl --user enable volmon
systemctl --user restart volmon
systemctl --user enable volmon-hardware
systemctl --user restart volmon-hardware

# ── Done ──────────────────────────────────────────────────────────────
echo ""
green "VolMon installed successfully!"
echo ""
echo "  Daemon:         systemctl --user status volmon"
echo "  Hardware:       systemctl --user status volmon-hardware"
echo "  GUI:            volmon-gui"
echo "  Hardware GUI:   volmon-hardware-gui"
echo "  CLI:            volmon --help"
echo ""

# Check PATH
if [[ ":$PATH:" != *":$INSTALL_BIN:"* ]]; then
    echo "  Note: $INSTALL_BIN is not in your PATH."
    echo "  Add it with:  export PATH=\"\$HOME/.local/bin:\$PATH\""
    echo ""
fi
