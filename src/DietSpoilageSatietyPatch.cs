using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;

namespace dietsetup;

/// <summary>
/// Postfix on GlobalConstants.FoodSpoilageSatLossMul -- the single static method every vanilla
/// eat/drink path and the tooltip call to turn TransitionLevel into a satiety multiplier (patching
/// here covers every food type uniformly; vanilla's own extension point has no usable setter).
/// Replaces vanilla's result rather than stacking on it -- a patch further downstream that also
/// scaled by spoilage would double-apply it (notes/dietsetup-tag-engine.md section 6); this is
/// the only patch on this method (both dietsetup and rfmechanics grepped clean).
/// Generalizes the former goblin-only GoblinInverseFreshnessSatietyPatch: an entity whose diet has
/// an authored rule matching the stack's tags (including spoiled/fresh) gets that rule's curve;
/// everyone else -- no diet assigned, an uncompiled diet id, or (the common case) "base"/any diet
/// falling through to its neutral fallback -- gets vanilla unchanged (DietResolveResult.Matched).
/// No re-entrancy guard needed: DietSpoilageResolution/DietResolver read no shared mutable state,
/// so this postfix firing once per ingredient from DietMealContentNutritionPatch's loop is just
/// repeated independent calls, never a nested/recursive one.
/// </summary>
[HarmonyPatch(typeof(GlobalConstants), nameof(GlobalConstants.FoodSpoilageSatLossMul))]
public static class DietSpoilageSatietyPatch
{
    [HarmonyPostfix]
    public static void Postfix(float spoilState, ItemStack stack, EntityAgent byEntity, ref float __result)
    {
        if (DietSpoilageResolution.TryResolveSatietyMultiplier(spoilState, stack, byEntity, out float satietyMult))
        {
            __result = satietyMult;
        }
    }
}
