using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SurfTimer.Configuration;
using SurfTimer.Diagnostics;
using SurfTimer.Maps;
using SurfTimer.Players;
using SurfTimer.Timing;
using SurfTimer.Commands;
using SurfTimer.Hud;
using SurfTimer.Storage;
using SurfTimer.Replays;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Plugins;
using BotControllerApi;

namespace SurfTimer;

[PluginMetadata(
    Id = "surf_timer",
    Version = BuildInfo.Version,
    Name = "SurfTimer",
    Author = "SurfTimer Contributors",
    Description = "A Surf-focused CS2 timer built for SwiftlyS2.")]
public sealed class Plugin(ISwiftlyCore core) : BasePlugin(core)
{
    private ServiceProvider? _services;
    private IBotControllerApi? _botController;
    private IInterfaceManager? _interfaceManager;
    private readonly BotControllerBridge _botControllerBridge = new();
    private bool _botControllerAvailabilityLogged;

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        _interfaceManager = interfaceManager;
        ResolveBotController();
    }

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager)
    {
        _interfaceManager = interfaceManager;
        ResolveBotController();
    }

    public override void OnAllPluginsLoaded() => ResolveBotController();

    private void ResolveBotController()
    {
        if (_interfaceManager is not null)
            _interfaceManager.TryGetSharedInterface<IBotControllerApi>("botcontroller:api", out _botController);
        _botControllerBridge.SetApi(_botController);
        if (_botController is not null && !_botControllerAvailabilityLogged)
        {
            _botControllerAvailabilityLogged = true;
            Core.LoggerFactory.CreateLogger<Plugin>().LogInformation(
                "Bot Controller shared API injected successfully (ABI {AbiVersion}).", _botController.AbiVersion);
        }
    }

    public override void Load(bool hotReload)
    {
        Core.Configuration.InitializeJsonWithModel<SurfTimerOptions>(
            "config.jsonc",
            SurfTimerOptions.SectionName);
        var options = SurfTimerOptionsLoader.Load(Core);

        var serviceCollection = new ServiceCollection();
        serviceCollection
            .AddSwiftly(Core)
            .AddSingleton(_botControllerBridge)
            .AddSingleton(options)
            .AddSingleton<MigrationRunner>()
            .AddSingleton<RecordRepository>()
            .AddSingleton<PlayerPreferenceRepository>()
            .AddSingleton<ReplayRecorder>()
            .AddSingleton<ReplayPlaybackManager>()
            .AddSingleton<PluginRuntime>()
            .AddSingleton<SurfPlayerManager>()
            .AddSingleton<MapConfigurationProvider>()
            .AddSingleton<MapLifecycle>()
            .AddSingleton<TimerManager>()
            .AddSingleton<TimerCommands>()
            .AddSingleton<PracticeCommands>()
            .AddSingleton<RecordCommands>()
            .AddSingleton<ReplayCommands>()
            .AddSingleton<PreferenceCommands>()
            .AddSingleton<PublicCommands>()
            .AddSingleton<ProfileCommands>()
            .AddSingleton<AdminCommands>()
            .AddSingleton<MapVoteManager>()
            .AddSingleton<FlashingHtmlHudFix>()
            .AddSingleton<HudManager>()
            .AddSingleton<StatusCommands>();

        _services = serviceCollection.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

        _services.GetRequiredService<RecordRepository>().Start();
        _services.GetRequiredService<PlayerPreferenceRepository>().Start();
        _services.GetRequiredService<MapLifecycle>().Start(hotReload);
        _services.GetRequiredService<SurfPlayerManager>().Start(hotReload);
        _services.GetRequiredService<TimerManager>().Start();
        _services.GetRequiredService<ReplayRecorder>().Start();
        _services.GetRequiredService<ReplayPlaybackManager>().Start();
        _services.GetRequiredService<TimerCommands>().Register();
        _services.GetRequiredService<PracticeCommands>().Register();
        _services.GetRequiredService<RecordCommands>().Register();
        _services.GetRequiredService<ReplayCommands>().Register();
        _services.GetRequiredService<PreferenceCommands>().Register();
        _services.GetRequiredService<PublicCommands>().Register();
        _services.GetRequiredService<ProfileCommands>().Register();
        _services.GetRequiredService<AdminCommands>().Register();
        _services.GetRequiredService<MapVoteManager>().Start();
        _services.GetRequiredService<FlashingHtmlHudFix>().Start();
        _services.GetRequiredService<HudManager>().Start();
        _services.GetRequiredService<PluginRuntime>().Start(hotReload);
        _services.GetRequiredService<StatusCommands>().Register();
    }

    public override void Unload()
    {
        if (_services is null)
        {
            return;
        }

        _services.GetService<StatusCommands>()?.Unregister();
        _services.GetService<RecordCommands>()?.Unregister();
        _services.GetService<ReplayCommands>()?.Unregister();
        _services.GetService<PreferenceCommands>()?.Unregister();
        _services.GetService<PublicCommands>()?.Unregister();
        _services.GetService<ProfileCommands>()?.Unregister();
        _services.GetService<AdminCommands>()?.Unregister();
        _services.GetService<MapVoteManager>()?.Stop();
        _services.GetService<HudManager>()?.Stop();
        _services.GetService<FlashingHtmlHudFix>()?.Stop();
        _services.GetService<TimerCommands>()?.Unregister();
        _services.GetService<PracticeCommands>()?.Unregister();
        _services.GetService<TimerManager>()?.Stop();
        _services.GetService<ReplayRecorder>()?.Stop();
        _services.GetService<ReplayPlaybackManager>()?.Stop();
        _services.GetService<SurfPlayerManager>()?.Stop();
        _services.GetService<MapLifecycle>()?.Stop();
        _services.GetService<PlayerPreferenceRepository>()?.Stop();
        _services.GetService<PluginRuntime>()?.Stop();
        _services.Dispose();
        _services = null;
    }
}

