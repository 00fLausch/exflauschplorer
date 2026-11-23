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
        if ($settings.localSetupToken) {
            $activity = Invoke-RestMethod -Uri "http://127.0.0.1:$($settings.port)/api/v1/local-setup" -Headers @{ 'X-Explorer-Setup' = $settings.localSetupToken } -TimeoutSec 4
            $activeJobs = [int]$activity.activeJobs
            $activeUploads = [int]$activity.activeUploads
        }
        else {
            $jobs = @(Invoke-RestMethod -Uri "http://127.0.0.1:$($settings.port)/api/v1/jobs" -Headers $headers -TimeoutSec 4)
            $uploads = @(Invoke-RestMethod -Uri "http://127.0.0.1:$($settings.port)/api/v1/uploads" -Headers $headers -TimeoutSec 4)
            $activeJobs = @($jobs | Where-Object { $_.status -in @('queued','running','paused','conflict') }).Count
            $activeUploads = @($uploads | Where-Object { $_.state -in @('uploading','ready') }).Count
        }
        if ($activeJobs -gt 0 -or $activeUploads -gt 0) {
            throw 'Es sind noch Datei- oder Uploadvorgänge aktiv. Bitte zuerst abschließen und die Deinstallation erneut starten.'
        }
    }
    foreach ($processInfo in @($watchers) + @($apps)) {
        $process = Get-Process -Id $processInfo.ProcessId -ErrorAction SilentlyContinue
        if ($process) {
            Stop-Process -Id $process.Id -Force
            if (-not $process.WaitForExit(5000)) { throw 'Die App konnte nicht beendet werden. Bitte erneut versuchen.' }
        }
    }
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'WindowsMobileExplorer' -ErrorAction SilentlyContinue
    exit 0
} catch {
    Add-Type -AssemblyName System.Windows.Forms
    [Windows.Forms.MessageBox]::Show("Deinstallation angehalten: $($_.Exception.Message)", 'EXfLauschPLORER', 'OK', 'Warning') | Out-Null
    exit 1
}
