using System;

namespace dietsetup.Rules;

/// <summary>The pure core (architecture 5.1): no ICoreAPI, no Entity, no IWorldAccessor, no static
/// mutable state, no clock, no random. Every caller does gather/resolve/apply outside this method
/// (5.3) -- a Harmony patch contains all three steps and no logic of its own.</summary>
public static class DietResolver
{
    // spoilLevel is part of the architecture-mandated signature (5.1) but this schema version has
    // no curve or spoil-sensitive field to evaluate it against -- reserved for a later effect
    // (7.3's rot accumulator). Unused on purpose, not a bug.
    public static DietResolveResult Resolve(CompiledDiet diet, ulong tagMask, float spoilLevel)
    {
        CompiledRule[] rules = diet.Rules;
        for (int i = 0; i < rules.Length; i++)
        {
            if (rules[i].Matches(tagMask))
            {
                CompiledRule winner = rules[i];
                return Apply(winner.Verdict, winner.SatietyMult, winner.NutritionMult, winner.Effects, matched: true);
            }
        }

        return Apply(DietVerdict.Edible, diet.FallbackSatietyMult, diet.FallbackNutritionMult, Array.Empty<CompiledEffect>(), matched: false);
    }

    // Architecture 5.2 steps 4-5: apply the winner's multipliers to the neutral (1.0) baseline,
    // then clamp both at 0. The only two multiply sites in the mod -- see the standing rule at
    // architecture 5.4 that anything else touching satiety/nutrition/health is a defect.
    private static DietResolveResult Apply(DietVerdict verdict, float satietyMult, float nutritionMult, CompiledEffect[] effects, bool matched)
    {
        float satiety = 1f;
        satiety *= satietyMult;

        float nutrition = 1f;
        nutrition *= nutritionMult;

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
