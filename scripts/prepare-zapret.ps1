$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$lock = Get-Content -LiteralPath (Join-Path $projectRoot 'payload/zapret.lock.json') -Raw | ConvertFrom-Json
$cacheRoot = Join-Path $projectRoot '.tools/zapret-downloads'
New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null
$archive = Join-Path $cacheRoot ($lock.sha256 + '.zip')
if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $lock.url -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $lock.sha256) { throw 'Zapret release SHA256 mismatch' }
$destination = Join-Path $cacheRoot ('verified-' + $lock.sha256)
if (-not (Test-Path -LiteralPath (Join-Path $destination '.complete'))) {
    $finalDestination = $destination
    if (Test-Path -LiteralPath $finalDestination) { throw 'An incomplete Zapret cache exists. Rename it before retrying.' }
    $destination = Join-Path $cacheRoot ('verified-' + $lock.sha256 + '-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $destination | Out-Null
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $total = 0L
        $prefix = [IO.Path]::GetFullPath($destination).TrimEnd('\') + '\'
        foreach ($entry in $zip.Entries) {
            $total += $entry.Length
            if ($total -gt 100MB -or $entry.FullName -match '(^[\\/]|:|(^|[\\/])\.\.([\\/]|$))' -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Unsafe Zapret archive' }
            $path = [IO.Path]::GetFullPath((Join-Path $destination $entry.FullName))
            if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive path escapes staging' }
            if ($entry.FullName.EndsWith('/') -or $entry.FullName.EndsWith('\')) { New-Item -ItemType Directory -Path $path -Force | Out-Null; continue }
            New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $false)
        }
    }
    finally { $zip.Dispose() }
    Set-Content -LiteralPath (Join-Path $destination '.complete') -Value $lock.sha256 -NoNewline
    $cachePrefix = [IO.Path]::GetFullPath($cacheRoot).TrimEnd('\') + '\'
    if (-not [IO.Path]::GetFullPath($destination).StartsWith($cachePrefix, [StringComparison]::OrdinalIgnoreCase) -or -not [IO.Path]::GetFullPath($finalDestination).StartsWith($cachePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Cache move escapes its directory' }
    Move-Item -LiteralPath $destination -Destination $finalDestination
    $destination = $finalDestination
}
$binary = @(Get-ChildItem -LiteralPath $destination -Recurse -Filter winws.exe -File)
if ($binary.Count -ne 1) { throw 'Expected one winws.exe in pinned Zapret release' }
$payloadRoot = Split-Path -Parent (Split-Path -Parent $binary[0].FullName)
[pscustomobject]@{ Directory = $payloadRoot; Version = $lock.version; Commit = $lock.commit; Sha256 = $lock.sha256 }
