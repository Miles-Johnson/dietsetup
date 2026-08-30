using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace dietsetup;

/// <summary>
/// Postfix on CollectibleObject.GetNutritionProperties -- the choke point tryEatBegin/tryEatStep/
/// tryEatStop and the tooltip/animation code all consult. No-op until phase 3's schema/bindings
/// land; rewritten body resolves via DietResolver instead of the deleted profile registry.
/// </summary>
[HarmonyPatch(typeof(CollectibleObject), nameof(CollectibleObject.GetNutritionProperties))]
public static class DietNutritionPropertiesPatch
{
    [HarmonyPostfix]
    public static void Postfix(CollectibleObject __instance, IWorldAccessor world, ItemStack itemstack, Entity forEntity, ref FoodNutritionProperties? __result)
    {
    }
}
