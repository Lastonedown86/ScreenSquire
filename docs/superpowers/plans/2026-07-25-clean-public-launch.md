# ScreenSquire Clean Public Launch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish the existing project as the guarded public repository `Lastonedown86/ScreenSquire` after privacy-safe history rewriting and complete local, hosted, and clean-clone verification.

**Architecture:** Prepare and verify all public-facing repository content locally, preserve the original repository in an external Git bundle, and then rewrite only the unpublished history. Stage the result privately for CI, change visibility only after CI passes, configure public-repository security and a `main` ruleset through GitHub, and finish with an independent clean-clone verification.

**Tech Stack:** Git and `git-filter-repo`, GitHub CLI and REST API, GitHub Actions, Dependabot, CodeQL, dependency review, Python 3.13/FastAPI/pytest, .NET 8/WPF/xUnit, Bash, Gitleaks

## Global Constraints

- The public repository is `Lastonedown86/ScreenSquire`.
- The repository license is MIT.
- The installed Windows application remains named **Pi Signage Control**.
- Preserve useful unpublished history, but replace every author and committer email with `159793913+Lastonedown86@users.noreply.github.com`.
- Do not publish real Wi-Fi credentials, provisioning PINs, client or store details, machine-specific user paths, actual Git recovery refs, or backup artifacts. Documented recovery procedures and functional backup code are allowed.
- Publish only `main`.
- Nothing becomes public until local tests and the rewritten-history privacy scan pass.
- Normal post-launch changes reach `main` through pull requests with zero required approvals.
- Keep an administrator bypass for emergency recovery only.
- Do not rename the Windows app, publish a binary release, add code signing, or redesign provisioning in this plan.

## File Structure

**Create**

- `.github/workflows/ci.yml` — required Python, .NET, Bash, and repository checks.
- `.github/workflows/codeql.yml` — C# and Python CodeQL analysis, skipped while the staging repository is private.
- `.github/workflows/dependency-review.yml` — dependency-diff review for pull requests.
- `.github/dependabot.yml` — weekly pip, NuGet, and GitHub Actions updates.
- `.github/ISSUE_TEMPLATE/bug_report.yml` — structured bug reports with privacy guidance.
- `.github/ISSUE_TEMPLATE/feature_request.yml` — structured feature requests.
- `.github/ISSUE_TEMPLATE/config.yml` — directs security reports to private vulnerability reporting.
- `.github/PULL_REQUEST_TEMPLATE.md` — focused validation and privacy checklist.
- `LICENSE` — MIT license.
- `CONTRIBUTING.md` — solo-maintainer contribution workflow.
- `SECURITY.md` — private vulnerability-reporting policy.
- `SUPPORT.md` — public support boundary.

**Modify**

- `README.md` — use the ScreenSquire public identity, add status badges, and retain the existing operational and security documentation.
- `docs/superpowers/plans/2026-07-24-tournament-round-console.md` — replace the personal absolute repository path with a portable path.

**Temporary, never commit**

- `.public-launch-replacements` — replaces the personal path in historical blobs.
- `.public-launch-ruleset.json` — exact `main` ruleset request body.
- `.public-launch-security.json` — exact GitHub security-settings request body.
- `ScreenSquire-pre-public-2026-07-25.bundle` in the parent directory — recovery bundle containing the pre-launch repository.

---

### Task 1: Establish the Recoverable Pre-Launch Baseline

**Files:**
- Verify: `docs/superpowers/specs/2026-07-25-clean-public-launch-design.md`
- Verify: `docs/superpowers/plans/2026-07-25-clean-public-launch.md`
- Create outside repository: `../ScreenSquire-pre-public-2026-07-25.bundle`

**Interfaces:**
- Consumes: the current clean local `main` branch with no configured remote.
- Produces: a verified Git bundle capable of restoring every current ref and object.

- [ ] **Step 1: Confirm the exact repository and unpublished state**

Run:

```powershell
$repo = (git rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0) { throw "Not inside the ScreenSquire source repository" }
if ((git branch --show-current).Trim() -ne "main") { throw "Expected main branch" }
if (git status --porcelain) { throw "Working tree must be clean" }
if (git remote) { throw "A remote already exists; inspect it before launch" }
git branch --all
git log -5 --oneline
```

Expected: only local `main` is listed, the worktree is clean, and no remote is printed.

- [ ] **Step 2: Create the recovery bundle outside the repository**

Run:

```powershell
$repo = (git rev-parse --show-toplevel).Trim()
$launchBackup = Join-Path (Split-Path $repo -Parent) "ScreenSquire-pre-public-2026-07-25.bundle"
if (Test-Path -LiteralPath $launchBackup) {
    throw "Refusing to overwrite existing recovery bundle: $launchBackup"
}
git bundle create $launchBackup --all
if ($LASTEXITCODE -ne 0) { throw "git bundle create failed" }
git bundle verify $launchBackup
if ($LASTEXITCODE -ne 0) { throw "git bundle verify failed" }
Get-FileHash -Algorithm SHA256 -LiteralPath $launchBackup
```

Expected: `git bundle verify` reports that the bundle is okay and a SHA-256 hash is printed. Record the path and hash in the execution notes.

