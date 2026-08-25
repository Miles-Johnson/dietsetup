using dietsetup.Diet;
using dietsetup.Rules;
using dietsetup.Tags;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Prefix on the protected CollectibleObject.tryEatStop, sibling to DietEatDoTPatch on the same
/// method -- produces the nutrition-multiplier value DietSaturationScalePatch consumes for a
/// standalone (non-meal) eat. Must be a prefix: it has to enqueue before tryEatStop's own body
/// calls ReceiveSaturation (verified against the decompiled body,
/// reference/decompiled/1.22/VintagestoryAPI/Vintagestory.API.Common/CollectibleObject.cs:1779).
///
/// tryEatStop's own guard (nutritionProperties == null -&gt; return, no ReceiveSaturation call) is
/// computed from CollectibleObject.GetNutritionProperties, which runs after any prefix -- so this
/// can't observe that null directly. An assigned diet's Inedible verdict is exactly what makes
/// GetNutritionProperties return null (via DietProfileRegistry.ResolveNutritionProperties), so
/// checking the verdict here directly reproduces vanilla's own downstream decision and keeps the
/// enqueue 1:1 with real ReceiveSaturation calls.
/// </summary>
[HarmonyPatch(typeof(CollectibleObject), "tryEatStop", new[] { typeof(float), typeof(ItemSlot), typeof(EntityAgent) })]
public static class DietNutritionMultiplierEatPatch
{
    [HarmonyPrefix]
    public static void Prefix(float secondsUsed, ItemSlot slot, EntityAgent byEntity)
    {
        DietProfileRegistry.ClearNutritionMultiplierQueue(byEntity.EntityId);

        if (byEntity is not EntityPlayer || !DietSetupModSystem.Config.EnableDietSystem)
        {
            return;
        }

        // Mirrors vanilla's own guard in tryEatStop -- only the server ever actually applies
        // saturation, and a client-side call here would collide with the server's own queue
        // entry (see DietProfileRegistry.PendingNutritionMultipliers' doc comment).
        if (byEntity.World is not IServerWorldAccessor || secondsUsed < 0.95f)
        {
            return;
        }

        ItemStack? stack = slot?.Itemstack;
        if (stack?.Collectible == null) return;

        string dietId = byEntity.WatchedAttributes.GetString(DietSetupModSystem.AttrProfile, DietSetupModSystem.Config.DefaultProfileId);
        CompiledDiet? diet = DietRuleRegistry.GetDiet(dietId);

        var tagSlot = new DummySlot(stack);
        ulong tagMask = FoodTagRegistry.GetTagMask(byEntity.World, tagSlot, out float spoilLevel, out bool determined);

        float nutritionMult = FoodTagRegistry.TagNutritionMultiplier(tagMask, byEntity);

        if (diet != null && determined)
        {
            DietResolveResult result = DietResolver.Resolve(diet, tagMask, spoilLevel, byEntity.Api, byEntity);
            if (result.Verdict == DietVerdict.Inedible)
            {
                return; // no ReceiveSaturation call follows -- GetNutritionProperties already returned null for this stack
            }
            nutritionMult *= result.Nutrition;
        }

        DietProfileRegistry.EnqueueNutritionMultiplier(byEntity.EntityId, nutritionMult);
    }
}
