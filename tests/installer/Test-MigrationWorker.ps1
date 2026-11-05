param([Parameter(Mandatory)][string]$Worker)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this isolated file-lock fixture from elevated Windows PowerShell 5.1.'
}
$assembly = [Reflection.Assembly]::LoadFile((Resolve-Path -LiteralPath $Worker).Path)
$type = $assembly.GetType('Program', $true)
$flags = [Reflection.BindingFlags]'Static,NonPublic'
function Invoke-Helper([string]$Name, [object[]]$Arguments) {
    try {
        $method = $type.GetMethod($Name, $flags)
        $parameters = $method.GetParameters()
        $values = [object[]]::new($Arguments.Count)
        for ($index = 0; $index -lt $Arguments.Count; $index++) {
            $values[$index] = [Management.Automation.LanguagePrimitives]::ConvertTo($Arguments[$index].PSObject.BaseObject, $parameters[$index].ParameterType)
        }
        $method.Invoke($null, $values)
    }
    catch {
        $cause = $_.Exception
        while ($cause.InnerException) { $cause = $cause.InnerException }
        throw $cause
    }
}
$script:passed = 0
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:passed++
    Write-Output ('PASS ' + $Message)
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Protect-FixtureFile([string]$Path) {
    $acl = Get-Acl -LiteralPath $Path
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    Set-Acl -LiteralPath $Path -AclObject $acl
}
$fixtureName = 'PursovStudio-MigrationTest-' + [Guid]::NewGuid().ToString('N')
$fixture = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) $fixtureName
$lock = $null
try {
    Invoke-Helper 'SecureDirectory' @($fixture)
    $snapshots = Join-Path $fixture 'snapshots'
    Invoke-Helper 'SecureDirectory' @($snapshots)
    $product = [Guid]::NewGuid().ToString('N')
    $retired = Join-Path $snapshots ($product + '-' + [Guid]::NewGuid().ToString('N'))
    Invoke-Helper 'SecureDirectory' @($retired)
    $lockedFile = Join-Path $retired 'loaded-driver-fixture.bin'
    [IO.File]::WriteAllText($lockedFile, 'test-only loaded file')
    Protect-FixtureFile $lockedFile
    $originalHash = Hash $lockedFile
    $lock = [IO.File]::Open($lockedFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $removed = Invoke-Helper 'DeleteCompletedSnapshot' @($snapshots, $retired)
    Check (-not $removed -and (Test-Path -LiteralPath $lockedFile) -and (Hash $lockedFile) -eq $originalHash) 'Locked completed snapshot is retained without failing restore'
    Check ($null -eq (Invoke-Helper 'FindPendingSnapshot' @($snapshots, $product))) 'Retired locked snapshot is not an active transaction'
    $next = Join-Path $snapshots ($product + '-' + [Guid]::NewGuid().ToString('N'))
    Invoke-Helper 'SecureDirectory' @($next)
    [IO.File]::WriteAllText((Join-Path $next 'snapshot.json'), '{}')
    Protect-FixtureFile (Join-Path $next 'snapshot.json')
    Check ((Invoke-Helper 'FindPendingSnapshot' @($snapshots, $product)) -eq $next) 'Next attempt selects a fresh snapshot while old file remains locked'
    $lock.Dispose(); $lock = $null
    Check ((Invoke-Helper 'DeleteCompletedSnapshot' @($snapshots, $retired)) -and -not (Test-Path -LiteralPath $retired)) 'Retired files can be cleaned after the lock is released'

    $untrusted = Join-Path $snapshots 'untrusted'
    Invoke-Helper 'SecureDirectory' @($untrusted)
    $acl = Get-Acl -LiteralPath $untrusted
    $originalAcl = $acl.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::All)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'), 'Write', 'Allow'))
    Set-Acl -LiteralPath $untrusted -AclObject $acl
    $rejected = $false
    try { $null = Invoke-Helper 'DeleteCompletedSnapshot' @($snapshots, $untrusted) }
    catch [UnauthorizedAccessException] { $rejected = $true }
    Check ($rejected -and (Test-Path -LiteralPath $untrusted)) 'Cleanup does not suppress unsafe ACL validation'
    $acl.SetSecurityDescriptorSddlForm($originalAcl)
    Set-Acl -LiteralPath $untrusted -AclObject $acl

    $source = Join-Path $fixture 'installed'
    $backups = Join-Path $fixture 'backups'
    Invoke-Helper 'SecureDirectory' @($source)
    Invoke-Helper 'SecureDirectory' @($backups)
    $legacyExe = Join-Path $source 'PursovStudio.exe'
    $fixtureSource = 'using System.Reflection; [assembly: AssemblyFileVersion("1.0.1.0")] public static class LegacyFixture { public static void Main() {} }'
    Add-Type -TypeDefinition $fixtureSource -OutputAssembly $legacyExe -OutputType ConsoleApplication
    Protect-FixtureFile $legacyExe
    $legacyOnly = Join-Path $source 'legacy-only.pdb'
    [IO.File]::WriteAllText($legacyOnly, 'legacy-only fixture data')
    Protect-FixtureFile $legacyOnly
    $exeHash = Hash $legacyExe
    $legacyHash = Hash $legacyOnly
    $backup = Join-Path $backups ([Guid]::NewGuid().ToString('N'))
    Invoke-Helper 'QuarantineLegacyTree' @($source, $backup)
    Check ((Test-Path -LiteralPath $source) -and @(Get-ChildItem -LiteralPath $source -Force).Count -eq 0) 'Legacy move leaves an empty protected MSI install directory'
    Invoke-Helper 'AssertProtectedTree' @($source)
    Check ((Hash (Join-Path $backup 'PursovStudio.exe')) -eq $exeHash -and (Hash (Join-Path $backup 'legacy-only.pdb')) -eq $legacyHash) 'Durable quarantine preserves every legacy fixture byte'
    Invoke-Helper 'RestoreLegacyTree' @($source, $backup)
    Check ((Hash $legacyExe) -eq $exeHash -and (Hash $legacyOnly) -eq $legacyHash -and (Test-Path -LiteralPath $backup)) 'Rollback restores legacy-only files and retains the durable backup'

    $rejected = $false
    try { Invoke-Helper 'QuarantineLegacyTree' @($source, $backup) }
    catch [IO.IOException] { $rejected = $true }
    Check ($rejected -and (Hash $legacyExe) -eq $exeHash -and (Hash (Join-Path $backup 'PursovStudio.exe')) -eq $exeHash) 'Failed atomic move leaves original and existing backup intact'
    Invoke-Helper 'RestoreLegacyTree' @($source, (Join-Path $backups 'move-never-happened'))
    Check ((Hash $legacyExe) -eq $exeHash -and (Hash $legacyOnly) -eq $legacyHash) 'Rollback tolerates a pending marker when move never happened'
}
finally {
    if ($null -ne $lock) { $lock.Dispose() }
    $resolved = [IO.Path]::GetFullPath($fixture)
    $expected = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) $fixtureName))
    if ($resolved -ne $expected -or [IO.Path]::GetFileName($resolved) -notmatch '^PursovStudio-MigrationTest-[a-f0-9]{32}$') { throw 'Invalid fixture cleanup path' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
Write-Output ('PASS ' + $passed + ' migration worker checks; temporary fixture removed')
