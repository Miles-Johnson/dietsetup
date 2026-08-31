using dietsetup.Rules;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Apply site for a meal eat's effects (architecture 9, task 2). Same target as
/// RotIntakeMealEatPatch -- BlockMeal never calls tryEatStop, so this is the meal path's
/// equivalent of DietEatResolvePatch's postfix.
///
/// Gated on secondsUsed >= 1.45f and IServerWorldAccessor, not on __result: tryFinishEatMeal's own
/// body (BlockMeal.cs:222-228) calls GetContentNutritionProperties -- which populates
/// PendingMealEffects -- unconditionally, before checking those same two conditions plus its own
/// null-result check, and returns false on any of the three. __result alone can't tell "not a real
/// attempt" (secondsUsed too low, client side) apart from "a real attempt refused for Inedible", and
/// the latter still needs its effects (see DietMealContentNutritionPatch's doc). Matching vanilla's
/// own secondsUsed/side guard exactly is what tells them apart.
/// </summary>
[HarmonyPatch(typeof(BlockMeal), "tryFinishEatMeal")]
public static class DietMealEffectFirePatch
{
    [HarmonyPostfix]
    public static void Postfix(float secondsUsed, EntityAgent byEntity)
    {
        if (!DietSetupModSystem.Config.EnableDietSystem) return;
        if (byEntity?.World is not IServerWorldAccessor) return;
        if (secondsUsed < 1.45f) return;

        foreach (DietResolveResult result in PendingMealEffects.TakeAll(byEntity.EntityId))
        {
            DietEffectRunner.Fire(byEntity.Api, byEntity, result);
        }
    }
}
