using System;
using System.Collections.Generic;
using dietsetup.Diet;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Prefix+postfix on the protected BlockMeal.tryFinishEatMeal (string target, same reason as
/// DietEatDoTPatch) -- that patch's meal-path equivalent, since BlockMeal never calls tryEatStop.
/// Double-resolution and ordering details:
/// notes/dietsetup-patch-internals.md#meal-eat-dot-patch--dietmealeatdotpatchcs.
/// </summary>
[HarmonyPatch(typeof(BlockMeal), "tryFinishEatMeal")]
public static class DietMealEatDoTPatch
{
    [HarmonyPrefix]
    public static void Prefix(EntityAgent byEntity)
    {
        DietProfileRegistry.ClearPendingDoT(byEntity.EntityId);
    }

    [HarmonyPostfix]
    public static void Postfix(EntityAgent byEntity, bool __result)
    {
        if (byEntity is not EntityPlayer || !DietSetupModSystem.Config.EnableDietSystem)
        {
            return;
        }

        // tryFinishEatMeal returns false at BlockMeal.cs:302 (e.g. secondsUsed < 1.45, client
        // side, no valid contents) *after* the discarded validation call already resolved and
        // queued -- __result, not a reimplemented timing check, is the signal a bite was consumed.
        if (!__result || byEntity.World is not IServerWorldAccessor)
        {
            return;
        }

        // GetIngredientStackNutritionProperties resolves twice per eat (once discarded, once in
        // Consume), so a single reacting ingredient queues two identical PendingDoT entries per
        // bite -- collapsed here rather than patched at the source. Multi-ingredient semantics and
        // full reasoning: notes/dietsetup-patch-internals.md#meal-dot-collapse--dietmealeatdotpatchcs.
        var applied = new HashSet<(float Health, float DurationSec, int Ticks)>();
        foreach (DietReaction reaction in DietProfileRegistry.TakePendingDoT(byEntity.EntityId))
        {
            if (!applied.Add((reaction.Health, reaction.DurationSec, reaction.Ticks)))
            {
                continue;
            }

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
