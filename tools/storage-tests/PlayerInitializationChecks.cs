using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SurfTimer.Commands;
using SurfTimer.Configuration;
using SurfTimer.Players;
using SurfTimer.Storage;
using SurfTimer.Titles;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;

internal static class PlayerInitializationChecks
{
    public static async Task RunAsync(ISwiftlyCore databaseCore, RecordRepository records,
        MigrationRunner migrations, SurfTimerOptions options, ulong steamId)
    {
        var preferences = new PlayerPreferenceRepository(databaseCore, options, migrations, NullLogger<PlayerPreferenceRepository>.Instance);
        await preferences.SaveAsync(steamId, new PlayerPreferences(SoundsEnabled: false, KeysEnabled: false));
        var callbacks = new ConcurrentQueue<Action>();
        var player = (IPlayer)Proxy(typeof(IPlayer), (method, _) => method.Name switch
        {
            "get_PlayerID" => 7, "get_SessionId" => 123UL, "get_SteamID" => steamId,
            "get_Name" => "initialization test", "get_IsFakeClient" => false,
            "get_IsAuthorized" or "get_IsAlive" or "get_IsValid" => true,
            _ => throw new NotSupportedException(method.Name)
        });
        var scheduler = Proxy(typeof(ISwiftlyCore).GetProperty("Scheduler")!.PropertyType, (method, args) =>
        {
            if (method.Name == "NextTick") { callbacks.Enqueue((Action)args![0]!); return null; }
            throw new NotSupportedException(method.Name);
        });
        var events = Proxy(typeof(ISwiftlyCore).GetProperty("Event")!.PropertyType, (_, _) => null);
        var gameEvents = Proxy(typeof(ISwiftlyCore).GetProperty("GameEvent")!.PropertyType,
            (method, _) => method.ReturnType == typeof(Guid) ? Guid.NewGuid() : null);
        var playerService = Proxy(typeof(ISwiftlyCore).GetProperty("PlayerManager")!.PropertyType, (method, _) => method.Name switch
        {
            "GetAllPlayers" => method.ReturnType.IsArray ? new[] { player } : new List<IPlayer> { player },
            "GetPlayer" => player,
            _ => throw new NotSupportedException(method.Name)
        });
        var core = (ISwiftlyCore)Proxy(typeof(ISwiftlyCore), (method, _) => method.Name switch
        {
            "get_Scheduler" => scheduler, "get_Event" => events, "get_GameEvent" => gameEvents,
            "get_PlayerManager" => playerService, _ => throw new NotSupportedException(method.Name)
        });
        var titleOptions = new SurfTimerOptions { Titles = new TitleOptions { Enabled = false } };
        var titleRepository = new PlayerTitleRepository(databaseCore, options, migrations, NullLogger<PlayerTitleRepository>.Instance);
        var titles = new TitleManager(core, records, titleRepository, titleOptions, NullLogger<TitleManager>.Instance);
        var manager = new SurfPlayerManager(core, records, preferences, titles, NullLogger<SurfPlayerManager>.Instance);
        var replies = new List<string>();
        var context = (ICommandContext)Proxy(typeof(ICommandContext), (method, args) => method.Name switch
        {
            "get_IsSentByPlayer" => true, "get_Sender" => player, "get_Args" => new[] { "off" },
            "Reply" => Reply(args), _ => throw new NotSupportedException(method.Name)
        });
        object? Reply(object?[]? args) { replies.Add((string)args![0]!); return null; }
        var commands = new PreferenceCommands(core, manager, preferences, NullLogger<PreferenceCommands>.Instance);
        var toggle = typeof(PreferenceCommands).GetMethod("Toggle", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var hud = Enum.Parse(toggle.GetParameters()[1].ParameterType, "Hud");
        try
        {
            manager.Start(hotReload: true);
            var session = manager.Get(7)!;
            Check(session.PreferencesLoading && !session.PreferencesLoaded, "Hot reload failed to initialize saved settings.");
            manager.EnsurePreferencesLoaded(session);
            toggle.Invoke(commands, [context, hud]);
            Check(session.PreferenceRevision == 0 && replies.Any(reply => reply.Contains("loading")), "Early toggle mutated unloaded defaults.");
            Check(!(await preferences.LoadAsync(steamId)).SoundsEnabled, "Early toggle erased saved sound setting.");
            await WaitUntilAsync(() => callbacks.Count > 0);
            Check(callbacks.Count == 1, "Repeated initialization scheduled duplicate loads.");
            callbacks.TryDequeue(out var load);
            load!();
            Check(session.PreferencesLoaded && !session.Preferences.SoundsEnabled && !session.Preferences.KeysEnabled, "Hot reload lost saved preferences.");
            toggle.Invoke(commands, [context, hud]);
            for (var attempt = 0; attempt < 100 && (await preferences.LoadAsync(steamId)).HudEnabled; attempt++) await Task.Delay(20);
            var saved = await preferences.LoadAsync(steamId);
            Check(!saved.HudEnabled && !saved.SoundsEnabled && !saved.KeysEnabled, "HUD toggle overwrote unrelated loaded settings.");

            // An old callback must not initialize a replacement session after another reload.
            manager.Stop(); manager.Start(hotReload: true);
            await WaitUntilAsync(() => callbacks.Count > 0);
            callbacks.TryDequeue(out var stale);
            manager.Stop(); manager.Start(hotReload: true);
            stale!();
            Check(!manager.Get(7)!.PreferencesLoaded, "Old callback changed replacement session.");
            await WaitUntilAsync(() => callbacks.Count > 0);
            callbacks.TryDequeue(out var fresh); fresh!();
            Check(manager.Get(7)!.PreferencesLoaded && !manager.Get(7)!.Preferences.HudEnabled, "Fresh reload did not restore settings.");
            Console.WriteLine("PASS: actual hot-reload initialization, early command guard, unrelated settings preservation and stale callbacks.");
        }
        finally { manager.Stop(); preferences.Stop(); titleRepository.Stop(); }
    }

    private static object Proxy(Type type, Func<MethodInfo, object?[]?, object?> handler)
    {
        var proxy = DispatchProxy.Create(type, typeof(TestProxy));
        ((TestProxy)proxy).InvokeHandler = handler;
        return proxy;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 250 && !condition(); attempt++) await Task.Delay(20);
        Check(condition(), "Preference callback did not arrive.");
    }
}
