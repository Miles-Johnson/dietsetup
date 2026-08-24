using dietsetup.Diet;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Harmony prefix fully replacing EntityBehaviorHunger.UpdateNutrientHealthBoost for players --
/// weights vanilla's health-bonus formula by each category's authored NutritionMult (reduces to
/// vanilla exactly when every mult is 1). Full derivation and decompiled verification:
/// notes/dietsetup-patch-internals.md#nutrient-health-boost--dietnutrienthealthboostpatchcs.
/// </summary>
[HarmonyPatch(typeof(EntityBehaviorHunger), nameof(EntityBehaviorHunger.UpdateNutrientHealthBoost))]
public static class DietNutrientHealthBoostPatch
{
    [HarmonyPrefix]
    public static bool Prefix(EntityBehaviorHunger __instance)
    {
        if (__instance.entity is not EntityPlayer || !DietSetupModSystem.Config.EnableDietSystem)
        {
            return true;
        }

        float maxSaturation = __instance.MaxSaturation;
        if (maxSaturation <= 0f)
        {
            return true;
        }

        DietProfile profile = DietProfileRegistry.ResolveProfileForEntity(__instance.entity, DietSetupModSystem.Config.DefaultProfileId);

        float weightedSum = 0f;
        float weightTotal = 0f;
        Accumulate("Fruit", __instance.FruitLevel);
        Accumulate("Vegetable", __instance.VegetableLevel);
        Accumulate("Protein", __instance.ProteinLevel);
        Accumulate("Grain", __instance.GrainLevel);
        Accumulate("Dairy", __instance.DairyLevel);

        float bonus = weightTotal == 0f ? 0f : 12.5f * weightedSum / weightTotal;
        __instance.entity.GetBehavior<EntityBehaviorHealth>()?.SetMaxHealthModifiers("nutrientHealthMod", bonus);
        return false;

        void Accumulate(string category, float level)
        {
            DietCategoryDefault catDefault = profile.CategoryDefaults.TryGetValue(category, out DietCategoryDefault? cd) ? cd : DietCategoryDefault.PassThrough;
            weightedSum += level / maxSaturation * catDefault.NutritionMult;
            weightTotal += catDefault.NutritionMult;
        }
    }
}
