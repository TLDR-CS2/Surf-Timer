# SharpTimer MariaDB importer

Imports Martian/poor-SharpTimer `PlayerRecords` and `PlayerStats` data into SurfTimer. The default run only reports what would change.

By default it selects `surf_*`, style `0`, mode `Standard`. Use `--mode Custom` if that is how the source server stored Surf records. Faster SurfTimer PBs are kept. Completion counts use the larger value, so rerunning an import does not keep adding completions.

```powershell
$env:SURFTIMER_IMPORT_SOURCE = 'Server=127.0.0.1;Database=sharptimer;User ID=...;Password=...'
$env:SURFTIMER_IMPORT_TARGET = 'Server=127.0.0.1;Database=surftimer;User ID=...;Password=...'
& '.\.research\.dotnet\dotnet.exe' run --project '.\tools\sharptimer-import' -- --mode Standard

# After reviewing the dry-run counts:
& '.\.research\.dotnet\dotnet.exe' run --project '.\tools\sharptimer-import' -- --mode Standard --commit
```

If both schemas are in the same database, omit `SURFTIMER_IMPORT_TARGET`. For a prefixed SharpTimer player table, pass its full table name with `--player-stats PlayerStats_yourprefix`.

Stage records are not imported because SharpTimer's independent stage times do not map to SurfTimer's PB splits. Replay files also require a separate conversion.
