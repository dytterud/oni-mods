using UnityEngine;

namespace BlueprintsV2.BlueprintData.NoteToolPlacedEntities;

internal class ElementNoteConfig : CommonPlacerConfig, IEntityConfig
{
    public static string ID = "BlueprintsV2_Element_Note";
    static Material slurpPlacerMaterial = null!;
    public GameObject CreatePrefab()
    {
        slurpPlacerMaterial = new Material(Assets.instance.mopPlacerAssets.material);
        slurpPlacerMaterial.mainTexture = ModAssets.Gas_Placer_Sprite.texture;
        GameObject prefab = this.CreatePrefab(ID, ID, slurpPlacerMaterial);
        prefab.AddTag(GameTags.NotConversationTopic);
        UnityEngine.Object.Destroy(prefab.GetComponent<Prioritizable>());
        prefab.AddOrGet<KSelectable>();
        prefab.AddOrGet<InfoDescription>();
        prefab.AddOrGet<ElementOnlyFilterable>();
        prefab.AddOrGet<ElementNote>();
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
