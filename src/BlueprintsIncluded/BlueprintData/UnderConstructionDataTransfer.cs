using BlueprintsV2.ModAPI;
using KSerialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UtilLibs;

namespace BlueprintsV2.BlueprintData;

public class UnderConstructionDataTransfer : KMonoBehaviour
, ISidescreenButtonControl
{
    public static Dictionary<Tuple<int, ObjectLayer>, UnderConstructionDataTransfer> RegisteredTransferPlans = new();


    [Serialize]
    [SerializeField]
    public Dictionary<string, string> ToApplyData = new();

    [MyCmpGet]
    public BuildingUnderConstruction building = null!;

    Tuple<int, ObjectLayer> currentPos = null!;

    public override void OnPrefabInit()
    {
        base.OnPrefabInit();
        currentPos = new(Grid.PosToCell(this), building.Def.ObjectLayer);
        RegisteredTransferPlans[currentPos] = this;
    }
    public override void OnSpawn()
    {
        base.OnSpawn();
    }


    public override void OnCleanUp()
    {
        RegisteredTransferPlans.Remove(currentPos);
        //Unsubscribe((int)GameHashes.SelectObject, OnSelectObject);
        base.OnCleanUp();
    }

    public void SetDataToApply(string id, JObject value)
    {
        if (id.IsNullOrWhiteSpace())
            return;
        ToApplyData[id] = JsonConvert.SerializeObject(value);
    }
    public void SetDataToApply(string id, string serializedValue)
    {
        //SgtLogger.l("registering stored data for " + id);
        if (id.IsNullOrWhiteSpace() || serializedValue.IsNullOrWhiteSpace())
        {
            //SgtLogger.l("data was null");
            return;
        }
        //SgtLogger.l("registering stored data for " + id);
        ToApplyData[id] = serializedValue;
    }

    public Dictionary<string, string> GetStoredData()
    {
        return new(ToApplyData);
    }

    /// <summary>
    /// The pending data, parsed. An entry that does not parse as a JSON object is logged and left
    /// out rather than thrown: the strings can come from any caller of the public
    /// <see cref="SetDataToApply(string, string)"/>, and this feeds the public
    /// <see cref="API_Methods.GetAllAdditionalBuildingData"/>, where one bad entry must not cost
    /// the caller every other setting or surface as an exception it cannot explain.
    /// </summary>
    internal Dictionary<string, JObject> GetDataDeserialized()
    {
        var parsed = new Dictionary<string, JObject>(ToApplyData.Count);
        foreach (var entry in ToApplyData)
        {
            try
            {
                parsed[entry.Key] = JObject.Parse(entry.Value);
            }
            catch (Exception e)
            {
                SgtLogger.error($"Skipping unreadable pending data for {entry.Key} on {gameObject.name}:\n{e.Message}");
            }
        }
        return parsed;
    }

    public static void TransferDataTo(GameObject targetBuilding, Dictionary<string, string> toApply)
    {
        foreach (var data in toApply)
        {
            API_Methods.TryApplyingStoredData(targetBuilding, data.Key, JObject.Parse(data.Value));
        }
    }
    #region DataSetter
    public string SidescreenButtonText => STRINGS.UI.PRECONFIGURE_UNDERCONSTRUCTION.TITLE;
    public string SidescreenButtonTooltip => STRINGS.UI.PRECONFIGURE_UNDERCONSTRUCTION.TOOLTIP;
    public void SetButtonTextOverride(ButtonMenuTextOverride textOverride)
    {
        SgtLogger.l("SetButtonTextOverride");
    }

    public static bool SelectButtonUnlocked = true;

    public bool SidescreenEnabled() => UnderConstructionDataSettingHelper.HasDataTransferComponents(building);

    public bool SidescreenButtonInteractable() => SelectButtonUnlocked;

    public void OnSidescreenButtonPressed()
    {
        if (!SelectButtonUnlocked)
            return;
        SelectButtonUnlocked = false;
        UnderConstructionDataSettingHelper.StartEditingUnderConstructionData(this);
    }

    public int HorizontalGroupID() => -1;

    public int ButtonSideScreenSortOrder() => 22;

    internal void TransferStoredDataToBlueprintEntry(BuildingConfig buildingConfig)
    {
        foreach (var data in GetStoredData())
        {
            buildingConfig.SetBuildingData(data.Key, JObject.Parse(data.Value));
        }
    }
    #endregion
}
