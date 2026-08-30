using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using dietsetup.Diet;
using dietsetup.Gui;
using dietsetup.Network;
using dietsetup.Rules;
using dietsetup.Tags;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace dietsetup;

public class DietSetupModSystem : ModSystem
{
    public const string ChannelName = "dietselection";

    // Namespaced WatchedAttributes keys -- the documented, read-only-for-other-mods contract.
    public const string AttrProfile = "dietsetup:profile";
    public const string AttrConfigured = "dietsetup:configured";
    public const string AttrAllowSelOnce = "dietsetup:allowselonce";

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

    // Old flat-multiplier keys from the pre-rewrite system. Left in place, unread except by
    // DietMigration (which reads them fresh on every resolve for a "legacy_custom" player) and
    // the one-time migration check below -- never deleted, never written to again.
    private const string OldAttrConfigured = "dietConfigured";

    private const string PendingModDataKey = "dietsetup:pending";
    private const string HarmonyId = "dietsetup";

    private static DietSetupConfig? config;
    public static DietSetupConfig Config => config ??= new DietSetupConfig();

    private ICoreServerAPI? sapi;
    private ICoreClientAPI? capi;
    private GuiDialogDietSetup? dialog;
    private Harmony? harmony;

    // Static guard so PatchAll runs at most once for the process's lifetime -- singleplayer
    // instantiates a separate DietSetupModSystem per side in the same process, and an
    // unpatch-then-repatch inside Start() was observed stacking patches 2-3x, compounding the saturation math.
    private static bool harmonyPatched;

    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        LoadConfig(api);

        api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<DietTriggerPacket>()
            .RegisterMessageType<DietSelectionPacket>();

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

    // Mod-shipped assets aren't indexed yet during Start() -- api.Assets.Get() throws for
    // anything but a base asset then. AssetsLoaded() runs after asset origins are fully
    // initialized, still before StartServerSide/StartClientSide, so the registry is ready for both.
    public override void AssetsLoaded(ICoreAPI api)
    {
        base.AssetsLoaded(api);
        LoadDietAssets(api);
        LoadFoodTagAssets(api);
        LoadDietRuleAssets(api);
    }

    // Runs after AssetsLoaded, on both sides, once api.World.Collectibles is populated -- the
    // earliest point the tag registry can walk every collectible and resolve static masks. Diet
    // rules compile after that, since requires/excludes masks need FoodTagRegistry's tag bits final.
    public override void AssetsFinalize(ICoreAPI api)
    {
        base.AssetsFinalize(api);
        FoodTagRegistry.ResolveStaticTags(api);
        DietRuleRegistry.CompileAll(api);
    }

