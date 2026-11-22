# Run in Windows PowerShell 5.1. Every filesystem and process dependency is isolated.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$base = [IO.Path]::GetFullPath((Join-Path $repo ('artifacts\setup-test-' + [Guid]::NewGuid().ToString('N'))))
$artifacts = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts')).TrimEnd('\')
if (-not $base.StartsWith($artifacts + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Ungültiges Testverzeichnis.' }
[IO.Directory]::CreateDirectory((Join-Path $base 'bin')) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $base 'package\publish\win-x64\wwwroot')) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $base 'profile\WindowsMobileExplorer')) | Out-Null
$fakeTailscale = @'
using System;
public class FakeTailscale {
  public static int Main(string[] args) {
    if (args.Length > 0 && args[0] == "status") Console.Write("{\"BackendState\":\"Running\",\"Self\":{\"UserID\":123,\"DNSName\":\"demo.example.ts.net.\"},\"User\":{\"123\":{\"LoginName\":\"demo@example.com\"}}}");
    else if (args.Length > 1 && args[0] == "serve" && args[1] == "status") Console.Write("https://demo.example.ts.net:8443\n |-- / proxy http://127.0.0.1:5180\n");
    return 0;
  }
}
'@
$fakeHost = @'
using System;
using System.IO;
using System.Threading;
using System.Diagnostics;
public class FakeHost {
  public static void Main() {
    File.AppendAllText(Environment.GetEnvironmentVariable("WME_TEST_PIDS"), Process.GetCurrentProcess().Id + "\n");
    Thread.Sleep(30000);
  }
}
'@
Add-Type -TypeDefinition $fakeTailscale -OutputAssembly (Join-Path $base 'bin\tailscale.exe') -OutputType ConsoleApplication
Add-Type -TypeDefinition $fakeHost -OutputAssembly (Join-Path $base 'package\publish\win-x64\Explorer.Host.exe') -OutputType ConsoleApplication
[IO.File]::WriteAllText((Join-Path $base 'package\publish\win-x64\wwwroot\index.html'), 'demo')
Copy-Item -LiteralPath (Join-Path $repo 'setup.ps1') -Destination (Join-Path $base 'package\setup.ps1')
$settingsPath = Join-Path $base 'profile\WindowsMobileExplorer\settings.json'
[IO.File]::WriteAllText($settingsPath, '{"allowedLogins":["demo@example.com"],"tailscaleHttpsPort":8443,"shareAllDrives":true,"protectSystemFolders":true,"favoritesByLogin":{"demo@example.com":["C:\\Demo"]},"futureOption":"keep"}')
$env:LOCALAPPDATA = Join-Path $base 'profile'
$env:PATH = (Join-Path $base 'bin') + ';' + $env:PATH
$env:WME_TEST_PIDS = Join-Path $base 'pids.txt'
$global:wmeTestStartupRemoved = $false
function Get-CimInstance { @() }
function Invoke-RestMethod { param($Uri, $Headers, $TimeoutSec) return @{ ok = $true } }
function New-Item {
    param($ItemType, $Path, [switch]$Force)
    if ($Path -like 'HKCU:*') { return }
    Microsoft.PowerShell.Management\New-Item @PSBoundParameters
}
function Remove-ItemProperty { param($Path, $Name, $ErrorAction) $global:wmeTestStartupRemoved = $true }
try {
    & (Join-Path $base 'package\setup.ps1') -AllowedLogin 'demo@example.com' -ShareAllDrives $true -Autostart $false -Watchdog $true | Out-Null
    if (-not $global:wmeTestStartupRemoved) { throw 'Autostart-Auswahl wurde nicht übernommen.' }
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    if ($settings.futureOption -ne 'keep' -or $settings.favoritesByLogin.'demo@example.com'[0] -ne 'C:\Demo') { throw 'Einstellungen wurden nicht erhalten.' }
    if (-not (Test-Path -LiteralPath (Join-Path $base 'profile\Programs\WindowsMobileExplorer\wwwroot\index.html'))) { throw 'App-Dateien fehlen.' }
    $fakeIds = @()
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        if (Test-Path -LiteralPath $env:WME_TEST_PIDS) { $fakeIds = @(Get-Content -LiteralPath $env:WME_TEST_PIDS) }
        if ($fakeIds.Count -ge 2) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($fakeIds.Count -lt 2) { throw 'App und Wächter wurden nicht gestartet.' }
    Write-Output 'Isolierte Einrichtung mit simuliertem Tailscale, Wächter und vorhandenen Einstellungen erfolgreich.'
} finally {
    # Stop only fake executables created by this test.
    if (Test-Path -LiteralPath $env:WME_TEST_PIDS) {
        foreach ($idText in (Get-Content -LiteralPath $env:WME_TEST_PIDS)) {
            $process = Get-Process -Id ([int]$idText) -ErrorAction SilentlyContinue
            if ($process -and $process.Path -and $process.Path.StartsWith($base + '\', [StringComparison]::OrdinalIgnoreCase)) { Stop-Process -Id $process.Id -Force }
        }
    }
}
