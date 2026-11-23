# Changelog

All notable changes to EXfLauschPLORER are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Public distribution preparation: release builds exclude machine-specific PDB paths, and the Windows package build is documented.
- Access-control hardening: requests are accepted only on the configured Tailscale HTTPS endpoint, from the local Tailscale service or a shared identity; host, origin, and remote program starts are validated; concurrent search, watch, and job streams are capped.
- Entry-mutation safety: links remain entries; replacing share roots or protected parents is refused; file or folder names that would hide internal `.wme` state are rejected. Recycle bin, archives, editor, clean mode, uploads, and jobs were reviewed; expired upload sessions are cleaned up in the background.
- New regression coverage: `HttpSecurityRegressionTests`, `SecurityReviewRegressionTests`, `EntryMutationSafetyTests`, and further tests.
- English documentation, MIT license, changelog, contributing guide, security policy, and a bug-report issue form.

### Changed

- README, feature-status matrix, technical manual, and build guide translated to English.
- Screenshot file renamed to `02-files.png`.

### Fixed

- Release builds no longer contain machine-specific PDB paths.
- Setup and uninstall scripts were updated for the new request-trust checks.

## [0.1.1] - 2026-11-22

### Added

- Reliable mobile PDF preview: page navigation, direct page entry, zoom, password entry, protected stream access, and HTTP Range support.
- PDF.js assets and E2E coverage for the PDF viewer, PWA metadata, and password-protected PDFs.

## [0.1.0] - 2026-11-22

### Added

- Initial EXfLauschPLORER release: Windows tray host, Tailscale Serve setup, file browsing, shares, background jobs, uploads, media streaming, recycle bin, search, properties, PIN protection, and the React/TypeScript PWA.

### Fixed

- Setup preserves existing settings.
- Setup verifies the Tailscale route.
- Uninstall waits for processes to stop.

[Unreleased]: https://github.com/00fLausch/exflauschplorer/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/00fLausch/exflauschplorer/releases/tag/v0.1.1
[0.1.0]: https://github.com/00fLausch/exflauschplorer/releases/tag/v0.1.0