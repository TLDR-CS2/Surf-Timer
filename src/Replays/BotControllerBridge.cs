using BotControllerApi;

namespace SurfTimer.Replays;

public sealed class BotControllerBridge
{
    public IBotControllerApi? Api { get; private set; }
    public bool IsAvailable => Api is not null;
    public int? AbiVersion => Api?.AbiVersion;

    public void SetApi(IBotControllerApi? api) => Api = api;
}
