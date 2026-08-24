using System.Text.Json;
using System.Diagnostics;
using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using MySqlConnector;

var builder=WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["SURFTIMER_WEB_URL"]??"http://127.0.0.1:5080");
builder.Logging.ClearProviders();builder.Logging.AddJsonConsole();
var workspace=Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath,".."));
var localConfig=Path.Combine(workspace,"tools","local-server","database.local.jsonc");
var connectionString=Environment.GetEnvironmentVariable("SURFTIMER_DB_CONNECTION_STRING");
if(string.IsNullOrWhiteSpace(connectionString)&&!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SURFTIMER_DB_HOST")))
    connectionString=new MySqlConnectionStringBuilder{Server=Environment.GetEnvironmentVariable("SURFTIMER_DB_HOST"),Port=GetUInt("SURFTIMER_DB_PORT",3306),Database=RequireEnvironment("SURFTIMER_DB_NAME"),UserID=RequireEnvironment("SURFTIMER_DB_USER"),Password=RequireEnvironment("SURFTIMER_DB_PASSWORD"),CharacterSet="utf8mb4",MaximumPoolSize=32,ConnectionTimeout=10,DefaultCommandTimeout=10,SslMode=Enum.TryParse<MySqlSslMode>(Environment.GetEnvironmentVariable("SURFTIMER_DB_SSL_MODE"),true,out var ssl)?ssl:MySqlSslMode.Preferred}.ConnectionString;
if(string.IsNullOrWhiteSpace(connectionString))
{
    if(!File.Exists(localConfig))throw new InvalidOperationException("Configure SURFTIMER_DB_CONNECTION_STRING or SURFTIMER_DB_HOST/NAME/USER/PASSWORD. The local development database file was not found.");
    using var configDocument=JsonDocument.Parse(File.ReadAllText(localConfig),new JsonDocumentOptions{CommentHandling=JsonCommentHandling.Skip,AllowTrailingCommas=true});
    var configRoot=configDocument.RootElement;var connectionName=configRoot.GetProperty("default_connection").GetString()!;var db=configRoot.GetProperty("connections").GetProperty(connectionName);
    connectionString=new MySqlConnectionStringBuilder{Server=db.GetProperty("host").GetString(),Port=db.GetProperty("port").GetUInt32(),Database=db.GetProperty("database").GetString(),UserID=db.GetProperty("user").GetString(),Password=db.GetProperty("pass").GetString(),CharacterSet="utf8mb4",MaximumPoolSize=32,ConnectionTimeout=10,DefaultCommandTimeout=10,SslMode=MySqlSslMode.Disabled}.ConnectionString;
}
var recordCacheSeconds=GetInt("SURFTIMER_CACHE_RECORDS_SECONDS",10,1,300);var metadataCacheSeconds=GetInt("SURFTIMER_CACHE_METADATA_SECONDS",30,1,600);var rateLimit=GetInt("SURFTIMER_RATE_LIMIT_PER_MINUTE",120,10,10_000);var startedAt=DateTimeOffset.UtcNow;
const string PointsCte="""
    WITH map_rankings AS (
      SELECT r.player_steam_id,r.map_id,m.tier,RANK() OVER(PARTITION BY r.map_id ORDER BY r.best_time_us) rank_no,COUNT(*) OVER(PARTITION BY r.map_id) total_records
      FROM st_records r JOIN st_maps m ON m.id=r.map_id WHERE r.route_type='main' AND r.route_index=0 AND r.style=0 AND r.mode='surf' AND m.enabled=1
    ), main_scored AS (
      SELECT *,ROUND((25*POW(2,tier-1))*(1+10*CASE WHEN rank_no=1 THEN 1 WHEN rank_no<=10 THEN .85-.05*(rank_no-1) WHEN rank_no<=100 THEN 4/rank_no ELSE 0 END)) route_points FROM map_rankings
    ), portfolio_ranked AS (
      SELECT *,ROW_NUMBER() OVER(PARTITION BY player_steam_id,tier ORDER BY route_points DESC,map_id) portfolio_position FROM main_scored
    ), main_scores AS (
      SELECT player_steam_id,SUM(CASE WHEN portfolio_position<=20 THEN route_points ELSE 0 END) map_points,
        COUNT(*) completed_maps,SUM((rank_no-1)/total_records<.01) group1,SUM((rank_no-1)/total_records>=.01 AND (rank_no-1)/total_records<.05) group2,SUM((rank_no-1)/total_records>=.05 AND (rank_no-1)/total_records<.10) group3,SUM((rank_no-1)/total_records>=.10 AND (rank_no-1)/total_records<.25) group4,SUM((rank_no-1)/total_records>=.25 AND (rank_no-1)/total_records<.50) group5
      FROM portfolio_ranked GROUP BY player_steam_id
    ), stage_rankings AS (
      SELECT sr.player_steam_id,sr.map_id,m.tier,GREATEST(m.stage_count,1) stage_count,RANK() OVER(PARTITION BY sr.map_id,sr.stage ORDER BY sr.best_time_us) rank_no
      FROM st_stage_records sr JOIN st_maps m ON m.id=sr.map_id WHERE m.enabled=1
    ), stage_scores AS (
      SELECT player_steam_id,map_id,ROUND(SUM(((25*POW(2,tier-1))*10*.35/stage_count)*CASE WHEN rank_no=1 THEN 1 WHEN rank_no<=10 THEN .85-.05*(rank_no-1) WHEN rank_no<=100 THEN 4/rank_no ELSE 0 END)) stage_points
      FROM stage_rankings GROUP BY player_steam_id,map_id
    ), counted_stage_scores AS (
      SELECT ss.player_steam_id,SUM(ss.stage_points) stage_points FROM stage_scores ss JOIN portfolio_ranked p ON p.player_steam_id=ss.player_steam_id AND p.map_id=ss.map_id AND p.portfolio_position<=20 GROUP BY ss.player_steam_id
    ), bonus_scores AS (
      SELECT r.player_steam_id,COUNT(*)*10 bonus_points FROM st_records r JOIN st_maps m ON m.id=r.map_id
      WHERE r.route_type='bonus' AND r.style=0 AND r.mode='surf' AND m.enabled=1 GROUP BY r.player_steam_id
    ), score_players AS (
      SELECT player_steam_id FROM main_scores UNION SELECT player_steam_id FROM counted_stage_scores UNION SELECT player_steam_id FROM bonus_scores
    ), scores AS (
      SELECT p.player_steam_id,COALESCE(m.map_points,0)+COALESCE(st.stage_points,0)+COALESCE(b.bonus_points,0) points,COALESCE(m.completed_maps,0) completed_maps,
        COALESCE(m.group1,0) group1,COALESCE(m.group2,0) group2,COALESCE(m.group3,0) group3,COALESCE(m.group4,0) group4,COALESCE(m.group5,0) group5,
        COALESCE(m.map_points,0) map_points,COALESCE(st.stage_points,0) stage_points,COALESCE(b.bonus_points,0) bonus_points
      FROM score_players p LEFT JOIN main_scores m ON m.player_steam_id=p.player_steam_id LEFT JOIN counted_stage_scores st ON st.player_steam_id=p.player_steam_id LEFT JOIN bonus_scores b ON b.player_steam_id=p.player_steam_id
    ), ranked AS (SELECT s.*,RANK() OVER(ORDER BY s.points DESC) overall_rank,COUNT(*) OVER() ranked_players FROM scores s), overall AS (
      SELECT r.*,CASE WHEN completed_maps<5 THEN 'Provisional' WHEN overall_rank=1 AND completed_maps>=25 THEN 'God' WHEN (overall_rank-1)/ranked_players<.005 THEN 'Legend' WHEN (overall_rank-1)/ranked_players<.01 THEN 'Master' WHEN (overall_rank-1)/ranked_players<.05 THEN 'Elite' WHEN (overall_rank-1)/ranked_players<.10 THEN 'Expert' WHEN (overall_rank-1)/ranked_players<.25 THEN 'Veteran' WHEN (overall_rank-1)/ranked_players<.50 THEN 'Skilled' ELSE 'Surfer' END title FROM ranked r)
    """;
builder.Services.AddSingleton(new Database(connectionString));
builder.Services.AddMemoryCache(options=>options.SizeLimit=2_000);
builder.Services.AddRateLimiter(options=>
{
    options.RejectionStatusCode=StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter=PartitionedRateLimiter.Create<HttpContext,string>(context=>
        context.Request.Path.StartsWithSegments("/api")
            ? RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString()??"local",_=>new FixedWindowRateLimiterOptions
              {PermitLimit=rateLimit,Window=TimeSpan.FromMinutes(1),QueueLimit=0,AutoReplenishment=true})
            : RateLimitPartition.GetNoLimiter("static"));
    options.OnRejected=async(context,token)=>
    {
        context.HttpContext.Response.Headers.RetryAfter="60";
        await context.HttpContext.Response.WriteAsJsonAsync(new{error="Too many API requests. Try again shortly."},token);
    };
});
var app=builder.Build();
app.UseExceptionHandler(errorApp=>errorApp.Run(async context=>
{
    var error=context.Features.Get<IExceptionHandlerFeature>()?.Error;var requestId=Activity.Current?.Id??context.TraceIdentifier;
    context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SurfTimer.Web.Errors").LogError(error,"Unhandled request failure {RequestId} on {Method} {Path}",requestId,context.Request.Method,context.Request.Path);
    context.Response.StatusCode=StatusCodes.Status500InternalServerError;
    if(context.Request.Path.StartsWithSegments("/api"))await context.Response.WriteAsJsonAsync(new{error="An internal server error occurred.",requestId});
    else{context.Response.ContentType="text/plain";await context.Response.WriteAsync($"SurfTimer web request failed. Reference: {requestId}");}
}));
app.Use(async(context,next)=>
{
    var stopwatch=Stopwatch.StartNew();var logger=context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SurfTimer.Web.Requests");
    context.Response.Headers["X-Content-Type-Options"]="nosniff";context.Response.Headers["Referrer-Policy"]="no-referrer";
    await next();stopwatch.Stop();
    logger.LogInformation("HTTP {Method} {Path} returned {StatusCode} in {ElapsedMs:F2} ms",context.Request.Method,context.Request.Path,context.Response.StatusCode,stopwatch.Elapsed.TotalMilliseconds);
});
app.UseRateLimiter();app.UseDefaultFiles(); app.UseStaticFiles(new StaticFileOptions{OnPrepareResponse=ctx=>ctx.Context.Response.Headers.CacheControl="public,max-age=300"});

