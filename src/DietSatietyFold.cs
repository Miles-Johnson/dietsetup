using dietsetup.Binding;
using dietsetup.Rules;
using dietsetup.Tags;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace dietsetup;

/// <summary>Shared gather+resolve for the GetNutritionProperties family (patch targets 1-3):
/// CollectibleObject, BlockLiquidContainerBase and BlockMeal.GetIngredientStackNutritionProperties.
/// Resolves the diet for the nutrition-gain axis handoff (DietMealNutritionPatch) only. Does NOT
/// fold Satiety: vanilla always computes actual satiety as
/// <c>nutriProps.Satiety * GlobalConstants.FoodSpoilageSatLossMul(...)</c> a few lines downstream of
/// every GetNutritionProperties call (reference/decompiled/1.22/VintagestoryAPI/Vintagestory.API.
/// Common/CollectibleObject.cs:1781/1787/1789, BlockLiquidContainerBase.cs:822-823ish per notes/
/// 1.22-verification.md, BlockMeal's per-ingredient loop via DietMealContentNutritionPatch:76-89),
/// and DietSpoilageResolution already owns that second factor with the live, never-synthetic
/// spoilState vanilla itself computed and the full tag mask (spoil bit included). This site
/// structurally can't match that: GetNutritionProperties's own signature carries no ItemSlot, so
/// deriving spoilState here would mean a fidelity-losing DummySlot guess (loses crock dampening etc.,
/// see FoodTagRegistry.GetTagMaskForSpoilState's doc). Folding Satiety at both sites would be the
/// two-resolve-one-axis defect architecture 5.4 forbids: a 'raw' rule winning here (static mask only)
/// and a 'spoiled' rule winning at DietSpoilageResolution (full mask) would compound instead of the
/// diet resolving once. See DietSpoilageResolution's doc for why that site is the sole owner --
/// architecture 5.4's table now names it, not this family, as the satiety fold point.</summary>
internal static class DietSatietyFold
{
    public static void TryFold(CollectibleObject collectible, Entity? forEntity, ref FoodNutritionProperties? result, out DietResolveResult? resolved)
    {
        resolved = null;
        if (result == null) return;
        if (!DietSetupModSystem.Config.EnableDietSystem) return;

        CompiledDiet? diet = DietIdResolver.ResolveDiet(forEntity);
        if (diet == null) return;

        ulong tagMask = FoodTagRegistry.GetStaticMask(collectible);
        resolved = DietResolver.Resolve(diet, tagMask, 0f);

        // Not result.Clone(): JsonItemStack.CloneTo (vsapi) calls Code.Clone() unconditionally,
        // which NREs on a malformed EatenStack (null Code) -- hit routinely by CreateSearchCache
        // walking every creative-tab collectible. Build fresh and carry EatenStack by reference;
        // Satiety is copied unmodified -- see class doc for why this site doesn't fold it.
        result = new FoodNutritionProperties
        {
            FoodCategory = result.FoodCategory,
            Satiety = result.Satiety,
            Health = result.Health,
            Intoxication = result.Intoxication,
            Psychedelic = result.Psychedelic,
            SaturationLossDelay = result.SaturationLossDelay,
            EatenStack = result.EatenStack
        };
    }
}
