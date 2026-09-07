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
    Stop-Process -Id $server.Id -ErrorAction SilentlyContinue
    if (Get-Process -Id $server.Id -ErrorAction SilentlyContinue) {
        throw "Local CS2 server PID $($server.Id) did not stop."
    }
    Write-Host "Stopped local CS2 server PID $($server.Id) (or it had already exited)."
}
