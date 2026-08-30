using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace dietsetup;

/// <summary>
/// Prefix on the protected CollectibleObject.tryEatStop -- produces the nutrition-multiplier value
/// DietSaturationScalePatch consumes for a standalone (non-meal) eat. Must be a prefix: it has to
/// enqueue before tryEatStop's own body calls ReceiveSaturation (verified against the decompiled
/// body, reference/decompiled/1.22/VintagestoryAPI/Vintagestory.API.Common/CollectibleObject.cs:1779).
/// No-op until phase 3.
/// </summary>
[HarmonyPatch(typeof(CollectibleObject), "tryEatStop", new[] { typeof(float), typeof(ItemSlot), typeof(EntityAgent) })]
public static class DietNutritionMultiplierEatPatch
{
    [HarmonyPrefix]
    public static void Prefix(float secondsUsed, ItemSlot slot, EntityAgent byEntity)
    {
    }
}
