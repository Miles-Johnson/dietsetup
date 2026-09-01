using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dietsetup.Binding;
using dietsetup.Grants;
using dietsetup.Tags;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace dietsetup.Rules;

/// <summary>One pipeline run's result: the human-readable log (written to server-main.log in
/// full) plus the counts /dietreload's chat summary needs, and the parsed bindings table, which
/// the caller stores per-side and syncs to clients (task 1).</summary>
public readonly struct DietLoadResult
{
    public readonly string Log;
    public readonly BindingsFile Bindings;
    public readonly int DietCount;
    public readonly int RefusedCount;
    public readonly int WarningCount;

    public DietLoadResult(string log, BindingsFile bindings, int dietCount, int refusedCount, int warningCount)
    {
        Log = log;
        Bindings = bindings;
        DietCount = dietCount;
        RefusedCount = refusedCount;
        WarningCount = warningCount;
    }
}

/// <summary>Runs the full 8-step load pipeline (architecture 6) end to end. Called once from
/// AssetsFinalize on each side, and again by /dietreload -- the whole point of this being one
/// function is that both call sites run identical steps in identical order.</summary>
public static class DietLoadPipeline
{
    private const string ModConfigDietsDir = "dietsetup/diets";
    private const string ModConfigBindingsFile = "dietsetup/bindings.json";

    public static DietLoadResult RunAndLog(ICoreAPI api)
    {
        var log = new List<string>();
        int warningCount = 0;

        // Step 1
        FoodTagRegistry.Reset();

        // Edibility grants (architecture 7.6) -- must run before tag resolution below, so a
        // granted item's freshly-set NutritionProps is visible to FoodTagRegistry's
        // relevance/untagged-nutritious accounting instead of looking untagged-and-irrelevant.
        FoodOverrideRegistry.LoadApplyAndLog(api, log);

        // Step 2
        LoadTags(api, log);
        FoodTagRegistry.ResolveStaticTags(api);

        // Step 3
        var refused = new List<(string Id, DietValidationMessage Reason)>();
        Dictionary<string, (DietDocumentFile Doc, string Domain)> raw = LoadDietDocuments(api, log, refused);

        // Steps 4-7: extends, compile, derive, validate -- one diet at a time, in id order for a
        // deterministic log.
        var compiledTable = new Dictionary<string, CompiledDiet>();

        var rawDocs = raw.ToDictionary(kv => kv.Key, kv => kv.Value.Doc);
        foreach (string id in raw.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var fatal = new List<DietValidationMessage>();
            var warnings = new List<DietValidationMessage>();

            if (id == DietIdResolver.ClearKeyword)
            {
                var reserved = new DietValidationMessage(15, "id 'clear' is reserved for /dietassignrules clear, diet refused");
                refused.Add((id, reserved));
                api.Logger.Error("[dietsetup] diet '{0}': rule {1}, {2}", id, reserved.Rule, reserved.Text);
                continue;
            }

            DietDocumentFile? resolved = DietExtendsResolver.Resolve(id, rawDocs, out string? extendsError);
            if (resolved == null)
            {
                fatal.Add(new DietValidationMessage(3, extendsError ?? "extends resolution failed"));
                refused.Add((id, fatal[0]));
                foreach (DietValidationMessage f in fatal) api.Logger.Error("[dietsetup] diet '{0}': rule {1}, {2}", id, f.Rule, f.Text);
                continue;
            }

            CompiledDiet? compiledDiet = DietCompiler.Compile(id, resolved, raw[id].Domain, DietSetupModSystem.Config.CapacityFloor, fatal, warnings);

            foreach (DietValidationMessage w in warnings)
            {
                api.Logger.Warning("[dietsetup] diet '{0}': rule {1}, {2}", id, w.Rule, w.Text);
            }
            warningCount += warnings.Count;

            if (compiledDiet == null)
            {
                refused.Add((id, fatal[0]));
                foreach (DietValidationMessage f in fatal) api.Logger.Error("[dietsetup] diet '{0}': rule {1}, {2}", id, f.Rule, f.Text);
                continue;
            }

            compiledTable[id] = compiledDiet;
        }

        DietRuleRegistry.ReplaceAll(compiledTable);

        // Rule 14 (warning): per diet, granted items (7.6) no rule matches -- one line per diet
        // naming the count, not one line per item.
        warningCount += LogUnmatchedGrantedItems(api, log, compiledTable);

        // Step 8
        log.Add($"[dietsetup] diets: {compiledTable.Count} loaded, {refused.Count} refused");
        int idColumnWidth = compiledTable.Count == 0 ? 0 : compiledTable.Values.Max(d => d.Id.Length) + 1;
        foreach (CompiledDiet diet in compiledTable.Values.OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            log.Add(FormatDietRow(diet, idColumnWidth));
        }
        // Refused diets are already logged once via api.Logger.Error at the point of refusal
        // above; a REFUSED row here duplicated that into the Notification-level table.

        BindingsFile bindings = LoadAndLogBindings(api, log, compiledTable, ref warningCount);

        log.Add($"[dietsetup] untagged nutritious collectibles: {FoodTagRegistry.UntaggedNutritiousCount}");

        foreach (string line in log) api.Logger.Notification(line);
        return new DietLoadResult(string.Join("\n", log), bindings, compiledTable.Count, refused.Count, warningCount);
    }

