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
/// </summary>
[HarmonyPatch(typeof(BlockMeal), nameof(BlockMeal.GetContentNutritionProperties),
    new[] { typeof(IWorldAccessor), typeof(ItemSlot), typeof(ItemStack[]), typeof(EntityAgent), typeof(bool), typeof(float), typeof(float) })]
public static class DietMealContentNutritionPatch
{
    [HarmonyPrefix]
    public static bool Prefix(IWorldAccessor world, ItemSlot inSlot, ItemStack?[]? contentStacks, EntityAgent? forEntity, bool mulWithStacksize, float nutritionMul, float healthMul, ref FoodNutritionProperties[] __result)
    {
        var list = new List<FoodNutritionProperties>();
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
            }
        }

        if (forEntity != null)
        {
            DietProfileRegistry.ClearNutritionMultiplierQueue(forEntity.EntityId);
            foreach (float nutritionMult in MealIngredientNutritionHandoff.TakeAll(forEntity.EntityId))
            {
                DietProfileRegistry.EnqueueNutritionMultiplier(forEntity.EntityId, nutritionMult);
            }
        }

        __result = list.ToArray();
        return false;
    }
}
