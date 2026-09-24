using System.Collections;
using BlueprintsV2.BlueprintData;
using BlueprintsV2.Tools;
using BlueprintsV2.UnityUI.Components;
using STRINGS;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UtilLibs;
using UtilLibs.UIcmp;
using static BlueprintsV2.STRINGS.UI;
using static BlueprintsV2.STRINGS.UI.TOOLS;
using static BlueprintsV2.STRINGS.UI.USEBLUEPRINTSTATECONTAINER.INFOITEMSCONTAINER;

namespace BlueprintsV2.UnityUI;

internal class CurrentBlueprintStateScreen : KScreen
{
    public static CurrentBlueprintStateScreen Instance = null!;

    LocText CurrentBPName = null!;
    FButton SelectPrevBP = null!, SelectNextBP = null!;
    LocText FolderInfo = null!;
    GameObject FolderInfoGO = null!;

    GameObject ColorPreviewPrefab = null!;

    FToggle ApplyBPSettings = null!, ForceRebuildMismatchedBuildings = null!, EnableSnapshotMaterialOverrides = null!, UseToolPriority = null!, ForceOverrideTransformations = null!, ApplySettingsToExistingBuildings = null!, EnableGridSnapping = null!;
    FInputField2 GridSnapX = null!, GridSnapY = null!;
    ///the blueprint, and its revision, that the grid-snap step was last defaulted from. Reopening
    ///the tool on the same blueprint re-runs SetSelectedBlueprint, and must not throw away a step
    ///the player typed.
    Blueprint? gridStepSource;
    int gridStepSourceRevision;
    ///the cloned step fields come from a full-width title input; a step is a few digits.
    const float GridStepFieldWidth = 40f, GridStepFieldGap = 6f;
    //YesNoInfo CanRotate;
    FButton RotateL = null!, RotateR = null!, ChangeMaterialOverrides = null!;
    FButton SaveSnapshot = null!, ExportSnapshot = null!;
    GameObject ExportActions = null!;
    //YesNoInfo CanFlipH, CanFlipV;
    FButton FlipH = null!, FlipV = null!;
    ToolTip CanRotateL_TT = null!, CanRotateR_TT = null!, CanFlipH_TT = null!, CanFlipV_TT = null!;

    public static void DestroyInstance() { Instance = null!; }

    public static void ShowScreen(bool show)
    {
        if (Instance == null)
        {
            GameObject baseContent = ToolMenu.Instance.toolParameterMenu.content;
            //GameObject baseWidgetContainer = ToolMenu.Instance.toolParameterMenu.widgetContainer;

            Instance = Util.KInstantiateUI<CurrentBlueprintStateScreen>(ModAssets.BlueprintInfoStateGO, baseContent.transform.parent.gameObject);
            Instance.gameObject.SetActive(true);
        }
        Instance.gameObject.SetActive(show);
        if (show)
        {
            ///Reactivate with a frame delay to get the content size fitter resize to reach the outer container
            Instance.StartCoroutine(Instance.RefreshSize());
        }
    }
    IEnumerator RefreshSize()
    {
        yield return null;
        gameObject.SetActive(false);
        gameObject.SetActive(true);
    }

    public void SetSelectedBlueprint(Blueprint? bp)
    {
        if (bp == null)
        {
            CurrentBPName.SetText("-");
            return;
        }

        CurrentBPName.SetText(bp.FriendlyName);
        DefaultGridStep(bp);
        EnableSnapshotMaterialOverrides.gameObject.SetActive(BlueprintState.CurrentStateInfo().IsPlacingSnapshot);
        ChangeMaterialOverrides.transform.parent.gameObject.SetActive(BlueprintState.CurrentStateInfo().IsPlacingSnapshot);
        ///a saved blueprint is already on disk and the selection screen exports it, so these two
        ///are only useful for a snapshot, which otherwise lives and dies with the session.
        ExportActions.SetActive(BlueprintState.CurrentStateInfo().IsPlacingSnapshot);
        if (BlueprintState.CurrentStateInfo().IsPlacingSnapshot)
        {
            CurrentBPName.SetText("-");
            FolderInfoGO.SetActive(true);
            string folderInfo = string.Format(FOLDERINFO.LABEL_SNAPSHOT, SnapshotTool.SnapshotIndex + 1, SnapshotTool.SnapshotCount);
            FolderInfo.SetText(folderInfo);
            ChangeMaterialOverrides.SetInteractable(BlueprintState.CurrentStateInfo().MaterialReplacementInSnapshots);
        }
        else
        {
            FolderInfoGO.SetActive(true);
            var folder = ModAssets.GetCurrentFolder();
            int bpIndex = folder.GetBlueprintIndex(bp) + 1;
            int folderCount = folder.BlueprintCount;
            string folderInfo = string.Format(FOLDERINFO.LABEL, folder.Name, bpIndex, folderCount);
            FolderInfo.SetText(folderInfo);
        }
        RefreshButtonStates();
    }

