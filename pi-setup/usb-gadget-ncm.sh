#!/usr/bin/env bash
# Bring up a USB NCM ethernet gadget on usb0 with a fixed IP.
# Runs at boot (see provision-usb.sh). Requires dtoverlay=dwc2 in config.txt.
set -e
modprobe libcomposite
G=/sys/kernel/config/usb_gadget/pisignage
if [ ! -d "$G" ]; then
  mkdir -p "$G"; cd "$G"
  echo 0x1d6b > idVendor        # Linux Foundation
  echo 0x0104 > idProduct       # Multifunction Composite Gadget
  echo 0x0100 > bcdDevice; echo 0x0200 > bcdUSB
  mkdir -p strings/0x409
  echo "PiSignage" > strings/0x409/manufacturer
  echo "PiSignage Setup" > strings/0x409/product
  echo "0001" > strings/0x409/serialnumber
  mkdir -p configs/c.1/strings/0x409
  echo "NCM" > configs/c.1/strings/0x409/configuration
  mkdir -p functions/ncm.usb0
  ln -sf functions/ncm.usb0 configs/c.1/
  ls /sys/class/udc > UDC
fi
ip addr add 10.55.0.1/24 dev usb0 2>/dev/null || true
ip link set usb0 up
