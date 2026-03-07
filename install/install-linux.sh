#!/usr/bin/env bash
# VolMon — Linux installer
#
# Usage:
#   ./install-linux.sh                    # interactive install
#   ./install-linux.sh --include-hardware # install with hardware support (no prompt)
#   ./install-linux.sh --no-hardware      # install without hardware support (no prompt)
#   ./install-linux.sh --uninstall        # remove everything
#
# What this script does:
#   1. Removes any previous VolMon installation cleanly
#   2. Copies VolMon binaries to ~/.local/lib/volmon/
#   3. Writes wrapper scripts to ~/.local/bin/
#   4. Installs a systemd user service for the daemon
#   5. Writes .desktop entries and registers them
#   6. Installs the application icon
#
# Requirements: bash 4+, systemd (user session), PulseAudio or PipeWire
# For hardware support: libusb-1.0

set -euo pipefail

LIB_DIR="${HOME}/.local/lib/volmon"
BIN_DIR="${HOME}/.local/bin"
SYSTEMD_DIR="${HOME}/.config/systemd/user"
DESKTOP_DIR="${HOME}/.local/share/applications"
AUTOSTART_DIR="${HOME}/.config/autostart"
ICON_DIR="${HOME}/.local/share/icons/hicolor"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="${SCRIPT_DIR}/app"

# ── Colors ────────────────────────────────────────────────────────────
red()   { printf '\033[0;31m%s\033[0m\n' "$*"; }
green() { printf '\033[0;32m%s\033[0m\n' "$*"; }
bold()  { printf '\033[1m%s\033[0m\n' "$*"; }

# ── Parse flags ───────────────────────────────────────────────────────
INCLUDE_HARDWARE=""   # empty = not decided yet; "true"/"false" = headless
UNINSTALL=false
for arg in "$@"; do
    case "$arg" in
        --include-hardware) INCLUDE_HARDWARE=true ;;
        --no-hardware)      INCLUDE_HARDWARE=false ;;
        --uninstall)        UNINSTALL=true ;;
        *)
            echo "Unknown option: $arg"
            echo "Usage: $0 [--include-hardware | --no-hardware] [--uninstall]"
            exit 1
            ;;
    esac
done

# ── Shared cleanup logic (used by both --uninstall and upgrade) ───────
remove_existing() {
    # Stop and disable services
    for svc in volmon volmon-hardware; do
        if systemctl --user is-active "$svc" &>/dev/null; then
            systemctl --user stop "$svc"
        fi
        if systemctl --user is-enabled "$svc" &>/dev/null; then
            systemctl --user disable "$svc"
        fi
        rm -f "${SYSTEMD_DIR}/${svc}.service"
    done
    systemctl --user daemon-reload 2>/dev/null || true

    # Wrappers
    rm -f "${BIN_DIR}/volmon-daemon"
    rm -f "${BIN_DIR}/volmon-gui"
    rm -f "${BIN_DIR}/volmon-hardware"
    rm -f "${BIN_DIR}/volmon-hardware-gui"

    # Binaries
    rm -rf "${LIB_DIR}"

    # Desktop / autostart
    rm -f "${AUTOSTART_DIR}/volmon-gui.desktop"
    rm -f "${DESKTOP_DIR}/volmon.desktop"
    rm -f "${DESKTOP_DIR}/volmon-hardware-gui.desktop"

    # Icon
    rm -f "${ICON_DIR}/256x256/apps/volmon.png"
    command -v gtk-update-icon-cache &>/dev/null && \
        gtk-update-icon-cache -q -t -f "${ICON_DIR}" 2>/dev/null || true
    command -v update-desktop-database &>/dev/null && \
        update-desktop-database "${DESKTOP_DIR}" 2>/dev/null || true
}

# ── Uninstall ─────────────────────────────────────────────────────────
if $UNINSTALL; then
    bold "Uninstalling VolMon..."
    remove_existing
    green "VolMon uninstalled."
    exit 0
fi

# ── Validate app directory ────────────────────────────────────────────
if [[ ! -d "${APP_DIR}/daemon" || ! -d "${APP_DIR}/gui" ]]; then
    red "Error: app/ directory not found or incomplete next to this script."
    red "Make sure you are running install-linux.sh from inside the extracted zip."
    exit 1
fi

