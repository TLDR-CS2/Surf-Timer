# Storage integration tests

Runs the real record repository against an isolated `st_test_<GUID>` MySQL/MariaDB database. The database account must be able to create and drop databases. Production tables are never used. The generated database is dropped by default, including after test failures.

Set `SURFTIMER_TEST_DB_CONNECTION_STRING` to a test server connection string, then run:

```powershell
dotnet run --project tools/storage-tests
```

If the environment variable is absent, the server credentials come from the local server's `database.local.jsonc`; the production database name is still replaced with a generated isolated name. Do not print credentials. Temporary pending-run data stays under `build/storage-tests/<schema>` for failure inspection.

The suite checks interrupted migrations, main/bonus/stage idempotency, missing replay replacement, receipt ruleset provenance, reversible record moderation and rollback when a newer PB exists, catalog authority, and durable recovery after database failure.

For coordinated local API testing, `--keep-db` retains the generated schema and updates `build/storage-test-runtime.local.json` (which must already contain the temporary server runtime handle) with its connection string and schema. Explicit `--seed-api` uses the environment connection's existing isolated `st_test_<GUID>` schema to add tie/unranked/empty-route fixtures. These options are for development only. Stop the temporary server after all participating tests finish.
