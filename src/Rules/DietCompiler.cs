using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using dietsetup.Tags;
using Vintagestory.API.Common;

namespace dietsetup.Rules;

/// <summary>One fatal or warning finding from compiling a diet, tagged with its architecture 6.1
/// rule number so the pipeline's log/refusal messages can cite it.</summary>
public readonly record struct DietValidationMessage(int Rule, string Text);

/// <summary>Compiles one already-extends-resolved diet document into a CompiledDiet (architecture
/// 6 steps 5-7: compile, derive, validate). Stateless -- the pipeline owns raw loading and the
/// final table.</summary>
public static class DietCompiler
{
    private static readonly EnumFoodCategory[] AllCategories =
    {
        EnumFoodCategory.Fruit, EnumFoodCategory.Vegetable, EnumFoodCategory.Grain,
        EnumFoodCategory.Protein, EnumFoodCategory.Dairy
    };

    public static CompiledDiet? Compile(string id, DietDocumentFile doc, string domain, float capacityFloor, List<DietValidationMessage> fatal, List<DietValidationMessage> warnings)
    {
        if (doc.SchemaVersion != 1)
        {
            fatal.Add(new DietValidationMessage(1, $"schemaVersion missing or unknown (got {(doc.SchemaVersion?.ToString() ?? "(missing)")})"));
        }

        Dictionary<EnumFoodCategory, CompiledCategory> categories = CompileCategories(id, doc.Categories, capacityFloor, fatal, warnings);

        if (categories.Values.All(c => c.Capacity == 0f))
        {
            fatal.Add(new DietValidationMessage(8, "all five capacities are 0, this diet can never gain health"));
        }

        float fallbackSatiety = doc.Fallback?.SatietyMult ?? 1f;
        float fallbackNutrition = doc.Fallback?.NutritionMult ?? 1f;

        var rules = new List<CompiledRule>(doc.Rules.Length);
        for (int i = 0; i < doc.Rules.Length; i++)
        {
            CompiledRule? rule = CompileRule(id, doc.Rules[i], i, fatal, warnings);
            if (rule != null) rules.Add(rule.Value);
        }

        CompiledRule[] sorted = SortByWinOrder(rules);
        CheckShadowedRules(id, sorted, warnings);
        CheckUncoveredCategories(id, categories, sorted, fallbackNutrition, warnings);

        if (fatal.Count > 0) return null;

        return new CompiledDiet
        {
            Id = id,
            SourceDomain = domain,
            Categories = categories,
            FallbackSatietyMult = fallbackSatiety,
            FallbackNutritionMult = fallbackNutrition,
            Rules = sorted,
        };
    }

    private static Dictionary<EnumFoodCategory, CompiledCategory> CompileCategories(string id, Dictionary<string, DietCategoryFile> categoryFiles, float capacityFloor, List<DietValidationMessage> fatal, List<DietValidationMessage> warnings)
    {
        var result = new Dictionary<EnumFoodCategory, CompiledCategory>();

        foreach ((string name, DietCategoryFile catFile) in categoryFiles)
        {
            if (!Enum.TryParse(name, true, out EnumFoodCategory cat) || !AllCategories.Contains(cat))
            {
                // Not one of the 15 numbered rules -- an unrecognized category name is a malformed
                // document, not a scope collision, but must still refuse rather than silently drop it.
                fatal.Add(new DietValidationMessage(0, $"categories block names unknown category '{name}'"));
                continue;
            }

            if (catFile.SatietyMult.HasValue || catFile.NutritionMult.HasValue)
            {
                fatal.Add(new DietValidationMessage(6, $"category '{cat}' sets a rule-scoped multiplier (satietyMult/nutritionMult belong on rules, not categories)"));
            }

            result[cat] = DeriveCategory(id, cat, catFile.Capacity ?? 1f, capacityFloor, warnings);
        }

        foreach (EnumFoodCategory cat in AllCategories)
        {
            if (!result.ContainsKey(cat))
            {
                result[cat] = DeriveCategory(id, cat, 1f, capacityFloor, warnings);
            }
        }

        return result;
    }

