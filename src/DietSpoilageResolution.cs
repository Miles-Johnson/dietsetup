using System;
using dietsetup.Binding;
using dietsetup.Rules;
using dietsetup.Tags;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace dietsetup;

/// <summary>
/// Sole satiety-axis fold site (not DietSatietyFold -- see its doc comment). Architecture 5.4 named
/// the GetNutritionProperties family as the fold point because that's where Satiety appears
/// syntactically, not because that's where the mask is complete -- this site is the only one with a
/// live spoil state on every path (a real ItemSlot for a standalone eat, vanilla's own DummySlot for
/// the liquid-container/meal paths, same fidelity vanilla itself has, never guessed), so it's the
/// only one that can see a fresh- or spoiled-keyed rule at all.
///
/// Known gap, not a regression from this being the sole owner but now load-bearing where it used to
/// be a second, partial fold: BlockPie.cs:442 (GetNutritionHealthMul, 1.22) passes the outer pie
/// stack to FoodSpoilageSatLossMul, not the filling being eaten (notes/1.22-verification.md, Items 9
/// and 11) -- a pie's mask is whatever the pie item's own code carries (typically just the 'meal'
/// form tag), so a rule keyed on a filling's source tag (meat, raw, etc.) can never match and pie
/// satiety silently falls through to the diet's fallback. BlockLiquidContainerBase.cs:822 and
/// BlockMeal.cs:508 pass the correct per-content/per-ingredient stack (BlockMeal via
/// DietMealContentNutritionPatch's full replacement); CollectibleObject.cs:1787/1957 have no
/// container indirection to get wrong. See mods/dietsetup/README.md's known-limitations entry.
///
/// Shared by both GlobalConstants.FoodSpoilageSatLossMul and FoodSpoilageHealthLossMul postfixes --
/// health has no authored curve of its own (spec section 6, amended prompt 7 target 2: "no new rule
/// field in v1, health is derived from satiety, not authored"), it mirrors this same resolve. Vanilla
/// always calls SatLossMul immediately before HealthLossMul with the identical (stack, spoilState) on
/// every path (notes/1.22-verification.md Item 1's caller table) -- cached below so the second call
/// reads the first's result instead of resolving again, rather than two calls that happen to agree.
///
/// Gated on DietResolveResult.Matched: only overrides vanilla's own spoilage curve when an
/// authored rule actually won. "base" (no rules, fallback 1.0/1.0) always falls through to the
/// fallback branch with Matched=false, so an unconfigured player keeps vanilla's built-in
/// freshness falloff untouched -- architecture 4.4, "vanilla numbers and nothing else." Without
/// this gate, base's flat 1.0 fallback would silently erase vanilla's decay curve for everyone.
/// </summary>
internal static class DietSpoilageResolution
{
    // Keyed on stack reference + spoilState + entity + ambient pie context (2026-09-04: widened
    // after a live bug -- a pie filling's spoilState stays pinned near 0 by UnspoilContents and its
    // ItemStack reference is stable for the pie's life, so the narrower (stack, spoilState) key let
    // a stale result computed under one pieFillingPieStack/pieFillingPieSpoilLevel replay under a
    // later, different one, silently skipping a rule that should now match. The same narrower key
    // also dropped byEntity, so two different entities' diets could share one cached verdict. Both
    // are folded into one key rather than fixed separately -- both are "this cache forgot something
    // that changes the answer." [ThreadStatic] for the same reason as the context fields below:
    // client and server share a process in singleplayer.
    [ThreadStatic]
    private static ItemStack? cachedStack;
    [ThreadStatic]
    private static float cachedSpoilState;
    [ThreadStatic]
    private static EntityAgent? cachedEntity;
    [ThreadStatic]
    private static ItemStack? cachedPieStack;
    [ThreadStatic]
    private static float cachedPieSpoilLevel;
    [ThreadStatic]
    private static DietResolveResult cachedResult;
    [ThreadStatic]
    private static bool cacheValid;

    // [ThreadStatic], same reasoning as DietMealFactsContext.DisplayOnly: client and server share a
    // process in singleplayer, so a plain static here could let a client-side tooltip read observe
    // or clobber a concurrent server-side real eat's pie context.
    [ThreadStatic]
    private static ItemStack? pieFillingPieStack;
    [ThreadStatic]
    private static float pieFillingPieSpoilLevel;

