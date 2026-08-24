# Map catalog

SurfTimer uses mapper-created trigger entities exclusively. A JSON definition describes the
expected trigger names; it does not create or replace zones. Run `surftimer_map_check` after a
map loads to verify its live entities, and `surftimer_catalog_check` to validate the static catalog.

| Map | Tier | Structure | Bonuses | Workshop ID |
|---|---:|---|---:|---:|
| surf_boreas | 1 | Linear, 2 checkpoints | 0 | 3133346713 |
| surf_kitsune | 1 | Staged, 9 stages | 0 | 3076153623 |
| surf_mesa_revo | 1 | Linear | 0 | 3076980482 |
| surf_mom | 1 | Staged, 6 stages | 1 | 3282137145 |
| surf_prisma | 1 | Staged, 7 stages | 3 | 3319154265 |
| surf_aquaflow | 2 | Linear, 2 checkpoints | 0 | 3255589335 |
| surf_newbie | 2 | Staged, 4 stages | 3 | 3263974751 |
| surf_cyka_ksf | 3 | Staged, 4 stages | 2 | 3263197243 |
| surf_mesa_aether | 3 | Linear, 6 checkpoints | 0 | 3125360522 |
| surf_zeitgeist | 3 | Staged, 12 stages | 4 | 3265329080 |
| surf_jive | 3 | Linear | 2 | 3318285030 |
| surf_cannonball | 4 | Linear, 4 checkpoints | 2 | 3152119098 |
| surf_sippysip | 4 | Linear, 4 checkpoints | 2 | 3246776437 |
| surf_elysium | 5 | Staged, 4 stages | 1 | 3147764666 |
| surf_lt_omnific | 6 | Staged, 18 stages | 3 | 3660894345 |
| surf_goliath | 7 | Staged, 4 stages | 4 | 3448505317 |

Workshop metadata is discovery information only. The runtime compatibility result is authoritative
for timer support because Workshop maps can be updated independently of this repository.
