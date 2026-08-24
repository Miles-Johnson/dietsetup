namespace dietsetup.Diet;

public class DietCategoryDefault
{
    public float SatietyMult { get; set; } = 1f;
    public float NutritionMult { get; set; } = 1f;

    /// <summary>Authored per category, not derived. Only fires when SatietyMult AND NutritionMult
    /// are both exactly 0 -- a satiety-0/nutrition-nonzero category is an intentional "fills but
    /// doesn't nourish" state, not a reaction trigger.</summary>
    public DietReaction? Reaction { get; set; }

    /// <summary>Reused wherever a profile has no entry for a category (missing key from a
    /// third-party-registered profile, or Unknown/NoNutrition food categories) -- pure vanilla
    /// passthrough, never throws.</summary>
    public static readonly DietCategoryDefault PassThrough = new() { SatietyMult = 1f, NutritionMult = 1f, Reaction = null };
}
