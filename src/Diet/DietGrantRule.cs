namespace dietsetup.Diet;

/// <summary>Global, not per-profile -- granting a category to an item vanilla has no nutrition
/// data for (raw mammal meat) is a fact about the item, not about whoever's eating it. What the
/// eater actually gets out of that category is governed entirely by their profile's
/// DietCategoryDefault for the granted category, same as any other food. A grant rule only ever
/// fires when vanilla's own GetNutritionProperties returned null -- it never overrides existing
/// vanilla nutrition data.</summary>
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
    /// grant's Category is untouched from baseline (SatietyMult >= 1 and NutritionMult >= 1) and
    /// hasn't already fired its own category-level reaction -- lets an item-specific hazard (e.g.
    /// eating meat raw) apply to profiles that were never specifically adapted to this category,
    /// without altering their normal tolerance for everything else in that category, and without
    /// double-firing on top of a profile whose category default already reacts on its own.</summary>
    public DietReaction? Reaction { get; set; }
}
