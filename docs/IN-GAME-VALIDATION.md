# In-game validation

Run this checklist against the release candidate. Record the commit and results; automated builds and isolated database checks do not replace in-game testing.

Use a test deployment of this build with two human players, one with `surftimer.admin`. Use one working linear map and one staged map; include a bonus route if available. Before each test record the map, player, server ID, time, and starting PB/completion count. Use `!stadmin` and `!stmapcheck` to capture initial health. Database outages, plugin hot reload and the two-server test need administrator assistance; do not interrupt a production database to perform them.

## 1. Saved settings and early commands

- [ ] Run `!sounds off` and `!keys off`. Wait for saving, then reconnect.
- [ ] Immediately run `!hud off`. If told settings are loading, retry once loading completes.
- [ ] Run `!settings`: sounds, keys and HUD must all be OFF. Reconnect again and confirm they remain OFF.
- [ ] Turn the HUD on and finish a run. Keys stay hidden and your save sound stays muted.

Pass: an early command either applies after settings have loaded or explicitly asks you to retry. It never resets unrelated saved options. A loading response does **not** queue that settings command.

## 2. Hot reload with connected players

- [ ] Give the two players different settings, including one with sounds/keys disabled. Wait for saves.
- [ ] While both remain connected, have the administrator hot-reload SurfTimer using the installed Swiftly management procedure. A map change or `!stmapreload` is not a plugin hot reload.
- [ ] Run `!settings` without reconnecting. Retry if loading is reported. Each player must regain their own saved options.
- [ ] Toggle only HUD, reload again, and confirm sounds/keys remain unchanged.
- [ ] Start a fresh timed run after reload. An interrupted pre-reload run must not resume or save a bogus time.

Pass: settings restore without a new Steam authorization event, players never receive each other's settings, and fresh timing works.

## 3. Start, restart and invalidation

- [ ] Enter start: READY. Leave start: timer begins once. If the map has overlapping start triggers, leaving only one must not start early.
- [ ] Re-enter start mid-run: previous progress clears. Leave again and complete a normal run.
- [ ] Finish once and check `!pb`: exactly one new completion. Remaining inside or touching end again must not add another.
- [ ] During separate runs, test `!r`, death, switching to spectator, `!noclip`, and `!tele` to a saved practice location. None may turn the interrupted run into a ranked finish.
- [ ] Change map, then immediately use `!r`: restart must use the new map's start.

Pass: no duplicate, stale, zero-time or practice-assisted record is saved. Complete a normal run afterward to confirm recovery.

## 4. Independent stages

- [ ] Record `!stagepb 1` and `!stagetop 1`, then complete `!stageattempt 1`. Stage 1 starts on leaving the main start.
- [ ] Test a later stage with `!stageattempt <stage>` and the final stage. Later stages start on entry; the final stage ends at map end.
- [ ] On an uncertified map, the finish explicitly says unranked practice and changes no stage PB/completion count.
- [ ] Death, spectator change or an unexpected stage boundary cancels the attempt.

Do not enable `IndependentStageRecordsCertified` merely to make this test rank. Certification requires verification of equivalent starting geometry/velocity against full-run splits. Maps with checkpoints remain unranked for independent attempts. For an already-certified checkpoint-free test map, verify exactly one stage completion is saved and other routes are unchanged.

## 5. Replays and spectators

- [ ] Player A uses `!replay 1`; player B selects another available replay.
- [ ] A uses `!replay pause`, `!replay seek 5`, `!replay speed 0.5`, and `!replay resume`. B's playback must remain independent. Choose a replay longer than five seconds.
- [ ] `!replay stop` restores ordinary play; no replay movement becomes a ranked run.
- [ ] Spectate a real player on main, bonus and stage attempts. HUD time/route/PB must describe the observed player.
- [ ] If a test leaderboard has a known missing capture, requesting that rank must report no replay, not substitute another player. If it has ties, use the displayed competition ranks. Skip this fixture-dependent check if no suitable test data exists; it is covered by the database suite.

Pass: correct player/rank/route, independent controls, and no stale replay after map change. Unsupported native-only controls should report their limitation rather than affect another viewer.

## 6. Missing baseline across two test servers

Requires two instances sharing an isolated test database, unique server IDs and the same configured catalog authority. Choose a map with **no existing ruleset baseline**; do not delete a live baseline. Both instances must report the same current fingerprint in `!stadmin`.

- [ ] Finish on the non-authority server first. Expect a safely queued, awaiting-approval message.
- [ ] `!stadmin` shows pending evidence, no new quarantine entry, and no database-failure count caused solely by waiting for approval.
- [ ] Finish the same map on the authority server to establish its baseline.
- [ ] Allow the next recovery pass, normally 15–30 seconds for a small healthy queue. The earlier player receives saved feedback without rerunning or moving files manually.
- [ ] Check `!pb`: the earlier run is recorded exactly once. Repeat for a bonus and an already-certified stage route if available.

Pass: matching early runs recover automatically. A genuinely different fingerprint must remain rejected/quarantined once a baseline exists. If testing that mismatch, change physics only on the disposable test instance while idle, complete the test, then restore settings; never approve altered evidence merely to clear quarantine.

## 7. Database outage and recovery

Use a map whose baseline is already approved. Have the administrator interrupt only the test database connection.

- [ ] Complete two runs, with the second faster. Expect safely queued messages, not successful database-save claims.
- [ ] Have the administrator hot-reload the plugin while the database remains unavailable. Settings edits should report loading instead of overwriting saved settings.
- [ ] Restore connectivity. Pending runs must recover automatically; after prior repeated failures, backoff can take up to about five minutes plus the next recovery tick.
- [ ] `!pb` gains exactly two completions and retains the faster PB. `!stadmin` pending count returns to its starting value.
- [ ] Retry `!settings` after connectivity returns and confirm previously saved preferences are restored. A failed settings load may require a retry after the current request finishes.

Pass: pending evidence survives reload, no duplicate completions appear, and settings are preserved. Ask the administrator to confirm recorded achievement times reflect the finishes rather than the recovery time; PB-history timestamps are not fully inspectable from chat.

## Test record

For each numbered test, report PASS, FAIL or NOT RUN. For a failure include map, server, route, commands/actions, expected versus actual result, approximate timestamp, and relevant chat/server log lines. Keep credentials out of shared logs. Include `!stadmin` before/after for queue or ruleset failures.
