namespace dietsetup.Diet;

/// <summary>Global, not per-profile -- granting a category to an item vanilla has no data for
/// (e.g. raw mammal meat) is a fact about the item, not the eater. A grant only ever fires when
/// vanilla's GetNutritionProperties returned null; it never overrides existing vanilla data.</summary>
public class DietGrantRule
{
    /// <summary>Wildcard vs Collectible.Code.ToString(), e.g. "game:redmeat-raw". Checked before
    /// Tag; first match in declaration order wins.</summary>
    public string? ItemPattern { get; set; }

    /// <summary>A tag name from tags.json / RegisterTag, checked only if ItemPattern is unset or
    /// didn't match.</summary>
    public string? Tag { get; set; }

    /// <summary>EnumFoodCategory.ToString(), e.g. "Protein".</summary>
    public string Category { get; set; } = "";

    public float BaseSatiety { get; set; }

    /// <summary>Optional. Fires only when the eating profile's own CategoryDefault for this
    /// grant's Category is untouched from baseline (SatietyMult and NutritionMult both >= 1) --
    /// lets an item hazard (e.g. eating meat raw) apply without altering normal category
    /// tolerance elsewhere, and without double-firing on a profile that already reacts on its own.</summary>
    public DietReaction? Reaction { get; set; }
}
