using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;

namespace dietsetup;

/// <summary>
/// Postfix on GlobalConstants.FoodSpoilageHealthLossMul -- new in 1.22, a separate multiplier from
/// FoodSpoilageSatLossMul (notes/1.22-verification.md item 2), so the satiety patch alone does not
/// move healing. Mirrors DietSpoilageSatietyPatch's resolved value rather than authoring a second
/// curve: no rule field exists for health in v1 (spec section 6, amended prompt 7 target 2), and an
/// unclamped mirror would make a goblin's >1.0 rot-satiety curve a healing item. Clamped to 1.0 here
/// -- vanilla's own floor (Math.Max(0f, ...)) still applies underneath since we only ever lower this
/// toward 0, never raise it above what DietSpoilageResolution returns.
/// Same "no re-entrancy guard needed" reasoning as DietSpoilageSatietyPatch -- see that file.
/// </summary>
[HarmonyPatch(typeof(GlobalConstants), nameof(GlobalConstants.FoodSpoilageHealthLossMul))]
public static class DietSpoilageHealthPatch
{
    [HarmonyPostfix]
    public static void Postfix(float spoilState, ItemStack stack, EntityAgent byEntity, ref float __result)
    {
        if (DietSpoilageResolution.TryResolveSatietyMultiplier(spoilState, stack, byEntity, out float satietyMult))
        {
            __result = Math.Min(1f, satietyMult);
        }
    }
}