app.MapGet("/api/version",()=>Results.Ok(new{service="SurfTimer.Web",version="0.1.0",environment=app.Environment.EnvironmentName,startedAt,framework=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,cache=new{recordsSeconds=recordCacheSeconds,metadataSeconds=metadataCacheSeconds},rateLimit=new{requestsPerMinute=rateLimit}}));

app.MapGet("/api/health",async(Database database,CancellationToken token)=>
{
    await using var connection=await database.OpenAsync(token); await using var command=new MySqlCommand("SELECT COALESCE(MAX(version),0) FROM st_schema_migrations",connection);
    return Results.Ok(new{status="ok",schemaVersion=Convert.ToInt32(await command.ExecuteScalarAsync(token)),checkedAt=DateTimeOffset.UtcNow});
});

app.MapGet("/api/maps",async(Database database,IMemoryCache cache,CancellationToken token)=>
{
    var payload=await cache.GetOrCreateAsync<object>("maps",async entry=>
    {
    entry.AbsoluteExpirationRelativeToNow=TimeSpan.FromSeconds(metadataCacheSeconds);entry.Size=1;
    await using var connection=await database.OpenAsync(token);await using var command=new MySqlCommand("""
        SELECT m.name,m.tier,m.enabled,m.workshop_id,COUNT(r.id),COALESCE(SUM(r.completions),0),MIN(r.best_time_us)
        FROM st_maps m LEFT JOIN st_records r ON r.map_id=m.id AND r.route_type='main' AND r.route_index=0 AND r.style=0 AND r.mode='surf'
        WHERE m.enabled=1 AND m.name LIKE 'surf\\_%' GROUP BY m.id,m.name,m.tier,m.enabled,m.workshop_id ORDER BY m.tier,m.name
        """,connection);
    var maps=new List<object>(); await using var reader=await command.ExecuteReaderAsync(token);
    while(await reader.ReadAsync(token)) maps.Add(new{name=reader.GetString(0),tier=reader.GetInt32(1),enabled=reader.GetBoolean(2),workshopId=reader.IsDBNull(3)?null:reader.GetString(3),players=reader.GetInt32(4),completions=reader.GetInt64(5),worldRecordUs=reader.IsDBNull(6)?(long?)null:reader.GetInt64(6)});
    return maps;
    });return Results.Ok(payload);
});

