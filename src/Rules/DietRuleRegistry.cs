using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using dietsetup.Tags;
using Vintagestory.API.Common;

namespace dietsetup.Rules;

/// <summary>Load + compile for the diet rules engine (spec sections 3-5, migration step 6).
/// LoadFrom merges raw JSON per diet id, duplicate id across domains is a hard error at load
/// time. CompileAll (AssetsFinalize, after FoodTagRegistry.ResolveStaticTags so tag bits are
/// final) turns each raw file into a CompiledDiet: requires/excludes to masks, curves to sorted
/// anchor arrays, rules sorted once for the resolver's specificity-then-priority scan.</summary>
public static class DietRuleRegistry
{
    private static readonly Dictionary<string, (DietDefinitionFile File, string Domain)> raw = new();
    private static readonly Dictionary<string, CompiledDiet> compiled = new();

    public static CompiledDiet? GetDiet(string id) => compiled.TryGetValue(id, out CompiledDiet? d) ? d : null;

    public static IEnumerable<CompiledDiet> AllDiets => compiled.Values;

    public static IEnumerable<CompiledDiet> PickerDiets => compiled.Values.Where(d => !d.HiddenFromPicker);

    public static void LoadFrom(DietDefinitionFile file, string domain, ILogger logger)
    {
        if (string.IsNullOrEmpty(file.Id))
        {
            logger.Error("[dietsetup] A diet definition in domain '{0}' has no 'id', skipped.", domain);
            return;
        }

        if (raw.TryGetValue(file.Id, out (DietDefinitionFile File, string Domain) existing))
        {
            throw new InvalidOperationException(
                $"[dietsetup] Duplicate diet id '{file.Id}' -- registered by both domain '{existing.Domain}' and domain '{domain}'.");
        }

        raw[file.Id] = (file, domain);
    }

    public static void CompileAll(ICoreAPI api)
    {
        compiled.Clear();
        foreach ((string id, (DietDefinitionFile file, string domain)) in raw)
        {
            CompiledDiet? diet = CompileOne(api, file, domain);
            if (diet != null) compiled[id] = diet;
        }
    }

    private static CompiledDiet? CompileOne(ICoreAPI api, DietDefinitionFile file, string domain)
    {
        Dictionary<string, CurveAnchor[]> curves = CompileCurves(file.Curves);
        bool fatal = false;
        bool degraded = false;

        CompiledValue defaultSatiety = CompileValue(api, file.Id, "default", "satiety", file.Default.Satiety, curves, ref fatal);
        CompiledValue defaultNutrition = CompileValue(api, file.Id, "default", "nutrition", file.Default.Nutrition, curves, ref fatal);
        DietVerdict defaultVerdict = ParseVerdict(api, file.Id, "default", file.Default.Verdict, ref fatal);
        CompiledEffect[] defaultEffects = CompileEffects(api, file.Id, "default", file.Default.Effects, ref fatal, ref degraded);

        var rules = new List<CompiledRule>(file.Rules.Length);
        foreach (DietRuleFileEntry ruleFile in file.Rules)
        {
            string label = ruleFile.Requires.Length == 0 ? "(no requires)" : string.Join("+", ruleFile.Requires);

            bool requiresOk = TryCompileMask(api, file.Id, label, ruleFile.Requires, out ulong requiresMask);
            bool excludesOk = TryCompileMask(api, file.Id, label, ruleFile.Excludes, out ulong excludesMask);
            if (!requiresOk || !excludesOk)
            {
                fatal = true;
                continue;
            }

            DietVerdict verdict = ParseVerdict(api, file.Id, label, ruleFile.Verdict, ref fatal);
            CompiledValue satiety = CompileValue(api, file.Id, label, "satiety", ruleFile.Satiety, curves, ref fatal);
            CompiledValue nutrition = CompileValue(api, file.Id, label, "nutrition", ruleFile.Nutrition, curves, ref fatal);
            CompiledEffect[] effects = CompileEffects(api, file.Id, label, ruleFile.Effects, ref fatal, ref degraded);

            rules.Add(new CompiledRule(requiresMask, excludesMask, BitOperations.PopCount(requiresMask), ruleFile.Priority, verdict, satiety, nutrition, effects, label));
        }

        if (fatal)
        {
            api.Logger.Error("[dietsetup] Diet '{0}' (domain '{1}') failed to compile due to the error(s) above and will not be loaded.", file.Id, domain);
            return null;
        }

        rules.Sort((a, b) => b.Specificity != a.Specificity ? b.Specificity.CompareTo(a.Specificity) : b.Priority.CompareTo(a.Priority));

        return new CompiledDiet
        {
            Id = file.Id,
            SourceDomain = domain,
            HiddenFromPicker = file.HiddenFromPicker || degraded,
            Degraded = degraded,
            DefaultVerdict = defaultVerdict,
            DefaultSatiety = defaultSatiety,
            DefaultNutrition = defaultNutrition,
            DefaultEffects = defaultEffects,
            Rules = rules.ToArray(),
        };
    }