    private static void LoadTags(ICoreAPI api, List<string> log)
    {
        Dictionary<AssetLocation, FoodTagConfigFile> files = api.Assets.GetMany<FoodTagConfigFile>(api.Logger, "config/foodtags.json");
        foreach ((AssetLocation loc, FoodTagConfigFile file) in files)
        {
            FoodTagRegistry.LoadFrom(file);
            int count = file.Source.Count + file.State.Count + file.Form.Count;
            api.Logger.Notification("[dietsetup] tags: domain '{0}' registered {1} tag(s)", loc.Domain, count);
        }
    }

    // Assets first (every domain), then ModConfig whole-file overrides on top (architecture 6.2).
    private static Dictionary<string, (DietDocumentFile Doc, string Domain)> LoadDietDocuments(ICoreAPI api, List<string> log, List<(string Id, DietValidationMessage Reason)> refused)
    {
        var raw = new Dictionary<string, (DietDocumentFile Doc, string Domain)>();
        var seenPerDomain = new Dictionary<string, HashSet<string>>();
        var sourcePath = new Dictionary<string, string>();
        var crossDomainPaths = new Dictionary<string, List<string>>();

        Dictionary<AssetLocation, DietDocumentFile> assetFiles = api.Assets.GetMany<DietDocumentFile>(api.Logger, "config/diets/");
        foreach ((AssetLocation loc, DietDocumentFile doc) in assetFiles)
        {
            if (string.IsNullOrEmpty(doc.Id))
            {
                api.Logger.Error("[dietsetup] diet file '{0}': rule 2, missing 'id', skipped", loc);
                continue;
            }

            if (!seenPerDomain.TryGetValue(loc.Domain, out HashSet<string>? idsInDomain))
            {
                seenPerDomain[loc.Domain] = idsInDomain = new HashSet<string>();
            }
            if (!idsInDomain.Add(doc.Id))
            {
                api.Logger.Error("[dietsetup] diet '{0}': rule 2, declared twice in domain '{1}' ('{2}' skipped)", doc.Id, loc.Domain, loc);
                continue;
            }

            // Cross-domain collision refuses both, not last-domain-wins: silent override is the
            // exact compat-pack failure mode rule 2 exists to catch (orchestrator decision, 2026-08-30).
            // A ModConfig override below can still resolve it explicitly.
            if (raw.TryGetValue(doc.Id, out var existing) && existing.Domain != loc.Domain)
            {
                if (!crossDomainPaths.TryGetValue(doc.Id, out List<string>? paths))
                {
                    crossDomainPaths[doc.Id] = paths = new List<string> { sourcePath[doc.Id] };
                }
                paths.Add(loc.ToString());
                raw.Remove(doc.Id);
                continue;
            }

            raw[doc.Id] = (doc, loc.Domain);
            sourcePath[doc.Id] = loc.ToString();
        }

        string modConfigDir = Path.Combine(GamePaths.ModConfig, ModConfigDietsDir);
        if (Directory.Exists(modConfigDir))
        {
            var seenInModConfig = new HashSet<string>();
            foreach (string path in Directory.GetFiles(modConfigDir, "*.json"))
            {
                DietDocumentFile? doc;
                try
                {
                    doc = JsonConvert.DeserializeObject<DietDocumentFile>(File.ReadAllText(path));
                }
                catch (Exception ex)
                {
                    api.Logger.Error("[dietsetup] ModConfig diet override '{0}' failed to parse, skipped: {1}", path, ex.Message);
                    continue;
                }

                if (doc == null || string.IsNullOrEmpty(doc.Id))
                {
                    api.Logger.Error("[dietsetup] ModConfig diet override '{0}': rule 2, missing 'id', skipped", path);
                    continue;
                }

                if (!seenInModConfig.Add(doc.Id))
                {
                    api.Logger.Error("[dietsetup] ModConfig diet override for '{0}' declared twice under {1}, '{2}' skipped", doc.Id, modConfigDir, path);
                    continue;
                }

                if (sourcePath.TryGetValue(doc.Id, out string? losingPath))
                {
                    log.Add($"[dietsetup] diet '{doc.Id}': ModConfig override wins ({path}) over asset ({losingPath})");
                }
                else if (crossDomainPaths.ContainsKey(doc.Id))
                {
                    log.Add($"[dietsetup] diet '{doc.Id}': ModConfig override ({path}) resolves a cross-domain conflict");
                }

                crossDomainPaths.Remove(doc.Id);
                raw[doc.Id] = (doc, "ModConfig");
                sourcePath[doc.Id] = path;
            }
        }

        foreach ((string id, List<string> paths) in crossDomainPaths)
        {
            refused.Add((id, new DietValidationMessage(2, $"declared in multiple domains ({string.Join(", ", paths)}), both refused")));
        }

        return raw;
    }

