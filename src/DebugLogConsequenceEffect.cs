using dietsetup.Rules;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace dietsetup;

/// <summary>Registered under "dietsetup:debuglog" at Start() (task 4 verification, item e): a
/// throwaway fixture rule can reference this key via effects:[{"type":"custom","key":"dietsetup:debuglog"}]
/// to prove the consequence registry fires exactly once per real eat, with no engine-field write
/// path available to it. Always registered, inert unless a rule names the key.</summary>
internal sealed class DebugLogConsequenceEffect : IDietConsequenceEffect
{
    public void Handle(ICoreAPI api, Entity forEntity, DietResolveResult result)
    {
        api.Logger.Notification(
            "[dietsetup] debug consequence effect: entity={0} verdict={1} satietyMult={2:F2} nutritionMult={3:F2} effectCount={4}",
            forEntity.EntityId, result.Verdict, result.Satiety, result.Nutrition, result.Effects.Length);
    }
}
