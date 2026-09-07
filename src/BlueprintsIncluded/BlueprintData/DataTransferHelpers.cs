using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UtilLibs;
using static AccessControl;

namespace BlueprintsV2.BlueprintData
{
	internal class DataTransferHelpers
	{
		internal class DataTransfer_UserNameable
		{
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<UserNameable>(out var component))
				{
					return new JObject()
					{
						{ "savedName", component.savedName},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<UserNameable>(out var targetComponent))
				{
					if (!jObject.TryGet<string>("savedName", out var savedName))
						return;
					targetComponent.SetName(savedName);
				}
			}
		}
		internal class DataTransfer_BuildingEnabledButton
		{
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<BuildingEnabledButton>(out var component))
				{
					bool shouldBeEnabled = component.IsEnabled;
					if (component.queuedToggle)
						shouldBeEnabled = !shouldBeEnabled; //queued toggle means it will be toggled to the opposite state, so we need to invert it here

					return new JObject()
					{
						{ "IsEnabled", shouldBeEnabled},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<BuildingEnabledButton>(out var targetComponent))
				{
					if (!jObject.TryGet<bool>("IsEnabled", out var IsEnabled))
						return;
					targetComponent.IsEnabled = IsEnabled;
				}
			}
		}
		internal class DataTransfer_Repairable
		{
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
						{ "ForbiddenRepair", repairForbiddenState},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<Repairable>(out var targetComponent))
				{
					if (!jObject.TryGet<bool>("ForbiddenRepair", out var RepairForbidden))
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
			internal static JObject? TryGetData(GameObject arg)
			{
				var smi = arg.GetSMI<StorageTile.Instance>();

				if (smi != null && smi.TargetTag != StorageTile.INVALID_TAG)
				{
					return new JObject()
					{
						{ "TargetTag", smi.TargetTag.ToString()},
						{ "UserMaxCapacity", smi.UserMaxCapacity},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;

				var smi = building.GetSMI<StorageTile.Instance>();

				if (smi != null)
				{
					if (!jObject.TryGet<string>("TargetTag", out var TargetTag))
						return;
					var tagParsed = TagManager.Create(TargetTag);
					if (tagParsed.IsValid)
						smi.SetTargetItem(tagParsed);

					if (!jObject.TryGet<float>("UserMaxCapacity", out var UserMaxCapacity))
						return;
					smi.UserMaxCapacity = UserMaxCapacity;
				}
			}
		}
		internal class DataTransfer_SingleEntityReceptacle
		{
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
						{ "requestedEntityTag", requestedEntityTagString},
						{ "requestedEntityAdditionalFilterTag", additionalFilterTagString},
						{ "autoReplaceEntity", component.autoReplaceEntity }
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<SingleEntityReceptacle>(out var targetComponent))
				{
					if (!jObject.TryGet<string>("requestedEntityTag", out var requestedEntityTag))
						return;
					if (!jObject.TryGet<string>("requestedEntityAdditionalFilterTag", out var requestedEntityAdditionalFilterTag))
						return;
					if (!jObject.TryGet<bool>("autoReplaceEntity", out var autoReplaceEntity))
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
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<LogicClusterLocationSensor>(out var component))
				{
					return new JObject()
					{
                        { "activeInSpace", component.activeInSpace},
                        { "activeLocations", EmbeddedJson.From(component.activeLocations.Select(axial => new Tuple<int,int>(axial.Q,axial.R)))},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<LogicClusterLocationSensor>(out var targetComponent))
				{
					if (!jObject.TryGet<bool>("activeInSpace", out var activeInSpace))
						return;
					if (!jObject.TryGetEmbedded<List<Tuple<int, int>>>("activeLocations", out var activeLocations))
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
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<LogicCounter>(out var component))
				{
					return new JObject()
					{
						{ "maxCount", component.maxCount},
						{ "resetCountAtMax", component.resetCountAtMax},
						{ "advancedMode", component.advancedMode},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<LogicCounter>(out var targetComponent))
				{
					if (!jObject.TryGet<int>("maxCount", out var maxCount))
						return;
					targetComponent.maxCount = maxCount;

					if (!jObject.TryGet<bool>("resetCountAtMax", out var resetCountAtMax))
						return;
					targetComponent.resetCountAtMax = resetCountAtMax;

					if (!jObject.TryGet<bool>("advancedMode", out var advancedMode))
						return;
					targetComponent.advancedMode = advancedMode;
				}
			}
		}
		internal class DataTransfer_PixelPack
		{
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
						{ "colorSettings", EmbeddedJson.From(transferedData)},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<PixelPack>(out var targetComponent))
				{
					if (!jObject.TryGetEmbedded<PixelPackColorData[]>("colorSettings", out var colorSettings))
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
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<IUserControlledCapacity>(out var component))
				{
					return new JObject()
					{
						{ "UserMaxCapacity", component.UserMaxCapacity},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<IUserControlledCapacity>(out var targetComponent))
				{
					if (!jObject.TryGet<float>("UserMaxCapacity", out var UserMaxCapacity))
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
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<Automatable>(out var component))
				{
					return new JObject()
					{
						{ "automationOnly", component.automationOnly},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<Automatable>(out var targetComponent))
				{
					if (!jObject.TryGet<bool>("automationOnly", out var automationOnly))
						return;
					targetComponent.SetAutomationOnly(automationOnly);
				}
			}
		}
		internal class DataTransfer_LogicTimeOfDaySensor
		{
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<LogicTimeOfDaySensor>(out var component))
				{
					return new JObject()
					{
						{ "startTime", component.startTime},
						{ "duration", component.duration},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<LogicTimeOfDaySensor>(out var targetComponent))
				{
					if (!jObject.TryGet<float>("startTime", out var startTime))
						return;
					if (!jObject.TryGet<float>("duration", out var duration))
						return;

					//applying values
					targetComponent.startTime = startTime;
					targetComponent.duration = duration;
				}
			}
		}
		internal class DataTransfer_IActivationRangeTarget
		{
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<IActivationRangeTarget>(out var component))
				{
					return new JObject()
					{
						{ "ActivateValue", component.ActivateValue},
						{ "DeactivateValue", component.DeactivateValue},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<IActivationRangeTarget>(out var targetComponent))
				{
					if (!jObject.TryGet<int>("DeactivateValue", out var DeactivateValue))
						return;
					targetComponent.DeactivateValue = DeactivateValue;

					if (!jObject.TryGet<int>("ActivateValue", out var ActivateValue))
						return;
					targetComponent.ActivateValue = ActivateValue;
				}
			}
		}
		internal class DataTransfer_SpaceHeater
		{
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<SpaceHeater>(out var component))
				{
					if (!component.produceHeat)
						return null;

					return new JObject()
					{
						{ "CurrentPowerConsumption", component.CurrentPowerConsumption},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<SpaceHeater>(out var targetComponent))
				{
					if (!targetComponent.produceHeat)
						return;

					if (!jObject.TryGet<float>("CurrentPowerConsumption", out var CurrentPowerConsumption))
						return;
					targetComponent.SetUserSpecifiedPowerConsumptionValue(CurrentPowerConsumption);
				}
			}
		}
		internal class DataTransfer_Clinic
		{
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<Clinic>(out var component))
				{
					return new JObject()
					{
						{ "sicknessSliderValue", component.sicknessSliderValue},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<Clinic>(out var targetComponent))
				{
					if (!jObject.TryGet<float>("sicknessSliderValue", out var sicknessSliderValue))
						return;

					if (targetComponent is ISliderControl sliderControl)
						sliderControl.SetSliderValue(sicknessSliderValue, 0);
				}
			}
		}
		internal class DataTransfer_EnergyGenerator
		{
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<EnergyGenerator>(out var component))
				{
					if (component.ignoreBatteryRefillPercent)
						return null;
					return new JObject()
					{
						{ "batteryRefillPercent", component.batteryRefillPercent},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<EnergyGenerator>(out var targetComponent))
				{
					if (targetComponent.ignoreBatteryRefillPercent)
						return;

					if (!jObject.TryGet<float>("batteryRefillPercent", out var batteryRefillPercent))
						return;
					targetComponent.batteryRefillPercent = batteryRefillPercent;
				}
			}
		}
		internal class DataTransfer_FoodStorage
		{
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<FoodStorage>(out var component) && component.SpicedFoodOnly == true)
				{
					return new JObject()
					{
						{ "SpicedFoodOnly", component.SpicedFoodOnly},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<FoodStorage>(out var targetComponent))
				{
					if (jObject.TryGet<bool>("SpicedFoodOnly", out var spicedFoodOnly))
						targetComponent.SpicedFoodOnly = spicedFoodOnly;
				}
			}
		}
		internal class DataTransfer_AutoDisinfectable
		{
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<AutoDisinfectable>(out var component) && component.enableAutoDisinfect == false) //only store nondefault value
				{
					return new JObject()
					{
						{ "enableAutoDisinfect", component.enableAutoDisinfect},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<AutoDisinfectable>(out var targetComponent))
				{
					if (jObject.TryGet<bool>("enableAutoDisinfect", out var enableAutoDisinfect))
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
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<Door>(out var component))
				{
					if (component.doorType == Door.DoorType.Sealed && !component.hasBeenUnsealed)
						return null;

					return new JObject()
					{
						{ "requestedState", (int)component.RequestedState},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<Door>(out var targetComponent))
				{
					if (jObject.TryGet<int>("requestedState", out var requestedStateRaw))
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
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<DirectionControl>(out var component))
				{
					return new JObject()
					{
						{ "allowedDirection", (int)component.allowedDirection},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<DirectionControl>(out var targetComponent))
				{
					if (jObject.TryGet<int>("allowedDirection", out var allowedDirectionRaw))
						targetComponent.SetAllowedDirection((WorkableReactable.AllowedDirection)allowedDirectionRaw);
				}
			}
		}

		internal class DataTransfer_Prioritizable
		{
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
						{ "masterPrioritySetting", EmbeddedJson.From(prio)},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<Prioritizable>(out var targetComponent))
				{
					if (jObject.TryGetEmbedded<PrioritySetting>("masterPrioritySetting", out var masterPrioritySetting))
					{
						//SgtLogger.l("applying prio: " + masterPrioritySetting.priority_value);
						targetComponent.SetMasterPriority(masterPrioritySetting);
					}
				}
			}
		}
		internal class DataTransfer_TreeFilterable
		{
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
						{ "acceptedTagSet", EmbeddedJson.From(tags)},
						{ "onlyFetchMarkedItems", onlyFetchMarkedItems},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<TreeFilterable>(out var targetComponent))
				{
					if (!targetComponent.copySettingsEnabled)
						return;

					if (jObject.TryGetEmbedded<HashSet<Tag>>("acceptedTagSet", out var acceptedTagSet))
						targetComponent.UpdateFilters(acceptedTagSet);

					if (jObject.TryGet<bool>("onlyFetchMarkedItems", out var onlyFetchMarkedItems))
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
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<FlatTagFilterable>(out var component))
				{
					if (!component.currentlyUserAssignable)
						return null;

					var selectedTags = component.selectedTags;

					return new JObject()
					{
						{ "selectedTags", EmbeddedJson.From(selectedTags)},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<FlatTagFilterable>(out var targetComponent))
				{
					if (!targetComponent.currentlyUserAssignable)
						return;
					if (jObject.TryGetEmbedded<HashSet<Tag>>("selectedTags", out var selectedTags))
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
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<Filterable>(out var component))
				{
					return new JObject()
					{
						{ "SelectedTag", component.SelectedTag.ToString()}
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<Filterable>(out var targetComponent))
				{
					if (!jObject.TryGet<string>("SelectedTag", out var selectedTagString))
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
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<AccessControl>(out var component) && component.controlEnabled)
				{
					return new JObject()
					{
						{ "defaultPermissionByTag", EmbeddedJson.From(component.defaultPermissionByTag)},
						{ "savedPermissionsById", EmbeddedJson.From(component.savedPermissionsById)}
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<AccessControl>(out var targetComponent) && targetComponent.controlEnabled)
				{
					if (!jObject.TryGetEmbedded<List<KeyValuePair<Tag, Permission>>>("defaultPermissionByTag", out var defaultPermissionByTag))
						return;
					targetComponent.defaultPermissionByTag = defaultPermissionByTag;

					if (!jObject.TryGetEmbedded<List<KeyValuePair<int, Permission>>>("savedPermissionsById", out var savedPermissionsById))
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
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<LimitValve>(out var component))
				{
					return new JObject()
					{
						{ "Limit", component.Limit}
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<LimitValve>(out var targetComponent))
				{
					if (!jObject.TryGet<float>("Limit", out var Limit))
						return;

					//applying values
					targetComponent.Limit = Limit;
				}
			}
		}
		internal class DataTransfer_Valve
		{
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<Valve>(out var component))
				{
					return new JObject()
					{
						{ "DesiredFlow", component.DesiredFlow}
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<Valve>(out var targetComponent))
				{
					if (!jObject.TryGet<float>("DesiredFlow", out var DesiredFlow))
						return;

					//applying values
					targetComponent.ChangeFlow(DesiredFlow);
				}
			}
		}

		internal class DataTransfer_LogicTimerSensor
		{
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<LogicTimerSensor>(out var sourceComponent))
				{
					return new JObject()
					{
						{ "onDuration", sourceComponent.onDuration},
						{ "offDuration", sourceComponent.offDuration},
						{ "timeElapsedInCurrentState", sourceComponent.timeElapsedInCurrentState},
						{ "displayCyclesMode", sourceComponent.displayCyclesMode},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<LogicTimerSensor>(out var targetComponent))
				{
					if (!jObject.TryGet<float>("onDuration", out var onDuration))
						return;
					if (!jObject.TryGet<float>("offDuration", out var offDuration))
						return;
					if (!jObject.TryGet<float>("timeElapsedInCurrentState", out var timeElapsedInCurrentState))
						return;
					if (!jObject.TryGet<bool>("displayCyclesMode", out var displayCyclesMode))
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
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<LogicAlarm>(out var component))
				{
					return new JObject()
					{
						{ "notificationName", component.notificationName},
						{ "notificationTooltip", component.notificationTooltip},
						{ "notificationType", (int)component.notificationType},
						{ "pauseOnNotify", component.pauseOnNotify},
						{ "zoomOnNotify", component.zoomOnNotify},
						{ "cooldown", component.cooldown},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<LogicAlarm>(out var targetComponent))
				{
					if (jObject.TryGet<string>("notificationName", out var notificationName))
						targetComponent.notificationName = notificationName;
					if (jObject.TryGet<string>("notificationTooltip", out var notificationTooltip))
						targetComponent.notificationTooltip = notificationTooltip;
					if (jObject.TryGet<int>("notificationType", out var notificationType))
						targetComponent.notificationType = (NotificationType)notificationType;
					if (jObject.TryGet<bool>("pauseOnNotify", out var pauseOnNotify))
						targetComponent.pauseOnNotify = pauseOnNotify;
					if (jObject.TryGet<bool>("zoomOnNotify", out var zoomOnNotify))
						targetComponent.zoomOnNotify = zoomOnNotify;
					if (jObject.TryGet<float>("cooldown", out var cooldown))
						targetComponent.cooldown = cooldown;
					targetComponent.UpdateNotification(true);
				}
			}
		}
		internal class DataTransfer_Switch
		{
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<Switch>(out var component))
				{
					bool isSwitchedOn = component.IsSwitchedOn;
					if (component is IPlayerControlledToggle playerControlledToggle && playerControlledToggle.ToggleRequested)
						isSwitchedOn = !isSwitchedOn;

					return new JObject()
					{
						{ "switchedOn", isSwitchedOn}
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<Switch>(out var targetComponent))
				{
					if (!jObject.TryGet<bool>("switchedOn", out var switchedOn))
						return;

					//applying values
					if (switchedOn != targetComponent.switchedOn)
						targetComponent.Toggle();
				}
			}
		}
		internal class DataTransfer_LogicCritterCountSensor
		{
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<LogicCritterCountSensor>(out var sourceComponent))
				{
					return new JObject()
					{
						{ "countThreshold", sourceComponent.countThreshold},
						{ "activateOnGreaterThan", sourceComponent.activateOnGreaterThan},
						{ "countCritters", sourceComponent.countCritters},
						{ "countEggs", sourceComponent.countEggs},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<LogicCritterCountSensor>(out var targetComponent))
				{
					if (!jObject.TryGet<int>("countThreshold", out var countThreshold))
						return;
					if (!jObject.TryGet<bool>("activateOnGreaterThan", out var activateAboveThreshold))
						return;
					if (!jObject.TryGet<bool>("countCritters", out var countCritters))
						return;
					if (!jObject.TryGet<bool>("countEggs", out var countEggs))
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
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<IThresholdSwitch>(out var sourceComponent))
				{
					return new JObject()
					{
						{ "Threshold", sourceComponent.Threshold},
						{ "ActivateAboveThreshold", sourceComponent.ActivateAboveThreshold}
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<IThresholdSwitch>(out var targetComponent))
				{
					if (!jObject.TryGet<float>("Threshold", out var Threshold))
						return;
					if (!jObject.TryGet<bool>("ActivateAboveThreshold", out var activateAboveThreshold))
						return;
					targetComponent.ActivateAboveThreshold = activateAboveThreshold;
					targetComponent.Threshold = Threshold;
				}
			}
		}
		internal class DataTransfer_GenericLogicGateDelay<T>
		{
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<T>(out var sourceComponent))
				{
					return new JObject()
					{
						{ "DelayAmount", (float)Traverse.Create(sourceComponent).Property("DelayAmount").GetValue()}
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<T>(out var targetComponent))
				{
					if (!jObject.TryGet<float>("DelayAmount", out var DelayAmount))
						return;

					//applying values
					Traverse.Create(targetComponent).Property("DelayAmount").SetValue(DelayAmount);
				}
			}
		}

		internal class DataTransfer_LogicRibbonWriter
		{
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<LogicRibbonWriter>(out var sourceComponent))
				{
					return new JObject()
					{
						{ "selectedBit", sourceComponent.GetBitSelection()}
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<LogicRibbonWriter>(out var targetComponent))
				{
					if (!jObject.TryGet<int>("selectedBit", out var selectedBit))
						return;

					//applying values
					targetComponent.SetBitSelection(selectedBit);
				}
			}
		}
		internal class DataTransfer_LogicRibbonReader
		{
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<LogicRibbonReader>(out var sourceComponent))
				{
					return new JObject()
					{
						{ "selectedBit", sourceComponent.GetBitSelection()}
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<LogicRibbonReader>(out var targetComponent))
				{
					if (!jObject.TryGet<int>("selectedBit", out var selectedBit))
						return;

					//applying values
					targetComponent.SetBitSelection(selectedBit);
				}
			}
		}


		internal class DataTransfer_HighEnergyParticleSpawner
		{
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<HighEnergyParticleSpawner>(out var component))
				{
					return new JObject()
					{
						{ "Direction", (int)component.Direction},
						{ "particleThreshold", component.particleThreshold},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<HighEnergyParticleSpawner>(out var targetComponent))
				{
					if (!jObject.TryGet<int>("Direction", out var Direction))
						return;
					if (!jObject.TryGet<float>("particleThreshold", out var particleThreshold))
						return;

					//applying values
					targetComponent.Direction = (EightDirection)Direction;
					targetComponent.particleThreshold = particleThreshold;
				}
			}
		}
		internal class DataTransfer_HighEnergyParticleRedirector
		{
			internal static JObject? TryGetData(GameObject arg)
			{
				if (arg.TryGetComponent<HighEnergyParticleRedirector>(out var component))
				{
					return new JObject()
					{
						{ "Direction", (int)component.Direction},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;
				if (building.TryGetComponent<HighEnergyParticleRedirector>(out var targetComponent))
				{
					if (!jObject.TryGet<int>("Direction", out var Direction))
						return;

					//applying values
					targetComponent.Direction = (EightDirection)Direction;
				}
			}
		}
		internal class DataTransfer_HEPBattery
		{
			internal static JObject? TryGetData(GameObject arg)
			{
				var component = arg.GetSMI<HEPBattery.Instance>();
				if (component != null)
				{
					return new JObject()
					{
						{ "particleThreshold", component.particleThreshold},
					};
				}
				return null;
			}
			public static void TryApplyData(GameObject building, JObject jObject)
			{
				if (jObject == null)
					return;

				var targetComponent = building.GetSMI<HEPBattery.Instance>();
				if (targetComponent != null)
				{
					if (!jObject.TryGet<float>("particleThreshold", out var particleThreshold))
						return;

					//applying values
					targetComponent.particleThreshold = particleThreshold;
				}
			}
		}
	}
}