    public void RefreshButtonStates()
    {
        var info = BlueprintState.CurrentStateInfo();
        bool canRotate = info.CanRotate;

        //CanRotate.SetInfoState(canRotate);
        RotateL.SetInteractable(canRotate);
        RotateR.SetInteractable(canRotate);
        CanRotateL_TT.SetSimpleTooltip(canRotate ? string.Empty : string.Format(USEBLUEPRINTSTATECONTAINER.ROTATION_BLOCKED, info.TransformationBlockedByBuildingName));
        CanRotateR_TT.SetSimpleTooltip(canRotate ? string.Empty : string.Format(USEBLUEPRINTSTATECONTAINER.ROTATION_BLOCKED, info.TransformationBlockedByBuildingName));

        bool canFlipH = info.CanFlipH;
        //CanFlipH.SetInfoState(canFlipH);
        FlipH.SetInteractable(canFlipH);
        CanFlipH_TT.SetSimpleTooltip(canFlipH ? string.Empty : string.Format(USEBLUEPRINTSTATECONTAINER.FLIP_BLOCKED, info.TransformationBlockedByBuildingName));

        bool canFlipV = info.CanFlipV;
        //CanFlipV.SetInfoState(canFlipV);
        FlipV.SetInteractable(canFlipV);
        CanFlipV_TT.SetSimpleTooltip(canFlipV ? string.Empty : string.Format(USEBLUEPRINTSTATECONTAINER.FLIP_BLOCKED, info.TransformationBlockedByBuildingName));
        RefreshStateChangeBPs();

        ApplyBPSettings.SetOnFromCode(info.ApplyBlueprintSettings);
        UseToolPriority.SetOnFromCode(info.UseToolPriority);
        ForceRebuildMismatchedBuildings.SetOnFromCode(false);
        ForceOverrideTransformations.SetOnFromCode(info.ForceOverrideTransformations);
        ApplySettingsToExistingBuildings.SetOnFromCode(info.ApplySettingsToExistingBuildings);

        EnableGridSnapping.SetOnFromCode(info.SnapToGrid);
        GridSnapX.SetTextFromData(info.GridSnapX.ToString());
        GridSnapY.SetTextFromData(info.GridSnapY.ToString());
    }

    /// <summary>Defaults the grid-snap step to the blueprint's exact footprint, so copies sit edge to
    /// edge - but only for a blueprint (or a revision of one) the step was not already set for.</summary>
    void DefaultGridStep(Blueprint bp)
    {
        if (ReferenceEquals(bp, gridStepSource) && bp.ContentRevision == gridStepSourceRevision)
            return;
        gridStepSource = bp;
        gridStepSourceRevision = bp.ContentRevision;

        var size = bp.FootprintSize();
        var info = BlueprintState.CurrentStateInfo();
        info.GridSnapX = Math.Max(size.x, 1);
        info.GridSnapY = Math.Max(size.y, 1);
    }
    void RefreshStateChangeBPs()
    {
        if (BlueprintState.CurrentStateInfo().IsPlacingSnapshot)
        {
            bool storedBPs = SnapshotTool.HasSnapshotsStored;
            bool canGoNext = SnapshotTool.Instance?.HasNextSnapshot ?? false;
            bool canGoPrev = SnapshotTool.Instance?.HasPrevSnapshot ?? false;
            SelectNextBP.SetInteractable(storedBPs && canGoNext);
            SelectPrevBP.SetInteractable(storedBPs && canGoPrev);
        }
        else
        {
            var folder = ModAssets.GetCurrentFolder();
            var bp = ModAssets.SelectedBlueprint;

            bool storedBPs = folder.HasBlueprints;
            bool canGoNext = folder.HasNextBlueprint(bp);
            bool canGoPrev = folder.HasPrevBlueprint(bp);
            SelectNextBP.SetInteractable(storedBPs && canGoNext);
            SelectPrevBP.SetInteractable(storedBPs && canGoPrev);
        }
    }

