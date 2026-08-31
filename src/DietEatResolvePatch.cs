using dietsetup.Binding;
using dietsetup.Diet;
using dietsetup.Rules;
using dietsetup.Tags;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace dietsetup;

/// <summary>
/// Gather+resolve+apply for a standalone (non-meal) eat, on the protected CollectibleObject.tryEatStop
/// (architecture 9, task 2). Same secondsUsed/server/slot guards as vanilla's own body, so this only
/// fires for a real completed eat, not every eat-step tick (verified against the decompiled body,
/// reference/decompiled/1.22/VintagestoryAPI/Vintagestory.API.Common/CollectibleObject.cs:1779-1789).
///
/// Prefix resolves once and either enqueues the nutrition multiplier DietSaturationScalePatch
/// consumes, or -- for an Inedible verdict (7.4) -- returns false to skip tryEatStop's own body
/// entirely. That has to happen here, before the original runs: tryEatStop's body is what calls
/// ReceiveSaturation and slot.TakeOut, so skipping it is the only way "refuses the eat" means no
/// consumption and no satiety, not just a zeroed multiplier after the fact.
///
/// Postfix fires the winning rule's effects (damage, consequence) from the same resolve via
/// __state. Harmony always runs postfixes even when a prefix skips the original, so a refused
/// Inedible eat still gets its authored effects -- architecture 4.2's own example pairs
/// verdict:inedible with a damage effect for exactly this reason.
/// </summary>
[HarmonyPatch(typeof(CollectibleObject), "tryEatStop", new[] { typeof(float), typeof(ItemSlot), typeof(EntityAgent) })]
public static class DietEatResolvePatch
{
    [HarmonyPrefix]
    public static bool Prefix(float secondsUsed, ItemSlot slot, EntityAgent byEntity, out DietResolveResult? __state)
    {
        __state = null;
        if (!DietSetupModSystem.Config.EnableDietSystem) return true;
        if (byEntity?.World is not IServerWorldAccessor) return true;
        if (secondsUsed < 0.95f) return true;
        if (slot?.Itemstack?.Collectible == null) return true;

        CompiledDiet? diet = DietIdResolver.ResolveDiet(byEntity);
        if (diet == null) return true;

        ulong tagMask = FoodTagRegistry.GetTagMask(byEntity.World, slot, out float spoilLevel, out bool determined);
        if (!determined) return true;

        DietResolveResult resolved = DietResolver.Resolve(diet, tagMask, spoilLevel);
        __state = resolved;

        if (resolved.Verdict == DietVerdict.Inedible)
        {
            return false;
        }

        DietProfileRegistry.EnqueueNutritionMultiplier(byEntity.EntityId, resolved.Nutrition);
        return true;
    }

    [HarmonyPostfix]
    public static void Postfix(EntityAgent byEntity, DietResolveResult? __state)
    {
        if (__state == null) return;
        DietEffectRunner.Fire(byEntity.Api, byEntity, __state.Value);
    }
}
