# SurfTimer web API tests

Run the read-only integration suite against a SurfTimer website instance:

```powershell
.\tools\web-api\Test-WebApi.ps1
```

Use `-BaseUrl` for a deployed environment. `-IncludeRateLimit` additionally verifies the configured request ceiling; it intentionally exhausts the calling client's allowance for up to one minute.
