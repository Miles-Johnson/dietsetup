using System.Collections.Generic;
using System.Linq;
using dietsetup.Diet;
using dietsetup.Rules;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Full-body replacement of BlockMeal.GetContentNutritionProperties (whole-bowl visibility has no
/// other patch point -- see README known limitations for the drift risk that implies). Explicit
/// argument types in HarmonyPatch disambiguate from the 3-param instance overload (BlockMeal.cs:574),
/// a thin wrapper around this one that would otherwise make the target ambiguous.
///
/// Reproduces vanilla's per-ingredient loop (BlockMeal.cs:459-521) with one fix (landmine I):
/// vanilla computes spoilState from each ingredient's own stack but then calls
/// FoodSpoilageSatLossMul/HealthLossMul with the *bowl's* stack, not the ingredient's -- wrong
/// collectible identity for our tag-based resolution. Below, contentStack (the ingredient) goes
/// into both calls instead of inSlot.Itemstack (the bowl).
///
/// Calling BlockMeal.GetIngredientStackNutritionProperties and GlobalConstants.FoodSpoilage*Mul
/// here goes through their own Harmony patches like any other call -- this method does not
/// duplicate their resolve logic, only vanilla's surrounding arithmetic plus the one bug fix.
///
/// Drains DietMealNutritionPatch's per-ingredient nutrition-multiplier hand-off (in the same loop
/// order) into DietProfileRegistry's queue for DietSaturationScalePatch to consume -- clearing
/// that queue first, because this method fires twice per real eat (landmine G: once for a
/// UI/interaction check, once from the actual Consume() call) and a stale first-call queue must
/// not survive into the second.
///
/// Also peeks DietSpoilageResolution's cache (populated by the FoodSpoilageSatLossMul call above)
/// for each ingredient's Verdict and Effects and queues them in PendingMealEffects for
/// DietMealEffectFirePatch. Architecture 7.5: an Inedible ingredient does not refuse the eat --
/// its satiety/nutrition are already zeroed by DietResolver, so it reaches this loop's sum as a
/// zero contribution like any other, and __result is always the built (possibly all-zero) list,
/// never null. Nulling it here used to double as vanilla's own "nothing to eat" refusal signal,
/// which also meant every null-checking choke point upstream (tryHeldBeginEatMeal,
/// tryHeldContinueEatMeal, the tooltip's GetContentNutritionFacts -- the last one has no null
/// guard at all) broke on an Inedible ingredient, not just the final Consume() call.
/// </summary>
[HarmonyPatch(typeof(BlockMeal), nameof(BlockMeal.GetContentNutritionProperties),
    new[] { typeof(IWorldAccessor), typeof(ItemSlot), typeof(ItemStack[]), typeof(EntityAgent), typeof(bool), typeof(float), typeof(float) })]
public static class DietMealContentNutritionPatch
{
    [HarmonyPrefix]
    public static bool Prefix(IWorldAccessor world, ItemSlot inSlot, ItemStack?[]? contentStacks, EntityAgent? forEntity, bool mulWithStacksize, float nutritionMul, float healthMul, ref FoodNutritionProperties[] __result)
    {
        if (!DietSetupModSystem.Config.EnableDietSystem) return true;

        var list = new List<FoodNutritionProperties>();
        var mealEffects = new List<DietResolveResult>();
        ItemStack? bowlStack = inSlot.Itemstack;

        if (contentStacks != null && bowlStack != null)
        {
            bool timeFrozen = bowlStack.Attributes.GetBool("timeFrozen");

            // BlockPie's fillings are held permanently fresh (UnspoilContents) and keep whatever
            // raw/cooked code they had going in, so their own state axis is meaningless here -- the
            // pie's own age is read once and threaded per-filling via DietSpoilageResolution's
            // ambient context (Q1/Q6 of the 2026-09-04 notes entry).
            bool bowlIsPie = bowlStack.Collectible is BlockPie;
            float pieSpoilLevel = 0f;
            if (bowlIsPie && !timeFrozen)
            {
                pieSpoilLevel = bowlStack.Collectible.UpdateAndGetTransitionState(world, inSlot, EnumTransitionType.Perish)?.TransitionLevel ?? 0f;
            }

            string recipeCode = bowlStack.Attributes.GetString("recipeCode");
            List<CookingRecipeIngredient>? recipeIngredients = world.Api.GetCookingRecipe(recipeCode)?.Ingredients?
                .Select(ing => ing.Clone()).ToList();

            foreach (ItemStack? contentStack in contentStacks)
            {
                if (contentStack == null) continue;

                float quantity = contentStack.StackSize;
                ItemStack nutriStack = contentStack.Clone();
                nutriStack.StackSize = 1;

                if (!mulWithStacksize)
                {
                    nutriStack.StackSize = (int)(BlockLiquidContainerBase.GetContainableProps(nutriStack)?.ItemsPerLitre ?? 1f);
                    CookingRecipeIngredient? matched = recipeIngredients?.FirstOrDefault(ing => ing.Matches(nutriStack));
                    if (matched != null)
                    {
                        quantity = matched.GetMatchingStack(nutriStack)?.StackSize ?? 1;
                        nutriStack.StackSize = (int)(nutriStack.StackSize * matched.PortionSizeLitres);
                        matched.MaxQuantity--;
                        if (matched.MaxQuantity == 0) recipeIngredients!.Remove(matched);
                    }
                    else
                    {
                        quantity = 1f;
                    }
                }

                FoodNutritionProperties? ingredientProps = BlockMeal.GetIngredientStackNutritionProperties(world, nutriStack, forEntity);
                if (ingredientProps == null) continue;

                // Not ingredientProps.Clone(): a filled liquid container ingredient's EatenStack has
                // a null Code (BlockLiquidContainerBase.GetNutritionProperties only sets
                // ResolvedItemstack), and JsonItemStack.CloneTo dereferences Code unconditionally.
                FoodNutritionProperties props = new FoodNutritionProperties
                {
                    FoodCategory = ingredientProps.FoodCategory,
                    Satiety = ingredientProps.Satiety,
                    Health = ingredientProps.Health,
                    Intoxication = ingredientProps.Intoxication,
                    Psychedelic = ingredientProps.Psychedelic,
                    SaturationLossDelay = ingredientProps.SaturationLossDelay,
                    EatenStack = ingredientProps.EatenStack
                };
                float spoilState = 0f;
                if (!timeFrozen)
                {
                    var dummySlot = new DummySlot(contentStack, inSlot.Inventory);
                    spoilState = contentStack.Collectible.UpdateAndGetTransitionState(world, dummySlot, EnumTransitionType.Perish)?.TransitionLevel ?? 0f;
                }

                // Not spoilage-only despite the vanilla method name: DietSpoilageSatietyPatch's
                // postfix *replaces* (not folds) vanilla's own spoilage value with the diet's fully
                // resolved satiety/health multiplier whenever a rule matched -- including the
                // Inedible zero override -- so these locals carry either vanilla's spoilage curve
                // (no rule matched) or the diet's resolve (one did), never both combined.
                float ingredientSatietyMult;
                float ingredientHealthMult;
                DietResolveResult? ingredientResolved = null;
                if (bowlIsPie) DietSpoilageResolution.SetPieFillingContext(bowlStack, pieSpoilLevel);
                try
                {
                    ingredientSatietyMult = GlobalConstants.FoodSpoilageSatLossMul(spoilState, contentStack, forEntity);
                    ingredientHealthMult = GlobalConstants.FoodSpoilageHealthLossMul(spoilState, contentStack, forEntity);

                    // Peeks the resolve FoodSpoilageSatLossMul's own postfix (DietSpoilageSatietyPatch)
                    // just cached for this exact key -- the same fold that set ingredientSatietyMult
                    // above, not a second resolve for this ingredient. Must run before the pie context
                    // is cleared below: the cache key now includes the ambient pie context, so peeking
                    // after clearing it would never match a pie filling's own entry (2026-09-04).
                    if (DietSpoilageResolution.TryGetLastResolved(contentStack, spoilState, forEntity, out DietResolveResult resolved))
                    {
                        ingredientResolved = resolved;
                    }
                }
                finally
                {
                    if (bowlIsPie) DietSpoilageResolution.ClearPieFillingContext();
                }
                props.Satiety *= ingredientSatietyMult * nutritionMul * quantity;
                props.Health *= ingredientHealthMult * healthMul * quantity;
                props.Intoxication *= quantity;
                props.Psychedelic *= quantity;
                list.Add(props);

                if (ingredientResolved.HasValue)
                {
                    mealEffects.Add(ingredientResolved.Value);
                }
            }
        }

        // DisplayOnly (DietMealFactsContext) is set by DietMealContentNutritionFactsPatch around a
        // GetContentNutritionFacts call -- a facts/tooltip build, never a real eat. Without this
        // guard, forEntity being non-null there (substituted, or already real for crock/pot GUI
        // panels) would still clear+repopulate this entity's real-eat queues on every hover/GUI open.
        if (forEntity != null && !DietMealFactsContext.DisplayOnly)
        {
            DietProfileRegistry.ClearNutritionMultiplierQueue(forEntity.EntityId);
            foreach (float nutritionMult in MealIngredientNutritionHandoff.TakeAll(forEntity.EntityId))
            {
                DietProfileRegistry.EnqueueNutritionMultiplier(forEntity.EntityId, nutritionMult);
            }

            // Replaced, not merged, every call -- see PendingMealEffects' doc for why that's what
            // makes this safe against landmine C's double invocation.
            PendingMealEffects.Replace(forEntity.EntityId, mealEffects);
        }

        // Never null (7.5): every caller of this method null-checks it as "nothing to eat", and one
        // of them (GetContentNutritionFacts, the tooltip) has no null guard at all -- an Inedible
        // ingredient is a zero contribution already folded into list above, not a refusal.
        __result = list.ToArray();
        return false;
    }
}
