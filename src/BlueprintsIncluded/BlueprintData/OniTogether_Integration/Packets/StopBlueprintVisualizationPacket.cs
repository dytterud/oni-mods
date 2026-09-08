using ONI_Together.Networking.Packets.Architecture;
using ONI_Together_API;

namespace BlueprintsV2.BlueprintData.OniTogether_Integration.Packets;

internal class StopBlueprintVisualizationPacket : IPacket
{
    public StopBlueprintVisualizationPacket()
    {
        SenderId = SessionInfoAPI.LocalUserID;
    }
    ulong SenderId;


    public void Deserialize(BinaryReader reader)
    {
        SenderId = reader.ReadUInt64();
    }
    public void Serialize(BinaryWriter writer)
    {
        writer.Write(SenderId);
    }
    public void OnDispatched()
    {
        BlueprintState.ClearDisplayBlueprintForPlayer(SenderId);
    }
}
