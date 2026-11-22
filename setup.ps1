[CmdletBinding()]
param(
    [string]$AllowedLogin,
    [bool]$ShareAllDrives = $true,
    [string[]]$ShareRoot = @(),
    [switch]$ReadOnly,
    [switch]$Rebuild,
    [bool]$Autostart = $true,
    [bool]$Watchdog = $false,
    [switch]$ConfigureOnly
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Das Setup muss mit normalen Benutzerrechten laufen. Öffne PowerShell ohne „Als Administrator ausführen“ und wiederhole den Befehl.'
}

$projectRoot = $PSScriptRoot
$publishRoot = Join-Path $projectRoot 'publish\win-x64'
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\WindowsMobileExplorer'
$exePath = Join-Path $installRoot 'Explorer.Host.exe'
$settingsDirectory = Join-Path $env:LOCALAPPDATA 'WindowsMobileExplorer'
$settingsPath = Join-Path $settingsDirectory 'settings.json'
$httpPort = 5180

function Get-TailscaleExe {
    $command = Get-Command tailscale.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $knownPath = Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'
    if (Test-Path -LiteralPath $knownPath) { return $knownPath }
    throw 'Tailscale ist nicht installiert. Installiere Tailscale, melde diesen PC in deinem Tailnet an und führe setup.ps1 erneut aus.'
}

function Get-TailscaleStatus($tailscale) {
    $raw = & $tailscale status --json 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Tailscale antwortet nicht. Starte Tailscale und melde diesen PC in deinem Tailnet an.' }
    try { return (($raw -join "`n") | ConvertFrom-Json) }
    catch { throw 'Tailscale lieferte keinen gültigen Status. Prüfe `tailscale status --json` und versuche es erneut.' }
}

function Test-FunnelForLocalPort([string]$status, [int]$localPort) {
    $publicRoutes = [regex]::Matches($status, '(?m)^https?://[^\r\n]*\(Funnel on\)[^\r\n]*(?:\r?\n(?!https?://)[^\r\n]*)*')
    foreach ($route in $publicRoutes) {
        if ([regex]::IsMatch($route.Value, "127\.0\.0\.1:$localPort\b")) { return $true }
    }
    return $false
}

function Get-CurrentTailnetLogin($status) {
    if (-not $status.Self.UserID -or -not $status.User) { return $null }
    $user = $status.User.PSObject.Properties[[string]$status.Self.UserID].Value
    if ($user.LoginName) { return [string]$user.LoginName }
    return $null
}

function Build-PublishFolder {
    if ((Test-Path -LiteralPath (Join-Path $publishRoot 'Explorer.Host.exe')) -and -not $Rebuild) { return }
    $webProject = Join-Path $projectRoot 'src\Explorer.Web'
    $hostProject = Join-Path $projectRoot 'src\Explorer.Host\Explorer.Host.csproj'
    $dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet)) {
        $dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
        if (-not $dotnetCommand) { throw 'Der veröffentlichte App-Ordner fehlt und das .NET-10-SDK ist nicht installiert. Nutze den fertigen Ordner publish\win-x64 oder installiere das .NET-10-SDK.' }
        $dotnet = $dotnetCommand.Source
    }
    $node = Get-Command node.exe -ErrorAction SilentlyContinue
    $npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if (-not $node -or -not $npm) { throw 'Der veröffentlichte App-Ordner fehlt. Für den Build aus dem Quellcode werden Node.js und npm benötigt.' }

    Push-Location $webProject
    try {
        if (-not (Test-Path -LiteralPath (Join-Path $webProject 'node_modules'))) { & $npm.Source ci }
        if ($LASTEXITCODE -ne 0) { throw 'npm ci ist fehlgeschlagen.' }
        & $npm.Source run build
        if ($LASTEXITCODE -ne 0) { throw 'Der PWA-Build ist fehlgeschlagen.' }
    }
    finally { Pop-Location }

    & $dotnet publish $hostProject -c Release -r win-x64 --self-contained true -o $publishRoot
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish ist fehlgeschlagen.' }
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'wwwroot\index.html'))) {
        throw 'Der veröffentlichte Ordner enthält die PWA nicht unter wwwroot. Der Build wurde nicht installiert.'
    }
}

