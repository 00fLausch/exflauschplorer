# Windows file explorer for iPhone and Tailscale

## PDF preview

PDF files are displayed page by page with PDF.js on the mobile device. The preview offers page navigation, direct page entry, zoom, and password entry. Data comes through the protected stream; large documents use partial requests. PDF.js and the required font, CMap, color profile, and decoder files are included in the app build. If a display problem occurs, “Open in browser” and “Load” are available. Text selection, search within the document, and interactive form editing are not implemented in this preview. According to the project, the PDF.js legacy build supports Safari 18 and later; on older devices, the browser or download alternative may be necessary.

A Windows tray program provides a German-language, installable PWA. The HTTP server binds exclusively to `127.0.0.1`; Tailscale Serve handles HTTPS and sets the Tailscale identity headers. The app runs in the logged-in Windows session, starts via HKCU autostart, and does not require administrator rights.

*The app and installer UI are currently German; UI labels in this manual are translated for readability.*

## Architecture plan

- **Backend:** .NET 10 / ASP.NET Core on Windows; tray host with WinForms and access to the Windows shell, recycle bin, known folders, filesystem, ACLs, and shell thumbnails. Port `5180`, loopback only.
- **Frontend:** React/TypeScript PWA with a `standalone` manifest, safe-area insets, light/dark mode, and a central German UI text collection in `src/Explorer.Web/src/locales/de.ts`; dynamic messages and date, number, and size formatting also run through this language file.
- **Access protection:** Tailscale Serve over MagicDNS HTTPS, `Tailscale-User-Login` check against `AllowedLogins`, shared roots, reparse-point checks, read-only system paths, and CSRF headers.
- **Data model:** `ExplorerSettings` in `settings.json`; `FileEntryDto`, `DriveInfoDto`, `JobSnapshot`, `UploadSessionDto`, `TextDocumentDto`, and property DTOs. Running jobs live in `jobs.json`; resumable uploads have separate staging and JSON metadata files.
- **Jobs:** two workers independent of the browser; persistent status, progress, conflict decision, pause/cancel, and undo records. Copies first write to a hidden `.partial` file in the destination folder and move it after successful completion.
- **API:** read `GET /api/v1/health`, `/settings/shares`, `/drives`, `/known-folders`, `/quick-access`, `/windows-recent`, `/entries`, `/shortcuts/resolve`, `/stream`, `/thumbnail`, `/archive`, `/properties`, `/text`, `/search`, `/jobs`, `/uploads`, `/recycle-bin`; events via `/watch`, `/jobs/events`, and SSE search. Write `PUT /api/v1/settings/shares` and `POST/PUT/DELETE` for file operations, `/jobs`, `/undo`, `/uploads`, `/text`, `/properties/attributes`, `/open`, and `/recycle-bin`.

