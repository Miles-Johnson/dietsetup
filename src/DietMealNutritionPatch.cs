using dietsetup.Diet;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Postfix on BlockMeal.GetIngredientStackNutritionProperties -- meal ingredients resolve
/// nutrition straight from the nutritionPropsWhenInMeal JSON attribute (or a liquid-container
/// equivalent) and only fall back to CollectibleObject.GetNutritionProperties as a last resort, so
/// DietNutritionPropertiesPatch never sees most real meal ingredients. This restores the same
/// per-tag multiplier / grant / reaction resolution for them, via
/// DietProfileRegistry.ResolveNutritionProperties -- its existing Processed guard already covers
/// the one call path that goes through the already-patched CollectibleObject.GetNutritionProperties
/// fallback. Called with queueReaction: false, since a single ingredient's reaction magnitude isn't
/// meaningful on its own for a meal -- DietMealContentNutritionPatch sees the whole bowl at once and
/// computes/queues one weighted DoT per reaction shape from the per-ingredient results this pushes
/// onto DietProfileRegistry's MealIngredientContext buffer. See DietMealEatDoTPatch for the DoT
/// consumer this ultimately feeds and the double-resolution wrinkle in tryFinishEatMeal.
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
