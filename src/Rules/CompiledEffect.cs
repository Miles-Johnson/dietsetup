namespace dietsetup.Rules;

/// <summary>One compiled effect entry from a rule or a diet's default. CustomEffect is resolved
/// once at compile time (spec section 4's named registry) so eval never does a dictionary lookup.</summary>
public readonly struct CompiledEffect
{
    public readonly DietEffectType Type;
    public readonly float Amount;
    public readonly float DurationSec;
    public readonly string? StatKey;
    public readonly string? CustomKey;
    public readonly IDietCustomEffect? CustomEffect;

    public CompiledEffect(DietEffectType type, float amount, float durationSec, string? statKey, string? customKey, IDietCustomEffect? customEffect)
    {
        Type = type;
        Amount = amount;
        DurationSec = durationSec;
        StatKey = statKey;
        CustomKey = customKey;
        CustomEffect = customEffect;
    }
}
