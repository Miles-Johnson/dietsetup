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
    public readonly float SatietyMult;
    public readonly float NutritionMult;
    public readonly CompiledEffect[] Effects;
    public readonly string DebugLabel;

    public CompiledRule(ulong requiresMask, ulong excludesMask, int specificity, int priority, DietVerdict verdict, float satietyMult, float nutritionMult, CompiledEffect[] effects, string debugLabel)
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
    }

    public bool Matches(ulong tagMask) => (tagMask & RequiresMask) == RequiresMask && (tagMask & ExcludesMask) == 0;
}
