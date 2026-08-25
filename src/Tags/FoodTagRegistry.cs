using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
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
        if (axis == FoodTagAxis.Source)
        {
            sourceAxisMask |= 1UL << bit;
        }
        return bit;
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

    /// <summary>Static mask for the stack's collectible, plus fresh/spoiled read from its own
    /// live transition state -- never from the item code. TransitionLevel reads exactly 0f for
    /// the whole fresh window (confirmed notes/1.22-verification.md item 6), so >0f is spoiled.
    /// determined is false only if the engine call itself threw -- a genuinely unavailable
    /// read, which the caller must not treat as resolved. A clean null (e.g. game:resin, which
    /// has no Perish transition at all) is a real, permanent answer, not a failure, and
    /// resolves to fresh on purpose so an elf's "requires: fresh" rule matches it.</summary>
    public static ulong GetTagMask(IWorldAccessor world, ItemSlot slot, out bool determined)
    {
        determined = true;
        ItemStack? stack = slot.Itemstack;
        if (stack?.Collectible == null) return 0;

        ulong mask = GetStaticMask(stack.Collectible);

        float? transitionLevel;
        try
        {
            transitionLevel = stack.Collectible.UpdateAndGetTransitionState(world, slot, EnumTransitionType.Perish)?.TransitionLevel;
        }
        catch (Exception)
        {
            determined = false;
            return mask;
        }

        mask |= 1UL << tagBits[transitionLevel > 0f ? SpoiledTag : FreshTag];

        return mask;
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
