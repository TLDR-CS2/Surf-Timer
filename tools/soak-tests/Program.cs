using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MySqlConnector;

var playerCount=args.Length>0?int.Parse(args[0]):32;
var durationSeconds=args.Length>1?int.Parse(args[1]):60;
if(playerCount is <1 or >64) throw new ArgumentOutOfRangeException(nameof(playerCount),"Players must be 1-64.");
if(durationSeconds is <10 or >3600) throw new ArgumentOutOfRangeException(nameof(durationSeconds),"Duration must be 10-3600 seconds.");

var workspace=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..",".."));
var configPath=Path.Combine(workspace,"tools","local-server","database.local.jsonc");
using var document=JsonDocument.Parse(File.ReadAllText(configPath),new JsonDocumentOptions{CommentHandling=JsonCommentHandling.Skip,AllowTrailingCommas=true});
var root=document.RootElement; var connectionName=root.GetProperty("default_connection").GetString()!;
var value=root.GetProperty("connections").GetProperty(connectionName);
var connectionString=new MySqlConnectionStringBuilder
{
    Server=value.GetProperty("host").GetString(),Port=value.GetProperty("port").GetUInt32(),
    Database=value.GetProperty("database").GetString(),UserID=value.GetProperty("user").GetString(),
    Password=value.GetProperty("pass").GetString(),CharacterSet="utf8mb4",MaximumPoolSize=80
}.ConnectionString;

