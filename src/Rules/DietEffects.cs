using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace dietsetup.Rules;

/// <summary>A race mod's consequence handler (architecture 7.2), invoked by DietEffectRunner after
/// DietResolver.Resolve for a "custom" effect entry matching its registered key. DietResolveResult
/// is taken by value, not ref/out -- there is no parameter through which a handler could write back
/// into the record DietResolver produced, so it structurally cannot touch satiety, nutrition,
/// verdict or health. That's what keeps the open registry safe: a race mod that wants to change one
/// of those writes a rule, not a handler.</summary>
public interface IDietConsequenceEffect
{
    void Handle(ICoreAPI api, Entity forEntity, DietResolveResult result);
}

/// <summary>Named registry for consequence effects, e.g. DietEffects.Register("rfmechanics:orcThewBurn",
/// new OrcThewBurnEffect()) from a race mod's own Start(), which runs before dietsetup's
/// AssetsLoaded/AssetsFinalize compiles diet rules against it.</summary>
public static class DietEffects
{
    private static readonly Dictionary<string, IDietConsequenceEffect> registry = new();

    public static void Register(string key, IDietConsequenceEffect effect) => registry[key] = effect;

    public static bool TryGet(string key, out IDietConsequenceEffect? effect) => registry.TryGetValue(key, out effect);
}
