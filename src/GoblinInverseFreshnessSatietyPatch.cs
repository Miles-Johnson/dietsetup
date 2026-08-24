using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Postfix on GlobalConstants.FoodSpoilageSatLossMul -- the single static method every vanilla
/// eat/drink path and the tooltip call to turn TransitionLevel into a satiety multiplier (patching
/// here covers every food type uniformly; vanilla's own extension point has no usable setter).
/// Goblin-only override and game:rot composability:
/// notes/dietsetup-patch-internals.md#goblin-satiety-patch--goblininversefreshnesssatietypatchcs.
/// </summary>
[HarmonyPatch(typeof(GlobalConstants), nameof(GlobalConstants.FoodSpoilageSatLossMul))]
public static class GoblinInverseFreshnessSatietyPatch
{
    [HarmonyPostfix]
    public static void Postfix(float spoilState, ItemStack stack, EntityAgent byEntity, ref float __result)
    {
        DietSetupConfig cfg = DietSetupModSystem.Config;
        if (!cfg.EnableGoblinInverseFreshness) return;

        // Goblin identity chain, copied verbatim from rfmechanics' GoblinRotAuraBehavior.IsGoblin --
        // characterClass null/empty check BEFORE HasTrait is load-bearing (HasTrait returns true
        // for a null class). Uses byEntity's own Api, never RFMechanicsModSystem.Api (confirmed last-writer-wins).
        if (byEntity is not EntityPlayer player) return;

        string charClass = player.WatchedAttributes.GetString("characterClass");
        if (string.IsNullOrEmpty(charClass)) return;

        IPlayer? iplayer = player.World.PlayerByUid(player.PlayerUID);
        if (iplayer == null) return;

        CharacterSystem? charSys = byEntity.Api?.ModLoader.GetModSystem<CharacterSystem>();
        if (charSys == null) return;

        if (!charSys.HasTrait(iplayer, cfg.GoblinTraitCode)) return;

        if (!AppliesTo(stack, cfg)) return;

        __result = ComputeMultiplier(spoilState, cfg);
    }

    /// <summary>Single chokepoint for exemptions -- an exempt stack is left untouched, keeping
    /// vanilla's own penalty exactly as a non-goblin would see it. Empty array (default) always
    /// returns true (WildcardUtil.Match on empty is unconditionally false, no length check needed).</summary>
    private static bool AppliesTo(ItemStack stack, DietSetupConfig cfg)
    {
        string? code = stack?.Collectible?.Code?.ToString();
        if (code == null) return true;
        return !WildcardUtil.Match(cfg.GoblinInverseFreshnessExemptCodes, code);
    }

    /// <summary>t=0 always maps to 1.0 for both curve shapes (also what keeps game:rot's flat
    /// grant path untouched, see class doc). t=1 maps to GoblinInverseFreshnessMaxMultiplier.
    /// LateWeighted uses t^2 to concentrate the bonus near full decay.</summary>
    private static float ComputeMultiplier(float spoilState, DietSetupConfig cfg)
    {
        float t = spoilState < 0f ? 0f : (spoilState > 1f ? 1f : spoilState);
        float max = (float)cfg.GoblinInverseFreshnessMaxMultiplier;
        float shaped = cfg.GoblinInverseFreshnessCurve == GoblinInverseFreshnessCurveMode.LateWeighted ? t * t : t;
        return 1f + (max - 1f) * shaped;
    }
}