    public override void OnPrefabInit()
    {
        base.OnPrefabInit();
        Init();
    }
    void Init()
    {
        //UIUtils.ListAllChildrenPath(this.transform);

        CurrentBPName = transform.Find("InfoItemsContainer/CurrentBP/Label").gameObject.GetComponent<LocText>();
        FolderInfo = transform.Find("InfoItemsContainer/FolderInfo/Label").gameObject.GetComponent<LocText>();
        FolderInfoGO = transform.Find("InfoItemsContainer/FolderInfo").gameObject;

        SelectPrevBP = transform.Find("InfoItemsContainer/CurrentBP/Prev").gameObject.AddOrGet<FButton>();
        SelectPrevBP.OnClick += HandlePrevBP;
        UIUtils.AddSimpleTooltipToObject(SelectPrevBP.gameObject, string.Format(USE_TOOL.SELECTPREV, UI.FormatAsHotkey("[" + GameUtil.GetActionString(ModAssets.Actions.BlueprintsSelectPrevious.GetKAction()) + "]")));

        SelectNextBP = transform.Find("InfoItemsContainer/CurrentBP/Next").gameObject.AddOrGet<FButton>();
        SelectNextBP.OnClick += HandleNextBP;
        UIUtils.AddSimpleTooltipToObject(SelectNextBP.gameObject, string.Format(USE_TOOL.SELECTNEXT, UI.FormatAsHotkey("[" + GameUtil.GetActionString(ModAssets.Actions.BlueprintsSelectNext.GetKAction()) + "]")));


        ApplyBPSettings = transform.Find("InfoItemsContainer/ApplyStoredSettings").gameObject.AddOrGet<FToggle>();
        ApplyBPSettings.SetCheckmark("Checkbox/Checkmark");
        ApplyBPSettings.SetOnFromCode(BlueprintState.CurrentStateInfo().ApplyBlueprintSettings);
        ApplyBPSettings.OnChange += (on) => BlueprintState.CurrentStateInfo().ApplyBlueprintSettings = on;
        UIUtils.AddSimpleTooltipToObject(ApplyBPSettings.gameObject, APPLYSTOREDSETTINGS.TOOLTIP);


        ForceRebuildMismatchedBuildings = transform.Find("InfoItemsContainer/ForceRebuild").gameObject.AddOrGet<FToggle>();
        ForceRebuildMismatchedBuildings.SetCheckmark("Checkbox/Checkmark");
        ForceRebuildMismatchedBuildings.SetOnFromCode(BlueprintState.CurrentStateInfo().ForceBuild);
        ForceRebuildMismatchedBuildings.OnChange += (on) => BlueprintState.CurrentStateInfo().ForceBuild = on;
        UIUtils.AddSimpleTooltipToObject(ForceRebuildMismatchedBuildings.gameObject,
            FORCEREBUILD.TOOLTIP + "\n" + UI.FormatAsHotkey("[" + GameUtil.GetActionString(ModAssets.Actions.BlueprintsToggleForce.GetKAction()) + "]"));

        EnableSnapshotMaterialOverrides = transform.Find("InfoItemsContainer/MaterialReplacement").gameObject.AddOrGet<FToggle>();
        EnableSnapshotMaterialOverrides.SetCheckmark("Checkbox/Checkmark");
        EnableSnapshotMaterialOverrides.SetOnFromCode(BlueprintState.CurrentStateInfo().MaterialReplacementInSnapshots);
        EnableSnapshotMaterialOverrides.OnChange += OnSnapshotOverrideChanged;
        UIUtils.AddSimpleTooltipToObject(EnableSnapshotMaterialOverrides.gameObject, MATERIALREPLACEMENT.TOOLTIP);

        UseToolPriority = transform.Find("InfoItemsContainer/PriorityOverride").gameObject.AddOrGet<FToggle>();
        UseToolPriority.SetCheckmark("Checkbox/Checkmark");
        UseToolPriority.SetOnFromCode(BlueprintState.CurrentStateInfo().UseToolPriority);
        UseToolPriority.OnChange += OnToolPriorityChanged;
        UIUtils.AddSimpleTooltipToObject(UseToolPriority.gameObject, PRIORITYOVERRIDE.TOOLTIP);

        ForceOverrideTransformations = transform.Find("InfoItemsContainer/ForceTransformationToggle").gameObject.AddOrGet<FToggle>();
        ForceOverrideTransformations.SetCheckmark("Checkbox/Checkmark");
        ForceOverrideTransformations.SetOnFromCode(BlueprintState.CurrentStateInfo().ForceOverrideTransformations);
        ForceOverrideTransformations.OnChange += OnForceOverrideTransformationsChanged;
        UIUtils.AddSimpleTooltipToObject(ForceOverrideTransformations.gameObject, FORCETRANSFORMATIONTOGGLE.TOOLTIP);

        ApplySettingsToExistingBuildings = transform.Find("InfoItemsContainer/ApplySettingsToExisting").gameObject.AddOrGet<FToggle>();
        ApplySettingsToExistingBuildings.SetCheckmark("Checkbox/Checkmark");
        ApplySettingsToExistingBuildings.SetOnFromCode(BlueprintState.CurrentStateInfo().ApplySettingsToExistingBuildings);
        ApplySettingsToExistingBuildings.OnChange += OnApplySettingsToExistingChanged;
        UIUtils.AddSimpleTooltipToObject(ApplySettingsToExistingBuildings.gameObject, APPLYSETTINGSTOEXISTING.TOOLTIP);

        BuildGridSnapRow();

        ChangeMaterialOverrides = transform.Find("InfoItemsContainer/MaterialOverrides/Button").gameObject.AddOrGet<FButton>();
        ChangeMaterialOverrides.OnClick += ShowMaterialReplacementList;

        BuildExportActions();

        //CanRotate = transform.Find("InfoItemsContainer/CanRotateYesNo").gameObject.AddOrGet<YesNoInfo>();
        RotateL = transform.Find("InfoItemsContainer/RotateActions/RotateL").gameObject.AddOrGet<FButton>();
        RotateL.OnClick += HandleRotationL;
        UIUtils.AddSimpleTooltipToObject(RotateL.gameObject, UI.FormatAsHotkey("[" + GameUtil.GetActionString(ModAssets.Actions.BlueprintsRotateInverse.GetKAction()) + "]"));

        RotateR = transform.Find("InfoItemsContainer/RotateActions/RotateR").gameObject.AddOrGet<FButton>();
        RotateR.OnClick += HandleRotationR;
        UIUtils.AddSimpleTooltipToObject(RotateR.gameObject, UI.FormatAsHotkey("[" + GameUtil.GetActionString(ModAssets.Actions.BlueprintsRotate.GetKAction()) + "]"));

        //CanFlipH = transform.Find("InfoItemsContainer/CanFlipHYesNo").gameObject.AddOrGet<YesNoInfo>();
        //CanFlipV = transform.Find("InfoItemsContainer/CanFlipVYesNo").gameObject.AddOrGet<YesNoInfo>();
        FlipH = transform.Find("InfoItemsContainer/FlipActions/FlipH").gameObject.AddOrGet<FButton>();
        FlipH.OnClick += HandleFlipH;
        UIUtils.AddSimpleTooltipToObject(FlipH.gameObject, UI.FormatAsHotkey("[" + GameUtil.GetActionString(ModAssets.Actions.BlueprintsFlipHorizontal.GetKAction()) + "]"));
        FlipV = transform.Find("InfoItemsContainer/FlipActions/FlipV").gameObject.AddOrGet<FButton>();
        FlipV.OnClick += HandleFlipV;
        UIUtils.AddSimpleTooltipToObject(FlipV.gameObject, UI.FormatAsHotkey("[" + GameUtil.GetActionString(ModAssets.Actions.BlueprintsFlipVertical.GetKAction()) + "]"));

        ColorPreviewPrefab = transform.Find("InfoItemsContainer/PreviewColorPrefab").gameObject;
        ColorPreviewPrefab.AddOrGet<ColorLegendEntry>();
        ColorPreviewPrefab.SetActive(false);

        CanRotateL_TT = UIUtils.AddSimpleTooltipToObject(RotateL.gameObject, string.Empty);
        CanRotateR_TT = UIUtils.AddSimpleTooltipToObject(RotateR.gameObject, string.Empty);
        CanFlipH_TT = UIUtils.AddSimpleTooltipToObject(FlipH.gameObject, string.Empty);
        CanFlipV_TT = UIUtils.AddSimpleTooltipToObject(FlipV.gameObject, string.Empty);

        BuildColorLegend();
    }