    // TEMP, prompt-5 verification only -- remove before this lands. Called from GameReady, not
    // AssetsFinalize -- api.World.Calendar is still null there (confirmed live), so any
    // perishable's UpdateAndGetTransitionState throws regardless of slot/inventory shape.
    private static void DumpFoodTagsForVerification(ICoreAPI api)
    {
        foreach (string code in new[] { "game:resin", "game:redmeat-raw", "game:axe-flint" })
        {
            var loc = new AssetLocation(code);
            CollectibleObject? collectible = (CollectibleObject?)api.World.GetItem(loc) ?? api.World.GetBlock(loc);
            if (collectible == null)
            {
                api.Logger.Notification("[dietsetup] tagdump {0}: not found", code);
                continue;
            }
            var slot = new DummySlot(new ItemStack(collectible));
            ulong mask = FoodTagRegistry.GetTagMask(api.World, slot, out bool determined);
            string tags = string.Join(", ", FoodTagRegistry.TagNames(mask));
            api.Logger.Notification("[dietsetup] tagdump {0}: determined={1} tags={2}", code, determined, tags.Length == 0 ? "(no tags)" : tags);
        }
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

    /// <summary>Merges every domain's config/profiles.json into the registry (tag-engine
    /// migration step 10) -- GetMany, not Get, so a race mod can ship its own profile (e.g.
    /// raceframework's Elf) with zero dietsetup code dependency, same pattern as
    /// LoadFoodTagAssets/LoadDietRuleAssets. A duplicate Id across domains is a hard error naming
    /// both, mirroring DietRuleRegistry.LoadFrom's duplicate-diet-id check -- same undebuggable
    /// last-writer-wins failure mode. Tag content is FoodTagRegistry's job now
    /// (LoadFoodTagAssets, config/foodtags.json) -- the old config/tags.json mechanism was retired
    /// in tag-engine migration step 9.</summary>
    private static void LoadDietAssets(ICoreAPI api)
    {
        DietProfileRegistry.Reset();
        Dictionary<AssetLocation, DietProfile[]> files = api.Assets.GetMany<DietProfile[]>(api.Logger, "config/profiles.json");
        var domainById = new Dictionary<string, string>();
        foreach ((AssetLocation loc, DietProfile[] profiles) in files)
        {
            foreach (DietProfile profile in profiles ?? Array.Empty<DietProfile>())
            {
                if (domainById.TryGetValue(profile.Id, out string? existingDomain))
                {
                    throw new InvalidOperationException(
                        $"[dietsetup] Duplicate profile id '{profile.Id}' -- registered by both domain '{existingDomain}' and domain '{loc.Domain}'.");
                }
                domainById[profile.Id] = loc.Domain;
                DietProfileRegistry.RegisterProfile(profile);
            }
        }

        ValidateContent(api);
    }

    /// <summary>Merges every domain's config/foodtags.json into the tag registry (prompt 5) --
    /// GetMany, not Get, so a compat pack can add tags for a third-party mod without touching
    /// dietsetup's own file. dietsetup ships the vanilla tags only.</summary>
    private static void LoadFoodTagAssets(ICoreAPI api)
    {
        FoodTagRegistry.Reset();
        Dictionary<AssetLocation, FoodTagConfigFile> files = api.Assets.GetMany<FoodTagConfigFile>(api.Logger, "config/foodtags.json");
        foreach (FoodTagConfigFile file in files.Values)
        {
            FoodTagRegistry.LoadFrom(file);
        }
    }

    /// <summary>Merges every domain's config/diets/*.json into the rules engine (prompt 6) --
    /// pathBegins "config/diets/" catches every file under that folder across every domain, one
    /// diet definition per file. Duplicate diet id across domains throws here (spec section 11:
    /// hard error at startup, not a log-and-skip).</summary>
    private static void LoadDietRuleAssets(ICoreAPI api)
    {
        DietRuleRegistry.Reset();
        Dictionary<AssetLocation, DietDefinitionFile> files = api.Assets.GetMany<DietDefinitionFile>(api.Logger, "config/diets/");
        foreach ((AssetLocation loc, DietDefinitionFile file) in files)
        {
            DietRuleRegistry.LoadFrom(file, loc.Domain, api.Logger);
        }
    }

    private static readonly HashSet<string> ValidCategories = new() { "Fruit", "Vegetable", "Protein", "Grain", "Dairy" };

    /// <summary>A typo'd category key (e.g. "Vegtable") fails silently otherwise -- the entry just
    /// never matches, and that profile behaves as pass-through with no error. Runs after all
    /// profiles/tags are registered, catching shipped content and third-party registrations.</summary>
    private static void ValidateContent(ICoreAPI api)
    {
        foreach (DietProfile profile in DietProfileRegistry.AllProfiles)
        {
            foreach (string key in profile.CategoryDefaults.Keys)
            {
                if (!ValidCategories.Contains(key))
                {
                    api.Logger.Warning(
                        "[dietsetup] Profile '{0}' has a CategoryDefaults entry for unrecognized category '{1}' -- likely a typo (valid: Fruit, Vegetable, Protein, Grain, Dairy). This entry will never be used.",
                        profile.Id, key);
                }
            }
        }
    }

    // ── Public API (reachable via api.ModLoader.GetModSystem<DietSetupModSystem>()) ──

    /// <summary>Silent, no dialog. Rejects an unrecognized profileId (logs, does not throw --
    /// mirrors how OnDietSelectionReceived treats a bad client-sent id).</summary>
    public void AssignProfile(IServerPlayer player, string profileId)
    {
        if (DietProfileRegistry.GetProfile(profileId) == null)
        {
            sapi?.Logger.Warning("[dietsetup] AssignProfile called with unknown profile id '{0}' for {1}, ignored.", profileId, player.PlayerName);
            return;
        }

        ITreeAttribute wa = player.Entity.WatchedAttributes;
        wa.SetString(AttrProfile, profileId);
        wa.SetBool(AttrConfigured, true);
    }

    /// <summary>The raw assigned profile id (may be "legacy_custom"), or null if the player has
    /// never been configured.</summary>
    public string? GetProfile(IServerPlayer player) => player.Entity.WatchedAttributes.GetString(AttrProfile, null!);

    public void RegisterProfile(DietProfile profile) => DietProfileRegistry.RegisterProfile(profile);

    public override void StartServerSide(ICoreServerAPI api)
    {
        base.StartServerSide(api);
        sapi = api;

        // PlayerCreate fires exactly once ever per player UID (true new-character creation) --
        // never on relog, never on death respawn (PlayerRespawn is a separate event we don't
        // subscribe to), and never for players who existed before this mod was installed.
        api.Event.PlayerCreate += OnPlayerCreate;

        // PlayerNowPlaying only fires once the vanilla character-creation wizard has closed
        // (or been skipped), so this trigger can never stack with it.
        api.Event.PlayerNowPlaying += OnPlayerNowPlaying;

        // Closes the nutrition-multiplier queue's only leak path (DietProfileRegistry, step 9) --
        // without this a departed player's dictionary entry sits forever.
        api.Event.PlayerDisconnect += OnPlayerDisconnect;

        api.Network.GetChannel(ChannelName).SetMessageHandler<DietSelectionPacket>(OnDietSelectionReceived);

        RegisterAdminGrantCommand(api);
        RegisterDrainSatietyCommand(api);
        RegisterTagMultCommand(api);
        RegisterRotIntakeDebugCommand(api);
        RegisterAssignRulesDietCommand(api);

        // GameReady, not AssetsFinalize -- CharacterSystem.traits is populated by its own
        // ServerRunPhase(LoadGamePre) handler, which runs concurrently with mod StartServerSide
        // calls. GameReady is the next phase, guaranteeing LoadGamePre has fully completed first.
        api.Event.ServerRunPhase(EnumServerRunPhase.GameReady, () => ValidateTraitKeys(api));
        api.Event.ServerRunPhase(EnumServerRunPhase.GameReady, () => DumpFoodTagsForVerification(api));
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

    private void OnPlayerCreate(IServerPlayer byPlayer)
    {
        byPlayer.SetModData(PendingModDataKey, true);
    }

    private static void OnPlayerDisconnect(IServerPlayer byPlayer)
    {
        DietProfileRegistry.RemoveNutritionMultiplierQueue(byPlayer.Entity.EntityId);
        DietProfileRegistry.ClearWarnedMissingProfile(byPlayer.Entity.EntityId);
    }

    private void OnPlayerNowPlaying(IServerPlayer byPlayer)
    {
        MigrateLegacyProfileIfNeeded(byPlayer);
        MigrateLegacyRotIntakeIfNeeded(byPlayer);

        if (!Config.EnableDietSystem || !Config.AutoPromptNewCharacters) return;
        if (!byPlayer.GetModData(PendingModDataKey, false)) return;

        byPlayer.SetModData(PendingModDataKey, false);
        sapi!.Network.GetChannel(ChannelName).SendPacket(new DietTriggerPacket(), byPlayer);
    }

    /// <summary>Existing dietConfigured=true players (pre-rewrite flat-multiplier system, no
    /// reaction concept) get pointed at the "legacy_custom" sentinel, computed from their own old
    /// attributes on every resolve -- see DietMigration. Idempotent via the AttrProfile presence check.</summary>
    private static void MigrateLegacyProfileIfNeeded(IServerPlayer byPlayer)
    {
        ITreeAttribute wa = byPlayer.Entity.WatchedAttributes;
        if (wa.HasAttribute(AttrProfile) || !wa.GetBool(OldAttrConfigured, false)) return;

        wa.SetString(AttrProfile, DietMigration.LegacyCustomProfileId);
        wa.SetBool(AttrConfigured, true);
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

    private void OnDietSelectionReceived(IServerPlayer fromPlayer, DietSelectionPacket packet)
    {
        if (DietProfileRegistry.GetProfile(packet.ProfileId) == null)
        {
            sapi?.Logger.Warning("[dietsetup] {0} sent unknown profile id '{1}', ignored.", fromPlayer.PlayerName, packet.ProfileId);
            return;
        }

        sapi?.Logger.Notification("[dietsetup] {0} selected profile '{1}'", fromPlayer.PlayerName, packet.ProfileId);

        // Never trust the client beyond the id itself -- SetString always overwrites by key
        // (never a "fill empty slot only" no-op), so this also correctly overwrites a prior
        // "legacy_custom" migration assignment when a grandfathered player repicks.
        ITreeAttribute wa = fromPlayer.Entity.WatchedAttributes;
        wa.SetString(AttrProfile, packet.ProfileId);
        wa.SetBool(AttrConfigured, true);
        wa.SetBool(AttrAllowSelOnce, false); // consume the reopen grant, mirrors CharacterSystem clearing "allowcharselonce"
    }

    /// <summary>Admin-only: grant a specific online player one-time permission to reopen the
    /// dialog via /dietsel, mirroring vanilla's /charsel + allowcharselonce pattern.</summary>
    private void RegisterAdminGrantCommand(ICoreServerAPI api)
    {
        api.ChatCommands.Create("dietselgrant")
            .WithDescription("Grant a player one-time permission to reopen the Diet Setup dialog")
            .RequiresPrivilege(Privilege.commandplayer)
            .WithArgs(api.ChatCommands.Parsers.OnlinePlayer("player"))
            .HandleWith(args =>
            {
                IPlayer target = (IPlayer)args[0];
                target.Entity.WatchedAttributes.SetBool(AttrAllowSelOnce, true);

                if (target is IServerPlayer targetServerPlayer)
                {
                    targetServerPlayer.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("dietsetup:granted-target"), EnumChatType.Notification);
                }

                return TextCommandResult.Success(Lang.Get("dietsetup:granted-admin", target.PlayerName));
            });
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

    /// <summary>Debug/testing only: sets/clears a "dietsetup:&lt;tag&gt;Mult" entity stat on the
    /// caller, to simulate a race-trait grant. EntityStats.Set seeds a WeightedSum base of 1, so
    /// the blended result is 1 + delta -- pass 0.3 for "+30%", not 1.3. Syncs to client, so /dietdiag reflects it.</summary>
    private void RegisterTagMultCommand(ICoreServerAPI api)
    {
        api.ChatCommands.Create("diettagmult")
            .WithDescription("Debug: set/clear a dietsetup:<tag>Mult entity stat on yourself, to simulate a race-trait grant. Blended value is 1 + delta (pass 0.3 for a 1.3x effect, not 1.3).")
            // controlserver (admin-only), not commandplayer -- this command has no legitimate
            // non-debug purpose, unlike /dietdrainsatiety, so it gets the highest bar available
            // rather than being reachable by ordinary moderators.
            .RequiresPrivilege(Privilege.controlserver)
            .WithArgs(api.ChatCommands.Parsers.Word("tag"), api.ChatCommands.Parsers.OptionalFloat("delta"))
            .HandleWith(args =>
            {
                IPlayer caller = args.Caller.Player;
                string tag = (string)args[0];
                string statKey = "dietsetup:" + tag + "Mult";

                // FloatArgParser.GetValue() doesn't null out when the optional arg is missing
                // (unlike WordArgParser) -- it always returns the boxed default. Check IsMissing
                // on the parser itself instead of `args[1] == null`.
                if (args.Parsers[1].IsMissing)
                {
                    caller.Entity.Stats.Remove(statKey, "debug");
                    return TextCommandResult.Success($"Cleared {statKey}. Blended value now {caller.Entity.Stats.GetBlended(statKey):F2}.");
                }

                float delta = (float)args[1];
                caller.Entity.Stats.Set(statKey, "debug", delta, false);
                return TextCommandResult.Success($"Set {statKey} debug delta to {delta:F2}. Blended value now {caller.Entity.Stats.GetBlended(statKey):F2} (base 1 + delta).");
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

    /// <summary>Standing admin tool, not a throwaway: sets the caller's dietsetup:profile directly
    /// to a rules-engine diet id (e.g. "goblin"), bypassing the old profile picker. /dietsel's own
    /// picker only offers profiles.json ids until diet ids and profile ids are unified (tag-engine
    /// migration step 10/11) -- until then this is the only assignment path for a rules-engine diet.</summary>
    private void RegisterAssignRulesDietCommand(ICoreServerAPI api)
    {
        api.ChatCommands.Create("dietassignrules")
            .WithDescription("Admin: set your own dietsetup:profile directly to a rules-engine diet id, bypassing the profile picker")
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

                caller.Entity.WatchedAttributes.SetString(AttrProfile, dietId);
                return TextCommandResult.Success($"dietsetup:profile set to '{dietId}' (rules-engine diet, bypasses the profile picker).");
            });
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        base.StartClientSide(api);
        capi = api;

        api.Network.GetChannel(ChannelName).SetMessageHandler<DietTriggerPacket>(OnDietTriggerReceived);

        RegisterSelfCommand(api);
        RegisterHandbookPage(api);
        RegisterDiagCommand(api);
        RegisterTagDiagCommand(api);
        RegisterDietResolveCommand(api);
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

    /// <summary>Diagnostic (prompt 6): resolves the item in the caller's active hotbar slot
    /// against their currently assigned diet (dietsetup:profile, same attribute the old
    /// profile system uses -- prompt 6 has no assignment UI of its own yet) through the rules
    /// engine, printing verdict, satiety, nutrition and every rule that matched. Optional dietId
    /// overrides the assigned diet -- there's no assignment UI yet for rules-engine diet ids
    /// (e.g. "goblin"), so without this override the command would be untestable.</summary>
    private void RegisterDietResolveCommand(ICoreClientAPI api)
    {
        api.ChatCommands.Create("dietresolve")
            .WithDescription("Diagnostic: resolve the item in your active hotbar slot through the rules engine, against your assigned diet or an optional override id")
            .WithArgs(api.ChatCommands.Parsers.OptionalWord("dietId"))
            .HandleWith(args =>
            {
                var playerEntity = api.World.Player?.Entity;
                ItemSlot? slot = playerEntity?.RightHandItemSlot;
                if (slot?.Itemstack == null)
                {
                    return TextCommandResult.Success("Not holding an item.");
                }

                string dietId = args[0] as string ?? playerEntity!.WatchedAttributes.GetString(AttrProfile, Config.DefaultProfileId);
                CompiledDiet? diet = DietRuleRegistry.GetDiet(dietId);
                if (diet == null)
                {
                    return TextCommandResult.Success($"No rules-engine diet registered for id '{dietId}' yet -- the goblin/elf/orc port is a later migration step.");
                }

                var matchedRuleIndices = new List<int>();
                DietResolveResult result = DietResolver.Resolve(api, api.World, slot, diet, playerEntity, 1f, matchedRuleIndices);
                if (!result.Determined)
                {
                    return TextCommandResult.Success($"{slot.Itemstack.Collectible.Code}: transition state unavailable, try again.");
                }

                string ruleLabels = matchedRuleIndices.Count == 0
                    ? "(none, default applied)"
                    : string.Join(", ", matchedRuleIndices.Select(i => diet.Rules[i].DebugLabel));
                string degradedNote = diet.Degraded ? " [DEGRADED: rule references a missing custom effect key, default behaviour applied]" : "";

                return TextCommandResult.Success(
                    $"{slot.Itemstack.Collectible.Code} vs diet '{dietId}'{degradedNote}: verdict={result.Verdict} satiety={result.Satiety:F2} nutrition={result.Nutrition:F2} matched=[{ruleLabels}]");
            });
    }

    /// <summary>Diagnostic: with no argument, dumps the caller's resolved profile, category
    /// defaults, live hunger values, and whether the 4 Harmony patches are attached. With an item
    /// code, reports how that item resolves without needing to eat it.</summary>
    private void RegisterDiagCommand(ICoreClientAPI api)
    {
        api.ChatCommands.Create("dietdiag")
            .WithDescription("Diagnostic: dump diet state for the calling player, or resolution for a given item code")
            .WithArgs(api.ChatCommands.Parsers.OptionalWord("itemcode"))
            .HandleWith(args =>
            {
                string? itemCode = args[0] as string;
                return string.IsNullOrEmpty(itemCode) ? DiagPlayerState(api) : DiagItem(api, itemCode);
            });
    }

    private TextCommandResult DiagPlayerState(ICoreClientAPI api)
    {
        var entity = api.World.Player.Entity;
        var wa = entity.WatchedAttributes;
        var hunger = entity.GetBehavior<EntityBehaviorHunger>();
        var health = entity.GetBehavior<EntityBehaviorHealth>();

        DietProfile profile = DietProfileRegistry.ResolveProfileForEntity(entity, Config.DefaultProfileId);
        string catSummary = string.Join(" | ", new[] { "Fruit", "Vegetable", "Protein", "Grain", "Dairy" }.Select(cat =>
        {
            DietCategoryDefault cd = profile.CategoryDefaults.TryGetValue(cat, out DietCategoryDefault? found) ? found : DietCategoryDefault.PassThrough;
            return $"{cat}: sat={cd.SatietyMult:F2} nut={cd.NutritionMult:F2} reaction={(cd.Reaction != null ? cd.Reaction.Health.ToString("F1") : "none")}";
        }));

#pragma warning disable CS0618 // MaxHealthModifiers is obsolete for writing; reading it here is fine
        float nutrientBonus = health?.MaxHealthModifiers != null && health.MaxHealthModifiers.TryGetValue("nutrientHealthMod", out float b) ? b : -1f;
#pragma warning restore CS0618

        string patchSummary = string.Join(", ", new[]
        {
            SaturationPatchDiagnostic(api),
            $"UpdateNutrientHealthBoost(prefix)={PatchCount(typeof(EntityBehaviorHunger), nameof(EntityBehaviorHunger.UpdateNutrientHealthBoost), prefix: true)}",
            $"CollectibleObject.GetNutritionProperties(postfix)={PatchCount(typeof(CollectibleObject), nameof(CollectibleObject.GetNutritionProperties), prefix: false)}",
            $"BlockLiquidContainerBase.GetNutritionProperties(postfix)={PatchCount(typeof(BlockLiquidContainerBase), nameof(BlockLiquidContainerBase.GetNutritionProperties), prefix: false)}"
        });

        string msg = string.Format(
            "EnableDietSystem={0} profile={1} configured={2} allowSelOnce={3}\ncategories: {4}\nhunger: Sat={5:F1}/{6:F1} FruitLvl={7:F1} VegLvl={8:F1} ProteinLvl={9:F1} GrainLvl={10:F1} DairyLvl={11:F1} | nutrientHealthMod={12:F2}/12.50 MaxHealth={13:F1}\npatches: {14}",
            Config.EnableDietSystem,
            wa.GetString(AttrProfile, "(unconfigured, falls back to " + Config.DefaultProfileId + ")"),
            wa.GetBool(AttrConfigured, false),
            wa.GetBool(AttrAllowSelOnce, false),
            catSummary,
            hunger?.Saturation ?? -1f, hunger?.MaxSaturation ?? -1f,
            hunger?.FruitLevel ?? -1f, hunger?.VegetableLevel ?? -1f, hunger?.ProteinLevel ?? -1f, hunger?.GrainLevel ?? -1f, hunger?.DairyLevel ?? -1f,
            nutrientBonus, health?.MaxHealth ?? -1f,
            patchSummary);

        return TextCommandResult.Success(msg);
    }

    private TextCommandResult DiagItem(ICoreClientAPI api, string itemCode)
    {
        var loc = new AssetLocation(itemCode);
        CollectibleObject? collectible = (CollectibleObject?)api.World.GetItem(loc) ?? api.World.GetBlock(loc);
        if (collectible == null)
        {
            return TextCommandResult.Success($"No item or block registered with code '{itemCode}'.");
        }

        var entity = api.World.Player.Entity;
        FoodNutritionProperties? vanilla = collectible.GetNutritionProperties(api.World, new ItemStack(collectible), entity);
        // GetNutritionProperties above already runs through our own postfix (Harmony patches the
        // real method), so `vanilla` here is already the fully resolved result -- this command
        // just reports what it is, it doesn't re-resolve anything itself.
        if (vanilla == null)
        {
            return TextCommandResult.Success($"{itemCode}: no nutrition data (not food, and no grant rule matched).");
        }

        return TextCommandResult.Success($"{itemCode}: category={vanilla.FoodCategory} satiety={vanilla.Satiety:F1} health={vanilla.Health:F2}");
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

    private void OnDietTriggerReceived(DietTriggerPacket packet)
    {
        // Don't open immediately: PlayerNowPlaying can fire fast enough (especially singleplayer,
        // in-process) that our dialog opens while vanilla's character-creation wizard is still
        // visually closing. Wait for that dialog's own OnClosed event for strict ordering.
        GuiDialog? createCharDlg = capi!.Gui.LoadedGuis.FirstOrDefault(dlg => dlg is GuiDialogCreateCharacter && dlg.IsOpened());
        if (createCharDlg != null)
        {
            createCharDlg.OnClosed += OpenDialog;
        }
        else
        {
            OpenDialog();
        }
    }

    private void OpenDialog()
    {
        if (dialog != null && dialog.IsOpened()) return;
        dialog = new GuiDialogDietSetup(capi!);
        dialog.TryOpen();
    }

    /// <summary>Self-service reopen, gated by the already-synced allowdietselonce flag (or
    /// Creative, matching vanilla's /charsel dev bypass). The server independently re-validates
    /// the same flag before writing anything -- this client-side check is purely for instant local feedback.</summary>
    private void RegisterSelfCommand(ICoreClientAPI api)
    {
        api.ChatCommands.Create("dietsel")
            .WithDescription("Reopen the Diet Setup dialog (requires admin-granted permission)")
            .HandleWith(args =>
            {
                var entity = api.World.Player.Entity;
                bool allowed = entity.WatchedAttributes.GetBool(AttrAllowSelOnce, false)
                               || api.World.Player.WorldData.CurrentGameMode == EnumGameMode.Creative;

                if (!allowed)
                {
                    return TextCommandResult.Success(Lang.Get("dietsetup:noaccess"));
                }

                OpenDialog();
                return TextCommandResult.Success("");
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
