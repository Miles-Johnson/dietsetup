using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using dietsetup;
using dietsetup.Rules;
using dietsetup.Tags;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace dietsetup.Diet;

/// <summary>
/// Central registry + resolver for diet profiles. Tag matching itself lives in
/// dietsetup.Tags.FoodTagRegistry (the three-axis registry the rules engine also uses) -- this
/// class no longer keeps a separate tag index (tag-engine migration step 9).
/// </summary>
public static class DietProfileRegistry
{
    private static readonly Dictionary<string, DietProfile> profiles = new();

    // Per-entity FIFO of combined nutrition-gain multipliers (tag-fold * rule-matched Nutrition),
    // one entry per real eaten ingredient, consumed in order by DietSaturationScalePatch. Server
    // side only (see the enqueue sites' IServerWorldAccessor guard) -- a client-side tooltip
    // render must never write here, since singleplayer runs client+server DietSetupModSystem
    // instances in the same process sharing this same static dictionary.
    private static readonly Dictionary<long, Queue<float>> PendingNutritionMultipliers = new();

    public static void ClearNutritionMultiplierQueue(long entityId)
    {
        if (PendingNutritionMultipliers.TryGetValue(entityId, out Queue<float>? queue))
        {
            queue.Clear();
        }
    }

    /// <summary>Full removal, not just Clear -- called on player disconnect so a departed
    /// player's entry doesn't sit in this dictionary forever.</summary>
    public static void RemoveNutritionMultiplierQueue(long entityId) => PendingNutritionMultipliers.Remove(entityId);

    // One-shot per entity for ResolveProfileForEntity's orphaned-profile-id warning (server side
    // only -- see the Side gate there for why, same client/server static-state sharing as above).
    private static readonly HashSet<long> warnedMissingProfileEntities = new();

    public static void ClearWarnedMissingProfile(long entityId) => warnedMissingProfileEntities.Remove(entityId);

    internal static void EnqueueNutritionMultiplier(long entityId, float value)
    {
        if (!PendingNutritionMultipliers.TryGetValue(entityId, out Queue<float>? queue))
        {
            PendingNutritionMultipliers[entityId] = queue = new Queue<float>();
        }
        if (queue.Count >= DietSetupModSystem.Config.NutritionMultiplierQueueCap)
        {
            queue.Dequeue(); // defensive only -- see DietSetupConfig.NutritionMultiplierQueueCap
        }
        queue.Enqueue(value);
    }

    public static bool TryDequeueNutritionMultiplier(long entityId, out float value)
    {
        if (PendingNutritionMultipliers.TryGetValue(entityId, out Queue<float>? queue) && queue.Count > 0)
        {
            value = queue.Dequeue();
            return true;
        }
        value = 1f;
        return false;
    }

    // Marks clones this resolver already produced, so BlockLiquidContainerBase's fallback (which
    // calls the already-patched base.GetNutritionProperties internally, then fires its own
    // postfix on the same object) can't apply a reaction twice.
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

    /// <summary>Pure registry lookup -- does not know about the "legacy_custom" sentinel. Used to
    /// validate an id a player/API caller is trying to assign.</summary>
    public static DietProfile? GetProfile(string id) => profiles.TryGetValue(id, out DietProfile? p) ? p : null;

    public static IEnumerable<DietProfile> PickerProfiles => profiles.Values.Where(p => !p.HiddenFromPicker);

    /// <summary>Unfiltered, unlike PickerProfiles -- for startup validation, which needs to check
    /// every registered profile including hidden ones.</summary>
    public static IEnumerable<DietProfile> AllProfiles => profiles.Values;

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

        DietProfile? resolved = GetProfile(profileId);
        if (resolved != null) return resolved;