    FInputField2 InitGridStepInput(string path, Action<BlueprintState.BlueprintTransformationInfo, int> set, Func<BlueprintState.BlueprintTransformationInfo, int> get)
    {
        var input = EnableGridSnapping.transform.Find(path).gameObject.AddOrGet<FInputField2>();
        ///AddListener, not OnValueChanged.AddListener: only the former honours the DataTextUpdate
        ///guard, so RefreshButtonStates writing the value back does not re-enter this.
        input.AddListener(text => OnGridStepEdited(text, set));
        input.SetTextFromData(get(BlueprintState.CurrentStateInfo()).ToString());
        input.ClearPlace();
        return input;
    }

    /// <summary>
    /// Takes a typed step only if it is a whole number of at least 1. Upstream carried on after a
    /// failed parse and stored 1, and let 0 through to a divide in the snapshot tool. Anything else
    /// - a cleared field mid-edit, a stray letter - leaves the stored step alone; the field shows
    /// the stored value again the next time the screen refreshes, e.g. on selecting a blueprint.
    /// </summary>
    static void OnGridStepEdited(string text, Action<BlueprintState.BlueprintTransformationInfo, int> set)
    {
        if (int.TryParse(text, out int value) && value >= 1)
            set(BlueprintState.CurrentStateInfo(), value);
    }

