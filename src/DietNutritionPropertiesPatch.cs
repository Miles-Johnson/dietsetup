using dietsetup.Diet;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace dietsetup;

/// <summary>
/// Postfix on CollectibleObject.GetNutritionProperties -- the choke point tryEatBegin/tryEatStep/
/// tryEatStop all consult before doing anything, and the source tooltip/animation code reads too.
/// Grants nutrition to items vanilla has none for (raw mammal meat, via grant rules) and writes
/// reaction damage into Health for zero-satiety/zero-nutrition categories with an authored
/// reaction. Never returns null for something that wasn't already null, never mutates the shared
/// __result in place -- see DietProfileRegistry.ResolveNutritionProperties for the clone
/// discipline and the double-fire guard needed because BlockLiquidContainerBase's empty-container
/// fallback calls this same patched method internally.
/// </summary>
[HarmonyPatch(typeof(CollectibleObject), nameof(CollectibleObject.GetNutritionProperties))]
public static class DietNutritionPropertiesPatch
{
    [HarmonyPostfix]
    public static void Postfix(CollectibleObject __instance, IWorldAccessor world, ItemStack itemstack, Entity forEntity, ref FoodNutritionProperties? __result)
    {
        if (forEntity is not EntityPlayer || !DietSetupModSystem.Config.EnableDietSystem)
        {
            return;
        }

        __result = DietProfileRegistry.ResolveNutritionProperties(forEntity.Api, forEntity, __instance, __result, DietSetupModSystem.Config.DefaultProfileId);
    }
}