try {
    $tailscale = Get-TailscaleExe
    $tailnetStatus = Get-TailscaleStatus $tailscale
    if ($tailnetStatus.BackendState -ne 'Running') { throw 'Tailscale ist derzeit nicht verbunden. Verbinde diesen PC mit deinem Tailnet und starte das Setup erneut.' }

    if ([string]::IsNullOrWhiteSpace($AllowedLogin)) { $AllowedLogin = Get-CurrentTailnetLogin $tailnetStatus }
    if ([string]::IsNullOrWhiteSpace($AllowedLogin)) { $AllowedLogin = Read-Host 'Tailscale-Anmeldename, der Zugriff auf den Datei-Explorer erhalten soll' }
    $AllowedLogin = $AllowedLogin.Trim()
    if ($AllowedLogin -notmatch '^[^\s@]+@[^\s@]+$') { throw 'Der Tailscale-Anmeldename muss eine E-Mail-Adresse sein.' }

    $savedSettings = $null
    if (Test-Path -LiteralPath $settingsPath) {
        try { $savedSettings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json }
        catch { throw "Die vorhandene Konfiguration ist ungültig: $settingsPath" }
    }

    $allowedLogins = @()
    if ($savedSettings -and $savedSettings.allowedLogins) {
        $allowedLogins = @($savedSettings.allowedLogins | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    }
    if ($allowedLogins -notcontains $AllowedLogin) { $allowedLogins += $AllowedLogin }

    $serveStatus = (& $tailscale serve status 2>&1 | Out-String)
    $funnelStatus = (& $tailscale funnel status 2>&1 | Out-String)
    if (Test-FunnelForLocalPort $funnelStatus $httpPort) {
        throw 'Für den App-Port ist bereits ein Tailscale Funnel konfiguriert. Entferne diese öffentliche Freigabe in Tailscale, bevor du das private Setup fortsetzt.'
    }
    $usedPorts = @{}
    foreach ($match in [regex]::Matches($serveStatus, 'https?://[^/\s:]+:(\d+)')) { $usedPorts[$match.Groups[1].Value] = $true }
    foreach ($match in [regex]::Matches($funnelStatus, 'https?://[^/\s:]+:(\d+)')) { $usedPorts[$match.Groups[1].Value] = $true }
    $httpsPort = 0
    $savedRouteIsActive = $false
    if ($savedSettings -and $savedSettings.tailscaleHttpsPort) {
        $savedPort = [int]$savedSettings.tailscaleHttpsPort
        $savedRouteIsActive = [regex]::IsMatch($serveStatus, "(?ms)https://[^`r`n]*:$savedPort\b[^`r`n]*(?:`r?`n[^`r`n]*){0,3}127\.0\.0\.1:$httpPort\b")
        if ($savedRouteIsActive -or -not $usedPorts.ContainsKey([string]$savedPort)) { $httpsPort = $savedPort }
    }
    if ($httpsPort -eq 0) {
        $httpsPort = 8443
        while ($usedPorts.ContainsKey([string]$httpsPort) -and $httpsPort -lt 8500) { $httpsPort++ }
    }
    if ($httpsPort -lt 1 -or $httpsPort -gt 65535 -or ($usedPorts.ContainsKey([string]$httpsPort) -and -not $savedRouteIsActive)) {
        throw 'Zwischen 8443 und 8500 ist kein freier HTTPS-Port für Tailscale Serve verfügbar. Bestehende Serve-Einträge wurden nicht verändert.'
    }

    if ($PSBoundParameters.ContainsKey('ShareAllDrives')) {
        $shareEverything = $ShareAllDrives
    }
    elseif ($savedSettings) {
        $shareEverything = [bool]$savedSettings.ShareAllDrives
    }
    else {
        $answer = Read-Host 'Alle lokalen, USB- und Netzlaufwerke freigeben? (J/n; Enter = Ja)'
        $shareEverything = $answer -notmatch '^(n|nein)$'
    }

    if ($ShareRoot.Count -gt 0) {
        $roots = @($ShareRoot | ForEach-Object { [pscustomobject]@{ Path = [IO.Path]::GetFullPath($_); ReadOnly = [bool]$ReadOnly; Label = (Split-Path -Path $_ -Leaf) } })
    }
    elseif ($savedSettings -and -not $PSBoundParameters.ContainsKey('ShareRoot')) {
        $roots = @($savedSettings.Roots)
    }
    elseif ($shareEverything) {
        $roots = @()
    }
    else {
        $defaultRoot = $env:USERPROFILE
        $rootText = Read-Host "Freigabepfade, mit Semikolon getrennt (Enter = $defaultRoot)"
        $selectedRoots = if ([string]::IsNullOrWhiteSpace($rootText)) { @($defaultRoot) } else { @($rootText -split ';' | ForEach-Object { $_.Trim() }) }
        $roots = @($selectedRoots | ForEach-Object {
            if (-not (Test-Path -LiteralPath $_ -PathType Container)) { throw "Freigabeordner nicht gefunden: $_" }
            [pscustomobject]@{ Path = [IO.Path]::GetFullPath($_); ReadOnly = [bool]$ReadOnly; Label = (Split-Path -Path $_ -Leaf) }
        })
    }

    if ($savedSettings -and -not $PSBoundParameters.ContainsKey('ReadOnly')) { $readOnlyMode = [bool]$savedSettings.ReadOnlyMode }
    else { $readOnlyMode = [bool]$ReadOnly }
    if ($savedSettings -and $null -ne $savedSettings.protectSystemFolders) { $protectSystemFolders = [bool]$savedSettings.protectSystemFolders }
    else { $protectSystemFolders = $true }

    if (-not $ConfigureOnly) {
        Build-PublishFolder
        if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'wwwroot\index.html'))) { throw 'Die veröffentlichte PWA fehlt. Abbruch vor der Installation.' }
    }
    elseif (-not (Test-Path -LiteralPath $exePath)) { throw 'Die App ist noch nicht installiert. Bitte das vollständige Setup starten.' }
    $watchdogProcesses = @(Get-CimInstance Win32_Process -Filter "Name='Explorer.Host.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -eq $exePath -and $_.CommandLine -match '(?:^|\s)--watchdog(?:\s|$)' })
    $runningApp = Get-CimInstance Win32_Process -Filter "Name='Explorer.Host.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -eq $exePath -and $_.CommandLine -notmatch '(?:^|\s)--watchdog(?:\s|$)' }
    $otherApp = Get-CimInstance Win32_Process -Filter "Name='Explorer.Host.exe'" -ErrorAction SilentlyContinue |
        Where-Object { -not $_.ExecutablePath -or -not $_.ExecutablePath.StartsWith($installRoot, [StringComparison]::OrdinalIgnoreCase) }
    if ($otherApp) { throw 'Eine andere Instanz von Explorer.Host.exe läuft. Beende sie vor der Installation.' }

    if ($runningApp) {
        $statusLogin = $AllowedLogin
        if ($savedSettings -and @($savedSettings.allowedLogins).Count -gt 0 -and
            @($savedSettings.allowedLogins) -notcontains $AllowedLogin) {
            $statusLogin = [string]@($savedSettings.allowedLogins)[0]
        }
        $identityHeaders = @{ 'Tailscale-User-Login' = $statusLogin }
        try {
            $jobSnapshots = @(Invoke-RestMethod -Uri "http://127.0.0.1:$httpPort/api/v1/jobs" -Headers $identityHeaders -TimeoutSec 4)
            $uploadSessions = @(Invoke-RestMethod -Uri "http://127.0.0.1:$httpPort/api/v1/uploads" -Headers $identityHeaders -TimeoutSec 4)
        }
        catch { throw 'Die laufende App konnte vor dem Update nicht auf aktive Datei- und Uploadvorgänge geprüft werden. Sie wurde nicht verändert.' }
        $activeJobs = @($jobSnapshots | Where-Object { $_.status -in @('queued', 'running', 'paused', 'conflict') })
        $activeUploads = @($uploadSessions | Where-Object { $_.state -in @('uploading', 'ready') })
        if ($activeJobs.Count -gt 0 -or $activeUploads.Count -gt 0) {
            throw "Das Update wartet: Es laufen noch $($activeJobs.Count) Datei- und $($activeUploads.Count) Uploadvorgänge. Beende oder pausiere sie und starte setup.ps1 erneut."
        }
        foreach ($processInfo in $watchdogProcesses) { Stop-Process -Id $processInfo.ProcessId -Force }
        $watchdogProcesses = @()
        foreach ($processInfo in $runningApp) { Stop-Process -Id $processInfo.ProcessId -Force }
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            $stillRunning = Get-CimInstance Win32_Process -Filter "Name='Explorer.Host.exe'" -ErrorAction SilentlyContinue |
                Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($installRoot, [StringComparison]::OrdinalIgnoreCase) }
            if (-not $stillRunning) { break }
            Start-Sleep -Milliseconds 250
        }
        if ($stillRunning) { throw 'Die Tray-App konnte für das Update nicht beendet werden. Die Installation wurde abgebrochen.' }
    }

    foreach ($processInfo in $watchdogProcesses) { Stop-Process -Id $processInfo.ProcessId -Force }
    New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
    if (-not $ConfigureOnly) { Copy-Item -Path (Join-Path $publishRoot '*') -Destination $installRoot -Recurse -Force }
    if (-not (Test-Path -LiteralPath $exePath)) { throw 'Explorer.Host.exe wurde nicht korrekt installiert.' }

    New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $settingsPath) {
        $backupPath = "$settingsPath.backup-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        Copy-Item -LiteralPath $settingsPath -Destination $backupPath
    }
    $pinHash = $null
    if ($savedSettings -and $savedSettings.pinHash) { $pinHash = [string]$savedSettings.pinHash }
    # Keep favorites, display preferences and future settings when reconfiguring.
    $settings = [ordered]@{}
    if ($savedSettings) {
        foreach ($property in $savedSettings.PSObject.Properties) { $settings[$property.Name] = $property.Value }
    }
    $settings['port'] = $httpPort
    $settings['tailscaleHttpsPort'] = $httpsPort
    $settings['allowedLogins'] = $allowedLogins
    $settings['shareAllDrives'] = $shareEverything
    $settings['roots'] = $roots
    $settings['readOnlyMode'] = $readOnlyMode
    $settings['protectSystemFolders'] = $protectSystemFolders
    $settings['pinHash'] = $pinHash
    $temporarySettings = "$settingsPath.tmp"
    [IO.File]::WriteAllText($temporarySettings, ($settings | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporarySettings -Destination $settingsPath -Force

    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $runKey -Force | Out-Null
    if ($Autostart) {
        $startupArgs = if ($Watchdog) { ' --watchdog' } else { '' }
        Set-ItemProperty -Path $runKey -Name 'WindowsMobileExplorer' -Value ('"{0}"{1}' -f $exePath, $startupArgs)
    }
    else { Remove-ItemProperty -Path $runKey -Name 'WindowsMobileExplorer' -ErrorAction SilentlyContinue }

    $process = Start-Process -FilePath $exePath -WorkingDirectory $installRoot -WindowStyle Hidden -PassThru
    $healthUri = "http://127.0.0.1:$httpPort/api/v1/health"
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        if ($process -and $process.HasExited) { throw 'Die Tray-App wurde beim Start beendet. Prüfe die Protokolle unter %LOCALAPPDATA%\WindowsMobileExplorer\logs.' }
        try {
            $health = Invoke-RestMethod -Uri $healthUri -Headers @{ 'Tailscale-User-Login' = $AllowedLogin } -TimeoutSec 2
            if ($health.ok) { $ready = $true; break }
        }
        catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $ready) { throw 'Der lokale Server wurde nicht bereit. Es wurde kein Tailscale-Proxy eingerichtet.' }
    if ($Watchdog) { Start-Process -FilePath $exePath -ArgumentList "--watchdog $($process.Id)" -WorkingDirectory $installRoot -WindowStyle Hidden | Out-Null }

    $serveStart = [System.Diagnostics.ProcessStartInfo]::new()
    $serveStart.FileName = $tailscale
    $serveStart.Arguments = "serve --bg --yes --https=$httpsPort http://127.0.0.1:$httpPort"
    $serveStart.UseShellExecute = $false
    $serveStart.CreateNoWindow = $true
    $serveStart.RedirectStandardOutput = $true
    $serveStart.RedirectStandardError = $true
    $serveProcess = [System.Diagnostics.Process]::new()
    $serveProcess.StartInfo = $serveStart
    try {
        if (-not $serveProcess.Start()) { throw 'Tailscale Serve konnte nicht gestartet werden.' }
        $stdoutTask = $serveProcess.StandardOutput.ReadToEndAsync()
        $stderrTask = $serveProcess.StandardError.ReadToEndAsync()
        $serveExited = $serveProcess.WaitForExit(10000)
        if (-not $serveExited) {
            $serveProcess.Kill()
            $serveProcess.WaitForExit()
        }
        $serveOutput = (@($stdoutTask.GetAwaiter().GetResult(), $stderrTask.GetAwaiter().GetResult()) | Where-Object { $_ }) -join "`n"
        if ($serveOutput -match 'Serve is not enabled on your tailnet') {
            $approvalLink = [regex]::Match($serveOutput, 'https://login\.tailscale\.com/f/serve\?[^\s]+').Value
            $linkText = if ($approvalLink) { " Öffne $approvalLink, aktiviere Serve für dieses Tailnet und führe setup.ps1 danach erneut aus." } else { ' Aktiviere Serve im Tailnet und führe setup.ps1 danach erneut aus.' }
            throw "Tailscale Serve ist noch nicht für dieses Tailnet freigegeben.$linkText Die App bleibt bis dahin ausschließlich auf 127.0.0.1 erreichbar."
        }
        if (-not $serveExited) { throw 'Tailscale Serve hat innerhalb von 10 Sekunden nicht geantwortet. Der Setup-Aufruf wurde beendet; es wurde kein Funnel eingerichtet.' }
        if ($serveProcess.ExitCode -ne 0) {
            throw "Tailscale Serve konnte nicht eingerichtet werden. Die App bleibt ausschließlich auf 127.0.0.1 erreichbar. Tailscale meldet: $serveOutput"
        }
    }
    finally { $serveProcess.Dispose() }

    $confirmedServe = (& $tailscale serve status 2>&1 | Out-String)
    if (-not [regex]::IsMatch($confirmedServe, "(?ms)https://[^`r`n]*:$httpsPort\b[^`r`n]*(?:`r?`n[^`r`n]*){0,3}127\.0\.0\.1:$httpPort\b")) {
        throw 'Tailscale Serve hat den Proxy nicht bestätigt. Die App bleibt ausschließlich auf 127.0.0.1 erreichbar.'
    }
    $confirmedFunnel = (& $tailscale funnel status 2>&1 | Out-String)
    if (Test-FunnelForLocalPort $confirmedFunnel $httpPort) {
        throw 'Die Tailscale-Statusprüfung hat einen Funnel für den App-Port gefunden. Richte den privaten Zugang nicht ein, bevor diese öffentliche Route entfernt wurde.'
    }

    $tailnetStatus = Get-TailscaleStatus $tailscale
    $dnsName = [string]$tailnetStatus.Self.DNSName
    $dnsName = $dnsName.TrimEnd('.')
    $address = if ($dnsName) { "https://$dnsName`:$httpsPort/" } else { "https://<MagicDNS-Name>:$httpsPort/" }
    Write-Host ''
    Write-Host 'Installation abgeschlossen.' -ForegroundColor Green
    Write-Host "Freigegebene Tailscale-Anmeldung: $AllowedLogin"
    Write-Host "Tailscale-Adresse: $address"
    Write-Host "Konfiguration: $settingsPath"
    Write-Host 'Der Server lauscht nur auf 127.0.0.1. Es wurde kein Funnel eingerichtet.'
    Write-Host 'Öffne die HTTPS-Adresse in Safari und wähle Teilen > Zum Home-Bildschirm.'
}
catch {
    Write-Error $_
    exit 1
}