    void OnApplySettingsToExistingChanged(bool on)
    {
        BlueprintState.CurrentStateInfo().ApplySettingsToExistingBuildings = on;
    }
    void OnForceOverrideTransformationsChanged(bool on)
    {
        BlueprintState.CurrentStateInfo().ForceOverrideTransformations = on;
        RefreshButtonStates();
    }
    void OnToolPriorityChanged(bool on)
    {
        BlueprintState.CurrentStateInfo().UseToolPriority = on;
    }
    void OnSnapshotOverrideChanged(bool on)
    {
        BlueprintState.CurrentStateInfo().MaterialReplacementInSnapshots = on;
        ChangeMaterialOverrides.SetInteractable(on);
        SnapshotTool.CurrentSnapshot?.CacheCost();
    }

    void BuildColorLegend()
    {
        AddLegend(COLOR_LEGEND.BLUEPRINTS_COLOR_VALIDPLACEMENT, ModAssets.BLUEPRINTS_COLOR_VALIDPLACEMENT);
        AddLegend(COLOR_LEGEND.BLUEPRINTS_COLOR_CAN_APPLY_SETTINGS, ModAssets.BLUEPRINTS_COLOR_CAN_APPLY_SETTINGS);
        if (Config.Instance.RequireConstructable_Tech)
            AddLegend(COLOR_LEGEND.BLUEPRINTS_COLOR_NOTECH, ModAssets.BLUEPRINTS_COLOR_NOTECH);
        if (Config.Instance.RequireConstructable_Material)
            AddLegend(COLOR_LEGEND.BLUEPRINTS_COLOR_NOMATERIALS, ModAssets.BLUEPRINTS_COLOR_NOMATERIALS);
        AddLegend(COLOR_LEGEND.BLUEPRINTS_COLOR_NOTALLOWEDINWORLD, ModAssets.BLUEPRINTS_COLOR_NOTALLOWEDINWORLD);
        AddLegend(COLOR_LEGEND.BLUEPRINTS_COLOR_INVALIDPLACEMENT, ModAssets.BLUEPRINTS_COLOR_INVALIDPLACEMENT);
    }

