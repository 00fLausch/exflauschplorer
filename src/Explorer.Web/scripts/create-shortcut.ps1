param(
    [Parameter(Mandatory = $true)][string]$ShortcutPath,
    [Parameter(Mandatory = $true)][string]$TargetPath
)

$ErrorActionPreference = 'Stop'
$shell = $null
$shortcut = $null
try {
    $shellType = [Type]::GetTypeFromProgID('WScript.Shell')
    if (-not $shellType) { throw 'Windows Shell-Verknüpfungsfunktion nicht verfügbar.' }
    $shell = [Activator]::CreateInstance($shellType)
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.Save()
}
finally {
    if ($shortcut -and [Runtime.InteropServices.Marshal]::IsComObject($shortcut)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
    }
    if ($shell -and [Runtime.InteropServices.Marshal]::IsComObject($shell)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }
}
