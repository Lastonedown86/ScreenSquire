# Push the agent (main.py + static pages) to Pis over HTTP — no SSH, no password.
#   .\deploy-agent.ps1                 # deploys to every Pi saved in the control app
#   .\deploy-agent.ps1 -Hosts 192.168.0.58, pisignage2.local
# Requires PowerShell 7 (Invoke-RestMethod -Form).
param(
    [string[]]$Hosts,
    [int]$Port = 8080
)

$agentDir = Join-Path $PSScriptRoot "agent"

$expected = (Select-String -Path (Join-Path $agentDir "main.py") -Pattern 'AGENT_VERSION\s*=\s*"([^"]+)"').Matches[0].Groups[1].Value
if (-not $expected) { Write-Error "Couldn't read AGENT_VERSION from agent\main.py"; exit 1 }

if (-not $Hosts) {
    # same list the control app uses
    $devicesFile = Join-Path $env:APPDATA "PiSignage\devices.json"
    if (-not (Test-Path $devicesFile)) {
        Write-Error "No -Hosts given and no saved devices at $devicesFile"; exit 1
    }
    $devices = Get-Content $devicesFile | ConvertFrom-Json
    $Hosts = $devices | ForEach-Object { $_.Ip }
    Write-Host "Deploying $expected to saved Pis: $($devices | ForEach-Object { "$($_.Name) ($($_.Ip))" } | Join-String -Separator ', ')"
}

# build the zip once: main.py at the root + static/ folder
$staging = Join-Path ([IO.Path]::GetTempPath()) "pisignage-agent-update"
$zip = "$staging.zip"
Remove-Item $staging, $zip -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $staging | Out-Null
Copy-Item (Join-Path $agentDir "main.py") $staging
Copy-Item (Join-Path $agentDir "static") $staging -Recurse
Compress-Archive -Path (Join-Path $staging "main.py"), (Join-Path $staging "static") -DestinationPath $zip

$failed = @()
foreach ($h in $Hosts) {
    Write-Host "`n==> $h"
    $base = "http://${h}:$Port"
    try {
        $resp = Invoke-RestMethod -Method Post -Uri "$base/api/update" -Form @{ file = Get-Item $zip } -TimeoutSec 30
    } catch {
        Write-Warning "$h — push failed: $($_.Exception.Message) (agent too old? bootstrap it over SSH once)"
        $failed += $h; continue
    }
    if (-not $resp.ok) { Write-Warning "$h — Pi rejected the update"; $failed += $h; continue }

    # agent restarts itself now — wait for it to come back with the new version
    $back = $false
    foreach ($i in 1..60) {
        Start-Sleep 1
        try {
            $st = Invoke-RestMethod "$base/api/status" -TimeoutSec 3
            if ($st.agent_version -eq $expected) { $back = $true; break }
        } catch { }  # still restarting
    }
    if ($back) { Write-Host "$h — updated to $expected (TV will blink once)" }
    else { Write-Warning "$h — pushed, but agent didn't come back within 60s"; $failed += $h }
}
Remove-Item $staging, $zip -Recurse -Force -ErrorAction SilentlyContinue

if ($failed) { Write-Warning "Failed: $($failed -join ', ')"; exit 1 }
Write-Host "`nAll Pis updated to $expected."
