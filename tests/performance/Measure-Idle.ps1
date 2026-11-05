# Run elevated after connecting and letting strategy selection finish. The operator sets window/tray mode.
[CmdletBinding()]
param(
    [ValidateSet('window', 'tray')][string]$Mode = 'window',
    [ValidateRange(1, 86400)][int]$Seconds = 600,
    [ValidateRange(1, 60)][int]$Interval = 5,
    [string]$OutputPath,
    [switch]$SelfCheck
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-SamePath([string]$Actual, [string]$Expected) {
    return $Actual -and [IO.Path]::GetFullPath($Actual).Equals([IO.Path]::GetFullPath($Expected), [StringComparison]::OrdinalIgnoreCase)
}

function Test-EnginePath([string]$Actual, [string]$Root) {
    if (-not $Actual) { return $false }
    $prefix = [regex]::Escape([IO.Path]::GetFullPath($Root).TrimEnd('\') + '\')
    return [IO.Path]::GetFullPath($Actual) -match ('^' + $prefix + '[0-9]+\.[0-9]+\.[0-9]+\\bin\\winws\.exe$')
}

function Get-CpuPercent([double]$Delta, [double]$Elapsed, [int]$Cores) {
    if ($Delta -lt 0 -or $Elapsed -le 0 -or $Cores -le 0) { throw 'Invalid CPU interval.' }
    return $Delta / $Elapsed / $Cores * 100
}

function Get-Identity($Entry) { return '{0}:{1}' -f $Entry.ProcessId, $Entry.StartTicks }

if ($SelfCheck) {
    $testRoot = 'C:\ProgramData\PURSOV STUDIO\zapret'
    if (-not (Test-EnginePath "$testRoot\1.10.4\bin\winws.exe" $testRoot)) { throw 'Engine path rejected.' }
    if (Test-EnginePath "${testRoot}-other\1.10.4\bin\winws.exe" $testRoot) { throw 'Sibling path accepted.' }
    if (Test-EnginePath "$testRoot\1.10.4\bin\..\winws.exe" $testRoot) { throw 'Unexpected engine path accepted.' }
    if (Test-SamePath 'C:\Other\python.exe' 'C:\Program Files\PURSOV STUDIO\runtime\python\python.exe') { throw 'Foreign Python accepted.' }
    if ([Math]::Abs((Get-CpuPercent 0.4 5 16) - 0.5) -gt 0.000001) { throw 'CPU normalization failed.' }
    if ((Get-Identity @{ ProcessId = 10; StartTicks = 1 }) -eq (Get-Identity @{ ProcessId = 10; StartTicks = 2 })) { throw 'PID reuse accepted.' }
    Write-Output 'PASS: 6 isolated path, CPU and process identity checks; no host processes accessed.'
    return
}

if (-not $OutputPath) { throw 'Specify -OutputPath for the JSON report.' }
if ($Interval -gt $Seconds) { throw 'Interval must not exceed Seconds.' }
if (-not [Environment]::Is64BitProcess) { throw 'Use 64-bit PowerShell for the installed x64 application.' }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run PowerShell elevated to read the service processes.' }
$reportPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $reportPath) { throw 'OutputPath already exists; choose a new report path.' }
$null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($reportPath)) -Force
$appRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'PURSOV STUDIO'
$appExe = Join-Path $appRoot 'PursovStudio.exe'
$installedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($appExe)
if ($installedVersion.FileMajorPart -ne 2) { throw 'The installed application is not PURSOV STUDIO 2.x.' }
$pythonExe = Join-Path $appRoot 'runtime\python\python.exe'
$engineRoot = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'PURSOV STUDIO\zapret'
$session = [Diagnostics.Process]::GetCurrentProcess().SessionId
$cores = [int](Get-CimInstance Win32_ComputerSystem -Property NumberOfLogicalProcessors).NumberOfLogicalProcessors
if ($cores -lt 1) { throw 'Logical processor count unavailable.' }

function Read-OwnedProcesses {
    $service = Get-CimInstance Win32_Service -Filter "Name='PursovStudioBroker'" -Property ProcessId, State
    if (-not $service -or $service.State -ne 'Running' -or $service.ProcessId -eq 0) { throw 'Expected broker service is not running.' }
    $candidates = @(Get-CimInstance Win32_Process -Filter "Name='PursovStudio.exe' OR Name='python.exe' OR Name='winws.exe'" -Property ProcessId, ParentProcessId, ExecutablePath, SessionId)
    $uis = @($candidates | Where-Object { (Test-SamePath $_.ExecutablePath $appExe) -and $_.ProcessId -ne $service.ProcessId -and $_.SessionId -eq $session })
    if ($uis.Count -ne 1) { throw "Expected one installed UI in this session; found $($uis.Count)." }
    $uiId = $uis[0].ProcessId
    $roles = [ordered]@{
        ui = $uis
        broker = @($candidates | Where-Object { $_.ProcessId -eq $service.ProcessId -and (Test-SamePath $_.ExecutablePath $appExe) })
        telegram = @($candidates | Where-Object { $_.ParentProcessId -eq $uiId -and (Test-SamePath $_.ExecutablePath $pythonExe) })
        zapret = @($candidates | Where-Object { $_.ParentProcessId -eq $service.ProcessId -and (Test-EnginePath $_.ExecutablePath $engineRoot) })
    }
    foreach ($role in $roles.Keys) {
        $matches = @($roles[$role])
        if ($matches.Count -ne 1) { throw "Expected one $role process; found $($matches.Count). Finish diagnostics before measuring idle." }
        $process = [Diagnostics.Process]::GetProcessById([int]$matches[0].ProcessId)
        try {
            $process.Refresh()
            if (-not (Test-SamePath $process.MainModule.FileName $matches[0].ExecutablePath)) { throw "$role process image changed." }
            $started = $process.StartTime.ToUniversalTime()
            [pscustomobject]@{
                Role = $role; ProcessId = $process.Id; StartTicks = $started.Ticks; StartUtc = $started.ToString('O')
                Executable = $process.MainModule.FileName; CpuSeconds = $process.TotalProcessorTime.TotalSeconds
                PrivateMiB = $process.PrivateMemorySize64 / 1MB; UptimeSeconds = ([DateTime]::UtcNow - $started).TotalSeconds
            }
        }
        finally { $process.Dispose() }
    }
}

$samples = [Collections.Generic.List[object]]::new()
$issues = [Collections.Generic.List[string]]::new()
$gaps = [Collections.Generic.List[object]]::new()
$first = @{}
$last = @{}
$peaks = @{}
$startedUtc = [DateTime]::UtcNow.ToString('O')
$watch = [Diagnostics.Stopwatch]::StartNew()
$completed = $false
try {
    $nextDue = 0.0
    while ($true) {
        $delay = $nextDue - $watch.Elapsed.TotalSeconds
        if ($delay -gt 0) { Start-Sleep -Milliseconds ([int][Math]::Ceiling($delay * 1000)) }
        $entries = @(Read-OwnedProcesses)
        $elapsed = $watch.Elapsed.TotalSeconds
        $baseline = $samples.Count -eq 0
        $gap = if ($baseline) { 0.0 } else { $elapsed - $samples[$samples.Count - 1].ElapsedSeconds }
        if (-not $baseline -and $gap -gt $Interval * 2) {
            $gaps.Add([pscustomobject]@{ ElapsedSeconds = $elapsed; GapSeconds = $gap })
        }
        $rows = foreach ($entry in $entries) {
            $role = $entry.Role
            if (-not $baseline -and (Get-Identity $entry) -ne (Get-Identity $first[$role])) { throw "$role disappeared or restarted; PID/start-time identity changed." }
            if ($baseline) { $first[$role] = $entry; $peaks[$role] = 0.0 }
            $cpu = if ($baseline) { $null } else { Get-CpuPercent ($entry.CpuSeconds - $last[$role].CpuSeconds) $gap $cores }
            $peaks[$role] = [Math]::Max($peaks[$role], $entry.PrivateMiB)
            $last[$role] = $entry
            [pscustomobject]@{ Role = $role; ProcessId = $entry.ProcessId; StartTicks = $entry.StartTicks; CpuPercent = $cpu; PrivateMiB = $entry.PrivateMiB; UptimeSeconds = $entry.UptimeSeconds }
        }
        $samples.Add([pscustomobject]@{
            Utc = [DateTime]::UtcNow.ToString('O'); ElapsedSeconds = $elapsed; GapSeconds = $gap; Processes = @($rows)
            CpuPercent = if ($baseline) { $null } else { ($rows | Measure-Object CpuPercent -Sum).Sum }
            PrivateMiB = ($rows | Measure-Object PrivateMiB -Sum).Sum
        })
        if (-not $baseline -and ($elapsed - $samples[0].ElapsedSeconds) -ge $Seconds) { $completed = $true; break }
        $nextDue = [Math]::Min($samples[0].ElapsedSeconds + $Seconds, $elapsed + $Interval)
    }
}
catch { $issues.Add($_.Exception.Message) }
finally { $watch.Stop() }

$measuredSeconds = if ($samples.Count -gt 1) { $samples[$samples.Count - 1].ElapsedSeconds - $samples[0].ElapsedSeconds } else { 0.0 }
$processSummary = foreach ($role in $first.Keys | Sort-Object) {
    [pscustomobject]@{
        Role = $role; ProcessId = $first[$role].ProcessId; StartTicks = $first[$role].StartTicks
        StartUtc = $first[$role].StartUtc; Executable = $first[$role].Executable; SampleCount = $samples.Count
        UptimeAtStartSeconds = $first[$role].UptimeSeconds; UptimeAtEndSeconds = $last[$role].UptimeSeconds
        CpuSeconds = $last[$role].CpuSeconds - $first[$role].CpuSeconds
        AverageCpuPercent = if ($measuredSeconds -gt 0) { Get-CpuPercent ($last[$role].CpuSeconds - $first[$role].CpuSeconds) $measuredSeconds $cores } else { $null }
        CurrentPrivateMiB = $last[$role].PrivateMiB; PeakPrivateMiB = $peaks[$role]
    }
}
$averageCpu = if ($measuredSeconds -gt 0) { ($processSummary | Measure-Object AverageCpuPercent -Sum).Sum } else { $null }
$peakPrivate = if ($samples.Count) { ($samples | Measure-Object PrivateMiB -Maximum).Maximum } else { $null }
$valid = $completed -and $issues.Count -eq 0 -and $gaps.Count -eq 0
$report = [ordered]@{
    Mode = $Mode; ModeVerification = 'Operator confirmed; script does not inspect or change window visibility.'
    StartedUtc = $startedUtc; FinishedUtc = [DateTime]::UtcNow.ToString('O'); RequestedSeconds = $Seconds
    MeasuredSeconds = $measuredSeconds; WallSeconds = $watch.Elapsed.TotalSeconds; IntervalSeconds = $Interval
    LogicalProcessors = $cores; InstalledFileVersion = $installedVersion.FileVersion
    Completed = $completed; Valid = $valid; Issues = @($issues.ToArray())
    SampleCount = $samples.Count; IntervalCount = [Math]::Max(0, $samples.Count - 1)
    MaximumSamplingGapSeconds = if ($samples.Count) { ($samples | Measure-Object GapSeconds -Maximum).Maximum } else { $null }
    SamplingGapsOverTwiceInterval = @($gaps.ToArray()); AverageCpuPercent = $averageCpu
    CurrentPrivateMiB = if ($samples.Count) { $samples[$samples.Count - 1].PrivateMiB } else { $null }; PeakPrivateMiB = $peakPrivate
    CpuTargetPassed = $valid -and $averageCpu -le 0.5; MemoryTargetPassed = $valid -and $peakPrivate -le (250000000 / 1MB)
    MemoryTargetBytes = 250000000; Processes = @($processSummary); Samples = @($samples.ToArray())
    Limitations = 'Sampled user-mode process memory, not kernel/driver memory. Brief processes between samples and subinterval memory peaks can be missed. Idle connected state must remain unchanged; no user traffic or manual diagnostics. PowerShell/CIM measurement overhead is excluded from target CPU.'
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Output ([pscustomobject]@{ Report = $reportPath; Valid = $valid; Samples = $samples.Count; Seconds = $measuredSeconds; AverageCpuPercent = $averageCpu; PeakPrivateMiB = $peakPrivate })
if (-not $valid) { exit 1 }
