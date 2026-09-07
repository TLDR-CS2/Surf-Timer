using Microsoft.Extensions.Logging;
using SurfTimer.Configuration;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.SchemaDefinitions;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;

namespace SurfTimer.Maps;

public sealed class FinishZoneRenderer(ISwiftlyCore core, MapLifecycle maps, SurfTimerOptions options,
    ILogger<FinishZoneRenderer> logger)
{
    private static readonly (int A,int B)[] Edges =
        [(0,1),(1,3),(3,2),(2,0),(4,5),(5,7),(7,6),(6,4),(0,4),(1,5),(2,6),(3,7)];
    private readonly List<CBeam> _beams=[];
    private Guid? _activateHook;
    private bool _started;

    public void Start(bool hotReload)
    {
        if(_started)return;_started=true;core.Event.OnMapLoad+=OnMapLoad;core.Event.OnMapUnload+=OnMapUnload;
        core.Event.OnClientPutInServer+=OnClientPutInServer;
        _activateHook=core.GameEvent.HookPost<EventPlayerActivate>(OnPlayerActivate);
        if(hotReload&&maps.Current is not null)Schedule(maps.Current.Generation);
    }

    public void Stop(){if(!_started)return;core.Event.OnMapLoad-=OnMapLoad;core.Event.OnMapUnload-=OnMapUnload;core.Event.OnClientPutInServer-=OnClientPutInServer;if(_activateHook is{} hook)core.GameEvent.Unhook(hook);_activateHook=null;Clear();_started=false;}
    private void OnMapLoad(IOnMapLoadEvent value)
    {
        Clear();
        var generation=maps.Current?.Generation;
        if(generation is not null)core.Scheduler.DelayBySeconds(9f,()=>Draw(generation.Value));
    }
    private void OnMapUnload(IOnMapUnloadEvent value)=>Clear();
    private HookResult OnPlayerActivate(EventPlayerActivate value)
    {
        var generation=maps.Current?.Generation;if(generation is not null)core.Scheduler.DelayBySeconds(1f,()=>Draw(generation.Value));
        return HookResult.Continue;
    }
    private void OnClientPutInServer(IOnClientPutInServerEvent value)
    {
        var generation=maps.Current?.Generation;
        if(generation is not null)core.Scheduler.DelayBySeconds(2f,()=>Draw(generation.Value));
    }
    private void Schedule(long generation)=>core.Scheduler.DelayBySeconds(1f,()=>Draw(generation));

    private void Draw(long generation)
    {
        Clear();var current=maps.Current;if(current is null||current.Generation!=generation||!options.ZoneVisibility.Enabled||
           (!options.ZoneVisibility.RenderAllMaps &&
            !options.ZoneVisibility.Maps.Contains(current.Name,StringComparer.OrdinalIgnoreCase)))return;
        var snapshot=maps.Triggers.LastOrDefault(trigger=>maps.IsEndTrigger(trigger.TargetName));
        if(snapshot is null){logger.LogWarning("Finish-zone renderer could not find end trigger on {Map}.",current.Name);return;}
        var entity=core.EntitySystem.GetEntityByIndex<CBaseEntity>(snapshot.EntityIndex);
        if(entity is null||!entity.IsValid||entity.AbsOrigin is not{} origin){logger.LogWarning("Finish-zone renderer found no live end trigger on {Map}.",current.Name);return;}
        var collision=entity.Collision;if(collision is null){logger.LogWarning("Finish-zone trigger has no collision data on {Map}.",current.Name);return;}
        var mins=collision.Mins;var maxs=collision.Maxs;
        if(!Finite(mins)||!Finite(maxs)||maxs.X<=mins.X||maxs.Y<=mins.Y||maxs.Z<=mins.Z){logger.LogWarning("Finish-zone bounds are invalid on {Map}: mins={Mins}, maxs={Maxs}.",current.Name,mins,maxs);return;}
        var rotation=entity.AbsRotation??QAngle.Zero;
        var corners=new[]{
            Point(mins.X,mins.Y,mins.Z),Point(maxs.X,mins.Y,mins.Z),Point(mins.X,maxs.Y,mins.Z),Point(maxs.X,maxs.Y,mins.Z),
            Point(mins.X,mins.Y,maxs.Z),Point(maxs.X,mins.Y,maxs.Z),Point(mins.X,maxs.Y,maxs.Z),Point(maxs.X,maxs.Y,maxs.Z)}
            .Select(local=>Transform(local,origin,rotation)).ToArray();
        foreach(var edge in Edges)if(TryCreateBeam(corners[edge.A],corners[edge.B],out var beam))_beams.Add(beam);
        logger.LogInformation("Rendered finish-zone box for {Map} from trigger #{EntityIndex} with {BeamCount} beams.",current.Name,snapshot.EntityIndex,_beams.Count);
    }

    private bool TryCreateBeam(Vector start,Vector end,out CBeam beam)
    {
        beam=core.EntitySystem.CreateEntityByDesignerName<CBeam>("beam");if(beam is null)return false;
        var setting=options.ZoneVisibility;beam.Render=new Color(setting.Red,setting.Green,setting.Blue,setting.Alpha);
        beam.BeamType=BeamType_t.BEAM_POINTS;beam.Width=setting.Width;beam.EndWidth=setting.Width;beam.HDRColorScale=1f;beam.TurnedOff=false;
        beam.Teleport(start,QAngle.Zero,Vector.Zero);beam.EndPos.X=end.X;beam.EndPos.Y=end.Y;beam.EndPos.Z=end.Z;beam.DispatchSpawn();
        beam.BeamTypeUpdated();beam.RenderUpdated();beam.WidthUpdated();beam.EndWidthUpdated();beam.HDRColorScaleUpdated();beam.TurnedOffUpdated();beam.EndPosUpdated();beam.EntityUpdated();return true;
    }

    private void Clear(){foreach(var beam in _beams)if(beam.IsValid)beam.Despawn();_beams.Clear();}
    private static Vector Point(float x,float y,float z)=>new(x,y,z);
    private static bool Finite(Vector value)=>float.IsFinite(value.X)&&float.IsFinite(value.Y)&&float.IsFinite(value.Z);
    private static Vector Transform(Vector point,Vector origin,QAngle angles)
    {
        var pitch=angles.X*Math.PI/180d;var yaw=angles.Y*Math.PI/180d;var roll=angles.Z*Math.PI/180d;
        var cp=Math.Cos(pitch);var sp=Math.Sin(pitch);var cy=Math.Cos(yaw);var sy=Math.Sin(yaw);var cr=Math.Cos(roll);var sr=Math.Sin(roll);
        var x=point.X*(cy*cp)+point.Y*(cy*sp*sr-sy*cr)+point.Z*(cy*sp*cr+sy*sr);
        var y=point.X*(sy*cp)+point.Y*(sy*sp*sr+cy*cr)+point.Z*(sy*sp*cr-cy*sr);
        var z=point.X*(-sp)+point.Y*(cp*sr)+point.Z*(cp*cr);
        return new Vector(origin.X+(float)x,origin.Y+(float)y,origin.Z+(float)z);
    }
}
