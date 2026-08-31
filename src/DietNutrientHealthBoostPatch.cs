using dietsetup.Binding;
using dietsetup.Rules;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Full prefix replacement of UpdateNutrientHealthBoost (2.2): weighted average
/// bonus = 12.5 * sum(level_i/maxSaturation * capacity_i) / sum(capacity_i). HealthWeight ==
/// Capacity by construction (standing rule 10 -- must track DietSaturationScalePatch's gain scale).
/// </summary>
[HarmonyPatch(typeof(EntityBehaviorHunger), nameof(EntityBehaviorHunger.UpdateNutrientHealthBoost))]
public static class DietNutrientHealthBoostPatch
{
    [HarmonyPrefix]
    public static bool Prefix(EntityBehaviorHunger __instance)
    {
        if (!DietSetupModSystem.Config.EnableDietSystem) return true;

        CompiledDiet? diet = DietIdResolver.ResolveDiet(__instance.entity);
        if (diet == null) return true;

        float bonus = ComputeBonus(diet, __instance);
        __instance.entity.GetBehavior<EntityBehaviorHealth>()?.SetMaxHealthModifiers("nutrientHealthMod", bonus);

        return false;
    }

    /// <summary>Exposed so /dietsetnutrition reports this exact number instead of a second
    /// formula: EntityBehaviorHealth's public MaxHealthModifiers getter is a dead auto-property,
    /// disconnected from the private dict SetMaxHealthModifiers actually writes.</summary>
    public static float ComputeBonus(CompiledDiet diet, EntityBehaviorHunger hunger)
    {
        float maxSaturation = hunger.MaxSaturation;
        float numerator = 0f;
        float denominator = 0f;

        AddCategory(diet, EnumFoodCategory.Fruit, hunger.FruitLevel, maxSaturation, ref numerator, ref denominator);
        AddCategory(diet, EnumFoodCategory.Vegetable, hunger.VegetableLevel, maxSaturation, ref numerator, ref denominator);
        AddCategory(diet, EnumFoodCategory.Protein, hunger.ProteinLevel, maxSaturation, ref numerator, ref denominator);
        AddCategory(diet, EnumFoodCategory.Grain, hunger.GrainLevel, maxSaturation, ref numerator, ref denominator);
        AddCategory(diet, EnumFoodCategory.Dairy, hunger.DairyLevel, maxSaturation, ref numerator, ref denominator);

        // No zero-denominator guard: all-zero capacity is fatal at load (rule 8), so a
        // capacity-0 category (2.4) just drops its own term from both sums instead.
        return 12.5f * (numerator / denominator);
    }

    private static void AddCategory(CompiledDiet diet, EnumFoodCategory cat, float level, float maxSaturation, ref float numerator, ref float denominator)
    {
        if (!diet.Categories.TryGetValue(cat, out CompiledCategory category)) return;

        numerator += level / maxSaturation * category.HealthWeight;
        denominator += category.HealthWeight;
    }
}
