using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace dietsetup.Diet;

/// <summary>
/// Central registry + resolver for diet profiles, tags and grant rules. The reverse tag->items
/// index is rebuilt lazily behind a dirty flag on first use after any RegisterTag call -- there's
/// no fixed "build late" point, since a fixed point can be beaten by another mod registering a tag later.
/// </summary>
public static class DietProfileRegistry
{
    private static readonly Dictionary<string, DietProfile> profiles = new();
    private static readonly Dictionary<string, List<string>> tagPatterns = new();
    private static readonly List<DietGrantRule> grantRules = new();
    // Keyed by AssetLocation, not string -- the hot-path tag-multiplier lookup in
    // ResolveNutritionProperties never pays AssetLocation.ToString()'s allocation this way.
    private static readonly Dictionary<AssetLocation, HashSet<string>> itemToTags = new();
    // "dietsetup:<tag>Mult" per registered tag, precomputed once in RegisterTag so
    // GetTagMultiplier never concatenates strings on the hot path.
    private static readonly Dictionary<string, string> tagStatKeys = new();
    private static bool dirty = true;

    // Marks clones this resolver already produced, so BlockLiquidContainerBase's fallback (which
    // calls the already-patched base.GetNutritionProperties internally, then fires its own
    // postfix on the same object) can't apply a grant/reaction twice.
    private static readonly ConditionalWeakTable<FoodNutritionProperties, object> Processed = new();
    private static readonly object ProcessedMarker = new();

    // Hand-off from ResolveNutritionProperties (mutates only the FoodNutritionProperties clone) to
    // DietEatDoTPatch/DietMealEatDoTPatch (which can actually call ReceiveDamage with a Duration).
    // Keyed by entity id; a list because one bite can resolve several reacting ingredients.
    private static readonly Dictionary<long, List<DietReaction>> PendingDoT = new();
    private static readonly List<DietReaction> EmptyDoTList = new();

    public static void ClearPendingDoT(long entityId) => PendingDoT.Remove(entityId);

    internal static void AddPendingDoT(long entityId, DietReaction reaction)
    {
        if (!PendingDoT.TryGetValue(entityId, out List<DietReaction>? list))
        {
            PendingDoT[entityId] = list = new List<DietReaction>();
        }
        list.Add(reaction);
    }

    public static List<DietReaction> TakePendingDoT(long entityId) =>
        PendingDoT.Remove(entityId, out List<DietReaction>? found) ? found : EmptyDoTList;

    // Hand-off from DietMealNutritionPatch (resolves one ingredient at a time) to
    // DietMealContentNutritionPatch (sees the whole bowl, computes each reaction's meal share).
    // Keyed by entity, one entry per ingredient stack, in call order.
    private static readonly Dictionary<long, List<(float NotionalSatiety, DietReaction? Reaction, bool ReactionSourced)>> MealIngredientContext = new();

    public static void ClearMealIngredientContext(long entityId) => MealIngredientContext.Remove(entityId);

    internal static void AddMealIngredientContext(long entityId, float notionalSatiety, DietReaction? reaction, bool reactionSourced)
    {
        if (!MealIngredientContext.TryGetValue(entityId, out var list))
        {
            MealIngredientContext[entityId] = list = new();
        }
        list.Add((notionalSatiety, reaction, reactionSourced));
    }

    public static List<(float NotionalSatiety, DietReaction? Reaction, bool ReactionSourced)> TakeMealIngredientContext(long entityId) =>
        MealIngredientContext.Remove(entityId, out var found) ? found : new();

    // Only the diet-sourced portion of a reaction is ever clamped -- a food's own vanilla Health
    // (e.g. a death cap mushroom) is never touched, so it stays exactly as lethal as base game.
    // Callers decide clamp-eligibility (see ResolveNutritionProperties's reactionSourced out param).
    internal static float ClampReactionMagnitude(Entity forEntity, float magnitude)
    {
        if (magnitude >= 0f) return magnitude;
        float currentHealth = forEntity.GetBehavior<EntityBehaviorHealth>()?.Health ?? 20f;
        float maxAllowedMagnitude = Math.Max(0f, currentHealth - 2.0f);
        return Math.Max(magnitude, -maxAllowedMagnitude);
    }

