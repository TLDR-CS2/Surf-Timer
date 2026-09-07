# Replays

## Native playback setup

BotController must load through Metamod. In `game/csgo/gameinfo.gi`, keep the Metamod search-path entry before SwiftlyS2:

```text
Game csgo/addons/metamod
Game csgo/addons/swiftlys2
```

After a CS2 update, check both entries and restart. `tools/local-server/Repair-PluginLoadPaths.ps1 -GameInfoPath <path>` restores this order.

Run `meta list` and `bc_status` in the server console. BotController should be loaded and its input, weapon, bot and buy hooks should report `ok`. ABI 18 availability alone does not confirm native hook initialization. Do not place a duplicate BotController DLL beside `cs2.exe`.

## Playback and controls

Replay selection and controls are per viewer. Main, stage and bonus selection use leaderboard competition ranks. Ties select the earliest PB timestamp, then Steam ID. A missing capture or skipped rank returns no replay.

`!replay pause`, `!replay seek <seconds>` and `!replay speed <0.25-4>` switch to compatibility playback. The shared API has no native pause, seek or speed controls. A capture without compatibility frames cannot use these controls. Redundant `resume` and `speed 1` commands preserve native playback.

Compatibility playback interpolates position, velocity and camera angles while preserving button states, teleports and recording gaps. Replay viewing hides inventory weapons and prevents pickup; stopping restores the saved return position and weapon state.

`MaximumReplayMinutes` defaults to 30. At the limit, capture stops and timing continues without a replay. This limits each capture, not total process memory.

## Native stalls

After one second of simulation time without cursor progress, playback falls back to compatibility frames. Startup is retried once if the cursor has never advanced. A mid-run stall keeps the last displayed position. Stalls also request `bc_status` in the server console.

For a stall, collect the managed replay diagnostics, BotController startup output and `bc_status` while reproducing it. Check loader entries, hooks, the native binary and gamedata against the running server build. A successful API call to start playback does not prove the movement hooks are running.

## In-game checks

- Watch main, bonus and stage replays; verify native cursor progress and camera movement.
- Exercise pause, resume, seek, 0.25x/2x/4x speed, loop boundaries and ramp teleports.
- Check weapon visibility and return position after `!replay stop`.
- Use two simultaneous viewers and check cleanup on death, disconnect and map change.

These checks require a connected player. Hook status and regression tests alone do not verify playback fidelity.
