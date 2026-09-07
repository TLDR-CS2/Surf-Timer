using SurfTimer.Players;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Sounds;

namespace SurfTimer.Sounds;

public static class FinishSounds
{
    // Call on the game thread, after confirming the same player session and a saved result.
    public static void Play(IPlayer player, SurfPlayerSession session, bool personalBest)
    {
        if (!session.Preferences.SoundsEnabled || session.IsBot || player.SessionId != session.SessionId) return;
        var sound = new SoundEvent("UIPanorama.generic_button_press", 0.7f, personalBest ? 1.25f : 1f);
        sound.Recipients.AddRecipient(player.PlayerID);
        sound.Emit();
    }
}
