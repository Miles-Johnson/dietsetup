namespace dietsetup;

/// <summary>
/// Configuration for the Diet Setup mod. Loaded from dietsetup.json in the ModConfig folder.
/// Profile/tag/grant content lives in assets/dietsetup/config/*.json instead (JSON-patchable) --
/// this is pure server-admin toggles, no diet content.
/// </summary>
public class DietSetupConfig
{
    /// <summary>Master toggle. If false, every diet patch is a no-op (falls through to vanilla) and no dialog is ever shown.</summary>
    public bool EnableDietSystem { get; set; } = true;

    /// <summary>If false, new characters are never auto-prompted; the feature becomes purely admin-grant/opt-in via /dietselgrant + /dietsel.</summary>
    public bool AutoPromptNewCharacters { get; set; } = true;

    /// <summary>Profile id assigned to a player before they've ever picked one (and used by "Use Defaults" in the dialog). Must match a registered DietProfile.Id.</summary>
    public string DefaultProfileId { get; set; } = "balanced";
}
