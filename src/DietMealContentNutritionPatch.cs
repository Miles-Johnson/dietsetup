using System;
using System.Collections.Generic;
using System.Linq;
using dietsetup.Diet;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Prefix fully replacing the static BlockMeal.GetContentNutritionProperties overload -- the only
/// place with whole-bowl visibility. Explicit argument types in HarmonyPatch disambiguate from the
/// 3-param instance overload (BlockMeal.cs:574), a thin wrapper around this one that would otherwise
/// make the target ambiguous.
/// Two responsibilities merged into one patch rather than left as two Harmony patches racing on the
/// same method: (1) vanilla computes spoilState correctly per ingredient but hands
/// FoodSpoilageSatLossMul/HealthLossMul the outer meal stack instead of that ingredient's own stack
/// (a case-only "itemstack" vs "itemStack" bug) -- fixed here by passing the correct stack, so
/// DietSpoilageSatietyPatch/DietSpoilageHealthPatch resolve each ingredient's own tags naturally;
/// (2) drains MealIngredientContext (populated by DietMealNutritionPatch's postfix as the loop below
/// calls GetIngredientStackNutritionProperties per ingredient), groups queued reactions by shape, and
/// queues one satiety-weighted DoT per shape -- this used to be a separate Prefix+Postfix pair on
/// this same method; folded in here so clear/populate/drain always run in that exact order with no
/// cross-patch Harmony priority to get right.
/// No re-entrancy guard: each FoodSpoilageSatLossMul/HealthLossMul call below is independent and
/// stateless (DietResolver.Resolve reads no shared mutable state) -- one ingredient stack per call,
/// calls run one after another, never nested.
/// Risk: this is a full-body replacement of a vanilla method, not a behavior delta -- it will drift
/// silently on the next Vintage Story update, and any other mod prefixing this exact overload races
/// it (Harmony runs every prefix, but only one's __result and skip-original decision can end up
/// observed, in an order this mod does not control). See README known limitations.
/// </summary>
[HarmonyPatch(typeof(BlockMeal), nameof(BlockMeal.GetContentNutritionProperties),
    new[] { typeof(IWorldAccessor), typeof(ItemSlot), typeof(ItemStack[]), typeof(EntityAgent), typeof(bool), typeof(float), typeof(float) })]
public static class DietMealContentNutritionPatch
{
    [HarmonyPrefix]
    public static bool Prefix(IWorldAccessor world, ItemSlot inSlot, ItemStack?[]? contentStacks, EntityAgent? forEntity, bool mulWithStacksize, float nutritionMul, float healthMul, ref FoodNutritionProperties[] __result)
    {
        if (!DietSetupModSystem.Config.EnableDietSystem) return true;

        if (forEntity is EntityPlayer) DietProfileRegistry.ClearMealIngredientContext(forEntity.EntityId);

        var results = new List<FoodNutritionProperties>();
        ItemStack? mealStack = inSlot.Itemstack;
        if (contentStacks == null || mealStack == null)
        {
            __result = results.ToArray();
            return false;
        }

        bool timeFrozen = mealStack.Attributes.GetBool("timeFrozen");
        string recipeCode = mealStack.Attributes.GetString("recipeCode");
        List<CookingRecipeIngredient>? ingredients = world.Api.GetCookingRecipe(recipeCode)?.Ingredients?.Select(ingred => ingred.Clone()).ToList();

        foreach (ItemStack? ingredientStack in contentStacks)
        {
            if (ingredientStack == null) continue;

            float quantity = ingredientStack.StackSize;
            ItemStack nutriStack = ingredientStack.Clone();
            nutriStack.StackSize = 1;
            if (!mulWithStacksize)
            {
                nutriStack.StackSize = (int)(BlockLiquidContainerBase.GetContainableProps(nutriStack)?.ItemsPerLitre ?? 1f);
                CookingRecipeIngredient? matched = ingredients?.FirstOrDefault(ing => ing.Matches(nutriStack));
                if (matched != null)
                {
                    quantity = matched.GetMatchingStack(nutriStack)?.StackSize ?? 1;
                    nutriStack.StackSize = (int)(nutriStack.StackSize * matched.PortionSizeLitres);
                    matched.MaxQuantity--;
                    if (matched.MaxQuantity == 0) ingredients!.Remove(matched);
                }
                else
                {
                    quantity = 1f;
                }
            }

            FoodNutritionProperties? ingredientProps = BlockMeal.GetIngredientStackNutritionProperties(world, nutriStack, forEntity);
            if (ingredientProps == null) continue;

            FoodNutritionProperties props = ingredientProps.Clone();
            float spoilState = 0f;
            if (!timeFrozen)
            {
                var dummySlot = new DummySlot(ingredientStack, inSlot.Inventory);
                spoilState = ingredientStack.Collectible.UpdateAndGetTransitionState(world, dummySlot, EnumTransitionType.Perish)?.TransitionLevel ?? 0f;
            }

            float satMul = GlobalConstants.FoodSpoilageSatLossMul(spoilState, ingredientStack, forEntity);
            float healthLossMul = GlobalConstants.FoodSpoilageHealthLossMul(spoilState, ingredientStack, forEntity);
            props.Satiety *= satMul * nutritionMul * quantity;
            props.Health *= healthLossMul * healthMul * quantity;
            props.Intoxication *= quantity;
            props.Psychedelic *= quantity;
            results.Add(props);
        }

        __result = results.ToArray();

        if (forEntity is EntityPlayer) QueueMealReactionDoT(forEntity);

        return false;
    }

    /// <summary>Drains MealIngredientContext, groups by reaction shape, and queues one
    /// satiety-weighted DoT per shape -- ported unchanged from the former standalone
    /// DietMealContentNutritionPatch postfix.</summary>
    private static void QueueMealReactionDoT(EntityAgent forEntity)
    {
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
