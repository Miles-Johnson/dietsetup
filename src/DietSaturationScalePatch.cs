using dietsetup.Diet;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Prefix on EntityBehaviorHunger.OnEntityReceiveSaturation -- the "apply" half of the nutrition
/// fold (architecture 5.4). This method has no item/tag parameters, so the multiplier was already
/// resolved and queued elsewhere (DietNutritionMultiplierEatPatch for a standalone eat,
/// DietMealContentNutritionPatch for a meal); this just dequeues and applies it. Only
/// result.Nutrition this task, not the capacity gain scale -- that's phase 4 (task rule 8).
/// </summary>
[HarmonyPatch(typeof(EntityBehaviorHunger), nameof(EntityBehaviorHunger.OnEntityReceiveSaturation))]
public static class DietSaturationScalePatch
{
    [HarmonyPrefix]
    public static bool Prefix(EntityBehaviorHunger __instance, ref float saturation, EnumFoodCategory foodCat, ref float saturationLossDelay, ref float nutritionGainMultiplier)
    {
        if (!DietSetupModSystem.Config.EnableDietSystem) return true;

        if (DietProfileRegistry.TryDequeueNutritionMultiplier(__instance.entity.EntityId, out float nutritionMult))
        {
            nutritionGainMultiplier *= nutritionMult;
        }
        return true;
    }
}
