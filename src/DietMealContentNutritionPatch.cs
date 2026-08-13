using System;
using System.Collections.Generic;
using dietsetup.Diet;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Prefix+postfix on the static BlockMeal.GetContentNutritionProperties overload (the one that
/// builds the per-ingredient FoodNutritionProperties array vanilla ultimately sums in Consume) --
/// the only place with whole-bowl visibility. DietMealNutritionPatch resolves one ingredient at a
/// time and can't tell how much of the meal a reacting ingredient actually is; this patch drains
/// the per-ingredient results DietMealNutritionPatch pushed onto DietProfileRegistry's
/// MealIngredientContext buffer, groups them by reaction shape, and queues exactly one
/// satiety-weighted DoT per shape -- replacing the old "one full-magnitude hit regardless of how
/// much of the bowl reacted" behavior. DietMealEatDoTPatch's drain/collapse logic is unchanged and
/// still needed: GetContentNutritionProperties is called twice per real eat (once for a discarded
/// validation check, once inside Consume), so this postfix runs twice per bite and queues two
/// identical entries per shape -- the collapse there absorbs that exactly as it did before.
///
/// Explicit argument types in the HarmonyPatch attribute disambiguate from the 3-param instance
/// overload (BlockMeal.cs:660) of the same name, which just delegates into this one.
/// </summary>
[HarmonyPatch(typeof(BlockMeal), nameof(BlockMeal.GetContentNutritionProperties),
    new[] { typeof(IWorldAccessor), typeof(ItemSlot), typeof(ItemStack[]), typeof(EntityAgent), typeof(bool), typeof(float), typeof(float) })]
public static class DietMealContentNutritionPatch
{
    [HarmonyPrefix]
    public static void Prefix(EntityAgent? forEntity)
    {
        if (forEntity is not EntityPlayer) return;
        DietProfileRegistry.ClearMealIngredientContext(forEntity.EntityId);
    }

    [HarmonyPostfix]
    public static void Postfix(EntityAgent? forEntity)
    {
        if (forEntity is not EntityPlayer || !DietSetupModSystem.Config.EnableDietSystem) return;

        List<(float NotionalSatiety, DietReaction? Reaction, bool ReactionSourced)> buffer = DietProfileRegistry.TakeMealIngredientContext(forEntity.EntityId);

        var reactingSatietyByShape = new Dictionary<(float Health, float DurationSec, int Ticks), float>();
        var reactionByShape = new Dictionary<(float Health, float DurationSec, int Ticks), (DietReaction Reaction, bool ReactionSourced)>();
        float totalSatiety = 0f;

        foreach ((float notionalSatiety, DietReaction? reaction, bool reactionSourced) in buffer)
        {
            totalSatiety += notionalSatiety;
            if (reaction == null) continue;

            (float Health, float DurationSec, int Ticks) shape = (reaction.Health, reaction.DurationSec, reaction.Ticks);
            reactingSatietyByShape[shape] = reactingSatietyByShape.GetValueOrDefault(shape) + notionalSatiety;
            reactionByShape[shape] = (reaction, reactionSourced);
        }

        if (totalSatiety <= 0f) return;

        foreach (KeyValuePair<(float Health, float DurationSec, int Ticks), float> entry in reactingSatietyByShape)
        {
            (DietReaction reaction, bool reactionSourced) = reactionByShape[entry.Key];
            float share = Math.Min(1f, entry.Value / totalSatiety);
            float scaled = reaction.Health * share;
            float weighted = reactionSourced ? DietProfileRegistry.ClampReactionMagnitude(forEntity, scaled) : scaled;
            DietProfileRegistry.AddPendingDoT(forEntity.EntityId, new DietReaction { Health = weighted, DurationSec = reaction.DurationSec, Ticks = reaction.Ticks });
        }
    }
}