app.MapGet("/api/maps/{map}/routes",async(string map,Database database,IMemoryCache cache,CancellationToken token)=>
{
    if(!await MapExistsAsync(map,database,cache,metadataCacheSeconds,token))return Results.NotFound(new{error="map not found"});
    var payload=await cache.GetOrCreateAsync<object>($"routes:{map.ToLowerInvariant()}",async entry=>
    {
    entry.AbsoluteExpirationRelativeToNow=TimeSpan.FromSeconds(metadataCacheSeconds);entry.Size=1;
    await using var connection=await database.OpenAsync(token);await using var command=new MySqlCommand("""
        SELECT route_type,route_index FROM (
          SELECT 'main' AS route_type,0 AS route_index
          UNION SELECT r.route_type,r.route_index FROM st_records r JOIN st_maps m ON m.id=r.map_id
            WHERE m.name=@map AND r.style=0 AND r.mode='surf'
          UNION SELECT 'stage',sr.stage FROM st_stage_records sr JOIN st_maps m ON m.id=sr.map_id WHERE m.name=@map
        ) routes ORDER BY FIELD(route_type,'main','bonus','stage'),route_index
        """,connection);command.Parameters.AddWithValue("@map",map);
    var rows=new List<object>();await using var reader=await command.ExecuteReaderAsync(token);
    while(await reader.ReadAsync(token))rows.Add(new{route=reader.GetString(0),index=reader.GetInt32(1)});
    return new{map,routes=rows};
    });return Results.Ok(payload);
});

app.MapGet("/api/maps/{map}/stats",async(string map,Database database,IMemoryCache cache,CancellationToken token)=>
{
    if(!await MapExistsAsync(map,database,cache,metadataCacheSeconds,token))return Results.NotFound(new{error="map not found"});
    var payload=await cache.GetOrCreateAsync<object>($"map-stats:{map.ToLowerInvariant()}",async entry=>
    {
        entry.AbsoluteExpirationRelativeToNow=TimeSpan.FromSeconds(metadataCacheSeconds);entry.Size=1;
        await using var connection=await database.OpenAsync(token);await using var command=new MySqlCommand("""
            SELECT r.player_steam_id,p.last_name,r.best_time_us,r.completions
            FROM st_records r JOIN st_maps m ON m.id=r.map_id JOIN st_players p ON p.steam_id=r.player_steam_id
            WHERE m.name=@map AND r.route_type='main' AND r.route_index=0 AND r.style=0 AND r.mode='surf'
            ORDER BY r.best_time_us,r.pb_updated_at,r.player_steam_id
            """,connection);command.Parameters.AddWithValue("@map",map);
        var rows=new List<(string Steam,string Name,long Time,long Completions)>();await using var reader=await command.ExecuteReaderAsync(token);
        while(await reader.ReadAsync(token))rows.Add((reader.GetUInt64(0).ToString(),reader.GetString(1),reader.GetInt64(2),reader.GetInt64(3)));
        long? average=rows.Count==0?null:(long)rows.Average(x=>x.Time);long? median=rows.Count==0?null:rows.Count%2==1?rows[rows.Count/2].Time:(rows[rows.Count/2-1].Time+rows[rows.Count/2].Time)/2;
        return new{map,uniqueSurfers=rows.Count,totalCompletions=rows.Sum(x=>x.Completions),averagePbUs=average,medianPbUs=median,worldRecord=rows.Count==0?null:new{steamId=rows[0].Steam,playerName=rows[0].Name,timeUs=rows[0].Time}};
    });return Results.Ok(payload);
});

