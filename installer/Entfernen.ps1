param([Parameter(Mandatory)][string]$InstallRoot)
$ErrorActionPreference = 'Stop'
try {
    $exe = Join-Path $InstallRoot 'Explorer.Host.exe'
    $all = @(Get-CimInstance Win32_Process -Filter "Name='Explorer.Host.exe'" | Where-Object { $_.ExecutablePath -eq $exe })
    $apps = @($all | Where-Object { $_.CommandLine -notmatch '(?:^|\s)--watchdog(?:\s|$)' })
    $watchers = @($all | Where-Object { $_.CommandLine -match '(?:^|\s)--watchdog(?:\s|$)' })
    if ($apps.Count -gt 0) {
        $settings = Get-Content -LiteralPath (Join-Path $env:LOCALAPPDATA 'WindowsMobileExplorer\settings.json') -Raw | ConvertFrom-Json
        $login = @($settings.allowedLogins)[0]
        if (-not $login) { throw 'Die laufende App konnte nicht auf aktive Vorgänge geprüft werden. Bitte zuerst über das Symbol im Infobereich beenden.' }
        $headers = @{ 'Tailscale-User-Login' = [string]$login }
        $jobs = @(Invoke-RestMethod -Uri "http://127.0.0.1:$($settings.port)/api/v1/jobs" -Headers $headers -TimeoutSec 4)
        $uploads = @(Invoke-RestMethod -Uri "http://127.0.0.1:$($settings.port)/api/v1/uploads" -Headers $headers -TimeoutSec 4)
        if (@($jobs | Where-Object { $_.status -in @('queued','running','paused','conflict') }).Count -or @($uploads | Where-Object { $_.state -in @('uploading','ready') }).Count) {
            throw 'Es sind noch Datei- oder Uploadvorgänge aktiv. Bitte zuerst abschließen und die Deinstallation erneut starten.'
        }
    }
    foreach ($processInfo in @($watchers) + @($apps)) { Stop-Process -Id $processInfo.ProcessId -Force }
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'WindowsMobileExplorer' -ErrorAction SilentlyContinue
    exit 0
} catch {
    Add-Type -AssemblyName System.Windows.Forms
    [Windows.Forms.MessageBox]::Show("Deinstallation angehalten: $($_.Exception.Message)", 'EXfLauschPLORER', 'OK', 'Warning') | Out-Null
    exit 1
}