- [ ] **Step 3: Prove the bundle can expose the saved `main` commit**

Run:

```powershell
$repo = (git rev-parse --show-toplevel).Trim()
$launchBackup = Join-Path (Split-Path $repo -Parent) "ScreenSquire-pre-public-2026-07-25.bundle"
$savedMain = (git bundle list-heads $launchBackup | Select-String "refs/heads/main").ToString()
if (-not $savedMain) { throw "Recovery bundle does not contain refs/heads/main" }
$savedMain
```

Expected: one line containing the current commit ID and `refs/heads/main`.

---

### Task 2: Add Required CI and Security Automation

**Files:**
- Create: `.github/workflows/ci.yml`
- Create: `.github/workflows/codeql.yml`
- Create: `.github/workflows/dependency-review.yml`
- Create: `.github/dependabot.yml`

**Interfaces:**
- Consumes: `agent/requirements.txt`, `agent/requirements-dev.txt`, `PiSignage.slnx`, and `pi-setup/*.sh`.
- Produces: stable required check names `Python agent tests`, `.NET tests and Release build`, `Shell and repository checks`, and `Dependency review`.

- [ ] **Step 1: Create the required CI workflow**

Create `.github/workflows/ci.yml` with:

```yaml
name: CI

on:
  push:
    branches: [main]
  pull_request:
    branches: [main]

permissions:
  contents: read

jobs:
  python:
    name: Python agent tests
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v6
      - uses: actions/setup-python@v6
        with:
          python-version: "3.13"
          cache: pip
          cache-dependency-path: |
            agent/requirements.txt
            agent/requirements-dev.txt
      - name: Install Python dependencies
        run: python -m pip install -r agent/requirements.txt -r agent/requirements-dev.txt
      - name: Test without altering runtime data
        shell: bash
        run: |
          snapshot() {
            if [[ -f agent/data/dashboard.json ]]; then
              sha256sum agent/data/dashboard.json
              stat -c '%Y' agent/data/dashboard.json
            else
              printf 'MISSING\n'
            fi
          }
          before="$(snapshot)"
          python -m pytest agent -q
          after="$(snapshot)"
          if [[ "$before" != "$after" ]]; then
            echo "Runtime dashboard data changed during tests"
            exit 1
          fi

  dotnet:
    name: .NET tests and Release build
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v6
      - uses: actions/setup-dotnet@v5
        with:
          dotnet-version: "8.0.x"
      - name: Restore
        run: dotnet restore PiSignage.slnx
      - name: Test
        run: dotnet test PiSignage.slnx -c Release --no-restore
      - name: Build
        run: dotnet build PiSignage.slnx -c Release --no-restore

  shell:
    name: Shell and repository checks
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v6
        with:
          fetch-depth: 0
      - name: Validate Raspberry Pi scripts
        shell: bash
        run: |
          for script in pi-setup/*.sh; do
            bash -n "$script"
          done
      - name: Check repository whitespace
        run: git log --check --oneline --all
```

- [ ] **Step 2: Create CodeQL analysis**

Create `.github/workflows/codeql.yml` with:

```yaml
name: CodeQL

on:
  push:
    branches: [main]
  pull_request:
    branches: [main]
  schedule:
    - cron: "17 7 * * 1"
  workflow_dispatch:

permissions:
  contents: read
  security-events: write
  packages: read

jobs:
  analyze:
    name: Analyze (${{ matrix.language }})
    if: github.event_name != 'push' || github.event.repository.visibility == 'public'
    runs-on: ubuntu-latest
    strategy:
      fail-fast: false
      matrix:
        language: [csharp, python]
    steps:
      - uses: actions/checkout@v6
      - uses: github/codeql-action/init@v4
        with:
          languages: ${{ matrix.language }}
      - uses: github/codeql-action/autobuild@v4
      - uses: github/codeql-action/analyze@v4
        with:
          category: "/language:${{ matrix.language }}"
```

- [ ] **Step 3: Create pull-request dependency review**

Create `.github/workflows/dependency-review.yml` with:

```yaml
name: Dependency Review

on:
  pull_request:
    branches: [main]

permissions:
  contents: read

jobs:
  dependency-review:
    name: Dependency review
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v6
      - uses: actions/dependency-review-action@v5
        with:
          fail-on-severity: moderate
```

- [ ] **Step 4: Configure weekly dependency updates**

Create `.github/dependabot.yml` with:

```yaml
version: 2
updates:
  - package-ecosystem: pip
    directory: /agent
    schedule:
      interval: weekly
      day: monday
    groups:
      python-dependencies:
        patterns: ["*"]
  - package-ecosystem: nuget
    directory: /
    schedule:
      interval: weekly
      day: monday
    groups:
      dotnet-dependencies:
        patterns: ["*"]
  - package-ecosystem: github-actions
    directory: /
    schedule:
      interval: weekly
      day: monday
    groups:
      github-actions:
        patterns: ["*"]
```

- [ ] **Step 5: Validate the YAML and local commands**

Run:

```powershell
$runtimeFile = "agent\data\dashboard.json"
$before = if (Test-Path $runtimeFile) {
    (Get-FileHash $runtimeFile -Algorithm SHA256).Hash + "|" + (Get-Item $runtimeFile).LastWriteTimeUtc.Ticks
} else { "MISSING" }
agent\.venv\Scripts\python.exe -m pytest agent -q
if ($LASTEXITCODE -ne 0) { throw "Python tests failed" }
$after = if (Test-Path $runtimeFile) {
    (Get-FileHash $runtimeFile -Algorithm SHA256).Hash + "|" + (Get-Item $runtimeFile).LastWriteTimeUtc.Ticks
} else { "MISSING" }
if ($before -ne $after) { throw "Runtime dashboard data changed during tests" }
dotnet test PiSignage.slnx -c Release
if ($LASTEXITCODE -ne 0) { throw ".NET tests failed" }
dotnet build PiSignage.slnx -c Release
if ($LASTEXITCODE -ne 0) { throw ".NET Release build failed" }
Get-ChildItem pi-setup -Filter *.sh | ForEach-Object {
    & "C:\Program Files\Git\bin\bash.exe" -n $_.FullName
    if ($LASTEXITCODE -ne 0) { throw "Bash syntax failed: $($_.Name)" }
}
git diff --check
if ($LASTEXITCODE -ne 0) { throw "Repository whitespace check failed" }
```

Expected: 127 Python tests pass without changing the runtime file, 123 .NET tests pass, the Release build succeeds, all shell scripts parse, and `git diff --check` is silent.

- [ ] **Step 6: Commit the automation**

```powershell
git add .github/workflows .github/dependabot.yml
git diff --cached --check
if ($LASTEXITCODE -ne 0) { throw "Staged automation has whitespace errors" }
git commit -m "ci: add public repository quality gates"
```

---

### Task 3: Add the Public Project and Community Documentation

**Files:**
- Create: `LICENSE`
- Create: `CONTRIBUTING.md`
- Create: `SECURITY.md`
- Create: `SUPPORT.md`
- Create: `.github/ISSUE_TEMPLATE/bug_report.yml`
- Create: `.github/ISSUE_TEMPLATE/feature_request.yml`
- Create: `.github/ISSUE_TEMPLATE/config.yml`
- Create: `.github/PULL_REQUEST_TEMPLATE.md`
- Modify: `README.md`
- Modify: `docs/superpowers/plans/2026-07-24-tournament-round-console.md:13`

**Interfaces:**
- Consumes: the existing detailed operational README and the repository check names from Task 2.
- Produces: a public landing page, contribution contract, private security-reporting route, support boundary, and structured contribution templates.

- [ ] **Step 1: Update the README identity and badges**

Replace the title and opening paragraph at the top of `README.md` with:

```markdown
# ScreenSquire

[![CI](https://github.com/Lastonedown86/ScreenSquire/actions/workflows/ci.yml/badge.svg)](https://github.com/Lastonedown86/ScreenSquire/actions/workflows/ci.yml)
[![CodeQL](https://github.com/Lastonedown86/ScreenSquire/actions/workflows/codeql.yml/badge.svg)](https://github.com/Lastonedown86/ScreenSquire/actions/workflows/codeql.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

ScreenSquire is digital-signage software for Raspberry Pi with the **Pi Signage
Control** Windows app. Each Display Pi is the source of truth for its media,
playlist, tournament board, and timer, so the TV keeps running when the
Controller laptop is off.
```

Keep the remaining architecture, security, setup, recovery, support, API,
verification, acceptance, and known-limits sections intact.

- [ ] **Step 2: Remove the machine-specific path**

In `docs/superpowers/plans/2026-07-24-tournament-round-console.md`, replace
the complete `- Repo root:` bullet on line 13 with:

```markdown
- Repo root: the directory containing `PiSignage.slnx`. All paths below are relative to it; run all `git`/`dotnet`/`pytest` commands from that directory.
```

- [ ] **Step 3: Add the MIT license**

Create `LICENSE` with:

```text
MIT License

Copyright (c) 2026 ScreenSquire contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

- [ ] **Step 4: Add contribution and support policies**

Create `CONTRIBUTING.md` with:

```markdown
# Contributing

ScreenSquire is currently maintained by one person. Focused bug fixes,
documentation improvements, and small feature proposals are welcome.

## Before opening a change

- Search existing issues first.
- Open an issue before a large behavioral or architectural change.
- Never include real Wi-Fi credentials, Recovery PINs, controller secrets,
  client or store details, or personal machine paths.
- Keep the installed Windows app name **Pi Signage Control** unless a separate
  branding change has been approved.

## Development checks

From the repository root, run:

```powershell
agent\.venv\Scripts\python.exe -m pytest agent -q
dotnet test PiSignage.slnx -c Release
dotnet build PiSignage.slnx -c Release
```

Validate each file in `pi-setup/` with `bash -n` when setup scripts change.
Real-Pi behavior must not be marked verified without observed hardware results.

## Pull requests

Create a branch, make focused commits, and open a pull request into `main`.
All required checks and review conversations must be resolved before merging.
Approval is not mechanically required while the project has one maintainer.
```

Create `SUPPORT.md` with:

```markdown
# Support

