using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using dietsetup.Binding;
using dietsetup.Diet;
using dietsetup.Rules;
using dietsetup.Tags;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace dietsetup;

public class DietSetupModSystem : ModSystem
{
    // Public API: per-tag intake accumulator (Phase G3, for rfmechanics' goblin rot aura; "rot"
    // is the only tag written in v1). Raw value + a world.Calendar.TotalHours timestamp so any
    // external reader (no assembly reference needed) can compute the live, continuously-decaying
    // value on demand with no tick loop either side. Key shape and units are a cross-mod contract
    // -- see README.md.
    public static string AttrIntake(string tag) => $"dietsetup:intake:{tag}";
    public static string AttrIntakeUpdatedHours(string tag) => $"dietsetup:intake:{tag}:updatedHours";

    // Old single-tag intake keys, pre-dating the "dietsetup:intake:<tag>" rename. Migrated once
    // per player in MigrateLegacyRotIntakeIfNeeded, then never read/written again.
    private const string OldAttrRotIntake = "dietsetup:rotIntake";
    private const string OldAttrRotIntakeUpdatedHours = "dietsetup:rotIntakeUpdatedHours";

    private const string HarmonyId = "dietsetup";

    private static DietSetupConfig? config;
    public static DietSetupConfig Config => config ??= new DietSetupConfig();

    private ICoreServerAPI? sapi;
    private ICoreClientAPI? capi;
    private Harmony? harmony;

    private const string BindingsChannelName = "dietsetup-bindings";
    private IServerNetworkChannel? serverBindingsChannel;

    // Instance field, not a shared static (landmine C, task 1): client and server each get their
    // own DietSetupModSystem instance even in singleplayer, so this is already side-isolated.
    // Server: authoritative, loaded from ModConfig/dietsetup/bindings.json every pipeline run.
    // Client: provisional until the join/reload packet lands, see OnBindingsPacket.
    private BindingsFile bindings = new() { SchemaVersion = 1, Default = DietIdResolver.DefaultDietId };

    /// <summary>What DietIdResolver.Resolve reads for this side (task 2). Not a shared static --
    /// see the field comment above.</summary>
    public BindingsFile CurrentBindings => bindings;

    // Static guard so PatchAll runs at most once for the process's lifetime -- singleplayer
    // instantiates a separate DietSetupModSystem per side in the same process, and an
    // unpatch-then-repatch inside Start() was observed stacking patches 2-3x, compounding the saturation math.
    private static bool harmonyPatched;

    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        LoadConfig(api);

        // Always available, inert unless a rule references its key -- see the class doc.
        DietEffects.Register("dietsetup:debuglog", new DebugLogConsequenceEffect());

