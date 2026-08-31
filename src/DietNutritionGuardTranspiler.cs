using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.GameContent;

namespace dietsetup;

/// <summary>
/// Transpiler on OnEntityReceiveSaturation: deletes vanilla's "if (!flag)" guard (ldloc.1 +
/// brtrue.s, confirmed against the shipped VSEssentials.dll IL, VS 1.22.6) around each category
/// write, lifting it for nutrition per architecture 9 while satiety's own Math.Min cap is untouched.
/// </summary>
[HarmonyPatch(typeof(EntityBehaviorHunger), nameof(EntityBehaviorHunger.OnEntityReceiveSaturation))]
public static class DietNutritionGuardTranspiler
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        CodeMatcher matcher = new(instructions);
        int guardsRemoved = 0;

        matcher.MatchStartForward(
            new CodeMatch(OpCodes.Ldloc_1),
            new CodeMatch(instr => instr.opcode == OpCodes.Brtrue || instr.opcode == OpCodes.Brtrue_S)
        ).Repeat(m =>
        {
            List<Label> labels = new(m.Labels);
            m.RemoveInstructions(2);
            m.Labels.AddRange(labels);
            guardsRemoved++;
        });

        // Local index 1 and this count are the version-pinned surface -- fail loud on IL drift, not a silent mis-patch.
        if (guardsRemoved != 5)
        {
            throw new InvalidOperationException(
                $"DietNutritionGuardTranspiler expected 5 full-stomach guards, found {guardsRemoved} -- OnEntityReceiveSaturation's IL shape changed.");
        }

        return matcher.InstructionEnumeration();
    }
}
