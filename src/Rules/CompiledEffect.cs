namespace dietsetup.Rules;

/// <summary>One compiled effect entry from a rule's effects list (architecture 7.1/7.2). Not
/// applied anywhere yet (phase 5) -- compiled and validated only, so /dietshow can print it and
/// validation rules 7/9/13 have something to check.</summary>
public readonly struct CompiledEffect
{
    public readonly DietEffectType Type;
    public readonly float Amount;
    public readonly string? Mode;
    public readonly DietVerdict? Verdict;
    public readonly string? CustomKey;
    public readonly IDietCustomEffect? CustomEffect;

    public CompiledEffect(DietEffectType type, float amount, string? mode, DietVerdict? verdict, string? customKey, IDietCustomEffect? customEffect)
    {
        Type = type;
        Amount = amount;
        Mode = mode;
        Verdict = verdict;
        CustomKey = customKey;
        CustomEffect = customEffect;
    }
}
