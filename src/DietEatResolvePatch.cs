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
/// (architecture 9). Same secondsUsed/server/slot guards as vanilla's own body, so this only fires
/// for a real completed eat, not every eat-step tick (verified against the decompiled body,
/// reference/decompiled/1.22/VintagestoryAPI/Vintagestory.API.Common/CollectibleObject.cs:1779-1789).
///
/// Prefix resolves once and enqueues the nutrition multiplier DietSaturationScalePatch consumes,
/// then always lets tryEatStop's own body run (architecture 7.5: Inedible no longer skips it). For
/// an Inedible verdict resolved.Nutrition is already zero (DietResolver), so the enqueued multiplier
/// is zero and vanilla's own ReceiveSaturation/slot.TakeOut still complete the eat and consume the
/// item -- satiety just doesn't move.
///
/// Postfix fires the winning rule's effects (damage, consequence) from the same resolve via __state.
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
