param([Parameter(Mandatory)][string]$Worker)
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFile((Resolve-Path -LiteralPath $Worker).Path)
$type = $assembly.GetType('Program', $true)
$method = $type.GetMethod('AssertAccess', [Reflection.BindingFlags]'Static,NonPublic')
$administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$users = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
function Descriptor([Security.AccessControl.FileSystemRights]$UserRights) {
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    foreach ($sid in @($administrators,$system)) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','Allow'))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($users,$UserRights,'Allow'))
    return $acl
}
$null = $method.Invoke($null, @((Descriptor 'ReadAndExecute'),$administrators,$system))
$passed = 1
foreach ($rights in @('Write','Delete','ChangePermissions','TakeOwnership','DeleteSubdirectoriesAndFiles')) {
    $rejected = $false
    try { $null = $method.Invoke($null, @((Descriptor $rights),$administrators,$system)) }
    catch {
        $cause = $_.Exception
        while ($cause.InnerException) { $cause = $cause.InnerException }
        if ($cause -is [UnauthorizedAccessException]) { $rejected = $true } else { throw }
    }
    if (-not $rejected) { throw ('Installer accepted ordinary-user rights: ' + $rights) }
    $passed++
}
Write-Output ('PASS ' + $passed + ' update worker ACL boundary checks')
