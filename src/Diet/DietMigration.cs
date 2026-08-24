using Vintagestory.API.Datastructures;

namespace dietsetup.Diet;

/// <summary>
/// Legacy players (pre-rewrite flat-multiplier system) must not start taking reaction damage the
/// moment this update deploys. Rather than snapping their sliders onto the nearest named profile
/// (changing their ratios), each is pointed at a sentinel computed live from their own old attributes.
/// </summary>
public static class DietMigration
{
    public const string LegacyCustomProfileId = "legacy_custom";

    public static DietProfile BuildLegacyCustomProfile(ITreeAttribute watchedAttributes)
    {
        var profile = new DietProfile { Id = LegacyCustomProfileId, HiddenFromPicker = true };
        AddCategory(profile, "Fruit", watchedAttributes.GetFloat("dietFruitMult", 1f));
        AddCategory(profile, "Vegetable", watchedAttributes.GetFloat("dietVegetableMult", 1f));
        AddCategory(profile, "Protein", watchedAttributes.GetFloat("dietProteinMult", 1f));
        AddCategory(profile, "Grain", watchedAttributes.GetFloat("dietGrainMult", 1f));
        AddCategory(profile, "Dairy", watchedAttributes.GetFloat("dietDairyMult", 1f));
        return profile;
    }

    private static void AddCategory(DietProfile profile, string category, float legacySatietyMult)
    {
        profile.CategoryDefaults[category] = new DietCategoryDefault
        {
            SatietyMult = legacySatietyMult,
            // Mirrors the pre-rewrite system: nutrition gain derived from the same input as satiety,
            // so a zeroed category always produced zero nutrition too -- keeps a migrated Carnivore's
            // max-HP bonus split across the same categories as before, not spread across all 5.
            NutritionMult = legacySatietyMult > 0f ? 1f : 0f,
            Reaction = null
        };
    }
}
