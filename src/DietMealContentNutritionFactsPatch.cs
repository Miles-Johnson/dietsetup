using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Prefix on BlockMeal.GetContentNutritionFacts -- every vanilla call site to this method (the
/// held-item/bowl tooltip via GetHeldItemInfo at BlockMeal.cs:783, and the crock/cooked-container/
/// pot GetBlockInfo panels at BlockEntityCookedContainer.cs:264 and BlockEntityMeal.cs:179) builds
/// display text, never triggers a real eat. Two independent fixes share this one patch:
///
/// 1. forEntity is null at the held-item/bowl tooltip call site (vanilla hardcodes it), so the
///    already-patched GetContentNutritionProperties resolves "base" (nobody's diet) instead of the
///    viewer's -- substituted here to the client-side viewing player, when there is one.
/// 2. Every call into this method -- substituted-entity or already-real (the crock/pot GetBlockInfo
///    sites already pass forPlayer.Entity) -- must not write into the real-eat side-effect queues
///    (DietProfileRegistry, MealIngredientNutritionHandoff, PendingMealEffects) that
///    DietMealContentNutritionPatch/DietMealNutritionPatch populate whenever forEntity is
///    non-null. DietMealFactsContext.DisplayOnly gates those off regardless of which reason
///    forEntity ended up non-null here -- without it, opening a crock's GUI or hovering a bowl
///    already pollutes that player's real-eat queues today.
///
/// Not a full-body replacement: vanilla's own dictionary/text-building loop (BlockMeal.cs:588-609)
/// runs untouched, calling the already-patched GetContentNutritionProperties for the real
/// per-ingredient resolve -- nothing here reimplements it.
/// </summary>
[HarmonyPatch(typeof(BlockMeal), nameof(BlockMeal.GetContentNutritionFacts),
    new[] { typeof(IWorldAccessor), typeof(ItemSlot), typeof(ItemStack[]), typeof(EntityAgent), typeof(bool), typeof(float), typeof(float) })]
public static class DietMealContentNutritionFactsPatch
{
    [HarmonyPrefix]
    public static void Prefix(IWorldAccessor world, ref EntityAgent? forEntity)
    {
        if (!DietSetupModSystem.Config.EnableDietSystem) return;

        if (forEntity == null && world is IClientWorldAccessor clientWorld && clientWorld.Player?.Entity is EntityAgent viewer)
        {
            forEntity = viewer;
        }

        DietMealFactsContext.DisplayOnly = true;
    }

    [HarmonyPostfix]
    public static void Postfix()
    {
        DietMealFactsContext.DisplayOnly = false;
    }
}
