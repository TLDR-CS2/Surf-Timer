[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$taskName = "SurfTimer-MariaDB-Backup"
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($null -eq $task) {
    Write-Host "Scheduled task '$taskName' is not installed."
    return
}
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
Write-Host "Scheduled task '$taskName' removed. Existing backups were preserved."
