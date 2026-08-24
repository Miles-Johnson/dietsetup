using dietsetup.Diet;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Postfix on BlockMeal.GetIngredientStackNutritionProperties -- restores grant/reaction
/// resolution for meal ingredients, which mostly resolve from JSON and skip the already-patched
/// CollectibleObject path. Full context:
/// notes/dietsetup-patch-internals.md#meal-nutrition-patch--dietmealnutritionpatchcs.
/// </summary>
[HarmonyPatch(typeof(BlockMeal), nameof(BlockMeal.GetIngredientStackNutritionProperties))]
public static class DietMealNutritionPatch
{
    [HarmonyPostfix]
    public static void Postfix(ItemStack? stack, EntityAgent? forEntity, ref FoodNutritionProperties? __result)
    {
        if (stack == null || forEntity is not EntityPlayer || !DietSetupModSystem.Config.EnableDietSystem)
        {
            return;
        }

        __result = DietProfileRegistry.ResolveNutritionProperties(
            forEntity.Api, forEntity, stack.Collectible, __result, DietSetupModSystem.Config.DefaultProfileId,
            queueReaction: false, out DietReaction? queuedReaction, out float notionalSatiety, out bool reactionSourced);
        DietProfileRegistry.AddMealIngredientContext(forEntity.EntityId, notionalSatiety, queuedReaction, reactionSourced);
    }
}
