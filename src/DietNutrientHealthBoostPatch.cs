using dietsetup.Diet;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Harmony prefix that fully replaces EntityBehaviorHunger.UpdateNutrientHealthBoost for players
/// (returns false to skip the original). Vanilla's formula is
/// `2.5 * (FruitFraction + VegFraction + ProteinFraction + GrainFraction + DairyFraction)`,
/// capped at 12.5. Since NutritionMult is now an authored-per-category value rather than a binary
/// active/inactive flag, the replacement is a weighted average:
/// `12.5 * sum(fraction_i * NutritionMult_i) / sum(NutritionMult_i)`.
/// When every NutritionMult is 1 (the default, untouched profile) this reduces to exactly
/// vanilla's `2.5 * sum` -- no behavior change for anyone on an all-1 profile. A category with
/// NutritionMult 0 (e.g. a "fills but doesn't nourish" profile) carries zero weight, so a bar the
/// player can structurally never fill can't drag their health-bonus ceiling down.
///
/// Verified against reference/decompiled/VSEssentials/Vintagestory.GameContent/EntityBehaviorHunger.cs:416-426.
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