    // Architecture 5.5/5.6: capacity 0 is a special case (gain scale 0, not a division), and a
    // nonzero capacity below the floor is clamped up, logged once per diet+category (also rule 11).
    private static CompiledCategory DeriveCategory(string id, EnumFoodCategory cat, float rawCapacity, float capacityFloor, List<DietValidationMessage> warnings)
    {
        float capacity = rawCapacity;
        if (rawCapacity > 0f && rawCapacity < capacityFloor)
        {
            capacity = capacityFloor;
            warnings.Add(new DietValidationMessage(11, $"category '{cat}' capacity {rawCapacity:F3} clamped to floor {capacityFloor:F3}"));
        }

        float gainScale = capacity > 0f ? 1f / capacity : 0f;
        return new CompiledCategory(capacity, gainScale, capacity);
    }

    private static CompiledRule? CompileRule(string id, DietRuleFileEntry rf, int declarationIndex, List<DietValidationMessage> fatal, List<DietValidationMessage> warnings)
    {
        string[] requires = rf.Requires ?? Array.Empty<string>();
        string[] excludes = rf.Excludes ?? Array.Empty<string>();
        string label = requires.Length == 0 ? "(no requires)" : string.Join("+", requires);

        if (rf.Capacity.HasValue)
        {
            fatal.Add(new DietValidationMessage(5, $"rule '{label}': sets 'capacity', a category-scoped field"));
        }

        bool requiresOk = TryCompileMask(label, requires, fatal, out ulong requiresMask);
        bool excludesOk = TryCompileMask(label, excludes, fatal, out ulong excludesMask);
        if (!requiresOk || !excludesOk) return null;

        DietVerdict verdict = ParseVerdict(label, rf.Verdict, fatal);

        bool satietyIsCurve = rf.SatietyCurve is { Length: > 0 };
        bool nutritionIsCurve = rf.NutritionCurve is { Length: > 0 };

        // Rule 16: a rule that authors both a flat multiplier and a curve for the same field
        // must refuse to load, not silently prefer the curve -- the pre-ee2f142 CompiledValue
        // schema documented "never both" without enforcing it.
        if (satietyIsCurve && rf.SatietyMult.HasValue)
        {
            fatal.Add(new DietValidationMessage(16, $"rule '{label}': sets both 'satietyMult' and 'satietyCurve' -- author one, not both"));
        }
        if (nutritionIsCurve && rf.NutritionMult.HasValue)
        {
            fatal.Add(new DietValidationMessage(16, $"rule '{label}': sets both 'nutritionMult' and 'nutritionCurve' -- author one, not both"));
        }

        float satietyMult = rf.SatietyMult ?? 1f;
        float nutritionMult = rf.NutritionMult ?? 1f;

        // Architecture 7.1: satietyMult/nutritionMult/verdict are one authoring path with two
        // spellings (the top-level field here, or an effects-list entry below) -- both must reach
        // the same CompiledRule field DietResolver.Apply already evaluates exactly once, not a
        // second one. Seeding rule 9's collision set with whichever top-level fields were
        // explicitly authored (flat or curve) means writing both spellings for the same field is
        // caught the same way as writing it twice within the effects list.
        var writtenFields = new HashSet<string>();
        if (rf.SatietyMult.HasValue || satietyIsCurve) writtenFields.Add("Satiety");
        if (rf.NutritionMult.HasValue || nutritionIsCurve) writtenFields.Add("Nutrition");
        if (rf.Verdict != null) writtenFields.Add("Verdict");

        CompiledEffect[] effects = CompileEffects(label, rf.Effects, writtenFields, fatal, warnings,
            ref satietyMult, ref nutritionMult, ref verdict);

        CompiledValue satiety = satietyIsCurve ? CompiledValue.FromCurve(SortAnchors(rf.SatietyCurve!)) : CompiledValue.Flat(satietyMult);
        CompiledValue nutrition = nutritionIsCurve ? CompiledValue.FromCurve(SortAnchors(rf.NutritionCurve!)) : CompiledValue.Flat(nutritionMult);

        return new CompiledRule(
            requiresMask, excludesMask, BitOperations.PopCount(requiresMask), rf.Priority ?? 0,
            verdict, satiety, nutrition, effects, label);
    }

    // FromCurve requires ascending order; authors write anchors in whatever order reads best.
    private static CurveAnchor[] SortAnchors(CurveAnchorFile[] anchorsFile)
    {
        var anchors = new CurveAnchor[anchorsFile.Length];
        for (int i = 0; i < anchorsFile.Length; i++)
        {
            anchors[i] = new CurveAnchor(anchorsFile[i].Spoil, anchorsFile[i].Value);
        }
        Array.Sort(anchors, (a, b) => a.Spoil.CompareTo(b.Spoil));
        return anchors;
    }

