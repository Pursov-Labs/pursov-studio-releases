param([Parameter(Mandatory)][string]$Package)
$ErrorActionPreference = 'Stop'
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase((Resolve-Path -LiteralPath $Package).Path, 0)
function Read-Table([string]$Table, [string[]]$Columns) {
    $quoted = $Columns | ForEach-Object { '`' + $_ + '`' }
    $view = $database.OpenView(('SELECT ' + ($quoted -join ', ') + ' FROM `' + $Table + '`'))
    $null = $view.Execute()
    try {
        while ($record = $view.Fetch()) {
            $row = [ordered]@{}
            for ($index = 0; $index -lt $Columns.Count; $index++) {
                $row[$Columns[$index]] = $record.GetType().InvokeMember('StringData', [Reflection.BindingFlags]::GetProperty, $null, $record, @($index + 1))
            }
            [pscustomobject]$row
        }
    }
    finally { $null = $view.Close() }
}
$script:passed = 0
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:passed++
}
$properties = @{}
Read-Table 'Property' @('Property','Value') | ForEach-Object { $properties[$_.Property] = $_.Value }
Check ($properties.ALLUSERS -eq '1') 'MSI must install per machine'
Check ($properties.ProductName -eq 'PURSOV STUDIO') 'Product name mismatch'
$services = @(Read-Table 'ServiceInstall' @('Name','ServiceType','StartType','StartName','Arguments'))
Check ($services.Count -eq 1 -and $services[0].Name -eq 'PursovStudioBroker') 'Expected one broker service'
Check ($services[0].ServiceType -eq '16' -and $services[0].StartType -eq '3' -and $services[0].StartName -eq 'LocalSystem' -and $services[0].Arguments -eq '--service') 'Service must be demand-start LocalSystem own-process'
$controls = @(Read-Table 'ServiceControl' @('Name','Event','Wait'))
Check ($controls.Count -eq 1 -and ([int]$controls[0].Event -band 0x22) -eq 0x22 -and ([int]$controls[0].Event -band 0x80) -ne 0 -and $controls[0].Wait -eq '1') 'Stop broker before install/uninstall and remove on uninstall'
$shortcuts = @(Read-Table 'Shortcut' @('Directory_','Arguments','Icon_'))
Check (@($shortcuts | Where-Object { $_.Directory_ -eq 'StartupFolder' -and $_.Arguments -eq '--signin' }).Count -eq 1) 'Common startup shortcut is missing'
Check (@($shortcuts | Where-Object { $_.Directory_ -eq 'ProgramMenuFolder' }).Count -eq 1) 'Start shortcut is missing'
Check (@($shortcuts | Where-Object { $_.Icon_ -ne 'PursovStudio.exe' }).Count -eq 0) 'Shortcuts must use the branded icon'
$actions = @(Read-Table 'CustomAction' @('Action','Type','Target'))
foreach ($name in @('BackupLegacy','RollbackLegacy','InitializeInstall','CommitLegacy')) {
    $action = @($actions | Where-Object Action -eq $name)
    Check ($action.Count -eq 1 -and ([int]$action[0].Type -band 2048) -ne 0) ('Missing system-context action: ' + $name)
}
$sequence = @{}
Read-Table 'InstallExecuteSequence' @('Action','Sequence') | ForEach-Object { $sequence[$_.Action] = [int]$_.Sequence }
Check ($sequence.BackupLegacy -lt $sequence.CreateFolders) 'Root security must be established before folder creation'
Check ($sequence.InitializeInstall -gt $sequence.InstallServices) 'Initialize ACL and service security after service installation'
Check ($sequence.RollbackLegacy -lt $sequence.BackupLegacy) 'Rollback must be queued before snapshot changes'
Check ($sequence.RemoveExistingProducts -gt $sequence.InstallInitialize -and $sequence.RemoveExistingProducts -lt $sequence.InstallFinalize) 'Major upgrade must stay within MSI rollback transaction'
$files = @(Read-Table 'File' @('FileName'))
Check (@($files | Where-Object { $_.FileName -match '(^|\|)python.exe$' }).Count -eq 1) 'Python is not bundled'
Check (@($files | Where-Object { $_.FileName -match '(^|\|)coreclr.dll$' }).Count -eq 1) '.NET runtime is not bundled'
Check (@($files | Where-Object { $_.FileName -match '(^|\|)UpdateWorker.exe$' }).Count -eq 1) 'Update helper is missing'
Check (@($files | Where-Object { $_.FileName -match '(^|\|)(settings.json|tg-config.json|application.log)$|\.dpapi$' }).Count -eq 0) 'User settings or signing secret found in installer'
Write-Output ('PASS ' + $passed + ' MSI structure checks; ' + $files.Count + ' files; product ' + $properties.ProductVersion)
