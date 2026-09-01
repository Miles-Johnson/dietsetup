using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;

namespace dietsetup.Grants;

/// <summary>Loads, validates and applies ModConfig/dietsetup/food-overrides.json (architecture 7.6):
/// admin-authored edibility grants for collectibles vanilla shipped with no nutritionProps. Called
/// from DietLoadPipeline.RunAndLog, which already runs once per side from AssetsFinalize -- that's
/// what makes grants apply on both client and server without any extra wiring here.
///
/// Applied on both sides, not server-only: GetNutritionProperties (the edibility check that decides
/// whether right-click even starts an eat animation, and what the tooltip shows) runs client-side
/// against the client's own CollectibleObject instances, which are separate objects from the
/// server's even in singleplayer (Standing rule 3, dietsetup-handover.md) -- a server-only grant
/// would leave the client seeing the item as never-food. Known gap: in dedicated multiplayer the
/// client's own ModConfig directory doesn't have the server admin's file, so a remote client's copy
/// silently applies 0 grants (missing file is normal, not an error) -- same limitation bindings.json
/// solves with a network packet (architecture 4.5); out of scope here.
///
/// LINQ and per-load allocation throughout are fine -- this runs once per side at load, not the
/// per-bite resolver Standing rule 6 is about.</summary>
public static class FoodOverrideRegistry
{
    private const string ModConfigFile = "dietsetup/food-overrides.json";

    private static readonly EnumFoodCategory[] AllCategories =
    {
        EnumFoodCategory.Fruit, EnumFoodCategory.Vegetable, EnumFoodCategory.Grain,
        EnumFoodCategory.Protein, EnumFoodCategory.Dairy
    };

    private readonly record struct CompiledOverride(string Pattern, EnumFoodCategory Category, float BaseSatiety, int Specificity);

    // Keyed by side, not a single shared static: client and server each get their own
    // DietSetupModSystem instance in one process in singleplayer (Standing rule 3), and each
    // side's AssetsFinalize call is a legitimate first-ever apply for that side's own collectible
    // instances, not a repeat of the other side's.
    private sealed class SideState
    {
        public bool Applied;
        public string? Hash;
        public List<(string Pattern, string Category, float BaseSatiety)> Rows = new();
        public readonly List<CollectibleObject> Granted = new();
        public readonly List<(CollectibleObject Collectible, EnumFoodCategory Category, float BaseSatiety)> GrantedRows = new();
    }

    private static readonly Dictionary<EnumAppSide, SideState> stateBySide = new();

    private static SideState GetState(EnumAppSide side)
    {
        if (!stateBySide.TryGetValue(side, out SideState? s))
        {
            stateBySide[side] = s = new SideState();
        }
        return s;
    }

    /// <summary>Granted collectibles for this side, for DietLoadPipeline's "granted item no rule
    /// matches" warning (architecture 6.1 rule 14). Empty until LoadApplyAndLog has run for this side.</summary>
    public static IReadOnlyList<CollectibleObject> GrantedCollectibles(EnumAppSide side) =>
        stateBySide.TryGetValue(side, out SideState? s) ? s.Granted : Array.Empty<CollectibleObject>();

    /// <summary>Resolved (collectible, category, baseSatiety) triples granted on this side, for the
    /// server to build DietFoodOverridesPacket from at send time (architecture 7.6 sync). Empty
    /// until LoadApplyAndLog has run for this side.</summary>
    public static IReadOnlyList<(CollectibleObject Collectible, EnumFoodCategory Category, float BaseSatiety)> GrantedRows(EnumAppSide side) =>
        stateBySide.TryGetValue(side, out SideState? s)
            ? s.GrantedRows
            : Array.Empty<(CollectibleObject, EnumFoodCategory, float)>();

