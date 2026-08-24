namespace SurfTimer.Commands;

using SurfTimer.Players;
using SurfTimer.Practice;
using SurfTimer.Replays;
using SurfTimer.Timing;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.SchemaDefinitions;

public sealed class PracticeCommands(
    ISwiftlyCore core,
    SurfPlayerManager players,
    ReplayRecorder replays,
    ReplayPlaybackManager playback)
{
    private readonly List<Guid> _registrations = [];

    public void Register()
    {
        if (_registrations.Count != 0) return;
        Register("saveloc", OnSave, "Saves your current practice location.");
        Register("tele", context => OnTeleport(context, 0), "Teleports to your current saved location.");
        Register("teleprev", context => OnTeleport(context, -1), "Selects and teleports to the previous saved location.");
        Register("telenext", context => OnTeleport(context, 1), "Selects and teleports to the next saved location.");
        Register("noclip", OnNoclip, "Toggles practice noclip.");
        Register("ncspeed", OnNoclipSpeed, "Sets practice noclip speed: !ncspeed <500-2000>.");
        foreach (var name in new[] { "saveloc", "tele", "teleprev", "telenext", "noclip", "ncspeed" })
            core.Command.RegisterCommandAlias("sw_" + name, "css_" + name, registerRaw: true);
        core.Event.OnTick += OnTick;
    }

    public void Unregister()
    {
        core.Event.OnTick -= OnTick;
        foreach (var name in new[] { "css_saveloc", "css_tele", "css_teleprev", "css_telenext", "css_noclip", "css_ncspeed" })
            core.Command.UnregisterCommand(name);
        foreach (var registration in _registrations) core.Command.UnregisterCommand(registration);
        _registrations.Clear();
    }

    public void Exit(SurfPlayerSession session, CCSPlayerPawn? pawn)
    {
        SetNoclip(pawn, false);
        session.Practice.Reset();
    }

    private void Register(string name, ICommandService.CommandListener callback, string help) =>
        _registrations.Add(core.Command.RegisterCommand(name, callback, registerRaw: false, helpText: help));

    private void OnSave(ICommandContext context)
    {
        if (!TryPlayer(context, out var session, out var pawn) || pawn.AbsOrigin is not { } origin) return;
        var checkpoint = session.Run.LastCheckpoint;
        StopCompetitive(session, RunInvalidationReason.PracticeSave);
        var index = session.Practice.Save(new SavedLocation(new Vector(origin), new QAngle(pawn.EyeAngles),
            new Vector(pawn.AbsVelocity), checkpoint));
        context.Reply($"[SurfTimer] Practice location #{index + 1} saved.");
    }

    private void OnTeleport(ICommandContext context, int direction)
    {
        if (context.Sender is null || !TrySession(context, out var session)) return;
        var location = direction == 0 ? session.Practice.Current() : session.Practice.Move(direction);
        if (location is null) { context.Reply("[SurfTimer] No practice location saved. Use !saveloc first."); return; }
        StopCompetitive(session, RunInvalidationReason.PracticeTeleport);
        session.Practice.Activate();
        var playerId = context.Sender.PlayerID;
        var sessionId = context.Sender.SessionId;
        if (!context.Sender.IsAlive)
        {
            context.Sender.Respawn();
            core.Scheduler.NextTick(() => Teleport(playerId, sessionId, location));
        }
        else Teleport(playerId, sessionId, location);
        context.Reply($"[SurfTimer] Practice location #{session.Practice.CurrentIndex + 1}/{session.Practice.Locations.Count}.");
    }

    private void OnNoclip(ICommandContext context)
    {
        if (!TryPlayer(context, out var session, out var pawn)) return;
        StopCompetitive(session, RunInvalidationReason.Noclip);
        var enabled = !session.Practice.IsNoclip;
        session.Practice.SetNoclip(enabled);
        SetNoclip(pawn, enabled);
        context.Reply($"[SurfTimer] Practice noclip {(enabled ? "enabled" : "disabled")}.");
    }

    private void OnNoclipSpeed(ICommandContext context)
    {
        if (!TrySession(context, out var session)) return;
        if (context.Args.Length == 0)
        {
            context.Reply($"[SurfTimer] Noclip speed: {session.Practice.NoclipSpeed} u/s. Usage: !ncspeed <500-2000>");
            return;
        }
        if (context.Args.Length != 1 || !int.TryParse(context.Args[0], out var speed) || speed is < 500 or > 2000)
        {
            context.Reply("[SurfTimer] Usage: !ncspeed <500-2000>");
            return;
        }
        session.Practice.SetNoclipSpeed(speed);
        context.Reply($"[SurfTimer] Noclip speed set to {speed} u/s.");
    }

    private void OnTick()
    {
        foreach (var session in players.Sessions)
        {
            if (!session.Practice.IsNoclip) continue;
            var pawn = core.PlayerManager.GetPlayer(session.PlayerId)?.PlayerPawn;
            if (pawn is null || !pawn.IsValid) continue;
            var velocity = pawn.AbsVelocity;
            var magnitude = Math.Sqrt((velocity.X * velocity.X) + (velocity.Y * velocity.Y) + (velocity.Z * velocity.Z));
            var limit = session.Practice.NoclipSpeed;
            if (magnitude <= limit || magnitude <= 0.001d) continue;
            var scale = limit / magnitude;
            pawn.Teleport(null, null, new Vector(
                (float)(velocity.X * scale), (float)(velocity.Y * scale), (float)(velocity.Z * scale)));
        }
    }

    private void Teleport(int playerId, ulong sessionId, SavedLocation location)
    {
        var player = core.PlayerManager.GetPlayer(playerId);
        if (player is null || player.SessionId != sessionId) return;
        player.Teleport(location.Position, location.Angles, location.Velocity);
    }

    private void StopCompetitive(SurfPlayerSession session, RunInvalidationReason reason)
    {
        replays.Cancel(session.SessionId);
        playback.StopWatching(session.SessionId, restore: false);
        session.Run.Invalidate(reason);
        session.ClearBonus();
    }

    private static void SetNoclip(CCSPlayerPawn? pawn, bool enabled)
    {
        if (pawn is null || !pawn.IsValid) return;
        var type = enabled ? MoveType_t.MOVETYPE_NOCLIP : MoveType_t.MOVETYPE_WALK;
        pawn.MoveType = type;
        pawn.ActualMoveType = type;
    }

    private bool TryPlayer(ICommandContext context, out SurfPlayerSession session, out CCSPlayerPawn pawn)
    {
        pawn = null!;
        if (!TrySession(context, out session)) return false;
        var current = context.Sender?.PlayerPawn;
        if (context.Sender is null || !context.Sender.IsAlive || current is null || !current.IsValid)
        { context.Reply("[SurfTimer] You must be alive to use this command."); return false; }
        pawn = current;
        return true;
    }

    private bool TrySession(ICommandContext context, out SurfPlayerSession session)
    {
        session = null!;
        if (context.Sender is null) { context.Reply("[SurfTimer] This command requires a player caller."); return false; }
        var current = players.Get(context.Sender.PlayerID);
        if (current is null) { context.Reply("[SurfTimer] Player session is not ready."); return false; }
        session = current;
        return true;
    }
}