# ── Check prerequisites ───────────────────────────────────────────────
if ! command -v systemctl &>/dev/null; then
    red "Error: systemd is required but systemctl was not found."
    exit 1
fi

# ── Hardware prompt (interactive only) ───────────────────────────────
if [[ -z "${INCLUDE_HARDWARE}" ]]; then
    if [[ -d "${APP_DIR}/hardware" && -d "${APP_DIR}/hardware-gui" ]]; then
        echo ""
        read -r -p "Install hardware daemon and GUI (for USB controllers like Beacn Mix)? [y/N] " _hw
        [[ "${_hw}" =~ ^[Yy]$ ]] && INCLUDE_HARDWARE=true || INCLUDE_HARDWARE=false
        echo ""
    else
        INCLUDE_HARDWARE=false
    fi
fi

if [[ "${INCLUDE_HARDWARE}" == true ]]; then
    if [[ ! -d "${APP_DIR}/hardware" || ! -d "${APP_DIR}/hardware-gui" ]]; then
        red "Error: hardware binaries not found in app/ (expected app/hardware/ and app/hardware-gui/)."
        exit 1
    fi
    if ! ldconfig -p 2>/dev/null | grep -q libusb; then
        echo "WARNING: libusb-1.0 does not appear to be installed."
        echo "Install it first, e.g.:"
        echo "  Ubuntu/Debian: sudo apt install libusb-1.0-0"
        echo "  Fedora:        sudo dnf install libusb1"
        echo "  Arch:          sudo pacman -S libusb"
        echo ""
    fi
fi

# ── Remove previous installation ──────────────────────────────────────
if [[ -d "${LIB_DIR}" || -f "${BIN_DIR}/volmon-daemon" ]]; then
    bold "Removing previous installation..."
    remove_existing
    green "  Previous installation removed."
fi

# ── Install binaries ──────────────────────────────────────────────────
bold "Installing VolMon to ${LIB_DIR}..."
mkdir -p "${LIB_DIR}" "${BIN_DIR}"

copy_component() {
    local component="$1"
    rsync -a "${APP_DIR}/${component}/" "${LIB_DIR}/${component}/" 2>/dev/null || \
        (mkdir -p "${LIB_DIR}/${component}" && \
         cd "${APP_DIR}/${component}" && find . -print0 | cpio -0pdm "${LIB_DIR}/${component}")
}

copy_component daemon
copy_component gui
chmod +x "${LIB_DIR}/daemon/VolMon.Daemon"
chmod +x "${LIB_DIR}/gui/VolMon.GUI"

if [[ "${INCLUDE_HARDWARE}" == true ]]; then
    copy_component hardware
    copy_component hardware-gui
    chmod +x "${LIB_DIR}/hardware/VolMon.Hardware"
    chmod +x "${LIB_DIR}/hardware-gui/VolMon.HardwareGUI"
fi

green "  Binaries installed."

# ── Wrapper scripts in PATH ───────────────────────────────────────────
bold "Writing launchers to ${BIN_DIR}..."

cat > "${BIN_DIR}/volmon-daemon" <<WRAPPER
#!/usr/bin/env bash
exec "${LIB_DIR}/daemon/VolMon.Daemon" "\$@"
WRAPPER
chmod +x "${BIN_DIR}/volmon-daemon"

cat > "${BIN_DIR}/volmon-gui" <<WRAPPER
#!/usr/bin/env bash
exec "${LIB_DIR}/gui/VolMon.GUI" "\$@"
WRAPPER
chmod +x "${BIN_DIR}/volmon-gui"

if [[ "${INCLUDE_HARDWARE}" == true ]]; then
    cat > "${BIN_DIR}/volmon-hardware" <<WRAPPER
#!/usr/bin/env bash
exec "${LIB_DIR}/hardware/VolMon.Hardware" "\$@"
WRAPPER
    chmod +x "${BIN_DIR}/volmon-hardware"

    cat > "${BIN_DIR}/volmon-hardware-gui" <<WRAPPER
#!/usr/bin/env bash
exec "${LIB_DIR}/hardware-gui/VolMon.HardwareGUI" "\$@"
WRAPPER
    chmod +x "${BIN_DIR}/volmon-hardware-gui"
fi

green "  Launchers written."

