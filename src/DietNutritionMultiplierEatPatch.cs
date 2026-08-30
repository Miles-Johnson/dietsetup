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
/// Prefix on the protected CollectibleObject.tryEatStop -- gathers/resolves and enqueues the
/// nutrition-multiplier value DietSaturationScalePatch consumes for a standalone (non-meal) eat.
/// OnEntityReceiveSaturation has no item/tag parameters of its own (just a saturation float and a
/// category enum), so this hand-off queue is the only channel for architecture 5.4's nutrition
/// fold to reach it. Must be a prefix: it has to enqueue before tryEatStop's own body calls
/// ReceiveSaturation (verified against the decompiled body, reference/decompiled/1.22/
/// VintagestoryAPI/Vintagestory.API.Common/CollectibleObject.cs:1779-1789). Same
/// secondsUsed/server/slot guards as vanilla's own body, so this only fires for a real completed
/// eat, not every eat-step tick.
/// </summary>
[HarmonyPatch(typeof(CollectibleObject), "tryEatStop", new[] { typeof(float), typeof(ItemSlot), typeof(EntityAgent) })]
public static class DietNutritionMultiplierEatPatch
{
    [HarmonyPrefix]
    public static void Prefix(float secondsUsed, ItemSlot slot, EntityAgent byEntity)
    {
        if (!DietSetupModSystem.Config.EnableDietSystem) return;
        if (byEntity?.World is not IServerWorldAccessor) return;
        if (secondsUsed < 0.95f) return;
        if (slot?.Itemstack?.Collectible == null) return;

        CompiledDiet? diet = DietIdResolver.ResolveDiet(byEntity);
        if (diet == null) return;

        ulong tagMask = FoodTagRegistry.GetTagMask(byEntity.World, slot, out float spoilLevel, out bool determined);
        if (!determined) return;

        DietResolveResult resolved = DietResolver.Resolve(diet, tagMask, spoilLevel);
        DietProfileRegistry.EnqueueNutritionMultiplier(byEntity.EntityId, resolved.Nutrition);
    }
}
