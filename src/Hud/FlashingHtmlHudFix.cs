using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;

namespace SurfTimer.Hud;

/// <summary>
/// SwiftlyS2 adaptation of girlglock/CS2FlashingHtmlHudFix's game-rules workaround.
/// Prevents CS2 center-HTML messages from flashing during periodic refreshes.
/// </summary>
internal sealed class FlashingHtmlHudFix(
    ISwiftlyCore core,
    ILogger<FlashingHtmlHudFix> logger)
{
    private bool _started;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        core.Event.OnTick += OnTick;
        logger.LogInformation("Flashing center-HTML HUD workaround enabled.");
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        core.Event.OnTick -= OnTick;
        _started = false;
    }

    private void OnTick()
    {
        var gameRules = core.EntitySystem.GetGameRules();
        if (gameRules is not null &&
            gameRules.RestartRoundTime.Value < core.Engine.GlobalVars.CurrentTime)
        {
            gameRules.GameRestart = true;
        }
    }
}
