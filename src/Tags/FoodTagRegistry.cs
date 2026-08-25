using System;
using System.Collections.Generic;
using System.Numerics;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Util;

namespace dietsetup.Tags;

/// <summary>
/// Three-axis (source/state/form) tag registry for the diet rules engine (prompt 6, not built
/// yet). Static tags (source, most of state, form) resolve once per collectible id at
/// AssetsFinalize into a 64-bit bitmask array. Dynamic tags (fresh/spoiled) resolve per stack
/// from its live transition state. No rule matching lives here.
/// </summary>
public static class FoodTagRegistry
{
    public const int MaxTags = 64;
    public const string FreshTag = "fresh";
    public const string SpoiledTag = "spoiled";

    private static readonly Dictionary<string, int> tagBits = new();
    private static readonly Dictionary<string, FoodTagAxis> tagAxis = new();
    private static readonly Dictionary<string, List<string>> tagPatterns = new();

    // "dietsetup:<tag>Mult" per registered tag, bit-indexed (not dictionary-keyed) so the hot-path
    // fold in ApplySatietyTagMultiplier/TagNutritionMultiplier never allocates or hashes -- folds
    // the retired DietProfileRegistry.GetTagMultiplier into this registry (tag-engine step 9).
    private static readonly string?[] tagStatKeysByBit = new string?[MaxTags];

    // Keyed by (isBlock, id) -- Item.Id and Block.Id share the same low-id range (see
    // itemMasks/blockMasks note below), so a plain id would conflate an item and a block.
    private static readonly HashSet<(bool isBlock, int id)> loggedTransitionFailures = new();

    // Item.Id and Block.Id are separate id spaces (both start near 0) -- api.World.Collectibles
    // is just Items followed by Blocks, so a single array indexed by CollectibleObject.Id would
    // have every low item id silently overwritten by an unrelated block sharing that same id.
    private static ulong[] itemMasks = Array.Empty<ulong>();
    private static ulong[] blockMasks = Array.Empty<ulong>();
    private static ulong sourceAxisMask;

    // Global source-tag -> vanilla nutrient bar mapping (spec section 2). Fixed, not
    // per-diet and not compat-pack-extensible in v1 -- a third-party source tag with no
    // entry here just has no bar association yet.
    private static readonly Dictionary<string, EnumFoodCategory> SourceBar = new()
    {
        ["meat"] = EnumFoodCategory.Protein,
        ["organ"] = EnumFoodCategory.Protein,
        ["blood"] = EnumFoodCategory.Protein,
        ["carrion"] = EnumFoodCategory.Protein,
        ["fish"] = EnumFoodCategory.Protein,
        ["insect"] = EnumFoodCategory.Protein,
        ["egg"] = EnumFoodCategory.Protein,
        ["dairy"] = EnumFoodCategory.Dairy,
        ["grain"] = EnumFoodCategory.Grain,
        ["seed"] = EnumFoodCategory.Grain,
        ["root"] = EnumFoodCategory.Vegetable,
        ["leaf"] = EnumFoodCategory.Vegetable,
        ["fruit"] = EnumFoodCategory.Fruit,
        ["nut"] = EnumFoodCategory.Fruit,
        ["sap"] = EnumFoodCategory.Fruit,
        ["resin"] = EnumFoodCategory.Fruit,
        ["bone"] = EnumFoodCategory.Vegetable,
        ["mineral"] = EnumFoodCategory.Vegetable,
    };

    // fresh/spoiled always occupy the first two bits, reserved before any config loads,
    // so their bit positions don't depend on load order.
    static FoodTagRegistry()
    {
        EnsureBit(FreshTag, FoodTagAxis.State);
        EnsureBit(SpoiledTag, FoodTagAxis.State);
    }

    public static IEnumerable<string> AllTagNames => tagBits.Keys;

    public static EnumFoodCategory? NutrientBarFor(string sourceTag) =>
        SourceBar.TryGetValue(sourceTag, out EnumFoodCategory bar) ? bar : null;

    /// <summary>Bit index for a registered tag name, for the rules engine to compile
    /// requires/excludes into masks at load. False for an unregistered (likely typo'd) tag.</summary>
    public static bool TryGetBit(string tag, out int bit) => tagBits.TryGetValue(tag, out bit);

    /// <summary>Merges one config/foodtags.json's worth of tag definitions into the registry.
    /// Every declared tag reserves a bit even with zero patterns, so a rule can reference a
    /// tag the current mod set has no matching item for yet (e.g. "organ") without erroring.</summary>
    public static void LoadFrom(FoodTagConfigFile file)
    {
        LoadAxis(file.Source, FoodTagAxis.Source);
        LoadAxis(file.State, FoodTagAxis.State);
        LoadAxis(file.Form, FoodTagAxis.Form);
    }

