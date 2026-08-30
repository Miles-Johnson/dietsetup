using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace dietsetup;

/// <summary>
/// Shared entity-diet lookup for both GlobalConstants.FoodSpoilageSatLossMul and
/// FoodSpoilageHealthLossMul postfixes -- health has no authored curve of its own (spec section 6,
/// amended prompt 7 target 2: "no new rule field in v1, health is derived from satiety, not
/// authored"), it mirrors this same satiety resolution and clamps separately. One evaluation path,
/// same as DietResolver itself -- the two call sites differ only in what they do with the result.
/// No-op until phase 3 wires a diet id source (bindings.json) in place of the deleted AttrProfile
/// attribute -- always false, so both callers' __result is left at vanilla.
/// </summary>
internal static class DietSpoilageResolution
{
    public static bool TryResolveSatietyMultiplier(float spoilState, ItemStack? stack, EntityAgent? byEntity, out float satietyMult)
    {
        satietyMult = 0f;
        return false;
    }
}
