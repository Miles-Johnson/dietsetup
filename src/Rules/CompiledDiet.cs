using System;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace dietsetup.Rules;

public sealed class CompiledDiet
{
    public string Id = "";
    public string SourceDomain = "";

    public Dictionary<EnumFoodCategory, CompiledCategory> Categories = new();

    public float FallbackSatietyMult = 1f;
    public float FallbackNutritionMult = 1f;

    /// <summary>Sorted once at compile time: priority descending, then requires-popcount
    /// (specificity) descending, then declaration order (architecture 5.2 step 2) -- DietResolver's
    /// first match in this order is the winner.</summary>
    public CompiledRule[] Rules = Array.Empty<CompiledRule>();
}

/// <summary>Capacity plus its two derived values (architecture 2.2, 5.6). HealthWeight always
/// equals Capacity by construction (2.3's standing rule) -- kept as a separate field anyway so a
/// caller never has to know that, per rule 13 (derived values are derived, but a *cached* derived
/// value is still fine -- the rule bans authoring one beside the other in JSON, not caching it).</summary>
public readonly struct CompiledCategory
{
    public readonly float Capacity;
    public readonly float NutritionGainScale;
    public readonly float HealthWeight;

    public CompiledCategory(float capacity, float nutritionGainScale, float healthWeight)
    {
        Capacity = capacity;
        NutritionGainScale = nutritionGainScale;
        HealthWeight = healthWeight;
    }
}
