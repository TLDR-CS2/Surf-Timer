# Multi-server configuration

Each CS2 instance must use a unique `SurfTimer.ServerId` and the same logical
Swiftly database connection name. The three example configurations use
`surf-1`, `surf-2`, and `surf-3`.

Copy the applicable `config.jsonc` into that server's Swiftly SurfTimer config
directory. Configure the `surftimer` connection in Swiftly's `database.jsonc`
on every host so all instances resolve it to the same MariaDB database.

Server IDs are persisted with player connections, map records, completions,
PBs, splits, and replay ownership metadata. Do not reuse an ID for two live
instances. SurfTimer refuses to load with `change-me`, an empty ID, an ID over
64 characters, uppercase characters, spaces, or unsupported punctuation.

After deployment, run these commands on each instance:

```text
surftimer_status
surftimer_db_health
```

Confirm each status reports a different server ID and each health check reports
the same connection name with `healthy` status.
