using ProtoBuf;

namespace dietsetup.Network;

/// <summary>Server -> client. Empty marker telling the client to open the Diet Setup dialog.
/// No payload needed: the client reads current values straight from its own already-synced
/// WatchedAttributes.</summary>
[ProtoContract]
public class DietTriggerPacket
{
}
