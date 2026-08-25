using System.Collections.Generic;

namespace dietsetup.Tags;

/// <summary>One config/foodtags.json across any domain (dietsetup ships the vanilla one; a
/// compat pack ships its own under its own domain) -- merged via api.Assets.GetMany.</summary>
public class FoodTagConfigFile
{
    public Dictionary<string, string[]> Source { get; set; } = new();
    public Dictionary<string, string[]> State { get; set; } = new();
    public Dictionary<string, string[]> Form { get; set; } = new();
}
