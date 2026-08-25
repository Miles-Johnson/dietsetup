using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace dietsetup.Rules;

/// <summary>Mutable accumulator effects apply against -- built-in effect handling in
/// DietResolver and IDietCustomEffect.Apply both mutate the same shape, so a race mod's custom
/// effect composes with satietyMult/nutritionMult/etc. on the same rule.</summary>
public struct DietEffectAccumulator
{
    public DietVerdict Verdict;
    public float Satiety;
    public float Nutrition;
    public float DamageMagnitude;
    public float DamageDurationSec;
}

/// <summary>A race mod's C# effect, called by a diet rule's "custom" effect entry via its
/// registered key. Kept narrow on purpose (spec section 4) -- anything expressible as a
/// built-in effect type must use that instead.</summary>
public interface IDietCustomEffect
{
    void Apply(ICoreAPI api, Entity? forEntity, float portionSize, ref DietEffectAccumulator acc);
}

/// <summary>Named registry for custom diet effects, e.g. DietEffects.Register("rfmechanics:orcThewBurn",
/// new OrcThewBurnEffect()) from a race mod's own Start(), which runs before dietsetup's
/// AssetsLoaded/AssetsFinalize compiles diet rules against it.</summary>
public static class DietEffects
{
    private static readonly Dictionary<string, IDietCustomEffect> registry = new();

    public static void Register(string key, IDietCustomEffect effect) => registry[key] = effect;

    public static bool TryGet(string key, out IDietCustomEffect? effect) => registry.TryGetValue(key, out effect);
}
