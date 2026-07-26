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