    public static void RegisterProfile(DietProfile profile)
    {
        if (profile.Id == DietMigration.LegacyCustomProfileId)
        {
            throw new ArgumentException($"'{DietMigration.LegacyCustomProfileId}' is a reserved profile id used internally for legacy-player migration and cannot be registered.");
        }
        profiles[profile.Id] = profile;
    }

    public static void RegisterTag(string tag, string pattern)
    {
        if (!tagPatterns.TryGetValue(tag, out List<string>? patterns))
        {
            tagPatterns[tag] = patterns = new List<string>();
            tagStatKeys[tag] = "dietsetup:" + tag + "Mult";
        }
        patterns.Add(pattern);
        dirty = true;
    }

    public static void RegisterGrantRule(DietGrantRule rule) => grantRules.Add(rule);

    /// <summary>Pure registry lookup -- does not know about the "legacy_custom" sentinel. Used to
    /// validate an id a player/API caller is trying to assign.</summary>
    public static DietProfile? GetProfile(string id) => profiles.TryGetValue(id, out DietProfile? p) ? p : null;

    public static IEnumerable<DietProfile> PickerProfiles => profiles.Values.Where(p => !p.HiddenFromPicker);

    /// <summary>Unfiltered, unlike PickerProfiles -- for startup validation, which needs to check
    /// every registered profile including hidden ones.</summary>
    public static IEnumerable<DietProfile> AllProfiles => profiles.Values;

    public static IEnumerable<string> AllTagNames => tagPatterns.Keys;

    public static IEnumerable<DietGrantRule> AllGrantRules => grantRules;

    /// <summary>Honors the "legacy_custom" sentinel by building a profile from the entity's own
    /// legacy attributes on the fly (see DietMigration), falls back to defaultProfileId, and as a
    /// last resort a pure-passthrough profile so this never returns null.</summary>
    public static DietProfile ResolveProfileForEntity(Entity entity, string defaultProfileId)
    {
        string profileId = entity.WatchedAttributes.GetString("dietsetup:profile", defaultProfileId);
        if (profileId == DietMigration.LegacyCustomProfileId)
        {
            return DietMigration.BuildLegacyCustomProfile(entity.WatchedAttributes);
        }
        return GetProfile(profileId) ?? GetProfile(defaultProfileId) ?? PassThroughProfile;
    }

    private static readonly DietProfile PassThroughProfile = new() { Id = "__passthrough", HiddenFromPicker = true };

