using System;

namespace dietsetup.Rules;

/// <summary>The pure core (architecture 5.1): no ICoreAPI, no Entity, no IWorldAccessor, no static
/// mutable state, no clock, no random. Every caller does gather/resolve/apply outside this method
/// (5.3) -- a Harmony patch contains all three steps and no logic of its own.</summary>
public static class DietResolver
{
    // spoilLevel now drives CompiledValue.Evaluate for the winning (or fallback) rule's
    // satiety/nutrition -- restored 2026-09-04, the effect this parameter was reserved for
    // when curve capability was removed from the schema at ee2f142.
    public static DietResolveResult Resolve(CompiledDiet diet, ulong tagMask, float spoilLevel)
    {
        CompiledRule[] rules = diet.Rules;
        for (int i = 0; i < rules.Length; i++)
        {
            if (rules[i].Matches(tagMask))
            {
                CompiledRule winner = rules[i];
                return Apply(winner.Verdict, winner.SatietyMult, winner.NutritionMult, spoilLevel, winner.Effects, matched: true);
            }
        }

        return Apply(DietVerdict.Edible, CompiledValue.Flat(diet.FallbackSatietyMult), CompiledValue.Flat(diet.FallbackNutritionMult), spoilLevel, Array.Empty<CompiledEffect>(), matched: false);
    }

    // Architecture 5.2 steps 4-5: evaluate the winner's satiety/nutrition (flat or curve) against
    // spoilLevel, then clamp both at 0. The only two evaluate sites in the mod -- see the standing
    // rule at architecture 5.4 that anything else touching satiety/nutrition/health is a defect.
    private static DietResolveResult Apply(DietVerdict verdict, CompiledValue satietyValue, CompiledValue nutritionValue, float spoilLevel, CompiledEffect[] effects, bool matched)
    {
        float satiety = satietyValue.Evaluate(spoilLevel);
        float nutrition = nutritionValue.Evaluate(spoilLevel);

        // Architecture 7.5: Inedible contributes zero regardless of the rule's authored
        // multipliers -- a reaction-axis verdict, not a second place to author the same number.
        if (verdict == DietVerdict.Inedible)
        {
            satiety = 0f;
            nutrition = 0f;
        }

        return new DietResolveResult(verdict, Math.Max(0f, satiety), Math.Max(0f, nutrition), effects, matched);
    }
}
