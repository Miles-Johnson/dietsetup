using dietsetup.Diet;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace dietsetup;

/// <summary>
/// Postfix on CollectibleObject.GetNutritionProperties -- the choke point tryEatBegin/tryEatStep/
/// tryEatStop and the tooltip/animation code all consult. Grants nutrition to items vanilla has
/// none for and writes reaction damage for zero-satiety categories. Clone/double-fire discipline:
/// notes/dietsetup-patch-internals.md#nutrition-properties-patch--dietnutritionpropertiespatchcs.
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

        __result = DietProfileRegistry.ResolveNutritionProperties(
            forEntity.Api, forEntity, __instance, itemstack, __result, DietSetupModSystem.Config.DefaultProfileId,
            queueReaction: true, out _, out _, out _);
    }
}
