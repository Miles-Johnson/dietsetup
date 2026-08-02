namespace dietsetup.Diet;

/// <summary>An object (not a bare float) so a later status-effect integration can add new keys
/// without migrating hand-edited deployed configs.</summary>
public class DietReaction
{
    /// <summary>Negative = damage. On a category default, only applied when the owning
    /// category's SatietyMult and NutritionMult are both exactly 0 -- see DietCategoryDefault. On
    /// a DietGrantRule, applied under that rule's own condition -- see DietGrantRule.Reaction.</summary>
    public float Health { get; set; }

    /// <summary>0 (default) = applied as one instant hit, same as always. Greater than 0 spreads
    /// Health across a damage-over-time effect (vanilla's EnumDamageOverTimeEffectType.Poison)
    /// over this many seconds instead, via a Harmony patch on CollectibleObject.tryEatStop --
    /// vanilla's own instant-hit path is suppressed for these (see DietEatDoTPatch).</summary>
    public float DurationSec { get; set; } = 0f;

    /// <summary>How many portions DurationSec's total damage is split into. Ignored when
    /// DurationSec is 0.</summary>
    public int Ticks { get; set; } = 1;
}
