function Get-SurfTimerDatabaseToolContext {
    [CmdletBinding()]
    param()

    $workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $settingsPath = Join-Path $PSScriptRoot "database.local.jsonc"
    if (-not (Test-Path -LiteralPath $settingsPath)) {
        throw "Local database settings were not found at $settingsPath."
    }

    $settings = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
    $connectionName = $settings.default_connection
    $connection = $settings.connections.$connectionName
    if ($null -eq $connection -or $connection.driver -ne "mysql") {
        throw "Database connection '$connectionName' is missing or is not a MySQL/MariaDB connection."
    }

    $distribution = Get-ChildItem -Directory -LiteralPath "C:\CS2Server\mariadb" -Filter "mariadb-*-winx64" |
        Sort-Object Name -Descending |
        Select-Object -First 1
    if ($null -eq $distribution) { throw "Portable MariaDB was not found under C:\CS2Server\mariadb." }

    [pscustomobject]@{
        Workspace = $workspace
        Connection = $connection
        ClientExe = Join-Path $distribution.FullName "bin\mariadb.exe"
        DumpExe = Join-Path $distribution.FullName "bin\mariadb-dump.exe"
    }
}

function New-SurfTimerClientDefaultsFile {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Connection)

    $temporary = [System.IO.Path]::GetTempFileName()
    @(
        "[client]"
        "host=$($Connection.host)"
        "port=$($Connection.port)"
        "user=$($Connection.user)"
        "password=$($Connection.pass)"
        "default-character-set=utf8mb4"
        # These tools target the loopback-only portable development database.
        # Explicitly disable TLS so the MariaDB client does not request a
        # Windows Schannel client credential on newer client builds.
        "ssl=0"
    ) | Set-Content -LiteralPath $temporary -Encoding UTF8
    return $temporary
}
