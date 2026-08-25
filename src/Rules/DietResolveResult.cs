namespace dietsetup.Rules;

/// <summary>Per-bite, per-ingredient answer from DietResolver.Resolve. No allocation on the
/// hot path -- see DietResolver's matchedRuleIndices param for the diagnostic-only alternative.</summary>
public readonly struct DietResolveResult
{
    public static readonly DietResolveResult Undetermined = new(false, DietVerdict.Edible, 0f, 0f, 0f, 0f);

    public readonly bool Determined;
    public readonly DietVerdict Verdict;
    public readonly float Satiety;
    public readonly float Nutrition;
    public readonly float DamageMagnitude;
    public readonly float DamageDurationSec;

    public DietResolveResult(bool determined, DietVerdict verdict, float satiety, float nutrition, float damageMagnitude, float damageDurationSec)
    {
        Determined = determined;
        Verdict = verdict;
        Satiety = satiety;
        Nutrition = nutrition;
        DamageMagnitude = damageMagnitude;
        DamageDurationSec = damageDurationSec;
    }
}
