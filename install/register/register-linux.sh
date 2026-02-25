#!/usr/bin/env bash
#
# VolMon registration script for Linux
#
# Registers the daemon and hardware daemon as systemd user services,
# and the GUI and hardware GUI as XDG autostart/desktop applications.
# All start automatically on login.
#
# Run from inside the publish/linux-x64/ folder (or wherever the
# VolMon binaries are located).
#
# Usage:
#   ./register.sh              # register (install + enable + start)
#   ./register.sh --unregister # stop, disable, and remove everything
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

DAEMON_BIN="$SCRIPT_DIR/VolMon.Daemon"
GUI_BIN="$SCRIPT_DIR/VolMon.GUI"
HARDWARE_BIN="$SCRIPT_DIR/VolMon.Hardware"
HARDWARE_GUI_BIN="$SCRIPT_DIR/VolMon.HardwareGUI"
ICON_SRC="$SCRIPT_DIR/volmon.png"

SYSTEMD_DIR="$HOME/.config/systemd/user"
AUTOSTART_DIR="$HOME/.config/autostart"
APPS_DIR="$HOME/.local/share/applications"
ICON_DIR="$HOME/.local/share/icons/hicolor"

SERVICE_NAME="volmon"
HARDWARE_SERVICE_NAME="volmon-hardware"
SERVICE_FILE="$SYSTEMD_DIR/$SERVICE_NAME.service"
HARDWARE_SERVICE_FILE="$SYSTEMD_DIR/$HARDWARE_SERVICE_NAME.service"
AUTOSTART_FILE="$AUTOSTART_DIR/volmon-gui.desktop"
DESKTOP_FILE="$APPS_DIR/volmon.desktop"
HARDWARE_DESKTOP_FILE="$APPS_DIR/volmon-hardware-gui.desktop"
ICON_FILE="$ICON_DIR/256x256/apps/volmon.png"

# ── Colors ────────────────────────────────────────────────────────────
red()   { printf '\033[0;31m%s\033[0m\n' "$*"; }
green() { printf '\033[0;32m%s\033[0m\n' "$*"; }
bold()  { printf '\033[1m%s\033[0m\n' "$*"; }

# ── Unregister ────────────────────────────────────────────────────────
unregister() {
    bold "Unregistering VolMon..."

    # Daemons
    for svc in "$SERVICE_NAME" "$HARDWARE_SERVICE_NAME"; do
        if systemctl --user is-active "$svc" &>/dev/null; then
            systemctl --user stop "$svc"
        fi
        if systemctl --user is-enabled "$svc" &>/dev/null; then
            systemctl --user disable "$svc"
        fi
    done
    rm -f "$SERVICE_FILE"
    rm -f "$HARDWARE_SERVICE_FILE"
    systemctl --user daemon-reload 2>/dev/null || true

    # GUI autostart + desktop entries
    rm -f "$AUTOSTART_FILE"
    rm -f "$DESKTOP_FILE"
    rm -f "$HARDWARE_DESKTOP_FILE"

    # Icon
    rm -f "$ICON_FILE"
    gtk-update-icon-cache -f -t "$ICON_DIR" 2>/dev/null || true

    green "VolMon unregistered."
    exit 0
}

if [[ "${1:-}" == "--unregister" ]]; then
    unregister
fi

# ── Validate binaries ────────────────────────────────────────────────
for bin in "$DAEMON_BIN" "$GUI_BIN" "$HARDWARE_BIN" "$HARDWARE_GUI_BIN"; do
    if [[ ! -f "$bin" ]]; then
        red "Error: $(basename "$bin") not found in $SCRIPT_DIR"
        red "Run this script from the publish/linux-x64/ folder."
        exit 1
    fi
done

# ── Daemon: systemd user service ─────────────────────────────────────
bold "Installing systemd user service (daemon)..."
mkdir -p "$SYSTEMD_DIR"

cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=VolMon Audio Group Volume Daemon
After=default.target pulseaudio.service pipewire.service

[Service]
Type=simple
ExecStart=$DAEMON_BIN
Restart=on-failure
RestartSec=5

[Install]
WantedBy=default.target
EOF

systemctl --user daemon-reload
systemctl --user enable "$SERVICE_NAME"
systemctl --user restart "$SERVICE_NAME"
green "  Daemon service installed and started."

# ── Hardware Daemon: systemd user service ────────────────────────────
bold "Installing systemd user service (hardware daemon)..."

cat > "$HARDWARE_SERVICE_FILE" <<EOF
[Unit]
Description=VolMon Hardware Daemon
After=volmon.service
Wants=volmon.service

[Service]
Type=simple
ExecStart=$HARDWARE_BIN
Restart=on-failure
RestartSec=5

[Install]
WantedBy=default.target
EOF

systemctl --user daemon-reload
systemctl --user enable "$HARDWARE_SERVICE_NAME"
systemctl --user restart "$HARDWARE_SERVICE_NAME"
green "  Hardware daemon service installed and started."

# ── GUI: XDG autostart ───────────────────────────────────────────────
bold "Installing GUI autostart entry..."
mkdir -p "$AUTOSTART_DIR"

cat > "$AUTOSTART_FILE" <<EOF
[Desktop Entry]
Type=Application
Name=VolMon
Comment=Volume Monitoring and Control
Exec=$GUI_BIN
Icon=volmon
Terminal=false
Categories=AudioVideo;Audio;Mixer;Settings;
X-GNOME-Autostart-enabled=true
EOF

green "  GUI will start automatically on next login."

# ── GUI: desktop entry (application launcher) ────────────────────────
bold "Installing desktop entries..."
mkdir -p "$APPS_DIR"

cat > "$DESKTOP_FILE" <<EOF
[Desktop Entry]
Type=Application
Name=VolMon
Comment=Volume Monitoring and Control
Exec=$GUI_BIN
Icon=volmon
Terminal=false
Categories=AudioVideo;Audio;Mixer;Settings;
Keywords=volume;audio;mixer;sound;group;
StartupNotify=true
EOF

cat > "$HARDWARE_DESKTOP_FILE" <<EOF
[Desktop Entry]
Type=Application
Name=VolMon Hardware Manager
Comment=Configure VolMon hardware devices
Exec=$HARDWARE_GUI_BIN
Icon=volmon
Terminal=false
Categories=AudioVideo;Audio;Mixer;Settings;HardwareSettings;
Keywords=volume;audio;mixer;hardware;beacn;
StartupNotify=true
EOF

green "  Desktop entries installed (visible in application launcher)."

# ── Icon: XDG hicolor theme ──────────────────────────────────────────
bold "Installing application icon..."
if [[ -f "$ICON_SRC" ]]; then
    mkdir -p "$ICON_DIR/256x256/apps"
    cp "$ICON_SRC" "$ICON_FILE"
    gtk-update-icon-cache -f -t "$ICON_DIR" 2>/dev/null || true
    green "  Icon installed."
else
    echo "  Warning: volmon.png not found, skipping icon install."
fi

# ── Done ──────────────────────────────────────────────────────────────
echo ""
green "VolMon registered successfully!"
echo ""
echo "  Daemon status:    systemctl --user status $SERVICE_NAME"
echo "  Hardware status:  systemctl --user status $HARDWARE_SERVICE_NAME"
echo "  Start GUI now:    $GUI_BIN"
echo "  Hardware GUI:     $HARDWARE_GUI_BIN"
echo "  Unregister:       $0 --unregister"
echo ""
