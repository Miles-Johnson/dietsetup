using System.Collections.Generic;

namespace dietsetup.Binding;

/// <summary>ModConfig/dietsetup/bindings.json (architecture 4.5) -- server config, not an asset.
/// Maps a race trait string to a diet id. Loaded server-side only; the client never reads this
/// file directly (it may not even exist on a dedicated client's own data folder) and instead
/// receives it over the network as a DietBindingsPacket -- see DietSetupModSystem.</summary>
public class BindingsFile
{
    public int? SchemaVersion { get; set; }
    public Dictionary<string, string> Bindings { get; set; } = new();
    public string? Default { get; set; }
}
