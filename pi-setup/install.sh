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
sudo tee /etc/systemd/system/signage-agent.service >/dev/null <<EOF
[Unit]
Description=Pi Signage Agent
After=network-online.target
Wants=network-online.target

[Service]
User=$USER_NAME
WorkingDirectory=$AGENT_DIR
Environment=SIGNAGE_PORT=8080
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

# ---------------------------------------------------------------- autostart (covers labwc, wayfire, and X11 sessions)
mkdir -p "$HOME_DIR/.config/labwc" "$HOME_DIR/.config/autostart"

grep -q kiosk.sh "$HOME_DIR/.config/labwc/autostart" 2>/dev/null || \
  echo "$APP_DIR/kiosk.sh &" >> "$HOME_DIR/.config/labwc/autostart"

if [ -f "$HOME_DIR/.config/wayfire.ini" ] && ! grep -q kiosk.sh "$HOME_DIR/.config/wayfire.ini"; then
  printf "\n[autostart]\nkiosk = %s\n" "$APP_DIR/kiosk.sh" >> "$HOME_DIR/.config/wayfire.ini"
fi

cat > "$HOME_DIR/.config/autostart/signage-kiosk.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Signage Kiosk
Exec=$APP_DIR/kiosk.sh
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
