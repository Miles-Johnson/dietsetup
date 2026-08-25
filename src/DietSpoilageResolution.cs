using dietsetup.Rules;
using dietsetup.Tags;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace dietsetup;

/// <summary>
/// Shared entity-diet lookup for both GlobalConstants.FoodSpoilageSatLossMul and
/// FoodSpoilageHealthLossMul postfixes -- health has no authored curve of its own (spec section 6,
/// amended prompt 7 target 2: "no new rule field in v1, health is derived from satiety, not
/// authored"), it mirrors this same satiety resolution and clamps separately. One evaluation path,
/// same as DietResolver itself -- the two call sites differ only in what they do with the result.
/// </summary>
internal static class DietSpoilageResolution
{
    public static bool TryResolveSatietyMultiplier(float spoilState, ItemStack? stack, EntityAgent? byEntity, out float satietyMult)
    {
        satietyMult = 0f;
        if (!DietSetupModSystem.Config.EnableDietSystem) return false;
        if (byEntity?.Api == null || stack?.Collectible == null) return false;

        string dietId = byEntity.WatchedAttributes.GetString(DietSetupModSystem.AttrProfile, DietSetupModSystem.Config.DefaultProfileId);
        CompiledDiet? diet = DietRuleRegistry.GetDiet(dietId);
        if (diet == null) return false; // no rules-engine diet for this entity -- vanilla result stands

        ulong tagMask = FoodTagRegistry.GetTagMaskForSpoilState(stack.Collectible, spoilState);
        satietyMult = DietResolver.Resolve(diet, tagMask, spoilState, byEntity.Api, byEntity).Satiety;
        return true;
    }
}
