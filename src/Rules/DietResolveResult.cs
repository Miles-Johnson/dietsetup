namespace dietsetup.Rules;

/// <summary>Answer from DietResolver.Resolve. Satiety/Nutrition are multipliers, not absolute
/// values -- a caller multiplies them into the food's own vanilla value (architecture 5.3's "apply"
/// step). Health is never carried here; it derives from capacity and is never authored (5.1).</summary>
public readonly struct DietResolveResult
{
    public readonly DietVerdict Verdict;
    public readonly float Satiety;
    public readonly float Nutrition;
    public readonly CompiledEffect[] Effects;

    public DietResolveResult(DietVerdict verdict, float satiety, float nutrition, CompiledEffect[] effects)
    {
        Verdict = verdict;
        Satiety = satiety;
        Nutrition = nutrition;
        Effects = effects;
    }
}
