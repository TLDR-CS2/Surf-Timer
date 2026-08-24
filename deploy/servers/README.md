# Multi-server configuration

Give each CS2 instance a unique `SurfTimer.ServerId`. Instances sharing records must point their Swiftly database connection at the same MariaDB database.

The example configs use `surf-1`, `surf-2` and `surf-3`. Copy the relevant file to the instance's Swiftly SurfTimer config directory and configure the `surftimer` connection in SwiftlyS2.

Do not reuse an ID on two live instances. IDs are lowercase, at most 64 characters, and may contain numbers, `.`, `_` and `-`. SurfTimer refuses to load with `change-me` or an invalid ID.

After deployment, run these commands on each instance:

```text
surftimer_status
surftimer_db_health
```

Check that the server IDs differ and both database checks are healthy.
