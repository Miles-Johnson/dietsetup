using System;

namespace dietsetup.Rules;

public sealed class CompiledDiet
{
    public string Id = "";
    public string SourceDomain = "";
    public bool HiddenFromPicker;

    /// <summary>Set when a rule referenced an unregistered custom effect key (spec section 11).
    /// The diet stays loaded but DietResolver skips rule matching entirely and always applies
    /// the default -- HiddenFromPicker is also forced true so nobody new selects it.</summary>
    public bool Degraded;

    public DietVerdict DefaultVerdict;
    public CompiledValue DefaultSatiety;
    public CompiledValue DefaultNutrition;
    public CompiledEffect[] DefaultEffects = Array.Empty<CompiledEffect>();

    /// <summary>Sorted once at compile time: specificity descending, then priority descending
    /// (spec section 3) -- DietResolver's first match in this order is the stage-1 verdict winner.</summary>
    public CompiledRule[] Rules = Array.Empty<CompiledRule>();
}
