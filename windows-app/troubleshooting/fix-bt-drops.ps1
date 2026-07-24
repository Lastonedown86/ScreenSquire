# Fix Bluetooth mouse/keyboard drops on PCs with a combo Wi-Fi + Bluetooth card.
#
# Symptom: your BT mouse/keyboard disconnects while using the Pi Signage control
# app (or any time Wi-Fi is busy). Cause: Windows power-saves the Wi-Fi adapter,
# and on combo cards (e.g. Realtek 8852BE) that hiccups the Bluetooth radio on the
# same chip.
#
# This disables that power-saving. Admin is required; the script self-elevates.
# Most PCs never need this — only run it if you actually see BT drops.

# --- self-elevate ---
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell -Verb RunAs -ArgumentList `
        '-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`""
    return
}

$ErrorActionPreference = 'Continue'
Write-Host "Pi Signage — Bluetooth-drop fix`n" -ForegroundColor Cyan

# 1. Stop Windows turning off the Wi-Fi adapter (the combo chip Bluetooth rides on)
foreach ($a in Get-NetAdapter | Where-Object {
        $_.InterfaceDescription -match 'Wireless|Wi-Fi|802\.11|8852' -and $_.Status -eq 'Up' }) {
    try {
        $pm = Get-NetAdapterPowerManagement -Name $a.Name
        $pm.AllowComputerToTurnOffDevice = 'Disabled'   # set the property, then pipe it back
        $pm | Set-NetAdapterPowerManagement
        Write-Host "  Wi-Fi adapter power-off disabled: $($a.Name)" -ForegroundColor Green
    } catch { Write-Host "  Wi-Fi adapter: $($_.Exception.Message)" -ForegroundColor Yellow }
}

# 2. Disable USB selective suspend (Bluetooth often enumerates over USB internally)
$sub  = '2a737441-1930-4402-8d77-b2bebba308a3'   # USB settings
$item = '48e6b7a6-50f5-4782-a5d4-53bb8f07e226'   # USB selective suspend
powercfg /setacvalueindex SCHEME_CURRENT $sub $item 0 | Out-Null
powercfg /setdcvalueindex SCHEME_CURRENT $sub $item 0 | Out-Null
powercfg /setactive SCHEME_CURRENT | Out-Null
Write-Host "  USB selective suspend disabled" -ForegroundColor Green

# 3. Turn off power-save on the Bluetooth radio device(s)
foreach ($d in Get-PnpDevice -Class Bluetooth -PresentOnly -ErrorAction SilentlyContinue |
                Where-Object { $_.FriendlyName -match 'Bluetooth Adapter|Bluetooth Radio' }) {
    $p = "HKLM:\SYSTEM\CurrentControlSet\Enum\$($d.InstanceId)\Device Parameters"
    if (Test-Path $p) {
        New-ItemProperty -Path $p -Name 'SelectiveSuspendEnabled' -PropertyType DWord -Value 0 -Force | Out-Null
        New-ItemProperty -Path $p -Name 'EnhancedPowerManagementEnabled' -PropertyType DWord -Value 0 -Force | Out-Null
        Write-Host "  Bluetooth radio power-save disabled: $($d.FriendlyName)" -ForegroundColor Green
    }
}

Write-Host "`nDone. If a device is still asleep, toggle it off/on once to reconnect."
Write-Host "If drops persist, update the Wi-Fi/Bluetooth (e.g. Realtek 8852BE) driver."
Read-Host "`nPress Enter to close"
