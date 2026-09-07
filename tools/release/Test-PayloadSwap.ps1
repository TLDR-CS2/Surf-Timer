[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PluginPayloadSwap.ps1')
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$fixture = Join-Path $workspace ('build\swap-test-' + [guid]::NewGuid().ToString('N'))
try {
    $payload = Join-Path $fixture 'payload'
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $payload 'SurfTimer.dll') 'new'
    $targets = @('first\swiftlys2', 'second\swiftlys2') | ForEach-Object {
        $root = Join-Path $fixture $_
        $plugin = Join-Path $root 'plugins\SurfTimer'
        New-Item -ItemType Directory -Path $plugin -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $plugin 'SurfTimer.dll') $_
        Set-Content -LiteralPath (Join-Path $plugin 'obsolete.dll') 'obsolete'
        [pscustomobject]@{ Root=$root; Source=$payload }
    }
    $snapshots = Join-Path $fixture 'snapshots'
    Invoke-PluginPayloadSwap -Targets $targets -SnapshotRoot $snapshots
    $manifest = @(Get-Content -LiteralPath (Join-Path $snapshots 'deployment-targets.json') -Raw | ConvertFrom-Json)
    if ($manifest.Count -ne 2 -or $manifest[0].Snapshot -eq $manifest[1].Snapshot) { throw 'Colliding instance names overwrote a snapshot.' }
    foreach ($target in $targets) {
        $plugin = Join-Path $target.Root 'plugins\SurfTimer'
        if (Test-Path -LiteralPath (Join-Path $plugin 'obsolete.dll')) { throw 'Upgrade retained a removed release file.' }
        if ((Get-Content -LiteralPath (Join-Path $plugin 'SurfTimer.dll') -Raw).Trim() -ne 'new') { throw 'Upgrade did not replace payload.' }
    }
    $firstPlugin = Join-Path $targets[0].Root 'plugins\SurfTimer\SurfTimer.dll'
    Set-Content -LiteralPath $firstPlugin 'must remain'
    $invalid = [pscustomobject]@{ Root=(Join-Path $fixture 'missing'); Source=$payload }
    $rejected = $false
    try { Invoke-PluginPayloadSwap -Targets @($targets[0],$invalid) -SnapshotRoot (Join-Path $fixture 'bad-snapshot') }
    catch { $rejected = $true }
    if (-not $rejected -or (Get-Content -LiteralPath $firstPlugin -Raw).Trim() -ne 'must remain') { throw 'Invalid second target mutated the first target.' }
    # Inject a failure during the second directory swap, after the first has completed.
    $script:injectedSwapFailure = $false
    function Move-Item {
        param([string]$LiteralPath, [string]$Destination)
        if (-not $script:injectedSwapFailure -and $LiteralPath.Contains('second') -and (Split-Path -Leaf $LiteralPath).StartsWith('.surftimer-stage-')) {
            $script:injectedSwapFailure = $true
            throw 'Injected swap failure'
        }
        Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination
    }
    $rejected = $false
    try { Invoke-PluginPayloadSwap -Targets $targets -SnapshotRoot (Join-Path $fixture 'rollback-snapshot') }
    catch { $rejected = $true }
    finally { Remove-Item Function:\Move-Item }
    if (-not $rejected -or -not $script:injectedSwapFailure) { throw 'Fault injection did not run.' }
    if ((Get-Content -LiteralPath $firstPlugin -Raw).Trim() -ne 'must remain') { throw 'Failed multi-instance swap did not restore first target.' }
    if ((Get-Content -LiteralPath (Join-Path $targets[1].Root 'plugins\SurfTimer\SurfTimer.dll') -Raw).Trim() -ne 'new') { throw 'Failed swap did not restore second target.' }
    Write-Host 'Payload swap replacement, collision, preflight and rollback tests passed.'
}
finally {
    $expected = [IO.Path]::GetFullPath((Join-Path $workspace 'build'))
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($fixture)) -ne $expected) { throw 'Invalid test cleanup path.' }
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}

