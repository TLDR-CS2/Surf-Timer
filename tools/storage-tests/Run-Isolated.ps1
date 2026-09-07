[CmdletBinding()]
param([Parameter(Mandatory)][string]$MariaDbBin, [string]$DotNetPath)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$dotnet = & (Join-Path $workspace 'tools/Resolve-DotNet.ps1') -DotNetPath $DotNetPath
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$data = Join-Path $temporaryRoot ('surftimer-isolated-db-' + [Guid]::NewGuid().ToString('N'))
$oldConnection = $env:SURFTIMER_TEST_DB_CONNECTION_STRING
$process = $null
try {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $password = [Guid]::NewGuid().ToString('N')
    & (Join-Path $MariaDbBin 'mariadb-install-db.exe') "--datadir=$data" "--password=$password" "--port=$port" *> ($data + '.install.log')
    if ($LASTEXITCODE -ne 0) { throw 'Isolated MariaDB initialization failed.' }
    $process = Start-Process -FilePath (Join-Path $MariaDbBin 'mariadbd.exe') -ArgumentList @('--no-defaults', "--datadir=`"$data`"", "--port=$port", '--bind-address=127.0.0.1', '--console') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $data 'server.stdout.log') -RedirectStandardError (Join-Path $data 'server.stderr.log')
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($process.HasExited) { throw 'Isolated MariaDB exited during startup.' }
        $client = [Net.Sockets.TcpClient]::new()
        try { $client.Connect('127.0.0.1', $port); $ready = $true; break } catch { Start-Sleep -Milliseconds 250 } finally { $client.Dispose() }
    }
    if (!$ready) { throw 'Isolated MariaDB did not become ready.' }
    $env:SURFTIMER_TEST_DB_CONNECTION_STRING = "Server=127.0.0.1;Port=$port;User ID=root;Password=$password;Connection Timeout=5;Default Command Timeout=10;SSL Mode=Disabled"
    & $dotnet run --project (Join-Path $PSScriptRoot 'StorageTests.csproj') -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Storage integration checks failed.' }
}
finally {
    $env:SURFTIMER_TEST_DB_CONNECTION_STRING = $oldConnection
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force; if (!$process.WaitForExit(5000)) { throw 'Isolated MariaDB did not stop; its data directory was retained.' } }
    $resolved = [IO.Path]::GetFullPath($data)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $temporaryRoot -or [IO.Path]::GetFileName($resolved) -notmatch '^surftimer-isolated-db-[a-f0-9]{32}$') { throw 'Unsafe isolated database cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    if (Test-Path -LiteralPath ($resolved + '.install.log')) { Remove-Item -LiteralPath ($resolved + '.install.log') -Force }
}
