using System.Collections;
using BlueprintsV2.BlueprintData.NoteToolPlacedEntities;
using BlueprintsV2.BlueprintData.OniTogether_Integration;
using UnityEngine;
using UtilLibs.UI.FUI;
using UtilLibs.UIcmp;
using static BlueprintsV2.STRINGS.UI;

namespace BlueprintsV2.UnityUI;

internal class TextNoteSideScreen : SideScreenContent
{
    TextNote? Target = null;

    FButton ClearTitle = null!, ClearText = null!;
    FInputField2 TitleInput = null!, TextInput = null!;
    FColorPickerArray ColorPicker = null!;
    FItemPickerArray SymbolPicker = null!;
    public override bool IsValidForTarget(GameObject target) => !target.IsNullOrDestroyed() && target.TryGetComponent<TextNote>(out _);
    public override void OnPrefabInit()
    {
        base.OnPrefabInit();
        Init();
    }
    bool spawned = false;
    string? title = null, text = null;

    public override void OnSpawn()
    {
        base.OnSpawn();
        spawned = true;
        StartCoroutine(SetTextDelayed());

    }
    void RefreshClearButtons()
    {
        ClearTitle.SetInteractable(TitleInput.Text.Any());
        ClearText.SetInteractable(TextInput.Text.Any());
    }

    IEnumerator SetTextDelayed()
    {
        yield return null;
        if (!text.IsNullOrWhiteSpace())
        {
            TextInput.SetTextFromData(text, true);
            text = null;
        }
        if (!title.IsNullOrWhiteSpace())
        {
            TitleInput.SetTextFromData(title, true);
            title = null;
        }
        RefreshClearButtons();
    }

    public override string GetTitle() => NOTETOOLSTATECONTAINER.TITLE.SIDESCREENTEXT;
    void Init()
    {
        transform.Find("Title").gameObject.SetActive(false);
        transform.Find("Buttons").gameObject.SetActive(false);

        SymbolPicker = transform.Find("SymbolPicker").gameObject.AddOrGet<FItemPickerArray>();
        SymbolPicker.Init(TextNote.SymbolMap, ModAssets.IconSort);
        SymbolPicker.OnSelectionChanged += SetSymbol;

        ColorPicker = transform.Find("ColorPicker").gameObject.AddOrGet<FColorPickerArray>();
        ColorPicker.OnColorChange += SetColor;

        TitleInput = transform.Find("NoteTitleInput/Input").gameObject.AddOrGet<FInputField2>();
        TitleInput.Text = string.Empty;
        ///AddListener, not OnValueChanged.AddListener: only the former honours FInputField2's
        ///DataTextUpdate guard, so pushing a note's own text into the field with SetTextFromData
        ///no longer comes back through here and writes it straight back to the note (#63).
        TitleInput.AddListener(SetTitle);

        TextInput = transform.Find("NoteTextInput/Input").gameObject.AddOrGet<FInputField2>();
        TextInput.Text = string.Empty;
        TextInput.AddListener(SetText);

        ///the clear buttons deliberately set Text raw: the resulting event is what clears the
        ///note itself and repaints these buttons. Routing them through the guard would stop them
        ///clearing anything.
        ClearTitle = transform.Find("NoteTitleInput/DeleteButton").gameObject.AddOrGet<FButton>();
        ClearTitle.OnClick += () => TitleInput.Text = string.Empty;

        ClearText = transform.Find("NoteTextInput/DeleteButton").gameObject.AddOrGet<FButton>();
        ClearText.OnClick += () => TextInput.Text = string.Empty;
    }

    public override void ClearTarget()
    {
        MP_Helpers.HandleNoteUpdate(Target);
        Target = null;
        base.ClearTarget();
    }

    public override void SetTarget(GameObject target)
    {
        base.SetTarget(target);

        if (!target.TryGetComponent<TextNote>(out var note)) return;

        Target = note;
        var data = note.GetNoteData();
        if (spawned)
        {
            TextInput.SetTextFromData(data.Text != STRINGS.BLUEPRINTS_BLUEPRINTNOTE.TEXTNOTE_EMPTY.TEXT ? data.Text : string.Empty, true);
            TitleInput.SetTextFromData(data.Title != STRINGS.BLUEPRINTS_BLUEPRINTNOTE.TEXTNOTE_EMPTY.TITLE ? data.Title : string.Empty, true);
        }
        else
        {
            text = (data.Text != STRINGS.BLUEPRINTS_BLUEPRINTNOTE.TEXTNOTE_EMPTY.TEXT ? data.Text : string.Empty);
            title = (data.Title != STRINGS.BLUEPRINTS_BLUEPRINTNOTE.TEXTNOTE_EMPTY.TITLE ? data.Title : string.Empty);
        }
        ColorPicker.SetSelected(data.SymbolTint);
        SymbolPicker.SetSelected(data.Symbol);
        ///explicitly, now that the pushes above no longer re-enter SetTitle/SetText: they were
        ///what used to repaint these buttons on this path, so without this they would keep the
        ///previous note's state.
        RefreshClearButtons();
    }
    void SetTitle(string val)
    {
        val = val.Any() ? val : STRINGS.BLUEPRINTS_BLUEPRINTNOTE.TEXTNOTE_EMPTY.TITLE;
        Target?.UpdateInfo(val);
        RefreshClearButtons();
    }
    void SetText(string val)
    {
        val = val.Any() ? val : STRINGS.BLUEPRINTS_BLUEPRINTNOTE.TEXTNOTE_EMPTY.TEXT;
        Target?.UpdateInfo(text: val);
        RefreshClearButtons();
    }
    void SetColor(Color color) => Target?.UpdateInfo(tint: color);
    void SetSymbol(string select) => Target?.UpdateInfo(symbol: select);
}
