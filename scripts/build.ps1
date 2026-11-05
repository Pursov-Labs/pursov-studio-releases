param([switch]$SkipRuntime, [switch]$FrameworkDependent, [string]$Version)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Version) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src/PursovStudio/PursovStudio.csproj') -Raw
    $Version = $project.Project.PropertyGroup.Version
}
if ($Version -notmatch '^(0|[1-9][0-9]{0,2})\.(0|[1-9][0-9]{0,2})\.(0|[1-9][0-9]{0,4})$') { throw 'Use a three-part MSI-compatible version' }
$parsedVersion = [Version]::Parse($Version)
if ($parsedVersion.Major -gt 255 -or $parsedVersion.Minor -gt 255 -or $parsedVersion.Build -gt 65535) { throw 'Version exceeds MSI limits' }
$sdkRoot = Join-Path $projectRoot '.tools/dotnet'
$dotnetExe = Join-Path $sdkRoot 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetExe)) {
    New-Item -ItemType Directory -Path $sdkRoot -Force | Out-Null
    $sdkArchive = Join-Path $projectRoot '.tools/dotnet-sdk-10.0.403-win-x64.zip'
    Invoke-WebRequest -Uri 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-win-x64.zip' -OutFile $sdkArchive
    if ((Get-FileHash -LiteralPath $sdkArchive -Algorithm SHA512).Hash -ne '7A114C318FA7F1D9822991B534B2814D0E78386CEDA816B371F5141ACEFE741D87CCB606EDE06CADAC8F9AC5070E32B708A67BBD1619FF5E5A7858A27874EF48') { throw 'Official .NET SDK SHA512 mismatch' }
    Expand-Archive -LiteralPath $sdkArchive -DestinationPath $sdkRoot -Force
}
if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'src/PursovStudio/ReleaseSigningKey.cs'))) { throw 'Run scripts/release-key.ps1 -Initialize once before the first release build.' }
if (-not $SkipRuntime) { & (Join-Path $PSScriptRoot 'prepare-tg.ps1') }
if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'runtime/python/python.exe'))) { throw 'TG runtime is not prepared' }
$zapret = & (Join-Path $PSScriptRoot 'prepare-zapret.ps1')
& (Join-Path $PSScriptRoot 'prepare-sources.ps1')
$destination = Join-Path $projectRoot ('output/builds/' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$contained = if ($FrameworkDependent) { 'false' } else { 'true' }
& $dotnetExe publish (Join-Path $projectRoot 'src/PursovStudio/PursovStudio.csproj') -c Release -r win-x64 --self-contained $contained -o $destination ('-p:Version=' + $Version) ('-p:AssemblyVersion=' + $Version + '.0') ('-p:FileVersion=' + $Version + '.0') -p:DebugType=None --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
function Copy-PayloadTree([string]$Source, [string]$Target) {
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($Source, $file.FullName)
        if ($relative -match '(^|[\\/])(__pycache__|\.git)([\\/]|$)|\.(pyc|log|pdb)$') { continue }
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw ('Linked payload file: ' + $relative) }
        $targetPath = Join-Path $Target $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $targetPath) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $targetPath
    }
}
Copy-PayloadTree (Join-Path $projectRoot 'runtime') (Join-Path $destination 'runtime')
Copy-PayloadTree $zapret.Directory (Join-Path $destination ('payload/zapret/' + $zapret.Version))
# Upstream service.bat creates these empty per-user overrides before its first launch.
foreach ($name in @('list-general-user.txt','list-exclude-user.txt','ipset-exclude-user.txt')) {
    [IO.File]::WriteAllText((Join-Path $destination ('payload/zapret/' + $zapret.Version + '/lists/' + $name)), '')
}
$tgLock = Get-Content -LiteralPath (Join-Path $projectRoot 'runtime/tg-dependencies.lock.json') -Raw | ConvertFrom-Json
Copy-PayloadTree (Join-Path $projectRoot ('payload/tg/' + $tgLock.source.version)) (Join-Path $destination ('payload/tg/' + $tgLock.source.version))
Copy-PayloadTree (Join-Path $projectRoot 'payload/licenses') (Join-Path $destination 'payload/licenses')
foreach ($document in @('README_RU.md','THIRD_PARTY_LICENSES.md','LICENSE')) {
    $source = Join-Path $projectRoot $document
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $destination }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'payload/zapret.lock.json') -Destination (Join-Path $destination 'payload/zapret.lock.json')
Copy-Item -LiteralPath (Join-Path $projectRoot 'payload/sources.lock.json') -Destination (Join-Path $destination 'payload/sources.lock.json')
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework C# compiler is required for the small update helper.' }
$workerVersionSource = Join-Path (Split-Path -Parent $destination) ('worker-version-' + [Guid]::NewGuid().ToString('N') + '.cs')
[IO.File]::WriteAllText($workerVersionSource, ('[assembly: System.Reflection.AssemblyVersion("' + $Version + '.0")][assembly: System.Reflection.AssemblyFileVersion("' + $Version + '.0")]'))
& $compiler '/nologo' '/target:winexe' '/platform:x64' '/optimize+' '/reference:System.Runtime.Serialization.dll' '/reference:System.Windows.Forms.dll' ('/win32icon:' + (Join-Path $projectRoot 'src/PursovStudio/Assets/App.ico')) ('/out:' + (Join-Path $destination 'UpdateWorker.exe')) (Join-Path $projectRoot 'src/UpdateWorker/Program.cs') $workerVersionSource
if ($LASTEXITCODE -ne 0) { throw 'Update worker compilation failed' }
$report = Join-Path $projectRoot ('output/builds/self-test-' + $Version + '-' + [Guid]::NewGuid().ToString('N') + '.json')
$process = Start-Process -FilePath (Join-Path $destination 'PursovStudio.exe') -ArgumentList @('--self-test','--report',('"' + $report + '"')) -PassThru -Wait -WindowStyle Hidden
if ($process.ExitCode -ne 0) { throw ('Self-test failed: ' + $report) }
$manifest = Get-ChildItem -LiteralPath $destination -Recurse -File | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($destination, $_.FullName); Length = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $destination 'payload-manifest.json') -Encoding utf8NoBOM
[pscustomobject]@{ Version=$Version; Directory=$destination; SelfContained=(-not $FrameworkDependent); SelfTest=$report } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $projectRoot 'output/last-build.json') -Encoding utf8NoBOM
Write-Output ('Release build ready: ' + $destination)
