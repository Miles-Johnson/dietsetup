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
/// Patches the protected BlockMeal.tryFinishEatMeal (string-named target -- protected members
/// can't be referenced via nameof from here) to apply the damage-over-time portion of any
/// reactions DietMealNutritionPatch's resolution deferred to DietProfileRegistry's PendingDoT
/// queue. BlockMeal never calls CollectibleObject.tryEatStop, so DietEatDoTPatch has no consumer
/// during meal eating -- this is that consumer's meal-path equivalent.
///
/// tryFinishEatMeal resolves the meal's nutrition twice per eat-completion: once at line ~301 for
/// a null-check whose result is discarded, once for real inside Consume. Both calls reach
/// GetIngredientStackNutritionProperties, and for the common nutritionPropsWhenInMeal JSON path a
/// fresh FoodNutritionProperties is deserialized each time, so ResolveNutritionProperties's
/// Processed guard (keyed by reference) does not catch the duplicate -- a single reacting
/// ingredient genuinely queues two identical PendingDoT entries per bite. Rather than chase that
/// ordering dependency by clearing mid-flow inside Consume, the Postfix collapses duplicate
/// entries before applying -- see the comment at the collapse site for why and what that means for
/// multi-ingredient bites.
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

        // tryFinishEatMeal itself returns false at BlockMeal.cs:302 (e.g. secondsUsed < 1.45,
        // client side, no valid contents) *after* the discarded validation call has already
        // resolved and queued -- __result, not a reimplemented timing check, is the signal that a
        // bite was actually consumed this call.
        if (!__result || byEntity.World is not IServerWorldAccessor)
        {
            return;
        }

        // GetIngredientStackNutritionProperties (BlockMeal.cs:605) is resolved twice per
        // eat-completion: once at tryFinishEatMeal:301 for a discarded null-check, once for real
        // inside Consume (tryFinishEatMeal:323 -> Consume:503). Since path (b) -- the common
        // nutritionPropsWhenInMeal JSON case -- deserializes a fresh FoodNutritionProperties on
        // every call, ResolveNutritionProperties's Processed guard (keyed by reference) does not
        // catch this: a single reacting ingredient genuinely queues two identical PendingDoT
        // entries per bite. Collapsing here rather than clearing mid-flow keeps this correct even
        // if vanilla's call order changes later -- worst case it under-applies, never
        // double-applies.
        //
        // Phase-one semantics, not a per-ingredient guarantee: reactions are resolved per profile
        // category, so two different incompatible ingredients in the same bowl will normally
        // produce an identical (Health, DurationSec, Ticks) tuple too, and will also collapse into
        // a single application. This applies at most one DoT per bite per distinct reaction shape,
        // not one per reacting ingredient. Per-ingredient weighting is phase two, on
        // GetContentNutritionProperties.
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
