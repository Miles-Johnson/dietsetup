using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Public API: dietsetup writes a decaying per-player "intake" counter for how much of a food
/// tag a player has recently eaten, exposed as WatchedAttributes keys any mod can read by
/// string, no assembly reference required --
///   "dietsetup:intake:&lt;tag&gt;"              double, 0..DietSetupConfig.RotIntakeCap, unitless
///   "dietsetup:intake:&lt;tag&gt;:updatedHours"  double, world.Calendar.TotalHours at last write
/// Only "rot" is written in v1 (Phase G3, for rfmechanics' goblin rot aura). Renaming either key
/// shape breaks that consumer -- see README.md.
///
/// Shared accrual formula for the two patches below. Decay-then-add on the in-game calendar
/// clock. Details: notes/dietsetup-patch-internals.md#rot-intake-accrual--rotintakeaccrualpatchcs.
/// </summary>
internal static class RotIntakeAccrual
{
    private const string Tag = "rot";
    private const double DefaultHalfLifeHours = 48.0;

    /// <summary>transitionLevel &lt;= 0 (fresh food) contributes nothing and skips the write
    /// entirely -- eating fresh food should not decay the accumulator faster than time alone
    /// already does.</summary>
    public static void AccrueRotIntake(EntityPlayer player, float transitionLevel)
    {
        if (transitionLevel <= 0f) return;

        DietSetupConfig cfg = DietSetupModSystem.Config;
        if (!cfg.EnableRotIntakeTracking) return;

        ITreeAttribute wa = player.WatchedAttributes;
        double nowHours = player.World.Calendar.TotalHours;
        string valueKey = DietSetupModSystem.AttrIntake(Tag);
        string updatedKey = DietSetupModSystem.AttrIntakeUpdatedHours(Tag);
        double lastHours = wa.GetDouble(updatedKey, nowHours);
        double raw = wa.GetDouble(valueKey, 0.0);

        double halfLife = cfg.IntakeHalfLifeHours.TryGetValue(Tag, out double h) ? h : DefaultHalfLifeHours;
        double decayed = raw * Math.Pow(0.5, Math.Max(0.0, nowHours - lastHours) / halfLife);
        double next = Math.Min(cfg.RotIntakeCap, decayed + cfg.RotIntakePerBite * transitionLevel);

        wa.SetDouble(valueKey, next);
        wa.SetDouble(updatedKey, nowHours);
    }
}

/// <summary>Standalone eating -- mirrors vanilla's own tryEatStop spoilage read
/// (Collectible.cs:1858-1859), repurposing TransitionLevel for this accumulator instead of
/// satiety/health falloff. Same Harmony target as DietEatDoTPatch, independent postfix.</summary>
[HarmonyPatch(typeof(CollectibleObject), "tryEatStop")]
public static class RotIntakeStandaloneEatPatch
{
    [HarmonyPostfix]
    public static void Postfix(float secondsUsed, ItemSlot slot, EntityAgent byEntity)
    {
        if (byEntity is not EntityPlayer player) return;
        if (byEntity.World is not IServerWorldAccessor || secondsUsed < 0.95f) return;

        TransitionState? state = slot.Itemstack?.Collectible?.UpdateAndGetTransitionState(byEntity.World, slot, EnumTransitionType.Perish);
        RotIntakeAccrual.AccrueRotIntake(player, state?.TransitionLevel ?? 0f);
    }
}

/// <summary>
/// Meal eating -- same target as DietMealEatDoTPatch, independent postfix (BlockMeal never calls
/// tryEatStop). Averages TransitionLevel across the pot's contents since cooking pools freshness
/// before it's stamped on stacks -- known, accepted limitation. Details:
/// notes/dietsetup-patch-internals.md#rot-intake-meal--rotintakeaccrualpatchcs-rotintakemealeatpatch.
/// BlockPie is the one exception: its fillings are held permanently fresh by UnspoilContents, so it
/// accrues from the pie's own Perish level instead of averaging them (never fresh, so never zero).
/// </summary>
[HarmonyPatch(typeof(BlockMeal), "tryFinishEatMeal")]
public static class RotIntakeMealEatPatch
{
    [HarmonyPostfix]
    public static void Postfix(BlockMeal __instance, ItemSlot slot, EntityAgent byEntity, bool __result)
    {
        if (!__result) return;
        if (byEntity is not EntityPlayer player) return;
        if (byEntity.World is not IServerWorldAccessor) return;

        // A pie's own Perish clock, not its (permanently unspoiled) fillings' -- see
        // notes/1.22-meal-pie-eat-trace.md's deferred entry for why averaging fillings here
        // would always read fresh for a pie.
        if (__instance is BlockPie)
        {
            TransitionState? pieState = slot.Itemstack?.Collectible.UpdateAndGetTransitionState(byEntity.World, slot, EnumTransitionType.Perish);
            RotIntakeAccrual.AccrueRotIntake(player, pieState?.TransitionLevel ?? 0f);
            return;
        }

        ItemStack[] contents = __instance.GetNonEmptyContents(byEntity.World, slot.Itemstack);
        if (contents.Length == 0) return;

        float total = 0f;
        int counted = 0;
        foreach (ItemStack contentStack in contents)
        {
            if (contentStack?.Collectible == null) continue;
            var dummySlot = new DummySlot(contentStack);
            TransitionState? state = contentStack.Collectible.UpdateAndGetTransitionState(byEntity.World, dummySlot, EnumTransitionType.Perish);
            if (state == null) continue;
            total += state.TransitionLevel;
            counted++;
        }
        if (counted == 0) return;

        RotIntakeAccrual.AccrueRotIntake(player, total / counted);
    }
}
