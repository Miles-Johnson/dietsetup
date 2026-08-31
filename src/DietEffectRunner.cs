using System;
using dietsetup.Rules;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace dietsetup;

/// <summary>
/// The one consumer for a resolved rule's effects list (architecture 7.1/7.2, task rule 4: effects
/// fire after the resolve, never inside it). Called from the eat/meal apply-site postfixes, never
/// from DietResolver. satietyMult/nutritionMult/verdict are already folded into DietResolveResult
/// by the time this runs (via CompiledRule's own fields, architecture 5.2) -- only damage and custom
/// entries do anything here.
/// </summary>
internal static class DietEffectRunner
{
    public static void Fire(ICoreAPI? api, EntityAgent byEntity, DietResolveResult result)
    {
        if (api == null) return;

        foreach (CompiledEffect effect in result.Effects)
        {
            switch (effect.Type)
            {
                case DietEffectType.Damage:
                    ApplyDamage(byEntity, effect);
                    break;
                case DietEffectType.Custom:
                    ApplyCustom(api, byEntity, result, effect);
                    break;
            }
        }
    }

    // Old profile system's Reaction/DamageOverTime split, replaced by one authored list and one
    // mode switch (architecture 7.1). Amount's sign is authoring convention (negative = damage,
    // matching the architecture 4.2 example) -- ReceiveDamage always wants a positive magnitude.
    private static void ApplyDamage(EntityAgent byEntity, CompiledEffect effect)
    {
        float amount = Math.Abs(effect.Amount);
        if (amount <= 0f) return;

        var source = new DamageSource
        {
            Source = EnumDamageSource.Internal,
            Type = EnumDamageType.Poison
        };

        if (effect.DamageMode == DietDamageMode.OverTime)
        {
            source.Duration = TimeSpan.FromSeconds(effect.DurationSec);
            source.TicksPerDuration = effect.Ticks;
            source.DamageOverTimeTypeEnum = EnumDamageOverTimeEffectType.Poison;
        }

        byEntity.ReceiveDamage(source, amount);
    }

    // DietResolveResult passed by value, not ref -- see IDietConsequenceEffect's doc for why that's
    // what makes the open registry safe (architecture 7.2).
    private static void ApplyCustom(ICoreAPI api, EntityAgent byEntity, DietResolveResult result, CompiledEffect effect)
    {
        effect.CustomEffect?.Handle(api, byEntity, result);
    }
}
