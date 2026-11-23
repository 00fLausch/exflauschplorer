param(
    [string]$BaseUrl
)

$ErrorActionPreference = 'Stop'

$settingsPath = Join-Path $env:LOCALAPPDATA 'WindowsMobileExplorer\settings.json'
if (-not (Test-Path -LiteralPath $settingsPath)) {
    throw 'Die lokale Explorer-Konfiguration wurde nicht gefunden.'
}

$settings = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    if ([string]::IsNullOrWhiteSpace($settings.TailscaleHostName)) {
        throw 'Fuehre die Einrichtung erneut aus oder gib die MagicDNS-HTTPS-Adresse mit BaseUrl an.'
    }
    $BaseUrl = ([UriBuilder]::new('https', [string]$settings.TailscaleHostName, [int]$settings.TailscaleHttpsPort)).Uri.AbsoluteUri
}
$baseUri = [Uri]$BaseUrl
if (-not $baseUri.IsAbsoluteUri -or $baseUri.Scheme -ne 'https' -or
    -not $baseUri.DnsSafeHost.EndsWith('.ts.net', [StringComparison]::OrdinalIgnoreCase) -or
    $baseUri.AbsolutePath -ne '/' -or $baseUri.Query -or $baseUri.Fragment -or $baseUri.UserInfo) {
    throw 'BaseUrl muss eine reine MagicDNS-HTTPS-Adresse sein.'
}
if ($baseUri.Port -ne $settings.TailscaleHttpsPort) {
    throw 'Der HTTPS-Port in BaseUrl stimmt nicht mit der lokalen App-Konfiguration ueberein.'
}

$tailscaleExe = (Get-Command tailscale.exe -ErrorAction SilentlyContinue).Source
if ([string]::IsNullOrWhiteSpace($tailscaleExe)) {
    $tailscalePath = Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'
    if (Test-Path -LiteralPath $tailscalePath) { $tailscaleExe = $tailscalePath }
}
if ([string]::IsNullOrWhiteSpace($tailscaleExe)) { throw 'Tailscale wurde nicht gefunden.' }
$statusJson = & $tailscaleExe status --json 2>$null
if ($LASTEXITCODE -ne 0) { throw 'Tailscale antwortet nicht.' }
$status = ($statusJson -join "`n") | ConvertFrom-Json
$localDnsName = ([string]$status.Self.DNSName).TrimEnd('.')
if (-not [string]::Equals($baseUri.DnsSafeHost, $localDnsName, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'BaseUrl muss exakt auf den MagicDNS-Namen dieses PCs zeigen, damit kein lokaler Pfad an ein fremdes Geraet gesendet wird.'
}
if ($settings.PinHash) {
    throw 'Das Range-Testskript braucht eine Testinstallation ohne PIN oder eine authentifizierte Browserpruefung.'
}

$temporaryRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
$testDirectory = [IO.Path]::GetFullPath((Join-Path $env:TEMP ("WME-RangeProbe-" + [Guid]::NewGuid().ToString('N'))))
if (-not $testDirectory.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Der Testordner liegt nicht innerhalb von %TEMP%.'
}
if (-not $settings.ShareAllDrives -and -not @($settings.Roots | Where-Object {
    $_.Path -and $temporaryRoot.StartsWith([IO.Path]::GetFullPath($_.Path).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}).Count) {
    throw 'Dieser Test braucht eine Freigabe, die %TEMP% umfasst.'
}

$videoPath = Join-Path $testDirectory 'sparse-video.mp4'
$size = [long]5000000000
$null = Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()

try {
    [IO.Directory]::CreateDirectory($testDirectory) | Out-Null
    $created = [IO.File]::Create($videoPath)
    $created.Dispose()

    & (Join-Path $env:windir 'System32\fsutil.exe') sparse setflag $videoPath | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Windows konnte die temporäre Datei nicht als sparse markieren.'
    }

    $file = [IO.File]::Open($videoPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read)
    try {
        $file.SetLength($size)
        $startBytes = [Text.Encoding]::ASCII.GetBytes('WME0')
        $endBytes = [Text.Encoding]::ASCII.GetBytes('WME5')
        $file.Position = 0
        $file.Write($startBytes, 0, $startBytes.Length)
        $file.Position = $size - $endBytes.Length
        $file.Write($endBytes, 0, $endBytes.Length)
        $file.Flush()
    }
    finally {
        $file.Dispose()
    }

    $escapedPath = [Uri]::EscapeDataString($videoPath)
    $url = "$($baseUri.GetLeftPart([UriPartial]::Authority))/api/v1/stream?path=$escapedPath"
    $ranges = @(
        @{ Start = 0L; End = 3L; Expected = 'WME0' },
        @{ Start = ($size - 4L); End = ($size - 1L); Expected = 'WME5' }
    )
    $checks = foreach ($range in $ranges) {
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $url)
        try {
            $request.Headers.Range = [System.Net.Http.Headers.RangeHeaderValue]::new([Nullable[long]]$range.Start, [Nullable[long]]$range.End)
            $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            try {
                if ($response.StatusCode -ne [System.Net.HttpStatusCode]::PartialContent -or $response.Content.Headers.ContentLength -ne 4) {
                    throw "Die Range-Anfrage lieferte keine vier Bytes (HTTP $([int]$response.StatusCode))."
                }
                $body = [Text.Encoding]::ASCII.GetString($response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult())
                $contentRange = $response.Content.Headers.ContentRange
                [pscustomobject]@{
                    Valid = $response.StatusCode -eq [System.Net.HttpStatusCode]::PartialContent -and
                        $contentRange.From -eq $range.Start -and $contentRange.To -eq $range.End -and
                        $contentRange.Length -eq $size -and $body -eq $range.Expected
                }
            }
            finally {
                $response.Dispose()
            }
        }
        finally {
            $request.Dispose()
        }
    }

    if ($checks.Count -ne 2 -or @($checks | Where-Object { -not $_.Valid }).Count -gt 0) {
        throw 'Mindestens eine 5-GB-Range-Anfrage war nicht korrekt.'
    }

    [pscustomobject]@{
        SizeBytes = $size
        SparseFile = $true
        InitialRange = $checks[0].Valid
        FinalRange = $checks[1].Valid
        Transport = 'Tailscale HTTPS Serve'
    } | ConvertTo-Json -Compress
}
finally {
    $client.Dispose()
    if (Test-Path -LiteralPath $videoPath) {
        [IO.File]::Delete($videoPath)
    }
    if ((Test-Path -LiteralPath $testDirectory) -and
        ((Get-ChildItem -LiteralPath $testDirectory -Force | Measure-Object).Count -eq 0)) {
        [IO.Directory]::Delete($testDirectory)
    }
}
