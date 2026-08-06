#!/usr/bin/env bash
# Bring up a USB NCM ethernet gadget on usb0 with a fixed IP.
# Runs at boot (see provision-usb.sh). Requires dtoverlay=dwc2 in config.txt.
#
# Idempotent and race-safe: the UDC can register seconds after
# sys-kernel-config.mount, and writing an empty string to UDC is a silent
# no-op — a run that lost that race used to leave the gadget directory created
# but unbound, and the directory check kept every later run from retrying, so
# onboarding at 10.55.0.1 was dead until reboot. Every run now waits for a UDC
# and binds whenever the gadget is not bound yet.
#
# SYS_ROOT and UDC_WAIT_TRIES exist so the tests can drive this script against
# a fake /sys; both default to the real thing on a Pi.
set -e
SYS_ROOT="${SYS_ROOT:-}"
G="$SYS_ROOT/sys/kernel/config/usb_gadget/pisignage"
UDC_CLASS="$SYS_ROOT/sys/class/udc"
UDC_WAIT_TRIES="${UDC_WAIT_TRIES:-60}"   # x 0.5s = 30s

modprobe libcomposite

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
fi

bound="$(cat "$G/UDC" 2>/dev/null | tr -d '[:space:]')"
if [ -z "$bound" ]; then
  udc=""
  for _ in $(seq 1 "$UDC_WAIT_TRIES"); do
    udc="$(ls "$UDC_CLASS" 2>/dev/null | head -n1)"
    [ -n "$udc" ] && break
    sleep 0.5
  done
  if [ -z "$udc" ]; then
    echo "usb-gadget-ncm: no UDC appeared; is dtoverlay=dwc2,dr_mode=peripheral set?" >&2
    exit 1
  fi
  echo "$udc" > "$G/UDC"
fi

# the ncm function creates usb0 asynchronously after the UDC bind — wait for it
for _ in $(seq 1 50); do ip link show usb0 >/dev/null 2>&1 && break; sleep 0.1; done
command -v udevadm >/dev/null && udevadm settle || true
ip addr add 10.55.0.1/24 dev usb0 2>/dev/null || true
ip link set usb0 up
