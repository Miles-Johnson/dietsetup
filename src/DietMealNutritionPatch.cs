using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Postfix on BlockMeal.GetIngredientStackNutritionProperties -- restores grant/reaction
/// resolution for meal ingredients, which mostly resolve from JSON and skip the already-patched
/// CollectibleObject path. No-op until phase 3; DietProfileRegistry.AddMealIngredientContext
/// (entityId, satiety) survives for the rewritten body to hand off to DietMealContentNutritionPatch.
/// </summary>
[HarmonyPatch(typeof(BlockMeal), nameof(BlockMeal.GetIngredientStackNutritionProperties))]
public static class DietMealNutritionPatch
{
    [HarmonyPostfix]
    public static void Postfix(ItemStack? stack, EntityAgent? forEntity, ref FoodNutritionProperties? __result)
    {
    }
}
