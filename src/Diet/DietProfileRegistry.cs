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
/// Central registry + resolver for diet profiles, tags and grant rules. Replaces
/// DietCategoryHelper. The reverse tag->items index is rebuilt lazily behind a dirty flag on
/// first use after any RegisterTag call -- there's no fixed "build late" lifecycle point, since
/// any fixed point can be beaten by another mod's ModSystem.Start() registering a tag even later.
/// </summary>
public static class DietProfileRegistry
{
    private static readonly Dictionary<string, DietProfile> profiles = new();
    private static readonly Dictionary<string, List<string>> tagPatterns = new();
    private static readonly List<DietGrantRule> grantRules = new();
    // Keyed by AssetLocation (not string) so the per-resolve tag-multiplier lookup in
    // ResolveNutritionProperties -- which runs on every call, including the hot path -- never
    // pays AssetLocation.ToString()'s allocation. Only EnsureIndexFresh's rebuild (behind the
    // dirty flag) needs the string form, once per collectible per rebuild.
    private static readonly Dictionary<AssetLocation, HashSet<string>> itemToTags = new();
    // "dietsetup:<tag>Mult" per registered tag, precomputed once in RegisterTag so
    // GetTagMultiplier never concatenates strings on the hot path.
    private static readonly Dictionary<string, string> tagStatKeys = new();
    private static bool dirty = true;

    // Marks clones this resolver has already produced, so the BlockLiquidContainerBase postfix's
    // fallback path (which calls the already-patched base.GetNutritionProperties internally, then
    // fires its own postfix on the same returned object) can't apply a grant or reaction twice.
    private static readonly ConditionalWeakTable<FoodNutritionProperties, object> Processed = new();
    private static readonly object ProcessedMarker = new();

    // Hand-off from ResolveNutritionProperties (which knows a reaction should be a DoT, but only
    // gets to mutate the FoodNutritionProperties clone) to DietEatDoTPatch (which patches
    // CollectibleObject.tryEatStop and can actually call ReceiveDamage with a Duration) and
    // DietMealEatDoTPatch (same idea for BlockMeal.tryFinishEatMeal). Keyed by entity so it
    // survives the trip through vanilla's own eat-completion body without needing to thread a new
    // parameter through vanilla's method signature. A list, not a single value: one bite of a meal
    // can resolve several ingredients, each queuing its own reaction.
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

    // Hand-off from DietMealNutritionPatch (which resolves one meal ingredient stack at a time,
    // via BlockMeal.GetIngredientStackNutritionProperties) to DietMealContentNutritionPatch (which
    // sees the whole ingredient array at once, via BlockMeal.GetContentNutritionProperties, and is
    // the only place that can compute a reaction's share of the meal). Keyed by entity, one entry
    // per ingredient stack, in call order -- see DietMealContentNutritionPatch for how it's drained.
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
    // value (e.g. a death cap mushroom) is never touched, so it stays exactly as lethal as in the
    // base game. Callers decide clamp-eligibility themselves (see ResolveNutritionProperties's
    // reactionSourced out param) since a magnitude computed from vanilla Health should never reach
    // here in the first place. No-ops for non-negative magnitudes.
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

    /// <summary>The actual per-eat resolution path: honors the "legacy_custom" sentinel by
    /// building a profile from the entity's own legacy attributes on the fly (see DietMigration),
    /// falls back to defaultProfileId, and as a last resort a pure-passthrough profile so this
    /// never returns null even on a fresh install with no profiles registered yet.</summary>
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