app.MapGet("/api/activity",async(int? limit,Database database,IMemoryCache cache,CancellationToken token)=>
{
    var take=Math.Clamp(limit??20,1,50);var payload=await cache.GetOrCreateAsync<object>($"activity:{take}",async entry=>
    {
        entry.AbsoluteExpirationRelativeToNow=TimeSpan.FromSeconds(recordCacheSeconds);entry.Size=1;
        await using var connection=await database.OpenAsync(token);await using var command=new MySqlCommand("""
            SELECT activity.player_steam_id,p.last_name,m.name,activity.route_type,activity.route_index,
                   activity.previous_time_us,activity.new_time_us,activity.achieved_at,activity.is_world_record
            FROM (
              SELECT h.player_steam_id,h.map_id,h.route_type,h.route_index,h.previous_time_us,h.new_time_us,h.achieved_at,
                NOT EXISTS(SELECT 1 FROM st_records faster WHERE faster.map_id=h.map_id AND faster.route_type=h.route_type AND faster.route_index=h.route_index AND faster.style=0 AND faster.mode='surf' AND faster.best_time_us<h.new_time_us) is_world_record
              FROM st_pb_history h
              UNION ALL
              SELECT h.player_steam_id,h.map_id,'stage',h.stage,h.previous_time_us,h.new_time_us,h.achieved_at,
                NOT EXISTS(SELECT 1 FROM st_stage_records faster WHERE faster.map_id=h.map_id AND faster.stage=h.stage AND faster.best_time_us<h.new_time_us)
              FROM st_stage_pb_history h
            ) activity JOIN st_players p ON p.steam_id=activity.player_steam_id JOIN st_maps m ON m.id=activity.map_id
            ORDER BY activity.achieved_at DESC LIMIT @limit
            """,connection);command.Parameters.AddWithValue("@limit",take);
        var rows=new List<object>();await using var reader=await command.ExecuteReaderAsync(token);
        while(await reader.ReadAsync(token))rows.Add(new{steamId=reader.GetUInt64(0).ToString(),playerName=reader.GetString(1),map=reader.GetString(2),route=reader.GetString(3),index=reader.GetInt32(4),previousTimeUs=reader.IsDBNull(5)?(long?)null:reader.GetInt64(5),timeUs=reader.GetInt64(6),achievedAt=reader.GetDateTime(7),isWorldRecord=reader.GetBoolean(8)});
        return new{activity=rows};
    });return Results.Ok(payload);
});

app.MapGet("/api/rankings",async(int? limit,Database database,IMemoryCache cache,CancellationToken token)=>
{
    var take=Math.Clamp(limit??25,1,100);var payload=await cache.GetOrCreateAsync<object>($"points-rankings:{take}",async entry=>
    {
        entry.AbsoluteExpirationRelativeToNow=TimeSpan.FromSeconds(recordCacheSeconds);entry.Size=1;
        await using var connection=await database.OpenAsync(token);await using var command=new MySqlCommand(PointsCte+" SELECT o.overall_rank,o.player_steam_id,p.last_name,o.points,o.completed_maps,o.group1,o.group2,o.group3,o.group4,o.group5,o.map_points,o.stage_points,o.bonus_points,o.title FROM overall o JOIN st_players p ON p.steam_id=o.player_steam_id ORDER BY o.points DESC,o.group1 DESC,o.group2 DESC,o.completed_maps DESC,p.last_name LIMIT @limit",connection);command.Parameters.AddWithValue("@limit",take);
        var rows=new List<object>();await using var reader=await command.ExecuteReaderAsync(token);while(await reader.ReadAsync(token))rows.Add(ReadPointsRow(reader));return new{policy="Points",rankings=rows};
    });return Results.Ok(payload);
});

