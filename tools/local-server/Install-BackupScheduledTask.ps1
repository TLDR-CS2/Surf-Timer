[CmdletBinding()]
param(
    [ValidatePattern('^([01]\d|2[0-3]):[0-5]\d$')]
    [string]$DailyAt = "04:00",
    [ValidateRange(2, 365)]
    [int]$KeepLatest = 14
)

$ErrorActionPreference = "Stop"
$taskName = "SurfTimer-MariaDB-Backup"
$runner = Join-Path $PSScriptRoot "Invoke-ScheduledBackup.ps1"
$powershell = Join-Path $PSHOME "powershell.exe"
if (-not (Test-Path -LiteralPath $powershell)) {
    $powershell = (Get-Command powershell.exe -ErrorAction Stop).Source
}
$arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$runner`" -KeepLatest $KeepLatest"
$action = New-ScheduledTaskAction -Execute $powershell -Argument $arguments -WorkingDirectory $PSScriptRoot
$trigger = New-ScheduledTaskTrigger -Daily -At $DailyAt
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2)
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Limited
$task = New-ScheduledTask -Action $action -Trigger $trigger -Settings $settings -Principal $principal `
    -Description "Nightly transactional SurfTimer MariaDB backup, integrity audit, and retention."
Register-ScheduledTask -TaskName $taskName -InputObject $task -Force | Out-Null

Write-Host "Scheduled task '$taskName' installed for $DailyAt daily (keep latest $KeepLatest)."
Write-Host "The task runs as $identity when that user is logged on and starts when available after a missed run."
