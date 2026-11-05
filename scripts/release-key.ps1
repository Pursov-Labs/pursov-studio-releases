param([switch]$Initialize)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$keyRoot = Join-Path $env:LOCALAPPDATA 'PursovReleaseKeys'
$keyPath = Join-Path $keyRoot 'pursov-studio.p256.dpapi'
$publicPath = Join-Path $projectRoot 'src/PursovStudio/ReleaseSigningKey.cs'
if (-not (Test-Path -LiteralPath $keyPath)) {
    if (-not $Initialize) { throw 'Signing key is absent. Initialize it once with scripts/release-key.ps1 -Initialize.' }
    if (Test-Path -LiteralPath $publicPath) { throw 'A public release key already exists. Restore the matching private key instead of rotating it.' }
    New-Item -ItemType Directory -Path $keyRoot -Force | Out-Null
    $keyDirectory = [IO.DirectoryInfo]::new($keyRoot)
    $acl = [IO.FileSystemAclExtensions]::GetAccessControl($keyDirectory, [Security.AccessControl.AccessControlSections]::Access)
    $acl.SetAccessRuleProtection($true, $false)
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    foreach ($rule in @($acl.Access)) { if ($null -ne $rule) { $acl.RemoveAccessRuleSpecific($rule) } }
    foreach ($owner in @($sid, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($owner, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    [IO.FileSystemAclExtensions]::SetAccessControl($keyDirectory, $acl)
    $key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
    try {
        $private = $key.ExportPkcs8PrivateKey()
        $protected = [Security.Cryptography.ProtectedData]::Protect($private, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        [IO.File]::WriteAllBytes($keyPath, $protected)
        $public = [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo())
        [IO.File]::WriteAllText($publicPath, "namespace PursovStudio;`n`ninternal static class ReleaseSigningKey`n{`n    internal const string PublicKey = `"$public`";`n}`n", [Text.UTF8Encoding]::new($false))
    }
    finally { if ($private) { [Array]::Clear($private, 0, $private.Length) }; if ($key) { $key.Dispose() } }
}
Write-Output 'The release signing key is protected by Windows DPAPI for this user. Only its public key is included in source.'
