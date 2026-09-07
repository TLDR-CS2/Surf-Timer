# Stages every replacement and snapshots every destination before swapping any instance.
function Invoke-PluginPayloadSwap {
    param([Parameter(Mandatory)][object[]]$Targets, [Parameter(Mandatory)][string]$SnapshotRoot)
    if ($Targets.Count -eq 0) { throw "At least one Swiftly root is required." }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $prepared = @()
    foreach ($target in $Targets) {
        $root = [IO.Path]::GetFullPath($target.Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $parent = [IO.Path]::GetFullPath((Join-Path $root 'plugins'))
        $destination = [IO.Path]::GetFullPath((Join-Path $parent 'SurfTimer'))
        if (-not $seen.Add($destination)) { throw "Duplicate installation target: $destination" }
        if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw "Missing plugins directory: $parent" }
        # Junctions can redirect otherwise valid-looking paths outside the intended server.
        $cursor = Get-Item -LiteralPath $parent
        while ($cursor) {
            if ($cursor.LinkType) { throw "Deployment target traverses a link: $($cursor.FullName)" }
            $cursor = $cursor.Parent
        }
        if (Test-Path -LiteralPath $destination) {
            if ((Get-Item -LiteralPath $destination).LinkType) { throw "Plugin destination is a link: $destination" }
            if (Get-ChildItem -LiteralPath $destination -Recurse -Force | Where-Object { $_.LinkType }) {
                throw "Existing plugin contains links: $destination"
            }
        }
        if (-not (Test-Path -LiteralPath (Join-Path $target.Source 'SurfTimer.dll') -PathType Leaf)) { throw "Invalid plugin payload: $($target.Source)" }
        if (Get-ChildItem -LiteralPath $target.Source -Recurse -Force | Where-Object { $_.LinkType }) {
            throw "Source payload contains links: $($target.Source)"
        }
        $key = '{0:D3}-{1}' -f $prepared.Count, (Split-Path -Leaf $root)
        $prepared += [pscustomobject]@{ Root = $root; Source = $target.Source; Destination = $destination; Parent = $parent;
            Stage = Join-Path $parent ('.surftimer-stage-' + [guid]::NewGuid().ToString('N'));
            Previous = Join-Path $parent ('.surftimer-previous-' + [guid]::NewGuid().ToString('N'));
            Snapshot = $key; Existed = Test-Path -LiteralPath $destination; Swapped = $false; MovedOld = $false }
    }
    New-Item -ItemType Directory -Path $SnapshotRoot -ErrorAction Stop | Out-Null
    try {
        foreach ($entry in $prepared) {
            Copy-Item -LiteralPath $entry.Source -Destination $entry.Stage -Recurse -Force
            if ($entry.Existed) {
                Copy-Item -LiteralPath $entry.Destination -Destination (Join-Path $SnapshotRoot $entry.Snapshot) -Recurse -Force
            }
        }
        @($prepared | Select-Object Root, Snapshot, Existed) | ConvertTo-Json -AsArray | Set-Content -LiteralPath (Join-Path $SnapshotRoot 'deployment-targets.json') -Encoding UTF8
        foreach ($entry in $prepared) {
            if ([IO.Path]::GetDirectoryName($entry.Destination) -ne $entry.Parent -or [IO.Path]::GetDirectoryName($entry.Previous) -ne $entry.Parent -or [IO.Path]::GetDirectoryName($entry.Stage) -ne $entry.Parent) {
                throw "Invalid resolved swap path."
            }
            if ($entry.Existed) { Move-Item -LiteralPath $entry.Destination -Destination $entry.Previous; $entry.MovedOld = $true }
            Move-Item -LiteralPath $entry.Stage -Destination $entry.Destination
            $entry.Swapped = $true
        }
    }
    catch {
        $failure = $_
        foreach ($entry in @($prepared | Sort-Object Destination -Descending)) {
            try {
                if ($entry.Swapped) {
                    # Move the new payload back to its unique stage before restoring the old directory.
                    if ([IO.Path]::GetDirectoryName($entry.Destination) -ne $entry.Parent -or [IO.Path]::GetDirectoryName($entry.Stage) -ne $entry.Parent) { throw 'Invalid rollback path.' }
                    Move-Item -LiteralPath $entry.Destination -Destination $entry.Stage
                }
                if ($entry.MovedOld) {
                    if ([IO.Path]::GetDirectoryName($entry.Previous) -ne $entry.Parent -or [IO.Path]::GetDirectoryName($entry.Destination) -ne $entry.Parent) { throw 'Invalid rollback path.' }
                    Move-Item -LiteralPath $entry.Previous -Destination $entry.Destination
                }
            }
            catch { Write-Warning "Automatic rollback failed for $($entry.Destination). Recover from $SnapshotRoot. $_" }
        }
        throw $failure
    }
    finally {
        foreach ($entry in $prepared) {
            # Never delete the previous directory: it is a second recovery copy if rollback was interrupted.
            if ([IO.Path]::GetDirectoryName($entry.Stage) -ne $entry.Parent) { throw 'Invalid cleanup path.' }
            if (Test-Path -LiteralPath $entry.Stage) { Remove-Item -LiteralPath $entry.Stage -Recurse -Force }
        }
    }
    foreach ($entry in $prepared) {
        if ([IO.Path]::GetDirectoryName($entry.Previous) -ne $entry.Parent) { throw 'Invalid previous-payload cleanup path.' }
        if (Test-Path -LiteralPath $entry.Previous) { Remove-Item -LiteralPath $entry.Previous -Recurse -Force }
        Write-Host "Installed complete payload to $($entry.Destination)"
    }
}