    /// <summary>First call for a side: loads, validates and applies the grants file. Every later call
    /// for the same side (i.e. /dietreload) never re-applies -- nutritionProps is a field on a
    /// collectible object built at AssetsFinalize, not data the reload pipeline can redo -- it only
    /// compares the file's hash and reports whether a restart is needed (architecture 7.6).</summary>
    public static void LoadApplyAndLog(ICoreAPI api, List<string> log)
    {
        SideState state = GetState(api.Side);
        string path = Path.Combine(GamePaths.ModConfig, ModConfigFile);

        if (state.Applied)
        {
            CompareAndLogReload(api, log, state, path);
            return;
        }

        state.Applied = true;

        if (!File.Exists(path))
        {
            log.Add("[dietsetup] food-overrides: 0 grants loaded (no ModConfig/dietsetup/food-overrides.json)");
            return;
        }

        string raw;
        try
        {
            raw = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            api.Logger.Error("[dietsetup] food-overrides.json could not be read, 0 grants applied: {0}", ex.Message);
            return;
        }

        state.Hash = ComputeHash(raw);

        FoodOverrideDocumentFile? doc;
        try
        {
            doc = JsonConvert.DeserializeObject<FoodOverrideDocumentFile>(raw);
        }
        catch (Exception ex)
        {
            api.Logger.Error("[dietsetup] food-overrides.json failed to parse, 0 grants applied: {0}", ex.Message);
            return;
        }

        if (doc == null)
        {
            api.Logger.Error("[dietsetup] food-overrides.json produced no usable data on parse, 0 grants applied.");
            return;
        }

        if (doc.SchemaVersion != 1)
        {
            api.Logger.Error("[dietsetup] food-overrides.json: schemaVersion missing or unknown (got {0}), 0 grants applied.",
                doc.SchemaVersion?.ToString() ?? "(missing)");
            return;
        }

        state.Rows = doc.Grants.Select(g => (g.Pattern ?? "", g.Category ?? "", g.BaseSatiety ?? 0f)).ToList();

        if (!ValidateStructure(api, doc.Grants))
        {
            log.Add("[dietsetup] food-overrides: file refused, 0 grants applied (see errors above)");
            return;
        }

        List<CompiledOverride> compiled = doc.Grants
            .Select(g => new CompiledOverride(g.Pattern!, ParseCategory(g.Category!), g.BaseSatiety!.Value, Specificity(g.Pattern!)))
            .ToList();

        if (compiled.Count == 0)
        {
            log.Add("[dietsetup] food-overrides: 0 grants loaded (empty grants list)");
            return;
        }

        ValidateAndApply(api, log, compiled, state);
    }

    // Structural checks only (rule set 2a-2c): no collectibles needed yet.
    private static bool ValidateStructure(ICoreAPI api, List<FoodOverrideEntryFile> rows)
    {
        bool ok = true;
        for (int i = 0; i < rows.Count; i++)
        {
            FoodOverrideEntryFile row = rows[i];
            string label = string.IsNullOrEmpty(row.Pattern) ? $"row {i}" : row.Pattern;

            if (string.IsNullOrEmpty(row.Pattern) || string.IsNullOrEmpty(row.Category) || row.BaseSatiety == null)
            {
                api.Logger.Error("[dietsetup] food-overrides '{0}': pattern, category and baseSatiety are all required.", label);
                ok = false;
                continue;
            }

            if (!TryParseCategory(row.Category, out _))
            {
                api.Logger.Error("[dietsetup] food-overrides '{0}': category '{1}' is not one of Fruit/Vegetable/Grain/Protein/Dairy.", label, row.Category);
                ok = false;
            }

            if (row.BaseSatiety.Value < 0f)
            {
                api.Logger.Error("[dietsetup] food-overrides '{0}': baseSatiety {1} is negative.", label, row.BaseSatiety.Value);
                ok = false;
            }
        }
        return ok;
    }

