using System;
using System.Collections.Generic;

namespace dietsetup.Rules;

/// <summary>One config/diets/*.json -- a race mod ships one file per diet, any domain, merged
/// via api.Assets.GetMany (spec section 1: "a new race costs one JSON file, not C#").</summary>
public class DietDefinitionFile
{
    public string Id { get; set; } = "";
    public bool HiddenFromPicker { get; set; }
    public Dictionary<string, CurveAnchorFile[]> Curves { get; set; } = new();
    public DietDefaultFile Default { get; set; } = new();
    public DietRuleFileEntry[] Rules { get; set; } = Array.Empty<DietRuleFileEntry>();
}

/// <summary>Baseline applied when no rule matches a food's tag set (spec section 3, "default verdict").</summary>
public class DietDefaultFile
{
    public string Verdict { get; set; } = "edible";
    public DietValueFile Satiety { get; set; } = DietValueFile.FlatOne;
    public DietValueFile Nutrition { get; set; } = DietValueFile.FlatOne;
    public DietEffectFile[] Effects { get; set; } = Array.Empty<DietEffectFile>();
}

public class DietRuleFileEntry
{
    public string[] Requires { get; set; } = Array.Empty<string>();
    public string[] Excludes { get; set; } = Array.Empty<string>();
    public int Priority { get; set; }
    public string Verdict { get; set; } = "edible";
    public DietValueFile Satiety { get; set; } = DietValueFile.FlatOne;
    public DietValueFile Nutrition { get; set; } = DietValueFile.FlatOne;
    public DietEffectFile[] Effects { get; set; } = Array.Empty<DietEffectFile>();
}

/// <summary>Either Flat or Curve, never both -- Curve wins if both are set.</summary>
public class DietValueFile
{
    public float? Flat { get; set; }
    public string? Curve { get; set; }

    public static DietValueFile FlatOne => new() { Flat = 1f };
}

public class CurveAnchorFile
{
    public float Spoil { get; set; }
    public float Value { get; set; }
}

public class DietEffectFile
{
    public string Type { get; set; } = "";
    public float Amount { get; set; }
    public float DurationSec { get; set; }
    public string? Stat { get; set; }
    public string? Key { get; set; }
}