app.MapGet("/api/maps/{map}/leaderboard",async(string map,string? route,int? index,int? limit,int? page,int? pageSize,Database database,IMemoryCache cache,CancellationToken token)=>
{
    route=route?.ToLowerInvariant()??"main"; if(route is not("main" or "bonus")) return Results.BadRequest(new{error="route must be main or bonus"});
    if(!await MapExistsAsync(map,database,cache,metadataCacheSeconds,token))return Results.NotFound(new{error="map not found"});
    var routeIndex=route=="main"?0:Math.Clamp(index??1,1,99);var currentPage=Math.Max(page??1,1);var take=Math.Clamp(pageSize??limit??10,1,100);var offset=(currentPage-1)*take;
    var cacheKey=$"leaderboard:{map.ToLowerInvariant()}:{route}:{routeIndex}:{currentPage}:{take}";
    var payload=await cache.GetOrCreateAsync<object>(cacheKey,async entry=>
    {
    entry.AbsoluteExpirationRelativeToNow=TimeSpan.FromSeconds(recordCacheSeconds);entry.Size=1;
    await using var connection=await database.OpenAsync(token); await using var command=new MySqlCommand("""
        SELECT r.player_steam_id,p.last_name,r.best_time_us,r.completions,r.pb_updated_at,
               EXISTS(SELECT 1 FROM st_replays rp WHERE rp.record_id=r.id),COUNT(*) OVER()
        FROM st_records r JOIN st_maps m ON m.id=r.map_id JOIN st_players p ON p.steam_id=r.player_steam_id
        WHERE m.name=@map AND r.route_type=@route AND r.route_index=@index AND r.style=0 AND r.mode='surf'
        ORDER BY r.best_time_us,r.pb_updated_at,r.player_steam_id LIMIT @limit OFFSET @offset
        """,connection);
    command.Parameters.AddWithValue("@map",map);command.Parameters.AddWithValue("@route",route);command.Parameters.AddWithValue("@index",routeIndex);command.Parameters.AddWithValue("@limit",take);command.Parameters.AddWithValue("@offset",offset);
    var rows=new List<object>();await using var reader=await command.ExecuteReaderAsync(token);var rank=offset;long total=0;
    while(await reader.ReadAsync(token)){total=reader.GetInt64(6);var currentRank=++rank;rows.Add(new{rank=currentRank,group=route=="main"?GetTimeGroup(currentRank,total):(int?)null,steamId=reader.GetUInt64(0).ToString(),playerName=reader.GetString(1),timeUs=reader.GetInt64(2),completions=reader.GetInt32(3),achievedAt=reader.GetDateTime(4),hasReplay=reader.GetBoolean(5)});}
    return new{map,route,index=routeIndex,records=rows,pagination=new{page=currentPage,pageSize=take,total,totalPages=(long)Math.Ceiling(total/(double)take)}};
    });return Results.Ok(payload);
});

app.MapGet("/api/maps/{map}/stages/{stage:int}",async(string map,int stage,int? limit,int? page,int? pageSize,Database database,IMemoryCache cache,CancellationToken token)=>
{
    if(stage<1)return Results.BadRequest(new{error="stage must be positive"});var currentPage=Math.Max(page??1,1);var take=Math.Clamp(pageSize??limit??10,1,100);var offset=(currentPage-1)*take;
    if(!await MapExistsAsync(map,database,cache,metadataCacheSeconds,token))return Results.NotFound(new{error="map not found"});
    var payload=await cache.GetOrCreateAsync<object>($"stage:{map.ToLowerInvariant()}:{stage}:{currentPage}:{take}",async entry=>
    {
    entry.AbsoluteExpirationRelativeToNow=TimeSpan.FromSeconds(recordCacheSeconds);entry.Size=1;
    await using var connection=await database.OpenAsync(token);await using var command=new MySqlCommand("""
        SELECT sr.player_steam_id,p.last_name,sr.best_time_us,sr.completions,sr.pb_updated_at,
               EXISTS(SELECT 1 FROM st_stage_replays rp WHERE rp.stage_record_id=sr.id),COUNT(*) OVER()
        FROM st_stage_records sr JOIN st_maps m ON m.id=sr.map_id JOIN st_players p ON p.steam_id=sr.player_steam_id
        WHERE m.name=@map AND sr.stage=@stage ORDER BY sr.best_time_us,sr.pb_updated_at,sr.player_steam_id LIMIT @limit OFFSET @offset
        """,connection);
    command.Parameters.AddWithValue("@map",map);command.Parameters.AddWithValue("@stage",stage);command.Parameters.AddWithValue("@limit",take);command.Parameters.AddWithValue("@offset",offset);
    var rows=new List<object>();await using var reader=await command.ExecuteReaderAsync(token);var rank=offset;long total=0;
    while(await reader.ReadAsync(token)){total=reader.GetInt64(6);rows.Add(new{rank=++rank,steamId=reader.GetUInt64(0).ToString(),playerName=reader.GetString(1),timeUs=reader.GetInt64(2),completions=reader.GetInt32(3),achievedAt=reader.GetDateTime(4),hasReplay=reader.GetBoolean(5)});}
    return new{map,stage,records=rows,pagination=new{page=currentPage,pageSize=take,total,totalPages=(long)Math.Ceiling(total/(double)take)}};
    });return Results.Ok(payload);
});

app.MapGet("/api/players/search",async(string? q,Database database,CancellationToken token)=>
{
    q=q?.Trim();if(string.IsNullOrWhiteSpace(q)||q.Length<2)return Results.BadRequest(new{error="query must contain at least two characters"});
    await using var connection=await database.OpenAsync(token);await using var command=new MySqlCommand("SELECT steam_id,last_name,last_seen_at FROM st_players WHERE steam_id=@q OR LOCATE(LOWER(@q),LOWER(last_name))>0 ORDER BY (LOWER(last_name)=LOWER(@q)) DESC,last_seen_at DESC LIMIT 10",connection);
    command.Parameters.AddWithValue("@q",q);var rows=new List<object>();await using var reader=await command.ExecuteReaderAsync(token);
    while(await reader.ReadAsync(token))rows.Add(new{steamId=reader.GetUInt64(0).ToString(),playerName=reader.GetString(1),lastSeen=reader.GetDateTime(2)});
    return Results.Ok(rows);
});

