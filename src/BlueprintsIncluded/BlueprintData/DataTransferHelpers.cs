using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UtilLibs;
using static AccessControl;

namespace BlueprintsV2.BlueprintData;

internal class DataTransferHelpers
{
    internal class DataTransfer_UserNameable
    {
        const string SavedNameKey = "savedName";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<UserNameable>(out var component))
            {
                return new JObject()
                {
                    { SavedNameKey, component.savedName},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<UserNameable>(out var targetComponent))
            {
                if (!jObject.TryGet<string>(SavedNameKey, out var savedName))
                    return;
                targetComponent.SetName(savedName);
            }
        }
    }
    internal class DataTransfer_BuildingEnabledButton
    {
        const string IsEnabledKey = "IsEnabled";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<BuildingEnabledButton>(out var component))
            {
                bool shouldBeEnabled = component.IsEnabled;
                if (component.queuedToggle)
                    shouldBeEnabled = !shouldBeEnabled; //queued toggle means it will be toggled to the opposite state, so we need to invert it here

                return new JObject()
                {
                    { IsEnabledKey, shouldBeEnabled},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<BuildingEnabledButton>(out var targetComponent))
            {
                if (!jObject.TryGet<bool>(IsEnabledKey, out var IsEnabled))
                    return;
                targetComponent.IsEnabled = IsEnabled;
            }
        }
    }
    internal class DataTransfer_Repairable
    {
        const string ForbiddenRepairKey = "ForbiddenRepair";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<Repairable>(out var component))
            {
                bool repairForbiddenState = false;
                if (component.smi != null)
                {
                    try
                    {
                        repairForbiddenState = component.smi?.GetCurrentState() == component.smi?.sm?.forbidden;
                    }
                    catch (Exception e)
                    {
                        SgtLogger.l("Error getting repairable state: " + e.Message);
                    }
                }
                if (repairForbiddenState == false) //only store non-default value
                    return null;

                return new JObject()
                {
                    { ForbiddenRepairKey, repairForbiddenState},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<Repairable>(out var targetComponent))
            {
                if (!jObject.TryGet<bool>(ForbiddenRepairKey, out var RepairForbidden))
                    return;
                if (targetComponent.smi == null)
                {
                    //SgtLogger.l("Repairable component has no state machine, skipping repair state transfer.");
                    return;
                }

                if (RepairForbidden) ///enabled is default and this smi is buggy af, so we only set it if its forbidden
						targetComponent.CancelRepair();
            }
        }
    }
    internal class DataTransfer_StorageTile
    {
        const string TargetTagKey = "TargetTag";
        const string UserMaxCapacityKey = "UserMaxCapacity";
        internal static JObject? TryGetData(GameObject arg)
        {
            var smi = arg.GetSMI<StorageTile.Instance>();

            if (smi != null && smi.TargetTag != StorageTile.INVALID_TAG)
            {
                return new JObject()
                {
                    { TargetTagKey, smi.TargetTag.ToString()},
                    { UserMaxCapacityKey, smi.UserMaxCapacity},
                };
            }
            ///This handler answers two different questions. Capture asks a real building "what are
            ///your settings"; UnderConstructionDataSettingHelper.HasDataTransferComponents asks the
            ///*prefab* "is this building type configurable at all", to decide whether a planned
            ///building gets a preconfigure button. A prefab has no live state machine, so returning
            ///null here hid the button for every Storage Tile (issue #85).
            ///
            ///<para>The <c>smi == null</c> guard is what keeps capture untouched, and it is the one
            ///place this deliberately differs from upstream 03ceccc. Upstream instead dropped the
            ///INVALID_TAG filter above so the first branch always wins for a real building. Without
            ///the guard, an *unconfigured* Storage Tile would fall through to this empty object
            ///during capture - and an empty entry is not inert: GetAdditionalBuildingData filters
            ///only on null, and BuildingConfig.HasAnyBuildingData counts entries rather than
            ///content, so the preview would paint the apply-settings colour and placement would pop
            ///"Settings applied!" with nothing to apply.</para>
            else if (smi == null && arg.GetDef<StorageTile.Def>() != null)
            {
                return new();
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            var smi = building.GetSMI<StorageTile.Instance>();

            if (smi != null)
            {
                if (!jObject.TryGet<string>(TargetTagKey, out var TargetTag))
                    return;
                var tagParsed = TagManager.Create(TargetTag);
                if (tagParsed.IsValid)
                    smi.SetTargetItem(tagParsed);

                if (!jObject.TryGet<float>(UserMaxCapacityKey, out var UserMaxCapacity))
                    return;
                smi.UserMaxCapacity = UserMaxCapacity;
            }
        }
    }
    internal class DataTransfer_SingleEntityReceptacle
    {
        const string RequestedEntityTagKey = "requestedEntityTag";
        const string RequestedEntityAdditionalFilterTagKey = "requestedEntityAdditionalFilterTag";
        const string AutoReplaceEntityKey = "autoReplaceEntity";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<SingleEntityReceptacle>(out var component))
            {
                var occupant = component.Occupant;

                Tag requestedEntityTag = component.requestedEntityTag;
                Tag additionalFilterTag = component.requestedEntityAdditionalFilterTag;
                if (occupant != null && occupant.TryGetComponent<KPrefabID>(out var occupantPrefabID))
                {
                    requestedEntityTag = occupantPrefabID.PrefabTag;
                    if (occupantPrefabID.TryGetComponent<SeedProducer>(out var seedProducer))
                    {
                        requestedEntityTag = TagManager.Create(seedProducer.seedInfo.seedId);

                        if (occupant.TryGetComponent<MutantPlant>(out var mutant))
                            additionalFilterTag = mutant.SubSpeciesID;
                        else
                            additionalFilterTag = Tag.Invalid;
                    }
                }

                if (requestedEntityTag == null)
                    requestedEntityTag = Tag.Invalid;
                if (additionalFilterTag == null)
                    additionalFilterTag = Tag.Invalid;

                string requestedEntityTagString = requestedEntityTag == Tag.Invalid ? string.Empty : requestedEntityTag.ToString();
                string additionalFilterTagString = additionalFilterTag == Tag.Invalid ? string.Empty : additionalFilterTag.ToString();


                return new JObject()
                {
                    { RequestedEntityTagKey, requestedEntityTagString},
                    { RequestedEntityAdditionalFilterTagKey, additionalFilterTagString},
                    { AutoReplaceEntityKey, component.autoReplaceEntity }
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<SingleEntityReceptacle>(out var targetComponent))
            {
                if (!jObject.TryGet<string>(RequestedEntityTagKey, out var requestedEntityTag))
                    return;
                if (!jObject.TryGet<string>(RequestedEntityAdditionalFilterTagKey, out var requestedEntityAdditionalFilterTag))
                    return;
                if (!jObject.TryGet<bool>(AutoReplaceEntityKey, out var autoReplaceEntity))
                    return;

                //SgtLogger.l("Requested Entity Tag: " + requestedEntityTag + ", extra filter: " + requestedEntityAdditionalFilterTag);

                targetComponent.autoReplaceEntity = autoReplaceEntity;

                var tagParsed = requestedEntityTag.IsNullOrWhiteSpace() ? Tag.Invalid : TagManager.Create(requestedEntityTag);
                var extraTagParsed = requestedEntityAdditionalFilterTag.IsNullOrWhiteSpace() ? Tag.Invalid : TagManager.Create(requestedEntityAdditionalFilterTag);

                if (targetComponent.Occupant == null && tagParsed.IsValid)
                    targetComponent.CreateOrder(tagParsed, extraTagParsed);
            }
        }
    }
    internal class DataTransfer_LogicClusterLocationSensor
    {
        const string ActiveInSpaceKey = "activeInSpace";
        const string ActiveLocationsKey = "activeLocations";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<LogicClusterLocationSensor>(out var component))
            {
                return new JObject()
                {
                    { ActiveInSpaceKey, component.activeInSpace},
                    { ActiveLocationsKey, EmbeddedJson.From(component.activeLocations.Select(axial => new Tuple<int,int>(axial.Q,axial.R)))},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<LogicClusterLocationSensor>(out var targetComponent))
            {
                if (!jObject.TryGet<bool>(ActiveInSpaceKey, out var activeInSpace))
                    return;
                if (!jObject.TryGetEmbedded<List<Tuple<int, int>>>(ActiveLocationsKey, out var activeLocations))
                    return;

                //applying values
                targetComponent.activeInSpace = activeInSpace;
                targetComponent.activeLocations.Clear();
                foreach (var entry in activeLocations)
                {
                    var location = new AxialI(entry.first, entry.second);

                    if (ClusterManager.Instance?.m_grid?.GetAsteroidAtCell(location) != null) //only add valid asteroids
                        targetComponent.SetLocationEnabled(location, true);
                }

            }
        }
    }
    internal class DataTransfer_LogicCounter
    {
        const string MaxCountKey = "maxCount";
        const string ResetCountAtMaxKey = "resetCountAtMax";
        const string AdvancedModeKey = "advancedMode";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<LogicCounter>(out var component))
            {
                return new JObject()
                {
                    { MaxCountKey, component.maxCount},
                    { ResetCountAtMaxKey, component.resetCountAtMax},
                    { AdvancedModeKey, component.advancedMode},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<LogicCounter>(out var targetComponent))
            {
                if (!jObject.TryGet<int>(MaxCountKey, out var maxCount))
                    return;
                targetComponent.maxCount = maxCount;

                if (!jObject.TryGet<bool>(ResetCountAtMaxKey, out var resetCountAtMax))
                    return;
                targetComponent.resetCountAtMax = resetCountAtMax;

                if (!jObject.TryGet<bool>(AdvancedModeKey, out var advancedMode))
                    return;
                targetComponent.advancedMode = advancedMode;
            }
        }
    }
    internal class DataTransfer_PixelPack
    {
        const string ColorSettingsKey = "colorSettings";
        class PixelPackColor
        {
            public float r, g, b, a;
            public PixelPackColor(Color original)
            {
                r = original.r;
                g = original.g;
                b = original.b;
                a = original.a;
            }
            public Color ToColor()
            {
                return new Color(r, g, b, a);
            }
        }
        class PixelPackColorData
        {
            public PixelPackColor activeColor, standbyColor;

            public PixelPackColorData(Color a, Color b)
            {
                activeColor = new PixelPackColor(a);
                standbyColor = new PixelPackColor(b);
            }
            public PixelPack.ColorPair GetData()
            {
                return new PixelPack.ColorPair()
                {
                    activeColor = this.activeColor.ToColor(),
                    standbyColor = this.standbyColor.ToColor()
                };
            }
        }

        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<PixelPack>(out var component))
            {
                PixelPackColorData[] transferedData = new PixelPackColorData[component.colorSettings.Count];
                for (int i = 0; i < component.colorSettings.Count; ++i)
                {
                    var col = component.colorSettings[i];
                    transferedData[i] = new PixelPackColorData(col.activeColor, col.standbyColor);
                }
                return new JObject()
                {
                    { ColorSettingsKey, EmbeddedJson.From(transferedData)},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<PixelPack>(out var targetComponent))
            {
                if (!jObject.TryGetEmbedded<PixelPackColorData[]>(ColorSettingsKey, out var colorSettings))
                    return;

                //applying values
                if (targetComponent.colorSettings == null)
                {
                    //filling with temp empty items
                    var p1 = new PixelPack.ColorPair();
                    p1.activeColor = targetComponent.defaultActive;
                    p1.standbyColor = targetComponent.defaultStandby;
                    var p2 = p1;
                    var p3 = p1;
                    var p4 = p1;
                    targetComponent.colorSettings = new List<PixelPack.ColorPair>
                    {
                        p1,
                        p2,
                        p3,
                        p4
                    };
                }


                for (int index = 0; index < colorSettings.Length; ++index)
                {
                    targetComponent.colorSettings[index] = colorSettings[index].GetData();
                }
                targetComponent.UpdateColors();
            }
        }
    }
    internal class DataTransfer_IUserControlledCapacity
    {
        const string UserMaxCapacityKey = "UserMaxCapacity";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<IUserControlledCapacity>(out var component))
            {
                return new JObject()
                {
                    { UserMaxCapacityKey, component.UserMaxCapacity},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<IUserControlledCapacity>(out var targetComponent))
            {
                if (!jObject.TryGet<float>(UserMaxCapacityKey, out var UserMaxCapacity))
                    return;
                targetComponent.UserMaxCapacity = UserMaxCapacity;
            }
        }
    }
    /// <summary>
    /// AllowManualDelivery
    /// </summary>
    internal class DataTransfer_Automatable
    {
        const string AutomationOnlyKey = "automationOnly";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<Automatable>(out var component))
            {
                return new JObject()
                {
                    { AutomationOnlyKey, component.automationOnly},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<Automatable>(out var targetComponent))
            {
                if (!jObject.TryGet<bool>(AutomationOnlyKey, out var automationOnly))
                    return;
                targetComponent.SetAutomationOnly(automationOnly);
            }
        }
    }
    internal class DataTransfer_LogicTimeOfDaySensor
    {
        const string StartTimeKey = "startTime";
        const string DurationKey = "duration";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<LogicTimeOfDaySensor>(out var component))
            {
                return new JObject()
                {
                    { StartTimeKey, component.startTime},
                    { DurationKey, component.duration},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<LogicTimeOfDaySensor>(out var targetComponent))
            {
                if (!jObject.TryGet<float>(StartTimeKey, out var startTime))
                    return;
                if (!jObject.TryGet<float>(DurationKey, out var duration))
                    return;

                //applying values
                targetComponent.startTime = startTime;
                targetComponent.duration = duration;
            }
        }
    }
    internal class DataTransfer_IActivationRangeTarget
    {
        const string ActivateValueKey = "ActivateValue";
        const string DeactivateValueKey = "DeactivateValue";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<IActivationRangeTarget>(out var component))
            {
                return new JObject()
                {
                    { ActivateValueKey, component.ActivateValue},
                    { DeactivateValueKey, component.DeactivateValue},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<IActivationRangeTarget>(out var targetComponent))
            {
                if (!jObject.TryGet<int>(DeactivateValueKey, out var DeactivateValue))
                    return;
                targetComponent.DeactivateValue = DeactivateValue;

                if (!jObject.TryGet<int>(ActivateValueKey, out var ActivateValue))
                    return;
                targetComponent.ActivateValue = ActivateValue;
            }
        }
    }
    internal class DataTransfer_SpaceHeater
    {
        const string CurrentPowerConsumptionKey = "CurrentPowerConsumption";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<SpaceHeater>(out var component))
            {
                if (!component.produceHeat)
                    return null;

                return new JObject()
                {
                    { CurrentPowerConsumptionKey, component.CurrentPowerConsumption},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<SpaceHeater>(out var targetComponent))
            {
                if (!targetComponent.produceHeat)
                    return;

                if (!jObject.TryGet<float>(CurrentPowerConsumptionKey, out var CurrentPowerConsumption))
                    return;
                targetComponent.SetUserSpecifiedPowerConsumptionValue(CurrentPowerConsumption);
            }
        }
    }
    internal class DataTransfer_Clinic
    {
        const string SicknessSliderValueKey = "sicknessSliderValue";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<Clinic>(out var component))
            {
                return new JObject()
                {
                    { SicknessSliderValueKey, component.sicknessSliderValue},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<Clinic>(out var targetComponent))
            {
                if (!jObject.TryGet<float>(SicknessSliderValueKey, out var sicknessSliderValue))
                    return;

                if (targetComponent is ISliderControl sliderControl)
                    sliderControl.SetSliderValue(sicknessSliderValue, 0);
            }
        }
    }
    internal class DataTransfer_EnergyGenerator
    {
        const string BatteryRefillPercentKey = "batteryRefillPercent";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<EnergyGenerator>(out var component))
            {
                if (component.ignoreBatteryRefillPercent)
                    return null;
                return new JObject()
                {
                    { BatteryRefillPercentKey, component.batteryRefillPercent},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<EnergyGenerator>(out var targetComponent))
            {
                if (targetComponent.ignoreBatteryRefillPercent)
                    return;

                if (!jObject.TryGet<float>(BatteryRefillPercentKey, out var batteryRefillPercent))
                    return;
                targetComponent.batteryRefillPercent = batteryRefillPercent;
            }
        }
    }
    internal class DataTransfer_FoodStorage
    {
        const string SpicedFoodOnlyKey = "SpicedFoodOnly";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<FoodStorage>(out var component) && component.SpicedFoodOnly == true)
            {
                return new JObject()
                {
                    { SpicedFoodOnlyKey, component.SpicedFoodOnly},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<FoodStorage>(out var targetComponent))
            {
                if (jObject.TryGet<bool>(SpicedFoodOnlyKey, out var spicedFoodOnly))
                    targetComponent.SpicedFoodOnly = spicedFoodOnly;
            }
        }
    }
    internal class DataTransfer_AutoDisinfectable
    {
        const string EnableAutoDisinfectKey = "enableAutoDisinfect";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<AutoDisinfectable>(out var component) && component.enableAutoDisinfect == false) //only store nondefault value
            {
                return new JObject()
                {
                    { EnableAutoDisinfectKey, component.enableAutoDisinfect},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<AutoDisinfectable>(out var targetComponent))
            {
                if (jObject.TryGet<bool>(EnableAutoDisinfectKey, out var enableAutoDisinfect))
                {
                    if (enableAutoDisinfect)
                        targetComponent.EnableAutoDisinfect();
                    else
                        targetComponent.DisableAutoDisinfect();
                }
            }
        }
    }
    internal class DataTransfer_Door
    {
        const string RequestedStateKey = "requestedState";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<Door>(out var component))
            {
                if (component.doorType == Door.DoorType.Sealed && !component.hasBeenUnsealed)
                    return null;

                return new JObject()
                {
                    { RequestedStateKey, (int)component.RequestedState},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<Door>(out var targetComponent))
            {
                if (jObject.TryGet<int>(RequestedStateKey, out var requestedStateRaw))
                {
                    var requestedState = (Door.ControlState)requestedStateRaw;
                    if (BlueprintState.InstantBuild)
                    {
                        targetComponent.requestedState = requestedState;
                        targetComponent.ApplyRequestedControlState();
                    }
                    else
                        targetComponent.QueueStateChange(requestedState);
                }
            }
        }
    }
    internal class DataTransfer_DirectionControl
    {
        const string AllowedDirectionKey = "allowedDirection";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<DirectionControl>(out var component))
            {
                return new JObject()
                {
                    { AllowedDirectionKey, (int)component.allowedDirection},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<DirectionControl>(out var targetComponent))
            {
                if (jObject.TryGet<int>(AllowedDirectionKey, out var allowedDirectionRaw))
                    targetComponent.SetAllowedDirection((WorkableReactable.AllowedDirection)allowedDirectionRaw);
            }
        }
    }

    internal class DataTransfer_Prioritizable
    {
        const string MasterPrioritySettingKey = "masterPrioritySetting";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<Prioritizable>(out var component))
            {
                var prio = component.GetMasterPriority();
                if (prio.priority_value == 5 && prio.priority_class == PriorityScreen.PriorityClass.basic) //ignore default state
                    return null;

                //SgtLogger.l("Getting prio " + prio.priority_value + " from " + arg.name);
                return new JObject()
                {
                    { MasterPrioritySettingKey, EmbeddedJson.From(prio)},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<Prioritizable>(out var targetComponent))
            {
                if (jObject.TryGetEmbedded<PrioritySetting>(MasterPrioritySettingKey, out var masterPrioritySetting))
                {
                    //SgtLogger.l("applying prio: " + masterPrioritySetting.priority_value);
                    targetComponent.SetMasterPriority(masterPrioritySetting);
                }
            }
        }
    }
    internal class DataTransfer_TreeFilterable
    {
        const string AcceptedTagSetKey = "acceptedTagSet";
        const string OnlyFetchMarkedItemsKey = "onlyFetchMarkedItems";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<TreeFilterable>(out var component))
            {
                if (!component.copySettingsEnabled)
                    return null;

                var tags = component.GetTags();
                var targetStorage = component.GetFilterStorage();
                bool onlyFetchMarkedItems = targetStorage != null && targetStorage.allowSettingOnlyFetchMarkedItems ? targetStorage.onlyFetchMarkedItems : false;

                return new JObject()
                {
                    { AcceptedTagSetKey, EmbeddedJson.From(tags)},
                    { OnlyFetchMarkedItemsKey, onlyFetchMarkedItems},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<TreeFilterable>(out var targetComponent))
            {
                if (!targetComponent.copySettingsEnabled)
                    return;

                if (jObject.TryGetEmbedded<HashSet<Tag>>(AcceptedTagSetKey, out var acceptedTagSet))
                    targetComponent.UpdateFilters(acceptedTagSet);

                if (jObject.TryGet<bool>(OnlyFetchMarkedItemsKey, out var onlyFetchMarkedItems))
                {
                    var storage = targetComponent.GetFilterStorage();
                    if (storage.allowSettingOnlyFetchMarkedItems)
                        storage.SetOnlyFetchMarkedItems(onlyFetchMarkedItems);
                }
            }
        }
    }
    internal class DataTransfer_FlatTagFilterable
    {
        const string SelectedTagsKey = "selectedTags";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<FlatTagFilterable>(out var component))
            {
                if (!component.currentlyUserAssignable)
                    return null;

                var selectedTags = component.selectedTags;

                return new JObject()
                {
                    { SelectedTagsKey, EmbeddedJson.From(selectedTags)},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<FlatTagFilterable>(out var targetComponent))
            {
                if (!targetComponent.currentlyUserAssignable)
                    return;
                if (jObject.TryGetEmbedded<HashSet<Tag>>(SelectedTagsKey, out var selectedTags))
                {
                    targetComponent.selectedTags.Clear();
                    foreach (Tag selectedTag in selectedTags)
                    {
                        if (!targetComponent.tagOptions.Contains(selectedTag))
                            targetComponent.tagOptions.Add(selectedTag);
                        targetComponent.SelectTag(selectedTag, state: true);
                    }
                    targetComponent.GetComponent<TreeFilterable>().UpdateFilters([.. selectedTags]);
                }
            }
        }
    }

    internal class DataTransfer_Filterable
    {
        const string SelectedTagKey = "SelectedTag";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<Filterable>(out var component))
            {
                return new JObject()
                {
                    { SelectedTagKey, component.SelectedTag.ToString()}
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<Filterable>(out var targetComponent))
            {
                if (!jObject.TryGet<string>(SelectedTagKey, out var selectedTagString))
                    return;

                var selectedTag = selectedTagString.IsNullOrWhiteSpace() ? Tag.Invalid : TagManager.Create(selectedTagString);
                if (selectedTag.IsValid)
                    targetComponent.SelectedTag = selectedTag;
            }
        }
    }
    /// <summary>
    /// Door access control
    /// </summary>
    internal class DataTransfer_AccessControl
    {
        const string DefaultPermissionByTagKey = "defaultPermissionByTag";
        const string SavedPermissionsByIdKey = "savedPermissionsById";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<AccessControl>(out var component) && component.controlEnabled)
            {
                return new JObject()
                {
                    { DefaultPermissionByTagKey, EmbeddedJson.From(component.defaultPermissionByTag)},
                    { SavedPermissionsByIdKey, EmbeddedJson.From(component.savedPermissionsById)}
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<AccessControl>(out var targetComponent) && targetComponent.controlEnabled)
            {
                if (!jObject.TryGetEmbedded<List<KeyValuePair<Tag, Permission>>>(DefaultPermissionByTagKey, out var defaultPermissionByTag))
                    return;
                targetComponent.defaultPermissionByTag = defaultPermissionByTag;

                if (!jObject.TryGetEmbedded<List<KeyValuePair<int, Permission>>>(SavedPermissionsByIdKey, out var savedPermissionsById))
                    return;
                try
                {
                    foreach (var item in savedPermissionsById)
                        targetComponent.SetPermission(item.Key, item.Value);
                }
                catch (Exception e)
                {
                    SgtLogger.error("Error while applying saved door permissions:\n" + e);
                }
            }
        }
    }
    internal class DataTransfer_LimitValve
    {
        const string LimitKey = "Limit";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<LimitValve>(out var component))
            {
                return new JObject()
                {
                    { LimitKey, component.Limit}
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<LimitValve>(out var targetComponent))
            {
                if (!jObject.TryGet<float>(LimitKey, out var Limit))
                    return;

                //applying values
                targetComponent.Limit = Limit;
            }
        }
    }
    internal class DataTransfer_Valve
    {
        const string DesiredFlowKey = "DesiredFlow";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<Valve>(out var component))
            {
                return new JObject()
                {
                    { DesiredFlowKey, component.DesiredFlow}
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<Valve>(out var targetComponent))
            {
                if (!jObject.TryGet<float>(DesiredFlowKey, out var DesiredFlow))
                    return;

                //applying values
                targetComponent.ChangeFlow(DesiredFlow);
            }
        }
    }

    internal class DataTransfer_LogicTimerSensor
    {
        const string OnDurationKey = "onDuration";
        const string OffDurationKey = "offDuration";
        const string TimeElapsedInCurrentStateKey = "timeElapsedInCurrentState";
        const string DisplayCyclesModeKey = "displayCyclesMode";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<LogicTimerSensor>(out var sourceComponent))
            {
                return new JObject()
                {
                    { OnDurationKey, sourceComponent.onDuration},
                    { OffDurationKey, sourceComponent.offDuration},
                    { TimeElapsedInCurrentStateKey, sourceComponent.timeElapsedInCurrentState},
                    { DisplayCyclesModeKey, sourceComponent.displayCyclesMode},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<LogicTimerSensor>(out var targetComponent))
            {
                if (!jObject.TryGet<float>(OnDurationKey, out var onDuration))
                    return;
                if (!jObject.TryGet<float>(OffDurationKey, out var offDuration))
                    return;
                if (!jObject.TryGet<float>(TimeElapsedInCurrentStateKey, out var timeElapsedInCurrentState))
                    return;
                if (!jObject.TryGet<bool>(DisplayCyclesModeKey, out var displayCyclesMode))
                    return;

                //applying values
                targetComponent.onDuration = onDuration;
                targetComponent.offDuration = offDuration;
                targetComponent.timeElapsedInCurrentState = timeElapsedInCurrentState;
                targetComponent.displayCyclesMode = displayCyclesMode;
            }
        }
    }
    internal class DataTransfer_LogicAlarm
    {
        const string NotificationNameKey = "notificationName";
        const string NotificationTooltipKey = "notificationTooltip";
        const string NotificationTypeKey = "notificationType";
        const string PauseOnNotifyKey = "pauseOnNotify";
        const string ZoomOnNotifyKey = "zoomOnNotify";
        const string CooldownKey = "cooldown";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<LogicAlarm>(out var component))
            {
                return new JObject()
                {
                    { NotificationNameKey, component.notificationName},
                    { NotificationTooltipKey, component.notificationTooltip},
                    { NotificationTypeKey, (int)component.notificationType},
                    { PauseOnNotifyKey, component.pauseOnNotify},
                    { ZoomOnNotifyKey, component.zoomOnNotify},
                    { CooldownKey, component.cooldown},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<LogicAlarm>(out var targetComponent))
            {
                if (jObject.TryGet<string>(NotificationNameKey, out var notificationName))
                    targetComponent.notificationName = notificationName;
                if (jObject.TryGet<string>(NotificationTooltipKey, out var notificationTooltip))
                    targetComponent.notificationTooltip = notificationTooltip;
                if (jObject.TryGet<int>(NotificationTypeKey, out var notificationType))
                    targetComponent.notificationType = (NotificationType)notificationType;
                if (jObject.TryGet<bool>(PauseOnNotifyKey, out var pauseOnNotify))
                    targetComponent.pauseOnNotify = pauseOnNotify;
                if (jObject.TryGet<bool>(ZoomOnNotifyKey, out var zoomOnNotify))
                    targetComponent.zoomOnNotify = zoomOnNotify;
                if (jObject.TryGet<float>(CooldownKey, out var cooldown))
                    targetComponent.cooldown = cooldown;
                targetComponent.UpdateNotification(true);
            }
        }
    }
    internal class DataTransfer_Switch
    {
        const string SwitchedOnKey = "switchedOn";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<Switch>(out var component))
            {
                bool isSwitchedOn = component.IsSwitchedOn;
                if (component is IPlayerControlledToggle playerControlledToggle && playerControlledToggle.ToggleRequested)
                    isSwitchedOn = !isSwitchedOn;

                return new JObject()
                {
                    { SwitchedOnKey, isSwitchedOn}
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<Switch>(out var targetComponent))
            {
                if (!jObject.TryGet<bool>(SwitchedOnKey, out var switchedOn))
                    return;

                //applying values
                if (switchedOn != targetComponent.switchedOn)
                    targetComponent.Toggle();
            }
        }
    }
    internal class DataTransfer_LogicCritterCountSensor
    {
        const string CountThresholdKey = "countThreshold";
        const string ActivateOnGreaterThanKey = "activateOnGreaterThan";
        const string CountCrittersKey = "countCritters";
        const string CountEggsKey = "countEggs";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<LogicCritterCountSensor>(out var sourceComponent))
            {
                return new JObject()
                {
                    { CountThresholdKey, sourceComponent.countThreshold},
                    { ActivateOnGreaterThanKey, sourceComponent.activateOnGreaterThan},
                    { CountCrittersKey, sourceComponent.countCritters},
                    { CountEggsKey, sourceComponent.countEggs},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<LogicCritterCountSensor>(out var targetComponent))
            {
                if (!jObject.TryGet<int>(CountThresholdKey, out var countThreshold))
                    return;
                if (!jObject.TryGet<bool>(ActivateOnGreaterThanKey, out var activateAboveThreshold))
                    return;
                if (!jObject.TryGet<bool>(CountCrittersKey, out var countCritters))
                    return;
                if (!jObject.TryGet<bool>(CountEggsKey, out var countEggs))
                    return;

                //applying values
                targetComponent.countThreshold = countThreshold;
                targetComponent.ActivateAboveThreshold = activateAboveThreshold;
                targetComponent.countCritters = countCritters;
                targetComponent.countEggs = countEggs;
            }
        }
    }

    internal class DataTransfer_IThresholdSwitch
    {
        const string ThresholdKey = "Threshold";
        const string ActivateAboveThresholdKey = "ActivateAboveThreshold";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<IThresholdSwitch>(out var sourceComponent))
            {
                return new JObject()
                {
                    { ThresholdKey, sourceComponent.Threshold},
                    { ActivateAboveThresholdKey, sourceComponent.ActivateAboveThreshold}
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<IThresholdSwitch>(out var targetComponent))
            {
                if (!jObject.TryGet<float>(ThresholdKey, out var Threshold))
                    return;
                if (!jObject.TryGet<bool>(ActivateAboveThresholdKey, out var activateAboveThreshold))
                    return;
                targetComponent.ActivateAboveThreshold = activateAboveThreshold;
                targetComponent.Threshold = Threshold;
            }
        }
    }
    internal class DataTransfer_GenericLogicGateDelay<T>
    {
        const string DelayAmountKey = "DelayAmount";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<T>(out var sourceComponent))
            {
                return new JObject()
                {
                    { DelayAmountKey, (float)Traverse.Create(sourceComponent).Property("DelayAmount").GetValue()}
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<T>(out var targetComponent))
            {
                if (!jObject.TryGet<float>(DelayAmountKey, out var DelayAmount))
                    return;

                //applying values
                Traverse.Create(targetComponent).Property("DelayAmount").SetValue(DelayAmount);
            }
        }
    }

    internal class DataTransfer_LogicRibbonWriter
    {
        const string SelectedBitKey = "selectedBit";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<LogicRibbonWriter>(out var sourceComponent))
            {
                return new JObject()
                {
                    { SelectedBitKey, sourceComponent.GetBitSelection()}
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<LogicRibbonWriter>(out var targetComponent))
            {
                if (!jObject.TryGet<int>(SelectedBitKey, out var selectedBit))
                    return;

                //applying values
                targetComponent.SetBitSelection(selectedBit);
            }
        }
    }
    internal class DataTransfer_LogicRibbonReader
    {
        const string SelectedBitKey = "selectedBit";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<LogicRibbonReader>(out var sourceComponent))
            {
                return new JObject()
                {
                    { SelectedBitKey, sourceComponent.GetBitSelection()}
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<LogicRibbonReader>(out var targetComponent))
            {
                if (!jObject.TryGet<int>(SelectedBitKey, out var selectedBit))
                    return;

                //applying values
                targetComponent.SetBitSelection(selectedBit);
            }
        }
    }


    internal class DataTransfer_HighEnergyParticleSpawner
    {
        const string DirectionKey = "Direction";
        const string ParticleThresholdKey = "particleThreshold";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<HighEnergyParticleSpawner>(out var component))
            {
                return new JObject()
                {
                    { DirectionKey, (int)component.Direction},
                    { ParticleThresholdKey, component.particleThreshold},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<HighEnergyParticleSpawner>(out var targetComponent))
            {
                if (!jObject.TryGet<int>(DirectionKey, out var Direction))
                    return;
                if (!jObject.TryGet<float>(ParticleThresholdKey, out var particleThreshold))
                    return;

                //applying values
                targetComponent.Direction = (EightDirection)Direction;
                targetComponent.particleThreshold = particleThreshold;
            }
        }
    }
    internal class DataTransfer_HighEnergyParticleRedirector
    {
        const string DirectionKey = "Direction";
        internal static JObject? TryGetData(GameObject arg)
        {
            if (arg.TryGetComponent<HighEnergyParticleRedirector>(out var component))
            {
                return new JObject()
                {
                    { DirectionKey, (int)component.Direction},
                };
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            if (building.TryGetComponent<HighEnergyParticleRedirector>(out var targetComponent))
            {
                if (!jObject.TryGet<int>(DirectionKey, out var Direction))
                    return;

                //applying values
                targetComponent.Direction = (EightDirection)Direction;
            }
        }
    }
    internal class DataTransfer_HEPBattery
    {
        const string ParticleThresholdKey = "particleThreshold";
        internal static JObject? TryGetData(GameObject arg)
        {
            var component = arg.GetSMI<HEPBattery.Instance>();
            if (component != null)
            {
                return new JObject()
                {
                    { ParticleThresholdKey, component.particleThreshold},
                };
            }
            ///Same prefab-has-no-SMI problem as DataTransfer_StorageTile above, and the same
            ///smi == null guard for the same reason - see the comment there.
            else if (component == null && arg.GetDef<HEPBattery.Def>() != null)
            {
                return new();
            }
            return null;
        }
        public static void TryApplyData(GameObject building, JObject jObject)
        {
            var targetComponent = building.GetSMI<HEPBattery.Instance>();
            if (targetComponent != null)
            {
                if (!jObject.TryGet<float>(ParticleThresholdKey, out var particleThreshold))
                    return;

                //applying values
                targetComponent.particleThreshold = particleThreshold;
            }
        }
    }
}
