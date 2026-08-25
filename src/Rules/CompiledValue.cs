namespace dietsetup.Rules;

/// <summary>A rule's satiety or nutrition value -- either a constant or a curve evaluated
/// against the stack's spoil level, anchors linearly interpolated and clamped flat outside
/// the anchor range (spec section 3).</summary>
public readonly struct CompiledValue
{
    private readonly float flat;
    private readonly CurveAnchor[]? curve;

    private CompiledValue(float flat, CurveAnchor[]? curve)
    {
        this.flat = flat;
        this.curve = curve;
    }

    public static CompiledValue Flat(float value) => new(value, null);

    /// <summary>anchors must already be sorted ascending by Spoil.</summary>
    public static CompiledValue FromCurve(CurveAnchor[] anchors) => new(0f, anchors);

    public float Evaluate(float spoilLevel)
    {
        if (curve == null || curve.Length == 0) return flat;

        int last = curve.Length - 1;
        if (spoilLevel <= curve[0].Spoil) return curve[0].Value;
        if (spoilLevel >= curve[last].Spoil) return curve[last].Value;

        for (int i = 0; i < last; i++)
        {
            CurveAnchor a = curve[i];
            CurveAnchor b = curve[i + 1];
            if (spoilLevel < a.Spoil || spoilLevel > b.Spoil) continue;

            float t = (spoilLevel - a.Spoil) / (b.Spoil - a.Spoil);
            return a.Value + (b.Value - a.Value) * t;
        }

        return curve[last].Value;
    }
}