    void AddLegend(string label, Color color)
    {
        var item = Util.KInstantiateUI<ColorLegendEntry>(ColorPreviewPrefab, ColorPreviewPrefab.transform.parent.gameObject);
        item.SetContent(label, color);
        item.gameObject.SetActive(true);
    }


    void HandleFlipH()
    {
        BlueprintState.CurrentStateInfo().FlipHorizontal();
        BlueprintState.RefreshBlueprintVisualizers();
    }
    void HandleFlipV()
    {

        BlueprintState.CurrentStateInfo().FlipVertical();
        BlueprintState.RefreshBlueprintVisualizers();
    }
    void HandleRotationR()
    {
        BlueprintState.CurrentStateInfo().TryRotateBlueprint();
        BlueprintState.RefreshBlueprintVisualizers();
    }
    void HandleRotationL()
    {
        BlueprintState.CurrentStateInfo().TryRotateBlueprint(true);
        BlueprintState.RefreshBlueprintVisualizers();
    }

    /// <summary>
    /// Builds the Snap to Grid row - the toggle and its two step fields - rather than taking it
    /// from upstream's prefab: the row first appears in upstream's cc28b8b bundle, which postdates
    /// their relicense, so this fork ships the bundles it forked with and assembles the row from
    /// parts those already contain. See docs/blueprints-included/asset-provenance.md.
    ///
    /// The toggle is a clone of the row above it, which has the same shape - a Label and a
    /// Checkbox/Checkmark. The step fields have no counterpart here at all: this prefab holds no
    /// input of any kind, so they come from the note tool's screen.
    /// </summary>
    void BuildGridSnapRow()
    {
        var template = transform.Find("InfoItemsContainer/ApplySettingsToExisting").gameObject;
        var row = Util.KInstantiateUI(template, template.transform.parent.gameObject, true);
        row.name = "GridSnap";
        row.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);
        ///TryChangeText clears the LocText key as well as setting the text. Left in place, the
        ///cloned key would resolve back to the template's string on the next language change.
        UIUtils.TryChangeText(row.transform, "Label", GRIDSNAP.LABEL);

        ///the clone carries the template's FToggle but not its C# event subscriptions, so it
        ///starts with no handler of its own. SetCheckmark is not optional: FToggle's own fallback
        ///takes the first Image in the subtree, which here is the row background.
        EnableGridSnapping = row.AddOrGet<FToggle>();
        EnableGridSnapping.SetCheckmark("Checkbox/Checkmark");
        EnableGridSnapping.SetOnFromCode(BlueprintState.CurrentStateInfo().SnapToGrid);
        EnableGridSnapping.OnChange += (on) => BlueprintState.CurrentStateInfo().SnapToGrid = on;
        UIUtils.AddSimpleTooltipToObject(EnableGridSnapping.gameObject, GRIDSNAP.TOOLTIP);

        ///right to left from the checkbox, so the row reads "Snap to Grid: [W] [H] [x]".
        CloneStepInput(row, "HeightInput", 0);
        CloneStepInput(row, "WidthInput", 1);

