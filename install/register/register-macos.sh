#!/usr/bin/env bash
#
# VolMon registration script for macOS
#
# Registers the daemon and GUI as launchd user agents so they start
# automatically on login. Wraps the GUI in a minimal .app bundle so
# macOS displays the correct icon in the Dock and app switcher.
#
# Run from inside the publish/osx-x64/ folder (or wherever the
# VolMon.Daemon and VolMon.GUI binaries are located).
#
# Usage:
#   ./register.sh              # register (install + load + start)
#   ./register.sh --unregister # unload and remove everything
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

DAEMON_BIN="$SCRIPT_DIR/VolMon.Daemon"
GUI_BIN="$SCRIPT_DIR/VolMon.GUI"
ICON_SRC="$SCRIPT_DIR/volmon.png"

LAUNCH_AGENTS="$HOME/Library/LaunchAgents"
DAEMON_LABEL="com.volmon.daemon"
GUI_LABEL="com.volmon.gui"
DAEMON_PLIST="$LAUNCH_AGENTS/$DAEMON_LABEL.plist"
GUI_PLIST="$LAUNCH_AGENTS/$GUI_LABEL.plist"

LOG_DIR="$HOME/Library/Logs/VolMon"
APP_BUNDLE="$HOME/Applications/VolMon.app"

# ── Colors ────────────────────────────────────────────────────────────
red()   { printf '\033[0;31m%s\033[0m\n' "$*"; }
green() { printf '\033[0;32m%s\033[0m\n' "$*"; }
bold()  { printf '\033[1m%s\033[0m\n' "$*"; }

# ── Unregister ────────────────────────────────────────────────────────
unregister() {
    bold "Unregistering VolMon..."

    for label in "$DAEMON_LABEL" "$GUI_LABEL"; do
        if launchctl list "$label" &>/dev/null; then
            launchctl bootout "gui/$(id -u)/$label" 2>/dev/null || \
                launchctl unload "$LAUNCH_AGENTS/$label.plist" 2>/dev/null || true
        fi
        rm -f "$LAUNCH_AGENTS/$label.plist"
    done

    # Remove .app bundle
    if [[ -d "$APP_BUNDLE" ]]; then
        rm -rf "$APP_BUNDLE"
    fi

    green "VolMon unregistered."
    exit 0
}

if [[ "${1:-}" == "--unregister" ]]; then
    unregister
fi

# ── Validate binaries ────────────────────────────────────────────────
for bin in "$DAEMON_BIN" "$GUI_BIN"; do
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

# ── GUI: .app bundle ─────────────────────────────────────────────────
# Wrap the GUI binary in a minimal .app bundle so macOS shows the
# correct icon in the Dock, app switcher, and Spotlight.
bold "Creating VolMon.app bundle..."

CONTENTS="$APP_BUNDLE/Contents"
MACOS_DIR="$CONTENTS/MacOS"
RESOURCES="$CONTENTS/Resources"

rm -rf "$APP_BUNDLE"
mkdir -p "$MACOS_DIR" "$RESOURCES"

# Launcher script that execs the real binary
cat > "$MACOS_DIR/VolMon" <<LAUNCHER
#!/usr/bin/env bash
exec "$GUI_BIN" "\$@"
LAUNCHER
chmod +x "$MACOS_DIR/VolMon"

# Info.plist
cat > "$CONTENTS/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>VolMon</string>
    <key>CFBundleDisplayName</key>
    <string>VolMon</string>
    <key>CFBundleIdentifier</key>
    <string>com.volmon.gui</string>
    <key>CFBundleExecutable</key>
    <string>VolMon</string>
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

# Convert PNG to icns if sips is available (it ships with macOS)
if [[ -f "$ICON_SRC" ]]; then
    if command -v sips &>/dev/null && command -v iconutil &>/dev/null; then
        ICONSET=$(mktemp -d)/volmon.iconset
        mkdir -p "$ICONSET"
        # Generate all required sizes from the 256px source
        for sz in 16 32 64 128 256; do
            sips -z $sz $sz "$ICON_SRC" --out "$ICONSET/icon_${sz}x${sz}.png" &>/dev/null
        done
        # Retina variants (NxN@2x uses the next size up)
        cp "$ICONSET/icon_32x32.png"   "$ICONSET/icon_16x16@2x.png"
        cp "$ICONSET/icon_64x64.png"   "$ICONSET/icon_32x32@2x.png"
        cp "$ICONSET/icon_256x256.png" "$ICONSET/icon_128x128@2x.png"
        # Remove the non-standard 64x64
        rm -f "$ICONSET/icon_64x64.png"
        iconutil -c icns -o "$RESOURCES/volmon.icns" "$ICONSET" 2>/dev/null && \
            green "  App icon created (icns)." || \
            cp "$ICON_SRC" "$RESOURCES/volmon.png"
        rm -rf "$(dirname "$ICONSET")"
    else
        # Fallback: just copy the PNG (won't show in Dock but bundle still works)
        cp "$ICON_SRC" "$RESOURCES/volmon.png"
    fi
else
    echo "  Warning: volmon.png not found, .app bundle will have no icon."
fi

green "  VolMon.app created at $APP_BUNDLE"

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

# Unload first if already loaded, then load fresh
launchctl bootout "gui/$(id -u)/$DAEMON_LABEL" 2>/dev/null || true
launchctl bootstrap "gui/$(id -u)" "$DAEMON_PLIST" 2>/dev/null || \
    launchctl load -w "$DAEMON_PLIST"

green "  Daemon agent installed and started."

# ── GUI: launchd user agent ──────────────────────────────────────────
bold "Installing GUI launch agent..."

# Launch the .app bundle so macOS picks up the icon and Info.plist
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

launchctl bootout "gui/$(id -u)/$GUI_LABEL" 2>/dev/null || true
launchctl bootstrap "gui/$(id -u)" "$GUI_PLIST" 2>/dev/null || \
    launchctl load -w "$GUI_PLIST"

green "  GUI agent installed and started."

# ── Done ──────────────────────────────────────────────────────────────
echo ""
green "VolMon registered successfully!"
echo ""
echo "  App bundle:   $APP_BUNDLE"
echo "  Daemon logs:  $LOG_DIR/daemon-*.log"
echo "  GUI logs:     $LOG_DIR/gui-*.log"
echo "  Unregister:   $0 --unregister"
echo ""