    private static bool TryCompileMask(string ruleLabel, string[] tags, List<DietValidationMessage> fatal, out ulong mask)
    {
        mask = 0;
        bool ok = true;
        foreach (string tag in tags)
        {
            if (!FoodTagRegistry.TryGetBit(tag, out int bit))
            {
                fatal.Add(new DietValidationMessage(4, $"rule '{ruleLabel}': references unknown tag '{tag}'"));
                ok = false;
                continue;
            }
            mask |= 1UL << bit;
        }
        return ok;
    }

    private static DietVerdict ParseVerdict(string ruleLabel, string? verdict, List<DietValidationMessage> fatal)
    {
        string v = verdict ?? "edible";
        if (Enum.TryParse(v, true, out DietVerdict parsed)) return parsed;

        // Not one of the 15 numbered rules -- same reasoning as the unknown-category check above.
        fatal.Add(new DietValidationMessage(0, $"rule '{ruleLabel}': unknown verdict '{v}'"));
        return DietVerdict.Edible;
    }

    // writtenFields arrives pre-seeded with whichever top-level rule fields (satietyMult/
    // nutritionMult/verdict) were explicitly authored -- an effects-list entry for the same field
    // is the same collision as two effects-list entries writing it (rule 9). satietyMult/
    // nutritionMult/verdict/ref params are the one CompiledRule field each spelling folds into
    // (architecture 7.1: "one code path, two authoring forms") -- never a second multiply site.
    private static CompiledEffect[] CompileEffects(string ruleLabel, DietEffectFile[]? effectsFile, HashSet<string> writtenFields, List<DietValidationMessage> fatal, List<DietValidationMessage> warnings, ref float satietyMult, ref float nutritionMult, ref DietVerdict verdict)
    {
        if (effectsFile == null || effectsFile.Length == 0) return Array.Empty<CompiledEffect>();

        var list = new List<CompiledEffect>(effectsFile.Length);

        foreach (DietEffectFile ef in effectsFile)
        {
            if (!Enum.TryParse(ef.Type, true, out DietEffectType type))
            {
                fatal.Add(new DietValidationMessage(7, $"rule '{ruleLabel}': unknown effect type '{ef.Type}'"));
                continue;
            }

            string? field = type switch
            {
                DietEffectType.SatietyMult => "Satiety",
                DietEffectType.NutritionMult => "Nutrition",
                DietEffectType.Verdict => "Verdict",
                _ => null
            };
            if (field != null && !writtenFields.Add(field))
            {
                fatal.Add(new DietValidationMessage(9, $"rule '{ruleLabel}': two effects write the field '{field}'"));
                continue;
            }

            DietVerdict? effectVerdict = null;
            if (type == DietEffectType.Verdict)
            {
                if (!Enum.TryParse(ef.Verdict ?? "", true, out DietVerdict parsedVerdict))
                {
                    // Not one of the 15 numbered rules -- the effect type itself parsed fine (rule 7
                    // is about the type string), this is its value being malformed.
                    fatal.Add(new DietValidationMessage(0, $"rule '{ruleLabel}': verdict effect has unknown verdict '{ef.Verdict}'"));
                    continue;
                }
                effectVerdict = parsedVerdict;
                verdict = parsedVerdict;
            }
            else if (type == DietEffectType.SatietyMult)
            {
                satietyMult = ef.Amount ?? 1f;
            }
            else if (type == DietEffectType.NutritionMult)
            {
                nutritionMult = ef.Amount ?? 1f;
            }

            IDietConsequenceEffect? customEffect = null;
            if (type == DietEffectType.Custom)
            {
                string key = ef.Key ?? "";
                if (!DietEffects.TryGet(key, out customEffect))
                {
                    warnings.Add(new DietValidationMessage(13, $"rule '{ruleLabel}': effect type 'custom' key '{key}' has no registered handler yet"));
                }
            }

            DietDamageMode? damageMode = null;
            float durationSec = 0f;
            int ticks = 1;
            if (type == DietEffectType.Damage)
            {
                if (!Enum.TryParse(ef.Mode, true, out DietDamageMode parsedMode))
                {
                    // Not one of the 15 numbered rules -- same reasoning as the unknown-verdict check
                    // above: the effect type parsed fine (rule 7), its mode value didn't.
                    fatal.Add(new DietValidationMessage(0, $"rule '{ruleLabel}': damage effect has missing or unknown mode '{ef.Mode}' (must be 'instant' or 'overTime')"));
                    continue;
                }
                damageMode = parsedMode;
                if (parsedMode == DietDamageMode.OverTime)
                {
                    durationSec = ef.DurationSec ?? 3f;
                    ticks = Math.Max(1, ef.Ticks ?? 3);
                }
            }

            // satietyMult/nutritionMult default to 1 (no-op), matching the top-level field
            // convention -- every other type's stored Amount defaults to 0 (no-op for damage).
            float amount = type is DietEffectType.SatietyMult or DietEffectType.NutritionMult ? ef.Amount ?? 1f : ef.Amount ?? 0f;
            list.Add(new CompiledEffect(type, amount, ef.Mode, effectVerdict, ef.Key, customEffect, damageMode, durationSec, ticks));
        }
        return list.ToArray();
    }

