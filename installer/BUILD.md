# Windows-Pakete bauen

Benötigt werden Windows x64, Node.js/npm, Python 3, das .NET-10-SDK und Inno Setup 6. Im Projektordner:

```powershell
Push-Location src/Explorer.Web
npm ci
npm run build
Pop-Location
dotnet publish src/Explorer.Host/Explorer.Host.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
python installer/build-zip.py
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer/EXfLauschPLORER.iss
```

Falls Inno Setup an einem anderen Ort installiert ist, den Pfad zu `ISCC.exe` entsprechend ändern. Die Pakete liegen anschließend unter `artifacts/`. Die ZIP-Variante enthält den gleichen Einrichtungsdialog und installiert für den aktuellen Benutzer; sie ist keine eigenständige portable Version. Nur der EXE-Installer registriert sich in Windows unter „Installierte Apps“.

Die Oberfläche für Dokumentationsbilder lässt sich mit `node scripts/capture-demo.mjs` in `src/Explorer.Web` starten. Das Skript verwendet ausschließlich synthetische API-Antworten und benötigt Google Chrome sowie optional FFmpeg für die Audiodemo.
