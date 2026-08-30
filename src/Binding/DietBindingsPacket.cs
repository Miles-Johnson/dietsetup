using System;
using System.Collections.Generic;
using ProtoBuf;

namespace dietsetup.Binding;

/// <summary>Wire shape for BindingsFile (task 1: the client has no access to the server's
/// ModConfig/dietsetup/bindings.json, so the server pushes the resolved table down instead).
/// Trait codes and diet ids are parallel arrays, not a Dictionary -- protobuf-net dictionary
/// support adds complexity this two-array zip avoids.</summary>
[ProtoContract]
public class DietBindingsPacket
{
    [ProtoMember(1)] public string[] TraitCodes = Array.Empty<string>();
    [ProtoMember(2)] public string[] DietIds = Array.Empty<string>();
    [ProtoMember(3)] public string Default = "base";

    public static DietBindingsPacket From(BindingsFile bindings)
    {
        var traitCodes = new string[bindings.Bindings.Count];
        var dietIds = new string[bindings.Bindings.Count];
        int i = 0;
        foreach ((string trait, string dietId) in bindings.Bindings)
        {
            traitCodes[i] = trait;
            dietIds[i] = dietId;
            i++;
        }

        return new DietBindingsPacket
        {
            TraitCodes = traitCodes,
            DietIds = dietIds,
            Default = bindings.Default ?? "base"
        };
    }

    public BindingsFile ToBindingsFile()
    {
        var bindings = new Dictionary<string, string>(TraitCodes.Length);
        for (int i = 0; i < TraitCodes.Length && i < DietIds.Length; i++)
        {
            bindings[TraitCodes[i]] = DietIds[i];
        }

        return new BindingsFile { SchemaVersion = 1, Bindings = bindings, Default = Default };
    }
}
