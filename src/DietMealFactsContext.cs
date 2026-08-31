using System;

namespace dietsetup;

/// <summary>Marks the current thread's call into BlockMeal.GetContentNutritionFacts as
/// display-only, so the real-eat side effects it would otherwise trigger (DietProfileRegistry's
/// nutrition-multiplier queue, MealIngredientNutritionHandoff, PendingMealEffects) stay untouched.
/// [ThreadStatic], not a plain static: client and server run on separate threads in the same
/// process for singleplayer (landmine C) -- a plain static here would let a client-side hover
/// observe or clobber a concurrent server-side real eat's flag, or vice versa.</summary>
internal static class DietMealFactsContext
{
    [ThreadStatic]
    private static bool displayOnly;

    public static bool DisplayOnly
    {
        get => displayOnly;
        set => displayOnly = value;
    }
}
