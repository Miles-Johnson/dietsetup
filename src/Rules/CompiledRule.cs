namespace dietsetup.Rules;

/// <summary>One compiled rule. Specificity is the requires popcount -- architecture 5.2 step 2
/// sorts on priority first, specificity second, declaration order last.</summary>
public readonly struct CompiledRule
{
    public readonly ulong RequiresMask;
    public readonly ulong ExcludesMask;
    public readonly int Specificity;
    public readonly int Priority;
    public readonly DietVerdict Verdict;
    public readonly CompiledValue SatietyMult;
    public readonly CompiledValue NutritionMult;
    public readonly CompiledEffect[] Effects;
    public readonly string DebugLabel;
    public readonly bool ShadowedIntentionally;

    public CompiledRule(ulong requiresMask, ulong excludesMask, int specificity, int priority, DietVerdict verdict, CompiledValue satietyMult, CompiledValue nutritionMult, CompiledEffect[] effects, string debugLabel, bool shadowedIntentionally = false)
    {
        RequiresMask = requiresMask;
        ExcludesMask = excludesMask;
        Specificity = specificity;
        Priority = priority;
        Verdict = verdict;
        SatietyMult = satietyMult;
        NutritionMult = nutritionMult;
        Effects = effects;
        DebugLabel = debugLabel;
        ShadowedIntentionally = shadowedIntentionally;
    }

    public bool Matches(ulong tagMask) => (tagMask & RequiresMask) == RequiresMask && (tagMask & ExcludesMask) == 0;
}