    // Shared by both cache readers so they can never drift onto different key shapes -- see the
    // 2026-09-04 fix note above the cache fields.
    private static bool CacheMatches(ItemStack? stack, float spoilState, EntityAgent? byEntity) =>
        cacheValid
        && ReferenceEquals(cachedStack, stack)
        && cachedSpoilState == spoilState
        && ReferenceEquals(cachedEntity, byEntity)
        && ReferenceEquals(cachedPieStack, pieFillingPieStack)
        && cachedPieSpoilLevel == pieFillingPieSpoilLevel;

    /// <summary>Set only by DietMealContentNutritionPatch's per-filling loop around its BlockPie
    /// fillings -- while set, TryResolveSatietyMultiplier reads the filling's state axis off this
    /// pie instead of the filling itself (FoodTagRegistry.GetPieFillingTagMask). Unset (the default)
    /// for every other caller, including BlockPie.cs:442's own pie-level resolve, which keeps
    /// resolving against the pie's own mask exactly as before.</summary>
    public static void SetPieFillingContext(ItemStack pieStack, float pieSpoilLevel)
    {
        pieFillingPieStack = pieStack;
        pieFillingPieSpoilLevel = pieSpoilLevel;
    }

    public static void ClearPieFillingContext()
    {
        pieFillingPieStack = null;
    }

    public static bool TryResolveSatietyMultiplier(float spoilState, ItemStack? stack, EntityAgent? byEntity, out float satietyMult)
    {
        satietyMult = 0f;
        if (!DietSetupModSystem.Config.EnableDietSystem) return false;
        if (stack?.Collectible == null) return false;

        DietResolveResult resolved;
        if (CacheMatches(stack, spoilState, byEntity))
        {
            resolved = cachedResult;
        }
        else
        {
            CompiledDiet? diet = DietIdResolver.ResolveDiet(byEntity);
            if (diet == null)
            {
                cacheValid = false;
                return false;
            }

            ulong tagMask = pieFillingPieStack != null
                ? FoodTagRegistry.GetPieFillingTagMask(stack.Collectible, pieFillingPieStack.Collectible, pieFillingPieSpoilLevel)
                : FoodTagRegistry.GetTagMaskForSpoilState(stack.Collectible, spoilState);

            // A pie filling's own spoilState is pinned near 0 by UnspoilContents (see class doc),
            // so a spoil-keyed rule must evaluate against the pie's own age, not the filling's.
            float dietSpoilLevel = pieFillingPieStack != null ? pieFillingPieSpoilLevel : spoilState;
            resolved = DietResolver.Resolve(diet, tagMask, dietSpoilLevel);

            cachedStack = stack;
            cachedSpoilState = spoilState;
            cachedEntity = byEntity;
            cachedPieStack = pieFillingPieStack;
            cachedPieSpoilLevel = pieFillingPieSpoilLevel;
            cachedResult = resolved;
            cacheValid = true;
        }

        if (!resolved.Matched) return false;

        satietyMult = resolved.Satiety;
        return true;
    }

    /// <summary>Read-only peek at the cache TryResolveSatietyMultiplier just populated for the same
    /// key (see CacheMatches) -- used by DietMealContentNutritionPatch to pull the same resolve's
    /// Verdict and Effects immediately after its own FoodSpoilageSatLossMul call, instead of
    /// resolving a second time for the same ingredient. Unlike TryResolveSatietyMultiplier, not
    /// gated on Matched -- a caller here wants the full result (including an unmatched fallback's
    /// empty effects list), not just an override-worthy satiety multiplier.</summary>
    public static bool TryGetLastResolved(ItemStack? stack, float spoilState, EntityAgent? byEntity, out DietResolveResult result)
    {
        // Not default(DietResolveResult): that zeroes Effects to null rather than calling the
        // constructor, an NRE waiting for any caller that reads result without checking the bool.
        result = new DietResolveResult(DietVerdict.Edible, 1f, 1f, Array.Empty<CompiledEffect>(), false);
        // Must be called while the caller's own pie context (if any) is still set -- CacheMatches
        // compares against the *current* ambient pieFillingPieStack, so calling this after
        // ClearPieFillingContext would never match a pie filling's own entry (2026-09-04).
        if (!CacheMatches(stack, spoilState, byEntity)) return false;

        result = cachedResult;
        return true;
    }
}
