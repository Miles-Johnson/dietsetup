using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Prefix on EntityBehaviorHunger.OnEntityReceiveSaturation -- applies the nutrition-gain scale
/// (architecture.md section 5.4). No-op until phase 3; DietProfileRegistry's nutrition-multiplier
/// queue (TryDequeueNutritionMultiplier etc.) survives for the rewritten body to consume.
/// </summary>
[HarmonyPatch(typeof(EntityBehaviorHunger), nameof(EntityBehaviorHunger.OnEntityReceiveSaturation))]
public static class DietSaturationScalePatch
{
    [HarmonyPrefix]
    public static bool Prefix(EntityBehaviorHunger __instance, ref float saturation, EnumFoodCategory foodCat, ref float saturationLossDelay, ref float nutritionGainMultiplier)
    {
        return true;
    }
}
