using dietsetup.Rules;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Postfix on BlockMeal.GetIngredientStackNutritionProperties -- one ingredient at a time, mostly
/// resolved from JSON, skipping the already-patched CollectibleObject path. Folds satiety via the
/// same DietSatietyFold as the other two GetNutritionProperties family targets, and hands the
/// resolved nutrition-gain multiplier off to DietMealContentNutritionPatch (via
/// MealIngredientNutritionHandoff) since this method's own return type has no field for it.
/// </summary>
[HarmonyPatch(typeof(BlockMeal), nameof(BlockMeal.GetIngredientStackNutritionProperties))]
public static class DietMealNutritionPatch
{
    [HarmonyPostfix]
    public static void Postfix(ItemStack? stack, EntityAgent? forEntity, ref FoodNutritionProperties? __result)
    {
        if (stack?.Collectible == null) return;

        DietSatietyFold.TryFold(stack.Collectible, forEntity, ref __result, out DietResolveResult? resolved);
        if (resolved != null && forEntity != null)
        {
            MealIngredientNutritionHandoff.Add(forEntity.EntityId, resolved.Value.Nutrition);
        }
    }
}
