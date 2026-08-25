namespace dietsetup.Diet;

/// <summary>An object (not a bare float) so a later status-effect integration can add new keys
/// without migrating hand-edited deployed configs.</summary>
public class DietReaction
{
    /// <summary>Negative = damage. Only applied when the owning category's SatietyMult and
    /// NutritionMult are both exactly 0 -- see DietCategoryDefault.</summary>
    public float Health { get; set; }

    /// <summary>0 (default) = applied as one instant hit. Greater than 0 spreads Health across a
    /// damage-over-time effect (vanilla's Poison DoT type) over this many seconds instead, via
    /// DietEatDoTPatch -- vanilla's own instant-hit path is suppressed for these.</summary>
    public float DurationSec { get; set; } = 0f;

    /// <summary>How many portions DurationSec's total damage is split into. Ignored when
    /// DurationSec is 0.</summary>
    public int Ticks { get; set; } = 1;
}