app.MapGet("/api/players/{steamId}",async(string steamId,Database database,CancellationToken token)=>
{
    if(!ulong.TryParse(steamId,out var id))return Results.BadRequest(new{error="invalid SteamID64"});
    await using var connection=await database.OpenAsync(token);await using var command=new MySqlCommand("""
        SELECT p.last_name,p.first_seen_at,p.last_seen_at,p.total_connections,
          COALESCE(SUM(r.completions),0),COUNT(DISTINCT r.map_id),SUM(r.route_type='main'),SUM(r.route_type='bonus'),
          COALESCE(s.tracked_time_us,0),COALESCE(s.tracked_completions,0)
        FROM st_players p LEFT JOIN st_records r ON r.player_steam_id=p.steam_id
        LEFT JOIN st_player_run_stats s ON s.player_steam_id=p.steam_id WHERE p.steam_id=@steam
        GROUP BY p.steam_id,p.last_name,p.first_seen_at,p.last_seen_at,p.total_connections,s.tracked_time_us,s.tracked_completions
        """,connection);command.Parameters.AddWithValue("@steam",id);
    await using var reader=await command.ExecuteReaderAsync(token);if(!await reader.ReadAsync(token))return Results.NotFound(new{error="player not found"});
    return Results.Ok(new{steamId=id.ToString(),playerName=reader.GetString(0),firstSeen=reader.GetDateTime(1),lastSeen=reader.GetDateTime(2),connections=reader.GetUInt32(3),completions=reader.GetInt64(4),uniqueMaps=reader.GetInt32(5),mainRecords=reader.IsDBNull(6)?0:reader.GetInt32(6),bonusRecords=reader.IsDBNull(7)?0:reader.GetInt32(7),trackedTimeUs=reader.GetInt64(8),trackedCompletions=reader.GetInt64(9)});
});

app.MapGet("/api/players/{steamId}/stats",async(string steamId,Database database,IMemoryCache cache,CancellationToken token)=>
{
    if(!ulong.TryParse(steamId,out var id))return Results.BadRequest(new{error="invalid SteamID64"});
    var payload=await cache.GetOrCreateAsync<object>($"player-stats:{id}",async entry=>
    {
        entry.AbsoluteExpirationRelativeToNow=TimeSpan.FromSeconds(metadataCacheSeconds);entry.Size=1;
        await using var connection=await database.OpenAsync(token);await using var command=new MySqlCommand("""
            SELECT
              (SELECT COUNT(*) FROM st_stage_records WHERE player_steam_id=@steam),
              (SELECT COUNT(*) FROM st_replays rp JOIN st_records r ON r.id=rp.record_id WHERE r.player_steam_id=@steam)+(SELECT COUNT(*) FROM st_stage_replays rp JOIN st_stage_records sr ON sr.id=rp.stage_record_id WHERE sr.player_steam_id=@steam),
              (SELECT COUNT(*) FROM st_records r WHERE r.player_steam_id=@steam AND NOT EXISTS(SELECT 1 FROM st_records f WHERE f.map_id=r.map_id AND f.route_type=r.route_type AND f.route_index=r.route_index AND f.style=r.style AND f.mode=r.mode AND f.best_time_us<r.best_time_us))+
              (SELECT COUNT(*) FROM st_stage_records sr WHERE sr.player_steam_id=@steam AND NOT EXISTS(SELECT 1 FROM st_stage_records f WHERE f.map_id=sr.map_id AND f.stage=sr.stage AND f.best_time_us<sr.best_time_us)),
              (SELECT MIN(position) FROM (
                SELECT 1+(SELECT COUNT(*) FROM st_records f WHERE f.map_id=r.map_id AND f.route_type=r.route_type AND f.route_index=r.route_index AND f.style=r.style AND f.mode=r.mode AND f.best_time_us<r.best_time_us) position FROM st_records r WHERE r.player_steam_id=@steam
                UNION ALL SELECT 1+(SELECT COUNT(*) FROM st_stage_records f WHERE f.map_id=sr.map_id AND f.stage=sr.stage AND f.best_time_us<sr.best_time_us) FROM st_stage_records sr WHERE sr.player_steam_id=@steam
              ) placements),
              (SELECT m.name FROM st_records r JOIN st_maps m ON m.id=r.map_id WHERE r.player_steam_id=@steam GROUP BY m.id,m.name ORDER BY SUM(r.completions) DESC,m.name LIMIT 1),
              (SELECT COUNT(*) FROM st_pb_history WHERE player_steam_id=@steam)
            """,connection);command.Parameters.AddWithValue("@steam",id);
        await using var reader=await command.ExecuteReaderAsync(token);await reader.ReadAsync(token);
        return new{steamId=id.ToString(),stageRecords=reader.GetInt32(0),replays=reader.GetInt32(1),worldRecords=reader.GetInt32(2),bestRank=reader.IsDBNull(3)?(int?)null:reader.GetInt32(3),mostPlayedMap=reader.IsDBNull(4)?null:reader.GetString(4),pbHistoryEntries=reader.GetInt32(5)};
    });return Results.Ok(payload);
});

app.MapGet("/api/players/{steamId}/points",async(string steamId,Database database,IMemoryCache cache,CancellationToken token)=>
{
    if(!ulong.TryParse(steamId,out var id))return Results.BadRequest(new{error="invalid SteamID64"});var payload=await cache.GetOrCreateAsync<object?>($"player-points:{id}",async entry=>
    {
        entry.AbsoluteExpirationRelativeToNow=TimeSpan.FromSeconds(recordCacheSeconds);entry.Size=1;
        await using var connection=await database.OpenAsync(token);await using var command=new MySqlCommand(PointsCte+" SELECT o.overall_rank,o.player_steam_id,p.last_name,o.points,o.completed_maps,o.group1,o.group2,o.group3,o.group4,o.group5,o.map_points,o.stage_points,o.bonus_points,o.title FROM overall o JOIN st_players p ON p.steam_id=o.player_steam_id WHERE o.player_steam_id=@steam",connection);command.Parameters.AddWithValue("@steam",id);
        await using var reader=await command.ExecuteReaderAsync(token);return await reader.ReadAsync(token)?ReadPointsRow(reader):null;
    });return payload is null?Results.NotFound(new{error="player has no Points yet"}):Results.Ok(new{policy="Points",ranking=payload});
});

