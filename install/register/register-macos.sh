#!/usr/bin/env bash
#
# VolMon registration script for macOS
#
# Registers the daemon and GUI as launchd user agents so they start
# automatically on login. Optionally registers the hardware daemon and
# hardware GUI when --include-hardware is passed. Wraps each GUI in a
# minimal .app bundle so macOS displays correct icons.
#
# Run from inside the publish/osx-x64/ folder (or wherever the
# VolMon binaries are located).
#
# Usage:
#   ./register.sh                              # core only
#   ./register.sh --include-hardware           # core + hardware
#   ./register.sh --unregister                 # unload and remove everything
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

DAEMON_BIN="$SCRIPT_DIR/daemon/VolMon.Daemon"
GUI_BIN="$SCRIPT_DIR/gui/VolMon.GUI"
HARDWARE_BIN="$SCRIPT_DIR/hardware/VolMon.Hardware"
HARDWARE_GUI_BIN="$SCRIPT_DIR/hardware-gui/VolMon.HardwareGUI"
ICON_SRC="$SCRIPT_DIR/volmon.png"

LAUNCH_AGENTS="$HOME/Library/LaunchAgents"
DAEMON_LABEL="com.volmon.daemon"
GUI_LABEL="com.volmon.gui"
HARDWARE_LABEL="com.volmon.hardware"
HARDWARE_GUI_LABEL="com.volmon.hardware-gui"
DAEMON_PLIST="$LAUNCH_AGENTS/$DAEMON_LABEL.plist"
GUI_PLIST="$LAUNCH_AGENTS/$GUI_LABEL.plist"
HARDWARE_PLIST="$LAUNCH_AGENTS/$HARDWARE_LABEL.plist"
HARDWARE_GUI_PLIST="$LAUNCH_AGENTS/$HARDWARE_GUI_LABEL.plist"

LOG_DIR="$HOME/Library/Logs/VolMon"
APP_BUNDLE="$HOME/Applications/VolMon.app"
HARDWARE_APP_BUNDLE="$HOME/Applications/VolMon Hardware Manager.app"

# ── Parse flags ───────────────────────────────────────────────────────
INCLUDE_HARDWARE=false
UNREGISTER=false
for arg in "$@"; do
    case "$arg" in
        --include-hardware) INCLUDE_HARDWARE=true ;;
        --unregister)       UNREGISTER=true ;;
        *)
            echo "Unknown option: $arg"
            echo "Usage: $0 [--include-hardware] [--unregister]"
            exit 1
            ;;
    esac
done

# ── Colors ────────────────────────────────────────────────────────────
red()   { printf '\033[0;31m%s\033[0m\n' "$*"; }
green() { printf '\033[0;32m%s\033[0m\n' "$*"; }
bold()  { printf '\033[1m%s\033[0m\n' "$*"; }

# ── Unregister ────────────────────────────────────────────────────────
unregister() {
    bold "Unregistering VolMon..."

    for label in "$DAEMON_LABEL" "$GUI_LABEL" "$HARDWARE_LABEL" "$HARDWARE_GUI_LABEL"; do
        if launchctl list "$label" &>/dev/null; then
            launchctl bootout "gui/$(id -u)/$label" 2>/dev/null || \
                launchctl unload "$LAUNCH_AGENTS/$label.plist" 2>/dev/null || true
        fi
        rm -f "$LAUNCH_AGENTS/$label.plist"
    done

    # Remove .app bundles
    rm -rf "$APP_BUNDLE"
    rm -rf "$HARDWARE_APP_BUNDLE"

    green "VolMon unregistered."
    exit 0
}

if $UNREGISTER; then
    unregister
fi

# ── Validate binaries ────────────────────────────────────────────────
REQUIRED_BINS=("$DAEMON_BIN" "$GUI_BIN")
if $INCLUDE_HARDWARE; then
    REQUIRED_BINS+=("$HARDWARE_BIN" "$HARDWARE_GUI_BIN")
fi

for bin in "${REQUIRED_BINS[@]}"; do
    if [[ ! -f "$bin" ]]; then
        red "Error: $(basename "$bin") not found in $SCRIPT_DIR"
        red "Run this script from the publish/osx-x64/ folder."
        exit 1
    fi
    chmod +x "$bin"
done

# ── Directories ───────────────────────────────────────────────────────
mkdir -p "$LAUNCH_AGENTS"
mkdir -p "$LOG_DIR"

# ── Helper: create .app bundle ───────────────────────────────────────
create_app_bundle() {
    local app_path="$1"
    local display_name="$2"
    local bundle_id="$3"
    local exec_bin="$4"
    local exec_name="$5"

    local contents="$app_path/Contents"
    local macos_dir="$contents/MacOS"
    local resources="$contents/Resources"

    rm -rf "$app_path"
    mkdir -p "$macos_dir" "$resources"

    # Launcher script that execs the real binary
    cat > "$macos_dir/$exec_name" <<LAUNCHER
#!/usr/bin/env bash
exec "$exec_bin" "\$@"
LAUNCHER
    chmod +x "$macos_dir/$exec_name"

    # Info.plist
    cat > "$contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>$display_name</string>
    <key>CFBundleDisplayName</key>
    <string>$display_name</string>
    <key>CFBundleIdentifier</key>
    <string>$bundle_id</string>
    <key>CFBundleExecutable</key>
    <string>$exec_name</string>
    <key>CFBundleIconFile</key>
    <string>volmon</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleVersion</key>
    <string>1.0</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>10.15</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
PLIST

    # Convert PNG to icns if tools are available
    if [[ -f "$ICON_SRC" ]]; then
        if command -v sips &>/dev/null && command -v iconutil &>/dev/null; then
            local iconset
            iconset=$(mktemp -d)/volmon.iconset
            mkdir -p "$iconset"
            for sz in 16 32 64 128 256; do
                sips -z $sz $sz "$ICON_SRC" --out "$iconset/icon_${sz}x${sz}.png" &>/dev/null
            done
            cp "$iconset/icon_32x32.png"   "$iconset/icon_16x16@2x.png"
            cp "$iconset/icon_64x64.png"   "$iconset/icon_32x32@2x.png"
            cp "$iconset/icon_256x256.png" "$iconset/icon_128x128@2x.png"
            rm -f "$iconset/icon_64x64.png"
            iconutil -c icns -o "$resources/volmon.icns" "$iconset" 2>/dev/null || \
                cp "$ICON_SRC" "$resources/volmon.png"
            rm -rf "$(dirname "$iconset")"
        else
            cp "$ICON_SRC" "$resources/volmon.png"
        fi
    fi
}