        DietProfile? fallback = GetProfile(defaultProfileId);
        if (profileId != defaultProfileId && entity.Api?.Side == EnumAppSide.Server
            && warnedMissingProfileEntities.Add(entity.EntityId))
        {
            string outcome = fallback != null
                ? $"falling back to defaultProfileId '{defaultProfileId}'"
                : $"defaultProfileId '{defaultProfileId}' is also not loaded -- resolving as pure vanilla nutrition, no diet effects";
            entity.Api?.Logger.Warning(
                "[dietsetup] Entity {0} has dietsetup:profile '{1}', which is not a loaded profile -- {2}.",
                entity.EntityId, profileId, outcome);
        }
        return fallback ?? PassThroughProfile;
    }

    private static readonly DietProfile PassThroughProfile = new() { Id = "__passthrough", HiddenFromPicker = true };

    /// <summary>Shared resolver for both GetNutritionProperties postfixes (queueReaction: true)
    /// and DietMealNutritionPatch's per-ingredient calls (queueReaction: false). The diet rules
    /// engine is the sole authority on whether an item is food at all: vanilla-inedible stays
    /// inedible (no grant fallback), and an assigned diet can additionally verdict a
    /// vanilla-edible item Inedible. Otherwise writes reaction damage into Health; returns
    /// vanillaResult untouched (no clone/alloc) for the common non-reacting case. Design
    /// rationale: notes/dietsetup-patch-internals.md#resolve-nutrition-properties--dietprofileregistrycs.
    ///
    /// The three out params are non-optional -- every call site must handle them explicitly.
    /// notionalSatiety: this ingredient's satiety for meal-wide weighting, captured before a firing
    /// reaction could zero it.
    /// queuedReaction: the *unclamped* reaction magnitude -- the safety clamp applies once, after
    /// meal-wide weighting, not per ingredient.
    /// reactionSourced: whether the magnitude came from the diet reaction (clamp-eligible) vs. the
    /// food's own vanilla Health (never clamped).
    /// </summary>
    public static FoodNutritionProperties? ResolveNutritionProperties(ICoreAPI api, Entity forEntity, CollectibleObject collectible, ItemStack? stack, FoodNutritionProperties? vanillaResult, string defaultProfileId, bool queueReaction, out DietReaction? queuedReaction, out float notionalSatiety, out bool reactionSourced)
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

        if (vanillaResult != null && Processed.TryGetValue(vanillaResult, out _))
        {
            return vanillaResult;
        }

        if (vanillaResult == null)
        {
            return null; // genuinely inedible -- vanilla behavior preserved, no grant fallback
        }

        // Resolved once here, reused below for both the Inedible check and (when no rules-engine
        // diet is assigned) the tag fold -- avoids a second FoodTagRegistry.GetTagMask call, which
        // does a live transition-state read.
        string dietId = forEntity.WatchedAttributes.GetString("dietsetup:profile", defaultProfileId);
        CompiledDiet? diet = DietRuleRegistry.GetDiet(dietId);
        var tagSlot = new DummySlot(stack ?? new ItemStack(collectible));
        ulong tagMask = FoodTagRegistry.GetTagMask(api.World, tagSlot, out float spoilLevel, out bool determined);

        if (IsInedibleForEntity(diet, tagMask, spoilLevel, determined, api, forEntity))
        {
            return null;
        }

        string category = vanillaResult.FoodCategory.ToString();

        if (category is not ("Fruit" or "Vegetable" or "Protein" or "Grain" or "Dairy"))
        {
            return vanillaResult; // Unknown/NoNutrition -- no profile governs it, pure passthrough
        }

        DietProfile profile = ResolveProfileForEntity(forEntity, defaultProfileId);
        DietCategoryDefault catDefault = profile.CategoryDefaults.TryGetValue(category, out DietCategoryDefault? cd) ? cd : DietCategoryDefault.PassThrough;

        bool reactionFires = catDefault.SatietyMult == 0f && catDefault.NutritionMult == 0f && catDefault.Reaction != null;

        // Only when no rules-engine diet is assigned -- an entity with a compiled diet already
        // gets this same fold inside DietResolver.Resolve (call site A). Applying it again here
        // would double the fold, rebuilding the exact bug this method exists to remove.
        float tagMult = 1f;
        if (diet == null)
        {
            FoodTagRegistry.ApplySatietyTagMultiplier(tagMask, forEntity, ref tagMult);
        }

        if (!reactionFires && tagMult == 1f)
        {
            return vanillaResult; // no-op: nothing this resolver needs to change
        }

        DietReaction? firingReaction = reactionFires ? catDefault.Reaction : null;

        // Manual shallow copy, not vanillaResult.Clone() -- vanilla's Clone() deep-clones
        // EatenStack via JsonItemStack.Clone(), which NREs for some liquids with a null Code.
        // EatenStack is never read/written below, so reusing it by reference sidesteps the bug.
        FoodNutritionProperties clone = new FoodNutritionProperties
        {
            FoodCategory = vanillaResult.FoodCategory,
            Satiety = vanillaResult.Satiety,
            Health = vanillaResult.Health,
            Intoxication = vanillaResult.Intoxication,
            SaturationLossDelay = vanillaResult.SaturationLossDelay,
            EatenStack = vanillaResult.EatenStack,
        };

        // Harmless *1f no-op when this clone only exists because of a reaction with no active
        // tag-mult source. If a reaction below zeroes Satiety, multiplying first vs. after makes
        // no observable difference.
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

            // firingReaction is only ever set from the zeroed-category branch above, so this is
            // always the "no benefit" case -- a category the profile rejects outright.
            clone.Satiety = 0f;

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

    /// <summary>Whether diet (the rules-engine diet assigned to forEntity, or null) verdicts this
    /// tagMask/spoilLevel Inedible. No diet, or an undetermined transition-state read, both fall
    /// through to false (old/vanilla edibility stands) -- this only ever narrows edibility, never
    /// grants it. tagMask/spoilLevel/determined are the caller's own already-resolved
    /// FoodTagRegistry.GetTagMask call, not re-derived here.</summary>
    private static bool IsInedibleForEntity(CompiledDiet? diet, ulong tagMask, float spoilLevel, bool determined, ICoreAPI api, Entity forEntity)
    {
        if (diet == null || !determined) return false;

        DietResolveResult result = DietResolver.Resolve(diet, tagMask, spoilLevel, api, forEntity);
        return result.Verdict == DietVerdict.Inedible;
    }
}