**Why .NET 10 instead of .NET 8:** Both offer Windows shell and ASP.NET Core access. .NET 8 support ends on November 10, 2026; .NET 10 LTS is supported until November 2028. This keeps new installations on a supported base longer. [Microsoft: Releases and support](https://learn.microsoft.com/dotnet/core/releases-and-support).

### Project structure

```text
src/Explorer.Core/       Path normalization, share boundaries, DTOs, German errors
src/Explorer.Host/       WinForms tray, ASP.NET Core API, Windows shell services
src/Explorer.Web/        React/TypeScript PWA, German texts, and WebKit E2E
tests/Explorer.Core.Tests/       Path and security tests
tests/Explorer.Host.Tests/       Windows integration tests, jobs, and shell
scripts/                 Range check and supporting Windows scripts
docs/verification/       Documented and rendered iPhone viewport checks
```

### API groups

- `GET /api/v1/health`, `/drives`, `/known-folders`, `/quick-access`, `/windows-recent`, `/favorites`; `PUT /favorites`; `GET /settings/shares` shows the shares.
- `GET /entries`, `/stream`, `/thumbnail`, `/archive`, `/shortcuts/resolve`; `POST /open`.
- `GET /jobs`, `/jobs/events`, `/watch`; `POST /jobs`, `/jobs/{id}/pause`, `/resume`, `/cancel`, `/conflict`, `/undo`.
- `GET/POST /uploads`, `PUT /uploads/{id}/chunk`, `POST /uploads/{id}/commit`, `DELETE /uploads/{id}`.
- `GET /search` streams results; `GET /properties`, `POST /properties/attributes`, `GET/PUT /text`.
- `GET /recycle-bin`, `POST /recycle-bin/{id}/restore`, `/delete`, `/empty`; additional PIN routes under `/security/pin`.

All API paths carry the `/api/v1` prefix. Write access requires the Tailscale allowlist, the same origin, and the PWA request header.

## Installation

1. Install and connect Tailscale on the Windows PC. In the admin console, **Network → DNS** must have MagicDNS and **HTTPS Certificates** enabled. Setup configures `tailscale serve` for the PC. If Tailscale asks for consent for this machine, setup shows the admin link and exits after at most ten seconds. Open the link, sign in, enable Serve for the PC, and run setup again.
2. Open PowerShell as a normal user in the project folder and run:

   ```powershell
   .\setup.ps1
   ```

   The script detects the current Tailscale login if possible. Without an existing publish folder, it builds the PWA and publishes the Windows host itself. On the first run, it shares your user folder by default. You can choose specific folders, set read-only separately, or share all drives. `C:\Windows` and `Program Files` remain write-protected by default.
3. Setup installs to `%LOCALAPPDATA%\Programs\WindowsMobileExplorer`, creates `%LOCALAPPDATA%\WindowsMobileExplorer\settings.json`, configures HKCU autostart, and configures `tailscale serve` as a proxy to `http://127.0.0.1:5180`. It keeps an active saved HTTPS port; otherwise it searches for a free port starting at `8443`. It never calls `tailscale funnel` and does not reset existing serve configurations.
4. Connect Tailscale on the iPhone and open the printed HTTPS MagicDNS address in Safari. Tap **Share** (on some Safari layouts first **Page menu → Share**), scroll to **Add to Home Screen**, and tap it. If the action is missing, add it via **Edit actions**. Enable **Open as web app** and tap **Add**. The PWA then gets an icon and starts in app mode. See also [Apple: Turn a website into an app on iPhone](https://support.apple.com/de-ch/guide/iphone/iphea86e5236/27/ios/27).

If an existing build folder is missing, setup needs Node.js/npm and the .NET 10 SDK. The build is self-contained (`win-x64`); the target PC does not need a separate .NET runtime.

Options:

```powershell
.\setup.ps1 -ShareAllDrives $false -ShareRoot 'C:\Users\Name\Documents','D:\Projects' -ReadOnly
.\setup.ps1 -AllowedLogin 'name@example.com'
```

For an update from changed source code, use `.\setup.ps1 -Rebuild`. Before an update, setup checks that no file operation or upload is active, then stops the tray app to replace the program files and starts it again afterward. Already shared Tailscale logins are preserved.

ZIP files are created and extracted with .NET. `.7z` archives can be extracted if 7-Zip is installed under `C:\Program Files\7-Zip` or `C:\Program Files (x86)\7-Zip`; the program is started without a shell call and writes archive files only individually to a checked temporary target folder.

**Share** opens the native iPhone share menu with the file itself for supported files up to 32 MB. Larger files and folders (as a streamed ZIP) are downloaded; you can then select them in the Files app and share from there. If Safari rejects the first handoff because the user action expired, a tappable share retry appears. If that is also rejected, the download starts as a fallback.

Under **More → Settings → File shares**, the PWA shows the shared folders and write protection. Make changes on the Windows PC with the app stopped in `settings.json`. Use **Open configuration** in the notification area to find the file. The home page shows Windows Quick Access folders if they are within the configured shares; the PWA cannot change Windows pins. App favorites are stored on the PC per Tailscale login account and displayed synchronized on every device. Already locally saved folder favorites from an older PWA version are migrated once after checking against the share. Only shared folder paths are stored. Under **Settings → Text size**, you can set the font between 100 and 200 percent. On iPhone and iPad, the iOS system text size is the base; the slider enlarges it additionally. Share settings are read when the Windows host starts. Text size remains stored locally on the respective mobile device. `AllowedLogins`, ports, and other host settings remain a local configuration and are changed in `settings.json` with the app stopped. When setup runs again, an existing configuration is backed up with a timestamp. For additional shared Tailscale logins, add `AllowedLogins` manually; the identity must match `Tailscale-User-Login` exactly. In the PWA under **More → Set up PIN**, you can optionally set an additional 6- to 12-digit device PIN. The server stores only a PBKDF2-SHA-256 hash, locks for 15 minutes after five incorrect PINs, and ends sessions after eight hours. **More → Lock app now** forces PIN entry again; changing or turning it off is possible after unlocking. Face ID/Passkey is not implemented.

Folder navigation remains visible on the left from 900 px and is available on smaller screens via **Open navigation**. It contains **This PC**, Windows standard folders, existing Windows Quick Access items, app favorites, and drives. **This PC** is also always available from the top bar and shows drives with drive letters first. The Windows read-only attribute of a folder does not count as a write lock; actual share locks still apply. Individual entries with unreadable metadata (for example, invalid Windows device names) do not block the rest of the folder list.

Music and video open in the integrated streaming player. Files remain on the PC; the player uses the protected stream with HTTP Range access for seeking. **Minimize player** turns it into a floating display while folder navigation and file menus remain usable. The mini player automatically keeps its distance from folder actions, the selection bar, and the bottom navigation, including with larger text and safe areas. Use **Add to queue** to add tracks; folders can be added completely and without subfolders via the play icon or their action menu. While the player is running, **Add all** adds the tracks of the current folder without changing the current track or its position. The queue applies to the current session.

For long sets, the player shows a real waveform of the entire track. Tapping seeks to a position; dragging and moving upward at the same time allows finer seeking. A timeline, 10/30-second skips, track changes, repeat, shuffle, and playback speed are also available. The waveform is calculated and cached with FFmpeg/FFprobe on the PC; the normal timeline already works while it is being calculated. The service finds existing WinGet installations, programs in PATH, bundled `tools/ffmpeg/bin`, or the explicit environment variables `WINDOWS_MOBILE_EXPLORER_FFMPEG` and `WINDOWS_MOBILE_EXPLORER_FFPROBE`. Video supports device-dependent fullscreen and picture-in-picture; device control runs through Media Session. The device must support the audio/video codec itself; playback transcoding is not implemented. The PC and Tailscale connection must remain reachable.

## Configuration and security

For an update from an older version, run setup again. It adopts the MagicDNS name and preserves existing shares. **Show in Explorer** opens the location on the PC. Programs and scripts cannot be started through the web app.

The recycle bin shows entries from the shares. Restore, permanent delete, and empty require a writable share for the original path. Entire drives and share roots are protected against move, rename, and delete. Deleting and renaming symbolic links and junctions affect the respective entry.

ZIP downloads process at most 10,000 entries and 4 GB of source data. Extraction allows at most 10,000 entries and 20 GB, plus limits based on compression ratio and free space. Interrupted uploads can be resumed for up to seven days after the last activity. On completion, the app compares the checksum with the local file. Chunks are stored in the app data folder when the destination and app data folder are on the same drive. On other drives, they remain at the destination.

The PWA remembers the last opened folder locally and checks it again against the current server share on the next start. If the folder is no longer shared or reachable, **This PC** opens and the saved position is discarded.

`ShareAllDrives` shares drives for the logged-in Windows user. Alternatively, individual directories can be listed in `Roots` with a `ReadOnly` flag. Share changes require a host restart. `ReadOnlyMode` blocks all write operations; `ProtectSystemFolders` keeps Windows and Program Files write-protected by default, even if a parent folder is shared. The normal NTFS permissions of this Windows user still apply. App data and program files, common browser profiles, SSH and GnuPG keys, known autostart folders, and the internal recycle-bin files remain excluded from file sharing.

Kestrel binds to loopback. The app accepts only the MagicDNS name and HTTPS port stored during setup, a loopback connection from the local Tailscale service, and a shared Tailscale identity. For local status queries, setup uses a separate key from the private data folder. API write access additionally requires a same-origin check and `X-Explorer-Request`. The PWA does not store file or API responses in its offline cache. Write operations are logged as JSONL under `%LOCALAPPDATA%\WindowsMobileExplorer\logs` with separate JSON fields and rotated; job and upload status live in the data folder.

Deleting to the Windows recycle bin runs on local media recognized by Windows as fixed drives. On UNC network paths, network drives, and USB drives recognized as removable, the default delete action is rejected before every shell call because Windows does not reliably provide a local recycle bin there. The file remains intact; permanent deletion requires the explicitly confirmed **Delete permanently** action.

The optional PIN protection supplements the Tailscale identity. Even with a PIN, the PWA remains reachable only through the Tailscale allowlist; server file access still uses the Windows rights of the logged-in user.

### Optional Everything search

If Everything and its ES command-line program are installed, recursive filename search prefers `es.exe`. Place `es.exe` next to `Everything.exe` or next to `Explorer.Host.exe`; content searches continue to use Windows Search or the filesystem fallback. ES must be running according to the [voidtools documentation](https://www.voidtools.com/support/everything/command_line_interface/). Without ES, the default search remains unchanged.

## Development and tests

```powershell
cd src\Explorer.Web
npm ci
npm run build
cd ..\..
.\.tools\dotnet\dotnet.exe test WindowsMobileExplorer.slnx --no-restore
cd src\Explorer.Web
npm run test:e2e:install
npm run test:e2e
```

The E2E host uses an isolated test recycle bin. Playwright's global teardown stops the host PID started by the test and waits for cleanup; only specifically created, randomly named `%TEMP%` folders are removed. A successful E2E run leaves neither `.runtime.json` nor its current 50,000-file fixture.

The WebKit E2E check starts a separate local test server with unique `%TEMP%` folders and a test profile; it uses neither the real share setup nor the tray icon or the user's Windows Recent folder. It checks navigation including an isolated Windows Recent shortcut, adding and removing shares, their immediate lockout effect, per-folder write protection, protection of critical system folders, the separate open and action menu with 44×44 touch targets, accessible names and touch sizes for visible controls at 320 px, long press and swipe to open the Share/Rename/Recycle bin actions, the tappable retry after a simulated Safari rejection, and the file passed to the native share API, focus return and Escape close. For dialogs, it checks focus guidance, focus return, Escape, properties above the preview, file-name entry, and protection against accidentally closing unsaved editor changes; it also tests simulated pinch zoom, edit/save, system settings, filters, grouping, PIN setup/lock/wrong PIN/unlock, and a folder with 50,000 files and virtualization down to 320 px width. A separate upload E2E test lets an 8 MB chunk response get lost after successful server storage and checks automatic offset reconciliation, reconnection, and the finished file. Integration tests measure the first page of this folder, check Windows `.lnk` targets including the share boundary and the Windows Recent history with share filtering and deduplication, `\\?\` normalization, and copying and renaming a path longer than 260 characters, a 2 GB upload with resume after a simulated service restart, extracting a `.7z` archive with Unicode paths (if 7-Zip is installed), and create 10,000 small files exclusively in a separate `%TEMP%` folder, observe the progress of a copy job, cancel it, and check that no `.partial` file remains. A separate Windows shell test deletes two uniquely named files from a separate `%TEMP%` folder into the Windows recycle bin, checks listing and restore by ID and undo path, and then cleans up only its own test files. All other integration tests use an isolated test recycle bin. The installed host additionally listed and streamed a 337-character file via HTTP. A property test checks the clean fallback when no shell metadata is available for invalid test media.

The Markdown E2E check verifies formatted headings and lists, loads relative images only through the protected local stream endpoint, and checks that embedded scripts are not executed, raw HTML images including `srcset` are removed, and no external image requests are triggered.

The separate PWA installation E2E test checks web manifest fields and icon files, the Apple touch icon and iOS metadata, `viewport-fit=cover`, safe-area variables, and an active service worker in the iPhone WebKit context.

Another iPhone WebKit E2E test creates a PNG file on Windows, checks Windows shell thumbnails for PNG, MP4, and PDF plus display in tile view, decodes synthetic MP4 and MP3 fixtures, seeks to the audio track by touching the native timeline, and checks media range responses from the protected stream endpoint. A custom-built, two-hour 5 GB sparse H.264 MP4 with repeated valid GOPs is decoded in the WebKit viewport and sought to at 10, 30, 75, and 5,400 seconds; the last seek returns a server-side HTTP 206 range response at about byte 3.73 GB. An additional range request at byte 3.75 GB must also return HTTP 206. A real HEIC file can optionally be included in the same test via `WINDOWS_MOBILE_EXPLORER_HEIC_FIXTURE`. It also saves three iPhone viewport images to `docs/verification/iphone-viewport/` and checks the overwide share card and accessible, at least 44 px tall, horizontally scrollable breadcrumb buttons. This is a Playwright WebKit test; home-screen installation, VoiceOver, real safe areas, and network switching need a connected iPhone.

For an additional HEIC shell thumbnail test, you can specify your own `.heic` test file locally. The `example.heic` from the [libheif example directory](https://github.com/strukturag/libheif/tree/master/examples) is also optionally available. The test copies the file into the isolated `%TEMP%` test folder and deletes the copy with the rest of the fixture:

```powershell
Set-Location .\src\Explorer.Web
$env:WINDOWS_MOBILE_EXPLORER_HEIC_FIXTURE = 'C:\path\to\example.heic'
npm run test:e2e
Remove-Item Env:\WINDOWS_MOBILE_EXPLORER_HEIC_FIXTURE
```

With the installed app running, `scripts\test-5gb-range.ps1` checks HTTP range access at the beginning and end of a 5 GB sparse file through Tailscale Serve. Without parameters, the script uses the HTTPS address stored during setup. You can also specify the address explicitly.

```powershell
.\scripts\test-5gb-range.ps1 -BaseUrl 'https://<host>.<tailnet>.ts.net:8443'
```

The script checks the MagicDNS name of the PC and the configured HTTPS port. The test requires a share for `%TEMP%` and a test installation without a PIN. The test file is in `%TEMP%` and is removed afterward. This PowerShell script checks the HTTP transport. The separate Playwright WebKit check also decodes and searches the 5 GB MP4; real Safari behavior on the iPhone remains unproven.

The WebKit E2E test also checks that selection mode shows the bottom action bar even with zero marked entries, tapping toggles individual files, and “Select all” captures 50,000 entries across all pages. The bar remains above the bottom navigation and within a 320 px iPhone viewport.

## Troubleshooting

- **“Tailscale is currently not connected”:** Start Tailscale, sign in to the PC in the desired tailnet, and run setup again.
- **HTTPS consent required:** Tailscale Serve shows the missing tailnet step or a confirmation page. Enable MagicDNS HTTPS in the tailnet and run `setup.ps1` again.
- **Serve consent link returns 404:** Open **Network → DNS → HTTPS Certificates → Enable HTTPS…** in the admin console. Check the tailnet DNS name before enabling so HTTPS is turned on in the same tailnet as the PC. Then run the serve command again.
- **Drive does not respond:** Folder queries abort after 20 seconds with a clear message. Reconnect the network drive or plug in the USB drive and refresh the folder.
- **Access denied:** Check the Tailscale account in `AllowedLogins`; check the share path and Windows ACLs.
- **Serve port in use:** Setup keeps a suitable saved port or automatically chooses a free port starting at `8443`; the current port is in `settings.json` and the tray menu address.
- **Restart the app:** Tray icon → Quit, then start from the Start menu or HKCU autostart.
- **Logs:** `%LOCALAPPDATA%\WindowsMobileExplorer\logs`.

## Implementation status

The complete feature list with limitations and open points is in [FEATURES.md](../FEATURES.md). It distinguishes tested features from implementations that still need to be checked on real iPhone/Windows hardware or with large media.