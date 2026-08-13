using System;
using dietsetup.Diet;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace dietsetup;

/// <summary>
/// Patches the protected CollectibleObject.tryEatStop (string-named target -- protected members
/// can't be referenced via nameof from here) to apply the damage-over-time portion of a reaction
/// that DietProfileRegistry.ResolveNutritionProperties deferred, by zeroing
/// FoodNutritionProperties.Health so vanilla's own instant-damage branch there never fires.
///
/// tryEatStop calls GetNutritionProperties internally as its first line (on both sides, even
/// though only the server acts on the result), which is what populates
/// DietProfileRegistry's pending-DoT entry for this entity. The Prefix clears any stale entry
/// (e.g. left over from a tooltip hover) before that happens, so the Postfix only ever sees an
/// entry that this exact eat produced.
///
/// Verified against reference/upstream/vsapi/Common/Collectible/Collectible.cs:1852-1912 --
/// mirrors vanilla's own Duration/TicksPerDuration/DamageOverTimeTypeEnum usage there, just
/// sourcing the values dynamically (per eating profile) instead of from the item's static
/// eatHealthEffectDurationSec/eatHealthEffectTicks attributes.
/// </summary>
[HarmonyPatch(typeof(CollectibleObject), "tryEatStop")]
public static class DietEatDoTPatch
{
    [HarmonyPrefix]
    public static void Prefix(EntityAgent byEntity)
    {
        DietProfileRegistry.ClearPendingDoT(byEntity.EntityId);
    }

    [HarmonyPostfix]
    public static void Postfix(float secondsUsed, EntityAgent byEntity)
    {
        if (byEntity is not EntityPlayer || !DietSetupModSystem.Config.EnableDietSystem)
        {
            return;
        }

        // Mirrors vanilla's own guard in tryEatStop -- only the server ever actually applies damage.
        if (byEntity.World is not IServerWorldAccessor || secondsUsed < 0.95f)
        {
            return;
        }

        // Standalone eating resolves nutrition exactly once per tryEatStop call (unlike
        // BlockMeal.tryFinishEatMeal, see DietMealEatDoTPatch), so this list holds at most one
        // entry -- the foreach reproduces the old single-value behavior exactly.
        foreach (DietReaction reaction in DietProfileRegistry.TakePendingDoT(byEntity.EntityId))
        {
            byEntity.ReceiveDamage(new DamageSource
            {
                Source = EnumDamageSource.Internal,
                Type = EnumDamageType.Poison,
                Duration = TimeSpan.FromSeconds(reaction.DurationSec),
                TicksPerDuration = Math.Max(1, reaction.Ticks),
                DamageOverTimeTypeEnum = EnumDamageOverTimeEffectType.Poison
            }, Math.Abs(reaction.Health));
        }
    }
}