    // Collectible-dependent checks (rule 2d/2e) plus the apply pass, combined because both need the
    // same per-collectible match table and there's no reason to walk api.World.Collectibles twice.
    private static void ValidateAndApply(ICoreAPI api, List<string> log, List<CompiledOverride> rows, SideState state)
    {
        var matchesByRow = new List<CollectibleObject>[rows.Count];
        for (int i = 0; i < rows.Count; i++) matchesByRow[i] = new List<CollectibleObject>();

        // One pass, not one per row: for each collectible, which rows matched it (for the tie
        // check) while simultaneously filling matchesByRow (for the "pattern only matches food
        // that's already food" check).
        var perCollectible = new List<(CollectibleObject Collectible, List<int> RowIdx)>();

        foreach (CollectibleObject collectible in api.World.Collectibles)
        {
            AssetLocation? code = collectible.Code;
            if (code == null) continue;
            string codeStr = code.ToString();

            List<int>? matchedRows = null;
            for (int i = 0; i < rows.Count; i++)
            {
                if (!WildcardUtil.Match(rows[i].Pattern, codeStr)) continue;
                matchesByRow[i].Add(collectible);
                (matchedRows ??= new List<int>()).Add(i);
            }
            if (matchedRows != null) perCollectible.Add((collectible, matchedRows));
        }

        bool ok = true;

        // Rule 2e: a pattern whose only matches already carry nutritionProps grants nothing.
        for (int i = 0; i < rows.Count; i++)
        {
            List<CollectibleObject> matches = matchesByRow[i];
            if (matches.Count > 0 && matches.TrueForAll(c => c.NutritionProps != null))
            {
                api.Logger.Error("[dietsetup] food-overrides '{0}': every matching item already has nutritionProps, this grant would do nothing.", rows[i].Pattern);
                ok = false;
            }
        }

        // Rule 2d: two patterns tied at the item's own max specificity is ambiguous, not last-wins.
        foreach ((CollectibleObject collectible, List<int> rowIdx) in perCollectible)
        {
            if (rowIdx.Count < 2) continue;
            int maxSpec = rowIdx.Max(i => rows[i].Specificity);
            List<int> tied = rowIdx.Where(i => rows[i].Specificity == maxSpec).ToList();
            if (tied.Count > 1)
            {
                api.Logger.Error("[dietsetup] food-overrides: patterns '{0}' and '{1}' tie at equal specificity for item '{2}'.",
                    rows[tied[0]].Pattern, rows[tied[1]].Pattern, collectible.Code);
                ok = false;
            }
        }

        if (!ok)
        {
            log.Add("[dietsetup] food-overrides: file refused, 0 grants applied (see errors above)");
            return;
        }

        int grantedCount = 0;
        foreach ((CollectibleObject collectible, List<int> rowIdx) in perCollectible)
        {
            int winner = rowIdx.Count == 1 ? rowIdx[0] : rowIdx.OrderByDescending(i => rows[i].Specificity).First();
            CompiledOverride row = rows[winner];

            // Non-fatal case (distinct from rule 2e above): this item is one of several matches for
            // its winning pattern, and only this one already carries nutritionProps -- skip it, but
            // the pattern still did useful work for its other matches.
            if (collectible.NutritionProps != null)
            {
                api.Logger.Warning("[dietsetup] food-overrides '{0}': item '{1}' already has nutritionProps, grant skipped.", row.Pattern, collectible.Code);
                continue;
            }

            collectible.NutritionProps = new FoodNutritionProperties
            {
                FoodCategory = row.Category,
                Satiety = row.BaseSatiety,
                Health = 0f,
                // Damage is a rule effect (7.1), not authored here -- the grant is global, damage varies per race.
                // EatenStack is the container a food returns, not the food. Null is correct for a bare grant.
            };

            state.Granted.Add(collectible);
            state.GrantedRows.Add((collectible, row.Category, row.BaseSatiety));
            grantedCount++;
        }

        log.Add($"[dietsetup] food-overrides: {grantedCount} item(s) granted nutritionProps ({rows.Count} pattern row(s))");
    }

