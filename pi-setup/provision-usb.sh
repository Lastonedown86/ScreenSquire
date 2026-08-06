#!/usr/bin/env bash
# Run ONCE on the Pi (as part of pre-imaging) to install USB WiFi-setup provisioning.
set -euo pipefail

USER_NAME="${SUDO_USER:-$USER}"
HOME_DIR="$(getent passwd "$USER_NAME" | cut -d: -f6)"
WIFI_COUNTRY="${WIFI_COUNTRY:-US}"   # override for non-US: WIFI_COUNTRY=GB bash provision-usb.sh

# 0. Enable the WiFi radio out of the box. Raspberry Pi OS soft-blocks WiFi until
#    the regulatory country is set — without this the customer's setup wizard sees
#    NO networks ("not found") even though the radio hardware is fine.
sudo raspi-config nonint do_wifi_country "$WIFI_COUNTRY"
sudo rfkill unblock wifi 2>/dev/null || true

# 1. USB gadget requires dwc2
grep -q '^dtoverlay=dwc2,dr_mode=peripheral' /boot/firmware/config.txt 2>/dev/null || \
  echo 'dtoverlay=dwc2,dr_mode=peripheral' | sudo tee -a /boot/firmware/config.txt >/dev/null

# 2. Install the gadget bring-up script
sudo install -m 0755 "$(dirname "$0")/usb-gadget-ncm.sh" /usr/local/sbin/usb-gadget-ncm.sh

# Initialize the device identity and one-time recovery PIN as the target user.
TRUST_FILE="$HOME_DIR/pi-signage/agent/data/trust.json"
if [ ! -f "$TRUST_FILE" ]; then
  mkdir -p "$(dirname "$TRUST_FILE")"
  chown "$USER_NAME:$USER_NAME" "$(dirname "$TRUST_FILE")"
  PIN_LINE="$(sudo -u "$USER_NAME" python3 "$HOME_DIR/pi-signage/agent/trust.py" init \
    --data-dir "$HOME_DIR/pi-signage/agent/data")"
  echo "============================================================"
  echo "$PIN_LINE"
  echo "Print this 8-digit PIN on the bottom label before delivery."
  echo "It will not be displayed again."
  echo "============================================================"
fi

# 3. systemd unit to run it at boot
sudo tee /etc/systemd/system/usb-gadget-ncm.service >/dev/null <<'EOF'
[Unit]
Description=PiSignage USB NCM gadget
After=sys-kernel-config.mount
[Service]
Type=oneshot
ExecStart=/usr/local/sbin/usb-gadget-ncm.sh
RemainAfterExit=yes
[Install]
WantedBy=multi-user.target
EOF

# 4. Let NetworkManager ignore usb0 (we own it)
sudo tee /etc/NetworkManager/conf.d/10-pisignage-usb.conf >/dev/null <<'EOF'
[keyfile]
unmanaged-devices=interface-name:usb0
EOF

# 5. DHCP for the PC on usb0
sudo apt-get install -y dnsmasq
sudo tee /etc/dnsmasq.d/pisignage-usb.conf >/dev/null <<'EOF'
port=0
interface=usb0
bind-dynamic
dhcp-range=10.55.0.10,10.55.0.20,255.255.255.0,1h
dhcp-option=3
dhcp-option=6
EOF

# 6. Allow the agent's user to drive nmcli without a password (validated before install)
TMP_SUDO="$(mktemp)"
echo "$USER_NAME ALL=(root) NOPASSWD: /usr/bin/nmcli" > "$TMP_SUDO"
sudo visudo -cf "$TMP_SUDO" && sudo install -m 0440 "$TMP_SUDO" /etc/sudoers.d/pisignage-nmcli
rm -f "$TMP_SUDO"

# 7. Remote support is attended on the store laptop through Windows Quick
#    Assist. Explicitly disable Raspberry Pi VNC, including on images where it
#    was enabled previously.
sudo raspi-config nonint do_vnc 1

sudo systemctl enable usb-gadget-ncm.service

# 8. Login console on the USB gadget's serial function (ttyGS0). Last-resort
#    recovery door for a shipped Pi whose agent is down: the builder guides the
#    store through a terminal on the COM port that appears beside the USB
#    network. Needs the Pi's login password — same trust as a local keyboard.
sudo systemctl enable serial-getty@ttyGS0.service
echo "==> USB provisioning installed. Reboot, then this Pi presents a USB setup link at 10.55.0.1:8080."
echo "    Remote support: use attended Windows Quick Assist on the store laptop."
