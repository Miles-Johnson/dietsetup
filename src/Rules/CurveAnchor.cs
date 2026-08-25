namespace dietsetup.Rules;

public readonly struct CurveAnchor
{
    public readonly float Spoil;
    public readonly float Value;

    public CurveAnchor(float spoil, float value)
    {
        Spoil = spoil;
        Value = value;
    }
}
