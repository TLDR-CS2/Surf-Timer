using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using SurfTimer.Configuration;
using SurfTimer.Replays;
using SurfTimer.Storage;
using SurfTimer.Timing;
using SwiftlyS2.Shared;

try
{
var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var suppliedConnection = Environment.GetEnvironmentVariable("SURFTIMER_TEST_DB_CONNECTION_STRING");
MySqlConnectionStringBuilder builder;
if (!string.IsNullOrWhiteSpace(suppliedConnection)) builder = new(suppliedConnection) { Database = "" };
else
{
    using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace, "tools/local-server/database.local.jsonc")),
        new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
    var root = config.RootElement;
    var db = root.GetProperty("connections").GetProperty(root.GetProperty("default_connection").GetString()!);
    builder = new MySqlConnectionStringBuilder {
        Server = db.GetProperty("host").GetString(), Port = db.GetProperty("port").GetUInt32(),
        UserID = db.GetProperty("user").GetString(), Password = db.GetProperty("pass").GetString(),
        ConnectionTimeout = 5, DefaultCommandTimeout = 10
    };
}if (args.Contains("--seed-api"))
{
    var fixtureConnection = new MySqlConnectionStringBuilder(suppliedConnection ?? throw new ArgumentException("Fixture mode needs explicit test connection."));
    if (!System.Text.RegularExpressions.Regex.IsMatch(fixtureConnection.Database, "^st_test_[a-f0-9]{32}$")) throw new ArgumentException("Fixture mode requires an isolated test schema.");
    await using var fixture = new MySqlConnection(fixtureConnection.ConnectionString);
    await fixture.OpenAsync();
    await using var seed = fixture.CreateCommand();
    seed.CommandText = """
        INSERT INTO st_players(steam_id,last_name,first_seen_at,last_seen_at,first_server_id,last_server_id,total_connections)
        VALUES(76561198000000002,'tie test',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6),'test','test',1),
              (76561198000000003,'unranked test',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6),'test','test',1)
        ON DUPLICATE KEY UPDATE last_name=VALUES(last_name);
        INSERT INTO st_records(map_id,player_steam_id,route_type,route_index,style,mode,best_time_us,completions,first_completed_at,last_completed_at,pb_updated_at,last_server_id)
        SELECT map_id,76561198000000002,route_type,route_index,style,mode,best_time_us,completions,first_completed_at,last_completed_at,pb_updated_at,last_server_id
        FROM st_records WHERE player_steam_id=76561198000000001
        ON DUPLICATE KEY UPDATE best_time_us=VALUES(best_time_us);
        INSERT INTO st_stage_records(map_id,player_steam_id,stage,best_time_us,completions,first_completed_at,last_completed_at,pb_updated_at,last_server_id)
        SELECT map_id,76561198000000002,stage,best_time_us,completions,first_completed_at,last_completed_at,pb_updated_at,last_server_id
        FROM st_stage_records WHERE player_steam_id=76561198000000001
        ON DUPLICATE KEY UPDATE best_time_us=VALUES(best_time_us);
        UPDATE st_maps SET stage_count=3,bonus_count=2 WHERE name='surf_test';
        """;
    await seed.ExecuteNonQueryAsync();
    Console.WriteLine("API fixtures ready: tie player 76561198000000002, unranked player 76561198000000003, empty stage3/bonus2 on surf_test.");
    return;
}var schema = "st_test_" + Guid.NewGuid().ToString("N");
var dataDirectory = Path.Combine(workspace, "build/storage-tests", schema);
Directory.CreateDirectory(dataDirectory);
await using var admin = new MySqlConnection(builder.ConnectionString);
await admin.OpenAsync();
await Execute("CREATE DATABASE `" + schema + "` CHARACTER SET utf8mb4");
builder.Database = schema;
await admin.ChangeDatabaseAsync(schema);
bool outage = false;
var databaseType = typeof(ISwiftlyCore).GetProperty("Database")!.PropertyType;
var database = DispatchProxy.Create(databaseType, typeof(TestProxy));
((TestProxy)database).InvokeHandler = (method, arguments) => method.Name switch {
    "GetConnection" => outage ? throw new IOException("Simulated database outage") : new MySqlConnection(builder.ConnectionString),
    "GetConnectionString" => builder.ConnectionString,
    _ => throw new NotSupportedException(method.Name)
};
var core = DispatchProxy.Create<ISwiftlyCore, TestProxy>();
((TestProxy)(object)core).InvokeHandler = (method, arguments) => method.Name switch {
    "get_Database" => database, "get_PluginPath" => workspace, "get_PluginDataDirectory" => dataDirectory,
    _ => throw new NotSupportedException(method.Name)
};
var options = new SurfTimerOptions { ServerId = "storage-test" };
var migrations = new MigrationRunner(core, options, NullLogger<MigrationRunner>.Instance);
using var repository = new RecordRepository(core, options, migrations, NullLogger<RecordRepository>.Instance);
try
{
    await migrations.ApplyAsync();
    // Simulate a server dying halfway through an ADD COLUMN migration, retaining its earlier column.
    await Execute("DELETE FROM st_schema_migrations WHERE version=3; ALTER TABLE st_maps DROP COLUMN enabled");
    await migrations.ApplyAsync();
    Check(await Scalar("SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='st_maps' AND COLUMN_NAME IN ('tier','enabled')") == 2, "Partial migration did not recover.");
    var replay = new ReplayCapture(64, [new ReplayFrame(0, 1, 2, 3, 0, 0, 0, 1, 0, 0, 0), new ReplayFrame(15625, 2, 2, 3, 0, 0, 0, 1, 0, 0, 0)], RecordedDurationMicroseconds: 1000000);
    var telemetry = new RunTelemetry(1, 1, 0, 1, 0, "none");
    var run = new CompletedRun(76561198000000001, "test", "surf_test", null, 1, 1000000, [500000], [1000000], options.ServerId, replay, telemetry) { RulesetFingerprint = "test-ruleset" };
    var first = await repository.SaveRunAsync(run);
    var duplicate = await repository.SaveRunAsync(run);
    Check(first.IsPersonalBest && duplicate.IsPersonalBest, "Receipt did not return original result.");
    Check(await Scalar("SELECT completions FROM st_records WHERE route_type='main'") == 1, "Duplicate receipt incremented completion count.");
    Check(await Scalar("SELECT COUNT(*) FROM st_run_receipts WHERE ruleset_fingerprint='test-ruleset' AND map_name='surf_test'") == 1, "Receipt provenance lost.");
    Check(await Scalar("SELECT COUNT(*) FROM st_replays") == 1 && await Scalar("SELECT COUNT(*) FROM st_stage_replays") == 1, "Replay not stored.");
    await repository.SaveRunAsync(run with { RunId = Guid.NewGuid(), TimeMicroseconds = 900000, StageTimes = [900000], Replay = null });
    Check(await Scalar("SELECT COUNT(*) FROM st_replays") == 0 && await Scalar("SELECT COUNT(*) FROM st_stage_replays") == 0, "Stale replay retained on faster PB without capture.");
    var bonus = new CompletedBonusRun(run.SteamId, run.PlayerName, run.MapName, null, 1, 1000000, options.ServerId, replay, telemetry) { RulesetFingerprint = run.RulesetFingerprint };
    await repository.SaveBonusAsync(bonus);
    await repository.SaveBonusAsync(bonus);
    Check(await Scalar("SELECT completions FROM st_records WHERE route_type='bonus'") == 1, "Bonus duplicate completion.");
    await repository.SaveBonusAsync(bonus with { RunId = Guid.NewGuid(), TimeMicroseconds = 900000, Replay = null });
    Check(await Scalar("SELECT COUNT(*) FROM st_replays") == 0, "Bonus stale replay retained.");
    var stage = new CompletedStageRun(run.SteamId, run.PlayerName, run.MapName, null, 2, 500000, options.ServerId, replay, telemetry) { RulesetFingerprint = run.RulesetFingerprint, CompetitiveStartCertified = true };
    await repository.SaveStageAsync(stage);
    await repository.SaveStageAsync(stage);
    Check(await Scalar("SELECT completions FROM st_stage_records WHERE stage=2") == 1, "Stage duplicate completion.");
    await repository.SaveStageAsync(stage with { RunId = Guid.NewGuid(), TimeMicroseconds = 400000, Replay = null });
    Check(await Scalar("SELECT COUNT(*) FROM st_stage_replays") == 0, "Stage stale replay retained.");
    // Archive a PB with replay evidence, remove it from active standings, and restore every dependent row.
    await repository.SaveRunAsync(run with { RunId = Guid.NewGuid(), TimeMicroseconds = 800000 });
    var archive = (await repository.InvalidateRecordAsync(run.SteamId, run.MapName, "main", 0, 123, "integration test"))!.Value;
    Check(await Scalar("SELECT COUNT(*) FROM st_records WHERE route_type='main'") == 0, "Invalid record stayed ranked.");
    Check(await repository.RestoreRecordAsync(archive, 123), "Restore failed.");
    Check(await Scalar("SELECT COUNT(*) FROM st_replays") == 1 && await Scalar("SELECT COUNT(*) FROM st_record_splits") == 1, "Restored evidence missing.");
    archive = (await repository.InvalidateRecordAsync(run.SteamId, run.MapName, "main", 0, 123, "rollback test"))!.Value;
    await repository.SaveRunAsync(run with { RunId = Guid.NewGuid(), TimeMicroseconds = 700000 });
    try { await repository.RestoreRecordAsync(archive, 123); throw new Exception("Restore replaced newer PB."); }
    catch (MySqlException e) when (e.Number == 1062) { }
    Check(await Scalar("SELECT best_time_us FROM st_records WHERE route_type='main'") == 700000, "Restore changed newer PB.");
    Check(await Scalar($"SELECT COUNT(*) FROM st_record_archives WHERE archive_id='{archive:N}' AND restored_at IS NULL") == 1, "Failed restore discarded archive.");
    foreach (var route in new[] { "bonus", "stage" })
    {
        var routeArchive = (await repository.InvalidateRecordAsync(run.SteamId, run.MapName, route, route == "stage" ? 2 : 1, 123, "route moderation"))!.Value;
        Check(await repository.RestoreRecordAsync(routeArchive, 123), route + " restore failed.");
    }
    await repository.TrackMapMetadataAsync(run.MapName, null, 1, 2, 1, 3, true);
    var otherOptions = new SurfTimerOptions { ServerId = "other-server" };
    var otherCore = DispatchProxy.Create<ISwiftlyCore, TestProxy>();
    ((TestProxy)(object)otherCore).InvokeHandler = (method, arguments) => method.Name == "get_PluginDataDirectory"
        ? dataDirectory + "-other" : ((TestProxy)(object)core).InvokeHandler(method, arguments);
    using var other = new RecordRepository(otherCore, otherOptions, new MigrationRunner(otherCore, otherOptions, NullLogger<MigrationRunner>.Instance), NullLogger<RecordRepository>.Instance);
    await other.TrackMapMetadataAsync(run.MapName, null, 0, 0, 0, 7, false);
    Check(await Scalar("SELECT tier FROM st_maps WHERE name='surf_test'") == 3, "Non-authority overwrote map metadata.");
    outage = true;
    var queuedRun = run with { RunId = Guid.NewGuid(), TimeMicroseconds = 600000 };
    try { await repository.SaveRunAsync(queuedRun); throw new Exception("Outage save was not pending."); }
    catch (PendingRunSaveException) { }
    Check(Directory.GetFiles(Path.Combine(dataDirectory, "pending-runs"), "*" + queuedRun.RunId.ToString("N") + ".json").Length == 1, "Outage run not durable.");
    outage = false;
    var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    repository.PendingRunRecovered += (_, _) => recovered.TrySetResult();
    await recovered.Task.WaitAsync(TimeSpan.FromSeconds(35));
    Check(await Scalar("SELECT best_time_us FROM st_records WHERE route_type='main'") == 600000, "Queued recovery did not save PB.");
    outage = true;
    using (var stopping = new RecordRepository(core, options, migrations, NullLogger<RecordRepository>.Instance))
    {
        var stopRun = run with { RunId = Guid.NewGuid() };
        var accepted = stopping.SaveRunAsync(stopRun);
        stopping.Dispose();
        try { await accepted; throw new Exception("Shutdown write unexpectedly reached unavailable DB."); }
        catch (PendingRunSaveException) { }
        var durableFile = Directory.GetFiles(Path.Combine(dataDirectory, "pending-runs"), "*" + stopRun.RunId.ToString("N") + ".json").Single();
        Check(File.Exists(durableFile), "Immediate shutdown canceled accepted disk enqueue.");
        File.Delete(durableFile);
    }
    outage = false;
    // A faster record without a capture must not shift replay selection on any route.
    var achieved = DateTimeOffset.UtcNow.AddDays(-2);
    var replayRun = run with { RunId = Guid.NewGuid(), MapName = "surf_replay_review", FinishedAtUtc = achieved, TimeMicroseconds = 1000000, Replay = null, StageTimes = [1000000] };
    await repository.SaveRunAsync(replayRun);
    var second = replayRun with { RunId = Guid.NewGuid(), SteamId = run.SteamId + 1, PlayerName = "second", TimeMicroseconds = 2000000, StageTimes = [2000000], Replay = replay };
    await repository.SaveRunAsync(second);
    Check(await repository.GetReplayAsync(replayRun.MapName, 1) is null, "Missing WR replay substituted second place.");
    Check((await repository.GetReplayAsync(replayRun.MapName, 2))?.PlayerName == "second", "Second place replay unavailable.");
    Check(await repository.GetStageReplayAsync(replayRun.MapName, 1, 1) is null, "Missing stage WR replay substituted second place.");
    Check((await repository.GetStageReplayAsync(replayRun.MapName, 1, 2))?.PlayerName == "second", "Second stage replay unavailable.");
    var replayBonus = bonus with { RunId = Guid.NewGuid(), MapName = replayRun.MapName, Replay = null, FinishedAtUtc = achieved };
    await repository.SaveBonusAsync(replayBonus);
    await repository.SaveBonusAsync(replayBonus with { RunId = Guid.NewGuid(), SteamId = second.SteamId, PlayerName = second.PlayerName, TimeMicroseconds = 2000000, Replay = replay });
    Check(await repository.GetBonusReplayAsync(replayRun.MapName, 1, 1) is null, "Missing bonus WR replay substituted second place.");
    Check((await repository.GetBonusReplayAsync(replayRun.MapName, 1, 2))?.PlayerName == "second", "Second bonus replay unavailable.");
    await repository.SaveRunAsync(second with { RunId = Guid.NewGuid(), SteamId = run.SteamId + 2, PlayerName = "tie", TimeMicroseconds = 1000000, FinishedAtUtc = achieved.AddSeconds(1) });
    Check(await repository.GetReplayAsync(replayRun.MapName, 1) is null, "Tie selection skipped earliest PB lacking replay.");
    Check(await repository.GetReplayAsync(replayRun.MapName, 2) is null, "Nonexistent competition rank returned a replay.");
    Check((await repository.GetReplayAsync(replayRun.MapName, 3))?.PlayerName == "second", "Competition rank after tie is incorrect.");
    Check(await Scalar("SELECT COUNT(*) FROM st_pb_history WHERE achieved_at < UTC_TIMESTAMP() - INTERVAL 1 DAY") >= 3, "Captured finish timestamps were replaced by save time.");
    Check(await repository.GetReplayAdminDetailsAsync(replayRun.MapName, 1) is null, "Admin replay inspection substituted a tied capture.");
    Check(await repository.GetStageReplayAdminDetailsAsync(replayRun.MapName, 1, 1) is null, "Admin stage inspection shifted over missing capture.");
    Check(await repository.GetBonusReplayAdminDetailsAsync(replayRun.MapName, 1, 1) is null, "Admin bonus inspection shifted over missing capture.");
    Check(await repository.DeleteReplayAsync(replayRun.MapName, 1) is null, "Admin main deletion removed another player's capture.");
    Check(await repository.DeleteStageReplayAsync(replayRun.MapName, 1, 1) is null, "Admin stage deletion removed another player's capture.");
    Check(await repository.DeleteBonusReplayAsync(replayRun.MapName, 1, 1) is null, "Admin bonus deletion removed another player's capture.");
    Check((await repository.DeleteReplayAsync(replayRun.MapName, 3))?.PlayerName == "second", "Admin deletion did not use competition rank.");
    Check((await repository.DeleteBonusReplayAsync(replayRun.MapName, 1, 2))?.PlayerName == "second", "Admin bonus deletion selected wrong player.");
    Check((await repository.DeleteStageReplayAsync(replayRun.MapName, 1, 2))?.PlayerName == "second", "Admin stage deletion selected wrong player.");
    var beforeRejected = await Scalar("SELECT SUM(completions) FROM st_records");
    try { await repository.SaveRunAsync(replayRun with { RunId = Guid.NewGuid(), RulesetFingerprint = "different-physics", TimeMicroseconds = 1 }); throw new Exception("Mismatched ruleset accepted."); }
    catch (RunQuarantinedException) { }
    Check(await Scalar("SELECT SUM(completions) FROM st_records") == beforeRejected, "Rejected ruleset changed records.");
    Check(Directory.GetFiles(Path.Combine(dataDirectory, "pending-runs", "quarantine"), "*.json").Length == 1, "Rejected evidence was not retained.");
    await other.SaveRunAsync(replayRun with { RunId = Guid.NewGuid(), ServerId = otherOptions.ServerId });
    Check(repository.PendingQueueStatus().Contains("quarantine=1"), "Queue diagnostics omit quarantined evidence.");
    try { await repository.SaveStageAsync(stage with { RunId = Guid.NewGuid(), CompetitiveStartCertified = false, TimeMicroseconds = 1 }); throw new Exception("Uncertified stage attempt accepted."); }
    catch (RunQuarantinedException) { }
    Check(await Scalar("SELECT best_time_us FROM st_stage_records WHERE stage=2") == 400000, "Uncertified stage attempt changed competitive records.");
    var awaiting = replayRun with { RunId = Guid.NewGuid(), MapName = "surf_unapproved", ServerId = otherOptions.ServerId };
    var awaitingBonus = bonus with { RunId = Guid.NewGuid(), MapName = awaiting.MapName, ServerId = otherOptions.ServerId };
    var awaitingStage = stage with { RunId = Guid.NewGuid(), MapName = awaiting.MapName, ServerId = otherOptions.ServerId };
    var failuresBeforeApproval = other.ConsecutiveFailures;
    foreach (var save in new Func<Task<SaveRecordResult>>[] { () => other.SaveRunAsync(awaiting), () => other.SaveBonusAsync(awaitingBonus), () => other.SaveStageAsync(awaitingStage) })
    {
        try { await save(); throw new Exception("Non-authority established a new baseline."); }
        catch (PendingRunSaveException exception) when (exception.InnerException is RulesetAwaitingApprovalException) { }
    }
    Check(other.ConsecutiveFailures == failuresBeforeApproval, "Awaiting map approval was counted as a database outage.");
    Check(!Directory.Exists(Path.Combine(dataDirectory + "-other", "pending-runs", "quarantine")), "Awaiting approval was quarantined.");
    await other.SaveRunAsync(replayRun with { RunId = Guid.NewGuid(), ServerId = otherOptions.ServerId });
    var approvalRecovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var approvalRecoveries = 0;
    other.PendingRunRecovered += (_, map) => { if (map == awaiting.MapName && Interlocked.Increment(ref approvalRecoveries) == 3) approvalRecovered.TrySetResult(); };
    await repository.SaveRunAsync(awaiting with { RunId = Guid.NewGuid(), SteamId = run.SteamId + 11, PlayerName = "authority", ServerId = options.ServerId });
    await approvalRecovered.Task.WaitAsync(TimeSpan.FromSeconds(35));
    Check(await Scalar("SELECT COUNT(*) FROM st_run_receipts WHERE run_id IN ('" + awaiting.RunId.ToString("N") + "','" + awaitingBonus.RunId.ToString("N") + "','" + awaitingStage.RunId.ToString("N") + "')") == 3, "Approval did not automatically save all three routes.");
    await PlayerInitializationChecks.RunAsync(core, repository, migrations, options, run.SteamId);
    var preferences = new PlayerPreferenceRepository(core, options, migrations, NullLogger<PlayerPreferenceRepository>.Instance);
    var toggles = Enumerable.Range(0, 20).Select(i => preferences.SaveAsync(run.SteamId, new SurfTimer.Players.PlayerPreferences(HudEnabled: i % 2 == 0))).ToArray();
    await Task.WhenAll(toggles);
    Check(!(await preferences.LoadAsync(run.SteamId)).HudEnabled, "Concurrent preference saves lost final toggle.");
    preferences.Stop();
    // Two outage finishes must replay chronologically and retain both PB improvements.
    outage = true;
    var olderQueued = replayRun with { RunId = Guid.NewGuid(), MapName = "surf_queue_order", TimeMicroseconds = 6000000, FinishedAtUtc = achieved };
    var newerQueued = olderQueued with { RunId = Guid.NewGuid(), TimeMicroseconds = 5000000, FinishedAtUtc = achieved.AddSeconds(30) };
    var pendingTasks = new[] { repository.SaveRunAsync(olderQueued), repository.SaveRunAsync(newerQueued) };
    foreach (var task in pendingTasks) { try { await task; throw new Exception("Outage unexpectedly saved."); } catch (PendingRunSaveException) { } }
    await File.WriteAllTextAsync(Path.Combine(dataDirectory, "pending-runs", "0000000000000000001-corrupt.json"), "{broken");
    var orderedRecovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var recoveredCount = 0;
    repository.PendingRunRecovered += (_, map) => { if (map == olderQueued.MapName && Interlocked.Increment(ref recoveredCount) == 2) orderedRecovery.TrySetResult(); };
    outage = false;
    await orderedRecovery.Task.WaitAsync(TimeSpan.FromSeconds(50));
    Check(Directory.GetFiles(Path.Combine(dataDirectory, "pending-runs", "quarantine"), "*.json").Length == 3, "Malformed queue entry was not quarantined before healthy recovery.");
    Check(await Scalar("SELECT COUNT(*) FROM st_pb_history h JOIN st_maps m ON m.id=h.map_id WHERE m.name='surf_queue_order'") == 2, "Recovery omitted intermediate PB history.");
    Check(await Scalar("SELECT COUNT(*) FROM st_records r JOIN st_maps m ON m.id=r.map_id WHERE m.name='surf_queue_order' AND best_time_us=5000000 AND first_completed_at < UTC_TIMESTAMP() - INTERVAL 1 DAY AND last_completed_at < UTC_TIMESTAMP() - INTERVAL 1 DAY") == 1, "Recovery lost finish time or fastest record.");
    Console.WriteLine("PASS: replay gaps/ties, achievement timestamps, ruleset quarantine, ordered preferences, chronological outage recovery.");
    Console.WriteLine("PASS: partial migrations, idempotent main/bonus/stage saves, replay replacement, provenance, archive/restore rollback, authority, durable outage recovery and shutdown drain.");
}
finally
{
    repository.Dispose();
    if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^st_test_[a-f0-9]{32}$")) throw new InvalidOperationException("Unsafe test schema.");
    if (args.Contains("--keep-db"))
    {
        var runtimePath = Path.Combine(workspace, "build/storage-test-runtime.local.json");
        var runtime = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(runtimePath))!;
        runtime["ConnectionString"] = builder.ConnectionString; runtime["Schema"] = schema;
        await File.WriteAllTextAsync(runtimePath, runtime.ToJsonString());
        Console.WriteLine("Retained isolated schema: " + schema);
    }
    else await Execute("DROP DATABASE `" + schema + "`");
    // Retain local queue directory if a failed test left evidence there.
}
async Task Execute(string sql) { await using var command = admin.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
async Task<long> Scalar(string sql) { await using var command = admin.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(await command.ExecuteScalarAsync()); }
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
catch (Exception exception) { Console.Error.WriteLine(exception); Environment.ExitCode = 1; }

public class TestProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> InvokeHandler = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => InvokeHandler(targetMethod!, args);
}




