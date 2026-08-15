using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using dietsetup.Diet;
using dietsetup.Gui;
using dietsetup.Network;
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

    // Rot-intake accumulator (Phase G3, for rfmechanics' goblin rot aura). Raw value + a
    // world.Calendar.TotalHours timestamp, not a single number -- lets any external reader
    // (rfmechanics, via a plain WatchedAttributes.GetDouble, no assembly reference) compute the
    // live, continuously-decaying value on demand with no tick loop on either side, mirroring
    // how the game's own transition system is lazy/timestamp-based. See RotIntakeAccrualPatch.
    public const string AttrRotIntake = "dietsetup:rotIntake";
    public const string AttrRotIntakeUpdatedHours = "dietsetup:rotIntakeUpdatedHours";

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

    // Static guard so PatchAll runs at most once for the process's lifetime, regardless of how
    // many DietSetupModSystem instances/Start() calls occur -- singleplayer instantiates a
    // separate instance per side (client + integrated server) in the same process, and mod
    // loading has been observed running its warning/scan passes more than once per session, so
    // an unpatch-then-repatch inside Start() (the previous approach) wasn't fully race-proof:
    // patches were observed stacking up to 2-3x, compounding the saturation multiplier.
    private static bool harmonyPatched;

    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        LoadConfig(api);

        api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<DietTriggerPacket>()
            .RegisterMessageType<DietSelectionPacket>();

        // Nutrition scaling is applied via Harmony patches on EntityBehaviorHunger and the
        // GetNutritionProperties chain, not by re-registering the "hunger" entity behavior class
        // -- RegisterEntityBehaviorClass is backed by a plain Dictionary.Add and throws on a
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
    // anything but a base asset at that stage. AssetsLoaded() runs after asset origins are
    // fully initialized (EnumServerRunPhase.AssetsReady -> AssetsFinalize) and still before
    // StartServerSide/StartClientSide, so the registry is populated in time for both.
    public override void AssetsLoaded(ICoreAPI api)
    {
        base.AssetsLoaded(api);
        LoadDietAssets(api);
    }

    public override void Dispose()
    {
        // Only the instance that actually applied the patch (its own `harmony` field is
        // non-null) resets the shared flag -- singleplayer disposes a client instance and a
        // server instance separately, and the one that lost the Start()-time race must not
        // reset harmonyPatched out from under the other instance's still-active patch.
        if (harmony != null)
        {
            harmony.UnpatchAll(HarmonyId);
            harmony = null;
            harmonyPatched = false;
        }
        base.Dispose();
    }

    /// <summary>
    /// Load dietsetup.json. Missing file or successful parse are stored back (this is what
    /// drops stale/removed keys and adds newly introduced ones, since StoreModConfig
    /// serializes the strongly-typed config, not raw JSON). Malformed JSON falls back to
    /// defaults in memory only, without touching the file, so the user's broken JSON is left
    /// in place to fix.
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

        // Newtonsoft's JsonConvert.DeserializeObject<T>("") returns null instead of throwing, so
        // an existing-but-empty (or otherwise null-producing) file looks identical to "file never
        // existed" to LoadModConfig. Without this check that would silently be treated as a first
        // run and overwritten with defaults below -- discarding whatever was there before (e.g. a
        // file truncated by a crash mid-write) with no warning at all, unlike the throwing case above.
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

        // Only overwrite when nothing is at risk (loaded == null -- nothing on disk to lose) or
        // the pre-rewrite backup actually succeeded. If fields would be dropped and the backup
        // itself fails (permissions, disk full, locked file), skipping the rewrite this session is
        // the only way to avoid silently losing those fields with no copy anywhere -- StoreModConfig
        // would otherwise run right after regardless of whether WarnAndBackupIfFieldsWillBeDropped
        // actually managed to protect anything.
        bool safeToOverwrite = loaded == null || WarnAndBackupIfFieldsWillBeDropped(api, filename);
        if (!safeToOverwrite)
        {
            api.Logger.Warning("[dietsetup] Skipping rewrite of {0} this session -- couldn't confirm a backup of fields that would be dropped. Will retry next load.", filename);
            return;
        }

        api.StoreModConfig(config, filename);
    }

    /// <summary>StoreModConfig always rewrites the file using only the current DietSetupConfig
    /// shape -- any JSON key with no matching C# property is silently dropped on that rewrite
    /// (documented VS behavior, not something dietsetup does deliberately). This update removes
    /// DietPreset and the slider-bound fields entirely, so an existing hand-tuned dietsetup.json
    /// (a live deployment has one) would lose that data with zero warning and zero trace the
    /// first time this version loads. Detect it, warn loudly, and back up the pre-rewrite file so
    /// the values are recoverable. Returns false only when fields would be dropped AND the backup
    /// could not be confirmed -- the caller uses that to skip the rewrite entirely rather than
    /// overwrite with no safety net.</summary>
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

    /// <summary>Loads the shipped profiles/tags/grants and registers them through the same
    /// RegisterProfile/RegisterTag/RegisterGrantRule calls a third-party mod would use -- one
    /// code path for "how content enters the registry," whether it's ours or theirs. Runs on both
    /// sides (Start(ICoreAPI) fires for client and server) so DietProfileRegistry stays identical
    /// on both, since the Harmony patches and the tooltip resolution both need it client-side too.</summary>
    private static void LoadDietAssets(ICoreAPI api)
    {
        DietProfile[]? profiles = api.Assets.Get(new AssetLocation("dietsetup", "config/profiles.json")).ToObject<DietProfile[]>();
        foreach (DietProfile profile in profiles ?? Array.Empty<DietProfile>())
        {
            DietProfileRegistry.RegisterProfile(profile);
        }

        Dictionary<string, string[]>? tags = api.Assets.Get(new AssetLocation("dietsetup", "config/tags.json")).ToObject<Dictionary<string, string[]>>();
        foreach ((string tag, string[] patterns) in tags ?? new Dictionary<string, string[]>())
        {
            foreach (string pattern in patterns)
            {
                DietProfileRegistry.RegisterTag(tag, pattern);
            }
        }

        DietGrantRule[]? grants = api.Assets.Get(new AssetLocation("dietsetup", "config/grants.json")).ToObject<DietGrantRule[]>();
        foreach (DietGrantRule grant in grants ?? Array.Empty<DietGrantRule>())
        {
            DietProfileRegistry.RegisterGrantRule(grant);
        }

        ValidateContent(api);
    }

    private static readonly HashSet<string> ValidCategories = new() { "Fruit", "Vegetable", "Protein", "Grain", "Dairy" };

    /// <summary>A typo'd category key (e.g. "Vegtable") fails completely silently otherwise -- the
    /// entry just never matches anything, and that profile behaves as pass-through for the real
    /// category with no error anywhere. Runs after all profiles/tags/grants are registered, so it
    /// catches both the shipped content and any third-party RegisterProfile call.</summary>
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

        var knownTags = new HashSet<string>(DietProfileRegistry.AllTagNames);
        foreach (DietGrantRule grant in DietProfileRegistry.AllGrantRules)
        {
            if (grant.Tag != null && !knownTags.Contains(grant.Tag))
            {
                api.Logger.Warning(
                    "[dietsetup] Grant rule for category '{0}' references tag '{1}', which is not defined in tags.json -- likely a typo. This rule's Tag match will never fire.",
                    grant.Category, grant.Tag);
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

    public void RegisterTag(string tag, string pattern) => DietProfileRegistry.RegisterTag(tag, pattern);

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

        api.Network.GetChannel(ChannelName).SetMessageHandler<DietSelectionPacket>(OnDietSelectionReceived);

        RegisterAdminGrantCommand(api);
        RegisterDrainSatietyCommand(api);
        RegisterTagMultCommand(api);
        RegisterRotIntakeDebugCommand(api);
    }

    private void OnPlayerCreate(IServerPlayer byPlayer)
    {
        byPlayer.SetModData(PendingModDataKey, true);
    }

    private void OnPlayerNowPlaying(IServerPlayer byPlayer)
    {
        MigrateLegacyProfileIfNeeded(byPlayer);

        if (!Config.EnableDietSystem || !Config.AutoPromptNewCharacters) return;
        if (!byPlayer.GetModData(PendingModDataKey, false)) return;

        byPlayer.SetModData(PendingModDataKey, false);
        sapi!.Network.GetChannel(ChannelName).SendPacket(new DietTriggerPacket(), byPlayer);
    }

    /// <summary>Existing dietConfigured=true players (the pre-rewrite flat-multiplier system, no
    /// reaction concept ever existed for them) get pointed at the "legacy_custom" sentinel, whose
    /// category defaults are computed from their own old attributes on every resolve -- see
    /// DietMigration. Idempotent via the AttrProfile presence check; a brand-new player (never
    /// had OldAttrConfigured either) isn't touched, and falls through to the normal auto-prompt /
    /// Config.DefaultProfileId path.</summary>
    private static void MigrateLegacyProfileIfNeeded(IServerPlayer byPlayer)
    {
        ITreeAttribute wa = byPlayer.Entity.WatchedAttributes;
        if (wa.HasAttribute(AttrProfile) || !wa.GetBool(OldAttrConfigured, false)) return;

        wa.SetString(AttrProfile, DietMigration.LegacyCustomProfileId);
        wa.SetBool(AttrConfigured, true);
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

    /// <summary>Debug/testing only: zeroes the calling player's own satiety bar while leaving
    /// their per-category nutrition levels (FruitLevel etc.) untouched -- OnEntityReceiveSaturation
    /// only lets nutrition rise while satiety isn't already full, so testing the max-HP-bonus
    /// ceiling normally means waiting out a real-time drain between bites. This skips the wait
    /// without touching the thing actually under test.</summary>
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

    /// <summary>Debug/testing only: sets or clears a "dietsetup:&lt;tag&gt;Mult" entity stat on the
    /// calling player, to simulate a race-trait grant without touching raceframework's JSON.
    /// EntityStats.Set defaults to a WeightedSum blend seeded with a "base" of 1, so the blended
    /// result is 1 + delta, not delta itself -- pass 0.3 to simulate a "+30% benefit" trait, not
    /// 1.3. Stats set here go through WatchedAttributes and sync to the calling client, so
    /// /dietdiag (client-side) will reflect the change immediately.</summary>
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

    /// <summary>Debug/testing only: get/set/clear the calling player's raw rot-intake
    /// accumulator (AttrRotIntake) directly, bypassing needing to eat rotten food repeatedly
    /// and wait out RotIntakeHalfLifeHours' calendar-time decay to see rfmechanics' goblin
    /// rot aura respond. Setting also stamps AttrRotIntakeUpdatedHours to "now" so the value
    /// doesn't immediately start decaying from a stale timestamp the moment it's set. Same
    /// controlserver bar as /diettagmult -- no legitimate non-debug purpose.</summary>
    private void RegisterRotIntakeDebugCommand(ICoreServerAPI api)
    {
        api.ChatCommands.Create("dietrotintake")
            .WithDescription("Debug: get/set/clear your own rot-intake accumulator (dietsetup:rotIntake), for testing rfmechanics' goblin rot aura without eating rotten food and waiting for decay.")
            .RequiresPrivilege(Privilege.controlserver)
            .WithArgs(api.ChatCommands.Parsers.OptionalFloat("value"))
            .HandleWith(args =>
            {
                IPlayer caller = args.Caller.Player;
                ITreeAttribute wa = caller.Entity.WatchedAttributes;

                if (args.Parsers[0].IsMissing)
                {
                    double nowHours = caller.Entity.World.Calendar.TotalHours;
                    double lastHours = wa.GetDouble(AttrRotIntakeUpdatedHours, nowHours);
                    double raw = wa.GetDouble(AttrRotIntake, 0.0);
                    return TextCommandResult.Success($"{AttrRotIntake}={raw:F4}, elapsed {nowHours - lastHours:F2}h since last write (halfLife={Config.RotIntakeHalfLifeHours:F1}h).");
                }

                float value = (float)args[0];
                wa.SetDouble(AttrRotIntake, value);
                wa.SetDouble(AttrRotIntakeUpdatedHours, caller.Entity.World.Calendar.TotalHours);
                return TextCommandResult.Success($"Set {AttrRotIntake}={value:F4} (timestamp reset to now, cap is {Config.RotIntakeCap:F2}). Check rfmechanics' /rfrotdiag to see the resulting aura shape.");
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
    }

    /// <summary>Diagnostic: with no argument, dumps the calling player's resolved profile,
    /// category defaults, live hunger-behavior values, and whether the 4 Harmony patches are
    /// actually attached. With an item code argument, reports how that item resolves (vanilla
    /// category vs. granted, final satiety/nutrition/reaction) without needing to eat it.</summary>
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
            $"OnEntityReceiveSaturation(prefix)={PatchCount(typeof(EntityBehaviorHunger), nameof(EntityBehaviorHunger.OnEntityReceiveSaturation), prefix: true)}",
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

    private void OnDietTriggerReceived(DietTriggerPacket packet)
    {
        // Don't open immediately: PlayerNowPlaying can fire fast enough (especially in
        // singleplayer, in-process) that our dialog opens while the vanilla character-creation
        // wizard is still visually closing, producing an overlap. Wait for that specific dialog
        // instance's own OnClosed event when it's present, guaranteeing strict ordering.
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
    /// Creative mode, matching vanilla's dev-convenience bypass for /charsel). The server
    /// independently re-validates the same flag before writing anything in
    /// OnDietSelectionReceived -- this client-side check is purely for instant local
    /// feedback, never trusted alone for the actual state mutation.</summary>
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
            // -- skipping this leaves titleCached null forever, which NREs the moment a player
            // types anything into the handbook search box (GuiHandbookTextPage.GetTextMatchWeight).
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
