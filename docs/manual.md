# Windows Datei-Explorer für iPhone und Tailscale

## PDF-Vorschau

PDF-Dateien werden mit PDF.js auf dem Mobilgerät seitenweise angezeigt. Die Vorschau bietet Seitenwechsel, direkte Seiteneingabe, Zoom und Passwortabfrage. Daten kommen über den geschützten Stream; bei großen Dokumenten werden Teilanfragen genutzt. PDF.js und benötigte Schrift-, CMap-, Farbprofil- und Decoderdateien liegen im App-Build. Bei einem Anzeigeproblem stehen „Im Browser öffnen“ und „Laden“ zur Verfügung. Textauswahl, Suche im Dokument und interaktive Formularbearbeitung sind in dieser Vorschau nicht implementiert. Der PDF.js-Legacy-Build unterstützt laut Projektangaben Safari ab Version 18; auf älteren Geräten kann die Browser- oder Download-Alternative notwendig sein.

Ein Windows-Trayprogramm stellt eine deutschsprachige, installierbare PWA bereit. Der HTTP-Server bindet ausschließlich an `127.0.0.1`; Tailscale Serve übernimmt HTTPS und setzt die Tailscale-Identitätsheader. Die App läuft in der angemeldeten Windows-Sitzung, startet über den HKCU-Autostart und benötigt keine Administratorrechte.

## Architekturplan

- **Backend:** .NET 10 / ASP.NET Core auf Windows; Tray-Host mit WinForms und Zugriff auf Windows Shell, Papierkorb, bekannte Ordner, Dateisystem, ACLs und Shell-Miniaturen. Port `5180`, ausschließlich Loopback.
- **Frontend:** React/TypeScript-PWA mit `standalone`-Manifest, Safe-Area-Abständen, Hell-/Dunkelmodus und zentraler deutscher UI-Textsammlung in `src/Explorer.Web/src/locales/de.ts`; dynamische Meldungen sowie Datum-, Zahlen- und Größenformatierung laufen ebenfalls über diese Sprachdatei.
- **Zugriffsschutz:** Tailscale Serve über MagicDNS-HTTPS, Prüfung von `Tailscale-User-Login` gegen `AllowedLogins`, freigegebene Roots, Reparse-Point-Prüfung, schreibgeschützte Systempfade und CSRF-Header.
- **Datenmodell:** `ExplorerSettings` in `settings.json`; `FileEntryDto`, `DriveInfoDto`, `JobSnapshot`, `UploadSessionDto`, `TextDocumentDto` und Eigenschaften-DTOs. Laufende Aufträge liegen in `jobs.json`; fortsetzbare Uploads haben getrennte Staging- und JSON-Metadateien.
- **Aufträge:** zwei vom Browser unabhängige Worker; persistenter Status, Fortschritt, Konfliktentscheidung, Pause/Abbruch und Undo-Aufzeichnungen. Kopien schreiben zunächst in eine versteckte `.partial`-Datei im Zielordner und verschieben sie nach erfolgreichem Abschluss.
- **API:** Lesend `GET /api/v1/health`, `/settings/shares`, `/drives`, `/known-folders`, `/quick-access`, `/windows-recent`, `/entries`, `/shortcuts/resolve`, `/stream`, `/thumbnail`, `/archive`, `/properties`, `/text`, `/search`, `/jobs`, `/uploads`, `/recycle-bin`; Ereignisse über `/watch`, `/jobs/events` und SSE-Suche. Schreibend `PUT /api/v1/settings/shares` sowie `POST/PUT/DELETE` für Dateioperationen, `/jobs`, `/undo`, `/uploads`, `/text`, `/properties/attributes`, `/open` und `/recycle-bin`.

