using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SurfTimer.Maps;
using SwiftlyS2.Shared;

namespace SurfTimer.Timing;

public static class RulesetFingerprint
{
    public static readonly IReadOnlySet<string> MovementVariables = new HashSet<string>(StringComparer.Ordinal)
    {
        "sv_airaccelerate", "sv_accelerate", "sv_air_max_wishspeed", "sv_gravity", "sv_friction",
        "sv_maxvelocity", "sv_autobunnyhopping", "sv_enablebunnyhopping", "sv_staminamax", "host_timescale", "sv_cheats"
    };

    public static string Capture(ISwiftlyCore core, MapLifecycle maps)
    {
        var map = maps.Current;
        if (map is null) return "no-map";
        var config = map.Configuration;
        var movement = MovementVariables.Order(StringComparer.Ordinal).ToDictionary(name => name,
            name => core.ConVar.FindAsString(name)?.ValueAsString ?? "unavailable");
        var payload = JsonSerializer.Serialize(new
        {
            protocol = "surftimer-entry-stage-v1", map.Name, map.WorkshopId,
            config.StartTrigger, config.EndTrigger, config.CheckpointPrefix,
            checkpoints = maps.CheckpointCount, config.StagePrefix, config.StageCount,
            config.BonusPrefix, config.BonusCount, config.MaxVelocity,
            cancelTriggers = config.CancelTriggers.Order(StringComparer.Ordinal).ToArray(), movement
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