Use GitHub Issues for reproducible installation, development, and product
problems that do not contain sensitive information.

This public repository does not provide emergency monitoring or unattended
access to deployed Display Pis. Store support uses attended Windows Quick
Assist initiated and approved by the person at the store.

Do not post Wi-Fi credentials, Recovery PINs, controller secrets, client or
store details, private IP inventories, or logs containing those values.
Security vulnerabilities must be reported through GitHub private vulnerability
reporting as described in `SECURITY.md`.
```

- [ ] **Step 5: Add the security policy**

Create `SECURITY.md` with:

```markdown
# Security Policy

## Reporting a vulnerability

Use GitHub's private vulnerability reporting for this repository:

https://github.com/Lastonedown86/ScreenSquire/security/advisories/new

Do not open a public issue for a suspected vulnerability. Include the affected
version or commit, reproduction conditions, likely impact, and a minimal
proof-of-concept with all credentials, PINs, client details, and store details
removed.

You should receive an initial response within seven days. Acknowledgement is
not a promise of a particular fix or release date.

## Supported versions

Security fixes are made on the current `main` branch until versioned releases
are introduced. Deployed Raspberry Pis are not remotely managed by this public
repository.

## Security boundaries

Controller authentication provides request integrity, ownership, and replay
protection. It does not provide transport encryption. Use ScreenSquire only on
the intended physical USB connection and trusted store LAN.
```

- [ ] **Step 6: Add issue forms**

Create `.github/ISSUE_TEMPLATE/bug_report.yml` with:

```yaml
name: Bug report
description: Report a reproducible ScreenSquire problem
title: "[Bug]: "
labels: ["bug"]
body:
  - type: markdown
    attributes:
      value: |
        Do not include Wi-Fi credentials, Recovery PINs, controller secrets,
        client or store details, or personal machine paths.
  - type: textarea
    id: summary
    attributes:
      label: What happened?
      description: Describe the observed and expected behavior.
    validations:
      required: true
  - type: dropdown
    id: component
    attributes:
      label: Component
      options:
        - Raspberry Pi agent
        - Pi Signage Control Windows app
        - USB or Wi-Fi provisioning
        - Installer or setup scripts
        - Documentation
    validations:
      required: true
  - type: textarea
    id: reproduce
    attributes:
      label: Reproduction steps
      description: Provide the smallest repeatable sequence using sanitized data.
    validations:
      required: true
  - type: input
    id: version
    attributes:
      label: Version or commit
      placeholder: Commit SHA or agent version
    validations:
      required: true
  - type: textarea
    id: environment
    attributes:
      label: Environment
      description: Windows and Raspberry Pi OS versions, Pi model, and connection type.
    validations:
      required: true
  - type: checkboxes
    id: privacy
    attributes:
      label: Privacy check
      options:
        - label: I removed credentials, PINs, client/store details, and personal paths.
          required: true
```

Create `.github/ISSUE_TEMPLATE/feature_request.yml` with:

```yaml
name: Feature request
description: Propose a focused improvement
title: "[Feature]: "
labels: ["enhancement"]
body:
  - type: textarea
    id: problem
    attributes:
      label: Problem
      description: What concrete operator or maintainer problem should be solved?
    validations:
      required: true
  - type: textarea
    id: outcome
    attributes:
      label: Desired outcome
      description: Describe success without prescribing unnecessary implementation details.
    validations:
      required: true
  - type: textarea
    id: constraints
    attributes:
      label: Constraints
      description: Note hardware, Windows, network, recovery, or security constraints.
  - type: checkboxes
    id: privacy
    attributes:
      label: Privacy check
      options:
        - label: I removed credentials, PINs, client/store details, and personal paths.
          required: true
```

Create `.github/ISSUE_TEMPLATE/config.yml` with:

```yaml
blank_issues_enabled: false
contact_links:
  - name: Private security report
    url: https://github.com/Lastonedown86/ScreenSquire/security/advisories/new
    about: Report suspected vulnerabilities privately.
```

- [ ] **Step 7: Add the pull-request checklist**

Create `.github/PULL_REQUEST_TEMPLATE.md` with:

```markdown
## Summary

Describe the focused change and why it is needed.

## Verification

- [ ] Python agent tests pass, or this change cannot affect Python.
- [ ] .NET tests and Release build pass, or this change cannot affect .NET.
- [ ] Changed Raspberry Pi shell scripts pass `bash -n`.
- [ ] Real-Pi claims are backed by observed hardware evidence.
- [ ] No credentials, Recovery PINs, controller secrets, client/store details,
      or personal machine paths are included.

## Operational impact

