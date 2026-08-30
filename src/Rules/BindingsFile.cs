using System.Collections.Generic;

namespace dietsetup.Rules;

/// <summary>ModConfig/dietsetup/bindings.json (architecture 4.5) -- server config, not an asset.
/// Maps a race trait string to a diet id. Nothing resolves a binding yet (that's phase 3); this
/// task only parses, validates and logs it.</summary>
public class BindingsFile
{
    public int? SchemaVersion { get; set; }
    public Dictionary<string, string> Bindings { get; set; } = new();
    public string? Default { get; set; }
}