app.MapGet("/api/players/{steamId}/history",async(string steamId,string? map,string? route,int? index,int? page,int? pageSize,Database database,IMemoryCache cache,CancellationToken token)=>
{
    if(!ulong.TryParse(steamId,out var id))return Results.BadRequest(new{error="invalid SteamID64"});route=route?.ToLowerInvariant();if(route is not null and not("main" or "bonus" or "stage"))return Results.BadRequest(new{error="history route must be main, bonus, or stage"});
    var currentPage=Math.Max(page??1,1);var take=Math.Clamp(pageSize??10,1,100);var offset=(currentPage-1)*take;var routeIndex=index??(route=="main"?0:1);
    var key=$"history:{id}:{map?.ToLowerInvariant()}:{route}:{routeIndex}:{currentPage}:{take}";var payload=await cache.GetOrCreateAsync<object>(key,async entry=>
    {
        entry.AbsoluteExpirationRelativeToNow=TimeSpan.FromSeconds(recordCacheSeconds);entry.Size=1;
        await using var connection=await database.OpenAsync(token);await using var command=new MySqlCommand("""
            SELECT history.map_name,history.route_type,history.route_index,history.previous_time_us,history.new_time_us,history.achieved_at,COUNT(*) OVER()
            FROM (
              SELECT m.name map_name,h.route_type,h.route_index,h.previous_time_us,h.new_time_us,h.achieved_at,h.id
              FROM st_pb_history h JOIN st_maps m ON m.id=h.map_id WHERE h.player_steam_id=@steam
              UNION ALL
              SELECT m.name,'stage',h.stage,h.previous_time_us,h.new_time_us,h.achieved_at,h.id
              FROM st_stage_pb_history h JOIN st_maps m ON m.id=h.map_id WHERE h.player_steam_id=@steam
            ) history
            WHERE (@map IS NULL OR history.map_name=@map) AND (@route IS NULL OR (history.route_type=@route AND history.route_index=@index))
            ORDER BY history.achieved_at DESC,history.id DESC LIMIT @limit OFFSET @offset
            """,connection);command.Parameters.AddWithValue("@steam",id);command.Parameters.AddWithValue("@map",string.IsNullOrWhiteSpace(map)?DBNull.Value:map);command.Parameters.AddWithValue("@route",string.IsNullOrWhiteSpace(route)?DBNull.Value:route);command.Parameters.AddWithValue("@index",routeIndex);command.Parameters.AddWithValue("@limit",take);command.Parameters.AddWithValue("@offset",offset);
        var rows=new List<object>();await using var reader=await command.ExecuteReaderAsync(token);long total=0;
        while(await reader.ReadAsync(token)){total=reader.GetInt64(6);var previous=reader.IsDBNull(3)?(long?)null:reader.GetInt64(3);var current=reader.GetInt64(4);rows.Add(new{map=reader.GetString(0),route=reader.GetString(1),index=reader.GetInt32(2),previousTimeUs=previous,timeUs=current,improvementUs=previous-current,achievedAt=reader.GetDateTime(5)});}
        return new{steamId=id.ToString(),history=rows,pagination=new{page=currentPage,pageSize=take,total,totalPages=(long)Math.Ceiling(total/(double)take)}};
    });return Results.Ok(payload);
});