    /// <summary>Applies a server-resolved grant packet client-side (architecture 7.6, dedicated-
    /// multiplayer gap: a remote client's own ModConfig has no food-overrides.json to read). Every
    /// row already passed the server's full match/validate pass (rules 2b-2e) before it could reach
    /// GrantedRows, so this does not re-run those checks -- only the client-local concern a
    /// server-side resolve can't see: does this collectible exist here, and does it already carry
    /// nutritionProps. Returns the collectibles newly granted by this call (the delta, not the full
    /// accumulated Granted list) for LogUnmatchedGrantedItems.</summary>
    public static List<CollectibleObject> ApplyFromPacket(ICoreClientAPI capi, DietFoodOverridesPacket packet, List<string> log)
    {
        SideState state = GetState(EnumAppSide.Client);
        var newlyApplied = new List<CollectibleObject>();
        int alreadyGranted = 0, notFound = 0, catalogMismatch = 0, badCategory = 0;

        int count = Math.Min(packet.ItemCodes.Length, Math.Min(packet.Categories.Length, packet.BaseSatiety.Length));
        for (int i = 0; i < count; i++)
        {
            string itemCode = packet.ItemCodes[i];
            var loc = new AssetLocation(itemCode);
            CollectibleObject? collectible = (CollectibleObject?)capi.World.GetItem(loc) ?? capi.World.GetBlock(loc);

            if (collectible == null)
            {
                capi.Logger.Warning("[dietsetup] food-overrides packet: item '{0}' not found client-side, grant skipped (client/server mod mismatch?).", itemCode);
                notFound++;
                continue;
            }

            // Singleplayer case: the client already granted this exact item from its own local
            // file read at AssetsFinalize, and the packet still arrives (OnPlayerNowPlaying fires
            // unconditionally) -- silent no-op, not a warning, since nothing is wrong here.
            if (state.Granted.Contains(collectible))
            {
                alreadyGranted++;
                continue;
            }

            if (collectible.NutritionProps != null)
            {
                capi.Logger.Warning("[dietsetup] food-overrides packet: item '{0}' already has nutritionProps client-side but the server had to grant it -- client/server catalog mismatch, grant skipped.", itemCode);
                catalogMismatch++;
                continue;
            }

            // The category crosses the wire as a string; a parse failure must not fall back to a
            // default category (7.6 has no defaults -- that's the "balanced" 0.4 defect this
            // section exists to prevent) and must not apply the row at all.
            if (!TryParseCategory(packet.Categories[i], out EnumFoodCategory category))
            {
                capi.Logger.Warning("[dietsetup] food-overrides packet: item '{0}' has unrecognized category '{1}', grant skipped.", itemCode, packet.Categories[i]);
                badCategory++;
                continue;
            }

            collectible.NutritionProps = new FoodNutritionProperties
            {
                FoodCategory = category,
                Satiety = packet.BaseSatiety[i],
                Health = 0f,
                // Damage is a rule effect (7.1), not authored here. EatenStack null matches
                // ValidateAndApply's grant construction above -- see that branch's comment.
            };

            state.Granted.Add(collectible);
            state.GrantedRows.Add((collectible, category, packet.BaseSatiety[i]));
            newlyApplied.Add(collectible);
        }

        log.Add($"[dietsetup] food-overrides packet: {newlyApplied.Count} newly applied, {alreadyGranted} already granted, " +
                $"{notFound} not found, {catalogMismatch} catalog mismatch, {badCategory} bad category ({count} row(s) received)");
        return newlyApplied;
    }

    private static bool TryParseCategory(string raw, out EnumFoodCategory category) =>
        Enum.TryParse(raw, true, out category) && Array.IndexOf(AllCategories, category) >= 0;

    private static EnumFoodCategory ParseCategory(string raw)
    {
        TryParseCategory(raw, out EnumFoodCategory category);
        return category;
    }

    // No wildcard = a literal, always wins any tie against a wildcarded pattern. Among wildcarded
    // patterns, a longer literal portion is more specific (e.g. "game:meat-pork-*" over "game:meat-*").
    private static int Specificity(string pattern) => pattern.Contains('*') ? pattern.Length : int.MaxValue;

    private static string ComputeHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    // /dietreload's view of this file: never re-applies (see class doc), just says whether the file
    // moved since the apply that's already live, and by how much.
    private static void CompareAndLogReload(ICoreAPI api, List<string> log, SideState state, string path)
    {
        if (!File.Exists(path))
        {
            if (state.Hash != null)
            {
                log.Add($"[dietsetup] food-overrides: file removed since last apply ({state.Rows.Count} row(s) changed) -- restart required for this to take effect.");
            }
            return;
        }

        string raw;
        try
        {
            raw = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[dietsetup] food-overrides.json could not be re-read for reload comparison: {0}", ex.Message);
            return;
        }

        string newHash = ComputeHash(raw);
        if (newHash == state.Hash) return;

        List<(string Pattern, string Category, float BaseSatiety)> newRows;
        try
        {
            FoodOverrideDocumentFile? doc = JsonConvert.DeserializeObject<FoodOverrideDocumentFile>(raw);
            newRows = (doc?.Grants ?? new()).Select(g => (g.Pattern ?? "", g.Category ?? "", g.BaseSatiety ?? 0f)).ToList();
        }
        catch
        {
            newRows = new();
        }

        var oldSet = new HashSet<(string, string, float)>(state.Rows);
        var newSet = new HashSet<(string, string, float)>(newRows);
        int changed = oldSet.Count(r => !newSet.Contains(r)) + newSet.Count(r => !oldSet.Contains(r));

        log.Add($"[dietsetup] food-overrides: file changed since last apply ({changed} row(s) changed) -- restart required for this to take effect.");
    }
}
