using KSerialization;
using UnityEngine;

namespace BlueprintsV2.BlueprintData;

public class DataTransferCleanup : KMonoBehaviour
{
    [Serialize]
    public bool ComponentInUse = false;
    public bool SetInUse(bool active = true)
    {
        ComponentInUse = active;
        return ComponentInUse;
    }
    bool destroyed = false;
    int handle = -1;

    public override void OnSpawn()
    {
        base.OnSpawn();
        handle = Game.Instance.Subscribe((int)GameHashes.SelectObject, GlobalSelectHandler);
    }
    public override void OnCleanUp()
    {
        base.OnCleanUp();
        //the subscription lives on Game.Instance, so it has to be released there —
        //KMonoBehaviour.Unsubscribe would look for the handle on our own event system and
        //leave the handler wired up on Game for the rest of the session
        if (handle != -1 && Game.Instance != null)
        {
            Game.Instance.Unsubscribe(handle);
            handle = -1;
        }
    }
    void GlobalSelectHandler(object? data)
    {
        if (!ComponentInUse || destroyed || gameObject == null)
            return;

        if (data is GameObject go && go == gameObject)
            return;

        UnderConstructionDataSettingHelper.HandleDeselection(this);
        Destroy(gameObject);
        destroyed = true;
    }
}
