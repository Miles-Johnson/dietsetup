using System.Text;
using dietsetup.Binding;
using dietsetup.Rules;
using dietsetup.Tags;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Postfix on CollectibleObject.GetHeldItemInfo -- the tooltip consumer that makes a verdict a
/// feature rather than a value nothing branches on (architecture 7.4, task 3). Edible appends
/// nothing (the unremarkable default); Harmful and Nourishing label the item; Inedible labels it
/// too, since the tooltip is what a player sees before attempting to eat, ahead of the refusal at
/// DietEatResolvePatch/DietMealContentNutritionPatch. Gather/resolve/apply, same as every other
/// patch here -- resolves against the viewing player's own diet, not the item's static tags alone.
/// Client-only: GetHeldItemInfo is a tooltip-render call, and world.Player is null server-side.
/// BlockMeal.GetHeldItemInfo calls base (BlockMeal.cs:728), so this postfix covers meals too --
/// BlockPie does not (see BlockPieVerdictTooltipPatch below), so it needs its own postfix reusing
/// AppendVerdict rather than a copy of this formatting.
/// </summary>
[HarmonyPatch(typeof(CollectibleObject), nameof(CollectibleObject.GetHeldItemInfo))]
public static class DietVerdictTooltipPatch
{
    [HarmonyPostfix]
    public static void Postfix(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world)
    {
        AppendVerdict(inSlot, dsc, world);
    }

    internal static void AppendVerdict(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world)
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

/// <summary>
/// Postfix on BlockPie.GetHeldItemInfo -- BlockPie's own override (BlockPie.cs:324-353) never
/// calls base.GetHeldItemInfo at any level, so DietVerdictTooltipPatch's target body (patched on
/// CollectibleObject.GetHeldItemInfo) never runs for a pie and the verdict line never appeared.
/// Resolves against inSlot.Itemstack, the pie stack -- the only stack available at this call site,
/// and the correct one: FoodTagRegistry.GetTagMask re-derives TransitionLevel by dispatching to
/// BlockPie's own UpdateAndGetTransitionState override, so the fresh/spoiled bit already reflects
/// the pie's real age here, unlike the eat path (DietMealContentNutritionPatch, which still reads a
/// filling's zeroed state -- see notes/1.22-meal-pie-eat-trace.md's deferred entry).
/// GetPlacedBlockInfo is untouched -- same note's deferred entry.
/// </summary>
[HarmonyPatch(typeof(BlockPie), nameof(BlockPie.GetHeldItemInfo))]
public static class BlockPieVerdictTooltipPatch
{
    [HarmonyPostfix]
    public static void Postfix(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world)
    {
        DietVerdictTooltipPatch.AppendVerdict(inSlot, dsc, world);
    }
}
