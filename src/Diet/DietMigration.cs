using Vintagestory.API.Datastructures;

namespace dietsetup.Diet;

/// <summary>
/// Legacy players (dietConfigured=true under the old flat-multiplier system, before reactions or
/// named profiles existed) must not start taking reaction damage the moment this update deploys.
/// Rather than snapping their hand-tuned 0-150% sliders onto the nearest of a handful of named
/// profiles (which would silently change their satiety ratios to whatever preset happens to be
/// closest, in whichever direction that preset's numbers pull), every legacy player is instead
/// pointed at one shared sentinel id, LegacyCustomProfileId, whose category defaults are computed
/// on the fly from that exact character's own old attributes -- exact preservation, no lookup
/// table, no new storage (the old dietFruitMult/etc. keys are already left in place, unread).
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
