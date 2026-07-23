#!/usr/bin/env bash
# Run ONCE on the Pi (as part of pre-imaging) to install USB WiFi-setup provisioning.
set -euo pipefail

USER_NAME="${SUDO_USER:-$USER}"

# 1. USB gadget requires dwc2
grep -q '^dtoverlay=dwc2,dr_mode=peripheral' /boot/firmware/config.txt 2>/dev/null || \
  echo 'dtoverlay=dwc2,dr_mode=peripheral' | sudo tee -a /boot/firmware/config.txt >/dev/null

# 2. Install the gadget bring-up script
sudo install -m 0755 "$(dirname "$0")/usb-gadget-ncm.sh" /usr/local/sbin/usb-gadget-ncm.sh

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
EOF

# 6. Allow the agent's user to drive nmcli without a password (validated before install)
TMP_SUDO="$(mktemp)"
echo "$USER_NAME ALL=(root) NOPASSWD: /usr/bin/nmcli" > "$TMP_SUDO"
sudo visudo -cf "$TMP_SUDO" && sudo install -m 0440 "$TMP_SUDO" /etc/sudoers.d/pisignage-nmcli
rm -f "$TMP_SUDO"

sudo systemctl enable usb-gadget-ncm.service
echo "==> USB provisioning installed. Reboot, then this Pi presents a USB setup link at 10.55.0.1:8080."
