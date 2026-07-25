#Requires -Version 7
# Push the complete agent bundle to paired Pis over authenticated HTTP.
[CmdletBinding(SupportsShouldProcess)]
param(
    [string[]]$Hosts,
    [int]$Port = 8080
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Security.Cryptography.ProtectedData

$agentDir = Join-Path $PSScriptRoot "agent"
$appDataDir = Join-Path $env:APPDATA "PiSignage"
$devicesFile = Join-Path $appDataDir "devices.json"
$credentialsFile = Join-Path $appDataDir "credentials.dat"
$approvedModules = @("main.py", "trust.py", "control_auth.py", "delivery_reset.py")

function Unprotect-Vault([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "No paired-controller vault exists at $Path."
    }

    $protectedBytes = [IO.File]::ReadAllBytes($Path)
    $plainBytes = [Security.Cryptography.ProtectedData]::Unprotect(
        $protectedBytes,
        $null,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)
    try {
        return ([Text.Encoding]::UTF8.GetString($plainBytes) |
            ConvertFrom-Json -AsHashtable)
    }
    finally {
        [Array]::Clear($plainBytes, 0, $plainBytes.Length)
    }
}

function Get-VaultMutexName([string]$Path) {
    $normalizedPath = [IO.Path]::GetFullPath($Path).ToUpperInvariant()
    $currentUser = "$([Environment]::UserDomainName)\$([Environment]::UserName)"
    $key = [Text.Encoding]::UTF8.GetBytes("$currentUser`n$normalizedPath")
    $hash = [Security.Cryptography.SHA256]::HashData($key)
    return "Local\PiSignage.CredentialVault.$([Convert]::ToHexString($hash))"
}

function Test-PairedDevice($Vault, [string]$DeviceId) {
    return (
        -not [string]::IsNullOrWhiteSpace($DeviceId) -and
        $Vault.ContainsKey("Devices") -and
        $null -ne $Vault["Devices"] -and
        $Vault["Devices"].ContainsKey($DeviceId)
    )
}

function Take-PersistedCredential([string]$DeviceId) {
    $mutex = [Threading.Mutex]::new($false, (Get-VaultMutexName $credentialsFile))
    $ownsMutex = $false
    try {
        try {
            $ownsMutex = $mutex.WaitOne()
        }
        catch [Threading.AbandonedMutexException] {
            $ownsMutex = $true
        }

        $vault = Unprotect-Vault $credentialsFile
        if (-not (Test-PairedDevice $vault $DeviceId)) {
            throw "No paired credential exists for DeviceId '$DeviceId'."
        }

        $credential = $vault["Devices"][$DeviceId]
        $counter = [long]$credential["NextCounter"]
        if ($counter -lt 1 -or $counter -eq [long]::MaxValue) {
            throw "The saved request counter for DeviceId '$DeviceId' is invalid."
        }
        $secret = [Convert]::FromBase64String([string]$credential["Secret"])
        if ($secret.Length -ne 32) {
            throw "The saved credential for DeviceId '$DeviceId' is invalid."
        }
        if ([string]::IsNullOrWhiteSpace([string]$vault["ControllerId"])) {
            throw "The controller identity in the credential vault is invalid."
        }
        if ([long]$vault["Revision"] -lt 0 -or
            [long]$vault["Revision"] -eq [long]::MaxValue) {
            throw "The credential vault revision is invalid."
        }

        $credential["NextCounter"] = $counter + 1
        $vault["Revision"] = [long]$vault["Revision"] + 1
        $jsonBytes = [Text.Encoding]::UTF8.GetBytes(
            ($vault | ConvertTo-Json -Depth 8))
        $protectedBytes = [Security.Cryptography.ProtectedData]::Protect(
            $jsonBytes,
            $null,
            [Security.Cryptography.DataProtectionScope]::CurrentUser)
        [Array]::Clear($jsonBytes, 0, $jsonBytes.Length)

        $directory = [IO.Path]::GetDirectoryName($credentialsFile)
        $temporaryPath = Join-Path $directory (
            ".$([IO.Path]::GetFileName($credentialsFile)).$([Guid]::NewGuid().ToString('N')).tmp")
        $committed = $false
        try {
            $stream = [IO.FileStream]::new(
                $temporaryPath,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None,
                4096,
                [IO.FileOptions]::WriteThrough)
            try {
                $stream.Write($protectedBytes, 0, $protectedBytes.Length)
                $stream.Flush($true)
            }
            finally {
                $stream.Dispose()
            }
            [IO.File]::Move($temporaryPath, $credentialsFile, $true)
            $committed = $true
        }
        finally {
            if (-not $committed) {
                [IO.File]::Delete($temporaryPath)
            }
        }

        return @{
            ControllerId = [string]$vault["ControllerId"]
            Counter = $counter
            Secret = $secret
        }
    }
    finally {
        if ($ownsMutex) {
            $mutex.ReleaseMutex()
        }
        $mutex.Dispose()
    }
}

function Get-SignedHeaders(
    [string]$DeviceId,
    [byte[]]$ZipBytes,
    [string]$PathAndQuery
) {
    $allocated = Take-PersistedCredential $DeviceId
    $entityHash = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($ZipBytes)).ToLowerInvariant()
    $counterText = $allocated.Counter.ToString(
        [Globalization.CultureInfo]::InvariantCulture)
    $canonical = @(
        $allocated.ControllerId
        $counterText
        "POST"
        $PathAndQuery
        $entityHash
    ) -join "`n"
    $hmac = [Security.Cryptography.HMACSHA256]::new($allocated.Secret)
    try {
        $signature = [Convert]::ToHexString(
            $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))
        ).ToLowerInvariant()
    }
    finally {
        $hmac.Dispose()
        [Array]::Clear($allocated.Secret, 0, $allocated.Secret.Length)
    }

    return @{
        "X-PiSignage-Controller" = $allocated.ControllerId
        "X-PiSignage-Counter" = $counterText
        "X-PiSignage-Entity-SHA256" = $entityHash
        "X-PiSignage-Signature" = $signature
    }
}

