using dietsetup.Rules;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace dietsetup.Binding;

/// <summary>Architecture 4.5's resolve order, in the one place every gather step calls (task 2):
/// explicit override, then race trait via the binding table, then the table's default. Resolves
/// fresh on every call, nothing cached or stored beyond the override itself -- that's the whole
/// point (4.5: a stored diet id would freeze a character on the numbers shipped that day).</summary>
public static class DietIdResolver
{
    public const string OverrideAttribute = "dietsetup:dietOverride";
    public const string DefaultDietId = "base";

    // Reserved for /dietassignrules clear -- DietLoadPipeline (rule 15) refuses any diet
    // authored with this id so the command can never confuse "clear the override" with
    // "assign the diet literally named clear".
    public const string ClearKeyword = "clear";

    /// <summary>Resolves a diet id and looks it up in the compiled table, falling back to
    /// "base" if the resolved id isn't a loaded diet (e.g. a stale binding). Returns null only
    /// if "base" itself isn't loaded either -- an unconfigured/broken install, callers should
    /// no-op to vanilla in that case.</summary>
    public static CompiledDiet? ResolveDiet(Entity? forEntity)
    {
        string dietId = Resolve(forEntity);
        return DietRuleRegistry.GetDiet(dietId) ?? DietRuleRegistry.GetDiet(DefaultDietId);
    }

    public enum ResolvePath { ExplicitOverride, RaceTrait, Default }

    public static string Resolve(Entity? forEntity) => ResolveCore(forEntity, out _, out _);

    /// <summary>/dietdiag's entry point (task 2): same steps as Resolve, plus which path fired
    /// and, for the race-trait path, which trait matched -- never called per-bite, so the extra
    /// out params are fine here even though Resolve itself must stay allocation-free.</summary>
    public static string ResolveDetailed(Entity? forEntity, out ResolvePath path, out string? matchedTrait) =>
        ResolveCore(forEntity, out path, out matchedTrait);

    private static string ResolveCore(Entity? forEntity, out ResolvePath path, out string? matchedTrait)
    {
        path = ResolvePath.Default;
        matchedTrait = null;
        if (forEntity == null) return DefaultDietId;

        string? overrideId = forEntity.WatchedAttributes?.GetString(OverrideAttribute);
        if (!string.IsNullOrEmpty(overrideId))
        {
            path = ResolvePath.ExplicitOverride;
            return overrideId;
        }

        // Side-specific instance, not a shared static (landmine C) -- forEntity.Api resolves to
        // the ModSystem instance for whichever side is asking, same pattern rfmechanics'
        // RaceTraits.HasTrait uses for CharacterSystem.
        DietSetupModSystem? modSystem = forEntity.Api?.ModLoader.GetModSystem<DietSetupModSystem>();
        BindingsFile bindings = modSystem?.CurrentBindings ?? new BindingsFile { Default = DefaultDietId };

        if (forEntity is EntityPlayer entityPlayer && entityPlayer.Player is IPlayer iplayer)
        {
            foreach ((string traitCode, string dietId) in bindings.Bindings)
            {
                if (HasTrait(iplayer, traitCode))
                {
                    path = ResolvePath.RaceTrait;
                    matchedTrait = traitCode;
                    return dietId;
                }
            }
        }

        return bindings.Default ?? DefaultDietId;
    }

    /// <summary>CharacterSystem.HasTrait returns true for a null/unset characterClass (landmine:
    /// "HasTrait needs a null-class guard at every call site") -- guarded here, the one call
    /// site. No dependency on raceframework/rfmechanics; CharacterSystem is vanilla, so this
    /// works with no race mod installed (Mods-solo).</summary>
    private static bool HasTrait(IPlayer iplayer, string traitCode)
    {
        if (iplayer.Entity?.Api == null) return false;

        string charClass = iplayer.Entity.WatchedAttributes.GetString("characterClass");
        if (string.IsNullOrEmpty(charClass)) return false;

        CharacterSystem? charSys = iplayer.Entity.Api.ModLoader.GetModSystem<CharacterSystem>();
        return charSys != null && charSys.HasTrait(iplayer, traitCode);
    }
}
