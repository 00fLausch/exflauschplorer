[CmdletBinding()]
param([string]$SetupDirectory = $PSScriptRoot, [switch]$ConfigureOnly, [switch]$SmokeTest)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()

$form = [Windows.Forms.Form]@{ Text = 'EXfLauschPLORER – Einrichtung'; ClientSize = '660,590'; StartPosition = 'CenterScreen'; Font = [Drawing.Font]::new('Segoe UI', 10); FormBorderStyle = 'FixedDialog'; MaximizeBox = $false }
function Label([string]$text, [int]$x, [int]$y, [int]$width, [int]$height = 25) {
    $control = [Windows.Forms.Label]@{ Text = $text; Location = "$x,$y"; Size = "$width,$height" }; $form.Controls.Add($control); return $control
}
function Button([string]$text, [int]$x, [int]$y, [int]$width) {
    $control = [Windows.Forms.Button]@{ Text = $text; Location = "$x,$y"; Size = "$width,34" }; $form.Controls.Add($control); return $control
}
function Link([string]$text, [string]$url, [int]$x, [int]$y, [int]$width) {
    $control = [Windows.Forms.LinkLabel]@{ Text = $text; Location = "$x,$y"; Size = "$width,25"; Tag = $url }
    $control.Add_LinkClicked({ param($sender) Start-Process -FilePath $sender.Tag }); $form.Controls.Add($control)
}
$title = Label 'Dein PC. Deine Dateien. Auch auf dem Handy.' 22 16 620 36
$title.Font = [Drawing.Font]::new('Segoe UI Semibold', 15)
$info = Label "Tailscale verbindet deine Geräte privat. Melde diesen Windows-PC und dein Handy im selben Tailnet an. Aktiviere bei Bedarf MagicDNS, HTTPS und Tailscale Serve in der Tailscale-Verwaltung." 24 60 610 65
Link 'Tailscale für Windows' 'https://tailscale.com/download/windows' 24 128 190
Link 'Tailscale für iPhone / iPad' 'https://tailscale.com/download/ios' 224 128 205
Link 'Hilfe zur Einrichtung' 'https://tailscale.com/docs/install' 438 128 190
$status = Label 'Tailscale wird geprüft …' 24 166 470 27
$check = Button 'Erneut prüfen' 500 158 135
$loginLabel = Label 'Tailscale-Anmeldename:' 24 201 205
$login = [Windows.Forms.TextBox]@{ Location = '230,198'; Size = '405,27' }; $form.Controls.Add($login)
$allDrives = [Windows.Forms.CheckBox]@{ Text = 'Alle Laufwerke freigeben (später in der App änderbar)'; Location = '24,235'; Size = '610,28'; Checked = $true }; $form.Controls.Add($allDrives)
$autostart = [Windows.Forms.CheckBox]@{ Text = 'Mit Windows automatisch starten'; Location = '24,269'; Size = '610,28'; Checked = $true }; $form.Controls.Add($autostart)
$watchdog = [Windows.Forms.CheckBox]@{ Text = 'Bei einem Absturz automatisch neu starten'; Location = '24,303'; Size = '610,28'; Checked = $true }; $form.Controls.Add($watchdog)
$savedSettingsPath = Join-Path $env:LOCALAPPDATA 'WindowsMobileExplorer\settings.json'
$script:existingShareAll = $null
if (Test-Path -LiteralPath $savedSettingsPath) {
    try {
        $previous = Get-Content -LiteralPath $savedSettingsPath -Raw | ConvertFrom-Json
        $script:existingShareAll = [bool]$previous.shareAllDrives
        $allDrives.Checked = $script:existingShareAll
        $runValue = (Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name WindowsMobileExplorer -ErrorAction SilentlyContinue).WindowsMobileExplorer
        $autostart.Checked = -not [string]::IsNullOrWhiteSpace($runValue)
        $watchdog.Checked = $runValue -match '--watchdog'
    } catch { }
}
$hint = Label 'Auf dem iPhone: Tailscale installieren, anmelden und verbinden. Nach der Einrichtung die angezeigte Adresse in Safari öffnen und über „Teilen → Zum Home-Bildschirm“ hinzufügen.' 24 341 610 66
$result = [Windows.Forms.TextBox]@{ Location = '24,408'; Size = '610,105'; Multiline = $true; ReadOnly = $true; ScrollBars = 'Vertical' }; $form.Controls.Add($result)
$start = Button 'Einrichtung starten' 414 530 220
$close = Button 'Schließen' 274 530 125
$close.Add_Click({ $form.Close() })
$form.Add_FormClosing({ param($sender, $event) if ($script:setupProcess -and -not $script:setupProcess.HasExited) { $event.Cancel = $true } })

