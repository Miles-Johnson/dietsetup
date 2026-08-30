using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// No-op until phase 3. Explicit argument types in HarmonyPatch disambiguate from the 3-param
/// instance overload (BlockMeal.cs:574), a thin wrapper around this one that would otherwise make
/// the target ambiguous -- keep the array even though the body below does nothing.
/// The rewritten body will again be a full-body replacement of a vanilla method (whole-bowl
/// visibility has no other patch point), merging per-ingredient spoilage resolution with draining
/// DietMealNutritionPatch's MealIngredientContext hand-off -- see README known limitations for the
/// drift risk that implies.
/// </summary>
[HarmonyPatch(typeof(BlockMeal), nameof(BlockMeal.GetContentNutritionProperties),
    new[] { typeof(IWorldAccessor), typeof(ItemSlot), typeof(ItemStack[]), typeof(EntityAgent), typeof(bool), typeof(float), typeof(float) })]
public static class DietMealContentNutritionPatch
{
    [HarmonyPrefix]
    public static bool Prefix(IWorldAccessor world, ItemSlot inSlot, ItemStack?[]? contentStacks, EntityAgent? forEntity, bool mulWithStacksize, float nutritionMul, float healthMul, ref FoodNutritionProperties[] __result)
    {
        return true;
    }
}
