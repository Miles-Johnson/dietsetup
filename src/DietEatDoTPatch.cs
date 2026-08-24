using System;
using dietsetup.Diet;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace dietsetup;

/// <summary>
/// Prefix+postfix on the protected CollectibleObject.tryEatStop (string target: protected members
/// aren't nameof-able). Applies a reaction's deferred DoT portion by zeroing
/// FoodNutritionProperties.Health so vanilla's instant-damage branch never fires. Ordering and
/// decompiled-verification details: notes/dietsetup-patch-internals.md#eat-dot-patch--dieteatdotpatchcs.
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