Describe provisioning, recovery, compatibility, migration, or rollback impact.
Write `None` when there is no operational impact.
```

- [ ] **Step 8: Validate and commit the public documentation**

Run:

```powershell
rg -n 'C:\\Users\\[^\\]+' . -g "!**/.git/**" -g "!**/.venv/**" -g "!**/.pytest_cache/**"
if ($LASTEXITCODE -eq 0) { throw "Personal machine path remains in the working tree" }
git diff --check
if ($LASTEXITCODE -ne 0) { throw "Documentation whitespace check failed" }
git add README.md LICENSE CONTRIBUTING.md SECURITY.md SUPPORT.md .github/ISSUE_TEMPLATE .github/PULL_REQUEST_TEMPLATE.md docs/superpowers/plans/2026-07-24-tournament-round-console.md
git diff --cached --check
if ($LASTEXITCODE -ne 0) { throw "Staged documentation has whitespace errors" }
git commit -m "docs: prepare ScreenSquire public launch"
```

Expected: both privacy searches return no matches, whitespace checks pass, and the documentation commit succeeds.

---

### Task 4: Verify Locally and Rewrite Unpublished History

**Files:**
- Temporary create/delete: `.public-launch-replacements`
- Rewrite: every commit reachable from local `main`

**Interfaces:**
- Consumes: the verified recovery bundle from Task 1 and clean launch commits from Tasks 2–3.
- Produces: a clean `main` history whose commits use the GitHub noreply email and whose blobs contain no personal repository path.

- [ ] **Step 1: Run the complete local release gate**

Run:

```powershell
$runtimeFile = "agent\data\dashboard.json"
$before = if (Test-Path $runtimeFile) {
    (Get-FileHash $runtimeFile -Algorithm SHA256).Hash + "|" + (Get-Item $runtimeFile).LastWriteTimeUtc.Ticks
} else { "MISSING" }
agent\.venv\Scripts\python.exe -m pytest agent -q
if ($LASTEXITCODE -ne 0) { throw "Python tests failed" }
$after = if (Test-Path $runtimeFile) {
    (Get-FileHash $runtimeFile -Algorithm SHA256).Hash + "|" + (Get-Item $runtimeFile).LastWriteTimeUtc.Ticks
} else { "MISSING" }
if ($before -ne $after) { throw "Runtime dashboard data changed during tests" }
dotnet test PiSignage.slnx -c Release
if ($LASTEXITCODE -ne 0) { throw ".NET tests failed" }
dotnet build PiSignage.slnx -c Release
if ($LASTEXITCODE -ne 0) { throw ".NET Release build failed" }
dotnet list PiSignage.slnx package --vulnerable --include-transitive
if ($LASTEXITCODE -ne 0) { throw "NuGet vulnerability scan failed" }
Get-ChildItem pi-setup -Filter *.sh | ForEach-Object {
    & "C:\Program Files\Git\bin\bash.exe" -n $_.FullName
    if ($LASTEXITCODE -ne 0) { throw "Bash syntax failed: $($_.Name)" }
}
git diff --check
if ($LASTEXITCODE -ne 0) { throw "Repository whitespace check failed" }
if (git status --porcelain) { throw "Working tree must be clean before history rewrite" }
```

Expected: Python and .NET counts match the approved design evidence, the runtime file is unchanged, no vulnerable NuGet package is reported, all scripts parse, and the tree is clean.

- [ ] **Step 2: Install and run Gitleaks against the pre-rewrite history**

Run:

```powershell
if (-not (Get-Command gitleaks -ErrorAction SilentlyContinue)) {
    winget install --id Gitleaks.Gitleaks --exact --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) { throw "Gitleaks installation failed" }
}
gitleaks git . --redact --no-banner
if ($LASTEXITCODE -ne 0) { throw "Gitleaks found a potential secret; stop and review it" }
```

Expected: Gitleaks exits zero with no findings. Any finding stops the launch and must be reviewed before continuing.

- [ ] **Step 3: Install `git-filter-repo`**

Run:

```powershell
py -3 -m pip install --user git-filter-repo
if ($LASTEXITCODE -ne 0) { throw "git-filter-repo installation failed" }
py -3 -m git_filter_repo --version
if ($LASTEXITCODE -ne 0) { throw "git-filter-repo is not runnable" }
```

Expected: a `git-filter-repo` version is printed.

- [ ] **Step 4: Create the exact temporary path-rewrite input**

Create `.public-launch-replacements` with:

```text
regex:C:\\Users\\[^\\]+\\Downloads\\pi-signage\\pi-signage==>the ScreenSquire repository root
```

Run:

```powershell
git status --short
```

Expected: only the untracked `.public-launch-replacements` file appears.

- [ ] **Step 5: Rewrite the unpublished history**

Run:

```powershell
py -3 -m git_filter_repo --force --commit-callback 'commit.author_email = b"159793913+Lastonedown86@users.noreply.github.com"; commit.committer_email = b"159793913+Lastonedown86@users.noreply.github.com"' --replace-text .public-launch-replacements
if ($LASTEXITCODE -ne 0) { throw "History rewrite failed; restore from the bundle" }
```

Expected: `git-filter-repo` rewrites `main` successfully. Do not add or commit the temporary file.

- [ ] **Step 6: Delete only the verified temporary input**

Resolve and verify that the file is inside the repository root, then delete it with the file-editing tool:

```powershell
$repo = (git rev-parse --show-toplevel).Trim()
$replacements = (Resolve-Path .public-launch-replacements).Path
if ((Split-Path $replacements -Parent) -ne $repo) { throw "Unexpected replacements path" }
```

Delete exactly `.public-launch-replacements`, then run:

```powershell
if (git status --porcelain) { throw "Working tree is not clean after rewrite" }
```

- [ ] **Step 7: Configure future commits to use the private GitHub email**

Run:

```powershell
git config user.name "William Chapman"
git config user.email "159793913+Lastonedown86@users.noreply.github.com"
if ((git config user.email) -ne "159793913+Lastonedown86@users.noreply.github.com") {
    throw "Repository email configuration failed"
}
```

- [ ] **Step 8: Audit every rewritten commit for personal values**

Run:

```powershell
$privacyPattern = "C:\\Users\\[^\\]+|RECOVERY_PIN=[0-9]{8}|WIFI_(PASSWORD|PSK)="
$privacyFindings = @()
git rev-list --all | ForEach-Object {
    $commit = $_
    $matches = git grep -I -n -E $privacyPattern $commit 2>$null
    if ($LASTEXITCODE -eq 0) { $privacyFindings += $matches }
}
if ($privacyFindings.Count -gt 0) {
    $privacyFindings
    throw "Privacy pattern found in rewritten history"
}
$emails = @(git log --all --format="%ae" | Sort-Object -Unique)
if ($emails.Count -ne 1 -or $emails[0] -ne "159793913+Lastonedown86@users.noreply.github.com") {
    $emails
    throw "Unexpected author email remains in rewritten history"
}
$committerEmails = @(git log --all --format="%ce" | Sort-Object -Unique)
if ($committerEmails.Count -ne 1 -or $committerEmails[0] -ne "159793913+Lastonedown86@users.noreply.github.com") {
    $committerEmails
    throw "Unexpected committer email remains in rewritten history"
}
git fsck --full
if ($LASTEXITCODE -ne 0) { throw "Rewritten repository failed git fsck" }
gitleaks git . --redact --no-banner
if ($LASTEXITCODE -ne 0) { throw "Gitleaks found a secret after rewriting" }
```

Expected: no privacy findings, the only project author email is the GitHub noreply address, `git fsck` succeeds, and Gitleaks exits zero.

- [ ] **Step 9: Repeat the full release gate on the rewritten history**

Repeat Task 4 Step 1 exactly.

Expected: all local validation remains green and the worktree is clean.

---

### Task 5: Create and Validate the Private GitHub Staging Repository

**Files:**
- External create: GitHub repository `Lastonedown86/ScreenSquire`
- Modify local Git metadata: add `origin`

**Interfaces:**
- Consumes: the clean, privacy-audited rewritten `main`.
- Produces: a private GitHub repository containing only `main`, with a successful hosted CI run.

- [ ] **Step 1: Confirm GitHub authentication and name availability**

Run:

```powershell
gh auth status
if ($LASTEXITCODE -ne 0) { throw "GitHub CLI authentication is unavailable" }
gh repo view Lastonedown86/ScreenSquire
if ($LASTEXITCODE -eq 0) { throw "Repository already exists; inspect it instead of overwriting it" }
```

Expected: GitHub authentication succeeds and `gh repo view` reports that the repository does not exist.

- [ ] **Step 2: Create the private staging repository and push only `main`**

Run:

```powershell
if ((git branch --show-current).Trim() -ne "main") { throw "Expected main branch" }
if (git status --porcelain) { throw "Working tree must be clean" }
if ((git branch --format="%(refname:short)" | Measure-Object).Count -ne 1) {
    throw "Only main may exist as a local branch at publication"
}
gh repo create Lastonedown86/ScreenSquire --private --source . --remote origin --push
if ($LASTEXITCODE -ne 0) { throw "Private staging repository creation failed" }
git remote -v
```

Expected: `origin` points to `Lastonedown86/ScreenSquire`, and only `main` is pushed.

- [ ] **Step 3: Verify private staging state**

Run:

```powershell
$repoState = gh repo view Lastonedown86/ScreenSquire --json nameWithOwner,visibility,defaultBranchRef,url
$repoState
if ($repoState -notmatch '"visibility":"PRIVATE"') { throw "Staging repository is not private" }
if ($repoState -notmatch '"name":"main"') { throw "Default branch is not main" }
git ls-remote --heads origin
```

Expected: visibility is `PRIVATE`, the default branch is `main`, and the only remote head is `refs/heads/main`.

- [ ] **Step 4: Wait for the private staging CI run**

Run:

```powershell
$runId = gh run list --repo Lastonedown86/ScreenSquire --workflow ci.yml --branch main --limit 1 --json databaseId --jq ".[0].databaseId"
if (-not $runId) { throw "No CI run found for main" }
gh run watch $runId --repo Lastonedown86/ScreenSquire --exit-status
if ($LASTEXITCODE -ne 0) { throw "Private staging CI failed; keep the repository private" }
gh run view $runId --repo Lastonedown86/ScreenSquire
```

Expected: all three required CI jobs succeed. CodeQL may be skipped while repository visibility is private, as designed.

---

### Task 6: Make the Repository Public and Apply GitHub Guardrails

**Files:**
- Temporary create/delete: `.public-launch-security.json`
- Temporary create/delete: `.public-launch-ruleset.json`
- External modify: GitHub repository visibility, security settings, ruleset, and merge settings

**Interfaces:**
- Consumes: the green private staging repository from Task 5.
- Produces: the guarded public repository with enabled security automation and an active `main` ruleset.

- [ ] **Step 1: Change visibility only after rechecking hosted CI**

Run:

```powershell
$conclusion = gh run list --repo Lastonedown86/ScreenSquire --workflow ci.yml --branch main --limit 1 --json conclusion --jq ".[0].conclusion"
if ($conclusion -ne "success") { throw "Latest main CI is not successful" }
gh repo edit Lastonedown86/ScreenSquire --visibility public --accept-visibility-change-consequences
if ($LASTEXITCODE -ne 0) { throw "Visibility change failed" }
$visibility = gh repo view Lastonedown86/ScreenSquire --json visibility --jq ".visibility"
if ($visibility -ne "PUBLIC") { throw "Repository is not public" }
```

- [ ] **Step 2: Enable repository merge hygiene**

Run:

```powershell
gh repo edit Lastonedown86/ScreenSquire --delete-branch-on-merge --enable-issues
if ($LASTEXITCODE -ne 0) { throw "Repository settings update failed" }
```

- [ ] **Step 3: Enable public-repository security features**

Create `.public-launch-security.json` with:

```json
{
  "security_and_analysis": {
    "advanced_security": { "status": "enabled" },
    "secret_scanning": { "status": "enabled" },
    "secret_scanning_push_protection": { "status": "enabled" }
  }
}
```

Run:

```powershell
gh api --method PATCH repos/Lastonedown86/ScreenSquire --input .public-launch-security.json
if ($LASTEXITCODE -ne 0) { throw "Security settings update failed" }
gh api --method PUT repos/Lastonedown86/ScreenSquire/private-vulnerability-reporting
if ($LASTEXITCODE -ne 0) { throw "Private vulnerability reporting could not be enabled" }
```

- [ ] **Step 4: Run CodeQL now that the repository is public**

Run:

```powershell
gh workflow run codeql.yml --repo Lastonedown86/ScreenSquire --ref main
if ($LASTEXITCODE -ne 0) { throw "Could not dispatch CodeQL" }
Start-Sleep -Seconds 5
$codeqlRun = gh run list --repo Lastonedown86/ScreenSquire --workflow codeql.yml --branch main --limit 1 --json databaseId --jq ".[0].databaseId"
if (-not $codeqlRun) { throw "No CodeQL run found" }
gh run watch $codeqlRun --repo Lastonedown86/ScreenSquire --exit-status
if ($LASTEXITCODE -ne 0) { throw "CodeQL failed" }
```

Expected: both `Analyze (csharp)` and `Analyze (python)` succeed.

- [ ] **Step 5: Create the exact active `main` ruleset**

Create `.public-launch-ruleset.json` with:

```json
{
  "name": "Protect main",
  "target": "branch",
  "enforcement": "active",
  "bypass_actors": [
    {
      "actor_id": 5,
      "actor_type": "RepositoryRole",
      "bypass_mode": "always"
    }
  ],
  "conditions": {
    "ref_name": {
      "include": ["~DEFAULT_BRANCH"],
      "exclude": []
    }
  },
  "rules": [
    { "type": "deletion" },
    { "type": "non_fast_forward" },
    { "type": "required_linear_history" },
    {
      "type": "pull_request",
      "parameters": {
        "dismiss_stale_reviews_on_push": false,
        "require_code_owner_review": false,
        "require_last_push_approval": false,
        "required_approving_review_count": 0,
        "required_review_thread_resolution": true
      }
    },
    {
      "type": "required_status_checks",
      "parameters": {
        "do_not_enforce_on_create": true,
        "strict_required_status_checks_policy": true,
        "required_status_checks": [
          { "context": "Python agent tests" },
          { "context": ".NET tests and Release build" },
          { "context": "Shell and repository checks" },
          { "context": "Dependency review" }
        ]
      }
    }
  ]
}
```

Run:

```powershell
gh api --method POST repos/Lastonedown86/ScreenSquire/rulesets --input .public-launch-ruleset.json
if ($LASTEXITCODE -ne 0) { throw "Main ruleset creation failed" }
```

- [ ] **Step 6: Delete only the two verified temporary configuration files**

Resolve `.public-launch-security.json` and `.public-launch-ruleset.json`, verify both parents equal the repository root, and delete exactly those two files with the file-editing tool. No `.public-launch-*` file may remain.

Run:

```powershell
$repo = (git rev-parse --show-toplevel).Trim()
foreach ($temporaryName in @(".public-launch-security.json", ".public-launch-ruleset.json")) {
    $temporaryPath = (Resolve-Path $temporaryName).Path
    if ((Split-Path $temporaryPath -Parent) -ne $repo) {
        throw "Unexpected temporary file path: $temporaryPath"
    }
}
```

After deleting exactly those two files with the file-editing tool, run:

```powershell
$remaining = @(Get-ChildItem -Force -Filter ".public-launch-*")
if ($remaining.Count -ne 0) { throw "Temporary public-launch files remain" }
if (git status --porcelain) { throw "Working tree changed while configuring GitHub" }
```

- [ ] **Step 7: Verify the live repository configuration**

Run:

```powershell
gh repo view Lastonedown86/ScreenSquire --json nameWithOwner,visibility,defaultBranchRef,url
gh api repos/Lastonedown86/ScreenSquire --jq "{private,visibility,default_branch,delete_branch_on_merge,security_and_analysis}"
gh api repos/Lastonedown86/ScreenSquire/private-vulnerability-reporting
gh api repos/Lastonedown86/ScreenSquire/rulesets --jq ".[] | {id,name,enforcement,target,conditions,rules,bypass_actors}"
gh api repos/Lastonedown86/ScreenSquire/code-scanning/alerts --jq "length"
gh api repos/Lastonedown86/ScreenSquire/dependabot/alerts --jq "length"
git remote get-url origin
```

Expected: the repository is public; `main` is default; delete-on-merge is true; secret scanning and push protection are enabled; private reporting is enabled; `Protect main` is active with the four required checks, PR requirement, linear history, deletion and force-push blocking, and administrator bypass; CodeQL and Dependabot endpoints are available; `origin` is the ScreenSquire URL.

---

### Task 7: Verify the Public Repository from a Clean Clone

**Files:**
- External create: a unique temporary clean-clone directory
- Verify only: all published repository files and commands

**Interfaces:**
- Consumes: the guarded public repository from Task 6.
- Produces: independent evidence that a new contributor can clone, install, test, build, and validate the published project.

- [ ] **Step 1: Clone into a new unique temporary directory**

Run:

```powershell
$verifyRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ScreenSquire-verify-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $verifyRoot | Out-Null
git clone https://github.com/Lastonedown86/ScreenSquire.git $verifyRoot
if ($LASTEXITCODE -ne 0) { throw "Clean clone failed" }
Set-Location $verifyRoot
if ((git branch --show-current).Trim() -ne "main") { throw "Clean clone did not select main" }
if (git status --porcelain) { throw "Clean clone is not clean" }
```

- [ ] **Step 2: Install clean-clone Python dependencies**

Run:

```powershell
py -3.13 -m venv agent\.venv
if ($LASTEXITCODE -ne 0) { throw "Python virtual environment creation failed" }
agent\.venv\Scripts\python.exe -m pip install -r agent\requirements.txt -r agent\requirements-dev.txt
if ($LASTEXITCODE -ne 0) { throw "Python dependency installation failed" }
```

- [ ] **Step 3: Run the clean-clone release gate**

Run:

```powershell
$runtimeFile = "agent\data\dashboard.json"
$before = if (Test-Path $runtimeFile) {
    (Get-FileHash $runtimeFile -Algorithm SHA256).Hash + "|" + (Get-Item $runtimeFile).LastWriteTimeUtc.Ticks
} else { "MISSING" }
agent\.venv\Scripts\python.exe -m pytest agent -q
if ($LASTEXITCODE -ne 0) { throw "Clean-clone Python tests failed" }
$after = if (Test-Path $runtimeFile) {
    (Get-FileHash $runtimeFile -Algorithm SHA256).Hash + "|" + (Get-Item $runtimeFile).LastWriteTimeUtc.Ticks
} else { "MISSING" }
if ($before -ne $after) { throw "Clean-clone tests changed runtime dashboard data" }
dotnet test PiSignage.slnx -c Release
if ($LASTEXITCODE -ne 0) { throw "Clean-clone .NET tests failed" }
dotnet build PiSignage.slnx -c Release
if ($LASTEXITCODE -ne 0) { throw "Clean-clone Release build failed" }
dotnet list PiSignage.slnx package --vulnerable --include-transitive
if ($LASTEXITCODE -ne 0) { throw "Clean-clone NuGet scan failed" }
Get-ChildItem pi-setup -Filter *.sh | ForEach-Object {
    & "C:\Program Files\Git\bin\bash.exe" -n $_.FullName
    if ($LASTEXITCODE -ne 0) { throw "Clean-clone Bash syntax failed: $($_.Name)" }
}
git diff --check
if ($LASTEXITCODE -ne 0) { throw "Clean-clone whitespace check failed" }
if (git status --porcelain) { throw "Tracked clean-clone files changed during verification" }
```

Expected: 127 Python and 123 .NET tests pass, the Release build succeeds, no vulnerable NuGet packages are reported, scripts parse, runtime data stays absent or unchanged, and tracked files remain clean.

- [ ] **Step 4: Verify final hosted checks and repository identity**

Run:

```powershell
gh run list --repo Lastonedown86/ScreenSquire --branch main --limit 10
gh repo view Lastonedown86/ScreenSquire --json nameWithOwner,visibility,defaultBranchRef,url
git remote -v
```

Expected: the latest CI and CodeQL runs are successful, the public repository renders as ScreenSquire, and the clean clone points to the public URL.

- [ ] **Step 5: Record launch completion without deleting recovery evidence**

Record:

- rewritten public `main` commit ID;
- recovery bundle path and SHA-256;
- local release-gate counts;
- private staging CI run URL;
- CodeQL run URL;
- live ruleset ID;
- clean-clone directory and validation results.

Keep `../ScreenSquire-pre-public-2026-07-25.bundle` until the user separately confirms it may be removed. Do not publish or attach the bundle to GitHub.