$expectedMatch = Select-String -Path (Join-Path $agentDir "main.py") `
    -Pattern 'AGENT_VERSION\s*=\s*"([^"]+)"'
$expected = $expectedMatch.Matches[0].Groups[1].Value
if (-not $expected) {
    throw "Couldn't read AGENT_VERSION from agent\main.py."
}
if (-not (Test-Path -LiteralPath $devicesFile -PathType Leaf)) {
    throw "No saved devices exist at $devicesFile."
}

try {
    $allDevices = @(Get-Content -Raw -LiteralPath $devicesFile | ConvertFrom-Json)
}
catch {
    throw "Couldn't read the saved Pi list at $devicesFile."
}

if ($Hosts) {
    $selected = foreach ($requestedHost in $Hosts) {
        $matches = @($allDevices | Where-Object {
            $_.Ip -ieq $requestedHost -or
            $_.Hostname -ieq $requestedHost -or
            $_.Name -ieq $requestedHost
        })
        if ($matches.Count -ne 1) {
            throw "Target '$requestedHost' must match exactly one saved Pi by name, hostname, or IP."
        }
        $matches[0]
    }
}
else {
    $selected = @($allDevices)
}

$selected = @($selected | Sort-Object DeviceId -Unique)
if ($selected.Count -eq 0) {
    throw "No saved Pis were selected for deployment."
}

$preflightVault = Unprotect-Vault $credentialsFile
$unpaired = @()
foreach ($device in $selected) {
    $paired = Test-PairedDevice $preflightVault ([string]$device.DeviceId)
    $state = if ($paired) { "paired" } else { "NOT PAIRED" }
    Write-Host "$($device.Name) — DeviceId $($device.DeviceId) — version $expected — $state"
    if (-not $paired) {
        $unpaired += $device.Name
    }
}
if ($unpaired.Count -gt 0) {
    throw "Deployment refused because these targets lack paired credentials: $($unpaired -join ', ')."
}
if ($WhatIfPreference) {
    Write-Host "WhatIf: no bundle, counters, credentials, or network state changed."
    return
}

$staging = Join-Path ([IO.Path]::GetTempPath()) (
    "pisignage-agent-update-$([Guid]::NewGuid().ToString('N'))")
$zip = "$staging.zip"
$failed = @()
try {
    New-Item -ItemType Directory -Path $staging | Out-Null
    foreach ($module in $approvedModules) {
        Copy-Item -LiteralPath (Join-Path $agentDir $module) -Destination $staging
    }
    Copy-Item -LiteralPath (Join-Path $agentDir "static") -Destination $staging -Recurse
    Compress-Archive `
        -Path @(
            ($approvedModules | ForEach-Object { Join-Path $staging $_ })
            (Join-Path $staging "static")
        ) `
        -DestinationPath $zip
    $zipBytes = [IO.File]::ReadAllBytes($zip)

    foreach ($device in $selected) {
        $targetHost = if (-not [string]::IsNullOrWhiteSpace([string]$device.Ip)) {
            [string]$device.Ip
        }
        else {
            [string]$device.Hostname
        }
        $targetPort = if ($PSBoundParameters.ContainsKey("Port")) {
            $Port
        }
        elseif ([int]$device.Port -gt 0) {
            [int]$device.Port
        }
        else {
            8080
        }
        $base = "http://${targetHost}:$targetPort"
        Write-Host "`n==> $($device.Name) ($targetHost)"
        try {
            $headers = Get-SignedHeaders `
                -DeviceId ([string]$device.DeviceId) `
                -ZipBytes $zipBytes `
                -PathAndQuery "/api/update"
            $response = Invoke-RestMethod `
                -Method Post `
                -Uri "$base/api/update" `
                -Headers $headers `
                -Form @{ file = Get-Item -LiteralPath $zip } `
                -TimeoutSec 30
        }
        catch {
            Write-Warning "$($device.Name) — push failed: $($_.Exception.Message)"
            $failed += $device.Name
            continue
        }
        if (-not $response.ok) {
            Write-Warning "$($device.Name) — Pi rejected the update"
            $failed += $device.Name
            continue
        }

        $back = $false
        foreach ($attempt in 1..60) {
            Start-Sleep 1
            try {
                $status = Invoke-RestMethod "$base/api/status" -TimeoutSec 3
                if ($status.agent_version -eq $expected) {
                    $back = $true
                    break
                }
            }
            catch {
                # The agent is still restarting.
            }
        }
        if ($back) {
            Write-Host "$($device.Name) — updated to $expected (TV will blink once)"
        }
        else {
            Write-Warning "$($device.Name) — pushed, but the agent did not return within 60s"
            $failed += $device.Name
        }
    }
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
}

if ($failed.Count -gt 0) {
    throw "Deployment failed for: $($failed -join ', ')."
}
Write-Host "`nAll selected Pis updated to $expected."
