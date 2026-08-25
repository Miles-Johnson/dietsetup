namespace dietsetup.Rules;

/// <summary>One compiled rule. Specificity is the requires popcount, used only for the
/// load-time sort (spec section 3: most entries in requires wins, priority tiebreaks).</summary>
public readonly struct CompiledRule
{
    public readonly ulong RequiresMask;
    public readonly ulong ExcludesMask;
    public readonly int Specificity;
    public readonly int Priority;
    public readonly DietVerdict Verdict;
    public readonly CompiledValue Satiety;
    public readonly CompiledValue Nutrition;
    public readonly CompiledEffect[] Effects;
    public readonly string DebugLabel;

    public CompiledRule(ulong requiresMask, ulong excludesMask, int specificity, int priority, DietVerdict verdict, CompiledValue satiety, CompiledValue nutrition, CompiledEffect[] effects, string debugLabel)
    {
        RequiresMask = requiresMask;
        ExcludesMask = excludesMask;
        Specificity = specificity;
        Priority = priority;
        Verdict = verdict;
        Satiety = satiety;
        Nutrition = nutrition;
        Effects = effects;
        DebugLabel = debugLabel;
    }

    public bool Matches(ulong tagMask) => (tagMask & RequiresMask) == RequiresMask && (tagMask & ExcludesMask) == 0;
}
