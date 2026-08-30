using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using dietsetup;
using dietsetup.Rules;
using dietsetup.Tags;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace dietsetup.Diet;

/// <summary>
/// Central registry + resolver for diet profiles. Tag matching itself lives in
/// dietsetup.Tags.FoodTagRegistry (the three-axis registry the rules engine also uses) -- this
/// class no longer keeps a separate tag index (tag-engine migration step 9).
/// </summary>
public static class DietProfileRegistry
{
    // Per-entity FIFO of combined nutrition-gain multipliers (tag-fold * rule-matched Nutrition),
    // one entry per real eaten ingredient, consumed in order by DietSaturationScalePatch. Server
    // side only (see the enqueue sites' IServerWorldAccessor guard) -- a client-side tooltip
    // render must never write here, since singleplayer runs client+server DietSetupModSystem
    // instances in the same process sharing this same static dictionary.
    private static readonly Dictionary<long, Queue<float>> PendingNutritionMultipliers = new();

    public static void ClearNutritionMultiplierQueue(long entityId)
    {
        if (PendingNutritionMultipliers.TryGetValue(entityId, out Queue<float>? queue))
        {
            queue.Clear();
        }
    }

    /// <summary>Full removal, not just Clear -- called on player disconnect so a departed
    /// player's entry doesn't sit in this dictionary forever.</summary>
    public static void RemoveNutritionMultiplierQueue(long entityId) => PendingNutritionMultipliers.Remove(entityId);

    internal static void EnqueueNutritionMultiplier(long entityId, float value)
    {
        if (!PendingNutritionMultipliers.TryGetValue(entityId, out Queue<float>? queue))
        {
            PendingNutritionMultipliers[entityId] = queue = new Queue<float>();
        }
        if (queue.Count >= DietSetupModSystem.Config.NutritionMultiplierQueueCap)
        {
            queue.Dequeue(); // defensive only -- see DietSetupConfig.NutritionMultiplierQueueCap
        }
        queue.Enqueue(value);
    }

    public static bool TryDequeueNutritionMultiplier(long entityId, out float value)
    {
        if (PendingNutritionMultipliers.TryGetValue(entityId, out Queue<float>? queue) && queue.Count > 0)
        {
            value = queue.Dequeue();
            return true;
        }
        value = 1f;
        return false;
    }

    // Hand-off from DietMealNutritionPatch (resolves one ingredient at a time) to
    // DietMealContentNutritionPatch.
    // Keyed by entity, one entry per ingredient stack, in call order.
    private static readonly Dictionary<long, List<float>> MealIngredientContext = new();

    public static void ClearMealIngredientContext(long entityId) => MealIngredientContext.Remove(entityId);

    internal static void AddMealIngredientContext(long entityId, float notionalSatiety)
    {
        if (!MealIngredientContext.TryGetValue(entityId, out var list))
        {
            MealIngredientContext[entityId] = list = new();
        }
        list.Add(notionalSatiety);
    }

    public static List<float> TakeMealIngredientContext(long entityId) =>
        MealIngredientContext.Remove(entityId, out var found) ? found : new();

}
