param([Parameter(Mandatory)][string]$Package, [Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$keyPath = Join-Path $env:LOCALAPPDATA 'PursovReleaseKeys/pursov-studio.p256.dpapi'
if (-not (Test-Path -LiteralPath $keyPath)) { throw 'Restore the release signing key for this Windows user before publishing.' }
if ([IO.Path]::GetFileName($Package) -cne ('PURSOV_STUDIO_' + $Version + '_x64.msi')) { throw 'Package name and version disagree' }
$packageInfo = Get-Item -LiteralPath $Package
if ($packageInfo.Length -le 0 -or $packageInfo.Length -gt 512MB) { throw 'Package size is outside supported limits' }
$manifest = [ordered]@{ Schema=1; Product='PURSOV_STUDIO'; Channel='stable'; Architecture='win-x64'; Version=$Version; AssetName=$packageInfo.Name; Sha256=(Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash.ToLowerInvariant(); Size=$packageInfo.Length }
$json = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Compress))
$key = [Security.Cryptography.ECDsa]::Create()
try {
    $private = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($keyPath), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $read = 0
    $key.ImportPkcs8PrivateKey($private, [ref]$read)
    if ($read -ne $private.Length) { throw 'Invalid private key encoding' }
    $public = [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo())
    $source = Get-Content -LiteralPath (Join-Path $projectRoot 'src/PursovStudio/ReleaseSigningKey.cs') -Raw
    if (-not $source.Contains('"' + $public + '"')) { throw 'Private key does not match the application trust anchor' }
    $signature = $key.SignData($json, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $Destination 'release.json'), $json)
    [IO.File]::WriteAllBytes((Join-Path $Destination 'release.sig'), $signature)
}
finally { if ($private) { [Array]::Clear($private, 0, $private.Length) }; $key.Dispose() }
Write-Output ('Signed release metadata for ' + $Version)
