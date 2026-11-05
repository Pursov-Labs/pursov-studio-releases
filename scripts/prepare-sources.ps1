$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$lock = Get-Content -LiteralPath (Join-Path $projectRoot 'payload/sources.lock.json') -Raw | ConvertFrom-Json
$cache = Join-Path $projectRoot '.tools/third-party-sources'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
foreach ($source in $lock.sources) {
    if ($source.file -ne [IO.Path]::GetFileName($source.file)) { throw 'Invalid source archive filename' }
    $archive = Join-Path $cache $source.file
    if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $source.url -OutFile $archive -TimeoutSec 180 }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $source.sha256) { throw ('Source checksum mismatch: ' + $source.name) }
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($license in $source.licenses) {
            if ($license.output -ne [IO.Path]::GetFileName($license.output)) { throw 'Invalid license output filename' }
            $entry = $zip.GetEntry($license.entry)
            if (-not $entry -or $entry.Length -gt 1MB) { throw ('Missing license: ' + $source.name) }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $projectRoot ('payload/licenses/' + $license.output)), $true)
        }
    }
    finally { $zip.Dispose() }
}
Write-Output 'Pinned third-party source archives and original license notices verified.'
