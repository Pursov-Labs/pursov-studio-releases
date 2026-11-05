# Requires PowerShell 7. Full execution is restricted to a disposable GitHub-hosted runner.
# -PrepareOnly verifies packages and creates a local test MSI without installing anything.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PreviousDirectory,
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$IsolatedMachine,
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7 -or -not $IsWindows) { throw 'Windows PowerShell 7 is required.' }
if (-not $PrepareOnly) {
    if (-not $IsolatedMachine -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
        throw 'Actual installation is restricted to a disposable GitHub-hosted runner. Use -PrepareOnly for local validation.'
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'The isolated runner must have an elevated administrator token.'
    }
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) {
    if (@(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count) { throw 'Use a new, empty evidence directory.' }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$script:checks = [Collections.Generic.List[object]]::new()
$script:transactions = [Collections.Generic.List[object]]::new()
$script:msiTimedOut = $false
$report = [ordered]@{
    startedUtc = [DateTime]::UtcNow.ToString('O')
    mode = $(if ($PrepareOnly) { 'prepare-only' } else { 'isolated-lifecycle' })
    os = (Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber)
    limits = @('Windows Server runner; Windows 10/11 remain untested.', 'No actual reboot, alternate-admin UAC, ordinary-user launch, or sign-in is tested.', 'No live Telegram, Discord, YouTube, network-engine connection, UI accessibility, or idle performance test.', 'MSI transaction exercised directly; broker-driven update and user restart are separate tests.')
    checks = $script:checks
    transactions = $script:transactions
    passed = $false
}
$installRoot = Join-Path $env:ProgramFiles 'PURSOV STUDIO'
$sharedRoot = Join-Path $env:ProgramData 'PURSOV STUDIO'
$userRoot = Join-Path $env:LOCALAPPDATA 'PURSOV STUDIO'
$serviceName = 'PursovStudioBroker'
$installer = New-Object -ComObject WindowsInstaller.Installer
$previous = $null
$candidate = $null
$installationAttempted = $false
$failure = $null

function Check([bool]$Condition, [string]$Name) {
    $script:checks.Add([ordered]@{ name = $Name; passed = $Condition })
    if (-not $Condition) { throw $Name }
    Write-Output ('PASS ' + $Name)
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Read-Rows($Database, [string]$Sql) {
    $view = $Database.OpenView($Sql)
    $null = $view.Execute()
    try {
        while ($record = $view.Fetch()) {
            $row = @()
            $fieldCount = $record.GetType().InvokeMember('FieldCount', [Reflection.BindingFlags]::GetProperty, $null, $record, $null)
            for ($index = 1; $index -le $fieldCount; $index++) {
                $row += $record.GetType().InvokeMember('StringData', [Reflection.BindingFlags]::GetProperty, $null, $record, @($index))
            }
            $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)
            ,$row
        }
    }
    finally { $null = $view.Close(); $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
}
function Execute-Sql($Database, [string]$Sql) {
    $view = $Database.OpenView($Sql)
    try { $null = $view.Execute() } finally { $null = $view.Close(); $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
}
function Verify-Release([string]$Directory) {
    $Directory = (Resolve-Path -LiteralPath $Directory).Path
    $jsonPath = Join-Path $Directory 'release.json'
    $signaturePath = Join-Path $Directory 'release.sig'
    if ((Get-Item -LiteralPath $jsonPath).Length -gt 16384 -or (Get-Item -LiteralPath $signaturePath).Length -ne 64) { throw 'Invalid manifest size.' }
    $json = [IO.File]::ReadAllBytes($jsonPath)
    $signature = [IO.File]::ReadAllBytes($signaturePath)
    $keySource = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../src/PursovStudio/ReleaseSigningKey.cs') -Raw
    $keyMatch = [regex]::Match($keySource, 'PublicKey\s*=\s*"([A-Za-z0-9+/=]+)"')
    if (-not $keyMatch.Success) { throw 'Embedded public release key is missing.' }
    $key = [Security.Cryptography.ECDsa]::Create()
    try {
        $read = 0
        $keyBytes = [Convert]::FromBase64String($keyMatch.Groups[1].Value)
        $key.ImportSubjectPublicKeyInfo($keyBytes, [ref]$read)
        $valid = $read -eq $keyBytes.Length -and $key.VerifyData($json, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
        if (-not $valid) { throw 'Production manifest signature is invalid.' }
    }
    finally { $key.Dispose() }
    $manifest = [Text.Encoding]::UTF8.GetString($json) | ConvertFrom-Json
    if ($manifest.Schema -ne 1 -or $manifest.Product -ne 'PURSOV_STUDIO' -or $manifest.Channel -ne 'stable' -or $manifest.Architecture -ne 'win-x64' -or $manifest.Version -notmatch '^\d{1,3}\.\d{1,3}\.\d{1,5}$' -or $manifest.AssetName -cne ('PURSOV_STUDIO_' + $manifest.Version + '_x64.msi')) { throw 'Unexpected release metadata.' }
    $package = Join-Path $Directory $manifest.AssetName
    if ($manifest.Size -le 0 -or $manifest.Size -gt 536870912 -or (Get-Item -LiteralPath $package).Length -ne $manifest.Size -or (Hash $package) -cne $manifest.Sha256) { throw 'Package length or SHA-256 mismatch.' }
    $database = $installer.OpenDatabase($package, 0)
    try {
        $properties = @{}
        Read-Rows $database 'SELECT `Property`, `Value` FROM `Property`' | ForEach-Object { $properties[$_[0]] = $_[1] }
        if ($properties.ProductVersion -ne $manifest.Version -or $properties.UpgradeCode -ne '{35A9E7FC-3892-44D7-9872-C879794C01AF}' -or $properties.ProductCode -notmatch '^\{[0-9A-Fa-f-]{36}\}$') { throw 'MSI product identity differs from verified manifest.' }
        [pscustomobject]@{ Package = $package; Version = $manifest.Version; ProductCode = $properties.ProductCode; Sha256 = $manifest.Sha256 }
    }
    finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
}
function Make-FailingCopy([string]$Package) {
    # This intentionally invalidates the test COPY's release hash. The authentic package is left untouched.
    $testPackage = Join-Path $OutputDirectory 'rollback-test-only.msi'
    Copy-Item -LiteralPath $Package -Destination $testPackage
    $database = $installer.OpenDatabase($testPackage, 1)
    try {
        $sequence = @{}
        Read-Rows $database 'SELECT `Action`, `Sequence` FROM `InstallExecuteSequence`' | ForEach-Object { $sequence[$_[0]] = [int]$_[1] }
        $failureSequence = $sequence.InitializeInstall + 1
        if ($sequence.CommitLegacy -ne $failureSequence -or $sequence.InstallFinalize -le ($failureSequence + 1)) { throw 'Unexpected installer sequence; review the failure injection before use.' }
        Execute-Sql $database ('UPDATE `InstallExecuteSequence` SET `Sequence` = ' + ($failureSequence + 1) + " WHERE `Action` = 'CommitLegacy'")
        # EXE + directory source (34), deferred (1024), no impersonation (2048).
        Execute-Sql $database 'INSERT INTO `CustomAction` (`Action`, `Type`, `Source`, `Target`) VALUES (''TestForcedFailure'', 3106, ''System64Folder'', ''"[System64Folder]cmd.exe" /d /c exit 42'')'
        Execute-Sql $database ('INSERT INTO `InstallExecuteSequence` (`Action`, `Condition`, `Sequence`) VALUES (''TestForcedFailure'', ''NOT REMOVE~="ALL"'', ' + $failureSequence + ')')
        $summary = $database.SummaryInformation(1)
        try {
            $null = $summary.GetType().InvokeMember('Property', [Reflection.BindingFlags]::SetProperty, $null, $summary, @(9, [Guid]::NewGuid().ToString('B').ToUpperInvariant()))
            $null = $summary.Persist()
        }
        finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) }
        $null = $database.Commit()
    }
    finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
    return $testPackage
}
function Invoke-Msi([string]$Step, [string[]]$MsiArguments, [int]$ExpectedCode = 0) {
    $log = Join-Path $OutputDirectory ($Step + '.log')
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:SystemRoot 'System32/msiexec.exe'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in ($MsiArguments + @('/qn','/norestart','/L*v',$log))) { $start.ArgumentList.Add($argument) }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(180000)) {
            $script:msiTimedOut = $true
            $script:transactions.Add([ordered]@{ step = $Step; timeout = $true; processId = $process.Id })
            throw 'MSI exceeded three minutes. Transaction was not killed; runner disposal will contain it.'
        }
        $script:transactions.Add([ordered]@{ step = $Step; exitCode = $process.ExitCode; elapsedSeconds = [Math]::Round($clock.Elapsed.TotalSeconds,2); log = [IO.Path]::GetFileName($log) })
        Check ($process.ExitCode -eq $ExpectedCode) ($Step + ' exits ' + $ExpectedCode + ' (actual ' + $process.ExitCode + ')')
    }
    finally { $process.Dispose() }
}
function Assert-Installation([string]$Version) {
    $exe = Join-Path $installRoot 'PursovStudio.exe'
    Check (Test-Path -LiteralPath $exe) ('Installed executable exists for ' + $Version)
    Check ([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion -eq ($Version + '.0')) ('Installed file version is ' + $Version)
    $service = Get-CimInstance Win32_Service -Filter "Name='PursovStudioBroker'"
    Check ($null -ne $service -and $service.StartMode -eq 'Manual' -and $service.StartName -eq 'LocalSystem' -and $service.PathName -match '--service') 'Broker is demand-start LocalSystem'
    $startup = Join-Path ([Environment]::GetFolderPath('CommonStartup')) 'PURSOV STUDIO.lnk'
    Check (Test-Path -LiteralPath $startup) 'Common Startup shortcut exists'
    $shell = New-Object -ComObject WScript.Shell
    try { Check ($shell.CreateShortcut($startup).Arguments -eq '--signin') 'Common Startup shortcut uses per-user sign-in mode' }
    finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    $registration = Join-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' $(if ($Version -eq $previous.Version) { $previous.ProductCode } else { $candidate.ProductCode })
    Check ((Get-ItemProperty -LiteralPath $registration).DisplayVersion -eq $Version) 'Apps and Features has the installed version'
}
function Tree-Digest([string]$Directory) {
    $rows = @(Get-ChildItem -LiteralPath $Directory -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{ file = [IO.Path]::GetRelativePath($Directory, $_.FullName); sha256 = Hash $_.FullName }
    })
    $rows | ConvertTo-Json -Compress -Depth 3
}

