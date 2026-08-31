using dietsetup.Binding;
using dietsetup.Diet;
using dietsetup.Rules;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Prefix on OnEntityReceiveSaturation, the nutrition fold's apply site (5.4): combines the rule
/// multiplier dequeued from the eat/meal gather patches with capacity's gain scale (1/capacity, 0
/// at capacity 0 -- 5.5), read from the cached CompiledDiet. The full-stomach guard that used to
/// gate the resulting write is removed at the IL level by DietNutritionGuardTranspiler on this
/// same method, so vanilla's own body always applies nutritionGainMultiplier exactly once.
/// </summary>
[HarmonyPatch(typeof(EntityBehaviorHunger), nameof(EntityBehaviorHunger.OnEntityReceiveSaturation))]
public static class DietSaturationScalePatch
{
    [HarmonyPrefix]
    public static bool Prefix(EntityBehaviorHunger __instance, EnumFoodCategory foodCat, ref float nutritionGainMultiplier)
    {
        if (!DietSetupModSystem.Config.EnableDietSystem) return true;

        if (DietProfileRegistry.TryDequeueNutritionMultiplier(__instance.entity.EntityId, out float nutritionMult))
        {
            nutritionGainMultiplier *= nutritionMult;
        }

        float gainScale = 1f;
        CompiledDiet? diet = DietIdResolver.ResolveDiet(__instance.entity);
        if (diet != null && diet.Categories.TryGetValue(foodCat, out CompiledCategory category))
        {
            gainScale = category.NutritionGainScale;
        }
        nutritionGainMultiplier *= gainScale;

        return true;
    }
}
