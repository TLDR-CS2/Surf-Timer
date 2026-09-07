using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace SurfTimer.Replays;

/// <summary>Temporarily unequip and hide weapons without deleting the viewer's inventory.</summary>
internal sealed class ReplayWeaponState(CCSPlayerPawn pawn)
{
    private const uint NoDraw = 1 << 5;
    private readonly CHandle<CCSPlayerPawn> _pawn = new() { Value = pawn };
    private readonly CHandle<CBasePlayerWeapon> _active = pawn.WeaponServices?.ActiveWeapon ?? CHandle<CBasePlayerWeapon>.Invalid;
    private readonly bool _preventPickup = pawn.WeaponServices?.PreventWeaponPickup ?? false;
    private readonly Dictionary<uint, (CHandle<CBasePlayerWeapon> Handle, bool Hidden)> _weapons = [];

    public void Apply()
    {
        if (!_pawn.IsValid || _pawn.Value is not { IsValid: true } owner || owner.WeaponServices is not { } services) return;
        services.PreventWeaponPickup = true;
        foreach (var weapon in services.MyValidWeapons)
        {
            var handle = new CHandle<CBasePlayerWeapon> { Value = weapon };
            _weapons.TryAdd(handle.Raw, (handle, (weapon.Effects & NoDraw) != 0));
            if ((weapon.Effects & NoDraw) == 0)
            {
                weapon.Effects |= NoDraw;
                weapon.EffectsUpdated();
            }
        }
        if (services.ActiveWeapon.IsValid)
        {
            services.ActiveWeapon = CHandle<CBasePlayerWeapon>.Invalid;
            services.ActiveWeaponUpdated();
        }
    }

    public void Restore()
    {
        foreach (var (handle, hidden) in _weapons.Values)
        {
            if (!handle.IsValid || handle.Value is not { IsValid: true } weapon) continue;
            weapon.Effects = hidden ? weapon.Effects | NoDraw : weapon.Effects & ~NoDraw;
            weapon.EffectsUpdated();
        }
        if (!_pawn.IsValid || _pawn.Value is not { IsValid: true } owner || owner.WeaponServices is not { } services) return;
        services.PreventWeaponPickup = _preventPickup;
        if (_active.IsValid && services.MyValidWeapons.Any(weapon => weapon.Index == _active.EntityIndex))
        {
            services.ActiveWeapon = _active;
            services.ActiveWeaponUpdated();
        }
    }
}
