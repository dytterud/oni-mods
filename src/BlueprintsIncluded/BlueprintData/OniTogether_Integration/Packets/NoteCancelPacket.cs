using BlueprintsV2.BlueprintData;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together_API;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BlueprintsV2.BlueprintsV2.BlueprintData.OniTogether_Integration.Packets
{
	internal class NoteCancelPacket : IPacket
	{
		public int cell;
		public NoteCancelPacket() { }
		public NoteCancelPacket(int cell)
		{
			this.cell = cell;
		}
		public void Deserialize(BinaryReader reader)
		{
			cell = reader.ReadInt32();
		}
		public void Serialize(BinaryWriter writer)
		{
			writer.Write(cell);
		}
		public void OnDispatched()
		{

			var note = Grid.Objects[cell, (int)ModAssets.BlueprintNotesLayer];

			if (note != null)//dont trigger loop
				note.DeleteObject();
		}
	}
}
