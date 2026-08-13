using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Postfix on GlobalConstants.FoodSpoilageSatLossMul -- the single static method every vanilla
/// eat/drink path (CollectibleObject.tryEatStop, BlockMeal.tryFinishEatMeal, BlockPie's eat path,
/// BlockLiquidContainerBase's drink path) and the item-description tooltip all call to turn
/// TransitionLevel into a satiety multiplier. Vanilla's own "extension point" for this
/// (GlobalConstants.FoodSpoilSatLossMulHandler) is a get-only property that hands back a fresh
/// lambda on every read -- there is no setter, so it can't actually be overridden; patching the
/// method itself is the only way in. One patch here covers every food type uniformly instead of
/// separately patching tryEatStop/tryFinishEatMeal/BlockPie/BlockLiquidContainerBase.
///
/// Goblins only: cancels vanilla's own spoilage penalty (Math.Max(0, 1 - spoilState)) outright by
/// overwriting __result rather than multiplying against it -- stacking the inverse curve on top of
/// the penalty instead of replacing it would fight it and produce an opaque net result, which the
/// brief explicitly calls out to avoid.
///
/// Composability with GoblinRotEdiblePatch (game:rot flat grant): game:rot's own itemtype JSON
/// (assets/survival/itemtypes/resource/rot.json) has no Perish transitionableProps, so
/// UpdateAndGetTransitionState returns null for it and every call site passes spoilState 0f for
/// game:rot. This curve evaluates to exactly 1.0 at t=0 by construction (both curve shapes), so it
/// never touches the game:rot flat-grant path regardless of curve shape or max multiplier --
/// structural, not incidental.
/// </summary>
[HarmonyPatch(typeof(GlobalConstants), nameof(GlobalConstants.FoodSpoilageSatLossMul))]
public static class GoblinInverseFreshnessSatietyPatch
{
    [HarmonyPostfix]
    public static void Postfix(float spoilState, ItemStack stack, EntityAgent byEntity, ref float __result)
    {
        DietSetupConfig cfg = DietSetupModSystem.Config;
        if (!cfg.EnableGoblinInverseFreshness) return;

        // ── Goblin identity chain, copied verbatim from rfmechanics' GoblinRotAuraBehavior.IsGoblin
        // / GoblinRotEdiblePatch -- characterClass null/empty check BEFORE HasTrait is load-bearing,
        // HasTrait returns true for a null class. Uses byEntity's OWN Api (not a mod-static Api
        // field) -- RFMechanicsModSystem.Api is confirmed last-writer-wins between client/server in
        // single-player and must not be used for anything side-sensitive; the entity handed to this
        // postfix already carries the correct per-side API. ──
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

    /// <summary>Single chokepoint for exemptions. Exempt stacks are left completely untouched
    /// (postfix returns before overwriting __result), so they keep vanilla's own penalty exactly
    /// as a non-goblin would see it. Empty GoblinInverseFreshnessExemptCodes (the default) makes
    /// this always true -- WildcardUtil.Match on an empty array returns false unconditionally, no
    /// separate length check needed. A future tag-based exemption rule replaces the body of this
    /// method only; nothing else in this file changes.</summary>
    private static bool AppliesTo(ItemStack stack, DietSetupConfig cfg)
    {
        string? code = stack?.Collectible?.Code?.ToString();
        if (code == null) return true;
        return !WildcardUtil.Match(cfg.GoblinInverseFreshnessExemptCodes, code);
    }

    /// <summary>t=0 (fresh) always maps to 1.0 for both curve shapes -- fresh food is normal, no
    /// bonus, and (see class doc comment) this is also what keeps game:rot's flat grant path
    /// untouched. t=1 (fully rotten) maps to GoblinInverseFreshnessMaxMultiplier. LateWeighted uses
    /// t^2 so the bonus is concentrated near full decay, matching the rot aura's larder-hold
    /// ceiling parking food near the top of the range rather than the middle.</summary>
    private static float ComputeMultiplier(float spoilState, DietSetupConfig cfg)
    {
        float t = spoilState < 0f ? 0f : (spoilState > 1f ? 1f : spoilState);
        float max = (float)cfg.GoblinInverseFreshnessMaxMultiplier;
        float shaped = cfg.GoblinInverseFreshnessCurve == GoblinInverseFreshnessCurveMode.LateWeighted ? t * t : t;
        return 1f + (max - 1f) * shaped;
    }
}