try {
    $previous = Verify-Release $PreviousDirectory
    $candidate = Verify-Release $CandidateDirectory
    Check ([version]$candidate.Version -gt [version]$previous.Version) 'Authentic candidate is newer than authentic baseline'
    $report.packages = @($previous, $candidate)
    $testPackage = Make-FailingCopy $candidate.Package
    Check ((Hash $candidate.Package) -eq $candidate.Sha256 -and (Hash $testPackage) -ne $candidate.Sha256) 'Failure injection changes only the isolated test copy'
    $testDatabase = $installer.OpenDatabase($testPackage, 0)
    try {
        $action = @(Read-Rows $testDatabase 'SELECT `Type`, `Target` FROM `CustomAction` WHERE `Action` = ''TestForcedFailure''')
        Check ($action.Count -eq 1 -and $action[0][0] -eq '3106' -and $action[0][1] -eq '"[System64Folder]cmd.exe" /d /c exit 42') 'Failure action persists as deferred SYSTEM exit 42'
    }
    finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($testDatabase) }
    $report.failureInjection = [ordered]@{ sha256 = Hash $testPackage; action = 'Deferred SYSTEM cmd.exe exit 42, after InitializeInstall, before commit'; published = $false }
    if ($PrepareOnly) {
        $report.passed = $true
    }
    else {
        Check (-not (Test-Path -LiteralPath $installRoot) -and -not (Test-Path -LiteralPath $sharedRoot) -and -not (Test-Path -LiteralPath $userRoot) -and $null -eq (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) 'Disposable runner starts without PURSOV STUDIO installation or data'
        $installationAttempted = $true
        Invoke-Msi '01-install-baseline' @('/i',$previous.Package)
        Assert-Installation $previous.Version
        Start-Service -Name $serviceName
        (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(15))
        $running = (Get-Service -Name $serviceName).Status.ToString()
        Stop-Service -Name $serviceName
        (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
        $stopped = (Get-Service -Name $serviceName).Status.ToString()
        $report.brokerSmoke = @{ afterStart = $running; afterStop = $stopped }
        Check ($running -eq 'Running' -and $stopped -eq 'Stopped') 'Installed broker reaches Running then Stopped'

        New-Item -ItemType Directory -Path $userRoot | Out-Null
        $settings = Join-Path $userRoot 'settings.json'
        $secret = Join-Path $userRoot 'tg-config.json'
        [IO.File]::WriteAllText($settings, '{"AutoStart":false,"AutoConnect":false,"InstallerTest":"synthetic-settings"}')
        [IO.File]::WriteAllText($secret, '{"InstallerTest":"synthetic-secret-never-used"}')
        $settingsHash = Hash $settings
        $secretHash = Hash $secret
        $pointer = Join-Path $sharedRoot 'zapret-active.json'
        $active = Get-Content -LiteralPath $pointer -Raw | ConvertFrom-Json
        $customList = Join-Path $active.Directory 'lists/list-general-user.txt'
        [IO.File]::WriteAllText($customList, "installer-preservation.invalid`r`n")
        $listHash = Hash $customList
        $pointerHash = Hash $pointer
        $installedBefore = Tree-Digest $installRoot
        $sharedBefore = Tree-Digest (Join-Path $sharedRoot 'zapret')

        Invoke-Msi '02-forced-rollback' @('/i',$testPackage) 1603
        $rollbackLog = Get-Content -LiteralPath (Join-Path $OutputDirectory '02-forced-rollback.log') -Raw
        $initializeIndex = $rollbackLog.IndexOf('CustomActionSchedule(Action=InitializeInstall,')
        $failureIndex = $rollbackLog.IndexOf('CustomActionSchedule(Action=TestForcedFailure,')
        $rollbackIndex = $rollbackLog.LastIndexOf('CustomActionSchedule(Action=RollbackLegacy,')
        Check ($initializeIndex -ge 0 -and $failureIndex -gt $initializeIndex -and $rollbackIndex -gt $failureIndex) 'Failure occurs after initializer and executes rollback helper'
        Assert-Installation $previous.Version
        Check ((Tree-Digest $installRoot) -ceq $installedBefore) 'Rollback restores every original installed file byte-for-byte'
        Check ((Tree-Digest (Join-Path $sharedRoot 'zapret')) -ceq $sharedBefore -and (Hash $pointer) -eq $pointerHash) 'Rollback restores shared payload and active pointer byte-for-byte'
        Check ((Hash $settings) -eq $settingsHash -and (Hash $secret) -eq $secretHash) 'Rollback preserves per-user settings and synthetic Telegram secret'

        Invoke-Msi '03-upgrade-candidate' @('/i',$candidate.Package)
        Assert-Installation $candidate.Version
        Check (-not (Test-Path -LiteralPath ('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + $previous.ProductCode))) 'Successful major upgrade removes old product registration'
        Check ((Hash $settings) -eq $settingsHash -and (Hash $secret) -eq $secretHash) 'Successful upgrade preserves per-user settings and synthetic Telegram secret'
        $active = Get-Content -LiteralPath $pointer -Raw | ConvertFrom-Json
        Check ((Hash (Join-Path $active.Directory 'lists/list-general-user.txt')) -eq $listHash) 'Successful upgrade preserves custom network list'

        Invoke-Msi '04-uninstall' @('/x',$candidate.ProductCode)
        Check ($null -eq (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) 'Uninstall removes broker service'
        Check (-not (Test-Path -LiteralPath (Join-Path $installRoot 'PursovStudio.exe'))) 'Uninstall removes application executable'
        foreach ($folder in @('CommonStartup','CommonPrograms','CommonDesktopDirectory')) {
            Check (-not (Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath($folder)) 'PURSOV STUDIO.lnk'))) ('Uninstall removes ' + $folder + ' shortcut')
        }
        Check ((Hash $settings) -eq $settingsHash -and (Hash $secret) -eq $secretHash) 'Uninstall preserves per-user settings and synthetic Telegram secret'
        Check ((Hash (Join-Path $active.Directory 'lists/list-general-user.txt')) -eq $listHash) 'Uninstall preserves shared user list'
        $report.passed = $true
    }
}
catch {
    $failure = $_
    $report.error = $_.Exception.Message
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $OutputDirectory 'failure.txt')
}
finally {
    # Best-effort cleanup of this product only. Never kill an active Windows Installer transaction.
    if ($installationAttempted -and -not $script:msiTimedOut) {
        foreach ($product in @($candidate,$previous)) {
            if ($null -ne $product -and (Test-Path -LiteralPath ('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + $product.ProductCode))) {
                try { Invoke-Msi ('cleanup-' + $product.Version) @('/x',$product.ProductCode) }
                catch { $report.cleanupError = $_.Exception.Message }
            }
        }
    }
    try {
        Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'MsiInstaller'; StartTime = [DateTime]::Parse($report.startedUtc) } -ErrorAction Stop |
            Select-Object TimeCreated, Id, LevelDisplayName, Message | ConvertTo-Json -Depth 4 |
            Set-Content -LiteralPath (Join-Path $OutputDirectory 'installer-events.json')
    }
    catch { $report.eventLogNote = $_.Exception.Message }
    # The deliberately corrupted MSI is disposable evidence, never an installable release artifact.
    $testCopy = Join-Path $OutputDirectory 'rollback-test-only.msi'
    try { if (Test-Path -LiteralPath $testCopy) { Remove-Item -LiteralPath $testCopy } }
    catch { $report.testCopyCleanupNote = $_.Exception.Message }
    $report.finishedUtc = [DateTime]::UtcNow.ToString('O')
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'report.json')
    $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}
if ($null -ne $failure) { throw $failure }
Write-Output ('PASS ' + $script:checks.Count + ' checks; evidence: ' + $OutputDirectory)
