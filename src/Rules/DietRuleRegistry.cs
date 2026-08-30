using System.Collections.Generic;

namespace dietsetup.Rules;

/// <summary>Holds the compiled diet table for this process side. Singleplayer's client and server
/// DietSetupModSystem instances share the same process but never the same table -- each side runs
/// its own DietLoadPipeline pass and calls ReplaceAll with its own result (landmine C).</summary>
public static class DietRuleRegistry
{
    private static Dictionary<string, CompiledDiet> compiled = new();

    public static CompiledDiet? GetDiet(string id) => compiled.TryGetValue(id, out CompiledDiet? d) ? d : null;

    public static IEnumerable<CompiledDiet> AllDiets => compiled.Values;

    internal static void ReplaceAll(Dictionary<string, CompiledDiet> table) => compiled = table;
}