# ── Helper: install launchd agent ────────────────────────────────────
install_agent() {
    local label="$1"
    local plist_path="$2"

    launchctl bootout "gui/$(id -u)/$label" 2>/dev/null || true
    launchctl bootstrap "gui/$(id -u)" "$plist_path" 2>/dev/null || \
        launchctl load -w "$plist_path"
}

# ── GUI: .app bundles ────────────────────────────────────────────────
bold "Creating VolMon.app bundle..."
create_app_bundle "$APP_BUNDLE" "VolMon" "com.volmon.gui" "$GUI_BIN" "VolMon"
green "  VolMon.app created at $APP_BUNDLE"

if $INCLUDE_HARDWARE; then
    bold "Creating VolMon Hardware Manager.app bundle..."
    create_app_bundle "$HARDWARE_APP_BUNDLE" "VolMon Hardware Manager" "com.volmon.hardware-gui" "$HARDWARE_GUI_BIN" "VolMon Hardware Manager"
    green "  VolMon Hardware Manager.app created at $HARDWARE_APP_BUNDLE"
fi

# ── Daemon: launchd user agent ───────────────────────────────────────
bold "Installing daemon launch agent..."

cat > "$DAEMON_PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>$DAEMON_LABEL</string>

    <key>ProgramArguments</key>
    <array>
        <string>$DAEMON_BIN</string>
    </array>

    <key>RunAtLoad</key>
    <true/>

    <key>KeepAlive</key>
    <dict>
        <key>SuccessfulExit</key>
        <false/>
    </dict>

    <key>StandardOutPath</key>
    <string>$LOG_DIR/daemon-stdout.log</string>
    <key>StandardErrorPath</key>
    <string>$LOG_DIR/daemon-stderr.log</string>

    <key>ProcessType</key>
    <string>Background</string>
</dict>
</plist>
EOF

install_agent "$DAEMON_LABEL" "$DAEMON_PLIST"
green "  Daemon agent installed and started."

# ── Hardware Daemon: launchd user agent (optional) ───────────────────
if $INCLUDE_HARDWARE; then
    bold "Installing hardware daemon launch agent..."

    cat > "$HARDWARE_PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>$HARDWARE_LABEL</string>

    <key>ProgramArguments</key>
    <array>
        <string>$HARDWARE_BIN</string>
    </array>

    <key>RunAtLoad</key>
    <true/>

    <key>KeepAlive</key>
    <dict>
        <key>SuccessfulExit</key>
        <false/>
    </dict>

    <key>StandardOutPath</key>
    <string>$LOG_DIR/hardware-stdout.log</string>
    <key>StandardErrorPath</key>
    <string>$LOG_DIR/hardware-stderr.log</string>

    <key>ProcessType</key>
    <string>Background</string>
</dict>
</plist>
EOF

    install_agent "$HARDWARE_LABEL" "$HARDWARE_PLIST"
    green "  Hardware daemon agent installed and started."
fi

# ── GUI: launchd user agent ──────────────────────────────────────────
bold "Installing GUI launch agent..."

cat > "$GUI_PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>$GUI_LABEL</string>

    <key>ProgramArguments</key>
    <array>
        <string>open</string>
        <string>-a</string>
        <string>$APP_BUNDLE</string>
    </array>

    <key>RunAtLoad</key>
    <true/>

    <key>StandardOutPath</key>
    <string>$LOG_DIR/gui-stdout.log</string>
    <key>StandardErrorPath</key>
    <string>$LOG_DIR/gui-stderr.log</string>

    <key>ProcessType</key>
    <string>Interactive</string>
</dict>
</plist>
EOF

install_agent "$GUI_LABEL" "$GUI_PLIST"
green "  GUI agent installed and started."

# ── Done ──────────────────────────────────────────────────────────────
echo ""
green "VolMon registered successfully!"
echo ""
echo "  App bundle:     $APP_BUNDLE"
if $INCLUDE_HARDWARE; then
    echo "  Hardware app:   $HARDWARE_APP_BUNDLE"
fi
echo "  Daemon logs:    $LOG_DIR/daemon-*.log"
if $INCLUDE_HARDWARE; then
    echo "  Hardware logs:  $LOG_DIR/hardware-*.log"
fi
echo "  GUI logs:       $LOG_DIR/gui-*.log"
echo "  Unregister:     $0 --unregister"
if ! $INCLUDE_HARDWARE; then
    echo ""
    echo "  Hardware was not registered. To include it, re-run with --include-hardware"
fi
echo ""
