# SharpTimer MariaDB importer

This tool imports the latest Martian/poor-SharpTimer `PlayerRecords` and `PlayerStats` schema into SurfTimer's global MariaDB schema. It defaults to a read-only dry run and is safe to rerun.

The default selection is main-route `surf_*` records in style `0`, mode `Standard`. Use `--mode Custom` if that was the source server's configured Surf mode. Existing faster SurfTimer PBs are retained, and completion counts use the larger value rather than being added repeatedly.

```powershell
$env:SURFTIMER_IMPORT_SOURCE = 'Server=127.0.0.1;Database=sharptimer;User ID=...;Password=...'
$env:SURFTIMER_IMPORT_TARGET = 'Server=127.0.0.1;Database=surftimer;User ID=...;Password=...'
& '.\.research\.dotnet\dotnet.exe' run --project '.\tools\sharptimer-import' -- --mode Standard

# After reviewing the dry-run counts:
& '.\.research\.dotnet\dotnet.exe' run --project '.\tools\sharptimer-import' -- --mode Standard --commit
```

If both schemas are in the same database, omit `SURFTIMER_IMPORT_TARGET`. For a prefixed SharpTimer player table, pass its full table name with `--player-stats PlayerStats_yourprefix`.

SharpTimer stage records are independent best-stage times rather than the cumulative checkpoint splits belonging to a particular PB, so they are not imported. SharpTimer replay files also require a separate format conversion milestone.
