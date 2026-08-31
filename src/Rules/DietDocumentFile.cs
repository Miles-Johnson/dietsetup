using System;
using System.Collections.Generic;

namespace dietsetup.Rules;

/// <summary>One diet document (architecture 4.2) -- assets/&lt;domain&gt;/config/diets/&lt;id&gt;.json,
/// or a whole-file override at ModConfig/dietsetup/diets/&lt;id&gt;.json. Raw/pre-extends shape: field
/// absence is meaningful (nullable) so DietCompiler can tell "not set, use the default" apart from
/// "explicitly set to the default value," which the scope-violation checks (rules 5/6) depend on.</summary>
public class DietDocumentFile
{
    public int? SchemaVersion { get; set; }
    public string? Id { get; set; }
    public string? Extends { get; set; }
    public Dictionary<string, DietCategoryFile> Categories { get; set; } = new();
    public DietFallbackFile? Fallback { get; set; }
    public DietRuleFileEntry[] Rules { get; set; } = Array.Empty<DietRuleFileEntry>();
}

public class DietCategoryFile
{
    public float? Capacity { get; set; }
    public float? DrainRate { get; set; }

    // Wrong-scope fields (validation rule 6) -- a category block must never set a rule multiplier.
    public float? SatietyMult { get; set; }
    public float? NutritionMult { get; set; }
}

public class DietFallbackFile
{
    public float? SatietyMult { get; set; }
    public float? NutritionMult { get; set; }
}

public class DietRuleFileEntry
{
    public string[]? Requires { get; set; }
    public string[]? Excludes { get; set; }
    public int? Priority { get; set; }
    public string? Verdict { get; set; }
    public float? SatietyMult { get; set; }
    public float? NutritionMult { get; set; }
    public DietEffectFile[]? Effects { get; set; }

    // Wrong-scope field (validation rule 5) -- a rule must never set capacity.
    public float? Capacity { get; set; }
}

/// <summary>One entry in rules[].effects (architecture 7.1/7.2). Fields are a union across every
/// effect type; only the ones relevant to Type are read.</summary>
public class DietEffectFile
{
    public string Type { get; set; } = "";
    public string? Mode { get; set; }
    public float? Amount { get; set; }
    public string? Verdict { get; set; }
    public string? Key { get; set; }

    // damage/overTime only -- ignored for mode instant, which applies Amount as one immediate hit.
    public float? DurationSec { get; set; }
    public int? Ticks { get; set; }
}
