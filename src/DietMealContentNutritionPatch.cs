using System.Collections.Generic;
using System.Linq;
using dietsetup.Diet;
using dietsetup.Rules;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Full-body replacement of BlockMeal.GetContentNutritionProperties (whole-bowl visibility has no
/// other patch point -- see README known limitations for the drift risk that implies). Explicit
/// argument types in HarmonyPatch disambiguate from the 3-param instance overload (BlockMeal.cs:574),
/// a thin wrapper around this one that would otherwise make the target ambiguous.
///
/// Reproduces vanilla's per-ingredient loop (BlockMeal.cs:459-521) with one fix (landmine I):
/// vanilla computes spoilState from each ingredient's own stack but then calls
/// FoodSpoilageSatLossMul/HealthLossMul with the *bowl's* stack, not the ingredient's -- wrong
/// collectible identity for our tag-based resolution. Below, contentStack (the ingredient) goes
/// into both calls instead of inSlot.Itemstack (the bowl).
///
/// Calling BlockMeal.GetIngredientStackNutritionProperties and GlobalConstants.FoodSpoilage*Mul
/// here goes through their own Harmony patches like any other call -- this method does not
/// duplicate their resolve logic, only vanilla's surrounding arithmetic plus the one bug fix.
///
/// Drains DietMealNutritionPatch's per-ingredient nutrition-multiplier hand-off (in the same loop
/// order) into DietProfileRegistry's queue for DietSaturationScalePatch to consume -- clearing
/// that queue first, because this method fires twice per real eat (landmine G: once for a
/// UI/interaction check, once from the actual Consume() call) and a stale first-call queue must
/// not survive into the second.
///
/// Task 2/3: also peeks DietSpoilageResolution's cache (populated by the FoodSpoilageSatLossMul
/// call above) for each ingredient's Verdict and Effects, queues them in PendingMealEffects for
/// DietMealEffectFirePatch, and -- if any ingredient resolved Inedible -- returns __result = null
/// instead of the built list, reusing vanilla's own null-check refusal path (see the Prefix body).
/// </summary>
[HarmonyPatch(typeof(BlockMeal), nameof(BlockMeal.GetContentNutritionProperties),
    new[] { typeof(IWorldAccessor), typeof(ItemSlot), typeof(ItemStack[]), typeof(EntityAgent), typeof(bool), typeof(float), typeof(float) })]
public static class DietMealContentNutritionPatch
{
    [HarmonyPrefix]
    public static bool Prefix(IWorldAccessor world, ItemSlot inSlot, ItemStack?[]? contentStacks, EntityAgent? forEntity, bool mulWithStacksize, float nutritionMul, float healthMul, ref FoodNutritionProperties[] __result)
    {
        var list = new List<FoodNutritionProperties>();
        var mealEffects = new List<DietResolveResult>();
        bool anyInedible = false;
        ItemStack? bowlStack = inSlot.Itemstack;

        if (contentStacks != null && bowlStack != null)
        {
            bool timeFrozen = bowlStack.Attributes.GetBool("timeFrozen");
            string recipeCode = bowlStack.Attributes.GetString("recipeCode");
            List<CookingRecipeIngredient>? recipeIngredients = world.Api.GetCookingRecipe(recipeCode)?.Ingredients?
                .Select(ing => ing.Clone()).ToList();

            foreach (ItemStack? contentStack in contentStacks)
            {
                if (contentStack == null) continue;

                float quantity = contentStack.StackSize;
                ItemStack nutriStack = contentStack.Clone();
                nutriStack.StackSize = 1;

                if (!mulWithStacksize)
                {
                    nutriStack.StackSize = (int)(BlockLiquidContainerBase.GetContainableProps(nutriStack)?.ItemsPerLitre ?? 1f);
                    CookingRecipeIngredient? matched = recipeIngredients?.FirstOrDefault(ing => ing.Matches(nutriStack));
                    if (matched != null)
                    {
                        quantity = matched.GetMatchingStack(nutriStack)?.StackSize ?? 1;
                        nutriStack.StackSize = (int)(nutriStack.StackSize * matched.PortionSizeLitres);
                        matched.MaxQuantity--;
                        if (matched.MaxQuantity == 0) recipeIngredients!.Remove(matched);
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
                    var dummySlot = new DummySlot(contentStack, inSlot.Inventory);
                    spoilState = contentStack.Collectible.UpdateAndGetTransitionState(world, dummySlot, EnumTransitionType.Perish)?.TransitionLevel ?? 0f;
                }

                float satLossMul = GlobalConstants.FoodSpoilageSatLossMul(spoilState, contentStack, forEntity);
                float healthLossMul = GlobalConstants.FoodSpoilageHealthLossMul(spoilState, contentStack, forEntity);
                props.Satiety *= satLossMul * nutritionMul * quantity;
                props.Health *= healthLossMul * healthMul * quantity;
                props.Intoxication *= quantity;
                props.Psychedelic *= quantity;
                list.Add(props);

                // Peeks the resolve FoodSpoilageSatLossMul's own postfix (DietSpoilageSatietyPatch)
                // just cached for this exact (contentStack, spoilState) pair -- the same fold that
                // set satLossMul above, not a second resolve for this ingredient (task 2).
                if (DietSpoilageResolution.TryGetLastResolved(contentStack, spoilState, out DietResolveResult ingredientResolved))
                {
                    mealEffects.Add(ingredientResolved);
                    if (ingredientResolved.Verdict == DietVerdict.Inedible) anyInedible = true;
                }
            }
        }

        if (forEntity != null)
        {
            DietProfileRegistry.ClearNutritionMultiplierQueue(forEntity.EntityId);
            foreach (float nutritionMult in MealIngredientNutritionHandoff.TakeAll(forEntity.EntityId))
            {
                DietProfileRegistry.EnqueueNutritionMultiplier(forEntity.EntityId, nutritionMult);
            }

            // Replaced, not merged, every call -- see PendingMealEffects' doc for why that's what
            // makes this safe against landmine C's double invocation.
            PendingMealEffects.Replace(forEntity.EntityId, mealEffects);
        }

        // Verdict Inedible refuses the eat (7.4): null reproduces vanilla's own "nothing to eat"
        // signal (BlockMeal.cs:225/136/102 all null-check this method's result), so
        // tryHeldBeginEatMeal never starts the interaction and tryFinishEatMeal never calls Consume
        // -- no consumption, no satiety. mealEffects (and therefore this ingredient's damage effect,
        // if any) is still queued above: architecture 4.2's own example rule pairs verdict:inedible
        // with a damage effect, so refusing the eat must not also silently drop it.
        __result = anyInedible ? null! : list.ToArray();
        return false;
    }
}
