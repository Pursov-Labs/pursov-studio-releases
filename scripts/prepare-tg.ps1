param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem
$lock = Get-Content -LiteralPath (Join-Path $ProjectRoot 'runtime/tg-dependencies.lock.json') -Raw | ConvertFrom-Json
$cache = Join-Path $ProjectRoot '.tools/tg-downloads'
$runtime = Join-Path $ProjectRoot 'runtime/python'
New-Item -ItemType Directory -Force -Path $cache, $runtime | Out-Null
function Get-Verified([string]$Url, [string]$Path, [string]$Hash) {
    if (!(Test-Path -LiteralPath $Path) -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Hash) {
        Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile ($Path + '.download')
        if ((Get-FileHash -LiteralPath ($Path + '.download') -Algorithm SHA256).Hash -ne $Hash) { throw "SHA256 mismatch: $([IO.Path]::GetFileName($Path))" }
        Move-Item -LiteralPath ($Path + '.download') -Destination $Path -Force
    }
}
function Expand-VerifiedZip([string]$Archive, [string]$Destination) {
    $dest = [IO.Path]::GetFullPath($Destination).TrimEnd('\') + '\'
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($entry in $zip.Entries) {
            $target = [IO.Path]::GetFullPath((Join-Path $Destination $entry.FullName))
            if (!$target.StartsWith($dest, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe archive entry' }
            if (!$entry.Name) { New-Item -ItemType Directory -Force -Path $target | Out-Null; continue }
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    } finally { $zip.Dispose() }
}
$pythonZip = Join-Path $cache 'python-3.12.10-embed-amd64.zip'
Get-Verified $lock.python.url $pythonZip $lock.python.sha256
Expand-VerifiedZip $pythonZip $runtime
$site = Join-Path $runtime 'Lib/site-packages'
New-Item -ItemType Directory -Force -Path $site | Out-Null
foreach ($wheel in $lock.wheels) {
    $metadata = Invoke-RestMethod -Uri "https://pypi.org/pypi/$($wheel.name)/$($wheel.version)/json"
    $asset = @($metadata.urls | Where-Object { $_.filename -eq $wheel.file -and $_.digests.sha256 -eq $wheel.sha256 })
    if ($asset.Count -ne 1 -or !([Uri]$asset[0].url).Host.Equals('files.pythonhosted.org')) { throw "Pinned wheel missing: $($wheel.name)" }
    $wheelPath = Join-Path $cache $wheel.file
    Get-Verified $asset[0].url $wheelPath $wheel.sha256
    Expand-VerifiedZip $wheelPath $site
}
@('python312.zip', '.', 'Lib/site-packages', '..') | Set-Content -LiteralPath (Join-Path $runtime 'python312._pth') -Encoding ascii
$sourceZip = Join-Path $cache ('tg-' + $lock.source.commit + '.zip')
Get-Verified $lock.source.url $sourceZip $lock.source.sha256
$sourceStage = Join-Path $cache ('source-' + $lock.source.commit)
Expand-VerifiedZip $sourceZip $sourceStage
$source = Join-Path $sourceStage ('tg-ws-proxy-' + $lock.source.commit)
$payload = Join-Path $ProjectRoot ('payload/tg/' + $lock.source.version)
New-Item -ItemType Directory -Force -Path $payload | Out-Null
foreach ($folder in @('proxy', 'utils')) { Copy-Item -LiteralPath (Join-Path $source $folder) -Destination $payload -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination $payload -Force
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'runtime/tg-dependencies.lock.json') -Destination (Join-Path $payload 'dependency-lock.json') -Force
@{ Version=$lock.source.version; Commit=$lock.source.commit; SourceSha256=$lock.source.sha256; PythonVersion=$lock.python.version } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payload 'version.json') -Encoding utf8
& (Join-Path $runtime 'python.exe') -I -c 'import cryptography,httpx,h2,certifi,cffi'
if ($LASTEXITCODE -ne 0) { throw 'Bundled Python import check failed' }
Write-Output "TG $($lock.source.version), Python $($lock.python.version), pinned dependencies prepared."
