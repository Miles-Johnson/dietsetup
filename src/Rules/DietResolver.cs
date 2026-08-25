using System;
using System.Collections.Generic;
using dietsetup.Tags;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace dietsetup.Rules;

/// <summary>The one evaluation path (spec sections 1, 5). Called from the /dietresolve
/// diagnostic command and, via DietSpoilageResolution, from DietSpoilageSatietyPatch,
/// DietSpoilageHealthPatch, and DietMealContentNutritionPatch (prompt 7, spoilage-curve sites)
/// -- all go through the same two overloads below, never a second copy of the stage-1/stage-2
/// logic.</summary>
public static class DietResolver
{
    /// <summary>matchedRuleIndices is null on the hot path (no allocation): pass a reusable list
    /// only from diagnostic callers that want to print which rules fired.</summary>
    public static DietResolveResult Resolve(ICoreAPI api, IWorldAccessor world, ItemSlot slot, CompiledDiet diet, Entity? forEntity, float portionSize = 1f, List<int>? matchedRuleIndices = null)
    {
        ulong tagMask = FoodTagRegistry.GetTagMask(world, slot, out float spoilLevel, out bool determined);
        if (!determined) return DietResolveResult.Undetermined;

        return Resolve(diet, tagMask, spoilLevel, api, forEntity, portionSize, matchedRuleIndices);
    }

    /// <summary>Core evaluation for a tag mask and spoil level already known by the caller --
    /// e.g. GlobalConstants.FoodSpoilageSatLossMul's own spoilState parameter (no ItemSlot exists
    /// at that patch site) or a meal's per-ingredient tags evaluated against the meal-wide pooled
    /// spoil level (spec section 7). The slot-based overload above derives tagMask/spoilLevel from
    /// a live transition-state read and calls this.</summary>
    public static DietResolveResult Resolve(CompiledDiet diet, ulong tagMask, float spoilLevel, ICoreAPI api, Entity? forEntity, float portionSize = 1f, List<int>? matchedRuleIndices = null)
    {
        DietVerdict verdict;
        float satiety;
        float nutrition;
        CompiledEffect[] effects;

        if (diet.Degraded)
        {
            // Section 11: a missing custom effect key degrades the whole diet to default
            // behaviour at eval, not just the offending rule -- so rule matching is skipped
            // entirely here, not just its effects.
            verdict = diet.DefaultVerdict;
            satiety = diet.DefaultSatiety.Evaluate(spoilLevel);
            nutrition = diet.DefaultNutrition.Evaluate(spoilLevel);
            effects = diet.DefaultEffects;
        }
        else
        {
            CompiledRule[] rules = diet.Rules;
            int winnerIndex = -1;
            float satietySum = 0f;
            float nutritionSum = 0f;

            for (int i = 0; i < rules.Length; i++)
            {
                if (!rules[i].Matches(tagMask)) continue;

                matchedRuleIndices?.Add(i);
                if (winnerIndex < 0) winnerIndex = i; // rules are pre-sorted specificity desc, priority desc -- first hit is stage 1's winner
                satietySum += rules[i].Satiety.Evaluate(spoilLevel);
                nutritionSum += rules[i].Nutrition.Evaluate(spoilLevel);
            }

            if (winnerIndex >= 0)
            {
                verdict = rules[winnerIndex].Verdict;
                satiety = satietySum;
                nutrition = nutritionSum;
                effects = rules[winnerIndex].Effects; // effects ride with the verdict, not summed across every match
            }
            else
            {
                verdict = diet.DefaultVerdict;
                satiety = diet.DefaultSatiety.Evaluate(spoilLevel);
                nutrition = diet.DefaultNutrition.Evaluate(spoilLevel);
                effects = diet.DefaultEffects;
            }
        }

        var acc = new DietEffectAccumulator
        {
            Verdict = verdict,
            Satiety = Math.Max(0f, satiety),
            Nutrition = Math.Max(0f, nutrition),
        };

        for (int i = 0; i < effects.Length; i++)
        {
            ApplyEffect(effects[i], api, forEntity, portionSize, ref acc);
        }

        // Race traits as a resolver input (tag-engine step 9), not a second multiply outside the
        // rules engine -- satiety-axis only, see FoodTagRegistry.TagNutritionMultiplier for the
        // nutrition-axis counterpart, applied only at the eat/meal queue producers.
        FoodTagRegistry.ApplySatietyTagMultiplier(tagMask, forEntity, ref acc.Satiety);

        return new DietResolveResult(true, acc.Verdict, Math.Max(0f, acc.Satiety), Math.Max(0f, acc.Nutrition), acc.DamageMagnitude, acc.DamageDurationSec);
    }

    private static void ApplyEffect(in CompiledEffect effect, ICoreAPI api, Entity? forEntity, float portionSize, ref DietEffectAccumulator acc)
    {
        switch (effect.Type)
        {
            case DietEffectType.SatietyMult:
                acc.Satiety *= effect.Amount;
                break;
            case DietEffectType.NutritionMult:
                acc.Nutrition *= effect.Amount;
                break;
            case DietEffectType.Edible:
                acc.Verdict = DietVerdict.Edible;
                break;
            case DietEffectType.Inedible:
                acc.Verdict = DietVerdict.Inedible;
                break;
            case DietEffectType.DamageOverTime:
                float magnitude = effect.Amount * portionSize;
                if (forEntity != null) magnitude = ClampDamageForEntity(forEntity, magnitude);
                acc.DamageMagnitude += magnitude;
                acc.DamageDurationSec = Math.Max(acc.DamageDurationSec, effect.DurationSec);
                break;
            case DietEffectType.StatModifier:
            case DietEffectType.Hydration:
                // Compiled and validated, but nothing applies these live yet -- that lands with
                // the Harmony patches (prompt 7+), out of scope for this no-patches session.
                break;
            case DietEffectType.Custom:
                effect.CustomEffect?.Apply(api, forEntity, portionSize, ref acc);
                break;
        }
    }

    /// <summary>Mirrors DietProfileRegistry.ClampReactionMagnitude's floor -- diet damage must
    /// never be the hit that kills, across either evaluation path.</summary>
    private static float ClampDamageForEntity(Entity forEntity, float magnitude)
    {
        if (magnitude <= 0f) return 0f;
        float currentHealth = forEntity.GetBehavior<EntityBehaviorHealth>()?.Health ?? 20f;
        float maxAllowed = Math.Max(0f, currentHealth - 2.0f);
        return Math.Min(magnitude, maxAllowed);
    }
}
