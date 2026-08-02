using System.Collections.Generic;

namespace dietsetup.Diet;

public class DietProfile
{
    public string Id { get; set; } = "";

    /// <summary>Picker button label, e.g. "dietsetup:profile-carnivore".</summary>
    public string NameLangCode { get; set; } = "";

    /// <summary>True for profiles that exist only to be assigned programmatically (currently
    /// unused by any built-in profile -- the legacy-migration case is handled dynamically by
    /// DietMigration instead, see DietProfileRegistry.ResolveProfileForEntity).</summary>
    public bool HiddenFromPicker { get; set; }

    /// <summary>Keyed by EnumFoodCategory.ToString() ("Fruit", "Vegetable", "Protein", "Grain",
    /// "Dairy") to sidestep enum-key JSON deserialization quirks. A third-party mod's
    /// RegisterProfile call may omit categories -- lookups must go through
    /// DietCategoryDefault.PassThrough, never a direct indexer.</summary>
    public Dictionary<string, DietCategoryDefault> CategoryDefaults { get; set; } = new();
}
