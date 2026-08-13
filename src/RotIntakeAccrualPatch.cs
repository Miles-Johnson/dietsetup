using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Shared accrual formula for RotIntakeStandaloneEatPatch/RotIntakeMealEatPatch below.
/// Phase G3: accrues a rot-intake accumulator (DietSetupModSystem.AttrRotIntake) on eat
/// completion, for rfmechanics' goblin rot aura to read back (radius/intensity driven by how
/// rot-fed the goblin has been). Independent of the DietReaction/DoT system (DietEatDoTPatch,
/// DietMealEatDoTPatch) -- this reads TransitionLevel directly via the same public
/// UpdateAndGetTransitionState call vanilla's own tryEatStop uses for its own satiety/health
/// spoilage falloff, not anything DietProfileRegistry resolves.
///
/// Decay-then-add, exponential half-life on the in-game calendar clock, computed lazily on
/// every write (no new tick loop) -- see DietSetupConfig.RotIntakeHalfLifeHours's doc comment
/// for why calendar-hours rather than real-world time.
/// </summary>
internal static class RotIntakeAccrual
{
    /// <summary>
    /// transitionLevel &lt;= 0 (fresh food) contributes nothing and skips the write entirely --
    /// eating fresh food should not reset/decay the accumulator faster than time alone already
    /// does.
    /// </summary>
    public static void AccrueRotIntake(EntityPlayer player, float transitionLevel)
    {
        if (transitionLevel <= 0f) return;

        DietSetupConfig cfg = DietSetupModSystem.Config;
        if (!cfg.EnableRotIntakeTracking) return;

        ITreeAttribute wa = player.WatchedAttributes;
        double nowHours = player.World.Calendar.TotalHours;
        double lastHours = wa.GetDouble(DietSetupModSystem.AttrRotIntakeUpdatedHours, nowHours);
        double raw = wa.GetDouble(DietSetupModSystem.AttrRotIntake, 0.0);

        double decayed = raw * Math.Pow(0.5, Math.Max(0.0, nowHours - lastHours) / cfg.RotIntakeHalfLifeHours);
        double next = Math.Min(cfg.RotIntakeCap, decayed + cfg.RotIntakePerBite * transitionLevel);

        wa.SetDouble(DietSetupModSystem.AttrRotIntake, next);
        wa.SetDouble(DietSetupModSystem.AttrRotIntakeUpdatedHours, nowHours);
    }
}

/// <summary>Standalone eating -- mirrors vanilla's own tryEatStop spoilage read exactly
/// (Collectible.cs:1858-1859), just repurposing the same TransitionLevel for a different
/// accumulator instead of the satiety/health falloff vanilla computes from it. Same Harmony
/// target as DietEatDoTPatch, independent postfix.</summary>
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
/// Meal eating. Same Harmony target as DietMealEatDoTPatch, independent postfix -- BlockMeal
/// never calls tryEatStop, so it needs its own hook. Per the freshness investigation, all
/// installed cooking recipes pool freshness across the whole pot at cook completion
/// (CollectibleObject.CarryOverFreshness) before it's stamped onto content stacks, so true
/// per-ingredient resolution doesn't survive cooking -- average TransitionLevel across the
/// meal's non-empty content stacks instead. Known, accepted limitation (matches the existing
/// freshness investigation's conclusion), not a regression this patch introduces.
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
