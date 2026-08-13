using dietsetup.Diet;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Same resolver, second target: BlockLiquidContainerBase.GetNutritionProperties has its own
/// override that reads NutritionPropsPerLitre directly for filled containers, only falling
/// through to CollectibleObject.GetNutritionProperties (already patched above) when empty --
/// filled drink containers would never reach the other postfix otherwise.
/// </summary>
[HarmonyPatch(typeof(BlockLiquidContainerBase), nameof(BlockLiquidContainerBase.GetNutritionProperties))]
public static class DietLiquidNutritionPropertiesPatch
{
    [HarmonyPostfix]
    public static void Postfix(BlockLiquidContainerBase __instance, IWorldAccessor world, ItemStack itemstack, Entity forEntity, ref FoodNutritionProperties? __result)
    {
        if (forEntity is not EntityPlayer || !DietSetupModSystem.Config.EnableDietSystem)
        {
            return;
        }

        __result = DietProfileRegistry.ResolveNutritionProperties(
            forEntity.Api, forEntity, __instance, __result, DietSetupModSystem.Config.DefaultProfileId,
            queueReaction: true, out _, out _, out _);
    }
}
