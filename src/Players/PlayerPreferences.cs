namespace SurfTimer.Players;

public sealed record PlayerPreferences(
    bool HudEnabled = true,
    bool SpeedEnabled = true,
    bool StatusEnabled = true,
    bool KeysEnabled = true,
    bool SoundsEnabled = true,
    bool ReplayHudEnabled = true);