app.MapGet("/api/players/{steamId}/records",async(string steamId,string? map,string? route,string? sort,int? page,int? pageSize,Database database,IMemoryCache cache,CancellationToken token)=>
{
    if(!ulong.TryParse(steamId,out var id))return Results.BadRequest(new{error="invalid SteamID64"});
    route=route?.ToLowerInvariant();if(route is not null and not("main" or "bonus" or "stage"))return Results.BadRequest(new{error="route must be main, bonus, or stage"});sort=sort?.ToLowerInvariant()??"recent";
    var orderBy=sort switch{"recent"=>"pb_updated_at DESC,map_name,route_type,route_index","rank"=>"position,map_name,route_type,route_index","time"=>"best_time_us,map_name,route_type,route_index","map"=>"map_name,route_type,route_index","name"=>"map_name,route_type,route_index",_=>null};if(orderBy is null)return Results.BadRequest(new{error="sort must be recent, rank, time, or map"});
    var currentPage=Math.Max(page??1,1);var take=Math.Clamp(pageSize??25,1,100);var offset=(currentPage-1)*take;
    var payload=await cache.GetOrCreateAsync<object>($"player-records:{id}:{map?.ToLowerInvariant()}:{route}:{sort}:{currentPage}:{take}",async entry=>
    {
    entry.AbsoluteExpirationRelativeToNow=TimeSpan.FromSeconds(recordCacheSeconds);entry.Size=1;
    await using var connection=await database.OpenAsync(token);await using var command=new MySqlCommand($$"""
        SELECT routes.*,COUNT(*) OVER() FROM (
        SELECT m.name AS map_name,m.tier,r.route_type,r.route_index,r.best_time_us,r.completions,r.pb_updated_at,
          1+(SELECT COUNT(*) FROM st_records faster WHERE faster.map_id=r.map_id AND faster.route_type=r.route_type
             AND faster.route_index=r.route_index AND faster.style=0 AND faster.mode='surf' AND faster.best_time_us<r.best_time_us) AS position,
          (SELECT COUNT(*) FROM st_records total WHERE total.map_id=r.map_id AND total.route_type=r.route_type AND total.route_index=r.route_index AND total.style=0 AND total.mode='surf') route_total
        FROM st_records r JOIN st_maps m ON m.id=r.map_id
        WHERE r.player_steam_id=@steam AND r.style=0 AND r.mode='surf'
        UNION ALL
        SELECT m.name,m.tier,'stage',sr.stage,sr.best_time_us,sr.completions,sr.pb_updated_at,
          1+(SELECT COUNT(*) FROM st_stage_records faster WHERE faster.map_id=sr.map_id AND faster.stage=sr.stage
             AND faster.best_time_us<sr.best_time_us) AS position,
          (SELECT COUNT(*) FROM st_stage_records total WHERE total.map_id=sr.map_id AND total.stage=sr.stage) route_total
        FROM st_stage_records sr JOIN st_maps m ON m.id=sr.map_id WHERE sr.player_steam_id=@steam
        ) routes WHERE (@map IS NULL OR map_name=@map) AND (@route IS NULL OR route_type=@route)
        ORDER BY {{orderBy}} LIMIT @limit OFFSET @offset
        """,connection);command.Parameters.AddWithValue("@steam",id);command.Parameters.AddWithValue("@map",string.IsNullOrWhiteSpace(map)?DBNull.Value:map);command.Parameters.AddWithValue("@route",string.IsNullOrWhiteSpace(route)?DBNull.Value:route);command.Parameters.AddWithValue("@limit",take);command.Parameters.AddWithValue("@offset",offset);
    var rows=new List<object>();await using var reader=await command.ExecuteReaderAsync(token);long total=0;
    while(await reader.ReadAsync(token)){total=reader.GetInt64(9);var recordRoute=reader.GetString(2);var recordRank=reader.GetInt32(7);rows.Add(new{map=reader.GetString(0),tier=reader.GetInt32(1),route=recordRoute,index=reader.GetInt32(3),timeUs=reader.GetInt64(4),completions=reader.GetInt32(5),achievedAt=reader.GetDateTime(6),rank=recordRank,group=recordRoute=="main"?GetTimeGroup(recordRank,reader.GetInt64(8)):null});}
    return new{steamId=id.ToString(),records=rows,pagination=new{page=currentPage,pageSize=take,total,totalPages=(long)Math.Ceiling(total/(double)take)}};
    });return Results.Ok(payload);
});

app.MapFallbackToFile("index.html");
app.Run();

static int GetInt(string name,int fallback,int minimum,int maximum)=>int.TryParse(Environment.GetEnvironmentVariable(name),out var value)?Math.Clamp(value,minimum,maximum):fallback;
static uint GetUInt(string name,uint fallback)=>uint.TryParse(Environment.GetEnvironmentVariable(name),out var value)?value:fallback;
static string RequireEnvironment(string name)=>Environment.GetEnvironmentVariable(name)??throw new InvalidOperationException($"Required environment variable {name} is missing.");
static int? GetTimeGroup(int rank,long total){if(total<=0)return null;var percentile=(rank-1)/(double)total;return percentile<.01?1:percentile<.05?2:percentile<.10?3:percentile<.25?4:percentile<.50?5:null;}
static PointsRow ReadPointsRow(MySqlDataReader reader)=>new(reader.GetInt32(0),reader.GetUInt64(1).ToString(),reader.GetString(2),Convert.ToInt64(reader.GetValue(3)),reader.GetInt32(4),reader.GetInt32(5),reader.GetInt32(6),reader.GetInt32(7),reader.GetInt32(8),reader.GetInt32(9),Convert.ToInt64(reader.GetValue(10)),Convert.ToInt64(reader.GetValue(11)),Convert.ToInt64(reader.GetValue(12)),reader.GetString(13));
static async Task<bool> MapExistsAsync(string map,Database database,IMemoryCache cache,int cacheSeconds,CancellationToken token)=>await cache.GetOrCreateAsync($"map-exists:{map.ToLowerInvariant()}",async entry=>
{
    entry.AbsoluteExpirationRelativeToNow=TimeSpan.FromSeconds(cacheSeconds);entry.Size=1;
    await using var connection=await database.OpenAsync(token);await using var command=new MySqlCommand("SELECT EXISTS(SELECT 1 FROM st_maps WHERE name=@map AND enabled=1)",connection);command.Parameters.AddWithValue("@map",map);
    return Convert.ToBoolean(await command.ExecuteScalarAsync(token));
});

sealed class Database(string connectionString)
{
    public async Task<MySqlConnection> OpenAsync(CancellationToken token){var connection=new MySqlConnection(connectionString);await connection.OpenAsync(token);return connection;}
}
sealed record PointsRow(int Rank,string SteamId,string PlayerName,long Points,int CompletedMaps,int Group1,int Group2,int Group3,int Group4,int Group5,long MapPoints,long StagePoints,long BonusPoints,string Title);
