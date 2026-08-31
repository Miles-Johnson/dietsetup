using System.Text;
using dietsetup.Binding;
using dietsetup.Rules;
using dietsetup.Tags;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;

namespace dietsetup;

/// <summary>
/// Postfix on CollectibleObject.GetHeldItemInfo -- the tooltip consumer that makes a verdict a
/// feature rather than a value nothing branches on (architecture 7.4, task 3). Edible appends
/// nothing (the unremarkable default); Harmful and Nourishing label the item; Inedible labels it
/// too, since the tooltip is what a player sees before attempting to eat, ahead of the refusal at
/// DietEatResolvePatch/DietMealContentNutritionPatch. Gather/resolve/apply, same as every other
/// patch here -- resolves against the viewing player's own diet, not the item's static tags alone.
/// Client-only: GetHeldItemInfo is a tooltip-render call, and world.Player is null server-side.
/// BlockMeal.GetHeldItemInfo calls base (BlockMeal.cs:728), so this postfix covers meals too.
/// </summary>
[HarmonyPatch(typeof(CollectibleObject), nameof(CollectibleObject.GetHeldItemInfo))]
public static class DietVerdictTooltipPatch
{
    [HarmonyPostfix]
    public static void Postfix(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world)
    {
        if (!DietSetupModSystem.Config.EnableDietSystem) return;
        if (world is not IClientWorldAccessor clientWorld) return;

        Entity? viewer = clientWorld.Player?.Entity;
        if (viewer == null) return;

        CompiledDiet? diet = DietIdResolver.ResolveDiet(viewer);
        if (diet == null) return;

        ulong tagMask = FoodTagRegistry.GetTagMask(world, inSlot, out float spoilLevel, out bool determined);
        if (!determined) return;

        DietResolveResult result = DietResolver.Resolve(diet, tagMask, spoilLevel);
        if (result.Verdict == DietVerdict.Edible) return;

        dsc.AppendLine(Lang.Get($"dietsetup:verdict-{result.Verdict.ToString().ToLowerInvariant()}"));
    }
}
