# Security policy

## Reporting a vulnerability

Please report vulnerabilities **privately**, not as a public issue. Use GitHub private vulnerability reporting:
<https://github.com/jrose462/Optimus-Vibe/security/advisories/new>.

## Scope

In scope: the app and CLI (`OptimusVibe.exe`, `optimus-vibe.exe`), the updater, config / Steam / registry editing, the
bug-report sanitizer (anything that would leak personal data into a public issue), and the CI and Claude workflows in
`.github/` (for example prompt injection through issue text that could make automation do something it should not, secret
exposure, or workflow permission problems).

Out of scope: vulnerabilities in third-party games, Steam, NVIDIA/AMD drivers or Windows itself.

## Known and intentional

The app contains a built-in **issues-only token** so people without a GitHub sign-in can file a bug report. It can only create
and read issues on this public repository, which anyone with a GitHub account can already do. It is deliberately not secret.
Please do not report its presence as a vulnerability. Abuse of it (spam) is handled by revoking and rotating it.

Pull requests created by Claude are never merged automatically; the maintainer reviews and merges them (branch protection).
