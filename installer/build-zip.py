"""Create the alternative ZIP setup from an existing self-contained publish."""
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED

root = Path(__file__).resolve().parent.parent
publish = root / 'publish' / 'win-x64'
if not (publish / 'Explorer.Host.exe').is_file() or not (publish / 'wwwroot' / 'index.html').is_file():
    raise SystemExit('Publish fehlt oder ist unvollständig.')
destination = root / 'artifacts' / 'EXfLauschPLORER-0.1.0-win-x64.zip'
destination.parent.mkdir(parents=True, exist_ok=True)
with ZipFile(destination, 'w', compression=ZIP_DEFLATED, compresslevel=6) as archive:
    for source, name in [(root / 'setup.ps1', 'setup.ps1'), (root / 'installer' / 'Einrichtung.ps1', 'Einrichtung.ps1'), (root / 'installer' / 'ZIP-starten.cmd', 'ZIP-starten.cmd')]:
        archive.write(source, name)
    archive.writestr('LESE-MICH.txt', 'ZIP vollständig entpacken und ZIP-starten.cmd öffnen. Die Einrichtung installiert die App für den aktuellen Windows-Benutzer. Weitere Hilfe: https://github.com/00fLausch/exflauschplorer\r\n')
    for file in sorted(publish.rglob('*')):
        if file.is_file() and file.suffix.lower() != '.pdb':
            archive.write(file, (Path('publish/win-x64') / file.relative_to(publish)).as_posix())
print(f'ZIP erstellt: {destination.name} ({destination.stat().st_size // 1024 // 1024} MB)')
