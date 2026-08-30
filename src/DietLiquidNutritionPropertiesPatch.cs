using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Same resolver, second target: BlockLiquidContainerBase.GetNutritionProperties reads
/// NutritionPropsPerLitre directly for filled containers, only falling through to the
/// already-patched CollectibleObject path when empty -- filled drinks need their own postfix.
/// No-op until phase 3, see DietNutritionPropertiesPatch.
/// </summary>
[HarmonyPatch(typeof(BlockLiquidContainerBase), nameof(BlockLiquidContainerBase.GetNutritionProperties))]
public static class DietLiquidNutritionPropertiesPatch
{
    [HarmonyPostfix]
    public static void Postfix(BlockLiquidContainerBase __instance, IWorldAccessor world, ItemStack itemstack, Entity forEntity, ref FoodNutritionProperties? __result)
    {
    }
}
