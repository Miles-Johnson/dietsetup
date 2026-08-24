using System;
using dietsetup.Diet;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Harmony prefix fully replacing EntityBehaviorHunger.OnEntityReceiveSaturation for players --
/// the only way to scale satiety and nutrition gain independently, since callers never pass the
/// eaten item. Full rationale and decompiled verification:
/// notes/dietsetup-patch-internals.md#saturation-patch--dietsaturationpatchcs.
/// </summary>
[HarmonyPatch(typeof(EntityBehaviorHunger), nameof(EntityBehaviorHunger.OnEntityReceiveSaturation))]
public static class DietSaturationPatch
{
    [HarmonyPrefix]
    public static bool Prefix(EntityBehaviorHunger __instance, float saturation, EnumFoodCategory foodCat, float saturationLossDelay, float nutritionGainMultiplier)
    {
        if (__instance.entity is not EntityPlayer || !DietSetupModSystem.Config.EnableDietSystem)
        {
            return true;
        }

        DietProfile profile = DietProfileRegistry.ResolveProfileForEntity(__instance.entity, DietSetupModSystem.Config.DefaultProfileId);
        DietCategoryDefault catDefault = profile.CategoryDefaults.TryGetValue(foodCat.ToString(), out DietCategoryDefault? cd) ? cd : DietCategoryDefault.PassThrough;

        float maxSaturation = __instance.MaxSaturation;
        bool wasFull = __instance.Saturation >= maxSaturation;

        __instance.Saturation = Math.Min(maxSaturation, __instance.Saturation + saturation * catDefault.SatietyMult);

        float levelIncrement = wasFull ? 0f : saturation / 2.5f * nutritionGainMultiplier * catDefault.NutritionMult;

        // Scaled by the same satiety multiplier as satiety itself, floored at 0 -- pass-through
        // would let a zero-satiety category stall vanilla's hunger-drain-start timer for free,
        // since SaturationLossDelay* has nothing to do with the (zero) benefit actually gained.
        float scaledDelay = Math.Max(0f, saturationLossDelay * catDefault.SatietyMult);

        switch (foodCat)
        {
            case EnumFoodCategory.Fruit:
                if (!wasFull) __instance.FruitLevel = Math.Min(maxSaturation, __instance.FruitLevel + levelIncrement);
                __instance.SaturationLossDelayFruit = Math.Max(__instance.SaturationLossDelayFruit, scaledDelay);
                break;
            case EnumFoodCategory.Vegetable:
                if (!wasFull) __instance.VegetableLevel = Math.Min(maxSaturation, __instance.VegetableLevel + levelIncrement);
                __instance.SaturationLossDelayVegetable = Math.Max(__instance.SaturationLossDelayVegetable, scaledDelay);
                break;
            case EnumFoodCategory.Protein:
                if (!wasFull) __instance.ProteinLevel = Math.Min(maxSaturation, __instance.ProteinLevel + levelIncrement);
                __instance.SaturationLossDelayProtein = Math.Max(__instance.SaturationLossDelayProtein, scaledDelay);
                break;
            case EnumFoodCategory.Grain:
                if (!wasFull) __instance.GrainLevel = Math.Min(maxSaturation, __instance.GrainLevel + levelIncrement);
                __instance.SaturationLossDelayGrain = Math.Max(__instance.SaturationLossDelayGrain, scaledDelay);
                break;
            case EnumFoodCategory.Dairy:
                if (!wasFull) __instance.DairyLevel = Math.Min(maxSaturation, __instance.DairyLevel + levelIncrement);
                __instance.SaturationLossDelayDairy = Math.Max(__instance.SaturationLossDelayDairy, scaledDelay);
                break;
        }

        __instance.UpdateNutrientHealthBoost(); // intercepted by DietNutrientHealthBoostPatch too
        return false;
    }
}
