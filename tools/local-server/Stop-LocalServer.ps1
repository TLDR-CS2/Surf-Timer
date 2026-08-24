[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$serverExe = "C:\CS2Server\server\game\bin\win64\cs2.exe"
$servers = Get-Process cs2 -ErrorAction SilentlyContinue |
    Where-Object Path -eq $serverExe

if (-not $servers) {
    Write-Host "The local CS2 server is not running."
    return
}

foreach ($server in $servers) {
    Stop-Process -Id $server.Id
    Write-Host "Stopped local CS2 server PID $($server.Id)."
}
