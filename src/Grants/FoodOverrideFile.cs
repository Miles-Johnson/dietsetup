using System.Collections.Generic;

namespace dietsetup.Grants;

/// <summary>Raw shape of ModConfig/dietsetup/food-overrides.json (architecture 7.6) -- admin-authored
/// edibility grants for collectibles vanilla shipped with no nutritionProps. ModConfig only, never an
/// asset: a compat pack cannot ship grants in v1.</summary>
public class FoodOverrideDocumentFile
{
    public int? SchemaVersion { get; set; }
    public List<FoodOverrideEntryFile> Grants { get; set; } = new();
}

/// <summary>One grant row. All three fields required -- the mod is inventing a number for an item
/// vanilla gave none, so the author states it; no default repeats the "balanced" 0.4 defect (7.6).</summary>
public class FoodOverrideEntryFile
{
    public string? Pattern { get; set; }
    public string? Category { get; set; }
    public float? BaseSatiety { get; set; }
}