    /// <summary>Shared resolver for both GetNutritionProperties postfixes (queueReaction: true)
    /// and DietMealNutritionPatch's per-ingredient calls (queueReaction: false). Fills in a
    /// category for grant-only items and writes reaction damage into Health; returns vanillaResult
    /// untouched (no clone/alloc) for the common non-granted, non-reacting case. Design rationale:
    /// notes/dietsetup-patch-internals.md#resolve-nutrition-properties--dietprofileregistrycs.
    ///
    /// The three out params are non-optional -- every call site must handle them explicitly.
    /// notionalSatiety: this ingredient's satiety for meal-wide weighting, captured before a firing
    /// reaction could zero it.
    /// queuedReaction: the *unclamped* reaction magnitude -- the safety clamp applies once, after
    /// meal-wide weighting, not per ingredient.
    /// reactionSourced: whether the magnitude came from the diet reaction (clamp-eligible) vs. the
    /// food's own vanilla Health (never clamped).
    /// </summary>
    public static FoodNutritionProperties? ResolveNutritionProperties(ICoreAPI api, Entity forEntity, CollectibleObject collectible, FoodNutritionProperties? vanillaResult, string defaultProfileId, bool queueReaction, out DietReaction? queuedReaction, out float notionalSatiety, out bool reactionSourced)
    {
        queuedReaction = null;
        reactionSourced = false;
        notionalSatiety = vanillaResult?.Satiety ?? 0f;

        // Vanilla genuinely calls this with a null entity in several real paths (CanSpoil,
        // GetHeldInteractionHelp, GetContentNutritionFacts). Callers already filter null via their
        // own EntityPlayer guard -- this is a direct safeguard so that isn't the only thing preventing an NRE.
        if (forEntity == null)
        {
            return vanillaResult;
        }

        EnsureIndexFresh(api);

        if (vanillaResult != null && Processed.TryGetValue(vanillaResult, out _))
        {
            return vanillaResult;
        }

        string category;
        float baseSatiety;
        bool isGrant;
        DietReaction? grantReaction = null;

        if (vanillaResult != null)
        {
            category = vanillaResult.FoodCategory.ToString();
            baseSatiety = vanillaResult.Satiety;
            isGrant = false;
        }
        else
        {
            (string Category, float BaseSatiety, DietReaction? Reaction)? grant = LookupGrant(collectible);
            if (grant == null)
            {
                return null; // genuinely inedible, nothing to grant -- vanilla behavior preserved
            }
            category = grant.Value.Category;
            baseSatiety = grant.Value.BaseSatiety;
            grantReaction = grant.Value.Reaction;
            isGrant = true;
        }

        if (category is not ("Fruit" or "Vegetable" or "Protein" or "Grain" or "Dairy"))
        {
            return vanillaResult; // Unknown/NoNutrition -- no profile governs it, pure passthrough
        }

        DietProfile profile = ResolveProfileForEntity(forEntity, defaultProfileId);
        DietCategoryDefault catDefault = profile.CategoryDefaults.TryGetValue(category, out DietCategoryDefault? cd) ? cd : DietCategoryDefault.PassThrough;

        bool reactionFires = catDefault.SatietyMult == 0f && catDefault.NutritionMult == 0f && catDefault.Reaction != null;
        float tagMult = GetTagMultiplier(forEntity, collectible.Code);

        if (!isGrant && !reactionFires && tagMult == 1f)
        {
            return vanillaResult; // no-op: nothing this resolver needs to change
        }

        // The category-level reaction (profile incompatible with this whole category) takes
        // priority; a grant's own reaction only fires when the category default is still
        // untouched baseline. Never double-fires: reactionFires already covers the zeroed-and-reacting case.
        DietReaction? firingReaction = reactionFires
            ? catDefault.Reaction
            : (isGrant && grantReaction != null && catDefault.SatietyMult >= 1f && catDefault.NutritionMult >= 1f ? grantReaction : null);

        // Manual shallow copy, not vanillaResult.Clone() -- vanilla's Clone() deep-clones
        // EatenStack via JsonItemStack.Clone(), which NREs for some liquids with a null Code.
        // EatenStack is never read/written below, so reusing it by reference sidesteps the bug.
        FoodNutritionProperties clone = vanillaResult == null
            ? new FoodNutritionProperties()
            : new FoodNutritionProperties
            {
                FoodCategory = vanillaResult.FoodCategory,
                Satiety = vanillaResult.Satiety,
                Health = vanillaResult.Health,
                Intoxication = vanillaResult.Intoxication,
                SaturationLossDelay = vanillaResult.SaturationLossDelay,
                EatenStack = vanillaResult.EatenStack,
            };

        if (isGrant)
        {
            clone.FoodCategory = Enum.Parse<EnumFoodCategory>(category);
            clone.Satiety = baseSatiety;
        }

        // Harmless *1f no-op when this clone only exists because of a grant/reaction with no
        // active tag-mult source. If a reaction below zeroes Satiety, multiplying first vs. after
        // makes no observable difference.
        clone.Satiety *= tagMult;
        // Captured here, not after a firing reaction potentially zeroes Satiety below -- see the
        // notionalSatiety doc comment on this method for why.
        notionalSatiety = clone.Satiety;

        if (firingReaction != null)
        {
            float reactionDamage = firingReaction.Health;
            float vanillaHealth = clone.Health;
            // Strict > so an exact tie keeps vanilla's value, never the reaction's.
            float winner = Math.Abs(reactionDamage) > Math.Abs(vanillaHealth) ? reactionDamage : vanillaHealth;
            float rawWinner = winner; // pre-clamp, handed to meal-aggregate callers via queuedReaction
            bool isReactionSourced = winner == reactionDamage;
            if (isReactionSourced)
            {
                // Only the diet-sourced portion is clamped -- see ClampReactionMagnitude.
                winner = ClampReactionMagnitude(forEntity, winner);
            }

            if (reactionFires)
            {
                // Only the zeroed-category case reads as "no benefit" -- a grant-only reaction
                // (e.g. Balanced eating raw meat) is still nourishing, just risky, so its satiety
                // is left as the grant set it above.
                clone.Satiety = 0f;
            }

            if (firingReaction.DurationSec > 0f)
            {
                // Suppress vanilla's own instant ReceiveDamage in tryEatStop -- DietEatDoTPatch
                // applies this as a damage-over-time effect instead once tryEatStop completes.
                clone.Health = 0f;
                queuedReaction = new DietReaction { Health = rawWinner, DurationSec = firingReaction.DurationSec, Ticks = firingReaction.Ticks };
                reactionSourced = isReactionSourced;
                if (queueReaction)
                {
                    AddPendingDoT(forEntity.EntityId, new DietReaction { Health = winner, DurationSec = firingReaction.DurationSec, Ticks = firingReaction.Ticks });
                }
            }
            else
            {
                clone.Health = winner;
            }
        }

        Processed.AddOrUpdate(clone, ProcessedMarker);
        return clone;
    }