# ── systemd user service ──────────────────────────────────────────────
bold "Installing systemd user service..."
mkdir -p "${SYSTEMD_DIR}"

cat > "${SYSTEMD_DIR}/volmon.service" <<SERVICE
[Unit]
Description=VolMon Volume Monitoring and Control Daemon
After=pulseaudio.service pipewire.service

[Service]
Type=simple
ExecStart=${BIN_DIR}/volmon-daemon
Restart=on-failure
RestartSec=5

[Install]
WantedBy=default.target
SERVICE

systemctl --user daemon-reload
systemctl --user enable volmon
systemctl --user restart volmon
green "  Daemon service installed and started."

if [[ "${INCLUDE_HARDWARE}" == true ]]; then
    cat > "${SYSTEMD_DIR}/volmon-hardware.service" <<SERVICE
[Unit]
Description=VolMon Hardware Daemon
After=volmon.service

[Service]
Type=simple
ExecStart=${BIN_DIR}/volmon-hardware
Restart=on-failure
RestartSec=5

[Install]
WantedBy=default.target
SERVICE

    systemctl --user daemon-reload
    systemctl --user enable volmon-hardware
    systemctl --user restart volmon-hardware
    green "  Hardware daemon service installed and started."
fi

# ── Icon ──────────────────────────────────────────────────────────────
bold "Installing icon..."
ICON_SRC="${APP_DIR}/volmon.png"
if [[ -f "${ICON_SRC}" ]]; then
    mkdir -p "${ICON_DIR}/256x256/apps"
    cp "${ICON_SRC}" "${ICON_DIR}/256x256/apps/volmon.png"
    command -v gtk-update-icon-cache &>/dev/null && \
        gtk-update-icon-cache -q -t -f "${ICON_DIR}" 2>/dev/null || true
    green "  Icon installed."
else
    echo "  Warning: volmon.png not found in app/, skipping icon."
fi

# ── Desktop entries ───────────────────────────────────────────────────
bold "Installing desktop entries..."
mkdir -p "${DESKTOP_DIR}" "${AUTOSTART_DIR}"

cat > "${DESKTOP_DIR}/volmon.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=VolMon
Comment=Volume Monitoring and Control
Exec=${BIN_DIR}/volmon-gui
Icon=volmon
Terminal=false
Categories=AudioVideo;Audio;Mixer;Settings;
Keywords=volume;audio;mixer;sound;group;
StartupNotify=true
DESKTOP

cp "${DESKTOP_DIR}/volmon.desktop" "${AUTOSTART_DIR}/volmon-gui.desktop"

if [[ "${INCLUDE_HARDWARE}" == true ]]; then
    cat > "${DESKTOP_DIR}/volmon-hardware-gui.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=VolMon Hardware Manager
Comment=Configure VolMon hardware devices
Exec=${BIN_DIR}/volmon-hardware-gui
Icon=volmon
Terminal=false
Categories=AudioVideo;Audio;Mixer;Settings;HardwareSettings;
Keywords=volume;audio;mixer;hardware;beacn;
StartupNotify=true
DESKTOP
fi

command -v update-desktop-database &>/dev/null && \
    update-desktop-database "${DESKTOP_DIR}" 2>/dev/null || true
if command -v kbuildsycoca6 &>/dev/null; then
    kbuildsycoca6 2>/dev/null || true
elif command -v kbuildsycoca5 &>/dev/null; then
    kbuildsycoca5 2>/dev/null || true
fi

green "  Desktop entries installed."

# ── Done ──────────────────────────────────────────────────────────────
echo ""
green "VolMon installed successfully!"
echo ""
echo "  Daemon status:  systemctl --user status volmon"
if [[ "${INCLUDE_HARDWARE}" == true ]]; then
    echo "  Hardware:       systemctl --user status volmon-hardware"
fi
echo "  Start GUI:      volmon-gui"
if [[ "${INCLUDE_HARDWARE}" == true ]]; then
    echo "  Hardware GUI:   volmon-hardware-gui"
fi
echo "  Uninstall:      $0 --uninstall"
echo ""

if [[ ":${PATH}:" != *":${BIN_DIR}:"* ]]; then
    echo "  Note: ${BIN_DIR} is not in your PATH."
    echo "  Add it with:  export PATH=\"\${HOME}/.local/bin:\${PATH}\""
    echo ""
fi
