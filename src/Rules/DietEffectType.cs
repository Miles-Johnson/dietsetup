namespace dietsetup.Rules;

/// <summary>Closed set of core effect types (architecture 7.1) plus Custom, the open-registry
/// escape hatch (7.2, DietEffects.Register) -- an unknown type string is validation rule 7 (fatal),
/// a Custom key with no registered handler is rule 13 (warning).</summary>
public enum DietEffectType
{
    SatietyMult,
    NutritionMult,
    Verdict,
    Damage,
    Custom
}
