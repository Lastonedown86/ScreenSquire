#!/usr/bin/env bash
# Pi Signage installer — run ON the Raspberry Pi (Raspberry Pi OS Bookworm, Desktop image)
#   bash install.sh
# Idempotent: safe to re-run.
set -euo pipefail

USER_NAME="${SUDO_USER:-$USER}"
HOME_DIR="$(getent passwd "$USER_NAME" | cut -d: -f6)"
APP_DIR="$HOME_DIR/pi-signage"
AGENT_DIR="$APP_DIR/agent"

echo "==> Installing for user: $USER_NAME ($HOME_DIR)"

# ---------------------------------------------------------------- packages
sudo apt-get update
sudo apt-get install -y python3-venv chromium-browser

# ---------------------------------------------------------------- agent
if [ ! -d "$AGENT_DIR" ]; then
  echo "ERROR: copy the repo to $APP_DIR first (agent/ must exist)"; exit 1
fi

python3 -m venv "$AGENT_DIR/.venv"
"$AGENT_DIR/.venv/bin/pip" install --upgrade pip
"$AGENT_DIR/.venv/bin/pip" install -r "$AGENT_DIR/requirements.txt"

# ---------------------------------------------------------------- agent service
USER_UID="$(id -u "$USER_NAME")"
# XDG_RUNTIME_DIR + the session bus let the agent drive the user-session kiosk
# service (systemctl --user) so the control app can stop/start the kiosk.
sudo tee /etc/systemd/system/signage-agent.service >/dev/null <<EOF
[Unit]
Description=Pi Signage Agent
After=network-online.target
Wants=network-online.target

[Service]
User=$USER_NAME
WorkingDirectory=$AGENT_DIR
Environment=SIGNAGE_PORT=8080
Environment=XDG_RUNTIME_DIR=/run/user/$USER_UID
Environment=DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$USER_UID/bus
ExecStart=$AGENT_DIR/.venv/bin/python main.py
Restart=always
RestartSec=3

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable --now signage-agent.service

# ---------------------------------------------------------------- kiosk launcher
mkdir -p "$APP_DIR"
cat > "$APP_DIR/kiosk.sh" <<'EOF'
#!/usr/bin/env bash
# Wait for the agent, then run Chromium fullscreen forever.
until curl -sf http://localhost:8080/api/status >/dev/null; do sleep 1; done
while true; do
  chromium-browser \
    --kiosk http://localhost:8080/ \
    --noerrdialogs --disable-infobars --disable-session-crashed-bubble \
    --autoplay-policy=no-user-gesture-required \
    --check-for-update-interval=31536000 \
    --overscroll-history-navigation=0
  sleep 2   # if Chromium ever crashes, relaunch it
done
EOF
chmod +x "$APP_DIR/kiosk.sh"

# ---- kiosk as a systemd USER service, so the app can stop/start it on demand ----
# (stopping it drops the Pi to its desktop, controllable over VNC; starting it
#  brings signage back)
mkdir -p "$HOME_DIR/.config/systemd/user"
cat > "$HOME_DIR/.config/systemd/user/pisignage-kiosk.service" <<EOF
[Unit]
Description=PiSignage Chromium kiosk
After=graphical-session.target
PartOf=graphical-session.target

[Service]
Environment=WAYLAND_DISPLAY=wayland-0
ExecStart=$APP_DIR/kiosk.sh
Restart=always
RestartSec=2

[Install]
WantedBy=graphical-session.target
EOF
# keep the user manager alive so the agent can reach it (systemctl --user)
sudo loginctl enable-linger "$USER_NAME" || true

# ---------------------------------------------------------------- autostart (labwc/wayfire)
# Start the kiosk via its user service, importing the compositor env first so
# Chromium finds the Wayland display.
mkdir -p "$HOME_DIR/.config/labwc" "$HOME_DIR/.config/autostart"
KIOSK_START='systemctl --user import-environment WAYLAND_DISPLAY XDG_RUNTIME_DIR XDG_CURRENT_DESKTOP; systemctl --user start pisignage-kiosk.service'

# remove any old direct-launch line, then add the service-start line
sed -i '/kiosk\.sh/d' "$HOME_DIR/.config/labwc/autostart" 2>/dev/null || true
grep -q pisignage-kiosk "$HOME_DIR/.config/labwc/autostart" 2>/dev/null || \
  echo "$KIOSK_START" >> "$HOME_DIR/.config/labwc/autostart"

cat > "$HOME_DIR/.config/autostart/signage-kiosk.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Signage Kiosk
Exec=sh -c "$KIOSK_START"
X-GNOME-Autostart-enabled=true
EOF

# ---------------------------------------------------------------- polish
# never blank the screen
sudo raspi-config nonint do_blanking 1 || true
# boot to desktop with autologin (kiosk needs a session)
sudo raspi-config nonint do_boot_behaviour B4 || true

echo ""
echo "==> Done. Reboot to start the kiosk:  sudo reboot"
echo "    Agent API:  http://$(hostname -I | awk '{print $1}'):8080/api/status"
