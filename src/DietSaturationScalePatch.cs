using System;
using dietsetup.Diet;
using dietsetup.Rules;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Prefix on EntityBehaviorHunger.OnEntityReceiveSaturation, replacing the deleted
/// DietSaturationPatch (removed prompt 7, commit ffdcb03) -- that deletion silently regressed
/// every old-style profile (balanced/carnivore/herbivore/elf) to full unscaled vanilla saturation
/// and nutrient-bar gain, since it was the only code that ever applied
/// CategoryDefaults[category].SatietyMult/NutritionMult. This patch closes that regression and
/// also applies the tag-engine's nutrition-axis fold (design 2, tag-engine step 9) for both diet
/// systems -- the one place FoodTagRegistry.TagNutritionMultiplier's contribution is ever applied.
///
/// Verified against the 1.22 decompiled body
/// (reference/decompiled/1.22/VSEssentials/Vintagestory.GameContent/EntityBehaviorHunger.cs:232-276):
/// vanilla's own body uses saturation/saturationLossDelay/nutritionGainMultiplier directly with no
/// multiply of its own, so a light ref-mutating prefix reproduces the old full-body-replacement
/// patch exactly, with no drift risk on a future game update.
///
/// Ordering: for a rules-engine-diet entity, saturation arriving here is already diet-curve-scaled
/// upstream by DietSpoilageSatietyPatch's FoodSpoilageSatLossMul postfix (before ReceiveSaturation
/// is even called) -- this patch must NOT re-scale ref saturation/saturationLossDelay for that
/// branch, only ref nutritionGainMultiplier, or the diet curve applies twice.
/// </summary>
[HarmonyPatch(typeof(EntityBehaviorHunger), nameof(EntityBehaviorHunger.OnEntityReceiveSaturation))]
public static class DietSaturationScalePatch
{
    private static bool loggedEmptyDequeue;

    [HarmonyPrefix]
    public static bool Prefix(EntityBehaviorHunger __instance, ref float saturation, EnumFoodCategory foodCat, ref float saturationLossDelay, ref float nutritionGainMultiplier)
    {
        if (!DietSetupModSystem.Config.EnableDietSystem) return true;

        Entity? entity = __instance?.entity;
        if (entity == null) return true;

        if (DietProfileRegistry.TryDequeueNutritionMultiplier(entity.EntityId, out float queuedMult))
        {
            nutritionGainMultiplier *= queuedMult;
        }
        else if (!loggedEmptyDequeue)
        {
            // Should never happen by construction (see the eat/meal producers) -- one warning is
            // enough to flag a real producer/consumer count mismatch without spamming the log.
            loggedEmptyDequeue = true;
            entity.Api?.Logger.Warning("[dietsetup] DietSaturationScalePatch: no queued nutrition multiplier for entity {0} on a real saturation gain -- producer/consumer count mismatch, nutrition-axis tag fold skipped this bite.", entity.EntityId);
        }

        string dietId = entity.WatchedAttributes.GetString(DietSetupModSystem.AttrProfile, DietSetupModSystem.Config.DefaultProfileId);
        CompiledDiet? diet = DietRuleRegistry.GetDiet(dietId);

        if (diet == null)
        {
            DietProfile profile = DietProfileRegistry.ResolveProfileForEntity(entity, DietSetupModSystem.Config.DefaultProfileId);
            DietCategoryDefault catDefault = profile.CategoryDefaults.TryGetValue(foodCat.ToString(), out DietCategoryDefault? cd) ? cd : DietCategoryDefault.PassThrough;

            saturation = Math.Max(0f, saturation * catDefault.SatietyMult);
            saturationLossDelay = Math.Max(0f, saturationLossDelay * catDefault.SatietyMult);
            nutritionGainMultiplier *= catDefault.NutritionMult;
        }

        return true;
    }
}
