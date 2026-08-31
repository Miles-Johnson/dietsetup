using System.Collections.Generic;
using dietsetup.Rules;

namespace dietsetup;

/// <summary>Per-entity hand-off from DietMealContentNutritionPatch's per-ingredient loop (which
/// resolves once per ingredient, reusing DietSpoilageResolution's cache -- no second resolve) to
/// DietMealEffectFirePatch's postfix on BlockMeal.tryFinishEatMeal. Replace, not append/enqueue:
/// GetContentNutritionProperties fires twice per real eat (landmine C), and each call's loop
/// produces its own complete ingredient list, so replacing on every call means the entry
/// tryFinishEatMeal's postfix reads back is always that same invocation's own final loop -- never a
/// stale one, never a doubled one.</summary>
internal static class PendingMealEffects
{
    private static readonly Dictionary<long, List<DietResolveResult>> pending = new();

    public static void Replace(long entityId, List<DietResolveResult> results) => pending[entityId] = results;

    public static List<DietResolveResult> TakeAll(long entityId) =>
        pending.Remove(entityId, out List<DietResolveResult>? found) ? found : new List<DietResolveResult>();

    public static void Remove(long entityId) => pending.Remove(entityId);
}
