# SurfTimer soak tests

Runs a synthetic 64 Hz HUD/replay load while writing records and querying leaderboards. Database work is limited to temporary `st_soak_*` tables.

```powershell
& '.\.research\.dotnet\dotnet.exe' run --project tools\soak-tests\SoakTests.csproj -c Release -- 32 60
```

Arguments are player count (1–64) and duration in seconds (10–3600). The release run is `64 3600` with both test servers online.