**Warum .NET 10 statt .NET 8:** Beide bieten Windows-Shell- und ASP.NET-Core-Zugriff. Der .NET-8-Support endet am 10. November 2026; .NET 10 LTS wird bis November 2028 unterstützt. Das hält die neue Installation länger auf einer unterstützten Basis. [Microsoft: Releases und Support](https://learn.microsoft.com/dotnet/core/releases-and-support).

### Projektstruktur

```text
src/Explorer.Core/       Pfadnormalisierung, Freigabegrenzen, DTOs, deutsche Fehler
src/Explorer.Host/       WinForms-Tray, ASP.NET-Core-API, Windows-Shell-Dienste
src/Explorer.Web/        React/TypeScript-PWA, deutsche Texte und WebKit-E2E
tests/Explorer.Core.Tests/       Pfad- und Sicherheitstests
tests/Explorer.Host.Tests/       Windows-Integrationstests, Jobs und Shell
scripts/                 Range-Prüfung und unterstützende Windows-Skripte
docs/verification/       dokumentierte und gerenderte iPhone-Viewport-Prüfungen
```

### API-Gruppen

- `GET /api/v1/health`, `/drives`, `/known-folders`, `/quick-access`, `/windows-recent`, `/favorites`; `PUT /favorites`; `GET /settings/shares` zeigt die Freigaben.
- `GET /entries`, `/stream`, `/thumbnail`, `/archive`, `/shortcuts/resolve`; `POST /open`.
- `GET /jobs`, `/jobs/events`, `/watch`; `POST /jobs`, `/jobs/{id}/pause`, `/resume`, `/cancel`, `/conflict`, `/undo`.
- `GET/POST /uploads`, `PUT /uploads/{id}/chunk`, `POST /uploads/{id}/commit`, `DELETE /uploads/{id}`.
- `GET /search` streamt Ergebnisse; `GET /properties`, `POST /properties/attributes`, `GET/PUT /text`.
- `GET /recycle-bin`, `POST /recycle-bin/{id}/restore`, `/delete`, `/empty`; zusätzliche PIN-Routen unter `/security/pin`.

Alle API-Pfade tragen das Präfix `/api/v1`. Schreibzugriffe benötigen Tailscale-Allowlist, gleichen Ursprung und den PWA-Request-Header.

## Installation

1. Installiere und verbinde Tailscale auf dem Windows-PC. In der Admin-Konsole müssen unter **Network → DNS** MagicDNS und **HTTPS Certificates** aktiviert sein. Das Setup konfiguriert `tailscale serve` für den PC. Falls Tailscale eine Freigabe dieses Rechners verlangt, zeigt das Setup den Admin-Link an und endet nach höchstens zehn Sekunden. Öffne den Link, melde dich an, aktiviere Serve für den PC und führe das Setup danach erneut aus.
2. Öffne PowerShell als normaler Benutzer im Projektordner und führe aus:

   ```powershell
   .\setup.ps1
   ```

   Das Skript erkennt nach Möglichkeit den aktuellen Tailscale-Login. Ohne vorhandenen Publish-Ordner baut es die PWA und veröffentlicht den Windows-Host selbständig. Beim ersten Lauf wird standardmäßig dein Benutzerordner freigegeben. Du kannst bestimmte Ordner wählen, getrennt Nur-Lesen einstellen oder alle Laufwerke freigeben. `C:\Windows` und `Program Files` bleiben standardmäßig schreibgeschützt.
3. Das Setup installiert nach `%LOCALAPPDATA%\Programs\WindowsMobileExplorer`, legt `%LOCALAPPDATA%\WindowsMobileExplorer\settings.json` an, richtet HKCU-Autostart ein und konfiguriert `tailscale serve` als Proxy zu `http://127.0.0.1:5180`. Es behält einen aktiven, gespeicherten HTTPS-Port bei; andernfalls sucht es ab `8443` nach einem freien Port. Es ruft nie `tailscale funnel` auf und setzt bestehende Serve-Konfigurationen nicht zurück.
4. Verbinde Tailscale auf dem iPhone und öffne die ausgegebene HTTPS-MagicDNS-Adresse in Safari. Tippe auf **Teilen** (bei manchen Safari-Layouts zuerst **Seitenmenü → Teilen**), scrolle zu **Zu Home-Bildschirm hinzufügen** und tippe darauf. Fehlt die Aktion, füge sie über **Aktionen bearbeiten** hinzu. Aktiviere **Als Web-App öffnen** und tippe auf **Hinzufügen**. Damit erhält die PWA ein Symbol und startet im App-Modus. Siehe auch [Apple: Website in Safari auf dem iPhone in eine App verwandeln](https://support.apple.com/de-ch/guide/iphone/iphea86e5236/27/ios/27).

Wenn ein vorhandener Build-Ordner fehlt, benötigt das Setup Node.js/npm und das .NET-10-SDK. Der Build ist selbstenthaltend (`win-x64`); auf dem Ziel-PC wird keine separate .NET-Runtime benötigt.

Optionen:

```powershell
.\setup.ps1 -ShareAllDrives $false -ShareRoot 'C:\Users\Name\Dokumente','D:\Projekte' -ReadOnly
.\setup.ps1 -AllowedLogin 'name@example.com'
```

Für ein Update aus geändertem Quellcode verwende `.\setup.ps1 -Rebuild`. Das Setup prüft vor einem Update, dass keine Dateioperation und kein Upload aktiv ist, beendet die Tray-App dann für das Ersetzen der Programmdateien und startet sie anschließend neu. Bereits freigegebene Tailscale-Anmeldungen bleiben erhalten.

ZIP-Dateien werden mit .NET erstellt und entpackt. `.7z`-Archive lassen sich entpacken, wenn 7-Zip unter `C:\Program Files\7-Zip` oder `C:\Program Files (x86)\7-Zip` installiert ist; das Programm wird ohne Shell-Aufruf gestartet und schreibt Archivdateien nur einzeln in einen geprüften temporären Zielordner.

**Teilen** öffnet für unterstützte Dateien bis 32 MB das native Teilen-Menü des iPhones mit der Datei selbst. Größere Dateien und Ordner (als gestreamtes ZIP) werden heruntergeladen; anschließend kannst du sie in der Dateien-App auswählen und von dort teilen. Wenn Safari die erste Übergabe wegen abgelaufener Nutzeraktion ablehnt, erscheint eine antippbare Teilen-Wiederholung. Wird auch sie abgelehnt, startet der Download als Rückfall.

Unter **Mehr → Einstellungen → Datei-Freigaben** zeigt die PWA die freigegebenen Ordner und den Schreibschutz an. Änderungen nimmst du bei gestoppter App am Windows-PC in `settings.json` vor. Über **Konfiguration öffnen** im Infobereich findest du die Datei. Die Startseite zeigt Windows-Schnellzugriff-Ordner, sofern sie innerhalb der konfigurierten Freigaben liegen; die PWA kann Windows-Anheftungen nicht ändern. App-Favoriten werden auf dem PC pro Tailscale-Anmeldekonto gespeichert und auf jedem Gerät synchron angezeigt. Bereits lokal gespeicherte Ordnerfavoriten einer älteren PWA-Version werden nach Prüfung gegen die Freigabe einmalig übernommen. Gespeichert werden nur freigegebene Ordnerpfade. Unter **Einstellungen → Textgröße** kannst du die Schrift zwischen 100 und 200 Prozent einstellen. Auf iPhone und iPad bildet die iOS-System-Textgröße die Basis; der Regler vergrößert sie zusätzlich. Freigabeeinstellungen werden beim Start des Windows-Hosts eingelesen. Die Textgröße bleibt lokal auf dem jeweiligen Mobilgerät gespeichert. `AllowedLogins`, Ports und weitere Host-Einstellungen bleiben eine lokale Konfiguration und werden bei gestoppter App in `settings.json` geändert. Bei erneutem Setup wird eine vorhandene Konfiguration mit Zeitstempel gesichert. Für weitere freigegebene Tailscale-Logins ergänze `AllowedLogins` von Hand; die Identität muss exakt mit `Tailscale-User-Login` übereinstimmen. In der PWA unter **Mehr → PIN einrichten** kannst du optional einen zusätzlichen 6- bis 12-stelligen Geräte-PIN setzen. Der Server speichert nur einen PBKDF2-SHA-256-Hash, sperrt nach fünf falschen PINs für 15 Minuten und beendet Sitzungen nach acht Stunden. **Mehr → App jetzt sperren** erzwingt die erneute PIN-Eingabe; Ändern oder Ausschalten ist nach dem Entsperren möglich. Face ID/Passkey ist nicht implementiert.

Die Ordnernavigation bleibt ab 900 px links sichtbar und ist auf kleineren Bildschirmen über **Navigation öffnen** erreichbar. Sie enthält **Dieser PC**, Windows-Standardordner, vorhandene Windows-Schnellzugriffe, App-Favoriten und Laufwerke. **Dieser PC** ist außerdem jederzeit über die obere Leiste erreichbar und zeigt die Laufwerke mit Laufwerksbuchstaben zuerst. Das Windows-ReadOnly-Attribut eines Ordners gilt nicht als Schreibsperre; tatsächliche Freigabesperren werden weiterhin berücksichtigt. Einzelne Einträge mit nicht lesbaren Metadaten (z. B. ungültige Windows-Gerätenamen) blockieren die übrige Ordnerliste nicht.

Musik und Videos öffnen im integrierten Streaming-Player. Dateien bleiben auf dem PC; der Player nutzt den geschützten Stream mit HTTP-Range-Zugriff zum Spulen. **Player minimieren** macht daraus eine schwebende Anzeige, während Ordnernavigation und Dateimenüs bedienbar bleiben. Der Mini-Player hält automatisch Abstand zu Ordneraktionen, Auswahlleiste und unterer Navigation, auch bei größerer Schrift und Safe Areas. Über **Zur Warteschlange hinzufügen** kannst du Titel ergänzen; Ordner lassen sich vollständig und ohne Unterordner über das Play-Symbol oder ihr Aktionsmenü übernehmen. Bei laufendem Player ergänzt **Alle hinzufügen** die Titel des aktuellen Ordners, ohne den aktuellen Titel oder seine Position zu ändern. Die Warteschlange gilt für die aktuelle Sitzung.

Für lange Sets zeigt der Player eine echte Wellenform des gesamten Titels. Tippen springt an eine Position; Ziehen und gleichzeitiges Bewegen nach oben ermöglicht feineres Spulen. Zusätzlich stehen eine Zeitleiste, 10-/30-Sekunden-Sprünge, Titelwechsel, Wiederholung, Zufall und Wiedergabegeschwindigkeit bereit. Die Wellenform wird mit FFmpeg/FFprobe auf dem PC berechnet und zwischengespeichert; währenddessen funktioniert die normale Zeitleiste bereits. Der Dienst findet vorhandene WinGet-Installationen, Programme im PATH, gebündelte `tools/ffmpeg/bin` oder die expliziten Umgebungsvariablen `WINDOWS_MOBILE_EXPLORER_FFMPEG` und `WINDOWS_MOBILE_EXPLORER_FFPROBE`. Video unterstützt geräteabhängig Vollbild und Bild-im-Bild; Gerätesteuerung läuft über Media Session. Das Gerät muss den Audio-/Videocodec selbst unterstützen; Playback-Transcodierung ist nicht implementiert. PC und Tailscale-Verbindung müssen erreichbar bleiben.

## Konfiguration und Sicherheit

Für ein Update von einer älteren Version führe das Setup erneut aus. Es übernimmt den MagicDNS-Namen und erhält die vorhandenen Freigaben. **Im Explorer anzeigen** öffnet den Speicherort am PC. Programme und Skripte lassen sich über die Web-App nicht starten.

Der Papierkorb zeigt Einträge aus den Freigaben. Wiederherstellen, endgültiges Löschen und Leeren erfordern eine schreibbare Freigabe für den ursprünglichen Pfad. Ganze Laufwerke und Freigabewurzeln sind gegen Verschieben, Umbenennen und Löschen geschützt. Löschen und Umbenennen von symbolischen Links und Junctions wirken auf den jeweiligen Eintrag.

ZIP-Downloads verarbeiten höchstens 10.000 Einträge und 4 GB Quelldaten. Beim Entpacken gelten höchstens 10.000 Einträge und 20 GB, zusätzlich abhängig von Kompressionsverhältnis und freiem Speicher. Unterbrochene Uploads können bis zu sieben Tage nach der letzten Aktivität fortgesetzt werden. Beim Abschluss vergleicht die App die Prüfsumme mit der lokalen Datei. Zwischenstücke liegen im App-Datenordner, wenn Ziel und App-Datenordner auf demselben Laufwerk liegen. Auf anderen Laufwerken bleiben sie am Ziel.


Die PWA merkt sich den zuletzt geöffneten Ordner lokal und prüft ihn beim nächsten Start erneut gegen die aktuelle Serverfreigabe. Ist der Ordner nicht mehr freigegeben oder erreichbar, öffnet sich „Dieser PC“ und die gespeicherte Position wird verworfen.

`ShareAllDrives` gibt Laufwerke für den angemeldeten Windows-Benutzer frei. Alternativ können in `Roots` einzelne Verzeichnisse samt `ReadOnly`-Flag aufgeführt werden. Freigabeänderungen erfordern einen Neustart des Hosts. `ReadOnlyMode` sperrt alle Schreiboperationen; `ProtectSystemFolders` hält Windows und Program Files standardmäßig schreibgeschützt, selbst wenn ein übergeordneter Ordner geteilt wird. Es gelten weiterhin die normalen NTFS-Berechtigungen dieses Windows-Benutzers. App-Daten und Programmdateien, gängige Browserprofile, SSH- und GnuPG-Schlüssel, bekannte Autostart-Ordner und die internen Papierkorbdateien bleiben von der Dateifreigabe ausgeschlossen.

Kestrel bindet auf Loopback. Die App akzeptiert nur den im Setup hinterlegten MagicDNS-Namen und HTTPS-Port, eine Loopback-Verbindung aus dem lokalen Tailscale-Dienst und eine freigegebene Tailscale-Identität. Das Setup verwendet für lokale Statusabfragen einen separaten Schlüssel aus dem privaten Datenordner. API-Schreibzugriffe erfordern zusätzlich einen Same-Origin-Check und `X-Explorer-Request`. Die PWA legt keine Datei- oder API-Antworten in ihrem Offline-Cache ab. Schreibvorgänge werden mit separaten JSON-Feldern als JSONL unter `%LOCALAPPDATA%\WindowsMobileExplorer\logs` rotiert protokolliert; Auftrags- und Uploadstatus liegen im Datenordner.

Löschen in den Windows-Papierkorb wird auf lokalen, von Windows als feste Laufwerke erkannten Datenträgern ausgeführt. Auf UNC-Netzpfaden, Netzlaufwerken und als Wechseldatenträger erkannten USB-Laufwerken wird die Standard-Löschaktion vor jedem Shell-Aufruf abgewiesen, weil Windows dort den lokalen Papierkorb nicht zuverlässig bereitstellt. Die Datei bleibt dabei erhalten; endgültiges Löschen erfordert die ausdrücklich bestätigte Aktion **Endgültig löschen**.

Der optionale PIN-Schutz ergänzt die Tailscale-Identität. Auch mit PIN bleibt die PWA ausschließlich über die Tailscale-Allowlist erreichbar; Serverdateizugriffe verwenden weiterhin die Windows-Rechte des angemeldeten Benutzers.

### Optionale Everything-Suche

Wenn Everything und dessen ES-Kommandozeilenprogramm installiert sind, nutzt die rekursive Dateinamensuche `es.exe` bevorzugt. Lege `es.exe` neben `Everything.exe` oder neben `Explorer.Host.exe`; Inhaltssuchen verwenden weiterhin Windows Search bzw. den Dateisystem-Fallback. ES muss laut [voidtools-Dokumentation](https://www.voidtools.com/support/everything/command_line_interface/) laufen. Ohne ES bleibt die Standardsuche unverändert.

## Entwicklung und Tests

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

Der E2E-Host verwendet einen isolierten Test-Papierkorb. Playwrights Global-Teardown beendet die vom Test gestartete Host-PID und wartet auf die Bereinigung; nur eigens erzeugte, zufällig benannte `%TEMP%`-Ordner werden entfernt. Ein erfolgreicher E2E-Lauf hinterlässt weder `.runtime.json` noch seine aktuelle 50.000-Dateien-Fixture.

Die WebKit-E2E-Prüfung startet einen getrennten lokalen Testserver mit einmaligen `%TEMP%`-Ordnern und Testprofil; sie verwendet weder das echte Freigabe-Setup noch das Tray-Symbol oder den Windows Recent-Ordner des Benutzers. Sie prüft Navigation einschließlich einer isolierten Windows-Recent-Verknüpfung, Freigaben hinzufügen und entfernen, deren sofortige Sperrwirkung, Schreibschutz pro Ordner, Schutz kritischer Systemordner, das getrennte Öffnen und Aktionsmenü mit 44×44-Touchfläche, bei 320 px zugängliche Namen und Touchgrößen für sichtbare Bedienelemente, langes Drücken und Wischen zum Öffnen der Aktionen Teilen/Umbenennen/Papierkorb, die antippbare Wiederholung nach einer simulierten Safari-Ablehnung und die an die native Teilen-API übergebene Datei, Fokus-Rückgabe und Escape-Schließen. Für die Dialoge prüft sie Fokusführung, Fokus-Rückgabe, Escape, Eigenschaften über der Vorschau, Dateinameingabe und den Schutz vor versehentlichem Schließen ungespeicherter Editoränderungen; außerdem simuliertes Pinch-Zoom, Bearbeiten/Speichern, Systemeinstellungen, Filter, Gruppierung, PIN einrichten/sperren/falschen PIN/entsperren sowie einen Ordner mit 50.000 Dateien und die Virtualisierung bis 320 px Breite. Ein zusätzlicher Upload-E2E-Test lässt eine 8-MB-Chunk-Antwort nach erfolgreichem Server-Speichern verloren gehen und prüft automatische Offset-Abstimmung, Wiederverbindung und fertige Datei. Integrationstests messen die erste Seite dieses Ordners, prüfen Windows-`.lnk`-Ziele samt Freigabegrenze sowie den Windows-Recent-Verlauf mit Freigabefilter und Deduplizierung, `\\?\`-Normalisierung sowie Kopieren und Umbenennen eines Pfads mit mehr als 260 Zeichen, einen 2-GB-Upload mit Wiederaufnahme nach einem simulierten Dienstneustart, entpacken ein `.7z`-Archiv mit Unicode-Pfaden (wenn 7-Zip installiert ist) und legen 10.000 kleine Dateien ausschließlich in einem eigenen `%TEMP%`-Ordner an, beobachten den Fortschritt eines Kopierauftrags, brechen ihn ab und prüfen, dass keine `.partial`-Datei bleibt. Ein separater Windows-Shell-Test löscht zwei eindeutig benannte Dateien aus einem eigenen `%TEMP%`-Ordner in den Windows-Papierkorb, prüft die Auflistung und Wiederherstellung per ID und Rückgängig-Pfad und bereinigt anschließend nur seine eigenen Testdateien. Alle übrigen Integrationstests nutzen einen isolierten Test-Papierkorb. Der installierte Host hat zusätzlich eine 337 Zeichen lange Datei gelistet und per HTTP gestreamt. Ein Eigenschaftstest prüft den sauberen Rückfall, wenn für ungültige Testmedien keine Shell-Metadaten verfügbar sind.

Die Markdown-E2E-Prüfung verifiziert formatierte Überschriften und Listen, lädt relative Bilder nur über den geschützten lokalen Stream-Endpunkt und prüft, dass eingebettete Scripts nicht ausgeführt, rohe HTML-Bilder samt `srcset` entfernt und keine externen Bildanfragen ausgelöst werden.

Der separate PWA-Installations-E2E-Test prüft Webmanifest-Felder und Icon-Dateien, Apple-Touch-Icon und iOS-Metadaten, `viewport-fit=cover`, Safe-Area-Variablen sowie einen aktiven Service Worker im iPhone-WebKit-Kontext.

Ein weiterer iPhone-WebKit-E2E-Test erzeugt eine PNG-Datei unter Windows, prüft Windows-Shell-Miniaturen für PNG, MP4 und PDF samt Anzeige in der Kachelansicht, decodiert synthetische MP4- und MP3-Fixtures, sucht die Audiospur durch Berührung der nativen Zeitleiste und kontrolliert Medien-Range-Antworten des geschützten Stream-Endpunkts. Ein eigens gebautes, zwei Stunden langes 5-GB-Sparse-H.264-MP4 mit wiederholten gültigen GOPs wird im WebKit-Viewport decodiert und bei 10, 30, 75 und 5.400 Sekunden angesprungen; der letzte Seek liefert eine serverseitige HTTP-206-Range-Antwort bei etwa Byte 3,73 GB. Eine zusätzliche Range-Anfrage bei Byte 3,75 GB muss ebenfalls HTTP 206 liefern. Eine echte HEIC-Datei lässt sich per `WINDOWS_MOBILE_EXPLORER_HEIC_FIXTURE` optional in denselben Test aufnehmen. Er sichert außerdem drei iPhone-Viewport-Bilder nach `docs/verification/iphone-viewport/` und prüft die überbreite Freigabekarte sowie zugängliche, mindestens 44 px große, horizontal scrollbar angeordnete Breadcrumb-Schaltflächen. Das ist ein WebKit-Test in Playwright; Home-Bildschirm-Installation, VoiceOver, tatsächliche Safe Areas und Netzwechsel brauchen ein verbundenes iPhone.

Für einen zusätzlichen HEIC-Shell-Miniaturtest kannst du lokal eine eigene `.heic`-Testdatei angeben. Optional steht auch `example.heic` aus dem [libheif-Beispielverzeichnis](https://github.com/strukturag/libheif/tree/master/examples) zur Verfügung. Der Test kopiert die Datei in den isolierten `%TEMP%`-Testordner und löscht die Kopie mit dem restlichen Fixture:

```powershell
Set-Location .\src\Explorer.Web
$env:WINDOWS_MOBILE_EXPLORER_HEIC_FIXTURE = 'C:\Pfad\zu\example.heic'
npm run test:e2e
Remove-Item Env:\WINDOWS_MOBILE_EXPLORER_HEIC_FIXTURE
```

Mit laufender installierter App prüft `scripts\test-5gb-range.ps1` HTTP-Range-Zugriffe am Anfang und Ende einer 5-GB-Sparse-Datei über Tailscale Serve. Ohne Parameter nutzt das Skript die im Setup hinterlegte HTTPS-Adresse. Du kannst diese Adresse auch explizit angeben.

```powershell
.\scripts\test-5gb-range.ps1 -BaseUrl 'https://<rechner>.<tailnet>.ts.net:8443'
```

Das Skript prüft den MagicDNS-Namen des eigenen PCs und den konfigurierten HTTPS-Port. Der Test setzt eine Freigabe für `%TEMP%` und eine Testinstallation ohne PIN voraus. Die Testdatei liegt in `%TEMP%` und wird danach entfernt. Dieses PowerShell-Skript prüft den HTTP-Transport. Die getrennte Playwright-WebKit-Prüfung decodiert und durchsucht zusätzlich das 5-GB-MP4; echtes Safari-Verhalten auf dem iPhone bleibt damit unbewiesen.

Der WebKit-E2E-Test prüft außerdem, dass der Auswahlmodus die untere Aktionsleiste schon bei null markierten Einträgen zeigt, Tippen einzelne Dateien umschaltet und „Alle auswählen“ über sämtliche Seiten hinweg auch 50.000 Einträge erfasst. Die Leiste bleibt dabei über der unteren Navigation und innerhalb eines 320-px-iPhone-Viewports.

## Fehlerbehebung

- **„Tailscale ist derzeit nicht verbunden“:** Tailscale starten, PC im gewünschten Tailnet anmelden und Setup erneut ausführen.
- **HTTPS-Zustimmung erforderlich:** Tailscale Serve zeigt den fehlenden Tailnet-Schritt bzw. eine Bestätigungsseite. MagicDNS-HTTPS im Tailnet aktivieren und `setup.ps1` erneut ausführen.
- **Serve-Freigabelink liefert 404:** In der Admin-Konsole **Network → DNS → HTTPS Certificates → Enable HTTPS…** öffnen. Vor dem Aktivieren den Tailnet-DNS-Namen prüfen, damit HTTPS im selben Tailnet wie der PC eingeschaltet wird. Danach den Serve-Befehl erneut ausführen.
- **Laufwerk antwortet nicht:** Ordnerabfragen brechen nach 20 Sekunden mit einer verständlichen Meldung ab. Netzlaufwerk erneut verbinden oder USB-Laufwerk einstecken und den Ordner aktualisieren.
- **Zugriff verweigert:** Tailscale-Konto in `AllowedLogins` prüfen; Freigabepfad und Windows-ACLs kontrollieren.
- **Serve-Port belegt:** Setup behält einen passenden gespeicherten Port bei oder wählt automatisch einen freien Port ab `8443`; den aktuellen Port findest du in `settings.json` und der Tray-Menüadresse.
- **App neu starten:** Tray-Symbol → Beenden, danach über Startmenü oder HKCU-Autostart starten.
- **Protokolle:** `%LOCALAPPDATA%\WindowsMobileExplorer\logs`.

## Umsetzungsstatus

Die vollständige Funktionsliste mit Einschränkungen und noch offenen Punkten steht in [FEATURES.md](FEATURES.md). Sie unterscheidet getestete Funktionen von Implementierungen, die noch auf echter iPhone-/Windows-Hardware oder mit großen Medien geprüft werden müssen.