function Check-Tailscale {
    $command = Get-Command tailscale.exe -ErrorAction SilentlyContinue
    $exe = if ($command) { $command.Source } else { Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe' }
    if (-not (Test-Path -LiteralPath $exe)) { $status.Text = 'Tailscale: nicht installiert'; $status.ForeColor = [Drawing.Color]::Firebrick; return }
    try {
        $raw = & $exe status --json 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'offline' }
        $state = ($raw -join "`n") | ConvertFrom-Json
        if ($state.BackendState -ne 'Running') { throw 'offline' }
        $status.Text = 'Tailscale: verfügbar und online'; $status.ForeColor = [Drawing.Color]::SeaGreen
        if (-not $login.Text -and $state.Self.UserID -and $state.User) {
            $account = $state.User.PSObject.Properties[[string]$state.Self.UserID].Value
            if ($account.LoginName) { $login.Text = [string]$account.LoginName }
        }
    } catch { $status.Text = 'Tailscale: installiert, aber nicht verbunden'; $status.ForeColor = [Drawing.Color]::DarkOrange }
}
$check.Add_Click({ Check-Tailscale })
$script:setupProcess = $null
$timer = [Windows.Forms.Timer]@{ Interval = 500 }
$timer.Add_Tick({
    if (-not $script:setupProcess.HasExited) { return }
    $timer.Stop(); $start.Enabled = $true; $check.Enabled = $true; $close.Enabled = $true
    $text = ''
    if (Test-Path -LiteralPath $script:outputFile) { $text += Get-Content -LiteralPath $script:outputFile -Raw -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $script:errorFile) { $text += "`r`n" + (Get-Content -LiteralPath $script:errorFile -Raw -ErrorAction SilentlyContinue) }
    $result.Text = if ($script:setupProcess.ExitCode -eq 0) { "Fertig! Öffne die angezeigte Tailscale-Adresse auf deinem Handy.`r`n$text" } else { "Die Einrichtung ist noch nicht abgeschlossen. Bitte Hinweise prüfen und erneut starten.`r`n$text" }
})
$start.Add_Click({
    if ($login.Text -notmatch '^[^\s@]+@[^\s@]+$') { [Windows.Forms.MessageBox]::Show('Bitte Tailscale verbinden und den Anmeldenamen als E-Mail-Adresse eintragen.', 'Tailscale') | Out-Null; return }
    $setup = Join-Path $SetupDirectory 'setup.ps1'
    if (-not (Test-Path -LiteralPath $setup)) { [Windows.Forms.MessageBox]::Show('Die Setup-Datei wurde nicht gefunden.', 'Einrichtung') | Out-Null; return }
    $script:outputFile = Join-Path $env:TEMP 'exflausch-setup-output.txt'; $script:errorFile = Join-Path $env:TEMP 'exflausch-setup-error.txt'
    $wrapper = Join-Path $env:TEMP ('exflausch-setup-' + [Guid]::NewGuid().ToString('N') + '.ps1')
    $quotedSetup = $setup.Replace("'", "''"); $quotedLogin = $login.Text.Trim().Replace("'", "''")
    $all = if ($allDrives.Checked) { '$true' } else { '$false' }; $auto = if ($autostart.Checked) { '$true' } else { '$false' }; $watch = if ($watchdog.Checked) { '$true' } else { '$false' }
    $share = if ($null -ne $script:existingShareAll -and $allDrives.Checked -eq $script:existingShareAll) { '' }
        elseif ($allDrives.Checked) { " -ShareAllDrives $all" }
        else { " -ShareAllDrives $all -ShareRoot `$env:USERPROFILE" }
    $configure = if ($ConfigureOnly) { ' -ConfigureOnly' } else { '' }
    [IO.File]::WriteAllText($wrapper, "& '$quotedSetup' -AllowedLogin '$quotedLogin'$share -Autostart $auto -Watchdog $watch$configure`r`nexit `$LASTEXITCODE", [Text.UTF8Encoding]::new($true))
    $result.Text = 'Einrichtung läuft …'; $start.Enabled = $false; $check.Enabled = $false; $close.Enabled = $false
    $script:setupProcess = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"{0}"' -f $wrapper)) -WindowStyle Hidden -RedirectStandardOutput $script:outputFile -RedirectStandardError $script:errorFile -PassThru
    $timer.Start()
})
Check-Tailscale
if ($SmokeTest) { Write-Output 'Einrichtungsdialog und Tailscale-Prüfung initialisiert.'; $form.Dispose() }
else { [void]$form.ShowDialog() }