    private static (string Category, float BaseSatiety, DietReaction? Reaction)? LookupGrant(CollectibleObject collectible)
    {
        string itemCode = collectible.Code?.ToString() ?? "";
        foreach (DietGrantRule rule in grantRules)
        {
            if (rule.ItemPattern != null && WildcardUtil.Match(rule.ItemPattern, itemCode))
            {
                return (rule.Category, rule.BaseSatiety, rule.Reaction);
            }
        }
        foreach (DietGrantRule rule in grantRules)
        {
            if (rule.Tag != null && collectible.Code != null
                && itemToTags.TryGetValue(collectible.Code, out HashSet<string>? tags) && tags.Contains(rule.Tag))
            {
                return (rule.Category, rule.BaseSatiety, rule.Reaction);
            }
        }
        return null;
    }

    private static void EnsureIndexFresh(ICoreAPI api)
    {
        if (!dirty) return;

        itemToTags.Clear();
        foreach (CollectibleObject collectible in api.World.Collectibles)
        {
            AssetLocation? code = collectible.Code;
            if (code == null) continue;
            string codeStr = code.ToString(); // paid once per collectible per rebuild only

            foreach ((string tag, List<string> patterns) in tagPatterns)
            {
                if (WildcardUtil.Match(patterns.ToArray(), codeStr))
                {
                    if (!itemToTags.TryGetValue(code, out HashSet<string>? tags))
                    {
                        itemToTags[code] = tags = new HashSet<string>();
                    }
                    tags.Add(tag);
                }
            }
        }

        dirty = false;
    }

    /// <summary>Per-entity, per-tag satiety multiplier from namespaced entity stats (e.g. a race
    /// trait's "dietsetup:mushroomMult"). Short-circuits to 1f for unmatched items (the fast path
    /// for most). Gotcha: EntityStats.Set seeds a WeightedSum base of 1 -- author 0.3 for "+30%", not 1.3.</summary>
    private static float GetTagMultiplier(Entity forEntity, AssetLocation? code)
    {
        if (code == null || !itemToTags.TryGetValue(code, out HashSet<string>? tags) || tags.Count == 0)
        {
            return 1f;
        }

        float mult = 1f;
        foreach (string tag in tags)
        {
            if (tagStatKeys.TryGetValue(tag, out string? statKey))
            {
                mult *= forEntity.Stats.GetBlended(statKey);
            }
        }
        return mult;
    }
}
