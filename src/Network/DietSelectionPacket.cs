using ProtoBuf;

namespace dietsetup.Network;

/// <summary>Client -> server. The chosen profile id. The server validates it against the
/// registry before writing anything -- never trust the client-sent id.</summary>
[ProtoContract]
public class DietSelectionPacket
{
    [ProtoMember(1)] public string ProfileId = "";
}
