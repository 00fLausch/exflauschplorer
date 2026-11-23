# Contributing to EXfLauschPLORER

Thanks for your interest. EXfLauschPLORER is a Windows tray app and React/TypeScript PWA that shares local files through a private Tailscale Serve route. Contributions should keep the loopback-only server, the Tailscale identity checks, and the share boundaries intact.

## Ways to contribute

- Report a reproducible bug.
- Suggest a focused improvement.
- Improve documentation or tests.
- Submit a pull request.

## Before you start

- Read the [README](README.md), [FEATURES.md](FEATURES.md), and [technical manual](docs/manual.md).
- Search existing issues first.
- For larger changes, open an issue before writing code.
- Keep pull requests focused: one concern per request.
- Never include private data: Tailscale tailnet names, MagicDNS names, pairing links, PINs, private paths, browser profiles, chat logs, or credentials.

## Requirements

- Windows x64.
- .NET 10 SDK.
- Node.js 22.13 or newer and npm.
- Tailscale for the private route.
- Inno Setup 6 only if you build the EXE installer or ZIP package.

## Build and test

```powershell
.\setup.ps1
dotnet test WindowsMobileExplorer.slnx --configuration Release
```

Web app:

```powershell
Set-Location src\Explorer.Web
npm ci
npm run lint
npm run build
npm run test:e2e:install
npm run test:e2e
Set-Location ..\..
```

The WebKit E2E suite starts an isolated local test server with temporary folders and a test profile. It does not use your real shares or tray icon. Some tests create large temporary fixtures; make sure the drive has enough free space.

## Code style

- Keep the server loopback-only and preserve the Tailscale identity checks.
- Keep share roots, read-only mode, system-folder protection, and reparse-point checks intact.
- Keep German UI strings centralized in `src/Explorer.Web/src/locales/de.ts` so labels and formatting stay consistent.
- Keep PowerShell scripts compatible with Windows PowerShell 5.1.
- Do not add public hosting, Funnel, or a second server.
- Do not log PINs, Tailscale identities, pairing links, or private paths.

## Tests

- Run the .NET suite and the web lint/build before submitting.
- Add a regression test for every bug fix.
- UI changes should include a screenshot or a short note in the pull request.
- Tests must not require your real shares, a paired iPhone, or a production Tailscale route.

## Pull requests

- Branch from `main`.
- Describe the change, the tests you ran, and any remaining limits.
- Link the related issue.
- Keep unrelated formatting out of the diff.
- By contributing, you agree to license your contribution under the [MIT License](LICENSE).

## Security

Do not report vulnerabilities in public issues. See [SECURITY.md](SECURITY.md).

## Questions

Open a GitHub issue with the bug-report form or a focused question. Read [docs/manual.md](docs/manual.md) first.