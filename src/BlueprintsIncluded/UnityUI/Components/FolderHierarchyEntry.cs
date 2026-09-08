using BlueprintsV2.BlueprintData;
using UtilLibs.UIcmp;

namespace BlueprintsV2.UnityUI.Components
{
	internal class FolderHierarchyEntry : KMonoBehaviour
	{
		public BlueprintFolder folder = null!;

		public System.Action OnEntryClicked = null!;
		FButton button = null!;
		LocText Label = null!;

		public override void OnPrefabInit()
		{
			base.OnPrefabInit();
			Label = transform.Find("Label").gameObject.GetComponent<LocText>();
			button = gameObject.AddComponent<FButton>();
		}
		public override void OnSpawn()
		{
			base.OnSpawn();
			if (folder != null)
			{
				Label.SetText(folder.Name);
				button.OnClick += OnEntryClicked;
			}
		}
	}
}
