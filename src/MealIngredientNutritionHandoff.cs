using System.Collections.Generic;

namespace dietsetup;

/// <summary>Per-entity, in-order hand-off from DietMealNutritionPatch (resolves one ingredient's
/// nutrition-gain multiplier at a time, via GetIngredientStackNutritionProperties) to
/// DietMealContentNutritionPatch (loops ingredients in the same order, and is the one positioned
/// to enqueue each value into DietProfileRegistry's nutrition-multiplier queue -- see task 3).
/// Server-only in practice: only the server-side eat path ever drains it, since
/// EntityBehaviorHunger is server-side only (landmine E).</summary>
internal static class MealIngredientNutritionHandoff
{
    private static readonly Dictionary<long, List<float>> pending = new();

    public static void Add(long entityId, float nutritionMult)
    {
        if (!pending.TryGetValue(entityId, out List<float>? list))
        {
            pending[entityId] = list = new List<float>();
        }
        list.Add(nutritionMult);
    }

    public static List<float> TakeAll(long entityId) =>
        pending.Remove(entityId, out List<float>? found) ? found : new List<float>();
}