    /// <summary>Shared resolver called by both the CollectibleObject and BlockLiquidContainerBase
    /// GetNutritionProperties postfixes (queueReaction: true), and by DietMealNutritionPatch for
    /// individual meal ingredients (queueReaction: false -- meal-wide reaction magnitude is
    /// computed by DietMealContentNutritionPatch instead, from the notionalSatiety/queuedReaction/
    /// reactionSourced this method hands back for every ingredient). Does exactly two things: fills
    /// in a category for items vanilla has no nutrition data for (grant rules), and writes reaction
    /// damage into Health. For every other call -- the overwhelming majority, since most foods are
    /// neither granted nor reaction-triggering -- it returns vanillaResult completely untouched,
    /// with no clone and no allocation, since GetNutritionProperties is also hit by
    /// GetHeldTpUseAnimation and the tooltip path on every single held food item.
    ///
    /// The three out params are deliberately non-optional -- every call site must be touched and
    /// reviewed, since this method is already-verified production code (see the meal-fix handover
    /// notes) and a silently-inherited default here would be easy to get wrong:
    /// - notionalSatiety: this ingredient's satiety as far as a meal-wide weighting computation is
    ///   concerned -- tag-mult-adjusted but captured *before* a firing reaction would zero it, since
    ///   weighting off the already-zeroed value would make every reacting ingredient's weight 0.
    ///   Defaults to vanillaResult's own satiety for every early-return path below, since those all
    ///   mean "dietsetup doesn't adjust this ingredient" but it must still count toward a meal's
    ///   satiety total.
    /// - queuedReaction: the *unclamped* DoT-shaped reaction this call would queue, when one fires
    ///   with DurationSec > 0. Unclamped deliberately -- the entity-relative safety clamp below is
    ///   only correct to apply once, after a meal-wide weighted magnitude is known, not per
    ///   ingredient and then scaled down again.
    /// - reactionSourced: whether queuedReaction's magnitude came from the diet reaction itself
    ///   (clamp-eligible) rather than from the food's own inherent vanilla Health value (never
    ///   clamped, e.g. a death cap mushroom stays exactly as lethal inside a meal as standalone).
    /// </summary>
    public static FoodNutritionProperties? ResolveNutritionProperties(ICoreAPI api, Entity forEntity, CollectibleObject collectible, FoodNutritionProperties? vanillaResult, string defaultProfileId, bool queueReaction, out DietReaction? queuedReaction, out float notionalSatiety, out bool reactionSourced)
    {
        queuedReaction = null;
        reactionSourced = false;
        notionalSatiety = vanillaResult?.Satiety ?? 0f;

        // Vanilla genuinely calls GetNutritionProperties with a null entity in several real paths
        // (CanSpoil, GetHeldInteractionHelp, BlockMeal.GetContentNutritionFacts). Today the calling
        // postfixes' "forEntity is not EntityPlayer" guard already filters null out before this
        // method is ever reached (a type-pattern test against null is always a non-match) -- this
        // is an explicit, direct safeguard so that behavior doesn't depend on that guard staying
        // exactly as-is.
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

        // The category-level reaction (profile fundamentally incompatible with this category)
        // takes priority. Otherwise, a grant can carry its own reaction that fires only when the
        // profile's category default is still at full, unadapted baseline -- see
        // DietGrantRule.Reaction. This never double-fires: reactionFires already covers a
        // zeroed-and-reacting category (e.g. Herbivore on Protein), so the grant branch is only
        // reachable when the category default is untouched (e.g. Balanced on Protein).
        DietReaction? firingReaction = reactionFires
            ? catDefault.Reaction
            : (isGrant && grantReaction != null && catDefault.SatietyMult >= 1f && catDefault.NutritionMult >= 1f ? grantReaction : null);

        // Manual shallow copy, not vanillaResult.Clone() -- vanilla's FoodNutritionProperties.Clone()
        // deep-clones EatenStack via JsonItemStack.Clone(), which NREs on some liquids whose vanilla
        // EatenStack has a null Code. EatenStack is never read or written below, so reusing it by
        // reference is safe and sidesteps the vanilla bug entirely.
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
                // Only the diet-sourced portion is clamped -- a food's own vanilla Health value
                // (e.g. a death cap mushroom) is never touched here, so it stays exactly as lethal
                // as in the base game.
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

    /// <summary>Per-entity, per-tag satiety multiplier sourced from arbitrary-mod-namespaced entity
    /// stats (e.g. a race trait's "dietsetup:mushroomMult"), reusing the itemToTags reverse index
    /// LookupGrant also uses. Short-circuits to 1f (no Stats.GetBlended calls at all) whenever the
    /// item matches no registered tag, which is the overwhelming majority of items -- this is the
    /// actual fast path for players/items not using this feature.
    ///
    /// Note: EntityStats.Set defaults to EnumStatBlendType.WeightedSum, which seeds a "base" entry
    /// of Value=1 -- so a value set via the vanilla trait system (CharacterSystem applying trait
    /// Attributes) is *added to* 1, not read as an absolute multiplier. A trait wanting "+30%
    /// benefit" must author 0.3 in JSON, not 1.3.</summary>
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
