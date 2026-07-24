# Push the agent (main.py + static pages) to Pis and restart their services.
#   .\deploy-agent.ps1                 # deploys to every Pi saved in the control app
#   .\deploy-agent.ps1 -Hosts 192.168.0.58, pisignage2.local
#   .\deploy-agent.ps1 -User admin
param(
    [string[]]$Hosts,
    [string]$User = "pi"
)

$agentDir = Join-Path $PSScriptRoot "agent"

if (-not $Hosts) {
    # same list the control app uses
    $devicesFile = Join-Path $env:APPDATA "PiSignage\devices.json"
    if (-not (Test-Path $devicesFile)) {
        Write-Error "No -Hosts given and no saved devices at $devicesFile"; exit 1
    }
    $devices = Get-Content $devicesFile | ConvertFrom-Json
    $Hosts = $devices | ForEach-Object { $_.Ip }
    Write-Host "Deploying to saved Pis: $($devices | ForEach-Object { "$($_.Name) ($($_.Ip))" } | Join-String -Separator ', ')"
}

$failed = @()
foreach ($h in $Hosts) {
    Write-Host "`n==> $h"
    ssh "${User}@${h}" "rm -rf /tmp/agent-push && mkdir -p /tmp/agent-push"
    if ($LASTEXITCODE -ne 0) { Write-Warning "$h — unreachable over SSH"; $failed += $h; continue }

    scp -r (Join-Path $agentDir "main.py") (Join-Path $agentDir "static") "${User}@${h}:/tmp/agent-push/"
    if ($LASTEXITCODE -ne 0) { Write-Warning "$h — copy failed (Pi off? wrong address?)"; $failed += $h; continue }

    ssh "${User}@${h}" "mv /tmp/agent-push/main.py ~/pi-signage/agent/main.py && cp -r /tmp/agent-push/static/. ~/pi-signage/agent/static/ && rm -rf /tmp/agent-push && sudo systemctl restart signage-agent && systemctl --user restart pisignage-kiosk"
    if ($LASTEXITCODE -ne 0) { Write-Warning "$h — install/restart failed"; $failed += $h; continue }

    Write-Host "$h — done (TV will blink once as the kiosk reloads)"
}

if ($failed) { Write-Warning "Failed: $($failed -join ', ')"; exit 1 }
Write-Host "`nAll Pis updated."