internal sealed class PluginRuntime(
    ISwiftlyCore core,
    Replays.BotControllerBridge botController,
    Maps.MapConfigurationProvider mapConfigurations,
    Storage.RecordRepository records,
    Configuration.SurfTimerOptions options,
    ILogger<PluginRuntime> logger)
{
    private bool _started;

    public void Start(bool hotReload)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        logger.LogInformation(
            "SurfTimer {Version} loaded. Hot reload: {HotReload}. Data directory: {DataDirectory}",
            BuildInfo.Version,
            hotReload,
            core.PluginDataDirectory);
        logger.LogInformation("Bot Controller shared API: {Status}.",
            botController.IsAvailable ? $"available (ABI {botController.AbiVersion})" : "unavailable");
        _ = SynchronizeCatalogAsync();
    }

    private async Task SynchronizeCatalogAsync()
    {
        try
        {
            var workshopIds=options.MapVoting.Maps.ToDictionary(value=>value.Name,value=>(string?)value.WorkshopId,StringComparer.OrdinalIgnoreCase);
            var catalog=mapConfigurations.LoadCatalog();
            foreach(var map in catalog)
            {
                workshopIds.TryGetValue(map.MapName,out var workshopId);
                await records.TrackMapMetadataAsync(map.MapName,workshopId,map.Configuration.CheckpointCount??0,
                    map.Configuration.StageCount,map.Configuration.BonusCount,map.Configuration.Tier,map.Configuration.Enabled).ConfigureAwait(false);
            }
            logger.LogInformation("Synchronized {MapCount} catalog map definitions to the shared database.",catalog.Count);
        }
        catch(Exception exception)
        {
            logger.LogError(exception,"Failed to synchronize map catalog metadata.");
        }
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        logger.LogInformation("SurfTimer {Version} unloaded.", BuildInfo.Version);
    }
}
