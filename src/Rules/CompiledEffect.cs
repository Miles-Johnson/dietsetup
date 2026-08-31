namespace dietsetup.Rules;

/// <summary>damage's two modes (architecture 7.1). Instant applies Amount as one immediate hit;
/// overTime spreads it across DurationSec via vanilla's own DamageOverTimeTypeEnum, replacing the
/// old profile system's Reaction/DamageOverTime split with one authored list.</summary>
public enum DietDamageMode
{
    Instant,
    OverTime
}

/// <summary>One compiled effect entry from a rule's effects list (architecture 7.1/7.2), fired by
/// DietEffectRunner at the eat/meal apply sites (task 2) -- never applied inside DietResolver
/// itself (rule 4: effects fire after the resolve, not inside it).</summary>
public readonly struct CompiledEffect
{
    public readonly DietEffectType Type;
    public readonly float Amount;
    public readonly string? Mode;
    public readonly DietVerdict? Verdict;
    public readonly string? CustomKey;
    public readonly IDietConsequenceEffect? CustomEffect;

    // Damage-only (7.1's "mode: instant or overTime"). DamageMode is null for every other type.
    public readonly DietDamageMode? DamageMode;
    public readonly float DurationSec;
    public readonly int Ticks;

    public CompiledEffect(DietEffectType type, float amount, string? mode, DietVerdict? verdict, string? customKey, IDietConsequenceEffect? customEffect, DietDamageMode? damageMode = null, float durationSec = 0f, int ticks = 1)
    {
        Type = type;
        Amount = amount;
        Mode = mode;
        Verdict = verdict;
        CustomKey = customKey;
        CustomEffect = customEffect;
        DamageMode = damageMode;
        DurationSec = durationSec;
        Ticks = ticks;
    }
}