    private static Dictionary<string, CurveAnchor[]> CompileCurves(Dictionary<string, CurveAnchorFile[]> curvesFile)
    {
        var result = new Dictionary<string, CurveAnchor[]>(curvesFile.Count);
        foreach ((string name, CurveAnchorFile[] anchorsFile) in curvesFile)
        {
            var anchors = new CurveAnchor[anchorsFile.Length];
            for (int i = 0; i < anchorsFile.Length; i++)
            {
                anchors[i] = new CurveAnchor(anchorsFile[i].Spoil, anchorsFile[i].Value);
            }
            Array.Sort(anchors, (a, b) => a.Spoil.CompareTo(b.Spoil));
            result[name] = anchors;
        }
        return result;
    }

    private static bool TryCompileMask(ICoreAPI api, string dietId, string ruleLabel, string[] tags, out ulong mask)
    {
        mask = 0;
        bool ok = true;
        foreach (string tag in tags)
        {
            if (!FoodTagRegistry.TryGetBit(tag, out int bit))
            {
                api.Logger.Error("[dietsetup] Diet '{0}' rule '{1}': references unknown tag '{2}'.", dietId, ruleLabel, tag);
                ok = false;
                continue;
            }
            mask |= 1UL << bit;
        }
        return ok;
    }

    private static DietVerdict ParseVerdict(ICoreAPI api, string dietId, string ruleLabel, string verdict, ref bool fatal)
    {
        if (Enum.TryParse(verdict, true, out DietVerdict parsed)) return parsed;

        api.Logger.Error("[dietsetup] Diet '{0}' rule '{1}': unknown verdict '{2}'.", dietId, ruleLabel, verdict);
        fatal = true;
        return DietVerdict.Edible;
    }

    private static CompiledValue CompileValue(ICoreAPI api, string dietId, string ruleLabel, string fieldName, DietValueFile? valueFile, Dictionary<string, CurveAnchor[]> curves, ref bool fatal)
    {
        DietValueFile v = valueFile ?? DietValueFile.FlatOne;
        if (v.Curve != null)
        {
            if (curves.TryGetValue(v.Curve, out CurveAnchor[]? anchors)) return CompiledValue.FromCurve(anchors);

            api.Logger.Error("[dietsetup] Diet '{0}' rule '{1}': {2} references unknown curve '{3}'.", dietId, ruleLabel, fieldName, v.Curve);
            fatal = true;
            return CompiledValue.Flat(0f);
        }
        return CompiledValue.Flat(v.Flat ?? 1f);
    }

    private static CompiledEffect[] CompileEffects(ICoreAPI api, string dietId, string ruleLabel, DietEffectFile[]? effectsFile, ref bool fatal, ref bool degraded)
    {
        if (effectsFile == null || effectsFile.Length == 0) return Array.Empty<CompiledEffect>();

        var list = new List<CompiledEffect>(effectsFile.Length);
        foreach (DietEffectFile ef in effectsFile)
        {
            if (!Enum.TryParse(ef.Type, true, out DietEffectType type))
            {
                api.Logger.Error("[dietsetup] Diet '{0}' rule '{1}': unknown effect type '{2}'.", dietId, ruleLabel, ef.Type);
                fatal = true;
                continue;
            }

            if (type is DietEffectType.StatModifier or DietEffectType.Hydration)
            {
                // Compiled and validated (DietResolver.ApplyEffect is a deliberate no-op for both
                // in v1), but silent otherwise -- an author has no way to know these don't apply yet.
                api.Logger.Warning("[dietsetup] Diet '{0}' rule '{1}': effect type '{2}' is recognized but not applied in v1.", dietId, ruleLabel, type);
            }

            IDietCustomEffect? customEffect = null;
            if (type == DietEffectType.Custom)
            {
                string key = ef.Key ?? "";
                if (!DietEffects.TryGet(key, out customEffect))
                {
                    // Soft failure (spec section 11): keep the diet loaded, fall back to default
                    // behaviour at evaluation, hide from picker -- not a compile-fatal error, since
                    // the missing mod may come back on a later load.
                    api.Logger.Error("[dietsetup] Diet '{0}' rule '{1}': custom effect key '{2}' is not registered by any installed mod -- this diet will use default behaviour and is hidden from the picker until it is.", dietId, ruleLabel, key);
                    degraded = true;
                    continue;
                }
            }

            list.Add(new CompiledEffect(type, ef.Amount, ef.DurationSec, ef.Stat, ef.Key, customEffect));
        }
        return list.ToArray();
    }
}