    // Architecture 5.2 step 2: highest priority, then most specific mask, then first declared.
    // Declaration index is an explicit tiebreak rather than relying on sort stability.
    private static CompiledRule[] SortByWinOrder(List<CompiledRule> rules)
    {
        var indexed = rules.Select((r, idx) => (Rule: r, Index: idx)).ToList();
        indexed.Sort((x, y) =>
        {
            int byPriority = y.Rule.Priority.CompareTo(x.Rule.Priority);
            if (byPriority != 0) return byPriority;
            int bySpecificity = y.Rule.Specificity.CompareTo(x.Rule.Specificity);
            if (bySpecificity != 0) return bySpecificity;
            return x.Index.CompareTo(y.Index);
        });
        return indexed.Select(t => t.Rule).ToArray();
    }

    // Rule 12 (warning): rule b is unreachable if some earlier rule a in win order matches
    // everything b would match (a's requires/excludes are a subset of b's).
    private static void CheckShadowedRules(string id, CompiledRule[] sorted, List<DietValidationMessage> warnings)
    {
        for (int i = 0; i < sorted.Length; i++)
        {
            for (int j = i + 1; j < sorted.Length; j++)
            {
                CompiledRule a = sorted[i];
                CompiledRule b = sorted[j];
                bool requiresSubset = (a.RequiresMask & ~b.RequiresMask) == 0;
                bool excludesSubset = (a.ExcludesMask & ~b.ExcludesMask) == 0;
                if (requiresSubset && excludesSubset)
                {
                    warnings.Add(new DietValidationMessage(12, $"rule '{b.DebugLabel}' is unreachable, fully shadowed by higher-priority rule '{a.DebugLabel}'"));
                }
            }
        }
    }

    // Rule 10 (warning): a category with capacity > 0 that neither the fallback nor any rule can
    // put nutrition into. A rule with no source-axis tag in requires matches food of any category.
    private static void CheckUncoveredCategories(string id, Dictionary<EnumFoodCategory, CompiledCategory> categories, CompiledRule[] rules, float fallbackNutrition, List<DietValidationMessage> warnings)
    {
        if (fallbackNutrition > 0f) return;

        foreach ((EnumFoodCategory cat, CompiledCategory compiled) in categories)
        {
            if (compiled.Capacity <= 0f) continue;
            if (rules.Any(r => r.NutritionMult.CanBePositive && RuleCoversCategory(r, cat))) continue;

            warnings.Add(new DietValidationMessage(10, $"category '{cat}' has capacity {compiled.Capacity:F2} but no rule (and no fallback) can produce nutrition for it"));
        }
    }

    private static bool RuleCoversCategory(CompiledRule rule, EnumFoodCategory cat)
    {
        bool referencesAnySourceTag = false;
        foreach (string tag in FoodTagRegistry.TagNames(rule.RequiresMask))
        {
            EnumFoodCategory? bar = FoodTagRegistry.NutrientBarFor(tag);
            if (bar == null) continue;
            referencesAnySourceTag = true;
            if (bar == cat) return true;
        }
        return !referencesAnySourceTag;
    }
}
