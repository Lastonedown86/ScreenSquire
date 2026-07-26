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
