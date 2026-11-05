param([switch]$SkipBuild, [switch]$SkipRuntime, [switch]$NoReleaseSignature, [string]$Version)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -SkipRuntime:$SkipRuntime -Version $Version }
$build = Get-Content -LiteralPath (Join-Path $projectRoot 'output/last-build.json') -Raw | ConvertFrom-Json
if ($Version -and $Version -ne $build.Version) { throw 'Last build version does not match requested package' }
if (-not $build.SelfContained) { throw 'The offline installer requires a self-contained build' }
$Version = $build.Version
if ($Version -notmatch '^(0|[1-9][0-9]{0,2})\.(0|[1-9][0-9]{0,2})\.(0|[1-9][0-9]{0,4})$') { throw 'Invalid build version' }
$stage = [IO.Path]::GetFullPath($build.Directory)
$stagePrefix = [IO.Path]::GetFullPath((Join-Path $projectRoot 'output/builds')).TrimEnd('\') + '\'
if (-not $stage.StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Build staging must be inside output/builds' }
if ((Get-Item -LiteralPath $stage).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked build staging is forbidden' }
$files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $_.Name -ne 'Payload.wxs' } | Sort-Object FullName)
$namespace = 'http://wixtoolset.org/schemas/v4/wxs'
$xml = [Xml.XmlDocument]::new()
$wix = $xml.CreateElement('Wix', $namespace); $null = $xml.AppendChild($wix)
$fragment = $xml.CreateElement('Fragment', $namespace); $null = $wix.AppendChild($fragment)
$directoryRef = $xml.CreateElement('DirectoryRef', $namespace); $directoryRef.SetAttribute('Id','INSTALLFOLDER'); $null = $fragment.AppendChild($directoryRef)
$directories = @{ '' = $directoryRef }
$group = $xml.CreateElement('ComponentGroup', $namespace); $group.SetAttribute('Id','Payload'); $null = $fragment.AppendChild($group)
function Stable-Id([string]$Prefix, [string]$Value) {
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant()))
    return $Prefix + [Convert]::ToHexString($hash).Substring(0,32)
}
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($stage, $file.FullName)
    if ($relative -eq 'PursovStudio.exe') { continue }
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked installer payload file' }
    $relativeDirectory = [IO.Path]::GetDirectoryName($relative)
    $current = ''; $parent = $directoryRef
    foreach ($part in $relativeDirectory.Split([IO.Path]::DirectorySeparatorChar, [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = if ($current) { Join-Path $current $part } else { $part }
        if (-not $directories.ContainsKey($current)) {
            $directory = $xml.CreateElement('Directory', $namespace)
            $directory.SetAttribute('Id',(Stable-Id 'D' $current)); $directory.SetAttribute('Name',$part)
            $null = $parent.AppendChild($directory); $directories[$current] = $directory
        }
        $parent = $directories[$current]
    }
    $component = $xml.CreateElement('Component', $namespace)
    $component.SetAttribute('Id',(Stable-Id 'C' $relative)); $component.SetAttribute('Guid','*'); $component.SetAttribute('Bitness','always64')
    $component.SetAttribute('Directory', $(if ($relativeDirectory) { Stable-Id 'D' $relativeDirectory } else { 'INSTALLFOLDER' }))
    $element = $xml.CreateElement('File', $namespace)
    $element.SetAttribute('Id',(Stable-Id 'F' $relative)); $element.SetAttribute('Source',$file.FullName); $element.SetAttribute('KeyPath','yes')
    $null = $component.AppendChild($element); $null = $group.AppendChild($component)
}
$xml.Save((Join-Path $stage 'Payload.wxs'))
$release = Join-Path $projectRoot ('output/releases/' + $Version)
New-Item -ItemType Directory -Path $release -Force | Out-Null
$dotnet = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
& $dotnet build (Join-Path $projectRoot 'installer/PursovStudio.Setup.wixproj') -c Release ('-p:StageRoot=' + $stage) ('-p:ProductVersion=' + $Version) ('-p:OutputPath=' + $release + '\') --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'MSI compilation failed' }
$msi = Join-Path $release ('PURSOV_STUDIO_' + $Version + '_x64.msi')
if (-not (Test-Path -LiteralPath $msi)) { throw 'MSI output is missing' }
if ($NoReleaseSignature) {
    foreach ($name in @('release.json','release.sig')) {
        $metadata = Join-Path $release $name
        if (Test-Path -LiteralPath $metadata) { Remove-Item -LiteralPath $metadata }
    }
    Write-Output 'Local MSI built without release-channel metadata.'
}
else { & (Join-Path $PSScriptRoot 'sign-release.ps1') -Package $msi -Version $Version -Destination $release }
$sourceRoot = Join-Path $projectRoot ('output/source-staging/' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sourceRoot -Force | Out-Null
foreach ($folder in @('src','scripts','tests','installer','payload/licenses')) {
    $inputRoot = Join-Path $projectRoot $folder
    foreach ($file in Get-ChildItem -LiteralPath $inputRoot -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($projectRoot, $file.FullName)
        if ($relative -match '(^|[\\/])(bin|obj|__pycache__)([\\/]|$)' -or $relative -match '\.(user|log|pyc)$' -or $relative -in @('tests\zapret-self-test.json','installer\GeneratedPayload.wxs')) { continue }
        $target = Join-Path $sourceRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
foreach ($file in @('README_RU.md','THIRD_PARTY_LICENSES.md','LICENSE','.gitignore','PRODUCT.md','DESIGN.md','.impeccable/design.json','runtime/tg_runner.py','runtime/tg_probe.py','runtime/tg-dependencies.lock.json','payload/zapret.lock.json','payload/sources.lock.json')) {
    $source = Join-Path $projectRoot $file
    if (-not (Test-Path -LiteralPath $source)) { continue }
    $target = Join-Path $sourceRoot $file
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}
$sourceZip = Join-Path $release ('PURSOV_STUDIO_' + $Version + '_sources.zip')
Compress-Archive -Path (Join-Path $sourceRoot '*') -DestinationPath $sourceZip -Force
& (Join-Path $PSScriptRoot 'prepare-sources.ps1')
$sourceLock = Get-Content -LiteralPath (Join-Path $projectRoot 'payload/sources.lock.json') -Raw | ConvertFrom-Json
$sourceArchives = @($sourceLock.sources | ForEach-Object { Join-Path $projectRoot ('.tools/third-party-sources/' + $_.file) })
$thirdPartyZip = Join-Path $release ('PURSOV_STUDIO_' + $Version + '_third_party_sources.zip')
Compress-Archive -LiteralPath ($sourceArchives + (Join-Path $projectRoot 'payload/sources.lock.json')) -DestinationPath $thirdPartyZip -Force
Get-ChildItem -LiteralPath $release -File | Where-Object { $_.Extension -in '.msi','.zip','.json','.sig' -and $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name
} | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
Write-Output ('Release package ready: ' + $release)
