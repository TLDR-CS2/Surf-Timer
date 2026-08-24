using Microsoft.Extensions.Logging;
using SurfTimer.Players;
using SurfTimer.Storage;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;

namespace SurfTimer.Commands;

public sealed class PreferenceCommands(
    ISwiftlyCore core,
    SurfPlayerManager players,
    PlayerPreferenceRepository repository,
    ILogger<PreferenceCommands> logger)
{
    private readonly List<Guid> _registrations = [];

    public void Register()
    {
        if (_registrations.Count != 0) return;
        Register("settings", OnSettings, "Shows your persistent SurfTimer preferences.");
        Register("hud", context => Toggle(context, Preference.Hud), "Toggles the timer HUD.");
        Register("speed", context => Toggle(context, Preference.Speed), "Toggles the HUD speed line.");
        Register("status", context => Toggle(context, Preference.Status), "Toggles checkpoint and run status.");
        Register("keys", context => Toggle(context, Preference.Keys), "Toggles the movement-key display.");
        Register("sounds", context => Toggle(context, Preference.Sounds), "Toggles timer sounds.");
        Register("replayhud", context => Toggle(context, Preference.ReplayHud), "Toggles the replay HUD.");
        foreach (var name in new[] { "settings", "hud", "speed", "status", "keys", "sounds", "replayhud" })
            core.Command.RegisterCommandAlias("sw_" + name, "css_" + name, registerRaw: true);
    }

    public void Unregister()
    {
        foreach (var name in new[] { "css_settings", "css_hud", "css_speed", "css_status", "css_keys", "css_sounds", "css_replayhud" })
            core.Command.UnregisterCommand(name);
        foreach (var registration in _registrations) core.Command.UnregisterCommand(registration);
        _registrations.Clear();
    }

    private void Register(string name, ICommandService.CommandListener callback, string help) =>
        _registrations.Add(core.Command.RegisterCommand(name, callback, registerRaw: false, helpText: help));

    private void OnSettings(ICommandContext context)
    {
        if (!TrySession(context, out var session)) return;
        var value = session.Preferences;
        context.Reply($"[SurfTimer] Settings — HUD {OnOff(value.HudEnabled)} | speed {OnOff(value.SpeedEnabled)} | " +
                      $"status {OnOff(value.StatusEnabled)} | keys {OnOff(value.KeysEnabled)} | " +
                      $"sounds {OnOff(value.SoundsEnabled)} | replay HUD {OnOff(value.ReplayHudEnabled)}");
    }

    private void Toggle(ICommandContext context, Preference preference)
    {
        if (!TrySession(context, out var session)) return;
        var current = Get(session.Preferences, preference);
        var enabled = ParseDesired(context.Args, current, context);
        if (enabled is null) return;
        var updated = Set(session.Preferences, preference, enabled.Value);
        session.SetPreferences(updated);
        context.Reply($"[SurfTimer] {Label(preference)}: {OnOff(enabled.Value)}");
        _ = SaveAsync(session.SteamId, updated);
    }

    private async Task SaveAsync(ulong steamId, PlayerPreferences preferences)
    {
        try { await repository.SaveAsync(steamId, preferences).ConfigureAwait(false); }
        catch (Exception exception)
        {
            logger.LogError(exception, "Preference command failed for {SteamId}.", steamId);
        }
    }

    private bool TrySession(ICommandContext context, out SurfPlayerSession session)
    {
        session = null!;
        if (!context.IsSentByPlayer || context.Sender is null)
        { context.Reply("This command requires a player caller."); return false; }
        session = players.Get(context.Sender.PlayerID)!;
        if (session is null || !session.IsAuthorized || session.SteamId == 0)
        { context.Reply("[SurfTimer] Your Steam account is not authorized yet."); return false; }
        return true;
    }

    private static bool? ParseDesired(string[] args, bool current, ICommandContext context)
    {
        if (args.Length == 0) return !current;
        if (args[0].Equals("on", StringComparison.OrdinalIgnoreCase) || args[0] == "1") return true;
        if (args[0].Equals("off", StringComparison.OrdinalIgnoreCase) || args[0] == "0") return false;
        context.Reply("[SurfTimer] Use on or off, or omit the value to toggle.");
        return null;
    }

    private static bool Get(PlayerPreferences value, Preference preference) => preference switch
    {
        Preference.Hud => value.HudEnabled, Preference.Speed => value.SpeedEnabled,
        Preference.Status => value.StatusEnabled, Preference.Keys => value.KeysEnabled,
        Preference.Sounds => value.SoundsEnabled, _ => value.ReplayHudEnabled
    };

    private static PlayerPreferences Set(PlayerPreferences value, Preference preference, bool enabled) => preference switch
    {
        Preference.Hud => value with { HudEnabled = enabled },
        Preference.Speed => value with { SpeedEnabled = enabled },
        Preference.Status => value with { StatusEnabled = enabled },
        Preference.Keys => value with { KeysEnabled = enabled },
        Preference.Sounds => value with { SoundsEnabled = enabled },
        _ => value with { ReplayHudEnabled = enabled }
    };

    private static string Label(Preference value) => value switch
    {
        Preference.Hud => "Timer HUD", Preference.Speed => "Speed display", Preference.Status => "Run status",
        Preference.Keys => "Key display", Preference.Sounds => "Sounds", _ => "Replay HUD"
    };
    private static string OnOff(bool value) => value ? "ON" : "OFF";
    private enum Preference { Hud, Speed, Status, Keys, Sounds, ReplayHud }
}
