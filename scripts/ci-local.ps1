# Run the CI suite locally so a GitHub Actions outage cannot block verification.
#
#   .\scripts\ci-local.ps1              # everything
#   .\scripts\ci-local.ps1 -SkipDotnet  # only the Docker (Linux) jobs
#
# Mirrors .github/workflows/ci.yml:
#   - "Python agent tests"            -> Docker (Linux, Python 3.13, like CI)
#   - "Shell and repository checks"   -> Docker (Linux bash + git, like CI)
#   - ".NET tests and Release build"  -> host. CI runs this on windows-latest;
#     the WPF app targets net8.0-windows, which cannot build in a Linux
#     container, so the Windows host is the faithful environment.
# CodeQL and Dependency Review are GitHub-hosted analyses with no local
# equivalent; they are not simulated.
param([switch]$SkipDotnet)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$failed = @()

Write-Host "==> Building ci-local image"
docker build -q -t screensquire-ci -f "$repo/scripts/ci-local.Dockerfile" "$repo/scripts" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "docker build failed" }

Write-Host "==> Python agent tests (Docker)"
docker run --rm -v "${repo}:/src" -v screensquire-pip-cache:/root/.cache/pip `
  screensquire-ci bash /src/scripts/ci-python.sh
if ($LASTEXITCODE -ne 0) { $failed += 'Python agent tests' }

Write-Host "==> Shell and repository checks (Docker)"
docker run --rm -v "${repo}:/src" screensquire-ci bash /src/scripts/ci-shell.sh
if ($LASTEXITCODE -ne 0) { $failed += 'Shell and repository checks' }

if (-not $SkipDotnet) {
    Write-Host "==> .NET tests and Release build (host)"
    dotnet restore "$repo/PiSignage.slnx"
    if ($LASTEXITCODE -ne 0) { $failed += '.NET restore' }
    else {
        dotnet test "$repo/PiSignage.slnx" -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { $failed += '.NET test' }
        dotnet build "$repo/PiSignage.slnx" -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { $failed += '.NET build' }
    }
}

Write-Host ""
if ($failed.Count -gt 0) {
    Write-Host "CI-LOCAL FAILED: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "CI-LOCAL PASSED" -ForegroundColor Green