    private static void LoadAxis(Dictionary<string, string[]> tags, FoodTagAxis axis)
    {
        foreach ((string tag, string[] patterns) in tags)
        {
            EnsureBit(tag, axis);
            foreach (string pattern in patterns)
            {
                if (!tagPatterns.TryGetValue(tag, out List<string>? list))
                {
                    tagPatterns[tag] = list = new List<string>();
                }
                list.Add(pattern);
            }
        }
    }

    private static int EnsureBit(string tag, FoodTagAxis axis)
    {
        if (tagBits.TryGetValue(tag, out int existing))
        {
            if (tagAxis[tag] != axis)
            {
                throw new InvalidOperationException(
                    $"[dietsetup] Tag '{tag}' registered under axis '{axis}' but was already registered as '{tagAxis[tag]}'.");
            }
            return existing;
        }

        if (tagBits.Count >= MaxTags)
        {
            throw new InvalidOperationException(
                $"[dietsetup] Cannot register tag '{tag}': the {MaxTags}-tag mask is already full.");
        }

        int bit = tagBits.Count;
        tagBits[tag] = bit;
        tagAxis[tag] = axis;
        tagStatKeysByBit[bit] = "dietsetup:" + tag + "Mult";
        if (axis == FoodTagAxis.Source)
        {
            sourceAxisMask |= 1UL << bit;
        }
        return bit;
    }

    /// <summary>Per-entity, per-tag satiety fold from namespaced entity stats (e.g. a race trait's
    /// "dietsetup:preservedMult") -- replaces the retired DietProfileRegistry.GetTagMultiplier.
    /// No-op for a null entity or empty mask (the common case). Gotcha: EntityStats.Set seeds a
    /// WeightedSum base of 1 -- author 0.3 for "+30%", not 1.3.</summary>
    public static void ApplySatietyTagMultiplier(ulong tagMask, Entity? forEntity, ref float satiety)
    {
        if (forEntity == null || tagMask == 0) return;

        float floor = DietSetupModSystem.Config.TagMultiplierFloor;
        ulong remaining = tagMask;
        while (remaining != 0)
        {
            int bit = BitOperations.TrailingZeroCount(remaining);
            remaining &= remaining - 1;
            string? statKey = tagStatKeysByBit[bit];
            if (statKey == null) continue;

            // Floor before multiplying, not after: stacked negative trait deltas on a single tag
            // can blend below 0, and multiplying two already-negative tags back to positive would
            // hide that instead of correcting it.
            satiety *= Math.Max(floor, forEntity.Stats.GetBlended(statKey));
        }
    }

    /// <summary>Same fold as <see cref="ApplySatietyTagMultiplier"/>, for the nutrition-gain axis
    /// (spec/step-9 design 2) -- returns the combined multiplier instead of mutating by ref, since
    /// callers combine it with a rule-matched Nutrition value before enqueueing (DietSetupConfig's
    /// nutrition-multiplier queue), not apply it in place.</summary>
    public static float TagNutritionMultiplier(ulong tagMask, Entity? forEntity)
    {
        if (forEntity == null || tagMask == 0) return 1f;

        float floor = DietSetupModSystem.Config.TagMultiplierFloor;
        float mult = 1f;
        ulong remaining = tagMask;
        while (remaining != 0)
        {
            int bit = BitOperations.TrailingZeroCount(remaining);
            remaining &= remaining - 1;
            string? statKey = tagStatKeysByBit[bit];
            if (statKey == null) continue;

            mult *= Math.Max(floor, forEntity.Stats.GetBlended(statKey));
        }
        return mult;
    }

    /// <summary>Walks every collectible once, matching each registered static tag's wildcard
    /// patterns against its item code, and stores one bitmask per collectible id. Call from
    /// AssetsFinalize, on both sides -- api.World.Collectibles isn't populated any earlier.</summary>
    public static void ResolveStaticTags(ICoreAPI api)
    {
        var patternArrays = new Dictionary<string, string[]>(tagPatterns.Count);
        foreach ((string tag, List<string> patterns) in tagPatterns)
        {
            patternArrays[tag] = patterns.ToArray();
        }

        // whole has no patterns of its own (config/foodtags.json's "_note") -- an item-code
        // wildcard can't express "not ground, not liquid, not meal", so it's the form-axis
        // default here instead, for any already-relevant item matching none of the other three.
        ulong formOtherMask = 0;
        int wholeBit = -1;
        foreach ((string tag, int bit) in tagBits)
        {
            if (!tagAxis.TryGetValue(tag, out FoodTagAxis axis) || axis != FoodTagAxis.Form) continue;
            if (tag == "whole") { wholeBit = bit; continue; }
            formOtherMask |= 1UL << bit;
        }

        itemMasks = new ulong[api.World.Items.Count];
        blockMasks = new ulong[api.World.Blocks.Count];

        foreach (CollectibleObject collectible in api.World.Collectibles)
        {
            AssetLocation? code = collectible.Code;
            if (code == null) continue;
            string codeStr = code.ToString();

            ulong mask = 0;
            foreach ((string tag, string[] patterns) in patternArrays)
            {
                if (patterns.Length > 0 && WildcardUtil.Match(patterns, codeStr))
                {
                    mask |= 1UL << tagBits[tag];
                }
            }

            bool relevant = (mask & sourceAxisMask) != 0 || collectible.NutritionProps != null;
            if (wholeBit >= 0 && relevant && (mask & formOtherMask) == 0)
            {
                mask |= 1UL << wholeBit;
            }

            if (collectible is Block)
            {
                blockMasks[collectible.Id] = mask;
            }
            else
            {
                itemMasks[collectible.Id] = mask;
            }

            if (collectible.NutritionProps != null && (mask & sourceAxisMask) == 0)
            {
                api.Logger.Warning("[dietsetup] Collectible '{0}' has nutrition properties but no source tag in the food tag registry.", codeStr);
            }
        }
    }

