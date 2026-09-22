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
    /// <see cref="GetStoredData"/> parsed back into <see cref="JObject"/>s, for
    /// <see cref="API_Methods.GetAllAdditionalBuildingData"/>.
    ///
    /// A malformed entry is skipped and logged rather than thrown: this feeds a public API surface
    /// that external mods reflect into, and the stored strings come from
    /// <see cref="SetDataToApply(string, string)"/>, which any caller can reach.
    /// </summary>
    internal Dictionary<string, JObject> GetDataDeserialized()
    {
        var result = new Dictionary<string, JObject>();
        foreach (var data in GetStoredData())
        {
            try
            {
                result[data.Key] = JObject.Parse(data.Value);
            }
            catch (Exception e)
            {
                SgtLogger.error($"Could not deserialize stored data for {data.Key}:\n{e.Message}");
            }
        }
        return result;
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
        try
        {
            UnderConstructionDataSettingHelper.StartEditingUnderConstructionData(this);
        }
        catch (Exception e)
        {
            ///The latch is already taken here and only CleanUp gives it back, so a throw anywhere
            ///in the session setup would grey the button out on every planned building for the
            ///rest of the process - it is static, and nothing re-initialises it on colony load
            ///(#109).
            SgtLogger.error($"Could not start a preconfigure session for {building.Def.PrefabID}:\n{e}");
            UnderConstructionDataSettingHelper.CleanUp();
        }
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