var prefix="st_soak_"+Guid.NewGuid().ToString("N")[..8];
var playersTable=prefix+"_players"; var recordsTable=prefix+"_records"; var historyTable=prefix+"_history";
await using var admin=new MySqlConnection(connectionString); await admin.OpenAsync();
var writeLatencies=new ConcurrentBag<double>(); var queryLatencies=new ConcurrentBag<double>();
var failures=new ConcurrentQueue<Exception>(); var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));
try
{
    await ExecuteAsync(admin,$"""
        CREATE TABLE `{playersTable}`(steam_id BIGINT UNSIGNED PRIMARY KEY,name VARCHAR(64) NOT NULL,last_seen DATETIME(6) NOT NULL) ENGINE=InnoDB;
        CREATE TABLE `{recordsTable}`(player_steam_id BIGINT UNSIGNED PRIMARY KEY,best_time_us BIGINT UNSIGNED NOT NULL,completions BIGINT UNSIGNED NOT NULL,pb_updated_at DATETIME(6) NOT NULL,KEY ix_top(best_time_us,pb_updated_at,player_steam_id)) ENGINE=InnoDB;
        CREATE TABLE `{historyTable}`(id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,player_steam_id BIGINT UNSIGNED NOT NULL,time_us BIGINT UNSIGNED NOT NULL,created_at DATETIME(6) NOT NULL,KEY ix_recent(player_steam_id,created_at)) ENGINE=InnoDB;
        """);
    await using(var seed=await admin.BeginTransactionAsync())
    {
        for(var i=0;i<playerCount;i++)
        {
            var steam=98_000_000_000_000_000UL+(ulong)i;
            await using var command=new MySqlCommand($"INSERT INTO `{playersTable}` VALUES(@steam,@name,UTC_TIMESTAMP(6))",admin,seed);
            command.Parameters.AddWithValue("@steam",steam); command.Parameters.AddWithValue("@name",$"Soak Surfer {i+1}"); await command.ExecuteNonQueryAsync();
        }
        await seed.CommitAsync();
    }
    Console.WriteLine($"SurfTimer soak: players={playerCount}, duration={durationSeconds}s, target=64Hz");
    Console.WriteLine($"Isolated tables: {prefix}_*; production st_* tables are untouched.");

    var dbWorkers=Enumerable.Range(0,playerCount).Select(index=>DatabaseWorkerAsync(index,cancellation.Token)).ToArray();
    var queryWorker=QueryWorkerAsync(cancellation.Token);
    var sessions=Enumerable.Range(0,playerCount).Select(i=>new VirtualSession(i)).ToArray();
    var tickLatencies=new List<double>(durationSeconds*64); var frames=new List<VirtualFrame>[playerCount];
    for(var i=0;i<playerCount;i++) frames[i]=new List<VirtualFrame>(durationSeconds*64);
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var initialMemory=GC.GetTotalMemory(true); var process=Process.GetCurrentProcess(); var initialWorkingSet=process.WorkingSet64;
    var cpuStart=process.TotalProcessorTime; var started=Stopwatch.StartNew(); var nextTick=TimeSpan.Zero; var tick=0;
    while(!cancellation.IsCancellationRequested)
    {
        var timer=Stopwatch.StartNew();
        for(var i=0;i<sessions.Length;i++)
        {
            ref var session=ref sessions[i]; session.Advance(tick);
            frames[i].Add(new(tick*15_625L,session.X,session.Y,session.Speed,session.Buttons));
            _=BuildHud(session,viewerKeys:true,spectators:(i%4)+1);
        }
        tickLatencies.Add(timer.Elapsed.TotalMilliseconds); tick++; nextTick+=TimeSpan.FromTicks(TimeSpan.TicksPerSecond/64);
        var remaining=nextTick-started.Elapsed;
        if(remaining>TimeSpan.Zero) await Task.Delay(remaining);
    }
    await Task.WhenAll(dbWorkers.Append(queryWorker));
    process.Refresh(); var elapsed=started.Elapsed; var cpu=process.TotalProcessorTime-cpuStart;
    var finalMemory=GC.GetTotalMemory(false); var workingSetGrowth=process.WorkingSet64-initialWorkingSet;
    var ticks=Stats(tickLatencies); var writes=Stats(writeLatencies); var queries=Stats(queryLatencies);
    Console.WriteLine($"Ticks: {tick:N0} ({tick/elapsed.TotalSeconds:F2}Hz), avg={ticks.Average:F3}ms p95={ticks.P95:F3}ms p99={ticks.P99:F3}ms max={ticks.Maximum:F3}ms");
    Console.WriteLine($"DB writes: {writeLatencies.Count:N0}, avg={writes.Average:F2}ms p95={writes.P95:F2}ms p99={writes.P99:F2}ms max={writes.Maximum:F2}ms");
    Console.WriteLine($"Top/profile queries: {queryLatencies.Count:N0}, avg={queries.Average:F2}ms p95={queries.P95:F2}ms p99={queries.P99:F2}ms max={queries.Maximum:F2}ms");
    Console.WriteLine($"Replay frames retained: {frames.Sum(x=>x.Count):N0}; managed growth={(finalMemory-initialMemory)/1048576.0:F1}MiB; working-set growth={workingSetGrowth/1048576.0:F1}MiB; CPU={(cpu.TotalMilliseconds/elapsed.TotalMilliseconds)*100:F1}% of one core");
    if(failures.TryPeek(out var failure)) throw new AggregateException($"{failures.Count} background operation(s) failed.",failures);
    var problems=new List<string>();
    if(tick/elapsed.TotalSeconds<63) problems.Add("tick throughput below 63Hz");
    if(ticks.P95>5) problems.Add("HUD/replay tick p95 above 5ms");
    if(writes.P95>100) problems.Add("DB write p95 above 100ms");
    if(queries.P95>50) problems.Add("DB query p95 above 50ms");
    if(workingSetGrowth>256L*1024*1024) problems.Add("working-set growth above 256MiB");
    if(problems.Count>0) throw new InvalidOperationException("Soak thresholds failed: "+string.Join("; ",problems));
    Console.WriteLine("SOAK PASSED");

    async Task DatabaseWorkerAsync(int index,CancellationToken token)
    {
        var steam=98_000_000_000_000_000UL+(ulong)index; var completion=0;
        while(!token.IsCancellationRequested)
        {
            try
            {
                var timer=Stopwatch.StartNew(); await using var connection=new MySqlConnection(connectionString); await connection.OpenAsync(token);
                var time=35_000_000L+index*250_000L-Math.Min(completion,100)*1_000L;
                await using var transaction=await connection.BeginTransactionAsync(token);
                await using(var record=new MySqlCommand($"INSERT INTO `{recordsTable}` VALUES(@steam,@time,1,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE completions=completions+1,best_time_us=LEAST(best_time_us,VALUES(best_time_us)),pb_updated_at=IF(VALUES(best_time_us)<best_time_us,UTC_TIMESTAMP(6),pb_updated_at)",connection,transaction))
                {record.Parameters.AddWithValue("@steam",steam);record.Parameters.AddWithValue("@time",time);await record.ExecuteNonQueryAsync(token);}
                await using(var history=new MySqlCommand($"INSERT INTO `{historyTable}`(player_steam_id,time_us,created_at) VALUES(@steam,@time,UTC_TIMESTAMP(6))",connection,transaction))
                {history.Parameters.AddWithValue("@steam",steam);history.Parameters.AddWithValue("@time",time);await history.ExecuteNonQueryAsync(token);}
                await transaction.CommitAsync(token); writeLatencies.Add(timer.Elapsed.TotalMilliseconds); completion++;
                await Task.Delay(TimeSpan.FromMilliseconds(750+index%8*25),token);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
            catch(Exception e){failures.Enqueue(e);break;}
        }
    }
    async Task QueryWorkerAsync(CancellationToken token)
    {
        while(!token.IsCancellationRequested)
        {
            try
            {
                var timer=Stopwatch.StartNew(); await using var connection=new MySqlConnection(connectionString); await connection.OpenAsync(token);
                await using var command=new MySqlCommand($"SELECT r.player_steam_id,p.name,r.best_time_us,r.completions FROM `{recordsTable}` r JOIN `{playersTable}` p ON p.steam_id=r.player_steam_id ORDER BY r.best_time_us,r.pb_updated_at,r.player_steam_id LIMIT 10",connection);
                await using var reader=await command.ExecuteReaderAsync(token); while(await reader.ReadAsync(token)){} queryLatencies.Add(timer.Elapsed.TotalMilliseconds);
                await Task.Delay(100,token);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
            catch(Exception e){failures.Enqueue(e);break;}
        }
    }
}
finally
{
    await ExecuteAsync(admin,$"DROP TABLE IF EXISTS `{historyTable}`,`{recordsTable}`,`{playersTable}`");
    Console.WriteLine("Disposable soak tables removed.");
}

static string BuildHud(VirtualSession s,bool viewerKeys,int spectators)
{
    var text=new StringBuilder(256).Append("SPECTATING Soak Surfer ").Append(s.Id+1).Append('|').Append(s.ElapsedUs).Append('|').Append(s.Speed).Append(" u/s|Stage ").Append(s.Stage).Append("|Running|PB 42000000|Rank #").Append(s.Id+1).Append("|Spectators ").Append(spectators);
    if(viewerKeys) text.Append('|').Append((s.Buttons&8)!=0?'W':'_').Append((s.Buttons&512)!=0?'A':'_').Append((s.Buttons&1024)!=0?'D':'_');
    return text.ToString();
}
static async Task ExecuteAsync(MySqlConnection connection,string sql){await using var command=new MySqlCommand(sql,connection);await command.ExecuteNonQueryAsync();}
static (double Average,double P95,double P99,double Maximum) Stats(IEnumerable<double> source)
{
    var values=source.Order().ToArray(); if(values.Length==0)return(0,0,0,0);
    return(values.Average(),values[(int)Math.Ceiling(values.Length*.95)-1],values[(int)Math.Ceiling(values.Length*.99)-1],values[^1]);
}
struct VirtualSession(int id)
{
    public int Id=id,Stage=1,Speed=250; public float X,Y; public ulong Buttons; public long ElapsedUs;
    public void Advance(int tick){ElapsedUs+=15_625;Speed=250+(tick*17+Id*31)%4750;X+=Speed/64f;Y=MathF.Sin((tick+Id)/20f)*512;Stage=1+(tick/640+Id)%8;Buttons=(tick+Id)%3==0?8UL:(tick+Id)%3==1?512UL:1024UL;}
}
readonly record struct VirtualFrame(long TimeUs,float X,float Y,int Speed,ulong Buttons);
