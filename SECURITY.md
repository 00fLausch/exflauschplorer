# Security policy

## Supported versions

| Version | Supported |
| --- | --- |
| 0.1.x | Yes |
| < 0.1 | No |

## Reporting a vulnerability

Please report privately through GitHub Security Advisories:

<https://github.com/00fLausch/exflauschplorer/security/advisories/new>

Do not open a public issue, pull request, or discussion for a vulnerability.

Include:

- EXfLauschPLORER version and commit.
- Windows version, Tailscale version, and installation mode (EXE, ZIP, or source).
- Steps to reproduce and the impact.
- Relevant logs, with Tailscale tailnet names, MagicDNS names, pairing links, PINs, private paths, and browser-profile data removed.
- Any suggested mitigation.

I aim to acknowledge reports within a few working days. Fix timing depends on severity and available time; this is a volunteer-maintained project. Please allow a reasonable disclosure window before publishing details.

## Scope

In scope:

- Tailscale identity checks, login allowlist, Serve configuration, and Funnel exclusion.
- Loopback binding and request-trust checks (host, origin, and remote program starts).
- Path/share policy, reparse-point handling, write protection, and protected system folders.
- PIN protection and session handling.
- Upload, download, archive, recycle-bin, and text-editor paths.
- XSS or HTML injection in the PWA.
- Leakage of private data in source, docs, or release assets.
- Installer and PowerShell setup/uninstall paths.

Out of scope:

- Vulnerabilities in Windows, Tailscale, .NET, ASP.NET Core, React, PDF.js, or other upstream dependencies. Report those to their maintainers.
- The user's own tailnet, device pairing links, or physical device security.
- Social engineering, phishing, or attacks that require an already compromised Windows account.
- Findings from automated scanners without a reproducible impact.
- Missing hardening that is documented as a limit.

## No bounty

There is no bug bounty. Fixes are released as time permits.

## Disclosure

Please coordinate disclosure. A public report should wait until a fix or mitigation is available. Credit is welcome if you want it.