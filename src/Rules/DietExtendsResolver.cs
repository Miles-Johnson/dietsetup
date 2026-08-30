using System;
using System.Collections.Generic;
using System.Linq;

namespace dietsetup.Rules;

/// <summary>Resolves `extends` (architecture 4.4) before compilation: child categories replace
/// parent entries by name, fallback replaces whole, rules append after the parent's (a child can
/// never delete a parent rule). Depth capped at 8; a cycle names the whole chain.</summary>
public static class DietExtendsResolver
{
    private const int MaxDepth = 8;

    public static DietDocumentFile? Resolve(string id, Dictionary<string, DietDocumentFile> raw, out string? error)
    {
        return ResolveRecursive(id, raw, new List<string>(), out error);
    }

    private static DietDocumentFile? ResolveRecursive(string id, Dictionary<string, DietDocumentFile> raw, List<string> chain, out string? error)
    {
        error = null;

        if (chain.Contains(id))
        {
            error = $"extends cycle involving '{chain[0]}' and '{id}' ({string.Join(" -> ", chain)} -> {id})";
            return null;
        }

        if (chain.Count >= MaxDepth)
        {
            error = $"extends chain for '{id}' exceeds the depth cap of {MaxDepth} ({string.Join(" -> ", chain)} -> {id})";
            return null;
        }

        if (!raw.TryGetValue(id, out DietDocumentFile? doc))
        {
            error = $"extends references unknown diet id '{id}'";
            return null;
        }

        chain.Add(id);

        if (string.IsNullOrEmpty(doc.Extends)) return doc;

        DietDocumentFile? parent = ResolveRecursive(doc.Extends, raw, chain, out error);
        if (parent == null) return null;

        return Merge(parent, doc);
    }

    private static DietDocumentFile Merge(DietDocumentFile parent, DietDocumentFile child)
    {
        var categories = new Dictionary<string, DietCategoryFile>(parent.Categories, StringComparer.OrdinalIgnoreCase);
        foreach ((string name, DietCategoryFile value) in child.Categories)
        {
            categories[name] = value;
        }

        var rules = new List<DietRuleFileEntry>(parent.Rules);
        rules.AddRange(child.Rules);

        return new DietDocumentFile
        {
            SchemaVersion = child.SchemaVersion,
            Id = child.Id,
            Extends = child.Extends,
            Categories = categories,
            Fallback = child.Fallback ?? parent.Fallback,
            Rules = rules.ToArray(),
        };
    }
}
