# SurfTimer soak tests

Runs a configurable 64 Hz synthetic HUD/replay workload alongside concurrent MariaDB record writes and leaderboard queries.
All database work uses randomly named disposable `st_soak_*` tables; production `st_*` tables are never modified.

```powershell
& '.\.research\.dotnet\dotnet.exe' run --project tools\soak-tests\SoakTests.csproj -c Release -- 32 60
```

Arguments are player count (1–64) and duration in seconds (10–3600). For release qualification, run `64 3600` while both game servers are online.
