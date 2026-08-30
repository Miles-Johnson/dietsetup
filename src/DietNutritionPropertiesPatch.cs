using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace dietsetup;

/// <summary>
/// Postfix on CollectibleObject.GetNutritionProperties -- the choke point tryEatBegin/tryEatStep/
/// tryEatStop and the tooltip/animation code all consult, standalone (non-meal, non-liquid) food.
/// Gather/resolve/apply via DietSatietyFold (architecture 5.4's satiety fold point).
/// </summary>
[HarmonyPatch(typeof(CollectibleObject), nameof(CollectibleObject.GetNutritionProperties))]
public static class DietNutritionPropertiesPatch
{
    [HarmonyPostfix]
    public static void Postfix(CollectibleObject __instance, IWorldAccessor world, ItemStack itemstack, Entity forEntity, ref FoodNutritionProperties? __result)
    {
        DietSatietyFold.TryFold(__instance, forEntity, ref __result, out _);
    }
}
