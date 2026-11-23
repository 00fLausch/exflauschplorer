# Build Windows packages

Requires Windows x64, Node.js 22.13 or newer and npm, Python 3, the .NET 10 SDK, and Inno Setup 6. In the project folder:

```powershell
Push-Location src/Explorer.Web
npm ci
npm run build
Pop-Location
dotnet publish src/Explorer.Host/Explorer.Host.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
python installer/build-zip.py
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer/EXfLauschPLORER.iss
```

If Inno Setup is installed elsewhere, change the path to `ISCC.exe` accordingly. The packages are then under `artifacts/`. The ZIP variant contains the same setup dialog and installs for the current user; it is not a standalone portable version. Only the EXE installer registers itself in Windows under “Installed apps”.

Release builds disable debug symbols via `Directory.Build.props` so the distributed DLLs do not contain local PDB paths from the build machine.

The UI for documentation images can be started with `node scripts/capture-demo.mjs` in `src/Explorer.Web`. The script uses only synthetic API responses and requires Google Chrome plus, optionally, FFmpeg for the audio demo.