        GridSnapX = InitGridStepInput("WidthInput", (info, v) => info.GridSnapX = v, info => info.GridSnapX);
        GridSnapY = InitGridStepInput("HeightInput", (info, v) => info.GridSnapY = v, info => info.GridSnapY);
    }

    /// <summary>
    /// Clones the note tool's title input into the grid-snap row under <paramref name="name"/>.
    /// It is the one input the loaded prefabs offer in the shape FInputField2 requires - a
    /// TMP_InputField whose viewport holds a Text and a Placeholder - and FInputField2 is
    /// [MyCmpReq] on that field, so it cannot be added to a bare GameObject.
    ///
    /// The row has no layout group - its children sit on absolute RectTransforms - so the clone is
    /// placed by hand: anchored to the row's right edge, <paramref name="slot"/> field widths
    /// inboard of the checkbox, and as tall as it. Left alone it keeps the title input's
    /// full-width anchors (#117).
    /// </summary>
    static void CloneStepInput(GameObject row, string name, int slot)
    {
        var source = ModAssets.NoteToolStateScreenGO.transform.Find("NoteTitleInput/Input").gameObject;
        var input = Util.KInstantiateUI(source, row, true);
        input.name = name;
        ///the clone inherits the note title's LocText keys, which resolve to real strings - the
        ///placeholder would read "Add note name..." the moment a language is applied. ClearPlace()
        ///blanks the text but not the key, so both have to go through TryChangeText.
        UIUtils.TryChangeText(input.transform, "TextArea/Text", string.Empty);
        UIUtils.TryChangeText(input.transform, "TextArea/Placeholder", string.Empty);

        var rowRect = (RectTransform)row.transform;
        var checkbox = (RectTransform)row.transform.Find("Checkbox");
        ///the checkbox's left edge, measured from the row's right edge, in row space.
        float checkboxLeft = checkbox.localPosition.x + checkbox.rect.xMin - rowRect.rect.xMax;

        var rect = (RectTransform)input.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 0.5f);
        rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, GridStepFieldWidth);
        rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, checkbox.rect.height);
        rect.anchoredPosition = new Vector2(
            checkboxLeft - GridStepFieldGap - slot * (GridStepFieldWidth + GridStepFieldGap),
            checkbox.localPosition.y + checkbox.rect.center.y - rowRect.rect.center.y);

        var textArea = (RectTransform)input.transform.Find("TextArea");
        textArea.offsetMin = new Vector2(GridStepFieldGap / 2f, 0f);
        textArea.offsetMax = new Vector2(-GridStepFieldGap / 2f, 0f);
        ///and its text is aligned and sized for a taller field, which clips the digits at this
        ///height: centre it, no larger than the row's own label.
        float fontSize = row.transform.Find("Label").GetComponent<LocText>().fontSize;
        TMPConverter.SetTextFit(input, "TextArea/Text", TextAlignmentOptions.Center, fontSize);
        TMPConverter.SetTextFit(input, "TextArea/Placeholder", TextAlignmentOptions.Center, fontSize);
    }

    /// <summary>
    /// Builds the snapshot Save / Export row by cloning the material-overrides row, rather than
    /// taking it from upstream's prefab: the bundle cb3991a ships it in is not a readable
    /// AssetBundle (no <c>UnityFS</c> header - ONI refuses to load it and the mod fails at
    /// startup), and it postdates their relicense in any case. Like <see cref="BuildGridSnapRow"/>,
    /// this builds the row from parts the fork's own bundles already contain.
    /// </summary>
    void BuildExportActions()
    {
        var template = transform.Find("InfoItemsContainer/MaterialOverrides").gameObject;
        ExportActions = Util.KInstantiateUI(template, template.transform.parent.gameObject, true);
        ExportActions.name = "ExportActions";
        ExportActions.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);

        var save = ExportActions.transform.Find("Button").gameObject;
        save.name = "Save";
        var export = Util.KInstantiateUI(save, ExportActions, true);
        export.name = "Export";

        ///the clones carry the template's serialized state but not its C# event subscriptions, so
        ///each starts with no handler of its own.
        SaveSnapshot = save.AddOrGet<FButton>();
        SaveSnapshot.ClearOnClick();
        SaveSnapshot.OnClick += SaveSnapshotAsBlueprint;
        SetButtonLabel(save, EXPORTACTIONS.SAVE_LABEL);
        UIUtils.AddSimpleTooltipToObject(save, EXPORTACTIONS.SAVE_TOOLTIP);

        ///upstream put both handlers on the Save button, leaving Export inert (cb3991a).
        ExportSnapshot = export.AddOrGet<FButton>();
        ExportSnapshot.ClearOnClick();
        ExportSnapshot.OnClick += ExportSnapshotToClipboard;
        SetButtonLabel(export, EXPORTACTIONS.EXPORT_LABEL);
        UIUtils.AddSimpleTooltipToObject(export, EXPORTACTIONS.EXPORT_TOOLTIP);
    }

    static void SetButtonLabel(GameObject button, string text)
    {
        var label = button.transform.Find("Label");
        if (label != null && label.TryGetComponent<LocText>(out var locText))
            locText.SetText(text);
    }

    /// <summary>
    /// Keeps a session snapshot as a real blueprint. A snapshot's FilePath is a bare GUID
    /// (<see cref="Blueprint.SetRandomSnapshotId"/>), so it cannot just be written - upstream's
    /// version called Write() on it, which throws in Directory.CreateDirectory(""). Naming it
    /// first is what gives it a file location, exactly as the create-blueprint tool does.
    /// </summary>
    void SaveSnapshotAsBlueprint()
    {
        var snapshot = SnapshotTool.CurrentSnapshot;
        if (snapshot == null || snapshot.IsEmpty())
        {
            SnapshotFX(EXPORTACTIONS.SAVE_EMPTY);
            return;
        }

        BlueprintRenamingScreen.OpenNamingDialogue(
            STRINGS.UI.DIALOGUE.NAMEBLUEPRINT_TITLE,
            name =>
            {
                SaveNamedSnapshot(snapshot, name);
                SnapshotFX(string.Format(EXPORTACTIONS.SAVED, snapshot.FriendlyName));
            },
            onCancel: () => { });
    }

    /// <summary>The save itself, without the naming dialog around it: give the snapshot a real file
    /// location under the blueprints folder, write it, and register it so the selection screen
    /// lists it.</summary>
    internal static void SaveNamedSnapshot(Blueprint snapshot, string name)
    {
        snapshot.Rename(name, rewrite: true);
        ModAssets.BlueprintFileHandling.HandleBlueprintLoading(snapshot.FilePath);
    }

    void ExportSnapshotToClipboard()
    {
        var snapshot = SnapshotTool.CurrentSnapshot;
        if (snapshot == null || snapshot.IsEmpty())
        {
            SnapshotFX(EXPORTACTIONS.SAVE_EMPTY);
            return;
        }
        ModAssets.ExportToClipboard(snapshot);
        SnapshotFX(EXPORTACTIONS.EXPORTED);
    }

    static void SnapshotFX(string message) => PopFXManager.Instance.SpawnFX(
        ModAssets.BLUEPRINTS_CREATE_ICON_SPRITE, message, null,
        PlayerController.GetCursorPos(KInputManager.GetMousePos()), Config.Instance.FXTime);

    void ShowMaterialReplacementList()
    {
        BlueprintSelectionScreen.ShowWindow((_) => SnapshotTool.CurrentSnapshot?.CacheCost(), SnapshotTool.CurrentSnapshot, false);
    }

    void HandleNextBP()
    {
        if (BlueprintState.CurrentStateInfo().IsPlacingSnapshot)
        {
            SnapshotTool.Instance.VisualizeNextSnapshot();
            SetSelectedBlueprint(SnapshotTool.CurrentSnapshot);
        }
        else
        {
            UseBlueprintTool.Instance.SelectNextBlueprint();
            SetSelectedBlueprint(ModAssets.SelectedBlueprint);
        }
    }
    void HandlePrevBP()
    {
        if (BlueprintState.CurrentStateInfo().IsPlacingSnapshot)
        {
            SnapshotTool.Instance.VisualizePreviousSnapshot();
            SetSelectedBlueprint(SnapshotTool.CurrentSnapshot);
        }
        else
        {
            UseBlueprintTool.Instance.SelectPrevBlueprint();
            SetSelectedBlueprint(ModAssets.SelectedBlueprint);
        }
    }

    internal void SetForceMaterialChange(bool enabled)
    {
        ForceRebuildMismatchedBuildings.SetOnFromCode(enabled);
    }
}