        // Nutrition scaling is applied via Harmony patches on EntityBehaviorHunger, not by
        // re-registering the "hunger" behavior class -- RegisterEntityBehaviorClass throws on a
        // duplicate key, and VSEssentials always registers "hunger" first.
        if (!harmonyPatched)
        {
            harmony = new Harmony(HarmonyId);
            try
            {
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                harmonyPatched = true;
            }
            catch (Exception ex)
            {
                api.Logger.Error("[dietsetup] Harmony patch failed: {0}", ex);
            }
        }
    }

    // Runs after asset origins are fully initialized (still before Start*Side), once
    // api.World.Collectibles is populated -- the earliest point the whole 8-step pipeline
    // (architecture 6) can run: it needs both the asset system and FoodTagRegistry.ResolveStaticTags'
    // per-collectible walk. api.World.Calendar is null here (landmine B) -- the pipeline never
    // touches it, only tags/diets/ModConfig, so this phase is safe for it.
    public override void AssetsFinalize(ICoreAPI api)
    {
        base.AssetsFinalize(api);
        bindings = DietLoadPipeline.RunAndLog(api).Bindings;
    }

    public override void Dispose()
    {
        // Only the instance that actually applied the patch (its own harmony field is non-null)
        // resets the shared flag -- singleplayer disposes a client and server instance separately,
        // and the loser of the Start()-time race must not reset the flag out from under the winner's patch.
        if (harmony != null)
        {
            harmony.UnpatchAll(HarmonyId);
            harmony = null;
            harmonyPatched = false;
        }
        base.Dispose();
    }

    /// <summary>
    /// Load dietsetup.json. A successful parse (or missing file) is stored back, which is what
    /// drops stale keys and adds new ones -- StoreModConfig serializes the strongly-typed config,
    /// not raw JSON. Malformed JSON falls back to in-memory defaults without touching the file.
    /// </summary>
    private static void LoadConfig(ICoreAPI api)
    {
        const string filename = "dietsetup.json";
        string configPath = Path.Combine(GamePaths.ModConfig, filename);
        bool fileExisted = File.Exists(configPath);

        DietSetupConfig? loaded;
        bool malformed = false;
        try
        {
            loaded = api.LoadModConfig<DietSetupConfig>(filename);
        }
        catch (Exception ex)
        {
            api.Logger.Error("[dietsetup] Failed to parse dietsetup.json, using defaults without overwriting the file: {0}", ex);
            loaded = null;
            malformed = true;
        }

        // Newtonsoft's DeserializeObject<T>("") returns null instead of throwing, so an
        // existing-but-empty file looks identical to "never existed." Without this check that's
        // silently treated as a first run and overwritten with defaults, discarding a crash-truncated file with no warning.
        if (loaded == null && fileExisted && !malformed)
        {
            malformed = true;
            api.Logger.Error("[dietsetup] {0} exists but produced no usable data on parse (empty or unrecognized content) -- using defaults without overwriting the file.", filename);
        }

        config = loaded ?? new DietSetupConfig();

        if (malformed)
        {
            return;
        }

        // Only overwrite when nothing is at risk (loaded == null, nothing to lose) or the
        // pre-rewrite backup actually succeeded. If a backup fails (permissions, disk full),
        // skipping the rewrite this session is the only way to avoid silently losing fields with no copy anywhere.
        bool safeToOverwrite = loaded == null || WarnAndBackupIfFieldsWillBeDropped(api, filename);
        if (!safeToOverwrite)
        {
            api.Logger.Warning("[dietsetup] Skipping rewrite of {0} this session -- couldn't confirm a backup of fields that would be dropped. Will retry next load.", filename);
            return;
        }

        api.StoreModConfig(config, filename);
    }

    /// <summary>StoreModConfig always rewrites using only the current DietSetupConfig shape -- any
    /// unmatched JSON key is silently dropped (documented VS behavior). Detects that, backs up the
    /// pre-rewrite file, and returns false only when a drop can't be confirmed backed up. Deployment context:
    /// notes/dietsetup-patch-internals.md#config-field-drop-protection--dietsetupmodsystemcs-warnandbackupiffieldswillbedropped.</summary>
    private static bool WarnAndBackupIfFieldsWillBeDropped(ICoreAPI api, string filename)
    {
        try
        {
            JsonObject raw = api.LoadModConfig(filename);
            if (raw.Token is not JObject rawObj) return true;

            var knownKeys = new HashSet<string>(
                typeof(DietSetupConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name),
                StringComparer.OrdinalIgnoreCase);
            List<string> droppedKeys = rawObj.Properties().Select(p => p.Name).Where(k => !knownKeys.Contains(k)).ToList();

            if (droppedKeys.Count == 0) return true;

            string configPath = Path.Combine(GamePaths.ModConfig, filename);
            string backupPath = configPath + ".bak";
            File.Copy(configPath, backupPath, true);
            api.Logger.Warning(
                "[dietsetup] {0} contains fields this version no longer uses ({1}) -- they will be dropped when the file is rewritten. A backup of the pre-update file was saved to {2}.",
                filename, string.Join(", ", droppedKeys), backupPath);
            return true;
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[dietsetup] Could not back up {0} before rewrite (fields would be dropped with no backup): {1}", filename, ex);
            return false;
        }
    }

    /// <summary>Merges every domain's config/foodtags.json into the tag registry (prompt 5) --
    /// GetMany, not Get, so a compat pack can add tags for a third-party mod without touching
    /// dietsetup's own file. dietsetup ships the vanilla tags only. Loading itself now lives in
    /// DietLoadPipeline (called from AssetsFinalize), which needs the same asset scan for its
    /// per-domain tag-count log line.</summary>
    public override void StartServerSide(ICoreServerAPI api)
    {
        base.StartServerSide(api);
        sapi = api;

        serverBindingsChannel = api.Network.RegisterChannel(BindingsChannelName)
            .RegisterMessageType<DietBindingsPacket>();

        api.Event.PlayerNowPlaying += OnPlayerNowPlaying;

        // Closes the nutrition-multiplier queue's only leak path (DietProfileRegistry, step 9) --
        // without this a departed player's dictionary entry sits forever.
        api.Event.PlayerDisconnect += OnPlayerDisconnect;

        RegisterDrainSatietyCommand(api);
        RegisterRotIntakeDebugCommand(api);
        RegisterSetNutritionCommand(api);
        RegisterAssignRulesDietCommand(api);
        RegisterDiagCommand(api);
        RegisterDietReloadCommand(api);
        RegisterDietShowCommand(api);
        RegisterFactsQueueDiagCommand(api);

        // GameReady, not AssetsFinalize -- CharacterSystem.traits is populated by its own
        // ServerRunPhase(LoadGamePre) handler, which runs concurrently with mod StartServerSide
        // calls. GameReady is the next phase, guaranteeing LoadGamePre has fully completed first.
        api.Event.ServerRunPhase(EnumServerRunPhase.GameReady, () => ValidateTraitKeys(api));
    }

    /// <summary>Cross-checks every "dietsetup:&lt;tag&gt;Mult" stat key any registered trait
    /// writes against the registered tag set, logging unmatched keys. Log only -- a third-party
    /// mod may register a tag we don't know about yet at this point in load order, and a typo'd
    /// key should never disable the trait it's attached to.</summary>
    private static void ValidateTraitKeys(ICoreServerAPI api)
    {
        CharacterSystem? charSys = api.ModLoader.GetModSystem<CharacterSystem>();
        if (charSys == null) return;

        var knownTags = new HashSet<string>(FoodTagRegistry.AllTagNames);
        foreach (Trait trait in charSys.traits)
        {
            if (trait.Attributes == null) continue;
            foreach (string key in trait.Attributes.Keys)
            {
                if (!key.StartsWith("dietsetup:", StringComparison.Ordinal) || !key.EndsWith("Mult", StringComparison.Ordinal)) continue;

                string tag = key.Substring("dietsetup:".Length, key.Length - "dietsetup:".Length - "Mult".Length);
                if (!knownTags.Contains(tag))
                {
                    api.Logger.Warning("[dietsetup] Trait '{0}' writes stat key '{1}', which does not match any tag in foodtags.json -- likely a typo.", trait.Code, key);
                }
            }
        }
    }

    private static void OnPlayerDisconnect(IServerPlayer byPlayer)
    {
        DietProfileRegistry.RemoveNutritionMultiplierQueue(byPlayer.Entity.EntityId);
        PendingMealEffects.Remove(byPlayer.Entity.EntityId);
    }

    private void OnPlayerNowPlaying(IServerPlayer byPlayer)
    {
        MigrateLegacyRotIntakeIfNeeded(byPlayer);
        serverBindingsChannel?.SendPacket(DietBindingsPacket.From(bindings), byPlayer);
    }

    /// <summary>One-time copy of the pre-rename "dietsetup:rotIntake" pair to
    /// "dietsetup:intake:rot", idempotent via the old attribute's presence check.</summary>
    private static void MigrateLegacyRotIntakeIfNeeded(IServerPlayer byPlayer)
    {
        ITreeAttribute wa = byPlayer.Entity.WatchedAttributes;
        if (!wa.HasAttribute(OldAttrRotIntake)) return;

        wa.SetDouble(AttrIntake("rot"), wa.GetDouble(OldAttrRotIntake, 0.0));
        wa.RemoveAttribute(OldAttrRotIntake);

        if (wa.HasAttribute(OldAttrRotIntakeUpdatedHours))
        {
            wa.SetDouble(AttrIntakeUpdatedHours("rot"), wa.GetDouble(OldAttrRotIntakeUpdatedHours, 0.0));
            wa.RemoveAttribute(OldAttrRotIntakeUpdatedHours);
        }
    }

    /// <summary>Debug/testing only: zeroes the calling player's satiety while leaving per-category
    /// nutrition levels untouched -- OnEntityReceiveSaturation only lets nutrition rise while
    /// satiety isn't already full, so this skips the real-time drain wait without touching what's under test.</summary>
    private void RegisterDrainSatietyCommand(ICoreServerAPI api)
    {
        api.ChatCommands.Create("dietdrainsatiety")
            .WithDescription("Debug: zero your own satiety without touching nutrition levels, to speed up testing")
            .RequiresPrivilege(Privilege.commandplayer)
            .HandleWith(args =>
            {
                IPlayer caller = args.Caller.Player;
                EntityBehaviorHunger? hunger = caller?.Entity?.GetBehavior<EntityBehaviorHunger>();
                if (hunger == null)
                {
                    return TextCommandResult.Error("No hunger behavior found on your entity.");
                }

                hunger.Saturation = 0f;
                return TextCommandResult.Success("Satiety drained to 0. Nutrition levels untouched.");
            });
    }

    /// <summary>Debug/testing only: get/set/clear the caller's raw rot-intake accumulator
    /// directly, bypassing eating rotten food repeatedly to see rfmechanics' goblin rot aura
    /// respond. Setting also stamps the updated-hours timestamp to "now" so the value doesn't
    /// immediately start decaying from a stale timestamp.</summary>
    private void RegisterRotIntakeDebugCommand(ICoreServerAPI api)
    {
        api.ChatCommands.Create("dietrotintake")
            .WithDescription("Debug: get/set/clear your own rot-intake accumulator (dietsetup:intake:rot), for testing rfmechanics' goblin rot aura without eating rotten food and waiting for decay.")
            .RequiresPrivilege(Privilege.controlserver)
            .WithArgs(api.ChatCommands.Parsers.OptionalFloat("value"))
            .HandleWith(args =>
            {
                IPlayer caller = args.Caller.Player;
                ITreeAttribute wa = caller.Entity.WatchedAttributes;
                string valueKey = AttrIntake("rot");
                string updatedKey = AttrIntakeUpdatedHours("rot");
                double halfLife = Config.IntakeHalfLifeHours.TryGetValue("rot", out double h) ? h : 48.0;

                if (args.Parsers[0].IsMissing)
                {
                    double nowHours = caller.Entity.World.Calendar.TotalHours;
                    double lastHours = wa.GetDouble(updatedKey, nowHours);
                    double raw = wa.GetDouble(valueKey, 0.0);
                    return TextCommandResult.Success($"{valueKey}={raw:F4}, elapsed {nowHours - lastHours:F2}h since last write (halfLife={halfLife:F1}h).");
                }

                float value = (float)args[0];
                wa.SetDouble(valueKey, value);
                wa.SetDouble(updatedKey, caller.Entity.World.Calendar.TotalHours);
                return TextCommandResult.Success($"Set {valueKey}={value:F4} (timestamp reset to now, cap is {Config.RotIntakeCap:F2}). Check rfmechanics' /rfrotdiag to see the resulting aura shape.");
            });
    }

    /// <summary>Debug/testing only: writes hunger levels directly so a capacity fixture's
    /// max-health checks are one command instead of force-feeding food. Server-only (landmine A).
    /// Never touches MaxSaturation (landmine F, rfmechanics' business) -- absolute value only, so 0 is the drain.</summary>
    private void RegisterSetNutritionCommand(ICoreServerAPI api)
    {
        api.ChatCommands.Create("dietsetnutrition")
            .WithDescription("Debug: set a nutrition level (or 'all' for all five) directly, clamped to your live maxsaturation. Prints all five levels and the current max-health bonus.")
            .RequiresPrivilege(Privilege.controlserver)
            .WithArgs(
                api.ChatCommands.Parsers.WordRange("category", "Fruit", "Vegetable", "Protein", "Grain", "Dairy", "all"),
                api.ChatCommands.Parsers.Float("value"))
            .HandleWith(args =>
            {
                IPlayer caller = args.Caller.Player;
                EntityBehaviorHunger? hunger = caller?.Entity?.GetBehavior<EntityBehaviorHunger>();
                if (hunger == null)
                {
                    return TextCommandResult.Error("No hunger behavior found on your entity.");
                }

                string category = (string)args[0];
                float value = Math.Clamp((float)args[1], 0f, hunger.MaxSaturation);

                switch (category)
                {
                    case "all":
                        hunger.FruitLevel = value;
                        hunger.VegetableLevel = value;
                        hunger.ProteinLevel = value;
                        hunger.GrainLevel = value;
                        hunger.DairyLevel = value;
                        break;
                    case "Fruit": hunger.FruitLevel = value; break;
                    case "Vegetable": hunger.VegetableLevel = value; break;
                    case "Protein": hunger.ProteinLevel = value; break;
                    case "Grain": hunger.GrainLevel = value; break;
                    case "Dairy": hunger.DairyLevel = value; break;
                }

                // UpdateNutrientHealthBoost only otherwise runs from the eat path (OnEntityReceiveSaturation)
                // or Initialize -- called here so entity.MaxHealth reflects this write immediately.
                hunger.UpdateNutrientHealthBoost();
                CompiledDiet? diet = DietIdResolver.ResolveDiet(hunger.entity);
                float nutrientHealthMod = diet == null ? 0f : DietNutrientHealthBoostPatch.ComputeBonus(diet, hunger);

                return TextCommandResult.Success(
                    $"Fruit={hunger.FruitLevel:F2} Vegetable={hunger.VegetableLevel:F2} Protein={hunger.ProteinLevel:F2} " +
                    $"Grain={hunger.GrainLevel:F2} Dairy={hunger.DairyLevel:F2} | nutrientHealthMod={nutrientHealthMod:F4}");
            });
    }

    /// <summary>Standing admin tool, not a throwaway: writes DietIdResolver.OverrideAttribute
    /// (architecture 4.5's explicit-override tier) on the caller's own entity. This was previously
    /// a no-op that only validated the id and returned a success message without writing anything
    /// -- every caller still resolved through the trait/default tiers. WatchedAttributes, not
    /// EntityStats: it's read fresh on every resolve (DietIdResolver.Resolve), so a retune reaches
    /// the player on their next meal with nothing to migrate, and it auto-syncs to the owning
    /// client the same way vanilla's own hunger levels do -- no separate sync code needed.</summary>
    private void RegisterAssignRulesDietCommand(ICoreServerAPI api)
    {
        api.ChatCommands.Create("dietassignrules")
            .WithDescription("Admin: set your own diet override to a rules-engine diet id, bypassing trait/default resolution")
            .RequiresPrivilege(Privilege.controlserver)
            .WithArgs(api.ChatCommands.Parsers.Word("dietId"))
            .HandleWith(args =>
            {
                IPlayer caller = args.Caller.Player;
                string dietId = (string)args[0];

                if (DietRuleRegistry.GetDiet(dietId) == null)
                {
                    return TextCommandResult.Error($"No rules-engine diet registered for id '{dietId}'.");
                }

                caller.Entity.WatchedAttributes.SetString(DietIdResolver.OverrideAttribute, dietId);
                return TextCommandResult.Success($"{DietIdResolver.OverrideAttribute} set to '{dietId}' (rules-engine diet, bypasses trait/default resolution).");
            });
    }

    /// <summary>Diagnostic: with no argument, dumps the caller's resolved profile, category
    /// defaults, live hunger/health values, held-item tags and satiety fold, and whether the 4
    /// Harmony patches are attached. With an item code, reports how that item resolves without
    /// needing to eat it. Server-side, not client-side: EntityBehaviorHunger and
    /// EntityBehaviorHealth are declared only in player.json's server: block -- reading them off a
    /// client-side EntityPlayer always returned null, so this command never reported real
    /// hunger/health state before the move.</summary>
    private void RegisterDiagCommand(ICoreServerAPI api)
    {
        api.ChatCommands.Create("dietdiag")
            .WithDescription("Diagnostic: dump diet state for the calling player, or resolution for a given item code")
            .RequiresPrivilege(Privilege.commandplayer)
            .WithArgs(api.ChatCommands.Parsers.OptionalWord("itemcode"))
            .HandleWith(args =>
            {
                string? itemCode = args[0] as string;
                var caller = (IServerPlayer)args.Caller.Player;
                return string.IsNullOrEmpty(itemCode) ? DiagPlayerState(api, caller) : DiagItem(api, caller, itemCode);
            });
    }

    /// <summary>Authoring tool, admin privilege (architecture 6): re-runs the entire 8-step load
    /// pipeline. The full result table always goes to server-main.log, same as AssetsFinalize's
    /// startup run; chat gets a one-line summary since the table has scrolled some servers'
    /// clients off their own history.</summary>
    private void RegisterDietReloadCommand(ICoreServerAPI api)
    {
        api.ChatCommands.Create("dietreload")
            .WithDescription("Admin: re-run the diet load pipeline (tags, diets, extends, compile, validate); full table goes to server-main.log")
            .RequiresPrivilege(Privilege.controlserver)
            .HandleWith(args =>
            {
                DietLoadResult result = DietLoadPipeline.RunAndLog(api);
                bindings = result.Bindings;

                // Task 1: re-sync every connected client, not just the caller -- a stale client
                // copy would let its tooltip disagree with the eat path until its next reconnect.
                serverBindingsChannel?.BroadcastPacket(DietBindingsPacket.From(bindings));

                return TextCommandResult.Success($"Reloaded. {result.DietCount} diets, {result.RefusedCount} refused, {result.WarningCount} warnings. Table in server-main.log.");
            });
    }

    /// <summary>Prints one compiled diet in full: capacities, both derived values per category,
    /// fallback, and every rule in win order with its priority, mask, verdict and multipliers --
    /// this is the primary way this task's work is verified (nothing else reads the compiled
    /// table yet).</summary>
    private void RegisterDietShowCommand(ICoreServerAPI api)
    {
        api.ChatCommands.Create("dietshow")
            .WithDescription("Print one compiled diet: capacities, derived values, fallback and rules in win order")
            .RequiresPrivilege(Privilege.commandplayer)
            .WithArgs(api.ChatCommands.Parsers.Word("id"))
            .HandleWith(args =>
            {
                string id = (string)args[0];
                CompiledDiet? diet = DietRuleRegistry.GetDiet(id);
                if (diet == null) return TextCommandResult.Error($"No compiled diet for id '{id}'.");

                return TextCommandResult.Success(FormatDietShow(diet));
            });
    }

    private static string FormatDietShow(CompiledDiet diet)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"diet '{diet.Id}' (domain '{diet.SourceDomain}')");

        foreach (EnumFoodCategory cat in new[] { EnumFoodCategory.Fruit, EnumFoodCategory.Vegetable, EnumFoodCategory.Grain, EnumFoodCategory.Protein, EnumFoodCategory.Dairy })
        {
            CompiledCategory c = diet.Categories[cat];
            sb.AppendLine($"  {cat,-10} capacity={c.Capacity:F3} gainScale={c.NutritionGainScale:F3} healthWeight={c.HealthWeight:F3}");
        }

        sb.AppendLine($"  fallback: satietyMult={diet.FallbackSatietyMult:F2} nutritionMult={diet.FallbackNutritionMult:F2}");
        sb.AppendLine($"  rules ({diet.Rules.Length}, win order):");

        for (int i = 0; i < diet.Rules.Length; i++)
        {
            CompiledRule r = diet.Rules[i];
            string requires = string.Join(",", FoodTagRegistry.TagNames(r.RequiresMask));
            string excludes = string.Join(",", FoodTagRegistry.TagNames(r.ExcludesMask));
            // satietyMult/nutritionMult below are the authored values -- Inedible forces both to 0
            // at Resolve() regardless (architecture 7.5), so they're not what a real eat produces.
            string inedibleNote = r.Verdict == DietVerdict.Inedible ? " (Inedible: Resolve() forces satiety/nutrition to 0, not the values above)" : "";
            sb.AppendLine($"    [{i}] priority={r.Priority} requires=[{requires}] excludes=[{excludes}] verdict={r.Verdict} satietyMult={r.SatietyMult:F2} nutritionMult={r.NutritionMult:F2}{inedibleNote}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Diagnostic: prints the caller's pending real-eat queues (DietProfileRegistry's
    /// nutrition-multiplier queue, MealIngredientNutritionHandoff's per-ingredient hand-off).
    /// Both are meant to be written only by a real eat's gather step -- DietMealFactsContext.
    /// DisplayOnly guards both against a GetContentNutritionFacts (tooltip/GUI-panel) call, so
    /// these should read 0 across any number of hovers with no eat in progress.</summary>
    private void RegisterFactsQueueDiagCommand(ICoreServerAPI api)
    {
        api.ChatCommands.Create("dietfactsqueue")
            .WithDescription("Diagnostic: print your pending real-eat nutrition-multiplier queue counts (stays 0 across hovers; only a real eat should move them)")
            .RequiresPrivilege(Privilege.commandplayer)
            .HandleWith(args =>
            {
                IPlayer caller = args.Caller.Player;
                long entityId = caller.Entity.EntityId;
                int profileQueueCount = DietProfileRegistry.PeekNutritionMultiplierQueueCount(entityId);
                int handoffCount = MealIngredientNutritionHandoff.PeekCount(entityId);
                return TextCommandResult.Success($"nutritionMultiplierQueue={profileQueueCount} mealIngredientHandoff={handoffCount}");
            });
    }

    private TextCommandResult DiagPlayerState(ICoreServerAPI api, IServerPlayer caller)
    {
        var entity = caller.Entity;
        var hunger = entity.GetBehavior<EntityBehaviorHunger>();
        var health = entity.GetBehavior<EntityBehaviorHealth>();

        string hungerSummary = hunger == null
            ? "unavailable (no hunger behavior on this entity)"
            : $"Sat={hunger.Saturation:F1}/{hunger.MaxSaturation:F1} FruitLvl={hunger.FruitLevel:F1} VegLvl={hunger.VegetableLevel:F1} ProteinLvl={hunger.ProteinLevel:F1} GrainLvl={hunger.GrainLevel:F1} DairyLvl={hunger.DairyLevel:F1}";

#pragma warning disable CS0618 // MaxHealthModifiers is obsolete for writing; reading it here is fine
        string healthSummary = health == null
            ? "unavailable (no health behavior on this entity)"
            : health.MaxHealthModifiers != null && health.MaxHealthModifiers.TryGetValue("nutrientHealthMod", out float nutrientBonus)
                ? $"nutrientHealthMod={nutrientBonus:F2}/12.50 MaxHealth={health.MaxHealth:F1}"
                : $"nutrientHealthMod=(not set) MaxHealth={health.MaxHealth:F1}";
#pragma warning restore CS0618

        ItemSlot? heldSlot = entity.RightHandItemSlot;
        string heldSummary;
        if (heldSlot?.Itemstack == null)
        {
            heldSummary = "not holding an item";
        }
        else
        {
            ItemStack heldStack = heldSlot.Itemstack;
            ulong tagMask = FoodTagRegistry.GetTagMask(api.World, heldSlot, out bool determined);
            string tags;
            if (!determined)
            {
                tags = "transition state unavailable, try again";
            }
            else
            {
                string joined = string.Join(", ", FoodTagRegistry.TagNames(tagMask));
                tags = joined.Length == 0 ? "(no tags)" : joined;
            }

            FoodNutritionProperties? afterTag = heldStack.Collectible.GetNutritionProperties(api.World, heldStack, entity);
            string satietySummary = afterTag == null ? "no nutrition data" : DescribeSatietyFold(entity, afterTag);
            heldSummary = $"{heldStack.Collectible.Code} tags=[{tags}] satiety: {satietySummary}";
        }

        string patchSummary = string.Join(", ", new[]
        {
            SaturationPatchDiagnostic(api),
            $"UpdateNutrientHealthBoost(prefix)={PatchCount(typeof(EntityBehaviorHunger), nameof(EntityBehaviorHunger.UpdateNutrientHealthBoost), prefix: true)}",
            $"CollectibleObject.GetNutritionProperties(postfix)={PatchCount(typeof(CollectibleObject), nameof(CollectibleObject.GetNutritionProperties), prefix: false)}",
            $"BlockLiquidContainerBase.GetNutritionProperties(postfix)={PatchCount(typeof(BlockLiquidContainerBase), nameof(BlockLiquidContainerBase.GetNutritionProperties), prefix: false)}"
        });

        string msg = string.Format(
            "EnableDietSystem={0}\nhunger: {1}\n{2}\nheld: {3}\npatches: {4}",
            Config.EnableDietSystem,
            hungerSummary,
            healthSummary,
            heldSummary,
            patchSummary);

        return TextCommandResult.Success(msg);
    }

    private TextCommandResult DiagItem(ICoreServerAPI api, IServerPlayer caller, string itemCode)
    {
        var loc = new AssetLocation(itemCode);
        CollectibleObject? collectible = (CollectibleObject?)api.World.GetItem(loc) ?? api.World.GetBlock(loc);
        if (collectible == null)
        {
            return TextCommandResult.Success($"No item or block registered with code '{itemCode}'.");
        }

        var entity = caller.Entity;
        var stack = new ItemStack(collectible);
        FoodNutritionProperties? vanilla = collectible.GetNutritionProperties(api.World, stack, entity);
        // GetNutritionProperties above already runs through our own postfix (Harmony patches the
        // real method), so `vanilla` here is already the fully resolved result -- this command
        // just reports what it is, it doesn't re-resolve anything itself.
        if (vanilla == null)
        {
            return TextCommandResult.Success($"{itemCode}: no nutrition data (not food, and no grant rule matched).");
        }

        string satietySummary = DescribeSatietyFold(entity, vanilla);
        return TextCommandResult.Success($"{itemCode}: category={vanilla.FoodCategory} satiety={vanilla.Satiety:F1} health={vanilla.Health:F2} | satiety fold: {satietySummary}");
    }

    /// <summary>Reports the satiety value /dietdiag and /dietassignrules-adjacent commands see
    /// after the diet patches (currently all no-ops, see /dietresolve for the rules-engine path
    /// once a diet is wired to bindings). Not a resolve of its own -- afterTag is already the
    /// fully patched value by the time this is called.</summary>
    private static string DescribeSatietyFold(Entity entity, FoodNutritionProperties afterTag)
    {
        return $"afterTagFold={afterTag.Satiety:F2}";
    }

    private static int PatchCount(Type type, string methodName, bool prefix)
    {
        MethodInfo? method = type.GetMethod(methodName);
        if (method == null) return 0;
        Patches? info = Harmony.GetPatchInfo(method);
        return (prefix ? info?.Prefixes?.Count : info?.Postfixes?.Count) ?? 0;
    }

    /// <summary>Owner-attributed prefix breakdown for OnEntityReceiveSaturation, plus the
    /// side this ran on and the live harmonyPatched value -- a bare count can't tell "dietsetup
    /// patched twice" apart from "one patch per side under two assembly load contexts"; this can.</summary>
    private static string SaturationPatchDiagnostic(ICoreAPI api)
    {
        MethodInfo? method = typeof(EntityBehaviorHunger).GetMethod(nameof(EntityBehaviorHunger.OnEntityReceiveSaturation));
        IList<Patch>? prefixes = method == null ? null : Harmony.GetPatchInfo(method)?.Prefixes;
        string byOwner = prefixes == null || prefixes.Count == 0
            ? "0"
            : string.Join("+", prefixes.GroupBy(p => p.owner).Select(g => $"{g.Key}:{g.Count()}"));

        return $"OnEntityReceiveSaturation(prefix)={byOwner} (side={api.Side}, harmonyPatched={harmonyPatched})";
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        base.StartClientSide(api);
        capi = api;

        api.Network.RegisterChannel(BindingsChannelName)
            .RegisterMessageType<DietBindingsPacket>()
            .SetMessageHandler<DietBindingsPacket>(OnBindingsPacket);

        RegisterHandbookPage(api);
        RegisterTagDiagCommand(api);
        RegisterDietResolveCommand(api);
    }

    /// <summary>Task 1: replaces this client's provisional (AssetsFinalize-time, likely empty)
    /// bindings with the server's authoritative table -- fires on join and again after the
    /// server admin runs /dietreload.</summary>
    private void OnBindingsPacket(DietBindingsPacket packet)
    {
        bindings = packet.ToBindingsFile();
    }

    /// <summary>Diagnostic (prompt 5, ahead of the resolver): prints the resolved food-tag set
    /// for the item in the caller's active hotbar slot, including live fresh/spoiled.</summary>
    private void RegisterTagDiagCommand(ICoreClientAPI api)
    {
        api.ChatCommands.Create("diettags")
            .WithDescription("Diagnostic: print the resolved food-tag set for the item in your active hotbar slot")
            .HandleWith(args =>
            {
                ItemSlot? slot = api.World.Player?.Entity?.RightHandItemSlot;
                if (slot?.Itemstack == null)
                {
                    return TextCommandResult.Success("Not holding an item.");
                }

                ulong mask = FoodTagRegistry.GetTagMask(api.World, slot, out bool determined);
                if (!determined)
                {
                    return TextCommandResult.Success($"{slot.Itemstack.Collectible.Code}: transition state unavailable, try again.");
                }

                string tags = string.Join(", ", FoodTagRegistry.TagNames(mask));
                return TextCommandResult.Success($"{slot.Itemstack.Collectible.Code}: {(tags.Length == 0 ? "(no tags)" : tags)}");
            });
    }

    /// <summary>Diagnostic: resolves the item in the caller's active hotbar slot against a given
    /// (explicitly named, not the caller's own resolved) diet id, printing the multipliers and
    /// match. Gathers and resolves but applies nothing -- per evaluation rule 2, this proves the
    /// pure core, not any Harmony patch; only a tooltip or a moving stat bar does that.</summary>
    private void RegisterDietResolveCommand(ICoreClientAPI api)
    {
        api.ChatCommands.Create("dietresolve")
            .WithDescription("Diagnostic: resolve the item in your active hotbar slot against a diet id (multipliers only, nothing applied)")
            .WithArgs(api.ChatCommands.Parsers.Word("dietId"))
            .HandleWith(args =>
            {
                ItemSlot? slot = api.World.Player?.Entity?.RightHandItemSlot;
                if (slot?.Itemstack == null)
                {
                    return TextCommandResult.Success("Not holding an item.");
                }

                string dietId = (string)args[0];
                CompiledDiet? diet = DietRuleRegistry.GetDiet(dietId);
                if (diet == null)
                {
                    return TextCommandResult.Success($"No compiled diet for id '{dietId}'.");
                }

                ulong tagMask = FoodTagRegistry.GetTagMask(api.World, slot, out float spoilLevel, out bool determined);
                if (!determined)
                {
                    return TextCommandResult.Success($"{slot.Itemstack.Collectible.Code}: transition state unavailable, try again.");
                }

                DietResolveResult result = DietResolver.Resolve(diet, tagMask, spoilLevel);
                string tags = string.Join(", ", FoodTagRegistry.TagNames(tagMask));

                return TextCommandResult.Success(
                    $"{slot.Itemstack.Collectible.Code} tags=[{tags}] vs diet '{dietId}': verdict={result.Verdict} satietyMult={result.Satiety:F2} nutritionMult={result.Nutrition:F2} matched={result.Matched} effects={result.Effects.Length}");
            });
    }

    private void RegisterHandbookPage(ICoreClientAPI api)
    {
        var handbookSys = api.ModLoader.GetModSystem<ModSystemSurvivalHandbook>();
        if (handbookSys == null) return;

        handbookSys.OnInitCustomPages += pages =>
        {
            // Vanilla's own JSON-authored pages get Init() called by GuiDialogSurvivalHandbook
            // before this event fires, but pages added here are never initialized by anyone else
            // -- skipping this leaves titleCached null forever, NRE-ing the moment a player types in the search box.
            var page = new GuiHandbookTextPage
            {
                pageCode = "dietsetup:diet-guide",
                Title = "dietsetup:handbook-title",
                Text = "dietsetup:handbook-body",
                categoryCode = "guide"
            };
            page.Init(api);
            pages.Add(page);
        };
    }
}