    // Load-time only (like the rest of this pipeline) -- resolves each granted item against each
    // compiled diet with the pure core, not the per-bite resolver Standing rule 6 is about.
    private static int LogUnmatchedGrantedItems(ICoreAPI api, List<string> log, Dictionary<string, CompiledDiet> compiledTable)
    {
        IReadOnlyList<CollectibleObject> granted = FoodOverrideRegistry.GrantedCollectibles(api.Side);
        if (granted.Count == 0) return 0;

        int dietsWithUnmatched = 0;
        foreach (CompiledDiet diet in compiledTable.Values.OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            int unmatched = 0;
            foreach (CollectibleObject collectible in granted)
            {
                ulong mask = FoodTagRegistry.GetStaticMask(collectible);
                if (!DietResolver.Resolve(diet, mask, 0f).Matched) unmatched++;
            }

            if (unmatched > 0)
            {
                log.Add($"[dietsetup]   diet '{diet.Id}': {unmatched} granted item(s) not matched by any rule");
                dietsWithUnmatched++;
            }
        }
        return dietsWithUnmatched;
    }

    private static string FormatDietRow(CompiledDiet diet, int idColumnWidth)
    {
        string Cap(EnumFoodCategory c) => diet.Categories[c].Capacity.ToString("F2");
        string Gain(EnumFoodCategory c) => diet.Categories[c].NutritionGainScale.ToString("F2");

        return $"[dietsetup]   {diet.Id.PadRight(idColumnWidth)}cap F{Cap(EnumFoodCategory.Fruit)} V{Cap(EnumFoodCategory.Vegetable)} G{Cap(EnumFoodCategory.Grain)} P{Cap(EnumFoodCategory.Protein)} D{Cap(EnumFoodCategory.Dairy)}"
             + $"  gain F{Gain(EnumFoodCategory.Fruit)} V{Gain(EnumFoodCategory.Vegetable)} G{Gain(EnumFoodCategory.Grain)} P{Gain(EnumFoodCategory.Protein)} D{Gain(EnumFoodCategory.Dairy)}"
             + $"  rules {diet.Rules.Length}";
    }

    // Parsed, validated and logged here; DietIdResolver (task 2) is the only consumer that
    // resolves against it. Returns the parsed table so the caller can store it per-side and
    // sync it to clients (task 1) -- this file is ModConfig, not an asset, so a client never
    // reads it off disk itself.
    private static BindingsFile LoadAndLogBindings(ICoreAPI api, List<string> log, Dictionary<string, CompiledDiet> compiledTable, ref int warningCount)
    {
        string path = Path.Combine(GamePaths.ModConfig, ModConfigBindingsFile);
        BindingsFile bindings;

        if (!File.Exists(path))
        {
            bindings = new BindingsFile { SchemaVersion = 1, Default = "base" };
        }
        else
        {
            try
            {
                bindings = JsonConvert.DeserializeObject<BindingsFile>(File.ReadAllText(path)) ?? new BindingsFile();
            }
            catch (Exception ex)
            {
                api.Logger.Error("[dietsetup] bindings.json failed to parse, treating as empty: {0}", ex.Message);
                bindings = new BindingsFile();
            }

            if (bindings.SchemaVersion != 1)
            {
                api.Logger.Error("[dietsetup] bindings.json: schemaVersion missing or unknown (got {0})", bindings.SchemaVersion?.ToString() ?? "(missing)");
            }
        }

        foreach ((string trait, string dietId) in bindings.Bindings)
        {
            if (!compiledTable.ContainsKey(dietId))
            {
                api.Logger.Warning("[dietsetup] bindings.json: trait '{0}' maps to diet '{1}', which is not a loaded diet", trait, dietId);
                warningCount++;
            }
        }

        string defaultId = bindings.Default ?? "base";
        if (!compiledTable.ContainsKey(defaultId))
        {
            api.Logger.Warning("[dietsetup] bindings.json: default diet '{0}' is not a loaded diet", defaultId);
            warningCount++;
        }

        log.Add($"[dietsetup] bindings: {bindings.Bindings.Count} mapped, default '{defaultId}'");
        return bindings;
    }
}
