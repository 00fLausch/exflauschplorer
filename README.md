<img src="src/Explorer.Web/public/pwa-192.png" width="96" alt="EXfLauschPLORER app icon">

# EXfLauschPLORER

**Your Windows PC. Your files. On your phone — even on the go.**

You know the moment: you’re on the road, and the file you need is sitting on your Windows PC at home. The usual options are clunky, limited, or both.

EXfLauschPLORER is the free, private, encrypted alternative: your own PC, reachable from your phone over Tailscale, with the drives, folders, and familiar Explorer structure you already know. Stream music and video, preview documents and images, and move files both ways — no subscription, no public file cloud, no second desktop to learn. It’s remote access that feels like your own Explorer, not a screen mirror.

On the phone, the UI runs as a web app in the browser or like an app from the home screen; the Windows PC provides the files.

*The app and installer UI are currently German; menu labels in this README are translated for readability.*

| System at a glance | Files on the go | Music from your PC |
|:---:|:---:|:---:|
| ![System overview with drives](docs/screenshots/01-system.png) | ![File view with DJ sets](docs/screenshots/02-files.png) | ![Player with waveform](docs/screenshots/03-player.png) |

*The screenshots show the original UI with entirely fictional demo drives, folders, and titles. The waveform is a demo data set too.*

## What you can do

- **Browse your PC:** Open drives, Windows Quick Access, important folders, and favorites directly on your phone; search, sort, and switch between views.
- **Manage files:** Copy, move, rename, upload and download, and use the recycle bin and properties. The actions actually available depend on Windows permissions and your shares.
- **Read PDFs:** Open multi-page documents directly in the app, switch pages, and zoom; password-protected PDFs can be displayed after entering the password.
- **Stream music and video:** Media stays on the PC. The player offers a queue, a floating mini view, a timeline, and seeks. For audio files, a waveform can help with seeking.
- **Find space hogs:** Clean mode lists supported cache and temporary files for selection with details. Nothing is deleted until you explicitly select it and confirm in the app.
- **Connect privately:** Tailscale Serve provides access within your tailnet; the local app server listens only on the PC. A login allowlist and an optional PIN add access protection.
- **Stay available:** Autostart and a watchdog for unexpected crashes can be enabled separately during setup.

## Quick start

### 1. Set up the Windows PC

1. Download the current **Windows installer** from the [Releases](https://github.com/00fLausch/exflauschplorer/releases/latest) and run it as a normal Windows user. A ZIP package is also available there: extract it completely and open `ZIP-starten.cmd`.
2. The setup dialog checks whether Tailscale is installed and connected. If needed, the links lead to the [Windows installation](https://tailscale.com/download/windows) and the [Tailscale help](https://tailscale.com/docs/install). Sign in and check again.
3. Choose shares, autostart, and the crash watchdog. New installations share the user folder by default. Existing shares are preserved if the selection is unchanged. Make further changes on the Windows PC via “Open configuration” in the notification area.
4. Start setup. It configures the local service and Tailscale Serve and shows your personal HTTPS address. If Tailscale asks for consent to use Serve, follow the displayed link and run setup again. MagicDNS and HTTPS must be available in the tailnet.

The installer includes the required .NET runtime; Node.js and the .NET SDK are not needed for the finished packages. The app does not require administrator rights and installs for the current Windows user. Tailscale itself may require elevated rights for its installation.

### 2. Connect your iPhone or iPad

1. Install [Tailscale for iOS](https://tailscale.com/download/ios), sign in to the same tailnet, and enable the connection. An [Android app](https://tailscale.com/download/android) is also available.
2. Open the HTTPS address shown by setup on the device in Safari. You can also copy the address from the app icon in the Windows notification area.
3. Optional: choose **Share → Add to Home Screen**. EXfLauschPLORER then opens like its own app.

The PC must be turned on and signed in, and both devices must be able to reach Tailscale. The UI runs on the mobile device; the files and server remain on the PC.

## Good to know

- Playback depends on the audio and video formats your device's browser supports. Conversion during playback is not built in.
- The audio waveform requires **FFmpeg and FFprobe on the PC**. Without them, the normal timeline remains usable. See the [technical manual](docs/manual.md).
- The PDF preview uses [Mozilla PDF.js](https://mozilla.github.io/pdf.js/) and displays one page at a time. Fillable forms and text selection are not supported in this preview; open the file in a suitable PDF app to edit it.
- The watchdog restarts the app after an unexpected process exit and limits repeated restarts. A regular **Quit** from the tray menu remains a quit; with autostart active, the app starts again at the next Windows login. The watchdog does not detect an app that is hung but still running.
- Shares apply within the Windows rights of the logged-in user. Browser profiles, autostart, SSH keys, and the app's data folder are blocked from file access. You can view your shares in the web app.
- Uninstalling removes the program and autostart. Settings and an existing Tailscale Serve route remain; such a route can be removed separately in Tailscale if needed.
- The source code and releases are publicly available. Access to a running app goes through your own Tailscale network.

## Build from source

Requirements: Windows x64, Node.js 22.13 or newer and npm, the .NET 10 SDK, and Tailscale. In the project folder:

```powershell
.\setup.ps1
```

The script builds the frontend and the Windows host if needed. Creating the installer and ZIP additionally requires Inno Setup; see the [build guide](installer/BUILD.md). Development details, security, API, and tests are in the [technical manual](docs/manual.md).

## Project status

The detailed status matrix, including limitations and open hardware/iOS checks, is in [FEATURES.md](FEATURES.md). “Done” means implemented and covered by the corresponding automated test; hardware, iOS, and media benchmarks are marked separately as not verified. Version history is in [CHANGELOG.md](CHANGELOG.md).

## How it works

```text
iPhone / Android browser or home-screen app
                  │
            Tailscale HTTPS
                  │
        Windows PC: Tailscale Serve
                  │
        local EXfLauschPLORER server
                  │
       shared Windows files
```

The server runs in the logged-in Windows session. Tailscale Serve brokers the connection; the app checks the Tailscale identity and its shares. The server listens on `127.0.0.1`, not directly on the local network.

## Contributing

Bug reports and feature ideas are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) and use the [bug-report form](.github/ISSUE_TEMPLATE/bug_report.yml). For security issues, see [SECURITY.md](SECURITY.md) — do not open a public issue.

## License

EXfLauschPLORER is licensed under the [MIT License](LICENSE). Dependencies retain their own licenses.