[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath
)

$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$PackagePath = [System.IO.Path]::GetFullPath($PackagePath)
if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf) -or [System.IO.Path]::GetExtension($PackagePath) -ne ".zip") {
    throw "Release package must be an existing .zip file: $PackagePath"
}

$checksumPath = $PackagePath + ".sha256"
if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) { throw "Release checksum is missing: $checksumPath" }
$expectedHash = ((Get-Content -LiteralPath $checksumPath -First 1) -split '\s+')[0].ToLowerInvariant()
$actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $PackagePath).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) { throw "Release SHA-256 verification failed." }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
try {
    $entries = @($archive.Entries)
    if ($entries.Count -eq 0) { throw "Release archive is empty." }
    foreach ($entry in $entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($name.StartsWith('/') -or $name -match '(^|/)\.\.(/|$)' -or $name -match '^[A-Za-z]:') {
            throw "Release archive contains an unsafe path: $($entry.FullName)"
        }
    }

    $required = @(
        "release-manifest.json",
        "SurfTimer/SurfTimer.dll",
        "SurfTimer/SurfTimer.deps.json",
        "SurfTimer/resources/configs/config.jsonc"
    )
    $names = @($entries | ForEach-Object { $_.FullName.Replace('\', '/').TrimEnd('/') })
    foreach ($requiredEntry in $required) {
        if ($requiredEntry -notin $names) { throw "Release archive is missing $requiredEntry" }
    }

    $manifestEntry = $entries | Where-Object { $_.FullName.Replace('\', '/') -eq "release-manifest.json" } | Select-Object -First 1
    $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json }
    finally { $reader.Dispose() }

    $buildInfo = Get-Content -Raw -LiteralPath (Join-Path $workspace "src\BuildInfo.cs")
    $versionMatch = [regex]::Match($buildInfo, 'Version\s*=\s*"([^"]+)"')
    if (-not $versionMatch.Success -or $manifest.version -ne $versionMatch.Groups[1].Value) {
        throw "Release manifest version does not match BuildInfo.Version."
    }

    $migrationVersions = Get-ChildItem -File -LiteralPath (Join-Path $workspace "resources\migrations\mysql") -Filter "*.sql" |
        ForEach-Object { if ($_.BaseName -match '^(\d+)_') { [int]$Matches[1] } else { throw "Invalid migration filename: $($_.Name)" } }
    $expectedSchema = ($migrationVersions | Measure-Object -Maximum).Maximum
    if ([int]$manifest.databaseSchemaVersion -ne $expectedSchema) {
        throw "Release schema version $($manifest.databaseSchemaVersion) does not match latest migration $expectedSchema."
    }
    if (-not $manifest.requiredPlugins -or $manifest.requiredPlugins -notcontains "BotController shared API ABI 18") {
        throw "Release manifest does not declare the current BotController ABI dependency."
    }

    $packagedMigrations = @($names | Where-Object { $_ -match '^SurfTimer/resources/migrations/mysql/\d+_.+\.sql$' })
    if ($packagedMigrations.Count -ne $migrationVersions.Count) {
        throw "Release contains $($packagedMigrations.Count) migrations; expected $($migrationVersions.Count)."
    }
    $unexpected = @($names | Where-Object {
        $_ -ne "release-manifest.json" -and $_ -notmatch '^SurfTimer(/|$)'
    })
    if ($unexpected.Count -gt 0) { throw "Release contains files outside the portable plugin payload: $($unexpected -join ', ')" }
    if ($names -contains "SurfTimer/SurfTimer.pdb") { throw "Release contains development symbols with potential local source paths." }

    $portableTextExtensions = @('.json', '.jsonc', '.sql', '.md', '.txt')
    foreach ($entry in $entries) {
        if ($portableTextExtensions -notcontains [System.IO.Path]::GetExtension($entry.FullName).ToLowerInvariant()) { continue }
        $entryReader = [System.IO.StreamReader]::new($entry.Open())
        try { $entryText = $entryReader.ReadToEnd() }
        finally { $entryReader.Dispose() }
        foreach ($forbidden in @('C:\CS2Server', '127.0.0.1:27015', '127.0.0.1:27016', '"ServerId": "surf-1"', '"ServerId": "surf-3"')) {
            if ($entryText.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw "Release text contains local deployment value '$forbidden' in $($entry.FullName)."
            }
        }
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Release package validation passed: version=$($manifest.version), schema=$($manifest.databaseSchemaVersion), entries=$($entries.Count)"
