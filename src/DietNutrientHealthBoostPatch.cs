using HarmonyLib;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// No-op until phase 3. Rewritten body derives the health weight from architecture.md section 2's
/// per-category `capacity` field instead of the deleted CategoryDefault.NutritionMult.
/// </summary>
[HarmonyPatch(typeof(EntityBehaviorHunger), nameof(EntityBehaviorHunger.UpdateNutrientHealthBoost))]
public static class DietNutrientHealthBoostPatch
{
    [HarmonyPrefix]
    public static bool Prefix(EntityBehaviorHunger __instance)
    {
        return true;
    }
}