    public static ulong GetStaticMask(CollectibleObject collectible)
    {
        int id = collectible.Id;
        ulong[] table = collectible is Block ? blockMasks : itemMasks;
        return id >= 0 && id < table.Length ? table[id] : 0;
    }

    private static bool IsRelevant(ulong staticMask, CollectibleObject collectible) =>
        (staticMask & sourceAxisMask) != 0 || collectible.NutritionProps != null;

    /// <summary>Static mask plus the fresh/spoiled bit for a spoil level the caller already has
    /// (e.g. GlobalConstants.FoodSpoilageSatLossMul's own spoilState parameter) -- avoids
    /// re-deriving TransitionLevel through a synthetic ItemSlot, which has no real Inventory and
    /// so can't reproduce container-specific rot rates (crock dampening etc.) the original slot
    /// already applied.</summary>
    public static ulong GetTagMaskForSpoilState(CollectibleObject collectible, float spoilLevel)
    {
        ulong mask = GetStaticMask(collectible);
        if (!IsRelevant(mask, collectible)) return mask;

        mask |= 1UL << tagBits[spoilLevel > 0f ? SpoiledTag : FreshTag];
        return mask;
    }

    /// <summary>Static mask for the stack's collectible, plus fresh/spoiled read from its own
    /// live transition state. >0f TransitionLevel is spoiled; a clean null (e.g. game:resin, no
    /// Perish transition) resolves to fresh on purpose, not a failure. determined is false only
    /// if the engine call itself threw. Gated to the same relevance check as ResolveStaticTags --
    /// otherwise requires: ["fresh"] would match every non-food collectible.</summary>
    public static ulong GetTagMask(IWorldAccessor world, ItemSlot slot, out bool determined) =>
        GetTagMask(world, slot, out _, out determined);

    /// <summary>Same as <see cref="GetTagMask(IWorldAccessor, ItemSlot, out bool)"/>, plus the
    /// raw 0..1 spoil level the rules engine's curves evaluate against -- callers that only need
    /// the tag set (e.g. /diettags) can ignore it via the other overload.</summary>
    public static ulong GetTagMask(IWorldAccessor world, ItemSlot slot, out float spoilLevel, out bool determined)
    {
        determined = true;
        spoilLevel = 0f;
        ItemStack? stack = slot.Itemstack;
        if (stack?.Collectible == null) return 0;

        ulong mask = GetStaticMask(stack.Collectible);

        if (!IsRelevant(mask, stack.Collectible)) return mask;

        float? transitionLevel;
        try
        {
            transitionLevel = stack.Collectible.UpdateAndGetTransitionState(world, slot, EnumTransitionType.Perish)?.TransitionLevel;
        }
        catch (Exception ex)
        {
            determined = false;
            LogTransitionFailureOnce(world, stack.Collectible, ex);
            return mask;
        }

        spoilLevel = transitionLevel ?? 0f;
        mask |= 1UL << tagBits[spoilLevel > 0f ? SpoiledTag : FreshTag];

        return mask;
    }

    private static void LogTransitionFailureOnce(IWorldAccessor world, CollectibleObject collectible, Exception ex)
    {
        var key = (collectible is Block, collectible.Id);
        if (!loggedTransitionFailures.Add(key)) return;
        world.Logger.Error("[dietsetup] GetTagMask: transition state read failed for '{0}': {1}", collectible.Code, ex);
    }

    public static IEnumerable<string> TagNames(ulong mask)
    {
        foreach ((string tag, int bit) in tagBits)
        {
            if ((mask & (1UL << bit)) != 0)
            {
                yield return tag;
            }
        }
    }
}
