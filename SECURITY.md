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

Security fixes ship as tagged releases (`vYYYY.MM.DD.N`) built by
`.github/workflows/release.yml`. Only the most recent release is supported.

The Windows control app updates itself from those releases. Deployed Raspberry
Pis are still not managed by this repository: a Display Pi accepts software only
from its paired Controller laptop over the signed `/api/update` channel, and
never downloads anything from the internet.

## Security boundaries

Controller authentication provides request integrity, ownership, and replay
protection. It does not provide transport encryption. Use ScreenSquire only on
the intended physical USB connection and trusted store LAN.

### Application updates

The control app downloads and then executes a binary, so the trust chain is
stated plainly rather than implied:

- The trust anchor is TLS to GitHub plus the fact that only the repository owner
  can publish a release. This is the same anchor as a builder downloading the
  executable by hand, so automatic updating does not lower the bar — but it does
  not raise it either.
- `SHA256SUMS.txt` is served from the same origin as the executable. It protects
  against a corrupted, truncated, or partially written download, and against
  tampering with the staged file between download and installation. **It is not
  an origin signature and does not protect against a compromised repository.**
- Before anything is installed, the app requires the asset URL to be HTTPS on a
  GitHub host, the asset name to be exactly `PiSignageControl.exe`, the SHA-256
  to match, the embedded file version to be newer than the running one, and the
  downloaded build to start and exit cleanly.
- **There is no code-signing certificate.** A release-signing key held in CI
  secrets would add nothing, because anyone who can run the workflow can use the
  key; it would only help if the key were held offline and applied by hand.
- Consequently, compromise of this repository is equivalent to compromise of the
  Controller laptop. The mitigations are account 2FA, branch protection, and not
  granting `contents: write` to third-party actions.
- A build that was not produced by the release workflow reports version `0.0.0`
  and never updates itself.
