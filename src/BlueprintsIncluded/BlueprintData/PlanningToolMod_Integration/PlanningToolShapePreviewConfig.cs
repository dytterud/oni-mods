using BlueprintsV2.BlueprintData.PlannedElements;
using System.Text;
using UnityEngine;

namespace BlueprintsV2.BlueprintData.PlanningToolMod_Integration
{
	internal class PlanningToolShapePreviewConfig : CommonPlacerConfig, IEntityConfig
	{
		public static string ID = "BlueprintsV2_PlanningToolShapePreview_Placer";
		static Material slurpPlacerMaterial = null!;
		public GameObject CreatePrefab()
		{
			slurpPlacerMaterial = new Material(Assets.instance.mopPlacerAssets.material);
			slurpPlacerMaterial.mainTexture = ModAssets.PlanningToolPreview_Square.texture;

			GameObject prefab = this.CreatePrefab(ID, ID, slurpPlacerMaterial);
			prefab.AddTag(GameTags.NotConversationTopic);
			UnityEngine.Object.Destroy(prefab.GetComponent<Prioritizable>());
			prefab.AddOrGet<PlanningToolShapePreview>();
			return prefab;
		}
		public string[] GetDlcIds() => null!;
		public void OnPrefabInit(GameObject go)
		{
		}

		public void OnSpawn(GameObject go)
		{
		}
	}
}
