$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\setup.ps1') -Raw
$start = $source.IndexOf('    $settings = [ordered]@{}')
$end = $source.IndexOf('    $temporarySettings =', $start)
if ($start -lt 0 -or $end -lt 0) { throw 'Einstellungsblock nicht gefunden.' }
$savedSettings = '{"favoritesByLogin":{"demo@example.com":["C:\\Demo"]},"showSystemByDefault":true,"futureOption":"keep","allowedLogins":["demo@example.com"]}' | ConvertFrom-Json
$httpPort = 5180; $httpsPort = 8443; $allowedLogins = @('demo@example.com'); $shareEverything = $true
$roots = @(); $readOnlyMode = $false; $protectSystemFolders = $true; $pinHash = $null
. ([scriptblock]::Create($source.Substring($start, $end - $start)))
$roundTrip = ($settings | ConvertTo-Json -Depth 8) | ConvertFrom-Json
if ($roundTrip.favoritesByLogin.'demo@example.com'[0] -ne 'C:\Demo' -or -not $roundTrip.showSystemByDefault -or $roundTrip.futureOption -ne 'keep') {
    throw 'Vorhandene Einstellungen wurden nicht erhalten.'
}
if ($roundTrip.port -ne 5180 -or @($roundTrip.allowedLogins).Count -ne 1) { throw 'Neue Setup-Werte sind fehlerhaft.' }
Write-Output 'Setup-Einstellungen: Favoriten, Ansichtsoptionen und unbekannte Felder bleiben erhalten.'
